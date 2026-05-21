using System.Text.Json.Nodes;
using InfiniContext.Cli;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using FluentAssertions;
using Serilog;

namespace InfiniContext.Tests.Cli;

public class ApprovalSinkFactoryTests
{
    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();

    [Fact]
    public void Ask_mode_builds_slot_sink()
    {
        var (factory, _, slot, transcript) = NewFactory();
        var sink = factory.Build(ApprovalMode.Ask, slot, transcript, cwd: "/tmp", out var effective);
        effective.Should().Be(ApprovalMode.Ask);
        Unwrap(sink).Should().BeOfType<SlotApprovalSink>();
    }

    [Fact]
    public async Task Deny_mode_returns_static_deny_sink()
    {
        var (factory, _, slot, transcript) = NewFactory();
        var sink = factory.Build(ApprovalMode.Deny, slot, transcript, cwd: "/tmp", out var effective);
        effective.Should().Be(ApprovalMode.Deny);
        Unwrap(sink).Should().BeOfType<StaticApprovalSink>();

        var response = await sink.RequestAsync(new ApprovalRequest("Bash", new JsonObject()), default);
        response.Behavior.Should().Be(ApprovalBehavior.Deny);
    }

    [Fact]
    public async Task Allow_mode_emits_warning_banner_suggests_auto_and_allows_everything()
    {
        var (factory, output, slot, transcript) = NewFactory();
        var sink = factory.Build(ApprovalMode.Allow, slot, transcript, cwd: "/tmp", out var effective);
        effective.Should().Be(ApprovalMode.Allow);

        var rendered = output.ToString();
        rendered.Should().Contain("WARNING");
        rendered.Should().Contain("ALLOW");
        rendered.Should().Contain("/approval auto"); // banner suggests the safer middle ground

        var response = await sink.RequestAsync(new ApprovalRequest("Bash", new JsonObject()), default);
        response.Behavior.Should().Be(ApprovalBehavior.Allow);
    }

