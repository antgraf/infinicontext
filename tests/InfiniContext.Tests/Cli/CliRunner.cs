namespace InfiniContext.Tests.Cli;

/// <summary>
/// Resolves the InfiniContext.Cli apphost binary built alongside this test project. Mirrors
/// <see cref="FakeClaudeRunner"/>: the test csproj's <c>ProjectReference ReferenceOutputAssembly="false"</c>
/// triggers the CLI build, and this helper locates the resulting executable on disk.
/// </summary>
internal static class CliRunner
{
    private static readonly Lazy<string> _executable = new(Locate);

    public static string ExecutablePath => _executable.Value;

    private static string Locate()
    {
        var cliBase = Path.Combine(TestPaths.RepoRoot, "src", "InfiniContext.Cli", "bin");
        if (!Directory.Exists(cliBase))
            throw new InvalidOperationException(
                $"InfiniContext.Cli has not been built (expected output under {cliBase}). " +
                "Ensure the test project's ProjectReference triggers a build of InfiniContext.Cli.csproj.");

        var exeName = OperatingSystem.IsWindows() ? "InfiniContext.Cli.exe" : "InfiniContext.Cli";

        var matches = Directory.EnumerateFiles(cliBase, exeName, SearchOption.AllDirectories).ToList();
        if (matches.Count == 0)
            throw new InvalidOperationException(
                $"InfiniContext.Cli executable '{exeName}' not found under {cliBase}.");

        return matches.OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).First();
    }
}
