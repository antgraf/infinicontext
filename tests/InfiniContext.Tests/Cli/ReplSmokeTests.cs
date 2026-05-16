using System.Diagnostics;
using System.Text;
using FluentAssertions;

namespace InfiniContext.Tests.Cli;

/// <summary>
/// Stage 1G smoke tests: spawn the real InfiniContext.Cli apphost against a FakeClaude child,
/// pipe a prompt + commands to stdin, and assert that stdout shows the streamed assistant
/// text and the expected state markers. These tests exercise the entire REPL pipeline —
/// argv parsing, lifecycle, renderer, slash commands, and exit summary — end-to-end.
/// </summary>
public class ReplSmokeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Repl_renders_assistant_text_and_state_markers_against_replay_fixture()
    {
        var stdinScript = "Tell me about Unix\n/quit\n";
        var result = await RunCliAsync(
            new[]
            {
                "run", TestPaths.RepoRoot,
                "--claude-exe", FakeClaudeRunner.ExecutablePath,
                "--", "--replay", TestPaths.Fixture("simple-prompt"),
            },
            stdinScript);

        result.ExitCode.Should().Be(0, "REPL should exit cleanly. stderr was: {0}", result.Stderr);

        // Assistant text streamed from the simple-prompt fixture.
        result.Stdout.Should().Contain("Unix originated in 1969",
            "the simple-prompt fixture's assistant text must be rendered");

        // Both [state: ...] markers — entering the turn and leaving it — must show up.
        result.Stdout.Should().Contain("[state: Idle -> Sending]");
        result.Stdout.Should().Contain("[state: Sending -> Streaming]");
        result.Stdout.Should().Contain("[state: Streaming -> Idle]");

        // result event renders a turn-done marker including the cost.
        result.Stdout.Should().Contain("[turn done:");

        // Exit summary includes the captured session id and a copy-paste resume hint.
        result.Stdout.Should().Contain("[done] session=");
        result.Stdout.Should().Contain("resume: infinicontext run --resume");
    }

    [Fact]
    public async Task Repl_state_command_reports_session_state_snapshot()
    {
        // Send /state immediately, before any send. Session is at Idle, no turns yet.
        var stdinScript = "/state\n/quit\n";
        var result = await RunCliAsync(
            new[]
            {
                "run", TestPaths.RepoRoot,
                "--claude-exe", FakeClaudeRunner.ExecutablePath,
                "--", "--replay", TestPaths.Fixture("simple-prompt"),
            },
            stdinScript);

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("[state] Idle | open_tools=0");
    }

    [Fact]
    public async Task Repl_rejects_unknown_options_with_exit_code_2()
    {
        var result = await RunCliAsync(
            new[] { "run", "--bogus-flag" },
            stdinScript: "");

        result.ExitCode.Should().Be(2);
        result.Stderr.Should().Contain("unknown option --bogus-flag");
    }

    [Fact]
    public async Task Repl_prints_usage_for_help_flag()
    {
        var result = await RunCliAsync(new[] { "--help" }, stdinScript: "");

        result.ExitCode.Should().Be(0);
        // New usage banner makes <cwd> and options the primary form (no implied-mandatory `run`).
        result.Stdout.Should().Contain("Usage: infinicontext [<cwd>]");
        // The catalog drives the rendered output, so every flag must show up.
        result.Stdout.Should().Contain("--resume");
        result.Stdout.Should().Contain("--approval-mode");
        // Banner now points at the in-REPL help rather than reproducing a stale list.
        result.Stdout.Should().Contain("/help inside the REPL");
    }

    [Fact]
    public async Task Repl_runs_with_no_args_using_defaults()
    {
        // No args: defaults to `run` with cwd=current dir. We still need to point at FakeClaude
        // so the test doesn't try to spawn real claude. Pass --claude-exe and -- before --replay.
        var stdinScript = "Tell me about Unix\n/quit\n";
        var result = await RunCliAsync(
            new[]
            {
                "--claude-exe", FakeClaudeRunner.ExecutablePath,
                "--", "--replay", TestPaths.Fixture("simple-prompt"),
            },
            stdinScript);

        result.ExitCode.Should().Be(0, "no-args invocation should boot the REPL. stderr was: {0}", result.Stderr);
        result.Stdout.Should().Contain("Unix originated in 1969");
        result.Stdout.Should().Contain("[state: Idle -> Sending]");
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    private static async Task<CliResult> RunCliAsync(IReadOnlyList<string> argv, string stdinScript)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CliRunner.ExecutablePath,
            WorkingDirectory = TestPaths.RepoRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        // Redirect the task registry to in-memory so smoke tests don't touch ~/.infinicontext/.
        psi.Environment["INFINICONTEXT__TASKS__DBPATH"] = ":memory:";
        foreach (var a in argv) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            throw new InvalidOperationException("Failed to start CLI process.");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!string.IsNullOrEmpty(stdinScript))
        {
            await proc.StandardInput.WriteAsync(stdinScript).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        proc.StandardInput.Close();

        using var cts = new CancellationTokenSource(TestTimeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"CLI did not exit within {TestTimeout}. Stdout so far: {await stdoutTask}");
        }

        return new CliResult(proc.ExitCode, await stdoutTask, await stderrTask);
    }
}
