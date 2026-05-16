using InfiniContext.Core.Tasks;

namespace InfiniContext.Cli.Ui;

/// <summary>
/// Outcome of one <see cref="MultiSessionHost.RunAsync"/> invocation. The outer loop in
/// <c>Program.cs</c> reads <see cref="ResumeSessionId"/> or <see cref="SwitchPlan"/> to decide
/// whether to relaunch a fresh session inside the same host process. At most one of those two
/// is non-null.
///
/// <para>Stage 2B Phase 4a: this replaces the nested <c>Repl.ReplResult</c> record so Phase 4b's
/// multi-pane driver can return the same shape without depending on the now-removed
/// <c>Repl</c> class.</para>
/// </summary>
public sealed record HostResult(int ExitCode, string? ResumeSessionId, TaskSwitchPlan? SwitchPlan = null);
