using InfiniContext.Core.Tasks;
using FluentAssertions;
using Serilog;

namespace InfiniContext.Tests.Tasks;

public class TaskActivityCoordinatorTests
{
    [Fact]
    public async Task Enter_marks_InProgress_with_clock_value()
    {
        await using var store = await TaskStore.OpenAsync(":memory:");
        var id = await SeedTaskAsync(store);
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        var coord = new TaskActivityCoordinator(store, clock.Now, Log.Logger);

        var result = await coord.EnterAsync(Guid.NewGuid(), id);

        result.Should().BeOfType<EnterTaskResult.GrantedResult>();
        var row = await store.GetAsync(id);
        row!.Status.Should().Be(TaskItemStatus.InProgress);
        row.LastActiveAtUnixMs.Should().Be(1_700_000_000_000);
    }

    [Fact]
    public async Task Leave_marks_Idle_with_clock_value()
    {
        await using var store = await TaskStore.OpenAsync(":memory:");
        var id = await SeedTaskAsync(store);
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_500_000));
        var coord = new TaskActivityCoordinator(store, clock.Now, Log.Logger);
        var session = Guid.NewGuid();
        await coord.EnterAsync(session, id);

        clock.Advance(TimeSpan.FromMinutes(5));
        await coord.LeaveAsync(session, id);

        var row = await store.GetAsync(id);
        row!.Status.Should().Be(TaskItemStatus.Idle);
        row.LastActiveAtUnixMs.Should().Be(clock.Now().ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Enter_refuses_when_another_session_holds_the_task()
    {
        await using var store = await TaskStore.OpenAsync(":memory:");
        var id = await SeedTaskAsync(store);
        var coord = new TaskActivityCoordinator(store, () => DateTimeOffset.UtcNow, Log.Logger);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await coord.EnterAsync(first, id);

        var result = await coord.EnterAsync(second, id);

        result.Should().BeOfType<EnterTaskResult.RefusedResult>()
            .Which.Should().BeEquivalentTo(new { TaskId = id, HolderSessionId = first });
    }

    [Fact]
    public async Task Enter_is_idempotent_for_the_same_session()
    {
        await using var store = await TaskStore.OpenAsync(":memory:");
        var id = await SeedTaskAsync(store);
        var coord = new TaskActivityCoordinator(store, () => DateTimeOffset.UtcNow, Log.Logger);
        var session = Guid.NewGuid();

        var first = await coord.EnterAsync(session, id);
        var second = await coord.EnterAsync(session, id);

        first.Should().BeOfType<EnterTaskResult.GrantedResult>();
        second.Should().BeOfType<EnterTaskResult.GrantedResult>("re-entering with the same session id must not be refused");
    }

    [Fact]
    public async Task Leave_releases_claim_allowing_another_session_to_enter()
    {
        await using var store = await TaskStore.OpenAsync(":memory:");
        var id = await SeedTaskAsync(store);
        var coord = new TaskActivityCoordinator(store, () => DateTimeOffset.UtcNow, Log.Logger);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await coord.EnterAsync(first, id);
        await coord.LeaveAsync(first, id);

        var result = await coord.EnterAsync(second, id);

        result.Should().BeOfType<EnterTaskResult.GrantedResult>();
    }

    [Fact]
    public async Task Leave_by_a_non_holder_does_not_drop_the_claim()
    {
        // Matches the prior outer-loop semantics: Idle writes flush unconditionally, but the
        // in-memory claim only releases when the holder leaves. This guards against a stray
        // OnExit on session B clearing session A's hold on the task.
        await using var store = await TaskStore.OpenAsync(":memory:");
        var id = await SeedTaskAsync(store);
        var coord = new TaskActivityCoordinator(store, () => DateTimeOffset.UtcNow, Log.Logger);
        var holder = Guid.NewGuid();
        var other = Guid.NewGuid();
        await coord.EnterAsync(holder, id);

        await coord.LeaveAsync(other, id);
        var reenter = await coord.EnterAsync(other, id);

        reenter.Should().BeOfType<EnterTaskResult.RefusedResult>()
            .Which.HolderSessionId.Should().Be(holder);
    }

    [Fact]
    public async Task Enter_does_not_throw_when_task_row_is_missing()
    {
        // TaskStore.UpdateStatusAsync returns false (rather than throwing) when the id is
        // unknown. The coordinator should not propagate that to the caller — the original
        // outer-loop just logged and continued.
        await using var store = await TaskStore.OpenAsync(":memory:");
        var coord = new TaskActivityCoordinator(store, () => DateTimeOffset.UtcNow, Log.Logger);

        var result = await coord.EnterAsync(Guid.NewGuid(), taskId: 9_999);

        result.Should().BeOfType<EnterTaskResult.GrantedResult>();
    }

    private static async Task<int> SeedTaskAsync(TaskStore store)
    {
        var id = await store.AllocateIdAsync();
        await store.CreateAsync(new TaskRecord(
            Id: id,
            HomeCwd: "/tmp/repo",
            Title: $"seeded task {id}",
            CreatedAtUnixMs: 0,
            LastActiveAtUnixMs: 0,
            Status: TaskItemStatus.Idle,
            ParentTaskId: null));
        return id;
    }

    private sealed class FakeClock
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset initial) { _now = initial; }
        public DateTimeOffset Now() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
