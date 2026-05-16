using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace InfiniContext.Core.Supervision.Posix;

/// <summary>
/// Stage 2B Phase 7 macOS/Linux backend for <see cref="IProcessSupervisor"/>. Tracks attached
/// child PIDs in-process and, on any of {clean dispose, host-process exit, SIGINT/SIGTERM/SIGHUP/SIGQUIT
/// delivered to the host}, sends SIGTERM to every survivor, waits up to <see cref="GracePeriod"/>
/// for them to leave, then sends SIGKILL.
///
/// <para>
/// Unlike the Windows backend (which leans on the kernel's Job Object + <c>KILL_ON_JOB_CLOSE</c>
/// to do the kill walk for us), POSIX has no equivalent kernel primitive a non-root process can
/// rely on. The compromise:
/// </para>
/// <list type="bullet">
///   <item><see cref="PosixSignalRegistration"/> covers the four signals normally responsible for a
///   clean shutdown (cmd-C, <c>kill</c>, terminal close, <c>kill -3</c>). Inside the handler we
///   set <c>cancel=true</c> so the runtime's default handler doesn't fire first, then synchronously
///   reap children before returning — at which point the runtime delivers its post-handler exit.</item>
///   <item><see cref="AppDomain.ProcessExit"/> covers clean managed shutdown (e.g. <c>return</c>
///   from <c>Main</c>) and is the path for tests that don't signal-kill.</item>
///   <item>SIGKILL of the host (<c>kill -9</c>) cannot be intercepted — children are orphaned.
///   That's the documented limitation in <c>docs/infinicontext-initial-design.md</c> §5.</item>
/// </list>
///
/// <para>
/// Concurrent <see cref="Attach"/> / dispose / signal arrival is safe: the PID set is guarded by a
/// gate and reap-loops snapshot the set before sending signals so they can't race a concurrent
/// Attach. Idempotent: a second reap-loop (e.g. ProcessExit firing after Dispose) finds no
/// survivors.
/// </para>
/// </summary>
[UnsupportedOSPlatform("windows")]
[UnsupportedOSPlatform("browser")]
public sealed class PosixSignalSupervisor : IProcessSupervisor
{
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<int, Process> _children = new();
    private readonly List<PosixSignalRegistration> _signalRegistrations = new();
    private readonly EventHandler _processExitHandler;
    private bool _detached;
    private bool _disposed;

    /// <summary>How long to wait between SIGTERM and SIGKILL during reap. Configurable for tests.</summary>
    public TimeSpan GracePeriod { get; }

    public PosixSignalSupervisor(ILogger? logger = null, TimeSpan? gracePeriod = null)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException($"{nameof(PosixSignalSupervisor)} is not supported on Windows; use JobObjectSupervisor.");

        _logger = logger ?? Log.Logger;
        GracePeriod = gracePeriod ?? TimeSpan.FromSeconds(2);

        // PosixSignalRegistration is the cross-platform .NET wrapper around sigaction. Each
        // registration cookie is disposable; we hold them in a list and dispose on teardown.
        TryRegisterSignal(PosixSignal.SIGINT);
        TryRegisterSignal(PosixSignal.SIGTERM);
        TryRegisterSignal(PosixSignal.SIGHUP);
        TryRegisterSignal(PosixSignal.SIGQUIT);

        _processExitHandler = (_, _) => ReapAll(reason: "process-exit");
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;

