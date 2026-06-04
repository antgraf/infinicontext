using InfiniContext.Core.Configuration;
using InfiniContext.Core.Issues;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.Tasks;

namespace InfiniContext.Core.SlashCommands;

/// <summary>
/// Readonly state and collaborators every slash-command needs. Constructed once by the host and
/// reused across calls. Commands themselves are stateless.
///
/// <para>
/// Slice B (renderer abstraction): output and rich rendering go through <see cref="Renderer"/>
/// (an <see cref="ISlashCommandRenderer"/>). The previous <c>Action&lt;string&gt; Write</c> and
/// <c>object? Console</c> surfaces are gone -- commands write only through the renderer. Slice C
/// promotes the handler classes (currently <c>object?</c>) into Core and tightens their typings.
/// </para>
/// </summary>
public sealed record SlashCommandContext(
    IClaudeSession Session,
    ISlashCommandRenderer Renderer,
    InfiniContextSettings Settings,
    string? EffectiveSettingsJson,
    Func<ApprovalMode>? GetApprovalMode,
    Func<ApprovalMode, ApprovalMode>? SwitchApprovalMode,
    TaskCommandHandler? TaskHandler,
    Func<int, CancellationToken, Task<TaskSwitchPlan?>>? PrepareTaskSwitch,
    Func<decimal?> GetLastCostUsd,
    Func<int?> GetLastNumTurns,
    IReplActions Actions,
    SlashCommandRegistry Registry)
{
    /// <summary>The host's session manager. Multi-session slash commands (<c>/n</c>, <c>/list</c>,
    /// <c>/focus</c>, the non-destructive <c>/resume</c>) reach <see cref="SessionManager.CreateAsync"/>
    /// through this. Null only in older tests that don't exercise multi-session paths.</summary>
    public SessionManager? SessionManager { get; init; }

    /// <summary>Host id of the currently focused session, when there is one. Slash commands that
    /// want to act on the focused session can compare against this without holding a reference.</summary>
    public Guid? FocusedHostSessionId { get; init; }

    /// <summary>The task id (if any) bound to the focused session. Read at slash-command dispatch
    /// time so commands that target the active task (notably <c>/issue</c>) don't need to
    /// re-resolve focus through <see cref="SessionManager"/>.</summary>
    public int? ActiveTaskId { get; init; }

    /// <summary>Issue-subsystem handler. Constructed when the task subsystem initializes
    /// successfully; null otherwise (the <c>/issue</c> surface prints an "unavailable" message
    /// in that case). Kept as an init-only property rather than a positional record param so
    /// callers (tests, future host modes) that don't care about the issue surface don't have to
    /// thread <c>null</c> through.</summary>
    public IssueCommandHandler? IssueHandler { get; init; }

    /// <summary>Register a task claim with the host's <see cref="TaskActivityCoordinator"/> when
    /// a slash command spawns a new task-bound session (currently only <c>/task spawn</c>).
    /// Returns the coordinator's response: <see cref="EnterTaskResult.GrantedResult"/> or
    /// <see cref="EnterTaskResult.RefusedResult"/>. Null when the task subsystem isn't wired up;
    /// commands that need it must handle the null case with a clear refusal.</summary>
    public Func<Guid, int, CancellationToken, Task<EnterTaskResult>>? TryEnterTask { get; init; }
}
