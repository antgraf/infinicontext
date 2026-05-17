using System.Globalization;
using System.Text;
using InfiniContext.Core.Mcp.Tasks;

namespace InfiniContext.Core.Tasks;

/// <summary>
/// Stage 2B follow-up. Read/write surface for the <c>issues.md</c> task artifact: an optional,
/// append-only structured log of out-of-scope findings ("noted, not in scope right now") that
/// the agent or user surfaces while working on a task. Backed by a markdown file under the task
/// folder; no SQLite mirror by design (single source of truth - see memory
/// <c>feedback-single-source-of-truth</c>).
///
/// <para>
/// Local issue IDs (<c>0001</c>, <c>0002</c>, ...) are per-task and stable; this service
/// auto-increments by scanning existing <c>## NNNN - ...</c> headings, so a missing ID (a manual
/// edit removed an entry) is fine - the next allocated ID is still <c>max(seen) + 1</c>.
/// </para>
///
/// <para>
/// All writes are wrapped in the host's <see cref="LockRegistry"/> contract - same registry the
/// MCP <c>task_acquire_lock</c> tool feeds into, so the host's writes serialize cleanly against
/// any agent session also editing the file. If no registry is supplied (degraded host), writes
/// proceed unsynchronized: the host can still record issues, just without parallel-session
/// safety. Lock owner is a stable per-service id.
/// </para>
/// </summary>
public sealed class IssueService
{
    private readonly TaskStore _store;
    private readonly LockRegistry? _lockRegistry;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _lockTtl;
    private readonly TimeSpan _lockMaxWait;
    private readonly string _lockOwnerId;

