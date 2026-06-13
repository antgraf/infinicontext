using InfiniContext.Core.Configuration;
using InfiniContext.Core.Issues;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Logging;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Mcp.Tasks;
using InfiniContext.Core.Review;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.Supervision;
using InfiniContext.Core.Tasks;
using Serilog;

namespace InfiniContext.Gui.Services;

/// <summary>
/// Minimum-viable host wrapper around the Core session subsystem for the Phase 3B scaffold.
/// Owns settings loader output, Serilog, the platform supervisor, and the SessionManager. The
/// full bring-up (light model, task store, review service, approval modes other than deny) lives
/// in the CLI's Program.cs and will be ported as Phase 3B grows; this class is the staircase
/// that lets the first GUI session spawn and produce transcript fragments.
/// </summary>
public sealed class SessionHost : IAsyncDisposable
{
    private readonly LoggingHandle _logging;
    private readonly HostInstanceLock _hostLock;
    private readonly IProcessSupervisor _supervisor;
    private readonly LockRegistry _lockRegistry;
    private readonly SessionManager _sessionManager;
    private readonly TaskStore? _taskStore;
    private readonly TaskService? _taskService;
    private readonly LightModelServiceFactory _lightModelFactory;
    private readonly ModeHolder _modeHolder;
    private readonly AutoRotationService? _autoRotationService;
    private readonly ReviewService? _reviewService;
    private readonly ScopeResolver? _scopeResolver;
    private readonly IssueService? _issueService;
    private readonly object _handlerWriteLock = new();
    private int _disposed;

    public InfiniContextSettings Settings { get; }
    public string LogFilePath => _logging.LogFilePath;
    public SessionManager SessionManager => _sessionManager;
    public TaskStore? TaskStore => _taskStore;
    public TaskService? TaskService => _taskService;
    public ILogger Logger => _logging.Logger;
    public event Action<SessionContext>? SessionCreated;
    public event Action<Guid>? SessionClosed;

    /// <summary>Re-exposed from the owned <see cref="AutoRotationService"/>. Fires on the state
    /// machine's locked thread; the VM must marshal to the UI thread before touching observable
    /// collections. Null payloads are never raised. When the task subsystem is unavailable
    /// (no <see cref="TaskService"/>) the underlying service is not constructed and this event
    /// will never fire.</summary>
    public event Action<AutoRotationProposal>? AutoRotationProposed
    {
        add { if (_autoRotationService is not null) _autoRotationService.AutoRotationProposed += value; }
        remove { if (_autoRotationService is not null) _autoRotationService.AutoRotationProposed -= value; }
    }

    /// <summary>Host-wide effective approval mode (D12 - one effective mode across all sessions).
    /// Updated at startup from <c>settings.Approval.Mode</c>, then pinned to the post-fallback
    /// mode returned by <see cref="SwitchApprovalMode"/> or by Auto's light-model construct path.</summary>
    public ApprovalMode CurrentApprovalMode => _modeHolder.Mode;

    /// <summary>Fired after <see cref="SwitchApprovalMode"/> resolves to a new effective mode, so
    /// the VM can sync its observable property when the effective mode differs from what was
    /// requested (e.g. Auto -&gt; Ask on light-model construct failure).</summary>
    public event Action<ApprovalMode>? ApprovalModeChanged;

    private SessionHost(
        InfiniContextSettings settings,
        LoggingHandle logging,
        HostInstanceLock hostLock,
        IProcessSupervisor supervisor,
        LockRegistry lockRegistry,
        SessionManager sessionManager,
        TaskStore? taskStore,
        TaskService? taskService,
        LightModelServiceFactory lightModelFactory,
        ModeHolder modeHolder,
        AutoRotationService? autoRotationService,
        ReviewService? reviewService,
        ScopeResolver? scopeResolver,
        IssueService? issueService)
    {
        Settings = settings;
        _logging = logging;
        _hostLock = hostLock;
        _supervisor = supervisor;
        _lockRegistry = lockRegistry;
        _sessionManager = sessionManager;
        _taskStore = taskStore;
        _taskService = taskService;
        _lightModelFactory = lightModelFactory;
        _modeHolder = modeHolder;
        _autoRotationService = autoRotationService;
        _reviewService = reviewService;
        _scopeResolver = scopeResolver;
        _issueService = issueService;
    }

