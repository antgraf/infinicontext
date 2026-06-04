using System.Threading.Channels;
using InfiniContext.Core.SlashCommands;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Events;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;
using InfiniContext.Core.Tasks;
using FluentAssertions;
using Serilog;

namespace InfiniContext.Tests.Cli.SlashCommands;

public class FocusCommandTests
{
    [Fact]
    public async Task Focus_with_unambiguous_prefix_requests_match()
    {
        var fx = await Fixture.OpenAsync();
        await using var _ = fx;
        var a = await fx.SpawnAsync("/a");
        var b = await fx.SpawnAsync("/b");
        var cmd = new FocusCommand();

        await cmd.ExecuteAsync(fx.Context, b.Id.ToString("D")[..4], CancellationToken.None);

        fx.Actions.FocusRequests.Should().ContainSingle().Which.Should().Be(b.Id);
    }

    [Fact]
    public async Task Focus_with_no_match_reports_error_and_does_not_request_focus()
    {
        var fx = await Fixture.OpenAsync();
        await using var _ = fx;
        await fx.SpawnAsync("/a");
        var cmd = new FocusCommand();

        await cmd.ExecuteAsync(fx.Context, "zzzzz", CancellationToken.None);

        fx.Output.Should().Contain("no active session matches");
        fx.Actions.FocusRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Focus_with_ambiguous_prefix_reports_error()
    {
        var fx = await Fixture.OpenAsync();
        await using var _ = fx;
        await fx.SpawnAsync("/a");
        await fx.SpawnAsync("/b");
        var cmd = new FocusCommand();

        await cmd.ExecuteAsync(fx.Context, "", CancellationToken.None);

        fx.Output.Should().Contain("usage: /focus");
    }

    [Fact]
    public async Task Next_cycles_forward_through_active_sessions()
    {
        var fx = await Fixture.OpenAsync();
        await using var _ = fx;
        var a = await fx.SpawnAsync("/a");
        var b = await fx.SpawnAsync("/b");
        var c = await fx.SpawnAsync("/c");
        fx.FocusedId = a.Id;
        var cmd = new FocusNextCommand();

        await cmd.ExecuteAsync(fx.Context, "", CancellationToken.None);

        fx.Actions.FocusRequests.Should().ContainSingle().Which.Should().Be(b.Id);
    }

    [Fact]
    public async Task Prev_cycles_backward_with_wraparound()
    {
        var fx = await Fixture.OpenAsync();
        await using var _ = fx;
        var a = await fx.SpawnAsync("/a");
        var b = await fx.SpawnAsync("/b");
        fx.FocusedId = a.Id;
        var cmd = new FocusPrevCommand();

        await cmd.ExecuteAsync(fx.Context, "", CancellationToken.None);

        fx.Actions.FocusRequests.Should().ContainSingle().Which.Should().Be(b.Id,
            "going prev from the first session wraps to the last");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SessionManager Mgr { get; init; }
        public required RecordingActions Actions { get; init; }
        public required FakeSlashCommandRenderer Renderer { get; init; }
        public Guid? FocusedId { get; set; }

        public string Output => Renderer.Captured;

        public SlashCommandContext Context => new(
            Session: new FakeSession(),
            Renderer: Renderer,
            Settings: InfiniContextSettings.Defaults,
            EffectiveSettingsJson: null,
            GetApprovalMode: null,
            SwitchApprovalMode: null,
            TaskHandler: null,
            PrepareTaskSwitch: null,
            GetLastCostUsd: () => null,
            GetLastNumTurns: () => null,
            Actions: Actions,
            Registry: new SlashCommandRegistry(Array.Empty<ISlashCommand>()))
        {
            SessionManager = Mgr,
            FocusedHostSessionId = FocusedId,
        };

        public Task<SessionContext> SpawnAsync(string cwd) =>
            Mgr.CreateAsync(new SpawnRequest { Cwd = cwd });

        public static async Task<Fixture> OpenAsync()
        {
            var mgr = await SessionManager.OpenAsync(
                InfiniContextSettings.Defaults,
                Log.Logger,
                (slot, transcript, cwd) => new StubApprovalSink(),
                indexPath: Path.Combine(Path.GetTempPath(), $"cp-focus-{Guid.NewGuid():N}.json"),
                sessionFactory: (_, _, _, _) => new FakeSession());
            return new Fixture
            {
                Mgr = mgr,
                Actions = new RecordingActions(),
                Renderer = new FakeSlashCommandRenderer(),
            };
        }

        public ValueTask DisposeAsync() => Mgr.DisposeAsync();
    }

    private sealed class RecordingActions : IReplActions
    {
        public List<Guid> FocusRequests { get; } = new();
        public void RequestExit() { }
        public void RequestResume(string sessionId) { }
        public void RequestSwitch(TaskSwitchPlan plan) { }
        public void RequestFocus(Guid hostSessionId) => FocusRequests.Add(hostSessionId);
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
        public ValueTask DisposeAsync() { _events.Writer.TryComplete(); _exit.TrySetResult(true); return ValueTask.CompletedTask; }
    }
}
