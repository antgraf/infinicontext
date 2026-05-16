using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Tasks;

/// <summary>
/// Pure-function tests for <see cref="TaskSessionPointer"/>. The composed string is the contract
/// between the host and the agent at session start; locking it down here avoids surprise
/// regressions when the format is tweaked.
/// </summary>
public class TaskSessionPointerTests
{
    [Fact]
    public void Without_drift_emits_canonical_pointer()
    {
        var msg = TaskSessionPointer.ComposeFirstMessage(42, "implement-foo", "Implement foo", Array.Empty<string>());

        msg.Should().Be(
            "Working on task 0042 (\"Implement foo\"). Task folder: .task/0042-implement-foo/. Use the infinicontext-task skill.");
    }

    [Fact]
    public void With_drift_prepends_reconcile_note()
    {
        var msg = TaskSessionPointer.ComposeFirstMessage(7, "demo", "Demo", new[] { "research.md", "decisions.md" });

        msg.Should().StartWith("Note: research.md, decisions.md exist on disk but are missing from index.md — please reconcile.\n\n");
        msg.Should().EndWith("Working on task 0007 (\"Demo\"). Task folder: .task/0007-demo/. Use the infinicontext-task skill.");
    }

    [Fact]
    public void Pads_id_to_four_digits_for_both_pointer_and_folder_name()
    {
        var msg = TaskSessionPointer.ComposeFirstMessage(3, "x", "X", Array.Empty<string>());

        msg.Should().Contain("task 0003");
        msg.Should().Contain(".task/0003-x/");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_non_positive_task_id(int id)
    {
        var act = () => TaskSessionPointer.ComposeFirstMessage(id, "slug", "title", Array.Empty<string>());

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Rejects_blank_slug_and_title()
    {
        FluentActions.Invoking(() => TaskSessionPointer.ComposeFirstMessage(1, "", "title", Array.Empty<string>()))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => TaskSessionPointer.ComposeFirstMessage(1, "slug", "  ", Array.Empty<string>()))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_null_drift_list()
    {
        FluentActions.Invoking(() => TaskSessionPointer.ComposeFirstMessage(1, "slug", "title", null!))
            .Should().Throw<ArgumentNullException>();
    }
}
