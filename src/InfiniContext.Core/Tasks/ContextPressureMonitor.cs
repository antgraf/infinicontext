using System.Text.Json.Nodes;
using InfiniContext.Core.Events;
using Serilog;

namespace InfiniContext.Core.Tasks;

/// <summary>
/// Stage 2A Phase 8. Watches a session's <see cref="ResultEvent"/>s and fires once-per-session
/// when the running token usage crosses the configured soft or hard threshold. The monitor does
/// not send anything itself — it raises C# events; <see cref="ThresholdInjector"/> turns those
/// into host-injected user turns.
///
/// <para>
/// Token sum follows design.md §8: <c>input_tokens + cache_creation_input_tokens +
/// cache_read_input_tokens</c> from the <c>result.usage</c> object. The context window is
/// resolved in priority order:
/// </para>
/// <list type="number">
///   <item>Claude's own <c>result.modelUsage[&lt;active model&gt;].contextWindow</c> (most accurate, since it reflects e.g. the <c>[1m]</c> beta).</item>
///   <item>The settings-supplied <c>tasks.modelContextWindows</c> map, keyed by model id from <c>system/init</c>.</item>
///   <item>If neither is available, the monitor stays silent for this turn — better no nudge than a wrong-window nudge.</item>
/// </list>
///
/// <para>
/// "Once per session" means: once each for soft and hard, and the flags reset only when a new
/// <c>system/init</c> with a different <see cref="ClaudeEvent.SessionId"/> is observed (i.e. the
/// host re-spawned the child). Within a single session the agent could in principle drop below
/// the threshold (e.g. via <c>/compact</c>), but we intentionally do not re-arm — the design
/// calls for "no flapping" in §13.
/// </para>
/// </summary>
public sealed class ContextPressureMonitor
{
    private readonly double _softFraction;
    private readonly double _hardFraction;
    private readonly IReadOnlyDictionary<string, int> _windowsBySettings;
    private readonly ILogger _logger;

    private string? _activeModel;
    private Guid? _sessionId;
    private bool _softCrossed;
    private bool _hardCrossed;

    public ContextPressureMonitor(
        int softThresholdPct,
        int hardThresholdPct,
        IReadOnlyDictionary<string, int>? settingsWindows = null,
        ILogger? logger = null)
    {
        if (softThresholdPct is <= 0 or >= 100) throw new ArgumentOutOfRangeException(nameof(softThresholdPct), "Must be in (0, 100).");
        if (hardThresholdPct is <= 0 or >= 100) throw new ArgumentOutOfRangeException(nameof(hardThresholdPct), "Must be in (0, 100).");
        if (softThresholdPct >= hardThresholdPct) throw new ArgumentException("softThresholdPct must be strictly less than hardThresholdPct.");

        _softFraction = softThresholdPct / 100.0;
        _hardFraction = hardThresholdPct / 100.0;
        _windowsBySettings = settingsWindows ?? new Dictionary<string, int>(StringComparer.Ordinal);
        _logger = logger ?? Log.Logger;
    }

    /// <summary>Raised the first time a session's used-token fraction reaches the soft threshold.</summary>
    public event Action<ContextPressureCrossing>? SoftCrossed;

    /// <summary>Raised the first time a session's used-token fraction reaches the hard threshold.</summary>
    public event Action<ContextPressureCrossing>? HardCrossed;

    /// <summary>Snapshot of internal state. Test/debug only.</summary>
    public ContextPressureSnapshot Snapshot() => new(_activeModel, _sessionId, _softCrossed, _hardCrossed);

    /// <summary>
    /// Feed one parsed event. The expected callers are <c>ClaudeSession</c>'s stdout loop or a
    /// test driver. Synchronous and side-effect-only — listeners are responsible for any
    /// async work (subscribers should fire-and-forget if they need to send a turn).
    /// </summary>
    public void OnEvent(ClaudeEvent ev)
    {
        switch (ev)
        {
            case SystemInitEvent init:
                HandleInit(init);
                break;
            case ResultEvent r:
                HandleResult(r);
                break;
        }
    }

    private void HandleInit(SystemInitEvent init)
    {
        // Reset whenever a new session id is observed — this is how we recognise a re-spawn.
        if (init.SessionId is { } sid && sid != _sessionId)
        {
            _sessionId = sid;
            _softCrossed = false;
            _hardCrossed = false;
        }
        _activeModel = init.Model;
    }

