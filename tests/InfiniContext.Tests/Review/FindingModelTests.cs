using System.Text.Json;
using FluentAssertions;
using InfiniContext.Core.Review;

namespace InfiniContext.Tests.Review;

public class FindingModelTests
{
    [Fact]
    public void ReviewerOutput_round_trips_through_json()
    {
        var src = new ReviewerOutput(
            Reviewer: "code",
            Findings:
            [
                new Finding(
                    Id: "code-001",
                    Severity: Severity.Critical,
                    Title: "Null deref",
                    Summary: "Dereferences `cfg` before checking null.",
                    Why: "Crashes on cold start when settings.user.json is missing.",
                    File: "src/Foo.cs",
                    LineStart: 42,
                    LineEnd: 42,
                    SuggestedFix: new SuggestedFix(FixKind.Edit, "cfg.Foo", "cfg?.Foo"),
                    Confidence: 0.93),
            ],
            Notes: "ran cleanly");

        var json = JsonSerializer.Serialize(src, FindingJson.Options);
        var roundTrip = JsonSerializer.Deserialize<ReviewerOutput>(json, FindingJson.Options);

        roundTrip.Should().NotBeNull();
        roundTrip!.Reviewer.Should().Be("code");
        roundTrip.Findings.Should().HaveCount(1);
        roundTrip.Findings[0].Severity.Should().Be(Severity.Critical);
        roundTrip.Findings[0].SuggestedFix!.Kind.Should().Be(FixKind.Edit);
        roundTrip.Findings[0].Confidence.Should().Be(0.93);
    }

    [Fact]
    public void Severity_serializes_as_snake_case_lowercase()
    {
        var json = JsonSerializer.Serialize(Severity.Critical, FindingJson.Options);
        json.Should().Be("\"critical\"");

        var parsed = JsonSerializer.Deserialize<Severity>("\"warning\"", FindingJson.Options);
        parsed.Should().Be(Severity.Warning);
    }

    [Fact]
    public void Disposition_serializes_as_snake_case()
    {
        JsonSerializer.Serialize(Disposition.AutoApply, FindingJson.Options).Should().Be("\"auto_apply\"");
        JsonSerializer.Serialize(Disposition.AutoReject, FindingJson.Options).Should().Be("\"auto_reject\"");
        JsonSerializer.Serialize(Disposition.NeedsUser, FindingJson.Options).Should().Be("\"needs_user\"");

        JsonSerializer.Deserialize<Disposition>("\"auto_apply\"", FindingJson.Options).Should().Be(Disposition.AutoApply);
    }

    [Fact]
    public void Optional_fields_round_trip_as_null_when_omitted()
    {
        const string sparse = """
            {
              "reviewer": "tests",
              "findings": [
                { "id": "t1", "severity": "suggestion", "title": "x", "summary": "y", "confidence": 0.5 }
              ]
            }
            """;
        var parsed = JsonSerializer.Deserialize<ReviewerOutput>(sparse, FindingJson.Options);
        parsed.Should().NotBeNull();
        var f = parsed!.Findings[0];
        f.Why.Should().BeNull();
        f.File.Should().BeNull();
        f.LineStart.Should().BeNull();
        f.LineEnd.Should().BeNull();
        f.SuggestedFix.Should().BeNull();
        parsed.Notes.Should().BeNull();
    }

    [Fact]
    public void ExtractJson_pulls_payload_out_of_json_fence()
    {
        var wrapped = "Some preamble.\n```json\n{\"reviewer\":\"code\",\"findings\":[]}\n```\nAnd trailing prose.";
        var extracted = FindingJson.ExtractJson(wrapped);
        extracted.Should().Be("{\"reviewer\":\"code\",\"findings\":[]}");
    }

    [Fact]
    public void ExtractJson_pulls_payload_out_of_bare_fence_when_it_starts_with_brace()
    {
        var wrapped = "```\n{\"reviewer\":\"code\",\"findings\":[]}\n```";
        var extracted = FindingJson.ExtractJson(wrapped);
        extracted.Should().Be("{\"reviewer\":\"code\",\"findings\":[]}");
    }

    [Fact]
    public void ExtractJson_falls_back_to_first_brace_through_last_brace()
    {
        var wrapped = "Sure, here you go: {\"reviewer\":\"tests\",\"findings\":[]} that's the report.";
        var extracted = FindingJson.ExtractJson(wrapped);
        extracted.Should().Be("{\"reviewer\":\"tests\",\"findings\":[]}");
    }

    [Fact]
    public void ExtractJson_returns_null_when_no_braces()
    {
        FindingJson.ExtractJson("not a json document at all").Should().BeNull();
        FindingJson.ExtractJson("").Should().BeNull();
    }

    [Fact]
    public void TryParseReviewerOutput_returns_null_on_garbage()
    {
        FindingJson.TryParseReviewerOutput("not json").Should().BeNull();
        FindingJson.TryParseReviewerOutput("{ broken json").Should().BeNull();
    }

    [Fact]
    public void TryParseReviewerOutput_succeeds_through_prose_wrapping()
    {
        var wrapped = "Here is the report:\n```json\n{\"reviewer\":\"security\",\"findings\":[]}\n```";
        var parsed = FindingJson.TryParseReviewerOutput(wrapped);
        parsed.Should().NotBeNull();
        parsed!.Reviewer.Should().Be("security");
    }

