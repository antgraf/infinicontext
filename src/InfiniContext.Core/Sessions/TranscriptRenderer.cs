using InfiniContext.Core.Events;
using InfiniContext.Core.State;

namespace InfiniContext.Core.Sessions;

/// <summary>
/// Stage 2B Phase 4a. Renders one <see cref="ClaudeEvent"/> or one <see cref="StateTransition"/>
/// into a list of transcript fragments — short strings that get appended to the per-session
/// <see cref="TranscriptModel"/> by <see cref="SessionTranscriptReader"/>. Centralizes what used
/// to live inline in the Repl renderer so Phase 4b's multi-pane focus echo can share it.
///
/// <para>
/// Pure function — no I/O. Returns plain strings; Phase 4b will upgrade to Spectre markup when
/// the multi-pane <c>Layout</c> needs styled rows.
/// </para>
/// </summary>
public static class TranscriptRenderer
{
    public static IReadOnlyList<string> Render(ClaudeEvent ev) => ev switch
    {
        AssistantEvent a => RenderAssistant(a),
        UserEvent u => RenderUser(u),
        ResultEvent r => RenderResult(r),
        UnknownEvent ue => new[] { $"\n[unknown_event: {ue.Discriminator}]\n" },
        _ => Array.Empty<string>(),
    };

    public static string RenderStateTransition(StateTransition t) =>
        $"\n[state: {t.From} -> {t.To}]\n";

    private static IReadOnlyList<string> RenderAssistant(AssistantEvent a)
    {
        var fragments = new List<string>(capacity: a.Content.Count);
        foreach (var b in a.Content)
        {
            switch (b)
            {
                case TextBlock tb:
                    fragments.Add(tb.Text);
                    break;
                case ToolUseBlock tu:
                    var inputSummary = tu.Input?.ToJsonString();
                    if (inputSummary is not null && inputSummary.Length > 200)
                        inputSummary = inputSummary[..200] + "…";
                    fragments.Add($"\n[tool: {tu.Name}({inputSummary ?? ""})]\n");
                    break;
                case UnknownBlock ub:
                    fragments.Add($"\n[unknown_block: {ub.Type}]\n");
                    break;
                // ThinkingBlock intentionally not rendered — already in the log.
            }
        }
        return fragments;
    }

    private static IReadOnlyList<string> RenderUser(UserEvent u)
    {
        var fragments = new List<string>(capacity: u.Content.Count);
        foreach (var b in u.Content)
        {
            switch (b)
            {
                case ToolResultBlock tr:
                    var status = tr.IsError ? "error" : "ok";
                    fragments.Add($"\n[tool-result: {status}]\n");
                    break;
                case UnknownBlock ub:
                    fragments.Add($"\n[unknown_block: {ub.Type}]\n");
                    break;
            }
        }
        return fragments;
    }

    private static IReadOnlyList<string> RenderResult(ResultEvent r)
    {
        var cost = r.TotalCostUsd is { } c ? $"${c:F4}" : "?";
        return new[] { $"\n[turn done: cost={cost} turns={r.NumTurns?.ToString() ?? "?"} reason={r.StopReason ?? "?"}]\n" };
    }
}
