# Phase 8: Retraining Loop — Locked Decisions

**Created:** 2026-05-09
**Source:** Inputs from 08-RESEARCH.md "Decisions to Lock at Planning Time" + planner adjudication of two open questions left to the planner.
**Purpose:** Single source of truth for architectural decisions Plans 08-01..08-03 must honor. No revisiting unless ROADMAP.md changes.

---

## Lock 1 — `fallback_rate` validation metric

**Decision:** **Option A — `fallback_rate := 1.0 - metrics.PositiveRecall`**.

`metrics` is the `BinaryClassificationMetrics` returned by
`mlContext.BinaryClassification.Evaluate(predictions, labelColumnName="Label", scoreColumnName="Score")`.

**Why Option A over Option B (confidence-margin proxy):**
- Single ML.NET-native metric — no coupling to `Routing.ML.Threshold`, which can change.
- Semantically cleanest: "rate at which model fails to route 122B-bound prompts to 122B" = false-negative rate on the positive class (Route122B = `Label=true`).
- Reproducible across hosts because it depends only on (model, held-out set), not on runtime config.

**Validator gate:** `Rejected` if either:
- `newAcc < baselineAcc`, OR
- `newFbRate > baselineFbRate` (strictly greater — equality passes).

Baseline computed fresh per retrain by loading current `models/router.zip` and evaluating on the held-out split. No cached baseline across host restarts.

---

## Lock 2 — `model_version` hot-update approach

**Decision:** **Option A — new `IModelVersionProvider` port in Core (BCL-only); Cli holds mutable singleton; `RetrainingService` updates it after successful write.**

**Hexagonal architecture is preserved** — Core owns the port (BCL-only); Cli owns the mutable state.

**Port (Core, BCL-only):**

```fsharp
// Append to src/SmartRouter.Core/RetrainingPorts.fs (or new MLPorts addition).
// Decided: append to RetrainingPorts.fs to keep retraining-related ports colocated.
type IModelVersionProvider =
    abstract member CurrentVersion : string with get
    abstract member Update         : newVersion: string -> unit
```

**Adapter (Cli):** `Adapters/ModelVersionProvider.fs` — mutable string field guarded by `lock` for non-tearing string read/write across threads.

**Wiring change in CompositionRoot:**

`RoutingAlgorithmRegistration.ModelVersion` is now resolved per-request, not at startup. Two adjustments:

1. Register `IModelVersionProvider` as singleton; seed initial version with `computeModelVersion mlOpts.ModelPath` at startup.
2. `RoutingAlgorithmRegistration.ModelVersion` field becomes load-bearing as a snapshot at registration time only — leave it (do not break the type) but `ChatCompletions.fs` will be updated to resolve `IModelVersionProvider` per-request and use that for `DecisionLog.model_version` instead of `regn.ModelVersion`.

**Critical:** Plan 08-02 modifies `ChatCompletions.fs` `buildDecisionLog` (line 112: `model_version = regn.ModelVersion`) to read from `IModelVersionProvider.CurrentVersion`. This is mandatory for RETRAIN-04 verification.

---

## Lock 3 — Dataset baseline source ("the 70%")

**Decision:** **Option B variant — first retrain has no "old" dataset; merger handles empty-old gracefully.**

`datasets/training-set.jsonl` does NOT exist as a committed seed. It is created by `RetrainingService` after each successful retrain (writes the merged-and-validated training set there for the next cycle).

**Bootstrap behavior (first retrain only):**
- `DatasetMerger.readTrainingSet` returns `[||]` if `datasets/training-set.jsonl` is absent (not an error).
- `DatasetMerger.merge` accepts old=`[||]` and returns the new samples unchanged (post-rebalance) without scaling. Skip the 70/30 ratio when old is empty.
- Log `Information` once: `"DatasetMerger: no existing training-set.jsonl found; using {N} new samples as bootstrap (70/30 split skipped on first retrain)."`
- After successful validation + write, `DatasetMerger.saveTrainingSet` writes the merged set to `datasets/training-set.jsonl`. Subsequent retrains have continuity.

**Why this over Option A (committed baseline.jsonl):**
- Zero seed-file maintenance burden.
- Aligns with existing `scripts/seed-hard-cases.fsx` synthetic-prime pattern for `hard-cases.jsonl`.
- The first retrain happens after 500 organic hard cases — at that scale, a 100% new-data set with class rebalancing is more representative than a synthetic seed.

**Why this over RESEARCH.md's recommendation to use 200 synthetic ModelBootstrapper samples:**
- Synthetic bootstrap data is randomly-generated 1024-dim noise (`rng.NextDouble * 0.002 - 0.001`); training on 70% noise + 30% real data injects pure noise into the classifier. Better to skip the 70/30 ratio on first retrain than to dilute real signal with synthetic noise.

---

## Lock 4 — `hard-cases.jsonl` after retrain (truncate vs cumulative)

**Decision:** **Option B — keep cumulative `hard-cases.jsonl`; track high-water mark in state file.**

**State file:** `datasets/.last-retrain.json` (note leading dot — operator-readable but signals "internal state").

