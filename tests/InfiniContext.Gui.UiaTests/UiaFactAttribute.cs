using System.Runtime.CompilerServices;

namespace InfiniContext.Gui.UiaTests;

/// <summary>
/// Gate for UIA end-to-end tests (design.md section 6.2): they launch the real GUI on a desktop
/// and steal mouse/keyboard focus, so they must never run from a casual
/// <c>dotnet test InfiniContext.slnx</c> or in ci.yml. Opt in with <c>ICX_UIA_TESTS=1</c>
/// (ci-uia.yml sets it; see also ICX_UIA_CONFIGURATION for the build flavour). Follows the
/// repo's ctor-Skip convention (WindowsOnlyFactAttribute) including the xUnit3003
/// CallerFilePath/CallerLineNumber pass-through.
/// </summary>
public sealed class UiaFactAttribute : FactAttribute
{
    public UiaFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "UIA tests are Windows-only (UIA is Windows)";
        }
        else if (Environment.GetEnvironmentVariable("ICX_UIA_TESTS") != "1")
        {
            Skip = "Set ICX_UIA_TESTS=1 to run UIA end-to-end tests (they take over the desktop)";
        }
    }
}
