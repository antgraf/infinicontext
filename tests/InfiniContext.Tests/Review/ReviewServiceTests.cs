using FluentAssertions;
using InfiniContext.Core.Review;

namespace InfiniContext.Tests.Review;

public class ReviewServiceTests
{
    [Fact]
    public async Task RunReviewAsync_throws_on_null_request()
    {
        var sut = new ReviewService(new FakeReviewer());
        var act = () => sut.RunReviewAsync(null!, new ReviewOptions(), default);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RunReviewAsync_throws_on_null_options()
    {
        var sut = new ReviewService(new FakeReviewer());
        var act = () => sut.RunReviewAsync(MakeRequest(), null!, default);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RunReviewAsync_rejects_non_positive_task_id(int badId)
    {
        var sut = new ReviewService(new FakeReviewer());
        var act = () => sut.RunReviewAsync(MakeRequest() with { TaskId = badId }, new ReviewOptions(), default);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*TaskId*");
    }

    [Fact]
    public async Task RunReviewAsync_rejects_blank_title_and_repo_root()
    {
        var sut = new ReviewService(new FakeReviewer());

        await ((Func<Task>)(() => sut.RunReviewAsync(MakeRequest() with { TaskTitle = "" }, new ReviewOptions(), default)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*TaskTitle*");
        await ((Func<Task>)(() => sut.RunReviewAsync(MakeRequest() with { RepoRoot = "" }, new ReviewOptions(), default)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*RepoRoot*");
    }

    // ---------- Happy paths ----------

    [Fact]
    public async Task RunReviewAsync_partitions_needs_user_findings_into_awaiting()
    {
        var reviewer = AllReviewersEmpty();
        reviewer.ResponsesByRole[ReviewerRole.Coordinator] = CoordinatedJson(MakeNeedsUserFinding("code-001"));

        var sut = new ReviewService(reviewer);
        var report = await sut.RunReviewAsync(MakeRequest(), new ReviewOptions(), TestContext.Current.CancellationToken);

        report.Awaiting.Should().ContainSingle(f => f.Id == "code-001");
        report.Applied.Should().BeEmpty();
        report.Rejected.Should().BeEmpty();
    }

    [Fact]
    public async Task RunReviewAsync_routes_auto_apply_findings_through_executor()
    {
        var reviewer = AllReviewersEmpty();
        var fix = MakeAutoApplyFinding("sec-001");
        reviewer.ResponsesByRole[ReviewerRole.Coordinator] = CoordinatedJson(fix);

        var executor = new FakeAutoApplyExecutor
        {
            Outcome = (applied: new[] { fix }, demoted: Array.Empty<ConsolidatedFinding>(), gatePassed: true, gateReason: null),
        };
        var sut = new ReviewService(reviewer, executor);

        var report = await sut.RunReviewAsync(MakeRequest(), new ReviewOptions(), TestContext.Current.CancellationToken);

        executor.Calls.Should().HaveCount(1);
        executor.Calls[0].repoRoot.Should().Be("D:/repo");
        executor.Calls[0].findings.Should().ContainSingle(f => f.Id == "sec-001");

        report.Applied.Should().ContainSingle(f => f.Id == "sec-001");
        report.Awaiting.Should().BeEmpty();
        report.BuildTestGatePassed.Should().BeTrue();
    }

    [Fact]
    public async Task RunReviewAsync_demotes_auto_apply_when_auto_apply_disabled()
    {
        var reviewer = AllReviewersEmpty();
        var fix = MakeAutoApplyFinding("code-002");
        reviewer.ResponsesByRole[ReviewerRole.Coordinator] = CoordinatedJson(fix);

        var executor = new FakeAutoApplyExecutor();
        var sut = new ReviewService(reviewer, executor);

        var report = await sut.RunReviewAsync(MakeRequest(), new ReviewOptions { AutoApplyEnabled = false }, TestContext.Current.CancellationToken);

        executor.Calls.Should().BeEmpty();
        report.Applied.Should().BeEmpty();
        report.Awaiting.Should().ContainSingle(f => f.Id == "code-002" && f.Disposition == Disposition.NeedsUser);
        report.Awaiting[0].ConsolidatorNote.Should().Contain("auto-apply disabled");
    }

    [Fact]
    public async Task RunReviewAsync_demotes_auto_apply_when_no_executor_configured()
    {
        var reviewer = AllReviewersEmpty();
        var fix = MakeAutoApplyFinding("sec-002");
        reviewer.ResponsesByRole[ReviewerRole.Coordinator] = CoordinatedJson(fix);

        var sut = new ReviewService(reviewer, autoApply: null);
        var report = await sut.RunReviewAsync(MakeRequest(), new ReviewOptions(), TestContext.Current.CancellationToken);

        report.Applied.Should().BeEmpty();
        report.Awaiting.Should().ContainSingle(f => f.Id == "sec-002");
        report.Awaiting[0].ConsolidatorNote.Should().Contain("no auto-apply executor");
    }

    [Fact]
    public async Task RunReviewAsync_routes_auto_reject_findings_to_rejected_bucket()
    {
        var reviewer = AllReviewersEmpty();
        var rejected = MakeAutoRejectFinding("arch-001", reason: "deprecated API is in fact still supported by the framework");
        reviewer.ResponsesByRole[ReviewerRole.Coordinator] = CoordinatedJson(rejected);

        var sut = new ReviewService(reviewer);
        var report = await sut.RunReviewAsync(MakeRequest(), new ReviewOptions(), TestContext.Current.CancellationToken);

        report.Rejected.Should().ContainSingle(f => f.Id == "arch-001");
        report.Rejected[0].RejectionReason.Should().Contain("still supported");
        report.Awaiting.Should().BeEmpty();
        report.Applied.Should().BeEmpty();
    }

    // ---------- Reviewer failures ----------

    [Fact]
    public async Task RunReviewAsync_synthesizes_needs_user_finding_when_a_reviewer_throws()
    {
        var reviewer = AllReviewersEmpty();
        reviewer.ThrowOnRole = ReviewerRole.Security;
        reviewer.ResponsesByRole[ReviewerRole.Coordinator] = CoordinatedJson();

        var sut = new ReviewService(reviewer);
        var report = await sut.RunReviewAsync(MakeRequest(), new ReviewOptions(), TestContext.Current.CancellationToken);

        report.Awaiting.Should().ContainSingle(f => f.Id.Contains("security") && f.Id.Contains("failure"));
        var synthesized = report.Awaiting.First(f => f.Id.Contains("security"));
        synthesized.Severity.Should().Be(Severity.Warning);
        synthesized.Disposition.Should().Be(Disposition.NeedsUser);
        synthesized.Summary.Should().Contain("synthetic failure");
    }

    [Fact]
    public async Task RunReviewAsync_synthesizes_needs_user_finding_when_a_reviewer_emits_unparseable_json()
    {
        var reviewer = AllReviewersEmpty();
        reviewer.ResponsesByRole[ReviewerRole.Tests] = "not even close to JSON";
        reviewer.ResponsesByRole[ReviewerRole.Coordinator] = CoordinatedJson();

        var sut = new ReviewService(reviewer);
        var report = await sut.RunReviewAsync(MakeRequest(), new ReviewOptions(), TestContext.Current.CancellationToken);

        report.Awaiting.Should().Contain(f => f.Id.Contains("tests") && f.Id.Contains("failure"));
    }

    [Fact]
    public async Task RunReviewAsync_handles_total_reviewer_failure_by_skipping_coordinator()
    {
        var reviewer = new FakeReviewer { ThrowAlways = true };

        var sut = new ReviewService(reviewer);
        var report = await sut.RunReviewAsync(MakeRequest(), new ReviewOptions(), TestContext.Current.CancellationToken);

        // Five synthetic findings, one per reviewer; coordinator never invoked.
        report.Awaiting.Should().HaveCount(5);
        report.Awaiting.Should().AllSatisfy(f => f.Disposition.Should().Be(Disposition.NeedsUser));
        report.Applied.Should().BeEmpty();
        report.Rejected.Should().BeEmpty();
        reviewer.Calls.Should().NotContain(c => c.role == ReviewerRole.Coordinator);
    }

    // ---------- Coordinator failures ----------

    [Fact]
    public async Task RunReviewAsync_demotes_all_to_needs_user_when_coordinator_output_unparseable()
    {
        var reviewer = AllReviewersEmpty();
        reviewer.ResponsesByRole[ReviewerRole.Tests] = ReviewerJson("tests", new (string Id, string Severity, string? Why)[] { ("t-001", "warning", null) });
        reviewer.ResponsesByRole[ReviewerRole.Coordinator] = "i refuse to emit JSON";

        var sut = new ReviewService(reviewer);
        var report = await sut.RunReviewAsync(MakeRequest(), new ReviewOptions(), TestContext.Current.CancellationToken);

        report.Awaiting.Should().ContainSingle(f => f.Id == "t-001");
        report.Awaiting[0].Disposition.Should().Be(Disposition.NeedsUser);
        report.Awaiting[0].ConsolidatorNote.Should().Contain("unparseable");
    }

    [Fact]
    public async Task RunReviewAsync_demotes_auto_findings_when_coordinator_output_fails_validation()
    {
        var reviewer = AllReviewersEmpty();
        // A coordinator output that violates the schema: AutoReject without a rejection_reason.
        reviewer.ResponsesByRole[ReviewerRole.Coordinator] = """
            {
              "findings": [
                {
                  "id": "x-001",
                  "severity": "critical",
                  "title": "x",
                  "summary": "y",
                  "confidence": 0.95,
                  "reviewers": ["code"],
                  "disposition": "auto_reject",
                  "disposition_confidence": 0.95,
                  "rejection_reason": ""
                }
              ],
              "summary": null
            }
            """;

        var sut = new ReviewService(reviewer);
        var report = await sut.RunReviewAsync(MakeRequest(), new ReviewOptions(), TestContext.Current.CancellationToken);

        report.Awaiting.Should().ContainSingle(f => f.Id == "x-001" && f.Disposition == Disposition.NeedsUser);
        report.Rejected.Should().BeEmpty();
        report.Awaiting[0].ConsolidatorNote.Should().Contain("validation");
    }

    // ---------- Report metadata ----------

    [Fact]
    public async Task RunReviewAsync_populates_raw_reviewer_outputs_for_every_role()
    {
        var reviewer = AllReviewersEmpty();
        reviewer.ResponsesByRole[ReviewerRole.Coordinator] = CoordinatedJson();

        var sut = new ReviewService(reviewer);
        var report = await sut.RunReviewAsync(MakeRequest(), new ReviewOptions(), TestContext.Current.CancellationToken);

        report.RawReviewerOutputs.Keys.Should().Contain(new[]
        {
            "Completeness", "Architecture", "CodeLogic", "Tests", "Security", "Coordinator",
        });
    }

    [Fact]
    public async Task RunReviewAsync_propagates_excluded_files_to_report()
    {
        var reviewer = AllReviewersEmpty();
        reviewer.ResponsesByRole[ReviewerRole.Coordinator] = CoordinatedJson();

        var scope = new ReviewScope(
            Description: "test scope",
            Files: Array.Empty<ReviewFile>(),
            ExcludedFiles: new[] { new ExcludedFile("big.json", "exceeds byte cap") },
            FallbackNote: null);

        var sut = new ReviewService(reviewer);
        var report = await sut.RunReviewAsync(MakeRequest() with { Scope = scope }, new ReviewOptions(), TestContext.Current.CancellationToken);

        report.ExcludedFiles.Should().ContainSingle(s => s.Contains("big.json") && s.Contains("byte cap"));
    }

    [Fact]
    public async Task RunReviewAsync_reports_gate_failure_from_executor()
    {
        var reviewer = AllReviewersEmpty();
        var fix = MakeAutoApplyFinding("code-003");
        reviewer.ResponsesByRole[ReviewerRole.Coordinator] = CoordinatedJson(fix);

        var executor = new FakeAutoApplyExecutor
        {
            Outcome = (
                applied: Array.Empty<ConsolidatedFinding>(),
                demoted: new[] { fix with { Disposition = Disposition.NeedsUser, ConsolidatorNote = "tests broke" } },
                gatePassed: false,
                gateReason: "test gate red after auto-apply"),
        };

        var sut = new ReviewService(reviewer, executor);
        var report = await sut.RunReviewAsync(MakeRequest(), new ReviewOptions(), TestContext.Current.CancellationToken);

        report.BuildTestGatePassed.Should().BeFalse();
        report.GateReversionReason.Should().Contain("test gate red");
        report.Applied.Should().BeEmpty();
        report.Awaiting.Should().ContainSingle(f => f.Id == "code-003");
    }

    // ---------- Coordinator channel split (CoordinatorModel routing) ----------

    [Fact]
    public async Task RunReviewAsync_routes_coordinator_through_the_coordinator_channel()
    {
        var reviewer = AllReviewersEmpty();
        var coordinator = new FakeReviewer();
        coordinator.ResponsesByRole[ReviewerRole.Coordinator] = CoordinatedJson(MakeNeedsUserFinding("code-001"));

        var sut = new ReviewService(reviewer, coordinator);
        var report = await sut.RunReviewAsync(MakeRequest(), new ReviewOptions(), TestContext.Current.CancellationToken);

        // Reviewer channel: exactly the five reviewer roles, never the coordinator.
        reviewer.Calls.Should().HaveCount(5);
        reviewer.Calls.Select(c => c.role).Should().BeEquivalentTo(new[]
        {
            ReviewerRole.Completeness,
            ReviewerRole.Architecture,
            ReviewerRole.CodeLogic,
            ReviewerRole.Tests,
            ReviewerRole.Security,
        });
        reviewer.Calls.Should().NotContain(c => c.role == ReviewerRole.Coordinator);

        // Coordinator channel: exactly one Coordinator-role call, never a reviewer-role call.
        coordinator.Calls.Should().ContainSingle();
        coordinator.Calls[0].role.Should().Be(ReviewerRole.Coordinator);

        report.Awaiting.Should().ContainSingle(f => f.Id == "code-001");
    }

    [Fact]
    public async Task RunReviewAsync_passing_same_instance_to_both_channels_still_works()
    {
        // The convenience ctor (single-arg) forwards to ctor(reviewer, reviewer, ...) — verify
        // the single-instance path keeps prior semantics: one instance sees all six calls.
        var both = AllReviewersEmpty();
        both.ResponsesByRole[ReviewerRole.Coordinator] = CoordinatedJson();

        var sut = new ReviewService(both);
        await sut.RunReviewAsync(MakeRequest(), new ReviewOptions(), TestContext.Current.CancellationToken);

        both.Calls.Should().HaveCount(6);
        both.Calls.Should().Contain(c => c.role == ReviewerRole.Coordinator);
    }

    [Fact]
    public void Ctor_throws_when_either_channel_is_null()
    {
        var fake = new FakeReviewer();
        Action a = () => new ReviewService(reviewer: null!, coordinator: fake);
        Action b = () => new ReviewService(reviewer: fake, coordinator: null!);

        a.Should().Throw<ArgumentNullException>().WithParameterName("reviewer");
        b.Should().Throw<ArgumentNullException>().WithParameterName("coordinator");
    }

    // ---------- Helpers ----------

    private static ReviewRequest MakeRequest() => new(
        TaskId: 42,
        TaskTitle: "Test task",
        RepoRoot: "D:/repo",
        Scope: new ReviewScope("test diff", Array.Empty<ReviewFile>(), Array.Empty<ExcludedFile>(), null),
        DescriptionMd: "# description",
        ProgressMd: "# progress",
        IndexMd: "# index",
        ClaudeMd: "# claude",
        DecisionsMd: "# decisions");

    private static FakeReviewer AllReviewersEmpty()
    {
        var r = new FakeReviewer();
        foreach (var role in new[] { ReviewerRole.Completeness, ReviewerRole.Architecture, ReviewerRole.CodeLogic, ReviewerRole.Tests, ReviewerRole.Security })
        {
            r.ResponsesByRole[role] = ReviewerJson(role.ToString().ToLowerInvariant(), Array.Empty<(string, string, string?)>());
        }
        return r;
    }

    private static string ReviewerJson(string reviewer, IReadOnlyList<(string Id, string Severity, string? Why)> findings)
    {
        var findingsJson = string.Join(",\n", findings.Select(f =>
            $$"""
            {
              "id": "{{f.Id}}",
              "severity": "{{f.Severity}}",
              "title": "title",
              "summary": "summary",
              "why": {{(f.Why is null ? "null" : "\"" + f.Why + "\"")}},
              "confidence": 0.8
            }
            """));
        return $$"""
            {
              "reviewer": "{{reviewer}}",
              "findings": [{{findingsJson}}],
              "notes": null
            }
            """;
    }

    private static string CoordinatedJson(params ConsolidatedFinding[] findings)
    {
        var findingsJson = string.Join(",\n", findings.Select(SerializeConsolidatedFinding));
        return $$"""
            {
              "findings": [{{findingsJson}}],
              "summary": "consolidated"
            }
            """;
    }

    private static string SerializeConsolidatedFinding(ConsolidatedFinding f) =>
        System.Text.Json.JsonSerializer.Serialize(f, FindingJson.Options);

    private static ConsolidatedFinding MakeNeedsUserFinding(string id) => new(
        Id: id,
        Severity: Severity.Warning,
        Title: "title",
        Summary: "summary",
        Why: "why",
        File: "src/Foo.cs",
        LineStart: 10,
        LineEnd: 12,
        SuggestedFix: null,
        Confidence: 0.7,
        Reviewers: new[] { "code" },
        Disposition: Disposition.NeedsUser,
        DispositionConfidence: 0.7,
        RejectionReason: null,
        ConsolidatorNote: null);

    private static ConsolidatedFinding MakeAutoApplyFinding(string id) => new(
        Id: id,
        Severity: Severity.Warning,
        Title: "fix this",
        Summary: "summary",
        Why: "why",
        File: "src/Foo.cs",
        LineStart: 10,
        LineEnd: 10,
        SuggestedFix: new SuggestedFix(FixKind.Edit, "old", "new"),
        Confidence: 0.95,
        Reviewers: new[] { "code" },
        Disposition: Disposition.AutoApply,
        DispositionConfidence: 0.92,
        RejectionReason: null,
        ConsolidatorNote: null);

    private static ConsolidatedFinding MakeAutoRejectFinding(string id, string reason) => new(
        Id: id,
        Severity: Severity.Warning,
        Title: "spurious",
        Summary: "summary",
        Why: "why",
        File: "src/Foo.cs",
        LineStart: null,
        LineEnd: null,
        SuggestedFix: null,
        Confidence: 0.8,
        Reviewers: new[] { "architecture" },
        Disposition: Disposition.AutoReject,
        DispositionConfidence: 0.9,
        RejectionReason: reason,
        ConsolidatorNote: null);

    private sealed class FakeReviewer : IReviewerAgentService
    {
        public Dictionary<ReviewerRole, string> ResponsesByRole { get; } = new();
        public List<(ReviewerRole role, string user)> Calls { get; } = new();
        public ReviewerRole? ThrowOnRole { get; set; }
        public bool ThrowAlways { get; set; }

        public Task<string> RunOneShotAsync(string systemPrompt, string userPayload, CancellationToken ct)
        {
            var role = InferRole(systemPrompt);
            Calls.Add((role, userPayload));

            if (ThrowAlways || ThrowOnRole == role)
                throw new InvalidOperationException("synthetic failure");

            if (!ResponsesByRole.TryGetValue(role, out var resp))
                throw new InvalidOperationException($"no response queued for role {role}");

            return Task.FromResult(resp);
        }

        private static ReviewerRole InferRole(string systemPrompt)
        {
            if (systemPrompt.Contains("Completeness reviewer")) return ReviewerRole.Completeness;
            if (systemPrompt.Contains("Architecture reviewer")) return ReviewerRole.Architecture;
            if (systemPrompt.Contains("Code & Logic reviewer")) return ReviewerRole.CodeLogic;
            if (systemPrompt.Contains("Tests reviewer")) return ReviewerRole.Tests;
            if (systemPrompt.Contains("Security reviewer")) return ReviewerRole.Security;
            if (systemPrompt.Contains("Coordinator")) return ReviewerRole.Coordinator;
            throw new InvalidOperationException("unknown reviewer system prompt");
        }
    }

    private sealed class FakeAutoApplyExecutor : IAutoApplyExecutor
    {
        public List<(string repoRoot, IReadOnlyList<ConsolidatedFinding> findings)> Calls { get; } = new();
        public (IReadOnlyList<ConsolidatedFinding> applied, IReadOnlyList<ConsolidatedFinding> demoted, bool gatePassed, string? gateReason) Outcome { get; set; }
            = (Array.Empty<ConsolidatedFinding>(), Array.Empty<ConsolidatedFinding>(), true, null);

        public Task<AutoApplyResult> ApplyAsync(string repoRoot, IReadOnlyList<ConsolidatedFinding> findings, CancellationToken ct)
        {
            Calls.Add((repoRoot, findings));
            return Task.FromResult(new AutoApplyResult(Outcome.applied, Outcome.demoted, Outcome.gatePassed, Outcome.gateReason));
        }
    }
}
