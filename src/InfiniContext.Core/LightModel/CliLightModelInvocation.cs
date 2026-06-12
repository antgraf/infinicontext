using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.Supervision;
using Serilog;

namespace InfiniContext.Core.LightModel;

/// <summary>
/// Internals shared by <see cref="SingleShotCliLightModelService"/> and
/// <see cref="LongLivedCliLightModelService"/>: argv + env-var construction, managed-cwd
/// resolution, and the spawn / drive-one-turn / fence-strip helpers. The actual recipe lives at
/// <c>docs/research/claude-cli-light-model.md</c> §6 — this file is the C# transliteration.
/// </summary>
internal static class CliLightModelInvocation
{
    /// <summary>
    /// Argv list for <c>claude</c> in light-model "MAX configuration" mode (recipe §6).
    /// Diverges intentionally from <see cref="ClaudeArgvBuilder"/>: the session builder emits
    /// approval-sidecar / allowed-tools flags; the light-model recipe wants the opposite — zero
    /// MCP, zero tools, zero slash-commands.
    /// </summary>
    public static IReadOnlyList<string> BuildArgv(string modelId, string systemPrompt)
    {
        return new[]
        {
            "--print", "--verbose",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--model", modelId,
            "--system-prompt", systemPrompt,
            "--tools", "",
            "--strict-mcp-config",
            "--mcp-config", "{\"mcpServers\":{}}",
            "--setting-sources", "project",
            "--disable-slash-commands",
            "--no-session-persistence",
        };
    }

    /// <summary>
    /// Env-var overlay applied on top of the parent's environment. Five context-strippers (always
    /// on) plus thinking toggle + output-token cap. Per D8 the disable-thinking flag is what
    /// actually suppresses thinking at the API level — prompt-level instructions only got us a
    /// 40% reduction; the env var collapses output to ~37 tokens for a JSON-verdict task.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildEnv(CliLightModelOptions cli, bool thinking)
    {
        var maxTokens = thinking ? cli.ThinkingMaxOutputTokens : cli.MaxOutputTokens;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false",
            ["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1",
            ["CLAUDE_AGENT_SDK_DISABLE_BUILTIN_AGENTS"] = "1",
            ["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1",
            ["CLAUDE_CODE_DISABLE_POLICY_SKILLS"] = "1",
            ["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = maxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!thinking)
        {
            dict["CLAUDE_CODE_DISABLE_THINKING"] = "1";
        }
        return dict;
    }

    /// <summary>
    /// Resolve the managed empty cwd, creating it if absent. The recipe requires a known-empty
    /// directory (no <c>CLAUDE.md</c>, no <c>.mcp.json</c>) — using the host's cwd would defeat
    /// most of the context-stripping work.
    /// </summary>
    public static string ResolveManagedCwd(CliLightModelOptions cli)
    {
        var path = cli.ManagedCwd;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".infinicontext",
                "light-cwd");
        }
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Strip a leading <c>```json ... ```</c> markdown fence from the model's output, if present.
    /// The §6 recipe leaves this cosmetic wart — the model wraps its single-line JSON in a fence.
    /// We unwrap rather than tighten the prompt further (the prompt already explicitly forbids
    /// preamble; the fence is a separate model habit that survives even with thinking off).
    /// </summary>
    public static string StripJsonFence(string text)
    {
        var s = text.Trim();
        if (!s.StartsWith("```")) return s;

        var firstLineEnd = s.IndexOf('\n');
        if (firstLineEnd < 0) return s;

        var lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstLineEnd) return s;

        return s[(firstLineEnd + 1)..lastFence].Trim();
    }
}

/// <summary>
/// Owns one spawned <c>claude</c> child for the light-model recipe. Construction spawns;
/// disposal kills + drains. Both single-shot and long-lived services use this — single-shot
/// constructs/disposes per call, long-lived holds the same instance across calls.
///
/// <para>Wire layer: writes one stream-json <c>user</c> message per turn, reads stdout until
/// a <c>result</c> event terminates the turn, returns the concatenated text from the
/// <c>assistant</c> event's <c>message.content[].text</c> blocks.</para>
/// </summary>
internal sealed class CliLightModelChild : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_writeJson = new();

    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    public DateTimeOffset SpawnedAt { get; }

    private CliLightModelChild(Process process, ILogger logger, DateTimeOffset spawnedAt)
    {
        _process = process;
        _logger = logger;
        SpawnedAt = spawnedAt;
    }

    public bool HasExited => _process.HasExited;

