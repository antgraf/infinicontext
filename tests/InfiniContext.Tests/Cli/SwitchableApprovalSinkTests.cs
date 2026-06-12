using System.Text.Json.Nodes;
using InfiniContext.Cli;
using InfiniContext.Core.Mcp;
using FluentAssertions;

namespace InfiniContext.Tests.Cli;

public class SwitchableApprovalSinkTests
{
    [Fact]
    public async Task Forwards_request_to_initial_inner()
    {
        var inner = new CountingSink(ApprovalResponse.Allow());
        var s = new SwitchableApprovalSink(inner);
        var resp = await s.RequestAsync(NewRequest(), TestContext.Current.CancellationToken);
        resp.Behavior.Should().Be(ApprovalBehavior.Allow);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Switch_replaces_inner_on_next_request()
    {
        var first = new CountingSink(ApprovalResponse.Allow());
        var second = new CountingSink(ApprovalResponse.Deny("nope"));
        var s = new SwitchableApprovalSink(first);
        await s.RequestAsync(NewRequest(), TestContext.Current.CancellationToken);
        s.Switch(second);
        var resp = await s.RequestAsync(NewRequest(), TestContext.Current.CancellationToken);
        resp.Behavior.Should().Be(ApprovalBehavior.Deny);
        first.Calls.Should().Be(1);
        second.Calls.Should().Be(1);
    }

    [Fact]
    public void Inner_property_returns_current()
    {
        var first = new CountingSink(ApprovalResponse.Allow());
        var second = new CountingSink(ApprovalResponse.Deny("x"));
        var s = new SwitchableApprovalSink(first);
        s.Inner.Should().BeSameAs(first);
        s.Switch(second);
        s.Inner.Should().BeSameAs(second);
    }

    private static ApprovalRequest NewRequest() => new("Bash", new JsonObject());

    private sealed class CountingSink : IApprovalSink
    {
        private readonly ApprovalResponse _response;
        public int Calls { get; private set; }
        public CountingSink(ApprovalResponse response) { _response = response; }
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_response);
        }
    }
}
