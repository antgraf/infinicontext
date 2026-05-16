// FakeClaude — minimal stand-in for the real `claude` CLI used by InfiniContext tests.
//
// Modes:
//   --replay <fixture>    Wait for one stdin line, then stream a recorded NDJSON fixture
//                         line-by-line (optionally paced with --delay-ms <int>).
//   --script <path>       Execute a JSON action script: wait-stdin / emit-line / emit-raw /
//                         emit-fixture / sleep-ms / exit. Used by Stage 1F adversarial tests.
//
// Unknown args — every claude flag the argv builder emits — are silently ignored. Test drivers
// pass ClaudeArgvBuilder's full argv list verbatim; FakeClaude just picks out the bits it needs.

using System.Text;
using System.Text.Json.Nodes;

string? fixturePath = null;
string? scriptPath = null;
var delayMs = 0;
var waitForStdin = true;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--replay" when i + 1 < args.Length: fixturePath = args[++i]; break;
        case "--script" when i + 1 < args.Length: scriptPath = args[++i]; break;
        case "--delay-ms" when i + 1 < args.Length: int.TryParse(args[++i], out delayMs); break;
        case "--no-wait-for-stdin": waitForStdin = false; break;
    }
}

// Env-var fallbacks for tests that exercise code paths whose argv shape they don't control —
// e.g., light-model services that build their own argv and want FakeClaude as a drop-in. The
// CLI flags still take precedence; env vars only kick in when neither --replay nor --script is set.
scriptPath ??= Environment.GetEnvironmentVariable("FAKE_CLAUDE_SCRIPT");
fixturePath ??= Environment.GetEnvironmentVariable("FAKE_CLAUDE_FIXTURE");

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

if (scriptPath is not null)
{
    return await RunScript(scriptPath, stdin);
}

if (fixturePath is null)
{
    await Console.Error.WriteLineAsync("FakeClaude: --replay <fixture> or --script <path> required");
    return 2;
}

if (!File.Exists(fixturePath))
{
    await Console.Error.WriteLineAsync($"FakeClaude: fixture not found: {fixturePath}");
    return 2;
}

if (waitForStdin)
{
    var first = await stdin.ReadLineAsync();
    if (first is null) return 0;
}

await StreamFixture(fixturePath, delayMs);
return 0;

static async Task StreamFixture(string path, int delayMs)
{
    await using var fs = File.OpenRead(path);
    using var sr = new StreamReader(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    string? line;
    while ((line = await sr.ReadLineAsync()) is not null)
    {
        await Console.Out.WriteLineAsync(line);
        await Console.Out.FlushAsync();
        if (delayMs > 0) await Task.Delay(delayMs);
    }
}

static async Task<int> RunScript(string scriptPath, StreamReader stdin)
{
    if (!File.Exists(scriptPath))
    {
        await Console.Error.WriteLineAsync($"FakeClaude: script not found: {scriptPath}");
        return 2;
    }

    var scriptDir = Path.GetDirectoryName(Path.GetFullPath(scriptPath))!;
    var json = JsonNode.Parse(await File.ReadAllTextAsync(scriptPath))
        ?? throw new InvalidOperationException("script JSON is null");
    var actions = json["actions"]?.AsArray()
        ?? throw new InvalidOperationException("script.actions missing");

    foreach (var action in actions)
    {
        if (action is null) continue;
        var type = (string?)action["type"]
            ?? throw new InvalidOperationException("action.type missing");

        switch (type)
        {
            case "wait-stdin":
                {
                    var lines = action["lines"]?.GetValue<int>() ?? 1;
                    var recordTo = (string?)action["record_to"];
                    StreamWriter? recorder = null;
                    if (recordTo is not null)
                    {
                        recorder = new StreamWriter(recordTo, append: true, Encoding.UTF8) { AutoFlush = true };
                    }
                    try
                    {
                        for (var i = 0; i < lines; i++)
                        {
                            var line = await stdin.ReadLineAsync();
                            if (line is null) return 0;
                            if (recorder is not null) await recorder.WriteLineAsync(line);
                        }
                    }
                    finally
                    {
                        recorder?.Dispose();
                    }
                    break;
                }
            case "emit-line":
                {
                    var node = action["json"]
                        ?? throw new InvalidOperationException("emit-line requires 'json'");
                    await Console.Out.WriteLineAsync(node.ToJsonString());
                    await Console.Out.FlushAsync();
                    break;
                }
            case "emit-raw":
                {
                    var bytes = (string?)action["bytes"]
                        ?? throw new InvalidOperationException("emit-raw requires 'bytes'");
                    await Console.Out.WriteAsync(bytes);
                    await Console.Out.FlushAsync();
                    break;
                }
            case "emit-fixture":
                {
                    var path = ResolveScriptPath(scriptDir, (string?)action["path"]
                        ?? throw new InvalidOperationException("emit-fixture requires 'path'"));
                    var perLineDelayMs = action["delay_ms"]?.GetValue<int>() ?? 0;
                    await StreamFixture(path, perLineDelayMs);
                    break;
                }
            case "sleep-ms":
                {
                    var ms = action["ms"]?.GetValue<int>() ?? 0;
                    if (ms > 0) await Task.Delay(ms);
                    break;
                }
            case "exit":
                {
                    var code = action["code"]?.GetValue<int>() ?? 0;
                    await Console.Out.FlushAsync();
                    return code;
                }
            default:
                throw new InvalidOperationException($"unknown action type: {type}");
        }
    }

    await Console.Out.FlushAsync();
    return 0;
}

static string ResolveScriptPath(string baseDir, string p)
    => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(baseDir, p));
