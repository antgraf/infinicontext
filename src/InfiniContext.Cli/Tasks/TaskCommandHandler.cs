using InfiniContext.Core.Review;
using InfiniContext.Core.Tasks;

namespace InfiniContext.Cli.Tasks;

/// <summary>
/// Dispatches a parsed <see cref="TaskCommand"/> to <see cref="TaskService"/> and renders the
/// outcome to <see cref="TextWriter"/>. Handler is the only place output formatting lives —
/// the parser is presentation-agnostic and the service is logic-only.
/// </summary>
internal sealed class TaskCommandHandler
{
    private readonly TaskService _service;
    private readonly TextWriter _output;
    private readonly object _writeLock;
    private readonly ReviewService? _reviewService;
    private readonly ScopeResolver? _scopeResolver;
    private readonly TextReader? _input;
    private readonly Func<DateTimeOffset> _clock;

    public TaskCommandHandler(
        TaskService service,
        TextWriter output,
        object? writeLock = null,
        ReviewService? reviewService = null,
        ScopeResolver? scopeResolver = null,
        TextReader? input = null,
        Func<DateTimeOffset>? clock = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _writeLock = writeLock ?? new object();
        _reviewService = reviewService;
        _scopeResolver = scopeResolver;
        _input = input;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
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
                    await HandleNewAsync(n.Description, n.Home, ct).ConfigureAwait(false);
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

                case TaskCommand.Delete(var deleteId, var removeFolder):
                    await HandleDeleteAsync(deleteId, removeFolder, ct).ConfigureAwait(false);
                    break;

                case TaskCommand.Review reviewCmd:
                    // Called without the SlashCommand context (which would supply the active task id);
                    // dispatch with activeTaskId=null. The handler will refuse with a clear message
                    // if the user didn't pass an id either.
                    await HandleReviewAsync(reviewCmd, activeTaskId: null, ct).ConfigureAwait(false);
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

    private async Task HandleNewAsync(string description, string? home, CancellationToken ct)
    {
        var homeCwd = ResolveHome(home);
        Directory.CreateDirectory(homeCwd);
        var record = await _service.CreateAsync(homeCwd, description, ct).ConfigureAwait(false);
        var folder = new TaskFolder(record.HomeCwd, record.Id, Slugify.For(record.Title));
        Write($"[task] created {record.Id:D4} \"{record.Title}\"\n");
        Write($"       folder: {folder.Path}\n");
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

        // Cost guard. --yes skips. Without --yes, prompt if we have an input reader; otherwise
        // refuse with a clear message (so the handler stays usable in non-interactive contexts).
        if (!cmd.SkipConfirm)
        {
            var prompt =
                $"[task review] will spawn 6 sub-Claude agents (5 reviewers + 1 coordinator) for task {record.Id:D4}.\n" +
                "              This is a real cost. Continue? [y/N]: ";
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

        // Gather inputs. Each file is best-effort: missing files become empty strings (the prompts
        // can handle "<no content>" gracefully).
        var folder = new TaskFolder(record.HomeCwd, record.Id, Slugify.For(record.Title));
        var descriptionMd = await ReadIfExistsAsync(Path.Combine(folder.Path, "description.md"), ct).ConfigureAwait(false);
        var progressMd = await ReadIfExistsAsync(Path.Combine(folder.Path, "progress.md"), ct).ConfigureAwait(false);
        var indexMd = await ReadIfExistsAsync(Path.Combine(folder.Path, "index.md"), ct).ConfigureAwait(false);
        var claudeMd = await ReadIfExistsAsync(Path.Combine(record.HomeCwd, "CLAUDE.md"), ct).ConfigureAwait(false);
        var decisionsMd = await ReadDecisionsAsync(record.HomeCwd, ct).ConfigureAwait(false);

        // --scope: v1 treats the value as a single relative path under HomeCwd; glob expansion is
        // a v1 follow-up (see review-feature-design.md). Splitting on ';' lets users pass a small
        // list without needing the full glob matcher.
        IReadOnlyList<string>? scopeOverridePaths = null;
        if (!string.IsNullOrWhiteSpace(cmd.Scope))
        {
            scopeOverridePaths = cmd.Scope.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

        Write($"[task review] done.\n");
        Write($"              applied={report.Applied.Count} rejected={report.Rejected.Count} awaiting={report.Awaiting.Count}\n");
        Write($"              report: {reportPath}\n");
        if (!report.BuildTestGatePassed)
            Write($"              build/test gate reverted: {report.GateReversionReason}\n");
        // index.md / progress.md updates under the lock contract are a v1 follow-up - users
        // currently have to add a row manually if they want the artifact tracked in the index.
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
          /task delete <id> [--remove-folder]   drop the registry row (folder kept unless
                                                --remove-folder is passed); refuses if InProgress
          /task review [<id>] [--scope <glob>] [--yes]
                                                run a multi-agent review on a task. Defaults id
                                                to the focused session's task. --yes skips the
                                                cost prompt (spawns 6 sub-Claude agents).

        """;

    private static string FormatUnixMs(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

    private static string PadRight(string s, int width) =>
        s.Length >= width ? s : s + new string(' ', width - s.Length);
}
