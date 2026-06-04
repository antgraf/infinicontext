using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Mcp.Sessions;
using InfiniContext.Core.Mcp.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Mcp.Sessions;

/// <summary>
/// Stage 3 bug A. Drives an <see cref="ApprovalChannel"/> with a raw named-pipe client (the
/// bytes-on-wire shape the real sidecar produces) and exercises the <c>session.exit</c> branch:
/// the callback fires with the agent's reason, the channel replies <c>{ ok: true }</c>, and a
/// channel wired with no handler refuses with <c>{ ok: false }</c> for back-compat.
/// </summary>
public class SessionExitChannelTests
{
    [Fact]
    public async Task Session_exit_fires_callback_and_replies_ok()
    {
        var fired = new TaskCompletionSource<SessionExitRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new DenyAllSink();
        var pipeName = ApprovalChannel.GeneratePipeName();
        await using var channel = new ApprovalChannel(
            pipeName, sink,
            onSessionExit: (req, _) => { fired.TrySetResult(req); return Task.CompletedTask; });
        await channel.StartAsync();

        await using var pipe = await ConnectAsync(pipeName);
        var (reader, writer) = Open(pipe);

        await writer.WriteLineAsync(new SessionExitRequest("flushed after hard nudge").ToJson().ToJsonString());
        var line = await reader.ReadLineAsync() ?? throw new InvalidOperationException("no session.exit response");
        var resp = SessionExitResponse.FromJson((JsonObject)JsonNode.Parse(line)!);

        resp.Ok.Should().BeTrue();
        ((JsonObject)JsonNode.Parse(line)!)[LockWireFormat.KindField]!.GetValue<string>()
            .Should().Be(LockWireFormat.SessionExitKind);

        // The handler is invoked fire-and-forget on the threadpool, so wait for it explicitly.
        var observed = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Reason.Should().Be("flushed after hard nudge");
        sink.Calls.Should().Be(0, "session.exit must not reach the approval sink");
    }

    [Fact]
    public async Task Session_exit_without_handler_replies_not_ok()
    {
        // A newer skill emitting session.exit against a host that wired no callback (older host, or
        // a session opened without the graceful-exit path) must refuse cleanly, not hang or crash.
        var pipeName = ApprovalChannel.GeneratePipeName();
        await using var channel = new ApprovalChannel(pipeName, new DenyAllSink());
        await channel.StartAsync();

        await using var pipe = await ConnectAsync(pipeName);
        var (reader, writer) = Open(pipe);

        await writer.WriteLineAsync(new SessionExitRequest(null).ToJson().ToJsonString());
        var line = await reader.ReadLineAsync() ?? throw new InvalidOperationException("no session.exit response");

        SessionExitResponse.FromJson((JsonObject)JsonNode.Parse(line)!).Ok.Should().BeFalse();
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5_000);
        return pipe;
    }

    private static (StreamReader reader, StreamWriter writer) Open(NamedPipeClientStream pipe)
    {
        var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        return (reader, writer);
    }

    private sealed class DenyAllSink : IApprovalSink
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(ApprovalResponse.Deny("not under test"));
        }
    }
}
