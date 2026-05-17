using System.Threading.Channels;
using InfiniContext.Cli.Tasks;
using InfiniContext.Cli.Ui;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Events;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;
using InfiniContext.Core.Tasks;
using FluentAssertions;
using Serilog;
using Spectre.Console.Testing;

namespace InfiniContext.Tests.Cli;

/// <summary>
/// Unit-level tests for the <see cref="Repl"/> <c>/task switch</c> interception (T2 + A3).
/// Uses a stub <see cref="IClaudeSession"/> and an in-memory <see cref="TaskStore"/> so the
/// REPL loop runs without spawning the binary or reading real fixtures.
/// </summary>
public class ReplTaskSwitchTests
{
    [Fact]
    public async Task Slash_task_switch_intercepts_and_returns_plan_in_ReplResult()
    {
        await using var fixture = await Fixture.OpenAsync();
        var record = await SeedTaskAsync(fixture, id: 1);
        var plan = new TaskSwitchPlan(record, "demo-task", "[task] resume 0001-demo-task", Array.Empty<string>());

        var result = await RunReplAsync(
            fixture,
            stdin: "/task switch 1\n",
            prepareTaskSwitch: (_, _) => Task.FromResult<TaskSwitchPlan?>(plan));

        result.SwitchPlan.Should().BeSameAs(plan);
        fixture.Console.Output.Should().Contain("[task] switching to 0001");
    }

    [Fact]
    public async Task Slash_task_switch_succeeds_when_task_home_cwd_differs_from_host_cwd()
    {
        // Tasks own their home_cwd; the host's outer loop overrides the next session's cwd to
        // the task's home. So a switch to a task whose home differs from the launch dir must
        // succeed, with the plan surfacing the destination cwd in the user-visible output.
        await using var fixture = await Fixture.OpenAsync();
        var otherHome = Path.Combine(Path.GetTempPath(), $"cp-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(otherHome);
        try
        {
            var record = new TaskRecord(
                Id: 7,
                HomeCwd: otherHome,
                Title: "task in another repo",
                CreatedAtUnixMs: 0,
                LastActiveAtUnixMs: 0,
                Status: TaskItemStatus.Idle,
                ParentTaskId: null);
            var plan = new TaskSwitchPlan(record, "task-in-another-repo", "[task] resume", Array.Empty<string>());

            var result = await RunReplAsync(
                fixture,
                stdin: "/task switch 7\n",
                prepareTaskSwitch: (_, _) => Task.FromResult<TaskSwitchPlan?>(plan));

            result.SwitchPlan.Should().BeSameAs(plan);
            var stdout = fixture.Console.Output;
            stdout.Should().Contain("switching to 0007");
            stdout.Should().Contain(otherHome); // cwd hint surfaces when it differs from cwd
        }
        finally
        {
            try { Directory.Delete(otherHome, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Slash_task_list_routes_to_handler_without_exiting_the_repl()
    {
        // /task <subcommand> other than `switch` must NOT exit the inner loop. A subsequent
        // /quit reaches the loop and returns cleanly with no SwitchPlan.
        await using var fixture = await Fixture.OpenAsync();
        await SeedTaskAsync(fixture, id: 1);

        var result = await RunReplAsync(
            fixture,
            stdin: "/task list\n/quit\n",
            prepareTaskSwitch: (_, _) => Task.FromResult<TaskSwitchPlan?>(null));

        result.SwitchPlan.Should().BeNull();
        var stdout = fixture.Output.ToString();
        // TaskCommandHandler renders an id column and the seeded title.
        stdout.Should().Contain("0001");
    }

    [Fact]
    public async Task Slash_task_switch_unknown_id_prints_not_found_and_keeps_repl_running()
    {
        await using var fixture = await Fixture.OpenAsync();

        var result = await RunReplAsync(
            fixture,
            stdin: "/task switch 999\n/quit\n",
            prepareTaskSwitch: (_, _) => Task.FromResult<TaskSwitchPlan?>(null));

        result.SwitchPlan.Should().BeNull();
        fixture.Console.Output.Should().Contain("[task] 0999 not found");
    }

    private static async Task<TaskRecord> SeedTaskAsync(Fixture fixture, int id)
    {
        var record = new TaskRecord(
            Id: id,
            HomeCwd: fixture.HomeCwd,
            Title: $"seeded task {id}",
            CreatedAtUnixMs: 0,
            LastActiveAtUnixMs: 0,
            Status: TaskItemStatus.Idle,
            ParentTaskId: null);
        await fixture.Store.CreateAsync(record);
        return record;
    }

    private static async Task<HostResult> RunReplAsync(
        Fixture fixture,
        string stdin,
        Func<int, CancellationToken, Task<TaskSwitchPlan?>> prepareTaskSwitch)
    {
        await using var session = new FakeSession();
        await using var ctx = new SessionContext(
            id: Guid.NewGuid(),
            session: session,
            pressureMonitor: new ContextPressureMonitor(60, 85),
            injector: new ThresholdInjector((_, _) => Task.CompletedTask),
            approvalSink: new SwitchableApprovalSink(new StubApprovalSink()),
            approvalSlot: new PendingApprovalSlot(),
            transcript: new TranscriptModel(),
            activeTaskId: null);
        var input = new StringReader(stdin);
        var host = new MultiSessionHost(
            ctx,
            input,
            fixture.Console,
            fixture.Logger,
            writeLock: new object(),
            settings: InfiniContextSettings.Defaults,
            taskHandler: fixture.Handler,
            prepareTaskSwitch: prepareTaskSwitch);

        return await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class StubApprovalSink : IApprovalSink
    {
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ApprovalResponse.Deny("stub"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TaskStore Store { get; init; }
        public required TaskCommandHandler Handler { get; init; }
        public required StringWriter Output { get; init; }
        public required string HomeCwd { get; init; }
        public required string TempPath { get; init; }
        // Default TestConsole width is 80 cols; on CI the runneradmin temp path exceeds that and
        // gets wrapped mid-string, breaking Contains-the-cwd assertions. Wide enough to never wrap.
        public TestConsole Console { get; } = new TestConsole().Width(1000);
        public ILogger Logger { get; } = Log.Logger;

        public static async Task<Fixture> OpenAsync()
        {
            var temp = Path.Combine(Path.GetTempPath(), $"cp-replswitch-{Guid.NewGuid():N}");
            var home = Path.Combine(temp, "repo");
            Directory.CreateDirectory(home);

            var store = await TaskStore.OpenAsync(":memory:");
            var service = new TaskService(store, new StubLightModel());
            var output = new StringWriter();
            var handler = new TaskCommandHandler(service, output);

            return new Fixture
            {
                Store = store,
                Handler = handler,
                Output = output,
                HomeCwd = home,
                TempPath = temp,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            try { Directory.Delete(TempPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Minimal <see cref="IClaudeSession"/> stub. State stays at Idle so the REPL prompt fires
    /// on the first iteration; events channel is empty; WaitForExitAsync resolves on dispose.
    /// </summary>
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

    private sealed class StubLightModel : ILightModelService
    {
        public Task<string> GenerateShortTitleAsync(string description, int maxWords = 6, CancellationToken cancellationToken = default)
            => Task.FromResult("stub-title");
        public Task<string> SummarizeAsync(string text, int maxSentences = 2, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<CategoryResult> CategorizeAsync(string text, IReadOnlyList<string> categories, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<SafetyReview> ReviewToolCallSafetyAsync(SafetyReviewRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
