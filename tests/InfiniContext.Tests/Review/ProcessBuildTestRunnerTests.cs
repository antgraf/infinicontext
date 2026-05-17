using FluentAssertions;
using InfiniContext.Core.Review;

namespace InfiniContext.Tests.Review;

public class ProcessBuildTestRunnerTests
{
    // These tests don't actually spawn `dotnet` тАФ that would be slow and brittle. They verify
    // option defaults, argument validation, and the "Process.Start returns null" handling that's
    // exercised by passing an empty argv (forces ArgumentException at psi.Start). Real
    // build/test invocation is covered by step 12's dogfood run, not here.

    [Fact]
    public void Defaults_match_the_infinicontext_repo_conventions()
    {
        var opts = new ProcessBuildTestRunnerOptions();
        opts.Solution.Should().Be("InfiniContext.slnx");
        opts.Configuration.Should().Be("Release");
        opts.BuildTimeoutSeconds.Should().Be(600);
        opts.TestTimeoutSeconds.Should().Be(900);
        opts.ExtraBuildArgs.Should().BeNull();
        opts.ExtraTestArgs.Should().BeNull();
    }

    [Fact]
    public void Options_record_supports_with_to_override_each_field()
    {
        var opts = new ProcessBuildTestRunnerOptions()
            with
        { Solution = "Other.sln", Configuration = "Debug", BuildTimeoutSeconds = 30, TestTimeoutSeconds = 60 };

        opts.Solution.Should().Be("Other.sln");
        opts.Configuration.Should().Be("Debug");
        opts.BuildTimeoutSeconds.Should().Be(30);
        opts.TestTimeoutSeconds.Should().Be(60);
    }

    [Fact]
    public async Task BuildAsync_throws_for_blank_repo_root()
    {
        var sut = new ProcessBuildTestRunner();
        var act = () => sut.BuildAsync("", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TestAsync_throws_for_blank_repo_root()
    {
        var sut = new ProcessBuildTestRunner();
        var act = () => sut.TestAsync("", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
