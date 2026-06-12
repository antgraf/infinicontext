using System.Text;
using System.Text.Json.Nodes;
using InfiniContext.Core.Events;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;
using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Sessions;

/// <summary>
/// End-to-end pipeline tests: spawn FakeClaude as a real child process, drive a
/// <see cref="ClaudeSession"/> through it, and assert the parser/state-machine
/// composition behaves correctly under both happy and adversarial scenarios.
///
/// These tests replace the stub written during Stage 1C and are the Stage 1F
/// done criterion: end-to-end without real <c>claude</c>, in seconds.
/// </summary>
public class FakeClaudeIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly string ScriptsDir = Path.Combine(TestPaths.RepoRoot, "tests", "scripts");

    [Fact]
    public async Task Replay_simple_prompt_produces_expected_event_trace()
    {
        var run = await DriveSessionAsync(extraArgs: new[]
        {
            "--replay", TestPaths.Fixture("simple-prompt"),
        });

        // Matches tests/fixtures/simple-prompt.expected-trace.txt: hook_response, init,
        // assistant[text], rate_limit_event, result/success.
        run.EventTypes.Should().Equal(
            "system/hook_response",
            "system/init",
            "assistant",
            "rate_limit_event",
            "result/success");

        run.SessionId.Should().NotBeNull("session_id must be picked up from system/init");
        run.FinalState.Should().Be(SessionState.Exited);
        run.UnknownEventCount.Should().Be(0);
        run.MalformedLineCount.Should().Be(0);
        run.ProcessExitCode.Should().Be(0);
    }

    [Fact]
    public async Task Replay_nested_tool_correlates_parent_and_child_tool_uses()
    {
        var run = await DriveSessionAsync(extraArgs: new[]
        {
            "--replay", TestPaths.Fixture("nested-tool"),
        });

        // At the deepest point, both the parent (top-level Agent/Task tool) and its child
        // (e.g. Bash) should have been simultaneously open. parent_tool_use_id correlates them.
        run.MaxOpenToolUses.Should().BeGreaterThanOrEqualTo(2,
            "nested-tool fixture has a sub-agent calling its own tool — both must be open simultaneously");
        run.SawChildWithCorrelatedParent.Should().BeTrue(
            "at least one tool slot must be opened with a non-null ParentToolUseId pointing at another open slot");
        run.FinalState.Should().Be(SessionState.Exited);
        run.MalformedLineCount.Should().Be(0);
    }

    [Fact]
    public async Task Script_truncated_line_counts_one_malformed_and_continues()
    {
        var run = await DriveSessionAsync(extraArgs: new[]
        {
            "--script", Path.Combine(ScriptsDir, "truncated-line.json"),
        });

        run.MalformedLineCount.Should().Be(1, "exactly one truncated line was emitted");
        run.EventTypes.Should().ContainInOrder(
            "system/init", "assistant", "result/success");
        run.FinalState.Should().Be(SessionState.Exited);
    }

    [Fact]
    public async Task Script_mid_line_exit_transitions_to_exited_cleanly()
    {
        var run = await DriveSessionAsync(extraArgs: new[]
        {
            "--script", Path.Combine(ScriptsDir, "mid-line-exit.json"),
        });

        // The unterminated final line is reported as malformed by the parser.
        run.MalformedLineCount.Should().Be(1);

        // Process exited non-zero mid-stream — state machine still reaches Exited via
        // HandleProcessExit, channel completes, no exceptions escape.
        run.FinalState.Should().Be(SessionState.Exited);
        run.ProcessExitCode.Should().Be(1);
        run.Transitions.Should().Contain(t => t.To == SessionState.Exited);
    }

    [Fact]
    public async Task Script_slow_stream_remains_in_streaming_through_inter_event_gap()
    {
        var run = await DriveSessionAsync(extraArgs: new[]
        {
            "--script", Path.Combine(ScriptsDir, "slow-stream.json"),
        });

        run.Transitions.Select(t => (t.From, t.To)).Should().ContainInOrder(
            (SessionState.Idle, SessionState.Sending),
            (SessionState.Sending, SessionState.Streaming),
            (SessionState.Streaming, SessionState.Idle),
            (SessionState.Idle, SessionState.Exited));
        run.FinalState.Should().Be(SessionState.Exited);
        run.MalformedLineCount.Should().Be(0);
        run.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150),
            "the script's 200ms sleep guarantees a measurable gap");
    }

    [Fact]
    public async Task Script_schema_drift_increments_unknown_event_counter()
    {
        var run = await DriveSessionAsync(extraArgs: new[]
        {
            "--script", Path.Combine(ScriptsDir, "schema-drift.json"),
        });

        run.UnknownEventCount.Should().Be(1,
            "exactly one unknown top-level event was emitted");
        run.UnknownTypeHistogram.Should().ContainKey("some_future_top_level");
        run.EventTypes.Should().ContainInOrder("system/init", "some_future_top_level", "assistant", "result/success");
        run.FinalState.Should().Be(SessionState.Exited);
    }

    [Fact]
    public async Task Oversized_tool_result_does_not_block_session()
    {
        // Generate a script with a ~32 KB tool_result content payload — exceeds the parser's
        // 2 KB rx-log truncation budget and the bounded channel's typical line size, but must
        // still flow through cleanly.
        var sessionId = "00000000-0000-0000-0000-000000000bbb";
        var bigText = new string('A', 32 * 1024);
        var toolUseId = "toolu_oversized_1";

        var assistantMsg = new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = toolUseId,
                        ["name"] = "Bash",
                        ["input"] = new JsonObject { ["command"] = "echo hello" },
                    },
                },
            },
            ["session_id"] = sessionId,
        };

        var userMsg = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolUseId,
                        ["content"] = bigText,
                        ["is_error"] = false,
                    },
                },
            },
            ["session_id"] = sessionId,
        };

        var resultMsg = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["duration_ms"] = 1,
            ["num_turns"] = 1,
            ["result"] = "done",
            ["stop_reason"] = "end_turn",
            ["session_id"] = sessionId,
            ["total_cost_usd"] = 0,
        };

        var script = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject { ["type"] = "wait-stdin" },
                new JsonObject { ["type"] = "emit-line", ["json"] = new JsonObject
                {
                    ["type"] = "system", ["subtype"] = "init",
                    ["session_id"] = sessionId, ["model"] = "fake", ["cwd"] = "/tmp",
                    ["claude_code_version"] = "0.0.0",
                } },
                new JsonObject { ["type"] = "emit-line", ["json"] = assistantMsg },
                new JsonObject { ["type"] = "emit-line", ["json"] = userMsg },
                new JsonObject { ["type"] = "emit-line", ["json"] = resultMsg },
                new JsonObject { ["type"] = "exit", ["code"] = 0 },
            },
        };

        var scriptPath = Path.Combine(Path.GetTempPath(), $"fakeclaude-oversized-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(scriptPath, script.ToJsonString(), Encoding.UTF8, TestContext.Current.CancellationToken);

        try
        {
            var run = await DriveSessionAsync(extraArgs: new[] { "--script", scriptPath });

            run.MalformedLineCount.Should().Be(0);
            run.UnknownEventCount.Should().Be(0);
            run.FinalState.Should().Be(SessionState.Exited);
            run.MaxOpenToolUses.Should().Be(1, "the oversized tool_result should close the slot");
            run.Transitions.Should().Contain(t => t.From == SessionState.Streaming && t.To == SessionState.Idle);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task FirstUserMessage_is_sent_to_stdin_on_session_start()
    {
        // Contract: when ClaudeSessionOptions.FirstUserMessage is set, the session writes that
        // message to stdin immediately after StartAsync wires the IO loops, BEFORE any event has
        // been observed. Real claude (--print --input-format stream-json, verified against
        // 2.1.143) does not emit system/init until it has read at least one user message;
        // gating the auto-send on system/init deadlocks claude (waiting for stdin) against
        // the host (waiting for init).
        // No SendUserMessageAsync from the test — the session's auto-send is the only thing that
        // can unblock FakeClaude's wait-stdin.
        var sessionId = "00000000-0000-0000-0000-000000000ccc";
        var pointer = "Working on task 0001 (\"Test\"). Task folder: .task/0001-test/. Use the infinicontext-task skill.";
        var stdinRecord = Path.Combine(Path.GetTempPath(), $"fakeclaude-stdin-{Guid.NewGuid():N}.txt");

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
                new JsonObject { ["type"] = "wait-stdin", ["record_to"] = stdinRecord, ["lines"] = 1 },
                new JsonObject { ["type"] = "emit-line", ["json"] = new JsonObject
                {
                    ["type"] = "assistant",
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "ok" } },
                    },
                    ["session_id"] = sessionId,
                } },
                new JsonObject { ["type"] = "emit-line", ["json"] = new JsonObject
                {
                    ["type"] = "result", ["subtype"] = "success", ["is_error"] = false,
                    ["duration_ms"] = 1, ["num_turns"] = 1, ["result"] = "ok", ["stop_reason"] = "end_turn",
                    ["session_id"] = sessionId, ["total_cost_usd"] = 0,
                } },
                new JsonObject { ["type"] = "exit", ["code"] = 0 },
            },
        };

        var scriptPath = Path.Combine(Path.GetTempPath(), $"fakeclaude-first-msg-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(scriptPath, script.ToJsonString(), Encoding.UTF8, TestContext.Current.CancellationToken);

        try
        {
            var options = new ClaudeSessionOptions
            {
                Cwd = TestPaths.RepoRoot,
                ClaudeExecutable = FakeClaudeRunner.ExecutablePath,
                ExtraArgs = new[] { "--script", scriptPath },
                FirstUserMessage = pointer,
            };

            await using var session = new ClaudeSession(options);
            await session.StartAsync(TestContext.Current.CancellationToken);

            // Drain events until the channel completes (process exit). Do NOT call SendUserMessageAsync.
            using var cts = new CancellationTokenSource(TestTimeout);
            try
            {
                while (await session.Events.WaitToReadAsync(cts.Token))
                {
                    while (session.Events.TryRead(out _)) { /* drain */ }
                }
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Session never completed.");
            }

            await session.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            File.Exists(stdinRecord).Should().BeTrue("FakeClaude should have written the recorded stdin line");
            var captured = await File.ReadAllTextAsync(stdinRecord, TestContext.Current.CancellationToken);
            // Each stream-json line on stdin is a JSON object whose `content` field is the user
            // message string. JSON-encoded inner quotes appear as \" so check for non-quoted
            // substrings of the pointer.
            captured.Should().Contain("Working on task 0001");
            captured.Should().Contain(".task/0001-test/");
            captured.Should().Contain("Use the infinicontext-task skill");
            captured.Should().Contain("\"type\":\"user\"", "the wire format must be a stream-json user envelope");
            // And it should be exactly one line.
            captured.TrimEnd('\r', '\n').Split('\n').Length.Should().Be(1, "exactly one user message expected");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best effort */ }
            try { File.Delete(stdinRecord); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task StdoutLoop_survives_Transitioned_subscriber_that_throws()
    {
        // Regression test for task 0008 root cause: a Transitioned subscriber on the state
        // machine ran synchronously inside _state.Apply (under the state lock, on the stdout-
        // loop reader thread). If that subscriber threw (e.g. SessionRowViewModel raising
        // PropertyChanged from the wrong thread and Avalonia's binding subscribers throwing
        // InvalidOperationException), the exception propagated out of _state.Apply, the
        // stdout-loop foreach terminated, and the loop entered its finally - which awaited
        // proc.WaitForExitAsync(). The operator saw "state X -> Y" logged, then nothing,
        // then a process-exit log when they finally closed the GUI minutes later.
        //
        // Fix in ClaudeSession.StdoutLoopAsync: wrap _state.Apply in try/catch (plus the same
        // around LogReceivedEvent, version check, and the channel writer). This test injects
        // a throwing Transitioned handler and asserts the full event sequence still drains
        // through the events channel.
        await using var session = new ClaudeSession(new ClaudeSessionOptions
        {
            Cwd = TestPaths.RepoRoot,
            ClaudeExecutable = FakeClaudeRunner.ExecutablePath,
            ExtraArgs = new[] { "--replay", TestPaths.Fixture("simple-prompt") },
        });

        var throwCount = 0;
        session.State.Transitioned += t =>
        {
            // Only throw on Apply-driven transitions (those carry an EventThatCaused - the
            // parsed ClaudeEvent that triggered the state change). Host-driven transitions
            // (BeginSend, Cancel, HandleProcessExit) have EventThatCaused == null; throwing on
            // those would surface back into SendUserMessageAsync / DisposeAsync where
            // production callers expect failures to propagate, which is unrelated to the
            // stdout-loop resilience we're testing here.
            if (t.EventThatCaused is null) return;
            Interlocked.Increment(ref throwCount);
            throw new InvalidOperationException($"simulated cross-thread binding failure #{throwCount}");
        };

        await session.StartAsync(TestContext.Current.CancellationToken);
        await session.SendUserMessageAsync("hi", TestContext.Current.CancellationToken);

        var eventTypes = new List<string>();
        using (var cts = new CancellationTokenSource(TestTimeout))
        {
            while (await session.Events.WaitToReadAsync(cts.Token))
            {
                while (session.Events.TryRead(out var ev))
                    eventTypes.Add(FormatType(ev));
            }
        }

        await session.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        // The full simple-prompt event trace must still flow even though every state transition
        // threw inside Apply. If the loop had terminated on the first throw (the original bug),
        // we'd only see system/hook_response (which fires before any state transition).
        eventTypes.Should().Equal(
            "system/hook_response",
            "system/init",
            "assistant",
            "rate_limit_event",
            "result/success");
        throwCount.Should().BeGreaterThan(1,
            "multiple state transitions occurred and each threw - the loop tolerated all of them.");
    }

    [Fact]
    public async Task ContextPressureMonitor_observes_result_and_injector_writes_soft_nudge_to_stdin()
    {
        // Phase 8: end-to-end wiring. The session feeds events to a real ContextPressureMonitor
        // via the eventObserver hook; when the monitor's SoftCrossed fires, a ThresholdInjector
        // sends a nudge through SendUserMessageAsync. FakeClaude records both stdin lines so we
        // can assert the second line is the nudge.
        var sessionId = "00000000-0000-0000-0000-000000000ddd";
        var stdinRecord = Path.Combine(Path.GetTempPath(), $"fakeclaude-pressure-stdin-{Guid.NewGuid():N}.txt");

        // 130k of 200k = 65% — above soft (60), below hard (85).
        var resultMsg = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["duration_ms"] = 1,
            ["num_turns"] = 1,
            ["result"] = "ok",
            ["stop_reason"] = "end_turn",
            ["session_id"] = sessionId,
            ["total_cost_usd"] = 0,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 50_000,
                ["cache_creation_input_tokens"] = 30_000,
                ["cache_read_input_tokens"] = 50_000,
                ["output_tokens"] = 100,
            },
            ["modelUsage"] = new JsonObject
            {
                ["claude-opus-4-7"] = new JsonObject
                {
                    ["contextWindow"] = 200_000,
                    ["inputTokens"] = 50_000,
                },
            },
        };

        // Task 0014: the monitor measures occupancy per API call from message.usage, so the
        // crossing is driven by this assistant event (50k + 30k + 50k = 130k = 65% of 200k). The
        // result above is emitted first only to seed the window via modelUsage; its summed usage
        // is intentionally ignored.
        var assistantMsg = new JsonObject
        {
            ["type"] = "assistant",
            ["session_id"] = sessionId,
            ["message"] = new JsonObject
            {
                ["model"] = "claude-opus-4-7",
                ["content"] = new JsonArray(),
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 50_000,
                    ["cache_creation_input_tokens"] = 30_000,
                    ["cache_read_input_tokens"] = 50_000,
                    ["output_tokens"] = 100,
                },
            },
        };

        var script = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject { ["type"] = "wait-stdin", ["record_to"] = stdinRecord, ["lines"] = 1 },
                new JsonObject { ["type"] = "emit-line", ["json"] = new JsonObject
                {
                    ["type"] = "system", ["subtype"] = "init",
                    ["session_id"] = sessionId, ["model"] = "claude-opus-4-7", ["cwd"] = "/tmp",
                    ["claude_code_version"] = "0.0.0",
                } },
                new JsonObject { ["type"] = "emit-line", ["json"] = resultMsg },
                new JsonObject { ["type"] = "emit-line", ["json"] = assistantMsg },
                // Now the host's monitor fires soft and the injector sends a nudge — capture it.
                new JsonObject { ["type"] = "wait-stdin", ["record_to"] = stdinRecord, ["lines"] = 1 },
                new JsonObject { ["type"] = "emit-line", ["json"] = new JsonObject
                {
                    ["type"] = "result", ["subtype"] = "success", ["is_error"] = false,
                    ["duration_ms"] = 1, ["num_turns"] = 2, ["result"] = "ok", ["stop_reason"] = "end_turn",
                    ["session_id"] = sessionId, ["total_cost_usd"] = 0,
                } },
                new JsonObject { ["type"] = "exit", ["code"] = 0 },
            },
        };

        var scriptPath = Path.Combine(Path.GetTempPath(), $"fakeclaude-pressure-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(scriptPath, script.ToJsonString(), Encoding.UTF8, TestContext.Current.CancellationToken);

        try
        {
            var monitor = new ContextPressureMonitor(softThresholdPct: 60, hardThresholdPct: 85);
            ClaudeSession? sessionRef = null;
            var injector = new ThresholdInjector(async (msg, ct) =>
            {
                if (sessionRef is not null) await sessionRef.SendUserMessageAsync(msg, ct);
            });
            monitor.SoftCrossed += c => _ = injector.OnSoftAsync(c);
            monitor.HardCrossed += c => _ = injector.OnHardAsync(c);

            var options = new ClaudeSessionOptions
            {
                Cwd = TestPaths.RepoRoot,
                ClaudeExecutable = FakeClaudeRunner.ExecutablePath,
                ExtraArgs = new[] { "--script", scriptPath },
            };

            await using var session = new ClaudeSession(options, eventObserver: monitor.OnEvent);
            sessionRef = session;
            await session.StartAsync(TestContext.Current.CancellationToken);
            await session.SendUserMessageAsync("hi", TestContext.Current.CancellationToken);

            using var cts = new CancellationTokenSource(TestTimeout);
            try
            {
                while (await session.Events.WaitToReadAsync(cts.Token))
                    while (session.Events.TryRead(out _)) { /* drain */ }
            }
            catch (OperationCanceledException) { throw new TimeoutException("Session never completed."); }

            await session.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            File.Exists(stdinRecord).Should().BeTrue("FakeClaude should have recorded both stdin lines");
            var captured = (await File.ReadAllTextAsync(stdinRecord, TestContext.Current.CancellationToken)).TrimEnd('\r', '\n').Split('\n');
            captured.Should().HaveCount(2, "first line = 'hi', second line = injected soft nudge");

            captured[0].Should().Contain("\"content\":\"hi\"");
            captured[1].Should().Contain("65%");
            captured[1].Should().Contain("flush progress");
            captured[1].Should().Contain("\"type\":\"user\"");

            monitor.Snapshot().SoftCrossed.Should().BeTrue();
            monitor.Snapshot().HardCrossed.Should().BeFalse();
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best effort */ }
            try { File.Delete(stdinRecord); } catch { /* best effort */ }
        }
    }

    private static async Task<RunResult> DriveSessionAsync(IReadOnlyList<string> extraArgs)
    {
        var options = new ClaudeSessionOptions
        {
            Cwd = TestPaths.RepoRoot,
            ClaudeExecutable = FakeClaudeRunner.ExecutablePath,
            ExtraArgs = extraArgs,
        };

        await using var session = new ClaudeSession(options);

        var transitions = new List<StateTransition>();
        var maxOpen = 0;
        var sawChildWithCorrelatedParent = false;
        var openIdsAtChange = new HashSet<string>();

        session.State.Transitioned += t => transitions.Add(t);
        session.State.ToolStackChanged += c =>
        {
            if (c.Kind == ToolStackChangeKind.Opened)
            {
                if (c.Slot.ParentToolUseId is { } parentId && openIdsAtChange.Contains(parentId))
                    sawChildWithCorrelatedParent = true;
                openIdsAtChange.Add(c.Slot.Id);
            }
            else
            {
                openIdsAtChange.Remove(c.Slot.Id);
            }
            maxOpen = Math.Max(maxOpen, openIdsAtChange.Count);
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await session.StartAsync();
        await session.SendUserMessageAsync("hi");

        var eventTypes = new List<string>();
        using (var cts = new CancellationTokenSource(TestTimeout))
        {
            try
            {
                while (await session.Events.WaitToReadAsync(cts.Token).ConfigureAwait(false))
                {
                    while (session.Events.TryRead(out var ev))
                    {
                        eventTypes.Add(FormatType(ev));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"FakeClaude session did not complete within {TestTimeout}. Last state: {session.State.State}.");
            }
        }

        await session.WaitForExitAsync().WaitAsync(TestTimeout);
        sw.Stop();

        // The state-machine HandleProcessExit fires inside the stdout-loop's finally block,
        // which runs concurrently with our Events drain — wait briefly for it to land.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (session.State.State != SessionState.Exited && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        // Process exit code is captured from the underlying Process; pulled out via reflection
        // on the state-machine reason string would be brittle, so instead read the most recent
        // Exited transition's reason.
        var exitReason = transitions.LastOrDefault(t => t.To == SessionState.Exited)?.Reason;
        var exitCode = ExtractExitCode(exitReason);

        return new RunResult(
            EventTypes: eventTypes,
            Transitions: transitions,
            FinalState: session.State.State,
            SessionId: session.SessionId,
            MaxOpenToolUses: maxOpen,
            SawChildWithCorrelatedParent: sawChildWithCorrelatedParent,
            UnknownEventCount: session.UnknownEventCount,
            MalformedLineCount: session.MalformedLineCount,
            UnknownTypeHistogram: session.UnknownTypeHistogram,
            Elapsed: sw.Elapsed,
            ProcessExitCode: exitCode);
    }

    private static string FormatType(ClaudeEvent ev)
        => ev.Subtype is { Length: > 0 } s ? $"{ev.Type}/{s}" : ev.Type;

    private static int? ExtractExitCode(string? reason)
    {
        // Reason format is "ProcessExit(exitCode=<n>)" or "ProcessExit(exitCode=<null>)".
        if (reason is null) return null;
        const string prefix = "ProcessExit(exitCode=";
        var i = reason.IndexOf(prefix, StringComparison.Ordinal);
        if (i < 0) return null;
        var tail = reason.AsSpan(i + prefix.Length);
        var end = tail.IndexOf(')');
        if (end < 0) return null;
        var value = tail[..end].ToString();
        return int.TryParse(value, out var n) ? n : null;
    }

    private sealed record RunResult(
        IReadOnlyList<string> EventTypes,
        IReadOnlyList<StateTransition> Transitions,
        SessionState FinalState,
        Guid? SessionId,
        int MaxOpenToolUses,
        bool SawChildWithCorrelatedParent,
        int UnknownEventCount,
        int MalformedLineCount,
        IReadOnlyDictionary<string, int> UnknownTypeHistogram,
        TimeSpan Elapsed,
        int? ProcessExitCode);
}
