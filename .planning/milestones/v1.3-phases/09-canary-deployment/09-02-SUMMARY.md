---
phase: 09-canary-deployment
plan: 02
subsystem: ml-routing
tags: [fsharp, dotnet, canary, feature-management, backgroundservice, filesystemwatcher, semaphoreslim, concurrentqueue]

# Dependency graph
requires:
  - phase: 09-01
    provides: "ICanaryGate + IModelVersionProvider.UpdateCanary/CanaryVersion; CorrelationId on RouterRequest; ModelVersion on RoutingDecision; NuGet pin Microsoft.FeatureManagement.AspNetCore 4.5.0"
  - phase: 08-02
    provides: "RetrainingService (modified to inject IRetrainLock); IModelVersionProvider concrete; ML PredictionEnginePool"
provides:
  - "8 new Cli files: CanaryTargetingAccessor, CanaryState, CanaryGate, CanaryMetrics, CanaryWatchdog, RetrainLock, CanaryService (7 adapters) + Endpoints/Canary.fs"
  - "ContextualTargetingFilter sticky bucketing via CanaryTargetingContextAccessor reads correlation_id for SHA-256 hash bucketing"
  - "Dual-classifier dispatch: keyed IClassifier baseline + canary; FeatureManagementCanaryGate two-layer (File.Exists + percentage > 0 + FM hash)"
  - "Rolling 60s fallback metric: ICanaryMetrics + NoOpCanaryMetrics heuristic fallback; ChatCompletions records post-response"
  - "CanaryWatchdog BackgroundService: polls metrics, auto-rollback when AutoRollbackEnabled + delta > threshold + min sample size"
  - "CanaryService: ICanaryService + IHostedService with FileSystemWatcher for post-startup canary file arrival"
  - "IRetrainLock shared between RetrainingService + CanaryService.Promote (409 Conflict under retrain)"
  - "/canary admin endpoint: GET status + POST promote/rollback/enable (loopback-only)"
  - "TryAddSingleton fallback + AddSingleton ML-override DI pattern (last-registration-wins)"
affects:
  - "09-03 (tests): all canary infrastructure testable; CanaryWatchdog, CanaryService, ICanaryGate, ICanaryMetrics all injectable"

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "TryAddSingleton fallback + plain AddSingleton ML-override = last-registration-wins DI default+override idiom"
    - "F# let bindings must precede interface implementations in type body (FS0960)"
    - "F# compile-order: consumed module must precede consumer (RetrainLock before RetrainingService)"
    - "IHostedService pattern: let mutable field bindings at top of type, then interface implementations"
    - "Triple-reg: AddSingleton<Concrete> + AddSingleton<IInterface> + AddHostedService<Concrete> for single-instance BackgroundService"
    - "ExceptionDispatchInfo.Capture(oce).Throw() for OperationCanceledException through task{} try/with (FS0413 reraise guard)"

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/CanaryTargetingAccessor.fs
    - src/SmartRouter.Cli/Adapters/CanaryState.fs
    - src/SmartRouter.Cli/Adapters/RetrainLock.fs
    - src/SmartRouter.Cli/Adapters/CanaryMetrics.fs
    - src/SmartRouter.Cli/Adapters/CanaryGate.fs
    - src/SmartRouter.Cli/Adapters/CanaryWatchdog.fs
    - src/SmartRouter.Cli/Adapters/CanaryService.fs
    - src/SmartRouter.Cli/Endpoints/Canary.fs
  modified:
    - src/SmartRouter.Cli/Adapters/MlNetClassifier.fs
    - src/SmartRouter.Cli/Adapters/RetrainingService.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/Program.fs
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - tests/SmartRouter.Tests/MLClassifierTests.fs
    - tests/SmartRouter.Tests/RetrainingTests.fs

key-decisions:
  - "ContextualTargetingFilter via WithTargeting<CanaryTargetingContextAccessor>; PercentageFilter EXPLICITLY FORBIDDEN (non-sticky — random per evaluation)"
  - "TryAddSingleton<ICanaryGate>(NullCanaryGate) Step 1.0 UNCONDITIONAL before ML branch; AddSingleton<ICanaryGate>(FeatureManagementCanaryGate) Step 1.2 inside ML branch — last-registration-wins override"
  - "RetrainLock.fs placed BEFORE RetrainingService.fs in compile order — F# FS0039 requires consumed type to compile first"
  - "CanaryService let bindings (mutable watcher, onCanaryFileMutation) placed before interface implementations — F# FS0960 rule"
  - "IRetrainLock shared between RetrainingService (skip-if-busy) and CanaryService.Promote (return 409 Conflict on contention)"
  - "FileSystemWatcher armed in IHostedService.StartAsync; StopAsync disposes in separate try/with (F# parsing trap avoidance)"
  - "Dual PredictionEnginePool .FromFile chain: router + router-canary; keyed IClassifier baseline/canary + backwards-compat non-keyed alias"
  - "NoOpCanaryMetrics (heuristic fallback) defined in CanaryMetrics.fs; NullCanaryGate (heuristic fallback) defined in CanaryGate.fs — both file-defined types, not inline object expressions"
  - "CanaryOptions typed record defined in CanaryWatchdog.fs (consumed by CanaryService which opens that module)"
  - "Test construction site fixes (Rule 3 auto-fix): MlNetClassifier + RetrainingService signatures changed; test files updated with new 4th arg RetrainLock()"

