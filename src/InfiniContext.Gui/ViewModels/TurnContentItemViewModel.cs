using CommunityToolkit.Mvvm.ComponentModel;

namespace InfiniContext.Gui.ViewModels;

public enum TurnContentKind
{
    Thinking,
    ToolUse,
    ToolResult,
    System,
    UserPrompt,
    UnknownEvent,
    UnknownBlock,
}

/// <summary>
/// One collapsible row inside a <see cref="TurnNodeViewModel"/>. Each kind has a glyph and a
/// short summary line shown when collapsed (e.g. <c>"Read(file.cs)"</c>); the full payload
/// (full tool input, thinking text, tool result body) is in <see cref="Detail"/> and rendered
/// only when the operator expands the row.
/// </summary>
public sealed partial class TurnContentItemViewModel : ObservableObject
{
    public TurnContentKind Kind { get; }
    public string Glyph { get; }
    public string Summary { get; }

    /// <summary>The id of the originating tool-use block (used to correlate tool-result rows).
    /// Null for non-tool kinds.</summary>
    public string? ToolUseId { get; }

    [ObservableProperty]
    private string _detail;

    [ObservableProperty]
    private bool _isExpanded;

    public TurnContentItemViewModel(TurnContentKind kind, string summary, string detail, string? toolUseId = null)
    {
        Kind = kind;
        Glyph = GlyphFor(kind);
        Summary = summary;
        _detail = detail;
        ToolUseId = toolUseId;
    }

    public void AppendDetail(string addendum) => Detail = string.IsNullOrEmpty(Detail) ? addendum : Detail + "\n" + addendum;

    private static string GlyphFor(TurnContentKind kind) => kind switch
    {
        TurnContentKind.Thinking => "ЁЯТн",
        TurnContentKind.ToolUse => "ЁЯФз",
        TurnContentKind.ToolResult => "тЖй",
        TurnContentKind.System => "тУШ",
        TurnContentKind.UserPrompt => "ЁЯСд",
        TurnContentKind.UnknownEvent => "тЪа",
        TurnContentKind.UnknownBlock => "тЪа",
        _ => "┬╖",
    };
}
