using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using InfiniContext.Core.Mcp.Sessions;
using InfiniContext.Core.Mcp.Tasks;
using Serilog;

namespace InfiniContext.Core.Mcp;

/// <summary>
/// Entry point for the approval sidecar - the in-process MCP server that claude spawns when given
/// <c>--permission-prompt-tool mcp__infinicontext__approval_prompt</c> with our generated <c>--mcp-config</c>.
///
/// The sidecar bridges JSON-RPC over its stdin/stdout (claude's view) to a named pipe back to the host
/// (where the human actually answers approve/deny). One sidecar process per claude session.
///
/// Promoted from <c>InfiniContext.Cli.Mcp</c> to Core in Phase 3B so both the CLI host and the
/// Avalonia GUI head can share the same sidecar entry point - the host process re-spawns itself
/// with <c>--mcp-approval-server &lt;pipe&gt;</c> regardless of which head is running.
/// </summary>
public static class ApprovalSidecarHost
{
    /// <summary>
    /// Runs the sidecar. Connects to the host pipe up-front; if that fails the sidecar exits before
    /// claude finishes <c>initialize</c>, which surfaces as an MCP-server error in claude's output.
    /// </summary>
    public static async Task<int> RunAsync(string pipeName, ILogger? logger = null, CancellationToken ct = default, int connectTimeoutSeconds = 10)
    {
        logger ??= Log.Logger;
        logger.Information("sidecar: starting pipe={PipeName} pid={Pid}", pipeName, Environment.ProcessId);

        NamedPipeClientStream pipe;
        try
        {
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(connectTimeoutSeconds), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.Information("sidecar: connect cancelled before establishing pipe");
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "sidecar: failed to connect to host pipe");
            return 1;
        }

        await using var pipeStream = pipe;
        using var pipeReader = new StreamReader(pipeStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        await using var pipeWriter = new StreamWriter(pipeStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: -1, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        var pipeLock = new SemaphoreSlim(1, 1);

        var stdin = Console.In;
        var stdout = Console.Out;

        var server = new McpServer(stdin, stdout, ApprovalSidecarContract.ServerName, ApprovalSidecarContract.ServerVersion, logger);

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

        // The pipe-name owner id is what the host's LockRegistry uses to scope per-session locks.
        var ownerId = pipeName;

        server.RegisterTool(
            ApprovalSidecarContract.ToolName,
            "Prompts the InfiniContext host to approve or deny a tool invocation.",
            schema,
            async (arguments, innerCt) =>
            {
                var request = ApprovalRequest.FromJson(arguments);
                logger.Debug("sidecar: forwarding approval request tool={Tool}", request.ToolName);

                var requestJson = request.ToJson();
                requestJson[LockWireFormat.KindField] = LockWireFormat.ApprovalKind;
                var responseObj = await ExchangeAsync(pipeWriter, pipeReader, pipeLock, requestJson, innerCt).ConfigureAwait(false);

                ApprovalResponse response;
                if (responseObj is null) response = ApprovalResponse.Deny("host disconnected");
                else
                {
                    try { response = ApprovalResponse.FromWireJson(responseObj); }
                    catch (Exception ex)
                    {
                        logger.Warning(ex, "sidecar: malformed approval response");
                        response = ApprovalResponse.Deny("malformed host response");
                    }
                }

                logger.Debug("sidecar: returning behavior={Behavior}", response.Behavior);
                // Double-encoded: claude reads the JSON-stringified inner payload from content[0].text.
                return (response.ToWireJson().ToJsonString(), false);
            });

        var acquireSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["file"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Path to the artifact under the task folder, e.g. '.task/0001-foo/progress.md'.",
                },
            },
            ["required"] = new JsonArray { "file" },
        };