    public static CliLightModelChild Spawn(
        LightModelOptions options,
        bool thinking,
        string systemPrompt,
        IProcessSupervisor? supervisor,
        ILogger logger,
        Func<DateTimeOffset>? clock = null)
    {
        var argv = CliLightModelInvocation.BuildArgv(options.Id, systemPrompt);
        var env = CliLightModelInvocation.BuildEnv(options.Cli, thinking);
        var cwd = CliLightModelInvocation.ResolveManagedCwd(options.Cli);
        var executable = ClaudeArgvBuilder.ResolveExecutable(options.Cli.ClaudeExecutable);

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        if (options.Cli.ExtraEnv is { } extra)
        {
            foreach (var (k, v) in extra) psi.Environment[k] = v;
        }

        logger.Debug(
            "cli-light-model: spawn exe={Executable} model={Model} thinking={Thinking} cwd={Cwd}",
            executable, options.Id, thinking, cwd);

        var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start claude executable '{psi.FileName}'.");

        if (supervisor is not null)
        {
            try { supervisor.Attach(proc); }
            catch
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { proc.Dispose(); } catch { }
                throw;
            }
        }

        // Discard stderr in the background so the buffer doesn't fill and stall the child.
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    logger.Debug("cli-light-model: stderr {Line}", line);
                }
            }
            catch { /* swallow — child going down */ }
        });

        var spawnedAt = (clock ?? (() => DateTimeOffset.UtcNow))();
        return new CliLightModelChild(proc, logger, spawnedAt);
    }

    /// <summary>
    /// Run one stream-json turn: write a user message, read stdout until the <c>result</c>
    /// event, return the concatenated assistant text. Throws on stream EOF before <c>result</c>,
    /// or on a <c>result</c> with <c>is_error=true</c>.
    /// </summary>
    public async Task<string> RunTurnAsync(string userPayload, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = userPayload },
        }, s_writeJson);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        var assistant = new StringBuilder();
        var sawAssistant = false;
        string? raw;
        while ((raw = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (TryConsumeTurnLine(raw, assistant, ref sawAssistant)) break; // result event reached
        }

        if (!sawAssistant)
            throw new InvalidOperationException("cli-light-model: stream ended before assistant text was produced");

        return assistant.ToString();
    }

    /// <summary>
    /// Consume one NDJSON line of a turn, appending any assistant <c>text</c> blocks to
    /// <paramref name="assistant"/>. Returns <c>true</c> when the terminating <c>result</c> event is
    /// reached (caller stops reading); throws when that result carries <c>is_error=true</c>.
    ///
    /// <para>Accumulates across <em>every</em> assistant event rather than capturing only the first.
    /// This matters with extended thinking on: <c>claude</c> emits the thinking as its own assistant
    /// event (<c>content:[{type:"thinking"}]</c>, no text block) <em>before</em> the answer event. The
    /// old "first assistant event wins" logic captured the thinking event, got an empty string from
    /// <see cref="ExtractAssistantText"/>, and then skipped the real answer - so every thinking-enabled
    /// caller (notably the <c>/task review</c> reviewer agents) returned empty output with no error,
    /// which surfaced as "(no output)" findings (task 0017). Non-thinking callers were unaffected
    /// because they emit a single text event. Breaking on <c>result</c> (not stdout EOF) is required so
    /// the long-lived reused child doesn't block waiting for the next turn.</para>
    /// </summary>
    internal static bool TryConsumeTurnLine(string raw, StringBuilder assistant, ref bool sawAssistant)
    {
        if (string.IsNullOrEmpty(raw)) return false;

        JsonNode? node;
        try { node = JsonNode.Parse(raw); }
        catch (JsonException) { return false; }
        if (node is not JsonObject obj) return false;

        var type = obj["type"]?.GetValue<string>();
        if (type == "assistant")
        {
            assistant.Append(ExtractAssistantText(obj));
            sawAssistant = true;
            return false;
        }
        if (type == "result")
        {
            var isError = obj["is_error"]?.GetValue<bool>() ?? false;
            if (isError)
            {
                var errText = obj["result"]?.ToString() ?? "<no detail>";
                throw new InvalidOperationException($"cli-light-model returned error: {Truncate(errText)}");
            }
            return true;
        }
        return false;
    }

    private static string ExtractAssistantText(JsonObject assistantEvent)
    {
        var content = assistantEvent["message"]?["content"];
        if (content is not JsonArray array) return string.Empty;

        var sb = new StringBuilder();
        foreach (var block in array)
        {
            if (block is JsonObject obj && obj["type"]?.GetValue<string>() == "text"
                && obj["text"]?.GetValue<string>() is { } txt)
            {
                sb.Append(txt);
            }
        }
        return sb.ToString();
    }

    private static string Truncate(string s) => s.Length <= 240 ? s : s[..240] + "…";

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // `--print` mode exits after one turn, so by the time we get here the child is usually
        // already on its way out. Closing stdin nudges it; a brief WaitForExit gives it the chance
        // to exit naturally before we resort to Kill. We avoid `entireProcessTree: true` — that
        // walks the OS process list (slow: ~16 s observed on Windows with AV hooks), and the
        // child has no descendants by construction (no MCP servers, no agents, no plugins).

        try { _process.StandardInput.Close(); } catch { }

        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        catch { /* timeout — fall through to kill */ }

        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: false); } catch { }
        }

        try { _process.Dispose(); } catch { }
        _writeLock.Dispose();
    }
}