        _logger.Information("supervisor: PosixSignalSupervisor active (grace={GraceMs}ms; SIGINT/SIGTERM/SIGHUP/SIGQUIT + ProcessExit)",
            (int)GracePeriod.TotalMilliseconds);
    }

    public void Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            int pid;
            try { pid = process.Id; }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    "Cannot attach a process that has not been started.", ex);
            }

            if (_children.ContainsKey(pid))
            {
                _logger.Debug("supervisor: pid={Pid} already attached", pid);
                return;
            }

            _children[pid] = process;
            _logger.Information("supervisor: attached pid={Pid}", pid);
        }
    }

    public void DetachAll()
    {
        lock (_gate)
        {
            if (_disposed || _detached) return;
            var n = _children.Count;
            _children.Clear();
            _detached = true;
            _logger.Information("supervisor: detached all ({Count} pids released — will survive host exit)", n);
        }
    }

    public void Dispose()
    {
        // Run the reap before tearing down signal/exit hooks. Dispose is the only path that
        // intentionally walks the kill, so don't suppress it the way ProcessExit does after a
        // signal-driven reap.
        bool alreadyDisposed;
        lock (_gate)
        {
            alreadyDisposed = _disposed;
            _disposed = true;
        }
        if (alreadyDisposed) return;

        ReapAll(reason: "dispose");

        try { AppDomain.CurrentDomain.ProcessExit -= _processExitHandler; } catch { }

        // Drop the signal registrations so we don't trigger a no-op reap after the supervisor is
        // gone. Each registration's Dispose is documented as safe to call multiple times.
        lock (_gate)
        {
            foreach (var reg in _signalRegistrations)
            {
                try { reg.Dispose(); } catch (Exception ex) { _logger.Warning(ex, "supervisor: signal registration dispose failed"); }
            }
            _signalRegistrations.Clear();
        }

        _logger.Information("supervisor: disposed");
    }

    /// <summary>Snapshot of attached pids. Test/debug only.</summary>
    public IReadOnlyList<int> AttachedPids()
    {
        lock (_gate) return _children.Keys.ToArray();
    }

    private void TryRegisterSignal(PosixSignal signal)
    {
        try
        {
            var reg = PosixSignalRegistration.Create(signal, OnSignal);
            _signalRegistrations.Add(reg);
        }
        catch (Exception ex)
        {
            // Some hosting environments (e.g. .NET on Linux containers without SIGQUIT support)
            // refuse certain signals. Don't fail construction; just log and continue with whatever
            // signals did register — ProcessExit is still wired.
            _logger.Warning(ex, "supervisor: failed to register {Signal}", signal);
        }
    }

    private void OnSignal(PosixSignalContext ctx)
    {
        // Suppress the runtime's default handling so we get to reap before the host disappears.
        // The runtime will still terminate the process once we return — Cancel=true only stops the
        // *default* handler, not the eventual exit cycle that ProcessExit also fires from.
        ctx.Cancel = true;
        ReapAll(reason: $"signal:{ctx.Signal}");
    }

    private void ReapAll(string reason)
    {
        if (_detached)
        {
            // DetachAll() was called — the user took ownership of these children.
            _logger.Information("supervisor: reap skipped (reason={Reason}) — children were detached", reason);
            return;
        }

        Process[] survivors;
        lock (_gate)
        {
            if (_children.Count == 0) return;
            // Filter out children that have already exited so we don't burn the grace window on them.
            survivors = _children.Values.Where(p =>
            {
                try { return !p.HasExited; }
                catch { return false; }
            }).ToArray();
        }

        if (survivors.Length == 0)
        {
            _logger.Information("supervisor: reap reason={Reason} — no live survivors", reason);
            return;
        }

        _logger.Information("supervisor: reap reason={Reason} survivors={Count} — SIGTERM", reason, survivors.Length);

        foreach (var p in survivors)
        {
            try
            {
                if (!p.HasExited) sys_kill(p.Id, SIGTERM);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "supervisor: SIGTERM failed for pid={Pid}", SafePid(p));
            }
        }

        // Bounded wait for graceful exit. Spin-poll because we're potentially inside a signal
        // handler — Process.WaitForExitAsync is fine here because PosixSignalRegistration delivers
        // the handler on a thread-pool thread, not synchronously inside the signal context.
        var deadline = DateTime.UtcNow + GracePeriod;
        var remaining = new List<Process>(survivors);
        while (remaining.Count > 0 && DateTime.UtcNow < deadline)
        {
            remaining.RemoveAll(p =>
            {
                try { return p.HasExited; }
                catch { return true; }
            });
            if (remaining.Count == 0) break;
            Thread.Sleep(20);
        }

        if (remaining.Count > 0)
        {
            _logger.Warning("supervisor: {Count} survivor(s) after grace window — SIGKILL", remaining.Count);
            foreach (var p in remaining)
            {
                try
                {
                    if (!p.HasExited) sys_kill(p.Id, SIGKILL);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "supervisor: SIGKILL failed for pid={Pid}", SafePid(p));
                }
            }
        }

        _logger.Information("supervisor: reap complete reason={Reason}", reason);
    }

    private static int SafePid(Process p)
    {
        try { return p.Id; } catch { return -1; }
    }

    // SIGTERM = 15 and SIGKILL = 9 across every POSIX platform we ship on (macOS, Linux). These
    // are stable parts of the POSIX ABI; hard-coding avoids a dependency on libc constants.
    private const int SIGTERM = 15;
    private const int SIGKILL = 9;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int sys_kill(int pid, int signal);
}
