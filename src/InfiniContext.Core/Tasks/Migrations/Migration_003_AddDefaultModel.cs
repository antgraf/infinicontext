using Microsoft.Data.Sqlite;

namespace InfiniContext.Core.Tasks.Migrations;

/// <summary>
/// Add <c>tasks.default_model</c> -- the model id a session inherits when spawned for this task,
/// unless the spawn request overrides it. Nullable: tasks created before this migration, and
/// tasks created without an explicit model choice, have NULL and fall back to
/// <c>settings.Session.Model</c> / <c>settings.Models.Main.Id</c> at spawn time.
/// </summary>
public sealed class Migration_003_AddDefaultModel : IMigration
{
    public int Version => 3;
    public string Description => "Add tasks.default_model for per-task model selection.";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "ALTER TABLE tasks ADD COLUMN default_model TEXT NULL;";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
