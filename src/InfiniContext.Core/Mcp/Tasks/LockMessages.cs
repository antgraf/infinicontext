using System.Text.Json.Nodes;

namespace InfiniContext.Core.Mcp.Tasks;

/// <summary>
/// Stage 2A Phase 7. Wire-format helpers for the lock messages multiplexed onto the existing
/// approval pipe. Each line on the pipe carries a <c>kind</c> discriminator; lock messages use
/// <see cref="AcquireKind"/> and <see cref="ReleaseKind"/>. Payload fields are flat (no nested
/// envelope) so they parse with the same shape on both sides.
/// </summary>
public static class LockWireFormat
{
    public const string KindField = "kind";
    public const string AcquireKind = "lock.acquire";
    public const string ReleaseKind = "lock.release";

    /// <summary>Approvals didn't carry a kind in v1; treat absence as approval for back-compat.</summary>
    public const string ApprovalKind = "approval";
}

/// <summary>Sidecar→host: please grant a lock on <see cref="FilePath"/> for <see cref="OwnerId"/>.</summary>
public sealed record LockAcquireRequest(string FilePath, string OwnerId)
{
    public JsonObject ToJson() => new()
    {
        [LockWireFormat.KindField] = LockWireFormat.AcquireKind,
        ["file"] = FilePath,
        ["owner"] = OwnerId,
    };

    public static LockAcquireRequest FromJson(JsonObject obj) => new(
        FilePath: obj["file"]?.GetValue<string>() ?? throw new ArgumentException("missing file"),
        OwnerId: obj["owner"]?.GetValue<string>() ?? throw new ArgumentException("missing owner"));
}

/// <summary>Host→sidecar response to <see cref="LockAcquireRequest"/>.</summary>
public sealed record LockAcquireResponse(bool Ok, string? Token, int TtlSeconds, string? Error)
{
    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            [LockWireFormat.KindField] = LockWireFormat.AcquireKind,
            ["ok"] = Ok,
        };
        if (Ok)
        {
            obj["token"] = Token;
            obj["ttl_seconds"] = TtlSeconds;
        }
        else
        {
            obj["error"] = Error ?? "unknown error";
        }
        return obj;
    }

    public static LockAcquireResponse FromJson(JsonObject obj) => new(
        Ok: obj["ok"]?.GetValue<bool>() ?? false,
        Token: obj["token"]?.GetValue<string>(),
        TtlSeconds: obj["ttl_seconds"]?.GetValue<int>() ?? 0,
        Error: obj["error"]?.GetValue<string>());

    public static LockAcquireResponse Granted(string token, int ttlSeconds) =>
        new(true, token, ttlSeconds, null);

    public static LockAcquireResponse Failed(string error) =>
        new(false, null, 0, error);
}

/// <summary>Sidecar→host: release the lock identified by <see cref="Token"/>.</summary>
public sealed record LockReleaseRequest(string Token)
{
    public JsonObject ToJson() => new()
    {
        [LockWireFormat.KindField] = LockWireFormat.ReleaseKind,
        ["token"] = Token,
    };

    public static LockReleaseRequest FromJson(JsonObject obj) => new(
        Token: obj["token"]?.GetValue<string>() ?? throw new ArgumentException("missing token"));
}

/// <summary>Host→sidecar response to <see cref="LockReleaseRequest"/>.</summary>
public sealed record LockReleaseResponse(bool Ok)
{
    public JsonObject ToJson() => new()
    {
        [LockWireFormat.KindField] = LockWireFormat.ReleaseKind,
        ["ok"] = Ok,
    };

    public static LockReleaseResponse FromJson(JsonObject obj) => new(
        Ok: obj["ok"]?.GetValue<bool>() ?? false);
}
