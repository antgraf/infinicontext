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
