using InfiniContext.Core.Tasks;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Mcp.Tasks;
using InfiniContext.Core.Review;
using FluentAssertions;

namespace InfiniContext.Tests.Cli.Tasks;

/// <summary>
/// Tests for <see cref="TaskCommandHandler.HandleReviewAsync"/>. Uses a real
/// <see cref="TaskService"/> against an in-memory <see cref="TaskStore"/> + temp filesystem (so
/// task records and folders exist on disk for the handler to find), plus a fake
/// <see cref="IReviewerAgentService"/> so the real <see cref="ReviewService"/> orchestrator runs
/// end-to-end without spawning <c>claude</c>.
/// </summary>
public class TaskCommandHandlerReviewTests
{
    [Fact]
    public async Task Refuses_when_review_subsystem_is_unavailable()
    {
        await using var fx = await Fixture.OpenAsync(withReview: false);
        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: 1, Scope: null, SkipConfirm: true), activeTaskId: null, CancellationToken.None);

        fx.Captured.Should().Contain("[task review] subsystem is unavailable");
        fx.Reviewer.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_when_no_id_and_no_active_task()
    {
        await using var fx = await Fixture.OpenAsync(withReview: true);
        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: null, Scope: null, SkipConfirm: true), activeTaskId: null, CancellationToken.None);

        fx.Captured.Should().Contain("no task id given and no active task");
        fx.Reviewer.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_when_task_id_does_not_exist()
    {
        await using var fx = await Fixture.OpenAsync(withReview: true);
        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: 999, Scope: null, SkipConfirm: true), activeTaskId: null, CancellationToken.None);

        fx.Captured.Should().Contain("0999 not found");
    }

    [Fact]
    public async Task Refuses_when_home_cwd_does_not_exist_on_disk()
    {
        await using var fx = await Fixture.OpenAsync(withReview: true);
        var task = await fx.CreateTaskAsync("Test task");

        // Wipe the home dir between creation and the review call.
        Directory.Delete(fx.HomeCwd, recursive: true);

        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: task.Id, Scope: null, SkipConfirm: true), activeTaskId: null, CancellationToken.None);

        fx.Captured.Should().Contain("home_cwd does not exist on disk");
    }

    [Fact]
    public async Task Uses_active_task_id_when_cmd_id_is_null()
    {
        await using var fx = await Fixture.OpenAsync(withReview: true);
        var task = await fx.CreateTaskAsync("Test task");
        fx.Reviewer.QueueAllReviewersEmpty();
        fx.Reviewer.QueueCoordinator(CoordinatorEmptyJson());

        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: null, Scope: null, SkipConfirm: true), activeTaskId: task.Id, CancellationToken.None);

        fx.Captured.Should().Contain($"{task.Id:D4}");
        fx.Reviewer.Calls.Should().HaveCount(6); // 5 reviewers + 1 coordinator
    }

    [Fact]
    public async Task Refuses_when_no_input_reader_and_no_yes_flag()
    {
        await using var fx = await Fixture.OpenAsync(withReview: true, input: null);
        var task = await fx.CreateTaskAsync("Test task");

        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: task.Id, Scope: null, SkipConfirm: false), activeTaskId: null, CancellationToken.None);

        fx.Captured.Should().Contain("Continue? [y/N]");
        fx.Captured.Should().Contain("pass --yes to skip the prompt");
        fx.Reviewer.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancels_when_user_answers_n()
    {
        await using var fx = await Fixture.OpenAsync(withReview: true, input: new StringReader("n\n"));
        var task = await fx.CreateTaskAsync("Test task");

        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: task.Id, Scope: null, SkipConfirm: false), activeTaskId: null, CancellationToken.None);

        fx.Captured.Should().Contain("[task review] cancelled");
        fx.Reviewer.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancels_when_user_answers_empty_line()
    {
        await using var fx = await Fixture.OpenAsync(withReview: true, input: new StringReader("\n"));
        var task = await fx.CreateTaskAsync("Test task");

        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: task.Id, Scope: null, SkipConfirm: false), activeTaskId: null, CancellationToken.None);

        fx.Captured.Should().Contain("[task review] cancelled");
        fx.Reviewer.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Proceeds_when_user_answers_y()
    {
        await using var fx = await Fixture.OpenAsync(withReview: true, input: new StringReader("y\n"));
        var task = await fx.CreateTaskAsync("Test task");
        fx.Reviewer.QueueAllReviewersEmpty();
        fx.Reviewer.QueueCoordinator(CoordinatorEmptyJson());

        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: task.Id, Scope: null, SkipConfirm: false), activeTaskId: null, CancellationToken.None);

        fx.Reviewer.Calls.Should().HaveCount(6);
    }

    [Fact]
    public async Task Yes_flag_skips_the_prompt()
    {
        await using var fx = await Fixture.OpenAsync(withReview: true, input: null);
        var task = await fx.CreateTaskAsync("Test task");
        fx.Reviewer.QueueAllReviewersEmpty();
        fx.Reviewer.QueueCoordinator(CoordinatorEmptyJson());

        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: task.Id, Scope: null, SkipConfirm: true), activeTaskId: null, CancellationToken.None);

        fx.Captured.Should().NotContain("Continue?");
        fx.Reviewer.Calls.Should().HaveCount(6);
    }

    [Fact]
    public async Task Writes_report_artifact_to_task_folder()
    {
        await using var fx = await Fixture.OpenAsync(withReview: true);
        var task = await fx.CreateTaskAsync("Test task");
        fx.Reviewer.QueueAllReviewersEmpty();
        fx.Reviewer.QueueCoordinator(CoordinatorEmptyJson());

        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: task.Id, Scope: null, SkipConfirm: true), activeTaskId: null, CancellationToken.None);

        var folder = new TaskFolder(task.HomeCwd, task.Id, Slugify.For(task.Title));
        var reportFiles = Directory.GetFiles(folder.Path, "review-*.md");
        reportFiles.Should().HaveCount(1);
        var reportContent = await File.ReadAllTextAsync(reportFiles[0], TestContext.Current.CancellationToken);
        reportContent.Should().Contain("# Review report - Test task");
        reportContent.Should().Contain($"**Task:** {task.Id:D4}");
    }

    [Fact]
    public async Task Appends_row_to_index_md_after_review_run()
    {
        await using var fx = await Fixture.OpenAsync(withReview: true, withLockRegistry: true);
        var task = await fx.CreateTaskAsync("Test task");
        fx.Reviewer.QueueAllReviewersEmpty();
        fx.Reviewer.QueueCoordinator(CoordinatorEmptyJson());

        var folder = new TaskFolder(task.HomeCwd, task.Id, Slugify.For(task.Title));
        var indexPath = Path.Combine(folder.Path, "index.md");
        var beforeContent = await File.ReadAllTextAsync(indexPath, TestContext.Current.CancellationToken);

        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: task.Id, Scope: null, SkipConfirm: true), activeTaskId: null, CancellationToken.None);

        var afterContent = await File.ReadAllTextAsync(indexPath, TestContext.Current.CancellationToken);
        afterContent.Should().StartWith(beforeContent.TrimEnd()); // existing content preserved
        afterContent.Should().Contain("| review-");
        afterContent.Should().Contain("Review report (applied=0 rejected=0 awaiting=0)");
    }

    [Fact]
    public async Task Appends_entry_to_progress_md_after_review_run()
    {
        await using var fx = await Fixture.OpenAsync(withReview: true, withLockRegistry: true);
        var task = await fx.CreateTaskAsync("Test task");
        fx.Reviewer.QueueAllReviewersEmpty();
        fx.Reviewer.QueueCoordinator(CoordinatorEmptyJson());

        var folder = new TaskFolder(task.HomeCwd, task.Id, Slugify.For(task.Title));
        var progressPath = Path.Combine(folder.Path, "progress.md");
        var beforeContent = await File.ReadAllTextAsync(progressPath, TestContext.Current.CancellationToken);

        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: task.Id, Scope: null, SkipConfirm: true), activeTaskId: null, CancellationToken.None);

        var afterContent = await File.ReadAllTextAsync(progressPath, TestContext.Current.CancellationToken);
        afterContent.Should().StartWith(beforeContent.TrimEnd()); // existing content preserved
        afterContent.Should().Contain("(review)");
        afterContent.Should().Contain("Ran `/task review`. applied=0 rejected=0 awaiting=0");
        afterContent.Should().Contain("Build/test gate: green");
    }

    [Fact]
    public async Task Works_without_lock_registry_too()
    {
        // Tests run without a LockRegistry; the handler must still write the report and update
        // index.md / progress.md (it falls back to direct file writes).
        await using var fx = await Fixture.OpenAsync(withReview: true, withLockRegistry: false);
        var task = await fx.CreateTaskAsync("Test task");
        fx.Reviewer.QueueAllReviewersEmpty();
        fx.Reviewer.QueueCoordinator(CoordinatorEmptyJson());

        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: task.Id, Scope: null, SkipConfirm: true), activeTaskId: null, CancellationToken.None);

        var folder = new TaskFolder(task.HomeCwd, task.Id, Slugify.For(task.Title));
        var indexAfter = await File.ReadAllTextAsync(Path.Combine(folder.Path, "index.md"), TestContext.Current.CancellationToken);
        indexAfter.Should().Contain("| review-");
    }

    // ---------- Empty-scope short-circuit ----------

    [Fact]
    public async Task Refuses_with_clear_message_when_scope_has_zero_files()
    {
        // emptyDiff: the FakeGitRunner returns an empty diff list, so scope.Files.Count == 0.
        await using var fx = await Fixture.OpenAsync(withReview: true, emptyDiff: true);
        var task = await fx.CreateTaskAsync("Test task");

        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: task.Id, Scope: null, SkipConfirm: true), activeTaskId: null, CancellationToken.None);

        fx.Captured.Should().Contain("nothing to review (0 files in scope)");
        // Helpful hint included when the user didn't pass --scope.
        fx.Captured.Should().Contain("Pass --scope");
        // No agents spawned.
        fx.Reviewer.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_scope_short_circuit_runs_before_the_cost_prompt()
    {
        // Even without --yes, an empty scope must NOT trigger the [y/N] prompt. The user
        // shouldn't be asked to confirm a 6-agent spawn that will never happen.
        await using var fx = await Fixture.OpenAsync(withReview: true, emptyDiff: true, input: new StringReader("y\n"));
        var task = await fx.CreateTaskAsync("Test task");

        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: task.Id, Scope: null, SkipConfirm: false), activeTaskId: null, CancellationToken.None);

        fx.Captured.Should().NotContain("Continue? [y/N]");
        fx.Captured.Should().Contain("nothing to review");
        fx.Reviewer.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_scope_with_user_supplied_scope_shows_a_scope_specific_message()
    {
        // When the user explicitly passed --scope and all paths got excluded / didn't resolve,
        // the message hint reflects that (not "pass --scope" - they already did).
        await using var fx = await Fixture.OpenAsync(withReview: true, emptyDiff: true);
        var task = await fx.CreateTaskAsync("Test task");

        // Scope override points at a non-existent path so it gets excluded.
        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: task.Id, Scope: "ghost.cs", SkipConfirm: true), activeTaskId: null, CancellationToken.None);

        fx.Captured.Should().Contain("nothing to review");
        fx.Captured.Should().Contain("Every --scope path was excluded or unreadable.");
        fx.Reviewer.Calls.Should().BeEmpty();
    }

    // ---------- AppendIndexRow / AppendProgressEntry static helpers ----------

    [Fact]
    public void AppendIndexRow_adds_row_with_summary_and_date()
    {
        var existing = """
            # Task index

            | File | Description | Last updated |
            |------|-------------|--------------|
            | description.md | Full task spec | 2026-05-16 |
            """;
        var report = MakeReport(applied: 2, rejected: 1, awaiting: 4, gatePassed: true);

        var updated = TaskCommandHandler.AppendIndexRow(existing, "review-2026-05-17T120000.md", report, new DateOnly(2026, 5, 17));

        updated.Should().Contain("| description.md | Full task spec | 2026-05-16 |");
        updated.Should().Contain("| review-2026-05-17T120000.md | Review report (applied=2 rejected=1 awaiting=4) | 2026-05-17 |");
    }

    [Fact]
    public void AppendIndexRow_handles_existing_without_trailing_newline()
    {
        var existing = "| description.md | x | 2026-05-16 |"; // no trailing newline
        var report = MakeReport(applied: 0, rejected: 0, awaiting: 0, gatePassed: true);

        var updated = TaskCommandHandler.AppendIndexRow(existing, "review-x.md", report, new DateOnly(2026, 5, 17));

        updated.Should().Contain("\n| review-x.md |");
    }

    [Fact]
    public void AppendProgressEntry_inserts_h2_heading_and_summary_bullet()
    {
        var existing = """
            # Progress

            ## 2026-05-16

            - Earlier work.
            """;
        var report = MakeReport(applied: 1, rejected: 0, awaiting: 2, gatePassed: true);

        var updated = TaskCommandHandler.AppendProgressEntry(existing, "review-2026-05-17T120000.md", report, new DateOnly(2026, 5, 17));

        updated.Should().Contain("## 2026-05-17 (review)");
        updated.Should().Contain("Ran `/task review`. applied=1 rejected=0 awaiting=2.");
        updated.Should().Contain("Report: `review-2026-05-17T120000.md`");
        updated.Should().Contain("Build/test gate: green");
    }

    [Fact]
    public void AppendProgressEntry_collapses_under_existing_same_day_heading()
    {
        // First review opens "## 2026-05-17 (review)"; second review the same day should NOT
        // open another heading - it should append the second bullet under the existing one.
        var existing = """
            # Progress

            ## 2026-05-17 (review)

            - Ran `/task review`. applied=0 rejected=0 awaiting=0. Report: `review-A.md`. Build/test gate: green.
            """;
        var report = MakeReport(applied: 1, rejected: 0, awaiting: 2, gatePassed: true);

        var updated = TaskCommandHandler.AppendProgressEntry(existing, "review-B.md", report, new DateOnly(2026, 5, 17));

        // Heading appears exactly once.
        System.Text.RegularExpressions.Regex.Matches(updated, @"## 2026-05-17 \(review\)").Count.Should().Be(1);
        // Both bullets are present.
        updated.Should().Contain("Report: `review-A.md`");
        updated.Should().Contain("Report: `review-B.md`");
        // The two bullets sit under the same heading.
        var headingIdx = updated.IndexOf("## 2026-05-17 (review)", StringComparison.Ordinal);
        updated.IndexOf("review-A.md", headingIdx, StringComparison.Ordinal).Should().BeGreaterThan(headingIdx);
        updated.IndexOf("review-B.md", headingIdx, StringComparison.Ordinal).Should().BeGreaterThan(headingIdx);
    }

    [Fact]
    public void AppendProgressEntry_inserts_bullet_before_next_heading_when_section_is_not_last()
    {
        // Today's review heading exists but is followed by a later-dated heading. The new
        // bullet must land BEFORE the next heading, inside today's section.
        var existing = """
            # Progress

            ## 2026-05-17 (review)

            - Ran `/task review`. applied=0 rejected=0 awaiting=0. Report: `review-A.md`. Build/test gate: green.

            ## 2026-05-18 (impl session)

            - Some other work.
            """;
        var report = MakeReport(applied: 0, rejected: 0, awaiting: 1, gatePassed: true);

        var updated = TaskCommandHandler.AppendProgressEntry(existing, "review-B.md", report, new DateOnly(2026, 5, 17));

        // Heading exactly once.
        System.Text.RegularExpressions.Regex.Matches(updated, @"## 2026-05-17 \(review\)").Count.Should().Be(1);
        // The new bullet appears BEFORE the next heading.
        var newBulletIdx = updated.IndexOf("review-B.md", StringComparison.Ordinal);
        var nextHeadingIdx = updated.IndexOf("## 2026-05-18", StringComparison.Ordinal);
        newBulletIdx.Should().BeGreaterThan(0);
        nextHeadingIdx.Should().BeGreaterThan(0);
        newBulletIdx.Should().BeLessThan(nextHeadingIdx);
        // The succeeding heading's content is preserved.
        updated.Should().Contain("Some other work.");
    }

    [Fact]
    public void AppendProgressEntry_does_not_reuse_a_review_heading_from_a_different_date()
    {
        // Yesterday's review heading exists. Today's run must open a fresh heading.
        var existing = """
            # Progress

            ## 2026-05-16 (review)

            - Ran `/task review`. applied=0 rejected=0 awaiting=0. Report: `review-A.md`. Build/test gate: green.
            """;
        var report = MakeReport(applied: 0, rejected: 0, awaiting: 0, gatePassed: true);

        var updated = TaskCommandHandler.AppendProgressEntry(existing, "review-B.md", report, new DateOnly(2026, 5, 17));

        updated.Should().Contain("## 2026-05-16 (review)");
        updated.Should().Contain("## 2026-05-17 (review)");
    }

    [Fact]
    public void AppendProgressEntry_does_not_collide_with_user_written_same_day_session_headings()
    {
        // A user-written "## 2026-05-17 (impl session)" must NOT be matched as a review-section
        // heading. The exact-match comparison guards against this.
        var existing = """
            # Progress

            ## 2026-05-17 (impl session - step X)

            - Some work.
            """;
        var report = MakeReport(applied: 0, rejected: 0, awaiting: 0, gatePassed: true);

        var updated = TaskCommandHandler.AppendProgressEntry(existing, "review-A.md", report, new DateOnly(2026, 5, 17));

        // The user's heading stays unchanged AND a new (review) heading opens.
        updated.Should().Contain("## 2026-05-17 (impl session - step X)");
        updated.Should().Contain("## 2026-05-17 (review)");
    }

    [Fact]
    public void AppendProgressEntry_preserves_crlf_line_endings()
    {
        // Windows checkouts may store progress.md with CRLF. The function shouldn't mix
        // conventions by writing LF separators into a CRLF file.
        var existing = "# Progress\r\n\r\n## 2026-05-17 (review)\r\n\r\n- Earlier bullet.\r\n";
        var report = MakeReport(applied: 0, rejected: 0, awaiting: 1, gatePassed: true);

        var updated = TaskCommandHandler.AppendProgressEntry(existing, "review-B.md", report, new DateOnly(2026, 5, 17));

        updated.Should().Contain("\r\n");
        // The append shouldn't produce a lone \n that wasn't preceded by \r.
        for (var i = 0; i < updated.Length; i++)
        {
            if (updated[i] == '\n')
            {
                (i > 0 && updated[i - 1] == '\r').Should().BeTrue($"lone LF found at index {i}");
            }
        }
    }

    [Fact]
    public void AppendProgressEntry_surfaces_gate_reversion_reason()
    {
        var report = MakeReport(applied: 0, rejected: 0, awaiting: 1, gatePassed: false, gateReason: "tests broke");

        var updated = TaskCommandHandler.AppendProgressEntry("# Progress", "review-x.md", report, new DateOnly(2026, 5, 17));

        updated.Should().Contain("Build/test gate: reverted (tests broke)");
    }

    [Fact]
    public async Task Prints_summary_with_applied_rejected_awaiting_counts()
    {
        await using var fx = await Fixture.OpenAsync(withReview: true);
        var task = await fx.CreateTaskAsync("Test task");
        fx.Reviewer.QueueAllReviewersEmpty();
        fx.Reviewer.QueueCoordinator(CoordinatorEmptyJson());

        await fx.Handler.HandleReviewAsync(new TaskCommand.Review(Id: task.Id, Scope: null, SkipConfirm: true), activeTaskId: null, CancellationToken.None);

        fx.Captured.Should().Contain("applied=0 rejected=0 awaiting=0");
        fx.Captured.Should().Contain("[task review] done.");
    }

    // ---------- Helpers ----------

    private static string CoordinatorEmptyJson() => """
        { "findings": [], "summary": null }
        """;

    private static string EmptyReviewerJson(string reviewer) => $$"""
        { "reviewer": "{{reviewer}}", "findings": [], "notes": null }
        """;

    private static ReviewReport MakeReport(int applied, int rejected, int awaiting, bool gatePassed, string? gateReason = null) => new(
        TaskId: 42,
        TaskTitle: "Test",
        RanAt: new DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero),
        ScopeDescription: "test scope",
        ExcludedFiles: Array.Empty<string>(),
        Applied: Enumerable.Range(0, applied).Select(i => MakeBlankFinding($"a-{i}")).ToList(),
        Rejected: Enumerable.Range(0, rejected).Select(i => MakeBlankFinding($"r-{i}")).ToList(),
        Awaiting: Enumerable.Range(0, awaiting).Select(i => MakeBlankFinding($"w-{i}")).ToList(),
        BuildTestGatePassed: gatePassed,
        GateReversionReason: gateReason,
        RawReviewerOutputs: new Dictionary<string, string>());

    private static ConsolidatedFinding MakeBlankFinding(string id) => new(
        Id: id, Severity: Severity.Warning, Title: "t", Summary: "s", Why: null,
        File: null, LineStart: null, LineEnd: null, SuggestedFix: null, Confidence: 0.5,
        Reviewers: Array.Empty<string>(), Disposition: Disposition.NeedsUser,
        DispositionConfidence: 0.5, RejectionReason: null, ConsolidatorNote: null);

    private sealed class Fixture : IAsyncDisposable
    {
        public required TaskStore Store { get; init; }
        public required TaskCommandHandler Handler { get; init; }
        public required FakeReviewer Reviewer { get; init; }
        public required TaskService Service { get; init; }
        public required StringWriter Writer { get; init; }
        public required string TempPath { get; init; }
        public required string HomeCwd { get; init; }

        public string Captured => Writer.ToString();

        public static async Task<Fixture> OpenAsync(
            bool withReview,
            TextReader? input = null,
            bool withLockRegistry = false,
            bool emptyDiff = false)
        {
            var temp = Path.Combine(Path.GetTempPath(), $"cp-taskreview-{Guid.NewGuid():N}");
            var home = Path.Combine(temp, "repo");
            Directory.CreateDirectory(home);

            var store = await TaskStore.OpenAsync(":memory:");
            var lightModel = new QueueLightModel();
            var service = new TaskService(store, lightModel);

            // Pre-queue titles for any task we'll create. The fake light model just dequeues.
            for (var i = 0; i < 4; i++) lightModel.QueueTitle("Test task");

            var writer = new StringWriter();
            var reviewer = new FakeReviewer();
            var lockRegistry = withLockRegistry ? new LockRegistry() : null;

            // By default, seed one file in the home cwd and have FakeGitRunner report it in
            // the diff, so scope.Files.Count > 0 and the new empty-scope short-circuit doesn't
            // skip the rest of the flow. Tests that exercise the short-circuit pass
            // emptyDiff: true.
            var fakeGit = new FakeGitRunner();
            if (!emptyDiff)
            {
                var srcDir = Path.Combine(home, "src");
                Directory.CreateDirectory(srcDir);
                await File.WriteAllTextAsync(Path.Combine(srcDir, "Foo.cs"), "// dummy");
                fakeGit.DiffResult = new[] { "src/Foo.cs" };
            }

            TaskCommandHandler handler;
            if (withReview)
            {
                var reviewService = new ReviewService(reviewer);
                var scopeResolver = new ScopeResolver(fakeGit);
                handler = new TaskCommandHandler(
                    service, writer, writeLock: null,
                    reviewService: reviewService,
                    scopeResolver: scopeResolver,
                    input: input,
                    lockRegistry: lockRegistry);
            }
            else
            {
                handler = new TaskCommandHandler(service, writer, writeLock: null,
                    reviewService: null, scopeResolver: null, input: input,
                    lockRegistry: lockRegistry);
            }

            return new Fixture
            {
                Store = store,
                Handler = handler,
                Reviewer = reviewer,
                Service = service,
                Writer = writer,
                TempPath = temp,
                HomeCwd = home,
            };
        }

        public Task<TaskRecord> CreateTaskAsync(string description) =>
            Service.CreateAsync(HomeCwd, description);

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            try { Directory.Delete(TempPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class QueueLightModel : ILightModelService
    {
        private readonly Queue<string> _titles = new();
        public void QueueTitle(string title) => _titles.Enqueue(title);

        public Task<string> GenerateShortTitleAsync(string description, int maxWords = 6, CancellationToken cancellationToken = default)
            => Task.FromResult(_titles.Count > 0 ? _titles.Dequeue() : "Untitled");

        public Task<string> SummarizeAsync(string text, int maxSentences = 2, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CategoryResult> CategorizeAsync(string text, IReadOnlyList<string> categories, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SafetyReview> ReviewToolCallSafetyAsync(SafetyReviewRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeReviewer : IReviewerAgentService
    {
        private readonly Dictionary<ReviewerRole, string> _responses = new();
        public List<(ReviewerRole role, string user)> Calls { get; } = new();

        public void QueueAllReviewersEmpty()
        {
            foreach (var role in new[] { ReviewerRole.Completeness, ReviewerRole.Architecture, ReviewerRole.CodeLogic, ReviewerRole.Tests, ReviewerRole.Security })
            {
                _responses[role] = $$"""
                    { "reviewer": "{{role.ToString().ToLowerInvariant()}}", "findings": [], "notes": null }
                    """;
            }
        }

        public void QueueCoordinator(string json) => _responses[ReviewerRole.Coordinator] = json;

        public Task<string> RunOneShotAsync(string systemPrompt, string userPayload, CancellationToken ct)
        {
            var role = InferRole(systemPrompt);
            Calls.Add((role, userPayload));
            if (!_responses.TryGetValue(role, out var resp))
                throw new InvalidOperationException($"no response queued for role {role}");
            return Task.FromResult(resp);
        }

        private static ReviewerRole InferRole(string systemPrompt)
        {
            if (systemPrompt.Contains("Completeness reviewer")) return ReviewerRole.Completeness;
            if (systemPrompt.Contains("Architecture reviewer")) return ReviewerRole.Architecture;
            if (systemPrompt.Contains("Code & Logic reviewer")) return ReviewerRole.CodeLogic;
            if (systemPrompt.Contains("Tests reviewer")) return ReviewerRole.Tests;
            if (systemPrompt.Contains("Security reviewer")) return ReviewerRole.Security;
            if (systemPrompt.Contains("Coordinator")) return ReviewerRole.Coordinator;
            throw new InvalidOperationException("unknown system prompt");
        }
    }

    private sealed class FakeGitRunner : IGitRunner
    {
        public IReadOnlyList<string> DiffResult { get; set; } = Array.Empty<string>();

        public Task<string?> ResolveHeadAsync(string repoRoot, CancellationToken ct) => Task.FromResult<string?>("h1234567" + new string('0', 32));
        public Task<string?> ResolveMergeBaseAsync(string repoRoot, string ref1, string ref2, CancellationToken ct) => Task.FromResult<string?>("m1234567" + new string('0', 32));
        public Task<IReadOnlyList<string>> DiffNameOnlyAsync(string repoRoot, string baseRef, string headRef, CancellationToken ct) => Task.FromResult(DiffResult);
    }
}
