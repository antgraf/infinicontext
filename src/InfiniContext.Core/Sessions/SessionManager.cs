using InfiniContext.Core.Configuration;
using InfiniContext.Core.Events;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Mcp.Tasks;
using InfiniContext.Core.Supervision;
using InfiniContext.Core.Tasks;
using Serilog;

namespace InfiniContext.Core.Sessions;

/// <summary>
/// Factory that produces an <see cref="IClaudeSession"/> from the provided
/// <see cref="ClaudeSessionOptions"/>, pressure-monitor observer, and per-session approval sink.
/// Pulled out as a delegate so tests can substitute a stub without spawning <c>claude</c>.
/// Phase 5: the approval sink is per-session (each session has its own
/// <see cref="SwitchableApprovalSink"/> wrapping a slot-aware inner), not a host-shared singleton.
/// </summary>
public delegate IClaudeSession SessionFactory(
    ClaudeSessionOptions options,
    Action<ClaudeEvent> eventObserver,
    IApprovalSink approvalSink,
    ILogger logger);

/// <summary>
/// Builds the inner approval sink for a session given its <see cref="PendingApprovalSlot"/> and
/// <see cref="TranscriptModel"/>. Supplied by the CLI (which closes over the current approval
/// mode + auto-mode light model). Phase 5: invoked once at session create; re-invoked by the
/// host's <c>/approval</c> handler to swap each session's inner (D12 — host-wide effective mode).
/// </summary>
public delegate IApprovalSink SessionApprovalSinkFactory(
    PendingApprovalSlot slot,
    TranscriptModel transcript,
    string cwd);

