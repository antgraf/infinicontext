using System.Text.Json.Nodes;
using InfiniContext.Core.Events;
using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Tasks;

/// <summary>
/// Unit tests for <see cref="ContextPressureMonitor"/>. Threshold detection is driven by synthetic
/// <see cref="AssistantEvent"/>s carrying a crafted <c>message.usage</c> (one API call's prompt
/// size); the window is supplied via a <see cref="ResultEvent"/>'s <c>modelUsage</c> or the
/// settings map. No parser, no transport, no FakeClaude. The crossings list captures every event
/// the monitor raises so "fires once" assertions are unambiguous.
///
/// <para>Task 0014: occupancy is measured per API call, never from the turn-level
/// <c>result.usage</c> (which sums across iterations and over-reports). The
/// "result summed usage does not drive threshold" and "sub-agent assistant events are ignored"
/// tests pin that contract.</para>
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

        Turn(monitor, used: 130_000, window: 200_000); // 65%
        Turn(monitor, used: 140_000, window: 200_000); // 70%

        soft.Should().HaveCount(1);
        hard.Should().BeEmpty();
        soft[0].UsedFraction.Should().BeApproximately(0.65, 0.001);
        soft[0].Model.Should().Be(Model);
    }

    [Fact]
    public void Hard_crossing_fires_once_and_implies_soft_is_silenced()
    {
        // Going straight from 50% to 90% must fire HARD only - no soft afterwards.
        var monitor = NewMonitor(out var soft, out var hard);
        Init(monitor);

        Turn(monitor, used: 100_000, window: 200_000); // 50% - no fire
        Turn(monitor, used: 180_000, window: 200_000); // 90% - HARD fires

        hard.Should().ContainSingle().Which.UsedFraction.Should().BeApproximately(0.90, 0.001);
        soft.Should().BeEmpty();
    }

    [Fact]
    public void Soft_then_hard_both_fire_once_in_order()
    {
        var monitor = NewMonitor(out var soft, out var hard);
        Init(monitor);

        Turn(monitor, used: 130_000, window: 200_000); // 65% - soft
        Turn(monitor, used: 180_000, window: 200_000); // 90% - hard

        soft.Should().ContainSingle();
        hard.Should().ContainSingle();
    }

    [Fact]
    public void No_flapping_after_compact_drops_usage_back_below_threshold()
    {
        var monitor = NewMonitor(out var soft, out var hard);
        Init(monitor);

        Turn(monitor, used: 130_000, window: 200_000); // 65% - soft fires
        Turn(monitor, used: 50_000, window: 200_000);  // 25% - back below
        Turn(monitor, used: 130_000, window: 200_000); // 65% again

        soft.Should().ContainSingle("crossings are once-per-session, no re-arm after dropping below");
        hard.Should().BeEmpty();
    }

    [Fact]
    public void New_session_id_resets_the_crossing_flags()
    {
        var monitor = NewMonitor(out var soft, out var hard);
        Init(monitor);
        Turn(monitor, used: 130_000, window: 200_000); // soft fires

        // Re-spawn observed: a different session id arrives in init.
        var newSid = Guid.NewGuid();
        Init(monitor, sessionId: newSid);
        Turn(monitor, used: 130_000, window: 200_000); // soft can fire again in the new session

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

        Assistant(monitor, used: 130_000); // no result/modelUsage seen; window comes from settings

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

        Assistant(monitor, used: 1_000_000); // huge usage, but no window known

        soft.Should().BeEmpty();
        hard.Should().BeEmpty();
    }

    [Fact]
    public void Assistant_without_usage_object_is_ignored()
    {
        var monitor = NewMonitor(out var soft, out var hard);
        Init(monitor);
        WindowResult(monitor, window: 200_000);
        var ev = new AssistantEvent
        {
            Type = "assistant",
            SessionId = Sid,
            Content = Array.Empty<ContentBlock>(),
            Raw = new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject() }, // no `usage`
        };

        monitor.OnEvent(ev);

        soft.Should().BeEmpty();
        hard.Should().BeEmpty();
    }

    [Fact]
    public void ResultSummedUsage_does_not_drive_threshold()
    {
        // Regression pin (task 0014): a result event carries usage summed across the turn's API
        // iterations - e.g. 3.59M against a 1M window ("359% of 1M"). It MUST NOT fire a crossing;
        // only per-call assistant footprints do.
        var monitor = NewMonitor(out var soft, out var hard);
        Init(monitor);

        var raw = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["session_id"] = Sid.ToString("D"),
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 10,
                ["cache_creation_input_tokens"] = 590_000,
                ["cache_read_input_tokens"] = 3_000_000, // summed across iterations
                ["output_tokens"] = 1000,
            },
            ["modelUsage"] = new JsonObject
            {
                [Model] = new JsonObject { ["contextWindow"] = 1_000_000 },
            },
        };
        monitor.OnEvent(new ResultEvent { Type = "result", Subtype = "success", SessionId = Sid, Raw = raw });

        soft.Should().BeEmpty("the summed result.usage must never drive the threshold");
        hard.Should().BeEmpty();
    }

    [Fact]
    public void Subagent_assistant_events_are_ignored()
    {
        // Task-tool sub-agents run against their own context window; their assistant events carry
        // parent_tool_use_id and must not count toward the main session's occupancy.
        var monitor = NewMonitor(out var soft, out var hard);
        Init(monitor);
        WindowResult(monitor, window: 200_000);

        Assistant(monitor, used: 190_000, parentToolUseId: "toolu_subagent"); // 95% but sub-agent

        soft.Should().BeEmpty();
        hard.Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_reflects_observed_state()
    {
        var monitor = NewMonitor(out _, out _);
        Init(monitor);
        Turn(monitor, used: 130_000, window: 200_000); // soft

        var snap = monitor.Snapshot();
        snap.ActiveModel.Should().Be(Model);
        snap.SessionId.Should().Be(Sid);
        snap.SoftCrossed.Should().BeTrue();
        snap.HardCrossed.Should().BeFalse();
    }

    [Fact]
    public void Large_window_selects_the_large_context_pair()
    {
        // Task 0018 follow-up: a default spawn names no model, so the pair must follow the window
        // the monitor resolves at runtime - 1M => the tighter 45/65, not the spawn-time 60/85.
        var monitor = NewLargeAwareMonitor(out var soft, out var hard);
        Init(monitor);

        Turn(monitor, used: 500_000, window: 1_000_000); // 50% - soft under the 45 large threshold
        Turn(monitor, used: 700_000, window: 1_000_000); // 70% - hard under the 65 large threshold

        soft.Should().ContainSingle().Which.UsedFraction.Should().BeApproximately(0.50, 0.001);
        hard.Should().ContainSingle().Which.UsedFraction.Should().BeApproximately(0.70, 0.001);
    }

    [Fact]
    public void Small_window_keeps_the_default_pair_when_the_large_pair_is_configured()
    {
        var monitor = NewLargeAwareMonitor(out var soft, out var hard);
        Init(monitor);

        Turn(monitor, used: 100_000, window: 200_000); // 50% - below the default 60 soft
        soft.Should().BeEmpty();

        // 65% would be a HARD crossing under the large pair; on a 200k window it must be the
        // default pair's soft-only.
        Turn(monitor, used: 130_000, window: 200_000);
        soft.Should().ContainSingle();
        hard.Should().BeEmpty();
    }

    [Fact]
    public void Self_reported_window_upgrade_switches_to_the_large_pair()
    {
        // The settings map carries a stale 200k for the model, but claude itself reports a 1M
        // window in result.modelUsage - the authoritative source wins and the large pair applies.
        var settingsWindows = new Dictionary<string, int> { [Model] = 200_000 };
        var monitor = new ContextPressureMonitor(
            softThresholdPct: 60,
            hardThresholdPct: 85,
            settingsWindows: settingsWindows,
            softThresholdPctLargeContext: 45,
            hardThresholdPctLargeContext: 65,
            largeContextWindowTokens: 1_000_000);
        var soft = new List<ContextPressureCrossing>();
        monitor.SoftCrossed += c => soft.Add(c);
        Init(monitor);
        WindowResult(monitor, window: 1_000_000);

        Assistant(monitor, used: 500_000); // 50% of 1M - soft fires only under the large pair

        soft.Should().ContainSingle().Which.WindowTokens.Should().Be(1_000_000);
    }

    [Fact]
    public void Zero_cutoff_disables_the_large_context_pair()
    {
        var monitor = new ContextPressureMonitor(
            softThresholdPct: 60,
            hardThresholdPct: 85,
            softThresholdPctLargeContext: 45,
            hardThresholdPctLargeContext: 65,
            largeContextWindowTokens: 0);
        var soft = new List<ContextPressureCrossing>();
        monitor.SoftCrossed += c => soft.Add(c);
        Init(monitor);

        Turn(monitor, used: 500_000, window: 1_000_000); // 50% - silent under the default 60/85

        soft.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_validates_the_large_pair_only_when_the_cutoff_is_positive()
    {
        FluentActions.Invoking(() => new ContextPressureMonitor(60, 85, largeContextWindowTokens: 1_000_000))
            .Should().Throw<ArgumentOutOfRangeException>("a positive cutoff requires a valid large pair");
        FluentActions.Invoking(() => new ContextPressureMonitor(
                60, 85, softThresholdPctLargeContext: 65, hardThresholdPctLargeContext: 45, largeContextWindowTokens: 1_000_000))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new ContextPressureMonitor(60, 85))
            .Should().NotThrow("an omitted/zero cutoff disables the large pair entirely");
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
        => Capture(new ContextPressureMonitor(softThresholdPct: 60, hardThresholdPct: 85), out soft, out hard);

    /// <summary>The production shape (task 0018 follow-up): both pairs configured, selection
    /// driven by the resolved window against the 1M cutoff.</summary>
    private static ContextPressureMonitor NewLargeAwareMonitor(out List<ContextPressureCrossing> soft, out List<ContextPressureCrossing> hard)
        => Capture(
            new ContextPressureMonitor(
                softThresholdPct: 60,
                hardThresholdPct: 85,
                softThresholdPctLargeContext: 45,
                hardThresholdPctLargeContext: 65,
                largeContextWindowTokens: 1_000_000),
            out soft, out hard);

    private static ContextPressureMonitor Capture(ContextPressureMonitor monitor, out List<ContextPressureCrossing> soft, out List<ContextPressureCrossing> hard)
    {
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

    /// <summary>One turn: feed a window-bearing result (when <paramref name="window"/> is set) so
    /// the monitor knows the context size, then a single assistant call whose <c>message.usage</c>
    /// footprint sums to <paramref name="used"/>. The result is fed first only for test convenience;
    /// in the wire stream the assistant precedes the result, and the settings-map fallback covers
    /// the first-turn-before-any-result case (see the settings-map test).</summary>
    private static void Turn(ContextPressureMonitor monitor, int used, int? window)
    {
        if (window is int w) WindowResult(monitor, w);
        Assistant(monitor, used);
    }

    /// <summary>Feed a result event carrying only <c>modelUsage[Model].contextWindow</c> so the
    /// monitor caches the authoritative window. The summed <c>usage</c> block is intentionally
    /// omitted - it must not influence the threshold.</summary>
    private static void WindowResult(ContextPressureMonitor monitor, int window)
    {
        var raw = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["session_id"] = Sid.ToString("D"),
            ["modelUsage"] = new JsonObject
            {
                [Model] = new JsonObject { ["contextWindow"] = window },
            },
        };
        monitor.OnEvent(new ResultEvent { Type = "result", Subtype = "success", SessionId = Sid, Raw = raw });
    }

    /// <summary>Feed one assistant event (a single API call) whose <c>message.usage</c> footprint
    /// sums to <paramref name="used"/>. A non-null <paramref name="parentToolUseId"/> marks it a
    /// sub-agent call, which the monitor must skip.</summary>
    private static void Assistant(ContextPressureMonitor monitor, int used, string? parentToolUseId = null)
    {
        var input = used / 3;
        var cacheCreate = used / 3;
        var cacheRead = used - input - cacheCreate;

        var raw = new JsonObject
        {
            ["type"] = "assistant",
            ["session_id"] = Sid.ToString("D"),
            ["message"] = new JsonObject
            {
                ["model"] = Model,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = input,
                    ["cache_creation_input_tokens"] = cacheCreate,
                    ["cache_read_input_tokens"] = cacheRead,
                    ["output_tokens"] = 100,
                },
            },
        };
        if (parentToolUseId is not null) raw["parent_tool_use_id"] = parentToolUseId;

        monitor.OnEvent(new AssistantEvent
        {
            Type = "assistant",
            SessionId = Sid,
            ParentToolUseId = parentToolUseId,
            Content = Array.Empty<ContentBlock>(),
            Raw = raw,
        });
    }
}
