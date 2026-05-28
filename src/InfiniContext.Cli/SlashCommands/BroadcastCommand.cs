using InfiniContext.Core.Sessions;
using InfiniContext.Core.SlashCommands;

namespace InfiniContext.Cli.SlashCommands;

/// <summary>
/// Stage 2B Phase 6: <c>/broadcast &lt;targets&gt; &lt;text&gt;</c> fans one prompt out to multiple
/// active sessions. Targets are a comma-separated list of host-id prefixes (the same prefix form
/// <see cref="FocusCommand"/> accepts), or the literal <c>*</c> for all active sessions. The
/// command always reports a per-row toast so the user can spot a session that failed to receive.
///
/// <para>Slash commands (<c>/task switch</c>, <c>/resume</c>, etc.) are intentionally not
/// broadcast-able — they carry session-specific intent (D8). <c>/broadcast</c> only sends a
/// free-form prompt.</para>
/// </summary>
internal sealed class BroadcastCommand : ISlashCommand
{
    public string Name => "/broadcast";
    public IReadOnlyList<string> Aliases { get; } = new[] { "/bcast" };
    public string Summary => "Send one prompt to multiple active sessions.";
    public string Details =>
        """
        /broadcast <targets> <text>
        /broadcast * <text>

        <targets> is a comma-separated list of host-id prefixes (the form /list under
        "Active sessions" shows; the same form /focus accepts). Use * to fan out to every
        active session.

        Example:
          /broadcast 7a2b,93cf  run the test suite
          /broadcast * /clear

        Only the focused session's transcript echoes to the console as it streams; the
        others fill in the background and replay via /focus.
        """;

    public async Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        if (ctx.SessionManager is null)
        {
            ctx.Renderer.Write("[broadcast] multi-session not available in this host\n");
            return;
        }

        var (targetsArg, text) = SplitFirstWord(argLine);
        if (targetsArg.Length == 0 || text.Length == 0)
        {
            ctx.Renderer.Write("[broadcast] usage: /broadcast <targets|*> <text>\n");
            return;
        }

        var active = ctx.SessionManager.Active;
        if (active.Count == 0)
        {
            ctx.Renderer.Write("[broadcast] no active sessions\n");
            return;
        }

        List<SessionContext> targets;
        if (targetsArg == "*")
        {
            targets = active.ToList();
        }
        else
        {
            targets = new List<SessionContext>();
            var seen = new HashSet<Guid>();
            foreach (var raw in targetsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var matches = active.Where(c => c.Id.ToString("D").StartsWith(raw, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0)
                {
                    ctx.Renderer.Write($"[broadcast] no active session matches '{raw}'\n");
                    return;
                }
                if (matches.Count > 1)
                {
                    ctx.Renderer.Write($"[broadcast] '{raw}' is ambiguous ({matches.Count} matches); use /list and try a longer prefix\n");
                    return;
                }
                if (seen.Add(matches[0].Id)) targets.Add(matches[0]);
            }
        }

        if (targets.Count < 2)
        {
            ctx.Renderer.Write("[broadcast] needs at least two targets — use /n + a regular prompt for a single session\n");
            return;
        }

        IReadOnlyList<BroadcastSendResult> results;
        try
        {
            results = await ctx.SessionManager.BroadcastAsync(targets.Select(t => t.Id), text, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Renderer.Write($"[broadcast] dispatch failed: {ex.Message}\n");
            return;
        }

        var sent = results.Count(r => r.Outcome == BroadcastSendOutcome.Sent);
        var failed = results.Count(r => r.Outcome == BroadcastSendOutcome.Failed);
        var missing = results.Count(r => r.Outcome == BroadcastSendOutcome.NotFound);

        ctx.Renderer.Write($"[broadcast] sent to {sent}/{results.Count} session(s)");
        if (failed > 0) ctx.Renderer.Write($", {failed} failed");
        if (missing > 0) ctx.Renderer.Write($", {missing} not found");
        ctx.Renderer.Write("\n");

        foreach (var r in results.Where(r => r.Outcome != BroadcastSendOutcome.Sent))
        {
            var tag = r.Outcome == BroadcastSendOutcome.Failed ? "failed" : "not-found";
            var detail = r.Error?.Message;
            ctx.Renderer.Write(detail is null
                ? $"  - {r.HostSessionId:D} ({tag})\n"
                : $"  - {r.HostSessionId:D} ({tag}): {detail}\n");
        }
    }

    private static (string Targets, string Text) SplitFirstWord(string argLine)
    {
        var trimmed = argLine.TrimStart();
        if (trimmed.Length == 0) return ("", "");
        var space = trimmed.IndexOf(' ');
        if (space < 0) return (trimmed, "");
        return (trimmed[..space], trimmed[(space + 1)..].TrimStart());
    }
}
