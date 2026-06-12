using System.Runtime.CompilerServices;

namespace InfiniContext.Tests.Supervision;

/// <summary>
/// xUnit <see cref="TheoryAttribute"/> that auto-skips on non-Windows runners. Sibling of
/// <see cref="WindowsOnlyFactAttribute"/> for parameterised cases — e.g., heuristics that take
/// Windows-shaped path strings as input and would be tripped by Unix's `Path.GetFullPath`
/// prepending the test runner's cwd to anything that doesn't start with `/`.
/// </summary>
public sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    public WindowsOnlyTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only test";
    }
}