patterns-established:
  - "Default+override DI: TryAddSingleton (fallback) + AddSingleton (override) pair for mode-switching interfaces"
  - "F# type body: all let/do bindings before any interface implementations"
  - "FileSystemWatcher lifecycle: arm in IHostedService.StartAsync (non-fatal on failure); dispose in separate try/with in StopAsync"

# Metrics
duration: 12min
completed: 2026-05-09
---

# Phase 9 Plan 02: Implementation Summary

**Canary deployment fully wired: FeatureManagement sticky bucketing + dual-classifier dispatch + FileSystemWatcher + rolling-60s watchdog + /canary admin endpoint + IRetrainLock coordination**

## Performance

- **Duration:** 12 min
- **Started:** 2026-05-09T08:07:40Z
- **Completed:** 2026-05-09T08:19:00Z
- **Tasks:** 3 (+ deviation fixes)
- **Files modified:** 14

## Accomplishments

- 8 new Cli adapter/endpoint files (7 Adapters/ + 1 Endpoints/): sticky bucketing, dual classifier, rolling metrics, FileSystemWatcher, promote/rollback/enable admin
- TryAddSingleton fallback + AddSingleton ML-override DI pattern ensures heuristic mode resolves NullCanaryGate + NoOpCanaryMetrics without branching in ChatCompletions
- RetrainingService refactored to accept shared IRetrainLock; CanaryService.Promote returns 409 Conflict when retrain in progress — race between promote and mid-cycle retrain eliminated
- Build: 0 errors, 0 warnings (TreatWarningsAsErrors=true). Tests: 73 passed, 10 ignored, 0 failed (Phase 8 baseline preserved)

## Task Commits

1. **Task 1: Leaf adapters (TargetingAccessor + CanaryState + CanaryGate + CanaryMetrics + RetrainLock)** - `ae9ad1f` (feat)
2. **Task 2: CanaryWatchdog + CanaryService + /canary endpoint + ChatCompletions metrics** - `f89aa89` (feat)
3. **Task 3: CompositionRoot DI wiring + Program.fs** - `f6ade74` (chore)
4. **Deviation: test construction sites** - `0b87630` (fix)

## Files Created/Modified

- `src/SmartRouter.Cli/Adapters/CanaryTargetingAccessor.fs` - ITargetingContextAccessor reads correlation_id for sticky SHA-256 bucketing
- `src/SmartRouter.Cli/Adapters/CanaryState.fs` - ICanaryState + CanaryState: lock-guarded mutable percentage + rollback marker
- `src/SmartRouter.Cli/Adapters/RetrainLock.fs` - IRetrainLock + RetrainLock: shared SemaphoreSlim(1,1); TryAcquire(0)
- `src/SmartRouter.Cli/Adapters/CanaryMetrics.fs` - ICanaryMetrics + CanaryMetrics (ConcurrentQueue rolling) + NoOpCanaryMetrics (heuristic fallback)
- `src/SmartRouter.Cli/Adapters/CanaryGate.fs` - FeatureManagementCanaryGate (two-layer) + NullCanaryGate (heuristic fallback)
- `src/SmartRouter.Cli/Adapters/CanaryWatchdog.fs` - BackgroundService: polls metrics; auto-rollback when enabled + delta > threshold + min N
- `src/SmartRouter.Cli/Adapters/CanaryService.fs` - ICanaryService + IHostedService: promote/rollback/enable/status + FileSystemWatcher
- `src/SmartRouter.Cli/Endpoints/Canary.fs` - GET /canary + POST /canary/{promote,rollback,enable} (loopback-only)
- `src/SmartRouter.Cli/Adapters/MlNetClassifier.fs` - accepts modelName: string (was hardcoded "router")
- `src/SmartRouter.Cli/Adapters/RetrainingService.fs` - accepts IRetrainLock; private SemaphoreSlim removed; use _ = lockHandle pattern
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` - ICanaryMetrics added to handler; metrics.Record after each Ok-decision decisionLogger.Log (5 arms)
- `src/SmartRouter.Cli/CompositionRoot.fs` - TryAddSingleton fallbacks (Step 1.0) + ML-block overrides (Step 1.2); AddScopedFeatureManagement+WithTargeting; dual .FromFile; keyed classifiers; RetrainingService now injects IRetrainLock
- `src/SmartRouter.Cli/Program.fs` - DefaultRolloutPercentage sync from Canary.PercentageEnabled; Canary.mapEndpoints app
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` - 8 new Compile entries; RetrainLock moved before RetrainingService

