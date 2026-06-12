using InfiniContext.Core.LightModel;
using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Tasks;

public class IssueServiceTests
{
    [Fact]
    public async Task Append_creates_file_with_header_and_first_entry()
    {
        await using var fx = await Fixture.OpenAsync();
        var task = await fx.CreateTaskAsync("Implement feature");

        var entry = await fx.Issues.AppendAsync(
            task.Id,
            new IssueCapture("First issue", IssueKind.Bug, Where: "src/Foo.cs:42", Body: null, LoggedBy: "test"),
            TestContext.Current.CancellationToken);

        entry.LocalId.Should().Be(1);
        entry.Title.Should().Be("First issue");
        entry.Kind.Should().Be(IssueKind.Bug);
        entry.Where.Should().Be("src/Foo.cs:42");
        entry.Status.Should().Be(IssueStatus.Open);

        var content = await File.ReadAllTextAsync(fx.IssuesPath(task), TestContext.Current.CancellationToken);
        content.Should().Contain("# Issues - Implement feature");
        content.Should().Contain("## 0001 - First issue");
        content.Should().Contain("- **Kind:** bug");
        content.Should().Contain("- **Where:** src/Foo.cs:42");
        content.Should().Contain("- **Status:** open");
        content.Should().Contain("by test");
    }

