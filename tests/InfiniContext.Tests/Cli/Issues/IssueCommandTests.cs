using InfiniContext.Core.Issues;
using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Cli.Issues;

public class IssueCommandTests
{
    [Theory]
    [InlineData("/issue")]
    [InlineData("/issue help")]
    [InlineData("/issue ?")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parses_help_for_bare_or_help_input(string input)
    {
        IssueCommand.TryParse(input, out var cmd, out var err).Should().BeTrue();
        err.Should().BeNull();
        cmd.Should().BeOfType<IssueCommand.Help>();
    }

    [Fact]
    public void Capture_with_quoted_title_defaults_kind_to_followup()
    {
        IssueCommand.TryParse("/issue \"The cache stores both forms\"", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new IssueCommand.Add("The cache stores both forms", IssueKind.Followup, Where: null));
    }

    [Fact]
    public void Capture_with_kind_flag()
    {
        IssueCommand.TryParse("/issue \"flaky test\" --kind bug", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new IssueCommand.Add("flaky test", IssueKind.Bug, Where: null));
    }

    [Fact]
    public void Capture_with_where_and_kind_flags()
    {
        IssueCommand.TryParse("/issue \"dead code\" --kind smell --where src/Foo.cs:42", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new IssueCommand.Add("dead code", IssueKind.Smell, Where: "src/Foo.cs:42"));
    }

    [Fact]
    public void Capture_with_unquoted_multi_word_title()
    {
        IssueCommand.TryParse("/issue unhandled error in parser --kind bug", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new IssueCommand.Add("unhandled error in parser", IssueKind.Bug, Where: null));
    }

    [Theory]
    [InlineData("--kind")]
    [InlineData("--where")]
    public void Capture_with_no_title_before_flag_errors(string flag)
    {
        IssueCommand.TryParse($"/issue {flag} bug", out _, out var err).Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Capture_with_unknown_kind_errors()
    {
        IssueCommand.TryParse("/issue \"x\" --kind catastrophe", out _, out var err).Should().BeFalse();
        err.Should().Contain("--kind");
    }

    [Fact]
    public void Capture_with_unknown_flag_errors()
    {
        IssueCommand.TryParse("/issue \"x\" --bogus value", out _, out var err).Should().BeFalse();
        err.Should().Contain("--bogus");
    }

    [Fact]
    public void List_subcommand()
    {
        IssueCommand.TryParse("/issue list", out var cmd, out _).Should().BeTrue();
        cmd.Should().BeOfType<IssueCommand.List>();
    }

    [Fact]
    public void List_with_unexpected_args_errors()
    {
        IssueCommand.TryParse("/issue list --status foo", out _, out var err).Should().BeFalse();
        err.Should().Contain("takes no arguments");
    }

    [Fact]
    public void Resolve_with_id_only_defaults_to_resolved_no_note()
    {
        IssueCommand.TryParse("/issue resolve 3", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new IssueCommand.Resolve(3, IssueStatus.Resolved, Note: null));
    }

    [Fact]
    public void Resolve_with_trailing_note()
    {
        IssueCommand.TryParse("/issue resolve 3 fixed in commit abc123", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new IssueCommand.Resolve(3, IssueStatus.Resolved, Note: "fixed in commit abc123"));
    }

    [Fact]
    public void Resolve_with_as_and_trailing_note()
    {
        IssueCommand.TryParse("/issue resolve 3 --as wont-fix not worth the churn", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new IssueCommand.Resolve(3, IssueStatus.WontFix, Note: "not worth the churn"));
    }

    [Fact]
    public void Resolve_with_note_switch()
    {
        IssueCommand.TryParse("/issue resolve 3 --note \"scripted note\"", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new IssueCommand.Resolve(3, IssueStatus.Resolved, Note: "scripted note"));
    }

    [Fact]
    public void Resolve_note_switch_wins_over_trailing_text()
    {
        IssueCommand.TryParse("/issue resolve 3 --note \"pinned\" some other text", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new IssueCommand.Resolve(3, IssueStatus.Resolved, Note: "pinned"));
    }

    [Fact]
    public void Resolve_with_as_deferred_and_no_note()
    {
        IssueCommand.TryParse("/issue resolve 7 --as deferred", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new IssueCommand.Resolve(7, IssueStatus.Deferred, Note: null));
    }

    [Fact]
    public void Resolve_rejects_open_as_target_status()
    {
        IssueCommand.TryParse("/issue resolve 1 --as open", out _, out var err).Should().BeFalse();
        err.Should().Contain("--as");
    }

    [Fact]
    public void Resolve_rejects_missing_id()
    {
        IssueCommand.TryParse("/issue resolve", out _, out var err).Should().BeFalse();
        err.Should().Contain("local id");
    }

    [Fact]
    public void Resolve_rejects_non_numeric_id()
    {
        IssueCommand.TryParse("/issue resolve foo", out _, out var err).Should().BeFalse();
        err.Should().Contain("local id");
    }

    [Fact]
    public void Bare_subcommand_form_works()
    {
        // Used by /task issue routing: passes "issue list" without the leading "/issue".
        IssueCommand.TryParse("/issue list", out var via1, out _).Should().BeTrue();
        IssueCommand.TryParse("list", out var via2, out _).Should().BeTrue();
        via1.Should().Be(via2);
    }
}
