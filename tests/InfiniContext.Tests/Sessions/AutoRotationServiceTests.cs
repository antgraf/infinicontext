using System.Threading.Channels;
using FluentAssertions;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Events;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;
using InfiniContext.Core.Tasks;
using Serilog;

namespace InfiniContext.Tests.Sessions;

/// <summary>
/// Task 0013 / Slice 1: covers the Core-side auto-rotation policy promoted out of
/// <c>MultiSessionHost.TryStartAutoRotation</c>. The service should fire exactly one
/// <see cref="AutoRotationProposal"/> per qualifying <see cref="SessionState.Exited"/> transition,
/// gated by <see cref="SessionContext.RotationReason"/>, <see cref="SessionContext.ActiveTaskId"/>,
/// and the <see cref="AutoRotationGuard"/>.
/// </summary>
public class AutoRotationServiceTests
{
    [Fact]
    public async Task Hard_nudge_then_exit_with_task_id_fires_Approved_with_plan()
    {
        var fake = new FakeSession();
        var factory = new QueueingFactory(new IClaudeSession[] { fake });
        await using var mgr = NewManager(factory);
        var prepareCalled = 0;
        var fakePlan = NewSwitchPlan(taskId: 7);
        using var service = new AutoRotationService(
            mgr,
            new AutoRotationGuard(3, TimeSpan.FromMinutes(5)),
            (id, ct) =>
            {
                Interlocked.Increment(ref prepareCalled);
                return Task.FromResult<TaskSwitchPlan?>(id == 7 ? fakePlan : null);
            },
            Log.Logger);

        var proposals = new List<AutoRotationProposal>();
        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AutoRotationProposed += p => { proposals.Add(p); fired.TrySetResult(true); };

        var ctx = await mgr.CreateAsync(new SpawnRequest { Cwd = "/repo", ActiveTaskId = 7 });
        ctx.MarkRotationReason(RotationReason.Hard);
        fake.State.BeginSend();
        fake.State.HandleProcessExit(0);

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        prepareCalled.Should().Be(1);
        proposals.Should().ContainSingle();
        var p = proposals[0];
        p.Status.Should().Be(AutoRotationStatus.Approved);
        p.TaskId.Should().Be(7);
        p.Reason.Should().Be(RotationReason.Hard);
        p.Plan.Should().BeSameAs(fakePlan);
        p.Failure.Should().BeNull();
        p.RefusedCap.Should().BeNull();
        p.RefusedWindow.Should().BeNull();
    }

    [Fact]
    public async Task Soft_nudge_then_exit_with_task_id_fires_Approved()
    {
        var fake = new FakeSession();
        var factory = new QueueingFactory(new IClaudeSession[] { fake });
        await using var mgr = NewManager(factory);
        var fakePlan = NewSwitchPlan(taskId: 13);
        using var service = new AutoRotationService(
            mgr,
            new AutoRotationGuard(3, TimeSpan.FromMinutes(5)),
            (id, ct) => Task.FromResult<TaskSwitchPlan?>(id == 13 ? fakePlan : null),
            Log.Logger);

        var fired = new TaskCompletionSource<AutoRotationProposal>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AutoRotationProposed += p => fired.TrySetResult(p);

        var ctx = await mgr.CreateAsync(new SpawnRequest { Cwd = "/repo", ActiveTaskId = 13 });
        ctx.MarkRotationReason(RotationReason.Soft);
        fake.State.BeginSend();
        fake.State.HandleProcessExit(0);

        var proposal = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        proposal.Status.Should().Be(AutoRotationStatus.Approved);
        proposal.Reason.Should().Be(RotationReason.Soft);
        proposal.Plan.Should().BeSameAs(fakePlan);
    }

    [Fact]
    public async Task Exit_with_no_rotation_reason_does_not_fire()
    {
        var fake = new FakeSession();
        var factory = new QueueingFactory(new IClaudeSession[] { fake });
        await using var mgr = NewManager(factory);
        var prepareCalled = 0;
        using var service = new AutoRotationService(
            mgr,
            new AutoRotationGuard(3, TimeSpan.FromMinutes(5)),
            (id, ct) => { Interlocked.Increment(ref prepareCalled); return Task.FromResult<TaskSwitchPlan?>(NewSwitchPlan(id)); },
            Log.Logger);

        var fired = false;
        service.AutoRotationProposed += _ => fired = true;

        await mgr.CreateAsync(new SpawnRequest { Cwd = "/repo", ActiveTaskId = 7 });
        // No MarkRotationReason call тАФ baseline.
        fake.State.BeginSend();
        fake.State.HandleProcessExit(0);

        // The service has no async work to do on this path; give it one yield to be safe.
        await Task.Yield();
        fired.Should().BeFalse();
        prepareCalled.Should().Be(0);
    }

