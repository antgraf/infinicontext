using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;

namespace InfiniContext.Gui.UiaDriver;

/// <summary>Page object over the modal New-task dialog (see docs/gui-automation-ids.md for the
/// id contract). Obtained via <see cref="MainWindowPage.OpenNewTaskDialog"/>.</summary>
public sealed class NewTaskDialogPage
{
    private static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(10);

    private readonly Window _window;
    private readonly MainWindowPage _owner;

    internal NewTaskDialogPage(Window window, MainWindowPage owner)
    {
        _window = window;
        _owner = owner;
    }

    public AutomationElement HomeCwdBox => Find("HomeCwdBox");
    public AutomationElement DescriptionBox => Find("DescriptionBox");
    public AutomationElement ModelBox => Find("ModelBox");
    public Button CreateButton => Find("CreateButton").AsButton();
    public Button CancelButton => Find("CancelButton").AsButton();

    public void SetHomeCwd(string path) => HomeCwdBox.Patterns.Value.Pattern.SetValue(path);
    public void SetDescription(string text) => DescriptionBox.Patterns.Value.Pattern.SetValue(text);

    /// <summary>Dismiss via the Cancel button and wait until the dialog window is gone.</summary>
    public void Cancel()
    {
        CancelButton.Invoke();
        Retry.WhileNotNull(
            () => _owner.FindNewTaskWindow(),
            timeout: FindTimeout,
            throwOnTimeout: true);
    }

    private AutomationElement Find(string automationId) =>
        // throwOnTimeout means Result is never null on return; the compiler can't see that.
        Retry.WhileNull(
            () => _window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            timeout: FindTimeout,
            throwOnTimeout: true).Result!;
}
