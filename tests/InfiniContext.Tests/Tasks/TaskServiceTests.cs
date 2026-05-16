using System.Text.Json.Nodes;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Tasks;

public class TaskServiceTests
{
    [Fact]
    public async Task Create_calls_light_model_and_persists_row_and_folder()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("Implement Phase 3");

        var record = await fixture.Service.CreateAsync(fixture.HomeCwd, "We need to wire TaskStore + TaskFolder + light model into a service.");

        record.Id.Should().Be(1);
        record.Title.Should().Be("Implement Phase 3");
        record.HomeCwd.Should().Be(fixture.HomeCwd);
        record.Status.Should().Be(TaskItemStatus.InProgress);
        record.ParentTaskId.Should().BeNull();
        record.CreatedAtUnixMs.Should().Be(fixture.Clock.UtcNow.ToUnixTimeMilliseconds());

        // Folder scaffolds + description override exist on disk.
        var folder = new TaskFolder(fixture.HomeCwd, record.Id, Slugify.For(record.Title));
        Directory.Exists(folder.Path).Should().BeTrue();
        var desc = await File.ReadAllTextAsync(Path.Combine(folder.Path, "description.md"));
        desc.Should().Contain("# Implement Phase 3").And.Contain("wire TaskStore + TaskFolder");

        // Row round-trips from the store.
        var roundtrip = await fixture.Store.GetAsync(record.Id);
        roundtrip.Should().Be(record);

