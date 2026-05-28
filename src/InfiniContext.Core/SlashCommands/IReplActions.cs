using InfiniContext.Core.Tasks;

namespace InfiniContext.Core.SlashCommands;

/// <summary>
/// Mutating actions a slash-command can request of the host: exit, resume to a different
/// session, switch to a task, change the focused session. Implemented by the host (CLI's
/// <c>MultiSessionHost</c> or the GUI's equivalent); stubbable in tests.
/// </summary>
public interface IReplActions
{
    void RequestExit();
    void RequestResume(string sessionId);
    void RequestSwitch(TaskSwitchPlan plan);

    /// <summary>Switch the focused pane to the host session identified by
    /// <paramref name="hostSessionId"/>. No-op if the host has no such session.</summary>
    void RequestFocus(Guid hostSessionId);
}
