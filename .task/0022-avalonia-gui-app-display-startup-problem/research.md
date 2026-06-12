# Research: task-artifact mojibake corruption

NOTE: this file is deliberately pure-ASCII. Non-ASCII bytes written on this machine
get corrupted (that is the very bug documented here). All affected characters below
are referred to by Unicode codepoint + hex bytes, never as literal glyphs.

## TL;DR

- The mojibake is REAL and was reproduced on demand (see "Reproduction").
- Root cause: a file-writing tool emits bytes using the system ANSI code page
  (Windows-1251) instead of UTF-8. The transform is UTF-8 -> decode-as-CP1251 ->
  re-encode-as-UTF-8.
- The writer is the AGENT HARNESS's Write/Edit tool, NOT InfiniContext host code.
  Proof: the same corruption was previously hit on src/.../StatusGlyph.cs - a C#
  SOURCE file that InfiniContext never writes (see its class comment).
- The 0020 report's proposed fix ("InfiniContext host code must pin Encoding.UTF8 on
  every artifact read/write") is misdirected. Host code is already UTF-8 throughout
  and is not the corruptor. Applying that "fix" would be a no-op.
- The 0020 report's secondary claim (backtick `Gui/` rewritten to `Gui\`) did NOT
  reproduce. Treat that part of the report as unreliable.

## Environment (the actual root cause)

- System ANSI code page (HKLM\SYSTEM\CurrentControlSet\Control\Nls\CodePage\ACP) = 1251.
- "Beta: Use Unicode UTF-8 for worldwide language support" = OFF (would set ACP=65001).
- So any program that writes text via the default ANSI encoding produces CP1251 bytes.

## Reproduction (2026-06-12)

Wrote tmp/enc-probe/probe.md via the agent Write tool containing U+2014, U+2192,
U+00A7, and a backtick-quoted "Gui/". Hex dump of the bytes on disk:

| Intended  | Codepoint | Correct UTF-8 | On disk        | CP1251 decode of UTF-8 bytes |
|-----------|-----------|---------------|----------------|------------------------------|
| em dash   | U+2014    | E2 80 94      | d0b2 d082 e2809d | E2->U+0432, 80->U+0402, 94->U+201D |
| arrow     | U+2192    | E2 86 92      | d0b2 e280a0 e28099 | E2->U+0432, 86->U+2020, 92->U+2019 |
| section   | U+00A7    | C2 A7         | d092 c2a7      | C2->U+0412, A7->U+00A7        |

Each on-disk sequence is exactly "take the correct UTF-8 bytes, decode them as
Windows-1251, then re-encode the resulting characters as UTF-8" - the canonical
mojibake round-trip. This matches the 0020 report's description of the em-dash
becoming three characters.

The backtick path stayed "Gui/" (bytes 47 75 69 2f) - forward slash preserved. The
report's "Gui/ -> Gui\" claim did not reproduce.

## Why InfiniContext host code is NOT the culprit

Static audit of src/:
- Every File.ReadAllText/WriteAllText/ReadAllLines call uses the .NET default, which
  on .NET 10 (Core) is UTF-8 (read with BOM detection, write without BOM). There is
  no ANSI path.
- Every explicit Encoding.* in the codebase is UTF8 (ApprovalChannel, ApprovalSidecar,
  SecretScrubbingEnricher, ClaudeSession/CliLightModelInvocation StandardOutputEncoding).
- No Encoding.Default, no Encoding.GetEncoding(1251), no Console.OutputEncoding hacks,
  no FileSystemWatcher that could rewrite artifacts.
- TaskFolder.CopyArtifactsToAsync streams raw bytes (no transcode).
- The only host read-modify-write of artifact content is /task review's
  UpdateFileUnderLockAsync (UTF-8, review-time only - not every turn).
- All path Replace() calls go backslash->forward-slash (for globbing/dedup/display),
  the OPPOSITE of the reported Gui/ -> Gui\.

Corroborating prior art: src/InfiniContext.Gui/ViewModels/StatusGlyph.cs builds all
glyphs at runtime via char.ConvertFromUtf32 specifically because an earlier attempt
with literal Unicode chars "was being transcoded to CP1251 mojibake by the tool that
wrote this file in a Cyrillic-locale Windows environment." That file is host SOURCE,
never written by InfiniContext - so the writer is the dev's editing tool, i.e. the
agent harness, in the CP1251 locale.

## Recommended resolution (in priority order)

