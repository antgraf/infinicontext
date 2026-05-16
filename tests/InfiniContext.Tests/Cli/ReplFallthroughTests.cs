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

namespace InfiniContext.Tests.Cli;

/// <summary>
/// Verifies the slash-command fall-through contract (decisions.md D6): any leading-/ token that
/// the host registry doesn't recognise is forwarded verbatim to <c>claude</c> via
/// <see cref="IClaudeSession.SendUserMessageAsync"/>. This is what makes claude's own slash-
/// commands (<c>/clear</c>, <c>/compact</c>) reach claude through the host.
/// </summary>
public class ReplFallthroughTests
{
    [Fact]
    public async Task Unknown_slash_command_is_forwarded_verbatim_to_claude()
    {
        await using var session = new RecordingSession();
        await using var ctx = BuildContext(session);
        var input = new StringReader("/clear\n/quit\n");
        var console = new TestConsole();
        var host = new MultiSessionHost(
            ctx,
            input,
            console,
            Log.Logger,
            writeLock: new object(),
            settings: InfiniContextSettings.Defaults);

        var result = await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        result.ExitCode.Should().Be(0);
        session.SentMessages.Should().ContainSingle().Which.Should().Be("/clear",
            "unknown slash-commands must reach claude as the literal text the user typed");
    }

    [Fact]
    public async Task Known_slash_command_is_NOT_forwarded_to_claude()
    {
        // /cost is a host command — it must be intercepted, not forwarded. Otherwise the
        // fall-through rule would shadow every host command.
        await using var session = new RecordingSession();
        await using var ctx = BuildContext(session);
        var input = new StringReader("/cost\n/quit\n");
        var console = new TestConsole();
        var host = new MultiSessionHost(
            ctx,
            input,
            console,
            Log.Logger,
            writeLock: new object(),
            settings: InfiniContextSettings.Defaults);

        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        session.SentMessages.Should().BeEmpty(
            "host-handled commands must not leak through to the spawned claude");
        // Confirm /cost actually executed by checking its output landed on stdout.
        console.Output.Should().Contain("[cost]");
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

    /// <summary>
    /// Minimal IClaudeSession that records each <c>SendUserMessageAsync</c> invocation. Used
    /// to assert what reached claude's stdin without needing the real binary.
    /// </summary>
    private sealed class RecordingSession : IClaudeSession
    {
        private readonly Channel<ClaudeEvent> _events = Channel.CreateUnbounded<ClaudeEvent>();
        private readonly TaskCompletionSource<bool> _exit = new();
        private readonly List<string> _sent = new();

        public IReadOnlyList<string> SentMessages
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
