---
phase: 13-service-logging
plan: "06"
subsystem: testing-and-documentation
tags: [expecto, serilog, logretention, rolling-file, readme, phase-13]

dependency-graph:
  requires: ["13-01", "13-02", "13-03", "13-04", "13-05"]
  provides: ["LogRotationTests.fs (14 test cases)", "README §9.6-9.9 Phase 13 operator guide"]
  affects: []

tech-stack:
  added: []
  patterns:
    - "testSequenced + withTempDir (unique temp dir per test, try/finally cleanup)"
    - "NullLogger<T>.Instance for BackgroundService unit tests"
    - "LogRetentionService instantiated directly for pruning behavior tests"
    - "Serilog.LoggerConfiguration + ILogEventSink for in-memory capture tests"
    - "CapturingSink (thread-safe ResizeArray) — private per-module pattern"

key-files:
  created:
    - tests/SmartRouter.Tests/LogRotationTests.fs
  modified:
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs
    - README.md

decisions:
  - id: D1
    decision: "Separate CapturingSink type per module (not shared)"
    rationale: "LoggingTests.CapturingSink is private; LogRotationTests defines its own with identical interface"
  - id: D2
    decision: "SourceContext assertion uses '.' separator (not '+' C# nested)"
    rationale: "F# types defined in a module compile to dotted fully-qualified names; actual value confirmed by running the test against live ILogger<HealthService>"
  - id: D3
    decision: "Program.main [|'--trace'|] returns 1 rather than throws"
    rationale: "The inner try/with in Program.fs catches the failwith and returns 1; test verifies exit code 1 as the behavioral contract"
  - id: D4
    decision: "README §9.6-9.9 fully rewritten (no two-column v1/Phase13 table)"
    rationale: "Phase 13 is implemented; forward-references removed; single Effect column"

metrics:
  duration: "~20 min (active execution)"
  completed: "2026-05-09"
---

# Phase 13 Plan 06: Tests and Docs Summary

**One-liner:** 14 LogRotationTests covering Phase 13 logging behavior (template, rolling, retention, --log-level, --trace) + README §9.6-9.9 rewritten to reflect implemented dual-sink + LogRetentionService reality.

## What Was Done

### Task 1 — LogRotationTests.fs (NEW)

Created `tests/SmartRouter.Tests/LogRotationTests.fs` with 14 `testCase` entries, all wrapped in `testSequenced`. Each test uses a unique temp directory via `Path.GetTempPath() + Guid.NewGuid().ToString("N")` with `try/finally` cleanup.

| # | Test Case | What It Verifies |
|---|-----------|-----------------|
| 1 | Output template timestamp is ISO-8601 with milliseconds | `yyyy-MM-ddTHH:mm:ss.fffzzz` regex match |
| 2 | `[{correlation_id}]` renders request value via LogContext.PushProperty | ScalarValue in event.Properties["correlation_id"] |
| 3 | `[-]` renders when correlation_id absent | Default Enrich.WithProperty("-") enricher |
| 4 | `{SourceContext}` is fully-qualified F# type name | `SmartRouter.Cli.Adapters.HealthService.HealthService` (dotted, not `+`) |
| 5 | `Microsoft.AspNetCore.*` Information filtered to Warning | MinimumLevel.Override blocks Info; Warning passes |
| 6 | Rolling file: size cap creates `_NNN` suffix file | fileSizeLimitBytes=1L + rollOnFileSizeLimit=true |
| 7 | Rolling file: new file has today's date in filename | RollingInterval.Day creates YYYYMMDD filename |
| 8 | `retainedFileCountLimit=2` deletes oldest on 3rd file | File count <= 2 after 6 emissions at 1-byte cap |
| 9 | LogRetentionService prunes decision JSONL > 90 days | 95-day file deleted; 30-day file kept |
| 10 | LogRetentionService prunes teacher-cap > 7 days | 10-day file deleted; 3-day file kept |
| 11 | LogRetentionService keeps files within retention | 60-day JSONL + 5-day teacher-cap both survive |
| 12 | `--log-level=debug` enables Debug emissions | setLevel(Debug); Debug event captured by CapturingSink |
| 13 | `--log-level=warning` blocks Information | setLevel(Warning); Information blocked; Warning passes |
| 14 | `--trace` flag causes Program.main to return exit code 1 | Behavioral: main [|"--trace"|] returns 1 |

