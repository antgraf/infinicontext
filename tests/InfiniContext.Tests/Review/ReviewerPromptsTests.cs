using FluentAssertions;
using InfiniContext.Core.Review;

namespace InfiniContext.Tests.Review;

public class ReviewerPromptsTests
{
    [Theory]
    [InlineData(ReviewerRole.Completeness)]
    [InlineData(ReviewerRole.Architecture)]
    [InlineData(ReviewerRole.CodeLogic)]
    [InlineData(ReviewerRole.Tests)]
    [InlineData(ReviewerRole.Security)]
    [InlineData(ReviewerRole.Coordinator)]
    public void SystemFor_returns_non_empty_for_every_role(ReviewerRole role)
    {
        ReviewerPrompts.SystemFor(role).Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(ReviewerRole.Completeness)]
    [InlineData(ReviewerRole.Architecture)]
    [InlineData(ReviewerRole.CodeLogic)]
    [InlineData(ReviewerRole.Tests)]
    [InlineData(ReviewerRole.Security)]
    [InlineData(ReviewerRole.Coordinator)]
    public void UserTemplateFor_returns_non_empty_for_every_role(ReviewerRole role)
    {
        ReviewerPrompts.UserTemplateFor(role).Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(ReviewerRole.Completeness)]
    [InlineData(ReviewerRole.Architecture)]
    [InlineData(ReviewerRole.CodeLogic)]
    [InlineData(ReviewerRole.Tests)]
    [InlineData(ReviewerRole.Security)]
    public void Reviewer_system_prompts_mention_the_three_severity_levels(ReviewerRole role)
    {
        var prompt = ReviewerPrompts.SystemFor(role);
        prompt.Should().Contain("critical").And.Contain("warning").And.Contain("suggestion");
    }

    [Theory]
    [InlineData(ReviewerRole.Completeness)]
    [InlineData(ReviewerRole.Architecture)]
    [InlineData(ReviewerRole.CodeLogic)]
    [InlineData(ReviewerRole.Tests)]
    [InlineData(ReviewerRole.Security)]
    public void Reviewer_system_prompts_mention_the_json_schema_fields(ReviewerRole role)
    {
        var prompt = ReviewerPrompts.SystemFor(role);
        // The key fields the orchestrator parses into Finding.
        prompt.Should().Contain("\"severity\"")
            .And.Contain("\"confidence\"")
            .And.Contain("\"suggested_fix\"")
            .And.Contain("\"findings\"");
    }

    [Theory]
    [InlineData(ReviewerRole.Completeness)]
    [InlineData(ReviewerRole.Architecture)]
    [InlineData(ReviewerRole.CodeLogic)]
    [InlineData(ReviewerRole.Tests)]
    [InlineData(ReviewerRole.Security)]
    public void Reviewer_system_prompts_instruct_json_only_output(ReviewerRole role)
    {
        var prompt = ReviewerPrompts.SystemFor(role);
        // "Output ONLY ... JSON ..." or similar тАФ the prompt must steer away from prose preambles.
        prompt.Should().MatchRegex(@"Output ONLY.*JSON");
    }

    [Fact]
    public void Coordinator_system_mentions_disposition_values()
    {
        var prompt = ReviewerPrompts.SystemFor(ReviewerRole.Coordinator);
        prompt.Should().Contain("auto_apply").And.Contain("auto_reject").And.Contain("needs_user");
    }

    [Fact]
    public void Coordinator_system_requires_rejection_reason_for_auto_reject()
    {
        var prompt = ReviewerPrompts.SystemFor(ReviewerRole.Coordinator);
        // The user's hard requirement: every rejection carries an explicit reason. The prompt
        // must surface this so the agent doesn't omit it.
        prompt.Should().Contain("rejection_reason");
        prompt.ToLowerInvariant().Should().Contain("required");
    }

    [Fact]
    public void Coordinator_system_states_severity_is_orthogonal_to_disposition()
    {
        // The user's clarification: any severity can auto_apply or auto_reject. The prompt must
        // surface this so the agent doesn't fall back to "critical always needs user".
        var prompt = ReviewerPrompts.SystemFor(ReviewerRole.Coordinator);
        prompt.ToLowerInvariant().Should().Contain("orthogonal");
    }

    [Fact]
    public void Reviewer_system_prompts_each_name_their_role()
    {
        ReviewerPrompts.SystemFor(ReviewerRole.Completeness).Should().Contain("Completeness");
        ReviewerPrompts.SystemFor(ReviewerRole.Architecture).Should().Contain("Architecture");
        ReviewerPrompts.SystemFor(ReviewerRole.CodeLogic).Should().Contain("Code & Logic");
        ReviewerPrompts.SystemFor(ReviewerRole.Tests).Should().Contain("Tests reviewer");
        ReviewerPrompts.SystemFor(ReviewerRole.Security).Should().Contain("Security reviewer");
        ReviewerPrompts.SystemFor(ReviewerRole.Coordinator).Should().Contain("Coordinator");
    }

    // ---------- Slot referencing per role ----------