    /// <summary>Mutable shared holder for the host-wide effective approval mode. Both the
    /// per-session sink factory (closure in <see cref="StartAsync"/>) and <see cref="SwitchApprovalMode"/>
    /// read/write through the same instance so a mid-session toggle is observed by any subsequent
    /// session-create call without re-wiring the manager.</summary>
    private sealed class ModeHolder
    {
        public ApprovalMode Mode;
        public ModeHolder(ApprovalMode initial) { Mode = initial; }
    }

    /// <summary>
    /// Stand up the host. Settings layering mirrors the CLI loader (committed defaults, optional
    /// settings.user.json, INFINICONTEXT__ env vars). Bad config surfaces as an exception the
    /// caller can show in a dialog.
    /// </summary>
    public static async Task<SessionHost> StartAsync()
    {
        var loaded = InfiniContextSettingsLoader.Load();
        var settings = loaded.Settings;

        var logging = InfiniContextLogger.ConfigureFromHostStartup(settings.Logging, prefix: "gui");
        logging.Logger.Information("InfiniContext.Gui starting log={LogPath} sources={Sources}",
            logging.LogFilePath, loaded.SourcesLoaded);

        // C1: enforce the single-host contract before opening any shared ~/.infinicontext state.
        // A GUI running alongside a CLI host (or a second GUI) breaks every cross-session invariant
        // because each process coordinates locks only in-memory. HostInstanceLock.Acquire throws
        // HostAlreadyRunningException if another host holds the sentinel; that propagates out of
        // CreateAsync and the App shows it in the startup banner. Flush the logger we just opened
        // before rethrowing so the refusal is recorded. Held for the host's life, released in
        // DisposeAsync (and by the OS on crash via DeleteOnClose).
        HostInstanceLock hostLock;
        try
        {
            hostLock = HostInstanceLock.Acquire(logger: logging.Logger);
        }
        catch (HostAlreadyRunningException ex)
        {
            logging.Logger.Error(ex, "host-lock: refused to start (another host running)");
            await logging.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var supervisor = ProcessSupervisor.CreateForCurrentPlatform(logging.Logger);
        var lockRegistry = new LockRegistry(logger: logging.Logger);

        // Approval routing per settings.Approval.Mode (slice 4k). Picks the inner sink at
        // session-spawn time using the per-session cwd plumbed through SessionApprovalSinkFactory
        // in slice 4j; auto-mode's safety prompt is cwd-aware as of slice 4h.
        //
        // Mode -> sink:
        //   Ask  : SlotApprovalSink -> banner UI in the focused pane (parity with CLI ask).
        //   Deny : StaticApprovalSink(Deny) -> every tool call auto-denied.
        //   Allow: StaticApprovalSink(Allow) -> every tool call auto-allowed (logged as warning;
        //          GUI status-strip warning is a polish follow-up).
        //   Auto : AutoApprovalSink(light model) with the slot sink as fallback per
        //          approval.auto.onLowConfidence. Mirrors CLI ApprovalSinkFactory wiring.
        //
        // writeTranscriptBanner=false on the slot sink: the GUI's banner UI carries the same
        // information visually, so CLI-style "[approval] tool=..." lines stay out of the
        // transcript model.
        //
        // Mid-session toggle (status-strip ComboBox): the host-wide effective mode (D12) lives in
        // a shared ModeHolder that both the per-session sink factory below AND SessionHost itself
        // point at. Session-create reads it; SwitchApprovalMode mutates it. Auto -> Ask fallback
        // pins the post-fallback value so the next session-create observes the resolved mode.
        var lightModelFactory = new LightModelServiceFactory(settings, logging.Logger, supervisor);
        var initialMode = settings.Approval.Mode;
        var modeHolder = new ModeHolder(initialMode);
        SessionApprovalSinkFactory approvalFactory = (slot, transcript, cwd) =>
        {
            var inner = BuildApprovalSink(modeHolder.Mode, settings, slot, transcript, cwd, lightModelFactory, logging.Logger, out var effective);
            modeHolder.Mode = effective;
            return inner;
        };

        var manager = await SessionManager.OpenAsync(
            settings,
            logging.Logger,
            approvalFactory,
            supervisor: supervisor,
            lockRegistry: lockRegistry).ConfigureAwait(false);

        // Best-effort task-store open. Failure (e.g. DB ahead of code per migration policy) is
        // logged but does not block the GUI - the tasks tree degrades to "(no task subsystem)"
        // and sessions still spawn under the (unattached) root.
        TaskStore? taskStore = null;
        TaskService? taskService = null;
        ReviewService? reviewService = null;
        ScopeResolver? scopeResolver = null;
        IssueService? issueService = null;
        try
        {
            taskStore = await TaskStore.OpenAsync(settings.Tasks.ResolvedDbPath()).ConfigureAwait(false);
            // M14: startup reconciliation (sound under single-host) — reset any leftover InProgress
            // rows from a crash so they aren't wedged (delete refuses InProgress).
            var reset = await taskStore.ResetInProgressToIdleAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);
            if (reset > 0) logging.Logger.Information("tasks: startup reconcile reset {Count} InProgress row(s) to Idle", reset);
            var lightForTasks = lightModelFactory.Create(thinking: false, cliMode: settings.Models.Light.Cli.Mode);
            taskService = new TaskService(taskStore, lightForTasks);

            // Review + issue subsystems, mirroring the CLI's Program.cs wiring so /task review and
            // /issue work in the GUI through the shared command classes (task 0015 slice E).
            (reviewService, scopeResolver) = BuildReviewStack(settings, supervisor, logging.Logger);
            issueService = new IssueService(taskStore, lockRegistry);
            logging.Logger.Information("tasks: subsystem ready (db={DbPath}); review + issue: ready", settings.Tasks.ResolvedDbPath());
        }
        catch (Exception ex)
        {
            logging.Logger.Warning(ex, "tasks: subsystem unavailable; GUI tree will show sessions under (unattached) only");
        }

        // Task 0013 Slice 2: own the Core auto-rotation service. Same defaults as the CLI
        // (Program.cs:284): 3 rotations per task within 5 minutes. The service only fires when
        // PrepareSwitchAsync is available, mirroring the CLI's `taskService is null` gate.
        var autoRotationGuard = new AutoRotationGuard(maxRotations: 3, window: TimeSpan.FromMinutes(5));
        AutoRotationService? autoRotationService = taskService is null
            ? null
            : new AutoRotationService(
                manager,
                autoRotationGuard,
                (id, ct) => taskService.PrepareSwitchAsync(id, ct),
                logging.Logger);

        return new SessionHost(settings, logging, hostLock, supervisor, lockRegistry, manager, taskStore, taskService, lightModelFactory, modeHolder, autoRotationService, reviewService, scopeResolver, issueService);
    }

