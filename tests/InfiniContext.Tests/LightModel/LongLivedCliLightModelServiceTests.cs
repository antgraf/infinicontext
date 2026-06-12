using System.Text.Json.Nodes;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using FluentAssertions;
using Serilog;

namespace InfiniContext.Tests.LightModel;

/// <summary>
/// Tests for <see cref="LongLivedCliLightModelService"/>: multi-turn correctness against a single
/// long-lived FakeClaude child, and verification that <c>/clear</c> is sent after every real turn.
/// </summary>
public class LongLivedCliLightModelServiceTests
{
    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();
    private static readonly string ScriptsDir = Path.Combine(TestPaths.RepoRoot, "tests", "scripts");

    [Fact]
    public async Task Three_caller_calls_run_against_one_child_with_clear_in_between()
    {
        // FakeClaude script `light-model-multi-turn` services 6 wait-stdin/emit cycles: each
        // caller call triggers a real-verdict cycle followed by a /clear-confirmation cycle. If
        // the long-lived service didn't send /clear between calls, FakeClaude would emit "I have
        // cleared" in response to the second caller's user message, and the test would see
        // confusing reasons. Three correctly-shaped replies prove both that one child handled
        // every call (no respawn) and that /clear was correctly interleaved.
        await using var workCwd = new TempCwd();

        await using var service = new LongLivedCliLightModelService(
            options: BuildOptions(workCwd.Path, "light-model-multi-turn.json"),
            thinking: false,
            logger: Silent);

        var v1 = await service.ReviewToolCallSafetyAsync(NewReq("ls"), CancellationToken.None);
        var v2 = await service.ReviewToolCallSafetyAsync(NewReq("rm"), CancellationToken.None);
        var v3 = await service.ReviewToolCallSafetyAsync(NewReq("git"), CancellationToken.None);

        v1.Reason.Should().Be("turn 1");
        v2.Reason.Should().Be("turn 2");
        v3.Reason.Should().Be("turn 3");
    }

    [Fact]
    public async Task Sends_clear_message_after_each_real_turn()
    {
        // Generate a FakeClaude script at runtime so we can record stdin to a unique path and
        // then read it back to assert exactly which messages the service sent. Two FakeClaude
        // cycles (real + clear); test makes one caller call.
        await using var workCwd = new TempCwd();
        using var scriptFile = new TempScript(MakeRecordingScript(out var recordPath));

        await using var service = new LongLivedCliLightModelService(
            options: BuildOptions(workCwd.Path, scriptFile.Path),
            thinking: false,
            logger: Silent);

        await service.ReviewToolCallSafetyAsync(NewReq("ls"), CancellationToken.None);

        // Service may still be holding the child as it waits for cleanup; dispose to flush.
        await service.DisposeAsync();

        var recorded = await File.ReadAllLinesAsync(recordPath, TestContext.Current.CancellationToken);
        recorded.Length.Should().Be(2, "service writes one user message + one /clear per call");

        var firstMessage = ExtractUserText(recorded[0]);
        firstMessage.Should().Contain("\"command\":\"ls\"", "first stdin line is the safety-review user payload");

        var secondMessage = ExtractUserText(recorded[1]);
        secondMessage.Should().Be("/clear", "second stdin line must be the literal /clear command");
    }

    [Fact]
    public async Task DisposeAsync_is_idempotent()
    {
        await using var workCwd = new TempCwd();

        var service = new LongLivedCliLightModelService(
            options: BuildOptions(workCwd.Path, "light-model-safety-allow.json"),
            thinking: false,
            logger: Silent);

        // Dispose without ever calling — must not throw, and must not blow up on a second dispose.
        await service.DisposeAsync();
        await service.DisposeAsync();
    }

    private static SafetyReviewRequest NewReq(string cmd) =>
        new("Bash", new JsonObject { ["command"] = cmd });

    /// <summary>
    /// Pull the user-message <c>content</c> out of the stream-json envelope the service writes.
    /// Service writes lines like <c>{"type":"user","message":{"role":"user","content":"..."}}</c>.
    /// </summary>
    private static string ExtractUserText(string envelopeLine)
    {
        var node = JsonNode.Parse(envelopeLine) as JsonObject ?? throw new InvalidOperationException("non-object stdin line");
        return node["message"]?["content"]?.GetValue<string>()
            ?? throw new InvalidOperationException("user envelope missing message.content");
    }

    private static LightModelOptions BuildOptions(string managedCwd, string scriptName) => new()
    {
        Id = "claude-haiku-4-5",
        TimeoutSeconds = 15,
        Cli = new CliLightModelOptions
        {
            Mode = CliLightModelMode.LongLived,
            MaxOutputTokens = 128,
            ClaudeExecutable = FakeClaudeRunner.ExecutablePath,
            ManagedCwd = managedCwd,
            ExtraEnv = new Dictionary<string, string>
            {
                ["FAKE_CLAUDE_SCRIPT"] = Path.IsPathRooted(scriptName) ? scriptName : Path.Combine(ScriptsDir, scriptName),
            },
        },
    };

    private static string MakeRecordingScript(out string recordPath)
    {
        recordPath = Path.Combine(Path.GetTempPath(), $"cp-stdin-record-{Guid.NewGuid():N}.txt");
        // Two FakeClaude cycles. Each wait-stdin records the received line to recordPath so the
        // test can assert exactly what the service wrote. Emit blocks supply a real-verdict reply
        // + a /clear-confirmation reply respectively. The path placeholder is replaced with a
        // JSON-encoded string literal (handles Windows backslashes correctly).
        var pathLiteral = System.Text.Json.JsonSerializer.Serialize(recordPath);
        const string template = """
            {
              "actions": [
                {"type": "wait-stdin", "lines": 1, "record_to": __PATH__},
                {"type": "emit-line", "json": {"type":"system","subtype":"init","session_id":"00000000-0000-0000-0000-000000000030","model":"fake-haiku","cwd":"/tmp","claude_code_version":"0.0.0"}},
                {"type": "emit-line", "json": {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"{\"decision\":\"allow\",\"confidence\":\"high\",\"axis\":\"none\",\"reason\":\"ok\"}"}]},"session_id":"00000000-0000-0000-0000-000000000030"}},
                {"type": "emit-line", "json": {"type":"result","subtype":"success","is_error":false,"duration_ms":1,"num_turns":1,"result":"ok","stop_reason":"end_turn","session_id":"00000000-0000-0000-0000-000000000030","total_cost_usd":0.0007}},
                {"type": "wait-stdin", "lines": 1, "record_to": __PATH__},
                {"type": "emit-line", "json": {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I have cleared."}]},"session_id":"00000000-0000-0000-0000-000000000030"}},
                {"type": "emit-line", "json": {"type":"result","subtype":"success","is_error":false,"duration_ms":1,"num_turns":2,"result":"cleared","stop_reason":"end_turn","session_id":"00000000-0000-0000-0000-000000000030","total_cost_usd":0.0001}},
                {"type": "exit", "code": 0}
              ]
            }
            """;
        return template.Replace("__PATH__", pathLiteral);
    }

    private sealed class TempCwd : IAsyncDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "infinicontext-cli-light-" + Guid.NewGuid().ToString("N"));
        public TempCwd() => Directory.CreateDirectory(Path);
        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TempScript : IDisposable
    {
        public string Path { get; }
        public TempScript(string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cp-light-script-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, content);
        }
        public void Dispose() { try { File.Delete(Path); } catch { } }
    }
}
