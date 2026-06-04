using System.Text.Json.Nodes;
using InfiniContext.Core.Mcp.Tasks;

namespace InfiniContext.Core.Mcp.Sessions;

/// <summary>
/// Stage 3 bug A. Wire-format helpers for the <c>session.exit</c> message multiplexed onto the
/// existing approval pipe (the same pipe that carries approvals and <c>lock.*</c> kinds вЂ” see
/// <see cref="LockWireFormat"/>). The agent calls <c>mcp__infinicontext__session_exit</c> after a
/// context-pressure flush; the sidecar forwards a <see cref="SessionExitRequest"/>, the host closes
/// stdin, and the resulting <c>Exited</c> transition drives auto-rotation. Payload is flat (no nested
/// envelope) so it parses with the same shape on both sides.
/// </summary>
public sealed record SessionExitRequest(string? Reason)
{
    public JsonObject ToJson() => new()
    {
        [LockWireFormat.KindField] = LockWireFormat.SessionExitKind,
        ["reason"] = Reason,
    };

    public static SessionExitRequest FromJson(JsonObject obj) => new(
        Reason: obj["reason"]?.GetValue<string>());
}

/// <summary>Hostв†’sidecar response to <see cref="SessionExitRequest"/>. Always <c>ok</c> when a handler is wired.</summary>
public sealed record SessionExitResponse(bool Ok)
{
    public JsonObject ToJson() => new()
    {
        [LockWireFormat.KindField] = LockWireFormat.SessionExitKind,
        ["ok"] = Ok,
    };

    public static SessionExitResponse FromJson(JsonObject obj) => new(
        Ok: obj["ok"]?.GetValue<bool>() ?? false);
}
