using System.Text.Json.Nodes;

namespace InfiniContext.Core.Mcp;

/// <summary>
/// Host's answer to one approval request. Either approves (with optional <see cref="UpdatedInput"/>
/// rewrite) or denies (with a reason in <see cref="Message"/>).
/// </summary>
public sealed record ApprovalResponse
{
    public required ApprovalBehavior Behavior { get; init; }
    public JsonObject? UpdatedInput { get; init; }
    public string? Message { get; init; }

    public static ApprovalResponse Allow(JsonObject? updatedInput = null) =>
        new() { Behavior = ApprovalBehavior.Allow, UpdatedInput = updatedInput };

    public static ApprovalResponse Deny(string message) =>
        new() { Behavior = ApprovalBehavior.Deny, Message = message };

    /// <summary>
    /// Serializes the response payload. Used both for IPC across the host ↔ sidecar pipe AND as the
    /// inner JSON-stringified payload that goes into MCP's <c>content[0].text</c> — claude expects
    /// the response to be double-encoded (an MCP envelope wrapping a string that is itself a JSON
    /// document). See research notes §2.
    /// </summary>
    public JsonObject ToWireJson() => Behavior switch
    {
        ApprovalBehavior.Allow => new JsonObject
        {
            ["behavior"] = "allow",
            ["updatedInput"] = (UpdatedInput ?? new JsonObject()).DeepClone(),
        },
        ApprovalBehavior.Deny => new JsonObject
        {
            ["behavior"] = "deny",
            ["message"] = Message ?? "denied",
        },
        _ => throw new InvalidOperationException($"unknown behavior {Behavior}"),
    };

    public static ApprovalResponse FromWireJson(JsonObject obj)
    {
        var behavior = obj["behavior"]?.GetValue<string>();
        return behavior switch
        {
            "allow" => Allow(obj["updatedInput"] as JsonObject is { } u ? (JsonObject)u.DeepClone() : null),
            "deny" => Deny(obj["message"]?.GetValue<string>() ?? "denied"),
            _ => throw new ArgumentException($"unknown behavior '{behavior}'"),
        };
    }
}

public enum ApprovalBehavior
{
    Allow,
    Deny,
}
