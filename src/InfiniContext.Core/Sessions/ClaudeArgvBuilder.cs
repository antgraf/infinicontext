namespace InfiniContext.Core.Sessions;

/// <summary>
/// Single source of truth for the <c>claude</c> argv list. Used by <see cref="ClaudeSession"/>
/// and the Stage 0 recorder so the host's view of "how to spawn claude" is identical to what
/// recorded fixtures came from.
///
/// Invariants the builder enforces:
/// <list type="bullet">
///   <item><c>--print</c> is always first (required when using <c>stream-json</c> formats).</item>
///   <item><c>--verbose</c> precedes <c>--output-format</c> (claude rejects the reverse).</item>
///   <item><c>--input-format stream-json</c> and <c>--output-format stream-json</c> are always emitted.</item>
/// </list>
/// </summary>
public static class ClaudeArgvBuilder
{
    public static IReadOnlyList<string> BuildArgs(ClaudeSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var args = new List<string>(16)
        {
            "--print",
            "--verbose",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
        };

        if (!string.IsNullOrEmpty(options.ResumeSessionId))
        {
            args.Add("--resume");
            args.Add(options.ResumeSessionId);
        }

        if (!string.IsNullOrEmpty(options.Model))
        {
            args.Add("--model");
            args.Add(options.Model);
        }

        if (options.IncludePartialMessages)
        {
            args.Add("--include-partial-messages");
        }

        if (!string.IsNullOrEmpty(options.PermissionPromptTool))
        {
            args.Add("--permission-prompt-tool");
            args.Add(options.PermissionPromptTool);
        }

        if (!string.IsNullOrEmpty(options.McpConfigPath))
        {
            args.Add("--mcp-config");
            args.Add(options.McpConfigPath);
        }

        if (options.StrictMcpConfig)
        {
            args.Add("--strict-mcp-config");
        }

        if (options.AllowedTools is { Count: > 0 } allowed)
        {
            args.Add("--allowed-tools");
            args.Add(string.Join(",", allowed));
        }

        if (options.ExtraArgs.Count > 0)
        {
            args.AddRange(options.ExtraArgs);
        }

        return args;
    }

    /// <summary>
    /// Resolves the <c>claude</c> executable path. Honors the <c>CLAUDE_EXE</c> env var,
    /// then the Windows npm-cmd shim, then falls back to the bare name on PATH.
    /// </summary>
    public static string ResolveExecutable(string? overridePath = null)
    {
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return overridePath;

        var env = Environment.GetEnvironmentVariable("CLAUDE_EXE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        if (OperatingSystem.IsWindows())
        {
            var npmCmd = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm", "claude.cmd");
            if (File.Exists(npmCmd)) return npmCmd;
        }

        return "claude";
    }
}
