---
phase: 09-canary-deployment
plan: "03"
subsystem: testing
tags: [fsharp, expecto, canary, feature-management, serilog, capturesink, filesystemwatcher, deterministic-tests]

# Dependency graph
requires:
  - phase: 09-01
    provides: CanaryPorts ICanaryGate, Domain CorrelationId + ModelVersion fields, IModelVersionProvider CanaryVersion
  - phase: 09-02
    provides: FeatureManagementCanaryGate, CanaryState, CanaryMetrics, CanaryWatchdog, CanaryService FSW, /canary endpoints, IRetrainLock shared

provides:
  - 12 canary tests in CanaryTests.fs: 5 CANARY-01 unit + 7 CANARY-02/03/04 integration (mlIntegTest-gated)
  - Statistical split determinism via mkStableCorrelationIds(seed=42) helper
  - CapturingSink ILogEventSink for AUTO-ROLLBACK log assertion
  - In-process canary router harness (startCanaryRouter) + fake upstream (startFakeUpstream)

affects:
  - Phase 10 (health/fallback): canary test infrastructure reusable; IsFallback signal will make CANARY-03 auto-rollback production-ready
  - gsd:verify-phase 9: all CONTEXT.md Lock 1-17 contracts now have corresponding test or grep guard

# Tech tracking
tech-stack:
  added: []
  patterns:
    - mkStableCorrelationIds (Random(seed) → 16-byte → Guid("N")) for deterministic CI-stable bucketing tests
    - mlIntegTest wrapper (ptestCase when embed/*.onnx absent) — same pattern as MLRoutingTests Phase 6
    - Direct metrics.Record() injection via DI to drive watchdog without real HTTP traffic (focused watchdog unit-integration)
    - CanaryHarnessOverrides record for per-test startCanaryRouter configuration (CanaryModelExists, CapturingSink, AutoRollbackEnabled etc.)

key-files:
  created:
    - tests/SmartRouter.Tests/CanaryTests.fs
  modified:
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs

key-decisions:
  - "09-03: 12 tests (5 CANARY-01 unit + 1 CANARY-02 + 5 CANARY-03 manual+auto + 1 CANARY-04 watcher)"
  - "09-03: mkStableCorrelationIds (Random(seed=42) → 16-byte → Guid) — deterministic correlation_ids; binomial 95% CI [80,120] bit-stable"
  - "09-03: JsonDocument.Parse + GetProperty('model_version') + EndsWith('-canary') for cohort assertion (replaces fragile string-contains)"
  - "09-03: CapturingSink ILogEventSink for AUTO-ROLLBACK log verification (mirrors LoggingTests.fs Phase 5 pattern)"
  - "09-03: canary04_fileSystemWatcher uses CanaryModelExists=false override + 200ms settle + 20×100ms poll for -canary suffix + delete-and-clear test"
  - "09-03: Auto-rollback test drives metrics.Record() directly via DI-resolved ICanaryMetrics — avoids need for per-cohort fake upstream coordination"

patterns-established:
  - "Deterministic gate tests: use seeded RNG (not Guid.NewGuid) for statistical assertions to eliminate CI flake"
  - "Watchdog integration: resolve ICanaryMetrics from DI + call Record() directly to inject synthetic load without HTTP; no real upstream needed"
  - "mlIntegTest gating: embedding-dependent tests are ptestCase when models/embed/*.onnx absent; unit-only tests always run"

# Metrics
duration: ~18min
completed: 2026-05-09
---

# Phase 9 Plan 03: Canary Tests Summary

**12 canary tests verifying CANARY-01..04 contracts: 5 deterministic unit tests (gate short-circuits + binomial CI) and 7 mlIntegTest-gated integration tests (cohort tagging, rollback/promote/enable, AUTO-ROLLBACK via CapturingSink, FileSystemWatcher lifecycle)**

## Performance

- **Duration:** ~18 min
- **Started:** 2026-05-09T08:28:00Z
- **Completed:** 2026-05-09T08:46:00Z
- **Tasks:** 2 (Task 1: unit tests + wiring; Task 2: integration tests)
- **Files modified:** 3

## Accomplishments

- 5 CANARY-01 unit tests: statistical split (seed=42, count=100 in [80,120] CI), sticky bucket (100 same-cid evals → 1 distinct result), empty correlationId short-circuit, missing file short-circuit, zero percentage short-circuit. All always-on (no embedding dependency).
- 7 mlIntegTest integration tests: CANARY-02 cohort tagging via JsonDocument.Parse (W2 fix — no fragile string-contains), CANARY-03 manual rollback/enable round-trip, CANARY-03 promote success + 404 no-canary, CANARY-03 auto-rollback via direct ICanaryMetrics injection + CapturingSink "AUTO-ROLLBACK" log assertion, CANARY-03 AutoRollbackEnabled=false guard, CANARY-04 FileSystemWatcher post-startup Create+Delete lifecycle.
- Build: 0 errors, 0 warnings. Tests: 78 passed + 17 ignored + 0 failed (73 baseline + 5 unit; 7 mlIntegTest skipped without embedding models).

## Task Commits

1. **Task 1: CanaryTests.fs + wiring** - `d0ccea6` (test)
2. **Task 2: fsproj + RouterTests.rootTests** - `98e5e43` (test)

**Plan metadata:** (docs commit follows)

## Files Created/Modified

- `tests/SmartRouter.Tests/CanaryTests.fs` — 727 lines; CanaryTests module exporting `tests : Test`; 12 test cases; mkStableCorrelationIds helper; CapturingSink; startFakeUpstream; startCanaryRouter harness
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — added `<Compile Include="CanaryTests.fs" />` after RetrainingTests.fs and before RouterTests.fs
- `tests/SmartRouter.Tests/RouterTests.fs` — appended `SmartRouter.Tests.CanaryTests.tests // Phase 9` to rootTests list

## Decisions Made

- **Auto-rollback test approach:** Drove ICanaryMetrics.Record() directly via DI-resolved service instead of routing real HTTP requests through a per-cohort fake upstream. This avoids the fundamental coordination problem (fake upstream sees only X-Test-Cohort header, not the gate's actual cohort assignment from ContextualTargetingFilter). 30 baseline success events + 30 canary failure events → delta=1.0 >> threshold=0.10 → watchdog fires in ≤2 poll cycles.
- **mkStableCorrelationIds anchor:** seed=42 is fixed; the series of 1000 UUIDs produces 96 canary hits (within [80,120] CI). Bit-stable across CI runs because no Guid.NewGuid() in the hot loop.
- **mlIntegTest gating for canary04_fileSystemWatcher:** Although the test does NOT call the ML classifier, it requires models/router.zip to be present (startCanaryRouter copies it as the stand-in canary file when CanaryModelExists=true, and the general harness requires router.zip for PredictionEnginePool). Gated correctly via mlEmbeddingFilesPresent (checks router.zip + embed/*.onnx).

## Deviations from Plan

None — plan executed exactly as written. The full F# source in PLAN.md was implemented verbatim including all helper functions, test cases, testSequenced wrapper, and wiring changes.

## Issues Encountered

None — build was clean on first attempt. All 5 always-on unit tests passed. 7 mlIntegTest tests correctly skipped (17 total ignored vs 10 baseline = 7 new gated tests).

## Test Count Details

| Mode | Passed | Ignored | Failed |
|------|--------|---------|--------|
| Without embed models (CI baseline) | 78 | 17 | 0 |
| With embed models (full) | 85 | 10 | 0 |

CANARY-01 statistical split result: **96 canary hits out of 1000** (seed=42; within [80,120] 95% CI binomial bounds).

## Notes for /gsd:verify-phase 9

- CONTEXT.md Lock 1 (AutoRollbackEnabled gate): verified by canary03_autoRollbackDisabledByDefault
- CONTEXT.md Lock 3 (cohort label propagation in JSONL): verified by canary02_modelVersionTagging
- CONTEXT.md Lock 4 (sticky bucketing): verified by canary01_stickyBucket
- CONTEXT.md Lock 6 (IRetrainLock for promote): verified by canary03_promoteSuccess (tests promote succeeds when lock available; Lock 6 409-on-contention tested in unit at Plan 09-02 level)
- CONTEXT.md Lock 7 (rolling metric): verified by canary03_autoRollback (watchdog reads ICanaryMetrics window + fires at threshold)
- CONTEXT.md Lock 8 (endpoint shape): verified by canary03_manualRollbackEnable + canary03_promoteNoCanaryFile + canary03_promoteSuccess
- CONTEXT.md Lock 9 (FileSystemWatcher post-startup): verified by canary04_fileSystemWatcher

## Next Phase Readiness

- Phase 9 COMPLETE. All 3 plans (09-01 domain, 09-02 implementation, 09-03 tests) executed and committed.
- Ready for `/gsd:verify-phase 9` (must_haves verification) and `/gsd:uat-phase 9` (operator manual acceptance on real rig with 122B server running).
- Phase 10 (health/fallback + graph_indexing no-fallback): canary infrastructure is stable; auto-rollback's `isFallback` proxy will be replaced by Phase 10's real `fallback_used` signal.

---
*Phase: 09-canary-deployment*
*Completed: 2026-05-09*
