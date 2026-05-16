using Spectre.Console;

namespace InfiniContext.Cli.SlashCommands;

/// <summary>
/// Stage 2B Phase 4b: <c>/list</c> prints two Spectre tables — currently-active sessions and the
/// "recent" rows loaded from <c>~/.infinicontext/index.json</c>. When the host's
/// <see cref="SlashCommandContext.Console"/> is null (older test fixtures), falls back to plain
/// ASCII.
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
            ctx.Write("[list] multi-session not available in this host\n");
            return Task.CompletedTask;
        }

        var active = ctx.SessionManager.Active;
        var recent = ctx.SessionManager.RecentSessions;
        var console = ctx.Console;

        if (console is null)
        {
            // Plain-text fallback for tests / non-AnsiConsole environments.
            ctx.Write($"[list] active ({active.Count}):\n");
            foreach (var c in active)
                ctx.Write($"  {c.Id:D}  cwd={c.Cwd ?? "?"} unknown={c.Session.UnknownEventCount}\n");
            ctx.Write($"[list] recent ({recent.Count}):\n");
            foreach (var r in recent)
                ctx.Write($"  {r.SessionId}  cwd={r.Cwd}  last={r.LastActive:u}\n");
            return Task.CompletedTask;
        }

        var activeTable = new Table().Border(TableBorder.Rounded).Title("[bold]Active sessions[/]");
        activeTable.AddColumns("Host id", "Focused?", "Cwd", "Claude id", "Drift", "Cost");
        foreach (var c in active)
        {
            var focused = ctx.FocusedHostSessionId == c.Id ? "[green]*[/]" : "";
            var claudeId = c.Session.SessionId?.ToString("D") ?? "—";
            var cost = c.LastCostUsd is { } amt ? $"${amt:F4}" : "—";
            var drift = c.Session.UnknownEventCount + c.Session.MalformedLineCount;
            activeTable.AddRow(
                Markup.Escape(c.Id.ToString("D")),
                focused,
                Markup.Escape(c.Cwd ?? "—"),
                Markup.Escape(claudeId),
                drift.ToString(),
                Markup.Escape(cost));
        }
        console.Write(activeTable);

        var recentTable = new Table().Border(TableBorder.Rounded).Title("[bold]Recent sessions[/]");
        recentTable.AddColumns("Claude id", "Cwd", "Model", "Last active", "State");
        foreach (var r in recent)
        {
            recentTable.AddRow(
                Markup.Escape(r.SessionId),
                Markup.Escape(r.Cwd),
                Markup.Escape(r.Model ?? "—"),
                Markup.Escape(r.LastActive.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
                Markup.Escape(r.LastState ?? "—"));
        }
        console.Write(recentTable);
        return Task.CompletedTask;
    }
}
