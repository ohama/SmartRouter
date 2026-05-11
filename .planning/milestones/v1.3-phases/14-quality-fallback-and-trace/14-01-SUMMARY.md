---
phase: 14-quality-fallback-and-trace
plan: 01
subsystem: cli-infrastructure
tags: [cold-start, backup, cli-flags, logging, bcl]

dependency-graph:
  requires: [Phase 13 logging infrastructure (Logging.fs, LogRetentionService)]
  provides: [runColdStartBackup, --cold-start CLI flag wired into Program.fs]
  affects: [14-05 tests (uses --cold-start to guarantee fresh model state)]

tech-stack:
  added: []
  patterns: [DRY bootstrap preamble — Logging.configure hoisted before if/else split]

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/ColdStart.fs
  modified:
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/Program.fs

decisions:
  - id: DRY-bootstrap-preamble
    description: "Hoisted Logging.configure + applyLogLevelFromArgs to before the --retrain/Kestrel split. Both branches previously duplicated these two calls. Now a ConfigurationBuilder reads appsettings.json once; per-branch builders still load their own IConfiguration for DI, but logging setup runs exactly once. Removed duplicate calls from both branches."
  - id: xml-comment-dashes
    description: "Initial XML comment used --cold-start inside <!-- --> which is invalid XML (-- not allowed in XML comments). Auto-fixed to say 'cold-start' without leading dashes."

metrics:
  duration: 10m48s
  completed: 2026-05-10
---

# Phase 14 Plan 01: Cold-Start CLI Summary

**One-liner:** `--cold-start` flag with BCL-only `runColdStartBackup` — timestamp-suffixed backup of 4 model/dataset files, then continues normal startup so `ensureDummyModel` generates fresh `router.zip`.

## What Was Built

### Task 1: `Adapters/ColdStart.fs` + `.fsproj` registration

New module `SmartRouter.Cli.Adapters.ColdStart` (43 lines, BCL only — no Serilog or HTTP package references). Exposes one public function:

```fsharp
runColdStartBackup (logger: ILogger) (rootDir: string) : unit
```

Behavior:
- Timestamp: `DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")`
- 4 candidate paths relative to `rootDir`:
  1. `models/router.zip`
  2. `models/router.zip.prev`
  3. `datasets/hard-cases.jsonl`
  4. `datasets/training-set.jsonl`
- Each existing file renamed to `{path}.cold-start-backup-{timestamp}` via `File.Move`
- Missing files silently skipped (idempotent)
- No files present: logs Information saying `ensureDummyModel` will generate fresh `router.zip`
- Files backed up: logs Information with count, timestamp, comma-separated paths

Compile entry added to `SmartRouter.Cli.fsproj` immediately after `Adapters/Logging.fs` (line 22 vs Program.fs line 62 — ordering correct).

### Task 2: `Program.fs` — `--cold-start` flag handling

Chose the "Recommended (DRY)" approach from the plan:

**Bootstrap preamble added at the top of `main` (before the `--retrain` / Kestrel split):**

```fsharp
let bootstrapConfig =
    ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional = true)
        .Build() :> IConfiguration
Logging.configure(bootstrapConfig)
applyLogLevelFromArgs args

if args |> Array.contains "--cold-start" then
    use coldStartFactory =
        LoggerFactory.Create(fun b ->
            b.AddSerilog(Log.Logger, dispose = false) |> ignore)
    let coldStartLogger = coldStartFactory.CreateLogger("ColdStart")
    Adapters.ColdStart.runColdStartBackup coldStartLogger System.Environment.CurrentDirectory
    // Backup complete; startup continues — process does NOT exit
```

**Duplicate calls removed** from both the `--retrain` branch and the main Kestrel branch (both previously had `Logging.configure` + `applyLogLevelFromArgs`). Each branch retains its own `ConfigurationBuilder`/`WebApplication.CreateBuilder` for DI — only the logging setup was de-duplicated.

**Flag ordering:** `--cold-start` is checked BEFORE `--retrain`, satisfying the plan's requirement that the two flags can be combined.

## Verification Results

| Check | Result |
|---|---|
| `ColdStart.fs` exists | PASS |
| `runColdStartBackup` defined | PASS (1 definition) |
| 4 candidate files covered | PASS |
| Idempotent on missing files (no throw) | PASS (File.Exists guard) |
| `.fsproj` Compile entry present | PASS (1 entry, line 22) |
| Compile order: ColdStart.fs before Program.fs | PASS (line 22 vs 62) |
| `dotnet build` TreatWarningsAsErrors=true | PASS (0 warnings, 0 errors) |
| `--cold-start` in Program.fs before `--retrain` | PASS |
| No exit after backup | PASS (no `exit` call) |
| Test baseline preserved | PASS (80 passed, 16 ignored, 0 failed) |
| ColdStart.fs line count >= 30 | PASS (43 lines) |
| BCL only (no Serilog/HTTP packages in ColdStart.fs) | PASS |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Invalid XML comment with `--` in .fsproj**

- **Found during:** Task 1 build verification
- **Issue:** XML comment `<!-- Phase 14: --cold-start backup handler (BCL only) -->` is invalid XML — `--` is not allowed inside `<!-- -->` comments, and `.fsproj` is XML.
- **Fix:** Changed comment to `<!-- Phase 14: cold-start backup handler (BCL only) -->`.
- **Files modified:** `SmartRouter.Cli.fsproj`
- **Impact:** Build would fail otherwise; caught and fixed before commit.

## Commits

| Commit | Message | Files |
|---|---|---|
| 428b30b | feat(14-01): create Adapters/ColdStart.fs + register in fsproj | ColdStart.fs, SmartRouter.Cli.fsproj |
| fe12164 | feat(14-01): wire --cold-start flag into Program.fs | Program.fs |

## Next Phase Readiness

- **14-02** (prompt UID + trace logging): Program.fs changes were isolated to the top of `main` — 14-02 can add `applyTraceFlagFromArgs` right below `applyLogLevelFromArgs` in the same preamble without conflict.
- **14-05** (tests): `runColdStartBackup` is a pure F# function taking `ILogger` + `rootDir` — directly testable with `NullLogger.Instance` and a temp directory.
- **ARCH-01 (Pure-Core BCL only):** ColdStart.fs is BCL-only; no packages added.
- **TreatWarningsAsErrors=true:** Build clean.
- **OBS-04:** No changes to Serilog sink configuration.
