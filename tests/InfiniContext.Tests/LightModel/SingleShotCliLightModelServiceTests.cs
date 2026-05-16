using System.Text.Json.Nodes;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using FluentAssertions;
using Serilog;

namespace InfiniContext.Tests.LightModel;

/// <summary>
/// End-to-end tests for <see cref="SingleShotCliLightModelService"/> driven against FakeClaude
/// scripts. Covers the happy path (allow verdict), the markdown-fence quirk (model wraps JSON in
/// triple-backtick), and a multi-call sequence proving each call gets a fresh process.
///
/// <para>The light-model service builds its own argv (the §6 recipe shape) which doesn't include
/// FakeClaude's <c>--script</c> flag. Tests pass the script path via the <c>FAKE_CLAUDE_SCRIPT</c>
/// env var, which FakeClaude reads as a fallback. Production env-var passthrough does the rest.</para>
/// </summary>
public class SingleShotCliLightModelServiceTests
{
    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();
    private static readonly string ScriptsDir = Path.Combine(TestPaths.RepoRoot, "tests", "scripts");

    [Fact]
    public async Task Returns_parsed_safety_verdict_for_allow_response()
    {
        await using var workCwd = new TempCwd();
        var service = new SingleShotCliLightModelService(
            options: BuildOptions(workCwd.Path, "light-model-safety-allow.json"),
            thinking: false,
            logger: Silent);

        var verdict = await service.ReviewToolCallSafetyAsync(
            new SafetyReviewRequest("Bash", new JsonObject { ["command"] = "ls -la" }, Cwd: "/some/cwd"),
            CancellationToken.None);

        verdict.Decision.Should().Be(SafetyDecision.Allow);
        verdict.Confidence.Should().Be(SafetyConfidence.High);
        verdict.Axis.Should().Be(SafetyAxis.None);
        verdict.Reason.Should().Contain("benign");
    }

    [Fact]
    public async Task Strips_markdown_fence_and_parses_deny_response()
    {
        await using var workCwd = new TempCwd();
        var service = new SingleShotCliLightModelService(
            options: BuildOptions(workCwd.Path, "light-model-safety-deny-fenced.json"),
            thinking: false,
            logger: Silent);

        var verdict = await service.ReviewToolCallSafetyAsync(
            new SafetyReviewRequest("Bash", new JsonObject { ["command"] = "rm -rf /tmp/foo" }),
            CancellationToken.None);

        verdict.Decision.Should().Be(SafetyDecision.Deny);
        verdict.Axis.Should().Be(SafetyAxis.Destructive);
        verdict.Reason.Should().Contain("rm -rf");
    }

    [Fact]
    public async Task Each_call_uses_a_fresh_process_so_no_cross_call_state()
    {
        // Two back-to-back safety reviews against the same allow-script. Each call gets a fresh
        // FakeClaude — if single-shot were reusing a child the second call would hang waiting
        // for input the FakeClaude script can't produce (its `wait-stdin` action only fires once
        // before `exit`).
        await using var workCwd = new TempCwd();
        var service = new SingleShotCliLightModelService(
            options: BuildOptions(workCwd.Path, "light-model-safety-allow.json"),
            thinking: false,
            logger: Silent);

        var first = await service.ReviewToolCallSafetyAsync(
            new SafetyReviewRequest("Bash", new JsonObject { ["command"] = "ls" }), CancellationToken.None);
        var second = await service.ReviewToolCallSafetyAsync(
            new SafetyReviewRequest("Bash", new JsonObject { ["command"] = "pwd" }), CancellationToken.None);

        first.Decision.Should().Be(SafetyDecision.Allow);
        second.Decision.Should().Be(SafetyDecision.Allow);
    }

    private static LightModelOptions BuildOptions(string managedCwd, string scriptName) => new()
    {
        Id = "claude-haiku-4-5",
        TimeoutSeconds = 15,
        Cli = new CliLightModelOptions
        {
            Mode = CliLightModelMode.SingleShot,
            MaxOutputTokens = 128,
            ClaudeExecutable = FakeClaudeRunner.ExecutablePath,
            ManagedCwd = managedCwd,
            // Per-spawn env injection — avoids the cross-test race that bites if we instead set
            // FAKE_CLAUDE_SCRIPT on the test process (xUnit runs different test classes in
            // parallel by default).
            ExtraEnv = new Dictionary<string, string>
            {
                ["FAKE_CLAUDE_SCRIPT"] = Path.Combine(ScriptsDir, scriptName),
            },
        },
    };

    /// <summary>Test-owned temp dir so the recipe's auto-create of <c>~/.infinicontext/light-cwd</c> is bypassed.</summary>
    private sealed class TempCwd : IAsyncDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "infinicontext-cli-light-" + Guid.NewGuid().ToString("N"));
        public TempCwd() => Directory.CreateDirectory(Path);
        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
            return ValueTask.CompletedTask;
        }
    }
}