    [Fact]
    public async Task Exit_with_nudge_but_no_active_task_id_does_not_fire()
    {
        var fake = new FakeSession();
        var factory = new QueueingFactory(new IClaudeSession[] { fake });
        await using var mgr = NewManager(factory);
        var prepareCalled = 0;
        using var service = new AutoRotationService(
            mgr,
            new AutoRotationGuard(3, TimeSpan.FromMinutes(5)),
            (id, ct) => { Interlocked.Increment(ref prepareCalled); return Task.FromResult<TaskSwitchPlan?>(NewSwitchPlan(id)); },
            Log.Logger);

        var fired = false;
        service.AutoRotationProposed += _ => fired = true;

        var ctx = await mgr.CreateAsync(new SpawnRequest { Cwd = "/repo" }); // no ActiveTaskId
        ctx.MarkRotationReason(RotationReason.Hard);
        fake.State.BeginSend();
        fake.State.HandleProcessExit(0);

        await Task.Yield();
        fired.Should().BeFalse();
        prepareCalled.Should().Be(0);
    }

    [Fact]
    public async Task Guard_cap_fires_Refused_and_does_not_invoke_prepareSwitch()
    {
        var fake = new FakeSession();
        var factory = new QueueingFactory(new IClaudeSession[] { fake });
        await using var mgr = NewManager(factory);
        var prepareCalled = 0;
        var guard = new AutoRotationGuard(maxRotations: 2, window: TimeSpan.FromMinutes(5));
        guard.TryRegister(11).Should().BeTrue();
        guard.TryRegister(11).Should().BeTrue(); // cap is now 2/2

        using var service = new AutoRotationService(
            mgr,
            guard,
            (id, ct) => { Interlocked.Increment(ref prepareCalled); return Task.FromResult<TaskSwitchPlan?>(NewSwitchPlan(id)); },
            Log.Logger);
        var fired = new TaskCompletionSource<AutoRotationProposal>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AutoRotationProposed += p => fired.TrySetResult(p);

        var ctx = await mgr.CreateAsync(new SpawnRequest { Cwd = "/repo", ActiveTaskId = 11 });
        ctx.MarkRotationReason(RotationReason.Hard);
        fake.State.BeginSend();
        fake.State.HandleProcessExit(0);

        var proposal = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        proposal.Status.Should().Be(AutoRotationStatus.Refused);
        proposal.TaskId.Should().Be(11);
        proposal.Plan.Should().BeNull();
        proposal.RefusedCap.Should().Be(2);
        proposal.RefusedWindow.Should().Be(TimeSpan.FromMinutes(5));
        proposal.Failure.Should().BeNull();
        prepareCalled.Should().Be(0, "cap-hit path must not call the plan loader");
    }

    [Fact]
    public async Task PrepareSwitch_throw_fires_Failed_with_exception()
    {
        var fake = new FakeSession();
        var factory = new QueueingFactory(new IClaudeSession[] { fake });
        await using var mgr = NewManager(factory);
        var boom = new InvalidOperationException("loader exploded");
        using var service = new AutoRotationService(
            mgr,
            new AutoRotationGuard(3, TimeSpan.FromMinutes(5)),
            (id, ct) => Task.FromException<TaskSwitchPlan?>(boom),
            Log.Logger);
        var fired = new TaskCompletionSource<AutoRotationProposal>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AutoRotationProposed += p => fired.TrySetResult(p);

        var ctx = await mgr.CreateAsync(new SpawnRequest { Cwd = "/repo", ActiveTaskId = 5 });
        ctx.MarkRotationReason(RotationReason.Hard);
        fake.State.BeginSend();
        fake.State.HandleProcessExit(0);

        var proposal = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        proposal.Status.Should().Be(AutoRotationStatus.Failed);
        proposal.TaskId.Should().Be(5);
        proposal.Plan.Should().BeNull();
        proposal.Failure.Should().BeSameAs(boom);
    }

