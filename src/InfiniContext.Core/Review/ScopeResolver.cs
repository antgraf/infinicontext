using System.Text.RegularExpressions;

namespace InfiniContext.Core.Review;

/// <summary>
/// Identifies the file set that defines "the work attributable to a task" тАФ the scope every
/// reviewer agent will receive context excerpts from.
///
/// <para>Resolution order:</para>
/// <list type="number">
/// <item><description>If <c>scopeOverridePaths</c> is non-empty, use it verbatim (the <c>--scope</c> CLI flag wins).</description></item>
/// <item><description>Otherwise <c>git diff &lt;baseCommitSha&gt;..HEAD --name-only</c>, unioned with any paths the task's <c>progress.md</c> lists under a "Files touched" heading.</description></item>
/// <item><description>If <paramref name="baseCommitSha"/> is null, fall back to <c>git merge-base main HEAD</c> and report the fallback in <see cref="ReviewScope.FallbackNote"/>.</description></item>
/// </list>
///
/// <para>
/// Files that don't exist on disk, exceed the byte cap, or live outside the repo root are
/// excluded and listed in <see cref="ReviewScope.ExcludedFiles"/> with the reason.
/// </para>
/// </summary>
public sealed class ScopeResolver
{
    private readonly IGitRunner _git;
    private readonly long _byteCap;

