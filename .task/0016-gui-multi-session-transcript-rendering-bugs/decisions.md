# Decisions - GUI multi-session transcript rendering bugs

## D1 - Root cause: detaching realized Markdown.Avalonia visuals is unsafe

All three reported symptoms trace to one mechanism. Detaching a realized
`MarkdownScrollViewer` subtree from the logical tree (any `ContentControl.Content` swap)
changes the subtree's inherited `ActualThemeVariant`, which makes each
`DynamicResource` leg of Markdown.Avalonia's `{mde:DivideColor Blue, SystemBaseHighColor, 0.45}`
foreground setters (selector `ctxt|CCode`, i.e. inline code spans) re-resolve against a
truncated tree and publish `UnsetValue`. Markdown.Avalonia 12.0.0-a2's `DivideConverter`
then falls back to `values[0]` - the raw `Avalonia.Media.Color` literal (`Blue`) - and
Avalonia publishes that Color into the `IBrush` `Foreground` property ->
`InvalidCastException` mid-`ClearLogicalParent`. This is upstream bug
whistyun/Markdown.Avalonia#183 (and the earlier #152), fixed by PR #184 in 12.0.0-a3
(published 2026-04-15). Confirmed via a web-research pass against the repo + NuGet.

Downstream effects observed in the 2026-06-11 repro:

1. Second "+ session" click: `AttachRowForContext` set `FocusedSession` -> transcript
   rebuild detached session 1's view -> the cast exception aborted the attach **inside
   the spawn try/catch** -> status said "Failed to spawn session:" although the child
   process was alive and streaming; `SelectedNode` was never set, so the tree still
   highlighted the first (idle) session; the half-completed content swap left the pane
   corrupted (foregrounds broken - only inline-code chips visible).
2. Manual session switches kept detaching/re-attaching live markdown trees -> blank
   pane for the new session, and eventually the same exception escaped through the
   TreeView selection path and crashed the app (the reported stack).

## D2 - Per-session transcript views, visibility-toggled, never detached while alive

`TranscriptHost` is now a `Panel`. `MainWindow.ShowTranscript` lazily creates ONE
`ItemsControl` per session row (bound once to `row.Turns`), parents it under the panel
for the row's whole lifetime, and switches focus by flipping `IsVisible` only. No
detach on switch -> no variant cascade -> no cast crash, no corrupted re-style, no
blank pane. Bonus: switching back to a session preserves its realized visuals
(scroll/expander state aside) instead of re-templating the whole transcript.

Rejected alternatives:
- Keep rebuild-per-switch + try/catch: catching mid-cascade property-system exceptions
  leaves the tree in an undefined state.
- Bind a shared ItemsControl per task 0014: container recycling corrupts across
  sessions (the original bug).

## D3 - ThemeVariantScope pins each view's variant (detach firewall)

Each cached view is wrapped in a `ThemeVariantScope` whose `RequestedThemeVariant` is
pinned to the window's `ActualThemeVariant` (and kept in sync via the window's
property-changed hook, so OS light/dark flips still restyle). With the variant pinned
locally, the **one remaining detach** - pruning a closed session's view in
`PruneClosedTranscripts` - cannot change descendant `ActualThemeVariant`, so the
ColorTextBlock MultiBinding re-resolution never fires during removal.

## D4 - Spawn failures vs UI-attach failures are reported separately

`SpawnSessionForTaskAsync` now wraps `CreateSessionAsync` and `AttachRowForContext` in
separate try/catches, logs both, and includes `ex.Message` in the first line passed to
`ShowError` (the status strip shows only the first line - previously it displayed a
bare "Failed to spawn session:" with zero detail).

## D5 - Selection routes through SelectedNode

`AttachRowForContext` sets `SelectedNode` (the TreeView-bound property) before
`FocusedSession`, so tree highlight + derived pointers move together on spawn.

## D6 - Markdown.Avalonia pinned to 12.0.0-a3 (upstream fix), structural fix kept anyway

The package pin moves a2 -> a3, which contains the upstream converter fix for D1
(PR #184: invalid leg -> `UnsetValue` / `ImmutableSolidColorBrush` instead of a raw
`Color`). The per-session-view rework (D2/D3) stays regardless: it independently fixes
the corrupted-restyle and blank-pane symptoms (those came from swapping/re-templating
live transcripts, not just from the cast), avoids paying a full re-template per focus
switch, and keeps us insulated from the same crash class if a future alpha regresses -
the a-line has shipped this category of bug twice (#152, #183).
