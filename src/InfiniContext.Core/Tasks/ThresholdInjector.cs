using Serilog;

namespace InfiniContext.Core.Tasks;

/// <summary>
/// Stage 2A Phase 8. Turns <see cref="ContextPressureMonitor"/> crossings into host-injected
/// user turns. Decoupled from <c>ClaudeSession</c> by a delegate so tests don't need the
/// process pipeline to verify the message text.
///
/// <para>
/// The MVP injector sends a single nudge per crossing and trusts the agent (with the
/// <c>infinicontext-task</c> skill loaded) to comply. design.md §8 describes a "force-flush"
/// guardrail for the hard threshold — cancel the next turn and re-inject if the agent doesn't
/// flush — but that's deferred until field data shows it's needed; the first version of this
/// class is intentionally simple.
/// </para>
/// </summary>
public sealed class ThresholdInjector
{
    private readonly Func<string, CancellationToken, Task> _send;
    private readonly ILogger _logger;

    public ThresholdInjector(Func<string, CancellationToken, Task> send, ILogger? logger = null)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _logger = logger ?? Log.Logger;
    }

    public async Task OnSoftAsync(ContextPressureCrossing crossing, CancellationToken ct = default)
    {
        // Top-level catch so the fire-and-forget callers (`_ = injector.OnSoftAsync(...)`) never
        // see an unobserved task fault — even if formatting or logging throws synchronously.
        try
        {
            var pct = (int)Math.Round(crossing.UsedFraction * 100);
            var msg =
                $"You're at {pct}% of context. Please flush progress to artifacts when you reach a logical stopping point, then exit so I can hand off to a fresh session.";
            _logger.Information("context pressure: injecting SOFT nudge ({Pct}% of {Window} tokens)", pct, crossing.WindowTokens);
            await SafeSendAsync(msg, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort log; if even this throws there's nothing else we can do — but at least
            // the calling Task completes cleanly rather than triggering an unobserved-task event.
            try { _logger.Error(ex, "context pressure: OnSoftAsync failed"); } catch { /* ignore */ }
        }
    }

    public async Task OnHardAsync(ContextPressureCrossing crossing, CancellationToken ct = default)
    {
        try
        {
            var pct = (int)Math.Round(crossing.UsedFraction * 100);
            var msg =
                $"Context is critical ({pct}% of {crossing.WindowTokens} tokens). Flush all relevant progress to artifacts now. Do not start any new work or ask clarifying questions.";
            _logger.Warning("context pressure: injecting HARD nudge ({Pct}% of {Window} tokens)", pct, crossing.WindowTokens);
            await SafeSendAsync(msg, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { _logger.Error(ex, "context pressure: OnHardAsync failed"); } catch { /* ignore */ }
        }
    }

    private async Task SafeSendAsync(string message, CancellationToken ct)
    {
        try
        {
            await _send(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "context pressure: nudge send failed");
        }
    }
}
