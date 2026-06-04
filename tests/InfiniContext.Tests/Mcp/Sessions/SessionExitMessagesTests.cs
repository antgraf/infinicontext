using System.Text.Json.Nodes;
using InfiniContext.Core.Mcp.Sessions;
using InfiniContext.Core.Mcp.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Mcp.Sessions;

/// <summary>
/// Stage 3 bug A. Round-trips the <c>session.exit</c> wire records through ToJson/FromJson and
/// pins the <c>kind</c> discriminator that multiplexes them onto the shared approval pipe.
/// </summary>
public class SessionExitMessagesTests
{
    [Fact]
    public void Request_round_trips_with_reason()
    {
        var json = new SessionExitRequest("flushed after hard nudge").ToJson();

        json[LockWireFormat.KindField]!.GetValue<string>().Should().Be(LockWireFormat.SessionExitKind);
        json["reason"]!.GetValue<string>().Should().Be("flushed after hard nudge");

        SessionExitRequest.FromJson(json).Reason.Should().Be("flushed after hard nudge");
    }

    [Fact]
    public void Request_round_trips_with_null_reason()
    {
        var json = new SessionExitRequest(null).ToJson();

        json[LockWireFormat.KindField]!.GetValue<string>().Should().Be(LockWireFormat.SessionExitKind);
        json["reason"].Should().BeNull();

        SessionExitRequest.FromJson(json).Reason.Should().BeNull();
    }

    [Fact]
    public void FromJson_tolerates_missing_reason_field()
    {
        var obj = new JsonObject { [LockWireFormat.KindField] = LockWireFormat.SessionExitKind };

        SessionExitRequest.FromJson(obj).Reason.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Response_round_trips(bool ok)
    {
        var json = new SessionExitResponse(ok).ToJson();

        json[LockWireFormat.KindField]!.GetValue<string>().Should().Be(LockWireFormat.SessionExitKind);
        json["ok"]!.GetValue<bool>().Should().Be(ok);

        SessionExitResponse.FromJson(json).Ok.Should().Be(ok);
    }

    [Fact]
    public void Response_FromJson_defaults_ok_false_when_missing()
    {
        SessionExitResponse.FromJson(new JsonObject()).Ok.Should().BeFalse();
    }
}
