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
/// One collapsible row inside a <see cref="TurnNodeViewModel"/>. Each kind has a glyph + a
/// short summary line; the full payload lives in <see cref="Detail"/> and is rendered only
/// when expanded. Glyphs come from <see cref="char.ConvertFromUtf32(int)"/> so the source
/// is pure ASCII (a Cyrillic-locale Windows tool round-trip would otherwise corrupt embedded
/// Unicode literals).
/// </summary>
public sealed partial class TurnContentItemViewModel : ObservableObject
{
    private static readonly string ThinkingGlyph = char.ConvertFromUtf32(0x1F4AD);   // THOUGHT BALLOON
    private static readonly string ToolUseGlyph = char.ConvertFromUtf32(0x1F527);    // WRENCH
    private static readonly string ToolResultGlyph = char.ConvertFromUtf32(0x21A9);  // LEFTWARDS ARROW WITH HOOK
    private static readonly string SystemGlyph = char.ConvertFromUtf32(0x24D8);      // CIRCLED LATIN SMALL LETTER I
    private static readonly string UserPromptGlyph = char.ConvertFromUtf32(0x1F464); // BUST IN SILHOUETTE
    private static readonly string WarningGlyph = char.ConvertFromUtf32(0x26A0);     // WARNING SIGN
    private static readonly string FallbackGlyph = char.ConvertFromUtf32(0x00B7);    // MIDDLE DOT

    public TurnContentKind Kind { get; }
    public string Glyph { get; }
    public string Summary { get; }
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
        TurnContentKind.Thinking => ThinkingGlyph,
        TurnContentKind.ToolUse => ToolUseGlyph,
        TurnContentKind.ToolResult => ToolResultGlyph,
        TurnContentKind.System => SystemGlyph,
        TurnContentKind.UserPrompt => UserPromptGlyph,
        TurnContentKind.UnknownEvent => WarningGlyph,
        TurnContentKind.UnknownBlock => WarningGlyph,
        _ => FallbackGlyph,
    };
}