    public ScopeResolver(IGitRunner git, long byteCap = 200 * 1024)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        if (byteCap <= 0) throw new ArgumentOutOfRangeException(nameof(byteCap), "Byte cap must be positive.");
        _byteCap = byteCap;
    }

    /// <summary>
    /// Resolve the review scope for a task.
    /// </summary>
    /// <param name="repoRoot">Working directory тАФ the task's <c>HomeCwd</c>.</param>
    /// <param name="baseCommitSha">Captured at task-creation time. Null when the column was unset or the task pre-dates Migration_002.</param>
    /// <param name="scopeOverridePaths">When non-empty, takes precedence over diff-based resolution. Caller (CLI handler) is responsible for glob expansion.</param>
    /// <param name="progressMdPath">Optional absolute path to the task's <c>progress.md</c>. When supplied, "Files touched" entries union with the diff result.</param>
    public async Task<ReviewScope> ResolveAsync(
        string repoRoot,
        string? baseCommitSha,
        IReadOnlyList<string>? scopeOverridePaths,
        string? progressMdPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var normalizedRoot = Path.GetFullPath(repoRoot);

        // 1. --scope override wins.
        if (scopeOverridePaths is { Count: > 0 })
        {
            var (files, excluded) = await ReadFilesAsync(normalizedRoot, scopeOverridePaths, ct).ConfigureAwait(false);
            return new ReviewScope(
                Description: $"--scope override ({scopeOverridePaths.Count} paths)",
                Files: files,
                ExcludedFiles: excluded,
                FallbackNote: null);
        }

        // 2. Diff-based resolution. Determine the base ref.
        string baseRef;
        string? fallbackNote = null;
        if (!string.IsNullOrWhiteSpace(baseCommitSha))
        {
            baseRef = baseCommitSha;
        }
        else
        {
            var mergeBase = await _git.ResolveMergeBaseAsync(normalizedRoot, "main", "HEAD", ct).ConfigureAwait(false);
            if (mergeBase is null)
            {
                return new ReviewScope(
                    Description: "no diff base resolvable (no base_commit_sha, no merge-base main..HEAD)",
                    Files: [],
                    ExcludedFiles: [],
                    FallbackNote: "Task has no base_commit_sha captured and merge-base main..HEAD could not be resolved. Pass --scope to specify files explicitly.");
            }
            baseRef = mergeBase;
            fallbackNote = $"base_commit_sha was null; fell back to merge-base main..HEAD ({Short(mergeBase)}).";
        }

        var head = await _git.ResolveHeadAsync(normalizedRoot, ct).ConfigureAwait(false) ?? "HEAD";
        var diffPaths = await _git.DiffNameOnlyAsync(normalizedRoot, baseRef, head, ct).ConfigureAwait(false);

        // 3. Union with progress.md "Files touched" entries.
        var progressPaths = progressMdPath is not null && File.Exists(progressMdPath)
            ? await ParseFilesTouchedAsync(progressMdPath, ct).ConfigureAwait(false)
            : Array.Empty<string>();

        var union = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var p in diffPaths.Concat(progressPaths))
        {
            var norm = NormalizeForDedup(p);
            if (seen.Add(norm)) union.Add(p);
        }

        var (resolvedFiles, resolvedExcluded) = await ReadFilesAsync(normalizedRoot, union, ct).ConfigureAwait(false);

        var description = $"git diff {Short(baseRef)}..{Short(head)} (+{progressPaths.Count} from progress.md)";
        return new ReviewScope(
            Description: description,
            Files: resolvedFiles,
            ExcludedFiles: resolvedExcluded,
            FallbackNote: fallbackNote);
    }

    /// <summary>
    /// Parse a markdown <c>progress.md</c> and return every backtick-quoted path that appears
    /// under a heading containing "Files touched" (case-insensitive). Public for tests.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ParseFilesTouchedAsync(string progressMdPath, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(progressMdPath, ct).ConfigureAwait(false);
        return ParseFilesTouched(text);
    }

    /// <summary>Test seam for <see cref="ParseFilesTouchedAsync"/>.</summary>
    public static IReadOnlyList<string> ParseFilesTouched(string markdownText)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(markdownText)) return result;

        var lines = markdownText.Split('\n');
        var inSection = false;
        var sectionLevel = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var hm = HeadingPattern.Match(line);
            if (hm.Success)
            {
                var level = hm.Groups[1].Value.Length;
                var heading = hm.Groups[2].Value;
                if (heading.Contains("files touched", StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    sectionLevel = level;
                    continue;
                }
                // A heading at the section's level or higher (numerically lower or equal) ends the section.
                if (inSection && level <= sectionLevel)
                {
                    inSection = false;
                }
            }

            if (!inSection) continue;

            foreach (Match m in BacktickPath.Matches(line))
            {
                var candidate = m.Groups[1].Value.Trim();
                if (LooksLikePath(candidate)) result.Add(candidate);
            }
        }

        return result;
    }

    private static readonly Regex HeadingPattern = new(@"^(#{1,6})\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex BacktickPath = new(@"`([^`]+)`", RegexOptions.Compiled);

    private static bool LooksLikePath(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.Contains(' ')) return false;
        return s.Contains('/') || s.Contains('\\');
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string NormalizeForDedup(string p) => p.Replace('\\', '/');

    private static string Short(string sha) => sha.Length >= 8 && sha.All(IsHex) ? sha[..8] : sha;

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private async Task<(IReadOnlyList<ReviewFile> Files, IReadOnlyList<ExcludedFile> Excluded)> ReadFilesAsync(
        string repoRoot,
        IEnumerable<string> relativePaths,
        CancellationToken ct)
    {
        var files = new List<ReviewFile>();
        var excluded = new List<ExcludedFile>();

        foreach (var rel in relativePaths)
        {
            ct.ThrowIfCancellationRequested();
            var trimmed = rel.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                excluded.Add(new ExcludedFile(rel, "empty path"));
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(repoRoot, trimmed));
            if (!full.StartsWith(repoRoot, StringComparison.Ordinal))
            {
                excluded.Add(new ExcludedFile(rel, "outside repo root"));
                continue;
            }

            if (!File.Exists(full))
            {
                excluded.Add(new ExcludedFile(rel, "file does not exist"));
                continue;
            }

            var info = new FileInfo(full);
            if (info.Length > _byteCap)
            {
                excluded.Add(new ExcludedFile(rel, $"exceeds byte cap ({info.Length} > {_byteCap})"));
                continue;
            }

            string content;
            try { content = await File.ReadAllTextAsync(full, ct).ConfigureAwait(false); }
            catch (IOException ex) { excluded.Add(new ExcludedFile(rel, $"read failed: {ex.GetType().Name}")); continue; }

            files.Add(new ReviewFile(NormalizeForDedup(trimmed), info.Length, content));
        }

        return (files, excluded);
    }
}

/// <summary>The resolved review scope: a file list plus exclusion bookkeeping.</summary>
public sealed record ReviewScope(
    string Description,
    IReadOnlyList<ReviewFile> Files,
    IReadOnlyList<ExcludedFile> ExcludedFiles,
    string? FallbackNote);

/// <summary>One file in the review scope, with its content already loaded.</summary>
public sealed record ReviewFile(string RelativePath, long ByteCount, string Content);

/// <summary>A file that was named but excluded from the scope, with the reason.</summary>
public sealed record ExcludedFile(string RelativePath, string Reason);
