using FluentAssertions;
using InfiniContext.Gui.ViewModels;

namespace InfiniContext.Tests.Gui;

/// <summary>
/// Section 5.2 of the testability design: CanExecute contract for the main VM's commands.
/// Also covers the operator-facing 'Agent busy' indicator under the new init-based gate +
/// queue-warning contract from design-gui-testability.md section 12 alternative path.
/// </summary>
public class MainWindowViewModelTests
{
    private static MainWindowViewModel BuildMainVm() =>
        new(new SynchronousUiDispatcher(), new TestUiTimerFactory());

    private static SessionRowViewModel BuildRow(SessionContextFactory f) =>
        new(f.Context, new SynchronousUiDispatcher(), new TestUiTimerFactory());

    [Fact]
    public void CanSendPrompt_false_when_no_focused_session()
    {
        var vm = BuildMainVm();
        vm.PromptText = "hello";

        vm.SendPromptCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CanSendPrompt_false_when_prompt_empty()
    {
        var vm = BuildMainVm();
        var f = new SessionContextFactory(activeTaskId: null);
        vm.FocusedSession = BuildRow(f);

        vm.PromptText = "";

        vm.SendPromptCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CanSendPrompt_true_for_taskbound_after_init_even_with_turn_in_flight()
    {
        // The central regression-protection test: under the new contract, the operator CAN
        // fire Send while the pointer turn is still streaming. The queue indicator surfaces
        // the buffering behaviour; Send itself is no longer disabled.
        var vm = BuildMainVm();
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);
        f.RaiseEvent(SessionContextFactory.MakeInit());
        // Drive the state machine into Streaming (a turn-in-flight state).
        f.Session.State.BeginSend();
        f.RaiseEvent(SessionContextFactory.MakeAssistantText());

        vm.FocusedSession = row;
        vm.PromptText = "follow-up";

        vm.SendPromptCommand.CanExecute(null).Should().BeTrue(
            "init flipped IsReady; the state machine is in Streaming but the new contract still " +
            "lets the operator queue a message - the queue indicator does the warning.");
        vm.IsAgentBusy.Should().BeTrue(
            "the in-flight turn should be visible as 'Agent busy - next message will queue'.");
    }

    [Fact]
    public void CanSendPrompt_true_when_taskbound_idle_after_result()
    {
        var vm = BuildMainVm();
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);
        f.RaiseEvent(SessionContextFactory.MakeInit());
        f.Session.State.BeginSend();
        f.RaiseEvent(SessionContextFactory.MakeAssistantText());
        f.RaiseEvent(SessionContextFactory.MakeResult());
        vm.FocusedSession = row;
        vm.PromptText = "hello";

        vm.SendPromptCommand.CanExecute(null).Should().BeTrue();
        vm.IsAgentBusy.Should().BeFalse("ResultEvent returned the state machine to Idle.");
    }

    [Fact]
    public void CanSendPrompt_true_for_free_session_immediately()
    {
        var vm = BuildMainVm();
        var f = new SessionContextFactory(activeTaskId: null);
        vm.FocusedSession = BuildRow(f);
        vm.PromptText = "hello";

        vm.SendPromptCommand.CanExecute(null).Should().BeTrue(
            "free sessions ready at ctor - operator can type the first prompt to wake claude up.");
        vm.IsAgentBusy.Should().BeFalse();
    }

    [Fact]
    public void CanSendPrompt_false_when_session_exits()
    {
        var vm = BuildMainVm();
        var f = new SessionContextFactory(activeTaskId: null);
        var row = BuildRow(f);
        vm.FocusedSession = row;
        vm.PromptText = "hello";

        vm.SendPromptCommand.CanExecute(null).Should().BeTrue();

        f.Session.State.HandleProcessExit(-1);

        vm.SendPromptCommand.CanExecute(null).Should().BeFalse(
            "sending to a dead child silently fails; IsAlive gates the Send button.");
    }

    [Fact]
    public void CanSendPrompt_false_taskbound_before_init()
    {
        var vm = BuildMainVm();
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);
        vm.FocusedSession = row;
        vm.PromptText = "hello";

        vm.SendPromptCommand.CanExecute(null).Should().BeFalse(
            "task-bound row hasn't seen init yet - no proof claude is reading stdin.");
        vm.IsAgentBusy.Should().BeFalse("state machine is still Idle; no in-flight turn yet.");
    }

    [Fact]
    public void IsAgentBusy_flips_when_focused_session_begins_send()
    {
        var vm = BuildMainVm();
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);
        vm.FocusedSession = row;

        vm.IsAgentBusy.Should().BeFalse("state machine starts Idle");

        f.Session.State.BeginSend();

        vm.IsAgentBusy.Should().BeTrue("transition into Sending should flip the indicator.");
    }

    [Fact]
    public void SendButtonTooltip_explains_queueing_while_agent_busy()
    {
        var vm = BuildMainVm();
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);
        f.RaiseEvent(SessionContextFactory.MakeInit());
        f.Session.State.BeginSend();
        vm.FocusedSession = row;

        vm.SendButtonTooltip.Should().Contain("queue",
            "the tooltip's job under the new contract is to surface stdin-queueing behaviour, " +
            "not to apologise for a disabled button.");
    }

    [Fact]
    public void SendButtonTooltip_shows_keyboard_shortcut_when_not_busy()
    {
        var vm = BuildMainVm();
        var f = new SessionContextFactory(activeTaskId: null);
        vm.FocusedSession = BuildRow(f);

        vm.SendButtonTooltip.Should().Contain("Ctrl+Enter");
    }
}
