using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Tasks;

public class TaskStoreTests
{
    [Fact]
    public async Task Open_creates_db_and_runs_migrations()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);

        var list = await store.ListAsync(TestContext.Current.CancellationToken);
        list.Should().BeEmpty();
    }

    [Fact]
    public async Task File_path_creates_parent_directories()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cp-tasks-{Guid.NewGuid():N}", "nested");
        var path = Path.Combine(dir, "infinicontext.db");
        try
        {
            await using (var store = await TaskStore.OpenAsync(path, TestContext.Current.CancellationToken))
            {
                Directory.Exists(dir).Should().BeTrue();
                File.Exists(path).Should().BeTrue();
            }
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task AllocateId_returns_sequential_ids_starting_at_1()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);

        var a = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        var b = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        var c = await store.AllocateIdAsync(TestContext.Current.CancellationToken);

        new[] { a, b, c }.Should().Equal(new[] { 1, 2, 3 });
    }

    [Fact]
    public async Task Create_inserts_record_and_Get_returns_it()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var id = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        var record = NewRecord(id, "Implement Phase 1", createdAt: 1_000, lastActive: 1_000);

        await store.CreateAsync(record, TestContext.Current.CancellationToken);
        var roundtrip = await store.GetAsync(id, TestContext.Current.CancellationToken);

        roundtrip.Should().Be(record);
    }

    [Fact]
    public async Task Create_round_trips_default_model()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var id = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        var record = NewRecord(id, "Pick a model") with { DefaultModel = "claude-fable-5" };

        await store.CreateAsync(record, TestContext.Current.CancellationToken);
        var roundtrip = await store.GetAsync(id, TestContext.Current.CancellationToken);

        roundtrip.Should().Be(record);
        roundtrip!.DefaultModel.Should().Be("claude-fable-5");
    }

    [Fact]
    public async Task Get_returns_null_for_unknown_id()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);

        var result = await store.GetAsync(999, TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Create_rejects_non_positive_id(int badId)
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var record = NewRecord(badId, "title");

        var act = () => store.CreateAsync(record);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Create_rejects_blank_title()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var id = await store.AllocateIdAsync(TestContext.Current.CancellationToken);

        var act = () => store.CreateAsync(NewRecord(id, ""));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Create_rejects_blank_home_cwd()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var id = await store.AllocateIdAsync(TestContext.Current.CancellationToken);

        var record = new TaskRecord(id, "  ", "title", 0, 0, TaskItemStatus.InProgress, null);
        var act = () => store.CreateAsync(record);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task List_orders_by_last_active_desc()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);

        var oldId = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        var newId = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        var middleId = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(oldId, "old", lastActive: 100), TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(newId, "new", lastActive: 300), TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(middleId, "middle", lastActive: 200), TestContext.Current.CancellationToken);

        var list = await store.ListAsync(TestContext.Current.CancellationToken);

        list.Select(r => r.Id).Should().Equal(new[] { newId, middleId, oldId });
    }

    [Fact]
    public async Task UpdateStatus_changes_status_and_bumps_last_active()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var id = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(id, "t", lastActive: 100), TestContext.Current.CancellationToken);

        var ok = await store.UpdateStatusAsync(id, TaskItemStatus.Idle, whenUnixMs: 500, TestContext.Current.CancellationToken);

        ok.Should().BeTrue();
        var after = await store.GetAsync(id, TestContext.Current.CancellationToken);
        after!.Status.Should().Be(TaskItemStatus.Idle);
        after.LastActiveAtUnixMs.Should().Be(500);
        after.CreatedAtUnixMs.Should().Be(1_000); // unchanged from NewRecord default
    }

    [Fact]
    public async Task UpdateStatus_returns_false_for_unknown_id()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);

        var ok = await store.UpdateStatusAsync(999, TaskItemStatus.Idle, whenUnixMs: 0, TestContext.Current.CancellationToken);

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task RecordFork_sets_parent_on_child()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var parentId = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        var childId = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(parentId, "parent"), TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(childId, "child"), TestContext.Current.CancellationToken);

        await store.RecordForkAsync(parentId, childId, TestContext.Current.CancellationToken);

        var child = await store.GetAsync(childId, TestContext.Current.CancellationToken);
        child!.ParentTaskId.Should().Be(parentId);
    }

    [Fact]
    public async Task RecordFork_is_idempotent_when_re_linking_same_parent()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var parentId = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        var childId = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(parentId, "parent"), TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(childId, "child"), TestContext.Current.CancellationToken);
        await store.RecordForkAsync(parentId, childId, TestContext.Current.CancellationToken);

        var act = () => store.RecordForkAsync(parentId, childId);

        await act.Should().NotThrowAsync();
        var child = await store.GetAsync(childId, TestContext.Current.CancellationToken);
        child!.ParentTaskId.Should().Be(parentId);
    }

    [Fact]
    public async Task RecordFork_rejects_already_set_to_different_parent()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var p1 = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        var p2 = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        var childId = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(p1, "p1"), TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(p2, "p2"), TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(childId, "child"), TestContext.Current.CancellationToken);
        await store.RecordForkAsync(p1, childId, TestContext.Current.CancellationToken);

        var act = () => store.RecordForkAsync(p2, childId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already has parent*");
    }

    [Fact]
    public async Task RecordFork_rejects_self_fork()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var id = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(id, "x"), TestContext.Current.CancellationToken);

        var act = () => store.RecordForkAsync(id, id);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RecordFork_rejects_missing_child()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var parentId = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(parentId, "p"), TestContext.Current.CancellationToken);

        var act = () => store.RecordForkAsync(parentId, 999);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Child task 999 not found*");
    }

    [Fact]
    public async Task RecordFork_rejects_missing_parent()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var childId = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(childId, "c"), TestContext.Current.CancellationToken);

        var act = () => store.RecordForkAsync(999, childId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Parent task 999 not found*");
    }

    [Fact]
    public async Task Delete_removes_row_and_returns_zero_orphans_for_childless_task()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var id = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(id, "solo"), TestContext.Current.CancellationToken);

        var orphaned = await store.DeleteAsync(id, TestContext.Current.CancellationToken);

        orphaned.Should().Be(0);
        (await store.GetAsync(id, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_returns_null_for_unknown_id()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);

        var orphaned = await store.DeleteAsync(999, TestContext.Current.CancellationToken);

        orphaned.Should().BeNull();
    }

    [Fact]
    public async Task Delete_nulls_parent_task_id_on_children_and_returns_count()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var parentId = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        var child1Id = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        var child2Id = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(parentId, "parent"), TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(child1Id, "c1"), TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(child2Id, "c2"), TestContext.Current.CancellationToken);
        await store.RecordForkAsync(parentId, child1Id, TestContext.Current.CancellationToken);
        await store.RecordForkAsync(parentId, child2Id, TestContext.Current.CancellationToken);

        var orphaned = await store.DeleteAsync(parentId, TestContext.Current.CancellationToken);

        orphaned.Should().Be(2);
        (await store.GetAsync(parentId, TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.GetAsync(child1Id, TestContext.Current.CancellationToken))!.ParentTaskId.Should().BeNull();
        (await store.GetAsync(child2Id, TestContext.Current.CancellationToken))!.ParentTaskId.Should().BeNull();
    }

    [Fact]
    public async Task Delete_of_child_does_not_touch_parent_or_siblings()
    {
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var parentId = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        var child1Id = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        var child2Id = await store.AllocateIdAsync(TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(parentId, "parent"), TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(child1Id, "c1"), TestContext.Current.CancellationToken);
        await store.CreateAsync(NewRecord(child2Id, "c2"), TestContext.Current.CancellationToken);
        await store.RecordForkAsync(parentId, child1Id, TestContext.Current.CancellationToken);
        await store.RecordForkAsync(parentId, child2Id, TestContext.Current.CancellationToken);

        var orphaned = await store.DeleteAsync(child1Id, TestContext.Current.CancellationToken);

        orphaned.Should().Be(0);
        (await store.GetAsync(parentId, TestContext.Current.CancellationToken)).Should().NotBeNull();
        (await store.GetAsync(child2Id, TestContext.Current.CancellationToken))!.ParentTaskId.Should().Be(parentId);
    }

    private static TaskRecord NewRecord(
        int id,
        string title,
        long createdAt = 1_000,
        long lastActive = 1_000,
        TaskItemStatus status = TaskItemStatus.InProgress,
        int? parentId = null)
        => new(id, "C:/work/repo", title, createdAt, lastActive, status, parentId);
}
