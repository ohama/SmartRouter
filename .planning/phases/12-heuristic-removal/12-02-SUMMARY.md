---
phase: 12-heuristic-removal
plan: 02
subsystem: infra
tags: [fsharp, composition-root, dependency-injection, ml-routing, heuristic-removal]

# Dependency graph
requires:
  - phase: 12-01
    provides: Core deletion of Heuristic.fs, RoutingReason.Heuristic, RoutingConfig.Keywords/ComplexityThreshold
provides:
  - "configureRequestPipeline: full ML-only DI wiring for production HTTP service path"
  - "configureWithoutMl: offline subset for --retrain and future test fixtures"
  - "configureServices alias pointing to configureRequestPipeline (12-05 will remove)"
  - "appsettings.json cleaned of Algorithm/ComplexityThreshold/Keywords"
  - "Program.fs: --routing-algorithm flag gone; --retrain calls configureWithoutMl directly"
  - "Cli + Core build clean with TreatWarningsAsErrors=true"
affects: ["12-03", "12-04", "12-05"]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Q1=B split pattern: configureRequestPipeline (full ML) + configureWithoutMl (offline subset) replaces single configureServices with runtime algorithm switch"
    - "Unconditional ML wiring: no routingAlgoStr guard; ML is the only path in configureRequestPipeline"
    - "Backwards-compat alias: let configureServices = configureRequestPipeline (removed in 12-05 after test migration)"

key-files:
  created: []
  modified:
    - src/SmartRouter.Cli/appsettings.json
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/Program.fs

key-decisions:
  - "configureWithoutMl includes: HTTP factories, DecisionLogWriter triple-reg, retrain ports (IFailureDetector/ITeacherLabeler/IHardCaseDatasetWriter), TeacherLabelerOptions/HardCaseDatasetOptions/RetrainingOptions IOptions, ModelVersionProvider with ml-retrain placeholder"
  - "configureWithoutMl excludes: ensureEmbeddingFilesPresent, IEmbedder, PredictionEnginePool, IClassifier, makeApplyMl, RoutingAlgorithmRegistration, HealthService, CanaryService/CanaryMetrics/CanaryGate/CanaryWatchdog, RetrainingService BackgroundService, AddHttpContextAccessor, RetrainLock, IUpstreamClient/QueueDispatcher"
  - "ModelVersionProvider initial value in configureRequestPipeline: always ml-{sha} from router.zip (no heuristic-v1 branch)"
  - "ModelVersionProvider initial value in configureWithoutMl: ml-retrain placeholder (no model file check)"
  - "Phase 9 Canary block (AddHttpContextAccessor, ScopedFeatureManagement, RetrainLock, CanaryState/CanaryMetrics/CanaryGate/CanaryWatchdog/CanaryService, RetrainingService) is now unconditional in configureRequestPipeline (was guarded by routingAlgoStr = ml)"

patterns-established:
  - "Split composition pattern: one entry point per caller persona (production HTTP vs offline retrain)"

# Metrics
duration: 7min
completed: 2026-05-09
---

# Phase 12 Plan 02: CLI Rewire Summary

**Composition split: configureRequestPipeline (unconditional ML) + configureWithoutMl (offline retrain subset); --routing-algorithm flag deleted; Cli + Core build clean**

## Performance

- **Duration:** ~7 min
- **Started:** 2026-05-09T07:21:06Z
- **Completed:** 2026-05-09T07:28:02Z
- **Tasks:** 4 (+ final build verification)
- **Files modified:** 3

## Accomplishments

- Deleted `Algorithm`, `ComplexityThreshold`, `Keywords` from `appsettings.json` and `RoutingOptions` record — no heuristic config remains in Routing section
- Split `configureServices` into `configureRequestPipeline` (unconditional ML wiring) and `configureWithoutMl` (retrain-safe subset without IEmbedder/IClassifier/RoutingAlgorithmRegistration)
- Removed `routingAlgoStr` variable and all heuristic dispatch arms (`null | "" | "heuristic"` match case, `"valid values: heuristic, ml"` error, `if routingAlgoStr = "ml"` Phase 9 guard)
- Deleted `--routing-algorithm` CLI flag parsing block from `Program.fs`; `--retrain` branch now calls `configureWithoutMl` directly (eliminates in-memory `Routing:Algorithm = "heuristic"` injection)
- Cli + Core both build with 0 errors, 0 warnings (TreatWarningsAsErrors=true); Tests project broken as expected (12-03/04/05 will fix)