    [Fact]
    public void Options_MaxDepth_is_bounded_against_pathological_nesting()
    {
        // sec-008: an LLM that emits deeply-nested arrays/objects should hit the parser's
        // MaxDepth cap rather than running unbounded. Build a payload with depth well beyond
        // MaxParseDepth and verify the deserializer refuses it.
        FindingJson.Options.MaxDepth.Should().Be(FindingJson.MaxParseDepth);

        var depth = FindingJson.MaxParseDepth + 16;
        var nested = new string('[', depth) + new string(']', depth);
        var act = () => JsonSerializer.Deserialize<System.Text.Json.JsonElement>(nested, FindingJson.Options);
        act.Should().Throw<JsonException>();
    }

    // ---------- Validator ----------

    [Fact]
    public void Validate_passes_for_well_formed_needs_user_finding()
    {
        var f = NewFinding(Disposition.NeedsUser);
        FindingValidator.Validate(f).Should().BeEmpty();
    }

    [Fact]
    public void Validate_requires_rejection_reason_when_disposition_is_auto_reject()
    {
        var f = NewFinding(Disposition.AutoReject, rejectionReason: "");
        FindingValidator.Validate(f).Should().Contain("RejectionReason is required");
    }

    [Fact]
    public void Validate_accepts_auto_reject_with_a_reason()
    {
        var f = NewFinding(Disposition.AutoReject, rejectionReason: "reviewer cited deprecated API that's still supported");
        FindingValidator.Validate(f).Should().BeEmpty();
    }

    [Fact]
    public void Validate_requires_suggested_fix_for_auto_apply()
    {
        var f = NewFinding(Disposition.AutoApply, omitFix: true);
        FindingValidator.Validate(f).Should().Contain("SuggestedFix is required");
    }

    [Fact]
    public void Validate_rejects_manual_fix_for_auto_apply()
    {
        var f = NewFinding(Disposition.AutoApply, fix: new SuggestedFix(FixKind.Manual, null, null));
        FindingValidator.Validate(f).Should().Contain("cannot AutoApply a Manual fix");
    }

    [Fact]
    public void Validate_rejects_edit_fix_with_empty_texts_for_auto_apply()
    {
        var f = NewFinding(Disposition.AutoApply, fix: new SuggestedFix(FixKind.Edit, "", "y"));
        FindingValidator.Validate(f).Should().Contain("non-empty OldText and NewText");
    }

    [Fact]
    public void Validate_requires_file_for_auto_apply()
    {
        var f = NewFinding(Disposition.AutoApply, file: null);
        FindingValidator.Validate(f).Should().Contain("File is required");
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Validate_rejects_out_of_range_confidence(double bad)
    {
        var f = NewFinding(Disposition.NeedsUser) with { Confidence = bad };
        FindingValidator.Validate(f).Should().Contain("Confidence must be in [0,1]");
    }

    [Fact]
    public void Validate_critical_severity_can_auto_apply_when_well_formed()
    {
        // The user's clarification: severity is orthogonal to disposition. Critical with high
        // confidence + concrete fix is a valid auto-apply.
        var f = NewFinding(Disposition.AutoApply) with { Severity = Severity.Critical, Confidence = 0.95 };
        FindingValidator.Validate(f).Should().BeEmpty();
    }

    [Fact]
    public void Validate_critical_severity_can_auto_reject_with_reason()
    {
        var f = NewFinding(Disposition.AutoReject, rejectionReason: "false positive: code path is dead-code-eliminated")
            with
        { Severity = Severity.Critical };
        FindingValidator.Validate(f).Should().BeEmpty();
    }

    [Fact]
    public void ValidateAll_reports_one_error_per_offending_finding()
    {
        var report = new ConsolidatedReport(
            Findings:
            [
                NewFinding(Disposition.NeedsUser),                                    // ok
                NewFinding(Disposition.AutoReject, rejectionReason: ""),              // bad
                NewFinding(Disposition.AutoApply, omitFix: true),                     // bad
            ],
            Summary: null);

        var errors = FindingValidator.ValidateAll(report);
        errors.Should().HaveCount(2);
    }

    private static ConsolidatedFinding NewFinding(
        Disposition disposition,
        SuggestedFix? fix = null,
        bool omitFix = false,
        string? file = "src/Foo.cs",
        string? rejectionReason = null)
    {
        // omitFix: true forces SuggestedFix to null even for AutoApply (to exercise the validator's
        // "fix is required" rule). Otherwise an AutoApply finding gets a sensible default Edit fix.
        var resolvedFix = omitFix
            ? null
            : fix ?? (disposition == Disposition.AutoApply
                ? new SuggestedFix(FixKind.Edit, "old", "new")
                : null);

        return new ConsolidatedFinding(
            Id: "x-001",
            Severity: Severity.Warning,
            Title: "title",
            Summary: "summary",
            Why: "why",
            File: file,
            LineStart: 1,
            LineEnd: 1,
            SuggestedFix: resolvedFix,
            Confidence: 0.9,
            Reviewers: ["code"],
            Disposition: disposition,
            DispositionConfidence: 0.9,
            RejectionReason: rejectionReason,
            ConsolidatorNote: null);
    }
}