1. ROOT-CAUSE machine fix: enable Windows "Use Unicode UTF-8 for worldwide language
   support" (Control Panel > Region > Administrative > Change system locale; sets
   ACP=65001 after reboot). This makes the ANSI default UTF-8 and eliminates the bug
   for BOTH source files and task artifacts, regardless of which tool writes them.
   This is the only fix that addresses the cause rather than a symptom.

2. KEEP the existing mitigation as belt-and-suspenders: task artifacts stay pure-ASCII,
   and source glyphs use char.ConvertFromUtf32 (already in place). Cheap, robust,
   invariant under the bad round-trip.

3. DO NOT add "pin Encoding.UTF8" to InfiniContext host file I/O. It is already UTF-8
   and is not the writer; the change would be cosmetic and imply a false root cause.

4. OPTIONAL host-side defense (only if desired): have TaskFolder drift/scan flag
   artifacts that contain the CP1251-mojibake byte signature (e.g. a run of bytes that
   decodes cleanly as CP1251 of common UTF-8 punctuation) and warn, so future
   corruption is caught early rather than at a failed Edit match. This detects, does
   not prevent - prevention is item 1.

## Disposition (artifact mojibake)

The artifact-write mojibake (0020 #0001) requires NO InfiniContext code change. That
defect lives in the environment (ANSI=CP1251) plus the external editing tool's
write path, which crosses the Windows ANSI codepage. Proven: bash printf to the same
folder writes correct UTF-8 (e2 80 94) while the harness Write tool writes CP1251
mojibake (d0b2 d082 e2809d). A UTF-8-direct writer (Node fs, .NET File.WriteAllText)
is immune; InfiniContext host I/O is UTF-8-direct, hence not the corruptor.

## Boundary audit (follow-up request: find ALL encoding/process boundaries)

Every place InfiniContext crosses an encoding or process boundary:

| # | Boundary | Location | Status |
|---|----------|----------|--------|
| 1 | claude child stdin/stdout/stderr | ClaudeSession.cs:165-167 | OK - UTF-8 pinned |
| 2 | light-model child std streams | CliLightModelInvocation.cs:160-162 | OK - UTF-8 pinned |
| 3 | host<->sidecar named pipe | ApprovalSidecarHost.cs:50-51; ApprovalChannel.cs:98-99 | OK - UTF-8 pinned |
| 4 | sidecar<->claude MCP JSON-RPC over stdio | ApprovalSidecarHost.cs:58-59 | WAS BROKEN - now fixed |
| 5 | file I/O (artifacts/index/settings) | Tasks subsystem etc | OK - UTF-8 (.NET default) |
| 6 | CLI console display/input | Cli/Program.cs, MultiSessionHost (AnsiConsole) | WAS console CP - now fixed (UTF-8) |
| 7 | MCP config temp file | ApprovalSidecarLauncher.cs:84 | OK - UTF-8 (ASCII content) |

### #4 fix (applied)

The sidecar handed raw Console.In/Console.Out to McpServer as the JSON-RPC transport
with claude. Console.In/Out honor Console.Input/OutputEncoding, which default to the
Windows console code page (OEM/ANSI on a non-UTF-8 locale), NOT UTF-8. MCP stdio is
UTF-8 by spec, so non-ASCII in a JSON-RPC payload (e.g. a Write/Edit tool's content
forwarded for approval) would mojibake into the approval prompt, the log, and the
light-model safety review. (It does NOT write files, so it is NOT the 0020 artifact
bug - but it is a real boundary defect.) Fixed by wrapping Console.OpenStandardInput/
Output in UTF8Encoding(no BOM), mirroring the pipe pattern 8 lines above. Covers both
the GUI and CLI hosts (both route through ApprovalSidecarHost.RunAsync).

Build: Core compiles clean in Release. Full Debug build + test run pending - currently
blocked by the running GUI (PID 30484) + VS holding InfiniContext.Core.dll. Re-run
`dotnet test InfiniContext.slnx` once those are closed.

### #6 fix (applied)

CLI console display/input used the OEM/ANSI code page, so non-ASCII transcript text
(em-dash, arrows, StatusGlyph badges, CJK) rendered as mojibake in a non-UTF-8 terminal.
Fixed: Cli/Program.cs now sets Console.OutputEncoding and Console.InputEncoding to
UTF8Encoding(no BOM) immediately after the sidecar short-circuit and before any
Console/AnsiConsole write, so Spectre.Console picks up the UTF-8 writer. Guarded in
try/catch - setting the encoding throws when the handle is redirected (tests, pipes) and
this display-only concern must not block startup. The sidecar branch is excluded (it owns
its own raw UTF-8 std streams; stdout reserved for JSON-RPC). The GUI host is unaffected
(renders via Avalonia, no console codepage). Compiles clean in Release.
