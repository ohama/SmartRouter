---
phase: 13-service-logging
plan: 05
subsystem: cli-infrastructure
tags: [logging, retention, background-service, startup-banner, shutdown-banner, operational]
depends_on: ["13-02", "13-03", "13-04"]
provides:
  - LogRetentionService: BackgroundService pruning operational logs, decision JSONL, and teacher-cap datasets
  - startup-banner: multi-line INFO emission at startup showing port, model version, canary state, queue config
  - shutdown-banner: INFO emission on ApplicationStopping with in-flight + queue depth stats
tech-stack:
  added: []
  patterns:
    - PeriodicTimer-based BackgroundService with immediate first-run before tick wait
    - CLIMutable record with mutable fields for Configure<T>(Action<T>) DI wiring
    - IHostApplicationLifetime.ApplicationStopping for synchronous shutdown callback
    - ILoggerFactory.CreateLogger(name) for operator-targeted SourceContext (Startup/Shutdown)
key-files:
  created:
    - src/SmartRouter.Cli/Adapters/LogRetentionService.fs
  modified:
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/Program.fs
decisions:
  - description: "mutable fields on LogRetentionOptions record instead of [<CLIMutable>] alone"
    rationale: "F# compiler enforces immutability on record fields even with [<CLIMutable>]; the CLIMutable attribute only helps reflection-based JSON binders. A Configure<T>(Action<T>) lambda needs actual mutable fields."
    alternatives: ["Use a .NET class instead of F# record", "Use PostConfigure + separate section"]
metrics:
  duration: ~9 min
  completed: 2026-05-09
---

# Phase 13 Plan 05: Startup Banner + Shutdown Banner + LogRetentionService Summary

**One-liner:** Startup/shutdown banners via ILoggerFactory + ApplicationStopping; 60-min PeriodicTimer prunes logs/JSONL/teacher-cap files past retention.

## What Was Built

### Task 1: LogRetentionService (54bd24a)

New `src/SmartRouter.Cli/Adapters/LogRetentionService.fs`:

- `LogRetentionOptions` record with 7 mutable fields (OperationalDirectory, OperationalRetentionDays, DecisionDirectory, DecisionRetentionDays, DatasetsDirectory, TeacherCapRetentionDays, PollIntervalMinutes)
- `LogRetentionService` inherits `BackgroundService`; `ExecuteAsync` uses `PeriodicTimer` at 60-minute intervals
- Runs `pruneFiles` once immediately on startup, then on each periodic tick
- Three pruning targets:
  - `logs/operational/smart-router-*.log` > 30 days (Logging:RetentionDays)
  - `logs/decisions/*.jsonl` > 90 days (DecisionLog:RetentionDays)
  - `datasets/teacher-cap-*.json` > 7 days (hardcoded)
- Date parsing handles both YYYYMMDD compact (Serilog default) and YYYY-MM-DD dashed formats
- `StopAsync` override logs "LogRetentionService stopping"
- Registered in `configureRequestPipeline` only (NOT `configureWithoutMl`)
- Options wired via `Configure<LogRetentionOptions>(fun o -> ...)` reading from Logging + DecisionLog config sections; PollIntervalMinutes/TeacherCapRetentionDays/DatasetsDirectory hardcoded

### Task 2: Startup Banner (ae76295)

In `Program.fs`, after `Models.mapEndpoints app` and before `app.Run()`:

- Resolves `RoutingAlgorithmRegistration`, `IModelVersionProvider`, `IOptions<QueueDispatcherOptions>`, `IOptions<TeacherLabelerOptions>`, `IOptions<CanaryOptions>` from DI
- Constructs a multi-line sprintf banner with: listen URL, routing.algorithm, model.version, canary.version, canary.percent, queue.maxconc.122B, queue.fairnessK, teacher.cap.daily, log.dir
- Emits via `ILoggerFactory.CreateLogger("Startup").LogInformation("{Banner}", banner)` — SourceContext = "Startup"
- Added opens: `CanaryWatchdog`, `RoutingAlgorithm`, `TeacherLabeler`, `Core.RetrainingPorts`

### Task 3: Shutdown Banner (fba82d1)

