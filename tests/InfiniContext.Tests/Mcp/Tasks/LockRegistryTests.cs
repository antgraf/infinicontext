using InfiniContext.Core.Mcp.Tasks;
using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Mcp.Tasks;

/// <summary>
/// Unit tests for the host-side <see cref="LockRegistry"/>. Most tests use the system clock with
/// a tiny poll interval (5ms) and short max-waits (50–100ms) so wall time stays bounded. The
/// TTL-expiry test uses a controllable <see cref="FakeClock"/> to trigger expiry without
/// sleeping for the full TTL.
/// </summary>
public class LockRegistryTests
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Acquire_on_free_file_returns_granted_token()
    {
        var registry = NewRegistry();

        var outcome = await registry.AcquireAsync("a.md", "owner-1", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        outcome.Ok.Should().BeTrue();
        outcome.Token.Should().NotBeNullOrEmpty().And.StartWith("lt_");
        outcome.TtlGranted.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Acquire_when_held_by_other_owner_with_no_release_times_out()
    {
        var registry = NewRegistry();
        var first = await registry.AcquireAsync("a.md", "owner-1", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        first.Ok.Should().BeTrue();

        var second = await registry.AcquireAsync("a.md", "owner-2", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(80), TestContext.Current.CancellationToken);

        second.Ok.Should().BeFalse();
        second.Error.Should().Contain("timeout");
    }

    [Fact]
    public async Task Acquire_after_release_succeeds()
    {
        var registry = NewRegistry();
        var first = await registry.AcquireAsync("a.md", "owner-1", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        registry.Release(first.Token!).Should().BeTrue();

        var second = await registry.AcquireAsync("a.md", "owner-2", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        second.Ok.Should().BeTrue();
        second.Token.Should().NotBe(first.Token);
    }

    [Fact]
    public async Task Release_returns_false_for_unknown_or_already_released_token()
    {
        var registry = NewRegistry();
        var outcome = await registry.AcquireAsync("a.md", "owner-1", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        registry.Release(outcome.Token!).Should().BeTrue();
        registry.Release(outcome.Token!).Should().BeFalse("second release of the same token");
        registry.Release("lt_does_not_exist").Should().BeFalse();
    }

    [Fact]
    public async Task Owner_checked_release_denies_a_caller_that_does_not_own_the_token()
    {
        var registry = NewRegistry();
        var outcome = await registry.AcquireAsync("a.md", "owner-A", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        // M2: a caller claiming a different owner cannot release the lock, even with the real token.
        registry.Release(outcome.Token!, "owner-B").Should().BeFalse("a non-owner must not release the lock");
        registry.Snapshot().Should().ContainSingle(e => e.FilePath == "a.md", "the lock must survive the denied release");

        // The legitimate owner still can.
        registry.Release(outcome.Token!, "owner-A").Should().BeTrue();
        registry.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Ttl_expiry_lets_the_next_acquire_win()
    {
        // FakeClock — no Task.Delay-driven contention path needed because the second acquire
        // observes an expired entry on its first iteration and takes the lock immediately.
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 7, 10, 0, 0, TimeSpan.Zero));
        var registry = new LockRegistry(clock: () => clock.UtcNow, pollInterval: FastPoll);
        await registry.AcquireAsync("a.md", "owner-1", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(31)); // strictly past expiry

        var second = await registry.AcquireAsync("a.md", "owner-2", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        second.Ok.Should().BeTrue();
        registry.Snapshot().Should().ContainSingle().Which.OwnerId.Should().Be("owner-2");
    }

    [Fact]
    public async Task ReleaseAllOwnedBy_drops_only_that_owners_locks()
    {
        var registry = NewRegistry();
        var aOutcome = await registry.AcquireAsync("a.md", "owner-1", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        var bOutcome = await registry.AcquireAsync("b.md", "owner-2", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        aOutcome.Ok.Should().BeTrue();
        bOutcome.Ok.Should().BeTrue();

        var released = registry.ReleaseAllOwnedBy("owner-1");

        released.Should().Be(1);
        var snap = registry.Snapshot();
        snap.Should().ContainSingle();
        snap[0].OwnerId.Should().Be("owner-2");
        snap[0].FilePath.Should().Be("b.md");
    }

    [Fact]
    public async Task Different_files_can_be_locked_concurrently()
    {
        var registry = NewRegistry();
        var t1 = registry.AcquireAsync("a.md", "owner-1", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        var t2 = registry.AcquireAsync("b.md", "owner-2", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(t1, t2).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        results.Should().AllSatisfy(o => o.Ok.Should().BeTrue());
        registry.Snapshot().Should().HaveCount(2);
    }

    [Fact]
    public async Task Contended_waiter_unblocks_after_release()
    {
        var registry = NewRegistry();
        var first = await registry.AcquireAsync("a.md", "owner-1", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        first.Ok.Should().BeTrue();

        var waiter = registry.AcquireAsync("a.md", "owner-2", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        // Yield until the waiter has had several scheduling opportunities. If it's still not
        // completed after that, it's genuinely in the poll loop's `Task.Delay` — which is what
        // makes this a contention test (rather than a happy-path "lock was already free" race).
        for (var i = 0; i < 100 && !waiter.IsCompleted; i++)
            await Task.Yield();
        waiter.IsCompleted.Should().BeFalse("waiter must still be blocked while owner-1 holds the lock");

        registry.Release(first.Token!).Should().BeTrue();

        var outcome = await waiter.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        outcome.Ok.Should().BeTrue();
        outcome.Token.Should().NotBe(first.Token);
    }

    [Fact]
    public void Acquire_validates_inputs()
    {
        var registry = NewRegistry();

        FluentActions.Invoking(() => registry.AcquireAsync("", "owner", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
            .Should().ThrowAsync<ArgumentException>();
        FluentActions.Invoking(() => registry.AcquireAsync("file", "", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
            .Should().ThrowAsync<ArgumentException>();
        FluentActions.Invoking(() => registry.AcquireAsync("file", "owner", TimeSpan.Zero, TimeSpan.FromSeconds(1)))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => registry.AcquireAsync("file", "owner", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(-1)))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static LockRegistry NewRegistry() => new(pollInterval: FastPoll);

    private sealed class FakeClock
    {
        public FakeClock(DateTimeOffset start) { UtcNow = start; }
        public DateTimeOffset UtcNow { get; private set; }
        public void Advance(TimeSpan span) => UtcNow += span;
    }
}
