using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfiniContext.Core.Review;

/// <summary>
/// Severity of a single review finding. Picked by the reviewer, possibly overridden by the
/// coordinator. Severity is <b>orthogonal</b> to <see cref="Disposition"/>: a critical finding
/// can auto-apply at high confidence, and a critical finding can be auto-rejected with an
/// explicit reason. The gate is confidence and concrete-fix presence, not severity.
/// </summary>
public enum Severity { Critical, Warning, Suggestion }

/// <summary>
/// What the host decided to do with a finding after the coordinator pass.
/// </summary>
public enum Disposition
{
    /// <summary>Host applied the suggested fix and the build+test gate stayed green.</summary>
    AutoApply,
    /// <summary>Coordinator dismissed the reviewer. <see cref="ConsolidatedFinding.RejectionReason"/> is mandatory.</summary>
    AutoReject,
    /// <summary>Neither auto-applied nor auto-rejected. Lands in the user's queue.</summary>
    NeedsUser,
}

/// <summary>
/// How a <see cref="SuggestedFix"/> should be applied.
/// </summary>
public enum FixKind
{
    /// <summary>Replace <see cref="SuggestedFix.OldText"/> with <see cref="SuggestedFix.NewText"/> in the target file.</summary>
    Edit,
    /// <summary>Overwrite the entire target file with <see cref="SuggestedFix.NewText"/>.</summary>
    ReplaceFile,
    /// <summary>Fix needs human judgment тАФ host cannot auto-apply.</summary>
    Manual,
}

/// <summary>
/// A machine-applicable fix the reviewer attached to a finding. Only <see cref="FixKind.Edit"/>
/// and <see cref="FixKind.ReplaceFile"/> are eligible for auto-apply; <see cref="FixKind.Manual"/>
/// routes the finding to <see cref="Disposition.NeedsUser"/> regardless of confidence.
/// </summary>
public sealed record SuggestedFix(
    [property: JsonPropertyName("kind")] FixKind Kind,
    [property: JsonPropertyName("old_text")] string? OldText,
    [property: JsonPropertyName("new_text")] string? NewText);

/// <summary>
/// One reviewer-emitted finding. The reviewer fills every field it can; the host treats missing
/// optional fields as null. <see cref="Confidence"/> is the reviewer's self-rated confidence on
/// <c>[0, 1]</c>; the coordinator's own confidence in the finding is carried separately on
/// <see cref="ConsolidatedFinding.DispositionConfidence"/>.
/// </summary>
public sealed record Finding(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("severity")] Severity Severity,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("why")] string? Why,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("line_start")] int? LineStart,
    [property: JsonPropertyName("line_end")] int? LineEnd,
    [property: JsonPropertyName("suggested_fix")] SuggestedFix? SuggestedFix,
    [property: JsonPropertyName("confidence")] double Confidence);

/// <summary>
/// The JSON envelope each reviewer emits. <see cref="Reviewer"/> identifies which of the five
/// roles (completeness | architecture | code | tests | security) produced this output.
/// </summary>
public sealed record ReviewerOutput(
    [property: JsonPropertyName("reviewer")] string Reviewer,
    [property: JsonPropertyName("findings")] IReadOnlyList<Finding> Findings,
    [property: JsonPropertyName("notes")] string? Notes);

/// <summary>
/// A finding after the coordinator has consolidated the five reviewer reports. Carries the
/// original <see cref="Finding"/> fields plus the consolidator's decisions.
///
/// <para>
/// <b>Schema invariant:</b> when <see cref="Disposition"/> is <see cref="Disposition.AutoReject"/>,
/// <see cref="RejectionReason"/> MUST be non-empty. <see cref="Validate"/> enforces this.
/// </para>
/// </summary>
public sealed record ConsolidatedFinding(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("severity")] Severity Severity,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("why")] string? Why,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("line_start")] int? LineStart,
    [property: JsonPropertyName("line_end")] int? LineEnd,
    [property: JsonPropertyName("suggested_fix")] SuggestedFix? SuggestedFix,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reviewers")] IReadOnlyList<string> Reviewers,
    [property: JsonPropertyName("disposition")] Disposition Disposition,
    [property: JsonPropertyName("disposition_confidence")] double DispositionConfidence,
    [property: JsonPropertyName("rejection_reason")] string? RejectionReason,
    [property: JsonPropertyName("consolidator_note")] string? ConsolidatorNote);

/// <summary>
/// The coordinator agent's emitted document: consolidated findings + an overall summary.
/// </summary>
public sealed record ConsolidatedReport(
    [property: JsonPropertyName("findings")] IReadOnlyList<ConsolidatedFinding> Findings,
    [property: JsonPropertyName("summary")] string? Summary);

/// <summary>
/// Final host-side report after auto-apply / build-test gate. Partitions findings into the
/// three buckets that the user sees: applied, rejected, and awaiting decision.
/// </summary>
public sealed record ReviewReport(
    int TaskId,
    string TaskTitle,
    DateTimeOffset RanAt,
    string ScopeDescription,
    IReadOnlyList<string> ExcludedFiles,
    IReadOnlyList<ConsolidatedFinding> Applied,
    IReadOnlyList<ConsolidatedFinding> Rejected,
    IReadOnlyList<ConsolidatedFinding> Awaiting,
    bool BuildTestGatePassed,
    string? GateReversionReason,
    IReadOnlyDictionary<string, string> RawReviewerOutputs);