/// <summary>
/// Stage 2B Phase 2. Host-scoped registry of live sessions. Owns the per-session construction
/// that used to live inline in <c>Program.cs</c> (pressure monitor + threshold injector +
/// <see cref="ClaudeSession"/>, wired together and started in the correct order). Stage 2B
/// Phases 4–6 layer multi-pane UI, per-session approval queueing, and broadcast on top of this.
///
/// <para>
/// Disposal contract: <see cref="DisposeAsync"/> closes every active session in unspecified
/// order. Per-session disposal delegates to <see cref="IClaudeSession.DisposeAsync"/>, which
/// drains stdout, kills the child if alive, and tears down the approval sidecar in the order
/// the sidecar / lock-registry contract requires.
/// </para>
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly InfiniContextSettings _settings;
    private readonly ILogger _logger;
    private readonly SessionApprovalSinkFactory _approvalSinkFactory;
    private readonly IProcessSupervisor? _supervisor;
    private readonly LockRegistry? _lockRegistry;
    private readonly SessionFactory _sessionFactory;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, SessionContext> _sessions = new();
    private readonly Dictionary<Guid, SessionStartMetadata> _startMetadata = new();
    private readonly Dictionary<Guid, SessionTranscriptReader> _readers = new();
    private readonly List<SessionIndexEntry> _recent;
    private readonly string _indexPath;
    private readonly Func<DateTimeOffset> _clock;
    private int _disposed;

    /// <summary>Hard cap on rows persisted to <c>~/.infinicontext/index.json</c> (D24). Older rows
    /// are evicted FIFO once exceeded so the file stays manageable for callers that load it on
    /// every startup.</summary>
    public const int RecentSessionsCap = 1000;

    public SessionManager(
        InfiniContextSettings settings,
        ILogger logger,
        SessionApprovalSinkFactory approvalSinkFactory,
        IProcessSupervisor? supervisor = null,
        LockRegistry? lockRegistry = null,
        SessionFactory? sessionFactory = null,
        string? indexPath = null,
        SessionIndex? initialIndex = null,
        Func<DateTimeOffset>? clock = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _approvalSinkFactory = approvalSinkFactory ?? throw new ArgumentNullException(nameof(approvalSinkFactory));
        _supervisor = supervisor;
        _lockRegistry = lockRegistry;
        _sessionFactory = sessionFactory ?? DefaultFactory;
        _indexPath = indexPath ?? SessionIndex.DefaultPath();
        _recent = (initialIndex ?? new SessionIndex()).Sessions.ToList();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Load <c>~/.infinicontext/index.json</c> (or <paramref name="indexPath"/>) and construct a
    /// manager seeded with the recent-session list. Failures during load surface as warnings;
    /// the manager starts with an empty list.
    /// </summary>
    public static async Task<SessionManager> OpenAsync(
        InfiniContextSettings settings,
        ILogger logger,
        SessionApprovalSinkFactory approvalSinkFactory,
        IProcessSupervisor? supervisor = null,
        LockRegistry? lockRegistry = null,
        SessionFactory? sessionFactory = null,
        string? indexPath = null,
        Func<DateTimeOffset>? clock = null)
    {
        var path = indexPath ?? SessionIndex.DefaultPath();
        var index = await SessionIndex.LoadAsync(path, logger).ConfigureAwait(false);
        return new SessionManager(
            settings,
            logger,
            approvalSinkFactory,
            supervisor: supervisor,
            lockRegistry: lockRegistry,
            sessionFactory: sessionFactory,
            indexPath: path,
            initialIndex: index,
            clock: clock);
    }

    /// <summary>Absolute path the manager reads/writes the index at.</summary>
    public string IndexPath => _indexPath;

    /// <summary>Snapshot of sessions persisted in the index, newest first. Rebuilt from disk on
    /// host startup (<see cref="OpenAsync"/>) and appended to by <see cref="CloseAsync"/>.</summary>
    public IReadOnlyList<SessionIndexEntry> RecentSessions
    {
        get { lock (_gate) return _recent.ToArray(); }
    }

    /// <summary>Number of sessions currently registered (post-<see cref="CreateAsync"/>,
    /// pre-<see cref="CloseAsync"/>).</summary>
    public int ActiveCount
    {
        get { lock (_gate) return _sessions.Count; }
    }

    /// <summary>Snapshot of the live sessions. Safe to iterate while other threads create or
    /// close — the array is materialized under the gate.</summary>
    public IReadOnlyList<SessionContext> Active
    {
        get { lock (_gate) return _sessions.Values.ToArray(); }
    }

    /// <summary>Look up a context by id. Returns null if the session is gone (already closed or
    /// never existed).</summary>
    public SessionContext? TryGet(Guid id)
    {
        lock (_gate) return _sessions.TryGetValue(id, out var ctx) ? ctx : null;
    }

    /// <summary>
    /// Construct, start, and register a session. On <see cref="IClaudeSession.StartAsync"/>
    /// failure the underlying session is disposed and the exception propagates — the manager's
    /// registry is unchanged.
    /// </summary>
    public async Task<SessionContext> CreateAsync(SpawnRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        // Task 0012 finding #2: pick the threshold pair based on the resolved model's window.
        // 1M-context models flush at 45/65 (faster) so the agent has runway to summarise before
        // claude's own auto-compact wipes context; 200k-context models stay at 60/85.
        var effectiveModel = request.Model ?? _settings.Session.Model;
        var (softPct, hardPct) = _settings.Tasks.ResolveThresholds(effectiveModel);
        var pressure = new ContextPressureMonitor(
            softPct,
            hardPct,
            _settings.Tasks.ModelContextWindows,
            _logger);

        IClaudeSession? sessionRef = null;
        SessionContext? ctxRef = null;
        var injector = new ThresholdInjector(
            async (msg, innerCt) =>
            {
                if (sessionRef is null) return;
                await sessionRef.SendUserMessageAsync(msg, innerCt).ConfigureAwait(false);
            },
            _logger);
        pressure.SoftCrossed += c =>
        {
            ctxRef?.MarkRotationReason(RotationReason.Soft);
            _ = injector.OnSoftAsync(c);
        };
        pressure.HardCrossed += c =>
        {
            ctxRef?.MarkRotationReason(RotationReason.Hard);
            _ = injector.OnHardAsync(c);
        };

        var options = new ClaudeSessionOptions
        {
            Cwd = request.Cwd,
            Model = request.Model,
            ResumeSessionId = request.ResumeSessionId,
            IncludePartialMessages = request.IncludePartialMessages,
            ClaudeExecutable = request.ClaudeExecutable,
            ExtraArgs = request.ExtraArgs,
            FirstUserMessage = request.FirstUserMessage,
        };

        // Phase 5: mint the per-session approval slot and the SwitchableApprovalSink wrapping the
        // mode-dependent inner sink BEFORE starting the session so the sidecar (constructed inside
        // StartAsync) gets the right per-session sink wired through the channel. The slot stays
        // alive across mode swaps; only the wrapper's inner changes.
        var transcript = new TranscriptModel();
        var approvalSlot = new PendingApprovalSlot();
        var approvalSink = new SwitchableApprovalSink(_approvalSinkFactory(approvalSlot, transcript, request.Cwd));

        var session = _sessionFactory(options, pressure.OnEvent, approvalSink, _logger);
        sessionRef = session;

        try
        {
            await session.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var id = Guid.NewGuid();
        var ctx = new SessionContext(
            id,
            session,
            pressure,
            injector,
            approvalSink,
            approvalSlot,
            transcript,
            request.ActiveTaskId)
        {
            Cwd = request.Cwd,
        };
        ctxRef = ctx;
        var createdAt = _clock();

        // D3: per-session reader always runs so off-screen sessions don't lose their text.
        // Started here (post-StartAsync) so the events channel is producing before we listen.
        // Wire ResultEvent cost/turns straight onto the context so the UI doesn't have to
        // re-subscribe to the (single-consumer) events channel.
        var reader = new SessionTranscriptReader(ctx, _logger);
        reader.EventObserved += ev =>
        {
            if (ev is Events.ResultEvent r)
            {
                ctx.LastCostUsd = r.TotalCostUsd;
                ctx.LastNumTurns = r.NumTurns;
            }
            ctx.RaiseEventObserved(ev);
        };
        reader.Start();

        lock (_gate)
        {
            _sessions[id] = ctx;
            _startMetadata[id] = new SessionStartMetadata(request.Cwd, request.Model, createdAt);
            _readers[id] = reader;
        }
        _logger.Information("session-manager: opened {HostSessionId} cwd={Cwd} task={TaskId} resume={Resume}",
            id, request.Cwd, request.ActiveTaskId, request.ResumeSessionId ?? "<none>");
        return ctx;
    }

    /// <summary>
    /// Remove a session from the registry, dispose it, and persist a row to the index file
    /// (best-effort: index-write failure is logged but does not propagate). No-op if the id is
    /// unknown.
    /// </summary>
    public async Task CloseAsync(Guid id)
    {
        SessionContext? ctx;
        SessionStartMetadata? meta;
        SessionTranscriptReader? reader;
        lock (_gate)
        {
            if (!_sessions.Remove(id, out ctx)) return;
            _startMetadata.Remove(id, out meta);
            _readers.Remove(id, out reader);
        }

        // Snapshot state before disposal so the index row reflects the real session, not the
        // post-dispose blanks. SessionId is set on first event with one — for any session that
        // received system/init, it's non-null here.
        var claudeSessionId = ctx.Session.SessionId;
        var lastState = ctx.Session.State.State.ToString();

        // Stop the reader first so it doesn't emit fragments mid-dispose (which would race the
        // state-machine unsubscribe in the reader's own DisposeAsync).
        if (reader is not null)
        {
            try { await reader.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.Warning(ex, "session-manager: reader dispose failed for {HostSessionId}", id); }
        }

        try
        {
            await ctx.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "session-manager: dispose {HostSessionId} failed", id);
            throw;
        }
        finally
        {
            _logger.Information("session-manager: closed {HostSessionId}", id);
        }

        await TryRecordIndexEntryAsync(meta, claudeSessionId, lastState).ConfigureAwait(false);
    }

    /// <summary>
    /// Wait for a session's <see cref="SessionTranscriptReader"/> event loop to drain — i.e., the
    /// events channel is closed (writer completed by <see cref="ClaudeSession"/>'s stdout-loop
    /// finally) AND every item has been pulled, rendered, and emitted (so any
    /// <see cref="TranscriptModel.FragmentAppended"/> subscriber has seen the last fragment).
    /// Bounded so a stuck reader can't hang shutdown.
    /// </summary>
    /// <remarks>
    /// The host calls this between input-loop exit and unsubscribing its
    /// <c>FragmentAppended</c> handler so the final <c>ResultEvent</c> (the <c>[turn done:]</c>
    /// marker) lands on the console before the exit summary prints. Equivalent to a no-op if the
    /// session id is unknown.
    /// </remarks>
    public async Task DrainReaderAsync(Guid id, TimeSpan timeout)
    {
        SessionTranscriptReader? reader;
        lock (_gate) { _readers.TryGetValue(id, out reader); }
        if (reader is null) return;
        try { await reader.RunCompletion.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { /* bounded; caller proceeds */ }
        catch (Exception ex) { _logger.Warning(ex, "session-manager: reader drain faulted for {HostSessionId}", id); }
    }

    private async Task TryRecordIndexEntryAsync(SessionStartMetadata? meta, Guid? claudeSessionId, string lastState)
    {
        if (meta is null) return;
        if (claudeSessionId is not { } sid)
        {
            _logger.Warning("session-index: skipping write — claude session id never assigned (cwd={Cwd})", meta.Cwd);
            return;
        }

        var entry = new SessionIndexEntry
        {
            SessionId = sid.ToString(),
            Cwd = meta.Cwd,
            Model = meta.Model,
            CreatedAt = meta.CreatedAt,
            LastActive = _clock(),
            LastState = lastState,
        };

        SessionIndex snapshot;
        lock (_gate)
        {
            // Drop any prior row for the same session id — re-running --resume would otherwise
            // duplicate entries (one per host run).
            _recent.RemoveAll(e => string.Equals(e.SessionId, entry.SessionId, StringComparison.Ordinal));
            _recent.Insert(0, entry);
            while (_recent.Count > RecentSessionsCap) _recent.RemoveAt(_recent.Count - 1);
            snapshot = new SessionIndex { Sessions = _recent.ToList() };
        }

        try
        {
            await snapshot.SaveAsync(_indexPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "session-index: failed to persist {Path}", _indexPath);
        }
    }

    private sealed record SessionStartMetadata(string Cwd, string? Model, DateTimeOffset CreatedAt);

    /// <summary>
    /// Fan a single prompt out to multiple sessions. Each <see cref="IClaudeSession.SendUserMessageAsync"/>
    /// runs in parallel and its outcome is captured into the returned list, one entry per id the
    /// caller supplied (in input order). Per-session exceptions are surfaced through
    /// <see cref="BroadcastSendResult.Error"/> rather than propagating, so a partial failure still
    /// reports which targets succeeded. Unknown ids appear as
    /// <see cref="BroadcastSendResult.NotFound"/> entries.
    /// </summary>
    public async Task<IReadOnlyList<BroadcastSendResult>> BroadcastAsync(IEnumerable<Guid> ids, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(text);
        ThrowIfDisposed();

        var idList = ids.ToList();
        var pending = new List<(int Index, SessionContext Ctx)>();
        var results = new BroadcastSendResult?[idList.Count];
        lock (_gate)
        {
            for (var i = 0; i < idList.Count; i++)
            {
                if (_sessions.TryGetValue(idList[i], out var ctx))
                    pending.Add((i, ctx));
                else
                    results[i] = BroadcastSendResult.NotFound(idList[i]);
            }
        }

        if (pending.Count > 0)
        {
            var sends = pending
                .Select(async p =>
                {
                    try
                    {
                        await p.Ctx.Session.SendUserMessageAsync(text, ct).ConfigureAwait(false);
                        results[p.Index] = BroadcastSendResult.Sent(p.Ctx.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "session-manager: broadcast send to {HostSessionId} failed", p.Ctx.Id);
                        results[p.Index] = BroadcastSendResult.Failed(p.Ctx.Id, ex);
                    }
                })
                .ToArray();
            await Task.WhenAll(sends).ConfigureAwait(false);
        }

        return results.Select(r => r!).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        (SessionContext ctx, SessionStartMetadata? meta, SessionTranscriptReader? reader)[] all;
        lock (_gate)
        {
            all = _sessions.Values
                .Select(ctx =>
                {
                    _startMetadata.TryGetValue(ctx.Id, out var meta);
                    _readers.TryGetValue(ctx.Id, out var reader);
                    return (ctx, meta, reader);
                })
                .ToArray();
            _sessions.Clear();
            _startMetadata.Clear();
            _readers.Clear();
        }

        foreach (var (ctx, meta, reader) in all)
        {
            var claudeSessionId = ctx.Session.SessionId;
            var lastState = ctx.Session.State.State.ToString();
            if (reader is not null)
            {
                try { await reader.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.Warning(ex, "session-manager: reader dispose during shutdown failed for {HostSessionId}", ctx.Id); }
            }
            try { await ctx.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.Warning(ex, "session-manager: dispose {HostSessionId} during shutdown failed", ctx.Id); }
            await TryRecordIndexEntryAsync(meta, claudeSessionId, lastState).ConfigureAwait(false);
        }
    }

    private IClaudeSession DefaultFactory(ClaudeSessionOptions options, Action<ClaudeEvent> observer, IApprovalSink approvalSink, ILogger logger) =>
        new ClaudeSession(
            options,
            logger,
            _supervisor,
            approvalSink: approvalSink,
            settings: _settings,
            lockRegistry: _lockRegistry,
            eventObserver: observer);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SessionManager));
    }
}
