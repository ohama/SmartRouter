---
phase: 01-foundation
plan: 01
subsystem: infra
tags: [fsharp, dotnet10, aspnetcore, expecto, serilog, kestrel, nuget, scaffold]

# Dependency graph
requires: []
provides:
  - SmartRouter.slnx solution with three project entries (Core, Cli, Tests)
  - global.json pinning .NET SDK 10.0.100 with latestFeature rollForward
  - SmartRouter.Core classlib with FsToolkit.ErrorHandling 5.2.0 (no Serilog/HttpClient/ASP.NET)
  - SmartRouter.Cli web project with all 7 NuGet pins and stub Program.fs
  - appsettings.json with Kestrel binding to http://127.0.0.1:4000 (literal IP)
  - SmartRouter.Tests exe project with Expecto 10.2.1 and Mvc.Testing 10.0.7
  - RouterTests.fs with explicit empty rootTests list and runTestsWithCLIArgs entrypoint
  - scripts/check-no-async.sh CI guard (exit 0 if clean, exit 1 on async {})
  - .gitignore covering bin/, obj/, .vs/, *.user, .DS_Store
affects:
  - 01-02 (Core domain types need Core classlib and fsproj to exist)
  - 01-03 (Cli adapters + endpoints need Cli fsproj + appsettings skeleton)
  - All future phases depend on this scaffold

# Tech tracking
tech-stack:
  added:
    - FsToolkit.ErrorHandling 5.2.0
    - FSharp.SystemTextJson 1.4.36
    - Serilog 4.3.1
    - Serilog.Sinks.Console 6.1.1
    - Serilog.AspNetCore 10.0.0
    - Microsoft.Extensions.Http.Resilience 10.5.0
    - FSharp.Control.TaskSeq 1.1.1
    - Expecto 10.2.1
    - Microsoft.AspNetCore.Mvc.Testing 10.0.7
  patterns:
    - Explicit Expecto rootTests list (no [<Tests>] auto-discovery)
    - appsettings.json as single source of truth for Kestrel binding (no applicationUrl in launchSettings)
    - TreatWarningsAsErrors=true in Core and Cli
    - check-no-async.sh CI guard scoped to src/SmartRouter.Core

key-files:
  created:
    - SmartRouter.slnx
    - global.json
    - .gitignore
    - src/SmartRouter.Core/SmartRouter.Core.fsproj
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/Program.fs
    - src/SmartRouter.Cli/Properties/launchSettings.json
    - src/SmartRouter.Cli/appsettings.json
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs
    - scripts/check-no-async.sh
  modified: []

key-decisions:
  - "SmartRouter.slnx written manually in XML format (dotnet new slnx unavailable in SDK 10.0.203)"
  - "Kestrel bound to literal 127.0.0.1:4000 not localhost (IPv6 ::1 risk on macOS)"
  - "launchSettings.json has no applicationUrl — appsettings.json is single source of truth"
  - "SmartRouter.Cli has stub Program.fs to satisfy F# compiler (FS0988 empty main module)"
  - "Tests project references only Core (not Cli) at this stage"

patterns-established:
  - "Expecto rootTests: explicit typed Test list appended per module; no [<Tests>] attribute"
  - "check-no-async.sh: CI guard exits 0/1/2; run from repo root; grep src/SmartRouter.Core/**/*.fs"
  - "appsettings.json Kestrel section: single source of truth for URL; no launchSettings.json override"
  - "Per-task atomic commits with type(01-01): prefix"

# Metrics
duration: 3min
completed: 2026-05-07
---

# Phase 1 Plan 01: Scaffold Summary

**F# .NET 10 solution skeleton with three projects, all NuGet versions pinned, Kestrel bound to 127.0.0.1:4000, async-ban CI guard, and Expecto explicit rootTests pattern — zero build errors, zero warnings**

## Performance

- **Duration:** ~3 minutes
- **Started:** 2026-05-07T06:30:20Z
- **Completed:** 2026-05-07T06:33:50Z
- **Tasks:** 3
- **Files modified:** 11 created, 0 modified

