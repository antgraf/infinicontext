namespace InfiniContext.Tests.Supervision;

/// <summary>
/// xUnit <see cref="FactAttribute"/> that auto-skips on non-macOS runners. Sibling of
/// <see cref="WindowsOnlyFactAttribute"/>. Used by tests that exercise
/// <see cref="InfiniContext.Core.Supervision.Posix.PosixSignalSupervisor"/> against real signals or
/// real child processes — those paths only behave sanely on a POSIX kernel, and CI runs them on
/// <c>macos-latest</c>.
///
/// <para>
/// Linux runners are not part of the design-doc §2 target platforms, so a separate
/// <c>LinuxOnlyFact</c> attribute is not provided. If a contributor wants to validate locally on
/// Linux they can swap in <c>OperatingSystem.IsLinux()</c> temporarily.
/// </para>
/// </summary>
public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "macOS-only test";
    }
}