**Schema (single object, not JSONL):**

```json
{
  "schema_version": 1,
  "last_retrain_utc": "2026-05-09T03:00:00Z",
  "hard_case_count_at_retrain": 542,
  "model_version_after": "ml-a1b2c3d4"
}
```

**Trigger logic:** `currentHardCaseCount - hardCaseCountAtRetrain >= CountThreshold` (default 500).

If `.last-retrain.json` is absent, treat `hardCaseCountAtRetrain = 0` (first retrain triggers at 500 entries).

**State file write:** Synchronous, atomic via `.tmp + File.Move(overwrite=true)` inside `runRetrain`'s success arm AFTER both the model write and `training-set.jsonl` write succeed. If state-file write fails, the next tick sees the old state and may re-run the retrain — idempotent because validation will gate the write.

**Why this over Option A (truncate after retrain):**
- Audit trail intact — operator can grep `hard-cases.jsonl` for any historical correlation_id.
- No data loss on retrain crash (truncate-then-crash loses the just-merged data forever).
- Cost of cumulative file: at 500 entries/cycle and 1KB/entry, ~12MB/year. Negligible.

---

## Lock 5 — Held-out set strategy + train/validate pipeline order

**Decision:** **Carve from merged dataset via `mlContext.Data.TrainTestSplit(testFraction = 0.2, seed = 42)` BEFORE training; train on `split.TrainSet` only; validate (both candidate and baseline) on `split.TestSet`.**

- 80% train / 20% held-out.
- Seed = 42 (matches `ModelBootstrapper.ensureDummyModel` and the rest of the project).
- No separate held-out file. Ephemeral per retrain.
- Reproducible across retrains on the same merged dataset.

**Mandatory pipeline order (RetrainingService.runRetrain):**

1. Build `mlContext = MLContext(seed = Nullable<int>(opts.HeldOutRandomSeed))`.
2. Load merged samples into `dataView = mlContext.Data.LoadFromEnumerable(mergedSamples)`.
3. **Split FIRST:** `let split = mlContext.Data.TrainTestSplit(dataView, testFraction = opts.HeldOutFraction, seed = Nullable<int>(opts.HeldOutRandomSeed))`.
4. **Train candidate on `split.TrainSet`:** `let candidate = Retrainer.retrain mlContext split.TrainSet candidatePath opts.L2Regularization`.
5. **Evaluate candidate on `split.TestSet`:** `let candidateMetrics = Validator.evaluate mlContext candidate split.TestSet` — fair (held-out is unseen).
6. **Evaluate baseline on `split.TestSet`:** `let baselineMetrics = Validator.computeBaseline mlContext opts.ModelPath split.TestSet` — same split → fair comparison.
7. Pass both metrics to `Validator.validate` for the final accept/reject decision.

**Why split-before-train (not train-then-split):**
- Training on data that includes the held-out set produces an optimistically biased validation metric — the model has memorized samples it's then evaluated on.
- Worse: if the candidate is trained on full data and the baseline is evaluated on a portion of the candidate's training data, the baseline (which never saw any of these samples) is at an unfair distributional disadvantage.
- Locking the order at "split first, train on TrainSet only, evaluate both on TestSet" makes the comparison apples-to-apples.

**Retrainer signature:** `retrain : MLContext -> IDataView -> string -> float32 -> ITransformer`. The IDataView parameter is whatever the caller wants trained (typically `split.TrainSet`). Retrainer never internally splits — that responsibility lives in the caller (RetrainingService).

**Configurable via:**
- `Routing.Retraining.HeldOutFraction` (default 0.2)
- `Routing.Retraining.HeldOutRandomSeed` (default 42)

---

## Lock 6 — `appsettings.json` Retraining section

Add a new top-level section (NOT nested under `Routing`) so it parallels `TeacherLabeler` and `HardCaseDataset`:

```json
"Retraining": {
  "IntervalMinutes":         60,
  "HardCaseCountTrigger":    500,
  "CountCheckIntervalMinutes": 5,
  "HardCasePath":            "datasets/hard-cases.jsonl",
  "TrainingSetPath":         "datasets/training-set.jsonl",
  "StatePath":               "datasets/.last-retrain.json",
  "ModelPath":               "models/router.zip",
  "PreviousModelPath":       "models/router.zip.prev",
  "RejectionLogPath":        "logs/retraining-rejections.jsonl",
  "HeldOutFraction":         0.2,
  "HeldOutRandomSeed":       42,
  "L2Regularization":        0.1
}
```

Note: `IntervalMinutes` (not `IntervalHours`) — finer granularity for tests; default 60 = the spec's 1-hour cadence. Tests override to small values via `AddInMemoryCollection`.

`PreviousModelPath` is for Phase 9 rollback (per RESEARCH.md Pitfall 10). Plan 08-02's `runRetrain` copies the existing `router.zip` to `router.zip.prev` BEFORE writing the new one. Phase 9 reads this; Phase 8 only writes.

---

## Lock 7 — Lock primitive

