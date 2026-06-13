using System.Text;
using Serilog;

namespace InfiniContext.Core.Review;

/// <summary>
/// Orchestrates one <c>/task review</c> run: dispatches five concurrent reviewer agents,
/// runs the coordinator, hands <see cref="Disposition.AutoApply"/> findings to the
/// <see cref="IAutoApplyExecutor"/>, and partitions the result into the
/// <see cref="ReviewReport.Applied"/> / <see cref="ReviewReport.Rejected"/> /
/// <see cref="ReviewReport.Awaiting"/> buckets the user sees.
///
/// <para>
/// Data gathering -- loading task records, resolving scope, reading CLAUDE.md/decisions.md -- is
/// the CLI handler's job (step 9 of the design's code-touch plan). This service depends only on
/// what's passed in via <see cref="ReviewRequest"/>, so it can be unit-tested with a fake
/// <see cref="IReviewerAgentService"/> and no on-disk task scaffolding.
/// </para>
/// </summary>
public sealed class ReviewService
{
    private readonly IReviewerAgentService _reviewer;
    private readonly IReviewerAgentService _coordinator;
    private readonly IAutoApplyExecutor? _autoApply;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;

    /// <summary>
    /// Construct a review orchestrator with separate reviewer and coordinator channels.
    /// <para>
    /// The coordinator gets its own <see cref="IReviewerAgentService"/> so the host can drive
    /// it with a different model than the reviewers (per <c>ReviewSettings.CoordinatorModel</c>).
    /// When the two channels should use the same model, callers pass the same instance for
    /// both <paramref name="reviewer"/> and <paramref name="coordinator"/>.
    /// </para>
    /// </summary>
    public ReviewService(
        IReviewerAgentService reviewer,
        IReviewerAgentService coordinator,
        IAutoApplyExecutor? autoApply = null,
        Func<DateTimeOffset>? clock = null,
        ILogger? logger = null)
    {
        _reviewer = reviewer ?? throw new ArgumentNullException(nameof(reviewer));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _autoApply = autoApply;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _logger = logger ?? Log.ForContext<ReviewService>();
    }

    /// <summary>
    /// Convenience ctor: same agent service handles both reviewer and coordinator roles.
    /// Equivalent to passing <paramref name="reviewer"/> twice to the primary ctor.
    /// </summary>
    public ReviewService(
        IReviewerAgentService reviewer,
        IAutoApplyExecutor? autoApply = null,
        Func<DateTimeOffset>? clock = null,
        ILogger? logger = null)
        : this(reviewer, reviewer, autoApply, clock, logger)
    {
    }

    public async Task<ReviewReport> RunReviewAsync(ReviewRequest request, ReviewOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        if (request.TaskId <= 0) throw new ArgumentException("ReviewRequest.TaskId must be positive.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TaskTitle)) throw new ArgumentException("ReviewRequest.TaskTitle must be non-empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RepoRoot)) throw new ArgumentException("ReviewRequest.RepoRoot must be non-empty.", nameof(request));

        var ranAt = _clock();
        var slots = BuildSlots(request);

        // 1. Dispatch the five reviewer roles concurrently. Each call is wrapped so a single
        //    failure (parse failure, agent timeout, transport error) does not poison the others.
        var reviewerRoles = new[]
        {
            ReviewerRole.Completeness,
            ReviewerRole.Architecture,
            ReviewerRole.CodeLogic,
            ReviewerRole.Tests,
            ReviewerRole.Security,
        };

        var reviewerTasks = reviewerRoles
            .Select(role => RunSingleReviewerAsync(role, slots, ct))
            .ToArray();

        var reviewerResults = await Task.WhenAll(reviewerTasks).ConfigureAwait(false);

        var rawOutputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var parsedReviewerOutputs = new List<ReviewerOutput>();
        var syntheticFindings = new List<ConsolidatedFinding>();

        foreach (var rr in reviewerResults)
        {
            rawOutputs[rr.Role.ToString()] = rr.RawText ?? "";
            if (rr.Parsed is not null)
            {
                parsedReviewerOutputs.Add(rr.Parsed);
            }
            else
            {
                // Per the coordinator prompt's contract, unparseable / failed reviewer output
                // surfaces as a needs_user finding. We synthesize on the orchestrator side
                // (rather than letting the coordinator do it) because parse is deterministic
                // here and we don't want to depend on the coordinator inventing the structure.
                syntheticFindings.Add(SynthesizeReviewerFailureFinding(rr.Role, rr.Error, rr.RawText));
            }
        }

