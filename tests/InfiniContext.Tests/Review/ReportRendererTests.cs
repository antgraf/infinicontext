using FluentAssertions;
using InfiniContext.Core.Review;

namespace InfiniContext.Tests.Review;

public class ReportRendererTests
{
    [Fact]
    public void Render_throws_on_null_report()
    {
        var act = () => ReportRenderer.Render(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Render_emits_header_with_task_id_padded_to_4_digits()
    {
        var report = EmptyReport() with { TaskId = 7, TaskTitle = "Test task" };
        var md = ReportRenderer.Render(report);
        md.Should().Contain("**Task:** 0007 - Test task");
    }

    [Fact]
    public void Render_emits_findings_count_summary()
    {
        var report = EmptyReport() with
        {
            Applied = new[] { F("a-1") },
            Rejected = new[] { F("r-1"), F("r-2") },
            Awaiting = new[] { F("w-1"), F("w-2"), F("w-3") },
        };

        var md = ReportRenderer.Render(report);

        md.Should().Contain("**Findings:** total=6 (applied=1, rejected=2, awaiting=3)");
    }

    [Fact]
    public void Render_marks_gate_green_when_passed()
    {
        var md = ReportRenderer.Render(EmptyReport() with { BuildTestGatePassed = true });
        md.Should().Contain("**Build/test gate after auto-apply:** green");
    }

    [Fact]
    public void Render_marks_gate_reverted_with_reason_when_failed()
    {
        var md = ReportRenderer.Render(EmptyReport() with
        {
            BuildTestGatePassed = false,
            GateReversionReason = "tests broke: 3 failures",
        });
        md.Should().Contain("**Build/test gate after auto-apply:** reverted (tests broke: 3 failures)");
    }

    [Fact]
    public void Render_lists_excluded_files_when_present()
    {
        var md = ReportRenderer.Render(EmptyReport() with
        {
            ExcludedFiles = new[] { "big.json (exceeds byte cap)", "ghost.cs (file does not exist)" },
        });
        md.Should().Contain("**Excluded files:**");
        md.Should().Contain("- big.json (exceeds byte cap)");
        md.Should().Contain("- ghost.cs (file does not exist)");
    }

    [Fact]
    public void Render_says_none_when_excluded_files_empty()
    {
        var md = ReportRenderer.Render(EmptyReport());
        md.Should().Contain("**Excluded files:** none");
    }

    [Fact]
    public void Render_includes_all_three_buckets_even_when_empty()
    {
        var md = ReportRenderer.Render(EmptyReport());
        md.Should().Contain("## Applied (auto-applied by host)");
        md.Should().Contain("## Rejected (consolidator dismissed)");
        md.Should().Contain("## Awaiting decision");
        md.Should().Contain("## Raw reviewer outputs");
    }

    [Fact]
    public void Render_says_none_in_buckets_that_are_empty()
    {
        var md = ReportRenderer.Render(EmptyReport() with
        {
            Applied = new[] { F("a-1") },
        });
        md.Should().Contain("_None._");
    }

    [Fact]
    public void Render_orders_findings_critical_then_warning_then_suggestion_within_a_bucket()
    {
        var report = EmptyReport() with
        {
            Awaiting = new[]
            {
                F("low-1") with { Severity = Severity.Suggestion, Title = "lowest" },
                F("hi-1") with { Severity = Severity.Critical, Title = "highest" },
                F("mid-1") with { Severity = Severity.Warning, Title = "middle" },
            },
        };

        var md = ReportRenderer.Render(report);
        var hiIdx = md.IndexOf("highest", StringComparison.Ordinal);
        var midIdx = md.IndexOf("middle", StringComparison.Ordinal);
        var lowIdx = md.IndexOf("lowest", StringComparison.Ordinal);

        hiIdx.Should().BeLessThan(midIdx);
        midIdx.Should().BeLessThan(lowIdx);
    }

    [Fact]
    public void Render_applied_shows_severity_label_title_and_fix_applied_block()
    {
        var report = EmptyReport() with
        {
            Applied = new[]
            {
                F("a-1") with
                {
                    Severity = Severity.Critical,
                    Title = "Null deref",
                    File = "src/Foo.cs",
                    LineStart = 42,
                    LineEnd = 42,
                    SuggestedFix = new SuggestedFix(FixKind.Edit, "cfg.Foo", "cfg?.Foo"),
                },
            },
        };

        var md = ReportRenderer.Render(report);

        md.Should().Contain("### [critical] Null deref");
        md.Should().Contain("- Location: `src/Foo.cs:42`");
        md.Should().Contain("- Fix applied (edit):");
        md.Should().Contain("cfg.Foo");
        md.Should().Contain("cfg?.Foo");
    }

    [Fact]
    public void Render_collapses_single_line_location_to_just_line_number()
    {
        var report = EmptyReport() with
        {
            Applied = new[]
            {
                F("a-1") with { File = "src/A.cs", LineStart = 10, LineEnd = 10 },
            },
        };
        var md = ReportRenderer.Render(report);
        md.Should().Contain("`src/A.cs:10`");
        md.Should().NotContain("src/A.cs:10-10");
    }

    [Fact]
    public void Render_uses_range_when_line_start_and_end_differ()
    {
        var report = EmptyReport() with
        {
            Applied = new[]
            {
                F("a-1") with { File = "src/A.cs", LineStart = 10, LineEnd = 15 },
            },
        };
        ReportRenderer.Render(report).Should().Contain("`src/A.cs:10-15`");
    }

    [Fact]
    public void Render_says_file_not_specified_when_file_is_null()
    {
        var report = EmptyReport() with
        {
            Awaiting = new[] { F("w-1") with { File = null } },
        };
        ReportRenderer.Render(report).Should().Contain("- Location: _file not specified_");
    }

    [Fact]
    public void Render_rejected_surfaces_rejection_reason_verbatim()
    {
        var reason = "false positive: code path is dead-code-eliminated by the JIT in Release builds";
        var report = EmptyReport() with
        {
            Rejected = new[]
            {
                F("r-1") with
                {
                    Disposition = Disposition.AutoReject,
                    RejectionReason = reason,
                    Title = "Spurious null check",
                },
            },
        };

        var md = ReportRenderer.Render(report);
        md.Should().Contain("### [warning] Spurious null check");
        md.Should().Contain("**Rejection reason:** " + reason);
    }

    [Fact]
    public void Render_rejected_marks_missing_rejection_reason_explicitly()
    {
        var report = EmptyReport() with
        {
            Rejected = new[]
            {
                F("r-1") with { Disposition = Disposition.AutoReject, RejectionReason = "" },
            },
        };

        var md = ReportRenderer.Render(report);
        md.Should().Contain("**Rejection reason:** _(missing");
    }

    [Fact]
    public void Render_rejected_uses_rejection_specific_confidence_label()
    {
        var report = EmptyReport() with
        {
            Rejected = new[]
            {
                F("r-1") with
                {
                    Disposition = Disposition.AutoReject,
                    RejectionReason = "x",
                    Confidence = 0.78,
                    DispositionConfidence = 0.93,
                },
            },
        };

        var md = ReportRenderer.Render(report);
        md.Should().Contain("- Reviewer confidence: 0.78");
        md.Should().Contain("- Coordinator confidence in rejection: 0.93");
    }

    [Fact]
    public void Render_awaiting_shows_suggested_fix_label()
    {
        var report = EmptyReport() with
        {
            Awaiting = new[]
            {
                F("w-1") with
                {
                    SuggestedFix = new SuggestedFix(FixKind.Edit, "old", "new"),
                },
            },
        };
        ReportRenderer.Render(report).Should().Contain("- Suggested fix (edit):");
    }

    [Fact]
    public void Render_awaiting_shows_manual_marker_for_manual_fixes()
    {
        // The renderer emits `_manual <em-dash> host cannot auto-apply_`. Asserting the two
        // anchors around the em-dash avoids tools-vs-source encoding fights with the literal.
        var report = EmptyReport() with
        {
            Awaiting = new[]
            {
                F("w-1") with
                {
                    SuggestedFix = new SuggestedFix(FixKind.Manual, null, null),
                },
            },
        };
        var rendered = ReportRenderer.Render(report);
        rendered.Should().Contain("_manual ").And.Contain(" host cannot auto-apply_");
    }

    [Fact]
    public void Render_awaiting_says_suggested_fix_none_when_null()
    {
        var report = EmptyReport() with
        {
            Awaiting = new[] { F("w-1") with { SuggestedFix = null } },
        };
        ReportRenderer.Render(report).Should().Contain("- Suggested fix: _none_");
    }

    [Fact]
    public void Render_awaiting_surfaces_summary_detail_when_it_differs_from_why()
    {
        // Task 0017: a synthesized reviewer-failure finding puts the real error in Summary while Why
        // is a generic explanation. The old `Why ?? Summary` collapse dropped Summary, so the report
        // couldn't say WHY a review came back empty. Both must now render.
        var report = EmptyReport() with
        {
            Awaiting = new[]
            {
                F("w-1") with
                {
                    Why = "The reviewer failed before emitting; see raw output.",
                    Summary = "Reviewer 'security' did not produce output: cli-light-model returned error: overloaded",
                },
            },
        };

        var md = ReportRenderer.Render(report);
        md.Should().Contain("- Why: The reviewer failed before emitting");
        md.Should().Contain("- Detail: Reviewer 'security' did not produce output: cli-light-model returned error: overloaded");
    }

    [Fact]
    public void Render_awaiting_does_not_duplicate_summary_when_it_equals_why()
    {
        var report = EmptyReport() with
        {
            Awaiting = new[] { F("w-1") with { Why = "same text", Summary = "same text" } },
        };

        var md = ReportRenderer.Render(report);
        md.Should().Contain("- Why: same text");
        md.Should().NotContain("- Detail:");
    }

    [Fact]
    public void Render_includes_coordinator_note_when_present()
    {
        var report = EmptyReport() with
        {
            Awaiting = new[]
            {
                F("w-1") with { ConsolidatorNote = "demoted: build broke after auto-apply" },
            },
        };
        ReportRenderer.Render(report).Should().Contain("- Coordinator note: demoted: build broke");
    }

    [Fact]
    public void Render_emits_raw_outputs_in_collapsible_details_blocks()
    {
        var report = EmptyReport() with
        {
            RawReviewerOutputs = new Dictionary<string, string>
            {
                ["Completeness"] = "{\"reviewer\":\"completeness\",\"findings\":[]}",
                ["Security"] = "raw text from security",
            },
        };

        var md = ReportRenderer.Render(report);
        md.Should().Contain("<details>");
        md.Should().Contain("<summary>Completeness</summary>");
        md.Should().Contain("<summary>Security</summary>");
        md.Should().Contain("</details>");
        md.Should().Contain("{\"reviewer\":\"completeness\"");
    }

    [Fact]
    public void Render_emits_no_output_marker_for_empty_raw_text()
    {
        var report = EmptyReport() with
        {
            RawReviewerOutputs = new Dictionary<string, string> { ["Coordinator"] = "" },
        };
        ReportRenderer.Render(report).Should().Contain("(no output)");
    }

    [Fact]
    public void Render_says_none_captured_when_raw_outputs_empty()
    {
        var md = ReportRenderer.Render(EmptyReport());
        md.Should().Contain("_None captured._");
    }

    [Fact]
    public void Render_lists_multiple_reviewers_comma_separated()
    {
        var report = EmptyReport() with
        {
            Awaiting = new[] { F("w-1") with { Reviewers = new[] { "code", "security" } } },
        };
        ReportRenderer.Render(report).Should().Contain("- Reviewers: code, security");
    }

    [Fact]
    public void Render_marks_empty_reviewer_list_explicitly()
    {
        var report = EmptyReport() with
        {
            Awaiting = new[] { F("w-1") with { Reviewers = Array.Empty<string>() } },
        };
        ReportRenderer.Render(report).Should().Contain("- Reviewers: _none_");
    }

    private static ReviewReport EmptyReport() => new(
        TaskId: 42,
        TaskTitle: "Test task",
        RanAt: new DateTimeOffset(2026, 5, 17, 12, 30, 0, TimeSpan.Zero),
        ScopeDescription: "git diff abcd..HEAD",
        ExcludedFiles: Array.Empty<string>(),
        Applied: Array.Empty<ConsolidatedFinding>(),
        Rejected: Array.Empty<ConsolidatedFinding>(),
        Awaiting: Array.Empty<ConsolidatedFinding>(),
        BuildTestGatePassed: true,
        GateReversionReason: null,
        RawReviewerOutputs: new Dictionary<string, string>());

    private static ConsolidatedFinding F(string id) => new(
        Id: id,
        Severity: Severity.Warning,
        Title: $"title-{id}",
        Summary: $"summary-{id}",
        Why: $"why-{id}",
        File: "src/Foo.cs",
        LineStart: 10,
        LineEnd: 10,
        SuggestedFix: null,
        Confidence: 0.85,
        Reviewers: new[] { "code" },
        Disposition: Disposition.NeedsUser,
        DispositionConfidence: 0.85,
        RejectionReason: null,
        ConsolidatorNote: null);
}
