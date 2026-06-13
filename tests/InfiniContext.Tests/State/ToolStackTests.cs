using InfiniContext.Core.Events;
using InfiniContext.Core.Parser;
using InfiniContext.Core.State;
using FluentAssertions;

namespace InfiniContext.Tests.State;

/// <summary>
/// Step-through assertions on <see cref="SessionStateMachine.OpenToolUses"/> over the
/// <c>nested-tool</c> fixture. Validates the parent_tool_use_id correlation: at the deepest
/// point both the outer Agent and the inner Bash are open simultaneously, and they close in
/// the right order (inner first, then outer).
/// </summary>
public class ToolStackTests
{
    [Fact]
    public async Task NestedTool_ParentAndChild_AreOpenSimultaneously_ThenCloseInOrder()
    {
        var sm = new SessionStateMachine(clock: () => DateTimeOffset.UnixEpoch);
        var maxOpen = 0;
        ToolUseSlot? deepestOuter = null;
        ToolUseSlot? deepestInner = null;
        var closeOrder = new List<string>();

        sm.ToolStackChanged += c =>
        {
            if (c.Kind == ToolStackChangeKind.Opened)
            {
                if (sm.OpenToolUses.Count > maxOpen)
                {
                    maxOpen = sm.OpenToolUses.Count;
                    // Snapshot at deepest point: outer = the slot with no parent, inner = the one with a parent.
                    deepestOuter = sm.OpenToolUses.Values.SingleOrDefault(s => s.ParentToolUseId is null);
                    deepestInner = sm.OpenToolUses.Values.SingleOrDefault(s => s.ParentToolUseId is not null);
                }
            }
            else
            {
                closeOrder.Add(c.Slot.Id);
            }
        };

        sm.BeginSend();

        var parser = new StreamJsonParser();
        using var reader = new StreamReader(TestPaths.Fixture("nested-tool"));
        await foreach (var ev in parser.ParseAsync(reader, TestContext.Current.CancellationToken))
        {
            sm.Apply(ev);
        }

        // Deepest point: parent Agent + child Bash both open.
        maxOpen.Should().Be(2);
        deepestOuter.Should().NotBeNull();
        deepestInner.Should().NotBeNull();
        deepestOuter!.ParentToolUseId.Should().BeNull();
        deepestInner!.ParentToolUseId.Should().Be(deepestOuter.Id, "inner tool's parent_tool_use_id should reference the outer Agent's tool_use.id");

        // Close order: child closes before parent.
        closeOrder.Should().HaveCount(2);
        closeOrder[0].Should().Be(deepestInner.Id);
        closeOrder[1].Should().Be(deepestOuter.Id);

        sm.OpenToolUses.Should().BeEmpty("all tool uses must close by end of turn");
        sm.State.Should().Be(SessionState.Idle);
    }

    [Fact]
    public void Cancel_FromStreaming_TransitionsToCancelling_ThenIdleOnResult()
    {
        var sm = new SessionStateMachine(clock: () => DateTimeOffset.UnixEpoch);
        var transitions = new List<SessionState>();
        sm.Transitioned += t => transitions.Add(t.To);

        sm.BeginSend();
        sm.Apply(MakeAssistantEvent());
        sm.State.Should().Be(SessionState.Streaming);

        sm.Cancel();
        sm.State.Should().Be(SessionState.Cancelling);

        sm.Apply(MakeResultEvent());
        sm.State.Should().Be(SessionState.Idle);

        transitions.Should().Equal(SessionState.Sending, SessionState.Streaming, SessionState.Cancelling, SessionState.Idle);
    }

    [Fact]
    public void Compact_DuringStreaming_TransitionsToCompacting_ThenBackOnNextEvent()
    {
        var sm = new SessionStateMachine(clock: () => DateTimeOffset.UnixEpoch);
        var transitions = new List<SessionState>();
        sm.Transitioned += t => transitions.Add(t.To);

        sm.BeginSend();
        sm.Apply(MakeAssistantEvent());
        sm.Apply(MakeCompactEvent());
        sm.State.Should().Be(SessionState.Compacting);

        sm.Apply(MakeAssistantEvent());
        sm.State.Should().Be(SessionState.Streaming);

        sm.Apply(MakeResultEvent());
        sm.State.Should().Be(SessionState.Idle);
    }

    [Fact]
    public void HandleProcessExit_TransitionsToExited()
    {
        var sm = new SessionStateMachine();
        sm.BeginSend();
        sm.HandleProcessExit(exitCode: 1);
        sm.State.Should().Be(SessionState.Exited);
    }

