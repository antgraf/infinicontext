using InfiniContext.Core.Supervision.Posix;
using InfiniContext.Core.Supervision.Stub;
using InfiniContext.Core.Supervision.Windows;
using Serilog;

namespace InfiniContext.Core.Supervision;

/// <summary>
/// Picks the right <see cref="IProcessSupervisor"/> for the current OS:
/// <see cref="JobObjectSupervisor"/> on Windows, <see cref="PosixSignalSupervisor"/> on
/// macOS/Linux, <see cref="NoopSupervisor"/> on anything else (e.g. <c>browser</c>). Hosts call
/// this once at startup and inject the result into every <c>ClaudeSession</c>.
/// </summary>
public static class ProcessSupervisor
{
    public static IProcessSupervisor CreateForCurrentPlatform(ILogger? logger = null)
    {
        if (OperatingSystem.IsWindows()) return new JobObjectSupervisor(logger);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) return new PosixSignalSupervisor(logger);
        return new NoopSupervisor(logger);
    }
}