    [Fact]
    public async Task Append_auto_increments_local_id()
    {
        await using var fx = await Fixture.OpenAsync();
        var task = await fx.CreateTaskAsync("T");
        await fx.Issues.AppendAsync(task.Id, new IssueCapture("a"), TestContext.Current.CancellationToken);
        await fx.Issues.AppendAsync(task.Id, new IssueCapture("b"), TestContext.Current.CancellationToken);
        var third = await fx.Issues.AppendAsync(task.Id, new IssueCapture("c"), TestContext.Current.CancellationToken);

        third.LocalId.Should().Be(3);

        var entries = await fx.Issues.ListAsync(task.Id, TestContext.Current.CancellationToken);
        entries.Select(e => e.LocalId).Should().Equal(1, 2, 3);
        entries.Select(e => e.Title).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task Append_resumes_from_highest_seen_id_even_after_manual_delete()
    {
        // Simulate a hand-edit that removed entry #2 from the middle. New id should still be 4,
        // not 3 - max(seen) + 1.
        await using var fx = await Fixture.OpenAsync();
        var task = await fx.CreateTaskAsync("T");
        await fx.Issues.AppendAsync(task.Id, new IssueCapture("a"), TestContext.Current.CancellationToken);
        await fx.Issues.AppendAsync(task.Id, new IssueCapture("b"), TestContext.Current.CancellationToken);
        await fx.Issues.AppendAsync(task.Id, new IssueCapture("c"), TestContext.Current.CancellationToken);

        var path = fx.IssuesPath(task);
        var content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        // Strip the second entry block (heading line through next heading, exclusive).
        var lines = content.Split('\n').ToList();
        var b = lines.FindIndex(l => l.StartsWith("## 0002 "));
        var c = lines.FindIndex(l => l.StartsWith("## 0003 "));
        lines.RemoveRange(b, c - b);
        await File.WriteAllTextAsync(path, string.Join('\n', lines), TestContext.Current.CancellationToken);

        var fourth = await fx.Issues.AppendAsync(task.Id, new IssueCapture("d"), TestContext.Current.CancellationToken);
        fourth.LocalId.Should().Be(4);
    }

    [Fact]
    public async Task List_returns_empty_when_file_missing()
    {
        await using var fx = await Fixture.OpenAsync();
        var task = await fx.CreateTaskAsync("T");

        var entries = await fx.Issues.ListAsync(task.Id, TestContext.Current.CancellationToken);

        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_sets_status_and_inserts_resolution_line_with_note()
    {
        await using var fx = await Fixture.OpenAsync();
        var task = await fx.CreateTaskAsync("T");
        await fx.Issues.AppendAsync(task.Id, new IssueCapture("bug 1", IssueKind.Bug), TestContext.Current.CancellationToken);

        var updated = await fx.Issues.ResolveAsync(task.Id, 1, IssueStatus.Resolved, "fixed in abc123", TestContext.Current.CancellationToken);

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(IssueStatus.Resolved);
        updated.Resolution.Should().StartWith(fx.Clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd"));
        updated.Resolution.Should().EndWith("fixed in abc123");

        var content = await File.ReadAllTextAsync(fx.IssuesPath(task), TestContext.Current.CancellationToken);
        content.Should().Contain("- **Status:** resolved");
        content.Should().Contain("- **Resolution:** ");
        content.Should().Contain("fixed in abc123");
        content.Should().NotContain("- **Status:** open");
    }

    [Theory]
    [InlineData(IssueStatus.Resolved, "resolved")]
    [InlineData(IssueStatus.WontFix, "wont-fix")]
    [InlineData(IssueStatus.Deferred, "deferred")]
    public async Task Resolve_accepts_each_terminal_status(IssueStatus status, string expectedWire)
    {
        await using var fx = await Fixture.OpenAsync();
        var task = await fx.CreateTaskAsync("T");
        await fx.Issues.AppendAsync(task.Id, new IssueCapture("x"), TestContext.Current.CancellationToken);

        var updated = await fx.Issues.ResolveAsync(task.Id, 1, status, note: null, ct: TestContext.Current.CancellationToken);

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(status);
        (await File.ReadAllTextAsync(fx.IssuesPath(task), TestContext.Current.CancellationToken)).Should().Contain($"- **Status:** {expectedWire}");
    }

    [Fact]
    public async Task Resolve_rejects_open_as_terminal_state()
    {
        await using var fx = await Fixture.OpenAsync();
        var task = await fx.CreateTaskAsync("T");
        await fx.Issues.AppendAsync(task.Id, new IssueCapture("x"), TestContext.Current.CancellationToken);

        var act = () => fx.Issues.ResolveAsync(task.Id, 1, IssueStatus.Open, note: null);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*not a terminal state*");
    }

    [Fact]
    public async Task Resolve_unknown_local_id_returns_null()
    {
        await using var fx = await Fixture.OpenAsync();
        var task = await fx.CreateTaskAsync("T");
        await fx.Issues.AppendAsync(task.Id, new IssueCapture("x"), TestContext.Current.CancellationToken);

        var result = await fx.Issues.ResolveAsync(task.Id, 99, IssueStatus.Resolved, "n/a", TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_when_file_missing_returns_null()
    {
        await using var fx = await Fixture.OpenAsync();
        var task = await fx.CreateTaskAsync("T");
        // No issues.md created yet.

        var result = await fx.Issues.ResolveAsync(task.Id, 1, IssueStatus.Resolved, null, TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Append_round_trips_through_List()
    {
        await using var fx = await Fixture.OpenAsync();
        var task = await fx.CreateTaskAsync("T");
        await fx.Issues.AppendAsync(task.Id, new IssueCapture("Title 1", IssueKind.Smell, Where: "f.cs:1", LoggedBy: "alice"), TestContext.Current.CancellationToken);
        await fx.Issues.AppendAsync(task.Id, new IssueCapture("Title 2", IssueKind.Question), TestContext.Current.CancellationToken);

        var list = await fx.Issues.ListAsync(task.Id, TestContext.Current.CancellationToken);

        list.Should().HaveCount(2);
        list[0].Title.Should().Be("Title 1");
        list[0].Kind.Should().Be(IssueKind.Smell);
        list[0].Where.Should().Be("f.cs:1");
        list[0].LoggedBy.Should().Be("alice");
        list[0].Status.Should().Be(IssueStatus.Open);
        list[1].Title.Should().Be("Title 2");
        list[1].Kind.Should().Be(IssueKind.Question);
        list[1].Where.Should().BeNull();
    }

    [Fact]
    public async Task Append_throws_when_task_missing()
    {
        await using var fx = await Fixture.OpenAsync();
        var act = () => fx.Issues.AppendAsync(999, new IssueCapture("x"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*0999*not found*");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TaskStore Store { get; init; }
        public required TaskService TaskService { get; init; }
        public required IssueService Issues { get; init; }
        public required FakeClock Clock { get; init; }
        public required QueueLightModel LightModel { get; init; }
        public required string TempPath { get; init; }
        public required string HomeCwd { get; init; }

        public static async Task<Fixture> OpenAsync()
        {
            var temp = Path.Combine(Path.GetTempPath(), $"cp-issuesvc-{Guid.NewGuid():N}");
            var home = Path.Combine(temp, "repo");
            Directory.CreateDirectory(home);

            var store = await TaskStore.OpenAsync(":memory:");
            var clock = new FakeClock(new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero));
            var lightModel = new QueueLightModel();
            var taskSvc = new TaskService(store, lightModel, () => clock.UtcNow);
            var issueSvc = new IssueService(store, lockRegistry: null, clock: () => clock.UtcNow);

            return new Fixture
            {
                Store = store,
                TaskService = taskSvc,
                Issues = issueSvc,
                Clock = clock,
                LightModel = lightModel,
                TempPath = temp,
                HomeCwd = home,
            };
        }

        public async Task<TaskRecord> CreateTaskAsync(string title)
        {
            LightModel.QueueTitle(title);
            return await TaskService.CreateAsync(HomeCwd, $"Body for {title}");
        }

        public string IssuesPath(TaskRecord record)
        {
            var folder = new TaskFolder(record.HomeCwd, record.Id, Slugify.For(record.Title));
            return Path.Combine(folder.Path, IssueMarkdown.FileName);
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

    private sealed class QueueLightModel : ILightModelService
    {
        private readonly Queue<string> _titles = new();
        public void QueueTitle(string t) => _titles.Enqueue(t);

        public Task<string> GenerateShortTitleAsync(string description, int maxWords = 6, CancellationToken cancellationToken = default)
            => Task.FromResult(_titles.Count > 0 ? _titles.Dequeue() : "Untitled");
        public Task<string> SummarizeAsync(string text, int maxSentences = 2, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<CategoryResult> CategorizeAsync(string text, IReadOnlyList<string> categories, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<SafetyReview> ReviewToolCallSafetyAsync(SafetyReviewRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
