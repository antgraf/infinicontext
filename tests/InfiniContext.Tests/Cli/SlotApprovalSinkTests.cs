using System.Text.Json.Nodes;
using InfiniContext.Cli;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using FluentAssertions;

namespace InfiniContext.Tests.Cli;

/// <summary>
/// Unit tests for <see cref="SlotApprovalSink"/>. Phase 5 replacement for
/// <c>ConsoleApprovalSinkTests</c> — verifies the slot-based ask path renders the request banner
/// into the session's transcript and resolves through the host UI rather than stdin.
/// </summary>
public class SlotApprovalSinkTests
{
    [Fact]
    public async Task Request_appends_banner_to_transcript_then_awaits_resolution()
    {
        var slot = new PendingApprovalSlot();
        var transcript = new TranscriptModel();
        var sink = new SlotApprovalSink(slot, transcript, timeout: TimeSpan.FromSeconds(60));

        var pending = sink.RequestAsync(MakeRequest("Bash", "echo hi"), CancellationToken.None);

        // Banner is in transcript before resolution.
        var preResolve = string.Concat(transcript.Snapshot());
        preResolve.Should().Contain("[approval] tool=Bash");
        preResolve.Should().Contain("echo hi");
        preResolve.Should().Contain("(y/N, 60s timeout)");
        pending.IsCompleted.Should().BeFalse("the sink waits for the host UI to resolve");

        slot.Resolve(ApprovalBehavior.Allow);
        var response = await pending;
        response.Behavior.Should().Be(ApprovalBehavior.Allow);

        var postResolve = string.Concat(transcript.Snapshot());
        postResolve.Should().Contain("[approval] allowed");
    }

    [Fact]
    public async Task Deny_resolution_appends_denied_line()
    {
        var slot = new PendingApprovalSlot();
        var transcript = new TranscriptModel();
        var sink = new SlotApprovalSink(slot, transcript, timeout: TimeSpan.FromSeconds(60));

        var pending = sink.RequestAsync(MakeRequest("Write", "/etc/passwd"), CancellationToken.None);
        slot.Resolve(ApprovalBehavior.Deny);
        await pending;

        string.Concat(transcript.Snapshot()).Should().Contain("[approval] denied");
    }

    [Fact]
    public async Task Timeout_appends_timeout_line()
    {
        var slot = new PendingApprovalSlot();
        var transcript = new TranscriptModel();
        var sink = new SlotApprovalSink(slot, transcript, timeout: TimeSpan.FromMilliseconds(75));

        var response = await sink.RequestAsync(MakeRequest("Bash", "rm -rf /"), CancellationToken.None);

        response.Behavior.Should().Be(ApprovalBehavior.Deny);
        response.Message.Should().Be("user did not respond within timeout");
        string.Concat(transcript.Snapshot()).Should().Contain("timeout");
    }

    [Fact]
    public async Task Long_input_summary_is_truncated_in_banner()
    {
        var slot = new PendingApprovalSlot();
        var transcript = new TranscriptModel();
        var sink = new SlotApprovalSink(slot, transcript, timeout: TimeSpan.FromSeconds(60));

        var huge = string.Concat(Enumerable.Repeat("very-long-arg ", 50));
        var pending = sink.RequestAsync(MakeRequest("Bash", huge), CancellationToken.None);
        slot.Resolve(ApprovalBehavior.Allow);
        await pending;

        string.Concat(transcript.Snapshot()).Should().Contain("…", "the banner truncates with an ellipsis so the pane isn't flooded");
    }

    [Fact]
    public async Task Cancellation_propagates_and_appends_denied_line()
    {
        var slot = new PendingApprovalSlot();
        var transcript = new TranscriptModel();
        var sink = new SlotApprovalSink(slot, transcript, timeout: TimeSpan.FromSeconds(60));

        using var cts = new CancellationTokenSource();
        var pending = sink.RequestAsync(MakeRequest("Bash", "echo"), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => pending);
        string.Concat(transcript.Snapshot()).Should().Contain("[approval] denied");
    }

    private static ApprovalRequest MakeRequest(string toolName, string command) =>
        new(toolName, new JsonObject { ["command"] = command });
}