**Wired into:**
- `SmartRouter.Tests.fsproj`: `<Compile Include="LogRotationTests.fs" />` (Phase 13 section, after ModelsTests.fs)
- `RouterTests.fs`: `SmartRouter.Tests.LogRotationTests.tests` appended to rootTests

### Task 2 — README §9.6-9.9 Update

Rewrote four sections to reflect Phase 13 implemented reality:

**§9.6 Log files (overview)**
- Added `logs/operational/smart-router-YYYYMMDD[_NNN].log` as primary stream
- Added file layout tree showing daily roll + size-roll pattern
- Removed "v1 logging stream model" paragraph with its forward-references to "Phase 13 planned"
- Removed "no built-in retention in v1" caveat; LogRetentionService is now active

**§9.7 Reading the operational log**
- Updated output template to Phase 13 format: `{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Level:u3}] {SourceContext} [{correlation_id}] {Message:lj}`
- Updated example log lines to show full template with timestamp, SourceContext, and [cid]/[-]
- Replaced old `grep ~/llm-system/services/logs/smart-router.err` queries with rolling-file queries
- Added new operator queries: `tail -f logs/operational/smart-router-$(date +%Y%m%d).log`, CID trace across both streams, timeout detection

**§9.8 Log levels and filtering**
- Replaced "Phase 13 replaces --trace" forward-reference with factual docs for `--log-level`
- Added CLI usage examples (`--log-level=debug`, `--log-level=warn`)
- Removed caveat "not yet wired to Serilog's startup binding in v1"; `ReadFrom.Configuration(...)` now active
- Updated Debug volume description to reflect hot-path demotion (ChatCompletions no longer at Info)

**§9.9 Log parameters reference**
- Collapsed two-column table (v1 / Effect after Phase 13) into single Effect column
- Removed all "v1: ignored", "n/a (Phase 13)" entries
- Removed `--trace` row; added note that `--trace` was removed (causes startup error)
- Updated `Logging.Directory` default to `"logs/operational"`, `Logging.RetentionDays` to `30`, `DecisionLog.RetentionDays` to `90`

## Test Counts

| Baseline (before 13-06) | New (13-06) | Total |
|------------------------|-------------|-------|
| 62 passed + 16 ignored | +14 passed | 76 passed + 16 ignored + 0 failed |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] SourceContext uses `.` not `+` separator for F# module types**

- **Found during:** Test 4 first run
- **Issue:** Plan specified `SmartRouter.Cli.Adapters.HealthService+HealthService` (C# nested type syntax). Actual F# ILogger<T> SourceContext uses dotted module.type form: `SmartRouter.Cli.Adapters.HealthService.HealthService`
- **Fix:** Updated expected value in test assertion with explanatory comment
- **Files modified:** tests/SmartRouter.Tests/LogRotationTests.fs (assertion string only)
- **Commit:** cb2ab5f (included in the main task commit)

## Commits

| Task | Commit | Message |
|------|--------|---------|
| Task 1 — LogRotationTests.fs | `cb2ab5f` | `test(13-06): add LogRotationTests.fs with 14 testCase entries` |
| Task 2 — README §9.6-9.9 | `51f64c9` | `docs(13-06): update README §9.6-9.9 for Phase 13 operational reality` |
| Metadata | (this commit) | `docs(13-06): complete tests-and-docs plan` |

## Phase 13 Complete

All 6 plans of Phase 13 (Service Logging) are now complete:

| Plan | Deliverable | Status |
|------|-------------|--------|
| 13-01 | Logging.fs dual sink + ReadFrom.Configuration + new template + correlation_id default | Complete |
| 13-02 | ILogger<T> migration across 11 type-based + 4 module-based + ChatCompletions endpoint | Complete |
| 13-03 | Hot-path demoted to Debug; HealthService transition-only; 4 endpoint Debug hits | Complete |
| 13-04 | --log-level=enum CLI flag (replaces --trace; legacy --trace raises migration error) | Complete |
| 13-05 | LogRetentionService BackgroundService; startup + shutdown banners | Complete |
| 13-06 | 14 LogRotationTests + README §9.6-9.9 operator guide | Complete |

**Final test baseline: 76 passed, 16 ignored, 0 failed.**