## Accomplishments

- `dotnet build SmartRouter.slnx` exits 0 with zero errors and zero warnings across all three projects
- `scripts/check-no-async.sh` exits 0 on clean Core; canary smoke test confirmed exit 1 on `async {` literal
- `dotnet run --project tests/SmartRouter.Tests` exits 0, reports 0 tests run, no auto-discovery warnings

## Task Commits

1. **Task 1: Create solution + three .fsproj files + project references** - `fe8dcd1` (feat)
2. **Task 2: Add appsettings.json + check-no-async.sh** - `5e2cbe3` (feat)
3. **Task 3: Write RouterTests.fs entrypoint** - `a44e2ee` (feat)

## Resolved NuGet Versions (from `dotnet list package`)

**SmartRouter.Core:**
| Package | Requested | Resolved |
|---------|-----------|---------|
| FsToolkit.ErrorHandling | 5.2.0 | 5.2.0 |
| FSharp.Core | 10.1.203 | 10.1.203 |

**SmartRouter.Cli:**
| Package | Requested | Resolved |
|---------|-----------|---------|
| FSharp.Control.TaskSeq | 1.1.1 | 1.1.1 |
| FSharp.Core | 10.1.203 | 10.1.203 |
| FSharp.SystemTextJson | 1.4.36 | 1.4.36 |
| FsToolkit.ErrorHandling | 5.2.0 | 5.2.0 |
| Microsoft.Extensions.Http.Resilience | 10.5.0 | 10.5.0 |
| Serilog | 4.3.1 | 4.3.1 |
| Serilog.AspNetCore | 10.0.0 | 10.0.0 |
| Serilog.Sinks.Console | 6.1.1 | 6.1.1 |

**SmartRouter.Tests:**
| Package | Requested | Resolved |
|---------|-----------|---------|
| Expecto | 10.2.1 | 10.2.1 |
| FSharp.Core | 10.1.203 | 10.1.203 |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.7 | 10.0.7 |

All packages resolved at their pinned versions with no unexpected upgrades.

## Files Created

- `/Users/ohama/projs/smart-router/SmartRouter.slnx` — .slnx XML solution with three project entries
- `/Users/ohama/projs/smart-router/global.json` — SDK pin 10.0.100, rollForward: latestFeature
- `/Users/ohama/projs/smart-router/.gitignore` — bin/, obj/, .vs/, *.user, .DS_Store
- `/Users/ohama/projs/smart-router/src/SmartRouter.Core/SmartRouter.Core.fsproj` — net10.0 classlib, TreatWarningsAsErrors, FsToolkit.ErrorHandling only
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — Microsoft.NET.Sdk.Web, AssemblyName=SmartRouter, all 7 NuGet pins
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Program.fs` — stub [<EntryPoint>] returning 0
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Properties/launchSettings.json` — SmartRouter profile, no applicationUrl
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/appsettings.json` — Kestrel 127.0.0.1:4000, Serilog stub, Logging section
- `/Users/ohama/projs/smart-router/tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — Exe, Expecto + Mvc.Testing, references Core
- `/Users/ohama/projs/smart-router/tests/SmartRouter.Tests/RouterTests.fs` — [<EntryPoint>] with explicit empty rootTests list
- `/Users/ohama/projs/smart-router/scripts/check-no-async.sh` — executable CI guard

## Decisions Made

