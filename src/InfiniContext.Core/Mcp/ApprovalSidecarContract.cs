namespace InfiniContext.Core.Mcp;

/// <summary>
/// Constants describing the approval-MCP sidecar wire contract. Lives in Core so the
/// argv builder, recorder, and tests can reference them without depending on the CLI binary
/// (which owns the actual sidecar entry point — see <c>InfiniContext.Cli.Mcp.ApprovalSidecarHost</c>).
/// </summary>
public static class ApprovalSidecarContract
{
    public const string ServerName = "infinicontext-approval";
    public const string ServerVersion = "0.1.0";
    public const string ToolName = "approval_prompt";
    public const string McpServerKey = "infinicontext";
    public static readonly string FullyQualifiedToolName = $"mcp__{McpServerKey}__{ToolName}";

    /// <summary>Stage 2A Phase 7: MCP tool name for acquiring a per-file lock on task artifacts.</summary>
    public const string LockAcquireToolName = "task_acquire_lock";
    public static readonly string FullyQualifiedLockAcquireToolName = $"mcp__{McpServerKey}__{LockAcquireToolName}";

    /// <summary>Stage 2A Phase 7: MCP tool name for releasing a previously acquired lock.</summary>
    public const string LockReleaseToolName = "task_release_lock";
    public static readonly string FullyQualifiedLockReleaseToolName = $"mcp__{McpServerKey}__{LockReleaseToolName}";

    /// <summary>Stage 3 bug A: MCP tool the agent calls to gracefully exit its session after a context-pressure flush.</summary>
    public const string SessionExitToolName = "session_exit";
    public static readonly string FullyQualifiedSessionExitToolName = $"mcp__{McpServerKey}__{SessionExitToolName}";
}
