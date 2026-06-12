using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace InfiniContext.Gui.UiaDriver;

/// <summary>
/// Page object over the GUI's main window. Tests speak these verbs - no raw UIA lookups in
/// test bodies (design.md section 6.1). Every lookup goes through <see cref="Find"/>, which is
/// retry-based: control realization is asynchronous relative to the UIA client.
/// The id set lives in docs/gui-automation-ids.md and is a public test contract.
/// </summary>
public sealed class MainWindowPage
{
    private static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HostReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly UIA3Automation _automation;

    public MainWindowPage(Window window, UIA3Automation automation)
    {
        Raw = window;
        _automation = automation;
    }

    /// <summary>The underlying FlaUI window, for diagnostics (screenshots) and page-object
    /// composition - not for test-body lookups.</summary>
    public Window Raw { get; }

    public string Title => Raw.Title;

    public AutomationElement PromptBox => Find("PromptBox");
    public Button SendButton => Find("SendButton").AsButton();
    public Button InterruptButton => Find("InterruptButton").AsButton();
    public Button NewTaskButton => Find("NewTaskButton").AsButton();
    public AutomationElement SessionTree => Find("SessionTree");

    /// <summary>Block until the live VM has replaced the placeholder one (App's deferred
    /// CreateAsync). The footer log-path label is the beacon: empty under the placeholder,
    /// always a path once the host is up. Click nothing command-backed before this returns -
    /// the placeholder VM's commands are not wired to the host.</summary>
    public void WaitForHostReady() =>
        Retry.WhileTrue(
            () => string.IsNullOrEmpty(Find("LogFilePathLabel").Name),
            timeout: HostReadyTimeout,
            throwOnTimeout: true);

    /// <summary>Replace the prompt box's text via UIA ValuePattern (proven against Avalonia 12
    /// by the icx-smoke scripts; bypasses keyboard focus, so use the keyboard route instead when
    /// the test is ABOUT focus).</summary>
    public void SetPromptText(string text) => PromptBox.Patterns.Value.Pattern.SetValue(text);

    public string GetPromptText() => PromptBox.Patterns.Value.Pattern.Value.Value;

    /// <summary>Click "+ New task" and wait for the modal dialog to surface.</summary>
    public NewTaskDialogPage OpenNewTaskDialog()
    {
        NewTaskButton.Invoke();
        var dialog = Retry.WhileNull(
            () => FindNewTaskWindow(),
            timeout: FindTimeout,
            throwOnTimeout: true).Result!;
        return new NewTaskDialogPage(dialog.AsWindow(), this);
    }

    /// <summary>Whether the New-task dialog is currently open. Single probe, no retry - wrap in
    /// your own wait if the dialog is mid-transition.</summary>
    public bool IsNewTaskDialogOpen => FindNewTaskWindow() is not null;

    /// <summary>Locate the New-task dialog if currently open, else null. Avalonia 12 exposes an
    /// owned modal as a child Window of its owner in the UIA tree (verified by tree dump, task
    /// 0020) - NOT as a desktop top-level window, so don't search the desktop for it.</summary>
    internal AutomationElement? FindNewTaskWindow() =>
        Raw.FindFirstChild(cf =>
            cf.ByControlType(ControlType.Window).And(cf.ByName("New task")));

    /// <summary>One line per top-level window owned by the GUI process - dialog-discovery
    /// diagnostics for failure handlers.</summary>
    public string DumpProcessTopLevelWindows()
    {
        var sb = new StringBuilder();
        foreach (var element in _automation.GetDesktop().FindAllChildren(cf =>
                     cf.ByProcessId(Raw.Properties.ProcessId.Value)))
        {
            try
            {
                sb.AppendLine(
                    $"{element.Properties.ControlType.ValueOrDefault} " +
                    $"name='{element.Properties.Name.ValueOrDefault}' " +
                    $"id='{element.Properties.AutomationId.ValueOrDefault}' " +
                    $"class='{element.Properties.ClassName.ValueOrDefault}'");
            }
            catch
            {
                sb.AppendLine("(element became unavailable)");
            }
        }
        return sb.ToString();
    }

    /// <summary>Indented ControlType/Name/AutomationId dump of the window's UIA subtree - the
    /// uia.ps1 tree-dump verb, productized for failure diagnostics and ad-hoc probing.</summary>
    public string DumpTree()
    {
        var sb = new StringBuilder();
        Walk(Raw, 0);
        return sb.ToString();

        void Walk(AutomationElement element, int depth)
        {
            string Describe()
            {
                var type = element.Properties.ControlType.ValueOrDefault;
                var name = element.Properties.Name.ValueOrDefault;
                var id = element.Properties.AutomationId.ValueOrDefault;
                return $"{type}{(string.IsNullOrEmpty(name) ? "" : $" \"{name}\"")}{(string.IsNullOrEmpty(id) ? "" : $" [{id}]")}";
            }

            try
            {
                sb.Append(' ', depth * 2).AppendLine(Describe());
                foreach (var child in element.FindAllChildren())
                {
                    Walk(child, depth + 1);
                }
            }
            catch
            {
                sb.Append(' ', depth * 2).AppendLine("(element became unavailable)");
            }
        }
    }

    private AutomationElement Find(string automationId) =>
        // throwOnTimeout means Result is never null on return; the compiler can't see that.
        Retry.WhileNull(
            () => Raw.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            timeout: FindTimeout,
            throwOnTimeout: true).Result!;
}
