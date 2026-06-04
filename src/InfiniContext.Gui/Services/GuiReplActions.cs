using InfiniContext.Core.SlashCommands;
using InfiniContext.Core.Tasks;

namespace InfiniContext.Gui.Services;

/// <summary>
/// GUI implementation of <see cref="IReplActions"/> - the mutating host actions a slash-command can
/// request. The CLI's <c>MultiSessionHost</c> implements the same interface; here each action is a
/// delegate the <c>MainWindowViewModel</c> supplies so the actions plug into the GUI's focus model
/// and spawn machinery without this class referencing the VM directly.
/// </summary>
public sealed class GuiReplActions : IReplActions
{
    private readonly Action _requestExit;
    private readonly Action<string> _requestResume;
    private readonly Action<TaskSwitchPlan> _requestSwitch;
    private readonly Action<Guid> _requestFocus;

    public GuiReplActions(
        Action requestExit,
        Action<string> requestResume,
        Action<TaskSwitchPlan> requestSwitch,
        Action<Guid> requestFocus)
    {
        _requestExit = requestExit ?? throw new ArgumentNullException(nameof(requestExit));
        _requestResume = requestResume ?? throw new ArgumentNullException(nameof(requestResume));
        _requestSwitch = requestSwitch ?? throw new ArgumentNullException(nameof(requestSwitch));
        _requestFocus = requestFocus ?? throw new ArgumentNullException(nameof(requestFocus));
    }

    public void RequestExit() => _requestExit();
    public void RequestResume(string sessionId) => _requestResume(sessionId);
    public void RequestSwitch(TaskSwitchPlan plan) => _requestSwitch(plan);
    public void RequestFocus(Guid hostSessionId) => _requestFocus(hostSessionId);
}
