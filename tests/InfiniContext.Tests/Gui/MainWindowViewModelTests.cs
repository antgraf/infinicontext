using FluentAssertions;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.Tasks;
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

    [Fact]
    public void ApprovalModes_lists_all_four_modes_in_enum_order()
    {
        // The status-strip ComboBox binds to this list; order matches the ApprovalMode enum so
        // operators see Ask first (the safe default) and Auto last (newest / most subtle).
        var vm = BuildMainVm();

        vm.ApprovalModes.Should().Equal(
            ApprovalMode.Ask, ApprovalMode.Deny, ApprovalMode.Allow, ApprovalMode.Auto);
    }

    [Fact]
    public void CurrentApprovalMode_setter_raises_PropertyChanged()
    {
        // Without a host the setter is a no-op for the switch call, but PropertyChanged must
        // still fire so the ComboBox round-trips.
        var vm = BuildMainVm();
        var fired = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        vm.CurrentApprovalMode = ApprovalMode.Allow;

        fired.Should().Contain(nameof(MainWindowViewModel.CurrentApprovalMode));
        vm.CurrentApprovalMode.Should().Be(ApprovalMode.Allow);
    }

    [Fact]
    public void CurrentApprovalMode_starts_at_ask_default_for_hostless_vm()
    {
        // The host-attached ctor seeds from SessionHost.CurrentApprovalMode; this test pins the
        // hostless default so the ComboBox has a sensible initial selection in design preview /
        // tests / placeholder VM swap.
        var vm = BuildMainVm();

        vm.CurrentApprovalMode.Should().Be(ApprovalMode.Ask);
    }

    // ---------------------------------------------------------------------
    // Task 0013 Slice 2: auto-rotation proposal handling.
    //
    // The Approved-with-plan path drives _host.CreateSessionAsync which requires a real
    // SessionHost; we cover that via manual GUI smoke per plan.md's "skip the VM test and
    // rely on the Core service tests" escape hatch. These three cases pin the non-spawn
    // branches (Refused / Failed / Approved-but-task-not-found) -- pure StatusText updates
    // the hostless VM can exercise without spinning Avalonia or the host.
    // ---------------------------------------------------------------------

    private static AutoRotationProposal MakeRefusedProposal(int taskId = 42)
    {
        var f = new SessionContextFactory(activeTaskId: taskId, cwd: "/repo");
        return new AutoRotationProposal(
            Session: f.Context,
            TaskId: taskId,
            Reason: RotationReason.Hard,
            Status: AutoRotationStatus.Refused,
            Plan: null,
            RefusedCap: 3,
            RefusedWindow: TimeSpan.FromMinutes(5),
            Failure: null);
    }

    private static AutoRotationProposal MakeFailedProposal(int taskId = 42, string message = "db locked")
    {
        var f = new SessionContextFactory(activeTaskId: taskId, cwd: "/repo");
        return new AutoRotationProposal(
            Session: f.Context,
            TaskId: taskId,
            Reason: RotationReason.Hard,
            Status: AutoRotationStatus.Failed,
            Plan: null,
            RefusedCap: null,
            RefusedWindow: null,
            Failure: new InvalidOperationException(message));
    }

    private static AutoRotationProposal MakeApprovedNoPlan(int taskId = 42)
    {
        var f = new SessionContextFactory(activeTaskId: taskId, cwd: "/repo");
        return new AutoRotationProposal(
            Session: f.Context,
            TaskId: taskId,
            Reason: RotationReason.Soft,
            Status: AutoRotationStatus.Approved,
            Plan: null,
            RefusedCap: null,
            RefusedWindow: null,
            Failure: null);
    }

    [Fact]
    public void AutoRotation_refused_updates_status_text_with_cap_and_window()
    {
        var vm = BuildMainVm();
        var proposal = MakeRefusedProposal(taskId: 99);

        vm.OnAutoRotationProposed(proposal);

        vm.StatusText.Should().Contain("refused");
        vm.StatusText.Should().Contain("3");           // cap
        vm.StatusText.Should().Contain("5 min");        // window
        vm.StatusText.Should().Contain("0099");         // task id formatted D4
    }

    [Fact]
    public void AutoRotation_failed_updates_status_text_with_failure_message()
    {
        var vm = BuildMainVm();
        var proposal = MakeFailedProposal(taskId: 7, message: "db locked");

        vm.OnAutoRotationProposed(proposal);

        vm.StatusText.Should().Contain("failed");
        vm.StatusText.Should().Contain("db locked");
        vm.StatusText.Should().Contain("0007");
    }

    [Fact]
    public void AutoRotation_approved_without_plan_updates_status_text_task_not_found()
    {
        var vm = BuildMainVm();
        var proposal = MakeApprovedNoPlan(taskId: 12);

        vm.OnAutoRotationProposed(proposal);

        vm.StatusText.Should().Contain("not found");
        vm.StatusText.Should().Contain("0012");
    }

    [Fact]
    public void AutoRotation_marshals_through_dispatcher_when_off_thread()
    {
        var tracking = new TrackingUiDispatcher { CheckAccessReturns = false };
        var vm = new MainWindowViewModel(tracking, new TestUiTimerFactory());
        var proposal = MakeRefusedProposal();

        vm.OnAutoRotationProposed(proposal);

        // The handler runs on the source session's state-machine thread; the VM must Post the
        // observable mutation back. CheckAccess gates the branch; Post does the marshal. Both
        // counts must be non-zero, matching the existing OnSessionClosed marshal contract.
        tracking.CheckAccessCount.Should().BeGreaterThan(0);
        tracking.PostCount.Should().BeGreaterThan(0);
        vm.StatusText.Should().Contain("refused");
    }

    // ---------------------------------------------------------------------
    // Task 0016: per-session / per-task model selection (Slice B + C UI logic).
    //
    // The respawn itself (ApplyModelAsync -> CloseSessionAsync + spawn) needs a real SessionHost,
    // covered by manual GUI smoke per plan.md's escape hatch. These cases pin the pure VM logic
    // a hostless VM can exercise: the choice list, focus-sync, and the Apply CanExecute gate.
    // ---------------------------------------------------------------------

    [Fact]
    public void BuildModelChoices_leads_with_host_default_then_catalog()
    {
        var vm = BuildMainVm();

        var choices = vm.BuildModelChoices();

        choices.Should().NotBeEmpty();
        choices[0].Id.Should().BeNull("the first choice is the 'host default' sentinel (no per-task pin).");
        choices[0].Label.Should().Be("Host default");
        // Every catalog model is represented by a non-null-id choice.
        foreach (var m in KnownModels.Catalog)
            choices.Should().Contain(c => c.Id == m.Id);
        choices.Should().HaveCount(KnownModels.Catalog.Count + 1);
    }

    [Fact]
    public void Focusing_a_session_preselects_its_model_and_Apply_is_disabled()
    {
        var vm = BuildMainVm();
        var f = new SessionContextFactory(activeTaskId: null, cwd: "/repo", model: KnownModels.Main);

        vm.FocusedSession = BuildRow(f);

        vm.SelectedInspectorModel.Should().NotBeNull();
        vm.SelectedInspectorModel!.Id.Should().Be(KnownModels.Main,
            "the dropdown pre-selects the running session's model on focus.");
        vm.CanApplyModel.Should().BeFalse("selection equals the running model, so there's nothing to apply.");
        vm.ApplyModelCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Selecting_a_different_model_enables_Apply()
    {
        var vm = BuildMainVm();
        var f = new SessionContextFactory(activeTaskId: null, cwd: "/repo", model: KnownModels.Main);
        vm.FocusedSession = BuildRow(f);

        // Pick a catalog entry whose id differs from the running model.
        var other = vm.BuildModelChoices().First(c => c.Id is not null && c.Id != KnownModels.Main);
        vm.SelectedInspectorModel = other;

        vm.CanApplyModel.Should().BeTrue("selection now differs from the running model.");
        vm.ApplyModelCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Apply_is_disabled_when_focused_session_has_exited()
    {
        var vm = BuildMainVm();
        var f = new SessionContextFactory(activeTaskId: null, cwd: "/repo", model: KnownModels.Main);
        vm.FocusedSession = BuildRow(f);
        var other = vm.BuildModelChoices().First(c => c.Id is not null && c.Id != KnownModels.Main);
        vm.SelectedInspectorModel = other;
        vm.CanApplyModel.Should().BeTrue();

        f.Session.State.HandleProcessExit(-1);

        vm.CanApplyModel.Should().BeFalse("a dead session can't be respawned via Apply - it's already gone.");
        vm.ApplyModelCommand.CanExecute(null).Should().BeFalse();
    }
}
