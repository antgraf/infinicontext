using InfiniContext.Core.Sessions;
using FluentAssertions;

namespace InfiniContext.Tests.Sessions;

public class TranscriptModelTests
{
    [Fact]
    public void Append_grows_until_capacity_then_drops_oldest()
    {
        var model = new TranscriptModel(capacity: 3);

        model.Append("a");
        model.Append("b");
        model.Append("c");
        model.Snapshot().Should().Equal(new[] { "a", "b", "c" });

        model.Append("d");
        model.Snapshot().Should().Equal(new[] { "b", "c", "d" });
    }

    [Fact]
    public void Snapshot_is_disconnected_from_subsequent_mutations()
    {
        var model = new TranscriptModel(capacity: 4);
        model.Append("first");

        var snap = model.Snapshot();
        model.Append("second");

        snap.Should().Equal(new[] { "first" });
    }

    [Fact]
    public void Count_tracks_held_fragments()
    {
        var model = new TranscriptModel(capacity: 2);
        model.Count.Should().Be(0);

        model.Append("x");
        model.Count.Should().Be(1);

        model.Append("y");
        model.Append("z");
        model.Count.Should().Be(2, "capacity is 2 so the oldest entry is dropped on overflow");
    }

    [Fact]
    public void Constructor_rejects_nonpositive_capacity()
    {
        Action zero = () => _ = new TranscriptModel(capacity: 0);
        Action negative = () => _ = new TranscriptModel(capacity: -5);

        zero.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Append_rejects_null()
    {
        var model = new TranscriptModel();
        Action act = () => model.Append(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
