using System.Diagnostics;

namespace InfiniContext.Core.Review;

/// <summary>
/// Production <see cref="IGitRunner"/> that shells out to the <c>git</c> binary on PATH. Each
/// method has a 10-second budget; longer than that means git is wedged and we'd rather fail
/// tolerantly than block a review run.
/// </summary>
public sealed class ProcessGitRunner : IGitRunner
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public Task<string?> ResolveHeadAsync(string repoRoot, CancellationToken ct)
        => RunForShaAsync(repoRoot, ["rev-parse", "HEAD"], ct);

    public Task<string?> ResolveMergeBaseAsync(string repoRoot, string ref1, string ref2, CancellationToken ct)
        => RunForShaAsync(repoRoot, ["merge-base", ref1, ref2], ct);

    public async Task<IReadOnlyList<string>> DiffNameOnlyAsync(string repoRoot, string baseRef, string headRef, CancellationToken ct)
    {
        var stdout = await RunAsync(repoRoot, ["diff", "--name-only", $"{baseRef}..{headRef}"], ct).ConfigureAwait(false);
        if (stdout is null) return Array.Empty<string>();
        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
    }

    private static async Task<string?> RunForShaAsync(string repoRoot, IReadOnlyList<string> argv, CancellationToken ct)
    {
        var stdout = await RunAsync(repoRoot, argv, ct).ConfigureAwait(false);
        if (stdout is null) return null;
        var sha = stdout.Trim();
        return Is40Hex(sha) ? sha : null;
    }

    private static async Task<string?> RunAsync(string repoRoot, IReadOnlyList<string> argv, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in argv) psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Budget);

            string stdout;
            try { stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: false); } catch { } return null; }

            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return proc.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool Is40Hex(string s)
    {
        if (s.Length != 40) return false;
        foreach (var c in s)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        return true;
    }
}
