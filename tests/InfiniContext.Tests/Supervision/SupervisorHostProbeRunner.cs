namespace InfiniContext.Tests.Supervision;

/// <summary>
/// Resolves the <c>SupervisorHostProbe</c> apphost built alongside this test project (a
/// <c>&lt;ProjectReference ReferenceOutputAssembly="false" /&gt;</c> in the test csproj forces the
/// build). Mirrors <see cref="FakeClaudeRunner"/>. Used by the POSIX host-lifecycle smoke in
/// <see cref="PosixSignalSupervisorTests"/>; only ever invoked on macOS/Linux.
/// </summary>
internal static class SupervisorHostProbeRunner
{
    private static readonly Lazy<string> _executable = new(Locate);

    public static string ExecutablePath => _executable.Value;

    private static string Locate()
    {
        var probeBase = Path.Combine(TestPaths.RepoRoot, "tests", "SupervisorHostProbe", "bin");
        if (!Directory.Exists(probeBase))
            throw new InvalidOperationException(
                $"SupervisorHostProbe has not been built (expected output under {probeBase}). " +
                "Ensure the test project's ProjectReference triggers a build of SupervisorHostProbe.csproj.");

        var exeName = OperatingSystem.IsWindows() ? "SupervisorHostProbe.exe" : "SupervisorHostProbe";

        var matches = Directory.EnumerateFiles(probeBase, exeName, SearchOption.AllDirectories).ToList();
        if (matches.Count == 0)
            throw new InvalidOperationException(
                $"SupervisorHostProbe executable '{exeName}' not found under {probeBase}.");

        // Prefer the most recently modified — the latest build, matching this run's configuration.
        return matches.OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).First();
    }
}
