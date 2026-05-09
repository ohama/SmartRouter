---
phase: 13-service-logging
plan: 01
subsystem: infra
tags: [serilog, logging, file-sink, rolling-log, appsettings, IConfiguration, dual-sink]

# Dependency graph
requires:
  - phase: 12-heuristic-removal
    provides: clean codebase with configureRequestPipeline/configureWithoutMl split; 62+16+0 test baseline

provides:
  - Serilog.Sinks.File 7.0.0 + Serilog.Settings.Configuration 10.0.0 NuGet pins
  - Adapters/Logging.fs with configure(IConfiguration) — dual sink (Console stderr + rolling File)
  - Output template with ISO-8601 timestamp + [{Level:u3}] + SourceContext + [{correlation_id}] + message
  - Enrich.WithProperty("correlation_id", "-") default so background logs render [-]
  - setLevel(LogEventLevel) for Plan 13-04 CLI --log-level wiring
  - appsettings.json Serilog.MinimumLevel.Override table (5 namespaces)
  - appsettings.json Logging:Directory + Logging:RetentionDays + DecisionLog:RetentionDays
  - Program.fs passes IConfiguration to Logging.configure() in both --retrain and main branches

affects:
  - 13-02 (ILogger<T> migration — needs SourceContext from dual-sink to be correct)
  - 13-04 (CLI --log-level — calls setLevel(LogEventLevel) established here)
  - 13-05 (startup banner + LogRetentionService — needs Logging:Directory and RetentionDays keys)
  - 13-06 (tests — LogRotationTests verifies file sink behavior)

# Tech tracking
tech-stack:
  added:
    - Serilog.Sinks.File 7.0.0 (latest stable; plan suggested 6.0.0)
    - Serilog.Settings.Configuration 10.0.0 (latest stable; plan suggested 9.0.0)
  patterns:
    - ReadFrom.Configuration(IConfiguration) binding — appsettings.json:Serilog drives level overrides
    - configure(IConfiguration) replaces configure() — IConfiguration must be available before call
    - Dual-sink invariant: Console sink preserves OBS-04 (standardErrorFromLevel = Verbose); File sink is additive
    - Enrich.WithProperty default before WriteTo — ensures property present on every LogEvent
    - eprintfn + Log.Fatal dual write in outer `with` — covers pre/post-configure crash window

key-files:
  created: []
  modified:
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/Adapters/Logging.fs
    - src/SmartRouter.Cli/appsettings.json
    - src/SmartRouter.Cli/Program.fs

key-decisions:
  - "Serilog.Sinks.File pinned at 7.0.0 (latest stable, plan suggested 6.0.0)"
  - "Serilog.Settings.Configuration pinned at 10.0.0 (latest stable, plan suggested 9.0.0)"
  - "configure(IConfiguration) moved AFTER WebApplication.CreateBuilder — IConfiguration needed for Logging:Directory read"
  - "Pre-init crash window: eprintfn writes to stderr (launchd smart-router.err) + Log.Fatal after configure"
  - "File path pattern: smart-router-.log becomes smart-router-20260509.log / smart-router-20260509_001.log"

patterns-established:
  - "configure(IConfiguration) pattern: IConfiguration sourced from builder.Configuration after WebApp.CreateBuilder"
  - "Dual-sink: Console stderr-only (OBS-04) + File rolling daily/50MB/30-retain/2s-flush"
  - "Default enricher before sinks: Enrich.WithProperty('correlation_id', '-') renders [-] in background logs"

# Metrics
duration: 7min
completed: 2026-05-09
---

# Phase 13 Plan 01: Service Logging Foundation Summary

**Serilog dual-sink (Console stderr + rolling File) with ReadFrom.Configuration, ISO-8601 output template with [{correlation_id}] default, appsettings.json Override table, and IConfiguration-wired Program.fs**

## Performance

- **Duration:** ~7 min
- **Started:** 2026-05-09T11:42:51Z
- **Completed:** 2026-05-09T11:50:00Z
- **Tasks:** 5 (4 edits + 1 verification)
- **Files modified:** 4

