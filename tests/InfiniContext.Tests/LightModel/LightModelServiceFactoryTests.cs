using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using FluentAssertions;
using Serilog;

namespace InfiniContext.Tests.LightModel;

/// <summary>
/// Verifies <see cref="LightModelServiceFactory"/> picks the right concrete back end based on the
/// <c>ANTHROPIC_API_KEY</c> environment variable and the requested <see cref="CliLightModelMode"/>.
/// Each case snapshots and restores the env var so parallel test runs don't observe each other.
/// </summary>
public class LightModelServiceFactoryTests
{
    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();

    [Fact]
    public void Picks_sdk_back_end_when_api_key_is_set()
    {
        var prev = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-test-not-real");
            var factory = new LightModelServiceFactory(InfiniContextSettings.Defaults, Silent);
            var service = factory.Create(thinking: false, cliMode: CliLightModelMode.SingleShot);
            service.Should().BeOfType<AnthropicLightModelService>(
                "API key present means we should always use the SDK regardless of cliMode");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prev);
        }
    }

    [Theory]
    [InlineData(CliLightModelMode.SingleShot, typeof(SingleShotCliLightModelService))]
    [InlineData(CliLightModelMode.LongLived, typeof(LongLivedCliLightModelService))]
    public async Task Picks_cli_back_end_per_mode_when_api_key_is_absent(CliLightModelMode mode, Type expected)
    {
        var prev = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            var factory = new LightModelServiceFactory(InfiniContextSettings.Defaults, Silent);
            var service = factory.Create(thinking: false, cliMode: mode);
            service.Should().BeOfType(expected);
            if (service is IAsyncDisposable d) await d.DisposeAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prev);
        }
    }

    [Fact]
    public void Constructor_rejects_null_settings_and_logger()
    {
        FluentActions.Invoking(() => new LightModelServiceFactory(null!, Silent))
            .Should().Throw<ArgumentNullException>().WithParameterName("settings");
        FluentActions.Invoking(() => new LightModelServiceFactory(InfiniContextSettings.Defaults, null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }
}