    public IssueService(
        TaskStore store,
        LockRegistry? lockRegistry = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? lockTtl = null,
        TimeSpan? lockMaxWait = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _lockRegistry = lockRegistry;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _lockTtl = lockTtl ?? TimeSpan.FromSeconds(30);
        _lockMaxWait = lockMaxWait ?? TimeSpan.FromSeconds(5);
        _lockOwnerId = "host-issue-" + Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>
    /// Append a new entry to the task's <c>issues.md</c>. Scaffolds the file with a header if it
    /// doesn't yet exist. Assigns the next local ID by scanning existing headings. Returns the
    /// persisted entry (including the assigned <see cref="IssueEntry.LocalId"/>).
    /// </summary>
    public async Task<IssueEntry> AppendAsync(int taskId, IssueCapture capture, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (string.IsNullOrWhiteSpace(capture.Title))
            throw new ArgumentException("capture.Title must be non-empty.", nameof(capture));

        var (record, filePath) = await ResolveTaskAsync(taskId, ct).ConfigureAwait(false);

        return await UnderLockAsync(filePath, async () =>
        {
            var existing = File.Exists(filePath)
                ? await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false)
                : null;

            var nextId = existing is null ? 1 : NextLocalId(existing);
            var now = _clock();
            var entry = new IssueEntry(
                LocalId: nextId,
                Title: capture.Title.Trim(),
                LoggedAt: now,
                LoggedBy: capture.LoggedBy,
                Kind: capture.Kind,
                Where: capture.Where,
                Status: IssueStatus.Open,
                Body: capture.Body,
                Resolution: null);

            var sb = new StringBuilder();
            if (existing is null)
            {
                sb.Append("# Issues - ").AppendLine(record.Title).AppendLine();
            }
            else
            {
                sb.Append(existing);
                if (!existing.EndsWith('\n')) sb.AppendLine();
                // Separate from the previous entry with a blank line if the file doesn't already
                // end with one.
                if (!existing.EndsWith("\n\n", StringComparison.Ordinal)) sb.AppendLine();
            }

            AppendEntryMarkdown(sb, entry);
            await File.WriteAllTextAsync(filePath, sb.ToString(), ct).ConfigureAwait(false);
            return entry;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Parse all entries currently in the task's <c>issues.md</c>. Returns an empty list when the
    /// file doesn't exist. Order matches file order (which is also chronological since entries
    /// are append-only).
    /// </summary>
    public async Task<IReadOnlyList<IssueEntry>> ListAsync(int taskId, CancellationToken ct = default)
    {
        var (_, filePath) = await ResolveTaskAsync(taskId, ct).ConfigureAwait(false);
        if (!File.Exists(filePath)) return Array.Empty<IssueEntry>();
        var content = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        return IssueMarkdown.Parse(content);
    }

    /// <summary>
    /// Set <paramref name="localId"/>'s <c>Status:</c> line to <paramref name="newStatus"/> and
    /// insert a <c>Resolution:</c> field-line right under it carrying today's date and the
    /// optional <paramref name="note"/>. The edit is in-place at line granularity so any
    /// hand-written body text in the entry survives byte-identical. Returns the updated entry,
    /// or <c>null</c> if no entry with that id exists (or the file is missing).
    /// </summary>
    public async Task<IssueEntry?> ResolveAsync(
        int taskId,
        int localId,
        IssueStatus newStatus,
        string? note,
        CancellationToken ct = default)
    {
        if (newStatus == IssueStatus.Open)
            throw new ArgumentException("Use Resolved, WontFix, or Deferred - Open is not a terminal state.", nameof(newStatus));

        var (_, filePath) = await ResolveTaskAsync(taskId, ct).ConfigureAwait(false);
        if (!File.Exists(filePath)) return null;

        return await UnderLockAsync(filePath, async () =>
        {
            var original = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var lines = original.Split('\n').ToList();
            // ReadAllLines drops a trailing empty line from a final '\n'; preserve that shape so
            // a rewrite doesn't add or strip a terminator. We split on '\n' instead so a file
            // ending in '\n' yields a trailing empty string we can re-join with.
            var headingIndex = FindEntryHeading(lines, localId);
            if (headingIndex < 0) return null;

            // Range for this entry: from headingIndex up to (but not including) the next "## "
            // heading or end-of-file.
            var endExclusive = lines.Count;
            for (var i = headingIndex + 1; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("## ", StringComparison.Ordinal)) { endExclusive = i; break; }
            }

            // Find Status: within the entry. If missing, append a new Status line at the end of
            // the entry (defensive - the agent might have hand-written an entry without it).
            var statusLineIndex = -1;
            for (var i = headingIndex + 1; i < endExclusive; i++)
            {
                if (IssueMarkdown.IsFieldLine(lines[i], "Status"))
                {
                    statusLineIndex = i;
                    break;
                }
            }

            var newStatusLine = $"- **Status:** {IssueMarkdown.StatusString(newStatus)}";
            var today = DateOnly.FromDateTime(_clock().UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var resolutionLine = string.IsNullOrWhiteSpace(note)
                ? $"- **Resolution:** {today}"
                : $"- **Resolution:** {today} {note!.Trim()}";

            if (statusLineIndex >= 0)
            {
                lines[statusLineIndex] = newStatusLine;
                lines.Insert(statusLineIndex + 1, resolutionLine);
            }
            else
            {
                // Insert both lines at the end of the entry block, before any trailing blank.
                var insertAt = endExclusive;
                while (insertAt > headingIndex + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1]))
                    insertAt--;
                lines.Insert(insertAt, resolutionLine);
                lines.Insert(insertAt, newStatusLine);
            }

            var joined = string.Join("\n", lines);
            await File.WriteAllTextAsync(filePath, joined, ct).ConfigureAwait(false);

            // Re-parse so the returned entry reflects what we just wrote.
            var entries = IssueMarkdown.Parse(joined);
            return entries.FirstOrDefault(e => e.LocalId == localId);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve a task id to its on-disk <c>issues.md</c> path. Throws when the task or its
    /// folder is missing so callers (REPL handlers) surface a clear error.
    /// </summary>
    private async Task<(TaskRecord Record, string FilePath)> ResolveTaskAsync(int taskId, CancellationToken ct)
    {
        var record = await _store.GetAsync(taskId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Task {taskId:D4} not found.");
        var folder = new TaskFolder(record.HomeCwd, record.Id, Slugify.For(record.Title));
        if (!Directory.Exists(folder.Path))
            throw new InvalidOperationException(
                $"Task {taskId:D4} folder {folder.Path} does not exist on disk.");
        return (record, Path.Combine(folder.Path, IssueMarkdown.FileName));
    }

    private async Task<T> UnderLockAsync<T>(string filePath, Func<Task<T>> action)
    {
        if (_lockRegistry is null) return await action().ConfigureAwait(false);

        var outcome = await _lockRegistry.AcquireAsync(filePath, _lockOwnerId, _lockTtl, _lockMaxWait).ConfigureAwait(false);
        if (!outcome.Ok || outcome.Token is null)
            throw new InvalidOperationException(
                $"Timed out acquiring lock on {filePath} (max-wait={_lockMaxWait.TotalSeconds:F0}s). " +
                "Another session likely held it; try again.");
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _lockRegistry.Release(outcome.Token);
        }
    }

    /// <summary>
    /// Highest <c>NNNN</c> seen in <c>## NNNN - ...</c> headings, plus one. Returns 1 when the
    /// file has no entries yet. Tolerates non-4-digit numbers and trailing punctuation.
    /// </summary>
    internal static int NextLocalId(string fileContent)
    {
        var max = 0;
        foreach (var line in fileContent.Split('\n'))
        {
            var n = IssueMarkdown.TryParseHeadingId(line);
            if (n is int id && id > max) max = id;
        }
        return max + 1;
    }

    private static int FindEntryHeading(List<string> lines, int localId)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var n = IssueMarkdown.TryParseHeadingId(lines[i]);
            if (n == localId) return i;
        }
        return -1;
    }

    private static void AppendEntryMarkdown(StringBuilder sb, IssueEntry entry)
    {
        sb.Append("## ").Append(entry.LocalId.ToString("D4", CultureInfo.InvariantCulture))
            .Append(" - ").AppendLine(entry.Title);

        var loggedAt = entry.LoggedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        sb.Append("- **Logged:** ").Append(loggedAt);
        if (!string.IsNullOrWhiteSpace(entry.LoggedBy))
            sb.Append(" by ").Append(entry.LoggedBy);
        sb.AppendLine();

        sb.Append("- **Kind:** ").AppendLine(IssueMarkdown.KindString(entry.Kind));
        if (!string.IsNullOrWhiteSpace(entry.Where))
            sb.Append("- **Where:** ").AppendLine(entry.Where);
        sb.Append("- **Status:** ").AppendLine(IssueMarkdown.StatusString(entry.Status));
        if (!string.IsNullOrWhiteSpace(entry.Body))
            sb.Append("- **Body:** ").AppendLine(entry.Body);
        if (!string.IsNullOrWhiteSpace(entry.Resolution))
            sb.Append("- **Resolution:** ").AppendLine(entry.Resolution);
    }
}

/// <summary>One row in the issues log.</summary>
/// <param name="LocalId">Per-task numeric id (1, 2, 3, ...). Rendered as <c>NNNN</c> in headings.</param>
/// <param name="Title">Short human-readable label.</param>
/// <param name="LoggedAt">UTC timestamp the entry was first written.</param>
/// <param name="LoggedBy">Free-form source label (e.g. <c>host-cli</c>, agent session id prefix).</param>
/// <param name="Kind">Issue category - bug, smell, follow-up, question.</param>
/// <param name="Where">Optional file:line or other location pointer.</param>
/// <param name="Status">Current state.</param>
/// <param name="Body">Optional context body. Single-line in CLI captures; multi-line if the
/// agent wrote the entry via Edit.</param>
/// <param name="Resolution">Optional resolution note, set by <see cref="IssueService.ResolveAsync"/>.</param>
public sealed record IssueEntry(
    int LocalId,
    string Title,
    DateTimeOffset LoggedAt,
    string? LoggedBy,
    IssueKind Kind,
    string? Where,
    IssueStatus Status,
    string? Body,
    string? Resolution);

/// <summary>Input to <see cref="IssueService.AppendAsync"/> - fields a new entry carries.</summary>
public sealed record IssueCapture(
    string Title,
    IssueKind Kind = IssueKind.Followup,
    string? Where = null,
    string? Body = null,
    string? LoggedBy = null);

public enum IssueKind
{
    Bug,
    Smell,
    Followup,
    Question,
}

public enum IssueStatus
{
    Open,
    Resolved,
    WontFix,
    Deferred,
}