## Accomplishments
- Serilog.Sinks.File 7.0.0 + Serilog.Settings.Configuration 10.0.0 added to Cli.fsproj; restored clean
- Logging.fs fully rewritten: dual sink (Console stderr-only kept per OBS-04; rolling File new), ReadFrom.Configuration, new output template, Enrich.WithProperty("correlation_id", "-") default, setLevel(LogEventLevel) for 13-04
- appsettings.json Serilog.MinimumLevel.Override table (5 namespaces) + Logging:Directory + Logging:RetentionDays + DecisionLog:RetentionDays
- Program.fs wired: Logging.configure(builder.Configuration) in both --retrain + main branches; pre-init eprintfn fallback
- Full test suite: **62 passed, 16 ignored, 0 failed, 0 errored** — Phase 12 baseline preserved exactly

## Task Commits

1. **Task 1: Add NuGet packages** - `d72a4b2` (chore)
2. **Task 2: Rewrite Adapters/Logging.fs** - `79ed99b` (feat)
3. **Task 3: appsettings.json — Override table + retention keys** - `4f04c88` (chore)
4. **Task 4: Program.fs — pass IConfiguration** - `463404a` (feat)
5. **Task 5: Solution build + test smoke** — no commit (verification only; 62+16+0 confirmed)

## Files Created/Modified
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — Serilog.Sinks.File 7.0.0 + Serilog.Settings.Configuration 10.0.0
- `src/SmartRouter.Cli/Adapters/Logging.fs` — full rewrite: configure(IConfiguration), dual sink, new template, correlation_id default, setLevel, shutdown
- `src/SmartRouter.Cli/appsettings.json` — Serilog Override table; Logging:Directory + RetentionDays; DecisionLog:RetentionDays
- `src/SmartRouter.Cli/Program.fs` — Logging.configure(config) in both branches; eprintfn pre-init fallback

## Decisions Made

- **NuGet version pinning:** Used 7.0.0 for Serilog.Sinks.File and 10.0.0 for Serilog.Settings.Configuration (latest stable; plan suggested 6.0.0 / 9.0.0). Both resolved cleanly with no conflicts.
- **configure() call site:** Moved from before `try` (no IConfiguration available) to inside each branch after builder creation. Retrain branch: after `AddJsonFile`. Main branch: after `WebApplication.CreateBuilder`. Pre-init crash window now uses `eprintfn` + `Log.Fatal` dual write.
- **File path pattern:** `Path.Combine(logDir, "smart-router-.log")` — Serilog's RollingInterval.Day appends date token, producing `smart-router-20260509.log` (and `smart-router-20260509_001.log` on 50MB rollover within day).
- **OBS-04 invariant preserved:** Console sink `standardErrorFromLevel = Nullable<LogEventLevel>(LogEventLevel.Verbose)` — no change from Phase 5 pattern. File sink is purely additive.

## Deviations from Plan

None — plan executed exactly as written. NuGet versions upgraded to latest stable (7.0.0 / 10.0.0 instead of 6.0.0 / 9.0.0) per plan's explicit instruction: "If newer stable available, pin to that."

## Issues Encountered

None. Build succeeded first attempt after Program.fs fix. CapturingSink-based tests unaffected by file-sink addition (in-process sink installed before build; file sink writes to disk, not to test capture buffer).

## User Setup Required

None — no external service configuration required. The `logs/operational/` directory is created automatically by `Directory.CreateDirectory(logDir)` in `configure()` at process startup.

## Next Phase Readiness

- Foundation complete. Static `Log.*` call sites (89 emissions across 19 files) continue to work unchanged.
- Plan 13-02 (ILogger<T> migration): ready. All 19 source files can now reference ILogger<T> via DI; SourceContext will appear in output template.
- Plan 13-04 (CLI --log-level): ready. `setLevel(LogEventLevel)` is live; `levelSwitch` is exposed; only Program.fs parsing needed.
- Plan 13-05 (LogRetentionService): ready. `Logging:Directory`, `Logging:RetentionDays`, `DecisionLog:RetentionDays` keys all in appsettings.json.
- No blockers. Test baseline: 62 passed + 16 ignored + 0 failed.

---
*Phase: 13-service-logging*
*Completed: 2026-05-09*
