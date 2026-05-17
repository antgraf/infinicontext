using System.Globalization;
using InfiniContext.Core.Tasks;

namespace InfiniContext.Cli.Issues;

/// <summary>
/// Dispatches a parsed <see cref="IssueCommand"/> to <see cref="IssueService"/> and renders the
/// outcome to <see cref="TextWriter"/>. Mirror of <c>TaskCommandHandler</c>: handler is the only
/// place output formatting lives; the parser is presentation-agnostic and the service is
/// logic-only.
///
/// <para>
/// Issues are per-task, so every non-help dispatch needs the focused session's active task id
/// (or <c>null</c> when no task is bound, in which case the handler refuses with a clear
/// message rather than throwing). The id is passed in per-call by the slash-command surface,
/// which reads it from the <c>SlashCommandContext</c> at dispatch time; the handler holds no
/// "current task" state of its own.
/// </para>
/// </summary>
internal sealed class IssueCommandHandler
{
    private readonly IssueService _service;
    private readonly TextWriter _output;
    private readonly object _writeLock;

    public IssueCommandHandler(
        IssueService service,
        TextWriter output,
        object? writeLock = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _writeLock = writeLock ?? new object();
    }

    public async Task HandleAsync(IssueCommand command, int? activeTaskId, CancellationToken ct = default)
    {
        try
        {
            switch (command)
            {
                case IssueCommand.Help:
                    Write(HelpText);
                    return;
            }

            if (activeTaskId is null)
            {
                Write("[issue] no active task - bind one to this session first with /task switch <id>.\n");
                return;
            }
            var taskId = activeTaskId;

            switch (command)
            {
                case IssueCommand.Add a:
                    await HandleAddAsync(taskId.Value, a, ct).ConfigureAwait(false);
                    break;
                case IssueCommand.List:
                    await HandleListAsync(taskId.Value, ct).ConfigureAwait(false);
                    break;
                case IssueCommand.Resolve r:
                    await HandleResolveAsync(taskId.Value, r, ct).ConfigureAwait(false);
                    break;
                default:
                    Write($"[issue] internal: unhandled command type {command.GetType().Name}\n");
                    break;
            }
        }
        catch (Exception ex)
        {
            Write($"[issue] error: {ex.Message}\n");
        }
    }

    private async Task HandleAddAsync(int taskId, IssueCommand.Add a, CancellationToken ct)
    {
        var entry = await _service.AppendAsync(
            taskId,
            new IssueCapture(a.Title, a.Kind, a.Where, Body: null, LoggedBy: "host-cli"),
            ct).ConfigureAwait(false);
        Write($"[issue] logged {entry.LocalId:D4} \"{entry.Title}\" (kind={IssueMarkdown.KindString(entry.Kind)}, status=open)\n");
        if (!string.IsNullOrWhiteSpace(entry.Where))
            Write($"        where: {entry.Where}\n");
    }

    private async Task HandleListAsync(int taskId, CancellationToken ct)
    {
        var entries = await _service.ListAsync(taskId, ct).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            Write("[issue] no entries yet.\n");
            return;
        }

        Write("ID     STATUS      KIND       TITLE\n");
        foreach (var e in entries)
        {
            Write($"{e.LocalId:D4}   {PadRight(IssueMarkdown.StatusString(e.Status), 11)} {PadRight(IssueMarkdown.KindString(e.Kind), 10)} {e.Title}\n");
            if (!string.IsNullOrWhiteSpace(e.Where))
                Write($"        where: {e.Where}\n");
            if (!string.IsNullOrWhiteSpace(e.Resolution))
                Write($"        resolution: {e.Resolution}\n");
        }
    }

    private async Task HandleResolveAsync(int taskId, IssueCommand.Resolve r, CancellationToken ct)
    {
        var updated = await _service.ResolveAsync(taskId, r.LocalId, r.Status, r.Note, ct).ConfigureAwait(false);
        if (updated is null)
        {
            Write($"[issue] {r.LocalId:D4} not found in task {taskId:D4}.\n");
            return;
        }
        Write($"[issue] {updated.LocalId:D4} -> {IssueMarkdown.StatusString(updated.Status)}");
        if (!string.IsNullOrWhiteSpace(r.Note)) Write($" ({r.Note})");
        Write("\n");
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
        [issue] commands (operate on the focused session's active task):
          /issue "<title>" [--kind <k>] [--where <ref>]
                                                log a new issue.
                                                <k> = bug | smell | followup | question (default: followup)
                                                <ref> = e.g. path/to/file.cs:42
          /issue list                           list this task's issues
          /issue resolve <n> [--as <status>] [<note...>]
                                                set issue <n> to <status> (default: resolved),
                                                optionally with a note. <status> = resolved | wont-fix | deferred.
                                                --note "<text>" also accepted for scripted callers.

        /task issue <args> is an alias for /issue <args>.

        """;

    private static string PadRight(string s, int width) =>
        s.Length >= width ? s : s + new string(' ', width - s.Length);
}