    // M10: a tool_use still open at turn end (no matching tool_result) is drained as Abandoned on
    // the ResultEvent so the correlation map doesn't leak.
    [Fact]
    public void Result_drains_open_tool_uses_as_abandoned()
    {
        var sm = new SessionStateMachine(clock: () => DateTimeOffset.UnixEpoch);
        var changes = new List<ToolStackChange>();
        sm.ToolStackChanged += changes.Add;

        sm.BeginSend();
        sm.Apply(MakeAssistantWithToolUse("t1", "Bash"));
        sm.OpenToolUses.Should().ContainKey("t1");

        sm.Apply(MakeResultEvent()); // turn ends with no tool_result for t1

        sm.OpenToolUses.Should().BeEmpty();
        changes.Should().ContainSingle(c => c.Kind == ToolStackChangeKind.Abandoned && c.Slot.Id == "t1");
        sm.State.Should().Be(SessionState.Idle);
    }

    // M10: process exit drains open tool uses as Abandoned.
    [Fact]
    public void ProcessExit_drains_open_tool_uses_as_abandoned()
    {
        var sm = new SessionStateMachine(clock: () => DateTimeOffset.UnixEpoch);
        var changes = new List<ToolStackChange>();
        sm.ToolStackChanged += changes.Add;

        sm.BeginSend();
        sm.Apply(MakeAssistantWithToolUse("t1", "Bash"));

        sm.HandleProcessExit(exitCode: 137);

        sm.OpenToolUses.Should().BeEmpty();
        changes.Should().ContainSingle(c => c.Kind == ToolStackChangeKind.Abandoned && c.Slot.Id == "t1");
    }

    // M6: AbortSend rolls back a failed send so the machine isn't stuck Sending forever.
    [Fact]
    public void AbortSend_rolls_back_pending_send_and_returns_to_idle()
    {
        var sm = new SessionStateMachine(clock: () => DateTimeOffset.UnixEpoch);

        sm.BeginSend();
        sm.State.Should().Be(SessionState.Sending);
        sm.PendingSendCount.Should().Be(1);

        sm.AbortSend();

        sm.PendingSendCount.Should().Be(0);
        sm.State.Should().Be(SessionState.Idle);
    }

    // M6: aborting a buffered send (a prior turn still streaming) only decrements the count; the
    // streaming turn keeps running.
    [Fact]
    public void AbortSend_on_buffered_send_keeps_streaming_turn()
    {
        var sm = new SessionStateMachine(clock: () => DateTimeOffset.UnixEpoch);

        sm.BeginSend();
        sm.Apply(MakeAssistantEvent()); // -> Streaming, count=1
        sm.BeginSend();                  // buffered, count=2, still Streaming
        sm.State.Should().Be(SessionState.Streaming);
        sm.PendingSendCount.Should().Be(2);

        sm.AbortSend();

        sm.PendingSendCount.Should().Be(1);
        sm.State.Should().Be(SessionState.Streaming);
    }

    private static AssistantEvent MakeAssistantWithToolUse(string id, string name) => new()
    {
        Type = "assistant",
        Raw = new System.Text.Json.Nodes.JsonObject(),
        Content = new ContentBlock[]
        {
            new ToolUseBlock { Type = "tool_use", Raw = new System.Text.Json.Nodes.JsonObject(), Id = id, Name = name },
        },
    };

    [Fact]
    public void UnknownEvent_IncrementsDriftCounter()
    {
        var sm = new SessionStateMachine();
        sm.Apply(new UnknownEvent
        {
            Type = "future_type",
            Discriminator = "future_type",
            Raw = new System.Text.Json.Nodes.JsonObject(),
        });
        sm.UnknownEventCount.Should().Be(1);
    }

    private static AssistantEvent MakeAssistantEvent() => new()
    {
        Type = "assistant",
        Raw = new System.Text.Json.Nodes.JsonObject(),
        Content = Array.Empty<ContentBlock>(),
    };

    private static ResultEvent MakeResultEvent() => new()
    {
        Type = "result",
        Subtype = "success",
        Raw = new System.Text.Json.Nodes.JsonObject(),
    };

    private static SystemCompactEvent MakeCompactEvent() => new()
    {
        Type = "system",
        Subtype = "compact",
        Raw = new System.Text.Json.Nodes.JsonObject(),
    };
}
