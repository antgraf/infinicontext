using InfiniContext.Core.Logging;
using InfiniContext.Core.Sessions;
using FluentAssertions;

namespace InfiniContext.Tests.Logging;

/// <summary>
/// End-to-end logging tests: spawn a real <see cref="ClaudeSession"/> against the FakeClaude binary
/// (which streams a fixture in lieu of a real <c>claude</c>), drive it with one prompt, then
/// assert the per-run log file matches the design doc §7.4 contract.
/// </summary>
public class IntegrationTests
{
    [Fact]
    public async Task Replay_unknown_event_fixture_writes_expected_log_lines()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "infinicontext-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDir);

        try
        {
            var fixture = TestPaths.Fixture("unknown-event");
            var fakeExe = FakeClaudeRunner.ExecutablePath;

            var options = new ClaudeSessionOptions
            {
                Cwd = TestPaths.RepoRoot,
                ClaudeExecutable = fakeExe,
                ExtraArgs = new[] { "--replay", fixture, "--delay-ms", "5" },
            };

            string logPath;
            await using (var logging = InfiniContextLogger.ConfigureFromHostStartup(logDirectory: logDir))
            {
                logPath = logging.LogFilePath;
                await using var session = new ClaudeSession(options, logging.Logger);

                await session.StartAsync(TestContext.Current.CancellationToken);
                await session.SendUserMessageAsync("hi", TestContext.Current.CancellationToken);

                // Drain channel; this also gives FakeClaude time to finish streaming.
                while (await session.Events.WaitToReadAsync(TestContext.Current.CancellationToken))
                    while (session.Events.TryRead(out _)) { }

                await session.WaitForExitAsync(TestContext.Current.CancellationToken);
            }

            // The Serilog file sink flushes on dispose; read with shared access in case OS still has handle.
            var contents = ReadSharedAllText(logPath);

            contents.Should().Contain("spawn:", "argv must be logged at startup");
            contents.Should().Contain("started pid=");
            contents.Should().Contain("state Idle -> Sending");
            contents.Should().Contain("state Sending -> Streaming");
            contents.Should().Contain("tx:", "user message must be logged at Debug");
            contents.Should().Contain("rx:", "received events must be logged at Debug");
            contents.Should().Contain("unknown_event type=", "unknown-event fixture has one top-level UnknownEvent");
            contents.Should().Contain("unknown_block type=", "unknown-event fixture has one UnknownBlock");
            contents.Should().Contain("process exited code=");

            // Scrubber sanity: log path should not contain raw API key strings even if a future change
            // accidentally logs them — assert by confirming the pattern is never present.
            contents.Should().NotContain("ANTHROPIC_API_KEY=sk-");
        }
        finally
        {
            try { Directory.Delete(logDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string ReadSharedAllText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }
}
