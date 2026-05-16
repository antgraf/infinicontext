using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using FluentAssertions;

namespace InfiniContext.Tests.LightModel;

/// <summary>
/// Unit tests for the static argv / env-var / fence-stripping helpers shared by both CLI light-model
/// services. These cover the §6 recipe knobs without spawning a process: spawn-time correctness is
/// straight pattern-matching against the published recipe.
/// </summary>
public class CliLightModelInvocationTests
{
    [Fact]
    public void BuildArgv_emits_recipe_flags_in_print_first_then_verbose_then_format_order()
    {
        var argv = CliLightModelInvocation.BuildArgv("claude-haiku-4-5", "<system prompt>");

        argv[0].Should().Be("--print", "claude requires --print first when stream-json formats are used");

        // --verbose must precede --output-format (claude rejects the reverse). This ordering rule
        // is documented in docs/infinicontext-initial-design.md and enforced by ClaudeArgvBuilder; we
        // reaffirm it here so the light-model recipe doesn't drift away from it.
        var verboseIdx = argv.ToList().IndexOf("--verbose");
        var outputFormatIdx = argv.ToList().IndexOf("--output-format");
        verboseIdx.Should().BeGreaterThan(0);
        outputFormatIdx.Should().BeGreaterThan(verboseIdx);
    }

    [Fact]
    public void BuildArgv_emits_max_stripped_recipe_from_published_research()
    {
        var argv = CliLightModelInvocation.BuildArgv("claude-haiku-4-5", "<sys>").ToList();

        // Verbatim against docs/research/claude-cli-light-model.md §3.2 + §6.
        argv.Should().ContainInOrder("--input-format", "stream-json");
        argv.Should().ContainInOrder("--output-format", "stream-json");
        argv.Should().ContainInOrder("--model", "claude-haiku-4-5");
        argv.Should().ContainInOrder("--system-prompt", "<sys>");
        argv.Should().ContainInOrder("--tools", "");
        argv.Should().ContainInOrder("--mcp-config", "{\"mcpServers\":{}}");
        argv.Should().ContainInOrder("--setting-sources", "project");
        argv.Should().Contain("--strict-mcp-config");
        argv.Should().Contain("--disable-slash-commands");
        argv.Should().Contain("--no-session-persistence");
    }

    [Fact]
    public void BuildEnv_with_thinking_off_sets_disable_thinking_and_low_token_cap()
    {
        var cli = new CliLightModelOptions { MaxOutputTokens = 128 };
        var env = CliLightModelInvocation.BuildEnv(cli, thinking: false);

        // The five context-strippers from §3.2 — always on.
        env["ENABLE_CLAUDEAI_MCP_SERVERS"].Should().Be("false");
        env["CLAUDE_CODE_DISABLE_AUTO_MEMORY"].Should().Be("1");
        env["CLAUDE_AGENT_SDK_DISABLE_BUILTIN_AGENTS"].Should().Be("1");
        env["CLAUDE_CODE_DISABLE_CLAUDE_MDS"].Should().Be("1");
        env["CLAUDE_CODE_DISABLE_POLICY_SKILLS"].Should().Be("1");

        // D8 specifics: thinking suppressed at API level + hard token cap.
        env["CLAUDE_CODE_DISABLE_THINKING"].Should().Be("1");
        env["CLAUDE_CODE_MAX_OUTPUT_TOKENS"].Should().Be("128");
    }

    [Fact]
    public void BuildEnv_with_thinking_on_omits_disable_thinking_and_uses_thinking_token_cap()
    {
        var cli = new CliLightModelOptions { MaxOutputTokens = 128, ThinkingMaxOutputTokens = 1024 };
        var env = CliLightModelInvocation.BuildEnv(cli, thinking: true);

        env.Should().NotContainKey("CLAUDE_CODE_DISABLE_THINKING",
            "thinking=true must NOT set the kill switch — that's the whole point");
        env["CLAUDE_CODE_MAX_OUTPUT_TOKENS"].Should().Be("1024",
            "thinking mode uses the higher cap to leave headroom after the budget is consumed");
    }

    [Fact]
    public void BuildEnv_token_cap_uses_invariant_culture_independent_of_thread_locale()
    {
        // German "de-DE" formats integers with a thin space as thousands separator; if BuildEnv
        // accidentally used the current culture, "1024" would become "1.024" or "1 024" and
        // claude-code would silently reject the env var. Pin invariant.
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var env = CliLightModelInvocation.BuildEnv(new CliLightModelOptions { MaxOutputTokens = 1024 }, thinking: false);
            env["CLAUDE_CODE_MAX_OUTPUT_TOKENS"].Should().Be("1024");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prev;
        }
    }

    [Theory]
    [InlineData("{\"decision\":\"allow\"}", "{\"decision\":\"allow\"}")]
    [InlineData("```json\n{\"decision\":\"allow\"}\n```", "{\"decision\":\"allow\"}")]
    [InlineData("  ```json\n{\"decision\":\"allow\"}\n```  ", "{\"decision\":\"allow\"}")]
    [InlineData("```\n{\"x\":1}\n```", "{\"x\":1}")]
    public void StripJsonFence_removes_markdown_fence_when_present(string input, string expected)
    {
        CliLightModelInvocation.StripJsonFence(input).Should().Be(expected);
    }

    [Fact]
    public void StripJsonFence_leaves_text_alone_when_no_fence()
    {
        CliLightModelInvocation.StripJsonFence("plain text response").Should().Be("plain text response");
    }
}
