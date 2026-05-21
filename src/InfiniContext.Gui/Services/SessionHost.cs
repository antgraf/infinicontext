using InfiniContext.Core.Configuration;
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

        // Phase 3B scaffold sink: every approval request auto-denies. The auto-mode light-model
        // path lives in the CLI's ApprovalSinkFactory; porting it to the GUI is a later slice.
        // Users get an inert-but-safe session that can hold a conversation; tool calls (Bash,
        // Edit, Write, etc.) come back with "denied by host". Enough to verify the wire-up.
        SessionApprovalSinkFactory approvalFactory = (_, _) =>
            new DenyAllApprovalSink("auto-deny (GUI Phase 3B scaffold)");

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

    public async Task<SessionContext> CreateSessionAsync(string cwd, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        var ctx = await _sessionManager.CreateAsync(
            new SpawnRequest
            {
                Cwd = cwd,
                Model = Settings.Session.Model,
                IncludePartialMessages = Settings.Session.IncludePartialMessages,
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

    private sealed class DenyAllApprovalSink : IApprovalSink
    {
        private readonly string _message;
        public DenyAllApprovalSink(string message) => _message = message;

        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(ApprovalResponse.Deny(_message));
    }
}
