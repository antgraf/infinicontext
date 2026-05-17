namespace InfiniContext.Core.Review;

/// <summary>
/// Abstracts the post-auto-apply build and test gate that <see cref="AutoApplyExecutor"/>
/// runs after editing files on disk. Production wires <c>dotnet build</c> + <c>dotnet test</c>;
/// tests substitute a fake with canned outcomes so they don't actually shell out.
/// </summary>
public interface IBuildTestRunner
{
    /// <summary>Run the build gate (typically <c>dotnet build -c Release</c>) in <paramref name="repoRoot"/>.</summary>
    Task<BuildTestOutcome> BuildAsync(string repoRoot, CancellationToken ct);

    /// <summary>Run the test gate (typically <c>dotnet test -c Release --no-build</c>) in <paramref name="repoRoot"/>.</summary>
    Task<BuildTestOutcome> TestAsync(string repoRoot, CancellationToken ct);
}

/// <summary>Outcome of one build or test invocation.</summary>
/// <param name="Passed">True if the gate succeeded.</param>
/// <param name="FailureSummary">Short summary suitable for embedding in a report note. Null when <paramref name="Passed"/>.</param>
public sealed record BuildTestOutcome(bool Passed, string? FailureSummary);
