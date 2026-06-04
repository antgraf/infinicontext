using InfiniContext.Core.Mcp.Tasks;
using InfiniContext.Core.Review;
using InfiniContext.Core.Tasks;

namespace InfiniContext.Core.Tasks;

/// <summary>
/// Dispatches a parsed <see cref="TaskCommand"/> to <see cref="TaskService"/> and renders the
/// outcome to <see cref="TextWriter"/>. Handler is the only place output formatting lives —
/// the parser is presentation-agnostic and the service is logic-only.
/// </summary>
public sealed class TaskCommandHandler
{
    private readonly TaskService _service;
    private readonly TextWriter _output;
    private readonly object _writeLock;
    private readonly ReviewService? _reviewService;
    private readonly ScopeResolver? _scopeResolver;
    private readonly TextReader? _input;
    private readonly Func<DateTimeOffset> _clock;
    private readonly LockRegistry? _lockRegistry;
    private readonly string _lockOwnerId = "host-review-" + Guid.NewGuid().ToString("N")[..8];

    public TaskCommandHandler(
        TaskService service,
        TextWriter output,
        object? writeLock = null,
        ReviewService? reviewService = null,
        ScopeResolver? scopeResolver = null,
        TextReader? input = null,
        Func<DateTimeOffset>? clock = null,
        LockRegistry? lockRegistry = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _writeLock = writeLock ?? new object();
        _reviewService = reviewService;
        _scopeResolver = scopeResolver;
        _input = input;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _lockRegistry = lockRegistry;
    }

    /// <summary>
    /// Default home directory used when <c>/task new</c> is invoked without <c>--home</c>. Tasks
    /// are decoupled from the host's launch cwd by design — the design doc places task content
    /// at <c>&lt;home_cwd&gt;/.task/&lt;id&gt;-&lt;slug&gt;/</c>, and <c>~/.infinicontext/</c> is the
    /// host's per-user space (already home to <c>infinicontext.db</c> and <c>light-cwd/</c>).
    /// </summary>
    public static string DefaultHomeCwd { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".infinicontext");

    /// <summary>
    /// Resolve a user-supplied <c>--home</c> value (or its absence) to an absolute path. Pure
    /// function — does no I/O. Exposed for tests so the default-fallback path can be asserted
    /// without polluting the developer's real <c>~/.infinicontext/</c> directory.
    /// </summary>
    public static string ResolveHome(string? home) =>
        string.IsNullOrWhiteSpace(home) ? DefaultHomeCwd : Path.GetFullPath(home);

    public async Task HandleAsync(TaskCommand command, CancellationToken ct = default)
    {
        try
        {
            switch (command)
            {
                case TaskCommand.New n:
                    _ = await HandleNewAsync(n.Description, n.Home, ct).ConfigureAwait(false);
                    break;

                case TaskCommand.List(var limit, var status):
                    await HandleListAsync(limit, status, ct).ConfigureAwait(false);
                    break;

                case TaskCommand.Show(var id):
                    await HandleShowAsync(id, ct).ConfigureAwait(false);
                    break;

                case TaskCommand.Fork(var id, var title):
                    await HandleForkAsync(id, title, ct).ConfigureAwait(false);
                    break;

                case TaskCommand.Merge(var ids):
                    await HandleMergeAsync(ids, ct).ConfigureAwait(false);
                    break;

                case TaskCommand.Switch:
                    // /task switch is intercepted by Repl before dispatch (it needs to drive
                    // the host's outer session loop). Reaching this branch means the handler
                    // was called without the Repl in front of it — surface a clear note.
                    Write("[task] /task switch must be invoked from the REPL.\n");
                    break;

                case TaskCommand.Spawn:
                    // Same situation as /task switch: intercepted by the REPL because it needs
                    // SessionManager + TaskActivityCoordinator hooks the bare handler doesn't.
                    Write("[task] /task spawn must be invoked from the REPL.\n");
                    break;

                case TaskCommand.Delete(var deleteId, var removeFolder):
                    await HandleDeleteAsync(deleteId, removeFolder, ct).ConfigureAwait(false);
                    break;

                case TaskCommand.Review reviewCmd:
                    // Called without the SlashCommand context (which would supply the active task id);
                    // dispatch with activeTaskId=null. The handler will refuse with a clear message
                    // if the user didn't pass an id either.
                    await HandleReviewAsync(reviewCmd, activeTaskId: null, ct).ConfigureAwait(false);
                    break;

                case TaskCommand.Flush:
                    // /task flush is intercepted by the REPL because it needs to write into the
                    // focused session's stdin (via IClaudeSession.SendUserMessageAsync). The bare
                    // handler doesn't have a session reference.
                    Write("[task] /task flush must be invoked from the REPL.\n");
                    break;

                case TaskCommand.Help:
                    Write(HelpText);
                    break;

                default:
                    Write($"[task] internal: unhandled command type {command.GetType().Name}\n");
                    break;
            }
        }
        catch (Exception ex)
        {
            Write($"[task] error: {ex.Message}\n");
        }
    }