        server.RegisterTool(
            ApprovalSidecarContract.LockAcquireToolName,
            "Acquire a single-mutator lock on a InfiniContext task artifact. Call before Read/Edit/Write to coordinate parallel sessions on the same task. Returns { token, ttl_seconds } on success or { error } on timeout.",
            acquireSchema,
            async (arguments, innerCt) =>
            {
                var file = arguments["file"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(file))
                    return ("{\"error\":\"file is required\"}", true);

                var req = new LockAcquireRequest(file, ownerId);
                logger.Debug("sidecar: forwarding lock.acquire file={File}", file);
                var responseObj = await ExchangeAsync(pipeWriter, pipeReader, pipeLock, req.ToJson(), innerCt).ConfigureAwait(false);
                if (responseObj is null)
                    return ("{\"error\":\"host disconnected\"}", true);

                LockAcquireResponse response;
                try { response = LockAcquireResponse.FromJson(responseObj); }
                catch (Exception ex)
                {
                    logger.Warning(ex, "sidecar: malformed lock.acquire response");
                    return ("{\"error\":\"malformed host response\"}", true);
                }

                if (response.Ok)
                {
                    var success = new JsonObject
                    {
                        ["token"] = response.Token,
                        ["ttl_seconds"] = response.TtlSeconds,
                    };
                    return (success.ToJsonString(), false);
                }
                else
                {
                    var failure = new JsonObject { ["error"] = response.Error ?? "unknown error" };
                    return (failure.ToJsonString(), true);
                }
            });

        var releaseSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["token"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The token returned from a prior task_acquire_lock call.",
                },
            },
            ["required"] = new JsonArray { "token" },
        };

        server.RegisterTool(
            ApprovalSidecarContract.LockReleaseToolName,
            "Release a InfiniContext task lock previously acquired via task_acquire_lock. Returns { ok: true|false }.",
            releaseSchema,
            async (arguments, innerCt) =>
            {
                var token = arguments["token"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(token))
                    return ("{\"ok\":false,\"error\":\"token is required\"}", true);

                var req = new LockReleaseRequest(token);
                logger.Debug("sidecar: forwarding lock.release");
                var responseObj = await ExchangeAsync(pipeWriter, pipeReader, pipeLock, req.ToJson(), innerCt).ConfigureAwait(false);
                if (responseObj is null)
                    return ("{\"ok\":false,\"error\":\"host disconnected\"}", true);

                LockReleaseResponse response;
                try { response = LockReleaseResponse.FromJson(responseObj); }
                catch (Exception ex)
                {
                    logger.Warning(ex, "sidecar: malformed lock.release response");
                    return ("{\"ok\":false,\"error\":\"malformed host response\"}", true);
                }
                return ($"{{\"ok\":{(response.Ok ? "true" : "false")}}}", false);
            });

        var exitSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["reason"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Free-form description of why the session is exiting "
                        + "(e.g., 'flushed after hard nudge', 'task complete').",
                },
            },
        };

        server.RegisterTool(
            ApprovalSidecarContract.SessionExitToolName,
            "Request a graceful exit of this Claude session. The host closes stdin; the current turn "
            + "completes, then the process exits. Auto-rotation, if armed by a context-pressure nudge, "
            + "fires from the resulting Exited transition. Returns { ok: true }.",
            exitSchema,
            async (arguments, innerCt) =>
            {
                var reason = arguments["reason"]?.GetValue<string>();
                var req = new SessionExitRequest(reason);
                logger.Information("sidecar: forwarding session.exit reason={Reason}", reason ?? "<none>");
                var responseObj = await ExchangeAsync(pipeWriter, pipeReader, pipeLock, req.ToJson(), innerCt).ConfigureAwait(false);
                if (responseObj is null)
                    return ("{\"ok\":false,\"error\":\"host disconnected\"}", true);

                SessionExitResponse response;
                try { response = SessionExitResponse.FromJson(responseObj); }
                catch (Exception ex)
                {
                    logger.Warning(ex, "sidecar: malformed session.exit response");
                    return ("{\"ok\":false,\"error\":\"malformed host response\"}", true);
                }
                return ($"{{\"ok\":{(response.Ok ? "true" : "false")}}}", response.Ok ? false : true);
            });

        await server.RunAsync(ct).ConfigureAwait(false);
        logger.Information("sidecar: exiting");
        return 0;
    }

    /// <summary>
    /// One round-trip on the host pipe: serialize <paramref name="request"/>, write it under the
    /// pipe lock, read one response line, parse it as JSON. Returns null on host disconnect or
    /// malformed JSON. Errors are logged at the call site so each tool can map to its own response
    /// shape.
    /// </summary>
    private static async Task<JsonObject?> ExchangeAsync(
        StreamWriter pipeWriter,
        StreamReader pipeReader,
        SemaphoreSlim pipeLock,
        JsonObject request,
        CancellationToken ct)
    {
        await pipeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await pipeWriter.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
            var line = await pipeReader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return null;
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            pipeLock.Release();
        }
    }
}
