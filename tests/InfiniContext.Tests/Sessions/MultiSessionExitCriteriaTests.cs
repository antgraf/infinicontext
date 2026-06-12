using System.Text.Json.Nodes;
using System.Threading.Channels;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Events;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;
using FluentAssertions;
using Serilog;

namespace InfiniContext.Tests.Sessions;

/// <summary>
/// Stage 2B Phase 8 тАФ exit-criteria smoke. Each fact maps to one bullet in
/// <c>docs/infinicontext-initial-design.md</c> ┬з9 "Stage 2B exit criteria". The kill-host bullet
/// (┬з9.6) and CI-matrix bullet (┬з9.7) are not testable from inside xUnit and are documented in
/// <c>docs/stage2/exit-checklist.md</c> instead тАФ see D37 in <c>docs/stage2/decisions.md</c>.
///
/// <para>
/// These cases drive <see cref="SessionManager"/> against the local <see cref="StreamingFakeSession"/>
/// (in-memory, no spawned process). Real-spawn coverage of the same paths lives in the
/// <c>Repl*Tests</c> family (FakeClaude over <c>Process.Start</c>) тАФ keeping the exit smoke
/// in-process means it runs in &lt; 1 s and never flakes on process timing.
/// </para>
/// </summary>
public class MultiSessionExitCriteriaTests
{
    // ┬з9.1 тАФ Three concurrent sessions, each in a different cwd, all streaming visibly.
    [Fact]
    public async Task Three_concurrent_sessions_each_in_different_cwd_stream_independently()
    {
        var factory = new StreamingFakeSessionFactory();
        await using var mgr = NewManager(factory);

        var a = await mgr.CreateAsync(new SpawnRequest { Cwd = "/repo/a" }, TestContext.Current.CancellationToken);
        var b = await mgr.CreateAsync(new SpawnRequest { Cwd = "/repo/b" }, TestContext.Current.CancellationToken);
        var c = await mgr.CreateAsync(new SpawnRequest { Cwd = "/repo/c" }, TestContext.Current.CancellationToken);

        mgr.ActiveCount.Should().Be(3);
        mgr.Active.Select(s => s.Cwd).Should().BeEquivalentTo(new[] { "/repo/a", "/repo/b", "/repo/c" });

        // Stream one distinct assistant turn per session тАФ the SessionTranscriptReader running
        // inside the manager pumps each into its own TranscriptModel.
        factory.SessionFor(a).Emit(NewAssistantEvent("hello-from-a"));
        factory.SessionFor(b).Emit(NewAssistantEvent("hello-from-b"));
        factory.SessionFor(c).Emit(NewAssistantEvent("hello-from-c"));

        await WaitForTranscriptToContain(a.Transcript, "hello-from-a");
        await WaitForTranscriptToContain(b.Transcript, "hello-from-b");
        await WaitForTranscriptToContain(c.Transcript, "hello-from-c");

        // No cross-talk тАФ each transcript holds only its own session's text.
        a.Transcript.Snapshot().Should().NotContain(f => f.Contains("hello-from-b") || f.Contains("hello-from-c"));
        b.Transcript.Snapshot().Should().NotContain(f => f.Contains("hello-from-a") || f.Contains("hello-from-c"));
        c.Transcript.Snapshot().Should().NotContain(f => f.Contains("hello-from-a") || f.Contains("hello-from-b"));
    }

    // ┬з9.2 тАФ Switch focus between sessions with no I/O loss (events buffered in their channels
    // while off-screen). The implementation invariant: each session's reader runs regardless of
    // which session is focused, so events emitted to an "off-screen" session still land in its
    // transcript.
    [Fact]
    public async Task Events_to_unfocused_session_are_preserved_in_its_transcript()
    {
        var factory = new StreamingFakeSessionFactory();
        await using var mgr = NewManager(factory);

        var focused = await mgr.CreateAsync(new SpawnRequest { Cwd = "/focused" }, TestContext.Current.CancellationToken);
        var offscreen = await mgr.CreateAsync(new SpawnRequest { Cwd = "/offscreen" }, TestContext.Current.CancellationToken);

        // While the host "looks at" `focused`, claude is still producing turns in `offscreen`. The
        // host has no event-subscription side-effect that gates the reader тАФ emit and observe.
        factory.SessionFor(offscreen).Emit(NewAssistantEvent("offscreen-output-1"));
        factory.SessionFor(offscreen).Emit(NewAssistantEvent("offscreen-output-2"));

        await WaitForTranscriptToContain(offscreen.Transcript, "offscreen-output-2");
        offscreen.Transcript.Snapshot().Should().Contain(f => f.Contains("offscreen-output-1"));
        offscreen.Transcript.Snapshot().Should().Contain(f => f.Contains("offscreen-output-2"));
        focused.Transcript.Snapshot().Should().BeEmpty("the focused session received no events");
    }

