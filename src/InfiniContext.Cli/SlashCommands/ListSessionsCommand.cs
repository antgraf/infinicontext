using InfiniContext.Core.SlashCommands;

namespace InfiniContext.Cli.SlashCommands;

/// <summary>
/// Stage 2B Phase 4b: <c>/list</c> prints two tables -- currently-active sessions and the
/// "recent" rows loaded from <c>~/.infinicontext/index.json</c>. Output is routed through the
/// shared <see cref="ISlashCommandRenderer.WriteTable"/> seam so the CLI and GUI surfaces share
/// the same command implementation.
/// </summary>
internal sealed class ListSessionsCommand : ISlashCommand
{
    public string Name => "/list";
    public IReadOnlyList<string> Aliases { get; } = new[] { "/ls" };
    public string Summary => "List active and recent sessions.";
    public string Details =>
        """
        /list

        Prints a table of currently-active sessions (in this host) and, below it, a table
        of recent sessions from the host index file. Use the host id from the "active"
        table with /focus <id> to switch panes; use the claude session id from the
        "recent" table with /resume <id> to reopen a closed session.
        """;

    public Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        if (ctx.SessionManager is null)
        {
            ctx.Renderer.Write("[list] multi-session not available in this host\n");
            return Task.CompletedTask;
        }

        var active = ctx.SessionManager.Active;
        var recent = ctx.SessionManager.RecentSessions;

        var activeRows = active.Select(c => (IReadOnlyList<string>)new[]
        {
            c.Id.ToString("D"),
            ctx.FocusedHostSessionId == c.Id ? "*" : "",
            c.Cwd ?? "-",
            c.Session.SessionId?.ToString("D") ?? "-",
            (c.Session.UnknownEventCount + c.Session.MalformedLineCount).ToString(),
            c.LastCostUsd is { } amt ? $"${amt:F4}" : "-",
        }).ToList();
        ctx.Renderer.WriteTable(
            title: "Active sessions",
            headers: new[] { "Host id", "Focused?", "Cwd", "Claude id", "Drift", "Cost" },
            rows: activeRows);

        var recentRows = recent.Select(r => (IReadOnlyList<string>)new[]
        {
            r.SessionId,
            r.Cwd,
            r.Model ?? "-",
            r.LastActive.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            r.LastState ?? "-",
        }).ToList();
        ctx.Renderer.WriteTable(
            title: "Recent sessions",
            headers: new[] { "Claude id", "Cwd", "Model", "Last active", "State" },
            rows: recentRows);

        return Task.CompletedTask;
    }
}
