using InfiniContext.Cli.Tasks;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Review;
using InfiniContext.Core.Tasks;
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
        var reportContent = await File.ReadAllTextAsync(reportFiles[0]);
        reportContent.Should().Contain("# Review report - Test task");
        reportContent.Should().Contain($"**Task:** {task.Id:D4}");
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

        public static async Task<Fixture> OpenAsync(bool withReview, TextReader? input = null)
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

            TaskCommandHandler handler;
            if (withReview)
            {
                var reviewService = new ReviewService(reviewer);
                var scopeResolver = new ScopeResolver(new FakeGitRunner());
                handler = new TaskCommandHandler(
                    service, writer, writeLock: null,
                    reviewService: reviewService,
                    scopeResolver: scopeResolver,
                    input: input);
            }
            else
            {
                handler = new TaskCommandHandler(service, writer, writeLock: null,
                    reviewService: null, scopeResolver: null, input: input);
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
        public Task<string?> ResolveHeadAsync(string repoRoot, CancellationToken ct) => Task.FromResult<string?>("h1234567" + new string('0', 32));
        public Task<string?> ResolveMergeBaseAsync(string repoRoot, string ref1, string ref2, CancellationToken ct) => Task.FromResult<string?>("m1234567" + new string('0', 32));
        public Task<IReadOnlyList<string>> DiffNameOnlyAsync(string repoRoot, string baseRef, string headRef, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