## Decisions Made

- ContextualTargetingFilter is the sticky bucketing mechanism. PercentageFilter EXPLICITLY FORBIDDEN (non-sticky — random per evaluation, would break cohort assignment).
- `TryAddSingleton<ICanaryGate>(NullCanaryGate)` placed BEFORE the `if routingAlgoStr = "ml" then` block (Step 1.0 unconditional) so heuristic mode always resolves the interface. ML mode appends `AddSingleton<ICanaryGate>(FeatureManagementCanaryGate)` which wins via last-registration-wins.
- `CanaryOptions` record defined in CanaryWatchdog.fs (imported by CanaryService.fs via open). This keeps the options type adjacent to its primary consumer.
- CanaryService let bindings (`let mutable watcher`, `let onCanaryFileMutation`) placed at the top of the type body before any interface implementations — required by F# FS0960.
- RetrainLock.fs compile order moved to BEFORE RetrainingService.fs — F# FS0039 requires consumed modules to compile first.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] CanaryService.fs F# FS0960: let bindings after interface implementations**

- **Found during:** Task 2 — first build attempt
- **Issue:** The plan's source snippet placed `let mutable watcher` and `let onCanaryFileMutation` AFTER the `interface ICanaryService with` block. F# FS0960 requires all `let`/`do` bindings to precede member/interface definitions in a type body.
- **Fix:** Rewrote CanaryService.fs with the two let bindings at the top of the type body, before `interface ICanaryService with`.
- **Files modified:** `src/SmartRouter.Cli/Adapters/CanaryService.fs`
- **Committed in:** `f89aa89` (Task 2 commit, then `f6ade74` refined)

**2. [Rule 3 - Blocking] RetrainLock.fs compile-order: must precede RetrainingService.fs**

- **Found during:** Task 3 — build after fsproj changes
- **Issue:** Plan's compile order placed RetrainLock.fs in the Phase-9 block AFTER RetrainingService.fs. But RetrainingService now opens SmartRouter.Cli.Adapters.RetrainLock — F# FS0039 "namespace not defined" at compile time.
- **Fix:** Moved `<Compile Include="Adapters/RetrainLock.fs" />` to BEFORE `<Compile Include="Adapters/RetrainingService.fs" />` in SmartRouter.Cli.fsproj.
- **Files modified:** `src/SmartRouter.Cli/SmartRouter.Cli.fsproj`
- **Verification:** Build: 0 errors. 8-entry count + compile-order awk check still pass.
- **Committed in:** `f6ade74`

**3. [Rule 3 - Blocking] Test construction sites broke due to signature changes**

- **Found during:** Task 3 — build of test project
- **Issue:** MlNetClassifier ctor now requires 2 args (pool + modelName); RetrainingService ctor now requires 4 args (opts, embedder, provider, retrainLock). 6 test callsites in MLClassifierTests.fs and RetrainingTests.fs still used old signatures.
- **Fix:** Added modelName="router" to MLClassifierTests; added `new RetrainLock() :> IRetrainLock` to 5 RetrainingService construction sites; added `open SmartRouter.Cli.Adapters.RetrainLock` to RetrainingTests.fs.
- **Files modified:** `tests/SmartRouter.Tests/MLClassifierTests.fs`, `tests/SmartRouter.Tests/RetrainingTests.fs`
- **Verification:** Build 0/0; 73+10+0 tests.
- **Committed in:** `0b87630`
- **Note:** Plan states "Do NOT touch tests/" but this was a blocking build failure (TreatWarningsAsErrors + construction-site arity errors). Classified as Rule 3 auto-fix. Plan 09-03 owns tests going forward.

---

**Total deviations:** 3 auto-fixed (all Rule 3 — blocking build issues)
**Impact on plan:** All fixes necessary to achieve 0-error build and 73+10+0 test baseline. No scope creep. Plan 09-03 contract (73+10+0) preserved exactly.

## Issues Encountered

None beyond the deviations documented above.

## User Setup Required

None — no external service configuration required. The canary system is fully wired but gated on `Routing.Algorithm = "ml"` + `Canary.PercentageEnabled > 0` + `models/router-canary.zip` presence. All three default to inactive (heuristic mode / 0% / no file).

## Next Phase Readiness

- Plan 09-03 (tests) can now write integration tests for all canary infrastructure: ICanaryGate (heuristic=NullCanaryGate, ml=FeatureManagementCanaryGate), ICanaryMetrics cohort recording, CanaryWatchdog auto-rollback trigger, CanaryService promote/rollback/enable, /canary endpoint HTTP responses.
- Test the CANARY-01 statistical split (10% over 1000 UUIDs) and CANARY-02 stickiness (same UUID → same cohort) via AddInMemoryCollection test overrides.
- All CONTEXT.md locks (1-17) now implemented. No blockers.

---
*Phase: 09-canary-deployment*
*Completed: 2026-05-09*
