namespace InfiniContext.Tests.Supervision;

/// <summary>
/// xUnit <see cref="FactAttribute"/> that auto-skips on non-Windows runners. Used by tests that
/// exercise <see cref="InfiniContext.Core.Supervision.Windows.JobObjectSupervisor"/> P/Invokes,
/// which are Windows-only by definition. Stage 2 will add a sibling for the macOS backend.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only test";
    }
}
