using InfiniContext.Core.SlashCommands;

namespace InfiniContext.Cli.SlashCommands;

/// <summary>
/// User-triggered version of the host's context-pressure hard-threshold nudge. Injects a
/// synthetic user turn into the focused session telling the agent to write any pending edits
/// to the task folder (<c>progress.md</c>, <c>index.md</c>, any in-flight artifacts) and
/// acknowledge with <c>[flushed]</c>. The session stays open after the flush completes; the
/// user can <c>/quit</c> or <c>/task switch</c> to resume in a fresh session if they're
/// preparing to split the task.
///
/// <para>
/// Refuses when the focused session isn't bound to a task (mirrors the <c>/issue</c>
/// "no active task" semantics) -- there's no <c>.task/</c> folder to flush to.
/// </para>
/// </summary>
internal sealed class FlushSlashCommand : ISlashCommand
{
    public string Name => "/flush";
    public IReadOnlyList<string> Aliases { get; } = Array.Empty<string>();
    public string Summary => "Force the focused session's agent to write all pending task state to disk now.";

    public string Details =>
        """
        /flush

        Injects a synthetic user turn into the focused session telling the agent to:
          1. Update progress.md with where you are and what's left (lock-aware).
          2. Update index.md if any new artifacts exist (lock-aware).
          3. Acknowledge with "[flushed]" and stop.

        Use cases:
          - Finish in-flight actions before context fills.
          - Prepare to split the task (flush, then /quit or /task switch to a fresh session).

        Refuses if the focused session has no bound task.
        """;

    public async Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(argLine))
        {
            ctx.Renderer.Write("[flush] this command takes no arguments.\n");
            return;
        }

        if (ctx.ActiveTaskId is not int taskId)
        {
            ctx.Renderer.Write("[flush] no active task in this session. /flush only works in task-bound sessions.\n");
            return;
        }

        // The message mirrors the language the infinicontext-task skill already uses for the
        // hard-threshold nudge ("flush now ... update progress.md with where you are and what's
        // left, then exit"). The agent knows what to do.
        var nudge = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            FlushNudgeTemplate,
            taskId);

        try
        {
            await ctx.Session.SendUserMessageAsync(nudge, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Renderer.Write($"[flush] dispatch failed: {ex.Message}\n");
            return;
        }

        ctx.Renderer.Write($"[flush] requested for task {taskId:D4}; the agent will acknowledge with \"[flushed]\" when done.\n");
    }

    /// <summary>
    /// The synthetic user turn the host injects. Public-test-friendly format string; the only
    /// substitution is the task id (4-digit padded for stable wording).
    /// </summary>
    internal const string FlushNudgeTemplate =
        "Host /flush request for task {0:D4}. Please flush now: update progress.md with where you are and what's left " +
        "(acquire the lock first), update index.md if any new artifacts exist (lock-aware), then acknowledge with \"[flushed]\" and stop. " +
        "Do not start new work or ask clarifying questions.";
}
