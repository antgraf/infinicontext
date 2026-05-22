using FluentAssertions;
using InfiniContext.Core.State;
using InfiniContext.Gui.ViewModels;

namespace InfiniContext.Tests.Gui;

/// <summary>
/// Readiness-gate matrix for <see cref="SessionRowViewModel"/>. Locks the new contract per
/// design-gui-testability.md section 12 alternative: <b>init-based</b> gate for both free and
/// task-bound sessions, with <see cref="SessionRowViewModel.IsAgentTurnInFlight"/> surfaced
/// from the state machine so the operator gets a visible queue indicator when a turn is in
/// flight rather than a silently disabled Send button.
///
/// <para>Each test constructs the row VM directly against a <see cref="FakeClaudeSession"/> and
/// drives events through <see cref="SessionContextFactory.RaiseEvent"/>, bypassing the real
/// <see cref="InfiniContext.Core.Sessions.SessionTranscriptReader"/>. Public properties only -
/// no peeking at <c>_initSeen</c>, <c>_isReady</c>, or other private fields.</para>
/// </summary>
public class SessionRowViewModelTests
{
    private static SessionRowViewModel BuildRow(SessionContextFactory f) =>
        new(f.Context, new SynchronousUiDispatcher(), new TestUiTimerFactory());

    // ----- Free sessions (ActiveTaskId == null) -----

    [Fact]
    public void Free_session_no_event_yet_ready_at_ctor()
    {
        var f = new SessionContextFactory(activeTaskId: null);
        var row = BuildRow(f);

        row.IsReady.Should().BeTrue("free sessions have no pointer turn - claude defers system/init " +
            "until stdin input, so gating on init would deadlock (operator can't type while Send is disabled).");
        row.IsAlive.Should().BeTrue();
        row.IsAgentTurnInFlight.Should().BeFalse("state machine starts at Idle.");
    }

    [Fact]
    public void Free_session_sessionId_already_set_ready_at_ctor()
    {
        var f = new SessionContextFactory(activeTaskId: null);
        f.Session.SessionId = Guid.NewGuid();

        var row = BuildRow(f);

        row.IsReady.Should().BeTrue();
        row.IsAlive.Should().BeTrue();
    }

    [Fact]
    public void Free_session_init_event_keeps_ready()
    {
        var f = new SessionContextFactory(activeTaskId: null);
        var row = BuildRow(f);

        f.RaiseEvent(SessionContextFactory.MakeInit());

        row.IsReady.Should().BeTrue();
    }

    // ----- Task-bound sessions (ActiveTaskId != null) -----

    [Fact]
    public void Taskbound_no_event_yet_not_ready_at_ctor()
    {
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);

