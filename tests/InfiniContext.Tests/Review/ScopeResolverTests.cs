using FluentAssertions;
using InfiniContext.Core.Review;

namespace InfiniContext.Tests.Review;

public class ScopeResolverTests
{
    [Fact]
    public async Task ResolveAsync_uses_scope_override_verbatim_when_supplied()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/A.cs", "// a");
        fx.WriteFile("src/B.cs", "// b");
        var git = new FakeGitRunner { Head = "aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111" };
        var sut = new ScopeResolver(git);

        var scope = await sut.ResolveAsync(fx.Root, baseCommitSha: "0011" + new string('0', 36),
            scopeOverridePaths: new[] { "src/A.cs", "src/B.cs" }, progressMdPath: null);

        scope.Description.Should().Contain("--scope override");
        scope.Files.Should().HaveCount(2);
        scope.Files.Select(f => f.RelativePath).Should().BeEquivalentTo(new[] { "src/A.cs", "src/B.cs" });
        scope.FallbackNote.Should().BeNull();
        git.DiffCalls.Should().Be(0); // override skips git entirely
    }

    [Fact]
    public async Task ResolveAsync_uses_baseCommitSha_when_set()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/Foo.cs", "// foo");
        var git = new FakeGitRunner
        {
            Head = "h",
            DiffResult = ["src/Foo.cs"],
        };
        var sut = new ScopeResolver(git);

        var sha = "cafef00d" + new string('0', 32);
        var scope = await sut.ResolveAsync(fx.Root, baseCommitSha: sha, scopeOverridePaths: null, progressMdPath: null);

        scope.FallbackNote.Should().BeNull();
        scope.Files.Should().ContainSingle(f => f.RelativePath == "src/Foo.cs");
        git.LastDiffBaseRef.Should().Be(sha);
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_merge_base_when_baseCommitSha_is_null()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/Foo.cs", "// foo");
        var mergeBase = "deadbeef" + new string('0', 32);
        var git = new FakeGitRunner
        {
            Head = "h",
            MergeBase = mergeBase,
            DiffResult = ["src/Foo.cs"],
        };
        var sut = new ScopeResolver(git);

        var scope = await sut.ResolveAsync(fx.Root, baseCommitSha: null, scopeOverridePaths: null, progressMdPath: null);

        scope.FallbackNote.Should().NotBeNull();
        scope.FallbackNote.Should().Contain("merge-base");
        git.LastDiffBaseRef.Should().Be(mergeBase);
    }

    [Fact]
    public async Task ResolveAsync_reports_when_no_base_resolvable()
    {
        using var fx = new RepoFixture();
        var git = new FakeGitRunner { MergeBase = null };
        var sut = new ScopeResolver(git);

        var scope = await sut.ResolveAsync(fx.Root, baseCommitSha: null, scopeOverridePaths: null, progressMdPath: null);

        scope.Files.Should().BeEmpty();
        scope.FallbackNote.Should().NotBeNull();
        scope.FallbackNote.Should().Contain("--scope");
    }

    [Fact]
    public async Task ResolveAsync_unions_progress_md_files_touched_with_diff()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/FromDiff.cs", "// d");
        fx.WriteFile("src/FromProgress.cs", "// p");
        var progressPath = Path.Combine(fx.Root, "progress.md");
        await File.WriteAllTextAsync(progressPath, """
            # Progress

            ## 2026-05-17

            ### Files touched this session

            - `src/FromProgress.cs` - some change
            """);
        var git = new FakeGitRunner
        {
            Head = "h",
            DiffResult = ["src/FromDiff.cs"],
        };
        var sut = new ScopeResolver(git);

        var scope = await sut.ResolveAsync(fx.Root, baseCommitSha: "abcd" + new string('0', 36),
            scopeOverridePaths: null, progressMdPath: progressPath);

        scope.Files.Select(f => f.RelativePath).Should()
            .BeEquivalentTo(new[] { "src/FromDiff.cs", "src/FromProgress.cs" });
    }

    [Fact]
    public async Task ResolveAsync_dedups_when_diff_and_progress_overlap()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/Both.cs", "// both");
        var progressPath = Path.Combine(fx.Root, "progress.md");
        await File.WriteAllTextAsync(progressPath, """
            # Progress

            ### Files touched

            - `src/Both.cs` - also listed in diff
            """);
        var git = new FakeGitRunner
        {
            Head = "h",
            DiffResult = ["src/Both.cs"],
        };
        var sut = new ScopeResolver(git);

        var scope = await sut.ResolveAsync(fx.Root, baseCommitSha: "abcd" + new string('0', 36),
            scopeOverridePaths: null, progressMdPath: progressPath);

        scope.Files.Should().ContainSingle(f => f.RelativePath == "src/Both.cs");
    }

    [Fact]
    public async Task ResolveAsync_excludes_files_above_byte_cap()
    {
        using var fx = new RepoFixture();
        fx.WriteFile("src/Small.cs", new string('a', 100));
        fx.WriteFile("src/Big.cs", new string('a', 5_000));
        var git = new FakeGitRunner
        {
            Head = "h",
            DiffResult = ["src/Small.cs", "src/Big.cs"],
        };
        var sut = new ScopeResolver(git, byteCap: 1_000);

        var scope = await sut.ResolveAsync(fx.Root, baseCommitSha: "abcd" + new string('0', 36),
            scopeOverridePaths: null, progressMdPath: null);

        scope.Files.Should().ContainSingle(f => f.RelativePath == "src/Small.cs");
        scope.ExcludedFiles.Should().ContainSingle(e => e.RelativePath == "src/Big.cs");
        scope.ExcludedFiles[0].Reason.Should().Contain("byte cap");
    }

    [Fact]
    public async Task ResolveAsync_excludes_files_that_do_not_exist()
    {
        using var fx = new RepoFixture();
        var git = new FakeGitRunner
        {
            Head = "h",
            DiffResult = ["src/Ghost.cs"],
        };
        var sut = new ScopeResolver(git);

        var scope = await sut.ResolveAsync(fx.Root, baseCommitSha: "abcd" + new string('0', 36),
            scopeOverridePaths: null, progressMdPath: null);

        scope.Files.Should().BeEmpty();
        scope.ExcludedFiles.Should().ContainSingle(e => e.RelativePath == "src/Ghost.cs");
        scope.ExcludedFiles[0].Reason.Should().Contain("does not exist");
    }

    [Fact]
    public async Task ResolveAsync_excludes_paths_that_escape_the_repo_root()
    {
        using var fx = new RepoFixture();
        var git = new FakeGitRunner();
        var sut = new ScopeResolver(git);

        var scope = await sut.ResolveAsync(fx.Root, baseCommitSha: "abcd" + new string('0', 36),
            scopeOverridePaths: new[] { "../escape.cs" }, progressMdPath: null);

        scope.Files.Should().BeEmpty();
        scope.ExcludedFiles.Should().ContainSingle(e => e.Reason.Contains("outside"));
    }

    [Fact]
    public async Task ResolveAsync_throws_for_blank_repo_root()
    {
        var sut = new ScopeResolver(new FakeGitRunner());
        var act = () => sut.ResolveAsync("", baseCommitSha: null, scopeOverridePaths: null, progressMdPath: null);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Ctor_rejects_non_positive_byte_cap()
    {
        var act = () => new ScopeResolver(new FakeGitRunner(), byteCap: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---------- ParseFilesTouched ----------

    [Fact]
    public void ParseFilesTouched_finds_paths_under_files_touched_heading()
    {
        var md = """
            # Progress

            ## 2026-05-17

            ### Files touched this session

            - `src/Foo.cs` - some change
            - `src/Bar.cs` (new)
            - not a path
            - `also/with-dashes.md`
            """;

        ScopeResolver.ParseFilesTouched(md).Should().BeEquivalentTo(new[]
        {
            "src/Foo.cs",
            "src/Bar.cs",
            "also/with-dashes.md",
        });
    }

    [Fact]
    public void ParseFilesTouched_ignores_paths_outside_the_section()
    {
        var md = """
            # Progress

            Some preamble with a `src/Outside.cs` reference.

            ### Files touched

            - `src/Inside.cs`

            ### Other section

            - `src/AlsoOutside.cs`
            """;

        ScopeResolver.ParseFilesTouched(md).Should().BeEquivalentTo(new[] { "src/Inside.cs" });
    }

    [Fact]
    public void ParseFilesTouched_filters_out_non_path_backticks()
    {
        var md = """
            ### Files touched

            - `src/Real.cs` - real path
            - `Just a quoted phrase` - no slash
            - `git status` - no slash either
            """;

        ScopeResolver.ParseFilesTouched(md).Should().BeEquivalentTo(new[] { "src/Real.cs" });
    }

    [Fact]
    public void ParseFilesTouched_handles_case_insensitive_heading_match()
    {
        var md = """
            ### FILES TOUCHED This Session

            - `src/Yes.cs`
            """;
        ScopeResolver.ParseFilesTouched(md).Should().BeEquivalentTo(new[] { "src/Yes.cs" });
    }

    [Fact]
    public void ParseFilesTouched_returns_empty_for_empty_input()
    {
        ScopeResolver.ParseFilesTouched("").Should().BeEmpty();
    }

    // ---------- Helpers ----------

    private sealed class RepoFixture : IDisposable
    {
        public string Root { get; }

        public RepoFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"scoperesolver-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public void WriteFile(string rel, string content)
        {
            var full = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class FakeGitRunner : IGitRunner
    {
        public string? Head { get; set; } = "head1234" + new string('0', 32);
        public string? MergeBase { get; set; }
        public IReadOnlyList<string> DiffResult { get; set; } = Array.Empty<string>();
        public int DiffCalls { get; private set; }
        public string? LastDiffBaseRef { get; private set; }

        public Task<string?> ResolveHeadAsync(string repoRoot, CancellationToken ct) => Task.FromResult(Head);
        public Task<string?> ResolveMergeBaseAsync(string repoRoot, string ref1, string ref2, CancellationToken ct) => Task.FromResult(MergeBase);
        public Task<IReadOnlyList<string>> DiffNameOnlyAsync(string repoRoot, string baseRef, string headRef, CancellationToken ct)
        {
            DiffCalls++;
            LastDiffBaseRef = baseRef;
            return Task.FromResult(DiffResult);
        }
    }
}
