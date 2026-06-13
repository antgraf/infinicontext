using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace InfiniContext.Core.Supervision.Posix;

/// <summary>
/// Stage 2B Phase 7 macOS/Linux backend for <see cref="IProcessSupervisor"/>. Tracks attached
/// child PIDs in-process and, on any of {clean dispose, host-process exit, SIGTERM/SIGHUP/SIGQUIT
/// delivered to the host}, sends SIGTERM to every survivor, waits up to <see cref="GracePeriod"/>
/// for them to leave, then sends SIGKILL.
///
/// <para>
/// Unlike the Windows backend (which leans on the kernel's Job Object + <c>KILL_ON_JOB_CLOSE</c>
/// to do the kill walk for us), POSIX has no equivalent kernel primitive a non-root process can
/// rely on. The compromise:
/// </para>
/// <list type="bullet">
///   <item><see cref="PosixSignalRegistration"/> covers the three signals that mean "terminate the
///   host" (<c>kill</c>, terminal close, <c>kill -3</c>). Inside the handler we synchronously reap
///   children and then <b>terminate the host via <see cref="Environment.Exit(int)"/></b> (exit code
///   <c>128 + signum</c>) — so the host actually exits after its children are gone, rather than
///   lingering as a zombie (audit C3). The earlier design set <c>ctx.Cancel = true</c>, which
///   cancelled the runtime's default termination and produced exactly that zombie.</item>
///   <item><b>SIGINT (Ctrl+C) is deliberately NOT registered</b> (audit C2). On an interactive host
///   Ctrl+C is the "interrupt the current turn" gesture, owned by <c>MultiSessionHost</c> via
///   <c>Console.CancelKeyPress</c> (<c>e.Cancel = true</c>); reaping every session's children on that
///   gesture would destroy all sessions while the host kept running. If a SIGINT is instead allowed
///   to terminate the host (no console handler), .NET's default disposition raises
///   <see cref="AppDomain.ProcessExit"/>, which still reaps. So Ctrl+C never orphans children on a
///   real exit, but it also never kills them on an interrupt.</item>
///   <item><see cref="AppDomain.ProcessExit"/> covers clean managed shutdown (e.g. <c>return</c>
///   from <c>Main</c>, <c>Environment.Exit</c>, and the post-handler exit of an uncancelled signal)
///   and is the path for tests that don't signal-kill.</item>
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
        // SIGINT is intentionally NOT registered — the interactive host owns Ctrl+C as "interrupt
        // the turn" (audit C2). Only the host-terminating signals trigger a reap here; a
        // SIGINT-driven exit still reaps via the ProcessExit hook.
        TryRegisterSignal(PosixSignal.SIGTERM);
        TryRegisterSignal(PosixSignal.SIGHUP);
        TryRegisterSignal(PosixSignal.SIGQUIT);

        _processExitHandler = (_, _) => ReapAll(reason: "process-exit");
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;

        _logger.Information("supervisor: PosixSignalSupervisor active (grace={GraceMs}ms; SIGTERM/SIGHUP/SIGQUIT + ProcessExit)",
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
        // Only the host-terminating signals (SIGTERM/SIGHUP/SIGQUIT) are registered — SIGINT is the
        // interactive host's "interrupt the turn" gesture and is owned elsewhere (audit C2). Reap
        // children synchronously, then deterministically terminate the host via Environment.Exit.
        //
        // The C3 bug was `ctx.Cancel = true`, which cancelled the runtime's default termination and
        // left a zombie host (dead sessions, host still alive, clearable only by `kill -9`). The
        // audit's two fixes were "leave Cancel=false and let the default disposition run" or
        // "Environment.Exit(128+signal) after the reap"; we take the latter because it terminates
        // the host *deterministically* on every POSIX platform, independent of whatever the main
        // thread is blocked on and of per-signal default-disposition nuances. Environment.Exit
        // raises AppDomain.ProcessExit (re-entering ReapAll, which finds no survivors and returns)
        // and then exits with the conventional 128+signum code.
        ReapAll(reason: $"signal:{ctx.Signal}");
        Environment.Exit(128 + SignalNumber(ctx.Signal));
    }

    // PosixSignal is an abstract enum whose values are NOT the POSIX signal numbers, so map the
    // three we register to their conventional numbers for the 128+N exit code. SIGINT (2) is here
    // only for completeness; it is not registered.
    private static int SignalNumber(PosixSignal signal) => signal switch
    {
        PosixSignal.SIGHUP => 1,
        PosixSignal.SIGINT => 2,
        PosixSignal.SIGQUIT => 3,
        PosixSignal.SIGTERM => 15,
        _ => 0,
    };

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