    [Fact]
    public void Completeness_user_template_references_description_progress_index_diff_excerpts()
    {
        var refs = ReviewerPrompts.SlotsReferencedBy(ReviewerPrompts.UserTemplateFor(ReviewerRole.Completeness));
        refs.Should().Contain(new[]
        {
            ReviewerPrompts.SlotKeys.DescriptionMd,
            ReviewerPrompts.SlotKeys.ProgressMd,
            ReviewerPrompts.SlotKeys.IndexMd,
            ReviewerPrompts.SlotKeys.DiffSummary,
            ReviewerPrompts.SlotKeys.FileExcerpts,
        });
    }

    [Fact]
    public void Architecture_user_template_references_claude_md_and_decisions()
    {
        var refs = ReviewerPrompts.SlotsReferencedBy(ReviewerPrompts.UserTemplateFor(ReviewerRole.Architecture));
        refs.Should().Contain(new[]
        {
            ReviewerPrompts.SlotKeys.ClaudeMd,
            ReviewerPrompts.SlotKeys.DecisionsMd,
            ReviewerPrompts.SlotKeys.FileExcerpts,
        });
    }

    [Fact]
    public void Tests_user_template_references_test_excerpts_and_results()
    {
        var refs = ReviewerPrompts.SlotsReferencedBy(ReviewerPrompts.UserTemplateFor(ReviewerRole.Tests));
        refs.Should().Contain(new[]
        {
            ReviewerPrompts.SlotKeys.TestExcerpts,
            ReviewerPrompts.SlotKeys.TestResults,
        });
    }

    [Fact]
    public void Security_user_template_references_csproj_diffs()
    {
        var refs = ReviewerPrompts.SlotsReferencedBy(ReviewerPrompts.UserTemplateFor(ReviewerRole.Security));
        refs.Should().Contain(ReviewerPrompts.SlotKeys.CsprojDiffs);
    }

    [Fact]
    public void Coordinator_user_template_references_reviewer_outputs()
    {
        var refs = ReviewerPrompts.SlotsReferencedBy(ReviewerPrompts.UserTemplateFor(ReviewerRole.Coordinator));
        refs.Should().Contain(ReviewerPrompts.SlotKeys.ReviewerOutputs);
    }

    [Theory]
    [InlineData(ReviewerRole.Completeness)]
    [InlineData(ReviewerRole.Architecture)]
    [InlineData(ReviewerRole.CodeLogic)]
    [InlineData(ReviewerRole.Tests)]
    [InlineData(ReviewerRole.Security)]
    [InlineData(ReviewerRole.Coordinator)]
    public void Every_user_template_references_task_id_and_title_and_scope(ReviewerRole role)
    {
        var refs = ReviewerPrompts.SlotsReferencedBy(ReviewerPrompts.UserTemplateFor(role));
        refs.Should().Contain(new[]
        {
            ReviewerPrompts.SlotKeys.TaskId,
            ReviewerPrompts.SlotKeys.TaskTitle,
            ReviewerPrompts.SlotKeys.ScopeDescription,
        });
    }

    // ---------- Fill ----------

    [Fact]
    public void Fill_substitutes_a_single_slot()
    {
        var template = "Task {task_id}: hello.";
        var result = ReviewerPrompts.Fill(template, new Dictionary<string, string>
        {
            [ReviewerPrompts.SlotKeys.TaskId] = "42",
        });
        result.Should().Be("Task 42: hello.");
    }

    [Fact]
    public void Fill_substitutes_every_occurrence_of_a_repeated_slot()
    {
        var template = "{task_title} -- {task_title}";
        var result = ReviewerPrompts.Fill(template, new Dictionary<string, string>
        {
            [ReviewerPrompts.SlotKeys.TaskTitle] = "x",
        });
        result.Should().Be("x -- x");
    }

    [Fact]
    public void Fill_leaves_text_with_no_slots_unchanged()
    {
        var result = ReviewerPrompts.Fill("plain text", new Dictionary<string, string>());
        result.Should().Be("plain text");
    }

    [Fact]
    public void Fill_throws_when_a_referenced_slot_is_missing()
    {
        var template = "Task {task_id}: {task_title}";
        var slots = new Dictionary<string, string>
        {
            [ReviewerPrompts.SlotKeys.TaskId] = "1",
            // task_title intentionally missing
        };
        var act = () => ReviewerPrompts.Fill(template, slots);
        act.Should().Throw<InvalidOperationException>().WithMessage("*task_title*");
    }

    [Fact]
    public void Fill_ignores_extra_slot_keys_the_template_does_not_reference()
    {
        var template = "Task {task_id}";
        var slots = new Dictionary<string, string>
        {
            [ReviewerPrompts.SlotKeys.TaskId] = "1",
            [ReviewerPrompts.SlotKeys.ProgressMd] = "extra data",
        };
        var act = () => ReviewerPrompts.Fill(template, slots);
        act.Should().NotThrow();
    }

    [Fact]
    public void SlotsReferencedBy_preserves_document_order()
    {
        var template = "{b_slot} then {a_slot} then {c_slot}";
        ReviewerPrompts.SlotsReferencedBy(template).Should()
            .Equal(new[] { "b_slot", "a_slot", "c_slot" });
    }

    [Fact]
    public void Fill_throws_for_null_template()
    {
        var act = () => ReviewerPrompts.Fill(null!, new Dictionary<string, string>());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SystemFor_throws_for_unknown_role()
    {
        var act = () => ReviewerPrompts.SystemFor((ReviewerRole)999);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
