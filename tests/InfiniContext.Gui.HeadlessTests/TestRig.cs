using InfiniContext.Gui.Services;
using InfiniContext.Gui.ViewModels;
using InfiniContext.Tests.Gui;

namespace InfiniContext.Gui.HeadlessTests;

/// <summary>Composes view-models for headless view tests the same way the VM unit tests do
/// (SessionContextFactory + FakeClaudeSession; the only fake is the IClaudeSession boundary),
/// but injected with the PRODUCTION <see cref="AvaloniaUiDispatcher"/> because headless tests
/// run on a real <c>Dispatcher.UIThread</c>.</summary>
internal static class TestRig
{
    public static MainWindowViewModel CreateMainVm() =>
        new(AvaloniaUiDispatcher.Instance, AvaloniaUiTimerFactory.Instance);

    public static SessionRowViewModel CreateRow(SessionContextFactory factory) =>
        new(factory.Context, AvaloniaUiDispatcher.Instance, AvaloniaUiTimerFactory.Instance);
}