**`SemaphoreSlim(1, 1)` with `Wait(0)` (non-blocking try-acquire).** NOT `System.Threading.Mutex` (thread affinity breaks across `task{}` await points — see RESEARCH.md Pitfall 1).

Concurrent triggers SKIP (log `Warning`), do NOT queue. RETRAIN-05 verification asserts: exactly one retrain runs to completion when two triggers fire concurrently; the other produces a "skipping" log line.

---

## Lock 8 — File ownership & atomicity

| File | Owner | Atomicity |
|------|-------|-----------|
| `models/router.zip` | Plan 08-02 (RetrainingService) | `.tmp + File.Move(overwrite=true)` |
| `models/router.zip.prev` | Plan 08-02 (RetrainingService) | `File.Copy(router.zip, router.zip.prev, overwrite=true)` BEFORE the rename |
| `datasets/training-set.jsonl` | Plan 08-02 | `.tmp + File.Move(overwrite=true)` after model write |
| `datasets/.last-retrain.json` | Plan 08-02 | `.tmp + File.Move(overwrite=true)` after training-set write |
| `datasets/hard-cases.jsonl` | Plan 07-04 (`HardCaseDatasetWriter`) | Phase 8 reads only with `FileShare.ReadWrite` |
| `logs/retraining-rejections.jsonl` | Plan 08-02 | Append-only synchronous (low volume; no BackgroundService) |

`File.Delete(modelPath)` BEFORE rename is forbidden — creates a window where `router.zip` is missing. Use `File.Move(tmp, dst, overwrite=true)` (single `rename(2)` syscall on Unix; atomic).

---

## Lock 9 — F# idiom: no `while!`

The codebase consistently uses `while not stoppingToken.IsCancellationRequested do ...` and `while more do ...` (see `DecisionLogWriter.fs:79`, `HardCaseDatasetWriter.fs:129`, `QueueDispatcher.fs:140`). **Plan 08-02 must NOT use the `while!` syntax sugar**, even though F# 10 supports it — for consistency with the rest of the project. Use the imperative `mutable running` pattern with `let! tick = timer.WaitForNextTickAsync(ct)` inside.

---

## Lock 10 — Pure-Core invariant preservation

Phase 8 adds ONE Core type (`IModelVersionProvider` interface). Append to `src/SmartRouter.Core/RetrainingPorts.fs`. Allowed types: BCL only (`string`, `unit`).

ARCH-01 grep verification (CI):

```bash
grep -rn "Serilog\|HttpClient\|AspNetCore\|FSharp.SystemTextJson\|Microsoft.ML" src/SmartRouter.Core/
# MUST return 0 lines.
```

Plan 08-01 and 08-02 verify steps must include this grep.

---

## Lock 11 — Phase 9 file-layout constraint forward-compat

Phase 9 (canary) will register `models/router-canary.zip` as a SECOND named pool entry on `AddPredictionEnginePool<RouteInput, RoutePrediction>().FromFile(modelName="router-canary", ...)`. Phase 8 must NOT:

- Delete other `models/*.zip` files (cleanup logic forbidden).
- Modify the `AddPredictionEnginePool` signature in `CompositionRoot.fs` (only ADD lines around it; do not refactor the chain).

Phase 8 owns ONLY `router.zip` and `router.zip.prev`.

---

## Lock 12 — Test infrastructure reuse (Plan 08-03)

- Mirror `HardCaseDatasetTests.fs` temp-dir + lifecycle pattern (see test file lines 13-49: `mkTempDir`, `cleanupDir`, `runWith` with explicit `StartAsync`/`StopAsync`).
- Use `testSequenced` for any test that touches `Console`, the model pool, or shared file paths.
- Add new test file to `SmartRouter.Tests.fsproj` `<Compile>` BEFORE `RouterTests.fs`.
- Append `SmartRouter.Tests.RetrainingTests.tests` to `RouterTests.rootTests` (PITFALL-26: explicit list, no auto-discovery).
- For end-to-end test (RETRAIN-04 model_version flip), reuse `StreamingTests.startTestRouter` pattern: in-process Kestrel, `AddInMemoryCollection` to override `Routing.Algorithm` and `Retraining.*` paths.
- Mark heavy tests (full retrain pipeline with embedder) with `mlTestCase`-style ptestCase guard if `models/embed/*` files absent on executor — same precedent as MLEmbeddingTests Phase 6.

Existing test count baseline: **66 pass + 10 ignored**. Plan 08-03 adds new tests; final count must be ≥66 pass + ≥10 ignored.

---

## Open Items / Forward-Links

- **Phase 9 canary** depends on: `IModelVersionProvider` (DI-injectable), `models/router.zip.prev` (rollback target), stable `AddPredictionEnginePool` signature.
- **Phase 10 fallback**: when `fallback_used=true` flag flips on real fallback events, FailureDetector starts producing real hard cases — no Phase 8 change.
- **PeriodicTimer macOS sleep behavior** (RESEARCH.md Pitfall 4): the count-based trigger covers the wake-from-sleep case. Documented; no code change.
