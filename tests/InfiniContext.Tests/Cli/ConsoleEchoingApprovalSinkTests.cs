using System.Text.Json.Nodes;
using InfiniContext.Core.Mcp;
using FluentAssertions;

namespace InfiniContext.Tests.Cli;

public class ConsoleEchoingApprovalSinkTests
{
    [Fact]
    public async Task Allow_decision_writes_allow_line_with_tool_name()
    {
        var (sink, output) = New(ApprovalResponse.Allow());
        await sink.RequestAsync(new ApprovalRequest("Bash", new JsonObject()), default);

        var rendered = output.ToString();
        rendered.Should().Contain("[approval]");
        rendered.Should().Contain("Bash");
        rendered.Should().Contain("ALLOW");
    }

    [Fact]
    public async Task Deny_decision_writes_deny_line_with_reason()
    {
        var (sink, output) = New(ApprovalResponse.Deny("auto-deny [Destructive]: rm -rf /"));
        await sink.RequestAsync(new ApprovalRequest("Bash", new JsonObject()), default);

        var rendered = output.ToString();
        rendered.Should().Contain("DENY");
        rendered.Should().Contain("Destructive");
        rendered.Should().Contain("rm -rf /");
    }

    [Fact]
    public async Task Returns_inner_response_unchanged()
    {
        var inner = ApprovalResponse.Deny("nope");
        var (sink, _) = New(inner);
        var actual = await sink.RequestAsync(new ApprovalRequest("Bash", new JsonObject()), default);

        actual.Behavior.Should().Be(ApprovalBehavior.Deny);
        actual.Message.Should().Be("nope");
    }

    [Fact]
    public async Task Inner_exception_propagates_without_writing()
    {
        var output = new StringWriter();
        var sink = new ConsoleEchoingApprovalSink(new ThrowingSink(), output, new object());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sink.RequestAsync(new ApprovalRequest("Bash", new JsonObject()), default));
        output.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Inner_property_exposes_wrapped_sink()
    {
        var inner = new RecordingSink(ApprovalResponse.Allow());
        var output = new StringWriter();
        var sink = new ConsoleEchoingApprovalSink(inner, output, new object());

        sink.Inner.Should().BeSameAs(inner);
        await sink.RequestAsync(new ApprovalRequest("Bash", new JsonObject()), default);
        inner.Calls.Should().Be(1);
    }

    private static (ConsoleEchoingApprovalSink sink, StringWriter output) New(ApprovalResponse response)
    {
        var inner = new RecordingSink(response);
        var output = new StringWriter();
        return (new ConsoleEchoingApprovalSink(inner, output, new object()), output);
    }

    private sealed class RecordingSink : IApprovalSink
    {
        private readonly ApprovalResponse _response;
        public int Calls { get; private set; }
        public RecordingSink(ApprovalResponse response) { _response = response; }
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_response);
        }
    }

    private sealed class ThrowingSink : IApprovalSink
    {
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("inner failed");
    }
}
