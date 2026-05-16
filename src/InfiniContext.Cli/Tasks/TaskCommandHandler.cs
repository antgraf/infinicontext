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

    public TaskCommandHandler(TaskService service, TextWriter output, object? writeLock = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _writeLock = writeLock ?? new object();
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

        """;

    private static string FormatUnixMs(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

    private static string PadRight(string s, int width) =>
        s.Length >= width ? s : s + new string(' ', width - s.Length);
}
