namespace InfiniContext.Tests;

/// <summary>
/// Resolves the FakeClaude apphost binary built alongside this test project. We rely on the
/// <c>&lt;ProjectReference ReferenceOutputAssembly="false" /&gt;</c> in the test csproj to ensure
/// FakeClaude is built before tests run; this helper finds the resulting executable on disk.
/// </summary>
internal static class FakeClaudeRunner
{
    private static readonly Lazy<string> _executable = new(Locate);

    public static string ExecutablePath => _executable.Value;

    private static string Locate()
    {
        var fakeBase = Path.Combine(TestPaths.RepoRoot, "tests", "FakeClaude", "bin");
        if (!Directory.Exists(fakeBase))
            throw new InvalidOperationException(
                $"FakeClaude has not been built (expected output under {fakeBase}). " +
                "Ensure the test project's ProjectReference triggers a build of FakeClaude.csproj.");

        var exeName = OperatingSystem.IsWindows() ? "FakeClaude.exe" : "FakeClaude";

        // Prefer a binary matching this test process's configuration; fall back to any.
        var matches = Directory.EnumerateFiles(fakeBase, exeName, SearchOption.AllDirectories).ToList();
        if (matches.Count == 0)
            throw new InvalidOperationException(
                $"FakeClaude executable '{exeName}' not found under {fakeBase}.");

        // Heuristic: prefer the most recently modified, which will be the latest build.
        return matches.OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).First();
    }
}
