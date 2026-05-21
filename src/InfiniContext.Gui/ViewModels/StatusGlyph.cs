using InfiniContext.Core.State;

namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// Maps session lifecycle state into a single-glyph badge for the tree view. Unicode-only so we
/// don't need an asset pipeline for the Phase 3B scaffold; a real icon font / SVG set lands in
/// a polish slice. Priority is "what needs the operator's attention first."
/// </summary>
internal static class StatusGlyph
{
    public const string AwaitingApproval = "тЪа";   // warning sign
    public const string Streaming = "тЦ╢";          // play (right-pointing triangle)
    public const string Sending = "тЖТ";            // rightwards arrow
    public const string Cancelling = "тЬХ";         // multiplication X
    public const string Compacting = "тЛп";         // midline horizontal ellipsis
    public const string Retrying = "тЖ╗";           // clockwise open circle arrow
    public const string Errored = "тЫФ";            // no entry
    public const string Idle = "тЧЛ";               // hollow circle
    public const string Exited = "┬╖";             // middle dot
    public const string Empty = "тЧж";              // white bullet (task with no sessions)

    /// <summary>Lower number = higher priority for aggregating into a parent task.</summary>
    public static int Priority(string glyph) => glyph switch
    {
        AwaitingApproval => 0,
        Streaming => 1,
        Sending => 2,
        Compacting => 3,
        Retrying => 4,
        Cancelling => 5,
        Errored => 6,
        Idle => 7,
        Exited => 8,
        _ => 100,
    };

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

    /// <summary>Aggregate of child session glyphs. Returns <see cref="Empty"/> when no sessions.</summary>
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
