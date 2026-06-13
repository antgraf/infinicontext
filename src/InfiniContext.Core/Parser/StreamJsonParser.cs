using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfiniContext.Core.Events;

namespace InfiniContext.Core.Parser;

/// <summary>
/// Tolerant NDJSON parser for the <c>claude --output-format stream-json</c> stream.
///
/// Tolerance contract (design doc §4.2):
/// 1. Unknown top-level <c>type</c> → <see cref="UnknownEvent"/>; counter increments; never throws.
/// 2. Unknown content-block <c>type</c> inside <c>assistant</c>/<c>user</c> → <see cref="UnknownBlock"/>; counter increments.
/// 3. Unknown fields on a known event/block are preserved verbatim in <see cref="ClaudeEvent.Raw"/> and round-trip byte-equivalent.
/// 4. Malformed JSON lines and blank lines are counted and reported via <see cref="MalformedLine"/> but never throw.
/// </summary>
public sealed class StreamJsonParser
{
    private int _unknownEventCount;
    private int _malformedLineCount;
    // ConcurrentDictionary, not Dictionary: the stdout parse loop Bump()s this on a background
    // reader thread while /state (StateCommand) enumerates it from the command thread. A plain
    // Dictionary tears or throws InvalidOperationException exactly when an operator checks during
    // active drift — and the drift histogram is the documented load-bearing safety net (M12).
    private readonly ConcurrentDictionary<string, int> _unknownTypeHistogram = new(StringComparer.Ordinal);

    public int UnknownEventCount => _unknownEventCount;
    public int MalformedLineCount => _malformedLineCount;
    public IReadOnlyDictionary<string, int> UnknownTypeHistogram => _unknownTypeHistogram;

    /// <summary>Raised once per malformed or empty line. Never thrown — observers can log/alert.</summary>
    public event Action<MalformedLineWarning>? MalformedLine;

    public async IAsyncEnumerable<ClaudeEvent> ParseAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;

            // Strip a leading UTF-8 BOM (\uFEFF) if a future claude upgrade reintroduces one.
            if (line.Length > 0 && line[0] == '\uFEFF') line = line[1..];

            if (string.IsNullOrWhiteSpace(line))
            {
                _malformedLineCount++;
                MalformedLine?.Invoke(new MalformedLineWarning(lineNumber, line, "empty or whitespace-only line"));
                continue;
            }

            JsonObject? root;
            try
            {
                var node = JsonNode.Parse(line);
                root = node as JsonObject;
                if (root is null)
                {
                    _malformedLineCount++;
                    MalformedLine?.Invoke(new MalformedLineWarning(lineNumber, line, $"top-level JSON is not an object (got {node?.GetValueKind()})"));
                    continue;
                }
            }
            catch (JsonException ex)
            {
                _malformedLineCount++;
                MalformedLine?.Invoke(new MalformedLineWarning(lineNumber, line, ex.Message));
                continue;
            }

