using Microsoft.Extensions.FileSystemGlobbing;

namespace InfiniContext.Core.Tasks;

/// <summary>
/// Expands the raw <c>--scope</c> CLI value (e.g. <c>"src/**/*.cs;tests/Foo.cs"</c>) into a flat
/// list of repo-relative paths.
///
/// <para>
/// Tokens are <c>;</c>-separated. A token containing <c>*</c> goes through <see cref="Matcher"/>
/// (which understands <c>*</c> and <c>**</c>); matches are returned with forward-slash separators
/// (matching <c>ScopeResolver</c>'s normalisation). Tokens with no wildcards are returned as-is
/// so the v1 literal-path behaviour is preserved (and <c>ScopeResolver</c> can surface
/// "file does not exist" for typos). <c>?</c> is not supported by the underlying matcher and is
/// treated as a literal character.
/// </para>
///
/// <para>
/// Case sensitivity mirrors the host OS: <see cref="StringComparison.OrdinalIgnoreCase"/> on
/// Windows, <see cref="StringComparison.Ordinal"/> elsewhere. This matches what users expect
/// on each platform and aligns with <c>ScopeResolver.PathComparer</c>.
/// </para>
/// </summary>
public static class ScopeGlobExpander
{
    public static IReadOnlyList<string> Expand(string? scopeArg, string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(scopeArg)) return Array.Empty<string>();
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        var tokens = scopeArg.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return Array.Empty<string>();

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var seen = new HashSet<string>(comparer);
        var result = new List<string>();

        foreach (var token in tokens)
        {
            if (!HasWildcard(token))
            {
                var normalised = token.Replace('\\', '/');
                if (seen.Add(normalised)) result.Add(normalised);
                continue;
            }

            // One Matcher per token keeps results scoped -- different tokens may have unrelated
            // includes; we don't want them to bleed into each other's match set.
            var matcher = new Matcher(comparison);
            matcher.AddInclude(token.Replace('\\', '/'));
            foreach (var full in matcher.GetResultsInFullPath(repoRoot))
            {
                var rel = Path.GetRelativePath(repoRoot, full).Replace('\\', '/');
                if (seen.Add(rel)) result.Add(rel);
            }
        }

        return result;
    }

    private static bool HasWildcard(string token) =>
        token.Contains('*');
}
