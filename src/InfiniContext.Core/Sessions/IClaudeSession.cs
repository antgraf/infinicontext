using System.Threading.Channels;
using InfiniContext.Core.Events;
using InfiniContext.Core.State;

namespace InfiniContext.Core.Sessions;

/// <summary>
/// One running <c>claude</c> child process: stdin for prompts, stdout for parsed events,
/// a state machine that tracks turn boundaries and tool-use correlation, and a stderr ring buffer.
///
/// Lifecycle:
/// <list type="number">
///   <item><see cref="StartAsync"/> spawns the process and begins consuming stdout.</item>
///   <item><see cref="SendUserMessageAsync"/> writes one stream-json <c>user</c> message to stdin.</item>
///   <item><see cref="InterruptAsync"/> requests mid-stream interrupt via the control protocol.</item>
///   <item><see cref="DisposeAsync"/> kills the child if alive and drains channels.</item>
/// </list>
/// </summary>
public interface IClaudeSession : IAsyncDisposable
{
    /// <summary>Populated after the first <c>system/init</c> event arrives. Null until then.</summary>
    Guid? SessionId { get; }

    /// <summary>The session-scoped state machine. Always reflects the latest event seen on stdout.</summary>
    SessionStateMachine State { get; }

    /// <summary>Parsed events as they arrive. Bounded; backpressure drops oldest queued events.</summary>
    ChannelReader<ClaudeEvent> Events { get; }

    /// <summary>Snapshot of the most-recent stderr lines. Useful for surfacing crash hints.</summary>
    IReadOnlyList<string> StderrTail { get; }

    /// <summary>Count of unrecognized top-level event discriminators or content-block types observed by the parser.</summary>
    int UnknownEventCount { get; }

    /// <summary>Count of stdout lines the parser could not consume (blank, non-object JSON, malformed JSON).</summary>
    int MalformedLineCount { get; }

    /// <summary>Histogram of unknown discriminators keyed by <c>type</c> or <c>content-block:type</c>.</summary>
    IReadOnlyDictionary<string, int> UnknownTypeHistogram { get; }

    /// <summary>
    /// Completes when the first-user-message (task pointer) delivery attempt finishes — success
    /// OR failure (M11). When no first message is configured, completes immediately. Never faults;
    /// inspect <see cref="FirstMessageError"/> for the failure. <see cref="SendUserMessageAsync"/>
    /// awaits this before writing so a caller's prompt can't race ahead of the pointer.
    ///
    /// <para>Default implementation returns a completed task — fakes that don't model a first
    /// message need not override it.</para>
    /// </summary>
    Task FirstMessageDelivery => Task.CompletedTask;

    /// <summary>Non-null when first-user-message delivery failed; the session then ran without the pointer.</summary>
    Exception? FirstMessageError => null;

    /// <summary>Spawn the process and start the reader/stderr loops. Idempotent: subsequent calls throw.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Write a stream-json <c>user</c> message to the child's stdin and transition the state machine
    /// to <c>Sending</c>. Caller must serialize calls; the implementation guards with a semaphore but
    /// concurrent senders will interleave turns in a way claude does not handle gracefully.
    /// </summary>
    Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mid-stream interrupt: writes a <c>control_request</c> with <c>subtype=interrupt</c> to
    /// the child's stdin and marks the state machine as <c>Cancelling</c>. The child responds
    /// with a <c>control_response</c> (parsed but otherwise informational) and a final
    /// <c>result</c> event that returns the state machine to <c>Idle</c>. Fire-and-forget;
    /// the returned task completes once the request line is flushed.
    /// </summary>
    Task InterruptAsync(CancellationToken cancellationToken = default);

    /// <summary>Wait for the child process to exit. Completes after stdout/stderr loops drain.</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken = default);
}
