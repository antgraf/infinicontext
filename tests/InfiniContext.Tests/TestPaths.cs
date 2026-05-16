namespace InfiniContext.Tests;

internal static class TestPaths
{
    private static readonly Lazy<string> _repoRoot = new(FindRepoRoot);

    public static string RepoRoot => _repoRoot.Value;
    public static string FixturesDir => Path.Combine(RepoRoot, "tests", "fixtures");

    public static string Fixture(string scenarioWithoutExt)
        => Path.Combine(FixturesDir, scenarioWithoutExt + ".jsonl");

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "InfiniContext.slnx")) ||
                File.Exists(Path.Combine(dir, "InfiniContext.sln")))
            {
                return dir;
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent!;
        }
        throw new InvalidOperationException(
            $"Could not locate InfiniContext.slnx walking up from {AppContext.BaseDirectory}.");
    }
}
