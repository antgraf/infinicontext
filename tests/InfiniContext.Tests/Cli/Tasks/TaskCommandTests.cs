using InfiniContext.Cli.Tasks;
using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Cli.Tasks;

public class TaskCommandTests
{
    [Theory]
    [InlineData("/task")]
    [InlineData("/task help")]
    [InlineData("/task ?")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parses_help_for_bare_or_help_input(string input)
    {
        TaskCommand.TryParse(input, out var cmd, out var error).Should().BeTrue();
        error.Should().BeNull();
        cmd.Should().BeOfType<TaskCommand.Help>();
    }

    [Fact]
    public void New_with_quoted_description()
    {
        TaskCommand.TryParse("/task new \"implement Phase 5\"", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.New("implement Phase 5"));
    }

    [Fact]
    public void New_with_unquoted_multi_word_description()
    {
        TaskCommand.TryParse("/task new wire the REPL command surface", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.New("wire the REPL command surface"));
    }

    [Fact]
    public void New_with_single_quoted_description()
    {
        TaskCommand.TryParse("/task new 'with apostrophes inside'", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.New("with apostrophes inside"));
    }

    [Theory]
    [InlineData("/task new")]
    [InlineData("/task new \"\"")]
    [InlineData("/task new   ")]
    public void New_rejects_blank_description(string input)
    {
        TaskCommand.TryParse(input, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void New_accepts_home_flag_before_quoted_description()
    {
        TaskCommand.TryParse("/task new --home C:/repo \"implement Phase 5\"", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.New("implement Phase 5", "C:/repo"));
    }

    [Fact]
    public void New_accepts_home_flag_after_quoted_description()
    {
        TaskCommand.TryParse("/task new \"implement Phase 5\" --home C:/repo", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.New("implement Phase 5", "C:/repo"));
    }

    [Fact]
    public void New_accepts_home_flag_before_unquoted_multi_word_description()
    {
        TaskCommand.TryParse("/task new --home C:/repo wire the REPL command surface", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.New("wire the REPL command surface", "C:/repo"));
    }

    [Fact]
    public void New_accepts_home_flag_after_unquoted_multi_word_description()
    {
        TaskCommand.TryParse("/task new wire the REPL command surface --home C:/repo", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.New("wire the REPL command surface", "C:/repo"));
    }

    [Fact]
    public void New_accepts_home_path_with_spaces_when_quoted()
    {
        TaskCommand.TryParse("/task new --home \"C:/Program Files/repo\" \"do thing\"", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.New("do thing", "C:/Program Files/repo"));
    }

    [Fact]
    public void New_omits_home_when_flag_absent()
    {
        TaskCommand.TryParse("/task new \"plain description\"", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.New("plain description", null));
    }

    [Theory]
    [InlineData("/task new --home", "*--home requires*")]
    [InlineData("/task new --home    ", "*--home requires*")]
    public void New_rejects_home_without_value(string input, string expectedErrorPattern)
    {
        TaskCommand.TryParse(input, out _, out var error).Should().BeFalse();
        error.Should().Match(expectedErrorPattern);
    }

    [Fact]
    public void List_with_no_options()
    {
        TaskCommand.TryParse("/task list", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.List(null, null));
    }

    [Fact]
    public void List_alias_ls_works()
    {
        TaskCommand.TryParse("/task ls", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.List(null, null));
    }

    [Fact]
    public void List_with_limit_and_status()
    {
        TaskCommand.TryParse("/task list --limit 5 --status idle", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.List(5, TaskItemStatus.Idle));
    }

    [Theory]
    [InlineData("inprogress", TaskItemStatus.InProgress)]
    [InlineData("in-progress", TaskItemStatus.InProgress)]
    [InlineData("active", TaskItemStatus.InProgress)]
    [InlineData("idle", TaskItemStatus.Idle)]
    [InlineData("IDLE", TaskItemStatus.Idle)]
    public void List_status_aliases(string statusInput, TaskItemStatus expected)
    {
        TaskCommand.TryParse($"/task list --status {statusInput}", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.List(null, expected));
    }

    [Theory]
    [InlineData("/task list --limit abc", "*not a positive integer*")]
    [InlineData("/task list --limit 0", "*not a positive integer*")]
    [InlineData("/task list --limit -3", "*not a positive integer*")]
    [InlineData("/task list --limit", "*--limit requires*")]
    [InlineData("/task list --status bogus", "*not one of*")]
    [InlineData("/task list --status", "*--status requires*")]
    [InlineData("/task list --bogus", "*unknown option*")]
    public void List_rejects_malformed_options(string input, string expectedErrorPattern)
    {
        TaskCommand.TryParse(input, out _, out var error).Should().BeFalse();
        error.Should().Match(expectedErrorPattern);
    }

    [Theory]
    [InlineData("/task show 42", 42)]
    [InlineData("/task show 0042", 42)]    // zero-padded form humans see in /task list
    [InlineData("/task get 7", 7)]         // alias
    public void Show_parses_id(string input, int expectedId)
    {
        TaskCommand.TryParse(input, out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.Show(expectedId));
    }

    [Theory]
    [InlineData("/task show")]
    [InlineData("/task show abc")]
    [InlineData("/task show -1")]
    [InlineData("/task show 0")]
    [InlineData("/task show 42 extra")]
    public void Show_rejects_bad_arguments(string input)
    {
        TaskCommand.TryParse(input, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Fork_with_id_only()
    {
        TaskCommand.TryParse("/task fork 7", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.Fork(7, null));
    }

    [Fact]
    public void Fork_with_override_title()
    {
        TaskCommand.TryParse("/task fork 7 \"Spike: alternative\"", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.Fork(7, "Spike: alternative"));
    }

    [Theory]
    [InlineData("/task fork")]
    [InlineData("/task fork abc")]
    [InlineData("/task fork 1 \"a\" \"b\"")]
    public void Fork_rejects_bad_arguments(string input)
    {
        TaskCommand.TryParse(input, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Merge_two_ids()
    {
        TaskCommand.TryParse("/task merge 1 2", out var cmd, out _).Should().BeTrue();
        cmd.Should().BeOfType<TaskCommand.Merge>()
            .Which.Ids.Should().Equal(new[] { 1, 2 });
    }

    [Fact]
    public void Merge_more_than_two()
    {
        TaskCommand.TryParse("/task merge 1 2 3 4", out var cmd, out _).Should().BeTrue();
        cmd.Should().BeOfType<TaskCommand.Merge>()
            .Which.Ids.Should().Equal(new[] { 1, 2, 3, 4 });
    }

    [Theory]
    [InlineData("/task merge")]
    [InlineData("/task merge 1")]
    [InlineData("/task merge 1 abc")]
    public void Merge_rejects_too_few_or_invalid_ids(string input)
    {
        TaskCommand.TryParse(input, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Delete_parses_id_only()
    {
        TaskCommand.TryParse("/task delete 7", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.Delete(7, RemoveFolder: false));
    }

    [Fact]
    public void Delete_accepts_zero_padded_id()
    {
        TaskCommand.TryParse("/task delete 0042", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.Delete(42, RemoveFolder: false));
    }

    [Fact]
    public void Delete_alias_rm_works()
    {
        TaskCommand.TryParse("/task rm 7", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.Delete(7, RemoveFolder: false));
    }

    [Fact]
    public void Delete_accepts_remove_folder_after_id()
    {
        TaskCommand.TryParse("/task delete 7 --remove-folder", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.Delete(7, RemoveFolder: true));
    }

    [Fact]
    public void Delete_accepts_remove_folder_before_id()
    {
        TaskCommand.TryParse("/task delete --remove-folder 7", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.Delete(7, RemoveFolder: true));
    }

    [Theory]
    [InlineData("/task delete", "*requires a task id*")]
    [InlineData("/task delete --remove-folder", "*requires a task id*")]
    [InlineData("/task delete abc", "*not a valid task id*")]
    [InlineData("/task delete 0", "*not a valid task id*")]
    [InlineData("/task delete -5", "*not a valid task id*")]
    [InlineData("/task delete 1 2", "*extra arguments*")]
    public void Delete_rejects_bad_arguments(string input, string expectedErrorPattern)
    {
        TaskCommand.TryParse(input, out _, out var error).Should().BeFalse();
        error.Should().Match(expectedErrorPattern);
    }

    [Fact]
    public void Switch_parses_id()
    {
        TaskCommand.TryParse("/task switch 0042", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.Switch(42));
    }

    [Fact]
    public void Unknown_subcommand_returns_error()
    {
        TaskCommand.TryParse("/task wat", out _, out var error).Should().BeFalse();
        error.Should().Contain("unknown subcommand 'wat'");
    }

    [Fact]
    public void Bare_form_without_slash_task_prefix_works()
    {
        // Useful for unit tests; the REPL always passes the full "/task ..." line in practice.
        TaskCommand.TryParse("list --limit 3", out var cmd, out _).Should().BeTrue();
        cmd.Should().Be(new TaskCommand.List(3, null));
    }

    [Theory]
    [InlineData("a b c", new[] { "a", "b", "c" })]
    [InlineData("a   b", new[] { "a", "b" })]
    [InlineData("\"hello world\"", new[] { "hello world" })]
    [InlineData("/task new \"foo bar\" 42", new[] { "/task", "new", "foo bar", "42" })]
    [InlineData("'single' \"double\"", new[] { "single", "double" })]
    public void Tokenize_handles_whitespace_and_quotes(string input, string[] expected)
    {
        TaskCommand.Tokenize(input).Should().Equal(expected);
    }
}
