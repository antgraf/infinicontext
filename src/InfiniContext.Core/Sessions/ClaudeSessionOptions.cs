namespace InfiniContext.Core.Sessions;

/// <summary>
/// Inputs for spawning a <c>claude</c> child process.
/// Consumed by <see cref="ClaudeArgvBuilder"/> and <see cref="ClaudeSession"/>.
/// </summary>
public sealed record ClaudeSessionOptions
{
    /// <summary>Working directory passed to the spawned process. Required.</summary>
    public required string Cwd { get; init; }

    /// <summary>Model alias (e.g. <c>claude-sonnet-4-6</c>). Null = let claude pick the default.</summary>
    public string? Model { get; init; }

    /// <summary>Resume a prior session by id. Null = fresh session.</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>Add <c>--include-partial-messages</c> for SSE-shaped <c>stream_event</c> records.</summary>
    public bool IncludePartialMessages { get; init; }

    /// <summary>Stage 1H: <c>--permission-prompt-tool &lt;name&gt;</c>.</summary>
    public string? PermissionPromptTool { get; init; }

    /// <summary>Stage 1H: <c>--mcp-config &lt;path&gt;</c>.</summary>
    public string? McpConfigPath { get; init; }

    /// <summary>Stage 1H: emit <c>--strict-mcp-config</c> so claude ignores any auto-discovered .mcp.json.</summary>
    public bool StrictMcpConfig { get; init; }

    /// <summary>
    /// Stage 1H: tools to allowlist via <c>--allowed-tools</c>. Comma-joined into one CLI value.
    /// When the approval flow is wired, this MUST contain the fully-qualified approval tool name
    /// (<c>mcp__infinicontext__approval_prompt</c>); otherwise claude won't dispatch to it.
    /// </summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }

    /// <summary>Free-form extra args appended verbatim. Useful for <c>--bare</c>, <c>--permission-mode</c>, etc.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();

    /// <summary>Override the resolved <c>claude</c> executable path. Null = auto-resolve.</summary>
    public string? ClaudeExecutable { get; init; }

    /// <summary>
    /// Path to the executable claude should spawn as the approval MCP server. Defaults to
    /// <see cref="Environment.ProcessPath"/> (re-launches the host with the sidecar mode flag).
    /// Tests can override this to point at a fixture binary.
    /// </summary>
    public string? ApprovalSidecarExecutable { get; init; }

    /// <summary>
    /// Args prepended in front of the generated pipe name when claude spawns the sidecar.
    /// Defaults to <c>["--mcp-approval-server"]</c>.
    /// </summary>
    public IReadOnlyList<string>? ApprovalSidecarArgs { get; init; }

    /// <summary>
    /// Stage 2A Phase 6: when non-null, the session writes this string to stdin as a stream-json
    /// <c>user</c> message immediately after observing the first <c>system/init</c> event.
    /// Used to pre-prompt task-bound sessions with a pointer into <c>.task/&lt;id&gt;-&lt;slug&gt;/</c>.
    /// </summary>
    public string? FirstUserMessage { get; init; }
}
