using System.Diagnostics;
using System.Text;
using InfiniContext.Core.Supervision;

namespace InfiniContext.Core.Review;

/// <summary>
/// Production <see cref="IBuildTestRunner"/>. Shells out to <c>dotnet build</c> and
/// <c>dotnet test</c> in the repo root and captures the last N lines of stderr (falling back
/// to the last N lines of stdout when stderr is empty) for the failure summary.
///
/// <para>
/// Solution path and configuration are configurable so the runner doesn't have to assume the
/// project's solution name. Defaults match the InfiniContext repo: <c>InfiniContext.slnx</c>,
/// <c>Release</c>. Per-invocation timeouts default to 10 minutes for build and 15 for test --
/// long, because the alternative on a slow CI host is a false "gate failed" demotion that
/// reverts good edits.
/// </para>
/// </summary>
public sealed class ProcessBuildTestRunner : IBuildTestRunner
{
    private readonly ProcessBuildTestRunnerOptions _options;
    private readonly IProcessSupervisor? _supervisor;

    public ProcessBuildTestRunner(ProcessBuildTestRunnerOptions? options = null, IProcessSupervisor? supervisor = null)
    {
        _options = options ?? new ProcessBuildTestRunnerOptions();
        _supervisor = supervisor;
    }

    public Task<BuildTestOutcome> BuildAsync(string repoRoot, CancellationToken ct)
    {
        var args = new List<string> { "build", _options.Solution, "-c", _options.Configuration };
        if (_options.ExtraBuildArgs is { } extra) args.AddRange(extra);
        return RunAsync(repoRoot, args, _options.BuildTimeoutSeconds, ct);
    }

    public Task<BuildTestOutcome> TestAsync(string repoRoot, CancellationToken ct)
    {
        var args = new List<string> { "test", _options.Solution, "-c", _options.Configuration, "--no-build", "--nologo" };
        if (_options.ExtraTestArgs is { } extra) args.AddRange(extra);
        return RunAsync(repoRoot, args, _options.TestTimeoutSeconds, ct);
    }

    private async Task<BuildTestOutcome> RunAsync(
        string repoRoot,
        IReadOnlyList<string> argv,
        int timeoutSeconds,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null)
            return new BuildTestOutcome(Passed: false, FailureSummary: "Process.Start returned null for `dotnet`");

        // arch-003/004: attach to the host's IProcessSupervisor so dotnet build/test (and its
        // msbuild / vstest descendants) die with the host on Windows (Job Object KILL_ON_JOB_CLOSE)
        // and POSIX (SIGTERM cascade). Without this, killing the host mid-review leaks descendants.
        // Attach failures are tolerated: a missing supervisor is a no-op in tests and a degraded
        // (but still correct-for-this-run) state in production.
        if (_supervisor is not null)
        {
            try { _supervisor.Attach(proc); }
            catch { /* best-effort; the host's own supervision still applies on process exit */ }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Timeout. Tree-kill: `dotnet build`/`test` spawn MSBuild workers and testhost that
            // hold bin/obj locks; killing only the top-level dotnet leaves them running and makes
            // a subsequent revert/baseline gate fail spuriously mid-auto-apply (M24). D12's
            // tree-kill prohibition applies only to the light-model child (no descendants by
            // construction); this is the opposite case.
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new BuildTestOutcome(Passed: false, FailureSummary: $"timed out after {timeoutSeconds}s");
        }

        if (proc.ExitCode == 0)
            return new BuildTestOutcome(Passed: true, FailureSummary: null);

        var stderrText = (stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "") ?? "";
        var stdoutText = (stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "") ?? "";
        var summarySource = !string.IsNullOrWhiteSpace(stderrText) ? stderrText : stdoutText;
        return new BuildTestOutcome(
            Passed: false,
            FailureSummary: BuildFailureSummary(proc.ExitCode, summarySource));
    }

    private static string BuildFailureSummary(int exitCode, string output)
    {
        const int maxLines = 20;
        const int maxChars = 2_000;

        if (string.IsNullOrWhiteSpace(output))
            return $"exit code {exitCode}; no output captured";

        var lines = output.Replace("\r\n", "\n").Split('\n');
        var tail = lines.Length > maxLines ? lines[(lines.Length - maxLines)..] : lines;
        var sb = new StringBuilder();
        sb.Append("exit code ").Append(exitCode).AppendLine("; tail:");
        foreach (var l in tail)
        {
            if (string.IsNullOrWhiteSpace(l)) continue;
            sb.AppendLine(l.TrimEnd());
            if (sb.Length > maxChars) break;
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Configuration for <see cref="ProcessBuildTestRunner"/>. All fields have sensible defaults.</summary>
public sealed record ProcessBuildTestRunnerOptions
{
    /// <summary>Solution or project to build / test. Defaults to <c>InfiniContext.slnx</c>.</summary>
    public string Solution { get; init; } = "InfiniContext.slnx";

    /// <summary><c>dotnet build -c &lt;Configuration&gt;</c>. Defaults to <c>Release</c>.</summary>
    public string Configuration { get; init; } = "Release";

    /// <summary>Per-invocation budget for <c>dotnet build</c>. Default: 10 minutes.</summary>
    public int BuildTimeoutSeconds { get; init; } = 600;

    /// <summary>Per-invocation budget for <c>dotnet test</c>. Default: 15 minutes.</summary>
    public int TestTimeoutSeconds { get; init; } = 900;

    /// <summary>Extra args appended to the default build command line. Null = none.</summary>
    public IReadOnlyList<string>? ExtraBuildArgs { get; init; }

    /// <summary>Extra args appended to the default test command line. Null = none.</summary>
    public IReadOnlyList<string>? ExtraTestArgs { get; init; }
}
