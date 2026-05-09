---
phase: 13-service-logging
plan: 02
subsystem: infra
tags: [ilogger, dependency-injection, microsoft-extensions-logging, serilog-removal, adapter-migration]

# Dependency graph
requires:
  - phase: 13-service-logging/13-01
    provides: Serilog dual-sink foundation; SourceContext-aware output template; 62+16+0 baseline

provides:
  - All 10 type-based adapters migrated from Serilog.Log.* to ILogger<T> constructor injection
  - 4 module-based adapters migrated via Option A (ILogger param on each emitting function)
  - ChatCompletions endpoint migrated via ILoggerFactory.CreateLogger("ChatCompletions") in mapEndpoints
  - Program.fs --retrain branch migrated via host.Services ILoggerFactory post-Build
  - CompositionRoot + Program.fs retain static Log.* only for 2 startup-window call sites
  - NullLogger<T>.Instance at all test manual-construction sites
  - CapturingLogger<T> MEL-based in-memory sink replacing Serilog CapturingSink in RetrainingTests
  - 62 passed + 16 ignored + 0 failed (no regression)

affects:
  - 13-03 (behavior changes — all adapters now use ILogger<T>; SourceContext auto-populates category)
  - 13-04 (log-level CLI — ILogger<T> respects MEL log-level filtering via host configuration)
  - 13-06 (tests/docs — LoggingTests may reference ILogger<T> patterns established here)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - ILogger<T> constructor injection for all ASP.NET Core type-based adapters
    - ILogger function parameter (Option A) for F# module-based emit functions
    - NullLogger<T>.Instance at test manual construction sites
    - sp.GetRequiredService<ILogger<T>>() in CompositionRoot DI factory lambdas
    - ILoggerFactory.CreateLogger("CategoryName") for non-type endpoints

# File tracking
key-files:
  created: []
  modified:
    - src/SmartRouter.Cli/Adapters/HealthService.fs
    - src/SmartRouter.Cli/Adapters/CanaryService.fs
    - src/SmartRouter.Cli/Adapters/RetrainingService.fs
    - src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
    - src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
    - src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs
    - src/SmartRouter.Cli/Adapters/CanaryWatchdog.fs
    - src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
    - src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
    - src/SmartRouter.Cli/Adapters/FailureDetector.fs
    - src/SmartRouter.Cli/Adapters/BgeM3Embedder.fs
    - src/SmartRouter.Cli/Adapters/Validator.fs
    - src/SmartRouter.Cli/Adapters/DatasetMerger.fs
    - src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs
    - src/SmartRouter.Cli/Adapters/Retrainer.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/Program.fs
    - tests/SmartRouter.Tests/QueueTests.fs
    - tests/SmartRouter.Tests/LoadTests.fs
    - tests/SmartRouter.Tests/HardCaseDatasetTests.fs
    - tests/SmartRouter.Tests/RetrainingTests.fs
    - tests/SmartRouter.Tests/LoggingTests.fs
    - tests/SmartRouter.Tests/HealthFallbackTests.fs
    - tests/SmartRouter.Tests/StreamingTests.fs
    - tests/SmartRouter.Tests/FailureDetectorTests.fs
    - tests/SmartRouter.Tests/TeacherLabelerTests.fs
    - tests/SmartRouter.Tests/MLEmbeddingTests.fs
    - tests/SmartRouter.Tests/MLClassifierTests.fs

# Decisions
decisions:
  - id: D1
    choice: ILogger<T> ctor injection (not ILoggerFactory)
    rationale: DI auto-resolves ILogger<T> from AddLogging(); no extra factory boilerplate; SourceContext auto-populated from T
  - id: D2
    choice: Option A (logger param) for module functions, not Option B (module-level private val)
    rationale: F# modules don't have constructors; param threading is explicit and testable; avoids hidden global state
  - id: D3
    choice: ILoggerFactory.CreateLogger("ChatCompletions") in mapEndpoints
    rationale: Handler is a module function (not a type); endpoint registration has ctx.RequestServices available; category name is explicit
  - id: D4
    choice: NullLogger.Instance for ModelBootstrapper bootstrap calls in configureRequestPipeline
    rationale: DI container not yet built at configure time; startup-window Serilog static sink still captures any fatal exceptions; boot messages are rare
  - id: D5
    choice: CapturingLogger<T> MEL type replacing Serilog CapturingSink in RETRAIN-05
    rationale: ILogger<RetrainingService> injection means messages no longer flow through Serilog static logger; MEL capture is the correct interception point

# Metrics
metrics:
  duration: ~90 minutes
  completed: 2026-05-09
  tests-before: 62 passed, 16 ignored, 0 failed
  tests-after:  62 passed, 16 ignored, 0 failed
  files-migrated: 15 src files + 9 test files
  log-calls-migrated: ~55 static Log.* calls converted to ILogger.LogX
  static-log-retained: 2 (CompositionRoot.fs:validateConfig Log.Warning; Program.fs outermost Log.Fatal)

---

# Phase 13 Plan 02: ILogger Migration Summary

**One-liner:** Full Serilog.Log.* static removal from all adapters — ILogger<T> ctor injection for 10 type adapters, ILogger param for 4 module functions, ILoggerFactory for ChatCompletions endpoint.

## Objective

Migrate all `Serilog.Log.X(...)` static calls to proper `ILogger<T>` constructor injection across the smart-router codebase. Static `Log.*` is retained only in the 2 startup-window call sites where DI is unavailable.

## What Was Built

