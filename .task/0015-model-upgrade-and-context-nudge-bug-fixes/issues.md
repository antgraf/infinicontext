# Issues - Model upgrade and context nudge bug fixes

## 0001 - "Details" expander always visible (IsNotNull on an int)
- **Logged:** 2026-06-11 by (current session)
- **Kind:** bug
- **Where:** src/InfiniContext.Gui/Views/MainWindow.axaml (TurnNodeTemplate, Details Expander)
- **Status:** open
- **Body:** The per-turn "Details" Expander binds `IsVisible="{Binding Children.Count,
  Converter={x:Static ObjectConverters.IsNotNull}}"`. `Children.Count` is an `int`, which is never
  null, so the converter always returns true and the expander renders even for turns with zero
  children. Intent was clearly to hide it when a turn has no detail children (a `Count > 0` check,
  e.g. an int-to-bool / GreaterThanZero converter, would be correct). Pre-existing - preserved
  verbatim when the template was lifted into the `TurnNodeTemplate` keyed resource during the task
  0015 Issue 3 transcript-host rebuild; not introduced by that change. Out of scope for task 0015.
