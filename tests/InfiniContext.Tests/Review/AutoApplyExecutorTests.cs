using FluentAssertions;
using InfiniContext.Core.Review;

namespace InfiniContext.Tests.Review;

public class AutoApplyExecutorTests
{
    [Fact]
    public async Task ApplyAsync_throws_when_repo_root_is_missing()
    {
        var sut = new AutoApplyExecutor(new FakeRunner());
        var act = () => sut.ApplyAsync(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"), Array.Empty<ConsolidatedFinding>(), default);
        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task ApplyAsync_throws_on_blank_repo_root()
    {
        var sut = new AutoApplyExecutor(new FakeRunner());
        var act = () => sut.ApplyAsync("", Array.Empty<ConsolidatedFinding>(), default);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ApplyAsync_with_no_findings_skips_the_gate()
    {
        using var fx = new RepoFixture();
        var runner = new FakeRunner();
        var sut = new AutoApplyExecutor(runner);

        var result = await sut.ApplyAsync(fx.Root, Array.Empty<ConsolidatedFinding>(), default);

        result.Applied.Should().BeEmpty();
        result.Demoted.Should().BeEmpty();
        result.BuildTestGatePassed.Should().BeTrue();
        runner.BuildCalls.Should().Be(0);
        runner.TestCalls.Should().Be(0);
    }

    // ---------- Edit kind ----------

    [Fact]
    public async Task ApplyAsync_applies_an_edit_when_old_text_matches_exactly_once()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/Foo.cs", "public int Foo() { return 1; }");

        var finding = NewEditFinding("src/Foo.cs", oldText: "return 1;", newText: "return 2;");
        var sut = new AutoApplyExecutor(new FakeRunner());

        var result = await sut.ApplyAsync(fx.Root, new[] { finding }, default);

        result.Applied.Should().ContainSingle(f => f.Id == finding.Id);
        result.Demoted.Should().BeEmpty();
        fx.Read("src/Foo.cs").Should().Be("public int Foo() { return 2; }");
    }

    [Fact]
    public async Task ApplyAsync_demotes_edit_when_old_text_not_found()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/Foo.cs", "totally different content");

        var finding = NewEditFinding("src/Foo.cs", oldText: "expected", newText: "replacement");
        var sut = new AutoApplyExecutor(new FakeRunner());

        var result = await sut.ApplyAsync(fx.Root, new[] { finding }, default);

        result.Applied.Should().BeEmpty();
        result.Demoted.Should().ContainSingle();
        result.Demoted[0].Disposition.Should().Be(Disposition.NeedsUser);
        result.Demoted[0].ConsolidatorNote.Should().Contain("source diverged");
    }

    [Fact]
    public async Task ApplyAsync_demotes_edit_when_old_text_is_ambiguous()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/Foo.cs", "x x x");

        var finding = NewEditFinding("src/Foo.cs", oldText: "x", newText: "y");
        var sut = new AutoApplyExecutor(new FakeRunner());

        var result = await sut.ApplyAsync(fx.Root, new[] { finding }, default);

        result.Applied.Should().BeEmpty();
        result.Demoted.Should().ContainSingle(f => f.ConsolidatorNote!.Contains("ambiguous"));
        fx.Read("src/Foo.cs").Should().Be("x x x"); // unchanged
    }

    [Fact]
    public async Task ApplyAsync_demotes_edit_when_target_file_does_not_exist()
    {
        using var fx = new RepoFixture();
        var finding = NewEditFinding("src/Ghost.cs", oldText: "x", newText: "y");
        var sut = new AutoApplyExecutor(new FakeRunner());

        var result = await sut.ApplyAsync(fx.Root, new[] { finding }, default);

        result.Demoted.Should().ContainSingle(f => f.ConsolidatorNote!.Contains("does not exist"));
    }

    // ---------- Path safety ----------

    [Fact]
    public async Task ApplyAsync_demotes_edit_when_path_escapes_repo_root()
    {
        using var fx = new RepoFixture();
        var finding = NewEditFinding("../escape.cs", oldText: "x", newText: "y");
        var sut = new AutoApplyExecutor(new FakeRunner());

        var result = await sut.ApplyAsync(fx.Root, new[] { finding }, default);

        result.Demoted.Should().ContainSingle(f => f.ConsolidatorNote!.Contains("outside repo root"));
    }

    // ---------- ReplaceFile kind ----------

    [Fact]
    public async Task ApplyAsync_replaces_existing_file_for_replace_file_kind()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/Foo.cs", "old contents");

        var finding = NewReplaceFileFinding("src/Foo.cs", newText: "new contents");
        var sut = new AutoApplyExecutor(new FakeRunner());

        var result = await sut.ApplyAsync(fx.Root, new[] { finding }, default);

