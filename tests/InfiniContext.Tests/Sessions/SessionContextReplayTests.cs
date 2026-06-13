using FluentAssertions;
using InfiniContext.Core.Events;
using InfiniContext.Tests.Gui;

namespace InfiniContext.Tests.Sessions;

/// <summary>
/// M19: <see cref="InfiniContext.Core.Sessions.SessionContext.SubscribeWithReplay"/> must give a
/// late subscriber a gap-free, in-order, duplicate-free event sequence — the per-session reader
/// starts pumping before any UI consumer can attach. Uses the shared
/// <see cref="SessionContextFactory"/> (real Core types; the only fake is the IClaudeSession seam).
/// </summary>
public class SessionContextReplayTests
{
    [Fact]
    public void SubscribeWithReplay_replays_buffered_events_in_order()
    {
        var f = new SessionContextFactory();
        var init = SessionContextFactory.MakeInit();
        var asst = SessionContextFactory.MakeAssistantText("hello");
        f.RaiseEvent(init);
        f.RaiseEvent(asst);

        var seen = new List<ClaudeEvent>();
        using var _ = f.Context.SubscribeWithReplay(seen.Add);

        seen.Should().Equal(new ClaudeEvent[] { init, asst },
            "the whole pre-subscribe window replays in FIFO order.");
    }

    [Fact]
    public void Events_after_subscribe_deliver_live_without_duplicating_the_replay()
    {
        var f = new SessionContextFactory();
        var init = SessionContextFactory.MakeInit();
        f.RaiseEvent(init);

        var seen = new List<ClaudeEvent>();
        using var _ = f.Context.SubscribeWithReplay(seen.Add);

        var asst = SessionContextFactory.MakeAssistantText("live");
        f.RaiseEvent(asst);

        seen.Should().Equal(new ClaudeEvent[] { init, asst },
            "init is delivered once (replay) and the post-subscribe event once (live) — no gap, no dupe.");
    }

    [Fact]
    public void Disposing_the_subscription_stops_delivery()
    {
        var f = new SessionContextFactory();
        var seen = new List<ClaudeEvent>();
        var sub = f.Context.SubscribeWithReplay(seen.Add);

        f.RaiseEvent(SessionContextFactory.MakeInit());
        sub.Dispose();
        f.RaiseEvent(SessionContextFactory.MakeAssistantText("after-dispose"));

        seen.Should().HaveCount(1, "only events delivered before dispose are observed.");
    }

    [Fact]
    public void SubscribeWithReplay_on_an_empty_buffer_replays_nothing()
    {
        var f = new SessionContextFactory();

        var seen = new List<ClaudeEvent>();
        using var _ = f.Context.SubscribeWithReplay(seen.Add);

        seen.Should().BeEmpty("a free session that has emitted no events yet has nothing to replay.");
    }
}
