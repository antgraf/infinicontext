using System.Diagnostics;
using System.Runtime.Versioning;
using InfiniContext.Core.Supervision.Posix;
using FluentAssertions;

// Each test method below either short-circuits on Windows or is gated by [MacOnlyFact]. The
// analyzer's platform-guard recognition only fires on `if (OperatingSystem.IsXxx) { /* use */ }`
// scoping, not on `if (...) return; use;` — both forms are correct at runtime but the latter
// trips CA1416. Suppressing here keeps the test bodies readable.
#pragma warning disable CA1416

namespace InfiniContext.Tests.Supervision;

/// <summary>
/// Stage 2B Phase 7. Two layers of coverage for the POSIX supervisor:
///
/// <list type="bullet">
///   <item><see cref="UnixOnlyFactAttribute"/>-gated checks that exercise the public contract
///   through plain method calls — these run on any POSIX kernel (macOS in CI, Linux locally)
///   because they only inspect supervisor state and don't actually send signals. They guard against
///   regressions in the bookkeeping (attach guard, idempotent dispose). On Windows they report as
///   <em>skipped</em> (the supervisor's constructor throws there), not as a vacuous pass.</item>
///   <item><see cref="MacOnlyFactAttribute"/>-gated E2E tests that spawn a real child via
///   <c>/bin/sh -c "sleep 600"</c> and verify Dispose terminates it within the grace window.
///   These can't run on Windows runners (no <c>sh</c> on path; no SIGTERM semantics) and are the
///   first time the macOS code is exercised on actual macOS per Stage 2B D16.</item>
/// </list>
/// </summary>
public class PosixSignalSupervisorTests
{
    [WindowsOnlyFact]
    [UnsupportedOSPlatform("windows")]
    public void Constructor_throws_on_Windows()
    {
        Action act = () => new PosixSignalSupervisor();
        act.Should().Throw<PlatformNotSupportedException>();
    }

    [UnixOnlyFact]
    public void Attach_throws_when_process_not_started()
    {
        using var supervisor = new PosixSignalSupervisor();
        var unstarted = new Process();
        try
        {
            Action act = () => supervisor.Attach(unstarted);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*not been started*");
        }
        finally
        {
            unstarted.Dispose();
        }
    }

    [UnixOnlyFact]
    public void Attach_after_dispose_throws()
    {
        var supervisor = new PosixSignalSupervisor();
        supervisor.Dispose();
        var unstarted = new Process();
        try
        {
            Action act = () => supervisor.Attach(unstarted);
            act.Should().Throw<ObjectDisposedException>();
        }
        finally
        {
            unstarted.Dispose();
        }
    }

    [UnixOnlyFact]
    public void Dispose_is_idempotent()
    {
        var supervisor = new PosixSignalSupervisor();
        supervisor.Dispose();
        Action act = () => supervisor.Dispose();
        act.Should().NotThrow();
    }

    [MacOnlyFact]
    public async Task Dispose_kills_attached_child_within_grace_plus_buffer()
    {
        // Real child: /bin/sh -c "sleep 600" sleeps ~10 minutes unless we kill it. The supervisor
        // is given a tight grace (200 ms) so SIGKILL fires fast in the worst case; the assertion
        // window is generous (5 s) to absorb CI jitter.
        Process child;
        int childPid;

        using (var supervisor = new PosixSignalSupervisor(gracePeriod: TimeSpan.FromMilliseconds(200)))
        {
            child = StartLongRunningChild();
            try
            {
                supervisor.Attach(child);
                childPid = child.Id;
            }
            catch
            {
                try { child.Kill(); } catch { }
                child.Dispose();
                throw;
            }

            child.HasExited.Should().BeFalse("child should be alive while supervisor is alive");
        } // <- Dispose triggers SIGTERM → grace → SIGKILL.

        var died = await WaitForExitAsync(childPid, TimeSpan.FromSeconds(5));
        died.Should().BeTrue($"pid {childPid} should be dead within 5s of supervisor dispose");

        child.Dispose();
    }

    [MacOnlyFact]
    public async Task DetachAll_then_dispose_lets_child_survive()
    {
        var child = StartLongRunningChild();
        try
        {
            using (var supervisor = new PosixSignalSupervisor(gracePeriod: TimeSpan.FromMilliseconds(200)))
            {
                supervisor.Attach(child);
                supervisor.DetachAll();
            } // <- Dispose runs the reap path but it skips because _detached=true.

            // Brief grace period to let any (incorrect) kill propagate.
            await Task.Delay(500);
            child.HasExited.Should().BeFalse("DetachAll skips the reap; child must survive");
        }
        finally
        {
            try { child.Kill(); } catch { }
            child.Dispose();
        }
    }

    [MacOnlyFact]
    public async Task Dispose_after_child_already_exited_does_not_throw()
    {
        Process child;
        using (var supervisor = new PosixSignalSupervisor(gracePeriod: TimeSpan.FromMilliseconds(200)))
        {
            child = StartShortChild();
            supervisor.Attach(child);

            await child.WaitForExitAsync();
            child.HasExited.Should().BeTrue();
        } // <- Dispose reap-loop should skip the already-exited child cleanly.

        child.Dispose();
    }

