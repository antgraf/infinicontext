using System.Text.Json.Nodes;

namespace InfiniContext.Core.Events;

/// <summary>
/// Base type for every parsed claude stream-json event.
/// <see cref="Raw"/> preserves the entire original JSON object (unknown fields and all),
/// so the parser is tolerant to schema drift and round-trip is byte-equivalent.
/// </summary>
public abstract record ClaudeEvent
{
    public required string Type { get; init; }
    public string? Subtype { get; init; }
    public Guid? SessionId { get; init; }
    public Guid? Uuid { get; init; }
    public string? ParentToolUseId { get; init; }
    public required JsonObject Raw { get; init; }
}

public sealed record SystemInitEvent : ClaudeEvent
{
    public string? Cwd { get; init; }
    public string? Model { get; init; }
    public string? ClaudeCodeVersion { get; init; }
    public string? PermissionMode { get; init; }
}

public sealed record SystemHookResponseEvent : ClaudeEvent
{
    public string? HookName { get; init; }
    public string? HookEvent { get; init; }
    public string? Outcome { get; init; }
    public int? ExitCode { get; init; }
}

public sealed record SystemTaskStartedEvent : ClaudeEvent;
public sealed record SystemTaskProgressEvent : ClaudeEvent;
public sealed record SystemTaskNotificationEvent : ClaudeEvent;
public sealed record SystemStatusEvent : ClaudeEvent;
public sealed record SystemPluginInstallEvent : ClaudeEvent;
public sealed record SystemCompactEvent : ClaudeEvent;

public sealed record AssistantEvent : ClaudeEvent
{
    public required IReadOnlyList<ContentBlock> Content { get; init; }
}

public sealed record UserEvent : ClaudeEvent
{
    public required IReadOnlyList<ContentBlock> Content { get; init; }
}

public sealed record RateLimitEvent : ClaudeEvent;

public sealed record PartialMessageEvent : ClaudeEvent
{
    /// <summary>The Anthropic SSE inner event type, e.g. "content_block_delta".</summary>
    public string? InnerType { get; init; }
}

public sealed record ResultEvent : ClaudeEvent
{
    public bool? IsError { get; init; }
    public long? DurationMs { get; init; }
    public int? NumTurns { get; init; }
    public string? Result { get; init; }
    public string? StopReason { get; init; }
    public decimal? TotalCostUsd { get; init; }
    public string? TerminalReason { get; init; }
}

/// <summary>
/// Reply to a host-issued <c>control_request</c> (e.g. interrupt). Informational from the
/// state machine's perspective — the corresponding <c>result</c> event is what actually
/// returns the session to <c>Idle</c>.
/// </summary>
public sealed record ControlResponseEvent : ClaudeEvent
{
    public string? RequestId { get; init; }
    public string? Status { get; init; }
}

/// <summary>
/// Fallback for unrecognized top-level <c>type</c> or <c>system/&lt;subtype&gt;</c>.
/// Tracks the discriminator so the parser can keep a per-instance histogram.
/// </summary>
public sealed record UnknownEvent : ClaudeEvent
{
    /// <summary>The unrecognized discriminator: either the top-level <c>type</c>, or <c>system/&lt;subtype&gt;</c>.</summary>
    public required string Discriminator { get; init; }
}

public abstract record ContentBlock
{
    public required string Type { get; init; }
    public required JsonObject Raw { get; init; }
}

public sealed record TextBlock : ContentBlock
{
    public required string Text { get; init; }
}

public sealed record ThinkingBlock : ContentBlock
{
    public required string Thinking { get; init; }
}

public sealed record ToolUseBlock : ContentBlock
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public JsonNode? Input { get; init; }
}

public sealed record ToolResultBlock : ContentBlock
{
    public required string ToolUseId { get; init; }
    public JsonNode? Content { get; init; }
    public bool IsError { get; init; }
}

/// <summary>Fallback for an unrecognized content-block <c>type</c>.</summary>
public sealed record UnknownBlock : ContentBlock;
