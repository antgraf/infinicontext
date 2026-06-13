using FluentAssertions;
using InfiniContext.Gui.Services;

namespace InfiniContext.Tests.Gui;

/// <summary>
/// M22: <see cref="BatchingOutputSink"/> coalesces host-command output and flushes it on an
/// <see cref="IUiTimer"/> tick rather than per write — killing the per-character dispatcher-post +
/// O(n²) <c>Detail</c>-rebuild path. Uses <see cref="TestUiTimer"/> to fire ticks deterministically.
/// </summary>
public class BatchingOutputSinkTests
{
    [Fact]
    public void Appends_are_buffered_until_a_tick_then_flushed_as_one_call()
    {
        var factory = new TestUiTimerFactory();
        var flushes = new List<string>();
        using var sink = new BatchingOutputSink(flushes.Add, factory);

        sink.Append("a");
        sink.Append("b");
        sink.Append("c");

        flushes.Should().BeEmpty("nothing flushes until the timer ticks.");

        factory.Created.Single().FireTick();

        flushes.Should().ContainSingle().Which.Should().Be("abc",
            "all buffered writes coalesce into one flush — one dispatcher post + one concat per tick.");
    }

    [Fact]
    public void Empty_buffer_tick_does_not_flush()
    {
        var factory = new TestUiTimerFactory();
        var flushes = new List<string>();
        using var sink = new BatchingOutputSink(flushes.Add, factory);

        factory.Created.Single().FireTick();

        flushes.Should().BeEmpty("an empty buffer produces no flush call.");
    }

    [Fact]
    public void Dispose_flushes_the_tail_and_stops_the_timer()
    {
        var factory = new TestUiTimerFactory();
        var flushes = new List<string>();
        var sink = new BatchingOutputSink(flushes.Add, factory);
        var timer = factory.Created.Single();

        sink.Append("tail");
        sink.Dispose();

        flushes.Should().ContainSingle().Which.Should().Be("tail", "dispose flushes whatever is buffered.");
        timer.IsEnabled.Should().BeFalse("dispose stops the timer.");
        timer.StopCount.Should().Be(1);
    }

    [Fact]
    public void Timer_starts_on_construction()
    {
        var factory = new TestUiTimerFactory();
        using var sink = new BatchingOutputSink(_ => { }, factory);

        factory.Created.Single().IsEnabled.Should().BeTrue("the sink starts its timer so flushes happen.");
    }
}