    // ┬з9.3 тАФ Resume works for any session listed in the host index, including one whose process
    // exited cleanly previously.
    [Fact]
    public async Task Closed_session_persists_to_index_and_resume_threads_id_through_to_factory()
    {
        var resumeSessionId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var factory = new StreamingFakeSessionFactory { SessionIdOnStart = () => resumeSessionId };
        using var tmp = new TempIndexDir();

        // Phase 1: open manager, create one session that picks up `resumeSessionId`, close it.
        await using (var mgr = NewManager(factory, indexPath: tmp.IndexPath))
        {
            var ctx = await mgr.CreateAsync(new SpawnRequest { Cwd = "/work" }, TestContext.Current.CancellationToken);
            await mgr.CloseAsync(ctx.Id);
            mgr.RecentSessions.Should().ContainSingle().Which.SessionId.Should().Be(resumeSessionId.ToString());
        }

        // Phase 2: reopen the manager тАФ the recent-sessions list survives disk round-trip.
        var reopenFactory = new StreamingFakeSessionFactory();
        await using var resumed = await SessionManager.OpenAsync(
            InfiniContextSettings.Defaults,
            Log.Logger,
            (slot, transcript, cwd) => new StubApprovalSink(),
            sessionFactory: reopenFactory.Build,
            indexPath: tmp.IndexPath);

        resumed.RecentSessions.Should().ContainSingle().Which.SessionId.Should().Be(resumeSessionId.ToString());

        // Phase 3: caller picks a row тЖТ host issues a SpawnRequest with that ResumeSessionId.
        // The manager must thread it through to the session factory unchanged so claude is invoked
        // with --resume <id>.
        await resumed.CreateAsync(new SpawnRequest { Cwd = "/work", ResumeSessionId = resumeSessionId.ToString() }, TestContext.Current.CancellationToken);

        reopenFactory.LastOptions!.ResumeSessionId.Should().Be(resumeSessionId.ToString());
    }

    // ┬з9.4 тАФ Broadcast prompt to two sessions returns two independent transcripts.
    [Fact]
    public async Task Broadcast_to_two_of_three_sessions_lands_only_in_those_two()
    {
        var factory = new StreamingFakeSessionFactory();
        await using var mgr = NewManager(factory);
        var a = await mgr.CreateAsync(new SpawnRequest { Cwd = "/a" }, TestContext.Current.CancellationToken);
        var b = await mgr.CreateAsync(new SpawnRequest { Cwd = "/b" }, TestContext.Current.CancellationToken);
        var c = await mgr.CreateAsync(new SpawnRequest { Cwd = "/c" }, TestContext.Current.CancellationToken);

        var results = await mgr.BroadcastAsync(new[] { a.Id, c.Id }, "shared-prompt", TestContext.Current.CancellationToken);

        results.Should().OnlyContain(r => r.Outcome == BroadcastSendOutcome.Sent);
        factory.SessionFor(a).Sent.Should().Equal(new[] { "shared-prompt" });
        factory.SessionFor(b).Sent.Should().BeEmpty("b was not in the target set");
        factory.SessionFor(c).Sent.Should().Equal(new[] { "shared-prompt" });
    }

