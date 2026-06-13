using System.Runtime.CompilerServices;

namespace InfiniContext.Tests.Supervision;

/// <summary>
/// xUnit <see cref="FactAttribute"/> that auto-skips on Windows runners. Sibling of
/// <see cref="WindowsOnlyFactAttribute"/> and <see cref="MacOnlyFactAttribute"/>. Used by
/// <see cref="InfiniContext.Core.Supervision.Posix.PosixSignalSupervisor"/> tests that only inspect
/// bookkeeping (attach guard, idempotent dispose) and so behave identically on any POSIX kernel —
/// macOS in CI, Linux locally — but cannot run on Windows where the supervisor's constructor throws
/// <see cref="PlatformNotSupportedException"/>.
///
/// <para>
/// Distinct from <see cref="MacOnlyFactAttribute"/>: those tests send real signals / spawn real
/// children and are scoped to <c>macos-latest</c> CI. These run anywhere that is not Windows.
/// Using a skip attribute rather than an early <c>return</c> means the tests report as <em>skipped</em>
/// on Windows instead of falsely passing with no assertion reached.
/// </para>
/// </summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix-only test (Windows lacks POSIX supervisor semantics)";
    }
}
