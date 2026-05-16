using InfiniContext.Core.Tasks;

namespace InfiniContext.Core.Sessions;

/// <summary>
/// Per-iteration state for the host's session-spawn loop (Stage 1 single-session, Stage 2A
/// task-switch + resume). Owns the four scalars that drive the next iteration:
/// <see cref="CurrentSessionCwd"/>, <see cref="ResumeSessionId"/>,
/// <see cref="PendingFirstUserMessage"/>, <see cref="ActiveTaskId"/>.
///
/// <para>
/// Stage 2B Phase 1: this lifts the scalars out of <c>Program.cs</c> so Phase 2's
/// <c>SessionManager</c> can key one of these per session without tripping over globals.
/// </para>
/// </summary>
public sealed class HostLoopState
{
    private readonly string _launchCwd;

    /// <summary>Working directory for the next session. Defaults to the host's launch cwd;
    /// overridden to a task's <see cref="TaskRecord.HomeCwd"/> on <see cref="OnSwitchPlan"/>;
    /// reset to launch cwd on <see cref="OnResume"/> (resume isn't task-bound).</summary>
    public string CurrentSessionCwd { get; private set; }

    /// <summary>If set, the next session spawns with <c>--resume &lt;id&gt;</c>. Mutually
    /// exclusive with <see cref="PendingFirstUserMessage"/> per the host loop's
    /// switch-vs-resume branching.</summary>
    public string? ResumeSessionId { get; private set; }

    /// <summary>If set, the next session writes this string to stdin after the first
    /// <c>system/init</c> event. Used to pre-prompt a task-bound session with the
    /// <c>.task/&lt;id&gt;-&lt;slug&gt;/</c> pointer.</summary>
    public string? PendingFirstUserMessage { get; private set; }

    /// <summary>Task id currently held by the loop. Drives the InProgress/Idle status flips
    /// that <c>TaskActivityCoordinator</c> performs around each iteration.</summary>
    public int? ActiveTaskId { get; private set; }

    public HostLoopState(string launchCwd, string? initialResumeSessionId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(launchCwd);
        _launchCwd = launchCwd;
        CurrentSessionCwd = launchCwd;
        ResumeSessionId = initialResumeSessionId;
    }

    /// <summary>
    /// A <c>/task switch</c> result reached the host. Drop any pending resume id (a switch
    /// starts a fresh conversation), stash the pointer as the next session's first user
    /// message, claim the task, and follow the task to its home cwd.
    /// </summary>
    public void OnSwitchPlan(TaskSwitchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ResumeSessionId = null;
        PendingFirstUserMessage = plan.FirstUserMessage;
        ActiveTaskId = plan.Task.Id;
        CurrentSessionCwd = plan.Task.HomeCwd;
    }

    /// <summary>
    /// A <c>/resume &lt;id&gt;</c> result reached the host. The new session re-attaches to a
    /// prior transcript, so no task binding carries over and the cwd resets to the host's
    /// launch directory.
    /// </summary>
    public void OnResume(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ResumeSessionId = sessionId;
        PendingFirstUserMessage = null;
        ActiveTaskId = null;
        CurrentSessionCwd = _launchCwd;
    }

    /// <summary>
    /// The previous session ended without requesting a switch or resume. The pointer and
    /// resume id are consumed; the active task — if any — is dropped (the caller flips it
    /// back to Idle through <c>TaskActivityCoordinator.LeaveAsync</c> before calling this).
    /// </summary>
    public void OnExit()
    {
        ResumeSessionId = null;
        PendingFirstUserMessage = null;
        ActiveTaskId = null;
        CurrentSessionCwd = _launchCwd;
    }
}