        result.Applied.Should().ContainSingle();
        fx.Read("src/Foo.cs").Should().Be("new contents");
    }

    [Fact]
    public async Task ApplyAsync_creates_a_new_file_for_replace_file_kind_when_target_missing()
    {
        using var fx = new RepoFixture();
        var finding = NewReplaceFileFinding("src/New.cs", newText: "fresh contents");
        var sut = new AutoApplyExecutor(new FakeRunner());

        var result = await sut.ApplyAsync(fx.Root, new[] { finding }, default);

        result.Applied.Should().ContainSingle();
        fx.Read("src/New.cs").Should().Be("fresh contents");
    }

    // ---------- Manual / missing-fix demotions ----------

    [Fact]
    public async Task ApplyAsync_demotes_manual_fixes()
    {
        using var fx = new RepoFixture();
        var finding = NewEditFinding("src/Foo.cs", oldText: "x", newText: "y")
            with
        { SuggestedFix = new SuggestedFix(FixKind.Manual, null, null) };
        var sut = new AutoApplyExecutor(new FakeRunner());

        var result = await sut.ApplyAsync(fx.Root, new[] { finding }, default);

        result.Demoted.Should().ContainSingle(f => f.ConsolidatorNote!.Contains("Manual"));
    }

    [Fact]
    public async Task ApplyAsync_demotes_when_suggested_fix_is_null()
    {
        using var fx = new RepoFixture();
        var finding = NewEditFinding("src/Foo.cs", "x", "y") with { SuggestedFix = null };
        var sut = new AutoApplyExecutor(new FakeRunner());

        var result = await sut.ApplyAsync(fx.Root, new[] { finding }, default);

        result.Demoted.Should().ContainSingle(f => f.ConsolidatorNote!.Contains("SuggestedFix is null"));
    }

    // ---------- Build / test gate ----------

    [Fact]
    public async Task ApplyAsync_reverts_when_build_gate_fails()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/Foo.cs", "original");
        var finding = NewReplaceFileFinding("src/Foo.cs", newText: "edited");

        var runner = new FakeRunner
        {
            BuildOutcome = new BuildTestOutcome(Passed: false, FailureSummary: "1 error in StreamJsonParser.cs"),
        };
        var sut = new AutoApplyExecutor(runner);

        var result = await sut.ApplyAsync(fx.Root, new[] { finding }, default);

        result.Applied.Should().BeEmpty();
        result.Demoted.Should().ContainSingle();
        result.Demoted[0].ConsolidatorNote.Should().Contain("build broke");
        result.BuildTestGatePassed.Should().BeFalse();
        result.GateReversionReason.Should().Contain("StreamJsonParser");
        fx.Read("src/Foo.cs").Should().Be("original"); // reverted
        runner.TestCalls.Should().Be(0); // build failed тЖТ test never ran
    }

    [Fact]
    public async Task ApplyAsync_reverts_when_test_gate_fails()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/Foo.cs", "original");
        var finding = NewReplaceFileFinding("src/Foo.cs", newText: "edited");

        var runner = new FakeRunner
        {
            BuildOutcome = new BuildTestOutcome(Passed: true, FailureSummary: null),
            TestOutcome = new BuildTestOutcome(Passed: false, FailureSummary: "2 tests failed in ReviewServiceTests"),
        };
        var sut = new AutoApplyExecutor(runner);

        var result = await sut.ApplyAsync(fx.Root, new[] { finding }, default);

        result.Applied.Should().BeEmpty();
        result.Demoted.Should().ContainSingle();
        result.Demoted[0].ConsolidatorNote.Should().Contain("tests broke");
        result.BuildTestGatePassed.Should().BeFalse();
        result.GateReversionReason.Should().Contain("ReviewServiceTests");
        fx.Read("src/Foo.cs").Should().Be("original"); // reverted
    }

    [Fact]
    public async Task ApplyAsync_reverts_a_replace_file_that_created_a_new_file_by_deleting_it()
    {
        using var fx = new RepoFixture();
        var finding = NewReplaceFileFinding("src/Created.cs", newText: "new file content");

        var runner = new FakeRunner
        {
            BuildOutcome = new BuildTestOutcome(Passed: false, FailureSummary: "broken"),
        };
        var sut = new AutoApplyExecutor(runner);

        var result = await sut.ApplyAsync(fx.Root, new[] { finding }, default);

        result.Applied.Should().BeEmpty();
        File.Exists(Path.Combine(fx.Root, "src", "Created.cs")).Should().BeFalse();
    }

    // ---------- Multi-finding interactions ----------

    [Fact]
    public async Task ApplyAsync_applies_multiple_findings_on_different_files()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/A.cs", "AAA");
        fx.WriteFile("src/B.cs", "BBB");

        var findings = new[]
        {
            NewEditFinding("src/A.cs", "AAA", "aaa"),
            NewEditFinding("src/B.cs", "BBB", "bbb"),
        };

        var sut = new AutoApplyExecutor(new FakeRunner());
        var result = await sut.ApplyAsync(fx.Root, findings, default);

        result.Applied.Should().HaveCount(2);
        fx.Read("src/A.cs").Should().Be("aaa");
        fx.Read("src/B.cs").Should().Be("bbb");
    }

    [Fact]
    public async Task ApplyAsync_demotes_a_later_finding_whose_old_text_was_overwritten_by_an_earlier_one()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/Foo.cs", "alpha beta");

        // Two findings on the same file: first replaces "alpha", second tries to replace "alpha"
        // again тАФ but it's no longer there after the first edit.
        var findings = new[]
        {
            NewEditFinding("src/Foo.cs", "alpha", "gamma"),
            NewEditFinding("src/Foo.cs", "alpha", "delta"),
        };

        var sut = new AutoApplyExecutor(new FakeRunner());
        var result = await sut.ApplyAsync(fx.Root, findings, default);

        result.Applied.Should().HaveCount(1);
        result.Demoted.Should().ContainSingle();
        result.Demoted[0].ConsolidatorNote.Should().Contain("source diverged");
        fx.Read("src/Foo.cs").Should().Be("gamma beta");
    }

    [Fact]
    public async Task ApplyAsync_reverts_every_touched_file_when_gate_fails_after_multiple_applies()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/A.cs", "AAA");
        fx.WriteFile("src/B.cs", "BBB");

        var findings = new[]
        {
            NewEditFinding("src/A.cs", "AAA", "aaa"),
            NewEditFinding("src/B.cs", "BBB", "bbb"),
        };

        var runner = new FakeRunner
        {
            TestOutcome = new BuildTestOutcome(Passed: false, FailureSummary: "broken"),
        };
        var sut = new AutoApplyExecutor(runner);

        var result = await sut.ApplyAsync(fx.Root, findings, default);

        result.Applied.Should().BeEmpty();
        result.Demoted.Should().HaveCount(2);
        fx.Read("src/A.cs").Should().Be("AAA");
        fx.Read("src/B.cs").Should().Be("BBB");
    }

    // ---------- Ctor ----------

    [Fact]
    public void Ctor_throws_on_null_runner()
    {
        var act = () => new AutoApplyExecutor(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---------- Helpers ----------

    private static ConsolidatedFinding NewEditFinding(string file, string oldText, string newText) => new(
        Id: $"e-{Guid.NewGuid():N}".Substring(0, 8),
        Severity: Severity.Warning,
        Title: "edit",
        Summary: "summary",
        Why: "why",
        File: file,
        LineStart: 1,
        LineEnd: 1,
        SuggestedFix: new SuggestedFix(FixKind.Edit, oldText, newText),
        Confidence: 0.95,
        Reviewers: new[] { "code" },
        Disposition: Disposition.AutoApply,
        DispositionConfidence: 0.95,
        RejectionReason: null,
        ConsolidatorNote: null);

    private static ConsolidatedFinding NewReplaceFileFinding(string file, string newText) => new(
        Id: $"r-{Guid.NewGuid():N}".Substring(0, 8),
        Severity: Severity.Warning,
        Title: "replace",
        Summary: "summary",
        Why: "why",
        File: file,
        LineStart: null,
        LineEnd: null,
        SuggestedFix: new SuggestedFix(FixKind.ReplaceFile, null, newText),
        Confidence: 0.95,
        Reviewers: new[] { "code" },
        Disposition: Disposition.AutoApply,
        DispositionConfidence: 0.95,
        RejectionReason: null,
        ConsolidatorNote: null);

    private sealed class RepoFixture : IDisposable
    {
        public string Root { get; }

        public RepoFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"autoapply-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public void WriteFile(string rel, string content)
        {
            var full = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public string Read(string rel)
        {
            var full = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
            return File.ReadAllText(full);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class FakeRunner : IBuildTestRunner
    {
        public int BuildCalls { get; private set; }
        public int TestCalls { get; private set; }
        public BuildTestOutcome BuildOutcome { get; set; } = new(Passed: true, FailureSummary: null);
        public BuildTestOutcome TestOutcome { get; set; } = new(Passed: true, FailureSummary: null);

        public Task<BuildTestOutcome> BuildAsync(string repoRoot, CancellationToken ct)
        {
            BuildCalls++;
            return Task.FromResult(BuildOutcome);
        }

        public Task<BuildTestOutcome> TestAsync(string repoRoot, CancellationToken ct)
        {
            TestCalls++;
            return Task.FromResult(TestOutcome);
        }
    }
}
