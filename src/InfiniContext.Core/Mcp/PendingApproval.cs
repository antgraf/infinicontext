namespace InfiniContext.Core.Mcp;

/// <summary>
/// Snapshot of one in-flight approval request owned by a <see cref="PendingApprovalSlot"/>.
/// Surfaced to the UI so it can render a banner with the request details and the deadline at which
/// the slot will auto-deny.
/// </summary>
public sealed record PendingApproval(ApprovalRequest Request, DateTimeOffset DeadlineUtc);
