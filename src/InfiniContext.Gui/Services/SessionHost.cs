using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Logging;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Mcp.Tasks;
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
    private readonly IProcessSupervisor _supervisor;
    private readonly LockRegistry _lockRegistry;
    private readonly SessionManager _sessionManager;
    private readonly TaskStore? _taskStore;
    private readonly TaskService? _taskService;
    private readonly LightModelServiceFactory _lightModelFactory;
    private readonly TimeSpan _approvalTimeout;
    private readonly ModeHolder _modeHolder;
    private readonly AutoRotationService? _autoRotationService;
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
        IProcessSupervisor supervisor,
        LockRegistry lockRegistry,
        SessionManager sessionManager,
        TaskStore? taskStore,
        TaskService? taskService,
        LightModelServiceFactory lightModelFactory,
        TimeSpan approvalTimeout,
        ModeHolder modeHolder,
        AutoRotationService? autoRotationService)
    {
        Settings = settings;
        _logging = logging;
        _supervisor = supervisor;
        _lockRegistry = lockRegistry;
        _sessionManager = sessionManager;
        _taskStore = taskStore;
        _taskService = taskService;
        _lightModelFactory = lightModelFactory;
        _approvalTimeout = approvalTimeout;
        _modeHolder = modeHolder;
        _autoRotationService = autoRotationService;
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
        var approvalTimeout = TimeSpan.FromSeconds(settings.Approval.TimeoutSeconds);
        var lightModelFactory = new LightModelServiceFactory(settings, logging.Logger, supervisor);
        var initialMode = settings.Approval.Mode;
        var modeHolder = new ModeHolder(initialMode);
        SessionApprovalSinkFactory approvalFactory = (slot, transcript, cwd) =>
        {
            var inner = BuildApprovalSink(modeHolder.Mode, settings, slot, transcript, cwd, approvalTimeout, lightModelFactory, logging.Logger, out var effective);
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
        try
        {
            taskStore = await TaskStore.OpenAsync(settings.Tasks.ResolvedDbPath()).ConfigureAwait(false);
            // Light model for TaskService is unused by PrepareSwitchAsync (the only entry point the
            // GUI currently calls), but the ctor requires a non-null instance. Mirrors the CLI's
            // construction at Program.cs:149-150.
            var lightForTasks = lightModelFactory.Create(thinking: false, cliMode: settings.Models.Light.Cli.Mode);
            taskService = new TaskService(taskStore, lightForTasks);
            logging.Logger.Information("tasks: subsystem ready (db={DbPath})", settings.Tasks.ResolvedDbPath());
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

        return new SessionHost(settings, logging, supervisor, lockRegistry, manager, taskStore, taskService, lightModelFactory, approvalTimeout, modeHolder, autoRotationService);
    }

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
                _approvalTimeout,
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
        TimeSpan askTimeout,
        LightModelServiceFactory lightModelFactory,
        ILogger logger,
        out ApprovalMode effectiveMode)
    {
        effectiveMode = mode;
        var slotSink = new SlotApprovalSink(slot, transcript, timeout: askTimeout, writeTranscriptBanner: false);
        IApprovalSink inner;
        switch (mode)
        {
            case ApprovalMode.Ask:
                // Ask returns bare: the banner UI in the focused pane carries closure visually,
                // so a trailing transcript echo would be redundant clutter. Slice 4i intentionally
                // set writeTranscriptBanner=false on the slot sink for the same reason.
                return slotSink;

            case ApprovalMode.Deny:
                logger.Information("gui-approval: mode=deny; all tool calls auto-denied for session cwd={Cwd}", cwd);
                inner = new StaticApprovalSink(ApprovalBehavior.Deny, "auto-deny (approval mode=deny)");
                break;

            case ApprovalMode.Allow:
                logger.Warning("gui-approval: mode=ALLOW; every tool call will be auto-approved without review (cwd={Cwd})", cwd);
                inner = new StaticApprovalSink(ApprovalBehavior.Allow);
                break;

            case ApprovalMode.Auto:
                var effort = settings.Approval.Auto.Effort;
                ILightModelService light;
                try
                {
                    light = lightModelFactory.Create(thinking: effort == ApprovalAutoEffort.High, cliMode: settings.Approval.Auto.Mode);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "gui-approval: mode=auto; failed to construct light model - falling back to ask for cwd={Cwd}", cwd);
                    effectiveMode = ApprovalMode.Ask;
                    return slotSink;
                }
                logger.Information(
                    "gui-approval: mode=auto effort={Effort} on-low-confidence={OnLowConfidence} timeout={Timeout}s cwd={Cwd}",
                    effort, settings.Approval.Auto.OnLowConfidence, settings.Approval.Auto.TimeoutSeconds, cwd);
                inner = new AutoApprovalSink(light, slotSink, settings.Approval.Auto, cwd, logger);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown approval mode");
        }

        // Wrap non-ask sinks so every decision is echoed into the originating session's transcript.
        // For Deny/Allow this gives operators a record that the tool call happened and what was
        // decided; for Auto this is the only place the light-model verdict surfaces in the UI
        // (Serilog still logs centrally). Format matches the CLI: "[approval] Tool -> ALLOW (Nms)".
        return new ConsoleEchoingApprovalSink(inner, line => transcript.Append(line + "\n"));
    }

    public async Task<SessionContext> CreateSessionAsync(
        string cwd,
        int? activeTaskId = null,
        string? firstUserMessage = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        var ctx = await _sessionManager.CreateAsync(
            new SpawnRequest
            {
                Cwd = cwd,
                Model = Settings.Session.Model,
                IncludePartialMessages = Settings.Session.IncludePartialMessages,
                ActiveTaskId = activeTaskId,
                FirstUserMessage = firstUserMessage,
            },
            ct).ConfigureAwait(false);
        SessionCreated?.Invoke(ctx);
        return ctx;
    }

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
        // Detach the auto-rotation service first so its state-machine subscriptions don't outlive
        // the manager teardown below.
        _autoRotationService?.Dispose();
        try { await _sessionManager.DisposeAsync().ConfigureAwait(false); }
        catch { /* logged inside manager */ }
        if (_taskStore is not null)
        {
            try { await _taskStore.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
        _supervisor.Dispose();
        await _logging.DisposeAsync().ConfigureAwait(false);
    }
}
