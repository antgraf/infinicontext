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
- **Verification blocker, refined (session resume 2026-06-12):** confirmed all of the above is
  committed in 6d0280f and the working tree is clean; the Debug bin (built 16:36, after the 10:43
  commit) already carries the code. The precise obstacle to a hermetic live check is not only the
  bin lock: `App.OnFrameworkInitializationCompleted` calls `GuiSettings.LoadAsync()`/`SaveAsync`
  with NO path argument, so the GUI always reads/writes the single shared `~/.infinicontext/
  gui-settings.json` -- there is no env/arg override. With two operator GUI instances live (and
  rewriting that file on their close), launching a third to verify risks disturbing the operator's
  saved layout. Hermetic options for a future session: (a) add a gui-settings path override (env
  var, mirroring INFINICONTEXT__TASKS__DBPATH) then drive the UIA harness against a temp file on
  DISPLAY2; or (b) operator closes their instances and does the manual move-to-DISPLAY2/restart
  check. Two monitors are connected: DISPLAY1 primary (X=0), DISPLAY2 (X=-1920) -- recall is
  testable here. Left committed+unit-tested state as the deliverable; no live launch performed.
- **Operator-reported bug + fix (2026-06-12):** operator ran the committed build and hit two
  symptoms, single root cause:
  1. Saved-maximized launch came up maximized but OFFSET from the monitor's top-left corner.
  2. Saved-maximized-on-secondary-monitor launch came up on the PRIMARY at that same offset;
     manually toggling un-maximize/maximize snapped it to the correct monitor and position.
  **Root cause:** the persisted maximized state was applied PRE-SHOW in
  `MainWindow.OnDataContextChanged` (right after `Position` was set, on the placeholder VM before
  `Show`). A maximize computed before the window is realized has no real screen to maximize
  against -- Avalonia maximizes relative to the PRIMARY monitor and treats the manual `Position` as
  a literal origin offset instead of snapping to the work area. Hence maximized-but-offset, and
  wrong-monitor when the saved position was on the secondary. The manual toggle worked because by
  then the window was realized on a screen and Avalonia recomputed the geometry.
  **Fix:** keep restoring `Position` pre-show (preserves flicker-free monitor placement), but
  DEFER the maximize to `OnOpened` -- after the window is realized on the restored monitor, so the
  maximize snaps to that monitor's working area. Restructured `OnOpened` so its off-screen-check
  early-return no longer skips the maximize: step (1) off-screen correction, then step (2) apply
  `WindowState = Maximized`. Removed the pre-show maximize block from `OnDataContextChanged`.
  Only `MainWindow.axaml.cs` changed; `GuiSettings`/VM/`App` untouched, so the 3 GuiSettingsTests
  still apply unchanged.
  **Verified:** isolated GUI build 0/0 (output redirected to %TEMP%\icx-verify-0021-bin because the
  operator's two live instances lock the normal bin); `dotnet format --verify-no-changes` clean on
  the touched file. Live visual confirmation pending (operator to run the fixed build). NOT yet
  committed.
- **Hermetic verification unblocked + maximize fix CONFIRMED live (2026-06-12, session resume):**
  - Implemented the documented unblock (option a): `INFINICONTEXT__GUI__SETTINGSPATH` env override
    so a verification instance can point at a temp layout file instead of the shared
    `~/.infinicontext/gui-settings.json` (mirrors `INFINICONTEXT__TASKS__DBPATH`). Changes:
    `GuiSettings.cs` gained a `[JsonIgnore] SourcePath` that `LoadAsync` stamps and `SaveAsync`
    defaults to (`path ?? SourcePath ?? DefaultPath()`), so the override survives the VM's path-less
    `SaveLayoutAsync` with NO VM signature change; `App.axaml.cs` reads the env var (blank->null)
    and passes it to `LoadAsync`. New 4th `GuiSettingsTests` case covers the load->save-back
    round-trip. GUI build 0/0; all 4 GuiSettingsTests pass; `dotnet format --verify-no-changes` clean.
  - Drove the UIA smoke harness (`tmp/icx-smoke/verify-0021.ps1`, powershell 5.1) hermetically
    against the temp settings + FakeClaude: seeded `windowMaximized=true` with a Normal top-left on
    DISPLAY2 (X=-1920), launched the temp-bin GUI, read back the realized window rect via UIA
    (BoundingRectangle - no foreground steal / cursor move, minimal desktop intrusion).
    **RESULT: PASS** - window rect (-1920,23) 1920x1009, center on SECONDARY, fills DISPLAY2 work
    area (1920x1032). The operator-reported "maximized but offset on PRIMARY" symptom is fixed: the
    deferred-to-OnOpened maximize now snaps to the restored monitor's work area. Operator's real
    gui-settings.json untouched (override proven by the window opening per the temp seed).
  - **STATE: fix + override verified, format-clean, NOT committed.** Working tree has
    `MainWindow.axaml.cs` (maximize deferral) + `GuiSettings.cs` + `App.axaml.cs` + `GuiSettingsTests.cs`.
    Next: operator decision to commit (and optionally a manual eyeball, though UIA already confirms it).
- **Operator re-test + REAL root cause found & fixed (2026-06-12, same session):** operator ran the
  build: "properly maximized, but always on the MAIN display." The earlier seeded UIA test passed
  only because it hand-seeded a DISPLAY2 position, bypassing the PERSIST path. Wrote a full
  persist->restore round-trip harness (`tmp/icx-smoke/verify-0021-roundtrip.ps1`): open Normal on
  DISPLAY2 -> maximize via UIA -> graceful close (so SaveLayoutAsync flushes) -> relaunch.
  **Reproduced:** persisted file held the MAXIMIZED geometry as if windowed -
  `windowX=-1928,windowY=-8` (8px left of DISPLAY2's -1920 edge => off-screen) and
  `windowWidth/Height=1920x1009`. On restore, `OnOpened`'s off-screen check sees `-1928,-8` on no
  display and recenters onto PRIMARY -> maximizes on primary. **Root cause (proved by env-gated
  trace):** a maximize fires `PositionChanged` + Width/Height changes ~10ms BEFORE
  `Window.WindowState` flips to `Maximized`, so the synchronous "if (WindowState == Normal)" guards
  in `OnWindowPositionChanged` / the size handler still read Normal and captured the maximized
  origin/screen-size into `_lastNormalPosition` / `_lastNormalWidth/Height`. `OnClosed` (which DOES
  run before save) then faithfully persisted that poisoned snapshot. **Fix:** replaced the
  synchronous captures with a coalesced, deferred `QueueNormalGeometryCapture()` ->
  `CaptureNormalGeometry()` posted at `DispatcherPriority.Background`, so the synchronous WindowState
  flip lands first and the deferred capture correctly skips while maximized. Only `MainWindow.axaml.cs`
  changed for the fix (the temporary `GuiTrace` instrumentation added to diagnose was removed).
  **Verified on clean build (no trace):** round-trip harness now persists the WINDOWED geometry
  (`1100x700 @ -1770,150` on DISPLAY2, maximized=true) and restores **maximized on SECONDARY**
  (RESULT: PASS); the seeded maximize-on-secondary harness still PASS; 4 GuiSettingsTests pass;
  `dotnet format --verify-no-changes` exit 0 on all touched files. NOT committed.
  **Note for operator:** your existing `~/.infinicontext/gui-settings.json` may still hold a poisoned
  off-screen position from the buggy build; first launch of the fixed build will recenter it onto
  primary, but once you place+maximize where you want and close, it self-heals and sticks.