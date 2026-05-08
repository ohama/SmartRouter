---
phase: 08-retraining-loop
plan: 01
subsystem: ml-retraining
tags: [fsharp, mlnet, dataset-merger, retrainer, validator, imodelversion]

# Dependency graph
requires:
  - phase: phase-07
    provides: HardCaseEntry type + IHardCaseDatasetWriter + RetrainingPorts.fs BCL-only base + datasets/hard-cases.jsonl schema
provides:
  - IModelVersionProvider port (BCL-only) appended to Core/RetrainingPorts.fs
  - DatasetMerger.fs: readHardCases / readTrainingSet / merge (70/30 class-stratified + rebalance) / saveTrainingSet
  - Retrainer.fs: TrainSample [<CLIMutable>] + retrain (MLContext+IDataView signature, Lock 5)
  - Validator.fs: ValidationResult DU + computeBaseline + validate + writeRejectionLog
affects: [phase-08-02, phase-08-03, phase-09]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "DatasetMerger empty-old bootstrap: merge with old=[||] returns new samples (post-rebalance) — no synthetic-noise injection on first retrain (Lock 3)"
    - "FileShare.ReadWrite for reading files HardCaseDatasetWriter holds exclusively (Pitfall 3 — applies to any future Channel-backed JSONL writer)"
    - "Retrainer MLContext+IDataView signature: caller constructs MLContext + IDataView + TrainTestSplit; Retrainer trains on caller-supplied trainView (typically split.TrainSet); returns ITransformer so Validator can evaluate without reload"
    - "Compile-order: Retrainer.fs MUST precede DatasetMerger.fs and Validator.fs (both open Retrainer for TrainSample) — RESEARCH.md lines 66-71 has inverted order, DO NOT use"
    - "fallback_rate := 1.0 - PositiveRecall (Lock 1) — single ML.NET BinaryClassificationMetrics field, no coupling to MlThreshold"

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/Retrainer.fs
    - src/SmartRouter.Cli/Adapters/DatasetMerger.fs
    - src/SmartRouter.Cli/Adapters/Validator.fs
  modified:
    - src/SmartRouter.Core/RetrainingPorts.fs
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj

key-decisions:
  - "Lock 5 enforced at Retrainer signature level: retrain : MLContext -> IDataView -> string -> float32 -> ITransformer (caller does TrainTestSplit; Retrainer never sees held-out set)"
  - "Lock 1 enforced: fallback_rate = 1.0 - metrics.PositiveRecall in both computeBaseline and validate"
  - "Lock 3 enforced: bootstrap path when oldSamples=[||] returns new samples post-rebalance, no 70/30 scaling, no synthetic noise"
  - "ARCH-01 preserved: IModelVersionProvider uses only string + unit; zero forbidden imports in Core"
  - "RESEARCH.md compile order is inverted — Retrainer.fs must come BEFORE DatasetMerger.fs (DatasetMerger opens Retrainer for TrainSample)"

patterns-established:
  - "TrainSample mirrors MlNetClassifier.RouteInput exactly: [<CLIMutable>] + [<VectorType(1024)>] Features:float32[] + Label:bool (true=Route122B)"
  - "Atomic model write: .tmp + File.Move(overwrite=true), NO File.Delete before move (Lock 8)"
  - "Validator rejection gate: strict — < for accuracy, > for fallback_rate; equality passes"

# Metrics
duration: 7min
completed: 2026-05-08
---

# Phase 08 Plan 01: Dataset Merger and Retrainer Summary

**ML.NET retraining pipeline foundation: 70/30 stratified dataset merge with class rebalancing, caller-driven train/test split Retrainer, and PositiveRecall-based Validator gate**

## Performance

- **Duration:** 7 min
- **Started:** 2026-05-08T20:13:31Z
- **Completed:** 2026-05-08T20:20:57Z
- **Tasks:** 4
- **Files modified:** 5 (1 appended, 3 created, 1 .fsproj edited)