    // ---------------------------------------------------------------------------------------------
    // Host-lifecycle smoke (task 0023 C2/C3). The reap-primitive tests above cannot observe the
    // signal-handler *interaction* the audit flagged — that needs a real second process running a
    // live supervisor that we then signal. SupervisorHostProbe is that process: it wires the
    // production supervisor exactly as the CLI host does, attaches a child, mirrors the host's
    // ownership of Ctrl+C, prints CHILD_PID + READY, and blocks. We drive it with real signals.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// C2: SIGINT is the interactive host's "interrupt the turn" gesture. The supervisor must NOT
    /// register it, so a Ctrl+C leaves both the host and every session child alive. (Pre-fix the
    /// supervisor reaped on SIGINT, killing the child within the grace window.)
    /// </summary>
    [MacOnlyFact]
    public async Task HostLifecycle_SIGINT_leaves_host_and_child_alive()
    {
        var (probe, childPid) = await StartProbeAndWaitReadyAsync(TimeSpan.FromSeconds(20));
        try
        {
            SendSignal(probe.Id, "INT");

            // Wait comfortably past the supervisor's default grace (2s): on the pre-fix code the
            // child would have been SIGTERM'd then SIGKILL'd by now. On the fixed code nothing reaps.
            await Task.Delay(TimeSpan.FromSeconds(4));

            probe.HasExited.Should().BeFalse("the host owns SIGINT (Cancel=true) and must stay alive");
            IsAlive(childPid).Should().BeTrue($"child pid {childPid} must survive SIGINT — the supervisor must not reap on it (C2)");
        }
        finally
        {
            await KillProbeAndChildAsync(probe, childPid);
        }
    }

    /// <summary>
    /// C3: SIGTERM/SIGHUP/SIGQUIT mean "terminate the host". The supervisor reaps children and then
    /// the host actually exits. (Pre-fix it set <c>ctx.Cancel = true</c>, leaving a zombie host with
    /// dead sessions, clearable only by <c>kill -9</c>.)
    /// </summary>
    [MacOnlyFact]
    public async Task HostLifecycle_SIGTERM_terminates_host_and_child()
    {
        var (probe, childPid) = await StartProbeAndWaitReadyAsync(TimeSpan.FromSeconds(20));
        try
        {
            SendSignal(probe.Id, "TERM");

            var hostExited = await WaitForProcessExitAsync(probe, TimeSpan.FromSeconds(10));
            hostExited.Should().BeTrue("the host must terminate on SIGTERM, not zombie (C3)");

            // After the host exits, the (SIGTERM'd) child is reparented to launchd/init and reaped,
            // so its pid disappears. Poll for that within a generous window.
            var childGone = await WaitForPidGoneAsync(childPid, TimeSpan.FromSeconds(5));
            childGone.Should().BeTrue($"child pid {childPid} must die when the host is terminated");
        }
        finally
        {
            await KillProbeAndChildAsync(probe, childPid);
        }
    }

    private static async Task<(Process probe, int childPid)> StartProbeAndWaitReadyAsync(TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SupervisorHostProbeRunner.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var probe = Process.Start(psi)!;

        int? childPid = null;
        var sawReady = false;
        var deadline = DateTime.UtcNow + timeout;
        while (!sawReady)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            var readTask = probe.StandardOutput.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(remaining));
            if (completed != readTask) break;            // timed out waiting for a line
            var line = await readTask;
            if (line is null) break;                     // stream closed: probe died early

            const string pidPrefix = "CHILD_PID=";
            if (line.StartsWith(pidPrefix, StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(pidPrefix.Length), out var pid))
                childPid = pid;
            else if (line == "READY")
                sawReady = true;
        }

        if (!sawReady || childPid is null)
        {
            try { probe.Kill(entireProcessTree: true); } catch { }
            probe.Dispose();
            throw new InvalidOperationException("SupervisorHostProbe did not report CHILD_PID + READY in time.");
        }

        return (probe, childPid.Value);
    }

    /// <summary>Sends a signal by name (e.g. "INT", "TERM") to a pid via <c>/bin/kill -s</c>.</summary>
    private static void SendSignal(int pid, string name) => RunKill($"-s {name} {pid}");

    /// <summary><c>kill -0</c> sends no signal; exit 0 means the pid exists (and is signalable).</summary>
    private static bool IsAlive(int pid) => RunKill($"-0 {pid}") == 0;

    private static int RunKill(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/kill",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit(5000);
        return p.HasExited ? p.ExitCode : -1;
    }

    private static async Task<bool> WaitForProcessExitAsync(Process p, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await p.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return p.HasExited;
        }
    }

    private static async Task<bool> WaitForPidGoneAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsAlive(pid)) return true;
            await Task.Delay(100);
        }
        return !IsAlive(pid);
    }

    private static async Task KillProbeAndChildAsync(Process probe, int childPid)
    {
        try { if (!probe.HasExited) SendSignal(probe.Id, "KILL"); } catch { }
        try { if (IsAlive(childPid)) SendSignal(childPid, "KILL"); } catch { }
        try { await WaitForProcessExitAsync(probe, TimeSpan.FromSeconds(3)); } catch { }
        probe.Dispose();
    }

    /// <summary>Long-running child that survives until killed. /bin/sh is on macOS by default.</summary>
    private static Process StartLongRunningChild()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = "-c \"sleep 600\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        return Process.Start(psi)!;
    }

    /// <summary>Self-terminating child that exits ~immediately.</summary>
    private static Process StartShortChild()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = "-c \"exit 0\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        return Process.Start(psi)!;
    }

    private static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return true;
            }
            catch (ArgumentException)
            {
                return true; // pid is gone
            }
            await Task.Delay(50);
        }
        return false;
    }
}
