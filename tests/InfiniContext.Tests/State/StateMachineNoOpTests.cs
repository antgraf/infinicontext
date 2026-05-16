using System.Text.Json.Nodes;
using InfiniContext.Core.Events;
using InfiniContext.Core.State;
using FluentAssertions;

namespace InfiniContext.Tests.State;

/// <summary>
/// Pins the <see cref="SessionStateMachine"/>'s silent-no-op contract: certain method calls
/// from invalid starting states must NOT change state, NOT throw, and (where applicable) NOT
/// fire transition events. These tests document the contract that callers rely on.
/// </summary>
public class StateMachineNoOpTests
{
    [Fact]
    public void BeginSend_from_Sending_is_a_noop()
    {
        var sm = new SessionStateMachine();
        sm.BeginSend(); // Idle → Sending
        var transitions = new List<StateTransition>();
        sm.Transitioned += transitions.Add;

        sm.BeginSend();

        sm.State.Should().Be(SessionState.Sending);
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void BeginSend_from_Streaming_is_a_noop()
    {
        var sm = new SessionStateMachine();
        sm.BeginSend();
        sm.Apply(MakeAssistant());
        sm.State.Should().Be(SessionState.Streaming);

        var transitions = new List<StateTransition>();
        sm.Transitioned += transitions.Add;

        sm.BeginSend();

        sm.State.Should().Be(SessionState.Streaming);
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void BeginSend_from_Exited_is_a_noop_and_state_remains_Exited()
    {
        var sm = new SessionStateMachine();
        sm.HandleProcessExit(0);
        sm.State.Should().Be(SessionState.Exited);

        var transitions = new List<StateTransition>();
        sm.Transitioned += transitions.Add;

        sm.BeginSend();

        sm.State.Should().Be(SessionState.Exited);
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void Cancel_from_Idle_is_a_noop()
    {
        var sm = new SessionStateMachine();
        var transitions = new List<StateTransition>();
        sm.Transitioned += transitions.Add;

        sm.Cancel();

        sm.State.Should().Be(SessionState.Idle);
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void Cancel_from_Exited_is_a_noop()
    {
        var sm = new SessionStateMachine();
        sm.HandleProcessExit(0);
        var transitions = new List<StateTransition>();
        sm.Transitioned += transitions.Add;

        sm.Cancel();

        sm.State.Should().Be(SessionState.Exited);
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void Result_event_while_Idle_is_ignored()
    {
        var sm = new SessionStateMachine();
        var transitions = new List<StateTransition>();
        sm.Transitioned += transitions.Add;

        sm.Apply(MakeResult());

        sm.State.Should().Be(SessionState.Idle);
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void Events_after_HandleProcessExit_are_dropped()
    {
        var sm = new SessionStateMachine();
        sm.HandleProcessExit(0);
        var transitions = new List<StateTransition>();
        sm.Transitioned += transitions.Add;

        sm.Apply(MakeAssistant());
        sm.Apply(MakeResult());

        sm.State.Should().Be(SessionState.Exited);
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void HandleProcessExit_called_twice_is_a_noop_on_second_call()
    {
        var sm = new SessionStateMachine();
        sm.HandleProcessExit(0);
        var transitions = new List<StateTransition>();
        sm.Transitioned += transitions.Add;

        sm.HandleProcessExit(1);

        sm.State.Should().Be(SessionState.Exited);
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void UnknownEvent_increments_counter_without_changing_state()
    {
        var sm = new SessionStateMachine();
        var transitions = new List<StateTransition>();
        sm.Transitioned += transitions.Add;

        sm.Apply(new UnknownEvent
        {
            Type = "some_future_top_level",
            Raw = new JsonObject(),
            Discriminator = "some_future_top_level",
        });

        sm.State.Should().Be(SessionState.Idle);
        sm.UnknownEventCount.Should().Be(1);
        transitions.Should().BeEmpty();
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
