using InfiniContext.Core.Configuration;
using InfiniContext.Core.Supervision;
using Serilog;

namespace InfiniContext.Core.LightModel;

/// <summary>
/// Picks the concrete <see cref="ILightModelService"/> back end based on credential availability.
/// <c>ANTHROPIC_API_KEY</c> set → SDK (<see cref="AnthropicLightModelService"/>); otherwise the CLI
/// fallback path (<see cref="SingleShotCliLightModelService"/> or
/// <see cref="LongLivedCliLightModelService"/>, per the requested <see cref="CliLightModelMode"/>).
///
/// <para>Two consumers share this factory so the OAuth fallback applies uniformly: the auto-mode
/// safety reviewer in <c>ApprovalSinkFactory</c> and the title-generation path in
/// <see cref="Tasks.TaskService"/>. Each picks its own <c>thinking</c> and <c>cliMode</c> values
/// — the factory is policy-free.</para>
/// </summary>
public class LightModelServiceFactory
{
    private readonly InfiniContextSettings _settings;
    private readonly ILogger _logger;
    private readonly IProcessSupervisor? _supervisor;

    public LightModelServiceFactory(
        InfiniContextSettings settings,
        ILogger logger,
        IProcessSupervisor? supervisor = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _supervisor = supervisor;
    }

    /// <summary>
    /// Construct a light-model service for the given thinking and CLI-lifecycle settings.
    /// <paramref name="cliMode"/> is ignored when the SDK back end is selected.
    /// </summary>
    public virtual ILightModelService Create(bool thinking, CliLightModelMode cliMode)
    {
        var hasApiKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        if (hasApiKey)
        {
            return new AnthropicLightModelService(_settings.Models.Light, logger: _logger, thinking: thinking);
        }
        return cliMode switch
        {
            CliLightModelMode.LongLived => new LongLivedCliLightModelService(_settings.Models.Light, thinking: thinking, supervisor: _supervisor, logger: _logger),
            _ => new SingleShotCliLightModelService(_settings.Models.Light, thinking: thinking, supervisor: _supervisor, logger: _logger),
        };
    }
}
