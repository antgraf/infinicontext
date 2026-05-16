using Microsoft.Data.Sqlite;

namespace InfiniContext.Core.Tasks.Migrations;

/// <summary>
/// Initial schema for the host's task registry. Creates <c>tasks</c>, the
/// <c>task_id_seq</c> sequence table that backs <see cref="TaskStore.AllocateIdAsync"/>,
/// and the index used by <c>ORDER BY last_active_at_unix_ms DESC</c> queries.
/// </summary>
public sealed class Migration_001_InitialSchema : IMigration
{
    public int Version => 1;
    public string Description => "Initial schema: tasks, task_id_seq, idx_tasks_last_active.";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        const string sql = """
            CREATE TABLE tasks (
                id                       INTEGER PRIMARY KEY,
                home_cwd                 TEXT    NOT NULL,
                title                    TEXT    NOT NULL,
                created_at_unix_ms       INTEGER NOT NULL,
                last_active_at_unix_ms   INTEGER NOT NULL,
                status                   TEXT    NOT NULL,
                parent_task_id           INTEGER NULL REFERENCES tasks(id)
            );

            CREATE INDEX idx_tasks_last_active ON tasks(last_active_at_unix_ms DESC);

            CREATE TABLE task_id_seq (
                rowid    INTEGER PRIMARY KEY CHECK (rowid = 1),
                next_id  INTEGER NOT NULL
            );

            INSERT INTO task_id_seq (rowid, next_id) VALUES (1, 1);
            """;

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
