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
            (slot, transcript, cwd) => new StubApprovalSink(),
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
            (slot, transcript, cwd) => new StubApprovalSink(),
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

    [Fact]
    public async Task Hard_nudge_then_session_exit_auto_rotates_via_SwitchPlan()
    {
        // Issue 0013 (task 0005): when a task-bound session exits and a context-pressure nudge
        // fired during its life, the host must auto-spawn a fresh session on the same task.
        // The MVP reuses the SwitchPlan primitive тАФ Program.cs's outer loop already respawns
        // on it тАФ so we assert that HostResult.SwitchPlan is populated for the same task id.
        var fake = new FakeSession();
        await using var ctx = BuildContext(fake, activeTaskId: 7);
        ctx.MarkRotationReason(InfiniContext.Core.Sessions.RotationReason.Hard);

        var input = new StringReader("");
        var console = new TestConsole();
        var prepareCalled = 0;
        var fakePlan = NewSwitchPlan(taskId: 7);
        var host = new MultiSessionHost(
            ctx,
            input,
            console,
            Log.Logger,
            settings: FastDrainSettings,
            prepareTaskSwitch: (id, ct) =>
            {
                Interlocked.Increment(ref prepareCalled);
                return Task.FromResult<TaskSwitchPlan?>(id == 7 ? fakePlan : null);
            },
            autoRotationGuard: new AutoRotationGuard(3, TimeSpan.FromMinutes(5)));

        var hostRun = host.RunAsync();

        // Move state out of Idle so the input loop is parked on _exitSignal (not on
        // ReadLineAsync тАФ which would never wake on our StringReader).
        fake.State.BeginSend();
        fake.State.HandleProcessExit(0); // OnStateTransition тЖТ TryStartAutoRotation тЖТ AutoRotateAsync
        fake.SignalExit(); // unblock the host's post-loop drain immediately

        var result = await hostRun.WaitAsync(TimeSpan.FromSeconds(5));

        prepareCalled.Should().Be(1, "AutoRotateAsync asks the loader for the same task id");
        result.SwitchPlan.Should().NotBeNull();
        result.SwitchPlan!.Task.Id.Should().Be(7);
        console.Output.Should().Contain("hard-nudge flush detected");
    }

    [Fact]
    public async Task Soft_nudge_then_session_exit_also_auto_rotates_via_SwitchPlan()
    {
        // Issue 0013: soft nudge ("finish current sub-task and exit cleanly so a fresh session
        // can resume") is also a handoff signal тАФ if the agent obeys and exits, the host
        // should auto-rotate just like for hard. Mirrors the hard-nudge test; only the reason
        // and the console label differ.
        var fake = new FakeSession();
        await using var ctx = BuildContext(fake, activeTaskId: 13);
        ctx.MarkRotationReason(InfiniContext.Core.Sessions.RotationReason.Soft);

        var input = new StringReader("");
        var console = new TestConsole();
        var prepareCalled = 0;
        var fakePlan = NewSwitchPlan(taskId: 13);
        var host = new MultiSessionHost(
            ctx,
            input,
            console,
            Log.Logger,
            settings: FastDrainSettings,
            prepareTaskSwitch: (id, ct) =>
            {
                Interlocked.Increment(ref prepareCalled);
                return Task.FromResult<TaskSwitchPlan?>(id == 13 ? fakePlan : null);
            },
            autoRotationGuard: new AutoRotationGuard(3, TimeSpan.FromMinutes(5)));

        var hostRun = host.RunAsync();
        fake.State.BeginSend();
        fake.State.HandleProcessExit(0);
        fake.SignalExit();

        var result = await hostRun.WaitAsync(TimeSpan.FromSeconds(5));

        prepareCalled.Should().Be(1);
        result.SwitchPlan.Should().NotBeNull();
        result.SwitchPlan!.Task.Id.Should().Be(13);
        console.Output.Should().Contain("soft-nudge flush detected",
            "the rotation message labels the triggering reason so logs distinguish soft vs hard rotations");
    }

    [Fact]
    public async Task Session_exit_without_a_nudge_reason_does_not_auto_rotate()
    {
        // Baseline: a session exit with RotationReason.None is treated as a normal end. No
        // SwitchPlan, no rotation banner. This is what the user's `/quit` looks like.
        var fake = new FakeSession();
        await using var ctx = BuildContext(fake, activeTaskId: 7);
        // No MarkRotationReason тАФ explicitly testing the baseline.

        var input = new StringReader("");
        var console = new TestConsole();
        var prepareCalled = 0;
        var host = new MultiSessionHost(
            ctx,
            input,
            console,
            Log.Logger,
            settings: FastDrainSettings,
            prepareTaskSwitch: (id, ct) =>
            {
                Interlocked.Increment(ref prepareCalled);
                return Task.FromResult<TaskSwitchPlan?>(NewSwitchPlan(id));
            },
            autoRotationGuard: new AutoRotationGuard(3, TimeSpan.FromMinutes(5)));

        var hostRun = host.RunAsync();
        fake.State.BeginSend();
        fake.State.HandleProcessExit(0);
        fake.SignalExit();

        var result = await hostRun.WaitAsync(TimeSpan.FromSeconds(5));

        prepareCalled.Should().Be(0, "no nudge fired so the loader must not be invoked");
        result.SwitchPlan.Should().BeNull();
        console.Output.Should().NotContain("auto-rotating");
    }

    [Fact]
    public async Task Auto_rotation_refused_when_guard_cap_is_hit()
    {
        // Drain the guard's budget for task 11 before the host even starts, so when the
        // session exits with a hard nudge the host can't rotate тАФ it falls back to a normal
        // exit and writes a "[task] auto-rotation refused" line to the console.
        var fake = new FakeSession();
        await using var ctx = BuildContext(fake, activeTaskId: 11);
        ctx.MarkRotationReason(InfiniContext.Core.Sessions.RotationReason.Hard);

        var guard = new AutoRotationGuard(maxRotations: 2, window: TimeSpan.FromMinutes(5));
        guard.TryRegister(11).Should().BeTrue();
        guard.TryRegister(11).Should().BeTrue();
        // Cap is now at 2/2 тАФ the next attempt (the host's) must be refused.

        var input = new StringReader("");
        var console = new TestConsole();
        var prepareCalled = 0;
        var host = new MultiSessionHost(
            ctx,
            input,
            console,
            Log.Logger,
            settings: FastDrainSettings,
            prepareTaskSwitch: (id, ct) =>
            {
                Interlocked.Increment(ref prepareCalled);
                return Task.FromResult<TaskSwitchPlan?>(NewSwitchPlan(id));
            },
            autoRotationGuard: guard);

        var hostRun = host.RunAsync();
        fake.State.BeginSend();
        fake.State.HandleProcessExit(0);
        fake.SignalExit();

        var result = await hostRun.WaitAsync(TimeSpan.FromSeconds(5));

        prepareCalled.Should().Be(0, "cap-hit path must not invoke the plan loader");
        result.SwitchPlan.Should().BeNull("the host falls back to a normal exit when the cap is hit");
        console.Output.Should().Contain("auto-rotation refused");
        console.Output.Should().Contain("0011");
    }

    private static readonly InfiniContextSettings FastDrainSettings =
        InfiniContextSettings.Defaults with
        {
            Session = InfiniContextSettings.Defaults.Session with { ExitDrainWaitSeconds = 1 },
        };

    private static TaskSwitchPlan NewSwitchPlan(int taskId)
    {
        var record = new TaskRecord(
            Id: taskId,
            HomeCwd: Path.GetTempPath(),
            Title: $"Task {taskId}",
            CreatedAtUnixMs: 0,
            LastActiveAtUnixMs: 0,
            Status: TaskItemStatus.InProgress,
            ParentTaskId: null);
        return new TaskSwitchPlan(record, Slug: $"task-{taskId}", FirstUserMessage: $"resume {taskId}", Drift: Array.Empty<string>());
    }

    private sealed class QueueingFactory
    {
        private readonly Queue<IClaudeSession> _queue;
        public QueueingFactory(IEnumerable<IClaudeSession> sessions) => _queue = new Queue<IClaudeSession>(sessions);
        public IClaudeSession Build(ClaudeSessionOptions options, Action<ClaudeEvent> observer, IApprovalSink approvalSink, ILogger logger) => _queue.Dequeue();
    }

    private static SessionContext BuildContext(IClaudeSession session, int? activeTaskId = null) =>
        new(
            id: Guid.NewGuid(),
            session: session,
            pressureMonitor: new ContextPressureMonitor(60, 85),
            injector: new ThresholdInjector((_, _) => Task.CompletedTask),
            approvalSink: new SwitchableApprovalSink(new StubApprovalSink()),
            approvalSlot: new PendingApprovalSlot(),
            transcript: new TranscriptModel(),
            activeTaskId: activeTaskId);

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

        /// <summary>Test hook: complete <see cref="WaitForExitAsync"/> without disposing.
        /// MultiSessionHost's post-loop drain awaits this; calling it lets the host return
        /// immediately instead of waiting out the full <c>ExitDrainWaitSeconds</c> budget.</summary>
        public void SignalExit() => _exit.TrySetResult(true);

        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            _exit.TrySetResult(true);
            return ValueTask.CompletedTask;
        }
    }
}
