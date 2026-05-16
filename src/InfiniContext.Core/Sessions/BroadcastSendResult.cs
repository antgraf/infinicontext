namespace InfiniContext.Core.Sessions;

/// <summary>
/// Stage 2B Phase 6. Outcome of a single per-target send during
/// <see cref="SessionManager.BroadcastAsync"/>. The fan-out always returns one entry per id the
/// caller supplied, in input order, so a UI can render a per-row toast (sent / failed / unknown).
/// </summary>
public sealed record BroadcastSendResult
{
    /// <summary>Host session id the result refers to (the id the caller asked for, even when the
    /// session was already gone).</summary>
    public required Guid HostSessionId { get; init; }

    /// <summary>Outcome category. <see cref="BroadcastSendOutcome.NotFound"/> means the id wasn't
    /// in the active registry at fan-out time (closed already, or never created).</summary>
    public required BroadcastSendOutcome Outcome { get; init; }

    /// <summary>Exception raised by <c>SendUserMessageAsync</c> for this target, when
    /// <see cref="Outcome"/> is <see cref="BroadcastSendOutcome.Failed"/>. Null otherwise.</summary>
    public Exception? Error { get; init; }

    public bool IsSent => Outcome == BroadcastSendOutcome.Sent;

    public static BroadcastSendResult Sent(Guid id) => new()
    {
        HostSessionId = id,
        Outcome = BroadcastSendOutcome.Sent,
    };

    public static BroadcastSendResult Failed(Guid id, Exception ex) => new()
    {
        HostSessionId = id,
        Outcome = BroadcastSendOutcome.Failed,
        Error = ex,
    };

    public static BroadcastSendResult NotFound(Guid id) => new()
    {
        HostSessionId = id,
        Outcome = BroadcastSendOutcome.NotFound,
    };
}

public enum BroadcastSendOutcome
{
    Sent,
    Failed,
    NotFound,
}
