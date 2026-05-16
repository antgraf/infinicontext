using InfiniContext.Core.Tasks.Migrations;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace InfiniContext.Tests.Tasks;

public class MigrationRunnerTests
{
    [Fact]
    public async Task Built_in_migrations_apply_to_fresh_db()
    {
        await using var conn = await OpenInMemoryAsync();

        var applied = await MigrationRunner.ApplyAsync(conn);

        applied.Should().Equal(new[] { 1 });
        var tables = await ObjectNamesAsync(conn);
        tables.Should().BeEquivalentTo(new[] { "__migrations", "idx_tasks_last_active", "task_id_seq", "tasks" });
        // Sequence is initialised and ready to hand out 1 first.
        var seqStart = await ScalarAsync<long>(conn, "SELECT next_id FROM task_id_seq WHERE rowid = 1");
        seqStart.Should().Be(1L);
    }

    [Fact]
    public async Task Apply_is_idempotent_on_repeat_run()
    {
        await using var conn = await OpenInMemoryAsync();
        await MigrationRunner.ApplyAsync(conn);

        var second = await MigrationRunner.ApplyAsync(conn);

        second.Should().BeEmpty();
        // __migrations still has exactly the original row.
        var rows = await ScalarAsync<long>(conn, "SELECT COUNT(*) FROM __migrations");
        rows.Should().Be(1L);
    }

    [Fact]
    public async Task Apply_rejects_non_contiguous_input_list()
    {
        await using var conn = await OpenInMemoryAsync();
        IReadOnlyList<IMigration> list = new IMigration[] { new SkipMigration(2) };

        var act = () => MigrationRunner.ApplyAsync(conn, list);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*contiguous sequence starting at 1*");
    }

    [Fact]
    public async Task Apply_rejects_gap_in_applied_history()
    {
        await using var conn = await OpenInMemoryAsync();
        // Bootstrap the table without applying v1; pretend v2 was applied.
        await ExecAsync(conn, """
            CREATE TABLE __migrations (version INTEGER PRIMARY KEY, description TEXT NOT NULL, applied_at_unix_ms INTEGER NOT NULL);
            INSERT INTO __migrations (version, description, applied_at_unix_ms) VALUES (2, 'test', 0);
            """);

        var act = () => MigrationRunner.ApplyAsync(conn, new IMigration[] { new Migration_001_InitialSchema(), new SkipMigration(2) });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*gap*version 1*");
    }

    [Fact]
    public async Task Apply_rejects_unknown_applied_version()
    {
        await using var conn = await OpenInMemoryAsync();
        await MigrationRunner.ApplyAsync(conn);
        // Pretend we previously applied a version the current code doesn't know about.
        await ExecAsync(conn, "INSERT INTO __migrations (version, description, applied_at_unix_ms) VALUES (2, 'unknown', 0);");

        var act = () => MigrationRunner.ApplyAsync(conn);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unknown versions 2*");
    }

    [Fact]
    public async Task Failed_migration_rolls_back_and_does_not_record_version()
    {
        await using var conn = await OpenInMemoryAsync();
        var explosive = new IMigration[]
        {
            new Migration_001_InitialSchema(),
            new ExplodingMigration(2),
        };

        var act = () => MigrationRunner.ApplyAsync(conn, explosive);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        // v1 is in; v2 is not.
        var versions = await ListAppliedAsync(conn);
        versions.Should().Equal(new[] { 1 });
    }

    private static async Task<SqliteConnection> OpenInMemoryAsync()
    {
        var c = new SqliteConnection("Data Source=:memory:");
        await c.OpenAsync();
        return c;
    }

    private static async Task<List<string>> ObjectNamesAsync(SqliteConnection conn)
    {
        var names = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY name";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) names.Add(r.GetString(0));
        return names;
    }

    private static async Task<List<int>> ListAppliedAsync(SqliteConnection conn)
    {
        var versions = new List<int>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM __migrations ORDER BY version";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) versions.Add(r.GetInt32(0));
        return versions;
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T))!;
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class SkipMigration : IMigration
    {
        public SkipMigration(int version) { Version = version; }
        public int Version { get; }
        public string Description => $"no-op migration {Version}";
        public Task ApplyAsync(SqliteConnection c, SqliteTransaction tx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ExplodingMigration : IMigration
    {
        public ExplodingMigration(int version) { Version = version; }
        public int Version { get; }
        public string Description => "throws on apply";
        public Task ApplyAsync(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }
}
