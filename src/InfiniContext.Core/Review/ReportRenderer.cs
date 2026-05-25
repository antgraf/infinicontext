using System.Text;

namespace InfiniContext.Core.Review;

/// <summary>
/// Renders a <see cref="ReviewReport"/> to the markdown artifact written under the task folder
/// as <c>review-&lt;timestamp&gt;.md</c>. The structure matches the design's "Report artifact"
/// section: header -> Applied -> Rejected -> Awaiting -> Raw reviewer outputs. Every Rejected
/// finding surfaces its <see cref="ConsolidatedFinding.RejectionReason"/> verbatim -- that's the
/// user's hard requirement.
/// </summary>
public static class ReportRenderer
{
    /// <summary>
    /// Render <paramref name="report"/> as the markdown body for the review artifact. Pure
    /// function; the CLI handler is responsible for writing the result under the lock contract.
    /// </summary>
    public static string Render(ReviewReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();

        // ---- Header ----
        sb.Append("# Review report - ").AppendLine(report.TaskTitle).AppendLine();
        sb.Append("**Date:** ").AppendLine(report.RanAt.ToString("yyyy-MM-ddTHH:mm:ssK"));
        sb.Append("**Task:** ").Append(report.TaskId.ToString("D4")).Append(" - ").AppendLine(report.TaskTitle);
        sb.AppendLine("**Reviewers:** completeness, architecture, code, tests, security");
        sb.Append("**Scope:** ").AppendLine(report.ScopeDescription);

        if (report.ExcludedFiles.Count == 0)
        {
            sb.AppendLine("**Excluded files:** none");
        }
        else
        {
            sb.AppendLine("**Excluded files:**");
            foreach (var entry in report.ExcludedFiles)
                sb.Append("  - ").AppendLine(entry);
        }

        var total = report.Applied.Count + report.Rejected.Count + report.Awaiting.Count;
        sb.Append("**Findings:** total=").Append(total)
            .Append(" (applied=").Append(report.Applied.Count)
            .Append(", rejected=").Append(report.Rejected.Count)
            .Append(", awaiting=").Append(report.Awaiting.Count)
            .AppendLine(")");

        sb.Append("**Build/test gate after auto-apply:** ");
        if (report.BuildTestGatePassed) sb.AppendLine("green");
        else sb.Append("reverted (").Append(report.GateReversionReason ?? "no detail").AppendLine(")");

        sb.AppendLine();

        // ---- Applied ----
        sb.AppendLine("## Applied (auto-applied by host)").AppendLine();
        if (report.Applied.Count == 0)
        {
            sb.AppendLine("_None._").AppendLine();
        }
        else
        {
            foreach (var f in OrderBySeverity(report.Applied))
                AppendAppliedFinding(sb, f);
        }

        // ---- Rejected ----
        sb.AppendLine("## Rejected (consolidator dismissed)").AppendLine();
        if (report.Rejected.Count == 0)
        {
            sb.AppendLine("_None._").AppendLine();
        }
        else
        {
            foreach (var f in OrderBySeverity(report.Rejected))
                AppendRejectedFinding(sb, f);
        }

        // ---- Awaiting decision ----
        sb.AppendLine("## Awaiting decision").AppendLine();
        if (report.Awaiting.Count == 0)
        {
            sb.AppendLine("_None._").AppendLine();
        }
        else
        {
            foreach (var f in OrderBySeverity(report.Awaiting))
                AppendAwaitingFinding(sb, f);
        }

        // ---- Raw reviewer outputs ----
        sb.AppendLine("## Raw reviewer outputs").AppendLine();
        if (report.RawReviewerOutputs.Count == 0)
        {
            sb.AppendLine("_None captured._").AppendLine();
        }
        else
        {
            foreach (var (name, text) in report.RawReviewerOutputs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                AppendRawOutput(sb, name, text);
        }

        return sb.ToString();
    }

    // ---------- Per-bucket finding renderers ----------

    private static void AppendAppliedFinding(StringBuilder sb, ConsolidatedFinding f)
    {
        AppendFindingHeader(sb, f);
        AppendReviewerList(sb, f);
        AppendLocation(sb, f);
        AppendIfPresent(sb, "Why", f.Why ?? f.Summary);
        AppendFixSummary(sb, f, label: "Fix applied");
        AppendConfidence(sb, f);
        if (!string.IsNullOrEmpty(f.ConsolidatorNote))
            sb.Append("- Coordinator note: ").AppendLine(f.ConsolidatorNote);
        sb.AppendLine();
    }

    private static void AppendRejectedFinding(StringBuilder sb, ConsolidatedFinding f)
    {
        AppendFindingHeader(sb, f);
        AppendReviewerList(sb, f);
        AppendLocation(sb, f);
        AppendIfPresent(sb, "What the reviewer flagged", f.Summary);
        // Hard requirement: every rejected finding surfaces an explicit reason verbatim. Even if
        // it sneaks through validation (shouldn't), we surface a clear "missing" marker so the
        // user can spot it.
        var reason = string.IsNullOrWhiteSpace(f.RejectionReason)
            ? "_(missing -- schema violation, should not happen)_"
            : f.RejectionReason;
        sb.Append("- **Rejection reason:** ").AppendLine(reason);
        AppendConfidence(sb, f, dispositionConfidenceLabel: "Coordinator confidence in rejection");
        if (!string.IsNullOrEmpty(f.ConsolidatorNote))
            sb.Append("- Coordinator note: ").AppendLine(f.ConsolidatorNote);
        sb.AppendLine();
    }

    private static void AppendAwaitingFinding(StringBuilder sb, ConsolidatedFinding f)
    {
        AppendFindingHeader(sb, f);
        AppendReviewerList(sb, f);
        AppendLocation(sb, f);
        AppendIfPresent(sb, "Why", f.Why ?? f.Summary);
        AppendFixSummary(sb, f, label: "Suggested fix");
        AppendConfidence(sb, f);
        if (!string.IsNullOrEmpty(f.ConsolidatorNote))
            sb.Append("- Coordinator note: ").AppendLine(f.ConsolidatorNote);
        sb.AppendLine();
    }

    // ---------- Field renderers ----------

    private static void AppendFindingHeader(StringBuilder sb, ConsolidatedFinding f)
    {
        sb.Append("### [").Append(SeverityLabel(f.Severity)).Append("] ").AppendLine(f.Title);
    }

    private static void AppendReviewerList(StringBuilder sb, ConsolidatedFinding f)
    {
        if (f.Reviewers.Count == 0)
        {
            sb.AppendLine("- Reviewers: _none_");
            return;
        }
        sb.Append("- Reviewers: ").AppendLine(string.Join(", ", f.Reviewers));
    }

    private static void AppendLocation(StringBuilder sb, ConsolidatedFinding f)
    {
        if (string.IsNullOrWhiteSpace(f.File))
        {
            sb.AppendLine("- Location: _file not specified_");
            return;
        }

        var path = f.File.Replace('\\', '/');
        var locationSuffix = (f.LineStart, f.LineEnd) switch
        {
            (int start, int end) when start == end => $":{start}",
            (int start, int end) => $":{start}-{end}",
            (int start, null) => $":{start}",
            _ => "",
        };
        sb.Append("- Location: `").Append(path).Append(locationSuffix).AppendLine("`");
    }

    private static void AppendFixSummary(StringBuilder sb, ConsolidatedFinding f, string label)
    {
        if (f.SuggestedFix is null)
        {
            sb.Append("- ").Append(label).AppendLine(": _none_");
            return;
        }

        var fix = f.SuggestedFix;
        sb.Append("- ").Append(label).Append(" (").Append(FixKindLabel(fix.Kind)).AppendLine("):");

        if (fix.Kind == FixKind.Manual)
        {
            sb.AppendLine("  _manual -- host cannot auto-apply_");
            return;
        }

        if (!string.IsNullOrEmpty(fix.OldText))
        {
            sb.AppendLine("  Before:").AppendLine("  ```")
                .AppendLine(IndentBlock(fix.OldText, "  "))
                .AppendLine("  ```");
        }

        if (!string.IsNullOrEmpty(fix.NewText))
        {
            sb.AppendLine("  After:").AppendLine("  ```")
                .AppendLine(IndentBlock(fix.NewText, "  "))
                .AppendLine("  ```");
        }
    }

    private static void AppendConfidence(
        StringBuilder sb,
        ConsolidatedFinding f,
        string dispositionConfidenceLabel = "Coordinator confidence")
    {
        sb.Append("- Reviewer confidence: ").AppendLine(f.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append("- ").Append(dispositionConfidenceLabel).Append(": ").AppendLine(f.DispositionConfidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AppendIfPresent(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append("- ").Append(label).Append(": ").AppendLine(value);
    }

    private static void AppendRawOutput(StringBuilder sb, string name, string text)
    {
        sb.AppendLine("<details>");
        sb.Append("<summary>").Append(name).AppendLine("</summary>").AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(string.IsNullOrEmpty(text) ? "(no output)" : text);
        sb.AppendLine("```").AppendLine();
        sb.AppendLine("</details>").AppendLine();
    }

    // ---------- Small utilities ----------

    private static string SeverityLabel(Severity s) => s switch
    {
        Severity.Critical => "critical",
        Severity.Warning => "warning",
        Severity.Suggestion => "suggestion",
        _ => s.ToString().ToLowerInvariant(),
    };

    private static string FixKindLabel(FixKind k) => k switch
    {
        FixKind.Edit => "edit",
        FixKind.ReplaceFile => "replace-file",
        FixKind.Manual => "manual",
        _ => k.ToString().ToLowerInvariant(),
    };

    private static IEnumerable<ConsolidatedFinding> OrderBySeverity(IEnumerable<ConsolidatedFinding> findings)
    {
        // Critical first, then Warning, then Suggestion. Within a severity, preserve source order.
        static int Rank(Severity s) => s switch
        {
            Severity.Critical => 0,
            Severity.Warning => 1,
            Severity.Suggestion => 2,
            _ => 3,
        };
        return findings.OrderBy(f => Rank(f.Severity));
    }

    private static string IndentBlock(string text, string prefix)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select(l => prefix + l));
    }
}
