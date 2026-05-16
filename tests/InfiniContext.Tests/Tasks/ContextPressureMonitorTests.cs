using System.Text.Json.Nodes;
using InfiniContext.Core.Events;
using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Tasks;

/// <summary>
/// Unit tests for <see cref="ContextPressureMonitor"/>. Synthetic <see cref="ResultEvent"/>s with
/// crafted <c>usage</c> + <c>modelUsage</c> JSON drive the threshold logic — no parser, no
/// transport, no FakeClaude. The crossings list captures every event the monitor raises so
/// "fires once" assertions are unambiguous.
/// </summary>
public class ContextPressureMonitorTests
{
    private const string Model = "claude-opus-4-7";
    private static readonly Guid Sid = Guid.Parse("00000000-0000-0000-0000-000000000aaa");

    [Fact]
    public void Soft_crossing_fires_once_below_hard()
    {
        var monitor = NewMonitor(out var soft, out var hard);
        Init(monitor);

        Result(monitor, used: 130_000, window: 200_000); // 65%
        Result(monitor, used: 140_000, window: 200_000); // 70%

        soft.Should().HaveCount(1);
        hard.Should().BeEmpty();
        soft[0].UsedFraction.Should().BeApproximately(0.65, 0.001);
        soft[0].Model.Should().Be(Model);
    }

    [Fact]
    public void Hard_crossing_fires_once_and_implies_soft_is_silenced()
    {
        // Going straight from 50% to 90% must fire HARD only — no soft afterwards.
        var monitor = NewMonitor(out var soft, out var hard);
        Init(monitor);

        Result(monitor, used: 100_000, window: 200_000); // 50% — no fire
        Result(monitor, used: 180_000, window: 200_000); // 90% — HARD fires

        hard.Should().ContainSingle().Which.UsedFraction.Should().BeApproximately(0.90, 0.001);
        soft.Should().BeEmpty();
    }

    [Fact]
    public void Soft_then_hard_both_fire_once_in_order()
    {
        var monitor = NewMonitor(out var soft, out var hard);
        Init(monitor);

        Result(monitor, used: 130_000, window: 200_000); // 65% — soft
        Result(monitor, used: 180_000, window: 200_000); // 90% — hard

        soft.Should().ContainSingle();
        hard.Should().ContainSingle();
    }

    [Fact]
    public void No_flapping_after_compact_drops_usage_back_below_threshold()
    {
        var monitor = NewMonitor(out var soft, out var hard);
        Init(monitor);

        Result(monitor, used: 130_000, window: 200_000); // 65% — soft fires
        Result(monitor, used: 50_000, window: 200_000);  // 25% — back below
        Result(monitor, used: 130_000, window: 200_000); // 65% again

        soft.Should().ContainSingle("crossings are once-per-session, no re-arm after dropping below");
        hard.Should().BeEmpty();
    }

    [Fact]
    public void New_session_id_resets_the_crossing_flags()
    {
        var monitor = NewMonitor(out var soft, out var hard);
        Init(monitor);
        Result(monitor, used: 130_000, window: 200_000); // soft fires

        // Re-spawn observed: a different session id arrives in init.
        var newSid = Guid.NewGuid();
        Init(monitor, sessionId: newSid);
        Result(monitor, used: 130_000, window: 200_000); // soft can fire again in the new session

        soft.Should().HaveCount(2);
    }

    [Fact]
    public void Window_falls_back_to_settings_map_when_modelUsage_is_absent()
    {
        var settingsWindows = new Dictionary<string, int> { [Model] = 200_000 };
        var monitor = new ContextPressureMonitor(softThresholdPct: 60, hardThresholdPct: 85, settingsWindows: settingsWindows);
        var soft = new List<ContextPressureCrossing>();
        monitor.SoftCrossed += c => soft.Add(c);
        Init(monitor);

        Result(monitor, used: 130_000, window: null); // no modelUsage on the event

        soft.Should().ContainSingle().Which.WindowTokens.Should().Be(200_000);
    }

    [Fact]
    public void Without_any_window_source_no_crossing_fires()
    {
        var monitor = new ContextPressureMonitor(softThresholdPct: 60, hardThresholdPct: 85);
        var soft = new List<ContextPressureCrossing>();
        var hard = new List<ContextPressureCrossing>();
        monitor.SoftCrossed += c => soft.Add(c);
        monitor.HardCrossed += c => hard.Add(c);
        Init(monitor);

        Result(monitor, used: 1_000_000, window: null); // huge usage, but no window known

        soft.Should().BeEmpty();
        hard.Should().BeEmpty();
    }

    [Fact]
    public void Result_without_usage_object_is_ignored()
    {
        var monitor = NewMonitor(out var soft, out var hard);
        Init(monitor);
        var ev = new ResultEvent
        {
            Type = "result",
            SessionId = Sid,
            Raw = new JsonObject { ["type"] = "result" }, // no `usage`
        };

        monitor.OnEvent(ev);

        soft.Should().BeEmpty();
        hard.Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_reflects_observed_state()
    {
        var monitor = NewMonitor(out _, out _);
        Init(monitor);
        Result(monitor, used: 130_000, window: 200_000); // soft

        var snap = monitor.Snapshot();
        snap.ActiveModel.Should().Be(Model);
        snap.SessionId.Should().Be(Sid);
        snap.SoftCrossed.Should().BeTrue();
        snap.HardCrossed.Should().BeFalse();
    }

    [Fact]
    public void Constructor_validates_thresholds()
    {
        FluentActions.Invoking(() => new ContextPressureMonitor(0, 85))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new ContextPressureMonitor(60, 100))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new ContextPressureMonitor(85, 60))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new ContextPressureMonitor(60, 60))
            .Should().Throw<ArgumentException>();
    }

    private static ContextPressureMonitor NewMonitor(out List<ContextPressureCrossing> soft, out List<ContextPressureCrossing> hard)
    {
        var monitor = new ContextPressureMonitor(softThresholdPct: 60, hardThresholdPct: 85);
        var sList = new List<ContextPressureCrossing>();
        var hList = new List<ContextPressureCrossing>();
        monitor.SoftCrossed += c => sList.Add(c);
        monitor.HardCrossed += c => hList.Add(c);
        soft = sList;
        hard = hList;
        return monitor;
    }

    private static void Init(ContextPressureMonitor monitor, Guid? sessionId = null)
    {
        var sid = sessionId ?? Sid;
        var raw = new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "init",
            ["session_id"] = sid.ToString("D"),
            ["model"] = Model,
        };
        monitor.OnEvent(new SystemInitEvent
        {
            Type = "system",
            Subtype = "init",
            SessionId = sid,
            Model = Model,
            Raw = raw,
        });
    }

    private static void Result(ContextPressureMonitor monitor, int used, int? window)
    {
        // Split `used` deterministically across the three input fields the design sums.
        var input = used / 3;
        var cacheCreate = used / 3;
        var cacheRead = used - input - cacheCreate;

        var raw = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["session_id"] = Sid.ToString("D"),
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = input,
                ["cache_creation_input_tokens"] = cacheCreate,
                ["cache_read_input_tokens"] = cacheRead,
                ["output_tokens"] = 100,
            },
        };
        if (window is int w)
        {
            raw["modelUsage"] = new JsonObject
            {
                [Model] = new JsonObject
                {
                    ["contextWindow"] = w,
                },
            };
        }

        monitor.OnEvent(new ResultEvent
        {
            Type = "result",
            Subtype = "success",
            SessionId = Sid,
            Raw = raw,
        });
    }
}
