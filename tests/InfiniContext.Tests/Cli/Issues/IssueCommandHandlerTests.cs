using InfiniContext.Core.Issues;
using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Cli.Issues;

public class IssueCommandHandlerTests
{
    [Fact]
    public async Task Refuses_when_no_active_task_bound_to_focused_session()
    {
        // Use an in-memory TaskStore + IssueService just to satisfy the handler's constructor;
        // we won't actually exercise the service because the handler short-circuits on null
        // activeTaskId before touching it.
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var service = new IssueService(store);
        using var output = new StringWriter();
        var handler = new IssueCommandHandler(service, output);

        await handler.HandleAsync(new IssueCommand.List(), activeTaskId: null, TestContext.Current.CancellationToken);

        output.ToString().Should().Contain("[issue] no active task");
        output.ToString().Should().Contain("/task switch");
    }

    [Fact]
    public async Task Help_renders_without_active_task()
    {
        // Help is the one form that should work without a bound task - users discovering the
        // surface shouldn't have to switch to a task first.
        await using var store = await TaskStore.OpenAsync(":memory:", TestContext.Current.CancellationToken);
        var service = new IssueService(store);
        using var output = new StringWriter();
        var handler = new IssueCommandHandler(service, output);

        await handler.HandleAsync(new IssueCommand.Help(), activeTaskId: null, TestContext.Current.CancellationToken);

        output.ToString().Should().Contain("[issue] commands");
        output.ToString().Should().NotContain("no active task");
    }
}