- **SmartRouter.slnx written manually:** `dotnet new slnx` unavailable in SDK 10.0.203. Created XML file directly mirroring `BlueCode.slnx` structure. `dotnet restore` and `dotnet build` accepted the manually-written file without issues.
- **Stub Program.fs added to Cli:** The F# compiler emits `FS0988: main module is empty` when a project has zero source files in the compile list. Added a minimal stub `[<EntryPoint>]` returning 0 to suppress this. Plans 01-02 and 01-03 will replace this with the real composition root and host builder.
- **launchSettings.json has no applicationUrl:** Follows OPS-04 — `appsettings.json` is the single source of truth for the Kestrel binding. The auto-generated launchSettings had `applicationUrl: http://localhost:5200` which would have silently overridden the 127.0.0.1 setting; this was replaced.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `dotnet new slnx` unavailable — created .slnx manually**
- **Found during:** Task 1 (scaffold solution)
- **Issue:** `dotnet new slnx` exits 103 "no matching template" in SDK 10.0.203
- **Fix:** Wrote `SmartRouter.slnx` manually in XML format, mirroring `BlueCode.slnx` structure exactly
- **Files modified:** SmartRouter.slnx
- **Verification:** `dotnet restore SmartRouter.slnx` and `dotnet build SmartRouter.slnx` both succeed
- **Committed in:** fe8dcd1 (Task 1 commit)

**2. [Rule 3 - Blocking] SmartRouter.Cli had no source files → FS0988 empty main module**
- **Found during:** Task 1 verify step (first `dotnet build`)
- **Issue:** Cli fsproj had only an empty `<ItemGroup>` comment; F# compiler requires at least one source file with an entry point
- **Fix:** Added minimal stub `Program.fs` with `[<EntryPoint>] let main _argv = 0`; updated fsproj `<Compile>` list to include it
- **Files modified:** src/SmartRouter.Cli/Program.fs, src/SmartRouter.Cli/SmartRouter.Cli.fsproj
- **Verification:** Build succeeds with zero warnings; stub is replaced in plan 01-03
- **Committed in:** fe8dcd1 (Task 1 commit)

**3. [Rule 3 - Blocking] RouterTests.fs needed before Tests project could compile**
- **Found during:** Task 1 verify step (first `dotnet build`)
- **Issue:** SmartRouter.Tests.fsproj already references `RouterTests.fs` in the `<Compile>` list (per plan spec), but the file didn't exist yet — `FS0225: source file not found`
- **Fix:** Created `RouterTests.fs` during Task 1 verification, before committing Task 1. File is the exact Task 3 content; Task 3 commit covers only the staging of the file.
- **Files modified:** tests/SmartRouter.Tests/RouterTests.fs
- **Verification:** Build succeeds; `dotnet run --project tests/SmartRouter.Tests` exits 0
- **Committed in:** a44e2ee (Task 3 commit)

---

**Total deviations:** 3 auto-fixed (all Rule 3 — blocking issues)
**Impact on plan:** All three fixes were necessary to achieve a compiling scaffold. No scope creep. The RouterTests.fs content was already specified in Task 3; creating it earlier only reordered within-plan work.

## Issues Encountered

- `dotnet new slnx` unavailable in SDK 10.0.203 (plan assumed `.slnx` template available in .NET 10). Resolved by writing XML directly — same outcome, no functional difference.
- F# compiler requires at least one source file in a project (unlike C# SDK-style projects). The plan specified Cli compile items as "added in plans 01-02 and 01-03" but did not include a stub for 01-01 compilation. Added minimal stub.

## User Setup Required

None — no external service configuration required.

## Next Phase Readiness

**Ready for plan 01-02 (Core domain types):**
- `src/SmartRouter.Core/SmartRouter.Core.fsproj` has an empty `<ItemGroup>` comment placeholder for plan 01-02 to add `Domain.fs`, `Routing.fs`, `Ports.fs`
- Tests project has `RouterTests.fs` with empty `rootTests` list; plan 01-02 will add `RoutingTests.fs` above it in fsproj and append to `rootTests`
- All NuGet packages already restored

**Ready for plan 01-03 (Kestrel wiring + adapters):**
- `appsettings.json` has Kestrel 127.0.0.1:4000 binding; plan 01-03 adds `Upstreams` and `Routing` sections
- `SmartRouter.Cli.fsproj` has `Program.fs` stub that plan 01-03 replaces with real composition root

**No blockers.**

---
*Phase: 01-foundation*
*Completed: 2026-05-07*