    [Fact]
    public void Auto_mode_without_api_key_uses_cli_back_end_no_more_falling_back_to_ask()
    {
        // Snapshot then null the env var so this test is deterministic regardless of dev shell.
        var prev = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            ApprovalAutoEffort? observedEffort = null;
            var (factory, output, slot, transcript) = NewFactory(effort =>
            {
                observedEffort = effort;
                return new StubLightModel();
            });
            var sink = factory.Build(ApprovalMode.Auto, slot, transcript, cwd: "/tmp", out var effective);

            effective.Should().Be(ApprovalMode.Auto, "auto-without-key now lands on the CLI back end, not ask");
            Unwrap(sink).Should().BeOfType<AutoApprovalSink>();
            observedEffort.Should().Be(ApprovalAutoEffort.Low, "default effort is low");
            output.ToString().Should().Contain("OAuth fallback");
            output.ToString().Should().Contain("console.anthropic.com");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prev);
        }
    }

    [Fact]
    public void Auto_mode_with_api_key_uses_provided_light_model_factory()
    {
        var prev = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-test-not-real");
            var lightStub = new StubLightModel();
            var (factory, output, slot, transcript) = NewFactory(_ => lightStub);
            var sink = factory.Build(ApprovalMode.Auto, slot, transcript, cwd: "/tmp", out var effective);
            effective.Should().Be(ApprovalMode.Auto);
            Unwrap(sink).Should().BeOfType<AutoApprovalSink>();
            output.ToString().Should().Contain("back-end=SDK");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prev);
        }
    }

    [Fact]
    public void Auto_mode_when_light_model_factory_throws_falls_back_to_ask()
    {
        var prev = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-test-not-real");
            var (factory, output, slot, transcript) = NewFactory(_ => throw new InvalidOperationException("ctor exploded"));
            var sink = factory.Build(ApprovalMode.Auto, slot, transcript, cwd: "/tmp", out var effective);
            effective.Should().Be(ApprovalMode.Ask);
            Unwrap(sink).Should().BeOfType<SlotApprovalSink>();
            output.ToString().Should().Contain("InvalidOperationException");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prev);
        }
    }

    [Theory]
    [InlineData(ApprovalAutoEffort.Low)]
    [InlineData(ApprovalAutoEffort.High)]
    public void Auto_mode_threads_effort_setting_through_to_light_model_factory(ApprovalAutoEffort configuredEffort)
    {
        var prev = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            ApprovalAutoEffort? observed = null;
            var settings = InfiniContextSettings.Defaults with
            {
                Approval = InfiniContextSettings.Defaults.Approval with
                {
                    Auto = InfiniContextSettings.Defaults.Approval.Auto with { Effort = configuredEffort },
                },
            };
            var output = new StringWriter();
            var factory = new ApprovalSinkFactory(
                settings,
                new object(),
                output,
                Silent,
                lightModelFactory: e =>
                {
                    observed = e;
                    return new StubLightModel();
                });
            factory.Build(ApprovalMode.Auto, new PendingApprovalSlot(), new TranscriptModel(), cwd: "/tmp", out _);
            observed.Should().Be(configuredEffort);
            output.ToString().Should().Contain($"effort={configuredEffort}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prev);
        }
    }

    [Fact]
    public void Auto_mode_high_effort_oauth_path_includes_estimated_cost_in_banner()
    {
        var prev = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            var settings = InfiniContextSettings.Defaults with
            {
                Approval = InfiniContextSettings.Defaults.Approval with
                {
                    Auto = InfiniContextSettings.Defaults.Approval.Auto with { Effort = ApprovalAutoEffort.High },
                },
            };
            var output = new StringWriter();
            var factory = new ApprovalSinkFactory(
                settings,
                new object(),
                output,
                Silent,
                lightModelFactory: _ => new StubLightModel());
            factory.Build(ApprovalMode.Auto, new PendingApprovalSlot(), new TranscriptModel(), cwd: "/tmp", out _);

            // Catches accidental swap of low/high estimates in the banner.
            output.ToString().Should().Contain("~9 s");
            output.ToString().Should().NotContain("~2.3 s");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prev);
        }
    }

    [Theory]
    [InlineData(CliLightModelMode.SingleShot, typeof(SingleShotCliLightModelService))]
    [InlineData(CliLightModelMode.LongLived, typeof(LongLivedCliLightModelService))]
    public void Auto_mode_default_factory_picks_cli_service_from_approval_auto_mode(CliLightModelMode mode, Type expected)
    {
        // Force the no-API-key path so DefaultFactory takes the CLI branch, then verify
        // approval.auto.mode (NOT models.light.cli.mode) is what governs the lifecycle choice.
        var prev = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            var settings = InfiniContextSettings.Defaults with
            {
                Approval = InfiniContextSettings.Defaults.Approval with
                {
                    Auto = InfiniContextSettings.Defaults.Approval.Auto with { Mode = mode },
                },
                // Set models.light.cli.mode to the OPPOSITE of approval.auto.mode so the test
                // unambiguously proves which one wins.
                Models = InfiniContextSettings.Defaults.Models with
                {
                    Light = InfiniContextSettings.Defaults.Models.Light with
                    {
                        Cli = InfiniContextSettings.Defaults.Models.Light.Cli with
                        {
                            Mode = mode == CliLightModelMode.SingleShot ? CliLightModelMode.LongLived : CliLightModelMode.SingleShot,
                        },
                    },
                },
            };
            // No custom lightModelFactory — exercise DefaultFactory.
            var output = new StringWriter();
            var factory = new ApprovalSinkFactory(settings, new object(), output, Silent);

            var sink = factory.Build(ApprovalMode.Auto, new PendingApprovalSlot(), new TranscriptModel(), cwd: "/tmp", out _);
            var auto = Unwrap(sink).Should().BeOfType<AutoApprovalSink>().Subject;
            auto.LightModel.Should().BeOfType(expected,
                "approval.auto.mode={0} must override models.light.cli.mode for the auto path", mode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prev);
        }
    }

    [Fact]
    public async Task Built_sink_echoes_decision_to_transcript_for_deny()
    {
        var (factory, _, slot, transcript) = NewFactory();
        var sink = factory.Build(ApprovalMode.Deny, slot, transcript, cwd: "/tmp", out _);

        await sink.RequestAsync(new ApprovalRequest("Bash", new JsonObject()), default);

        // Phase 5: the per-request echo lands in the originating session's transcript (rather
        // than the host-shared console stream) so multi-session output stays per-pane.
        var rendered = string.Concat(transcript.Snapshot());
        rendered.Should().Contain("[approval]");
        rendered.Should().Contain("Bash");
        rendered.Should().Contain("DENY");
    }

    [Fact]
    public async Task Built_sink_echoes_decision_to_transcript_for_allow()
    {
        var (factory, _, slot, transcript) = NewFactory();
        var sink = factory.Build(ApprovalMode.Allow, slot, transcript, cwd: "/tmp", out _);

        await sink.RequestAsync(new ApprovalRequest("Bash", new JsonObject()), default);

        var rendered = string.Concat(transcript.Snapshot());
        rendered.Should().Contain("[approval]");
        rendered.Should().Contain("Bash");
        rendered.Should().Contain("ALLOW");
    }

    private static IApprovalSink Unwrap(IApprovalSink sink) =>
        sink is ConsoleEchoingApprovalSink echo ? echo.Inner : sink;

    private static (ApprovalSinkFactory factory, StringWriter output, PendingApprovalSlot slot, TranscriptModel transcript) NewFactory(Func<ApprovalAutoEffort, ILightModelService>? lightModelFactory = null)
    {
        var output = new StringWriter();
        var factory = new ApprovalSinkFactory(
            InfiniContextSettings.Defaults,
            new object(),
            output,
            Silent,
            lightModelFactory: lightModelFactory);
        return (factory, output, new PendingApprovalSlot(), new TranscriptModel());
    }

    private sealed class StubLightModel : ILightModelService
    {
        public Task<string> GenerateShortTitleAsync(string description, int maxWords = 6, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<string> SummarizeAsync(string text, int maxSentences = 2, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<CategoryResult> CategorizeAsync(string text, IReadOnlyList<string> categories, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<SafetyReview> ReviewToolCallSafetyAsync(SafetyReviewRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