## Task Commits

1. **Task 1: appsettings.json cleanup** - `2e33bf6` (chore)
2. **Task 2: CompositionRoot split** - `18ccfd9` (refactor)
3. **Task 3: Program.fs flag deletion** - `65d624a` (refactor)
4. **Task 4: Solution build sanity** - (no commit — verification only)

## Files Created/Modified

- `src/SmartRouter.Cli/appsettings.json` — Removed `Algorithm`, `ComplexityThreshold`, `Keywords` from `Routing` section
- `src/SmartRouter.Cli/CompositionRoot.fs` — Split into configureRequestPipeline + configureWithoutMl; removed routingAlgoStr; removed heuristic match arm; fixed all stale comments
- `src/SmartRouter.Cli/Program.fs` — Deleted --routing-algorithm flag block; --retrain calls configureWithoutMl

## Decisions Made

- **configureWithoutMl boundary (Q1=B):** INCLUDE HTTP factories, DecisionLogWriter, retrain ports, TeacherLabelerOptions/HardCaseDatasetOptions/RetrainingOptions IOptions, ModelVersionProvider. EXCLUDE: ensureEmbeddingFilesPresent, IEmbedder, PredictionEnginePool, IClassifier, makeApplyMl, RoutingAlgorithmRegistration, HealthService, CanaryService/CanaryGate/CanaryMetrics/CanaryWatchdog, RetrainingService BackgroundService, AddHttpContextAccessor, RetrainLock, IUpstreamClient/QueueDispatcher/IStatsProvider.
- **ModelVersionProvider initial value:** In `configureRequestPipeline` always `ml-{sha}` from `router.zip`. In `configureWithoutMl` uses `ml-retrain` placeholder (no model file check in offline path).
- **Phase 9 Canary block unconditional:** The `if routingAlgoStr = "ml" then` guard for AddHttpContextAccessor/ScopedFeatureManagement/RetrainLock/Canary/RetrainingService registrations removed — block is now unconditional within `configureRequestPipeline`.
- **Backwards-compat alias:** `let configureServices services config = configureRequestPipeline services config` added so test fixtures calling `configureServices` still compile until 12-05 migrates them.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] BgeM3EmbedderOptions doesn't exist as a type**
- **Found during:** Task 2 (configureWithoutMl implementation)
- **Issue:** Plan mentioned "BgeM3EmbedderOptions IOptions binding" but no such type exists in the codebase — BgeM3Embedder takes onnxPath/tokenizerPath/maxTokens directly as constructor args; the Routing:ML section is bound via MlOptions in the full pipeline
- **Fix:** Omitted the non-existent IOptions binding from configureWithoutMl; the retrain path doesn't need the embedder instance at all
- **Files modified:** src/SmartRouter.Cli/CompositionRoot.fs
- **Verification:** Cli builds clean; --retrain path resolves the three retrain ports it needs
- **Committed in:** 18ccfd9 (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 missing critical — non-existent type omitted)
**Impact on plan:** Auto-fix correct; omitting a non-existent IOptions binding has no functional impact. No scope creep.

## Issues Encountered

None beyond the auto-fixed deviation above.

## Next Phase Readiness

- Cli + Core build green — ready for 12-03 (RoutingTests.fs cleanup)
- Tests project has 15 expected errors referencing `scoreComplexity`, `applyHeuristic`, `ComplexityThreshold`, `Keywords`, `Heuristic` namespace — all will be fixed in 12-03/04/05
- `configureServices` alias in CompositionRoot.fs and all test fixture calls to `configureServices` remain in place until 12-05

---
*Phase: 12-heuristic-removal*
*Completed: 2026-05-09*
