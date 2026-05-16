using System.Text.Json.Nodes;

namespace InfiniContext.Core.Mcp;

/// <summary>
/// One pending tool-permission request from claude. Crosses the host ↔ sidecar pipe boundary as one
/// NDJSON line. <see cref="ToolName"/> is the claude tool being requested (e.g. "Bash"); <see cref="Input"/>
/// is the tool's full argument object as claude sees it.
/// </summary>
public sealed record ApprovalRequest(string ToolName, JsonObject Input)
{
    public JsonObject ToJson() => new()
    {
        ["tool_name"] = ToolName,
        ["input"] = Input.DeepClone(),
    };

    public static ApprovalRequest FromJson(JsonObject obj)
    {
        var name = obj["tool_name"]?.GetValue<string>() ?? throw new ArgumentException("missing tool_name");
        var input = obj["input"] as JsonObject ?? new JsonObject();
        return new ApprovalRequest(name, (JsonObject)input.DeepClone());
    }
}
