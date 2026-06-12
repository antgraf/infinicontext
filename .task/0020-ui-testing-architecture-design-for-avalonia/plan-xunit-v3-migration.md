# xUnit v3 migration plan -- all test projects on the latest harness

Status: **proposed** (2026-06-12; file deliberately ASCII-only, see issues.md#0001).
Companion to `design.md` (which requires xunit v3 for the headless suite); this plan removes
the v2/v3 split entirely by migrating `tests/InfiniContext.Tests` to xunit.v3. Written to be
executable by another agent.

## Decision

- **Target:** `xunit.v3` **3.2.2** (latest stable, 2026-01-14 -- verified on NuGet) in every
  test project. New projects from `design.md` (`Gui.HeadlessTests`, `Gui.UiaTests`) are born
  on v3; `design.md` rev 2 reflects this.
- **Runner mode: VSTest**, i.e. keep `Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio`.
  Rationale: `ci.yml` rides on VSTest features -- `--logger trx`, `--collect:"XPlat Code
  Coverage"` (coverlet.collector), `--filter FullyQualifiedName~...` -- and
  `xunit.runner.visualstudio` 3.x runs v3 assemblies under VSTest unchanged. Switching to
  Microsoft.Testing.Platform (MTP, xunit.v3's native mode) would force rework of coverage
  collection and CI flags for zero current benefit. Revisit MTP as a separate follow-up if
  ever desired; it is explicitly out of scope here.
- **Sequencing:** land this **before** design.md Phase 1, as one isolated commit (easy
  bisect/revert), after design.md Phase 0 or in parallel with it -- the two don't conflict.

## Audit of v2-specific surface (done 2026-06-12 -- this is why the migration is cheap)

| v2->v3 breaking area | Usage in `tests/InfiniContext.Tests` | Action |
|---|---|---|
| `ITestOutputHelper` / `Xunit.Abstractions` (namespace removed in v3) | **0 uses** | none |
| `IAsyncLifetime` (`Task` -> `ValueTask` in v3) | **0 uses** | none |
| `[MemberData]` / `[ClassData]` (v3 stricter serialization analyzers) | **0 uses** (InlineData only) | none |
| Raw `Assert.*` (minor signature tightening in v3) | **5 calls in 4 files** (suite is FluentAssertions throughout) | fix on compile if flagged |
| Custom fact/theory attributes | 3 -- `WindowsOnlyFact`, `WindowsOnlyTheory`, `MacOnlyFact`; all set `Skip` in the ctor | pattern unchanged in v3; recompile as-is |
| Collection fixtures | 1 hit (`InfiniContextSettingsLoaderTests.cs`) | API unchanged in v3 |
| `xunit.runner.json` | none exists | none |
| `InternalsVisibleTo` from src projects | keyed on assembly name `InfiniContext.Tests`, which doesn't change | none |
| Linked `<Compile Include>` CLI sources | no xunit references in linked files; none defines `Main` | none (but see Exe note below) |

## Steps

### 1. Record the baseline (before touching anything)

```
dotnet test InfiniContext.slnx -c Release --no-build -v q
```

Note total/passed/skipped counts. The acceptance gate at the end is *identical counts*
(skips included -- the OS-conditional attributes must still register as skipped-on-wrong-OS,
not silently vanish).

### 2. csproj changes (`tests/InfiniContext.Tests/InfiniContext.Tests.csproj`)

```diff
   <PropertyGroup>
     <TargetFramework>net10.0</TargetFramework>
+    <OutputType>Exe</OutputType>
     ...
   </PropertyGroup>
   ...
-    <PackageReference Include="xunit" Version="2.9.3" />
+    <PackageReference Include="xunit.v3" Version="3.2.2" />
     <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
```

Notes:
- `<OutputType>Exe</OutputType>` is **required** -- v3 test projects are self-contained
  executables (xunit.v3 generates the entry point; none of the linked CLI sources defines a
  `Main`, so no collision). Side benefit: `dotnet run --project tests/InfiniContext.Tests`
  now runs the suite natively.
- `Microsoft.NET.Test.Sdk` 17.14.1 stays (v3 needs >= 17.8 for VSTest mode).
- `xunit.runner.visualstudio` 3.1.5 stays (3.x line serves both v2 and v3).
- `<Using Include="Xunit" />`, FluentAssertions 7.2.0, coverlet.collector 10.0.1,
  Serilog/Spectre packages: all unchanged.

### 3. Build and fix stragglers

`dotnet build InfiniContext.slnx`. Expected: clean, per the audit. If anything surfaces, it
will be one of: a raw `Assert.*` overload tightening (4 files listed above), or a new
xunit analyzer diagnostic (v3 ships stricter analyzers -- fix the diagnostic, don't
suppress; `TreatWarningsAsErrors` is not set, so warnings can't break the build, but don't
leave new ones behind).

### 4. Local verification matrix

All from repo root; every line must behave as before:

```
dotnet test InfiniContext.slnx -c Release                          # counts == step-1 baseline
dotnet test --filter FullyQualifiedName~ReplSmokeTests             # CLAUDE.md documented flow
dotnet test --filter "DisplayName~Repl_renders"                    # CLAUDE.md documented flow
dotnet test InfiniContext.slnx -c Release --logger "trx;LogFileName=t.trx" --collect:"XPlat Code Coverage" --results-directory coverage
dotnet format InfiniContext.slnx --verify-no-changes               # format gate parity
```

For the coverage line, confirm a **non-empty** `coverage/**/coverage.cobertura.xml`
appears (coverlet collector is framework-agnostic and is expected to work under v3/VSTest,
but this is the single least-proven integration in this plan -- verify it explicitly, and if
it comes back empty, that finding goes to `progress.md` before any workaround).

Watch the smoke tests (`ReplSmokeTests`, `SidecarSmokeTests`, lock-contention tests --
everything that spawns real processes) for timing flakes: v3's default parallel scheduling
algorithm differs from v2's. A rerun that's green is fine; *consistent* new flakiness means
pinning `parallelAlgorithm` back to `aggressive` in an `xunit.runner.json` -- record either
outcome.

### 5. CI verification

Push the branch, let `ci.yml` run (PR trigger): format -> build -> test -> coverage summary
in the job step output. Then dispatch **`ci-macos.yml`** once -- `MacOnlyFact` tests must
show as *executed* on `macos-latest` and *skipped* on Windows, proving the ctor-`Skip`
pattern survived. (This is the rare case where the manual macOS run is warranted: the
migration touches the test harness those Phase 7 tests run on.)

### 6. Docs touch-up

- `docs/stage2/decisions.md` (or current decisions home): one-line entry -- solution
  standardized on xunit.v3 / VSTest mode, and why (headless suite forced v3; uniformity).
- CLAUDE.md needs no edit (it documents commands, not harness versions -- the commands are
  verified unchanged in step 4).

## Acceptance criteria

1. Test counts (pass/skip totals) identical to the step-1 baseline.
2. `ci.yml` green including the coverage summary table in the job output.
3. One green `ci-macos.yml` dispatch with MacOnlyFact tests executed.
4. Both CLAUDE.md filter invocations work.
5. Single commit, no change outside `tests/` + the decisions-doc line.

## Rollback

Revert the one commit. No data, schema, or fixture changes are involved.

## Explicit non-goals / follow-ups (do not do in this commit)

- Switching to Microsoft.Testing.Platform mode.
- Modernizing the OS-conditional attributes to v3 dynamic skips (`Skip.When` /
  `SkipUnless`) -- works as-is; cosmetic.
- Any test refactoring beyond what the compiler forces.
- The new GUI test projects themselves (that's design.md Phases 1-2; they simply inherit a
  uniform v3 world from this plan).