In `Program.fs`, immediately after the startup banner emission:

- Resolves `IHostApplicationLifetime` from DI
- Creates `ILoggerFactory.CreateLogger("Shutdown")` as `shutdownLogger`
- Registers `ApplicationStopping.Register(fun () -> ...)` callback:
  - Gets `IStatsProvider.GetSnapshot()` for live stats
  - Computes `inFlight = Active35B + Active122B`, `queueDepth = QueueDepth122BHigh + QueueDepth122BLow`
  - Emits: `SmartRouter stopping; in-flight=N queue.depth.total=N queue.depth.high=N queue.depth.low=N`
- `Logging.shutdown()` already present in outer `finally` block from 13-01; flushes Serilog including the shutdown banner

## Decisions Made

| Decision | Rationale |
|----------|-----------|
| `mutable` fields on `LogRetentionOptions` | `[<CLIMutable>]` only enables reflection-based binding; F# compiler still treats record fields as immutable in direct code. `Configure<T>(Action<T>)` lambda requires directly settable fields. |
| `IStatsProvider.GetSnapshot()` for shutdown stats | Cleaner than resolving concrete `QueueDispatcher`; the interface is already in scope via the open statement for the module. The plan mentioned `GetStats()` but the actual API is `GetSnapshot()`. |
| Hardcode PollIntervalMinutes=60, TeacherCapRetentionDays=7, DatasetsDirectory="datasets" | Plan explicitly requests these as hardcoded constants in the Configure action; operator can lift to appsettings.json in a future minor change without touching source code. |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] mutable fields required on LogRetentionOptions**

- **Found during:** Task 1 (first build attempt)
- **Issue:** Plan's code sample uses `opts.OperationalDirectory <- ...` mutation in a `Configure<T>` action lambda. F# enforces immutability on `[<CLIMutable>]` record fields in source code (the attribute only serves reflection-based JSON binders). Compiler error FS0005: "이 필드는 변경할 수 없습니다."
- **Fix:** Changed all 7 fields to `mutable` on the `LogRetentionOptions` record type. This is the idiomatic F# way to allow mutation in a `Configure<T>` action.
- **Files modified:** `src/SmartRouter.Cli/Adapters/LogRetentionService.fs`

**2. [Rule 1 - Bug] Plan references QueueDispatcher.GetStats() which doesn't exist**

- **Found during:** Task 3 (pre-read of QueueDispatcher.fs)
- **Issue:** Plan's code sample calls `queueDispatcher.GetStats()` but the actual API is `IStatsProvider.GetSnapshot()` (returns `StatsSnapshot`). Field names are `QueueDepth122BHigh` / `QueueDepth122BLow` (not `QueueDepthHigh` / `QueueDepthLow` as in plan sample).
- **Fix:** Used `IStatsProvider.GetSnapshot()` and correct field names `QueueDepth122BHigh`, `QueueDepth122BLow`. Resolved `IStatsProvider` from DI (registered in `configureRequestPipeline` as alias for `QueueDispatcher`).
- **Files modified:** `src/SmartRouter.Cli/Program.fs`

## Verification

- [x] `LogRetentionService.fs` exists; BackgroundService registered in `configureRequestPipeline` only
- [x] Startup banner emits all fields: listen, routing.algorithm, model.version, canary.version, canary.percent, queue.maxconc.122B, queue.fairnessK, teacher.cap.daily, log.dir
- [x] Shutdown banner via `ApplicationStopping` with in-flight + queue depth
- [x] `Logging.shutdown()` in outer `finally` block (carried from 13-01)
- [x] Build clean: `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — 0 warnings, 0 errors
- [x] Tests green: 62 passed, 16 ignored, 0 failed (baseline preserved)

## Next Phase Readiness

Phase 13 Plan 05 is the last content-heavy plan in Phase 13. Plans 13-06 (if any) would be final cleanup/documentation. The service logging phase is functionally complete:

- Dual sink (Console + rolling file) — 13-01
- ILogger<T> migration — 13-02
- Hot-path demotion, transition-only health, endpoint-hit DEBUG — 13-03
- --log-level CLI flag — 13-04
- Startup banner + shutdown banner + LogRetentionService — 13-05
