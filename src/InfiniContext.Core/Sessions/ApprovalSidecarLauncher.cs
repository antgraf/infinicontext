using System.Text.Json.Nodes;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Mcp.Tasks;
using Serilog;

namespace InfiniContext.Core.Sessions;

/// <summary>
/// Owns the per-session approval-MCP plumbing: starts an <see cref="ApprovalChannel"/> on a
/// generated pipe, writes a temp <c>mcp-config.json</c> pointing the sidecar at it, and overlays
/// the resulting flags on the user's options. Disposing the launcher tears down the channel and
/// deletes the temp config.
///
/// Extracted out of <see cref="ClaudeSession"/> so the approval bootstrap is testable in
/// isolation and so <see cref="ClaudeSession"/> stays focused on process I/O.
///
/// <para>
/// Stage 2A Phase 7: when a <see cref="LockRegistry"/> is supplied, the same pipe carries the
/// task-lock messages and the launcher allowlists the two MCP lock tools so claude can dispatch
/// to them.
/// </para>
/// </summary>
internal sealed class ApprovalSidecarLauncher : IAsyncDisposable
{
    private readonly ILogger _logger;
    private ApprovalChannel? _channel;
    private string? _tempConfigPath;
    private int _disposed;

    public ApprovalSidecarLauncher(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Reserves <c>McpConfigPath</c>, <c>PermissionPromptTool</c>, and <c>StrictMcpConfig</c> on the
    /// returned options; merges the approval tool into <see cref="ClaudeSessionOptions.AllowedTools"/>.
    /// Existing user-supplied allowlist entries are preserved.
    /// </summary>
    public async Task<ClaudeSessionOptions> ConfigureAsync(
        ClaudeSessionOptions baseOptions,
        IApprovalSink sink,
        string? sidecarExecutable,
        IReadOnlyList<string>? sidecarArgs,
        LockRegistry? lockRegistry = null,
        TimeSpan? lockTtl = null,
        TimeSpan? lockMaxWait = null)
    {
        var pipeName = ApprovalChannel.GeneratePipeName();
        _channel = new ApprovalChannel(pipeName, sink, _logger,
            lockRegistry: lockRegistry,
            lockTtl: lockTtl,
            lockMaxWait: lockMaxWait);
        await _channel.StartAsync().ConfigureAwait(false);

        var sidecarExe = string.IsNullOrEmpty(sidecarExecutable)
            ? Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve sidecar executable.")
            : sidecarExecutable;

        var argsForConfig = new JsonArray();
        var prefix = sidecarArgs ?? new[] { "--mcp-approval-server" };
        foreach (var a in prefix) argsForConfig.Add(a);
        argsForConfig.Add(pipeName);

        var configJson = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ApprovalSidecarContract.McpServerKey] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = sidecarExe,
                    ["args"] = argsForConfig,
                },
            },
        };

        _tempConfigPath = Path.Combine(Path.GetTempPath(), $"infinicontext-mcp-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(_tempConfigPath, configJson.ToJsonString()).ConfigureAwait(false);
        }
        catch
        {
            // If writing the config failed, clear our handle so DisposeAsync doesn't try to delete a
            // nonexistent file (and so the next attempt can pick a fresh path).
            _tempConfigPath = null;
            throw;
        }
        _logger.Information("approval: pipe={Pipe} mcp_config={ConfigPath}", pipeName, _tempConfigPath);

        var allowed = new List<string> { ApprovalSidecarContract.FullyQualifiedToolName };
        if (lockRegistry is not null)
        {
            allowed.Add(ApprovalSidecarContract.FullyQualifiedLockAcquireToolName);
            allowed.Add(ApprovalSidecarContract.FullyQualifiedLockReleaseToolName);
        }
        if (baseOptions.AllowedTools is { Count: > 0 } existing)
        {
            foreach (var t in existing) if (!allowed.Contains(t)) allowed.Add(t);
        }

        return baseOptions with
        {
            McpConfigPath = _tempConfigPath,
            PermissionPromptTool = ApprovalSidecarContract.FullyQualifiedToolName,
            StrictMcpConfig = true,
            AllowedTools = allowed,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        if (_channel is not null)
        {
            try { await _channel.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        }
        if (_tempConfigPath is not null)
        {
            try { File.Delete(_tempConfigPath); } catch { /* best-effort */ }
        }
    }
}