    private void HandleResult(ResultEvent r)
    {
        var usage = r.Raw["usage"] as JsonObject;
        if (usage is null) return;

        long used = ReadLong(usage, "input_tokens")
                  + ReadLong(usage, "cache_creation_input_tokens")
                  + ReadLong(usage, "cache_read_input_tokens");
        if (used <= 0) return;

        var window = ResolveWindow(r);
        if (window is null or <= 0)
        {
            _logger.Debug("context pressure: no window known for model={Model}; skipping check", _activeModel);
            return;
        }

        var fraction = (double)used / window.Value;
        var crossing = new ContextPressureCrossing(used, window.Value, fraction, _activeModel);

        if (!_hardCrossed && fraction >= _hardFraction)
        {
            _hardCrossed = true;
            // Implies soft is also crossed — flag it so a later soft check doesn't fire after hard.
            _softCrossed = true;
            _logger.Warning(
                "context pressure: HARD threshold crossed used={Used} window={Window} pct={Pct:P0} model={Model}",
                used, window.Value, fraction, _activeModel ?? "<unknown>");
            HardCrossed?.Invoke(crossing);
        }
        else if (!_softCrossed && fraction >= _softFraction)
        {
            _softCrossed = true;
            _logger.Information(
                "context pressure: soft threshold crossed used={Used} window={Window} pct={Pct:P0} model={Model}",
                used, window.Value, fraction, _activeModel ?? "<unknown>");
            SoftCrossed?.Invoke(crossing);
        }
    }

    private long? ResolveWindow(ResultEvent r)
    {
        // 1. claude's self-report.
        if (_activeModel is not null
            && r.Raw["modelUsage"] is JsonObject mu
            && mu[_activeModel] is JsonObject mm
            && mm["contextWindow"] is { } cw
            && TryReadLong(cw) is { } w0)
        {
            return w0;
        }

        // 2. Settings-supplied map.
        if (_activeModel is not null && _windowsBySettings.TryGetValue(_activeModel, out var w)) return w;

        return null;
    }

    private static long ReadLong(JsonObject obj, string key)
        => TryReadLong(obj[key]) ?? 0;

    /// <summary>
    /// Coerce a numeric <see cref="JsonNode"/> to <see cref="long"/> across the JsonValue
    /// flavours we see in practice: parsed wire data (<see cref="System.Text.Json.JsonElement"/>-
    /// backed, where <c>TryGetValue&lt;long&gt;</c> works for integers and we fall through to
    /// double for decimals like <c>123.0</c>) and in-memory test fixtures (<c>JsonValueOfT&lt;int&gt;</c>,
    /// where <c>TryGetValue&lt;long&gt;</c> returns false because the stored type is exactly
    /// <c>int</c>). Returns <c>null</c> for non-numeric or out-of-range values rather than
    /// silently zeroing — callers decide what zero means in their context.
    /// </summary>
    private static long? TryReadLong(JsonNode? node)
    {
        if (node is not JsonValue val) return null;
        if (val.TryGetValue<long>(out var l)) return l;
        if (val.TryGetValue<int>(out var i)) return i;
        if (val.TryGetValue<double>(out var d) && d >= long.MinValue && d <= long.MaxValue)
            return (long)d;
        return null;
    }
}

/// <summary>
/// Payload for <see cref="ContextPressureMonitor.SoftCrossed"/> / <see cref="ContextPressureMonitor.HardCrossed"/>.
/// </summary>
/// <param name="UsedTokens">Sum of <c>input + cache_creation_input + cache_read_input</c> at the crossing turn.</param>
/// <param name="WindowTokens">Resolved context window for the active model.</param>
/// <param name="UsedFraction">UsedTokens / WindowTokens. Caller can render <c>:P0</c> for the user.</param>
/// <param name="Model">Active model id from <c>system/init</c>, or null if it was never observed.</param>
public readonly record struct ContextPressureCrossing(long UsedTokens, long WindowTokens, double UsedFraction, string? Model);

/// <summary>Internal-state snapshot for tests and debug logging.</summary>
public readonly record struct ContextPressureSnapshot(string? ActiveModel, Guid? SessionId, bool SoftCrossed, bool HardCrossed);
