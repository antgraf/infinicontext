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
    private readonly LightModelServiceFactory _lightModelServiceFactory;
    private readonly Func<ApprovalAutoEffort, ILightModelService> _lightModelFactory;
    private readonly ApprovalSinkBuilder _builder;

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
        _lightModelServiceFactory = lightModelServiceFactory ?? new LightModelServiceFactory(settings, logger, supervisor);
        _lightModelFactory = lightModelFactory ?? DefaultFactory;
        // M20: the mode→sink decision (+ Auto construct/fallback + AutoApprovalSink wiring) lives in
        // Core's ApprovalSinkBuilder, shared with the GUI. The CLI keeps its console banners and
        // echo-wraps every mode (below). writeTranscriptBanner=true / cosmetics matches the prior
        // BuildAsk behaviour.
        _builder = new ApprovalSinkBuilder(settings, logger, _lightModelFactory,
            writeTranscriptBanner: true, cosmetics: settings.Cosmetics.Approval);
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

        // Console banners are emitted BEFORE the (possibly slow) Auto construct so the operator sees
        // the back-end context up front. The Core builder owns the rest of the decision + logging.
        if (announce)
        {
            switch (mode)
            {
                case ApprovalMode.Allow:
                    EmitAllowWarning();
                    break;
                case ApprovalMode.Auto:
                    EmitAutoModeBanner(
                        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
                        _settings.Approval.Auto.Effort);
                    break;
            }
        }

        var result = _builder.BuildInner(mode, slot, transcript, cwd);
        effectiveMode = result.EffectiveMode;
        if (announce && result.LightModelError is { } err)
        {
            Write($"[approval] failed to construct light model ({err.GetType().Name}); falling back to ask\n");
        }

        // Wrap every constructed sink so decisions land in the originating session's transcript
        // for ALL modes. ApprovalChannel already logs at Information for every request/response,
        // which covers the file side; the decorator covers the user-visible side. Phase 5 routes
        // the line through transcript.Append so the right session's pane gets the echo (rather
        // than the host-shared console stream).
        return new ConsoleEchoingApprovalSink(result.Sink, line => transcript.Append(line + "\n"));
    }

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
