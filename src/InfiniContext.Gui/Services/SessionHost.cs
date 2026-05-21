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
    private int _disposed;

    public InfiniContextSettings Settings { get; }
    public string LogFilePath => _logging.LogFilePath;
    public SessionManager SessionManager => _sessionManager;
    public TaskStore? TaskStore => _taskStore;
    public ILogger Logger => _logging.Logger;
    public event Action<SessionContext>? SessionCreated;
    public event Action<Guid>? SessionClosed;

    private SessionHost(
        InfiniContextSettings settings,
        LoggingHandle logging,
        IProcessSupervisor supervisor,
        LockRegistry lockRegistry,
        SessionManager sessionManager,
        TaskStore? taskStore)
    {
        Settings = settings;
        _logging = logging;
        _supervisor = supervisor;
        _lockRegistry = lockRegistry;
        _sessionManager = sessionManager;
        _taskStore = taskStore;
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
        var approvalTimeout = TimeSpan.FromSeconds(settings.Approval.TimeoutSeconds);
        var lightModelFactory = new LightModelServiceFactory(settings, logging.Logger, supervisor);
        SessionApprovalSinkFactory approvalFactory = (slot, transcript, cwd) =>
            BuildApprovalSink(settings, slot, transcript, cwd, approvalTimeout, lightModelFactory, logging.Logger);

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
        try
        {
            taskStore = await TaskStore.OpenAsync(settings.Tasks.ResolvedDbPath()).ConfigureAwait(false);
            logging.Logger.Information("tasks: subsystem ready (db={DbPath})", settings.Tasks.ResolvedDbPath());
        }
        catch (Exception ex)
        {
            logging.Logger.Warning(ex, "tasks: subsystem unavailable; GUI tree will show sessions under (unattached) only");
        }

        return new SessionHost(settings, logging, supervisor, lockRegistry, manager, taskStore);
    }

    private static IApprovalSink BuildApprovalSink(
        InfiniContextSettings settings,
        PendingApprovalSlot slot,
        TranscriptModel transcript,
        string cwd,
        TimeSpan askTimeout,
        LightModelServiceFactory lightModelFactory,
        ILogger logger)
    {
        var slotSink = new SlotApprovalSink(slot, transcript, timeout: askTimeout, writeTranscriptBanner: false);
        IApprovalSink inner;
        switch (settings.Approval.Mode)
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
                    logger.Error(ex, "gui-approval: mode=auto; failed to construct light model вЂ” falling back to ask for cwd={Cwd}", cwd);
                    return slotSink;
                }
                logger.Information(
                    "gui-approval: mode=auto effort={Effort} on-low-confidence={OnLowConfidence} timeout={Timeout}s cwd={Cwd}",
                    effort, settings.Approval.Auto.OnLowConfidence, settings.Approval.Auto.TimeoutSeconds, cwd);
                inner = new AutoApprovalSink(light, slotSink, settings.Approval.Auto, cwd, logger);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(settings), settings.Approval.Mode, "unknown approval mode");
        }

        // Wrap non-ask sinks so every decision is echoed into the originating session's transcript.
        // For Deny/Allow this gives operators a record that the tool call happened and what was
        // decided; for Auto this is the only place the light-model verdict surfaces in the UI
        // (Serilog still logs centrally). Format matches the CLI: "[approval] Tool -> ALLOW (Nms)".
        return new ConsoleEchoingApprovalSink(inner, line => transcript.Append(line + "\n"));
    }

    public async Task<SessionContext> CreateSessionAsync(string cwd, int? activeTaskId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        var ctx = await _sessionManager.CreateAsync(
            new SpawnRequest
            {
                Cwd = cwd,
                Model = Settings.Session.Model,
                IncludePartialMessages = Settings.Session.IncludePartialMessages,
                ActiveTaskId = activeTaskId,
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
