using InfiniContext.Core.Tasks.Migrations;
using Microsoft.Data.Sqlite;

namespace InfiniContext.Core.Tasks;

/// <summary>
/// Host-local registry of tracked tasks, backed by SQLite at
/// <c>~/.infinicontext/infinicontext.db</c> (overridable via <c>tasks.dbPath</c>).
///
/// <para>
/// Identity is allocated by the host, not by the agent. <see cref="AllocateIdAsync"/> bumps a
/// dedicated <c>task_id_seq</c> row so callers can reserve an id before doing filesystem work
/// (creating the task folder, etc.) and then commit the row with <see cref="CreateAsync"/>. If
/// the filesystem step fails, the id is leaked (no row is inserted) and that's a deliberate
/// trade-off: the cost of an unused integer is negligible compared with the contortions
/// required to roll back a folder create.
/// </para>
///
/// <para>
/// One <see cref="SqliteConnection"/> is kept open for the lifetime of the store; concurrent
/// callers serialize through a <see cref="SemaphoreSlim"/> since the connection is not
/// thread-safe.
/// </para>
/// </summary>
public sealed class TaskStore : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private TaskStore(SqliteConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Open or create the database at <paramref name="dbPath"/> and apply any pending
    /// migrations. Pass <c>":memory:"</c> for an in-memory database (test use).
    ///
    /// <para>
    /// <b>Single-writer contract.</b> The host expects exactly one process to own the registry
    /// at a time. Migration application (<c>CREATE TABLE IF NOT EXISTS __migrations …</c> and
    /// the per-migration apply) is not transaction-wrapped against concurrent openers, so two
    /// host processes opening a fresh DB simultaneously can race into a UNIQUE-violation
    /// surfaced as an opaque SQLite error. The supported model is single-host: scenarios that
    /// need multiple processes (e.g. a future "infinicontext admin" CLI) must serialise startup
    /// against the host or read-only-open the DB.
    /// </para>
    /// </summary>
    public static async Task<TaskStore> OpenAsync(string dbPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        if (!IsInMemory(dbPath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Foreign-key enforcement is per-connection; enable it explicitly.
            ForeignKeys = true,
        };

        var connection = new SqliteConnection(csb.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        try
        {
            await MigrationRunner.ApplyAsync(connection, ct).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new TaskStore(connection);
    }

    /// <summary>
    /// Reserve the next task id atomically. The reservation is durable: even if the caller
    /// crashes before <see cref="CreateAsync"/> runs, the id will not be reused. (See class
    /// remarks on the leak trade-off.)
    /// </summary>
    public async Task<int> AllocateIdAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            int next;
            await using (var read = _connection.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT next_id FROM task_id_seq WHERE rowid = 1;";
                var result = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
                next = Convert.ToInt32(result);
            }

            await using (var bump = _connection.CreateCommand())
            {
                bump.Transaction = tx;
                bump.CommandText = "UPDATE task_id_seq SET next_id = next_id + 1 WHERE rowid = 1;";
                await bump.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Insert a new <see cref="TaskRecord"/>. The id should already have been allocated.</summary>
    public async Task CreateAsync(TaskRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Id <= 0) throw new ArgumentException("TaskRecord.Id must be a positive integer (call AllocateIdAsync first).", nameof(record));
        if (string.IsNullOrWhiteSpace(record.HomeCwd)) throw new ArgumentException("TaskRecord.HomeCwd must not be empty.", nameof(record));
        if (string.IsNullOrWhiteSpace(record.Title)) throw new ArgumentException("TaskRecord.Title must not be empty.", nameof(record));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tasks (id, home_cwd, title, created_at_unix_ms, last_active_at_unix_ms, status, parent_task_id, base_commit_sha)
                VALUES ($id, $home_cwd, $title, $created, $last_active, $status, $parent, $base_sha);
                """;
            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.Parameters.AddWithValue("$home_cwd", record.HomeCwd);
            cmd.Parameters.AddWithValue("$title", record.Title);
            cmd.Parameters.AddWithValue("$created", record.CreatedAtUnixMs);
            cmd.Parameters.AddWithValue("$last_active", record.LastActiveAtUnixMs);
            cmd.Parameters.AddWithValue("$status", record.Status.ToString());
            cmd.Parameters.AddWithValue("$parent", (object?)record.ParentTaskId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$base_sha", (object?)record.BaseCommitSha ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TaskRecord?> GetAsync(int id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, home_cwd, title, created_at_unix_ms, last_active_at_unix_ms, status, parent_task_id, base_commit_sha
                FROM tasks WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            return ReadRecord(reader);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>List all tasks, most-recently-active first.</summary>
    public async Task<IReadOnlyList<TaskRecord>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rows = new List<TaskRecord>();
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, home_cwd, title, created_at_unix_ms, last_active_at_unix_ms, status, parent_task_id, base_commit_sha
                FROM tasks ORDER BY last_active_at_unix_ms DESC, id DESC;
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add(ReadRecord(reader));
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Update <c>status</c> and bump <c>last_active_at_unix_ms</c> on the row. No-op if the
    /// id does not exist (returns false rather than throwing — caller decides whether that's
    /// fatal in their context).
    /// </summary>
    public async Task<bool> UpdateStatusAsync(int id, TaskItemStatus status, long whenUnixMs, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE tasks SET status = $status, last_active_at_unix_ms = $when WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$when", whenUnixMs);
            cmd.Parameters.AddWithValue("$id", id);
            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return rows > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Delete the task row. Any child rows that point at this row via <c>parent_task_id</c>
    /// have their FK nulled first (so the foreign-key constraint can be satisfied — and so
    /// surviving children don't dangle against a vanished parent). Both updates happen in one
    /// transaction.
    ///
    /// <para>
    /// Returns the number of orphaned children, or <c>null</c> if no row with the given id
    /// existed. Callers (e.g. <see cref="TaskService.DeleteAsync"/>) use <c>null</c> as the
    /// "not found" signal rather than this method throwing.
    /// </para>
    /// </summary>
    public async Task<int?> DeleteAsync(int id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            // First check the row exists — UPDATE/DELETE on a missing id silently affects 0 rows
            // and we want a distinct "not found" signal for the caller.
            await using (var probe = _connection.CreateCommand())
            {
                probe.Transaction = tx;
                probe.CommandText = "SELECT 1 FROM tasks WHERE id = $id;";
                probe.Parameters.AddWithValue("$id", id);
                var hit = await probe.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (hit is null)
                {
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    return null;
                }
            }

            int orphaned;
            await using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "UPDATE tasks SET parent_task_id = NULL WHERE parent_task_id = $id;";
                clear.Parameters.AddWithValue("$id", id);
                orphaned = await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var del = _connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM tasks WHERE id = $id;";
                del.Parameters.AddWithValue("$id", id);
                await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return orphaned;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Record that <paramref name="childId"/> was forked from <paramref name="parentId"/>.
    /// Sets <c>parent_task_id</c> on the child row. Throws if either row is missing or if
    /// the child's parent_task_id was already set to a different value.
    /// </summary>
    public async Task RecordForkAsync(int parentId, int childId, CancellationToken ct = default)
    {
        if (parentId == childId)
            throw new ArgumentException("A task cannot be its own parent.", nameof(childId));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            int? existingParent = await ReadParentAsync(tx, childId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Child task {childId} not found.");

            // Detect the absent vs. set-to-same vs. set-to-different cases.
            if (existingParent == int.MinValue)
            {
                // Sentinel from ReadParentAsync meaning "row exists, parent is NULL" — fall through.
            }
            else if (existingParent.Value == parentId)
            {
                // Idempotent: already linked the same way.
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Child task {childId} already has parent {existingParent.Value}; refusing to overwrite with {parentId}.");
            }

            await using (var verifyParent = _connection.CreateCommand())
            {
                verifyParent.Transaction = tx;
                verifyParent.CommandText = "SELECT 1 FROM tasks WHERE id = $id;";
                verifyParent.Parameters.AddWithValue("$id", parentId);
                var hit = await verifyParent.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (hit is null)
                    throw new InvalidOperationException($"Parent task {parentId} not found.");
            }

            await using (var update = _connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = "UPDATE tasks SET parent_task_id = $parent WHERE id = $child;";
                update.Parameters.AddWithValue("$parent", parentId);
                update.Parameters.AddWithValue("$child", childId);
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the child's parent_task_id wrapped as <see cref="int.MinValue"/> when the row
    /// exists but the column is NULL; <c>null</c> when the row is missing.
    /// </summary>
    private async Task<int?> ReadParentAsync(SqliteTransaction tx, int childId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT parent_task_id FROM tasks WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", childId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return reader.IsDBNull(0) ? int.MinValue : reader.GetInt32(0);
    }

    private static TaskRecord ReadRecord(SqliteDataReader reader)
    {
        var statusText = reader.GetString(5);
        if (!Enum.TryParse<TaskItemStatus>(statusText, ignoreCase: false, out var status))
            throw new InvalidOperationException($"Unknown task status '{statusText}' in row id={reader.GetInt32(0)}.");

        return new TaskRecord(
            Id: reader.GetInt32(0),
            HomeCwd: reader.GetString(1),
            Title: reader.GetString(2),
            CreatedAtUnixMs: reader.GetInt64(3),
            LastActiveAtUnixMs: reader.GetInt64(4),
            Status: status,
            ParentTaskId: reader.IsDBNull(6) ? null : reader.GetInt32(6),
            BaseCommitSha: reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    private static bool IsInMemory(string path) =>
        path.Equals(":memory:", StringComparison.Ordinal)
        || path.StartsWith("file::memory:", StringComparison.Ordinal);
}
