using FluentAssertions;

namespace InfiniContext.Gui.UiaTests;

/// <summary>Task 0020 Phase 2 proof-of-concept: the real exe over the real UIA tree. Seed test
/// list in design.md section 6.3 grows from here. Until prerequisite P3 (session-index path
/// override) lands, tests here must not spawn sessions - see GuiAppHarness docs.</summary>
[Collection(GuiHarnessCollection.Name)]
public class MainWindowPocTests
{
    private readonly GuiHarnessFixture _fixture;

    public MainWindowPocTests(GuiHarnessFixture fixture) => _fixture = fixture;

    [UiaFact]
    public void Launch_exposes_the_core_automation_id_contract()
    {
        var page = _fixture.Harness.MainWindow;

        page.Title.Should().Be("InfiniContext");
        // Each property getter is a retry-based ByAutomationId lookup that throws on timeout;
        // together they assert the docs/gui-automation-ids.md main-window contract. This is the
        // canary for Avalonia automation-peer regressions (design.md section 6.3 #1).
        page.PromptBox.Should().NotBeNull();
        page.SendButton.Should().NotBeNull();
        page.InterruptButton.Should().NotBeNull();
        page.NewTaskButton.Should().NotBeNull();
        page.SessionTree.Should().NotBeNull();

        page.SendButton.IsEnabled.Should().BeFalse("a fresh sandboxed GUI has no focused session, so Send must be gated off");
    }

    [UiaFact]
    public void New_task_dialog_opens_modally_and_cancels_clean()
    {
        var page = _fixture.Harness.MainWindow;
        // The placeholder VM's NewTaskCommand is not wired to the host; clicking before the
        // live-VM swap would silently no-op (see the beacon comment in MainWindow.axaml).
        page.WaitForHostReady();

        var dialog = page.OpenNewTaskDialog();

        dialog.DescriptionBox.Should().NotBeNull();
        dialog.HomeCwdBox.Should().NotBeNull();
        dialog.ModelBox.Should().NotBeNull();
        dialog.CreateButton.IsEnabled.Should().BeTrue("Create validates on click (ErrorLabel), it is not pre-gated");

        dialog.Cancel();

        page.IsNewTaskDialogOpen.Should().BeFalse("Cancel must close the dialog without creating anything");
        page.SendButton.IsEnabled.Should().BeFalse("cancelling the dialog must not conjure a session");
    }
}
