using FluentAssertions;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Review;

namespace InfiniContext.Tests.Review;

public class CliReviewerAgentServiceTests
{
    [Fact]
    public void Ctor_throws_when_options_is_null()
    {
        var act = () => new CliReviewerAgentService(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_throws_when_model_is_blank(string bad)
    {
        var act = () => new CliReviewerAgentService(new ReviewerAgentOptions { Model = bad });
        act.Should().Throw<ArgumentException>().WithMessage("*Model must be non-empty*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_throws_when_max_output_tokens_is_non_positive(int bad)
    {
        var act = () => new CliReviewerAgentService(new ReviewerAgentOptions { MaxOutputTokens = bad });
        act.Should().Throw<ArgumentException>().WithMessage("*MaxOutputTokens must be positive*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Ctor_throws_when_timeout_is_non_positive(int bad)
    {
        var act = () => new CliReviewerAgentService(new ReviewerAgentOptions { InvocationTimeoutSeconds = bad });
        act.Should().Throw<ArgumentException>().WithMessage("*InvocationTimeoutSeconds must be positive*");
    }

    [Fact]
    public async Task RunOneShotAsync_throws_on_blank_system_prompt()
    {
        var sut = new CliReviewerAgentService(new ReviewerAgentOptions());
        var act = () => sut.RunOneShotAsync(systemPrompt: "", userPayload: "x", ct: default);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RunOneShotAsync_throws_on_blank_user_payload()
    {
        var sut = new CliReviewerAgentService(new ReviewerAgentOptions());
        var act = () => sut.RunOneShotAsync(systemPrompt: "x", userPayload: "", ct: default);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ---------- BuildLightModelOptions translation ----------

    [Fact]
    public void BuildLightModelOptions_propagates_model_id()
    {
        var opts = new ReviewerAgentOptions { Model = "claude-opus-4-7" };
        var light = CliReviewerAgentService.BuildLightModelOptions(opts);
        light.Id.Should().Be("claude-opus-4-7");
    }

    [Fact]
    public void BuildLightModelOptions_forces_single_shot_mode()
    {
        // Reviewers must not share a long-lived process - each gets a fresh child to isolate
        // failures and prevent context leakage between reviewers.
        var light = CliReviewerAgentService.BuildLightModelOptions(new ReviewerAgentOptions());
        light.Cli.Mode.Should().Be(CliLightModelMode.SingleShot);
    }

    [Fact]
    public void BuildLightModelOptions_propagates_max_output_tokens_to_both_caps()
    {
        // The light-model split between MaxOutputTokens (non-thinking) and ThinkingMaxOutputTokens
        // doesn't apply to reviewers - the cap is the cap. Sending the same value through both
        // keeps behavior uniform regardless of the thinking flag.
        var opts = new ReviewerAgentOptions { MaxOutputTokens = 16_384 };
        var light = CliReviewerAgentService.BuildLightModelOptions(opts);
        light.Cli.MaxOutputTokens.Should().Be(16_384);
        light.Cli.ThinkingMaxOutputTokens.Should().Be(16_384);
    }

    [Fact]
    public void BuildLightModelOptions_propagates_claude_executable()
    {
        var opts = new ReviewerAgentOptions { ClaudeExecutable = "/usr/local/bin/claude" };
        var light = CliReviewerAgentService.BuildLightModelOptions(opts);
        light.Cli.ClaudeExecutable.Should().Be("/usr/local/bin/claude");
    }

    [Fact]
    public void BuildLightModelOptions_propagates_managed_cwd_when_set()
    {
        var opts = new ReviewerAgentOptions { ManagedCwd = "/tmp/review-test" };
        var light = CliReviewerAgentService.BuildLightModelOptions(opts);
        light.Cli.ManagedCwd.Should().Be("/tmp/review-test");
    }

    [Fact]
    public void BuildLightModelOptions_resolves_default_managed_cwd_when_null()
    {
        var light = CliReviewerAgentService.BuildLightModelOptions(new ReviewerAgentOptions { ManagedCwd = null });
        light.Cli.ManagedCwd.Should().NotBeNullOrEmpty();
        // Must end with the review-specific subdir, not the light-cwd one (so the two channels
        // don't share a working directory).
        light.Cli.ManagedCwd!.Replace('\\', '/').Should().EndWith(".infinicontext/review-cwd");
    }

    [Fact]
    public void BuildLightModelOptions_propagates_extra_env()
    {
        var extra = new Dictionary<string, string> { ["FAKE_CLAUDE_SCRIPT"] = "/path/to/script" };
        var opts = new ReviewerAgentOptions { ExtraEnv = extra };
        var light = CliReviewerAgentService.BuildLightModelOptions(opts);
        light.Cli.ExtraEnv.Should().BeSameAs(extra);
    }

    // ---------- Argv shape ----------

    [Fact]
    public void Recipe_argv_contains_the_reviewer_model()
    {
        // CliLightModelInvocation is the shared argv builder; passing the reviewer model id
        // through it produces an argv that requests that model. This is the contract that lets
        // us reuse the spawn machinery — `--model <reviewerModel>` is what makes a "reviewer"
        // distinct from a "light model" invocation.
        var argv = CliLightModelInvocation.BuildArgv("claude-opus-4-7", "you are a code reviewer");

        argv.Should().Contain("--model");
        var modelIdx = argv.ToList().IndexOf("--model");
        argv[modelIdx + 1].Should().Be("claude-opus-4-7");
    }

    [Fact]
    public void Recipe_argv_contains_the_isolation_flags()
    {
        var argv = CliLightModelInvocation.BuildArgv("any-model", "any-system");

        // The five context-stripping flags the recipe requires.
        argv.Should().Contain("--tools").And.Contain("");
        argv.Should().Contain("--strict-mcp-config");
        argv.Should().Contain("--mcp-config").And.Contain("{\"mcpServers\":{}}");
        argv.Should().Contain("--setting-sources").And.Contain("project");
        argv.Should().Contain("--disable-slash-commands");
        argv.Should().Contain("--no-session-persistence");
        argv.Should().Contain("--print");
    }
}