    /// <summary>
    /// Create a new task record + on-disk folder and write the human-readable "[task] created"
    /// banner. Returns the persisted <see cref="TaskRecord"/> so callers that want to chain into
    /// another action (notably <c>TaskSlashCommand</c>'s auto-switch on <c>/task new</c> -- issue
    /// 0010 in task 0005) can do so without re-querying.
    /// </summary>
    internal async Task<TaskRecord> HandleNewAsync(string description, string? home, CancellationToken ct)
    {
        var homeCwd = ResolveHome(home);
        Directory.CreateDirectory(homeCwd);
        var record = await _service.CreateAsync(homeCwd, description, ct).ConfigureAwait(false);
        var folder = new TaskFolder(record.HomeCwd, record.Id, Slugify.For(record.Title));
        Write($"[task] created {record.Id:D4} \"{record.Title}\"\n");
        Write($"       folder: {folder.Path}\n");
        return record;
    }

    private async Task HandleListAsync(int? limit, TaskItemStatus? status, CancellationToken ct)
    {
        var rows = await _service.ListAsync(new ListFilter(status, limit), ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            Write("[task] no tasks match.\n");
            return;
        }

        Write("ID     STATUS      LAST ACTIVE              TITLE\n");
        foreach (var r in rows)
        {
            Write($"{r.Id:D4}   {PadRight(r.Status.ToString(), 11)} {PadRight(FormatUnixMs(r.LastActiveAtUnixMs), 24)} {r.Title}\n");
        }
    }

    private async Task HandleShowAsync(int id, CancellationToken ct)
    {
        var details = await _service.GetAsync(id, ct).ConfigureAwait(false);
        if (details is null)
        {
            Write($"[task] {id:D4} not found.\n");
            return;
        }

        var r = details.Record;
        Write($"[task] {r.Id:D4} \"{r.Title}\"\n");
        Write($"       home:    {r.HomeCwd}\n");
        Write($"       status:  {r.Status}\n");
        Write($"       parent:  {(r.ParentTaskId is { } pid ? pid.ToString("D4") : "(none)")}\n");
        Write($"       created: {FormatUnixMs(r.CreatedAtUnixMs)}\n");
        Write($"       active:  {FormatUnixMs(r.LastActiveAtUnixMs)}\n");

        if (details.Artifacts.Count == 0)
        {
            Write("       artifacts: (none)\n");
            return;
        }
        Write("       artifacts:\n");
        var nameWidth = details.Artifacts.Max(a => a.Name.Length);
        foreach (var a in details.Artifacts)
        {
            Write($"         {PadRight(a.Name, nameWidth)}  {a.LastModifiedUtc:yyyy-MM-dd HH:mm:ss} UTC\n");
        }
    }

    private async Task HandleForkAsync(int id, string? overrideTitle, CancellationToken ct)
    {
        var fork = await _service.ForkAsync(id, overrideTitle, ct).ConfigureAwait(false);
        Write($"[task] forked {id:D4} -> {fork.Id:D4} \"{fork.Title}\"\n");
    }

