# Issues - Architecture audit findings and fixes

## 0001 - ApprovalSinkFactoryTests env-var parallelism flake
- **Logged:** 2026-06-12 by (Phase D session)
- **Kind:** bug
- **Where:** tests/InfiniContext.Tests/Cli/ApprovalSinkFactoryTests.cs (Auto_mode_default_factory_picks_cli_service_from_approval_auto_mode)
- **Status:** open
- **Body:** The test sets `ANTHROPIC_API_KEY` to null and restores it in a finally. Run under the
  full parallel suite it intermittently fails ("Expected type AutoApprovalSink..."); run in isolation
  all 13 cases pass. Root cause is shared-process env-var mutation racing other tests that read
  `ANTHROPIC_API_KEY` — exactly the hazard CLAUDE.md warns about for `INFINICONTEXT__*`. Not caused by
  the Phase D changes. Fix would isolate the env-var (e.g. an env-var fixture/collection that
  serializes these tests, or refactor the factory to take the key explicitly rather than reading the
  process env). Out of scope for the audit fixes.

## 0002 - M3 sub-point deferred: auto-mode still auto-allows shell/network tools
- **Logged:** 2026-06-12 by (Phase D session)
- **Kind:** followup
- **Where:** src/InfiniContext.Core/Mcp/AutoApprovalSink.cs
- **Status:** open
- **Body:** The audit's M3 fix also recommends routing *any* shell- or network-capable tool to the
  human `ask` fallback rather than auto-allowing, on top of the data-framing + injection-axis
  escalation that were implemented. Defaulting that on would disable auto-approval for `Bash` (and
  similar) entirely — a material product/UX change that should be an operator decision, ideally
  behind an `AutoApprovalOptions` flag. The implemented changes (untrusted-data framing so injection
  can't flip the verdict; escalate a contradictory injection-flagged allow) already close the actual
  bypass vulnerability. Deferred pending an operator call on the policy default.

## 0003 - ci-macos.yml built the full slnx, which can't build on macOS
- **Logged:** 2026-06-12 by (Phase E session)
- **Kind:** bug
- **Where:** .github/workflows/ci-macos.yml
- **Status:** resolved (fixed in Phase E)
- **Body:** Discovered when dispatching `ci-macos.yml` to validate the new host-lifecycle smoke: the
  `dotnet build/test InfiniContext.slnx` steps fail on macOS with NETSDK1073 because the slnx now
  carries `InfiniContext.Gui.UiaDriver`/`.UiaTests` (net10.0-windows + `Microsoft.WindowsDesktop.App.WindowsForms`,
  added by task 0020) which cannot build off-Windows. Earlier `ci-macos.yml` runs passed only because
  the slnx predated the UIA projects (verified against the prior run's restore list). The Windows-only
  UIA suite is already covered by `ci-uia.yml`. Fix: scope `ci-macos.yml` to
  `tests/InfiniContext.Tests/InfiniContext.Tests.csproj`, which transitively builds Core/Cli/Gui/
  FakeClaude/SupervisorHostProbe — everything the cross-platform + `[MacOnlyFact]` suite needs.
  Resolved in this phase rather than deferred because a working dispatch is the deliverable for
  audit item 7.

## 0004 - M21 sub-point deferred: transcript panel virtualization
- **Logged:** 2026-06-12 by (Phase F session)
- **Kind:** followup
- **Where:** src/InfiniContext.Gui/Views/MainWindow.axaml.cs (ShowTranscript) + MainWindow.axaml (TranscriptScroll)
- **Status:** open
- **Body:** M21's fix list includes "virtualize transcript panels". The other three M21 items shipped
  (deleted the dead `TranscriptViewModel`; capped `Turns` at 500 with front-eviction; auto-retire the
  rotation predecessor + a manual close affordance). True virtualization was deferred: the transcript
  is a code-behind-built non-virtualizing `ItemsControl` wrapped in a pinned `ThemeVariantScope` inside
  the outer `TranscriptScroll` `ScrollViewer`, and auto-scroll drives that outer scroller. Avalonia's
  `ItemsControl` won't virtualize unless it owns its scrolling, so engaging virtualization means
  swapping to a `ListBox`/`ItemsRepeater` that scrolls internally and rewiring auto-scroll + the
  theme-variant pinning — the exact area whose detach/re-resolve cascade caused the task 0014/0015
  InvalidCastException crashes, and it has thin automated coverage. Capping `Turns` already bounds the
  realized visual tree per session (the unbounded growth was the actual leak), so the remaining win is
  incremental. Pick up alongside the broader Stage 3 GUI virtualization items in `docs/todo.md` §D
  ("Lazy-loading large tool results" / `VirtualizingStackPanel`). Source: `docs/architecture-review-2026-06-11.md` M21.
