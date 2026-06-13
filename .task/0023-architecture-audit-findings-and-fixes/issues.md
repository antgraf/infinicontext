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
