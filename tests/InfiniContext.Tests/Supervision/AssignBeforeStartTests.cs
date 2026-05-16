using System.Diagnostics;
using System.Runtime.Versioning;
using InfiniContext.Core.Supervision;
using InfiniContext.Core.Supervision.Posix;
using InfiniContext.Core.Supervision.Stub;
using InfiniContext.Core.Supervision.Windows;
using FluentAssertions;

namespace InfiniContext.Tests.Supervision;

/// <summary>
/// Guards the contract documented in <see cref="IProcessSupervisor"/>: <c>Attach</c> must be called
/// after <c>Process.Start()</c>. Calling it on a not-yet-started <see cref="Process"/> instance
/// surfaces as a clear <see cref="InvalidOperationException"/> at the boundary, instead of a
/// confusing Win32 error from deep inside the P/Invoke layer. The same contract holds for the
/// noop fallback so cross-platform unit tests can validate it.
/// </summary>
public class AssignBeforeStartTests
{
    [Fact]
    public void Noop_attach_throws_when_process_not_started()
    {
        using var supervisor = new NoopSupervisor();
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

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void JobObject_attach_throws_when_process_not_started()
    {
        using var supervisor = new JobObjectSupervisor();
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
    public void Noop_attach_throws_after_dispose()
    {
        var supervisor = new NoopSupervisor();
        supervisor.Dispose();

        var psi = new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        // Even on non-Windows runs we just need a started process; skip the OS-specific path.
        if (!OperatingSystem.IsWindows())
        {
            psi.FileName = "/bin/sh";
            psi.Arguments = "-c \"exit 0\"";
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit();

        Action act = () => supervisor.Attach(p);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Factory_returns_a_supervisor_for_current_platform()
    {
        using var sup = ProcessSupervisor.CreateForCurrentPlatform();
        sup.Should().NotBeNull();
        if (OperatingSystem.IsWindows())
            sup.GetType().Name.Should().Be("JobObjectSupervisor");
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            sup.Should().BeOfType<PosixSignalSupervisor>();
        else
            sup.Should().BeOfType<NoopSupervisor>();
    }
}
