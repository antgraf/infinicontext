# Progress

## 2026-06-11

- Task created from operator bug report (GUI multi-session: corrupted render on second session
  spawn, empty transcript on switch, InvalidCastException Color->IBrush on repeated switching).
- Root cause established (see decisions.md D1): one mechanism behind all three symptoms -
  detaching realized Markdown.Avalonia visuals on every focus switch trips upstream bug
  whistyun/Markdown.Avalonia#183 (a2's DivideConverter publishes a raw Color into IBrush when
  its DynamicResource leg unsets mid-detach). The exception also fired *inside the spawn
  try/catch*, which is why the GUI claimed "Failed to spawn session:" for a session that was
  alive and streaming, and why the tree selection stayed on the first idle session.
- Fixes landed (all building, 1069/0 tests, format clean):
  - `MainWindow.axaml`/`.axaml.cs`: transcript host is now a `Panel` with ONE cached
    `ItemsControl` per session (created on first focus, visibility-toggled, never detached
    while the session lives; closed sessions pruned). Each view wrapped in a
    `ThemeVariantScope` pinned to the window's variant as a detach firewall (D2/D3).
  - `MainWindowViewModel.SpawnSessionForTaskAsync`: spawn failure vs UI-attach failure now
    separately caught, logged, and reported with the actual exception message (D4).
  - `MainWindowViewModel.AttachRowForContext`: selection routed through `SelectedNode` so the
    tree highlights the newly spawned session (D5).
  - `InfiniContext.Gui.csproj`: Markdown.Avalonia 12.0.0-a2 -> 12.0.0-a3 (upstream fix, D6).
- Not yet done: manual GUI smoke test of the original repro (spawn two sessions under one task,
  switch back and forth) - needs the operator or an interactive run; headless suite has no
  Avalonia UI coverage for the view code-behind. Changes are uncommitted on branch
  task/0013-gui-session-transfer.

### Handoff to next session

Run the manual repro: GUI -> "+" on a task twice, wait for warmup, switch sessions repeatedly.
Expect: new session selected + focused on spawn, both transcripts render and survive switching,
no "Failed to spawn session:" unless the process really failed. Then commit (operator approval).