    /// <summary>
    /// Build the review-subsystem objects (<see cref="ReviewService"/> + <see cref="ScopeResolver"/>)
    /// used by <c>/task review</c>. M20: the wiring itself now lives in Core's
    /// <see cref="ReviewStackFactory"/>, shared with the CLI (it used to be copied verbatim here).
    /// Returns <c>(null, null)</c> on failure; the caller degrades <c>/task review</c> to an
    /// "unavailable" message rather than crashing the GUI.
    /// </summary>
    private static (ReviewService?, ScopeResolver?) BuildReviewStack(
        InfiniContextSettings settings, IProcessSupervisor supervisor, ILogger logger)
    {
        try
        {
            var stack = ReviewStackFactory.Build(settings, supervisor, logger);
            return (stack.ReviewService, stack.ScopeResolver);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "review: stack init failed; /task review will report 'unavailable'");
            return (null, null);
        }
    }

    /// <summary>
    /// Build a <see cref="TaskCommandHandler"/> whose output is written to <paramref name="output"/>
    /// (the GUI binds this to the focused session's transcript). Returns null when the task
    /// subsystem is unavailable. <paramref name="input"/> backs <c>/task review</c>'s interactive
    /// confirmation; the GUI passes <c>null</c>, so GUI review requires the <c>--yes</c> flag.
    /// </summary>
    public TaskCommandHandler? CreateTaskCommandHandler(TextWriter output, TextReader? input = null)
    {
        if (_taskService is null) return null;
        return new TaskCommandHandler(
            _taskService,
            output,
            _handlerWriteLock,
            reviewService: _reviewService,
            scopeResolver: _scopeResolver,
            input: input,
            lockRegistry: _lockRegistry);
    }

    /// <summary>Build an <see cref="IssueCommandHandler"/> writing to <paramref name="output"/>.
    /// Returns null when the issue subsystem is unavailable.</summary>
    public IssueCommandHandler? CreateIssueCommandHandler(TextWriter output) =>
        _issueService is null ? null : new IssueCommandHandler(_issueService, output, _handlerWriteLock);

    /// <summary>
    /// Switch the host-wide approval mode and hot-swap every active session's
    /// <see cref="SwitchableApprovalSink"/> to a fresh inner sink built for <paramref name="requested"/>.
    /// Returns the effective mode (which may differ when Auto's light-model construct fails and
    /// falls back to Ask). Mirrors the CLI's <c>/approval</c> path: D12 says one effective mode
    /// across all sessions, so this iterates <see cref="SessionManager.Active"/> in lockstep.
    /// </summary>
    public ApprovalMode SwitchApprovalMode(ApprovalMode requested)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SessionHost));

        ApprovalMode effective = requested;
        var active = _sessionManager.Active;
        foreach (var ctx in active)
        {
            var newInner = BuildApprovalSink(
                requested,
                Settings,
                ctx.ApprovalSlot,
                ctx.Transcript,
                ctx.Cwd ?? string.Empty,
                _lightModelFactory,
                _logging.Logger,
                out effective);
            ctx.ApprovalSink.Switch(newInner);
        }

        _modeHolder.Mode = effective;
        _logging.Logger.Information("gui-approval: mode switched requested={Requested} effective={Effective} sessions={Count}",
            requested, effective, active.Count);
        ApprovalModeChanged?.Invoke(effective);
        return effective;
    }

    private static IApprovalSink BuildApprovalSink(
        ApprovalMode mode,
        InfiniContextSettings settings,
        PendingApprovalSlot slot,
        TranscriptModel transcript,
        string cwd,
        LightModelServiceFactory lightModelFactory,
        ILogger logger,
        out ApprovalMode effectiveMode)
    {
        // M20: the mode→sink decision (+ Auto construct/fallback + AutoApprovalSink wiring + logging)
        // is Core's ApprovalSinkBuilder, shared with the CLI. The GUI's host-specific policy is
        // writeTranscriptBanner=false (the focused pane's banner UI carries closure) and returning
        // the Ask sink BARE — no transcript echo — while non-ask sinks are echo-wrapped so Deny/Allow
        // leave a record and the Auto light-model verdict surfaces in the pane. The Ask timeout is
        // derived inside the builder from settings.Approval.TimeoutSeconds.
        var builder = new ApprovalSinkBuilder(
            settings, logger,
            effort => lightModelFactory.Create(thinking: effort == ApprovalAutoEffort.High, cliMode: settings.Approval.Auto.Mode),
            writeTranscriptBanner: false);

        var result = builder.BuildInner(mode, slot, transcript, cwd);
        effectiveMode = result.EffectiveMode;

        // Ask (including an Auto→Ask fallback) returns bare: a trailing transcript echo would be
        // redundant clutter next to the banner UI. All other modes echo their decision into the
        // originating session's transcript. Format matches the CLI: "[approval] Tool -> ALLOW (Nms)".
        if (effectiveMode == ApprovalMode.Ask) return result.Sink;
        return new ConsoleEchoingApprovalSink(result.Sink, line => transcript.Append(line + "\n"));
    }

    public async Task<SessionContext> CreateSessionAsync(
        string cwd,
        int? activeTaskId = null,
        string? firstUserMessage = null,
        string? model = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        var ctx = await _sessionManager.CreateAsync(
            BuildSpawnRequest(Settings, cwd, activeTaskId, firstUserMessage, model),
            ct).ConfigureAwait(false);
        SessionCreated?.Invoke(ctx);
        return ctx;
    }

    /// <summary>Build the <see cref="SpawnRequest"/> for a GUI session spawn. Pure function of
    /// settings + the per-spawn arguments so the settings-to-request mapping (model precedence
    /// chain, <c>session.extraArgs</c> passthrough) is unit-testable without standing up a host.</summary>
    public static SpawnRequest BuildSpawnRequest(
        InfiniContextSettings settings,
        string cwd,
        int? activeTaskId = null,
        string? firstUserMessage = null,
        string? model = null) => new()
        {
            Cwd = cwd,
            // Precedence: explicit per-spawn model (e.g. a task's default_model or a
            // respawn-to-change request) > session.model setting > configured main model id.
            // Falling back to KnownModels.Main pins the model rather than letting claude pick
            // its own default.
            Model = ResolveModel(settings, model),
            IncludePartialMessages = settings.Session.IncludePartialMessages,
            ExtraArgs = settings.Session.ExtraArgs,
            ActiveTaskId = activeTaskId,
            FirstUserMessage = firstUserMessage,
        };

    /// <summary>Resolve the effective model id for a spawn: an explicit choice wins, else the
    /// configured session model, else the pinned main model id. Blank explicit values are treated
    /// as "unset" so a null/empty task default falls through to the settings chain.</summary>
    public string ResolveModel(string? explicitModel) => ResolveModel(Settings, explicitModel);

    /// <summary>Static core of <see cref="ResolveModel(string?)"/>; see there for the chain.</summary>
    public static string ResolveModel(InfiniContextSettings settings, string? explicitModel) =>
        string.IsNullOrWhiteSpace(explicitModel)
            ? settings.Session.Model ?? settings.Models.Main.Id
            : explicitModel.Trim();

    public async Task CloseSessionAsync(Guid id)
    {
        await _sessionManager.CloseAsync(id).ConfigureAwait(false);
        SessionClosed?.Invoke(id);
    }

    public async Task<IReadOnlyList<TaskRecord>> ListTasksAsync(CancellationToken ct = default)
    {
        if (_taskStore is null) return Array.Empty<TaskRecord>();
        try
        {
            return await _taskStore.ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logging.Logger.Warning(ex, "tasks: ListAsync failed");
            return Array.Empty<TaskRecord>();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Dispose the session manager BEFORE the auto-rotation service so any Exited transitions
        // raised by killing the child processes still propagate to AutoRotationService while it's
        // subscribed. The earlier order (service first) silently lost app-shutdown rotations:
        // unsubscribing the per-ctx Transitioned handler before the child kill meant the Exited
        // event arrived with no listener, so no proposal/log line was ever raised. See task 0013
        // bug B (the 2026-05-27 manual smoke handoff in progress.md).
        try { await _sessionManager.DisposeAsync().ConfigureAwait(false); }
        catch { /* logged inside manager */ }
        _autoRotationService?.Dispose();
        if (_taskStore is not null)
        {
            try { await _taskStore.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
        _supervisor.Dispose();
        // C1: release the single-host sentinel last among shared-state owners so a fresh host can
        // start as soon as this one has torn down its registry/index writers.
        _hostLock.Dispose();
        await _logging.DisposeAsync().ConfigureAwait(false);
    }
}
