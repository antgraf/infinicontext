using InfiniContext.Core.Sessions;
using FluentAssertions;

namespace InfiniContext.Tests.Sessions;

public class ArgvBuilderTests
{
    private static ClaudeSessionOptions Base() => new() { Cwd = "C:/some/path" };

    [Fact]
    public void Defaults_emit_required_flags_in_canonical_order()
    {
        var args = ClaudeArgvBuilder.BuildArgs(Base());

        args.Should().Equal(
            "--print",
            "--verbose",
            "--input-format", "stream-json",
            "--output-format", "stream-json");
    }

    [Fact]
    public void Verbose_must_precede_output_format()
    {
        // claude rejects --output-format before --verbose; this is the canary that catches a regression.
        var args = ClaudeArgvBuilder.BuildArgs(Base());

        var verboseIdx = args.ToList().IndexOf("--verbose");
        var outputIdx = args.ToList().IndexOf("--output-format");

        verboseIdx.Should().BeGreaterThan(-1);
        outputIdx.Should().BeGreaterThan(-1);
        verboseIdx.Should().BeLessThan(outputIdx);
    }

    [Fact]
    public void Print_is_first()
    {
        var args = ClaudeArgvBuilder.BuildArgs(Base());

        args[0].Should().Be("--print");
    }

    [Fact]
    public void Resume_session_id_emits_resume_flag()
    {
        var args = ClaudeArgvBuilder.BuildArgs(Base() with { ResumeSessionId = "abc-123" });

        args.Should().ContainInOrder("--resume", "abc-123");
    }

    [Fact]
    public void Empty_resume_session_id_does_not_emit_flag()
    {
        var args = ClaudeArgvBuilder.BuildArgs(Base() with { ResumeSessionId = "" });

        args.Should().NotContain("--resume");
    }

    [Fact]
    public void Model_emits_model_flag()
    {
        var args = ClaudeArgvBuilder.BuildArgs(Base() with { Model = "claude-sonnet-4-6" });

        args.Should().ContainInOrder("--model", "claude-sonnet-4-6");
    }

    [Fact]
    public void IncludePartialMessages_emits_flag_without_value()
    {
        var args = ClaudeArgvBuilder.BuildArgs(Base() with { IncludePartialMessages = true });

        args.Should().Contain("--include-partial-messages");
    }

    [Fact]
    public void IncludePartialMessages_default_false_omits_flag()
    {
        var args = ClaudeArgvBuilder.BuildArgs(Base());

        args.Should().NotContain("--include-partial-messages");
    }

    [Fact]
    public void PermissionPromptTool_and_McpConfig_pair_correctly()
    {
        var args = ClaudeArgvBuilder.BuildArgs(Base() with
        {
            PermissionPromptTool = "mcp__infinicontext__approval_prompt",
            McpConfigPath = "C:/tmp/mcp.json",
        });

        args.Should().ContainInOrder("--permission-prompt-tool", "mcp__infinicontext__approval_prompt");
        args.Should().ContainInOrder("--mcp-config", "C:/tmp/mcp.json");
    }

    [Fact]
    public void ExtraArgs_are_appended_verbatim()
    {
        var args = ClaudeArgvBuilder.BuildArgs(Base() with
        {
            ExtraArgs = new[] { "--bare", "--permission-mode", "bypassPermissions" },
        });

        args.Should().EndWith(new[] { "--bare", "--permission-mode", "bypassPermissions" });
    }

    [Fact]
    public void All_options_set_emits_full_combination_in_stable_order()
    {
        var args = ClaudeArgvBuilder.BuildArgs(new ClaudeSessionOptions
        {
            Cwd = "C:/x",
            ResumeSessionId = "11111111-1111-1111-1111-111111111111",
            Model = "claude-haiku-4-5",
            IncludePartialMessages = true,
            PermissionPromptTool = "mcp__infinicontext__approval_prompt",
            McpConfigPath = "C:/tmp/mcp.json",
            StrictMcpConfig = true,
            AllowedTools = new[] { "mcp__infinicontext__approval_prompt", "Bash(git *)" },
            ExtraArgs = new[] { "--bare" },
        });

        args.Should().Equal(
            "--print",
            "--verbose",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--resume", "11111111-1111-1111-1111-111111111111",
            "--model", "claude-haiku-4-5",
            "--include-partial-messages",
            "--permission-prompt-tool", "mcp__infinicontext__approval_prompt",
            "--mcp-config", "C:/tmp/mcp.json",
            "--strict-mcp-config",
            "--allowed-tools", "mcp__infinicontext__approval_prompt,Bash(git *)",
            "--bare");
    }

    [Fact]
    public void StrictMcpConfig_default_false_omits_flag()
    {
        var args = ClaudeArgvBuilder.BuildArgs(Base());

        args.Should().NotContain("--strict-mcp-config");
    }

    [Fact]
    public void AllowedTools_empty_omits_flag()
    {
        var args = ClaudeArgvBuilder.BuildArgs(Base() with { AllowedTools = Array.Empty<string>() });

        args.Should().NotContain("--allowed-tools");
    }

    [Fact]
    public void AllowedTools_single_emits_one_value()
    {
        var args = ClaudeArgvBuilder.BuildArgs(Base() with
        {
            AllowedTools = new[] { "mcp__infinicontext__approval_prompt" },
        });

        args.Should().ContainInOrder("--allowed-tools", "mcp__infinicontext__approval_prompt");
    }

    [Fact]
    public void ResolveExecutable_honors_override_when_file_exists()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            ClaudeArgvBuilder.ResolveExecutable(tmp).Should().Be(tmp);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void ResolveExecutable_ignores_override_that_does_not_exist()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "definitely-not-a-real-claude-" + Guid.NewGuid() + ".exe");

        // Should fall back without throwing. Result is environment-dependent but never the bogus path.
        ClaudeArgvBuilder.ResolveExecutable(bogus).Should().NotBe(bogus);
    }
}
