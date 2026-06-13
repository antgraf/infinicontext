using InfiniContext.Core.Configuration;
using InfiniContext.Core.Review;
using FluentAssertions;
using Serilog;

namespace InfiniContext.Tests.Review;

/// <summary>
/// M20: the <c>/task review</c> stack construction is Core-owned and shared by the CLI and GUI hosts
/// (it used to be copied verbatim into both). These cover the model fallback chain that drove the
/// duplication; constructing the agents spawns no processes, so this is a pure unit test.
/// </summary>
public class ReviewStackFactoryTests
{
    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();

    [Fact]
    public void Build_returns_a_wired_review_service_and_scope_resolver()
    {
        var stack = ReviewStackFactory.Build(InfiniContextSettings.Defaults, supervisor: null, logger: Silent);

        stack.ReviewService.Should().NotBeNull();
        stack.ScopeResolver.Should().NotBeNull();
    }

    [Fact]
    public void Build_tolerates_explicit_reviewer_and_coordinator_model_overrides()
    {
        var settings = InfiniContextSettings.Defaults with
        {
            Review = InfiniContextSettings.Defaults.Review with
            {
                ReviewerModel = "claude-sonnet-4-6",
                CoordinatorModel = "claude-opus-4-8",
            },
        };

        var act = () => ReviewStackFactory.Build(settings, supervisor: null, logger: Silent);

        act.Should().NotThrow("distinct reviewer/coordinator models construct two agents without error.");
    }
}
