using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Mcp.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Mcp.Tasks;

/// <summary>
/// End-to-end tests for the Phase 7 lock multiplex on the existing approval pipe. Drives an
/// <see cref="ApprovalChannel"/> with a real <see cref="LockRegistry"/> and connects a raw
/// named-pipe client; the bytes-on-wire shape is what the real sidecar will produce. No process
/// spawn — keeps the test fast and isolated from the sidecar's own argv parsing.
/// </summary>
public class LockChannelE2ETests
{
    [Fact]
    public async Task Acquire_then_release_round_trips_through_the_pipe()
    {
        var registry = new LockRegistry(pollInterval: TimeSpan.FromMilliseconds(5));
        var sink = new DenyAllSink();
        var pipeName = ApprovalChannel.GeneratePipeName();
        await using var channel = new ApprovalChannel(
            pipeName, sink,
            lockRegistry: registry,
            lockTtl: TimeSpan.FromSeconds(30),
            lockMaxWait: TimeSpan.FromSeconds(1));
        await channel.StartAsync();

        await using var pipe = await ConnectAsync(pipeName);
        var (reader, writer) = Open(pipe);

        // Acquire
        await writer.WriteLineAsync(new LockAcquireRequest("a.md", "owner-1").ToJson().ToJsonString());
        var acqLine = await reader.ReadLineAsync() ?? throw new InvalidOperationException("no acquire response");
        var acq = LockAcquireResponse.FromJson((JsonObject)JsonNode.Parse(acqLine)!);
        acq.Ok.Should().BeTrue();
        acq.Token.Should().NotBeNullOrEmpty();
        acq.TtlSeconds.Should().Be(30);

        // Release
        await writer.WriteLineAsync(new LockReleaseRequest(acq.Token!).ToJson().ToJsonString());
        var relLine = await reader.ReadLineAsync() ?? throw new InvalidOperationException("no release response");
        var rel = LockReleaseResponse.FromJson((JsonObject)JsonNode.Parse(relLine)!);
        rel.Ok.Should().BeTrue();

        registry.Snapshot().Should().BeEmpty();
        sink.Calls.Should().Be(0, "lock messages must not reach the approval sink");
    }

    [Fact]
    public async Task Acquire_on_held_file_returns_timeout_error()
    {
        var registry = new LockRegistry(pollInterval: TimeSpan.FromMilliseconds(5));
        // Pre-populate the registry with a held lock owned by a different owner.
        await registry.AcquireAsync("a.md", "external-owner", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50));

        var pipeName = ApprovalChannel.GeneratePipeName();
        await using var channel = new ApprovalChannel(
            pipeName, new DenyAllSink(),
            lockRegistry: registry,
            lockTtl: TimeSpan.FromSeconds(30),
            lockMaxWait: TimeSpan.FromMilliseconds(80));
        await channel.StartAsync();

        await using var pipe = await ConnectAsync(pipeName);
        var (reader, writer) = Open(pipe);

        await writer.WriteLineAsync(new LockAcquireRequest("a.md", "owner-1").ToJson().ToJsonString());
        var line = await reader.ReadLineAsync() ?? throw new InvalidOperationException("no response");
        var resp = LockAcquireResponse.FromJson((JsonObject)JsonNode.Parse(line)!);

        resp.Ok.Should().BeFalse();
        resp.Error.Should().Contain("timeout");
    }

    [Fact]
    public async Task Channel_dispose_releases_locks_owned_by_its_pipe_name()
    {
        var registry = new LockRegistry(pollInterval: TimeSpan.FromMilliseconds(5));
        var pipeName = ApprovalChannel.GeneratePipeName();
        var channel = new ApprovalChannel(
            pipeName, new DenyAllSink(),
            lockRegistry: registry,
            lockTtl: TimeSpan.FromSeconds(30),
            lockMaxWait: TimeSpan.FromSeconds(1));
        try
        {
            await channel.StartAsync();
            await using var pipe = await ConnectAsync(pipeName);
            var (reader, writer) = Open(pipe);

            // Sidecar uses the pipe name as the owner id (matches ApprovalSidecarHost).
            await writer.WriteLineAsync(new LockAcquireRequest("a.md", pipeName).ToJson().ToJsonString());
            var line = await reader.ReadLineAsync() ?? throw new InvalidOperationException("no response");
            LockAcquireResponse.FromJson((JsonObject)JsonNode.Parse(line)!).Ok.Should().BeTrue();
            registry.Snapshot().Should().HaveCount(1);
        }
        finally
        {
            await channel.DisposeAsync();
        }

        registry.Snapshot().Should().BeEmpty(
            "ApprovalChannel.DisposeAsync must reclaim every lock owned by its pipe name");
    }

    [Fact]
    public async Task Approval_messages_still_route_to_sink_alongside_lock_messages()
    {
        var registry = new LockRegistry(pollInterval: TimeSpan.FromMilliseconds(5));
        var sink = new ScriptedSink(req => ApprovalResponse.Allow(req.Input));
        var pipeName = ApprovalChannel.GeneratePipeName();
        await using var channel = new ApprovalChannel(
            pipeName, sink,
            lockRegistry: registry,
            lockTtl: TimeSpan.FromSeconds(30),
            lockMaxWait: TimeSpan.FromSeconds(1));
        await channel.StartAsync();

        await using var pipe = await ConnectAsync(pipeName);
        var (reader, writer) = Open(pipe);

        // Approval (no kind field — back-compat).
        await writer.WriteLineAsync(new ApprovalRequest("Bash", new JsonObject { ["command"] = "ls" })
            .ToJson().ToJsonString());
        var apprLine = await reader.ReadLineAsync() ?? "";
        var appr = (JsonObject)JsonNode.Parse(apprLine)!;
        appr["behavior"]!.GetValue<string>().Should().Be("allow");

        // Lock acquire on the same connection.
        await writer.WriteLineAsync(new LockAcquireRequest("a.md", "owner-1").ToJson().ToJsonString());
        var lockLine = await reader.ReadLineAsync() ?? "";
        var lockResp = LockAcquireResponse.FromJson((JsonObject)JsonNode.Parse(lockLine)!);
        lockResp.Ok.Should().BeTrue();

        sink.Calls.Should().Be(1, "the approval sink must see exactly the one approval request");
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

    private sealed class ScriptedSink : IApprovalSink
    {
        private readonly Func<ApprovalRequest, ApprovalResponse> _handler;
        private int _calls;
        public ScriptedSink(Func<ApprovalRequest, ApprovalResponse> handler) => _handler = handler;
        public int Calls => Volatile.Read(ref _calls);
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(_handler(request));
        }
    }
}
