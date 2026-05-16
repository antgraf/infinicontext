using InfiniContext.Core.Sessions;

namespace InfiniContext.Cli.SlashCommands;

/// <summary>
/// Stage 2B Phase 4b: <c>/n &lt;cwd&gt;</c> opens a new session in <see cref="SessionManager"/>.
/// D11: the cwd argument is required — explicit only. The new session is auto-focused so the
/// user immediately sees it in the active pane.
/// </summary>
internal sealed class NewSessionCommand : ISlashCommand
{
    public string Name => "/n";
    public IReadOnlyList<string> Aliases { get; } = new[] { "/new" };
    public string Summary => "Open a new claude session in <cwd> and focus it.";
    public string Details =>
        """
        /n <cwd>

        Spawns a new claude child process rooted at <cwd> and switches the focused pane to
        the new session. The cwd is required — there is no default (the host's launch cwd
        would be a surprising default for a multi-session host).

        Existing sessions keep running off-screen; their transcripts continue to fill so
        you can /focus back to them and see what happened while they were hidden.
        """;

    public async Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        if (ctx.SessionManager is null)
        {
            ctx.Write("[n] multi-session not available in this host\n");
            return;
        }

        var cwd = argLine.Trim();
        if (cwd.Length == 0)
        {
            ctx.Write("[n] usage: /n <cwd>\n");
            return;
        }
        if (!Directory.Exists(cwd))
        {
            ctx.Write($"[n] cwd does not exist: {cwd}\n");
            return;
        }

        var request = new SpawnRequest
        {
            Cwd = cwd,
            Model = ctx.Settings.Session.Model,
            IncludePartialMessages = ctx.Settings.Session.IncludePartialMessages,
            ExtraArgs = ctx.Settings.Session.ExtraArgs,
        };

        SessionContext newSession;
        try
        {
            newSession = await ctx.SessionManager.CreateAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Write($"[n] failed to start session: {ex.Message}\n");
            return;
        }

        ctx.Write($"[n] opened {newSession.Id:D} at {cwd}\n");
        ctx.Actions.RequestFocus(newSession.Id);
    }
}
