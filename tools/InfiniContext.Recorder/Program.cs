using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using Serilog.Core;
using Serilog.Events;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
InfiniContext.Recorder — Stage 0 NDJSON capture tool

Spawns `claude` in stream-json mode, sends scripted prompts on stdin,
captures raw NDJSON stdout to a fixture file. Does not parse — that's
InfiniContext.Core's job. The point is to record exactly what claude emits.

Usage:
  InfiniContext.Recorder --script <path>
                      [--scenario <name>]   default: script base name
                      [--cwd <dir>]         working dir for claude (default: cwd)
                      [--out <path>]        default: <repo>/tests/fixtures/<scenario>.jsonl
                      [--claude <path>]     override claude executable

Script JSON shape:
  {
    "prompts": ["What is 2+2?", "Now in binary."],
    "max_runtime_seconds": 120,         optional, default 120
    "claude_extra_args": ["--model", "claude-haiku-4-5-20251001"]   optional
  }
""");
    return 0;
}

string? scriptPath = null, scenario = null, cwd = null, outPath = null, claudeExe = null;
string approvalMode = "off"; // off | auto-allow | auto-deny
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--script": scriptPath = args[++i]; break;
        case "--scenario": scenario = args[++i]; break;
        case "--cwd": cwd = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--claude": claudeExe = args[++i]; break;
        case "--approval": approvalMode = args[++i]; break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}
if (scriptPath is null) { Console.Error.WriteLine("--script is required"); return 2; }

var script = JsonNode.Parse(File.ReadAllText(scriptPath))!;
var prompts = (script["prompts"]?.AsArray() ?? throw new InvalidOperationException("script.prompts missing"))
    .Select(n => n!.GetValue<string>()).ToList();
int maxRuntime = script["max_runtime_seconds"]?.GetValue<int>() ?? 120;
var extraArgs = (script["claude_extra_args"]?.AsArray()?.Select(n => n!.GetValue<string>()) ?? Array.Empty<string>()).ToList();

scenario ??= Path.GetFileNameWithoutExtension(scriptPath);
cwd ??= Directory.GetCurrentDirectory();
outPath ??= Path.Combine(FindRepoRoot(), "tests", "fixtures", $"{scenario}.jsonl");
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

if (!Directory.Exists(cwd))
{
    Console.Error.WriteLine($"[recorder] --cwd does not exist: {cwd}");
    Console.Error.WriteLine("           create it first, or pass an existing directory.");
    return 4;
}

// Use the host's single source of truth for argv + executable resolution.
var sessionOptions = new ClaudeSessionOptions
{
    Cwd = cwd,
    ClaudeExecutable = claudeExe,
    ExtraArgs = extraArgs,
};

ApprovalChannel? approvalChannel = null;
string? mcpConfigPath = null;
if (approvalMode is "auto-allow" or "auto-deny")
{
    var pipeName = ApprovalChannel.GeneratePipeName();
    IApprovalSink sink = approvalMode == "auto-allow"
        ? new AutoSink(req => ApprovalResponse.Allow(req.Input))
        : new AutoSink(_ => ApprovalResponse.Deny("recorder auto-deny"));
    approvalChannel = new ApprovalChannel(pipeName, sink);
    await approvalChannel.StartAsync();

    // The recorder is its own apphost — the sidecar must point at the CLI binary, not at us.
    var cliExe = ResolveCliExecutable();
    var configJson = new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            [ApprovalSidecarContract.McpServerKey] = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = cliExe,
                ["args"] = new JsonArray("--mcp-approval-server", pipeName),
            },
        },
    };
    mcpConfigPath = Path.Combine(Path.GetTempPath(), $"infinicontext-recorder-mcp-{Guid.NewGuid():N}.json");
    File.WriteAllText(mcpConfigPath, configJson.ToJsonString());

    sessionOptions = sessionOptions with
    {
        McpConfigPath = mcpConfigPath,
        PermissionPromptTool = ApprovalSidecarContract.FullyQualifiedToolName,
        StrictMcpConfig = true,
        AllowedTools = new[] { ApprovalSidecarContract.FullyQualifiedToolName },
    };
    Console.WriteLine($"[recorder] approval={approvalMode} pipe={pipeName} cli={cliExe}");
}

var resolvedExe = ClaudeArgvBuilder.ResolveExecutable(claudeExe);
var argv = ClaudeArgvBuilder.BuildArgs(sessionOptions);

var psi = new ProcessStartInfo
{
    FileName = resolvedExe,
    WorkingDirectory = cwd,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true,
};
foreach (var a in argv) psi.ArgumentList.Add(a);

Console.WriteLine($"[recorder] scenario : {scenario}");
Console.WriteLine($"[recorder] cwd      : {cwd}");
Console.WriteLine($"[recorder] out      : {outPath}");
Console.WriteLine($"[recorder] argv     : {resolvedExe} {string.Join(' ', psi.ArgumentList)}");

using var proc = new Process { StartInfo = psi };
if (!proc.Start()) { Console.Error.WriteLine("Failed to start claude"); return 3; }

var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var fixtureWriter = new StreamWriter(outPath, append: false, utf8NoBom) { AutoFlush = true };
using var stderrWriter = new StreamWriter(outPath + ".stderr.log", append: false, utf8NoBom) { AutoFlush = true };

var stdoutTask = Task.Run(async () =>
{
    string? line;
    while ((line = await proc.StandardOutput.ReadLineAsync()) is not null)
    {
        fixtureWriter.WriteLine(line);
        Console.WriteLine($"  rx: {(line.Length > 240 ? line[..240] + "…" : line)}");
    }
});
var stderrTask = Task.Run(async () =>
{
    string? line;
    while ((line = await proc.StandardError.ReadLineAsync()) is not null)
    {
        stderrWriter.WriteLine(line);
        Console.WriteLine($"  err: {line}");
    }
});

foreach (var prompt in prompts)
{
    var msg = JsonSerializer.Serialize(new
    {
        type = "user",
        message = new { role = "user", content = prompt }
    });
    await proc.StandardInput.WriteLineAsync(msg);
    await proc.StandardInput.FlushAsync();
    Console.WriteLine($"  tx: {msg}");
}
proc.StandardInput.Close();

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(maxRuntime));
try
{
    await proc.WaitForExitAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine($"[recorder] timeout after {maxRuntime}s; killing claude");
    try { proc.Kill(entireProcessTree: true); } catch { }
}
await Task.WhenAll(stdoutTask, stderrTask);

Console.WriteLine($"[recorder] exit={proc.ExitCode} fixture={outPath}");

if (approvalChannel is not null) { try { await approvalChannel.DisposeAsync(); } catch { } }
if (mcpConfigPath is not null) { try { File.Delete(mcpConfigPath); } catch { } }

return proc.ExitCode;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}

static string ResolveCliExecutable()
{
    var repoRoot = FindRepoRoot();
    var cliBase = Path.Combine(repoRoot, "src", "InfiniContext.Cli", "bin");
    var exeName = OperatingSystem.IsWindows() ? "InfiniContext.Cli.exe" : "InfiniContext.Cli";
    var matches = Directory.EnumerateFiles(cliBase, exeName, SearchOption.AllDirectories).ToList();
    if (matches.Count == 0)
        throw new InvalidOperationException(
            $"InfiniContext.Cli executable not found under {cliBase}. Run `dotnet build` first.");
    return matches.OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).First();
}

internal sealed class AutoSink(Func<ApprovalRequest, ApprovalResponse> handler) : IApprovalSink
{
    public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(handler(request));
}