/// <summary>
/// JSON parsing helpers shared across the review subsystem. Parser is tolerant тАФ unknown enum
/// values fall back to safe defaults, missing optional fields stay null. The reviewer/coordinator
/// agents sometimes wrap their JSON in prose or fences; <see cref="ExtractJson"/> handles that.
/// </summary>
public static class FindingJson
{
    /// <summary>
    /// Shared JSON options: case-insensitive property matching, tolerant enum-as-string
    /// reader (camelCase or PascalCase both accepted), null values skipped on serialize.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false),
        },
        WriteIndented = false,
    };

    /// <summary>
    /// Same as <see cref="Options"/> but with <c>WriteIndented = true</c> for human-readable
    /// payloads (the raw-reviewer-output blocks in the final markdown report).
    /// </summary>
    public static readonly JsonSerializerOptions PrettyOptions = new(Options) { WriteIndented = true };

    /// <summary>
    /// Pull a JSON document out of an agent's free-form text response. Strategy:
    /// 1. Look for a <c>```json</c> fence; if found, return its contents.
    /// 2. Look for an unlabelled <c>```</c> fence with a leading <c>{</c>; if found, return.
    /// 3. Fall back to the substring from the first <c>{</c> to the matching closing <c>}</c>.
    /// 4. If none of the above yields a parseable object, return <c>null</c>.
    /// </summary>
    public static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var jsonFence = FindFenced(text, "```json");
        if (jsonFence is not null) return jsonFence;

        var bareFence = FindFenced(text, "```");
        if (bareFence is not null && bareFence.TrimStart().StartsWith('{')) return bareFence;

        var firstBrace = text.IndexOf('{');
        if (firstBrace < 0) return null;
        var lastBrace = text.LastIndexOf('}');
        if (lastBrace <= firstBrace) return null;
        return text.Substring(firstBrace, lastBrace - firstBrace + 1);
    }

    private static string? FindFenced(string text, string openMarker)
    {
        var start = text.IndexOf(openMarker, StringComparison.Ordinal);
        if (start < 0) return null;
        var contentStart = start + openMarker.Length;
        // Skip the newline (or any line-trailing whitespace) after the opening marker.
        while (contentStart < text.Length && (text[contentStart] == '\r' || text[contentStart] == '\n'))
            contentStart++;
        var end = text.IndexOf("```", contentStart, StringComparison.Ordinal);
        if (end < 0) return null;
        return text.Substring(contentStart, end - contentStart).Trim();
    }

    /// <summary>
    /// Parse a single reviewer's JSON output. Returns <c>null</c> when the input cannot be
    /// extracted or deserialized тАФ callers turn that into a <see cref="Disposition.NeedsUser"/>
    /// finding with the raw text preserved.
    /// </summary>
    public static ReviewerOutput? TryParseReviewerOutput(string agentText)
    {
        var json = ExtractJson(agentText);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<ReviewerOutput>(json, Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Parse the coordinator's JSON output. Returns <c>null</c> on extraction or deserialization
    /// failure; callers treat that as a fatal review error (the consolidator is non-optional).
    /// </summary>
    public static ConsolidatedReport? TryParseConsolidatedReport(string agentText)
    {
        var json = ExtractJson(agentText);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<ConsolidatedReport>(json, Options); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Schema validators for review payloads. Run before acting on a consolidator output so the
/// auto-apply executor can rely on the invariants documented on <see cref="ConsolidatedFinding"/>.
/// </summary>
public static class FindingValidator
{
    /// <summary>
    /// Validate a consolidated finding against the schema invariants. Returns the empty string
    /// when the finding is well-formed; otherwise a non-empty error message describing the first
    /// violation found.
    /// </summary>
    public static string Validate(ConsolidatedFinding f)
    {
        if (string.IsNullOrWhiteSpace(f.Id)) return "Finding.Id is required.";
        if (string.IsNullOrWhiteSpace(f.Title)) return $"Finding {f.Id}: Title is required.";
        if (f.Confidence < 0 || f.Confidence > 1) return $"Finding {f.Id}: Confidence must be in [0,1].";
        if (f.DispositionConfidence < 0 || f.DispositionConfidence > 1)
            return $"Finding {f.Id}: DispositionConfidence must be in [0,1].";

        if (f.Disposition == Disposition.AutoReject && string.IsNullOrWhiteSpace(f.RejectionReason))
            return $"Finding {f.Id}: RejectionReason is required when Disposition=AutoReject.";

        if (f.Disposition == Disposition.AutoApply)
        {
            if (f.SuggestedFix is null) return $"Finding {f.Id}: SuggestedFix is required when Disposition=AutoApply.";
            if (f.SuggestedFix.Kind == FixKind.Manual)
                return $"Finding {f.Id}: cannot AutoApply a Manual fix.";
            if (f.SuggestedFix.Kind == FixKind.Edit
                && (string.IsNullOrEmpty(f.SuggestedFix.OldText) || string.IsNullOrEmpty(f.SuggestedFix.NewText)))
                return $"Finding {f.Id}: Edit fix requires non-empty OldText and NewText.";
            if (f.SuggestedFix.Kind == FixKind.ReplaceFile && string.IsNullOrEmpty(f.SuggestedFix.NewText))
                return $"Finding {f.Id}: ReplaceFile fix requires non-empty NewText.";
            if (string.IsNullOrWhiteSpace(f.File))
                return $"Finding {f.Id}: File is required when Disposition=AutoApply.";
        }

        return string.Empty;
    }

    /// <summary>
    /// Validate every finding in a consolidated report. Returns the empty list when all are
    /// well-formed; otherwise one error message per offending finding.
    /// </summary>
    public static IReadOnlyList<string> ValidateAll(ConsolidatedReport report)
    {
        var errors = new List<string>();
        foreach (var f in report.Findings)
        {
            var err = Validate(f);
            if (!string.IsNullOrEmpty(err)) errors.Add(err);
        }
        return errors;
    }
}
