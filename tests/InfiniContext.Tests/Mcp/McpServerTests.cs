using System.Text.Json.Nodes;
using InfiniContext.Core.Mcp;
using FluentAssertions;

namespace InfiniContext.Tests.Mcp;

/// <summary>
/// Drives <see cref="McpServer"/> over an in-memory pair of <see cref="StringReader"/>/<see cref="StringWriter"/>:
/// pump JSON-RPC requests, capture the responses, assert the shape claude expects.
/// </summary>
public class McpServerTests
{
    [Fact]
    public async Task Initialize_returns_server_info_and_echoes_protocol_version()
    {
        var (server, output) = await DriveAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            registerApprovalPrompt: false);

        var resp = ParseFirstResponse(output);
        resp["jsonrpc"]!.GetValue<string>().Should().Be("2.0");
        resp["id"]!.GetValue<int>().Should().Be(1);
        var result = resp["result"] as JsonObject;
        result.Should().NotBeNull();
        result!["protocolVersion"]!.GetValue<string>().Should().Be("2025-06-18");
        result["capabilities"]!["tools"].Should().NotBeNull();
        result["serverInfo"]!["name"]!.GetValue<string>().Should().Be("test-server");
    }

    [Fact]
    public async Task ToolsList_returns_registered_tools_with_schema()
    {
        var (_, output) = await DriveAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            registerApprovalPrompt: true);

        var result = ParseFirstResponse(output)["result"] as JsonObject;
        var tools = result!["tools"] as JsonArray;
        tools.Should().NotBeNull();
        tools!.Count.Should().Be(1);
        var tool = tools[0] as JsonObject;
        tool!["name"]!.GetValue<string>().Should().Be(ApprovalSidecarContract.ToolName);
        tool["inputSchema"]!["properties"]!["tool_name"].Should().NotBeNull();
    }

    [Fact]
    public async Task ToolsCall_dispatches_to_registered_handler_and_wraps_in_content_envelope()
    {
        var (_, output) = await DriveAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"approval_prompt","arguments":{"tool_name":"Bash","input":{"command":"ls"}}}}""",
            registerApprovalPrompt: true,
            handler: (_, _) => Task.FromResult(("{\"behavior\":\"allow\",\"updatedInput\":{}}", false)));

        var result = ParseFirstResponse(output)["result"] as JsonObject;
        result!["isError"]!.GetValue<bool>().Should().BeFalse();
        var content = result["content"] as JsonArray;
        content!.Count.Should().Be(1);
        var item = content[0] as JsonObject;
        item!["type"]!.GetValue<string>().Should().Be("text");
        // Critical: content[0].text is the JSON-stringified inner payload, not a structured object.
        var inner = JsonNode.Parse(item["text"]!.GetValue<string>()) as JsonObject;
        inner!["behavior"]!.GetValue<string>().Should().Be("allow");
    }

    [Fact]
    public async Task ToolsCall_unknown_tool_returns_error_envelope_not_jsonrpc_error()
    {
        var (_, output) = await DriveAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"nope","arguments":{}}}""",
            registerApprovalPrompt: true);

        var resp = ParseFirstResponse(output);
        resp["error"].Should().BeNull();
        var result = resp["result"] as JsonObject;
        result!["isError"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_method_returns_method_not_found_jsonrpc_error()
    {
        var (_, output) = await DriveAsync(
            """{"jsonrpc":"2.0","id":5,"method":"resources/list"}""",
            registerApprovalPrompt: false);

        var resp = ParseFirstResponse(output);
        var err = resp["error"] as JsonObject;
        err.Should().NotBeNull();
        err!["code"]!.GetValue<int>().Should().Be(-32601);
    }

    [Fact]
    public async Task Notifications_initialized_does_not_emit_response()
    {
        var (_, output) = await DriveAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            registerApprovalPrompt: false);

        output.Trim().Should().BeEmpty();
    }

    [Fact]
    public async Task Ping_returns_empty_result()
    {
        var (_, output) = await DriveAsync(
            """{"jsonrpc":"2.0","id":7,"method":"ping"}""",
            registerApprovalPrompt: false);

        var resp = ParseFirstResponse(output);
        resp["result"].Should().NotBeNull();
        resp["error"].Should().BeNull();
    }

    [Fact]
    public async Task Malformed_json_line_does_not_crash_server()
    {
        // A bad line followed by a good one. The server should log+skip the bad and respond to the good.
        var (_, output) = await DriveAsync(
            "this is not json\n" +
            """{"jsonrpc":"2.0","id":9,"method":"ping"}""",
            registerApprovalPrompt: false);

        var resp = ParseFirstResponse(output);
        resp["id"]!.GetValue<int>().Should().Be(9);
    }

    private static async Task<(McpServer Server, string Output)> DriveAsync(
        string inputLines,
        bool registerApprovalPrompt,
        McpServer.ToolHandler? handler = null)
    {
        var input = new StringReader(inputLines + "\n");
        var output = new StringWriter();
        var server = new McpServer(input, output, "test-server", "0.0.1");

        if (registerApprovalPrompt)
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["tool_name"] = new JsonObject { ["type"] = "string" },
                    ["input"] = new JsonObject { ["type"] = "object" },
                },
                ["required"] = new JsonArray { "tool_name", "input" },
            };
            server.RegisterTool(
                ApprovalSidecarContract.ToolName,
                "test",
                schema,
                handler ?? ((_, _) => Task.FromResult<(string, bool)>(("", false))));
        }

        // RunAsync drains until EOF and awaits all in-flight dispatches before returning.
        await server.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        return (server, output.ToString());
    }

    private static JsonObject ParseFirstResponse(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                   ?? throw new Xunit.Sdk.XunitException("No response written");
        return (JsonNode.Parse(line) as JsonObject)
               ?? throw new Xunit.Sdk.XunitException("Response was not a JSON object");
    }
}
