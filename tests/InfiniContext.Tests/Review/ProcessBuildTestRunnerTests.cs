using System.Diagnostics;
using FluentAssertions;
using InfiniContext.Core.Review;
using InfiniContext.Core.Supervision;

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

    [Fact]
    public async Task BuildAsync_attaches_spawned_dotnet_to_the_supervisor()
    {
        // arch-003/004: verify the supervisor receives an Attach(proc) call for the dotnet
        // build child so msbuild/vstest descendants die with the host. The supervisor is the
        // observable contract; whether the OS-level Job Object / SIGTERM cascade then triggers
        // is covered by the supervisor's own tests, not here.
        var supervisor = new RecordingSupervisor();
        // Point at a temp directory that's empty so `dotnet build` exits quickly with a clear
        // "no project" failure тАФ we don't care about the outcome, only that Attach happened.
        var temp = Path.Combine(Path.GetTempPath(), $"pbtr-attach-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var sut = new ProcessBuildTestRunner(
                new ProcessBuildTestRunnerOptions { Solution = "_does_not_exist.slnx", BuildTimeoutSeconds = 30 },
                supervisor: supervisor);
            await sut.BuildAsync(temp, CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }

        // The runner disposes the Process before returning, so don't introspect Attached[0] тАФ
        // the count alone proves Attach was called on the dotnet child.
        supervisor.Attached.Should().HaveCount(1);
    }

    private sealed class RecordingSupervisor : IProcessSupervisor
    {
        public List<Process> Attached { get; } = new();
        public void Attach(Process process) => Attached.Add(process);
        public void DetachAll() { }
        public void Dispose() { }
    }
}
