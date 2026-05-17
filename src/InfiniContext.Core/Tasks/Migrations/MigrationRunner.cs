using Microsoft.Data.Sqlite;

namespace InfiniContext.Core.Tasks.Migrations;

/// <summary>
/// Applies pending <see cref="IMigration"/>s on host startup. Tracks applied versions in a
/// <c>__migrations</c> bookkeeping table that the runner manages itself (no migration creates
/// it). Forward-only and gap-intolerant: if version N is applied but N-1 is missing, the
/// runner refuses to continue.
/// </summary>
public static class MigrationRunner
{
    /// <summary>
    /// Built-in migrations, in order. New migrations append to this list; existing ones never
    /// change number or content (per the design's migration discipline).
    /// </summary>
    public static IReadOnlyList<IMigration> BuiltIn { get; } =
    [
        new Migration_001_InitialSchema(),
        new Migration_002_AddBaseCommitSha(),
    ];

    /// <summary>
    /// Apply any unapplied <paramref name="migrations"/> against <paramref name="connection"/>.
    /// Each migration runs in its own transaction. Idempotent: if every migration is already
    /// recorded, this is a no-op.
    /// </summary>
    /// <returns>Versions that were applied during this call (empty when DB was already up to date).</returns>
    public static async Task<IReadOnlyList<int>> ApplyAsync(
        SqliteConnection connection,
        IReadOnlyList<IMigration> migrations,
        Func<DateTimeOffset>? clock = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(migrations);
        var nowFn = clock ?? (() => DateTimeOffset.UtcNow);

        EnsureMigrationsTable(connection);
        var versionsOrdered = SortAndValidate(migrations);

        var applied = await GetAppliedVersionsAsync(connection, ct).ConfigureAwait(false);
        ValidateNoGaps(applied, versionsOrdered);

        var newlyApplied = new List<int>();
        foreach (var migration in versionsOrdered)
        {
            if (applied.Contains(migration.Version)) continue;

            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await migration.ApplyAsync(connection, tx, ct).ConfigureAwait(false);

            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO __migrations (version, description, applied_at_unix_ms) VALUES ($v, $d, $t);";
                cmd.Parameters.AddWithValue("$v", migration.Version);
                cmd.Parameters.AddWithValue("$d", migration.Description);
                cmd.Parameters.AddWithValue("$t", nowFn().ToUnixTimeMilliseconds());
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            newlyApplied.Add(migration.Version);
        }

        return newlyApplied;
    }

    /// <summary>Convenience overload that applies <see cref="BuiltIn"/>.</summary>
    public static Task<IReadOnlyList<int>> ApplyAsync(SqliteConnection connection, CancellationToken ct = default)
        => ApplyAsync(connection, BuiltIn, clock: null, ct);

    private static void EnsureMigrationsTable(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS __migrations (
                version            INTEGER PRIMARY KEY,
                description        TEXT    NOT NULL,
                applied_at_unix_ms INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<IMigration> SortAndValidate(IReadOnlyList<IMigration> migrations)
    {
        var sorted = migrations.OrderBy(m => m.Version).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var expected = i + 1;
            if (sorted[i].Version != expected)
                throw new InvalidOperationException(
                    $"Migrations must be a contiguous sequence starting at 1; expected version {expected} but found {sorted[i].Version}.");
        }
        return sorted;
    }

    private static async Task<HashSet<int>> GetAppliedVersionsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var applied = new HashSet<int>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM __migrations";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            applied.Add(reader.GetInt32(0));
        return applied;
    }

    private static void ValidateNoGaps(HashSet<int> applied, IReadOnlyList<IMigration> sorted)
    {
        if (applied.Count == 0) return;
        var maxApplied = applied.Max();
        for (var v = 1; v <= maxApplied; v++)
        {
            // A previously-applied version is missing — DB has been hand-edited or migrations
            // were renumbered. Refuse rather than guess.
            if (!applied.Contains(v))
                throw new InvalidOperationException(
                    $"Migration history has a gap: version {v} is missing but later versions are applied (max applied: {maxApplied}).");
        }

        // Catch the inverse: an unknown applied version (DB ahead of code).
        var knownVersions = sorted.Select(m => m.Version).ToHashSet();
        var unknown = applied.Where(v => !knownVersions.Contains(v)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"Migration history references unknown versions {string.Join(", ", unknown.Order())} not present in code. Did you downgrade?");
    }
}
