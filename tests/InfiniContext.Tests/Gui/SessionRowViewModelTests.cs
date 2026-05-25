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

    // ----- Turn correlation: seed prompt + init + queueing (task 0011) -----

    [Fact]
    public void Seed_prompt_turn_survives_system_init_without_closing()
    {
        // Regression for task 0011 RC1: the init arm used to blank the ctor-default "thinking..."
        // headline of any pending turn and clear the in-flight pointer, which orphaned the
        // operator's pointer turn before claude even had a chance to respond.
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);

        row.BeginUserTurn("Working on task 0011. Task folder: .task/0011-foo/. Use the infinicontext-task skill.");
        row.Turns.Should().HaveCount(1);
        var seedTurn = row.Turns[0];

        f.RaiseEvent(SessionContextFactory.MakeInit());

        row.Turns.Should().HaveCount(1, "system/init must attach to the existing seed-prompt turn, not open a new one.");
        seedTurn.IsInProgress.Should().BeTrue("seed-prompt turns stay in flight until the matching ResultEvent.");
        seedTurn.Headline.Should().Be("thinking...", "claude has not emitted any assistant text yet; the 'thinking' headline must remain.");
        seedTurn.HasOperatorMessage.Should().BeTrue("the seed prompt is the operator's message and stays visible above the agent block.");
        seedTurn.Children.Should().Contain(c => c.Summary.StartsWith("system/init"),
            "init details attach as a system child of the seed-prompt turn so the operator can inspect them in Details.");
    }

    [Fact]
    public void Free_session_init_event_opens_synthetic_card_and_closes_it()
    {
        // Companion to the seed-prompt test: when there's NO operator pointer (free session
        // without a BeginUserTurn before init), the init arm still produces a synthetic
        // "(session start)" card and closes it immediately - the prior behaviour, but now
        // explicit via the HasOperatorMessage branch instead of a SyntheticHeadlines check.
        var f = new SessionContextFactory(activeTaskId: null);
        var row = BuildRow(f);

        f.RaiseEvent(SessionContextFactory.MakeInit());

        row.Turns.Should().HaveCount(1);
        var card = row.Turns[0];
        card.HasOperatorMessage.Should().BeFalse("'(session start)' is a synthetic placeholder, not a real operator message.");
        card.IsInProgress.Should().BeFalse("a free-session init card has nothing more to do - close it so the next assistant event opens a fresh '(claude-initiated)' card.");
        card.HasHeadline.Should().BeFalse("synthetic placeholder cards must not present a fake 'thinking' headline.");
    }

    [Fact]
    public void Seed_prompt_full_lifecycle_one_card_one_close()
    {
        // End-to-end: seed-prompt + init + assistant text + result -> exactly one turn,
        // headline = assistant text, IsInProgress = false.
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);

        row.BeginUserTurn("Working on task 0011. Task folder: .task/0011-foo/. Use the infinicontext-task skill.");
        f.RaiseEvent(SessionContextFactory.MakeInit());
        f.RaiseEvent(SessionContextFactory.MakeAssistantText("I've read the task and I'm ready."));
        f.RaiseEvent(SessionContextFactory.MakeResult(numTurns: 2, costUsd: 0.0023m));

        row.Turns.Should().HaveCount(1, "the entire seed-pointer exchange is a single turn from the operator's POV.");
        var turn = row.Turns[0];
        turn.IsInProgress.Should().BeFalse();
        turn.Headline.Should().Be("I've read the task and I'm ready.");
        turn.HasOperatorMessage.Should().BeTrue();
        turn.FooterSummary.Should().Contain("2 turn(s)").And.Contain("$0.0023");
    }

    [Fact]
    public void Operator_send_during_inflight_turn_is_queued()
    {
        // Regression for task 0011 RC2/RC3: BeginUserTurn used to overwrite the in-flight
        // pointer mid-turn, orphaning the first turn (it never closed) and misattributing
        // the first turn's ResultEvent to the second turn.
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);

        row.BeginUserTurn("seed");           // turn1 -> in-flight
        row.BeginUserTurn("operator follow-up");  // turn2 -> queued

        row.Turns.Should().HaveCount(2);
        var first = row.Turns[0];
        var second = row.Turns[1];

        first.IsQueued.Should().BeFalse("the first send is the in-flight turn.");
        second.IsQueued.Should().BeTrue("the second send must wait for the first ResultEvent before it becomes in-flight.");
        first.IsInProgress.Should().BeTrue();
        second.IsInProgress.Should().BeTrue();
    }

    [Fact]
    public void Result_event_promotes_next_queued_turn_and_clears_isqueued()
    {
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);

        row.BeginUserTurn("seed");
        row.BeginUserTurn("op follow-up");
        // claude eventually responds to the first send and the result lands:
        f.Session.State.BeginSend();  // drive Sending so the result transition fires
        f.RaiseEvent(SessionContextFactory.MakeAssistantText("seed acknowledged"));
        f.RaiseEvent(SessionContextFactory.MakeResult());

        var first = row.Turns[0];
        var second = row.Turns[1];
        first.IsInProgress.Should().BeFalse("first turn's ResultEvent closes the first turn, not the queued one.");
        first.Headline.Should().Be("seed acknowledged", "the ResultEvent's reply attributes to the turn that produced it, not to the queued follow-up.");
        second.IsQueued.Should().BeFalse("the second turn is now the in-flight turn.");
        second.IsInProgress.Should().BeTrue("the follow-up's spinner stays until ITS ResultEvent arrives.");
        second.Headline.Should().Be("thinking...", "the follow-up's headline must not have been clobbered by the first turn's result text.");
    }

    [Fact]
    public void Assistant_event_during_queued_state_attaches_to_inflight_not_queued()
    {
        // Claude only streams against the in-flight turn; queued turns stay "thinking..."
        // with no children until they are promoted.
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);

        row.BeginUserTurn("seed");
        row.BeginUserTurn("op follow-up");
        f.RaiseEvent(SessionContextFactory.MakeAssistantText("seed mid-response"));

        var first = row.Turns[0];
        var second = row.Turns[1];
        first.Headline.Should().Be("seed mid-response", "assistant text attaches to the in-flight turn.");
        second.Children.Should().BeEmpty("the queued turn has no agent activity attached until it becomes in-flight.");
        second.Headline.Should().Be("thinking...", "queued turn's headline remains the ctor default while it waits.");
    }

    [Fact]
    public void Process_exit_flushes_queue_and_marks_each_not_sent()
    {
        // If claude dies before draining queued sends, the operator should see that those
        // turns never ran - silently dropping them would leave phantom "thinking..." cards.
        var f = new SessionContextFactory(activeTaskId: 42);
        var row = BuildRow(f);

        row.BeginUserTurn("first");
        row.BeginUserTurn("second");
        row.BeginUserTurn("third");

        f.Session.State.HandleProcessExit(-1);

        row.Turns.Should().HaveCount(3);
        foreach (var turn in row.Turns)
        {
            turn.IsInProgress.Should().BeFalse("every turn must be marked complete when the session exits - no orphaned spinners.");
            turn.IsQueued.Should().BeFalse("the queued badge clears on exit; queueing has no meaning once claude is dead.");
        }
        row.Turns[1].Children.Should().Contain(c => c.Summary.Contains("not sent"),
            "operator must see that the buffered turn never ran.");
        row.Turns[2].Children.Should().Contain(c => c.Summary.Contains("not sent"));
    }
}
