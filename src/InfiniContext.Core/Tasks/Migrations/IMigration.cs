using Microsoft.Data.Sqlite;

namespace InfiniContext.Core.Tasks.Migrations;

/// <summary>
/// One forward-only schema migration applied by <see cref="MigrationRunner"/>. Migrations are
/// numbered sequentially starting at <c>1</c>; the runner refuses to start if it observes a
/// gap (a migration was applied but an earlier one wasn't) since that would indicate a
/// hand-edited DB and the safe move is to surface the discrepancy rather than guess.
/// </summary>
public interface IMigration
{
    /// <summary>Sequential, monotonic, ≥ 1. Used as the primary key of <c>__migrations</c>.</summary>
    int Version { get; }

    /// <summary>One-line description recorded alongside the version (audit aid; not load-bearing).</summary>
    string Description { get; }

    /// <summary>
    /// Apply this migration. Called inside a transaction; throwing aborts the migration and
    /// rolls back any DDL the implementation has issued. Implementations receive an open
    /// connection and an open transaction — bind both to every command they create.
    /// </summary>
    Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct);
}
