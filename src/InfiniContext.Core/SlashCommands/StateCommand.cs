using InfiniContext.Core.SlashCommands;

namespace InfiniContext.Core.SlashCommands;

public sealed class StateCommand : ISlashCommand
{
    public string Name => "/state";
    public IReadOnlyList<string> Aliases { get; } = Array.Empty<string>();
    public string Summary => "Show session state, open tool uses, unknown-event count, session id.";
    public string Details =>
        """
        /state

        Prints a snapshot of the underlying session state machine:
          - state name (Idle, Sending, Streaming, Compacting, Cancelling, Exited)
          - number of open tool uses (started but not yet resolved)
          - count of unknown events seen this session (parser drift signal)
          - session id (UUID), or <unassigned> until claude emits its first system/init.
        """;

    public Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        var s = ctx.Session.State;
        var session = ctx.Session.SessionId?.ToString("D") ?? "<unassigned>";
        var unknownTypes = ctx.Session.UnknownEventCount;
        var malformed = ctx.Session.MalformedLineCount;
        var active = ctx.SessionManager?.ActiveCount ?? 1;
        ctx.Renderer.Write($"[state] {s.State} | open_tools={s.OpenToolUses.Count} | drift=unk:{unknownTypes}/malformed:{malformed} | active_sessions={active} | session={session}\n");

        if (unknownTypes > 0 && ctx.Session.UnknownTypeHistogram.Count > 0)
        {
            var top = ctx.Session.UnknownTypeHistogram
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => $"{kv.Key}:{kv.Value}");
            ctx.Renderer.Write($"        unknown-types: {string.Join(", ", top)}\n");
        }
        return Task.CompletedTask;
    }
}
