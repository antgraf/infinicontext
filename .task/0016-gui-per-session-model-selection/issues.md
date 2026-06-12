# Issues - GUI per-session model selection

## 0001 - GUI spawns ignore `settings.Session.ExtraArgs`
- **Logged:** 2026-06-11T20:40:00+02:00 by gui-smoke session
- **Kind:** bug
- **Where:** src/InfiniContext.Gui/Services/SessionHost.cs:376 (CreateSessionAsync SpawnRequest construction)
- **Status:** fixed (2026-06-11, same working tree вЂ” `SessionHost.BuildSpawnRequest` now forwards
  `settings.Session.ExtraArgs`; covered by `SessionHostSpawnRequestTests`)
- **Body:** Every CLI spawn path forwards `settings.Session.ExtraArgs` into `SpawnRequest.ExtraArgs`
  (`Program.cs:316`, `NewSessionCommand.cs:53`, `ResumeCommand.cs:64`, `TaskSlashCommand.cs:193`), but
  the GUI's `SessionHost.CreateSessionAsync` builds its `SpawnRequest` without `ExtraArgs`, so the
  `session.extraArgs` setting is silently dropped for all GUI-spawned sessions. Found during the
  model-selection smoke: `INFINICONTEXT__SESSION__EXTRAARGS__0/1` env overrides never reached the
  spawn argv (verified in the gui-20260611-202849.log spawn lines). One-line fix:
  `ExtraArgs = Settings.Session.ExtraArgs` in the request initializer; CLI/GUI parity test would
  belong in MainWindowViewModelTests or a SessionHost-level test.
