using System.Threading.Channels;
using InfiniContext.Cli.SlashCommands;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Events;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;
using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Cli.SlashCommands;

public class FlushSlashCommandTests
{
    [Fact]
    public async Task Refuses_when_no_active_task_in_focused_session()
    {
        var session = new FakeSession();
        var output = new StringWriter();
        var ctx = MakeContext(session, output, activeTaskId: null);
        var cmd = new FlushSlashCommand();

        await cmd.ExecuteAsync(ctx, "", CancellationToken.None);

        output.ToString().Should().Contain("[flush] no active task");
        session.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Sends_nudge_with_task_id_when_active_task_is_set()
    {
        var session = new FakeSession();
        var output = new StringWriter();
        var ctx = MakeContext(session, output, activeTaskId: 42);
        var cmd = new FlushSlashCommand();

        await cmd.ExecuteAsync(ctx, "", CancellationToken.None);

        session.Sent.Should().ContainSingle();
        var msg = session.Sent[0];
        msg.Should().Contain("0042");
        msg.Should().Contain("progress.md");
        msg.Should().Contain("index.md");
        msg.Should().Contain("[flushed]");
        output.ToString().Should().Contain("[flush] requested for task 0042");
    }

    [Fact]
    public async Task Pads_task_id_to_four_digits_in_the_console_message()
    {
        var session = new FakeSession();
        var output = new StringWriter();
        var ctx = MakeContext(session, output, activeTaskId: 7);
        var cmd = new FlushSlashCommand();

        await cmd.ExecuteAsync(ctx, "", CancellationToken.None);

        output.ToString().Should().Contain("task 0007");
        session.Sent[0].Should().Contain("0007");
    }

    [Fact]
    public async Task Refuses_when_args_are_provided()
    {
        var session = new FakeSession();
        var output = new StringWriter();
        var ctx = MakeContext(session, output, activeTaskId: 42);
        var cmd = new FlushSlashCommand();

        await cmd.ExecuteAsync(ctx, "unexpected arg", CancellationToken.None);

        output.ToString().Should().Contain("takes no arguments");
        session.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Surfaces_dispatch_failure_without_crashing()
    {
        var session = new FakeSession { SendThrows = new IOException("pipe closed") };
        var output = new StringWriter();
        var ctx = MakeContext(session, output, activeTaskId: 42);
        var cmd = new FlushSlashCommand();

        await cmd.ExecuteAsync(ctx, "", CancellationToken.None);

        output.ToString().Should().Contain("[flush] dispatch failed: pipe closed");
    }

    [Fact]
    public void FlushNudgeTemplate_mentions_the_invariants_the_agent_should_follow()
    {
        // The template is what the agent sees verbatim (minus the task-id substitution). Pinning
        // a couple of stable invariants here prevents accidental drift in the prompt language.
        FlushSlashCommand.FlushNudgeTemplate.Should().Contain("{0:D4}");
        FlushSlashCommand.FlushNudgeTemplate.Should().Contain("progress.md");
        FlushSlashCommand.FlushNudgeTemplate.Should().Contain("index.md");
        FlushSlashCommand.FlushNudgeTemplate.Should().Contain("acquire the lock");
        FlushSlashCommand.FlushNudgeTemplate.Should().Contain("[flushed]");
        FlushSlashCommand.FlushNudgeTemplate.Should().Contain("Do not start new work");
    }

    // ---------- Helpers ----------

    private static SlashCommandContext MakeContext(FakeSession session, StringWriter output, int? activeTaskId) =>
        new(
            Session: session,
            Write: s => output.Write(s),
            Settings: InfiniContextSettings.Defaults,
            EffectiveSettingsJson: null,
            GetApprovalMode: null,
            SwitchApprovalMode: null,
            TaskHandler: null,
            PrepareTaskSwitch: null,
            GetLastCostUsd: () => null,
            GetLastNumTurns: () => null,
            Actions: new NoopActions(),
            Registry: new SlashCommandRegistry(Array.Empty<ISlashCommand>()))
        {
            ActiveTaskId = activeTaskId,
        };

    private sealed class NoopActions : IReplActions
    {
        public void RequestExit() { }
        public void RequestResume(string sessionId) { }
        public void RequestSwitch(TaskSwitchPlan plan) { }
        public void RequestFocus(Guid hostSessionId) { }
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
