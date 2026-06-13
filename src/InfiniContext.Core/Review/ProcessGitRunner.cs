using System.Diagnostics;
using InfiniContext.Core.Supervision;

namespace InfiniContext.Core.Review;

/// <summary>
/// Production <see cref="IGitRunner"/> that shells out to the <c>git</c> binary on PATH. Each
/// method has a 10-second budget; longer than that means git is wedged and we'd rather fail
/// tolerantly than block a review run.
/// </summary>
public sealed class ProcessGitRunner : IGitRunner
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly IProcessSupervisor? _supervisor;

    public ProcessGitRunner(IProcessSupervisor? supervisor = null) => _supervisor = supervisor;

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

    private async Task<string?> RunForShaAsync(string repoRoot, IReadOnlyList<string> argv, CancellationToken ct)
    {
        var stdout = await RunAsync(repoRoot, argv, ct).ConfigureAwait(false);
        if (stdout is null) return null;
        var sha = stdout.Trim();
        return Is40Hex(sha) ? sha : null;
    }

    private async Task<string?> RunAsync(string repoRoot, IReadOnlyList<string> argv, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return null;

        Process? proc = null;
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

            proc = Process.Start(psi);
            if (proc is null) return null;

            // Attach to the host supervisor so a wedged git (e.g. a blocked credential helper)
            // dies with the host instead of being orphaned past exit on POSIX (M23).
            if (_supervisor is not null)
            {
                try { _supervisor.Attach(proc); }
                catch { /* best-effort */ }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Budget);

            // Drain BOTH streams concurrently: stderr is redirected, and a chatty git that fills
            // the stderr pipe buffer (~64 KB) while we only read stdout would block on write and
            // deadlock WaitForExitAsync (M23).
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            return proc.ExitCode == 0 ? stdoutTask.Result : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            // Kill the (possibly wedged) git and its descendants on every exit path — timeout,
            // host-token cancellation, or an unexpected throw — so nothing is left running.
            if (proc is not null)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                proc.Dispose();
            }
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
