using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using InfiniContext.Core.Mcp;
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
        var first = JsonNode.Parse(await reader.ReadLineAsync() ?? "")! as JsonObject;
        first!["behavior"]!.GetValue<string>().Should().Be("allow");

        await writer.WriteLineAsync(new ApprovalRequest("Other", new JsonObject()).ToJson().ToJsonString());
        var second = JsonNode.Parse(await reader.ReadLineAsync() ?? "")! as JsonObject;
        second!["behavior"]!.GetValue<string>().Should().Be("deny");

        sink.Calls.Should().Be(2);
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