    private async Task HandleMergeAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        var merged = await _service.MergeAsync(ids, MergeTier.DeterministicConcat, ct).ConfigureAwait(false);
        Write($"[task] merged {string.Join(", ", ids.Select(i => i.ToString("D4")))} -> {merged.Id:D4} \"{merged.Title}\"\n");
        Write("       Note: only DeterministicConcat tier is implemented in MVP.\n");
    }

    private async Task HandleDeleteAsync(int id, bool removeFolder, CancellationToken ct)
    {
        var result = await _service.DeleteAsync(id, removeFolder, ct).ConfigureAwait(false);
        if (result is null)
        {
            Write($"[task] {id:D4} not found.\n");
            return;
        }

        Write($"[task] deleted {result.Record.Id:D4} \"{result.Record.Title}\"\n");
        if (result.OrphanedChildren > 0)
            Write($"       orphaned {result.OrphanedChildren} child task(s) (parent_task_id cleared)\n");

        if (removeFolder)
        {
            if (result.FolderRemoved)
                Write($"       removed folder: {result.FolderPath}\n");
            else if (!result.FolderExisted)
                Write($"       folder already absent: {result.FolderPath}\n");
        }
        else if (result.FolderExisted)
        {
            Write($"       folder left in place: {result.FolderPath}\n");
            Write("       (pass --remove-folder to also delete the on-disk task folder)\n");
        }
    }

    /// <summary>
    /// Dispatch <c>/task review</c>. Public so <c>TaskSlashCommand</c> can route the parsed
    /// command with the focused session's <paramref name="activeTaskId"/> threaded through. When
    /// the user passes an explicit id (<c>cmd.Id</c>), that wins; otherwise we fall back to the
    /// active session's task. Refuses with a clear message when neither is available.
    /// </summary>
    public async Task HandleReviewAsync(TaskCommand.Review cmd, int? activeTaskId, CancellationToken ct)
    {
        if (_reviewService is null || _scopeResolver is null)
        {
            Write("[task review] subsystem is unavailable (see startup warnings).\n");
            return;
        }

        var id = cmd.Id ?? activeTaskId;
        if (id is null)
        {
            Write("[task review] no task id given and no active task in this session. Pass an explicit id: /task review <id>.\n");
            return;
        }

        var details = await _service.GetAsync(id.Value, ct).ConfigureAwait(false);
        if (details is null)
        {
            Write($"[task review] {id.Value:D4} not found.\n");
            return;
        }

        var record = details.Record;
        if (!Directory.Exists(record.HomeCwd))
        {
            Write($"[task review] task {record.Id:D4} home_cwd does not exist on disk: {record.HomeCwd}\n");
            return;
        }

        // Resolve scope BEFORE the cost prompt. If scope is empty there's nothing to review;
        // we don't want to make the user confirm a 6-agent spawn just to discover that.
        var folder = new TaskFolder(record.HomeCwd, record.Id, Slugify.For(record.Title));

        // --scope: ';'-separated tokens; literal paths pass through, wildcards (`*`, `**`, `?`)
        // expand via Microsoft.Extensions.FileSystemGlobbing rooted at HomeCwd. See
        // ScopeGlobExpander for semantics. ScopeResolver still gets a flat path list either way.
        IReadOnlyList<string>? scopeOverridePaths = null;
        if (!string.IsNullOrWhiteSpace(cmd.Scope))
        {
            scopeOverridePaths = ScopeGlobExpander.Expand(cmd.Scope, record.HomeCwd);
        }

        Write($"[task review] resolving scope for {record.Id:D4} \"{record.Title}\"...\n");
        var progressPath = Path.Combine(folder.Path, "progress.md");
        var scope = await _scopeResolver.ResolveAsync(
            record.HomeCwd,
            record.BaseCommitSha,
            scopeOverridePaths,
            File.Exists(progressPath) ? progressPath : null,
            ct).ConfigureAwait(false);

        Write($"[task review] scope: {scope.Description} ({scope.Files.Count} files, {scope.ExcludedFiles.Count} excluded)\n");
        if (scope.FallbackNote is not null)
            Write($"              note: {scope.FallbackNote}\n");

        // Empty-scope short-circuit: nothing to review = no agents spawned.
        if (scope.Files.Count == 0)
        {
            Write("[task review] nothing to review (0 files in scope).\n");
            if (scope.ExcludedFiles.Count > 0)
            {
                Write($"              {scope.ExcludedFiles.Count} file(s) were excluded:\n");
                foreach (var ex in scope.ExcludedFiles)
                    Write($"                - {ex.RelativePath}: {ex.Reason}\n");
            }
            if (string.IsNullOrWhiteSpace(cmd.Scope))
            {
                Write("              (Pass --scope <path[;path]> to override, or check that the task's base_commit_sha is correct.)\n");
            }
            else
            {
                Write("              (Every --scope path was excluded or unreadable.)\n");
            }
            return;
        }

        // Cost guard. --yes skips. Without --yes, prompt if we have an input reader; otherwise
        // refuse with a clear message (so the handler stays usable in non-interactive contexts).
        // Runs after scope resolution so we don't prompt for a review that has nothing to do.
        if (!cmd.SkipConfirm)
        {
            var prompt =
                $"[task review] will spawn 6 sub-Claude agents (5 reviewers + 1 coordinator) for task {record.Id:D4}.\n" +
                $"              Scope: {scope.Files.Count} files. This is a real cost. Continue? [y/N]: ";
            Write(prompt);
            if (_input is null)
            {
                Write("\n[task review] no input reader available for confirmation; pass --yes to skip the prompt.\n");
                return;
            }
            var line = await _input.ReadLineAsync(ct).ConfigureAwait(false);
            var answer = (line ?? "").Trim();
            if (!answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                && !answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                Write("[task review] cancelled.\n");
                return;
            }
        }

        // Gather the rest of the inputs. Each file is best-effort: missing files become empty
        // strings (the prompts can handle "<no content>" gracefully).
        var descriptionMd = await ReadIfExistsAsync(Path.Combine(folder.Path, "description.md"), ct).ConfigureAwait(false);
        var progressMd = await ReadIfExistsAsync(Path.Combine(folder.Path, "progress.md"), ct).ConfigureAwait(false);
        var indexMd = await ReadIfExistsAsync(Path.Combine(folder.Path, "index.md"), ct).ConfigureAwait(false);
        var claudeMd = await ReadIfExistsAsync(Path.Combine(record.HomeCwd, "CLAUDE.md"), ct).ConfigureAwait(false);
        var decisionsMd = await ReadDecisionsAsync(record.HomeCwd, ct).ConfigureAwait(false);

        Write("[task review] running 5 reviewers + coordinator...\n");

        var request = new ReviewRequest(
            TaskId: record.Id,
            TaskTitle: record.Title,
            RepoRoot: record.HomeCwd,
            Scope: scope,
            DescriptionMd: descriptionMd,
            ProgressMd: progressMd,
            IndexMd: indexMd,
            ClaudeMd: claudeMd,
            DecisionsMd: decisionsMd);

        var report = await _reviewService.RunReviewAsync(request, new ReviewOptions(), ct).ConfigureAwait(false);

        // Render + write the artifact. The filename is timestamped so concurrent re-runs do not
        // collide; no lock-contract acquisition needed for the initial Write.
        var rendered = ReportRenderer.Render(report);
        var timestamp = _clock().UtcDateTime.ToString("yyyy-MM-ddTHHmmss");
        var reportName = $"review-{timestamp}.md";
        var reportPath = Path.Combine(folder.Path, reportName);
        await File.WriteAllTextAsync(reportPath, rendered, ct).ConfigureAwait(false);

        // Append a row to index.md + an entry to progress.md so the artifact is discoverable
        // without manual updates. Both go through the lock contract when one is configured;
        // otherwise we write directly (tests run without a LockRegistry).
        var today = DateOnly.FromDateTime(_clock().UtcDateTime);
        await UpdateFileUnderLockAsync(
            Path.Combine(folder.Path, "index.md"),
            existing => AppendIndexRow(existing, reportName, report, today),
            ct).ConfigureAwait(false);
        await UpdateFileUnderLockAsync(
            Path.Combine(folder.Path, "progress.md"),
            existing => AppendProgressEntry(existing, reportName, report, today),
            ct).ConfigureAwait(false);

        Write($"[task review] done.\n");
        Write($"              applied={report.Applied.Count} rejected={report.Rejected.Count} awaiting={report.Awaiting.Count}\n");
        Write($"              report: {reportPath}\n");
        if (!report.BuildTestGatePassed)
            Write($"              build/test gate reverted: {report.GateReversionReason}\n");
    }

    /// <summary>
    /// Read <paramref name="filePath"/>, apply <paramref name="transform"/>, write the result.
    /// Wraps the read+write in <see cref="LockRegistry"/> acquire/release when a registry is
    /// configured; otherwise writes directly (test code path). A lock-acquire timeout is
    /// surfaced to the user but isn't fatal -- the review report itself is already on disk.
    /// </summary>
    private async Task UpdateFileUnderLockAsync(string filePath, Func<string, string> transform, CancellationToken ct)
    {
        if (_lockRegistry is null)
        {
            var existing = File.Exists(filePath) ? await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false) : "";
            await File.WriteAllTextAsync(filePath, transform(existing), ct).ConfigureAwait(false);
            return;
        }

        var outcome = await _lockRegistry.AcquireAsync(
            filePath, _lockOwnerId, ttl: TimeSpan.FromSeconds(30), maxWait: TimeSpan.FromSeconds(5), ct)
            .ConfigureAwait(false);
        if (!outcome.Ok || outcome.Token is null)
        {
            Write($"[task review] warning: could not acquire lock on {Path.GetFileName(filePath)}; the artifact is still on disk but {Path.GetFileName(filePath)} was not updated.\n");
            return;
        }
        try
        {
            var existing = File.Exists(filePath) ? await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false) : "";
            await File.WriteAllTextAsync(filePath, transform(existing), ct).ConfigureAwait(false);
        }
        finally
        {
            _lockRegistry.Release(outcome.Token);
        }
    }

    /// <summary>
    /// Append a row to the task's <c>index.md</c> table. Assumes the table extends to the file's
    /// end (the v1 convention; the drift detector flags any deviation). The row is added on a
    /// new line after the existing content.
    /// </summary>
    internal static string AppendIndexRow(string existing, string reportName, ReviewReport report, DateOnly today)
    {
        var summary = $"applied={report.Applied.Count} rejected={report.Rejected.Count} awaiting={report.Awaiting.Count}";
        var row = $"| {reportName} | Review report ({summary}) | {today:yyyy-MM-dd} |";
        var sb = new System.Text.StringBuilder(existing);
        if (existing.Length > 0 && !existing.EndsWith('\n')) sb.Append('\n');
        sb.Append(row).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Append a short entry to the task's <c>progress.md</c>. Always opens a fresh heading rather
    /// than parsing for an existing one for today -- keeps the writes additive and side-effect-free.
    /// </summary>
    internal static string AppendProgressEntry(string existing, string reportName, ReviewReport report, DateOnly today)
    {
        var summary = $"applied={report.Applied.Count} rejected={report.Rejected.Count} awaiting={report.Awaiting.Count}";
        var gateStatus = report.BuildTestGatePassed
            ? "green"
            : $"reverted ({report.GateReversionReason ?? "no detail"})";
        var bullet = $"- Ran `/task review`. {summary}. Report: `{reportName}`. Build/test gate: {gateStatus}.";
        var todayHeading = $"## {today:yyyy-MM-dd} (review)";

        // Preserve the file's existing line-ending convention so we don't mix CRLF/LF in one file.
        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = existing.Split(newline).ToList();

        // Look for an existing same-day-review heading. Match on the full trimmed line so a
        // user-written heading like "## 2026-05-17 (impl session)" doesn't accidentally collide
        // with the auto-emitted "## 2026-05-17 (review)".
        var headingIdx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim() == todayHeading) { headingIdx = i; break; }
        }

        if (headingIdx >= 0)
        {
            // Find the end of this section: next heading at level 2 or 1, or EOF.
            var sectionEnd = lines.Count;
            for (var i = headingIdx + 1; i < lines.Count; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("## ", StringComparison.Ordinal) || t.StartsWith("# ", StringComparison.Ordinal))
                {
                    sectionEnd = i;
                    break;
                }
            }
            // Trim trailing blank lines inside the section so the new bullet sits cleanly.
            while (sectionEnd > headingIdx + 1 && string.IsNullOrWhiteSpace(lines[sectionEnd - 1]))
                sectionEnd--;
            lines.Insert(sectionEnd, bullet);
            // Reinstate a blank separator before the next heading (if any).
            if (sectionEnd + 1 < lines.Count
                && (lines[sectionEnd + 1].TrimStart().StartsWith("## ", StringComparison.Ordinal)
                    || lines[sectionEnd + 1].TrimStart().StartsWith("# ", StringComparison.Ordinal)))
            {
                lines.Insert(sectionEnd + 1, "");
            }
            return string.Join(newline, lines);
        }

        // No existing today's heading: append a fresh one at end of file.
        var sb = new System.Text.StringBuilder(existing);
        if (existing.Length > 0 && !existing.EndsWith(newline)) sb.Append(newline);
        if (existing.Length > 0 && !existing.EndsWith(newline + newline)) sb.Append(newline);
        sb.Append(todayHeading).Append(newline);
        sb.Append(newline);
        sb.Append(bullet).Append(newline);
        return sb.ToString();
    }

    private static async Task<string> ReadIfExistsAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return "";
        try { return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false); }
        catch (IOException) { return ""; }
    }

    /// <summary>
    /// Find the project's decisions doc. Tries a small set of well-known paths under
    /// <paramref name="repoRoot"/>; returns "" if none exists. The architecture reviewer's
    /// prompt handles an empty value gracefully.
    /// </summary>
    private static async Task<string> ReadDecisionsAsync(string repoRoot, CancellationToken ct)
    {
        var candidates = new[]
        {
            Path.Combine(repoRoot, "docs", "stage2", "decisions.md"),
            Path.Combine(repoRoot, "docs", "decisions.md"),
            Path.Combine(repoRoot, "DECISIONS.md"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return await ReadIfExistsAsync(candidate, ct).ConfigureAwait(false);
        }
        return "";
    }

    private void Write(string s)
    {
        lock (_writeLock)
        {
            _output.Write(s);
            _output.Flush();
        }
    }

    public const string HelpText = """
        [task] commands:
          /task new [--home <path>] "<description>"
                                                create a new task. --home accepted before OR after
                                                the description; defaults to ~/.infinicontext when omitted
          /task list [--status <s>] [--limit N] list tasks (most-recent first; status: inprogress|idle)
          /task show <id>                       show one task with its artifacts
          /task fork <id> ["<title>"]           fork a task; lineage recorded
          /task merge <id1> <id2> [...]         deterministic concat into a new task
          /task switch <id>                     start a fresh session bound to <id>
                                                (replaces the current session)
          /task spawn <id>                      spawn a NEW pane bound to <id>, alongside the
                                                current session (refuses if another live pane
                                                already binds <id>)
          /task delete <id> [--remove-folder]   drop the registry row (folder kept unless
                                                --remove-folder is passed); refuses if InProgress
          /task review [<id>] [--scope <glob>] [--yes]
                                                run a multi-agent review on a task. Defaults id
                                                to the focused session's task. --yes skips the
                                                cost prompt (spawns 6 sub-Claude agents).
          /task flush                           nudge the focused session to flush task documents
                                                (progress.md, index.md, etc.) right now, without
                                                waiting for a context-pressure threshold.

        """;

    private static string FormatUnixMs(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

    private static string PadRight(string s, int width) =>
        s.Length >= width ? s : s + new string(' ', width - s.Length);
}
