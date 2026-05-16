using System.Text.Json.Nodes;

namespace InfiniContext.Core.LightModel;

/// <summary>
/// Host-side service for short, one-shot calls to a small/cheap Claude model (default: Haiku 4.5).
/// All methods are stateless single request/response — no chat history, no tool use, no streaming.
/// Implementations should hard-fail-fast on a missing API key and bubble cancellation through.
///
/// <para>
/// Use cases (Stage 1+):
/// <list type="bullet">
///   <item><see cref="GenerateShortTitleAsync"/> — derive a short label from free-form text.</item>
///   <item><see cref="SummarizeAsync"/> — collapse text to a few sentences.</item>
///   <item><see cref="CategorizeAsync"/> — pick one of N labels for an input.</item>
///   <item><see cref="ReviewToolCallSafetyAsync"/> — classify a tool-call's safety axis (destructive,
///   exfiltration, prompt-injection) and return a structured allow/deny decision. Used by the
///   <c>auto</c> approval mode.</item>
/// </list>
/// </para>
/// </summary>
public interface ILightModelService
{
    Task<string> GenerateShortTitleAsync(string description, int maxWords = 6, CancellationToken cancellationToken = default);

    Task<string> SummarizeAsync(string text, int maxSentences = 2, CancellationToken cancellationToken = default);

    Task<CategoryResult> CategorizeAsync(string text, IReadOnlyList<string> categories, CancellationToken cancellationToken = default);

    Task<SafetyReview> ReviewToolCallSafetyAsync(SafetyReviewRequest request, CancellationToken cancellationToken = default);
}

public sealed record CategoryResult(string Category, SafetyConfidence Confidence);

public sealed record SafetyReviewRequest(
    string ToolName,
    JsonObject Input,
    string? Cwd = null,
    string? AdditionalContext = null);

/// <summary>
/// Structured safety verdict on a single tool call. <see cref="Decision"/> is what the auto-approval
/// path should do; <see cref="Confidence"/> qualifies the verdict (callers may demote
/// low-confidence allows to a fallback prompt). <see cref="Axis"/> records *why* it was flagged
/// when denied — destructive, exfiltration, prompt-injection — so logs are post-mortem-friendly.
/// <see cref="Reason"/> is a short human-readable sentence; never null but may be empty.
/// </summary>
public sealed record SafetyReview(
    SafetyDecision Decision,
    SafetyConfidence Confidence,
    SafetyAxis Axis,
    string Reason);

public enum SafetyDecision { Allow, Deny }

public enum SafetyConfidence { Low, Medium, High }

public enum SafetyAxis
{
    /// <summary>No risk axis flagged; used on Allow decisions.</summary>
    None,
    /// <summary>Irreversible or wide-blast-radius action: rm -rf, DROP TABLE, force-push, etc.</summary>
    Destructive,
    /// <summary>Exfiltrates secrets, credentials, or sensitive data off the local machine.</summary>
    Exfiltration,
    /// <summary>Tool input contains text that reads like instructions trying to override the agent.</summary>
    PromptInjection,
    /// <summary>Other concern that doesn't fit the above three.</summary>
    Other,
}
