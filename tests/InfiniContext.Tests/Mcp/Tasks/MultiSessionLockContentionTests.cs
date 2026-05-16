using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Mcp.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Mcp.Tasks;

/// <summary>
/// Stage 2B Phase 7 — closes 2A todo §B T4 (the "multi-session lock contention" gap).
///
/// <para>
/// Two <see cref="ApprovalChannel"/>s on distinct named pipes share one <see cref="LockRegistry"/>
/// (the host-singleton wiring per Stage 2A Phase 7 / Stage 2B D2). Each channel is a stand-in for
/// a real <c>ClaudeSession</c>'s sidecar — the owner id is the pipe name, which is exactly what
/// the production sidecar passes. Bytes-on-wire are the same shape <see cref="LockChannelE2ETests"/>
/// already exercises against a single channel; the new coverage here is the registry behaving
/// correctly when *two* channels race for the same file.
/// </para>
///
/// <para>
/// Why no FakeClaude spawn: the lock messages bypass <c>claude</c> entirely — they ride the
/// approval pipe directly between sidecar (Node) and host (C#). Spawning FakeClaude would test
/// process plumbing, not lock semantics. <see cref="SidecarSmokeTests"/> already covers the
/// "sidecar handshake works" path, so this file focuses on what was actually missing:
/// cross-session contention through one registry.
/// </para>
/// </summary>
public class MultiSessionLockContentionTests
{
    [Fact]
    public async Task Two_sessions_contend_then_winner_releases_loser_acquires()
    {
        // One shared registry — the host singleton in production. Tight poll interval so the
        // waiter notices the release quickly without burning wall-clock.
        var registry = new LockRegistry(pollInterval: TimeSpan.FromMilliseconds(5));

        var (pipeA, channelA) = await StartChannelAsync(registry);
        var (pipeB, channelB) = await StartChannelAsync(registry);
        await using var _disposeA = channelA;
        await using var _disposeB = channelB;

        await using var clientA = await ConnectAsync(pipeA);
        await using var clientB = await ConnectAsync(pipeB);
        var (readerA, writerA) = Open(clientA);
        var (readerB, writerB) = Open(clientB);

        const string file = "shared/file.md";

        // Session A acquires the lock first.
        var aTokenResp = await AcquireAsync(readerA, writerA, file, ownerOverride: pipeA);
        aTokenResp.Ok.Should().BeTrue("A is uncontended");
        aTokenResp.Token.Should().NotBeNullOrEmpty();

        // Session B sends its acquire while A still holds — kick it off without awaiting so we
        // can observe the contention from outside, then release A and confirm B wins.
        await writerB.WriteLineAsync(new LockAcquireRequest(file, pipeB).ToJson().ToJsonString());
        var bResponseTask = readerB.ReadLineAsync();

        // Give B a moment to start polling so we know it's actually contending, not finishing
        // before A's lock is even in the registry.
        await Task.Delay(50);
        bResponseTask.IsCompleted.Should().BeFalse(
            "B's acquire must still be in-flight (polling) while A holds the lock");
        registry.Snapshot().Should().ContainSingle()
            .Which.OwnerId.Should().Be(pipeA);

        // A releases — B's poll loop should pick this up on the next tick and respond Ok.
        await writerA.WriteLineAsync(new LockReleaseRequest(aTokenResp.Token!).ToJson().ToJsonString());
        var aReleaseLine = await readerA.ReadLineAsync() ?? throw new InvalidOperationException("no A release response");
        LockReleaseResponse.FromJson((JsonObject)JsonNode.Parse(aReleaseLine)!).Ok.Should().BeTrue();

        var bResponseLine = await bResponseTask ?? throw new InvalidOperationException("no B acquire response");
        var bAcquire = LockAcquireResponse.FromJson((JsonObject)JsonNode.Parse(bResponseLine)!);
        bAcquire.Ok.Should().BeTrue("B should win the lock once A releases, before B's maxWait expires");
        bAcquire.Token.Should().NotBeNullOrEmpty();

        registry.Snapshot().Should().ContainSingle().Which.OwnerId.Should().Be(pipeB,
            "the registry should now reflect B as the holder");
    }

    [Fact]
    public async Task Channel_dispose_releases_locks_so_other_session_can_acquire()
    {
        // Variant of the takeover path that exercises the "owner went away" release route —
        // ApprovalChannel.DisposeAsync calls LockRegistry.ReleaseAllOwnedBy(pipeName). Without that,
        // a crashed session would strand its lock until TTL expiry.
        var registry = new LockRegistry(pollInterval: TimeSpan.FromMilliseconds(5));

        var (pipeA, channelA) = await StartChannelAsync(registry);
        var (pipeB, channelB) = await StartChannelAsync(registry);
        await using var _disposeB = channelB; // B stays alive; A disposed mid-test.

        await using var clientB = await ConnectAsync(pipeB);
        var (readerB, writerB) = Open(clientB);

        // A holds the lock then disappears (simulated session crash / clean close).
        await using (var clientA = await ConnectAsync(pipeA))
        {
            var (_, writerA) = Open(clientA);
            await writerA.WriteLineAsync(new LockAcquireRequest("c.md", pipeA).ToJson().ToJsonString());
        }
        // Hold lock count check by sneaking through the registry — bypasses pipe ack-ordering races.
        await WaitUntilAsync(() => registry.Snapshot().Count == 1, TimeSpan.FromSeconds(1));

        await channelA.DisposeAsync(); // <- triggers ReleaseAllOwnedBy(pipeA).

        // B's acquire should now succeed almost immediately (the release happened before B asks).
        var bResp = await AcquireAsync(readerB, writerB, "c.md", ownerOverride: pipeB);
        bResp.Ok.Should().BeTrue("A's disposal must reclaim A's locks so B can take the file");
        registry.Snapshot().Should().ContainSingle().Which.OwnerId.Should().Be(pipeB);
    }

    private static async Task<(string PipeName, ApprovalChannel Channel)> StartChannelAsync(LockRegistry registry)
    {
        var pipe = ApprovalChannel.GeneratePipeName();
        var channel = new ApprovalChannel(
            pipe,
            new DenyAllSink(),
            lockRegistry: registry,
            lockTtl: TimeSpan.FromSeconds(30),
            lockMaxWait: TimeSpan.FromSeconds(3));
        await channel.StartAsync();
        return (pipe, channel);
    }

    private static async Task<LockAcquireResponse> AcquireAsync(
        StreamReader reader, StreamWriter writer, string file, string ownerOverride)
    {
        await writer.WriteLineAsync(new LockAcquireRequest(file, ownerOverride).ToJson().ToJsonString());
        var line = await reader.ReadLineAsync() ?? throw new InvalidOperationException("no acquire response");
        return LockAcquireResponse.FromJson((JsonObject)JsonNode.Parse(line)!);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5_000);
        return pipe;
    }

    private static (StreamReader reader, StreamWriter writer) Open(NamedPipeClientStream pipe)
    {
        var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        return (reader, writer);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        if (!predicate())
            throw new TimeoutException($"predicate did not become true within {timeout.TotalMilliseconds:F0}ms");
    }

    private sealed class DenyAllSink : IApprovalSink
    {
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ApprovalResponse.Deny("not under test"));
    }
}
