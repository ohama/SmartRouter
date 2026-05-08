---
phase: 08-retraining-loop
plan: 02
subsystem: ml-retraining
tags: [fsharp, ml-net, backgroundservice, periodic-timer, semaphore-slim, model-versioning, di-wiring]

# Dependency graph
requires:
  - phase: phase-07
    provides: IHardCaseDatasetWriter + HardCaseEntry + IFailureDetector ports; hard-cases.jsonl is written here
  - phase: phase-08-01
    provides: Retrainer.retrain, DatasetMerger.merge/readHardCases/readTrainingSet/saveTrainingSet, Validator.computeBaseline/validate/writeRejectionLog, IModelVersionProvider port in Core/RetrainingPorts.fs
provides:
  - ModelVersionProvider.fs — concrete IModelVersionProvider adapter (lock-guarded mutable string)
  - RetrainingService.fs — BackgroundService implementing two-timer retrain loop with SemaphoreSlim(1,1) skip gate
  - ChatCompletions.fs updated — buildDecisionLog reads IModelVersionProvider.CurrentVersion per-request (live model_version, not frozen-at-startup)
  - CompositionRoot.fs updated — 5 new DI registrations (Configure<RetrainingOptions> + double-reg MVP + double-reg RetrainingService)
  - appsettings.json updated — Retraining section (12 keys per CONTEXT.md Lock 6)
  - SmartRouter.Cli.fsproj updated — ModelVersionProvider.fs + RetrainingService.fs compile entries
affects: [phase-09-canary, phase-08-03-tests]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Two-PeriodicTimer race in single BackgroundService.ExecuteAsync via Task.WhenAll — periodic sweep (IntervalMinutes) + count-threshold check (CountCheckIntervalMinutes); both call same tryRunRetrain gate"
    - "Train-to-candidate-then-validate pattern: candidate file lives at modelPath + '.candidate.zip'; only on Accepted does File.Move swap it into modelPath; on Rejected, candidate is deleted and modelPath is untouched"
    - "IModelVersionProvider hot-update seam: per-request DI resolution + lock-guarded mutable string field; DecisionLog reflects live model_version without host restart"
    - "F# task{} two-tier try/with for BackgroundService isolation: inner catches generic ex (RETRAIN-06 logs and swallows); ExceptionDispatchInfo.Capture(oce).Throw() re-throws OCE (FS0413: reraise() not valid in task{} nested try/with)"
    - "IModelVersionProvider double-registration (concrete singleton + interface alias); RetrainingService double-registration (concrete singleton + AddHostedService factory) — no IInterface alias since no interface consumer"

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/ModelVersionProvider.fs
    - src/SmartRouter.Cli/Adapters/RetrainingService.fs
  modified:
    - src/SmartRouter.Core/RetrainingPorts.fs   # (already had IModelVersionProvider from Plan 08-01)
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/appsettings.json
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj

key-decisions:
  - "SemaphoreSlim(1,1).Wait(0) skip-if-busy — Mutex would break thread affinity across task{} await points (CONTEXT.md Lock 7)"
  - "ModelVersionProvider double-reg pattern: concrete AddSingleton<ModelVersionProvider> + interface alias AddSingleton<IModelVersionProvider> resolving via GetRequiredService; single instance, two roles"
  - "RetrainingService double-reg pattern: concrete AddSingleton<RetrainingService> + AddHostedService factory; no IInterface alias since RetrainingService has no interface consumer"
  - "ChatCompletions reads IModelVersionProvider.CurrentVersion per-request (live) instead of frozen-at-startup regn.ModelVersion; all 8 call sites updated"
  - "runRetrain pipeline order: split FIRST → train on split.TrainSet only → evaluate baseline + candidate on split.TestSet (fair comparison, Lock 5)"
  - "ExceptionDispatchInfo.Capture(oce).Throw() instead of reraise() for OperationCanceledException in task{} nested try/with (FS0413 limitation)"
  - "Cumulative training-set: Array.append oldEntries hardCaseEntries saved after each successful retrain; first-retrain bootstrap: oldEntries=[||] (Lock 3)"

patterns-established:
  - "Two-PeriodicTimer race with Task.WhenAll"
  - "ExceptionDispatchInfo for re-throw in task{} nested try/with"

# Metrics
duration: 11min
completed: 2026-05-08
---

# Phase 8 Plan 02: Retraining Service and DI Summary

**RetrainingService BackgroundService with two-timer race + SemaphoreSlim skip gate + IModelVersionProvider hot-flip wired into production DI via double-registration patterns**

## Performance

- **Duration:** ~11 min
- **Started:** 2026-05-08T20:24:36Z
- **Completed:** 2026-05-08T20:36:29Z
- **Tasks:** 3
- **Files modified:** 6

## Accomplishments

- ModelVersionProvider implements IModelVersionProvider with lock-guarded mutable string field; seeds initial version from router.zip SHA or "heuristic-v1" at startup
- RetrainingService.ExecuteAsync races two PeriodicTimers (interval sweep + count-threshold) via Task.WhenAll; SemaphoreSlim(1,1).Wait(0) gates single-writer retrain with skip semantics on concurrent triggers
- runRetrain follows Lock 5 mandatory pipeline order: TrainTestSplit FIRST → retrain on split.TrainSet only → computeBaseline + validate both on split.TestSet → Accepted: File.Copy .prev + File.Move candidate + Update versionProvider + saveTrainingSet + writeState; Rejected: writeRejectionLog + delete candidate
- ChatCompletions.buildDecisionLog reads IModelVersionProvider.CurrentVersion per-request at all 8 exit points (null body, unsupported task, generic error, stream normal, stream cancelled, stream error, non-stream ok, non-stream error) — live model_version, not frozen startup snapshot
- CompositionRoot: 5 new DI registrations; IModelVersionProvider unconditional (heuristic mode gets "heuristic-v1"), RetrainingService guarded on routingAlgoStr = "ml" (needs IEmbedder)

