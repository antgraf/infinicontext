namespace InfiniContext.Core.Review;

/// <summary>
/// Thin abstraction over the <c>git</c> CLI. Production code uses <see cref="ProcessGitRunner"/>;
/// tests substitute a fake. Methods return <c>null</c> (or empty) on any failure -- callers handle
/// missing-git / not-a-repo / unreachable-ref as recoverable conditions, not exceptions.
/// </summary>
public interface IGitRunner
{
    /// <summary>Resolve <c>HEAD</c> as a full 40-char SHA. Returns null on any failure.</summary>
    Task<string?> ResolveHeadAsync(string repoRoot, CancellationToken ct);

    /// <summary>
    /// Resolve <c>git merge-base <paramref name="ref1"/> <paramref name="ref2"/></c>. Returns
    /// null when the merge base can't be resolved (refs missing, no common ancestor, etc.).
    /// </summary>
    Task<string?> ResolveMergeBaseAsync(string repoRoot, string ref1, string ref2, CancellationToken ct);

    /// <summary>
    /// Return the list of files changed between <paramref name="baseRef"/> and
    /// <paramref name="headRef"/> (<c>git diff --name-only baseRef..headRef</c>). Empty list on
    /// any failure or when the range yields nothing.
    /// </summary>
    Task<IReadOnlyList<string>> DiffNameOnlyAsync(string repoRoot, string baseRef, string headRef, CancellationToken ct);
}
