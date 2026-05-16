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
///   <item>Platform-agnostic checks that exercise the public contract through plain method calls
///   — these run everywhere because they only inspect supervisor state and don't actually send
///   signals. They guard against regressions in the bookkeeping (attach guard, idempotent
///   dispose, DetachAll skipping the reap).</item>
///   <item><see cref="MacOnlyFactAttribute"/>-gated E2E tests that spawn a real child via
///   <c>/bin/sh -c "sleep 600"</c> and verify Dispose terminates it within the grace window.
///   These can't run on Windows runners (no <c>sh</c> on path; no SIGTERM semantics) and are the
///   first time the macOS code is exercised on actual macOS per Stage 2B D16.</item>
/// </list>
/// </summary>
public class PosixSignalSupervisorTests
{
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Constructor_throws_on_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        Action act = () => new PosixSignalSupervisor();
        act.Should().Throw<PlatformNotSupportedException>();
    }

    [Fact]
    public void Attach_throws_when_process_not_started()
    {
        if (OperatingSystem.IsWindows()) return;
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

    [Fact]
    public void Attach_after_dispose_throws()
    {
        if (OperatingSystem.IsWindows()) return;
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

    [Fact]
    public void Dispose_is_idempotent()
    {
        if (OperatingSystem.IsWindows()) return;
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