    // ┬з9.5 тАФ Approval prompts in one session don't block input or progress in another.
    [Fact]
    public async Task Pending_approval_in_one_session_does_not_block_message_send_in_another()
    {
        var factory = new StreamingFakeSessionFactory();
        await using var mgr = NewManager(factory);
        var a = await mgr.CreateAsync(new SpawnRequest { Cwd = "/a" }, TestContext.Current.CancellationToken);
        var b = await mgr.CreateAsync(new SpawnRequest { Cwd = "/b" }, TestContext.Current.CancellationToken);

        // Park an approval request in session A's slot тАФ it will never resolve. Per-session slot
        // design (Stage 2B Phase 5) means session B's send-path has no shared dependency.
        var aPending = a.ApprovalSlot.RequestAsync(
            new ApprovalRequest("Bash", new JsonObject { ["command"] = "rm -rf /" }),
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: CancellationToken.None);
        a.ApprovalSlot.Current.Should().NotBeNull("the approval request is parked and awaiting host resolution");

        var sendOnB = b.Session.SendUserMessageAsync("hi-b", TestContext.Current.CancellationToken);
        var raceWinner = await Task.WhenAny(sendOnB, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        raceWinner.Should().BeSameAs(sendOnB, "session B must process independently of session A's pending approval");

        factory.SessionFor(b).Sent.Should().Equal(new[] { "hi-b" });
        aPending.IsCompleted.Should().BeFalse("the parked approval is still in flight тАФ only B was unblocked");
    }

    // ------------- helpers -------------

    private static SessionManager NewManager(StreamingFakeSessionFactory? factory = null, string? indexPath = null) =>
        new(
            InfiniContextSettings.Defaults,
            Log.Logger,
            (slot, transcript, cwd) => new StubApprovalSink(),
            supervisor: null,
            lockRegistry: null,
            sessionFactory: (factory ?? new StreamingFakeSessionFactory()).Build,
            indexPath: indexPath);

    private static AssistantEvent NewAssistantEvent(string text) => new()
    {
        Type = "assistant",
        Raw = new JsonObject(),
        Content = new ContentBlock[]
        {
            new TextBlock { Type = "text", Text = text, Raw = new JsonObject() },
        },
    };

    private static async Task WaitForTranscriptToContain(TranscriptModel transcript, string needle)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (transcript.Snapshot().Any(f => f.Contains(needle))) return;
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException($"transcript never received fragment containing '{needle}' within 5s");
    }

    private sealed class TempIndexDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ic-exit-{Guid.NewGuid():N}");
        public string IndexPath => System.IO.Path.Combine(Path, "index.json");

        public TempIndexDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>Factory that hands out <see cref="StreamingFakeSession"/> instances and exposes
    /// each one back by the <see cref="SessionContext"/> the manager produced. Lets tests inject
    /// events into a specific session post-creation.</summary>
    private sealed class StreamingFakeSessionFactory
    {
        private readonly Dictionary<ClaudeSessionOptions, StreamingFakeSession> _byOptions = new();
        private readonly Dictionary<Guid, StreamingFakeSession> _byCwd = new(); // keyed off cwd-hash via ctx
        private readonly Queue<StreamingFakeSession> _pending = new();

        public ClaudeSessionOptions? LastOptions { get; private set; }
        public Func<Guid?>? SessionIdOnStart { get; init; }

        public IClaudeSession Build(ClaudeSessionOptions options, Action<ClaudeEvent> observer, IApprovalSink approvalSink, ILogger logger)
        {
            LastOptions = options;
            var session = new StreamingFakeSession { SessionIdAfterStart = SessionIdOnStart?.Invoke() };
            _byOptions[options] = session;
            _pending.Enqueue(session);
            return session;
        }

        public StreamingFakeSession SessionFor(SessionContext ctx)
        {
            // The manager exposes the session it built via ctx.Session тАФ narrow back to our fake.
            return (StreamingFakeSession)ctx.Session;
        }
    }

    private sealed class StreamingFakeSession : IClaudeSession
    {
        private readonly Channel<ClaudeEvent> _events = Channel.CreateUnbounded<ClaudeEvent>();
        private readonly TaskCompletionSource<bool> _exit = new();
        private readonly List<string> _sent = new();

        public Guid? SessionIdAfterStart { get; init; }
        public Guid? SessionId { get; private set; }
        public SessionStateMachine State { get; } = new();
        public ChannelReader<ClaudeEvent> Events => _events.Reader;
        public IReadOnlyList<string> StderrTail { get; } = Array.Empty<string>();
        public int UnknownEventCount => 0;
        public int MalformedLineCount => 0;
        public IReadOnlyDictionary<string, int> UnknownTypeHistogram { get; } = new Dictionary<string, int>();

        public IReadOnlyList<string> Sent
        {
            get { lock (_sent) return _sent.ToArray(); }
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            SessionId = SessionIdAfterStart;
            return Task.CompletedTask;
        }

        public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
        {
            lock (_sent) _sent.Add(text);
            return Task.CompletedTask;
        }

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => _exit.Task;

        /// <summary>Push an event through the session's read channel. The manager's per-session
        /// reader picks it up and pumps the rendered fragment into the session's transcript.</summary>
        public void Emit(ClaudeEvent ev) => _events.Writer.TryWrite(ev);

        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            _exit.TrySetResult(true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubApprovalSink : IApprovalSink
    {
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ApprovalResponse.Deny("stub"));
    }
}
