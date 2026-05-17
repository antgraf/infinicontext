using System.Diagnostics;

namespace InfiniContext.Core.Tasks;

/// <summary>
/// Best-effort capture of the current git HEAD SHA for a working directory. Used at task
/// creation to seed <see cref="TaskRecord.BaseCommitSha"/>, which the <c>/task review</c>
/// command uses as the base of the diff range that defines "the work attributable to this task".
///
/// <para>
/// "Best-effort" means: shelling out is allowed to fail (no git on PATH, cwd not in a repo,
/// detached/empty repo) and the caller treats the result as a tolerant <c>null</c> rather than
/// raising. Review falls back to <c>git merge-base main HEAD</c> when the column is null.
/// </para>
/// </summary>
public static class GitHead
{
    /// <summary>
    /// Resolve <c>HEAD</c> as a full 40-char SHA in <paramref name="workingDirectory"/>. Returns
    /// <c>null</c> on any failure (git missing, not a repo, empty HEAD, non-zero exit, timeout).
    /// </summary>
    public static async Task<string?> TryResolveAsync(string workingDirectory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return null;
        if (!Directory.Exists(workingDirectory)) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("HEAD");

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            // Five-second budget тАФ rev-parse is fast; if it isn't, git is wedged and we want to
            // fail tolerantly rather than block task creation.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            string stdout;
            try { stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: false); } catch { } return null; }

            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            if (proc.ExitCode != 0) return null;

            var sha = stdout.Trim();
            // Sanity-check: a real SHA is exactly 40 hex chars.
            if (sha.Length != 40) return null;
            foreach (var c in sha)
            {
                var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex) return null;
            }
            return sha;
        }
        catch
        {
            return null;
        }
    }
}
