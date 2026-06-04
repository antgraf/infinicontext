using System.Text.Json.Nodes;
using InfiniContext.Core.Tasks;
using InfiniContext.Core.LightModel;
using FluentAssertions;

namespace InfiniContext.Tests.Cli.Tasks;

/// <summary>
/// Output-formatting tests for <see cref="TaskCommandHandler"/>. Uses a real
/// <see cref="TaskService"/> against an in-memory SQLite store and a temp filesystem rather
/// than a mocked service — both because the handler is presentation-layer (so the integration
/// is what we care about) and because the repo convention is "no mocks of internal types".
/// </summary>
public class TaskCommandHandlerTests
{
    [Fact]
    public async Task New_prints_id_title_and_folder()
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.LightModel.QueueTitle("Implement Phase 5");

        await fixture.Handler.HandleAsync(new TaskCommand.New("wire the REPL command surface"));

        var output = fixture.Captured;
        output.Should().Contain("[task] created 0001 \"Implement Phase 5\"");
        output.Should().Contain("folder:").And.Contain("0001-implement-phase-5");
    }

    [Fact]
    public async Task List_empty_says_no_tasks()
    {
        await using var fixture = await Fixture.OpenAsync();

        await fixture.Handler.HandleAsync(new TaskCommand.List(null, null));

        fixture.Captured.Should().Contain("[task] no tasks match.");
    }

    [Fact]
    public async Task List_renders_table_with_id_status_and_title()
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.LightModel.QueueTitle("First");
        fixture.LightModel.QueueTitle("Second");
        await fixture.Handler.HandleAsync(new TaskCommand.New("a"));
        await fixture.Handler.HandleAsync(new TaskCommand.New("b"));
        fixture.Captured = "";

        await fixture.Handler.HandleAsync(new TaskCommand.List(null, null));

        var output = fixture.Captured;
        output.Should().Contain("ID     STATUS      LAST ACTIVE");
        output.Should().Contain("0001").And.Contain("First");
        output.Should().Contain("0002").And.Contain("Second");
        output.Should().Contain("InProgress");
    }

    [Fact]
    public async Task Show_renders_record_and_artifacts()
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.LightModel.QueueTitle("Showme");
        await fixture.Handler.HandleAsync(new TaskCommand.New("describe"));
        fixture.Captured = "";

        await fixture.Handler.HandleAsync(new TaskCommand.Show(1));

        var output = fixture.Captured;
        output.Should().Contain("[task] 0001 \"Showme\"");
        output.Should().Contain("home:").And.Contain("status:  InProgress");
        output.Should().Contain("artifacts:");
        output.Should().Contain("description.md").And.Contain("index.md").And.Contain("progress.md");
    }

    [Fact]
    public async Task Show_unknown_id_prints_friendly_message()
    {
        await using var fixture = await Fixture.OpenAsync();

        await fixture.Handler.HandleAsync(new TaskCommand.Show(999));

        fixture.Captured.Should().Contain("[task] 0999 not found.");
    }

    [Fact]
    public async Task Fork_prints_lineage_arrow()
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.LightModel.QueueTitle("Source");
        await fixture.Handler.HandleAsync(new TaskCommand.New("the source"));
        fixture.Captured = "";

        await fixture.Handler.HandleAsync(new TaskCommand.Fork(1, null));

        fixture.Captured.Should().Contain("forked 0001 -> 0002").And.Contain("\"Source (2)\"");
    }

    [Fact]
    public async Task Merge_prints_arrow_and_concat_warning()
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.LightModel.QueueTitle("A");
        fixture.LightModel.QueueTitle("B");
        await fixture.Handler.HandleAsync(new TaskCommand.New("a"));
        await fixture.Handler.HandleAsync(new TaskCommand.New("b"));
        fixture.Captured = "";

        await fixture.Handler.HandleAsync(new TaskCommand.Merge(new[] { 1, 2 }));

        fixture.Captured.Should().Contain("merged 0001, 0002 -> 0003");
        fixture.Captured.Should().Contain("only DeterministicConcat tier is implemented in MVP");
    }

    [Fact]
    public async Task Switch_outside_repl_surfaces_clear_note()
    {
        // /task switch is intercepted by Repl in the live host. If something calls the handler
        // directly with a Switch (e.g. a test harness), we want a clear message rather than a
        // silent no-op.
        await using var fixture = await Fixture.OpenAsync();

        await fixture.Handler.HandleAsync(new TaskCommand.Switch(42));

        fixture.Captured.Should().Contain("/task switch must be invoked from the REPL.");
    }

    [Fact]
    public async Task Delete_unknown_id_prints_not_found()
    {
        await using var fixture = await Fixture.OpenAsync();

        await fixture.Handler.HandleAsync(new TaskCommand.Delete(999, RemoveFolder: false));

        fixture.Captured.Should().Contain("[task] 0999 not found.");
    }

    [Fact]
    public async Task Delete_InProgress_task_surfaces_friendly_error()
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.LightModel.QueueTitle("active");
        await fixture.Handler.HandleAsync(new TaskCommand.New("a", fixture.HomeCwd));
        fixture.Captured = "";

        await fixture.Handler.HandleAsync(new TaskCommand.Delete(1, RemoveFolder: false));

        fixture.Captured.Should().Contain("[task] error:")
            .And.Contain("InProgress");
        (await fixture.Store.GetAsync(1)).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_without_remove_folder_drops_row_and_keeps_folder()
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.LightModel.QueueTitle("doomed");
        await fixture.Handler.HandleAsync(new TaskCommand.New("d", fixture.HomeCwd));
        await fixture.Store.UpdateStatusAsync(1, TaskItemStatus.Idle, whenUnixMs: 1);
        fixture.Captured = "";

        await fixture.Handler.HandleAsync(new TaskCommand.Delete(1, RemoveFolder: false));

        fixture.Captured.Should().Contain("[task] deleted 0001 \"doomed\"");
        fixture.Captured.Should().Contain("folder left in place");
        fixture.Captured.Should().Contain("--remove-folder");
        (await fixture.Store.GetAsync(1)).Should().BeNull();
        Directory.Exists(Path.Combine(fixture.HomeCwd, ".task", "0001-doomed")).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_with_remove_folder_drops_row_and_folder()
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.LightModel.QueueTitle("annihilate");
        await fixture.Handler.HandleAsync(new TaskCommand.New("a", fixture.HomeCwd));
        await fixture.Store.UpdateStatusAsync(1, TaskItemStatus.Idle, whenUnixMs: 1);
        fixture.Captured = "";

        await fixture.Handler.HandleAsync(new TaskCommand.Delete(1, RemoveFolder: true));

        fixture.Captured.Should().Contain("[task] deleted 0001 \"annihilate\"");
        fixture.Captured.Should().Contain("removed folder:");
        (await fixture.Store.GetAsync(1)).Should().BeNull();
        Directory.Exists(Path.Combine(fixture.HomeCwd, ".task", "0001-annihilate")).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_with_remove_folder_when_folder_already_gone_says_so()
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.LightModel.QueueTitle("ghost");
        await fixture.Handler.HandleAsync(new TaskCommand.New("g", fixture.HomeCwd));
        await fixture.Store.UpdateStatusAsync(1, TaskItemStatus.Idle, whenUnixMs: 1);
        // Reproduce the 2026-05-09 stale-data scenario: build wiped the home folder.
        Directory.Delete(Path.Combine(fixture.HomeCwd, ".task", "0001-ghost"), recursive: true);
        fixture.Captured = "";

        await fixture.Handler.HandleAsync(new TaskCommand.Delete(1, RemoveFolder: true));

        fixture.Captured.Should().Contain("[task] deleted 0001 \"ghost\"");
        fixture.Captured.Should().Contain("folder already absent");
        (await fixture.Store.GetAsync(1)).Should().BeNull();
    }

    [Fact]
    public async Task Help_prints_command_summary()
    {
        await using var fixture = await Fixture.OpenAsync();

        await fixture.Handler.HandleAsync(new TaskCommand.Help());

        fixture.Captured.Should().Contain("/task new").And.Contain("/task list").And.Contain("/task show");
        fixture.Captured.Should().Contain("/task fork").And.Contain("/task merge").And.Contain("/task switch");
        fixture.Captured.Should().Contain("/task delete");
    }

    [Fact]
    public async Task Fork_unknown_id_prints_error_message()
    {
        await using var fixture = await Fixture.OpenAsync();

        await fixture.Handler.HandleAsync(new TaskCommand.Fork(999, null));

        fixture.Captured.Should().Contain("[task] error:").And.Contain("999");
    }

    [Fact]
    public async Task New_with_explicit_home_creates_task_under_that_home()
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.LightModel.QueueTitle("Custom-home task");
        var customHome = Path.Combine(fixture.TempPath, "custom-home");

        await fixture.Handler.HandleAsync(new TaskCommand.New("describe", customHome));

        var record = (await fixture.Store.ListAsync()).Single();
        record.HomeCwd.Should().Be(Path.GetFullPath(customHome));
        Directory.Exists(Path.Combine(customHome, ".task", $"0001-{Slugify.For("Custom-home task")}"))
            .Should().BeTrue();
    }

    [Fact]
    public void ResolveHome_returns_default_when_arg_is_null_or_blank()
    {
        // Pure-function test — does not call HandleAsync, so no `.task/` folder gets created
        // under the developer's real ~/.infinicontext/ during the test run.
        TaskCommandHandler.ResolveHome(null).Should().Be(TaskCommandHandler.DefaultHomeCwd);
        TaskCommandHandler.ResolveHome("").Should().Be(TaskCommandHandler.DefaultHomeCwd);
        TaskCommandHandler.ResolveHome("   ").Should().Be(TaskCommandHandler.DefaultHomeCwd);
        TaskCommandHandler.DefaultHomeCwd.Should().EndWith(".infinicontext");
    }

    [Fact]
    public void ResolveHome_returns_absolute_path_for_explicit_home()
    {
        var resolved = TaskCommandHandler.ResolveHome("custom/relative");
        Path.IsPathRooted(resolved).Should().BeTrue();
        resolved.Should().EndWith($"custom{Path.DirectorySeparatorChar}relative");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TaskStore Store { get; init; }
        public required TaskCommandHandler Handler { get; init; }
        public required QueueLightModel LightModel { get; init; }
        public required CapturingWriter Writer { get; init; }
        public required string TempPath { get; init; }
        public required string HomeCwd { get; init; }

        public string Captured
        {
            get => Writer.ToString();
            set => Writer.Reset();
        }

        public static async Task<Fixture> OpenAsync()
        {
            var temp = Path.Combine(Path.GetTempPath(), $"cp-taskhandler-{Guid.NewGuid():N}");
            var home = Path.Combine(temp, "repo");
            Directory.CreateDirectory(home);

            var store = await TaskStore.OpenAsync(":memory:");
            var lightModel = new QueueLightModel();
            var service = new TaskService(store, lightModel);
            var writer = new CapturingWriter();
            var handler = new TaskCommandHandler(service, writer);

            return new Fixture
            {
                Store = store,
                Handler = handler,
                LightModel = lightModel,
                Writer = writer,
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

    private sealed class CapturingWriter : StringWriter
    {
        public void Reset()
        {
            GetStringBuilder().Clear();
        }
    }

    private sealed class QueueLightModel : ILightModelService
    {
        private readonly Queue<string> _titles = new();

        public void QueueTitle(string title) => _titles.Enqueue(title);

        public Task<string> GenerateShortTitleAsync(string description, int maxWords = 6, CancellationToken cancellationToken = default)
        {
            if (_titles.Count == 0) throw new InvalidOperationException("No queued title for this call.");
            return Task.FromResult(_titles.Dequeue());
        }

        public Task<string> SummarizeAsync(string text, int maxSentences = 2, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CategoryResult> CategorizeAsync(string text, IReadOnlyList<string> categories, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SafetyReview> ReviewToolCallSafetyAsync(SafetyReviewRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