### Task 1: High-Emission Type Adapters (5 adapters)
- **HealthService**: `logger: ILogger<HealthService>` as 4th ctor param; 8 Log.X replaced
- **CanaryService**: `logger: ILogger<CanaryService>` as 8th ctor param; 9 Log.X replaced
- **RetrainingService**: `logger: ILogger<RetrainingService>` as 5th ctor param; 10 Log.X replaced; private `readState` takes `(logger: ILogger)` param
- **TeacherLabeler**: `logger: ILogger<TeacherLabeler>` as 3rd ctor param; 6 Log.X replaced; `readCounter`/`writeCounter` module helpers take logger param
- **HardCaseDatasetWriter**: `logger: ILogger<HardCaseDatasetWriter>` as 2nd ctor param; 6 Log.X replaced

### Task 2: Second Batch of Type Adapters (5 adapters)
- **DecisionLogWriter**: `logger: ILogger<DecisionLogWriter>` as 2nd ctor param; 4 Log.X replaced
- **CanaryWatchdog**: `logger: ILogger<CanaryWatchdog>` as 4th ctor param; `Log.Verbose` → `logger.LogTrace`; 3 Log.X replaced
- **QueueDispatcher**: `logger: ILogger<QueueDispatcher>` as 4th ctor param (after healthProbe); 3 Log.X replaced
- **QwenUpstreamClient**: `logger: ILogger<QwenUpstreamClient>` as 3rd ctor param; private `probeModelIdAsync` takes `(logger: ILogger)` param; 6 Log.X replaced
- **FailureDetector**: `logger: ILogger<FailureDetector>` as 2nd ctor param; 5 Log.X replaced

All 9 test files updated (NullLogger<T>.Instance or sp.GetRequiredService<ILogger<T>>() in DI factories).

### Task 3: BgeM3Embedder + ChatCompletions
- **BgeM3Embedder**: `logger: ILogger<BgeM3Embedder>` as 4th ctor param; 2 Log.X replaced
- **ChatCompletions**: `logger: ILogger` added to `handler` signature; `mapEndpoints` resolves via `ILoggerFactory.CreateLogger("ChatCompletions")`; 5 Log.X replaced
- CompositionRoot: BgeM3Embedder factory updated with logger param

### Task 4: Module-Based Adapters (Option A)
- **Validator**: `computeBaseline` and `writeRejectionLog` each take `(logger: ILogger)` as first param; `Log.Fatal` in ModelBootstrapper mapped to `logger.LogCritical`
- **DatasetMerger**: `readHardCases`, `readTrainingSet`, `rebalance`, `merge` each take `(logger: ILogger)` as first param
- **ModelBootstrapper**: `ensureEmbeddingFilesPresent` and `ensureDummyModel` take `(logger: ILogger)` as first param
- **Retrainer**: `retrain` takes `(logger: ILogger)` as first param
- **RetrainingService**: all 6 call sites updated to thread `logger` through
- CompositionRoot: bootstrap calls use `NullLogger.Instance` (startup window before DI built)

### Task 5: Startup Window Confirmation + Program.fs Migration
- `Program.fs --retrain` branch: resolves `ILoggerFactory` from `host.Services` post-Build; 5 Log.X replaced with logger.LogX
- Retained: `CompositionRoot.fs:133 Log.Warning` in `validateConfig` (startup-time config check); `Program.fs:174 Log.Fatal` in outermost exception handler
- `CapturingLogger<T>` MEL type added to RetrainingTests replacing Serilog `CapturingSink` for RETRAIN-05 assertion

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] StreamingTests.fs QwenUpstreamClient/QueueDispatcher factories missing logger**
- Found during: Task 2 test file updates (build verification)
- Issue: StreamingTests registered QwenUpstreamClient and QueueDispatcher without the new logger params
- Fix: Updated both DI factory lambdas with `sp.GetRequiredService<ILogger<T>>()` 
- Files modified: `tests/SmartRouter.Tests/StreamingTests.fs`

**2. [Rule 1 - Bug] FailureDetectorTests.fs, TeacherLabelerTests.fs missing NullLogger**
- Found during: Task 2 build check
- Issue: Manual construction sites not updated
- Fix: Added `NullLogger<T>.Instance` + `open Microsoft.Extensions.Logging.Abstractions`

**3. [Rule 1 - Bug] MLEmbeddingTests.fs BgeM3Embedder lazy missing logger**
- Found during: Task 3 build check
- Fix: Added `NullLogger<BgeM3Embedder>.Instance` to lazy construction

**4. [Rule 1 - Bug] MLClassifierTests.fs ensureDummyModel direct calls missing logger**
- Found during: Task 4 build check
- Fix: Added `nullLogger` private value and threaded to 3 call sites

**5. [Rule 2 - Feature] DatasetMerger.readTrainingSet also needed logger param**
- Found during: Task 4 (readTrainingSet delegates to readHardCases)
- Fix: Added `(logger: ILogger)` param to `readTrainingSet` and updated call site in RetrainingService

**6. [Rule 1 - Bug] RETRAIN-05 test uses Serilog CapturingSink to assert on log lines that now flow through ILogger<T>**
- Found during: Task 5 test run (62 passed → 1 failed)
- Fix: Replaced `CapturingSink` type + Serilog global logger swap with `CapturingLogger<RetrainingService>` (ILogger<T> implementation) that captures messages via MEL

## Next Phase Readiness

Plan 13-03 (behavior changes) can proceed immediately. All adapters now use ILogger<T> which:
- Carries proper `SourceContext` = fully-qualified type name for Serilog filtering
- Respects MEL log-level filtering via appsettings.json `Logging:LogLevel` table
- Is testable without global Serilog state (NullLogger.Instance or CapturingLogger<T>)