## Task Commits

1. **Task 1: ModelVersionProvider adapter + ChatCompletions wiring + appsettings** - `9a9b8c1` (feat)
2. **Task 2: RetrainingService BackgroundService** - `8bf94e9` (feat)
3. **Task 3: CompositionRoot DI wiring** - `a7251be` (chore)

**Plan metadata:** _(final docs commit follows)_

## Files Created/Modified

- `src/SmartRouter.Cli/Adapters/ModelVersionProvider.fs` — NEW: lock-guarded mutable string implementing IModelVersionProvider
- `src/SmartRouter.Cli/Adapters/RetrainingService.fs` — NEW: BackgroundService (PeriodicTimers + SemaphoreSlim + try/with isolation + Lock 5 pipeline + File.Copy .prev + cumulative training-set + versionProvider.Update)
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — MODIFIED: buildDecisionLog + handler + mapEndpoints updated to thread IModelVersionProvider per-request
- `src/SmartRouter.Cli/CompositionRoot.fs` — MODIFIED: 2 new open statements + 5 DI registrations (Configure + double-reg MVP + double-reg RetrainingService)
- `src/SmartRouter.Cli/appsettings.json` — MODIFIED: Retraining section (12 keys)
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — MODIFIED: ModelVersionProvider.fs + RetrainingService.fs compile entries after Validator.fs

## Decisions Made

- **SemaphoreSlim(1,1).Wait(0)** — Mutex would break thread affinity across task{} await points (CONTEXT.md Lock 7 enforced). Concurrent triggers log Warning and return immediately (skip semantics, not queue).
- **ModelVersionProvider double-registration** — concrete AddSingleton<ModelVersionProvider> + interface alias AddSingleton<IModelVersionProvider> resolving via GetRequiredService<ModelVersionProvider>() — single instance for both roles. ChatCompletions reads via interface; RetrainingService updates via concrete. Mirrors HardCaseDatasetWriter pattern (lines 389-402) minus the AddHostedService leg since ModelVersionProvider is not a BackgroundService.
- **RetrainingService double-registration** — concrete AddSingleton<RetrainingService> + AddHostedService<RetrainingService>(factory) — no IInterface alias needed since RetrainingService is consumed only by IHostedService machinery and the test-only RunNowAsync seam.
- **ChatCompletions IModelVersionProvider per-request** — mapEndpoints resolves GetRequiredService<IModelVersionProvider>() per request so the live value (post-retrain) is always used for DecisionLog.model_version. RoutingAlgorithmRegistration.ModelVersion preserved for backward-compat but no longer load-bearing.
- **Lock 5 mandatory pipeline order** — TrainTestSplit called before retrain; retrain takes split.TrainSet only; both computeBaseline and validate use the same split.TestSet. This prevents optimistically biased metrics.
- **ExceptionDispatchInfo for OCE re-throw** — F# FS0413 error: `reraise()` is not valid inside a `task{}` computation expression's nested try/with handler. Fix: `System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(oce).Throw()` preserves the original stack trace and correctly propagates the OperationCanceledException to the outer ExecuteAsync loop.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] reraise() not valid in task{} nested try/with (FS0413)**
- **Found during:** Task 2 (RetrainingService.fs compilation)
- **Issue:** F# compiler emits FS0413 "call to reraise can only occur directly in the handler of a try/with" when `reraise()` appears inside a `task{}` CE's nested try/with block
- **Fix:** Replaced `reraise()` with `System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(oce).Throw()` which preserves the original stack trace while satisfying the F# compiler
- **Files modified:** src/SmartRouter.Cli/Adapters/RetrainingService.fs
- **Verification:** Build passes with 0 errors, 0 warnings; OCE still propagates correctly to outer loop
- **Committed in:** 8bf94e9 (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 — Bug)
**Impact on plan:** Fix preserves the semantically intended behavior (OCE propagation). No scope creep.

## Issues Encountered

- **FS0413 `reraise()` in task{}** — known F# limitation: `reraise()` is only valid at the immediate level of a `with` handler, not nested inside a computation expression. Fixed via ExceptionDispatchInfo (see Deviations above).

## Build / Test Status

- `dotnet build SmartRouter.slnx`: **0 errors, 0 warnings** (TreatWarningsAsErrors=true)
- `dotnet test`: **66 passed + 10 ignored, 0 failed** — Phase 7 baseline preserved; no new tests (Plan 08-03 ships them)

## Forward-Links to Phase 9 (canary)

- `IModelVersionProvider` is now DI-injectable as a singleton — Phase 9's CanaryService can resolve it
- `models/router.zip.prev` is now reliably maintained after each successful retrain — Phase 9 rollback can copy it back
- `AddPredictionEnginePool` registration in CompositionRoot was NOT touched — Phase 9 can append `.FromFile(modelName="router-canary", ...)` without conflict
- `RunNowAsync(CancellationToken)` test seam is exposed on `RetrainingService` — Plan 08-03 test4/5/6/7 resolve the concrete type and call it directly

## Next Phase Readiness

- Plan 08-03 (tests) can now write `RetrainingTests.fs` that constructs RetrainingService from temp-dir fixtures and calls RunNowAsync to drive the full pipeline
- Phase 9 canary can resolve IModelVersionProvider singleton from DI, read CurrentVersion, and inject into CanaryService without any changes to this phase's registrations

---
*Phase: 08-retraining-loop*
*Completed: 2026-05-08*
