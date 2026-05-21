using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.Supervision;
using Serilog;

namespace InfiniContext.Cli;

/// <summary>
/// Builds an <see cref="IApprovalSink"/> for a given <see cref="ApprovalMode"/>. Used at session
/// startup AND when the user runs <c>/approval &lt;mode&gt;</c> in the REPL — the same code path
/// guarantees consistent behavior, warnings, and logging in both cases.
///
/// <para>
/// Stage 2B Phase 5: the build signature now takes a per-session
/// <see cref="PendingApprovalSlot"/> + <see cref="TranscriptModel"/>. <c>Ask</c> resolves to a
/// <see cref="SlotApprovalSink"/> that parks the request in the slot and lets the host UI render
/// the banner / route user input. The slot/transcript params are unused for <c>Allow</c>/<c>Deny</c>
/// (static decisions) and surface as the fallback for <c>Auto</c> low-confidence cases.
/// </para>
///
/// <para>
/// <c>Auto</c> resolves to two back ends depending on environment:
/// <list type="bullet">
///   <item><c>ANTHROPIC_API_KEY</c> set → SDK back end (<see cref="AnthropicLightModelService"/>),
///   ~500 ms/call. <c>thinking</c> follows <c>approval.auto.effort</c>.</item>
///   <item><c>ANTHROPIC_API_KEY</c> absent → CLI back end (<see cref="SingleShotCliLightModelService"/>),
///   ~2.3 s/call (low effort) or ~9 s/call (high effort) per the §6 recipe. Works with OAuth-only
///   <c>claude login</c> credentials.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class ApprovalSinkFactory
{
    private readonly InfiniContextSettings _settings;
    private readonly object _writeLock;
    private readonly TextWriter _output;
    private readonly ILogger _logger;
    private readonly LightModelServiceFactory _lightModelServiceFactory;
    private readonly Func<ApprovalAutoEffort, ILightModelService> _lightModelFactory;

    public ApprovalSinkFactory(
        InfiniContextSettings settings,
        object writeLock,
        TextWriter output,
        ILogger logger,
        Func<ApprovalAutoEffort, ILightModelService>? lightModelFactory = null,
        IProcessSupervisor? supervisor = null,
        LightModelServiceFactory? lightModelServiceFactory = null)
    {
        _settings = settings;
        _writeLock = writeLock;
        _output = output;
        _logger = logger;
        _lightModelServiceFactory = lightModelServiceFactory ?? new LightModelServiceFactory(settings, logger, supervisor);
        _lightModelFactory = lightModelFactory ?? DefaultFactory;
    }

    // Auto-mode lifecycle is governed by approval.auto.mode (NOT models.light.cli.mode — that's
    // the default for general consumers like title generation; auto-mode safety review wants real
    // isolation by default, which only single-shot provides). Ignored on the SDK back end.
    private ILightModelService DefaultFactory(ApprovalAutoEffort effort) =>
        _lightModelServiceFactory.Create(thinking: effort == ApprovalAutoEffort.High, cliMode: _settings.Approval.Auto.Mode);

    public IApprovalSink Build(ApprovalMode mode, PendingApprovalSlot slot, TranscriptModel transcript, string cwd, out ApprovalMode effectiveMode, bool announce = true)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(cwd);

        var inner = BuildInner(mode, slot, transcript, cwd, announce, out effectiveMode);
        // Wrap every constructed sink so decisions land in the originating session's transcript
        // for ALL modes. ApprovalChannel already logs at Information for every request/response,
        // which covers the file side; the decorator covers the user-visible side. Phase 5 routes
        // the line through transcript.Append so the right session's pane gets the echo (rather
        // than the host-shared console stream).
        return new ConsoleEchoingApprovalSink(inner, line => transcript.Append(line + "\n"));
    }

    private IApprovalSink BuildInner(ApprovalMode mode, PendingApprovalSlot slot, TranscriptModel transcript, string cwd, bool announce, out ApprovalMode effectiveMode)
    {
        effectiveMode = mode;

        switch (mode)
        {
            case ApprovalMode.Ask:
                return BuildAsk(slot, transcript);

            case ApprovalMode.Deny:
                _logger.Information("approval-mode: deny — all tool calls auto-denied");
                return new StaticApprovalSink(ApprovalBehavior.Deny, "auto-deny (approval mode=deny)");

            case ApprovalMode.Allow:
                if (announce) EmitAllowWarning();
                _logger.Warning("approval-mode: ALLOW — every tool call will be auto-approved without review");
                return new StaticApprovalSink(ApprovalBehavior.Allow);

            case ApprovalMode.Auto:
                var effort = _settings.Approval.Auto.Effort;
                var hasApiKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
                if (announce) EmitAutoModeBanner(hasApiKey, effort);
                _logger.Information(
                    "approval-mode: auto — back-end={Backend} light-model={LightModel} effort={Effort} on-low-confidence={OnLowConfidence} timeout={Timeout}s",
                    hasApiKey ? "sdk" : "cli",
                    _settings.Models.Light.Id, effort,
                    _settings.Approval.Auto.OnLowConfidence, _settings.Approval.Auto.TimeoutSeconds);
                ILightModelService light;
                try { light = _lightModelFactory(effort); }
                catch (Exception ex)
                {
                    if (announce) Write($"[approval] failed to construct light model ({ex.GetType().Name}); falling back to ask\n");
                    _logger.Error(ex, "approval-mode: failed to construct light model; falling back to ask");
                    effectiveMode = ApprovalMode.Ask;
                    return BuildAsk(slot, transcript);
                }
                return new AutoApprovalSink(light, BuildAsk(slot, transcript), _settings.Approval.Auto, cwd, _logger);

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    private IApprovalSink BuildAsk(PendingApprovalSlot slot, TranscriptModel transcript) => new SlotApprovalSink(
        slot,
        transcript,
        timeout: TimeSpan.FromSeconds(_settings.Approval.TimeoutSeconds),
        cosmetics: _settings.Cosmetics.Approval);

    private void EmitAllowWarning()
    {
        Write("\n");
        Write("=========================================================================\n");
        Write("  WARNING: approval mode = ALLOW\n");
        Write("  Every tool call will be auto-approved without review.\n");
        Write("  Use only in trusted/isolated sandboxes. Consider /approval auto for a\n");
        Write("  safer middle ground (light-model safety review). Switch back to ask via\n");
        Write("  /approval ask.\n");
        Write("=========================================================================\n");
    }

    /// <summary>
    /// Inform the user which auto-mode back end is active. The CLI fallback (no API key) is the
    /// path most users hit on their personal machine because <c>claude login</c> issues OAuth
    /// credentials, not an API key. Calling out the latency / cost difference up front avoids
    /// surprise when the first verdict takes 2 s instead of 500 ms. See
    /// <c>docs/research/claude-cli-light-model.md</c> for the full numbers.
    /// </summary>
    private void EmitAutoModeBanner(bool hasApiKey, ApprovalAutoEffort effort)
    {
        if (hasApiKey)
        {
            Write($"[approval] mode=auto, back-end=SDK (ANTHROPIC_API_KEY set), effort={effort}\n");
        }
        else
        {
            var perCall = effort == ApprovalAutoEffort.High ? "~9 s, ~$0.0074" : "~2.3 s, ~$0.0007";
            Write($"[approval] mode=auto, back-end=CLI (OAuth fallback), effort={effort} ({perCall} per call)\n");
            Write("[approval] for the SDK fast path (~500 ms/call), set ANTHROPIC_API_KEY — see https://console.anthropic.com\n");
        }
    }

    private void Write(string s)
    {
        lock (_writeLock)
        {
            _output.Write(s);
            _output.Flush();
        }
    }
}
