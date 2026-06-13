using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using InfiniContext.Core.Mcp;
using InfiniContext.Tests.Supervision;
using FluentAssertions;

namespace InfiniContext.Tests.Mcp;

/// <summary>
/// End-to-end tests over a real named pipe pair: host runs <see cref="ApprovalChannel"/>; "sidecar" is
/// a <see cref="NamedPipeClientStream"/> we drive directly with NDJSON requests/responses. We don't
/// spawn a separate process — the bytes-on-wire shape is identical to what the real sidecar would send.
/// </summary>
public class ApprovalE2ETests
{
    [Fact]
    public async Task Allow_response_round_trips_back_to_sidecar()
    {
        var sink = new ScriptedSink(req => ApprovalResponse.Allow(req.Input));
        await using var channel = new ApprovalChannel(ApprovalChannel.GeneratePipeName(), sink);
        await channel.StartAsync();

        var response = await SendOneRequestAsync(channel.PipeName,
            new ApprovalRequest("Bash", new JsonObject { ["command"] = "ls" }));

        response["behavior"]!.GetValue<string>().Should().Be("allow");
        response["updatedInput"]!["command"]!.GetValue<string>().Should().Be("ls");
        sink.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Deny_response_round_trips_with_message()
    {
        var sink = new ScriptedSink(_ => ApprovalResponse.Deny("nope"));
        await using var channel = new ApprovalChannel(ApprovalChannel.GeneratePipeName(), sink);
        await channel.StartAsync();

        var response = await SendOneRequestAsync(channel.PipeName,
            new ApprovalRequest("Bash", new JsonObject { ["command"] = "rm -rf /" }));

        response["behavior"]!.GetValue<string>().Should().Be("deny");
        response["message"]!.GetValue<string>().Should().Be("nope");
    }

    [Fact]
    public async Task Sink_throwing_returns_deny_with_host_error_prefix()
    {
        var sink = new ScriptedSink(_ => throw new InvalidOperationException("boom"));
        await using var channel = new ApprovalChannel(ApprovalChannel.GeneratePipeName(), sink);
        await channel.StartAsync();

        var response = await SendOneRequestAsync(channel.PipeName,
            new ApprovalRequest("Bash", new JsonObject()));

        response["behavior"]!.GetValue<string>().Should().Be("deny");
        response["message"]!.GetValue<string>().Should().Contain("host error");
    }

    [Fact]
    public async Task Two_sequential_requests_on_one_connection_both_resolve()
    {
        var sink = new ScriptedSink(req => req.ToolName == "Bash"
            ? ApprovalResponse.Allow(req.Input)
            : ApprovalResponse.Deny("only Bash"));
        await using var channel = new ApprovalChannel(ApprovalChannel.GeneratePipeName(), sink);
        await channel.StartAsync();

        await using var pipe = await ConnectAsync(channel.PipeName);
        var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        await writer.WriteLineAsync(new ApprovalRequest("Bash", new JsonObject()).ToJson().ToJsonString());
        var first = JsonNode.Parse(await reader.ReadLineAsync(TestContext.Current.CancellationToken) ?? "")! as JsonObject;
        first!["behavior"]!.GetValue<string>().Should().Be("allow");

        await writer.WriteLineAsync(new ApprovalRequest("Other", new JsonObject()).ToJson().ToJsonString());
        var second = JsonNode.Parse(await reader.ReadLineAsync(TestContext.Current.CancellationToken) ?? "")! as JsonObject;
        second!["behavior"]!.GetValue<string>().Should().Be("deny");

        sink.Calls.Should().Be(2);
    }

    [WindowsOnlyFact]
    public async Task Connection_from_unexpected_client_image_is_rejected()
    {
        // M1: a same-user process that is NOT the expected sidecar image must be refused. The
        // in-process client's image is the test runner; pointing the channel at a bogus expected
        // image makes the identity check fail, so the peer is disconnected before any request is
        // served. (Enforcement is Windows-only — on Unix the check is best-effort and allows.)
        var sink = new ScriptedSink(req => ApprovalResponse.Allow(req.Input));
        await using var channel = new ApprovalChannel(
            ApprovalChannel.GeneratePipeName(), sink,
            expectedClientExecutable: @"C:\__infinicontext_nonexistent__\bogus-sidecar.exe");
        await channel.StartAsync();

        await using var pipe = await ConnectAsync(channel.PipeName);
        var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        string? response = null;
        try
        {
            // The rejection can break the pipe before or during the write, so guard both.
            await writer.WriteLineAsync(new ApprovalRequest("Bash", new JsonObject { ["command"] = "ls" }).ToJson().ToJsonString());
            var read = reader.ReadLineAsync();
            var done = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(3)));
            if (done == read) response = await read;
        }
        catch (IOException) { /* broken pipe is also an acceptable rejection signal */ }

        response.Should().BeNull("a rejected peer must receive no approval response");
        sink.Calls.Should().Be(0, "the approval sink must never see a request from a rejected peer");
    }

    private static async Task<JsonObject> SendOneRequestAsync(string pipeName, ApprovalRequest request)
    {
        await using var pipe = await ConnectAsync(pipeName);
        var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        await writer.WriteLineAsync(request.ToJson().ToJsonString());
        var line = await reader.ReadLineAsync() ?? throw new InvalidOperationException("pipe closed before response");
        return (JsonNode.Parse(line) as JsonObject) ?? throw new InvalidOperationException("non-object response");
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5_000);
        return pipe;
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
