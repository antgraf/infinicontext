using System.Threading.Channels;
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
/// Stage 2B Phase 4a unit tests for <see cref="MultiSessionHost"/>'s transcript-echo wiring.
/// End-to-end behavior (real spawned process, slash-command dispatch) lives in
/// <c>ReplSmokeTests</c> + <c>ReplFallthroughTests</c> + <c>ReplTaskSwitchTests</c>.
/// </summary>
public class MultiSessionHostTests
{
    [Fact]
    public async Task Fragments_appended_during_run_echo_to_the_focused_console()
    {
        await using var session = new FakeSession();
        await using var ctx = BuildContext(session);
        var input = new StringReader("/quit\n");
        var console = new TestConsole();
        var host = new MultiSessionHost(
            ctx,
            input,
            console,
            Log.Logger,
            settings: InfiniContextSettings.Defaults);
        var hostRun = host.RunAsync();

        // Simulate what SessionTranscriptReader does in a real session: append a fragment.
        ctx.Transcript.Append("hello world\n");

        var result = await hostRun.WaitAsync(TimeSpan.FromSeconds(5));

        result.ExitCode.Should().Be(0);
        console.Output.Should().Contain("hello world");
    }

    [Fact]
    public async Task Fragments_appended_after_RunAsync_returns_do_not_echo_to_console()
    {
        // Phase 4b multi-pane focus rotation depends on this: when a session is no longer the
        // active host, fragments may still flow into the transcript but the global console
        // shouldn't receive them. The single-pane host enforces the same invariant on shutdown.
        await using var session = new FakeSession();
        await using var ctx = BuildContext(session);
        var input = new StringReader("/quit\n");
        var console = new TestConsole();
        var host = new MultiSessionHost(
            ctx,
            input,
            console,
            Log.Logger,
            settings: InfiniContextSettings.Defaults);

        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var beforePostRun = console.Output;
        ctx.Transcript.Append("after-shutdown fragment\n");

        console.Output.Should().Be(beforePostRun, "the host must unsubscribe from FragmentAppended on RunAsync return");
    }

    [Fact]
    public async Task RequestFocus_replays_target_session_transcript_and_emits_switch_banner()
    {
        // ctxB has accumulated transcript while off-screen (the SessionTranscriptReader runs
        // regardless of focus). On /focus to ctxB, the host should:
        //   1) print a "[focus] switched to ..." banner,
        //   2) replay ctxB.Transcript.Snapshot() so the user sees recent context.
        var fakeA = new FakeSession();
        var fakeB = new FakeSession();
        var factory = new QueueingFactory(new IClaudeSession[] { fakeA, fakeB });
        await using var mgr = await SessionManager.OpenAsync(
            InfiniContextSettings.Defaults,
            Log.Logger,
            (slot, transcript) => new StubApprovalSink(),
            sessionFactory: factory.Build,
            indexPath: Path.Combine(Path.GetTempPath(), $"cp-multifocus-{Guid.NewGuid():N}.json"));
        var ctxA = await mgr.CreateAsync(new SpawnRequest { Cwd = "/a" });
        var ctxB = await mgr.CreateAsync(new SpawnRequest { Cwd = "/b" });
        ctxB.Transcript.Append("hello-from-B\n");

        var input = new StringReader($"/focus {ctxB.Id:D}\n/quit\n");
        var console = new TestConsole();
        var host = new MultiSessionHost(
            ctxA,
            input,
            console,
            Log.Logger,
            settings: InfiniContextSettings.Defaults,
            sessionManager: mgr);

        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        console.Output.Should().Contain("hello-from-B", "ctxB's accumulated transcript is replayed on /focus");
        console.Output.Should().Contain("[focus] switched to");
    }

    [Fact]
    public async Task Fragments_to_unfocused_session_do_not_echo()
    {
        var fakeA = new FakeSession();
        var fakeB = new FakeSession();
        var factory = new QueueingFactory(new IClaudeSession[] { fakeA, fakeB });
        await using var mgr = await SessionManager.OpenAsync(
            InfiniContextSettings.Defaults,
            Log.Logger,
            (slot, transcript) => new StubApprovalSink(),
            sessionFactory: factory.Build,
            indexPath: Path.Combine(Path.GetTempPath(), $"cp-unfoc-{Guid.NewGuid():N}.json"));
        var ctxA = await mgr.CreateAsync(new SpawnRequest { Cwd = "/a" });
        var ctxB = await mgr.CreateAsync(new SpawnRequest { Cwd = "/b" });

        var input = new StringReader("/quit\n");
        var console = new TestConsole();
        var host = new MultiSessionHost(
            ctxA,
            input,
            console,
            Log.Logger,
            settings: InfiniContextSettings.Defaults,
            sessionManager: mgr);
        // Append to the unfocused session BEFORE running, so the host won't see it on subscribe.
        // (FragmentAppended fires synchronously, so timing is deterministic.)
        ctxB.Transcript.Append("silent-from-B\n");

        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        console.Output.Should().NotContain("silent-from-B",
            "fragments to a non-focused session must not reach the live console");
    }

    private sealed class QueueingFactory
    {
        private readonly Queue<IClaudeSession> _queue;
        public QueueingFactory(IEnumerable<IClaudeSession> sessions) => _queue = new Queue<IClaudeSession>(sessions);
        public IClaudeSession Build(ClaudeSessionOptions options, Action<ClaudeEvent> observer, IApprovalSink approvalSink, ILogger logger) => _queue.Dequeue();
    }

    private static SessionContext BuildContext(IClaudeSession session) =>
        new(
            id: Guid.NewGuid(),
            session: session,
            pressureMonitor: new ContextPressureMonitor(60, 85),
            injector: new ThresholdInjector((_, _) => Task.CompletedTask),
            approvalSink: new SwitchableApprovalSink(new StubApprovalSink()),
            approvalSlot: new PendingApprovalSlot(),
            transcript: new TranscriptModel(),
            activeTaskId: null);

    private sealed class StubApprovalSink : IApprovalSink
    {
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ApprovalResponse.Deny("stub"));
    }

    private sealed class FakeSession : IClaudeSession
    {
        private readonly Channel<ClaudeEvent> _events = Channel.CreateUnbounded<ClaudeEvent>();
        private readonly TaskCompletionSource<bool> _exit = new();

        public Guid? SessionId => null;
        public SessionStateMachine State { get; } = new();
        public ChannelReader<ClaudeEvent> Events => _events.Reader;
        public IReadOnlyList<string> StderrTail { get; } = Array.Empty<string>();
        public int UnknownEventCount => 0;
        public int MalformedLineCount => 0;
        public IReadOnlyDictionary<string, int> UnknownTypeHistogram { get; } = new Dictionary<string, int>();

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