        // 2. Run the coordinator over the successfully-parsed reviewer outputs.
        IReadOnlyList<ConsolidatedFinding> consolidatedFindings;
        string? consolidatedSummary = null;
        string coordinatorRaw;
        if (parsedReviewerOutputs.Count == 0)
        {
            _logger.Warning("review: every reviewer failed; skipping coordinator and surfacing synthetic findings only");
            consolidatedFindings = Array.Empty<ConsolidatedFinding>();
            coordinatorRaw = "";
            rawOutputs[ReviewerRole.Coordinator.ToString()] = "";
        }
        else
        {
            var coordSlots = new Dictionary<string, string>(slots)
            {
                [ReviewerPrompts.SlotKeys.ReviewerOutputs] = SerializeReviewerOutputs(parsedReviewerOutputs),
            };

            var coordResult = await RunCoordinatorAsync(coordSlots, ct).ConfigureAwait(false);
            coordinatorRaw = coordResult.RawText ?? "";
            rawOutputs[ReviewerRole.Coordinator.ToString()] = coordinatorRaw;

            if (coordResult.Parsed is null)
            {
                _logger.Warning("review: coordinator output unparseable; demoting every reviewer finding to needs_user");
                consolidatedFindings = DemoteAllToNeedsUser(parsedReviewerOutputs, reason: "coordinator output was unparseable; cannot consolidate");
            }
            else
            {
                var validationErrors = FindingValidator.ValidateAll(coordResult.Parsed);
                if (validationErrors.Count > 0)
                {
                    _logger.Warning("review: coordinator output failed schema validation ({Count} errors); demoting auto_* to needs_user", validationErrors.Count);
                    consolidatedFindings = DemoteAutoToNeedsUser(coordResult.Parsed.Findings, $"coordinator output failed schema validation: {validationErrors[0]}");
                }
                else
                {
                    consolidatedFindings = coordResult.Parsed.Findings;
                    consolidatedSummary = coordResult.Parsed.Summary;
                }
            }
        }

        // 3. Run auto-apply (if enabled and executor present). Demote everything otherwise.
        IReadOnlyList<ConsolidatedFinding> appliedBucket = Array.Empty<ConsolidatedFinding>();
        var rejectedBucket = new List<ConsolidatedFinding>();
        var awaitingBucket = new List<ConsolidatedFinding>(syntheticFindings);
        var gatePassed = true;
        string? gateReversionReason = null;

        var autoApplyCandidates = consolidatedFindings.Where(f => f.Disposition == Disposition.AutoApply).ToList();
        var nonAutoApply = consolidatedFindings.Where(f => f.Disposition != Disposition.AutoApply).ToList();

        if (autoApplyCandidates.Count == 0)
        {
            // Nothing to apply; fall through.
        }
        else if (!options.AutoApplyEnabled)
        {
            foreach (var f in autoApplyCandidates)
                awaitingBucket.Add(WithDemotion(f, "auto-apply disabled by review options"));
        }
        else if (_autoApply is null)
        {
            foreach (var f in autoApplyCandidates)
                awaitingBucket.Add(WithDemotion(f, "no auto-apply executor configured"));
        }
        else
        {
            // C4: constrain auto-apply writes to the files this review actually covered. Anything
            // the coordinator points at outside that set (or in the .git/CI/build-file denylist) is
            // demoted to the user rather than written and then executed by the build/test gate.
            var allowedPaths = request.Scope.Files.Select(f => f.RelativePath).ToList();
            var executorResult = await _autoApply.ApplyAsync(request.RepoRoot, autoApplyCandidates, ct, allowedPaths).ConfigureAwait(false);
            appliedBucket = executorResult.Applied;
            foreach (var f in executorResult.Demoted) awaitingBucket.Add(f);
            gatePassed = executorResult.BuildTestGatePassed;
            gateReversionReason = executorResult.GateReversionReason;
        }

        // 4. Partition the non-auto-apply findings into rejected vs awaiting.
        foreach (var f in nonAutoApply)
        {
            if (f.Disposition == Disposition.AutoReject) rejectedBucket.Add(f);
            else awaitingBucket.Add(f);
        }