## Accomplishments
- IModelVersionProvider BCL-only port appended to Core/RetrainingPorts.fs (ARCH-01 preserved)
- DatasetMerger.fs ships readHardCases/readTrainingSet with FileShare.ReadWrite (Pitfall 3 mitigation), 70/30 merge with >=30% class-balance enforcement, and first-retrain bootstrap (no synthetic noise)
- Retrainer.fs: retrain signature matches Lock 5 (MLContext+IDataView — caller owns MLContext and does TrainTestSplit); atomic .tmp+File.Move write (Lock 8)
- Validator.fs: fallback_rate=1.0-PositiveRecall (Lock 1); Accepted/Rejected gate; writeRejectionLog to JSONL

## Task Commits

Each task was committed atomically:

1. **Task 1: Append IModelVersionProvider port to Core** - `3528d48` (feat)
2. **Task 2: Implement DatasetMerger.fs** - `5e1d178` (feat)
3. **Task 3: Implement Retrainer.fs and Validator.fs** - `823c565` (feat)
4. **Task 4: Wire .fsproj compile order + verify full build** - `6a8b1eb` (chore)

## Files Created/Modified
- `src/SmartRouter.Core/RetrainingPorts.fs` - Appended IModelVersionProvider (BCL-only, string+unit)
- `src/SmartRouter.Cli/Adapters/Retrainer.fs` - TrainSample [<CLIMutable>] + retrain (4-arg, returns ITransformer)
- `src/SmartRouter.Cli/Adapters/DatasetMerger.fs` - readHardCases/readTrainingSet/hardCaseToTrainSample/merge/saveTrainingSet
- `src/SmartRouter.Cli/Adapters/Validator.fs` - ValidationResult DU + computeBaseline + validate + writeRejectionLog
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` - Added 3 Compile entries: Retrainer→DatasetMerger→Validator (after HardCaseDatasetWriter, before Endpoints/)

## Decisions Made
- Lock 5 enforced at Retrainer signature level: `retrain : MLContext -> IDataView -> string -> float32 -> ITransformer`. Caller (Plan 08-02 RetrainingService) owns MLContext and does TrainTestSplit before calling Retrainer. Retrainer never sees the held-out set.
- Lock 1 enforced: `fallback_rate := 1.0 - metrics.PositiveRecall` — single ML.NET field, no coupling to MlThreshold config.
- Lock 3 enforced: when `oldSamples = [||]` (first retrain), merge returns new samples post-rebalance without 70/30 scaling and without synthetic-noise injection.
- ARCH-01 preserved: IModelVersionProvider added with only `string` and `unit` (both BCL). Zero forbidden imports across all of `src/SmartRouter.Core/`.

## Deviations from Plan

None - plan executed exactly as written.

The only notable observation: RESEARCH.md lines 66-71 lists compile order as DatasetMerger→Retrainer→Validator. This is confirmed wrong (DatasetMerger opens Retrainer for TrainSample — FS0039 error if inverted). The correct order Retrainer→DatasetMerger→Validator was used per PLAN.md Task 4 warning. RESEARCH.md not corrected in-place to preserve historical record.

## Issues Encountered
- None. All ML.NET API calls (LbfgsLogisticRegression, BinaryClassification.Evaluate, Model.Save/Load) match RESEARCH.md exactly. No surprises.

## Build / Test Status
- `dotnet build SmartRouter.slnx`: **0 errors, 0 warnings** (TreatWarningsAsErrors=true on Cli)
- `dotnet test`: **66 passed, 10 ignored, 0 failed** (Phase 7 baseline preserved; new tests ship in Plan 08-03)

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Plan 08-02 (RetrainingService BackgroundService) unblocked: all four building blocks (readHardCases, merge, retrain, validate) are compiled and committed.
- Plan 08-03 (RetrainingTests) unblocked: DatasetMerger/Retrainer/Validator are in .fsproj and can be directly unit-tested.
- Plan 08-02 must: register IModelVersionProvider singleton; add Retraining appsettings.json section (Lock 6); implement runRetrain with mandatory pipeline order (Lock 5 steps 1-7); use SemaphoreSlim(1,1) with Wait(0) for concurrency (Lock 7).
- Phase 9 note: IModelVersionProvider is DI-injectable (BCL-only port) — ready for canary's per-request model_version resolution.

---
*Phase: 08-retraining-loop*
*Completed: 2026-05-08*
