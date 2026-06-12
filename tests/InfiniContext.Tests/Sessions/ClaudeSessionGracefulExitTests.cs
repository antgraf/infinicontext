using System.Text;
using System.Text.Json.Nodes;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;
using FluentAssertions;

namespace InfiniContext.Tests.Sessions;

/// <summary>
/// Stage 3 bug A. Exercises <see cref="ClaudeSession.RequestGracefulExitAsync"/> against a real
/// FakeClaude child: closing stdin while the child is blocked on a read must let it exit naturally
/// (EOF в†’ exit code 0), driving the standard <c>Exited</c> transition вЂ” the same path the agent's
/// <c>session_exit</c> tool reaches through the sidecar. Distinct from DisposeAsync, which kills.
/// </summary>
public class ClaudeSessionGracefulExitTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RequestGracefulExitAsync_closes_stdin_and_child_exits_zero()
    {
        var sessionId = "00000000-0000-0000-0000-000000000eee";

        // init, then block on stdin. Closing stdin (graceful exit) yields EOF, which FakeClaude's
        // wait-stdin reads as null and returns 0 вЂ” a clean exit, NOT a kill.
        var script = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject { ["type"] = "emit-line", ["json"] = new JsonObject
                {
                    ["type"] = "system", ["subtype"] = "init",
                    ["session_id"] = sessionId, ["model"] = "fake", ["cwd"] = "/tmp",
                    ["claude_code_version"] = "0.0.0",
                } },
                new JsonObject { ["type"] = "wait-stdin", ["lines"] = 1 },
                new JsonObject { ["type"] = "exit", ["code"] = 0 },
            },
        };

        var scriptPath = Path.Combine(Path.GetTempPath(), $"fakeclaude-graceful-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(scriptPath, script.ToJsonString(), Encoding.UTF8, TestContext.Current.CancellationToken);

        try
        {
            await using var session = new ClaudeSession(new ClaudeSessionOptions
            {
                Cwd = TestPaths.RepoRoot,
                ClaudeExecutable = FakeClaudeRunner.ExecutablePath,
                ExtraArgs = new[] { "--script", scriptPath },
            });

            var transitions = new List<StateTransition>();
            session.State.Transitioned += t => transitions.Add(t);

            await session.StartAsync(TestContext.Current.CancellationToken);

            // Wait for init so we know the child is up and at its wait-stdin before we close stdin.
            using (var initCts = new CancellationTokenSource(TestTimeout))
            {
                while (session.SessionId is null && await session.Events.WaitToReadAsync(initCts.Token))
                    while (session.Events.TryRead(out _)) { /* drain */ }
            }
            session.SessionId.Should().NotBeNull("system/init must have been parsed before graceful exit");

            await session.RequestGracefulExitAsync(TestContext.Current.CancellationToken);

            // Drain the rest until the channel completes on process exit.
            using (var cts = new CancellationTokenSource(TestTimeout))
            {
                try
                {
                    while (await session.Events.WaitToReadAsync(cts.Token))
                        while (session.Events.TryRead(out _)) { /* drain */ }
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"Session never completed after graceful exit. State: {session.State.State}");
                }
            }

            await session.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (session.State.State != SessionState.Exited && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);

            session.State.State.Should().Be(SessionState.Exited);
            var exitReason = transitions.LastOrDefault(t => t.To == SessionState.Exited)?.Reason;
            exitReason.Should().Contain("exitCode=0",
                "closing stdin must let the child exit naturally (0), not be killed");

            // Idempotent: a second call after the child has exited is a harmless no-op.
            await session.RequestGracefulExitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best effort */ }
        }
    }
}
