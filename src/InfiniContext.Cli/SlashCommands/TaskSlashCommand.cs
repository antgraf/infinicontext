using InfiniContext.Cli.Issues;
using InfiniContext.Cli.Tasks;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.Tasks;

namespace InfiniContext.Cli.SlashCommands;

/// <summary>
/// Façade slash-command for the <c>/task</c> sub-grammar. The actual parser lives in
/// <see cref="TaskCommand"/> and the formatter in <see cref="TaskCommandHandler"/>; this class
/// only routes input + intercepts <c>switch</c> (which has to drive the host's outer loop).
///
/// Class name is <c>TaskSlashCommand</c> rather than <c>TaskCommand</c> to avoid collision with
/// the existing parser type. See <c>decisions.md</c> D12.
/// </summary>
internal sealed class TaskSlashCommand : ISlashCommand
{
    public string Name => "/task";
    public IReadOnlyList<string> Aliases { get; } = new[] { "/tasks" };
    public string Summary => "Manage host-side tasks (create, list, fork, switch, …). /help task for the full sub-grammar.";
    public string Details => TaskCommandHandler.HelpText.TrimEnd();

    public async Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        if (ctx.TaskHandler is null)
        {
            ctx.Write("[task] subsystem is unavailable (see startup warnings).\n");
            return;
        }

        // `/task issue ...` is the cross-namespace alias for `/issue ...` - route the remainder
        // straight into IssueCommand so both surfaces share one parser + handler. The user-facing
        // help is identical between the two forms.
        var trimmed = (argLine ?? string.Empty).TrimStart();
        if (trimmed.StartsWith("issue", StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == 5 || char.IsWhiteSpace(trimmed[5])))
        {
            if (ctx.IssueHandler is null)
            {
                ctx.Write("[issue] subsystem is unavailable (see startup warnings).\n");
                return;
            }
            var issueRaw = "/issue" + trimmed[5..];
            if (!IssueCommand.TryParse(issueRaw, out var issueCmd, out var issueErr))
            {
                ctx.Write($"[issue] {issueErr}\n");
                return;
            }
            await ctx.IssueHandler.HandleAsync(issueCmd, ctx.ActiveTaskId, ct).ConfigureAwait(false);
            return;
        }

        // TaskCommand.TryParse accepts both "/task <subcmd>" and the bare "<subcmd>" form.
        // We pass the arg portion directly so the parser doesn't see the "/task" prefix twice.
        var raw = string.IsNullOrWhiteSpace(argLine) ? "/task" : "/task " + argLine.Trim();

        if (!TaskCommand.TryParse(raw, out var cmd, out var error))
        {
            ctx.Write($"[task] {error}\n");
            return;
        }

        // /task switch is the one subcommand that needs to drive the outer host loop (re-spawn
        // a fresh ClaudeSession with a FirstUserMessage pointer). Intercept here so the rest of
        // the handler stays presentation-only.
        if (cmd is TaskCommand.Switch sw)
        {
            await TryStartTaskSwitchAsync(ctx, sw.Id, ct).ConfigureAwait(false);
            return;
        }

