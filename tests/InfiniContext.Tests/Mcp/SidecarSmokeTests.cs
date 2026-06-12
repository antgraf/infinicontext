using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Mcp.Sessions;
using InfiniContext.Core.Mcp.Tasks;
using InfiniContext.Tests.Cli;
using FluentAssertions;

namespace InfiniContext.Tests.Mcp;

/// <summary>
/// Spawns the real CLI binary in <c>--mcp-approval-server &lt;pipe&gt;</c> mode and drives it via
/// stdin/stdout (claude's view) while a host <see cref="ApprovalChannel"/> answers on the pipe side.
/// Validates the full sidecar wire-up — claude's expected handshake, tools/list, tools/call double-encoded
/// response — without needing a real claude binary.
/// </summary>
public class SidecarSmokeTests
{
    [Fact]
    public async Task Sidecar_handshake_then_tools_list_then_approval_call()
    {
        var pipeName = ApprovalChannel.GeneratePipeName();
        var sink = new RecordingSink(req => ApprovalResponse.Allow(req.Input));
        await using var channel = new ApprovalChannel(pipeName, sink);
        await channel.StartAsync();

        var cliPath = CliRunner.ExecutablePath;
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            ArgumentList = { "--mcp-approval-server", pipeName },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        using var proc = Process.Start(psi)!;
        try
        {
            await proc.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
            await proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            await proc.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"approval_prompt","arguments":{"tool_name":"Bash","input":{"command":"echo hi"}}}}""");
            await proc.StandardInput.FlushAsync(TestContext.Current.CancellationToken);

            // McpServer dispatches fire-and-forget so replies can arrive on stdout in any order.
            // Read all three, then look up by JSON-RPC id.
            var responses = new Dictionary<int, JsonObject>();
            for (var i = 0; i < 3; i++)
            {
                var line = await ReadLineWithTimeoutAsync(proc.StandardOutput, TimeSpan.FromSeconds(5));
                var obj = JsonNode.Parse(line!) as JsonObject
                    ?? throw new InvalidOperationException($"sidecar emitted non-object line: {line}");
                var id = obj["id"]?.GetValue<int>()
                    ?? throw new InvalidOperationException($"sidecar response missing id: {line}");
                responses[id] = obj;
            }

            var initResp = responses[1];
            initResp["result"]!["serverInfo"]!["name"]!.GetValue<string>().Should().Be(ApprovalSidecarContract.ServerName);

            var listResp = responses[2];
            var tools = listResp["result"]!["tools"] as JsonArray;
            // Approval + Phase 7 lock acquire/release + Stage 3 session_exit.
            tools!.Count.Should().Be(4);
            var toolNames = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
            toolNames.Should().BeEquivalentTo(new[]
            {
                ApprovalSidecarContract.ToolName,
                ApprovalSidecarContract.LockAcquireToolName,
                ApprovalSidecarContract.LockReleaseToolName,
                ApprovalSidecarContract.SessionExitToolName,
            });

            var callResp = responses[3];
            callResp["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();
            var text = callResp["result"]!["content"]![0]!["text"]!.GetValue<string>();
            var inner = JsonNode.Parse(text) as JsonObject;
            inner!["behavior"]!.GetValue<string>().Should().Be("allow");

            sink.Calls.Should().Be(1);
            sink.LastRequest!.ToolName.Should().Be("Bash");
        }
        finally
        {
            try { proc.StandardInput.Close(); } catch { }
            try { if (!proc.WaitForExit(2000)) proc.Kill(true); } catch { }
        }
    }

    [Fact]
    public async Task Sidecar_lock_acquire_and_release_round_trip_through_spawned_process()
    {
        // Phase 7: drive the real spawned sidecar through tools/call for both lock tools and verify
        // the host's LockRegistry sees the correct ownership transitions.
        var pipeName = ApprovalChannel.GeneratePipeName();
        var registry = new LockRegistry(pollInterval: TimeSpan.FromMilliseconds(5));
        await using var channel = new ApprovalChannel(
            pipeName, new RecordingSink(req => ApprovalResponse.Allow(req.Input)),
            lockRegistry: registry,
            lockTtl: TimeSpan.FromSeconds(30),
            lockMaxWait: TimeSpan.FromSeconds(1));
        await channel.StartAsync();

        var psi = new ProcessStartInfo
        {
            FileName = CliRunner.ExecutablePath,
            ArgumentList = { "--mcp-approval-server", pipeName },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        using var proc = Process.Start(psi)!;
        try
        {
            await proc.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
            await proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await proc.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"task_acquire_lock","arguments":{"file":".task/0001-foo/progress.md"}}}""");
            await proc.StandardInput.FlushAsync(TestContext.Current.CancellationToken);

            var responses = new Dictionary<int, JsonObject>();
            for (var i = 0; i < 2; i++)
            {
                var line = await ReadLineWithTimeoutAsync(proc.StandardOutput, TimeSpan.FromSeconds(5));
                var obj = JsonNode.Parse(line!) as JsonObject ?? throw new InvalidOperationException($"non-object line: {line}");
                var id = obj["id"]?.GetValue<int>() ?? throw new InvalidOperationException($"missing id: {line}");
                responses[id] = obj;
            }

            var acquireResp = responses[2];
            acquireResp["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();
            var acquireText = acquireResp["result"]!["content"]![0]!["text"]!.GetValue<string>();
            var acquireInner = JsonNode.Parse(acquireText) as JsonObject;
            acquireInner.Should().NotBeNull();
            var token = acquireInner!["token"]!.GetValue<string>();
            token.Should().StartWith("lt_");
            acquireInner["ttl_seconds"]!.GetValue<int>().Should().Be(30);

            registry.Snapshot().Should().ContainSingle()
                .Which.OwnerId.Should().Be(pipeName, "the sidecar uses its pipe name as the owner id");

            // Now release.
            var releaseRequest = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"task_release_lock\",\"arguments\":{\"token\":\"" + token + "\"}}}";
            await proc.StandardInput.WriteLineAsync(releaseRequest);
            await proc.StandardInput.FlushAsync(TestContext.Current.CancellationToken);

            var releaseLine = await ReadLineWithTimeoutAsync(proc.StandardOutput, TimeSpan.FromSeconds(5));
            var releaseResp = JsonNode.Parse(releaseLine!) as JsonObject;
            releaseResp!["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();
            var releaseText = releaseResp["result"]!["content"]![0]!["text"]!.GetValue<string>();
            (JsonNode.Parse(releaseText) as JsonObject)!["ok"]!.GetValue<bool>().Should().BeTrue();

            registry.Snapshot().Should().BeEmpty();
        }
        finally
        {
            try { proc.StandardInput.Close(); } catch { }
            try { if (!proc.WaitForExit(2000)) proc.Kill(true); } catch { }
        }
    }

    [Fact]
    public async Task Sidecar_session_exit_forwards_to_channel_callback_and_returns_ok()
    {
        // Stage 3 bug A: drive the real spawned sidecar through tools/call for session_exit and
        // verify the host channel's onSessionExit callback fires with the agent's reason and the
        // tool result is { ok: true }. This is the closest achievable end-to-end of the agent path
        // (real sidecar process + real pipe + real channel); FakeClaude can't speak MCP so it can't
        // originate the tools/call itself.
        var pipeName = ApprovalChannel.GeneratePipeName();
        var fired = new TaskCompletionSource<SessionExitRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var channel = new ApprovalChannel(
            pipeName, new RecordingSink(req => ApprovalResponse.Allow(req.Input)),
            onSessionExit: (req, _) => { fired.TrySetResult(req); return Task.CompletedTask; });
        await channel.StartAsync();

        var psi = new ProcessStartInfo
        {
            FileName = CliRunner.ExecutablePath,
            ArgumentList = { "--mcp-approval-server", pipeName },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        using var proc = Process.Start(psi)!;
        try
        {
            await proc.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
            await proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await proc.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"session_exit","arguments":{"reason":"flushed after hard nudge"}}}""");
            await proc.StandardInput.FlushAsync(TestContext.Current.CancellationToken);

            var responses = new Dictionary<int, JsonObject>();
            for (var i = 0; i < 2; i++)
            {
                var line = await ReadLineWithTimeoutAsync(proc.StandardOutput, TimeSpan.FromSeconds(5));
                var obj = JsonNode.Parse(line!) as JsonObject ?? throw new InvalidOperationException($"non-object line: {line}");
                var id = obj["id"]?.GetValue<int>() ?? throw new InvalidOperationException($"missing id: {line}");
                responses[id] = obj;
            }

            var exitResp = responses[2];
            exitResp["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();
            var text = exitResp["result"]!["content"]![0]!["text"]!.GetValue<string>();
            (JsonNode.Parse(text) as JsonObject)!["ok"]!.GetValue<bool>().Should().BeTrue();

            var observed = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            observed.Reason.Should().Be("flushed after hard nudge");
        }
        finally
        {
            try { proc.StandardInput.Close(); } catch { }
            try { if (!proc.WaitForExit(2000)) proc.Kill(true); } catch { }
        }
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, TimeSpan timeout)
    {
        var task = reader.ReadLineAsync();
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        if (done != task) throw new TimeoutException($"sidecar did not produce a line within {timeout}");
        return await task;
    }

    private sealed class RecordingSink : IApprovalSink
    {
        private readonly Func<ApprovalRequest, ApprovalResponse> _handler;
        private int _calls;
        public ApprovalRequest? LastRequest { get; private set; }
        public int Calls => Volatile.Read(ref _calls);

        public RecordingSink(Func<ApprovalRequest, ApprovalResponse> handler) => _handler = handler;

        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            LastRequest = request;
            return Task.FromResult(_handler(request));
        }
    }
}