        row.IsReady.Should().BeFalse(
            "before init we have no proof claude is listening; sending now would write to a process " +
            "that hasn't drained stdin yet.");
        row.IsAlive.Should().BeTrue();
        row.IsAgentTurnInFlight.Should().BeFalse("state machine starts at Idle; pointer turn hasn't been initiated yet.");
    }

    [Fact]
    public void Taskbound_sessionId_set_at_ctor_flips_ready_immediately()
    {
        var f = new SessionContextFactory(activeTaskId: 42);
        f.Session.SessionId = Guid.NewGuid();
        // Events fired before ctor: init landed (sessionId is set). Under the new contract this
        // is sufficient - the operator gets agency immediately even if the pointer turn is still
        // streaming (queue indicator surfaces that state separately).

        var row = BuildRow(f);

        row.IsReady.Should().BeTrue(
            "sessionId non-null at ctor proves init fired; that is the readiness gate under the new contract.");
    }

    [Fact]
    public void Taskbound_init_event_flips_ready_without_waiting_for_result()
    {
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);

        f.RaiseEvent(SessionContextFactory.MakeInit());

        row.IsReady.Should().BeTrue(
            "task-bound sessions now flip on init too - the prior result-based gate left buttons " +
            "disabled for 30s-2min during real pointer turns, which the operator rejected.");
    }

    [Fact]
    public void Taskbound_assistant_before_init_catchup_flips_ready()
    {
        // Race scenario: reader drained init before the row VM subscribed; we observe an
        // assistant event as the first event. Catch-up sets _initSeen AND IsReady.
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);

        f.RaiseEvent(SessionContextFactory.MakeAssistantText("partial response"));

        row.IsReady.Should().BeTrue(
            "any non-init event is proof init happened (channel preserves FIFO), so catch-up flips IsReady.");
    }

    [Fact]
    public void Taskbound_result_fallback_flips_ready_if_init_was_missed()
    {
        // Pathological scenario: somehow we never saw init or any in-between event, but a
        // ResultEvent reaches us. Treat that as last-resort proof that claude is responsive.
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);

        f.RaiseEvent(SessionContextFactory.MakeResult());

        row.IsReady.Should().BeTrue("result-fallback is the defensive last gate; never leave IsReady false " +
            "if we've observed any event from claude.");
    }

    // ----- IsAgentTurnInFlight (the queue-warning indicator) -----

    [Fact]
    public void IsAgentTurnInFlight_false_when_state_is_idle()
    {
        var f = new SessionContextFactory(activeTaskId: null);
        var row = BuildRow(f);

        // State machine starts Idle.
        row.IsAgentTurnInFlight.Should().BeFalse();
    }

    [Fact]
    public void IsAgentTurnInFlight_true_during_sending_streaming_cancelling_compacting()
    {
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);

        f.Session.State.BeginSend();
        f.Session.State.State.Should().Be(SessionState.Sending);
        row.IsAgentTurnInFlight.Should().BeTrue("Sending is a turn-in-flight state.");

        f.RaiseEvent(SessionContextFactory.MakeAssistantText());
        f.Session.State.State.Should().Be(SessionState.Streaming);
        row.IsAgentTurnInFlight.Should().BeTrue("Streaming is a turn-in-flight state.");

        f.Session.State.Cancel();
        f.Session.State.State.Should().Be(SessionState.Cancelling);
        row.IsAgentTurnInFlight.Should().BeTrue("Cancelling is a turn-in-flight state.");

        f.RaiseEvent(SessionContextFactory.MakeResult());
        f.Session.State.State.Should().Be(SessionState.Idle);
        row.IsAgentTurnInFlight.Should().BeFalse("ResultEvent returns the state machine to Idle.");
    }

    [Fact]
    public void IsAgentTurnInFlight_false_when_exited()
    {
        var f = new SessionContextFactory(activeTaskId: null);
        var row = BuildRow(f);

        f.Session.State.BeginSend();
        row.IsAgentTurnInFlight.Should().BeTrue();

        f.Session.State.HandleProcessExit(-1);

        row.IsAgentTurnInFlight.Should().BeFalse("a dead session can't have a turn in flight.");
    }

    [Fact]
    public void State_transition_raises_PropertyChanged_for_IsAgentTurnInFlight()
    {
        var f = new SessionContextFactory(activeTaskId: null);
        var row = BuildRow(f);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        f.Session.State.BeginSend();

        changed.Should().Contain(nameof(SessionRowViewModel.IsAgentTurnInFlight),
            "the host VM watches this property to flip the queue-warning indicator and tooltip text.");
    }

    [Fact]
    public void OnStateTransition_marshals_to_UI_thread_when_called_off_thread()
    {
        // Regression test for the task 0008 root cause: OnStateTransition used to call
        // OnPropertyChanged + assign StatusGlyph synchronously on the thread that fired the
        // state machine's Transitioned event (the stdout-loop reader thread). Avalonia binding
        // subscribers throw InvalidOperationException on cross-thread UI updates; that exception
        // propagated out of _state.Apply and silently killed the entire stdout loop - the
        // operator-visible symptom was "Agent busy never changes after the first assistant
        // event." Fix: dispatch the whole body of OnStateTransition through IUiDispatcher.
        var dispatcher = new TrackingUiDispatcher { CheckAccessReturns = false };
        var f = new SessionContextFactory(activeTaskId: null);
        var row = new SessionRowViewModel(f.Context, dispatcher, new TestUiTimerFactory());
        var postsBefore = dispatcher.PostCount;

        // Drive the state machine directly. Transitioned fires synchronously on the calling
        // thread - this models the stdout-loop reader-thread invocation that production hits.
        f.Session.State.BeginSend();

        dispatcher.PostCount.Should().BeGreaterThan(postsBefore,
            "OnStateTransition must Post when CheckAccess returns false; otherwise PropertyChanged " +
            "fires on the reader thread and Avalonia bindings throw, terminating the stdout loop.");
        row.IsAgentTurnInFlight.Should().BeTrue(
            "the dispatched action must still have run; tracking dispatcher executes Post inline.");
    }

    // ----- Liveness -----

    [Fact]
    public void IsAlive_flips_false_on_exited_transition()
    {
        var f = new SessionContextFactory(activeTaskId: null);
        var row = BuildRow(f);

        row.IsAlive.Should().BeTrue();

        f.Session.State.HandleProcessExit(-1);

        row.IsAlive.Should().BeFalse(
            "sending to a dead child silently fails; IsAlive false gates the Send/Interrupt buttons.");
    }

    [Fact]
    public void IsReady_stays_true_after_exit_but_send_path_gated_by_IsAlive()
    {
        var f = new SessionContextFactory(activeTaskId: null);
        var row = BuildRow(f);

        row.IsReady.Should().BeTrue();

        f.Session.State.HandleProcessExit(-1);

        row.IsReady.Should().BeTrue("IsReady is monotonic - the dead-session gate is IsAlive.");
        row.IsAlive.Should().BeFalse();
    }
}
