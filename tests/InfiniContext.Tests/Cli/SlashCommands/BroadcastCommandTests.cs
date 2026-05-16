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

public class BroadcastCommandTests
{
    [Fact]
    public async Task Missing_targets_or_text_prints_usage()
    {
        await using var fx = await Fixture.OpenAsync();
        await fx.SpawnAsync("/a");
        await fx.SpawnAsync("/b");
        var cmd = new BroadcastCommand();

        await cmd.ExecuteAsync(fx.Context, "", CancellationToken.None);
        await cmd.ExecuteAsync(fx.Context, "*", CancellationToken.None);

        fx.Output.ToString().Should().Contain("usage: /broadcast");
    }

    [Fact]
    public async Task Star_target_fans_out_to_every_active_session()
    {
        await using var fx = await Fixture.OpenAsync();
        var a = await fx.SpawnAsync("/a");
        var b = await fx.SpawnAsync("/b");
        var cmd = new BroadcastCommand();

        await cmd.ExecuteAsync(fx.Context, "* run the suite", CancellationToken.None);

        ((FakeSession)a.Session).Sent.Should().Equal(new[] { "run the suite" });
        ((FakeSession)b.Session).Sent.Should().Equal(new[] { "run the suite" });
        fx.Output.ToString().Should().Contain("sent to 2/2");
    }

    [Fact]
    public async Task Prefix_list_resolves_to_specific_active_sessions()
    {
        await using var fx = await Fixture.OpenAsync();
        var a = await fx.SpawnAsync("/a");
        var b = await fx.SpawnAsync("/b");
        var c = await fx.SpawnAsync("/c");
        var prefixA = a.Id.ToString("D")[..6];
        var prefixC = c.Id.ToString("D")[..6];
        var cmd = new BroadcastCommand();

        await cmd.ExecuteAsync(fx.Context, $"{prefixA},{prefixC} ping", CancellationToken.None);

        ((FakeSession)a.Session).Sent.Should().Equal(new[] { "ping" });
        ((FakeSession)b.Session).Sent.Should().BeEmpty();
        ((FakeSession)c.Session).Sent.Should().Equal(new[] { "ping" });
    }

    [Fact]
    public async Task Unknown_prefix_aborts_with_error_and_no_dispatch()
    {
        await using var fx = await Fixture.OpenAsync();
        var a = await fx.SpawnAsync("/a");
        var b = await fx.SpawnAsync("/b");
        var cmd = new BroadcastCommand();

        await cmd.ExecuteAsync(fx.Context, "zzzzzz,abcdef hi", CancellationToken.None);

        fx.Output.ToString().Should().Contain("no active session matches 'zzzzzz'");
        ((FakeSession)a.Session).Sent.Should().BeEmpty();
        ((FakeSession)b.Session).Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Single_target_is_rejected_to_steer_user_to_regular_prompt()
    {
        // /broadcast with one target is suspicious — the user could've just typed at the prompt or
        // /focus'd. Steer them rather than send.
        await using var fx = await Fixture.OpenAsync();
        var a = await fx.SpawnAsync("/a");
        await fx.SpawnAsync("/b");
        var prefixA = a.Id.ToString("D")[..6];
        var cmd = new BroadcastCommand();

        await cmd.ExecuteAsync(fx.Context, $"{prefixA} solo", CancellationToken.None);

        fx.Output.ToString().Should().Contain("needs at least two targets");
        ((FakeSession)a.Session).Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task No_active_sessions_is_reported_distinctly()
    {
        await using var fx = await Fixture.OpenAsync();
        var cmd = new BroadcastCommand();

        await cmd.ExecuteAsync(fx.Context, "* hi", CancellationToken.None);

        fx.Output.ToString().Should().Contain("no active sessions");
    }

    [Fact]
    public async Task Reports_failed_and_sent_counts_when_one_send_throws()
    {
        await using var fx = await Fixture.OpenAsync();
        var a = await fx.SpawnAsync("/a");
        var b = await fx.SpawnAsync("/b");
        ((FakeSession)b.Session).SendThrows = new InvalidOperationException("disconnected");
        var cmd = new BroadcastCommand();

        await cmd.ExecuteAsync(fx.Context, "* ping", CancellationToken.None);

        var output = fx.Output.ToString();
        output.Should().Contain("sent to 1/2");
        output.Should().Contain("1 failed");
        output.Should().Contain("disconnected");
    }

    [Fact]
    public async Task Without_session_manager_reports_unsupported()
    {
        var ctx = new SlashCommandContext(
            Session: new FakeSession(),
            Write: _ => { },
            Settings: InfiniContextSettings.Defaults,
            EffectiveSettingsJson: null,
            GetApprovalMode: null,
            SwitchApprovalMode: null,
            TaskHandler: null,
            PrepareTaskSwitch: null,
            GetLastCostUsd: () => null,
            GetLastNumTurns: () => null,
            Actions: new NoopActions(),
            Registry: new SlashCommandRegistry(Array.Empty<ISlashCommand>()));
        var output = new StringWriter();
        var withWrite = ctx with { Write = s => output.Write(s) };
        var cmd = new BroadcastCommand();

        await cmd.ExecuteAsync(withWrite, "* hello", CancellationToken.None);

        output.ToString().Should().Contain("multi-session not available");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SessionManager Mgr { get; init; }
        public required NoopActions Actions { get; init; }
        public required StringWriter Output { get; init; }

        public SlashCommandContext Context => new(
            Session: new FakeSession(),
            Write: s => Output.Write(s),
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
        };

        public Task<SessionContext> SpawnAsync(string cwd) =>
            Mgr.CreateAsync(new SpawnRequest { Cwd = cwd });

        public static async Task<Fixture> OpenAsync()
        {
            var mgr = await SessionManager.OpenAsync(
                InfiniContextSettings.Defaults,
                Log.Logger,
                (slot, transcript) => new StubApprovalSink(),
                indexPath: Path.Combine(Path.GetTempPath(), $"cp-bcast-{Guid.NewGuid():N}.json"),
                sessionFactory: (_, _, _, _) => new FakeSession());
            return new Fixture
            {
                Mgr = mgr,
                Actions = new NoopActions(),
                Output = new StringWriter(),
            };
        }

        public ValueTask DisposeAsync() => Mgr.DisposeAsync();
    }

    private sealed class NoopActions : IReplActions
    {
        public void RequestExit() { }
        public void RequestResume(string sessionId) { }
        public void RequestSwitch(TaskSwitchPlan plan) { }
        public void RequestFocus(Guid hostSessionId) { }
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
        private readonly List<string> _sent = new();

        public Exception? SendThrows { get; set; }
        public IReadOnlyList<string> Sent
        {
            get { lock (_sent) return _sent.ToArray(); }
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
            if (SendThrows is not null) return Task.FromException(SendThrows);
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
