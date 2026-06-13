using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Sessions;
using Serilog;

namespace InfiniContext.Core.Mcp;

/// <summary>
/// M20 (audit <c>docs/architecture-review-2026-06-11.md</c>): the single Core-owned decision for
/// "which <see cref="IApprovalSink"/> does an <see cref="ApprovalMode"/> resolve to". The CLI's
/// <c>ApprovalSinkFactory</c> and the GUI's <c>SessionHost.BuildApprovalSink</c> had drifted copies
/// of this (the GUI Ask path skipped the echo wrapper, the log prefixes differed); both now delegate
/// the construction logic here so the mode→sink mapping, the Auto light-model construct-with-ask-
/// fallback, and the <see cref="AutoApprovalSink"/> wiring exist exactly once.
///
/// <para>
/// Host-specific <i>presentation</i> is deliberately NOT promoted — it is legitimate per-host policy:
/// the CLI emits console banners (the "ALLOW" warning, the auto back-end notice) and echo-wraps every
/// mode into the transcript; the GUI logs only and returns the Ask sink bare because its banner UI
/// carries closure visually. Those stay in the hosts, layered over <see cref="BuildInner"/>.
/// </para>
/// </summary>
public sealed class ApprovalSinkBuilder
{
    private readonly InfiniContextSettings _settings;
    private readonly ILogger _logger;
    private readonly Func<ApprovalAutoEffort, ILightModelService> _lightModelFactory;
    private readonly bool _writeTranscriptBanner;
    private readonly ApprovalCosmetics? _cosmetics;

    /// <param name="lightModelFactory">Constructs the Auto-mode safety reviewer for a given effort.
    /// Hosts inject the back-end choice (SDK when <c>ANTHROPIC_API_KEY</c> is set, else CLI) here.</param>
    /// <param name="writeTranscriptBanner">Forwarded to the Ask <see cref="SlotApprovalSink"/>: the
    /// CLI wants the textual banner (<c>true</c>); the GUI renders closure in its pane UI
    /// (<c>false</c>).</param>
    /// <param name="cosmetics">Ask-sink cosmetics (CLI: <c>settings.Cosmetics.Approval</c>; GUI:
    /// <c>null</c> → defaults).</param>
    public ApprovalSinkBuilder(
        InfiniContextSettings settings,
        ILogger logger,
        Func<ApprovalAutoEffort, ILightModelService> lightModelFactory,
        bool writeTranscriptBanner,
        ApprovalCosmetics? cosmetics = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lightModelFactory = lightModelFactory ?? throw new ArgumentNullException(nameof(lightModelFactory));
        _writeTranscriptBanner = writeTranscriptBanner;
        _cosmetics = cosmetics;
    }

    /// <summary>
    /// Build the bare inner sink for <paramref name="mode"/> — no echo wrapper, so each host adds that
    /// per its own policy. <see cref="ApprovalSinkBuildResult.EffectiveMode"/> differs from
    /// <paramref name="mode"/> only when Auto's light-model construct throws and we fall back to Ask;
    /// in that case <see cref="ApprovalSinkBuildResult.LightModelError"/> carries the exception so the
    /// host can surface it (the CLI prints it to the console; the builder has already logged it).
    /// </summary>
    public ApprovalSinkBuildResult BuildInner(ApprovalMode mode, PendingApprovalSlot slot, TranscriptModel transcript, string cwd)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(cwd);

        var ask = BuildAsk(slot, transcript);
        switch (mode)
        {
            case ApprovalMode.Ask:
                return new ApprovalSinkBuildResult(ask, ApprovalMode.Ask, null);

            case ApprovalMode.Deny:
                _logger.Information("approval: mode=deny — all tool calls auto-denied (cwd={Cwd})", cwd);
                return new ApprovalSinkBuildResult(
                    new StaticApprovalSink(ApprovalBehavior.Deny, "auto-deny (approval mode=deny)"),
                    ApprovalMode.Deny, null);

            case ApprovalMode.Allow:
                _logger.Warning("approval: mode=ALLOW — every tool call auto-approved without review (cwd={Cwd})", cwd);
                return new ApprovalSinkBuildResult(new StaticApprovalSink(ApprovalBehavior.Allow), ApprovalMode.Allow, null);

            case ApprovalMode.Auto:
                var effort = _settings.Approval.Auto.Effort;
                ILightModelService light;
                try
                {
                    light = _lightModelFactory(effort);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "approval: mode=auto — failed to construct light model; falling back to ask (cwd={Cwd})", cwd);
                    return new ApprovalSinkBuildResult(ask, ApprovalMode.Ask, ex);
                }
                _logger.Information(
                    "approval: mode=auto effort={Effort} on-low-confidence={OnLowConfidence} timeout={Timeout}s (cwd={Cwd})",
                    effort, _settings.Approval.Auto.OnLowConfidence, _settings.Approval.Auto.TimeoutSeconds, cwd);
                return new ApprovalSinkBuildResult(
                    new AutoApprovalSink(light, ask, _settings.Approval.Auto, cwd, _logger),
                    ApprovalMode.Auto, null);

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown approval mode");
        }
    }

    private SlotApprovalSink BuildAsk(PendingApprovalSlot slot, TranscriptModel transcript) =>
        new(slot, transcript,
            timeout: TimeSpan.FromSeconds(_settings.Approval.TimeoutSeconds),
            cosmetics: _cosmetics,
            writeTranscriptBanner: _writeTranscriptBanner);
}

/// <summary>Result of <see cref="ApprovalSinkBuilder.BuildInner"/>: the bare inner sink, the
/// effective mode (Auto degrades to Ask on light-model construct failure), and the construct
/// exception when that fallback fired (else null).</summary>
public readonly record struct ApprovalSinkBuildResult(IApprovalSink Sink, ApprovalMode EffectiveMode, Exception? LightModelError);