        // /task new (REPL) chains create-then-switch: after the task is persisted, immediately
        // re-spawn the current session bound to it (task 0005 issue 0010). The bare CLI path
        // (TaskCommandHandler.HandleAsync) keeps the create-only behaviour so non-REPL invocations
        // and unit tests don't pick up the side effect.
        if (cmd is TaskCommand.New newCmd)
        {
            TaskRecord record;
            try
            {
                record = await ctx.TaskHandler.HandleNewAsync(newCmd.Description, newCmd.Home, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ctx.Write($"[task] new failed: {ex.Message}\n");
                return;
            }
            await TryStartTaskSwitchAsync(ctx, record.Id, ct).ConfigureAwait(false);
            return;
        }

        // /task review needs the focused session's active task id when the user didn't pass one.
        // Route directly to HandleReviewAsync with ctx.ActiveTaskId; falls back gracefully when
        // both are null (handler prints a refusal).
        if (cmd is TaskCommand.Review reviewCmd)
        {
            await ctx.TaskHandler.HandleReviewAsync(reviewCmd, ctx.ActiveTaskId, ct).ConfigureAwait(false);
            return;
        }

        // /task flush: send a user-message into the focused session asking the agent to flush
        // task documents now (task 0005 issue 0012). Same channel ThresholdInjector uses for the
        // automatic context-pressure nudges, just operator-triggered.
        if (cmd is TaskCommand.Flush)
        {
            await TryFlushFocusedSessionAsync(ctx, ct).ConfigureAwait(false);
            return;
        }

        // /task spawn: same task-binding plan as /task switch, but creates a NEW SessionContext
        // alongside the current one (a parallel pane) rather than replacing it. Useful when the
        // user wants to /flush the current session and start fresh on the same task without
        // losing the existing pane, or to fan out work across multiple tasks in parallel.
        if (cmd is TaskCommand.Spawn spawnCmd)
        {
            await TrySpawnTaskSessionAsync(ctx, spawnCmd.Id, ct).ConfigureAwait(false);
            return;
        }

        await ctx.TaskHandler.HandleAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Inject a single user-turn into the focused session asking the agent to flush task
    /// artifacts now (progress.md, index.md, decisions.md, etc.). Phrased so the agent treats it
    /// as a deliberate operator action rather than a context-pressure rescue (so it doesn't also
    /// exit). The actual injection goes through <see cref="IClaudeSession.SendUserMessageAsync"/>
    /// тАФ the same channel <c>ThresholdInjector</c> uses for the soft/hard threshold nudges.
    /// </summary>
    private static async Task TryFlushFocusedSessionAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var message = ctx.ActiveTaskId is { } activeId
            ? $"Operator-requested flush. Update this task's documents now (progress.md, index.md, and any other artifacts you've changed since the last flush) to reflect everything you've done so far. Task id: {activeId:D4}. Keep working after the flush; this is not a context-pressure handoff."
            : "Operator-requested flush. If this session has any task artifacts in flight, update them now (progress.md, index.md, etc.) to reflect everything you've done since the last flush. Keep working after the flush; this is not a context-pressure handoff.";

        ctx.Write("[task] flushing focused session...\n");
        try
        {
            await ctx.Session.SendUserMessageAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Write($"[task] flush failed: {ex.Message}\n");
        }
    }

    private static async Task TryStartTaskSwitchAsync(SlashCommandContext ctx, int id, CancellationToken ct)
    {
        var plan = await LoadTaskSwitchPlanAsync(ctx, id, "switch", ct).ConfigureAwait(false);
        if (plan is null) return;

        // The host's outer loop spawns the next session with cwd = plan.Task.HomeCwd, so the
        // relative `.task/<id>-<slug>/` pointer in FirstUserMessage resolves correctly regardless
        // of where InfiniContext itself was launched from. Tasks own their home; sessions follow.
        ctx.Write($"[task] switching to {plan.Task.Id:D4} \"{plan.Task.Title}\"\n");
        WriteCwdAndDrift(ctx, plan);

        ctx.Actions.RequestSwitch(plan);
    }

