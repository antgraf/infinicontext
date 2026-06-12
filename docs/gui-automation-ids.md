# GUI automation ids

`AutomationProperties.AutomationId` values exposed by the GUI's UIA tree. These ids are a
**public test contract**: both `tests/InfiniContext.Gui.HeadlessTests` (via `FindControl`,
which uses `Name` -- kept equal to the id by convention) and `tests/InfiniContext.Gui.UiaTests`
(via FlaUI `ByAutomationId`) address controls through them. **Renaming an id is a breaking
change to both suites**; update this table and the page objects in
`tests/InfiniContext.Gui.UiaDriver` together with any rename.

Convention (task 0020 design.md P1): the id equals the control's `x:Name`/`Name`; every
interactive control gets one. Coverage below is the task-0020 POC slice; controls without
ids yet are tracked in the "Not yet covered" list and must be added here as tests grow.

## MainWindow.axaml

| AutomationId | Control | Purpose |
|---|---|---|
| `NewTaskButton` | Button | Opens the New-task dialog |
| `SessionTree` | TreeView | Tasks & sessions tree |
| `PromptBox` | TextBox | Prompt input for the focused session |
| `SendButton` | Button | Send prompt (Ctrl+Enter equivalent) |
| `InterruptButton` | Button | Interrupt the in-flight turn (Esc equivalent) |
| `LogFilePathLabel` | TextBlock | Footer log path; empty under the placeholder VM, set once the live VM lands -- the UIA harness's "host is ready" beacon |

## NewTaskDialog.axaml

| AutomationId | Control | Purpose |
|---|---|---|
| `HomeCwdBox` | TextBox | Task home folder path |
| `BrowseButton` | Button | OS folder picker |
| `ModelBox` | ComboBox | Default model choice |
| `DescriptionBox` | TextBox | Task description |
| `ErrorLabel` | TextBlock | Validation error text (IsVisible on error) |
| `CreateButton` | Button | Confirm / create the task |
| `CancelButton` | Button | Dismiss without creating |

## Not yet covered (P1 remainder)

- MainWindow: approval-banner Approve/Deny buttons, Approval-mode ComboBox, Auto-scroll
  CheckBox, inspector model ComboBox + "Apply (respawn)" button, per-row tree action
  buttons (`+`, continue, what's-next; need ids via the item templates plus
  `AutomationProperties.Name` bound to the row title so UIA can address rows by text).
- ConfirmDialog: confirm/cancel buttons.
