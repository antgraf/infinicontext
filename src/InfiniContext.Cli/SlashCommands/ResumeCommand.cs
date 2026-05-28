using InfiniContext.Core.Sessions;
using InfiniContext.Core.SlashCommands;

namespace InfiniContext.Cli.SlashCommands;

/// <summary>
/// Stage 2B Phase 4b: <c>/resume &lt;session_id&gt;</c> is non-destructive — it opens a new
/// session via <see cref="SessionManager.CreateAsync"/> with <c>ResumeSessionId</c> set and
/// focuses it. Existing sessions keep running off-screen. Without a multi-session manager
/// (older tests), falls back to the destructive Stage-1 path of asking the outer loop to
/// respawn (via <see cref="IReplActions.RequestResume"/>).
/// </summary>
internal sealed class ResumeCommand : ISlashCommand
{
    public string Name => "/resume";
    public IReadOnlyList<string> Aliases { get; } = Array.Empty<string>();
    public string Summary => "Open a session by id in a new pane (or resume the current one if no id).";
    public string Details =>
        """
        /resume [<session_id>]

        Spawns a new claude child with --resume <id> and focuses it. The argument must be
        a UUID — the host looks up the corresponding jsonl in claude's session store under
        ~/.claude/projects/<encoded-cwd>/. Without an argument, resumes the current
        session id (useful after a crash + restart loop).

        Existing sessions in this host keep running; /resume only adds a new pane.
        """;

    public async Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        var arg = argLine.Trim();
        var target = string.IsNullOrEmpty(arg)
            ? ctx.Session.SessionId?.ToString("D")
            : arg;

        if (string.IsNullOrEmpty(target))
        {
            ctx.Renderer.Write("[resume] no session id provided and no current session id is known yet.\n");
            return;
        }

        if (!Guid.TryParse(target, out _))
        {
            ctx.Renderer.Write($"[resume] '{target}' is not a valid session id (expected a UUID).\n");
            return;
        }

        // Non-destructive path: spawn a new pane via SessionManager.
        if (ctx.SessionManager is not null)
        {
            // The resumed session re-enters the original cwd via --resume; for the host's
            // SpawnRequest we use the focused session's cwd as a sensible default (the cwd is
            // a host-side identity hint; claude itself ignores it when ResumeSessionId is set).
            var cwd = ctx.FocusedHostSessionId is { } fid
                ? ctx.SessionManager.TryGet(fid)?.Cwd ?? Environment.CurrentDirectory
                : Environment.CurrentDirectory;
            var request = new SpawnRequest
            {
                Cwd = cwd,
                ResumeSessionId = target,
                Model = ctx.Settings.Session.Model,
                IncludePartialMessages = ctx.Settings.Session.IncludePartialMessages,
                ExtraArgs = ctx.Settings.Session.ExtraArgs,
            };
            try
            {
                var spawned = await ctx.SessionManager.CreateAsync(request, ct).ConfigureAwait(false);
                ctx.Renderer.Write($"[resume] opened {spawned.Id:D} as resume of {target}\n");
                ctx.Actions.RequestFocus(spawned.Id);
            }
            catch (Exception ex)
            {
                ctx.Renderer.Write($"[resume] failed: {ex.Message}\n");
            }
            return;
        }

        // Fallback: legacy destructive resume for tests/contexts without a SessionManager.
        ctx.Renderer.Write($"[resume] switching to session {target}…\n");
        ctx.Actions.RequestResume(target);
    }
}
