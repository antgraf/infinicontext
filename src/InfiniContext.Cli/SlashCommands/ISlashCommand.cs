using InfiniContext.Cli.Issues;
using InfiniContext.Cli.Tasks;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.Tasks;
using Spectre.Console;

namespace InfiniContext.Cli.SlashCommands;

/// <summary>
/// One host-handled REPL slash-command. Each command self-describes via <see cref="Summary"/> +
/// <see cref="Details"/>, which the in-REPL <c>/help</c> renders directly. Adding a new command
/// to the <see cref="SlashCommandRegistry"/> automatically updates <c>/help</c>; there is no
/// other source of truth to keep aligned.
/// </summary>
internal interface ISlashCommand
{
    /// <summary>Primary trigger including the leading slash, e.g. <c>"/help"</c>.</summary>
    string Name { get; }

    /// <summary>Aliases including the leading slash, e.g. <c>["/?"]</c>. May be empty.</summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>One line. Shown in the <c>/help</c> summary table.</summary>
    string Summary { get; }

    /// <summary>Multi-line. Shown by <c>/help &lt;name&gt;</c>. May include usage examples.</summary>
    string Details { get; }

    /// <summary>
    /// Execute the command. <paramref name="argLine"/> is everything after the command name (or
    /// <c>""</c> if none). Exit / resume / switch are signalled via <see cref="IReplActions"/>.
    /// </summary>
    Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct);
}

/// <summary>
/// Mutating actions a slash-command can request of the host: exit, resume to a different
/// session, switch to a task, change the focused session. Implemented by
/// <c>MultiSessionHost</c>; stubbable in tests.
/// </summary>
internal interface IReplActions
{
    void RequestExit();
    void RequestResume(string sessionId);
    void RequestSwitch(TaskSwitchPlan plan);

    /// <summary>Switch the focused pane to the host session identified by
    /// <paramref name="hostSessionId"/>. No-op if the host has no such session.</summary>
    void RequestFocus(Guid hostSessionId);
}

/// <summary>
/// Readonly state and collaborators every slash-command needs. Constructed once by <c>Repl</c>
/// and reused across calls. Commands themselves are stateless.
/// </summary>
internal sealed record SlashCommandContext(
    IClaudeSession Session,
    Action<string> Write,
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

    /// <summary>The host's <see cref="IAnsiConsole"/>, when rich rendering is wanted (Spectre
    /// tables, panels). Plain string output continues to flow through <see cref="Write"/>.</summary>
    public IAnsiConsole? Console { get; init; }

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
}
