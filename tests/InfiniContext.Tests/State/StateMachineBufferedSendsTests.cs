using System.Text.Json.Nodes;
using InfiniContext.Core.Events;
using InfiniContext.Core.State;
using FluentAssertions;

namespace InfiniContext.Tests.State;

/// <summary>
/// Pins the task 0011 RC4 contract: <see cref="SessionStateMachine.PendingSendCount"/> tracks
/// buffered sends and the post-ResultEvent transition routes back to <see cref="SessionState.Sending"/>
/// while sends remain in flight. Consumers that watch <see cref="SessionStateMachine.Transitioned"/>
/// for per-turn boundaries need one clean Idle->Sending->Streaming->Idle cycle per claude turn,
/// even when an operator buffered a second prompt on stdin during the first turn's stream.
/// </summary>
public class StateMachineBufferedSendsTests
{
    [Fact]
    public void Single_BeginSend_then_Result_lands_in_Idle_with_count_zero()
    {
        var sm = new SessionStateMachine();
        sm.BeginSend();
        sm.PendingSendCount.Should().Be(1);
        sm.Apply(MakeAssistant());
        sm.State.Should().Be(SessionState.Streaming);

        sm.Apply(MakeResult());

        sm.State.Should().Be(SessionState.Idle, "no buffered sends remain, so the state machine returns to its rest state.");
        sm.PendingSendCount.Should().Be(0);
    }

    [Fact]
    public void Second_BeginSend_during_Streaming_increments_count_without_transition()
    {
        var sm = new SessionStateMachine();
        sm.BeginSend();
        sm.Apply(MakeAssistant());
        sm.State.Should().Be(SessionState.Streaming);
        sm.PendingSendCount.Should().Be(1);

        var transitions = new List<StateTransition>();
        sm.Transitioned += transitions.Add;

        sm.BeginSend();

        sm.State.Should().Be(SessionState.Streaming, "a buffered second send must not perturb the current turn's state.");
        sm.PendingSendCount.Should().Be(2, "the second BeginSend still corresponds to a real stdin write claude will drain after the current turn.");
        transitions.Should().BeEmpty("the no-op-from-non-Idle contract holds; only the count carries the queue signal.");
    }

    [Fact]
    public void Result_with_buffered_send_cycles_back_to_Sending()
    {
        var sm = new SessionStateMachine();
        sm.BeginSend();
        sm.Apply(MakeAssistant());
        sm.BeginSend();
        sm.State.Should().Be(SessionState.Streaming);
        sm.PendingSendCount.Should().Be(2);

        sm.Apply(MakeResult());

        sm.State.Should().Be(SessionState.Sending,
            "buffered send remains, so the post-result transition lands in Sending - the next AssistantEvent will flip to Streaming.");
        sm.PendingSendCount.Should().Be(1);
    }

    [Fact]
    public void Result_reason_includes_remaining_queue_depth_for_observability()
    {
        // The trace surface (logs, future debugging UIs) needs to see WHY a Result didn't land
        // in Idle. The reason string must include the remaining queue depth so a developer
        // reading a log can correlate the cycle-back with the buffered send.
        var sm = new SessionStateMachine();
        sm.BeginSend();
        sm.BeginSend();
        sm.Apply(MakeAssistant());

        var transitions = new List<StateTransition>();
        sm.Transitioned += transitions.Add;
        sm.Apply(MakeResult());

        transitions.Should().ContainSingle();
        transitions[0].Reason.Should().Contain("result/").And.Contain("queued sends remain: 1");
    }

    [Fact]
    public void Two_buffered_sends_produce_two_clean_cycles()
    {
        // End-to-end: every consumer that subscribes to Transitioned must see a clean
        // Idle->Sending->Streaming->Sending->Streaming->Idle trace, with the inner Sending
        // marking the per-turn boundary that pure event types cannot signal.
        var sm = new SessionStateMachine();
        var trace = new List<(SessionState From, SessionState To)>();
        sm.Transitioned += t => trace.Add((t.From, t.To));

        sm.BeginSend();                   // Idle -> Sending
        sm.Apply(MakeAssistant());        // Sending -> Streaming
        sm.BeginSend();                   // (count++, no transition)
        sm.Apply(MakeResult());           // Streaming -> Sending (queued send remains)
        sm.Apply(MakeAssistant());        // Sending -> Streaming
        sm.Apply(MakeResult());           // Streaming -> Idle

        trace.Should().Equal(
            (SessionState.Idle, SessionState.Sending),
            (SessionState.Sending, SessionState.Streaming),
            (SessionState.Streaming, SessionState.Sending),
            (SessionState.Sending, SessionState.Streaming),
            (SessionState.Streaming, SessionState.Idle));
        sm.PendingSendCount.Should().Be(0);
    }

    [Fact]
    public void Trailing_Result_from_Idle_is_still_ignored_after_count_drop()
    {
        // Regression guard: the prior "Result from Idle is a no-op" contract (see
        // StateMachineNoOpTests.Result_event_while_Idle_is_ignored) must still hold once we've
        // walked through a full Send to Result cycle. The new decrement code lives inside the
        // non-Idle guard, so a Result that arrives after the machine has already returned to
        // Idle must not change the count and must not fire a spurious transition.
        var sm = new SessionStateMachine();
        sm.BeginSend();
        sm.Apply(MakeAssistant());
        sm.Apply(MakeResult());
        sm.State.Should().Be(SessionState.Idle);
        sm.PendingSendCount.Should().Be(0);

        var transitions = new List<StateTransition>();
        sm.Transitioned += transitions.Add;
        sm.Apply(MakeResult());

        sm.PendingSendCount.Should().Be(0, "a Result that arrives after Idle must not touch the count.");
        sm.State.Should().Be(SessionState.Idle);
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void BeginSend_from_Exited_does_not_increment_count()
    {
        var sm = new SessionStateMachine();
        sm.HandleProcessExit(0);
        sm.State.Should().Be(SessionState.Exited);

        sm.BeginSend();

        sm.PendingSendCount.Should().Be(0, "once exited the writer cannot reach stdin, so the count must not desync from observable transitions.");
        sm.State.Should().Be(SessionState.Exited);
    }

    [Fact]
    public void Cancel_during_buffered_sends_still_routes_Result_through_queue_depth()
    {
        // Cancel mid-turn while a second send is buffered: the Result still lands, the count
        // decrements, and because a buffered send remains the state cycles to Sending instead
        // of Idle. This is the contract the GUI needs so the queued turn doesn't get
        // stranded as "in flight" forever after a cancel.
        var sm = new SessionStateMachine();
        sm.BeginSend();
        sm.Apply(MakeAssistant());
        sm.BeginSend();
        sm.Cancel();
        sm.State.Should().Be(SessionState.Cancelling);
        sm.PendingSendCount.Should().Be(2);

        sm.Apply(MakeResult());

        sm.State.Should().Be(SessionState.Sending, "the buffered second send must still get a clean per-turn boundary even when the first turn was cancelled.");
        sm.PendingSendCount.Should().Be(1);
    }

    private static AssistantEvent MakeAssistant() => new()
    {
        Type = "assistant",
        Raw = new JsonObject(),
        Content = Array.Empty<ContentBlock>(),
    };

    private static ResultEvent MakeResult() => new()
    {
        Type = "result",
        Subtype = "success",
        Raw = new JsonObject(),
    };
}
