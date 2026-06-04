using InfiniContext.Core.SlashCommands;

namespace InfiniContext.Core.SlashCommands;

/// <summary>
/// Stage 2B Phase 4b: <c>/focus &lt;host-id-or-prefix&gt;</c> switches the focused pane to the
/// named session. A short prefix is acceptable as long as it matches exactly one active session.
/// True <c>Tab</c> keybinding requires <c>Console.ReadKey</c>-level input handling — deferred to
/// Phase 4c+.
/// </summary>
public sealed class FocusCommand : ISlashCommand
{
    public string Name => "/focus";
    public IReadOnlyList<string> Aliases { get; } = Array.Empty<string>();
    public string Summary => "Switch the focused pane to <host-id-or-prefix>.";
    public string Details =>
        """
        /focus <host-id-or-prefix>

        Pick a session out of the active set by host id (the UUID printed by /list under
        "Active sessions"). A unique prefix is accepted. On success, the focused pane
        switches and the new session's recent transcript is replayed below.
        """;

    public Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        if (ctx.SessionManager is null)
        {
            ctx.Renderer.Write("[focus] multi-session not available in this host\n");
            return Task.CompletedTask;
        }
        var arg = argLine.Trim();
        if (arg.Length == 0)
        {
            ctx.Renderer.Write("[focus] usage: /focus <host-id-or-prefix>\n");
            return Task.CompletedTask;
        }

        var active = ctx.SessionManager.Active;
        var matches = active.Where(c => c.Id.ToString("D").StartsWith(arg, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            ctx.Renderer.Write($"[focus] no active session matches '{arg}'\n");
            return Task.CompletedTask;
        }
        if (matches.Count > 1)
        {
            ctx.Renderer.Write($"[focus] '{arg}' is ambiguous ({matches.Count} matches); use /list and try a longer prefix\n");
            return Task.CompletedTask;
        }
        ctx.Actions.RequestFocus(matches[0].Id);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Stage 2B Phase 4b: <c>/next</c> cycles focus to the next active session (substitute for
/// <c>Tab</c> until Phase 4c lifts input into <c>Console.ReadKey</c>).
/// </summary>
public sealed class FocusNextCommand : ISlashCommand
{
    public string Name => "/next";
    public IReadOnlyList<string> Aliases { get; } = Array.Empty<string>();
    public string Summary => "Cycle focus to the next active session.";
    public string Details => "/next\n\nMoves the focused pane forward through the active session list.";

    public Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct) =>
        FocusCycle.RunAsync(ctx, +1);
}

/// <summary>
/// Stage 2B Phase 4b: <c>/prev</c> cycles focus backward (the complement of <c>/next</c>).
/// </summary>
public sealed class FocusPrevCommand : ISlashCommand
{
    public string Name => "/prev";
    public IReadOnlyList<string> Aliases { get; } = Array.Empty<string>();
    public string Summary => "Cycle focus to the previous active session.";
    public string Details => "/prev\n\nMoves the focused pane backward through the active session list.";

    public Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct) =>
        FocusCycle.RunAsync(ctx, -1);
}

public static class FocusCycle
{
    public static Task RunAsync(SlashCommandContext ctx, int direction)
    {
        if (ctx.SessionManager is null)
        {
            ctx.Renderer.Write("[focus] multi-session not available in this host\n");
            return Task.CompletedTask;
        }
        var active = ctx.SessionManager.Active;
        if (active.Count <= 1)
        {
            ctx.Renderer.Write("[focus] only one active session\n");
            return Task.CompletedTask;
        }
        // Find current focus index (or pretend we're at -1 if the focused id is unknown to the
        // manager — shouldn't happen, but defensive).
        var current = ctx.FocusedHostSessionId is { } fid
            ? active.Select((c, i) => (c, i)).FirstOrDefault(p => p.c.Id == fid).i
            : -1;
        var n = active.Count;
        var nextIndex = ((current + direction) % n + n) % n; // positive modulo
        ctx.Actions.RequestFocus(active[nextIndex].Id);
        return Task.CompletedTask;
    }
}