        return new ReviewReport(
            TaskId: request.TaskId,
            TaskTitle: request.TaskTitle,
            RanAt: ranAt,
            ScopeDescription: BuildScopeDescription(request.Scope, consolidatedSummary),
            ExcludedFiles: request.Scope.ExcludedFiles.Select(e => $"{e.RelativePath} ({e.Reason})").ToList(),
            Applied: appliedBucket,
            Rejected: rejectedBucket,
            Awaiting: awaitingBucket,
            BuildTestGatePassed: gatePassed,
            GateReversionReason: gateReversionReason,
            RawReviewerOutputs: rawOutputs);
    }

    // ---------- Dispatch helpers ----------

    private async Task<ReviewerCallResult> RunSingleReviewerAsync(ReviewerRole role, IReadOnlyDictionary<string, string> slots, CancellationToken ct)
    {
        try
        {
            var system = ReviewerPrompts.SystemFor(role);
            var user = ReviewerPrompts.Fill(ReviewerPrompts.UserTemplateFor(role), slots);
            var text = await _reviewer.RunOneShotAsync(system, user, ct).ConfigureAwait(false);
            var parsed = FindingJson.TryParseReviewerOutput(text);
            return new ReviewerCallResult(role, parsed, parsed is null ? "parse failure: agent did not emit valid JSON" : null, text);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "review: reviewer {Role} failed", role);
            return new ReviewerCallResult(role, null, $"{ex.GetType().Name}: {ex.Message}", null);
        }
    }

    private async Task<CoordinatorCallResult> RunCoordinatorAsync(IReadOnlyDictionary<string, string> slots, CancellationToken ct)
    {
        try
        {
            var system = ReviewerPrompts.SystemFor(ReviewerRole.Coordinator);
            var user = ReviewerPrompts.Fill(ReviewerPrompts.UserTemplateFor(ReviewerRole.Coordinator), slots);
            // Coordinator goes through its own channel so ReviewSettings.CoordinatorModel can
            // drive a different model than the reviewers (e.g. a stronger reasoning model for
            // dedup/disposition while reviewers run on a cheaper tier).
            var text = await _coordinator.RunOneShotAsync(system, user, ct).ConfigureAwait(false);
            var parsed = FindingJson.TryParseConsolidatedReport(text);
            return new CoordinatorCallResult(parsed, text);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "review: coordinator failed");
            return new CoordinatorCallResult(null, null);
        }
    }

    // ---------- Slot + transform helpers ----------

    private static IReadOnlyDictionary<string, string> BuildSlots(ReviewRequest req)
    {
        var (fileExcerpts, testExcerpts, csprojDiffs) = SplitExcerpts(req.Scope);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ReviewerPrompts.SlotKeys.TaskId] = req.TaskId.ToString("D4"),
            [ReviewerPrompts.SlotKeys.TaskTitle] = req.TaskTitle,
            [ReviewerPrompts.SlotKeys.ScopeDescription] = req.Scope.Description,
            [ReviewerPrompts.SlotKeys.DescriptionMd] = req.DescriptionMd,
            [ReviewerPrompts.SlotKeys.ProgressMd] = req.ProgressMd,
            [ReviewerPrompts.SlotKeys.IndexMd] = req.IndexMd,
            [ReviewerPrompts.SlotKeys.ClaudeMd] = req.ClaudeMd,
            [ReviewerPrompts.SlotKeys.DecisionsMd] = req.DecisionsMd,
            [ReviewerPrompts.SlotKeys.DiffSummary] = BuildDiffSummary(req.Scope),
            [ReviewerPrompts.SlotKeys.FileExcerpts] = fileExcerpts,
            [ReviewerPrompts.SlotKeys.TestExcerpts] = testExcerpts,
            [ReviewerPrompts.SlotKeys.TestResults] = "<not run before review>",
            [ReviewerPrompts.SlotKeys.CsprojDiffs] = csprojDiffs,
        };
    }

    private static (string FileExcerpts, string TestExcerpts, string CsprojDiffs) SplitExcerpts(ReviewScope scope)
    {
        var files = new StringBuilder();
        var tests = new StringBuilder();
        var csproj = new StringBuilder();

        foreach (var f in scope.Files)
        {
            // Path-based routing: anything under tests/ goes to the test bucket; csproj files go
            // to the csproj bucket; everything else to the file bucket. Simple v1 heuristic;
            // refine in step 12 if dogfooding shows it misses.
            var normalized = f.RelativePath.Replace('\\', '/');
            var bucket = normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
                ? tests
                : normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    ? csproj
                    : files;

            bucket.Append("---- ").Append(normalized).AppendLine(" ----");
            bucket.AppendLine(f.Content);
            bucket.AppendLine();
        }

        return (
            files.Length == 0 ? "<no files in scope>" : files.ToString(),
            tests.Length == 0 ? "<no test files in scope>" : tests.ToString(),
            csproj.Length == 0 ? "<no .csproj files in scope>" : csproj.ToString());
    }

    private static string BuildDiffSummary(ReviewScope scope)
    {
        if (scope.Files.Count == 0) return $"{scope.Description} -- 0 files";
        var sb = new StringBuilder();
        sb.Append(scope.Description).Append(" -- ").Append(scope.Files.Count).AppendLine(" files in scope:");
        foreach (var f in scope.Files)
            sb.Append("  - ").Append(f.RelativePath.Replace('\\', '/')).Append(" (").Append(f.ByteCount).AppendLine(" bytes)");
        if (scope.FallbackNote is not null) sb.AppendLine().Append("Note: ").AppendLine(scope.FallbackNote);
        return sb.ToString();
    }

    private static string SerializeReviewerOutputs(IReadOnlyList<ReviewerOutput> outputs)
    {
        var sb = new StringBuilder();
        foreach (var o in outputs)
        {
            sb.Append("---- reviewer: ").Append(o.Reviewer).AppendLine(" ----");
            sb.AppendLine(System.Text.Json.JsonSerializer.Serialize(o, FindingJson.PrettyOptions));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static ConsolidatedFinding SynthesizeReviewerFailureFinding(ReviewerRole role, string? error, string? rawText)
    {
        var summary = string.IsNullOrEmpty(rawText)
            ? $"Reviewer '{role}' did not produce output: {error ?? "unknown error"}"
            : $"Reviewer '{role}' produced unparseable output ({error}). Raw text: {Truncate(rawText, 800)}";

        return new ConsolidatedFinding(
            Id: $"orchestrator-{role.ToString().ToLowerInvariant()}-failure",
            Severity: Severity.Warning,
            Title: $"{role} reviewer did not produce a usable report",
            Summary: summary,
            Why: "The reviewer's output could not be parsed or it failed before emitting. Human review of the raw text (in the report's Raw section) is required.",
            File: null,
            LineStart: null,
            LineEnd: null,
            SuggestedFix: null,
            Confidence: 1.0,
            Reviewers: new[] { role.ToString().ToLowerInvariant() },
            Disposition: Disposition.NeedsUser,
            DispositionConfidence: 1.0,
            RejectionReason: null,
            ConsolidatorNote: "Synthesized by the orchestrator on reviewer failure (the coordinator never saw this).");
    }

    private static IReadOnlyList<ConsolidatedFinding> DemoteAllToNeedsUser(IReadOnlyList<ReviewerOutput> reviewerOutputs, string reason)
    {
        // Used when the coordinator failed entirely. Convert each reviewer Finding into a
        // ConsolidatedFinding with disposition=NeedsUser. Severity is preserved (the reviewer's
        // call); we don't have the coordinator's re-scoring, so we trust the raw report.
        var result = new List<ConsolidatedFinding>();
        foreach (var ro in reviewerOutputs)
        {
            foreach (var f in ro.Findings)
            {
                result.Add(new ConsolidatedFinding(
                    Id: f.Id,
                    Severity: f.Severity,
                    Title: f.Title,
                    Summary: f.Summary,
                    Why: f.Why,
                    File: f.File,
                    LineStart: f.LineStart,
                    LineEnd: f.LineEnd,
                    SuggestedFix: f.SuggestedFix,
                    Confidence: f.Confidence,
                    Reviewers: new[] { ro.Reviewer },
                    Disposition: Disposition.NeedsUser,
                    DispositionConfidence: 0.0,
                    RejectionReason: null,
                    ConsolidatorNote: reason));
            }
        }
        return result;
    }

    private static IReadOnlyList<ConsolidatedFinding> DemoteAutoToNeedsUser(IReadOnlyList<ConsolidatedFinding> findings, string reason)
    {
        var result = new List<ConsolidatedFinding>(findings.Count);
        foreach (var f in findings)
        {
            result.Add(f.Disposition == Disposition.NeedsUser ? f : WithDemotion(f, reason));
        }
        return result;
    }

    private static ConsolidatedFinding WithDemotion(ConsolidatedFinding f, string reason)
    {
        var note = string.IsNullOrEmpty(f.ConsolidatorNote) ? reason : $"{f.ConsolidatorNote} | demoted: {reason}";
        return f with
        {
            Disposition = Disposition.NeedsUser,
            ConsolidatorNote = note,
            RejectionReason = null,
        };
    }

    private static string BuildScopeDescription(ReviewScope scope, string? consolidatedSummary)
    {
        if (string.IsNullOrEmpty(consolidatedSummary)) return scope.Description;
        return $"{scope.Description}\n\nCoordinator summary: {consolidatedSummary}";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private sealed record ReviewerCallResult(ReviewerRole Role, ReviewerOutput? Parsed, string? Error, string? RawText);
    private sealed record CoordinatorCallResult(ConsolidatedReport? Parsed, string? RawText);
}
