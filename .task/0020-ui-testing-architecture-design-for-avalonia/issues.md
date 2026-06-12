# Issues - UI testing architecture design for Avalonia

## 0001 - Task artifacts mojibake-corrupted after write (UTF-8 -> CP1251 -> UTF-8)
- **Logged:** 2026-06-12 by fable-0020
- **Kind:** bug
- **Where:** .task/0020-*/design.md, plan-xunit-v3-migration.md, progress.md
- **Status:** resolved (diagnosed in task 0022; see .task/0022-*/research.md)
- **Resolution (2026-06-12, task 0022):** Reproduced at byte level. Cause is the AGENT
  HARNESS write tool emitting CP1251 bytes on this ANSI=1251 (Cyrillic) Windows locale
  (UTF-8 -> decode-CP1251 -> re-encode-UTF-8), NOT InfiniContext host code - host I/O is
  UTF-8 throughout, so this issue's suggested "pin Encoding.UTF8 in host code" fix is a
  no-op. Corroborated by StatusGlyph.cs (same bug on a host SOURCE file the host never
  writes). The "Gui/ -> Gui\" claim did NOT reproduce. Real fix: enable Windows "Use
  Unicode UTF-8 worldwide" (ACP -> 65001); keep artifacts pure-ASCII as mitigation. No
  repo code change required.
- **Body:** Artifacts written with non-ASCII characters (em-dash U+2014, arrow U+2192,
  section sign U+00A7) were later found on disk with those characters replaced by their
  CP1251-misdecoded forms re-encoded as UTF-8 (e.g. em-dash became the three characters
  "vE'" mojibake). The corruption happened *between* sessions/turns, after locks were
  released - not at write time (a same-turn Edit on progress.md matched fine; a later-turn
  Edit on design.md failed to match). Suspect a host-side process (TaskFolder drift scan?
  lock channel? index refresh?) reads artifacts without specifying UTF-8 (gets the OS ANSI
  codepage, CP1251 on this machine) and writes them back. One file (design.md line ~37) also
  had a backtick-quoted `Gui/` changed to `Gui\`, suggesting a path-normalization pass over
  artifact *content* - that is corruption too if so. Repro likely: write any .task artifact
  containing an em-dash, wait for the host to scan, re-read bytes. Workaround adopted in
  this task: all artifacts rewritten pure-ASCII (invariant under the bad round-trip). Fix
  belongs in host code: every artifact read/write must pin Encoding.UTF8 explicitly.

## 0002 - Mojibake already committed in GUI source doc comment
- **Logged:** 2026-06-12 by fable-0020
- **Kind:** bug
- **Where:** src/InfiniContext.Gui/Views/NewTaskDialog.axaml.cs:92
- **Status:** resolved (2026-06-12, same session)
- **Resolution:** Repo-wide sweep executed over src/, tests/, docs/, .github/ (29 files).
  Two mojibake flavors found and restored to the intended Unicode (em/en-dash, arrow,
  ellipsis, section sign, multiply, check/cross marks): UTF-8-read-as-CP1251 and
  UTF-8-read-as-CP866, plus one half-repaired em-dash remnant in TaskSlashCommand.cs /
  ScopeResolver.cs. Replacements done via PowerShell byte-exact writes (NOT the agent edit
  tools, which are themselves the 0001 corruption vector on this machine). Historical
  .task/ artifacts (tasks 0003-0021) deliberately left untouched: several hits there are
  intentional quoted examples documenting the bug (0012 findings, 0004/0005 repro notes),
  and progress logs are append-only history. Build, full test suite (1110/1107/3) and
  dotnet format all green after the sweep.
- **Body:** The NewTaskRequest doc comment contains "vE'"-style mojibake (a CP1251-mangled
  em-dash) in the committed source - the same 0001 corruption mechanism, but already baked
  into the repo. Other source files may carry the same damage (StatusGlyph.cs was named in
  0001's diagnosis); a repo-wide sweep for the mojibake byte signature would be a cheap
  follow-up. Cosmetic (comments only) but worth cleaning before it spreads via copy-paste.
