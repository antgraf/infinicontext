# Progress

## 2026-06-04

- Task created.

### Root-cause investigation (both bugs)

**Bug 1 тАФ context % > 100% (e.g. "359% of 1M").** The nudge text comes from
`ThresholdInjector` ("Context is critical ({pct}% of {WindowTokens})"); past sessions copied
it into their `progress.md` flush markers. `pct` is computed by `ContextPressureMonitor` as
`used / window`. Root cause: `ContextPressureMonitor.HandleResult`
(`src/InfiniContext.Core/Tasks/ContextPressureMonitor.cs:99`) sums
`input_tokens + cache_creation_input_tokens + cache_read_input_tokens` straight off the
**result-level** `usage` object. Verified against `tests/fixtures/nested-tool.jsonl`: the
result-level `usage.cache_read_input_tokens` (47178) is the **sum across every API-call
iteration in the turn** (18921 + 28257 тАФ the two distinct assistant-message calls). A turn
with K tool-loop iterations therefore reports ~K ├Ч the real context occupancy; with enough
iterations the ratio blows past 100% (359/473/596/1425% seen in the field). The true
"how full is the window" figure is a **single** API call's `input + cache_creation +
cache_read`, which can never exceed the window тАФ and that per-call usage is carried on each
`assistant` event's `message.usage` (sub-agent calls, identifiable by `parent_tool_use_id`,
have their own separate window and must be excluded). Window resolution itself is fine
(init reports `claude-opus-4-7[1m]`; settings map keys on it тЖТ 1M).
Fix: drive threshold detection off main-agent `AssistantEvent`s (per-call footprint);
keep `ResultEvent` only to cache the authoritative window from `modelUsage.contextWindow`.

**Bug 2 тАФ chat view corrupted / sessions mixed after a session flip.** Not a data-layer
leak: each `SessionRowViewModel` owns its own `Turns` and subscribes only to its own
`context.EventObserved`; `CreateSessionAsync` always spawns a fresh claude process (no
resume), so there is no shared conversation. This is the view-layer "Fix 2" already scoped
in task 0013 (`.task/0013.../plan.md:41`): the central `ItemsControl` is bound to
`{Binding FocusedSession.Turns}` (`MainWindow.axaml:274`) and its `ItemsSource` is **swapped**
between two sessions' collections on focus change. The realized per-turn subtree тАФ
`md:MarkdownScrollViewer` from the alpha **Markdown.Avalonia 12.0.0-a2** тАФ does not tear down
cleanly across the rebind, leaving orphaned visuals from the previous session mixed into the
new one ("XAML tree corruption", matching the operator's report that it renders fine until a
flip). Markdown.Avalonia has no stable 12.x release, so a version bump isn't available.
Planned fix: host the transcript in a `ContentControl` keyed on `FocusedSession` so the
**entire** transcript subtree is rebuilt fresh on each flip (no `ItemsControl`/control reuse
across sessions), preserving markdown. Requires GUI run to verify (rendering can't be
asserted headlessly).

### Fixes landed (working tree, not yet committed)

**Bug 1 - DONE (unit-verified).**
- `ContextPressureMonitor` (`src/InfiniContext.Core/Tasks/ContextPressureMonitor.cs`) now measures
  occupancy per API call from each main-agent `AssistantEvent`'s `message.usage`
  (`input + cache_creation + cache_read`), which is bounded by the window. `ResultEvent`s are no
  longer used for the threshold - only mined for the authoritative window via
  `modelUsage.contextWindow` (cached in `_windowFromResult`, reset per session). Sub-agent
  assistant events (non-null `ParentToolUseId`) are skipped (separate window). Window fallback
  to the settings `ModelContextWindows` map is unchanged.
- Tests: rewrote `ContextPressureMonitorTests` to drive firing via assistant events; added two
  regression pins - `ResultSummedUsage_does_not_drive_threshold` (3.59M summed result.usage ->
  no crossing) and `Subagent_assistant_events_are_ignored`. Updated `SessionManagerTests`
  (`BuildAssistantEvent` helper, window from default settings map) and the
  `FakeClaudeIntegrationTests` pressure test (emits a window-seeding result then an assistant
  event that carries the 130k footprint). Full suite green: **1062 passed, 3 skipped** (POSIX).

**Bug 2 - IMPLEMENTED, needs GUI verification.**
- `MainWindow.axaml`: the transcript ItemsControl is now wrapped in a `ContentControl` bound to
  `FocusedSession` (inner `ItemsControl` binds `Turns`). On a session flip the whole subtree -
  ItemsControl + every turn card + every `MarkdownScrollViewer` - is discarded and rebuilt fresh,
  instead of reusing one ItemsControl whose `ItemsSource` is swapped. Streaming within a single
  session does NOT rebuild (Content reference is stable; only `Turns` mutates). The named
  `TranscriptScroll` ScrollViewer stays outside the ContentControl so the auto-scroll
  code-behind is untouched. Gui project builds clean (compiled-XAML pass OK).
- Open item: operator must run the GUI and reproduce the auto-rotate / manual-spawn-on-same-task
  flow (ideally with a successor whose first card has markdown - code fences, lists) to confirm
  the mixture/misrender is gone. If residual single-session markdown glitches remain, that is a
  separate alpha-Markdown.Avalonia issue (no stable 12.x exists), out of scope here.

### Handoff (hard-nudge flush, 2026-06-04)

Both fixes are complete in the working tree; **nothing is committed**. The live host that fired
this 1160%-of-1M nudge is the operator's running app, which does NOT yet have the Bug-1 fix - so
the over-100% report is expected until it is rebuilt/restarted with the new
`ContextPressureMonitor`. That is itself confirmation of the diagnosis.

State of play for the next session:
1. **Bug 1**: done, unit-verified (1062 passed). No further work needed beyond review/commit.
2. **Bug 2**: ContentControl fix landed in `MainWindow.axaml`; Gui builds clean. Still needs the
   operator to run the GUI and reproduce auto-rotate / manual-spawn-on-same-task to confirm the
   mixture/misrender is gone. Not yet visually verified.
3. **Open question put to operator** (unanswered): commit now on `task/0014...` branch, or hold
   until Bug 2 is visually verified.
4. Build/test note: the full-solution build is blocked by a DLL lock on
   `src/InfiniContext.Cli/bin/.../InfiniContext.Core.dll` (held by VS Insiders + two running
   `InfiniContext.Cli` processes - do NOT kill, one is likely this session's sidecar). Work
   around it with `dotnet test tests/InfiniContext.Tests/InfiniContext.Tests.csproj
   -p:BuildProjectReferences=false` and per-project builds.