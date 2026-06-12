using System.Threading.Channels;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Events;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;
using FluentAssertions;
using Serilog;

namespace InfiniContext.Tests.Sessions;

public class SessionManagerTests
{
    [Fact]
    public async Task CreateAsync_registers_session_and_returns_populated_context()
    {
        var factory = new RecordingSessionFactory();
        await using var mgr = NewManager(factory);

        var ctx = await mgr.CreateAsync(NewRequest(cwd: "/repo/a", activeTaskId: 42), TestContext.Current.CancellationToken);

        ctx.Id.Should().NotBe(Guid.Empty);
        ctx.ActiveTaskId.Should().Be(42);
        ctx.Session.Should().BeOfType<RecordingSession>();
        mgr.ActiveCount.Should().Be(1);
        mgr.Active.Should().ContainSingle().Which.Id.Should().Be(ctx.Id);
        mgr.TryGet(ctx.Id).Should().BeSameAs(ctx);

        var produced = (RecordingSession)ctx.Session;
        produced.StartCount.Should().Be(1, "the manager starts the session before returning");
        factory.LastOptions!.Cwd.Should().Be("/repo/a");
    }

    [Fact]
    public async Task CloseAsync_removes_from_registry_and_disposes_session()
    {
        var factory = new RecordingSessionFactory();
        await using var mgr = NewManager(factory);
        var ctx = await mgr.CreateAsync(NewRequest(), TestContext.Current.CancellationToken);
        var produced = (RecordingSession)ctx.Session;

        await mgr.CloseAsync(ctx.Id);

        mgr.ActiveCount.Should().Be(0);
        mgr.TryGet(ctx.Id).Should().BeNull();
        produced.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task CloseAsync_is_idempotent_for_unknown_ids()
    {
        await using var mgr = NewManager();

        Func<Task> act = () => mgr.CloseAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_closes_all_active_sessions()
    {
        var factory = new RecordingSessionFactory();
        var mgr = NewManager(factory);
        var a = await mgr.CreateAsync(NewRequest(cwd: "/a"), TestContext.Current.CancellationToken);
        var b = await mgr.CreateAsync(NewRequest(cwd: "/b"), TestContext.Current.CancellationToken);

        await mgr.DisposeAsync();

        ((RecordingSession)a.Session).DisposeCount.Should().Be(1);
        ((RecordingSession)b.Session).DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task BroadcastAsync_sends_to_every_target_in_parallel_and_reports_results()
    {
        await using var mgr = NewManager();
        var a = await mgr.CreateAsync(NewRequest(cwd: "/a"), TestContext.Current.CancellationToken);
        var b = await mgr.CreateAsync(NewRequest(cwd: "/b"), TestContext.Current.CancellationToken);
        var c = await mgr.CreateAsync(NewRequest(cwd: "/c"), TestContext.Current.CancellationToken);

        var results = await mgr.BroadcastAsync(new[] { a.Id, c.Id }, "hello", TestContext.Current.CancellationToken);

        ((RecordingSession)a.Session).Sent.Should().Equal(new[] { "hello" });
        ((RecordingSession)b.Session).Sent.Should().BeEmpty("b was not in the target set");
        ((RecordingSession)c.Session).Sent.Should().Equal(new[] { "hello" });

        results.Should().HaveCount(2);
        results.Select(r => r.HostSessionId).Should().Equal(a.Id, c.Id);
        results.Should().OnlyContain(r => r.Outcome == BroadcastSendOutcome.Sent);
    }

    [Fact]
    public async Task BroadcastAsync_reports_unknown_ids_as_NotFound_entries()
    {
        await using var mgr = NewManager();
        var ctx = await mgr.CreateAsync(NewRequest(), TestContext.Current.CancellationToken);
        var phantom = Guid.NewGuid();

        var results = await mgr.BroadcastAsync(new[] { ctx.Id, phantom }, "x", TestContext.Current.CancellationToken);

        ((RecordingSession)ctx.Session).Sent.Should().Equal(new[] { "x" });
        results.Should().HaveCount(2);
        results[0].Outcome.Should().Be(BroadcastSendOutcome.Sent);
        results[0].HostSessionId.Should().Be(ctx.Id);
        results[1].Outcome.Should().Be(BroadcastSendOutcome.NotFound);
        results[1].HostSessionId.Should().Be(phantom);
    }

    [Fact]
    public async Task BroadcastAsync_captures_per_target_failures_without_throwing()
    {
        var factory = new RecordingSessionFactory();
        await using var mgr = NewManager(factory);
        var good = await mgr.CreateAsync(NewRequest(cwd: "/good"), TestContext.Current.CancellationToken);
        var bad = await mgr.CreateAsync(NewRequest(cwd: "/bad"), TestContext.Current.CancellationToken);
        ((RecordingSession)bad.Session).SendThrows = new InvalidOperationException("kaboom");

        var results = await mgr.BroadcastAsync(new[] { good.Id, bad.Id }, "ping", TestContext.Current.CancellationToken);

        results.Should().HaveCount(2);
        results[0].Outcome.Should().Be(BroadcastSendOutcome.Sent);
        results[1].Outcome.Should().Be(BroadcastSendOutcome.Failed);
        results[1].Error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("kaboom");
        ((RecordingSession)good.Session).Sent.Should().Equal(new[] { "ping" });
    }

    [Fact]
    public async Task CreateAsync_on_StartAsync_failure_disposes_session_and_leaves_registry_empty()
    {
        var factory = new RecordingSessionFactory { StartThrows = new InvalidOperationException("boom") };
        await using var mgr = NewManager(factory);

        Func<Task> act = () => mgr.CreateAsync(NewRequest());

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("boom");
        mgr.ActiveCount.Should().Be(0);
        factory.LastSession!.DisposeCount.Should().Be(1, "the half-built session must be disposed on Start failure");
    }

    [Fact]
    public async Task CreateAsync_after_DisposeAsync_throws_ObjectDisposedException()
    {
        var mgr = NewManager();
        await mgr.DisposeAsync();

        Func<Task> act = () => mgr.CreateAsync(NewRequest());

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task CloseAsync_persists_index_row_with_claude_session_id_and_state()
    {
        var fixedSessionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var factory = new RecordingSessionFactory { SessionIdOnStart = () => fixedSessionId };
        using var tmp = new TempIndexDir();
        await using var mgr = NewManager(factory, indexPath: tmp.IndexPath);

        var ctx = await mgr.CreateAsync(NewRequest(cwd: "/repo/x", model: "claude-sonnet-4-6"), TestContext.Current.CancellationToken);
        await mgr.CloseAsync(ctx.Id);

        File.Exists(tmp.IndexPath).Should().BeTrue("close must persist the index file");
        var loaded = await SessionIndex.LoadAsync(tmp.IndexPath, Log.Logger);
        loaded.Sessions.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            SessionId = fixedSessionId.ToString(),
            Cwd = "/repo/x",
            Model = "claude-sonnet-4-6",
            LastState = "Idle",
        });
        mgr.RecentSessions.Should().ContainSingle().Which.SessionId.Should().Be(fixedSessionId.ToString());
    }

    [Fact]
    public async Task CreateAsync_sets_context_model_from_request()
    {
        var factory = new RecordingSessionFactory();
        await using var mgr = NewManager(factory);

        var ctx = await mgr.CreateAsync(NewRequest(cwd: "/repo/x", model: "claude-fable-5"), TestContext.Current.CancellationToken);

        ctx.Model.Should().Be("claude-fable-5",
            "the GUI inspector reads SessionContext.Model to show + change the running model.");
    }

    [Fact]
    public async Task CloseAsync_skips_index_write_when_session_id_was_never_assigned()
    {
        var factory = new RecordingSessionFactory { SessionIdOnStart = () => null };
        using var tmp = new TempIndexDir();
        await using var mgr = NewManager(factory, indexPath: tmp.IndexPath);

        var ctx = await mgr.CreateAsync(NewRequest(), TestContext.Current.CancellationToken);
        await mgr.CloseAsync(ctx.Id);

        File.Exists(tmp.IndexPath).Should().BeFalse(
            "a session that never observed system/init has no resumable id, so we don't persist a row");
        mgr.RecentSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenAsync_seeds_RecentSessions_from_index_on_disk()
    {
        using var tmp = new TempIndexDir();
        var preexisting = new SessionIndex
        {
            Sessions =
            {
                new SessionIndexEntry
                {
                    SessionId = "11111111-1111-1111-1111-111111111111",
                    Cwd = "/seed",
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    LastActive = DateTimeOffset.UnixEpoch,
                    LastState = "Exited",
                },
            },
        };
        await preexisting.SaveAsync(tmp.IndexPath);

        await using var mgr = await SessionManager.OpenAsync(
            InfiniContextSettings.Defaults,
            Log.Logger,
            (slot, transcript, cwd) => new StubApprovalSink(),
            sessionFactory: new RecordingSessionFactory().Build,
            indexPath: tmp.IndexPath);

        mgr.RecentSessions.Should().ContainSingle().Which.Cwd.Should().Be("/seed");
        mgr.IndexPath.Should().Be(tmp.IndexPath);
    }

    [Fact]
    public async Task CloseAsync_dedupes_rows_by_session_id_when_session_resumes()
    {
        // The same claude session id can recur when the host re-launches with --resume <id>.
        // The newer close should supersede the older row, not append a duplicate.
        var fixedSessionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var factory = new RecordingSessionFactory { SessionIdOnStart = () => fixedSessionId };
        using var tmp = new TempIndexDir();
        await using var mgr = NewManager(factory, indexPath: tmp.IndexPath);

        var first = await mgr.CreateAsync(NewRequest(cwd: "/first"), TestContext.Current.CancellationToken);
        await mgr.CloseAsync(first.Id);
        var second = await mgr.CreateAsync(NewRequest(cwd: "/second"), TestContext.Current.CancellationToken);
        await mgr.CloseAsync(second.Id);

        mgr.RecentSessions.Should().ContainSingle().Which.Cwd.Should().Be("/second",
            "the more recent close replaces the older row instead of duplicating it");
    }

    [Fact]
    public async Task CreateAsync_wires_pressure_crossings_to_RotationReason_on_the_context()
    {
        // Issue 0013 (task 0005): a context-pressure crossing must mark the SessionContext so
        // the host can detect "session ended because of a nudge" on the Exited transition and
        // auto-rotate to a fresh session bound to the same task.
        var factory = new RecordingSessionFactory();
        await using var mgr = NewManager(factory);
        // Deliberately no model on the spawn request (the default-session shape): the monitor
        // must still pick the large-context 45/65 pair from the window it resolves at runtime
        // via the init-reported model id (task 0018 follow-up).
        var ctx = await mgr.CreateAsync(NewRequest(activeTaskId: 99), TestContext.Current.CancellationToken);
        ctx.RotationReason.Should().Be(InfiniContext.Core.Sessions.RotationReason.None, "no nudge has fired yet");

        // Drive the monitor through the same observer SessionManager passes to the factory —
        // that's the wire that the manager's CreateAsync subscribes against.
        var observer = factory.LastObserver!;
        var sid = Guid.NewGuid();
        observer(new SystemInitEvent
        {
            Type = "system",
            Subtype = "init",
            SessionId = sid,
            Model = "claude-opus-4-8",
            Raw = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "system",
                ["subtype"] = "init",
                ["session_id"] = sid.ToString("D"),
                ["model"] = "claude-opus-4-8",
            },
        });
        // Opus 4.8 is a 1M-context model, so the large-context threshold pair applies
        // (soft 45% / hard 65%). Drive the monitor against the 1M window accordingly.
        observer(BuildAssistantEvent(sid, used: 500_000)); // 50%— soft
        ctx.RotationReason.Should().Be(InfiniContext.Core.Sessions.RotationReason.Soft);

        observer(BuildAssistantEvent(sid, used: 700_000)); // 70%— hard
        ctx.RotationReason.Should().Be(InfiniContext.Core.Sessions.RotationReason.Hard, "hard escalates over soft");
    }

    // Task 0014: context occupancy is measured per API call from each assistant event's
    // message.usage, not from the summed result.usage. The window resolves from the manager's
    // default ModelContextWindows map (claude-opus-4-8 -> 1_000_000), so no modelUsage is needed.
    private static AssistantEvent BuildAssistantEvent(Guid sessionId, int used)
    {
        var raw = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "assistant",
            ["session_id"] = sessionId.ToString("D"),
            ["message"] = new System.Text.Json.Nodes.JsonObject
            {
                ["model"] = "claude-opus-4-8",
                ["usage"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["input_tokens"] = used,
                    ["cache_creation_input_tokens"] = 0,
                    ["cache_read_input_tokens"] = 0,
                },
            },
        };
        return new AssistantEvent
        {
            Type = "assistant",
            SessionId = sessionId,
            Content = Array.Empty<ContentBlock>(),
            Raw = raw,
        };
    }

    [Fact]
    public async Task DisposeAsync_records_index_rows_for_live_sessions()
    {
        var fixedSessionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var factory = new RecordingSessionFactory { SessionIdOnStart = () => fixedSessionId };
        using var tmp = new TempIndexDir();
        var mgr = NewManager(factory, indexPath: tmp.IndexPath);
        await mgr.CreateAsync(NewRequest(cwd: "/live"), TestContext.Current.CancellationToken);

        await mgr.DisposeAsync();

        var loaded = await SessionIndex.LoadAsync(tmp.IndexPath, Log.Logger);
        loaded.Sessions.Should().ContainSingle().Which.Cwd.Should().Be("/live");
    }

    private static SessionManager NewManager(RecordingSessionFactory? factory = null, string? indexPath = null) =>
        new(
            InfiniContextSettings.Defaults,
            Log.Logger,
            (slot, transcript, cwd) => new StubApprovalSink(),
            supervisor: null,
            lockRegistry: null,
            sessionFactory: (factory ?? new RecordingSessionFactory()).Build,
            indexPath: indexPath);

    private static SpawnRequest NewRequest(string cwd = "/tmp/repo", int? activeTaskId = null, string? model = null) => new()
    {
        Cwd = cwd,
        Model = model,
        ActiveTaskId = activeTaskId,
    };

    private sealed class TempIndexDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cp-mgridx-{Guid.NewGuid():N}");
        public string IndexPath => System.IO.Path.Combine(Path, "index.json");

        public TempIndexDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class RecordingSessionFactory
    {
        public ClaudeSessionOptions? LastOptions { get; private set; }
        public Action<ClaudeEvent>? LastObserver { get; private set; }
        public RecordingSession? LastSession { get; private set; }
        public Exception? StartThrows { get; init; }
        public Func<Guid?>? SessionIdOnStart { get; init; }

        public IClaudeSession Build(ClaudeSessionOptions options, Action<ClaudeEvent> observer, IApprovalSink approvalSink, ILogger logger)
        {
            LastOptions = options;
            LastObserver = observer;
            LastSession = new RecordingSession
            {
                StartThrows = StartThrows,
                SessionIdAfterStart = SessionIdOnStart?.Invoke(),
            };
            return LastSession;
        }
    }

    private sealed class RecordingSession : IClaudeSession
    {
        private readonly Channel<ClaudeEvent> _events = Channel.CreateUnbounded<ClaudeEvent>();
        private readonly TaskCompletionSource<bool> _exit = new();
        private readonly List<string> _sent = new();

        public Exception? StartThrows { get; init; }
        public Exception? SendThrows { get; set; }
        public Guid? SessionIdAfterStart { get; init; }
        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }
        public IReadOnlyList<string> Sent
        {
            get { lock (_sent) return _sent.ToArray(); }
        }

        public Guid? SessionId { get; private set; }
        public SessionStateMachine State { get; } = new();
        public ChannelReader<ClaudeEvent> Events => _events.Reader;
        public IReadOnlyList<string> StderrTail { get; } = Array.Empty<string>();
        public int UnknownEventCount => 0;
        public int MalformedLineCount => 0;
        public IReadOnlyDictionary<string, int> UnknownTypeHistogram { get; } = new Dictionary<string, int>();

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            if (StartThrows is not null) return Task.FromException(StartThrows);
            SessionId = SessionIdAfterStart;
            return Task.CompletedTask;
        }

        public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
        {
            if (SendThrows is not null) return Task.FromException(SendThrows);
            lock (_sent) _sent.Add(text);
            return Task.CompletedTask;
        }

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => _exit.Task;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
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
