using InfiniContext.Core.Sessions;
using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Sessions;

public class HostLoopStateTests
{
    private const string LaunchCwd = "/launch";
    private const string TaskHome = "/launch/repo";

    [Fact]
    public void Constructor_defaults_cwd_to_launch_cwd_and_no_pending_fields()
    {
        var state = new HostLoopState(LaunchCwd);

        state.CurrentSessionCwd.Should().Be(LaunchCwd);
        state.ResumeSessionId.Should().BeNull();
        state.PendingFirstUserMessage.Should().BeNull();
        state.ActiveTaskId.Should().BeNull();
    }

    [Fact]
    public void Constructor_threads_through_initial_resume_session_id()
    {
        var state = new HostLoopState(LaunchCwd, initialResumeSessionId: "abc-123");

        state.ResumeSessionId.Should().Be("abc-123");
        state.CurrentSessionCwd.Should().Be(LaunchCwd);
        state.PendingFirstUserMessage.Should().BeNull();
        state.ActiveTaskId.Should().BeNull();
    }

    [Fact]
    public void Constructor_rejects_empty_launch_cwd()
    {
        Action act = () => _ = new HostLoopState("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void OnSwitchPlan_claims_task_overrides_cwd_drops_resume_id()
    {
        var state = new HostLoopState(LaunchCwd, initialResumeSessionId: "old-resume");
        var plan = NewPlan(taskId: 7, homeCwd: TaskHome, firstUserMessage: "[task] resume 0007");

        state.OnSwitchPlan(plan);

        state.ActiveTaskId.Should().Be(7);
        state.CurrentSessionCwd.Should().Be(TaskHome);
        state.PendingFirstUserMessage.Should().Be("[task] resume 0007");
        state.ResumeSessionId.Should().BeNull("a switch always starts a fresh conversation");
    }

    [Fact]
    public void OnResume_resets_cwd_to_launch_and_drops_task_binding()
    {
        var state = new HostLoopState(LaunchCwd);
        state.OnSwitchPlan(NewPlan(taskId: 3, homeCwd: TaskHome, firstUserMessage: "ptr"));

        state.OnResume("new-resume");

        state.ResumeSessionId.Should().Be("new-resume");
        state.CurrentSessionCwd.Should().Be(LaunchCwd, "resume is not task-bound");
        state.ActiveTaskId.Should().BeNull();
        state.PendingFirstUserMessage.Should().BeNull();
    }

    [Fact]
    public void OnExit_clears_pending_fields_and_resets_cwd()
    {
        var state = new HostLoopState(LaunchCwd);
        state.OnSwitchPlan(NewPlan(taskId: 5, homeCwd: TaskHome, firstUserMessage: "ptr"));

        state.OnExit();

        state.ActiveTaskId.Should().BeNull();
        state.PendingFirstUserMessage.Should().BeNull();
        state.ResumeSessionId.Should().BeNull();
        state.CurrentSessionCwd.Should().Be(LaunchCwd);
    }

    [Fact]
    public void OnSwitchPlan_rejects_null()
    {
        var state = new HostLoopState(LaunchCwd);
        Action act = () => state.OnSwitchPlan(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void OnResume_rejects_empty_session_id()
    {
        var state = new HostLoopState(LaunchCwd);
        Action act = () => state.OnResume("");
        act.Should().Throw<ArgumentException>();
    }

    private static TaskSwitchPlan NewPlan(int taskId, string homeCwd, string firstUserMessage)
    {
        var record = new TaskRecord(
            Id: taskId,
            HomeCwd: homeCwd,
            Title: $"task {taskId}",
            CreatedAtUnixMs: 0,
            LastActiveAtUnixMs: 0,
            Status: TaskItemStatus.Idle,
            ParentTaskId: null);
        return new TaskSwitchPlan(record, $"task-{taskId}", firstUserMessage, Array.Empty<string>());
    }
}
