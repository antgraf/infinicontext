using Microsoft.Data.Sqlite;

namespace InfiniContext.Core.Tasks.Migrations;

/// <summary>
/// Add <c>tasks.base_commit_sha</c> тАФ the git HEAD captured at task-creation time.
/// Nullable: tasks created before this migration, and tasks created outside a git repo,
/// have NULL. The <c>/task review</c> command falls back to <c>git merge-base main HEAD</c>
/// when the column is null.
/// </summary>
public sealed class Migration_002_AddBaseCommitSha : IMigration
{
    public int Version => 2;
    public string Description => "Add tasks.base_commit_sha for /task review diff-range resolution.";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "ALTER TABLE tasks ADD COLUMN base_commit_sha TEXT NULL;";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
