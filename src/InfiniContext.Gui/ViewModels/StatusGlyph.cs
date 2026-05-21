using InfiniContext.Core.State;

namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// Maps session lifecycle state into a single-glyph badge for the tree view.
///
/// All glyph constants are built at runtime via char.ConvertFromUtf32 so the source file is
/// pure ASCII. A prior attempt with embedded Unicode literals (e.g. the WARNING SIGN char)
/// was being transcoded to CP1251 mojibake by the tool that wrote this file in a Cyrillic-
/// locale Windows environment. Hex codepoints + runtime conversion dodge the problem.
/// </summary>
internal static class StatusGlyph
{
    public static readonly string AwaitingApproval = char.ConvertFromUtf32(0x26A0);  // WARNING SIGN
    public static readonly string Streaming = char.ConvertFromUtf32(0x25B6);          // BLACK RIGHT-POINTING TRIANGLE
    public static readonly string Sending = char.ConvertFromUtf32(0x2192);            // RIGHTWARDS ARROW
    public static readonly string Cancelling = char.ConvertFromUtf32(0x2715);         // MULTIPLICATION X
    public static readonly string Compacting = char.ConvertFromUtf32(0x22EF);         // MIDLINE HORIZONTAL ELLIPSIS
    public static readonly string Retrying = char.ConvertFromUtf32(0x21BB);           // CLOCKWISE OPEN CIRCLE ARROW
    public static readonly string Errored = char.ConvertFromUtf32(0x26D4);            // NO ENTRY
    public static readonly string Idle = char.ConvertFromUtf32(0x25CB);               // WHITE CIRCLE
    public static readonly string Exited = char.ConvertFromUtf32(0x00B7);             // MIDDLE DOT
    public static readonly string Empty = char.ConvertFromUtf32(0x25E6);              // WHITE BULLET

    public static int Priority(string glyph)
    {
        if (glyph == AwaitingApproval) return 0;
        if (glyph == Streaming) return 1;
        if (glyph == Sending) return 2;
        if (glyph == Compacting) return 3;
        if (glyph == Retrying) return 4;
        if (glyph == Cancelling) return 5;
        if (glyph == Errored) return 6;
        if (glyph == Idle) return 7;
        if (glyph == Exited) return 8;
        return 100;
    }

    public static string ForSession(SessionState state, bool awaitingApproval)
    {
        if (awaitingApproval) return AwaitingApproval;
        return state switch
        {
            SessionState.Sending => Sending,
            SessionState.Streaming => Streaming,
            SessionState.Cancelling => Cancelling,
            SessionState.Compacting => Compacting,
            SessionState.Retrying => Retrying,
            SessionState.Errored => Errored,
            SessionState.Exited => Exited,
            _ => Idle,
        };
    }

    public static string Aggregate(IEnumerable<string> childGlyphs)
    {
        string? best = null;
        var bestPriority = int.MaxValue;
        foreach (var g in childGlyphs)
        {
            var p = Priority(g);
            if (p < bestPriority) { best = g; bestPriority = p; }
        }
        return best ?? Empty;
    }
}
