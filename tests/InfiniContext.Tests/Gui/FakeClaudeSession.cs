using System.Threading.Channels;
using InfiniContext.Core.Events;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;

namespace InfiniContext.Tests.Gui;

/// <summary>Test double for <see cref="IClaudeSession"/>. Owns a real
/// <see cref="SessionStateMachine"/> (tests can call BeginSend/Cancel/HandleProcessExit through it),
/// a settable <see cref="SessionId"/>, and records of SendUserMessageAsync / InterruptAsync calls
/// so tests can assert "did the operator's prompt actually flow to the session?".</summary>
internal sealed class FakeClaudeSession : IClaudeSession
{
    private readonly Channel<ClaudeEvent> _events = Channel.CreateUnbounded<ClaudeEvent>();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid? SessionId { get; set; }
    public SessionStateMachine State { get; } = new();
    public ChannelReader<ClaudeEvent> Events => _events.Reader;
    public IReadOnlyList<string> StderrTail { get; } = Array.Empty<string>();
    public int UnknownEventCount { get; set; }
    public int MalformedLineCount { get; set; }
    public IReadOnlyDictionary<string, int> UnknownTypeHistogram { get; } = new Dictionary<string, int>();

    public List<string> SentMessages { get; } = new();
    public int InterruptCount { get; private set; }
    public bool StartAsyncCalled { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartAsyncCalled = true;
        return Task.CompletedTask;
    }

    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        SentMessages.Add(text);
        // Mirror the real ClaudeSession.SendUserMessageAsync, which calls _state.BeginSend() inside
        // the write lock before flushing to stdin. Without this, IsAgentTurnInFlight stays false
        // after a fake send and any code path that branches on "is the agent busy?" (e.g. the glue
        // buffer in SessionRowViewModel.SendOrQueueAsync) takes the Idle branch on subsequent calls.
        // Guarded: BeginSend is only legal from Idle, and tests may also drive the state machine
        // directly between sends.
        if (State.State == SessionState.Idle) State.BeginSend();
        return Task.CompletedTask;
    }

    public Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        InterruptCount++;
        return Task.CompletedTask;
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) => _exited.Task;

    public void SignalExit() => _exited.TrySetResult();

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        _exited.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
