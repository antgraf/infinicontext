# Progress

## 2026-06-12

- Task created.
- **Root cause:** `GuiSettings` persisted window size + maximized state but never the window
  *position*. With no `WindowStartupLocation`/`Position` ever set, the window always opened at the
  OS default placement on the primary monitor.
- **Fix implemented:**
  - `GuiSettings.cs` вЂ” added nullable `WindowX`/`WindowY` (physical screen pixels, PixelPoint
    space). Nullable so "never positioned" stays distinct from `0,0`; null is omitted from JSON.
    Deliberately not clamped (validity is screen-relative; the View validates against connected
    displays).
  - `MainWindowViewModel.cs` вЂ” added observable `WindowX`/`WindowY`, seeded from `GuiSettings` in
    the ctor and flushed in `SaveLayoutAsync`.
  - `MainWindow.axaml.cs` вЂ” `PositionChanged` handler captures the top-left while in Normal state
    (mirrors the existing Width/Height `_lastNormal*` snapshot pattern); `OnDataContextChanged`
    calls `TryRestoreWindowPosition()` which applies the saved `Position` **only if**
    `Screens.ScreenFromPoint` confirms the point is on a connected display (disconnected monitor в†’
    fall back to OS placement, never off-screen). Restore runs before the maximized-state apply so
    a maximize lands on the right monitor. `OnClosed` restores position from the Normal snapshot on
    a maximized-close, same as Width/Height.
- **Verified:** isolated `dotnet build` of the GUI succeeded (0 warn/0 err); 3 new `GuiSettingsTests`
  pass (position round-trip incl. negative X, null-stays-null, old-file-without-fields loads);
  `dotnet format --verify-no-changes` clean on all touched files.
- **Known limitation:** the deferred-VM architecture applies the restore after the window is already
  shown on the primary monitor, so there is a one-shot jump to the target monitor on launch вЂ” the
  same late-apply shape the persisted *size* already uses. Logged as issue #0001 (follow-up only).
- Could not run the full test suite / produce a release build: a live `InfiniContext.Gui` instance
  locks `Core.dll` in the GUI bin. Ran the new tests via an isolated `BaseOutputPath` instead.
- **Flicker-free placement implemented (resolves issue #0001).** The post-show jump is gone:
  - `App.axaml.cs` вЂ” loads the small `gui-settings.json` synchronously (`LoadAsync().GetAwaiter()
    .GetResult()`; safe to block since LoadAsync is `ConfigureAwait(false)` throughout) *before*
    showing the window, and constructs the placeholder VM seeded with it. Heavy bring-up
    (`SessionHost.StartAsync`) stays deferred; the same settings instance is handed to `CreateAsync`
    so the live VM doesn't reload.
  - `MainWindowViewModel` вЂ” new `public MainWindowViewModel(GuiSettings)` placeholder ctor;
    `CreateAsync` gained an optional preloaded-settings param (falls back to a load for tests).
  - `MainWindow.axaml.cs` вЂ” `TryRestoreWindowPosition` now applies `Position` **pre-show** (on the
    first attach, guarded by `_windowPlacementRestored` so the live-VM swap can't yank it back), so
    the window opens directly on the persisted monitor at the persisted size вЂ” no jump. The
    `Screens` off-screen validation moved to a new `OnOpened` handler (Screens isn't populated
    pre-show); a disconnected monitor recenters on the primary's working area. A mirror block in
    `OnDataContextChanged` syncs the live VM's `WindowX/Y` to the window's actual top-left so a
    correction/nudge persists on close rather than the stale loaded value.
- **Verified (flicker-free):** isolated GUI build 0/0; test project compiles against new signatures;
  3 `GuiSettingsTests` still pass; `dotnet format --verify-no-changes` clean on all touched files.
  Visual confirmation of the no-jump behavior still needs a real GUI launch (blocked by the bin lock
  from the running instance) or the UIA smoke harness.