            yield return BuildEvent(root);
        }
    }

    private ClaudeEvent BuildEvent(JsonObject root)
    {
        var type = (string?)root["type"] ?? "";
        var subtype = (string?)root["subtype"];
        var sessionId = TryParseGuid(root["session_id"]);
        var uuid = TryParseGuid(root["uuid"]);
        var parentToolUseId = (string?)root["parent_tool_use_id"];

        var common = new EventCommon(type, subtype, sessionId, uuid, parentToolUseId, root);

        return type switch
        {
            "system" => BuildSystemEvent(common),
            "assistant" => BuildAssistantEvent(common),
            "user" => BuildUserEvent(common),
            "rate_limit_event" => new RateLimitEvent
            {
                Type = type,
                Subtype = subtype,
                SessionId = sessionId,
                Uuid = uuid,
                ParentToolUseId = parentToolUseId,
                Raw = root,
            },
            "stream_event" => new PartialMessageEvent
            {
                Type = type,
                Subtype = subtype,
                SessionId = sessionId,
                Uuid = uuid,
                ParentToolUseId = parentToolUseId,
                Raw = root,
                InnerType = (string?)root["event"]?["type"],
            },
            "result" => BuildResultEvent(common),
            "control_response" => new ControlResponseEvent
            {
                Type = type,
                Subtype = subtype,
                SessionId = sessionId,
                Uuid = uuid,
                ParentToolUseId = parentToolUseId,
                Raw = root,
                RequestId = (string?)root["request_id"],
                Status = (string?)root["response"]?["status"],
            },
            _ => RecordUnknown(common, discriminator: type),
        };
    }

    private ClaudeEvent BuildSystemEvent(EventCommon c)
    {
        var (type, subtype, sessionId, uuid, parentToolUseId, root) = c;

        return subtype switch
        {
            "init" => new SystemInitEvent
            {
                Type = type,
                Subtype = subtype,
                SessionId = sessionId,
                Uuid = uuid,
                ParentToolUseId = parentToolUseId,
                Raw = root,
                Cwd = (string?)root["cwd"],
                Model = (string?)root["model"],
                ClaudeCodeVersion = (string?)root["claude_code_version"],
                PermissionMode = (string?)root["permissionMode"],
            },
            "hook_response" => new SystemHookResponseEvent
            {
                Type = type,
                Subtype = subtype,
                SessionId = sessionId,
                Uuid = uuid,
                ParentToolUseId = parentToolUseId,
                Raw = root,
                HookName = (string?)root["hook_name"],
                HookEvent = (string?)root["hook_event"],
                Outcome = (string?)root["outcome"],
                ExitCode = (int?)root["exit_code"],
            },
            "task_started" => new SystemTaskStartedEvent { Type = type, Subtype = subtype, SessionId = sessionId, Uuid = uuid, ParentToolUseId = parentToolUseId, Raw = root },
            "task_progress" => new SystemTaskProgressEvent { Type = type, Subtype = subtype, SessionId = sessionId, Uuid = uuid, ParentToolUseId = parentToolUseId, Raw = root },
            "task_notification" => new SystemTaskNotificationEvent { Type = type, Subtype = subtype, SessionId = sessionId, Uuid = uuid, ParentToolUseId = parentToolUseId, Raw = root },
            "status" => new SystemStatusEvent { Type = type, Subtype = subtype, SessionId = sessionId, Uuid = uuid, ParentToolUseId = parentToolUseId, Raw = root },
            "plugin_install" => new SystemPluginInstallEvent { Type = type, Subtype = subtype, SessionId = sessionId, Uuid = uuid, ParentToolUseId = parentToolUseId, Raw = root },
            "compact" => new SystemCompactEvent { Type = type, Subtype = subtype, SessionId = sessionId, Uuid = uuid, ParentToolUseId = parentToolUseId, Raw = root },
            "thinking_tokens" => new SystemThinkingTokensEvent { Type = type, Subtype = subtype, SessionId = sessionId, Uuid = uuid, ParentToolUseId = parentToolUseId, Raw = root },
            _ => RecordUnknown(c, discriminator: $"system/{subtype ?? "<null>"}"),
        };
    }

    private AssistantEvent BuildAssistantEvent(EventCommon c)
    {
        var blocks = ParseContentBlocks(c.Raw["message"]?["content"] as JsonArray);
        return new AssistantEvent
        {
            Type = c.Type,
            Subtype = c.Subtype,
            SessionId = c.SessionId,
            Uuid = c.Uuid,
            ParentToolUseId = c.ParentToolUseId,
            Raw = c.Raw,
            Content = blocks,
        };
    }

    private UserEvent BuildUserEvent(EventCommon c)
    {
        var blocks = ParseContentBlocks(c.Raw["message"]?["content"] as JsonArray);
        return new UserEvent
        {
            Type = c.Type,
            Subtype = c.Subtype,
            SessionId = c.SessionId,
            Uuid = c.Uuid,
            ParentToolUseId = c.ParentToolUseId,
            Raw = c.Raw,
            Content = blocks,
        };
    }

    private static ResultEvent BuildResultEvent(EventCommon c)
    {
        var root = c.Raw;
        return new ResultEvent
        {
            Type = c.Type,
            Subtype = c.Subtype,
            SessionId = c.SessionId,
            Uuid = c.Uuid,
            ParentToolUseId = c.ParentToolUseId,
            Raw = root,
            IsError = (bool?)root["is_error"],
            DurationMs = (long?)root["duration_ms"],
            NumTurns = (int?)root["num_turns"],
            Result = (string?)root["result"],
            StopReason = (string?)root["stop_reason"],
            TotalCostUsd = (decimal?)root["total_cost_usd"],
            TerminalReason = (string?)root["terminal_reason"],
        };
    }

    private IReadOnlyList<ContentBlock> ParseContentBlocks(JsonArray? content)
    {
        if (content is null) return Array.Empty<ContentBlock>();

        var blocks = new List<ContentBlock>(content.Count);
        foreach (var item in content)
        {
            if (item is not JsonObject block)
            {
                // Spec violation: skip but don't throw. Counted via UnknownBlock fallback would be wrong here;
                // leave it out of the list but flag via the malformed-line counter? Closest we have is "unknown block".
                // Pragmatic call: emit an UnknownBlock with a synthetic raw object so behaviour is predictable.
                blocks.Add(new UnknownBlock { Type = "<non-object>", Raw = new JsonObject() });
                _unknownEventCount++;
                Bump("content-block:non-object");
                continue;
            }

            var blockType = (string?)block["type"] ?? "";
            blocks.Add(blockType switch
            {
                "text" => new TextBlock { Type = blockType, Raw = block, Text = (string?)block["text"] ?? "" },
                "thinking" => new ThinkingBlock { Type = blockType, Raw = block, Thinking = (string?)block["thinking"] ?? "" },
                "tool_use" => new ToolUseBlock
                {
                    Type = blockType,
                    Raw = block,
                    Id = (string?)block["id"] ?? "",
                    Name = (string?)block["name"] ?? "",
                    Input = block["input"],
                },
                "tool_result" => new ToolResultBlock
                {
                    Type = blockType,
                    Raw = block,
                    ToolUseId = (string?)block["tool_use_id"] ?? "",
                    Content = block["content"],
                    IsError = (bool?)block["is_error"] ?? false,
                },
                _ => RecordUnknownBlock(blockType, block),
            });
        }
        return blocks;
    }

    private UnknownBlock RecordUnknownBlock(string blockType, JsonObject raw)
    {
        _unknownEventCount++;
        Bump($"content-block:{blockType}");
        return new UnknownBlock { Type = blockType, Raw = raw };
    }

    private UnknownEvent RecordUnknown(EventCommon c, string discriminator)
    {
        _unknownEventCount++;
        Bump(discriminator);
        return new UnknownEvent
        {
            Type = c.Type,
            Subtype = c.Subtype,
            SessionId = c.SessionId,
            Uuid = c.Uuid,
            ParentToolUseId = c.ParentToolUseId,
            Raw = c.Raw,
            Discriminator = discriminator,
        };
    }

    private void Bump(string key)
        => _unknownTypeHistogram.AddOrUpdate(key, 1, static (_, count) => count + 1);

    private static Guid? TryParseGuid(JsonNode? node)
    {
        var s = (string?)node;
        return Guid.TryParse(s, out var g) ? g : null;
    }

    private readonly record struct EventCommon(
        string Type, string? Subtype, Guid? SessionId, Guid? Uuid, string? ParentToolUseId, JsonObject Raw);
}

public sealed record MalformedLineWarning(long LineNumber, string Line, string Reason);
