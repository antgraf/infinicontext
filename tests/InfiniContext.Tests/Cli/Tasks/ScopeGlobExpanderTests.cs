using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Cli.Tasks;

public class ScopeGlobExpanderTests : IDisposable
{
    private readonly string _root;

    public ScopeGlobExpanderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "infinicontext-scope-glob-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string Seed(string relativePath, string content = "")
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public void Returns_empty_for_null_or_blank_input()
    {
        ScopeGlobExpander.Expand(null, _root).Should().BeEmpty();
        ScopeGlobExpander.Expand("", _root).Should().BeEmpty();
        ScopeGlobExpander.Expand("   ", _root).Should().BeEmpty();
    }

    [Fact]
    public void Literal_path_passes_through_unchanged()
    {
        var result = ScopeGlobExpander.Expand("src/Foo.cs", _root);
        result.Should().Equal("src/Foo.cs");
    }

    [Fact]
    public void Literal_paths_separated_by_semicolons_pass_through_in_order()
    {
        var result = ScopeGlobExpander.Expand("src/Foo.cs;tests/Bar.cs;docs/x.md", _root);
        result.Should().Equal("src/Foo.cs", "tests/Bar.cs", "docs/x.md");
    }

    [Fact]
    public void Backslashes_are_normalised_to_forward_slashes()
    {
        var result = ScopeGlobExpander.Expand(@"src\Foo.cs", _root);
        result.Should().Equal("src/Foo.cs");
    }

    [Fact]
    public void Single_star_matches_files_in_one_directory()
    {
        Seed("src/A.cs");
        Seed("src/B.cs");
        Seed("src/sub/C.cs");           // should NOT match src/*.cs
        Seed("src/Notes.md");           // wrong extension

        var result = ScopeGlobExpander.Expand("src/*.cs", _root);

        result.Should().BeEquivalentTo(new[] { "src/A.cs", "src/B.cs" });
    }

    [Fact]
    public void Double_star_matches_recursively()
    {
        Seed("src/A.cs");
        Seed("src/sub/B.cs");
        Seed("src/sub/deep/C.cs");
        Seed("src/sub/Notes.md");

        var result = ScopeGlobExpander.Expand("src/**/*.cs", _root);

        result.Should().BeEquivalentTo(new[] { "src/A.cs", "src/sub/B.cs", "src/sub/deep/C.cs" });
    }

    [Fact]
    public void Mixed_glob_and_literal_tokens_are_both_returned()
    {
        Seed("src/A.cs");
        Seed("src/B.cs");
        Seed("docs/manual.md");

        var result = ScopeGlobExpander.Expand("src/*.cs;docs/manual.md", _root);

        result.Should().Contain("src/A.cs");
        result.Should().Contain("src/B.cs");
        result.Should().Contain("docs/manual.md");
        result.Should().HaveCount(3);
    }

    [Fact]
    public void Wildcard_with_no_matches_returns_empty_list_for_that_token()
    {
        Seed("src/A.cs");

        var result = ScopeGlobExpander.Expand("nonexistent/**/*.foo", _root);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Wildcard_with_no_matches_does_not_drop_sibling_tokens()
    {
        Seed("src/A.cs");

        var result = ScopeGlobExpander.Expand("ghost/**/*.foo;src/A.cs", _root);

        result.Should().Equal("src/A.cs");
    }

    [Fact]
    public void Glob_and_literal_overlap_is_deduplicated()
    {
        Seed("src/A.cs");

        var result = ScopeGlobExpander.Expand("src/*.cs;src/A.cs", _root);

        result.Should().Equal("src/A.cs");
    }

    [Fact]
    public void Two_globs_with_overlapping_matches_are_deduplicated()
    {
        Seed("src/A.cs");
        Seed("src/sub/B.cs");

        var result = ScopeGlobExpander.Expand("src/*.cs;src/**/*.cs", _root);

        result.Should().BeEquivalentTo(new[] { "src/A.cs", "src/sub/B.cs" });
    }

    [Fact]
    public void Empty_tokens_are_skipped()
    {
        var result = ScopeGlobExpander.Expand(";src/Foo.cs;;tests/Bar.cs;", _root);
        result.Should().Equal("src/Foo.cs", "tests/Bar.cs");
    }

    [Fact]
    public void Blank_root_throws_when_scope_is_non_empty()
    {
        var act = () => ScopeGlobExpander.Expand("src/*.cs", "");
        act.Should().Throw<ArgumentException>();
    }
}
