using System.Threading.Channels;
using InfiniContext.Cli.SlashCommands;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Events;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;
using InfiniContext.Core.Tasks;
using FluentAssertions;
using Serilog;

namespace InfiniContext.Tests.Cli.SlashCommands;

public class NewSessionCommandTests
{
    [Fact]
    public async Task Without_cwd_argument_prints_usage()
    {
        var (mgr, ctx, actions, output) = await BuildAsync();
        await using var _ = mgr;
        var cmd = new NewSessionCommand();

        await cmd.ExecuteAsync(ctx, "", CancellationToken.None);

        output.ToString().Should().Contain("usage: /n <cwd>");
        mgr.ActiveCount.Should().Be(0);
        actions.FocusRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task With_missing_cwd_prints_error()
    {
        var (mgr, ctx, actions, output) = await BuildAsync();
        await using var _ = mgr;
        var cmd = new NewSessionCommand();

        var bogus = Path.Combine(Path.GetTempPath(), $"cp-nope-{Guid.NewGuid():N}");
        await cmd.ExecuteAsync(ctx, bogus, CancellationToken.None);

        output.ToString().Should().Contain("cwd does not exist");
        mgr.ActiveCount.Should().Be(0);
    }

    [Fact]
    public async Task Valid_cwd_spawns_session_and_requests_focus()
    {
        var (mgr, ctx, actions, output) = await BuildAsync();
        await using var _ = mgr;
        var cmd = new NewSessionCommand();

        await cmd.ExecuteAsync(ctx, Path.GetTempPath(), CancellationToken.None);

        mgr.ActiveCount.Should().Be(1);
        actions.FocusRequests.Should().ContainSingle()
            .Which.Should().Be(mgr.Active[0].Id);
        output.ToString().Should().Contain("[n] opened");
    }

    private static async Task<(SessionManager mgr, SlashCommandContext ctx, RecordingActions actions, StringWriter output)> BuildAsync()
    {
        var output = new StringWriter();
        var mgr = await SessionManager.OpenAsync(
            InfiniContextSettings.Defaults,
            Log.Logger,
            (slot, transcript, cwd) => new StubApprovalSink(),
            indexPath: Path.Combine(Path.GetTempPath(), $"cp-nstest-{Guid.NewGuid():N}.json"),
            sessionFactory: (_, _, _, _) => new FakeSession());
        var actions = new RecordingActions();
        var registry = new SlashCommandRegistry(new ISlashCommand[] { new NewSessionCommand() });
        var ctx = new SlashCommandContext(
            Session: new FakeSession(),
            Write: s => output.Write(s),
            Settings: InfiniContextSettings.Defaults,
            EffectiveSettingsJson: null,
            GetApprovalMode: null,
            SwitchApprovalMode: null,
            TaskHandler: null,
            PrepareTaskSwitch: null,
            GetLastCostUsd: () => null,
            GetLastNumTurns: () => null,
            Actions: actions,
            Registry: registry)
        {
            SessionManager = mgr,
        };
        return (mgr, ctx, actions, output);
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
