using InfiniContext.Core.Events;
using InfiniContext.Core.State;
using Serilog;

namespace InfiniContext.Core.Sessions;

/// <summary>
/// Stage 2B Phase 4a. Per-session background reader that drains
/// <see cref="IClaudeSession.Events"/> and pumps rendered fragments into the session's
/// <see cref="TranscriptModel"/>. Also bridges the state machine's <c>Transitioned</c> event into
/// the transcript so <c>[state: X -&gt; Y]</c> annotations land in the same stream as parsed events.
///
/// <para>
/// Why per-session-always (D3): in Phase 4b's multi-pane UI only the focused session echoes to
/// the live console; off-screen sessions still need their text preserved so the user can
/// <c>Tab</c> to them and see history. The transcript model is that durable store. UI consumers
/// subscribe to <see cref="TranscriptModel.FragmentAppended"/> for live echo; the reader has no
/// opinion about who's listening.
/// </para>
/// </summary>
public sealed class SessionTranscriptReader : IAsyncDisposable
{
    private readonly SessionContext _ctx;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<StateTransition> _onTransition;

    private Task? _eventLoop;
    private int _disposed;

    public SessionTranscriptReader(SessionContext ctx, ILogger logger)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onTransition = OnStateTransition;
    }

    /// <summary>Begin draining events. Subsequent calls are no-ops.</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0)
            throw new ObjectDisposedException(nameof(SessionTranscriptReader));
        if (_eventLoop is not null) return;

        _ctx.Session.State.Transitioned += _onTransition;
        _eventLoop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Fired with each raw <see cref="ClaudeEvent"/> the reader pulls from the channel,
    /// before transcript fragments are emitted. UI consumers use this to track non-rendered
    /// state like the latest <c>ResultEvent</c>'s cost/turn numbers. Handlers must not block —
    /// they run on the reader's loop.</summary>
    public event Action<ClaudeEvent>? EventObserved;

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _ctx.Session.Events.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_ctx.Session.Events.TryRead(out var ev))
                {
                    try { EventObserved?.Invoke(ev); }
                    catch (Exception ex) { _logger.Warning(ex, "transcript-reader: EventObserved handler threw for {HostSessionId}", _ctx.Id); }

                    foreach (var fragment in TranscriptRenderer.Render(ev))
                        Emit(fragment);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _logger.Warning(ex, "transcript-reader: event loop faulted for {HostSessionId}", _ctx.Id);
        }
    }

    private void OnStateTransition(StateTransition t)
    {
        try { Emit(TranscriptRenderer.RenderStateTransition(t)); }
        catch (Exception ex) { _logger.Warning(ex, "transcript-reader: state-transition emit failed for {HostSessionId}", _ctx.Id); }
    }

    private void Emit(string fragment) => _ctx.Transcript.Append(fragment);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _ctx.Session.State.Transitioned -= _onTransition; } catch { /* best-effort */ }

        _cts.Cancel();
        if (_eventLoop is { } loop)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* timeout or cancelled — proceed with shutdown */ }
        }
        _cts.Dispose();
    }
}
