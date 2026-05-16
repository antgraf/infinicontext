using System.Diagnostics;
using System.Runtime.Versioning;
using InfiniContext.Core.Supervision.Windows;
using FluentAssertions;

namespace InfiniContext.Tests.Supervision;

/// <summary>
/// Windows-only tests for the Job Object backend. The OS-level guarantee under test:
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> means closing the job handle terminates every
/// attached process. The handle closes both on explicit <see cref="JobObjectSupervisor.Dispose"/>
/// and implicitly when the host process dies (Windows closes all kernel handles on termination),
/// so a clean-dispose test exercises the same kernel path as a host crash.
/// </summary>
[Trait("Platform", "Windows")]
[SupportedOSPlatform("windows")]
public class JobObjectSupervisorTests
{
    [WindowsOnlyFact]
    public async Task Dispose_kills_attached_child_within_5s()
    {
        Process child;
        int childPid;

        using (var supervisor = new JobObjectSupervisor())
        {
            child = StartLongRunningChild();
            try
            {
                supervisor.Attach(child);
                childPid = child.Id;
            }
            catch
            {
                try { child.Kill(entireProcessTree: true); } catch { }
                child.Dispose();
                throw;
            }

            child.HasExited.Should().BeFalse("child should be alive while supervisor is alive");
        } // <- Dispose closes the job handle; KILL_ON_JOB_CLOSE terminates the child.

        var killed = await WaitForExitAsync(childPid, TimeSpan.FromSeconds(5));
        killed.Should().BeTrue($"pid {childPid} should be dead within 5s of supervisor dispose");

        child.Dispose();
    }

    [WindowsOnlyFact]
    public async Task DetachAll_then_dispose_lets_child_survive()
    {
        var child = StartLongRunningChild();
        try
        {
            using (var supervisor = new JobObjectSupervisor())
            {
                supervisor.Attach(child);
                supervisor.DetachAll();
            } // <- Dispose without KILL_ON_JOB_CLOSE: child must survive.

            // Brief grace period to let any (incorrect) kill propagate.
            await Task.Delay(500);
            child.HasExited.Should().BeFalse("DetachAll cleared kill-on-close; child must survive");
        }
        finally
        {
            try { child.Kill(entireProcessTree: true); } catch { }
            child.Dispose();
        }
    }

    [WindowsOnlyFact]
    public void Attach_after_dispose_throws()
    {
        var supervisor = new JobObjectSupervisor();
        supervisor.Dispose();

        var child = StartLongRunningChild();
        try
        {
            Action act = () => supervisor.Attach(child);
            act.Should().Throw<ObjectDisposedException>();
        }
        finally
        {
            try { child.Kill(entireProcessTree: true); } catch { }
            child.Dispose();
        }
    }

    /// <summary>
    /// <c>ping 127.0.0.1 -n 600 -w 1000</c> sleeps ~10 minutes without needing a console attached
    /// (unlike <c>timeout</c>). Output is discarded.
    /// </summary>
    private static Process StartLongRunningChild()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping 127.0.0.1 -n 600 -w 1000 > nul",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var p = Process.Start(psi)!;
        return p;
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
                // Process is gone — GetProcessById throws when there is no such pid.
                return true;
            }
            await Task.Delay(100);
        }
        return false;
    }
}
