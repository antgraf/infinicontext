using System.Text.Json.Nodes;
using InfiniContext.Core.Events;
using InfiniContext.Core.Parser;
using InfiniContext.Core.State;
using Serilog;

namespace InfiniContext.Core.Sessions;

/// <summary>
/// Subscribes to the parser's malformed-line warnings and the state machine's transitions/tool-stack
/// changes, plus exposes <see cref="LogReceivedEvent"/> for the stdout-loop to call per parsed event.
/// Owned by <see cref="ClaudeSession"/>; extracted so the noisy logging bundle is one focused unit
/// and so <see cref="ClaudeSession"/> stays focused on process I/O.
///
/// The session refreshes the underlying logger reference (with the bracketed session-id context)
/// after the first <c>system/init</c> event lands; <see cref="UpdateLogger"/> swaps it in atomically.
/// </summary>
internal sealed class SessionLogger
{
    private const int DefaultRxLogTruncationBytes = 2048;
    private volatile ILogger _logger;
    private readonly SessionStateMachine _state;
    private readonly int _rxLogTruncationBytes;

    public SessionLogger(ILogger logger, SessionStateMachine state, StreamJsonParser parser, int rxLogTruncationBytes = DefaultRxLogTruncationBytes)
    {
        _logger = logger;
        _state = state;
        _rxLogTruncationBytes = rxLogTruncationBytes;
        state.Transitioned += OnStateTransition;
        state.ToolStackChanged += OnToolStackChange;
        parser.MalformedLine += OnMalformedLine;
    }

    public void UpdateLogger(ILogger logger) => _logger = logger;

    public void LogReceivedEvent(ClaudeEvent ev)
    {
        var raw = ev.Raw.ToJsonString();
        if (raw.Length > _rxLogTruncationBytes)
        {
            raw = string.Concat(raw.AsSpan(0, _rxLogTruncationBytes), $"<… {raw.Length} chars elided>");
        }
        var label = ev.Subtype is { Length: > 0 } s ? $"{ev.Type}/{s}" : ev.Type;
        _logger.Debug("rx: {Label} {Raw}", label, raw);

        switch (ev)
        {
            case UnknownEvent ue:
                _logger.Warning("unknown_event type={Discriminator}; body preserved", ue.Discriminator);
                break;
            case AssistantEvent a when ContainsUnknownBlock(a.Content, out var bt):
                _logger.Warning("unknown_block type={BlockType}; body preserved", bt);
                break;
            case UserEvent u when ContainsUnknownBlock(u.Content, out var bt):
                _logger.Warning("unknown_block type={BlockType}; body preserved", bt);
                break;
        }
    }

    private static bool ContainsUnknownBlock(IReadOnlyList<ContentBlock> blocks, out string blockType)
    {
        foreach (var b in blocks)
        {
            if (b is UnknownBlock ub) { blockType = ub.Type; return true; }
        }
        blockType = "";
        return false;
    }

    private void OnStateTransition(StateTransition t) =>
        _logger.Information("state {From} -> {To} ({Reason})", t.From.ToString(), t.To.ToString(), t.Reason);

    private void OnToolStackChange(ToolStackChange c)
    {
        var sign = c.Kind switch
        {
            ToolStackChangeKind.Opened => "+",
            ToolStackChangeKind.Abandoned => "~", // force-drained without a tool_result (M10)
            _ => "-",
        };
        _logger.Information(
            "tool {Sign}{Name}#{Id} (parent={ParentId}, open={Open})",
            sign, c.Slot.Name, c.Slot.Id, c.Slot.ParentToolUseId ?? "<root>", _state.OpenToolUses.Count);
    }

    private void OnMalformedLine(MalformedLineWarning warn) =>
        _logger.Warning("malformed_line line={LineNumber} reason={Reason}", warn.LineNumber, warn.Reason);
}
