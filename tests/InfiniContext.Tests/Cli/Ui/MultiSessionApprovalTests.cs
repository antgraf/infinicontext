using System.Text.Json.Nodes;
using System.Threading.Channels;
using InfiniContext.Cli;
using InfiniContext.Cli.Ui;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Events;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;
using InfiniContext.Core.Tasks;
using FluentAssertions;
using Serilog;
using Spectre.Console.Testing;

namespace InfiniContext.Tests.Cli.Ui;

/// <summary>
/// Stage 2B Phase 5: two concurrent sessions firing approvals must not block each other. The
/// focused session's input drives its own slot; the non-focused session's request stays parked
/// until the user <c>/focus</c>es it.
/// </summary>
public class MultiSessionApprovalTests
{
    [Fact]
    public async Task Approval_in_unfocused_session_does_not_block_focused_input()
    {
        await using var sessionA = new FakeSession();
        await using var sessionB = new FakeSession();
        await using var ctxA = BuildContext(sessionA);
        await using var ctxB = BuildContext(sessionB);

        // ctxB is unfocused; fire an approval on its slot before the host starts.
        var bRequest = ctxB.ApprovalSink.RequestAsync(
            new ApprovalRequest("Write", new JsonObject { ["path"] = "/etc/passwd" }),
            CancellationToken.None);

        // The focused session is ctxA. Drive its input through /quit — the unfocused approval
        // must NOT consume input from the host's reader.
        var input = new StringReader("/quit\n");
        var console = new TestConsole();
        var host = new MultiSessionHost(
            ctxA, input, console, Log.Logger,
            settings: InfiniContextSettings.Defaults);

        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        bRequest.IsCompleted.Should().BeFalse(
            "an unfocused session's approval must remain parked, not be answered by the host's quit input");
        ctxB.ApprovalSlot.Current.Should().NotBeNull();

        // Resolve to drain the awaiting task so test teardown is clean.
        ctxB.ApprovalSlot.Resolve(ApprovalBehavior.Deny);
        (await bRequest).Behavior.Should().Be(ApprovalBehavior.Deny);
    }

    [Fact]
    public async Task Y_answer_in_input_resolves_focused_session_approval_with_allow()
    {
        await using var sessionA = new FakeSession();
        await using var ctxA = BuildContext(sessionA);

        var pending = ctxA.ApprovalSink.RequestAsync(
            new ApprovalRequest("Bash", new JsonObject { ["command"] = "echo hi" }),
            CancellationToken.None);

        var input = new StringReader("y\n/quit\n");
        var console = new TestConsole();
        var host = new MultiSessionHost(
            ctxA, input, console, Log.Logger,
            settings: InfiniContextSettings.Defaults);

        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var response = await pending;
        response.Behavior.Should().Be(ApprovalBehavior.Allow);
        sessionA.SentMessages.Should().BeEmpty(
            "while an approval is pending the input goes to the slot, not to claude as a prompt");
    }

    [Fact]
    public async Task Anything_other_than_y_resolves_focused_approval_with_deny()
    {
        await using var sessionA = new FakeSession();
        await using var ctxA = BuildContext(sessionA);

        var pending = ctxA.ApprovalSink.RequestAsync(
            new ApprovalRequest("Bash", new JsonObject { ["command"] = "rm -rf /" }),
            CancellationToken.None);

        var input = new StringReader("n\n/quit\n");
        var console = new TestConsole();
        var host = new MultiSessionHost(
            ctxA, input, console, Log.Logger,
            settings: InfiniContextSettings.Defaults);

        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var response = await pending;
        response.Behavior.Should().Be(ApprovalBehavior.Deny);
    }

    [Fact]
    public async Task Slash_commands_are_intercepted_only_when_no_approval_is_pending()
    {
        // /quit must still work to exit cleanly when no approval is pending — even if the user
        // happens to type `/quit` after seeing an approval banner that was already resolved by
        // timeout or cancellation.
        await using var session = new FakeSession();
        await using var ctx = BuildContext(session);

        var input = new StringReader("/quit\n");
        var console = new TestConsole();
        var host = new MultiSessionHost(
            ctx, input, console, Log.Logger,
            settings: InfiniContextSettings.Defaults);

        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // Reaching here means /quit was treated as a slash command and triggered exit.
    }

    private static SessionContext BuildContext(IClaudeSession session)
    {
        var slot = new PendingApprovalSlot();
        var transcript = new TranscriptModel();
        // Wire the per-session SwitchableApprovalSink around a SlotApprovalSink so the sink's
        // RequestAsync path actually parks into the slot (the same wiring SessionManager builds in
        // production via ApprovalSinkFactory).
        var sink = new SwitchableApprovalSink(new SlotApprovalSink(slot, transcript, timeout: TimeSpan.FromSeconds(60)));
        return new SessionContext(
            id: Guid.NewGuid(),
            session: session,
            pressureMonitor: new ContextPressureMonitor(60, 85),
            injector: new ThresholdInjector((_, _) => Task.CompletedTask),
            approvalSink: sink,
            approvalSlot: slot,
            transcript: transcript,
            activeTaskId: null);
    }

    private sealed class FakeSession : IClaudeSession
    {
        private readonly Channel<ClaudeEvent> _events = Channel.CreateUnbounded<ClaudeEvent>();
        private readonly TaskCompletionSource<bool> _exit = new();
        private readonly List<string> _sent = new();

        public List<string> SentMessages
        {
            get { lock (_sent) return new List<string>(_sent); }
        }

        public Guid? SessionId => null;
        public SessionStateMachine State { get; } = new();
        public ChannelReader<ClaudeEvent> Events => _events.Reader;
        public IReadOnlyList<string> StderrTail { get; } = Array.Empty<string>();
        public int UnknownEventCount => 0;
        public int MalformedLineCount => 0;
        public IReadOnlyDictionary<string, int> UnknownTypeHistogram { get; } = new Dictionary<string, int>();

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
        {
            lock (_sent) _sent.Add(text);
            return Task.CompletedTask;
        }
        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => _exit.Task;

        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            _exit.TrySetResult(true);
            return ValueTask.CompletedTask;
        }
    }
}
