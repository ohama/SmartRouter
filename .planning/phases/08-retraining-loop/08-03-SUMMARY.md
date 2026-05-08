---
phase: 08-retraining-loop
plan: 03
subsystem: ml-retraining
tags: [expecto, mlnet, serilog, backgroundservice, semaphoreslim, periodictimer, fsharp]

# Dependency graph
requires:
  - phase: 08-01
    provides: "Retrainer.fs, DatasetMerger.fs, Validator.fs, TrainSample type"
  - phase: 08-02
    provides: "RetrainingService.fs, ModelVersionProvider.fs, RunNowAsync test seam, SemaphoreSlim skip gate"
provides:
  - "7 Expecto tests covering RETRAIN-01 through RETRAIN-06"
  - "FakeEmbedder + ThrowingEmbedder + CapturingSink test doubles (no ONNX dependency)"
  - "Synthetic 1024-dim feature generation pattern for ML.NET unit tests"
  - "Phase 8 retraining loop verified end-to-end"
affects: [phase-09]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Synthetic feature generation for ML.NET tests: float32[1024] from seeded Random with class-separable means — gives LR enough signal to converge in <1s without bge-m3 ONNX"
    - "FakeEmbedder Task.Yield() pattern: fake IEmbedder must yield (not return Task.FromResult) so concurrent RunNowAsync calls are genuinely in-flight simultaneously, enabling SemaphoreSlim.Wait(0) skip to fire"
    - "CapturingSink installed before service construction, restored after test — global Serilog.Log.Logger swap; always save/restore in try/finally"
    - "RetrainingService.RunNowAsync test seam — bypasses PeriodicTimer, runs single full cycle synchronously; distinct from StartAsync which exercises the actual timer loop"

key-files:
  created:
    - tests/SmartRouter.Tests/RetrainingTests.fs
  modified:
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs

key-decisions:
  - "08-03: CapturingSink ILogEventSink pattern installed before service construction; test4 asserts exactly 1 'starting retrain cycle' log + >=1 'skipping trigger' log to prove SemaphoreSlim skip semantics"
  - "08-03: test7_countTriggerFires uses StartAsync (NOT RunNowAsync) with IntervalMinutes=1 to exercise the count-check PeriodicTimer branch — satisfies ROADMAP 'two integration tests' criterion for RETRAIN-02 alongside test1_runNowAsync"
  - "08-03: FakeEmbedder requires Task.Yield() — without it runRetrain executes synchronously (all .NET BCL work completes before yielding), causing both concurrent RunNowAsync calls to run sequentially and both acquiring the semaphore"
  - "08-03: test7 PeriodicTimer with IntervalMinutes=1 means ~60-90s wall-clock wait; 120s deadline bound; runs as testCase (not ptestCase) since no ONNX dependency — test7 passed on all local runs"

patterns-established:
  - "Synthetic feature generation for ML.NET tests: float32[1024] from seeded Random with class-separable means (label=true => +0.5 ± noise; label=false => -0.5 ± noise) — gives LR enough signal to converge in <1s without bge-m3 ONNX"
  - "FakeEmbedder + ThrowingEmbedder pattern: BCL-only test doubles for IEmbedder; avoids loading 580MB ONNX in unit tests"
  - "Train-test seam: RetrainingService.RunNowAsync(CancellationToken) is the test entry point — bypasses PeriodicTimer, runs single full cycle synchronously"

# Metrics
duration: 17min
completed: 2026-05-09
---

# Phase 8 Plan 03: Retraining Tests Summary

**7 Expecto tests covering RETRAIN-01..06: merger balance/bootstrap, timer-driven count trigger via StartAsync, validator accept/reject/log, model_version flip, SemaphoreSlim concurrent-skip with CapturingSink assertion, and throw isolation — 66 → 73 pass, 10 ignored, 0 failed**

## Performance

- **Duration:** 17 min
- **Started:** 2026-05-08T20:40:27Z
- **Completed:** 2026-05-08T20:57:29Z
- **Tasks:** 2 (+ 1 deviation fix)
- **Files modified:** 3

## Accomplishments

- 7 new Expecto tests covering all 6 RETRAIN-* requirements (66 → 73 passing)
- FakeEmbedder + ThrowingEmbedder + CapturingSink test doubles — no ONNX dependency
- test7 runs as `testCase` (not ptestCase) with ~60-90s wall-clock for PeriodicTimer integration
- Phase 8 retraining loop proven end-to-end without bge-m3 model files

## Task Commits

