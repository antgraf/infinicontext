using System.Text.Json.Nodes;
using InfiniContext.Core.Mcp;
using FluentAssertions;

namespace InfiniContext.Tests.Mcp;

/// <summary>
/// Unit tests for <see cref="PendingApprovalSlot"/>. Phase 5: the slot is the per-session bridge
/// between approval-sink producers (claude → sidecar → ApprovalChannel) and the host UI consumer.
/// </summary>
public class PendingApprovalSlotTests
{
    [Fact]
    public async Task Resolve_with_allow_completes_pending_request()
    {
        var slot = new PendingApprovalSlot();
        var request = new ApprovalRequest("Bash", new JsonObject { ["command"] = "echo hi" });

        var pending = slot.RequestAsync(request, TimeSpan.FromSeconds(60), CancellationToken.None);
        slot.Current.Should().NotBeNull();
        slot.Current!.Request.Should().BeSameAs(request);

        slot.Resolve(ApprovalBehavior.Allow).Should().BeTrue();
        var response = await pending;

        response.Behavior.Should().Be(ApprovalBehavior.Allow);
        response.UpdatedInput!["command"]!.GetValue<string>().Should().Be("echo hi");
        slot.Current.Should().BeNull("the slot is cleared after resolution");
    }

    [Fact]
    public async Task Resolve_with_deny_returns_deny_response()
    {
        var slot = new PendingApprovalSlot();
        var pending = slot.RequestAsync(NewRequest(), TimeSpan.FromSeconds(60), CancellationToken.None);
        slot.Resolve(ApprovalBehavior.Deny, message: "no thanks");

        var response = await pending;
        response.Behavior.Should().Be(ApprovalBehavior.Deny);
        response.Message.Should().Be("no thanks");
    }

    [Fact]
    public async Task Timeout_auto_denies_with_standard_message()
    {
        var slot = new PendingApprovalSlot();
        var pending = slot.RequestAsync(NewRequest(), TimeSpan.FromMilliseconds(75), CancellationToken.None);

        var response = await pending;
        response.Behavior.Should().Be(ApprovalBehavior.Deny);
        response.Message.Should().Be("user did not respond within timeout");
        slot.Current.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_throws_OperationCanceled_and_clears_slot()
    {
        var slot = new PendingApprovalSlot();
        using var cts = new CancellationTokenSource();
        var pending = slot.RequestAsync(NewRequest(), TimeSpan.FromSeconds(60), cts.Token);

        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => pending);
        slot.Current.Should().BeNull("cancellation clears the slot so a future request can be parked");
    }

    [Fact]
    public void Changed_fires_with_pending_then_null_around_resolve()
    {
        var slot = new PendingApprovalSlot();
        var observed = new List<PendingApproval?>();
        slot.Changed += observed.Add;

        _ = slot.RequestAsync(NewRequest(), TimeSpan.FromSeconds(60), CancellationToken.None);
        slot.Resolve(ApprovalBehavior.Allow);

        observed.Should().HaveCount(2);
        observed[0].Should().NotBeNull("Changed fires with the new pending request");
        observed[1].Should().BeNull("Changed fires with null when the slot clears");
    }

    [Fact]
    public void Resolve_on_empty_slot_returns_false()
    {
        var slot = new PendingApprovalSlot();
        slot.Resolve(ApprovalBehavior.Allow).Should().BeFalse(
            "the UI may race timeout/cancellation; resolving a cleared slot must be a no-op");
    }

    [Fact]
    public async Task Second_concurrent_request_throws_InvalidOperationException()
    {
        var slot = new PendingApprovalSlot();
        _ = slot.RequestAsync(NewRequest(), TimeSpan.FromSeconds(60), CancellationToken.None);

        // ApprovalChannel reads one request line at a time per session — two-in-flight is
        // unreachable in production but the slot enforces the contract defensively.
        var act = () => slot.RequestAsync(NewRequest(), TimeSpan.FromSeconds(60), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    [Fact]
    public async Task Slot_is_reusable_after_resolve()
    {
        var slot = new PendingApprovalSlot();
        var first = slot.RequestAsync(NewRequest("Bash"), TimeSpan.FromSeconds(60), CancellationToken.None);
        slot.Resolve(ApprovalBehavior.Allow);
        await first;

        var second = slot.RequestAsync(NewRequest("Write"), TimeSpan.FromSeconds(60), CancellationToken.None);
        slot.Current!.Request.ToolName.Should().Be("Write");
        slot.Resolve(ApprovalBehavior.Deny);
        (await second).Behavior.Should().Be(ApprovalBehavior.Deny);
    }

    private static ApprovalRequest NewRequest(string toolName = "Bash") =>
        new(toolName, new JsonObject { ["command"] = "echo hi" });
}
