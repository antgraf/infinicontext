# Progress

## 2026-06-12

- Task created.
- Spec contradiction found: title says "Avalonia GUI app display startup problem"
  but description.md cites "Issue #1 from 0020 issues.md", which is the unrelated
  mojibake artifact-corruption bug. Asked operator twice to disambiguate; both
  questions dismissed. Proceeding on the title (operator's direct words) = a GUI
  display/startup problem.
- Note on the mojibake interpretation: ruled out the 0020 reporter's "pin UTF-8"
  theory. On .NET 10, File.ReadAllText/WriteAllText and Encoding.Default are all
  UTF-8; every explicit Encoding.* in src/ is already UTF8; TaskFolder copy streams
  raw bytes. No CP1251 path exists in managed code, so that fix would be a no-op.
  Real mojibake mechanism (if pursued) is still unidentified.
- GUI startup investigation: could not reproduce a display failure.
  * One live GUI host (PID 30484, no args) HAS a working window ("InfiniContext",
    responding), started 10:14.
  * Four window-less InfiniContext.Gui.exe processes are MCP approval SIDECARS
    (--mcp-approval-server <pipe>) - headless by design, not a display bug.
  * Latest gui log (gui-20260611-234950) shows a clean startup, no errors.
  * Build currently BLOCKED: running Debug GUI + VS lock InfiniContext.Core.dll
    (MSB3027). Cannot rebuild/run a fresh instance until those are closed.
- BLOCKER: need a one-line description of the actual startup/display symptom
  (window never appears? off-screen/wrong monitor? blank? crash?) to repro + fix.
- Possible side issue (not this task): 4 sidecars from 10:17 and 10:51 still alive
  long after their sessions - looks like leaked sidecar processes.
- Operator clarified: title is misleading Haiku noise; real issue IS the mojibake
  bug in description.md (0020 #0001). Asked to consider the report critically.
- RESOLVED via diagnosis (see research.md). Mojibake reproduced on demand: agent
  Write tool emits CP1251 bytes on this ANSI=1251 (Cyrillic) Windows locale; transform
  is UTF-8 -> decode-CP1251 -> re-encode-UTF-8. Confirmed at byte level for U+2014,
  U+2192, U+00A7.
  * Corruptor is the harness write tool, NOT InfiniContext host code (host is UTF-8
    clean throughout). Corroborated by StatusGlyph.cs comment - same bug on a SOURCE
    file the host never writes.
  * 0020 report's "host must pin Encoding.UTF8" fix = misdirected/no-op.
  * 0020 report's "Gui/ -> Gui\" claim did NOT reproduce (forward slash preserved).
  * Real fix = enable Windows "Use Unicode UTF-8 worldwide" (ACP 1251 -> 65001).
    Keep pure-ASCII artifacts as belt-and-suspenders. No repo code change required.
- Cleaned up tmp/enc-probe probe file. Updated 0020 issues.md #0001 with corrected
  diagnosis. NO code change made for the artifact bug (would have been cargo-cult).
- Confirmed write-path mechanism empirically: bash printf -> correct UTF-8 (e2 80 94);
  harness Write tool -> CP1251 mojibake, same folder/disk. So it is the write path that
  crosses the Windows ANSI codepage, not the OS/disk. UTF-8-direct writers (Node fs,
  .NET File.WriteAllText, printf) are immune; standard Claude Code uses that path.
- Follow-up (operator asked to find ALL encoding/process boundaries): audited every
  boundary (see research.md table). Result:
  * Child processes (claude, light-model): stdin/stdout/stderr all UTF-8. OK.
  * host<->sidecar pipe: UTF-8. OK. File I/O: UTF-8. OK.
  * FOUND + FIXED: sidecar<->claude MCP JSON-RPC over Console.In/Out was NOT UTF-8 (used
    Windows console codepage). ApprovalSidecarHost.cs:58-59 now wraps
    Console.OpenStandardInput/Output in UTF8Encoding(no BOM). Affects approval-payload
    integrity (not file writes). Covers GUI + CLI hosts.
  * #6 CLI console display: NOW FIXED too (operator asked). Cli/Program.cs pins
    Console.OutputEncoding + InputEncoding to UTF8(no BOM) right after the sidecar
    short-circuit, before any Console/AnsiConsole write; guarded in try/catch for
    redirected handles. Sidecar branch excluded; GUI unaffected. Compiles clean (Release).
- Build: Core + CLI compile clean in Release. Full Debug build + `dotnet test` BLOCKED by
  the running GUI (PID 30484) + VS locking Core.dll - must close them, then run the suite.