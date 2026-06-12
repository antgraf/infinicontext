# Issues - GUI app display configuration issue

## 0001 - Window jumps to saved monitor after launch (no pre-show placement)
- **Logged:** 2026-06-12 by task/0021
- **Kind:** followup
- **Where:** src/InfiniContext.Gui/App.axaml.cs (deferred VM bring-up), src/InfiniContext.Gui/Views/MainWindow.axaml.cs (TryRestoreWindowPosition)
- **Status:** resolved (2026-06-12) вЂ” implemented the synchronous pre-show placement load described
  below. `App` now loads `gui-settings` before `Show` and seeds the placeholder VM; position is
  applied pre-show (guarded one-shot) and the off-screen check moved to `OnOpened`. See progress.md.
- **Body:** `App.OnFrameworkInitializationCompleted` shows the window with a placeholder VM
  (defaults) for fast paint, then swaps in the live VM once `CreateAsync` finishes loading
  `GuiSettings`. Window position is therefore applied *after* the window is already visible on the
  primary monitor, producing a one-shot jump to the persisted monitor on launch. This matches the
  pre-existing behaviour of the persisted *size* (which also resizes post-show), so it was left
  consistent rather than special-cased. A flicker-free fix would preload the small placement subset
  of `gui-settings.json` synchronously before `Show` and seed the placeholder window with it вЂ” a
  larger change to the startup sequence, out of scope for the display-recall fix.