1. **Task 1: Create RetrainingTests.fs** - `2e51a6a` (test)
2. **Task 2: Wire .fsproj + RouterTests.rootTests** - `2170cb9` (test)
3. **Deviation fix: Task.Yield() in FakeEmbedder** - `be5e8a4` (fix)

## RETRAIN Coverage

| Req | Test | Notes |
|-----|------|-------|
| RETRAIN-01 | test1_mergerClassBalance | 200 balanced + 500 imbalanced → >=30% each class |
| RETRAIN-01 | test2_mergerBootstrap | old=[||] → returns new unchanged (Lock 3) |
| RETRAIN-02 | test7_countTriggerFires | StartAsync + PeriodicTimer count-check (integration #2) |
| RETRAIN-03 | test3_validatorGate | accept + reject + rejection log JSONL |
| RETRAIN-04 | test6_modelVersionFlip | ModelVersionProvider.CurrentVersion flips; router.zip.prev created |
| RETRAIN-05 | test4_concurrentTriggers | CapturingSink: 1 start + >=1 skip; SemaphoreSlim proved |
| RETRAIN-06 | test5_throwIsolation | throw mid-embedAll caught; semaphore released; next cycle succeeds |

## Files Created/Modified

- `tests/SmartRouter.Tests/RetrainingTests.fs` — 450-line test file; 7 tests; FakeEmbedder/ThrowingEmbedder/CapturingSink helpers
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — `<Compile Include="RetrainingTests.fs" />` inserted before RouterTests.fs
- `tests/SmartRouter.Tests/RouterTests.fs` — `SmartRouter.Tests.RetrainingTests.tests` appended to rootTests

## Decisions Made

- **CapturingSink with Serilog.Log.Logger swap:** Installed before service construction; test4 asserts exact log strings ("starting retrain cycle" / "skipping trigger") matching the production contract in CONTEXT.md Lock 7
- **test7 as testCase (not ptestCase):** test7 requires only BCL + ML.NET (no ONNX model files); the 60-90s wall-clock cost is acceptable for a local dev machine; ptestCase-gate not needed
- **FakeEmbedder with Task.Yield():** Required so concurrent RunNowAsync calls are genuinely in-flight simultaneously; Task.FromResult returns immediately causing sequential execution and both tasks acquiring the semaphore

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] FakeEmbedder.EmbedAsync needed Task.Yield() for concurrent semaphore test**

- **Found during:** Task 1 test run (test4_concurrentTriggers)
- **Issue:** FakeEmbedder returned `Task.FromResult(...)` (already-completed task). Since all BCL work in `embedAll` completes synchronously without yielding, the first `RunNowAsync` task runs the entire `runRetrain` pipeline before the second task checks the semaphore — both tasks would acquire the semaphore sequentially, producing "2 starts, 0 skips"
- **Fix:** Added `do! Task.Yield()` inside `task {}` CE in FakeEmbedder.EmbedAsync; this yields control to the task scheduler at the first await point so t1 and t2 are genuinely concurrent
- **Files modified:** tests/SmartRouter.Tests/RetrainingTests.fs
- **Verification:** test4 passes; RetrainingTests filter run shows 7/7 passing
- **Committed in:** `be5e8a4`

---

**Total deviations:** 1 auto-fixed (Rule 1 - bug in test double design)
**Impact on plan:** Fix was essential for RETRAIN-05 concurrency contract verification. No scope creep.

## Issues Encountered

- **Pre-existing parallel flakiness in QueueTests (PITFALL-10 starvation) and HardCaseDatasetTests (graceful drain):** These tests are timing-sensitive and fail non-deterministically when run in Expecto's default parallel mode alongside CPU-heavy ML.NET training. Both pass reliably in isolation and in `--sequenced` mode. This pre-existed Phase 8 — confirmed by checking baseline before Plan 08-03 changes. Canonical test run is `dotnet run -- --sequenced` which produces 73 passed, 10 ignored, 0 failed consistently.

## Phase 8 Complete — Forward to Phase 9

- `IModelVersionProvider` singleton ready for canary cohort tagging
- `models/router.zip.prev` maintained — Phase 9 rollback can copy it back
- `AddPredictionEnginePool` registration unchanged — Phase 9 can append `.FromFile(modelName="router-canary")`
- `datasets/training-set.jsonl` is now a stable contract — Phase 9 reads only model artifacts and DecisionLog, not training data
- Test count delta confirmed: 66 pass + 10 ignored (baseline) → 73 pass + 10 ignored (Plan 08-03)

---
*Phase: 08-retraining-loop*
*Completed: 2026-05-09*
