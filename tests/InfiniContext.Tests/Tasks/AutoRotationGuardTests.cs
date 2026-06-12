using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Tasks;

/// <summary>
/// Unit tests for <see cref="AutoRotationGuard"/>: the cap on consecutive context-pressure
/// auto-rotations per task. A controllable clock lets us assert the rolling-window math
/// without sleeping in the test.
/// </summary>
public class AutoRotationGuardTests
{
    [Fact]
    public void TryRegister_allows_up_to_max_then_refuses()
    {
        var now = DateTimeOffset.UnixEpoch;
        var guard = new AutoRotationGuard(maxRotations: 3, window: TimeSpan.FromMinutes(5), clock: () => now);

        guard.TryRegister(42).Should().BeTrue();
        guard.TryRegister(42).Should().BeTrue();
        guard.TryRegister(42).Should().BeTrue();
        guard.TryRegister(42).Should().BeFalse("the cap of 3 has been hit");
        guard.CurrentCount(42).Should().Be(3);
    }

    [Fact]
    public void TryRegister_refusal_does_not_record_a_new_entry()
    {
        // A rejected attempt must not push the window forward — otherwise the operator could
        // never exit the cap state by waiting (every refused call would extend the cap window).
        var now = DateTimeOffset.UnixEpoch;
        var guard = new AutoRotationGuard(maxRotations: 1, window: TimeSpan.FromMinutes(5), clock: () => now);

        guard.TryRegister(7).Should().BeTrue();
        guard.TryRegister(7).Should().BeFalse();
        guard.TryRegister(7).Should().BeFalse();
        guard.CurrentCount(7).Should().Be(1, "refused attempts do not add new timestamps");
    }

    [Fact]
    public void Window_rolls_so_aged_out_entries_no_longer_count()
    {
        var now = DateTimeOffset.UnixEpoch;
        var guard = new AutoRotationGuard(maxRotations: 2, window: TimeSpan.FromMinutes(5), clock: () => now);

        guard.TryRegister(9).Should().BeTrue();
        now += TimeSpan.FromMinutes(1);
        guard.TryRegister(9).Should().BeTrue();
        guard.TryRegister(9).Should().BeFalse("two in-window rotations is the cap");

        // Age out the FIRST timestamp by advancing past its window.
        now += TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(1);
        guard.CurrentCount(9).Should().Be(1, "first entry is now older than the 5-minute window");
        guard.TryRegister(9).Should().BeTrue("aged-out entries free the slot back up");
    }

    [Fact]
    public void Different_task_ids_have_independent_counters()
    {
        var now = DateTimeOffset.UnixEpoch;
        var guard = new AutoRotationGuard(maxRotations: 1, window: TimeSpan.FromMinutes(5), clock: () => now);

        guard.TryRegister(1).Should().BeTrue();
        guard.TryRegister(1).Should().BeFalse();
        guard.TryRegister(2).Should().BeTrue("task 2's counter is separate from task 1's");
    }

    [Fact]
    public void Constructor_validates_arguments()
    {
        FluentActions.Invoking(() => new AutoRotationGuard(0, TimeSpan.FromMinutes(5)))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new AutoRotationGuard(3, TimeSpan.Zero))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new AutoRotationGuard(3, TimeSpan.FromSeconds(-1)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