    [Fact]
    public async Task Service_constructed_after_session_still_observes_exit()
    {
        // Production path constructs the service before any session (see Program.cs).
        // The other tests cover the SessionCreated event subscription. This test exercises the
        // alternate path: the service's ctor iterates SessionManager.Active and attaches to
        // sessions that already exist, so a service constructed mid-life still observes the
        // next Exited transition.
        var fake = new FakeSession();
        var factory = new QueueingFactory(new IClaudeSession[] { fake });
        await using var mgr = NewManager(factory);
        var ctx = await mgr.CreateAsync(new SpawnRequest { Cwd = "/repo", ActiveTaskId = 7 });
        ctx.MarkRotationReason(RotationReason.Hard);

        var plan = NewSwitchPlan(7);
        using var service = new AutoRotationService(
            mgr,
            new AutoRotationGuard(3, TimeSpan.FromMinutes(5)),
            (id, ct) => Task.FromResult<TaskSwitchPlan?>(plan),
            Log.Logger);
        var fired = new TaskCompletionSource<AutoRotationProposal>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AutoRotationProposed += p => fired.TrySetResult(p);

        fake.State.BeginSend();
        fake.State.HandleProcessExit(0);

        var proposal = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        proposal.Status.Should().Be(AutoRotationStatus.Approved);
        proposal.Plan.Should().BeSameAs(plan);
    }

    [Fact]
    public async Task Dispose_detaches_handlers_and_suppresses_later_proposals()
    {
        var fake = new FakeSession();
        var factory = new QueueingFactory(new IClaudeSession[] { fake });
        await using var mgr = NewManager(factory);
        var service = new AutoRotationService(
            mgr,
            new AutoRotationGuard(3, TimeSpan.FromMinutes(5)),
            (id, ct) => Task.FromResult<TaskSwitchPlan?>(NewSwitchPlan(id)),
            Log.Logger);

        var fired = false;
        service.AutoRotationProposed += _ => fired = true;
        var ctx = await mgr.CreateAsync(new SpawnRequest { Cwd = "/repo", ActiveTaskId = 7 });
        ctx.MarkRotationReason(RotationReason.Hard);

        service.Dispose();
        fake.State.BeginSend();
        fake.State.HandleProcessExit(0);

        await Task.Yield();
        fired.Should().BeFalse("a disposed service must not raise proposals");
    }

    [Fact]
    public async Task Dispose_drains_in_flight_ProposeAsync_until_prepareSwitch_completes()
    {
        // Follow-up #1b (task 0013, 2026-05-27 handoff): when prepareSwitch is mid-flight,
        // Dispose must hold on the bounded drain until the proposal completes. Without this,
        // SessionHost.DisposeAsync would tear down taskStore/supervisor/logging while
        // ProposeAsync is still awaiting, losing the rotation log line to ObjectDisposedException.
        var fake = new FakeSession();
        var factory = new QueueingFactory(new IClaudeSession[] { fake });
        await using var mgr = NewManager(factory);

        var prepareGate = new TaskCompletionSource<TaskSwitchPlan?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = NewSwitchPlan(7);
        var service = new AutoRotationService(
            mgr,
            new AutoRotationGuard(3, TimeSpan.FromMinutes(5)),
            async (id, ct) =>
            {
                await prepareGate.Task.ConfigureAwait(false);
                return plan;
            },
            Log.Logger);

        var proposalFired = new TaskCompletionSource<AutoRotationProposal>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AutoRotationProposed += p => proposalFired.TrySetResult(p);

        var ctx = await mgr.CreateAsync(new SpawnRequest { Cwd = "/repo", ActiveTaskId = 7 });
        ctx.MarkRotationReason(RotationReason.Hard);
        fake.State.BeginSend();
        fake.State.HandleProcessExit(0);
        // OnStateTransition ran synchronously on this thread inside HandleProcessExit; the
        // unfinished ProposeAsync Task is now in _pending (the await of prepareGate suspends
        // before _pending.Add returns).

        var disposeTask = Task.Run(() => service.Dispose());

        await Task.Delay(200);
        disposeTask.IsCompleted.Should().BeFalse("Dispose must drain the in-flight proposal");

        prepareGate.SetResult(plan);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

        var proposal = await proposalFired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        proposal.Status.Should().Be(AutoRotationStatus.Approved);
        proposal.Plan.Should().BeSameAs(plan);
    }

    private static SessionManager NewManager(QueueingFactory factory) =>
        new(
            InfiniContextSettings.Defaults,
            Log.Logger,
            (slot, transcript, cwd) => new StubApprovalSink(),
            supervisor: null,
            lockRegistry: null,
            sessionFactory: factory.Build,
            indexPath: Path.Combine(Path.GetTempPath(), $"cp-arstest-{Guid.NewGuid():N}.json"));

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
