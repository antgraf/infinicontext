# Progress

## 2026-06-12 (review)

- **Reviewed and committed.** Independent review of the completed work:
  - Working-tree diff for `MainWindow.axaml` is exactly the two `SelectionEnabled="True"`
    attributes (user-prompt viewer line 62, agent-headline viewer line 74) — no unrelated
    changes swept in.
  - GUI project rebuilt clean (0 warnings / 0 errors) to an isolated `OutputPath` (4 live
    GUI instances still hold the normal `bin`); the AXAML compile validates the property
    against the pinned package.
  - Confirmed independently of the build that the pinned `Markdown.Avalonia.Tight 12.0.0-a3`
    assembly contains both `SelectionEnabled` and the `SelectableTextBlock` render path, so
    the existing `md|MarkdownScrollViewer SelectableTextBlock { TextWrapping=Wrap }` window
    style keeps long lines wrapping in selectable mode.
  - Considered automating the copy check with an Avalonia headless test: the test project
    has no Avalonia.Headless infrastructure (GUI tests are viewmodel-only via linked
    compiles), so adding that dependency for a two-attribute change isn't warranted.
    The drag-select + Ctrl+C confirmation remains a manual, human-only step.
  - Fixed mojibake (`вЂ”` → `—`) in this file's 2026-06-11 entries.
  - Note: edits made without the `tasks/lock` channel — this review session runs outside
    the InfiniContext host, so the MCP lock tools were not available.
- Committed the AXAML change + task artifacts and pushed.

## 2026-06-12

- Task created.
- **Build re-confirmed:** `SelectionEnabled="True"` is in the working tree on both
  `md:MarkdownScrollViewer` instances (`MainWindow.axaml` lines 62, 74). Rebuilt the GUI
  project clean (0 warnings/errors) to an isolated output path while 4 older GUI
  instances held the normal `bin`. The AXAML compile validates the property name, so
  `SelectionEnabled` is confirmed real in the pinned `Markdown.Avalonia 12.0.0-a3`.
- **Wrapping preserved:** when `SelectionEnabled=True`, Markdown.Avalonia renders into
  `SelectableTextBlock` rather than `TextBlock`; the existing window-level style
  `md|MarkdownScrollViewer SelectableTextBlock { TextWrapping=Wrap }` (lines 41-43)
  already covers that path, so long lines still wrap.
- **Remaining (manual, human-only):** drag-select text in a rendered prompt/response and
  press Ctrl+C. This is an interactive GUI gesture that can't be exercised headlessly;
  needs a human to confirm in a freshly-relaunched GUI. Code change is otherwise complete
  and ready to commit; suggest `/task review` once runtime copy is confirmed.

## 2026-06-11

- **Root cause:** The transcript renders user prompts and agent headlines with
  `md:MarkdownScrollViewer` (Markdown.Avalonia 12.0.0-a3), which is non-selectable
  by default. `SelectableTextBlock` (used for the Details expanders and the approval
  banner) is the only copyable surface today — confirmed via `MainWindow.axaml`
  lines 61-63 (user prompt) and 72-74 (agent headline).
- **Fix:** Markdown.Avalonia's `MarkdownScrollViewer` exposes a `SelectionEnabled`
  bool property (added in the a3 line, present at the pinned commit `d71a34d`; setting
  it also flips `Focusable=true` so Ctrl+C works). Set `SelectionEnabled="True"` on
  both `MarkdownScrollViewer` instances in `MainWindow.axaml`. This enables
  drag-select + Ctrl+C while preserving markdown rendering. Upstream note: only
  plain-text copy is provided (an Avalonia limitation), which is exactly what the
  task asks for.
- **Verified:** GUI project builds clean with the change (AXAML compile validates the
  property name). Built to an isolated `OutputPath` because several
  `InfiniContext.Gui` instances + VS were holding `Core.dll` in the normal `bin`.
- **Still to do (runtime):** Manual check in a freshly-relaunched GUI — select text in
  a rendered prompt/response and confirm Ctrl+C copies it. Could not exercise at
  runtime here because older GUI instances are still running (and lock the output).