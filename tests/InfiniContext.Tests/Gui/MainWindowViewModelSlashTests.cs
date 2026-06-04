using FluentAssertions;
using InfiniContext.Gui.ViewModels;

namespace InfiniContext.Tests.Gui;

/// <summary>
/// Slice E (task 0015): the GUI must route host slash-commands through the shared
/// <c>SlashCommandRegistry</c> instead of sending them to claude's stdin. These tests use the
/// host-less VM ctor; a registered command resolves and is intercepted (so it never reaches the
/// session), while an unknown slash-command and plain text fall through to claude.
/// </summary>
public class MainWindowViewModelSlashTests
{
    private static MainWindowViewModel BuildVm() =>
        new(new SynchronousUiDispatcher(), new TestUiTimerFactory());

    private static SessionRowViewModel BuildReadyRow(SessionContextFactory f)
    {
        var row = new SessionRowViewModel(f.Context, new SynchronousUiDispatcher(), new TestUiTimerFactory());
        f.RaiseEvent(SessionContextFactory.MakeInit()); // flips IsReady; state stays Idle
        return row;
    }

    [Fact]
    public async Task Registered_slash_command_is_intercepted_and_not_sent_to_claude()
    {
        var vm = BuildVm();
        var f = new SessionContextFactory();
        vm.FocusedSession = BuildReadyRow(f);
        vm.PromptText = "/help";

        await vm.SendPromptCommand.ExecuteAsync(null);

        f.Session.SentMessages.Should().BeEmpty(
            "a command in the shared registry must be handled by the host, never forwarded to claude");
    }

    [Fact]
    public async Task Task_command_is_recognised_not_unknown()
    {
        // The reported bug: "/task" produced "Unknown command" in the GUI. It must now resolve.
        var vm = BuildVm();
        var f = new SessionContextFactory();
        vm.FocusedSession = BuildReadyRow(f);
        vm.PromptText = "/task review";

        await vm.SendPromptCommand.ExecuteAsync(null);

        f.Session.SentMessages.Should().BeEmpty(
            "/task is a registered host command and must not be forwarded to claude as unknown text");
    }

    [Fact]
    public async Task Unknown_slash_command_is_forwarded_to_claude()
    {
        var vm = BuildVm();
        var f = new SessionContextFactory();
        vm.FocusedSession = BuildReadyRow(f);
        vm.PromptText = "/definitely-not-a-host-command";

        await vm.SendPromptCommand.ExecuteAsync(null);

        f.Session.SentMessages.Should().ContainSingle()
            .Which.Should().Be("/definitely-not-a-host-command",
                "unknown slash-commands fall through to claude, which owns /clear, /compact, ...");
    }

    [Fact]
    public async Task Plain_text_is_sent_to_claude()
    {
        var vm = BuildVm();
        var f = new SessionContextFactory();
        vm.FocusedSession = BuildReadyRow(f);
        vm.PromptText = "hello there";

        await vm.SendPromptCommand.ExecuteAsync(null);

        f.Session.SentMessages.Should().ContainSingle().Which.Should().Be("hello there");
    }
}
