using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace InfiniContext.Core.Mcp;

/// <summary>
/// Minimal MCP stdio server. Speaks NDJSON JSON-RPC 2.0 on a <see cref="TextReader"/>/<see cref="TextWriter"/>
/// pair. Implements just enough of the MCP lifecycle for Claude Code's <c>--permission-prompt-tool</c>:
/// <c>initialize</c>, <c>notifications/initialized</c>, <c>tools/list</c>, <c>tools/call</c>, <c>ping</c>.
/// Tools are registered by name with an input schema and a handler that returns the unwrapped tool result;
/// the server wraps it in the standard <c>{ content: [...], isError }</c> envelope for the client.
///
/// One instance corresponds to one connected client. Stdout MUST be reserved for protocol output —
/// any logging goes to stderr or an out-of-band sink so claude's parser doesn't choke on log noise.
/// </summary>
public sealed class McpServer
{
    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        // Compact, single-line — required for NDJSON framing.
    };

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly ILogger _logger;
    private readonly string _serverName;
    private readonly string _serverVersion;
    private readonly Dictionary<string, ToolRegistration> _tools = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public McpServer(
        TextReader input,
        TextWriter output,
        string serverName,
        string serverVersion,
        ILogger? logger = null)
    {
        _input = input;
        _output = output;
        _serverName = serverName;
        _serverVersion = serverVersion;
        _logger = logger ?? Log.Logger;
    }

    public void RegisterTool(string name, string description, JsonNode inputSchema, ToolHandler handler)
    {
        _tools[name] = new ToolRegistration(name, description, inputSchema, handler);
    }

    /// <summary>
    /// Drives the request loop. Returns when stdin closes or <paramref name="cancellationToken"/> trips.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _logger.Debug("mcp: server starting name={Name} version={Version}", _serverName, _serverVersion);
        var inflight = new List<Task>();
        try
        {
            string? line;
            while ((line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0) continue;

                JsonNode? msg;
                try
                {
                    msg = JsonNode.Parse(line);
                }
                catch (JsonException ex)
                {
                    _logger.Warning("mcp: malformed json on stdin: {Reason}", ex.Message);
                    continue;
                }

                if (msg is not JsonObject obj)
                {
                    _logger.Warning("mcp: non-object json-rpc message ignored");
                    continue;
                }

                // Fire-and-forget so a slow tool handler (e.g. blocking on user approval) doesn't stall
                // unrelated requests. We track tasks so RunAsync awaits in-flight dispatches on shutdown.
                var task = Task.Run(() => DispatchAsync(obj, cancellationToken), CancellationToken.None);
                lock (inflight)
                {
                    inflight.Add(task);
                    inflight.RemoveAll(static t => t.IsCompleted);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (IOException) { /* stdin closed under us */ }
        finally
        {
            Task[] snapshot;
            lock (inflight) snapshot = inflight.ToArray();
            try { await Task.WhenAll(snapshot).ConfigureAwait(false); } catch { /* logged inside dispatch */ }
            _logger.Debug("mcp: server stopping");
        }
    }

    private async Task DispatchAsync(JsonObject obj, CancellationToken ct)
    {
        var id = obj["id"];
        var method = obj["method"]?.GetValue<string>();
        var isNotification = id is null;

        if (method is null)
        {
            // Likely a response to a server-initiated request — we don't issue any.
            return;
        }

        try
        {
            switch (method)
            {
                case "initialize":
                    await ReplyAsync(id, BuildInitializeResult(obj["params"] as JsonObject)).ConfigureAwait(false);
                    break;

                case "notifications/initialized":
                case "notifications/cancelled":
                    // Notifications: no reply.
                    break;

                case "ping":
                    if (!isNotification) await ReplyAsync(id, new JsonObject()).ConfigureAwait(false);
                    break;

                case "tools/list":
                    if (!isNotification) await ReplyAsync(id, BuildToolsListResult()).ConfigureAwait(false);
                    break;

                case "tools/call":
                    if (!isNotification)
                    {
                        var result = await HandleToolCallAsync(obj["params"] as JsonObject, ct).ConfigureAwait(false);
                        await ReplyAsync(id, result).ConfigureAwait(false);
                    }
                    break;

                default:
                    if (!isNotification)
                        await ReplyErrorAsync(id, -32601, $"Method not found: {method}").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "mcp: handler threw for method {Method}", method);
            if (!isNotification)
            {
                try { await ReplyErrorAsync(id, -32603, ex.Message).ConfigureAwait(false); }
                catch { /* output may be closed */ }
            }
        }
    }

    private JsonObject BuildInitializeResult(JsonObject? @params)
    {
        // Echo client's protocolVersion when present; default to the current spec.
        var clientProtocol = @params?["protocolVersion"]?.GetValue<string>();
        var protocol = string.IsNullOrEmpty(clientProtocol) ? "2025-06-18" : clientProtocol;

        return new JsonObject
        {
            ["protocolVersion"] = protocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = _serverName,
                ["version"] = _serverVersion,
            },
        };
    }

    private JsonObject BuildToolsListResult()
    {
        var arr = new JsonArray();
        foreach (var t in _tools.Values)
        {
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema.DeepClone(),
            });
        }
        return new JsonObject { ["tools"] = arr };
    }

    private async Task<JsonObject> HandleToolCallAsync(JsonObject? @params, CancellationToken ct)
    {
        var name = @params?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name) || !_tools.TryGetValue(name, out var tool))
            return ToolErrorResult($"Unknown tool: {name ?? "<missing>"}");

        var arguments = (@params?["arguments"] as JsonObject) ?? new JsonObject();

        try
        {
            var (text, isError) = await tool.Handler(arguments, ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
                ["isError"] = isError,
            };
        }
        catch (Exception ex)
        {
            return ToolErrorResult(ex.Message);
        }
    }

    private static JsonObject ToolErrorResult(string message) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = message } },
        ["isError"] = true,
    };

    private async Task ReplyAsync(JsonNode? id, JsonNode result)
    {
        var resp = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };
        await WriteLineAsync(resp).ConfigureAwait(false);
    }

    private async Task ReplyErrorAsync(JsonNode? id, int code, string message)
    {
        var resp = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        await WriteLineAsync(resp).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(JsonObject obj)
    {
        var line = obj.ToJsonString(s_writeOptions);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _output.WriteLineAsync(line).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Tool handler signature. <paramref name="arguments"/> is the JSON-RPC <c>params.arguments</c> object.
    /// Returns <c>(text, isError)</c>; the server wraps this in <c>content[0]</c>.
    /// </summary>
    public delegate Task<(string Text, bool IsError)> ToolHandler(JsonObject arguments, CancellationToken ct);

    private sealed record ToolRegistration(string Name, string Description, JsonNode InputSchema, ToolHandler Handler);
}
