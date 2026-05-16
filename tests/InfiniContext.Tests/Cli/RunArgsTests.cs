using InfiniContext.Cli;
using InfiniContext.Core.Configuration;
using FluentAssertions;

namespace InfiniContext.Tests.Cli;

/// <summary>
/// Direct unit tests for <see cref="RunArgs.TryParse"/>. The CLI source is linked into the test
/// assembly via <c>&lt;Compile Include="…RunArgs.cs" Link="…" /&gt;</c> so we can test it without
/// spawning the binary.
/// </summary>
public class RunArgsTests
{
    [Fact]
    public void No_args_defaults_cwd_to_current_directory()
    {
        Tokens(out var tokens);
        RunArgs.TryParse(tokens, out var args, out var error).Should().BeTrue();
        error.Should().BeNull();
        args.Cwd.Should().Be(Path.GetFullPath(Directory.GetCurrentDirectory()));
        args.Model.Should().BeNull();
        args.ResumeSessionId.Should().BeNull();
        args.IncludePartialMessages.Should().BeFalse();
        args.ClaudeExecutable.Should().BeNull();
        args.ExtraArgs.Should().BeEmpty();
    }

    [Fact]
    public void Resume_flag_captures_session_id()
    {
        Tokens(out var tokens, "--resume", "abc-123");
        RunArgs.TryParse(tokens, out var args, out var _).Should().BeTrue();
        args.ResumeSessionId.Should().Be("abc-123");
    }

    [Fact]
    public void Resume_flag_without_value_is_an_error()
    {
        Tokens(out var tokens, "--resume");
        RunArgs.TryParse(tokens, out var _, out var error).Should().BeFalse();
        error.Should().Contain("--resume");
    }

    [Fact]
    public void Model_and_partial_combine()
    {
        Tokens(out var tokens, "--model", "haiku", "--include-partial-messages");
        RunArgs.TryParse(tokens, out var args, out var _).Should().BeTrue();
        args.Model.Should().Be("haiku");
        args.IncludePartialMessages.Should().BeTrue();
    }

    [Fact]
    public void Unknown_option_is_an_error()
    {
        Tokens(out var tokens, "--bogus");
        RunArgs.TryParse(tokens, out var _, out var error).Should().BeFalse();
        error.Should().Contain("--bogus");
    }

    [Fact]
    public void Double_dash_routes_remainder_to_extra_args_verbatim()
    {
        Tokens(out var tokens, "--", "--bogus", "--another", "value");
        RunArgs.TryParse(tokens, out var args, out var _).Should().BeTrue();
        args.ExtraArgs.Should().Equal("--bogus", "--another", "value");
    }

    [Fact]
    public void Positional_cwd_argument_resolves_to_absolute_existing_directory()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Tokens(out var tokens, tempDir);
            RunArgs.TryParse(tokens, out var args, out var _).Should().BeTrue();
            args.Cwd.Should().Be(Path.GetFullPath(tempDir));
        }
        finally
        {
            try { Directory.Delete(tempDir); } catch { }
        }
    }

    [Fact]
    public void Nonexistent_cwd_is_an_error()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"));
        Tokens(out var tokens, bogus);
        RunArgs.TryParse(tokens, out var _, out var error).Should().BeFalse();
        error.Should().Contain("does not exist");
    }

    [Fact]
    public void Two_positional_args_is_an_error()
    {
        var temp = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Tokens(out var tokens, temp, "extra");
            RunArgs.TryParse(tokens, out var _, out var error).Should().BeFalse();
            error.Should().Contain("unexpected positional");
        }
        finally
        {
            try { Directory.Delete(temp); } catch { }
        }
    }

    [Theory]
    [InlineData("ask", ApprovalMode.Ask)]
    [InlineData("deny", ApprovalMode.Deny)]
    [InlineData("allow", ApprovalMode.Allow)]
    [InlineData("auto", ApprovalMode.Auto)]
    [InlineData("AUTO", ApprovalMode.Auto)]
    public void Approval_mode_flag_parses_each_mode(string raw, ApprovalMode expected)
    {
        Tokens(out var tokens, "--approval-mode", raw);
        RunArgs.TryParse(tokens, out var args, out var _).Should().BeTrue();
        args.ApprovalMode.Should().Be(expected);
    }

    [Fact]
    public void Approval_mode_flag_without_value_is_an_error()
    {
        Tokens(out var tokens, "--approval-mode");
        RunArgs.TryParse(tokens, out var _, out var error).Should().BeFalse();
        error.Should().Contain("--approval-mode");
    }

    [Fact]
    public void Approval_mode_flag_with_garbage_value_is_an_error()
    {
        Tokens(out var tokens, "--approval-mode", "yolo");
        RunArgs.TryParse(tokens, out var _, out var error).Should().BeFalse();
        error.Should().Contain("yolo");
    }

    [Fact]
    public void Approval_mode_flag_is_optional_and_null_when_omitted()
    {
        Tokens(out var tokens);
        RunArgs.TryParse(tokens, out var args, out var _).Should().BeTrue();
        args.ApprovalMode.Should().BeNull();
    }

    [Fact]
    public void Claude_exe_override_is_captured()
    {
        Tokens(out var tokens, "--claude-exe", "/usr/local/bin/claude");
        RunArgs.TryParse(tokens, out var args, out var _).Should().BeTrue();
        args.ClaudeExecutable.Should().Be("/usr/local/bin/claude");
    }

    private static void Tokens(out IReadOnlyList<string> tokens, params string[] values) =>
        tokens = values;
}
