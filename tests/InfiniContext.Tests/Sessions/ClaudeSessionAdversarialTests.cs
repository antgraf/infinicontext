using InfiniContext.Core.Sessions;
using FluentAssertions;

namespace InfiniContext.Tests.Sessions;

/// <summary>
/// Adversarial-path tests for <see cref="ClaudeSession"/>: invalid lifecycle ordering must throw
/// with clear messages rather than NRE, hang, or silently corrupt state. Complements the
/// happy-path coverage in <see cref="FakeClaudeIntegrationTests"/>.
/// </summary>
public class ClaudeSessionAdversarialTests
{
    [Fact]
    public async Task Send_before_StartAsync_throws_InvalidOperationException()
    {
        await using var session = new ClaudeSession(new ClaudeSessionOptions
        {
            Cwd = TestPaths.RepoRoot,
            ClaudeExecutable = FakeClaudeRunner.ExecutablePath,
        });

        var act = async () => await session.SendUserMessageAsync("hi");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not been started*");
    }

    [Fact]
    public async Task Interrupt_before_StartAsync_throws_InvalidOperationException()
    {
        await using var session = new ClaudeSession(new ClaudeSessionOptions
        {
            Cwd = TestPaths.RepoRoot,
            ClaudeExecutable = FakeClaudeRunner.ExecutablePath,
        });

        var act = async () => await session.InterruptAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not been started*");
    }

    [Fact]
    public async Task StartAsync_called_twice_throws_on_the_second_call()
    {
        await using var session = new ClaudeSession(new ClaudeSessionOptions
        {
            Cwd = TestPaths.RepoRoot,
            ClaudeExecutable = FakeClaudeRunner.ExecutablePath,
            ExtraArgs = new[] { "--replay", TestPaths.Fixture("simple-prompt") },
        });

        await session.StartAsync();
        var act = async () => await session.StartAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already started*");
    }

    [Fact]
    public async Task StartAsync_with_invalid_executable_throws_and_leaves_session_disposable()
    {
        // Write a real file at <override> that exists (so ResolveExecutable doesn't fall back to
        // PATH) but is NOT a valid executable. Process.Start surfaces this as Win32Exception on
        // Windows / similar on non-Windows. The session must then DisposeAsync cleanly.
        var bogusExe = Path.Combine(
            Path.GetTempPath(),
            $"infinicontext-not-an-exe-{Guid.NewGuid():N}" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        await File.WriteAllTextAsync(bogusExe, "this is plain text, not an executable\n");

        try
        {
            await using var session = new ClaudeSession(new ClaudeSessionOptions
            {
                Cwd = TestPaths.RepoRoot,
                ClaudeExecutable = bogusExe,
            });

            var act = async () => await session.StartAsync();

            // Either Win32Exception (Windows: "is not a valid Win32 application") or generic
            // exception subtype on Linux is acceptable; just want a clean throw without
            // orphaning state.
            await act.Should().ThrowAsync<Exception>();
        }
        finally
        {
            try { File.Delete(bogusExe); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task DisposeAsync_called_twice_is_idempotent()
    {
        await using var session = new ClaudeSession(new ClaudeSessionOptions
        {
            Cwd = TestPaths.RepoRoot,
            ClaudeExecutable = FakeClaudeRunner.ExecutablePath,
            ExtraArgs = new[] { "--replay", TestPaths.Fixture("simple-prompt") },
        });

        await session.StartAsync();
        // FakeClaude --replay waits for one stdin line before streaming the fixture and exiting.
        await session.SendUserMessageAsync("hi");
        await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await session.DisposeAsync();
        var act = async () => await session.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Send_after_process_exit_throws_InvalidOperationException()
    {
        await using var session = new ClaudeSession(new ClaudeSessionOptions
        {
            Cwd = TestPaths.RepoRoot,
            ClaudeExecutable = FakeClaudeRunner.ExecutablePath,
            ExtraArgs = new[] { "--replay", TestPaths.Fixture("simple-prompt") },
        });

        await session.StartAsync();
        // Drive a single message so FakeClaude actually runs the replay-and-exit path.
        await session.SendUserMessageAsync("hi");
        await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var act = async () => await session.SendUserMessageAsync("again");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Child process has exited*");
    }
}