        fixture.LightModel.Calls.Should().ContainSingle()
            .Which.Description.Should().Contain("wire TaskStore");
    }

    [Fact]
    public async Task Create_sanitizes_quoted_or_overlong_light_model_titles()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("\"Implement   Phase\u00a03\"   \n");

        var record = await fixture.Service.CreateAsync(fixture.HomeCwd, "anything");

        record.Title.Should().Be("Implement   Phase\u00a03"); // quotes + trailing whitespace gone
        // Slug strips all whitespace consistently regardless of input internal whitespace.
        Slugify.For(record.Title).Should().Be("implement-phase-3");
    }

    [Fact]
    public async Task Create_falls_back_to_deterministic_title_when_light_model_refuses()
    {
        // Repro for the 2026-05-16 bug: a /task new whose description referenced local files
        // (which the light model cannot read) had the model's refusal text stored verbatim as
        // the task title. The description itself must still be persisted to description.md so
        // the /task switch session can act on the references.
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("I don't have access to read files from your local filesystem. I cannot view the documents you've referenced to plan phase 3.");

        const string description = "Review @docs/foo.md and @docs/bar.md to plan phase 3 of the migration.";
        var record = await fixture.Service.CreateAsync(fixture.HomeCwd, description);

        record.Title.Should().NotContain("I don't have access");
        record.Title.Should().Be("Review @docs/foo.md and @docs/bar.md to plan");

        var folder = new TaskFolder(fixture.HomeCwd, record.Id, Slugify.For(record.Title));
        var desc = await File.ReadAllTextAsync(Path.Combine(folder.Path, "description.md"));
        desc.Should().Contain("@docs/foo.md").And.Contain("@docs/bar.md");
    }

    [Theory]
    [InlineData("I don't have access to /etc/passwd")]
    [InlineData("I cannot read those files")]
    [InlineData("I can't fulfill that request")]
    [InlineData("I'm unable to help with this")]
    [InlineData("I'm sorry, but I cannot")]
    [InlineData("Sorry, I cannot read local files")]
    [InlineData("Unfortunately, I have no file access")]
    public void LooksLikeRefusal_recognizes_common_refusal_openers(string title)
    {
        TaskService.LooksLikeRefusal(title).Should().BeTrue();
    }

    [Theory]
    [InlineData("Implement Phase 3")]
    [InlineData("Fix the parser drift counter")]
    [InlineData("")]
    [InlineData("   ")]
    public void LooksLikeRefusal_returns_false_for_normal_titles(string title)
    {
        TaskService.LooksLikeRefusal(title).Should().BeFalse();
    }

    [Theory]
    [InlineData("Review docs/foo.md and plan phase 3 of the migration.", "Review docs/foo.md and plan phase 3")]
    [InlineData("One sentence only", "One sentence only")]
    [InlineData("First line\nSecond line ignored", "First line")]
    [InlineData("", "Untitled task")]
    [InlineData("   ", "Untitled task")]
    public void DeriveFallbackTitle_clamps_to_first_six_words_of_first_line(string description, string expected)
    {
        TaskService.DeriveFallbackTitle(description).Should().Be(expected);
    }

    [Fact]
    public async Task Create_rejects_blank_inputs()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();

        var act1 = () => fixture.Service.CreateAsync("", "ok");
        var act2 = () => fixture.Service.CreateAsync(fixture.HomeCwd, "");

        await act1.Should().ThrowAsync<ArgumentException>();
        await act2.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Fork_defaults_to_appended_2_and_increments_on_repeat()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("Implement foo");
        var parent = await fixture.Service.CreateAsync(fixture.HomeCwd, "do the foo work");

        var fork1 = await fixture.Service.ForkAsync(parent.Id);
        var fork2 = await fixture.Service.ForkAsync(fork1.Id);

        fork1.Title.Should().Be("Implement foo (2)");
        fork2.Title.Should().Be("Implement foo (3)");
        fork1.ParentTaskId.Should().Be(parent.Id);
        fork2.ParentTaskId.Should().Be(fork1.Id);
    }

    [Fact]
    public async Task Fork_with_override_title_honors_caller()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("Implement foo");
        var parent = await fixture.Service.CreateAsync(fixture.HomeCwd, "do the foo work");

        var fork = await fixture.Service.ForkAsync(parent.Id, overrideTitle: "Spike: alternative approach");

        fork.Title.Should().Be("Spike: alternative approach");
        fork.ParentTaskId.Should().Be(parent.Id);
    }

    [Fact]
    public async Task Fork_copies_artifacts_from_source_to_child()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("Source task");
        var parent = await fixture.Service.CreateAsync(fixture.HomeCwd, "...");
        // Add a custom artifact into the source folder so we can verify it's copied.
        var parentFolder = new TaskFolder(fixture.HomeCwd, parent.Id, Slugify.For(parent.Title));
        await File.WriteAllTextAsync(Path.Combine(parentFolder.Path, "research.md"), "investigation notes");

        var fork = await fixture.Service.ForkAsync(parent.Id);

        var forkFolder = new TaskFolder(fixture.HomeCwd, fork.Id, Slugify.For(fork.Title));
        File.Exists(Path.Combine(forkFolder.Path, "research.md")).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(forkFolder.Path, "research.md"))).Should().Be("investigation notes");
    }

    [Fact]
    public async Task Fork_throws_when_source_missing()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();

        var act = () => fixture.Service.ForkAsync(999);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Source task 999 not found*");
    }

    [Fact]
    public async Task Merge_concat_combines_artifacts_under_section_dividers()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("Alpha");
        fixture.LightModel.QueueTitle("Bravo");
        var a = await fixture.Service.CreateAsync(fixture.HomeCwd, "alpha work");
        var b = await fixture.Service.CreateAsync(fixture.HomeCwd, "bravo work");
        // Mutate progress.md on each so we can see concatenation.
        var aFolder = new TaskFolder(fixture.HomeCwd, a.Id, Slugify.For(a.Title));
        var bFolder = new TaskFolder(fixture.HomeCwd, b.Id, Slugify.For(b.Title));
        await File.WriteAllTextAsync(Path.Combine(aFolder.Path, "progress.md"), "alpha progress");
        await File.WriteAllTextAsync(Path.Combine(bFolder.Path, "progress.md"), "bravo progress");

        var merged = await fixture.Service.MergeAsync(new[] { a.Id, b.Id }, MergeTier.DeterministicConcat);

        merged.ParentTaskId.Should().BeNull(); // multi-parent post-MVP
        merged.Title.Should().StartWith("Merge of ");
        var mergedFolder = new TaskFolder(fixture.HomeCwd, merged.Id, Slugify.For(merged.Title));
        var progress = await File.ReadAllTextAsync(Path.Combine(mergedFolder.Path, "progress.md"));
        progress.Should()
            .Contain("merged from 2 source tasks")
            .And.Contain($"From task {a.Id:D4}").And.Contain("alpha progress")
            .And.Contain($"From task {b.Id:D4}").And.Contain("bravo progress");

        // Index lists merged files (description, progress) and skips itself.
        var index = await File.ReadAllTextAsync(Path.Combine(mergedFolder.Path, "index.md"));
        index.Should().Contain("description.md").And.Contain("progress.md");
        index.Should().NotContain("| index.md |");
    }

    [Theory]
    [InlineData(MergeTier.LightModel)]
    [InlineData(MergeTier.FullModelSession)]
    public async Task Merge_throws_NotImplementedException_for_post_MVP_tiers(MergeTier tier)
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("a");
        fixture.LightModel.QueueTitle("b");
        var a = await fixture.Service.CreateAsync(fixture.HomeCwd, "x");
        var b = await fixture.Service.CreateAsync(fixture.HomeCwd, "y");

        var act = () => fixture.Service.MergeAsync(new[] { a.Id, b.Id }, tier);

        await act.Should().ThrowAsync<NotImplementedException>();
    }

    [Fact]
    public async Task Merge_requires_at_least_two_sources()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("only");
        var a = await fixture.Service.CreateAsync(fixture.HomeCwd, "x");

        var act = () => fixture.Service.MergeAsync(new[] { a.Id }, MergeTier.DeterministicConcat);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*at least two*");
    }

    [Fact]
    public async Task Merge_rejects_cross_home_cwd_sources()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("here");
        var here = await fixture.Service.CreateAsync(fixture.HomeCwd, "x");
        // Insert a record with a different home_cwd directly through the store so we don't
        // need to scaffold an extra temp dir.
        var elsewhereCwd = Path.Combine(fixture.TempPath, "other-repo");
        Directory.CreateDirectory(elsewhereCwd);
        var elsewhereId = await fixture.Store.AllocateIdAsync();
        var elsewhere = new TaskRecord(elsewhereId, elsewhereCwd, "Elsewhere", 0, 0, TaskItemStatus.InProgress, null);
        await fixture.Store.CreateAsync(elsewhere);

        var act = () => fixture.Service.MergeAsync(new[] { here.Id, elsewhere.Id }, MergeTier.DeterministicConcat);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*home_cwd*");
    }

    [Fact]
    public async Task List_orders_by_last_active_desc_and_filters_by_status_and_limit()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("first");
        fixture.LightModel.QueueTitle("second");
        fixture.LightModel.QueueTitle("third");

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var first = await fixture.Service.CreateAsync(fixture.HomeCwd, "1");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await fixture.Service.CreateAsync(fixture.HomeCwd, "2");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var third = await fixture.Service.CreateAsync(fixture.HomeCwd, "3");

        // Mark first as Idle so the status filter has something to remove.
        await fixture.Store.UpdateStatusAsync(first.Id, TaskItemStatus.Idle, fixture.Clock.UtcNow.ToUnixTimeMilliseconds() - 60_000);

        var all = await fixture.Service.ListAsync();
        all.Select(r => r.Id).Should().Equal(new[] { third.Id, second.Id, first.Id });

        var inProgress = await fixture.Service.ListAsync(new ListFilter(Status: TaskItemStatus.InProgress));
        inProgress.Select(r => r.Id).Should().Equal(new[] { third.Id, second.Id });

        var top = await fixture.Service.ListAsync(new ListFilter(Limit: 2));
        top.Select(r => r.Id).Should().Equal(new[] { third.Id, second.Id });
    }

    [Fact]
    public async Task Get_returns_record_plus_artifact_snapshot()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("only");
        var rec = await fixture.Service.CreateAsync(fixture.HomeCwd, "x");

        var details = await fixture.Service.GetAsync(rec.Id);

        details.Should().NotBeNull();
        details!.Record.Should().Be(rec);
        details.Artifacts.Select(a => a.Name)
            .Should().BeEquivalentTo(new[] { "description.md", "index.md", "progress.md" });
    }

    [Fact]
    public async Task Get_returns_null_for_unknown_id()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();

        var details = await fixture.Service.GetAsync(999);

        details.Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\src\repo\bin\Debug\net10.0")]
    [InlineData(@"C:\src\repo\bin\Release")]
    [InlineData(@"C:\src\repo\obj")]
    [InlineData(@"C:\src\repo\obj\Debug")]
    [InlineData("/home/user/proj/bin/Debug/net10.0")]
    [InlineData("/home/user/proj/obj/foo")]
    public void LooksLikeBuildOutput_flags_known_build_dirs(string path)
    {
        TaskService.LooksLikeBuildOutput(path).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\src\repo")]
    [InlineData(@"C:\src\objections\foo")]   // "obj" inside another word, not a segment
    [InlineData(@"C:\src\binoculars\foo")]   // "bin" inside another word
    [InlineData(@"C:\src\repo\bin")]         // bin alone (not paired with Debug/Release)
    [InlineData("/home/user/proj")]
    [InlineData("")]
    public void LooksLikeBuildOutput_passes_legitimate_paths(string path)
    {
        TaskService.LooksLikeBuildOutput(path).Should().BeFalse();
    }

    [Fact]
    public async Task Create_refuses_bin_debug_home()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        var binPath = Path.Combine(fixture.HomeCwd, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(binPath);

        var act = () => fixture.Service.CreateAsync(binPath, "do thing");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*build-output*bin/Debug*");
        // Light model must NOT have been called — the check fires before the API turn.
        fixture.LightModel.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_refuses_obj_home()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        var objPath = Path.Combine(fixture.HomeCwd, "obj", "intermediate");
        Directory.CreateDirectory(objPath);

        var act = () => fixture.Service.CreateAsync(objPath, "do thing");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*build-output*");
    }

    [Fact]
    public async Task PrepareSwitch_throws_when_home_cwd_no_longer_exists()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("Wandering home");
        var record = await fixture.Service.CreateAsync(fixture.HomeCwd, "wander");
        // Simulate the home folder disappearing (rebuild wipe, manual rm, etc.).
        Directory.Delete(fixture.HomeCwd, recursive: true);

        var act = () => fixture.Service.PrepareSwitchAsync(record.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{record.Id:D4}*home_cwd*no longer exists*/task delete*");
    }

    [Fact]
    public async Task PrepareSwitch_throws_when_task_folder_no_longer_exists()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("Ghost task");
        var record = await fixture.Service.CreateAsync(fixture.HomeCwd, "ghosted");
        // home_cwd still exists, but the task folder underneath got wiped.
        var folder = new TaskFolder(fixture.HomeCwd, record.Id, Slugify.For(record.Title));
        Directory.Delete(folder.Path, recursive: true);

        var act = () => fixture.Service.PrepareSwitchAsync(record.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{record.Id:D4}*scaffolded under*no longer exists*/task delete*");
    }

    [Fact]
    public async Task PrepareSwitch_returns_null_for_unknown_id()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();

        var plan = await fixture.Service.PrepareSwitchAsync(999);

        plan.Should().BeNull();
    }

    [Fact]
    public async Task PrepareSwitch_for_clean_task_omits_drift_note()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("Implement switch");
        var record = await fixture.Service.CreateAsync(fixture.HomeCwd, "build the switch flow");

        var plan = await fixture.Service.PrepareSwitchAsync(record.Id);

        plan.Should().NotBeNull();
        plan!.Task.Id.Should().Be(record.Id);
        plan.Slug.Should().Be("implement-switch");
        plan.Drift.Should().BeEmpty();
        plan.FirstUserMessage.Should().Contain($"task {record.Id:D4}").And.Contain("\"Implement switch\"");
        plan.FirstUserMessage.Should().Contain(".task/0001-implement-switch/");
        plan.FirstUserMessage.Should().Contain("infinicontext-task skill");
        plan.FirstUserMessage.Should().NotContain("Note:");
    }

    [Fact]
    public async Task PrepareSwitch_includes_drift_files_when_index_is_stale()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("Drifty task");
        var record = await fixture.Service.CreateAsync(fixture.HomeCwd, "introduce drift");

        // Drop a file on disk that the scaffolded index.md doesn't mention.
        var folder = new TaskFolder(fixture.HomeCwd, record.Id, Slugify.For(record.Title));
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "research.md"), "# notes");

        var plan = await fixture.Service.PrepareSwitchAsync(record.Id);

        plan.Should().NotBeNull();
        plan!.Drift.Should().Equal("research.md");
        plan.FirstUserMessage.Should().StartWith("Note: research.md exist on disk but are missing from index.md");
        plan.FirstUserMessage.Should().Contain("Working on task 0001");
    }

    [Fact]
    public async Task Delete_returns_null_for_unknown_id()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();

        var result = await fixture.Service.DeleteAsync(999, removeFolder: false);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Delete_refuses_InProgress_task()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("active");
        var rec = await fixture.Service.CreateAsync(fixture.HomeCwd, "x");
        // CreateAsync sets status=InProgress; do not flip to Idle.

        var act = () => fixture.Service.DeleteAsync(rec.Id, removeFolder: false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{rec.Id:D4}*InProgress*");
        (await fixture.Store.GetAsync(rec.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_with_removeFolder_false_drops_row_but_leaves_folder()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("to delete");
        var rec = await fixture.Service.CreateAsync(fixture.HomeCwd, "delete me");
        await fixture.Store.UpdateStatusAsync(rec.Id, TaskItemStatus.Idle, fixture.Clock.UtcNow.ToUnixTimeMilliseconds());
        var folder = new TaskFolder(fixture.HomeCwd, rec.Id, Slugify.For(rec.Title));

        var result = await fixture.Service.DeleteAsync(rec.Id, removeFolder: false);

        result.Should().NotBeNull();
        result!.Record.Id.Should().Be(rec.Id);
        result.FolderPath.Should().Be(folder.Path);
        result.FolderExisted.Should().BeTrue();
        result.FolderRemoved.Should().BeFalse();
        result.OrphanedChildren.Should().Be(0);
        (await fixture.Store.GetAsync(rec.Id)).Should().BeNull();
        Directory.Exists(folder.Path).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_with_removeFolder_true_drops_row_and_folder()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("to nuke");
        var rec = await fixture.Service.CreateAsync(fixture.HomeCwd, "nuke me");
        await fixture.Store.UpdateStatusAsync(rec.Id, TaskItemStatus.Idle, fixture.Clock.UtcNow.ToUnixTimeMilliseconds());
        var folder = new TaskFolder(fixture.HomeCwd, rec.Id, Slugify.For(rec.Title));

        var result = await fixture.Service.DeleteAsync(rec.Id, removeFolder: true);

        result.Should().NotBeNull();
        result!.FolderExisted.Should().BeTrue();
        result.FolderRemoved.Should().BeTrue();
        Directory.Exists(folder.Path).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_with_removeFolder_true_is_no_op_when_folder_already_gone()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("ghost row");
        var rec = await fixture.Service.CreateAsync(fixture.HomeCwd, "vanish");
        await fixture.Store.UpdateStatusAsync(rec.Id, TaskItemStatus.Idle, fixture.Clock.UtcNow.ToUnixTimeMilliseconds());
        var folder = new TaskFolder(fixture.HomeCwd, rec.Id, Slugify.For(rec.Title));
        // Simulate the stale-data scenario from the 2026-05-09 manual-testing session:
        // a row whose home_cwd folder got wiped (e.g. a bin/Debug output dir was the home).
        Directory.Delete(folder.Path, recursive: true);

        var result = await fixture.Service.DeleteAsync(rec.Id, removeFolder: true);

        result.Should().NotBeNull();
        result!.FolderExisted.Should().BeFalse();
        result.FolderRemoved.Should().BeFalse();
        (await fixture.Store.GetAsync(rec.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_orphans_children_and_reports_count()
    {
        await using var fixture = await TaskServiceFixture.OpenAsync();
        fixture.LightModel.QueueTitle("parent");
        var parent = await fixture.Service.CreateAsync(fixture.HomeCwd, "p");
        var child1 = await fixture.Service.ForkAsync(parent.Id);
        var child2 = await fixture.Service.ForkAsync(parent.Id);
        await fixture.Store.UpdateStatusAsync(parent.Id, TaskItemStatus.Idle, fixture.Clock.UtcNow.ToUnixTimeMilliseconds());

        var result = await fixture.Service.DeleteAsync(parent.Id, removeFolder: false);

        result.Should().NotBeNull();
        result!.OrphanedChildren.Should().Be(2);
        (await fixture.Store.GetAsync(child1.Id))!.ParentTaskId.Should().BeNull();
        (await fixture.Store.GetAsync(child2.Id))!.ParentTaskId.Should().BeNull();
    }

    private sealed class TaskServiceFixture : IAsyncDisposable
    {
        public required TaskStore Store { get; init; }
        public required TaskService Service { get; init; }
        public required QueueLightModel LightModel { get; init; }
        public required FakeClock Clock { get; init; }
        public required string TempPath { get; init; }
        public required string HomeCwd { get; init; }

        public static async Task<TaskServiceFixture> OpenAsync()
        {
            var temp = Path.Combine(Path.GetTempPath(), $"cp-tasksvc-{Guid.NewGuid():N}");
            var home = Path.Combine(temp, "repo");
            Directory.CreateDirectory(home);

            var store = await TaskStore.OpenAsync(":memory:");
            var lightModel = new QueueLightModel();
            var clock = new FakeClock(new DateTimeOffset(2026, 5, 7, 10, 0, 0, TimeSpan.Zero));
            var service = new TaskService(store, lightModel, () => clock.UtcNow);

            return new TaskServiceFixture
            {
                Store = store,
                Service = service,
                LightModel = lightModel,
                Clock = clock,
                TempPath = temp,
                HomeCwd = home,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            try { Directory.Delete(TempPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class FakeClock
    {
        public FakeClock(DateTimeOffset start) { UtcNow = start; }
        public DateTimeOffset UtcNow { get; private set; }
        public void Advance(TimeSpan span) => UtcNow += span;
    }

    /// <summary>
    /// Stub <see cref="ILightModelService"/> that hands out queued canned titles. Records every
    /// call so tests can assert which descriptions were passed in. Other interface methods are
    /// not exercised by <c>TaskService</c> and intentionally throw if called.
    /// </summary>
    private sealed class QueueLightModel : ILightModelService
    {
        private readonly Queue<string> _titles = new();
        public List<TitleCall> Calls { get; } = new();

        public void QueueTitle(string title) => _titles.Enqueue(title);

        public Task<string> GenerateShortTitleAsync(string description, int maxWords = 6, CancellationToken cancellationToken = default)
        {
            Calls.Add(new TitleCall(description, maxWords));
            if (_titles.Count == 0) throw new InvalidOperationException("No queued title for this call.");
            return Task.FromResult(_titles.Dequeue());
        }

        public Task<string> SummarizeAsync(string text, int maxSentences = 2, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not used by TaskService.");

        public Task<CategoryResult> CategorizeAsync(string text, IReadOnlyList<string> categories, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not used by TaskService.");

        public Task<SafetyReview> ReviewToolCallSafetyAsync(SafetyReviewRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not used by TaskService.");

        public sealed record TitleCall(string Description, int MaxWords);
    }
}