    private static async Task TrySpawnTaskSessionAsync(SlashCommandContext ctx, int id, CancellationToken ct)
    {
        if (ctx.SessionManager is null)
        {
            ctx.Write("[task] spawn is unavailable in this context (no session manager).\n");
            return;
        }

        // One-session-per-task (D9) up front: refuse if another live pane already binds this
        // task. The host-level TaskActivityCoordinator would refuse anyway after CreateAsync,
        // but we'd rather print a clean message than spin up and then tear down a session.
        foreach (var s in ctx.SessionManager.Active)
        {
            if (s.ActiveTaskId == id)
            {
                ctx.Write($"[task] {id:D4} is already active in session {s.Id:D}. Use /focus {s.Id:D} to switch to it.\n");
                return;
            }
        }

        var plan = await LoadTaskSwitchPlanAsync(ctx, id, "spawn", ct).ConfigureAwait(false);
        if (plan is null) return;

        var request = new SpawnRequest
        {
            Cwd = plan.Task.HomeCwd,
            FirstUserMessage = plan.FirstUserMessage,
            ActiveTaskId = plan.Task.Id,
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
            ctx.Write($"[task] spawn failed: {ex.Message}\n");
            return;
        }

        // Register the task claim with the coordinator so the row flips to InProgress and a
        // future spawn on the same task is refused at the host level. If the coordinator hook
        // isn't wired (no task subsystem), the local-Active check above is our only guard.
        if (ctx.TryEnterTask is not null)
        {
            EnterTaskResult enter;
            try
            {
                enter = await ctx.TryEnterTask(newSession.Id, plan.Task.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ctx.Write($"[task] spawn: coordinator enter failed: {ex.Message}\n");
                await ctx.SessionManager.CloseAsync(newSession.Id).ConfigureAwait(false);
                return;
            }

            if (enter is EnterTaskResult.RefusedResult refused)
            {
                ctx.Write($"[task] {refused.TaskId:D4} is already claimed by session {refused.HolderSessionId:D}; rolling back the spawn.\n");
                await ctx.SessionManager.CloseAsync(newSession.Id).ConfigureAwait(false);
                return;
            }
        }

        ctx.Write($"[task] spawned session {newSession.Id:D} bound to {plan.Task.Id:D4} \"{plan.Task.Title}\"\n");
        WriteCwdAndDrift(ctx, plan);

        ctx.Actions.RequestFocus(newSession.Id);
    }

    /// <summary>
    /// Shared plan-loading + error-printing path for <c>/task switch</c> and <c>/task spawn</c>
    /// (arch-012). Returns <c>null</c> when the task subsystem is unavailable, when the task
    /// doesn't resolve, or when <see cref="SlashCommandContext.PrepareTaskSwitch"/> threw ╤В╨Р╨д
    /// the user-facing message is already printed in each of those cases. <paramref name="verb"/>
    /// is the user-visible operation name ("switch" / "spawn") used in error strings.
    /// </summary>
    private static async Task<TaskSwitchPlan?> LoadTaskSwitchPlanAsync(SlashCommandContext ctx, int id, string verb, CancellationToken ct)
    {
        if (ctx.PrepareTaskSwitch is null)
        {
            ctx.Write($"[task] {verb} is unavailable in this context.\n");
            return null;
        }

        TaskSwitchPlan? plan;
        try
        {
            plan = await ctx.PrepareTaskSwitch(id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Write($"[task] {verb} failed: {ex.Message}\n");
            return null;
        }

        if (plan is null)
        {
            ctx.Write($"[task] {id:D4} not found.\n");
            return null;
        }

        return plan;
    }

    /// <summary>
    /// Shared post-plan output between <c>/task switch</c> and <c>/task spawn</c>: prints the
    /// task's <c>HomeCwd</c> when it differs from the current directory and any drift the plan
    /// captured. Both lines are indented to align under the per-verb status line printed above.
    /// </summary>
    private static void WriteCwdAndDrift(SlashCommandContext ctx, TaskSwitchPlan plan)
    {
        if (!PathsAreEquivalent(plan.Task.HomeCwd, Environment.CurrentDirectory))
            ctx.Write($"       cwd: {plan.Task.HomeCwd}\n");
        if (plan.Drift.Count > 0)
            ctx.Write($"       drift: {string.Join(", ", plan.Drift)} (will ask the agent to reconcile)\n");
    }

    private static bool PathsAreEquivalent(string a, string b)
    {
        // Normalise via GetFullPath (resolves relative segments and trailing slashes). Stage 2A
        // is Windows-only, so case-insensitive compare is correct here; revisit when 2B lands.
        var fa = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase);
    }
}
