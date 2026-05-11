# Phase 8: Retraining Loop - Research

**Researched:** 2026-05-08
**Domain:** ML.NET LbfgsLogisticRegression retraining, BackgroundService orchestration, atomic file I/O, F# task idioms
**Confidence:** HIGH (codebase read directly; ML.NET source and docs verified; pitfalls sourced from ML.NET GitHub issues)

---

## Summary

Phase 8 implements Loop B: a `BackgroundService` that periodically reads `datasets/hard-cases.jsonl`, merges it with an "old" training set at 70/30 with class balance, retrains the ML.NET LR classifier, validates the result, and atomically replaces `models/router.zip` only if validation passes. The existing `PredictionEnginePool` with `watchForChanges:true` (already wired in Phase 6 `CompositionRoot.fs`) handles hot-reload automatically. The only new orchestration primitive needed is a `SemaphoreSlim(1)` (not `System.Threading.Mutex`) to serialize concurrent triggers.

The codebase already has the complete ML.NET retraining idiom in `ModelBootstrapper.ensureDummyModel`: `MLContext → LoadFromEnumerable<RouteInput> → LbfgsLogisticRegression.Fit → mlContext.Model.Save → File.Move(tmp, dst)`. Phase 8 copies and extends that exact pattern. The only new ML work is: reading JSONL, stratified sampling for 70/30 merge, computing validation metrics on a held-out split, and gating the write behind those metrics.

Phase 9 (canary) requires that `models/router.zip` (baseline) and `models/router-canary.zip` (canary) coexist. `AddPredictionEnginePool` supports multiple named model registrations via chained `.FromFile()` calls — Phase 8 must write only `models/router.zip` and must NOT delete other `models/*.zip` files. The `models/` directory is shared state; Phase 8 owns only `router.zip` writes.

**Primary recommendation:** Implement `DatasetMerger` and `Retrainer` as pure functions in new Cli adapter files; implement `RetrainingService` as a `BackgroundService` mirroring `HardCaseDatasetWriter` + `PeriodicTimer`; use `SemaphoreSlim(1)` not `Mutex`; write the atomicity pattern from `ModelBootstrapper` verbatim (tmp + `File.Move(overwrite:true)`).

---

## Standard Stack

All packages already pinned in `SmartRouter.Cli.fsproj` from Phase 6. No new NuGet packages needed.

### Core (already in project)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `Microsoft.ML` | 5.0.0 | `MLContext`, `LbfgsLogisticRegression`, `IDataView`, `Model.Save` | Pinned Phase 6; already used in `ModelBootstrapper.fs` |
| `Microsoft.Extensions.ML` | 5.0.0 | `PredictionEnginePool` with `watchForChanges:true` | Pinned Phase 6; already wired in `CompositionRoot.fs` |
| `Microsoft.Extensions.Hosting` | .NET 10 BCL | `BackgroundService`, `PeriodicTimer` | BCL; used by `DecisionLogWriter` and `HardCaseDatasetWriter` |
| `FSharp.SystemTextJson` | 1.4.36 | `JsonFSharpConverter` for reading `hard-cases.jsonl` | Pinned; same opts as `HardCaseDatasetWriter` |
| `Serilog` | 4.3.1 | Structured logging | Pinned; all adapters use it |

### Supporting (already in project)

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `System.Text.Json` | BCL | JSONL line parsing | Reading `hard-cases.jsonl` in `DatasetMerger` |
| `System.Security.Cryptography.SHA256` | BCL | `computeModelVersion` | After successful write, same as `ModelBootstrapper` |

**No new `dotnet add package` commands needed for Phase 8.**

---

## Architecture Patterns

### Recommended File Layout

```
src/SmartRouter.Cli/Adapters/
├── DatasetMerger.fs         # pure function: read JSONL + merge 70/30 + stratify → TrainSample[]
├── Retrainer.fs             # pure: TrainSample[] → fit LR → save to tmp → File.Move
├── Validator.fs             # pure: held-out TrainSample[] → accuracy/fallback_rate → pass/fail
└── RetrainingService.fs     # BackgroundService: PeriodicTimer + count-trigger + SemaphoreSlim
```

`RetrainingOptions` (CLIMutable options record) bound from `appsettings.json "Retraining"` section goes at the top of `RetrainingService.fs` or in a shared `RetrainingOptions.fs`.

New Core file: `src/SmartRouter.Core/RetrainingPorts.fs` already exists (Phase 7). Phase 8 adds NO new Core types. All new work is in Cli/Adapters.

### F# Compile Order in `SmartRouter.Cli.fsproj`

Insert BEFORE `CompositionRoot.fs` (which wires them into DI), AFTER `HardCaseDatasetWriter.fs`:

```xml
<Compile Include="Adapters/DatasetMerger.fs" />
<Compile Include="Adapters/Retrainer.fs" />
<Compile Include="Adapters/Validator.fs" />
<Compile Include="Adapters/RetrainingService.fs" />
```

`RetrainingService.fs` depends on the other three, so it goes last among them.

---

### Pattern 1: ML.NET LR Training in F# (the correct call-site idiom)

The project already has the complete working idiom in `ModelBootstrapper.ensureDummyModel`. Reproduce and extend it:

```fsharp
// Source: ModelBootstrapper.fs (verified working in project)
// Phase 8 extends this exact pattern.

[<CLIMutable>]
type TrainSample =
    { [<VectorType(1024)>]
      Features : float32[]   // 1024-dim from BgeM3Embedder (EMBED-01)
      Label    : bool }      // true = Route122B (label=1), false = Route35B (label=0)

let retrain (samples: TrainSample[]) (modelPath: string) : unit =
    let mlContext = MLContext(seed = Nullable<int>(42))
    let dataView  = mlContext.Data.LoadFromEnumerable(samples)
    let pipeline  =
        mlContext.BinaryClassification.Trainers.LbfgsLogisticRegression(
            labelColumnName   = "Label",
            featureColumnName = "Features",
            l2Regularization  = 0.1f)
    let model = pipeline.Fit(dataView)

    let tmp = modelPath + ".tmp"
    mlContext.Model.Save(model, dataView.Schema, tmp)
    // Atomic replace: write to .tmp then rename overwrites atomically on Unix/APFS.
    // On Unix, File.Move(src, dst, overwrite:true) calls rename(2) — single syscall, atomic.
    File.Move(tmp, modelPath, overwrite = true)
```

Key points confirmed from codebase:
- `RouteInput` / `TrainSample` must be `[<CLIMutable>]` — ML.NET uses reflection-based property set; F# records without `[<CLIMutable>]` lack the public parameterless constructor ML.NET needs.
- `[<VectorType(1024)>]` on `Features` is required — already proven in `MlNetClassifier.fs` `RouteInput`.
- `Label: bool` (not `int`) for `LbfgsLogisticRegression` binary classification — the trainer signature is `LbfgsLogisticRegressionBinaryTrainer` which expects boolean labels. Convert `HardCaseEntry.Label` (int 0/1) to `bool` (`entry.Label = 1`) at merge time.
- `mlContext.Data.LoadFromEnumerable(samples)` assumes the IEnumerable is thread-safe (ML.NET docs note). Pass an `Array` not a lazy sequence to avoid races.
- `mlContext.Model.Save(model, dataView.Schema, path)` — third arg is schema (from the training `IDataView`, not from `PredictionEnginePool`'s loaded model).

### Pattern 2: RetrainingService BackgroundService

Mirror `HardCaseDatasetWriter.ExecuteAsync` shape exactly. Key differences: PeriodicTimer instead of Channel, SemaphoreSlim(1) guard, count-based secondary trigger.

```fsharp
// Source: design-two-loop-router.md + BackgroundService pattern from HardCaseDatasetWriter
type RetrainingService(options: RetrainingOptions, ...) =
    inherit BackgroundService()

    let semaphore = new SemaphoreSlim(1, 1)   // NOT System.Threading.Mutex (see Pitfall 6)

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            use timer = new PeriodicTimer(TimeSpan.FromHours(float options.IntervalHours))
            let mutable more = true
            while more do
                try
                    // WaitForNextTickAsync returns false when timer is disposed (graceful shutdown)
                    let! ticked = timer.WaitForNextTickAsync(stoppingToken)
                    if ticked then
                        do! runRetrain ()   // see try/with isolation below
                with
                | :? OperationCanceledException -> more <- false   // stoppingToken cancelled
                | :? ChannelClosedException     -> more <- false   // mirror HardCaseDatasetWriter
                | ex ->
                    Log.Error(ex, "RetrainingService: outer loop crashed; will not retry")
                    more <- false
        }
```

`runRetrain` MUST be wrapped in its own `try/with` (RETRAIN-06 isolation):

```fsharp
    let runRetrain () : Task<unit> =
        task {
            let acquired = semaphore.Wait(0)  // non-blocking tryAcquire
            if not acquired then
                Log.Warning("RetrainingService: retrain already running; skipping tick")
            else
                try
                    try
                        // ... merge, fit, validate, write ...
                        ()
                    with ex ->
                        Log.Error(ex, "RetrainingService: retrain pipeline threw; model unchanged")
                finally
                    semaphore.Release() |> ignore
        }
```

### Pattern 3: Count-Based Trigger (RETRAIN-02)

Do NOT use `FileSystemWatcher` on `hard-cases.jsonl` — `HardCaseDatasetWriter` holds it open with `FileShare.None` while appending (confirmed in `HardCaseDatasetWriter.fs` line 113). A `FileSystemWatcher`'s `Changed` event on an exclusively-locked file is unreliable on macOS.

Use a **cached line-count check** inside the PeriodicTimer loop instead:

```fsharp
let countLines (path: string) : int =
    if not (File.Exists(path)) then 0
    else
        // Count newline bytes without loading whole file into memory.
        // Hard-cases.jsonl is one JSON object per line; count \n bytes = line count.
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        let buf = Array.zeroCreate<byte> 65536
        let mutable count = 0
        let mutable read = 1
        while read > 0 do
            read <- stream.Read(buf, 0, buf.Length)
            for i in 0 .. read - 1 do
                if buf.[i] = byte '\n' then count <- count + 1
        count
```

`FileShare.ReadWrite` allows reading while `HardCaseDatasetWriter` holds the write lock. This is safe for counting purposes.

Check trigger inside `ExecuteAsync`:

```fsharp
let hardCaseCount = countLines options.HardCasePath
let shouldRetrain = hardCaseCount >= options.CountThreshold
if shouldRetrain then
    do! runRetrain ()
```

Combined with the timer tick: the timer fires every hour; each tick also checks count. This satisfies "whichever first" because the hourly tick is also a count-check opportunity. For a sub-hour trigger (count hits 500 before the hour), a separate `Task.Delay` polling loop at a shorter interval (e.g., 5 minutes) can be run in parallel inside `ExecuteAsync` using `Task.WhenAny`. See "Plan breakdown" section for the two-task racing design.

### Pattern 4: 70/30 DatasetMerger with Class Stratification (RETRAIN-01)

```fsharp
// Source: narrow-label-wide-train.md design + F# random sampling
let merge
    (oldSamples  : TrainSample[])
    (newSamples  : TrainSample[])
    (rng         : Random)        // caller controls seed for reproducibility
    : TrainSample[] =

    // Step 1: compute target counts
    let totalTarget   = oldSamples.Length + newSamples.Length  // or a fixed cap
    let oldTarget     = int (float totalTarget * 0.7)
    let newTarget     = int (float totalTarget * 0.3)

    // Step 2: sample with replacement if population < target
    let sampleN (arr: TrainSample[]) (n: int) : TrainSample[] =
        if arr.Length = 0 then [||]
        else Array.init n (fun _ -> arr.[rng.Next(arr.Length)])

    let oldPart = sampleN oldSamples (min oldTarget oldSamples.Length)
    let newPart = sampleN newSamples (min newTarget newSamples.Length)
    let merged  = Array.append oldPart newPart

    // Step 3: verify class balance — RETRAIN-01 requires each class ≥ 30%
    let total  = merged.Length
    let class1 = merged |> Array.filter (fun s -> s.Label) |> Array.length
    let class0 = total - class1
    let ratio0 = float class0 / float total
    let ratio1 = float class1 / float total

    if ratio0 < 0.30 || ratio1 < 0.30 then
        // Class imbalance: re-sample minority class with replacement to hit 30%
        rebalance merged rng  // see Pitfall 9 for rebalance impl
    else
        merged |> Array.sortBy (fun _ -> rng.Next())  // shuffle
```

**"Old" training set storage decision** (see "Decisions to lock" section): the simplest design is `datasets/training-set.jsonl` in the same schema as `hard-cases.jsonl`. `DatasetMerger` reads both files. On first retrain, if `training-set.jsonl` does not exist, fall back to using the dummy model's synthetic 200 samples (re-generate them via the same seed-42 logic in `ModelBootstrapper`) as the "old" set.

The "old" set is updated after each successful retrain: the merged training set (post-validation) is written as the new `datasets/training-set.jsonl` so the next retrain cycle has continuity.

### Pattern 5: Atomic Write (RETRAIN-04)

On macOS APFS (and all Unix), `File.Move(src, dst, overwrite: true)` uses the `rename(2)` syscall, which is atomic at the VFS layer. Confirmed by dotnet/runtime PR #47118 which shows the Unix implementation uses rename. `PredictionEnginePool`'s `FileSystemWatcher` detects the `Created`/`Changed`/`Renamed` events on the target path and triggers its retry loop (PR #5351: polls with `FileMode.Open, FileAccess.Read, FileShare.Read` every 50ms for up to 5 seconds).

Correct sequence (matches `ModelBootstrapper.ensureDummyModel` exactly):

```fsharp
let atomicWrite (modelPath: string) (tmp: string) : unit =
    // tmp was written by mlContext.Model.Save(model, schema, tmp)
    // File.Move with overwrite:true on Unix = rename(2) — single atomic kernel call.
    // PredictionEnginePool's watcher will see the change and reload within ~50–200ms.
    // Do NOT call File.Delete(modelPath) first — that creates a window where the
    // file is missing and PredictionEnginePool may attempt a reload of nothing.
    File.Move(tmp, modelPath, overwrite = true)
```

Do NOT use `File.Replace(replacementFileName, destinationFileName, destinationBackupFileName)` — it requires an explicit backup path and is unnecessary on Unix where `rename` is already atomic.

### Pattern 6: Validation Gate (RETRAIN-03)

```fsharp
type ValidationResult =
    | Accepted of accuracy: float * fallbackRate: float
    | Rejected of reason: string

let validate
    (mlContext       : MLContext)
    (newModel        : ITransformer)
    (heldOut         : TrainSample[])
    (baselineAcc     : float)
    (baselineFbRate  : float)
    : ValidationResult =

    let heldOutView  = mlContext.Data.LoadFromEnumerable(heldOut)
    let predictions  = newModel.Transform(heldOutView)
    let metrics      =
        mlContext.BinaryClassification.Evaluate(
            data             = predictions,
            labelColumnName  = "Label",
            scoreColumnName  = "Score")

    // accuracy = BinaryClassificationMetrics.Accuracy (proportion correctly classified)
    let newAcc    = metrics.Accuracy
    // fallback_rate proxy: fraction predicted Route35B (label=false) when ground truth is Route122B (label=true)
    // = false-negative rate on the hard-case dataset (which is biased toward 122B)
    // Simpler: use (1 - Accuracy) as a conservative upper bound.
    let newFbRate = 1.0 - newAcc

    if newAcc < baselineAcc then
        Rejected (sprintf "accuracy regression: %.4f < baseline %.4f" newAcc baselineAcc)
    elif newFbRate > baselineFbRate then
        Rejected (sprintf "fallback_rate increase: %.4f > baseline %.4f" newFbRate baselineFbRate)
    else
        Accepted (newAcc, newFbRate)
```

**Baseline computation**: load the current `models/router.zip` via `mlContext.Model.Load`, call `Transform` on the same held-out set, evaluate. This means baseline is always computed fresh against the held-out set — no stale baseline cached in memory across restarts.

**Held-out split**: take 20% of the merged dataset (before split). Use `mlContext.Data.TrainTestSplit(dataView, testFraction = 0.2, seed = 42)`. Seed = 42 makes held-out set reproducible across retrains on the same merged data. Confidence level: HIGH — the API exists in `MLContext.Data.TrainTestSplit`.

**Rejection log**: append a JSONL line to `logs/retraining-rejections.jsonl` (not a new BackgroundService — write synchronously inside `runRetrain` since this is already off the hot path).

### Pattern 7: F# try/with isolation (RETRAIN-06)

The F# `task {}` computation expression propagates exceptions identically to C# `async Task`. The correct isolation pattern for `BackgroundService`:

```fsharp
// CORRECT: inner try/with catches all exceptions from the retrain pipeline.
// Outer loop's OperationCanceledException handler only catches the cancellation.
let runRetrainIsolated (log: ILogger) () : Task<unit> =
    task {
        try
            // all retrain work here: read JSONL, merge, fit, validate, write
            ()
        with
        | :? OperationCanceledException ->
            // Let cancellation propagate — do NOT swallow it here.
            // It will be caught by the outer ExecuteAsync loop.
            reraise ()
        | ex ->
            // Log and swallow: retrain failure must not propagate to the outer loop.
            Log.Error(ex, "RetrainingService: retrain failed; will retry next tick")
            // Return unit — outer loop continues normally.
            ()
    }
```

**Critical F# idiom**: `reraise ()` in an `with` handler re-throws with the original stack trace preserved. Using `raise ex` instead creates a new stack frame. For `OperationCanceledException`, always `reraise ()`.

**Loop B never calls Task.WaitAll / .GetAwaiter().GetResult() on inner tasks** — that would deadlock in a `task {}` context. All awaits use `do! ...` or `let! ...`.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Thread-safe prediction serving | Manual lock around `PredictionEngine.Predict` | `PredictionEnginePool` (already wired) | ObjectPool pattern; ML.NET designed specifically for ASP.NET Core concurrent serving |
| Atomic model file replace | `File.Delete` + `File.Copy` two-step | `File.Move(tmp, dst, overwrite:true)` | Atomic on Unix via `rename(2)`; delete+copy has a window where file is missing |
| ML training pipeline | Custom SGD / gradient descent | `mlContext.BinaryClassification.Trainers.LbfgsLogisticRegression` | Proven convergence, L2 regularization, already working in `ModelBootstrapper` |
| Train/test split | Manual index slicing | `mlContext.Data.TrainTestSplit(dataView, testFraction, seed)` | Stratified, seeded, ML.NET-native |
| Binary classification metrics | Manually computing TP/FP/FN | `mlContext.BinaryClassification.Evaluate(...)` returns `BinaryClassificationMetrics` | `Accuracy`, `AreaUnderRocCurve`, `PositivePrecision`, `NegativeRecall` all available |
| Hot model reload | Manual FileSystemWatcher + ITransformer swap | `PredictionEnginePool watchForChanges:true` (already wired) | Already handles retry-on-lock-file (PR #5351), reference counting on in-flight engines |
| Schema versioning on model file | Custom header in zip | `IDataView.Schema` saved by `mlContext.Model.Save` | Schema is embedded in the zip; ML.NET validates on load |

**Key insight:** The entire ML.NET training + validation + save pipeline was already demonstrated in `ModelBootstrapper.ensureDummyModel`. Phase 8 wraps it with: (a) real data instead of random, (b) a validation gate before the write, and (c) a BackgroundService trigger. The core ML code adds roughly 40–60 lines.

---

## Common Pitfalls

### Pitfall 1: System.Threading.Mutex Cannot Cross await Points

**What goes wrong:** `System.Threading.Mutex` has thread affinity — it must be released by the thread that acquired it. In `task {}` computation expressions, execution may resume on a different thread pool thread after every `do!` or `let!`. Calling `mutex.ReleaseMutex()` on a different thread than `mutex.WaitOne()` throws `ApplicationException`.

**Why it happens:** The RETRAIN-05 requirement says "Mutex" but the semantics needed are "single-writer at a time", not "OS-level named mutex". The word "Mutex" in the spec describes the problem domain, not the implementation class.

**How to avoid:** Use `SemaphoreSlim(1, 1)` everywhere. `SemaphoreSlim` has no thread affinity — `Release()` can be called from any thread. Pattern: `semaphore.Wait(0)` (non-blocking tryAcquire, returns bool) in the BackgroundService loop; if false, log-and-skip. The `try/finally semaphore.Release()` pattern works correctly across await points.

**Warning signs:** `ApplicationException: Object synchronization method was called from an unsynchronized block of code.`

### Pitfall 2: PredictionEnginePool File Lock During Retrain Write

**What goes wrong:** The `FileSystemWatcher` fires when the `.tmp` file is being written, then again when `File.Move` renames it. Between these two events, the pool may attempt to load the `.tmp` file (transient wrong path). OR: writing to `.tmp` while the pool holds a lock on `models/router.zip`.

**Why it happens:** `PredictionEnginePool` watches the model path registered at startup (`models/router.zip`). It responds to `Changed`, `Created`, and `Renamed` events on that specific path. Writing to `models/router.zip.tmp` does NOT trigger a reload — only the final `File.Move` to `router.zip` does.

**How to avoid:** Use the `.tmp` + `File.Move(overwrite:true)` pattern (already in `ModelBootstrapper`). The pool will see only the rename event and begin its retry-on-lock-file loop (PR #5351: 50ms × 100 retries = 5 seconds). The rename itself is atomic, so there is never a window where `router.zip` is missing. In-flight prediction engines that were checked out before the rename complete on the old model; new checkouts after the watcher reloads use the new model.

**Warning signs:** Retrain writes succeed but the pool doesn't pick up the new model within 5 seconds; `IOException` with "file in use" from the pool's reload thread.

### Pitfall 3: HardCaseDatasetWriter FileShare Conflict

**What goes wrong:** `DatasetMerger` reads `datasets/hard-cases.jsonl` while `HardCaseDatasetWriter` holds the file open with `FileShare.None` (confirmed in `HardCaseDatasetWriter.fs` line 113-116). A normal `File.ReadAllLines` or `File.OpenRead` call will throw `IOException: The process cannot access the file`.

**Why it happens:** `HardCaseDatasetWriter` opens the file with `FileShare.None` to enforce single-writer semantics. This is correct behavior; it's `DatasetMerger` that must adapt.

**How to avoid:** Open with `FileShare.ReadWrite`:
```fsharp
use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
use reader = new StreamReader(stream, Encoding.UTF8)
```
This allows concurrent reading while the writer holds the file. The JSONL format guarantees that each line is a complete, self-contained JSON object (enforced by `sw.Flush()` per-line in `HardCaseDatasetWriter`), so a partial-line read is not possible as long as we read complete lines.

**Warning signs:** `IOException` with "The process cannot access the file because it is being used by another process" during retrain on a system with active teacher labeling.

### Pitfall 4: PeriodicTimer Drift on macOS Sleep

**What goes wrong:** When the Mac suspends (lid close), `PeriodicTimer.WaitForNextTickAsync` does NOT fire on schedule. The OS timer is paused. When the Mac wakes, the timer fires immediately (one coalesced tick, NOT multiple missed ticks). The 1-hour timer may effectively become "1 hour of awake time."

**Why it happens:** `PeriodicTimer` uses system monotonic clock timers which pause during OS suspend. This is by design — the count-based trigger (≥500 entries) covers the "woke up with lots of data" scenario. The timer is the background sweeper, not the primary trigger.

**How to avoid:** Treat this as acceptable behavior. The count-based trigger handles the wake-up scenario. Add an explicit "on wake from sleep" check if needed in future phases. For now, document that "1 hour" means "1 hour of host uptime."

**Warning signs:** Retraining never happens even after 12+ hours of calendar time; investigation reveals the host sleeps frequently (laptop use case).

### Pitfall 5: Embedding Step Inside Retraining (Cost: O(N × 50ms))

**What goes wrong:** `hard-cases.jsonl` stores `prompt_text` (confirmed in `HardCaseEntry` record). To feed the LR classifier, each sample needs a 1024-dim embedding. At N=500 entries and ~20–30ms per embedding (bge-m3 int8 warm path per Phase 6 EMBED-03 target), a single retrain requires 10–15 seconds of embedding work on the CPU, all blocking the `RetrainingService` loop.

**Why it happens:** The training schema requires `float32[1024]` features. `hard-cases.jsonl` stores text, not pre-computed embeddings. The embedder must be called for every training sample on every retrain.

**How to avoid:**
- Accept the cost at N=500. 500 × 25ms = 12.5 seconds. This happens hourly at most and runs in the background. Loop A is unaffected.
- For robustness: run embedding with `stoppingToken` passed to `EmbedAsync` so the embedding batch is cancellable on host shutdown.
- Future optimization (not Phase 8): cache embeddings alongside `hard-cases.jsonl` in a `datasets/embeddings-cache.bin` file. Out of scope for Phase 8.
- Embed ALL samples needed (old training set + new hard cases) before splitting into train/held-out. Avoids double-embedding held-out samples.

**Warning signs:** `RetrainingService` loop takes >60 seconds per retrain; host CPU pegged during retrain window; memory pressure from holding 500+ float32[1024] arrays simultaneously (500 × 1024 × 4 bytes ≈ 2MB — negligible).

### Pitfall 6: Concurrent Retrain Triggers Both Acquire SemaphoreSlim

**What goes wrong:** Both the PeriodicTimer tick and the count-trigger check fire in the same millisecond window. Both tasks call `semaphore.Wait(0)` — one succeeds, one is skipped. This is correct behavior but the test (RETRAIN-05 verification) must be designed around it.

**How to avoid:** Use `semaphore.Wait(0)` (non-blocking). The second trigger logs a warning and returns without error. The test asserts: (a) exactly one retrain runs to completion, (b) the other trigger produces a "skipping" log line, (c) the final model file is valid.

**Warning signs:** Two concurrent retrains both succeed but write to `.tmp` simultaneously — possible if a blocking `semaphore.Wait()` is used instead of `Wait(0)` and the first retrain is very fast.

### Pitfall 7: Catastrophic Forgetting (All-New-Data Training)

**What goes wrong:** Retraining only on the 500 new hard cases (which are heavily biased toward `Route122B` label since they are fallback cases) causes the classifier to learn "send everything to 122B." The existing correct 35B decisions are forgotten.

**Why it happens:** `hard-cases.jsonl` contains only failure cases (`fallback_used=true`), which by definition had `Route122B` as the correct label. Training on this alone creates severe class imbalance.

**How to avoid:** The 70/30 merge is precisely designed to prevent this (per `narrow-label-wide-train.md`). The "old" training set (70%) provides balanced representation of both classes. The class-balance check after merge (`≥30% each class`) catches edge cases. If the check fails, apply oversampling of the minority class (with replacement) to reach the threshold.

**Warning signs:** After retrain, >90% of requests route to 122B; fallback_rate on validation set INCREASES; validator correctly rejects the model.

### Pitfall 8: "Old" Training Set Bootstrap (First Retrain)

**What goes wrong:** On first retrain, `datasets/training-set.jsonl` does not exist. `DatasetMerger` has no "old" set to mix with.

**How to avoid:** Fall back to regenerating the same 200 synthetic samples that `ModelBootstrapper.ensureDummyModel` uses (seed=42, balanced labels). This is the bootstrap case. After the first successful retrain, write the merged training set to `datasets/training-set.jsonl` so subsequent retrains have a real history. Log a warning: "No existing training set found; using bootstrap dummy data for 70% share."

**Warning signs:** First retrain fails with `FileNotFoundException` on `training-set.jsonl`; OR first retrain silently uses only 30% of the intended data because the 70% falls back to an empty array.

### Pitfall 9: Class Imbalance After Merge (Extreme Case)

**What goes wrong:** New data has 499 `Route122B` and 1 `Route35B`. After 70/30 merge with old data, the combined dataset still has <30% `Route35B` samples if the old data is also imbalanced.

**How to avoid:**
```fsharp
let rebalance (samples: TrainSample[]) (rng: Random) : TrainSample[] =
    let class1 = samples |> Array.filter (fun s -> s.Label)
    let class0 = samples |> Array.filter (fun s -> not s.Label)
    let target  = max class0.Length class1.Length
    // Oversample the minority class with replacement
    let oversample (arr: TrainSample[]) (n: int) =
        Array.append arr (Array.init (n - arr.Length) (fun _ -> arr.[rng.Next(arr.Length)]))
    let balanced0 = if class0.Length < target then oversample class0 target else class0
    let balanced1 = if class1.Length < target then oversample class1 target else class1
    Array.append balanced0 balanced1 |> Array.sortBy (fun _ -> rng.Next())
```
Log a warning when rebalancing is triggered: "Class imbalance detected after merge (class0=X, class1=Y); oversampling minority class."

### Pitfall 10: Phase 9 Canary File Layout Constraint

**What goes wrong:** Phase 8 writes `models/router.zip` and might delete all other `*.zip` files in `models/`. Phase 9 requires `models/router-canary.zip` to coexist with `models/router.zip` so two model pools can run simultaneously.

**Why it happens:** Phase 8 writes to a hardcoded `router.zip` path; a naive cleanup step might delete other zips.

**How to avoid:**
- Phase 8 ONLY writes to `models/router.zip` (the baseline path). Never delete other `models/*.zip` files.
- Phase 9 registers a second `AddPredictionEnginePool<RouteInput, RoutePrediction>().FromFile(modelName = "router-canary", filePath = "models/router-canary.zip", watchForChanges = true)`. The canary pool is a separate named registration in the same `PredictionEnginePool<RouteInput, RoutePrediction>` singleton.
- Phase 8 must NOT change the `AddPredictionEnginePool` registration in `CompositionRoot.fs` to single-model-only semantics. The existing registration (`modelName = "router"`) is correct and extensible.
- File layout contract: `models/router.zip` = active baseline (Phase 8 writes here); `models/router-canary.zip` = canary (Phase 9 writes here, Phase 8 never touches).
- Keep `models/router.zip.prev` as the previous model for Phase 9's rollback scenario: after Phase 8 writes a new `router.zip`, copy the old one to `router.zip.prev` before the rename. Phase 9 can load `router.zip.prev` as the fallback if rollback is needed.

---

## Code Examples

### Complete ML.NET Retrain Pipeline (F#)

```fsharp
// Source: ModelBootstrapper.fs (existing, verified) + ML.NET docs
// This is the exact pattern Phase 8 extends.

open Microsoft.ML
open Microsoft.ML.Data
open System.IO

[<CLIMutable>]
type TrainSample =
    { [<VectorType(1024)>]
      Features : float32[]
      Label    : bool }   // true = Route122B

let retrainAndSave (samples: TrainSample[]) (modelPath: string) : unit =
    let mlContext = MLContext(seed = Nullable<int>(42))
    let dataView  = mlContext.Data.LoadFromEnumerable<TrainSample>(samples)

    // TrainTestSplit: 80% train, 20% held-out. Seed for reproducibility.
    let split = mlContext.Data.TrainTestSplit(dataView, testFraction = 0.2, seed = 42)

    let pipeline =
        mlContext.BinaryClassification.Trainers.LbfgsLogisticRegression(
            labelColumnName   = "Label",
            featureColumnName = "Features",
            l2Regularization  = 0.1f,
            maximumNumberOfIterations = 100)

    let model   = pipeline.Fit(split.TrainSet)
    let metrics = mlContext.BinaryClassification.Evaluate(
                      model.Transform(split.TestSet),
                      labelColumnName = "Label",
                      scoreColumnName = "Score")

    // metrics.Accuracy, metrics.AreaUnderRocCurve available
    let tmp = modelPath + ".tmp"
    mlContext.Model.Save(model, split.TrainSet.Schema, tmp)
    File.Move(tmp, modelPath, overwrite = true)
```

### Reading hard-cases.jsonl with FileShare.ReadWrite (F#)

```fsharp
// Source: HardCaseDatasetWriter.fs pattern (FileShare) + System.Text.Json
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open SmartRouter.Core.RetrainingPorts

let readHardCases (path: string) (jsonOpts: JsonSerializerOptions) : HardCaseEntry[] =
    if not (File.Exists(path)) then [||]
    else
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        use reader = new StreamReader(stream, Encoding.UTF8)
        let mutable entries = ResizeArray<HardCaseEntry>()
        let mutable line = reader.ReadLine()
        while not (isNull line) do
            if not (System.String.IsNullOrWhiteSpace(line)) then
                try
                    let entry = JsonSerializer.Deserialize<HardCaseEntry>(line, jsonOpts)
                    entries.Add(entry)
                with ex ->
                    Log.Warning(ex, "DatasetMerger: skipping malformed line in {Path}", path)
            line <- reader.ReadLine()
        entries.ToArray()
```

`jsonOpts` must include `JsonFSharpConverter()` and `SnakeCaseLower` naming policy — identical to `HardCaseDatasetWriter.jsonOpts`.

### SemaphoreSlim(1) Lock Pattern for BackgroundService (F#)

```fsharp
// Source: SemaphoreSlim docs + Pitfall 1 (no Mutex across await points)
let semaphore = new SemaphoreSlim(1, 1)

// Non-blocking try-acquire pattern (RETRAIN-05: concurrent triggers skip, not queue)
let tryRunRetrain () : Task<unit> =
    task {
        if semaphore.Wait(0) then   // Wait(0) = non-blocking; returns false if busy
            try
                try
                    do! runRetrainPipeline ()
                with
                | :? OperationCanceledException -> reraise ()
                | ex ->
                    Log.Error(ex, "RetrainingService: pipeline threw; model unchanged")
            finally
                semaphore.Release() |> ignore
        else
            Log.Warning("RetrainingService: retrain already in progress; skipping trigger")
    }
```

### PeriodicTimer + Count Check Combined Trigger (F#)

```fsharp
// Source: design-two-loop-router.md pattern + PeriodicTimer .NET 6+ API
override _.ExecuteAsync(ct: CancellationToken) =
    task {
        use periodicTimer = new PeriodicTimer(TimeSpan.FromHours(1.0))
        use countCheckTimer = new PeriodicTimer(TimeSpan.FromMinutes(5.0))

        // Race two timers: periodic 1h tick OR count-threshold check every 5 min
        // Both feed the same runRetrain gate.
        let periodicTask () = task {
            while! periodicTimer.WaitForNextTickAsync(ct) do
                do! tryRunRetrain ()
        }
        let countTask () = task {
            while! countCheckTimer.WaitForNextTickAsync(ct) do
                let count = countLines options.HardCasePath
                if count >= options.CountThreshold then
                    do! tryRunRetrain ()
        }

        try
            do! Task.WhenAll(periodicTask (), countTask ())
        with
        | :? OperationCanceledException -> ()
    }
```

**Note**: `while!` is F# 6+ syntax sugar for `while (let! b = expr in b)`. In F# 10, `while!` is available. If using older tooling, expand to `let mutable running = true; while running do let! tick = ...; if not tick then running <- false else ...`.

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Async.Sleep recursive loop for scheduling | `BackgroundService` + `PeriodicTimer` | .NET 6 (2021) | Crash-safe, host-lifecycle-aware, cancellation-aware |
| `System.Threading.Mutex` for single-writer | `SemaphoreSlim(1,1)` with `Wait(0)` | Best practice since async/await (2012) | No thread affinity; works across await points |
| `File.Delete + File.Copy` for atomic replace | `File.Move(src, dst, overwrite:true)` | .NET 5 PR #33054 + PR #47118 | Single `rename(2)` syscall on Unix; atomic |
| Manual PredictionEngine per-request | `PredictionEnginePool` | ML.NET 1.3 (2019) | Thread-safe, pooled, auto-reloads on file change |
| Fixed 50ms retry in PredictionEnginePool reload | Retry loop 50ms × 100 (5 second budget) | ML.NET PR #5351 | Tolerates slower file systems and temp+rename patterns |

**Deprecated/outdated:**
- `System.Threading.Mutex` in async contexts: use `SemaphoreSlim(1,1)`.
- `File.AppendAllText` for concurrent writes: documented concurrency bug in dotnet/runtime#70247 (use `Channel + BackgroundService` — already done in project).
- `MLContext` reuse across retrain cycles: each `MLContext` should be created fresh per retrain to avoid state accumulation (confirmed by ModelBootstrapper pattern of creating `MLContext(seed = Nullable<int>(42))` per call).

---

## Open Questions

1. **`BinaryClassificationMetrics.fallback_rate` mapping**
   - What we know: `BinaryClassificationMetrics` from `mlContext.BinaryClassification.Evaluate` provides `Accuracy`, `AreaUnderRocCurve`, `PositivePrecision`, `NegativeRecall`, `NegativePrecision`, `PositiveRecall`. There is no field named `fallback_rate`.
   - What's unclear: RETRAIN-03 says "fallback_rate on validation set" — this maps to the rate at which the new model would have predicted `Route35B` for prompts that actually needed `Route122B`. That is `1 - PositiveRecall` (false negative rate on the positive class, Route122B).
   - Recommendation: In the validator, define `fallback_rate = 1.0 - metrics.PositiveRecall`. This measures "how often would the new model fail to route 122B-bound prompts correctly." The planner should confirm this definition with the operator or lock it in PLAN frontmatter.

2. **"Old" training set initial content for first retrain**
   - What we know: `datasets/training-set.jsonl` will not exist on first retrain after Phase 8 is deployed. The bootstrap dummy model was trained on 200 random synthetic samples.
   - What's unclear: Should the bootstrap training data be regenerated (ephemeral) or should `training-set.jsonl` be seeded with the same 200 synthetic samples?
   - Recommendation: On first retrain, if `training-set.jsonl` is absent, regenerate the 200 seed-42 synthetic samples as `TrainSample[]` in memory (no disk write needed for the bootstrap). After retrain succeeds, write the full merged set to `datasets/training-set.jsonl` for future cycles. Log: "First retrain: using synthetic bootstrap data for 70% share."

3. **`while!` availability in F# 10 targeting .NET 10**
   - What we know: `while!` was introduced in F# 6. The project uses .NET 10 and presumably current F# tooling.
   - What's unclear: Exact F# toolchain version in the project's global.json or build config.
   - Recommendation: Use `while!` — if it compiles, it compiles. If the CI rejects it, expand to the manual `mutable running` pattern. Check `global.json` at planning time.

4. **model_version update after retrain**
   - What we know: `RoutingAlgorithmRegistration.ModelVersion` is computed at DI startup time (`computeModelVersion mlOpts.ModelPath` in `CompositionRoot.fs`). After hot-reload, the in-memory `ModelVersion` is stale — the DI singleton was resolved at startup.
   - What's unclear: Does Phase 8 need to update `ModelVersion` in-process, or is it acceptable that `model_version` in `DecisionLog` only reflects the startup-time hash until the next host restart?
   - Recommendation: Phase 8 should expose an `IModelVersionProvider` that `RetrainingService` can update after a successful retrain. `RoutingAlgorithmRegistration` should hold a `mutable ModelVersion` or use an `IOptions<>` approach. RETRAIN-04 verification test asserts the `model_version` flip in DecisionLog — this REQUIRES the in-memory version to update. Lock this design in PLAN 08-02.

---

## Sources

### Primary (HIGH confidence)
- `src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs` — complete ML.NET retrain + atomic write pattern, directly read
- `src/SmartRouter.Cli/Adapters/MlNetClassifier.fs` — `RouteInput`, `RoutePrediction`, `[<VectorType(1024)>]`, `[<CLIMutable>]` confirmed
- `src/SmartRouter.Cli/CompositionRoot.fs` — `AddPredictionEnginePool.FromFile(watchForChanges=true)` wiring, confirmed
- `src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs` — `FileShare.None`, `Channel`, BackgroundService drain pattern
- `src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs` — canonical BackgroundService pattern
- `src/SmartRouter.Core/RetrainingPorts.fs` — `HardCaseEntry` schema, JSONL field names
- `/Users/ohama/projs/smart-router-distillation/documentation/auto-retraining-research.md` — component feasibility, package matrix
- `/Users/ohama/projs/smart-router-distillation/documentation/howto/narrow-label-wide-train.md` — 70/30 merge rationale
- `/Users/ohama/projs/smart-router-distillation/documentation/howto/design-two-loop-router.md` — BackgroundService + PeriodicTimer pattern

### Secondary (MEDIUM confidence)
- [ML.NET PR #5351 — file lock retry fix for watchForChanges](https://github.com/dotnet/machinelearning/pull/5351) — confirmed via WebFetch: 50ms × 100 retry loop; tolerates temp+rename
- [dotnet/runtime PR #47118 — File.Move overwrite on Unix uses rename](https://github.com/dotnet/runtime/pull/47118) — confirmed via WebFetch: uses `rename(2)` on Unix
- [Microsoft.Extensions.ML PredictionEnginePool docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ml.predictionenginepool-2?view=ml-dotnet) — confirmed `watchForChanges` triggers FileSystemWatcher
- [ML.NET LbfgsLogisticRegressionBinaryTrainer docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.ml.trainers.lbfgslogisticregressionbinarytrainer?view=ml-dotnet) — label must be `bool`, features must be `float32` known-size vector
- [PeriodicTimer.WaitForNextTickAsync docs](https://learn.microsoft.com/en-us/dotnet/api/system.threading.periodictimer.waitfornexttickasync?view=net-10.0) — returns `false` when timer disposed (graceful shutdown)

### Tertiary (LOW confidence)
- Mutex + async pitfall: [Mixing Traditional Locks with Async Code](https://medium.com/@tyschenk20/mixing-traditional-locks-with-async-code-in-c-27431f857e01) — well-known pattern, independently verified against `SemaphoreSlim` docs
- PeriodicTimer macOS sleep behavior: NOT directly confirmed by official source. Mark as "likely correct" based on OS timer semantics (monotonic clock pauses on suspend). Verify empirically if operator runs router on a sleeping laptop.

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all packages already in project, no new dependencies
- ML.NET API (LbfgsLogisticRegression, IDataView, Model.Save): HIGH — existing working code in ModelBootstrapper; confirmed against docs
- Atomic file replace (File.Move + rename): HIGH — PR source confirms Unix rename(2) semantics
- PredictionEnginePool hot-reload: HIGH — existing wiring confirmed; PR #5351 confirms file-lock retry
- SemaphoreSlim vs Mutex: HIGH — thread-affinity constraint is documented and well-known
- 70/30 merge with class balance: HIGH — design sourced from project's own distillation docs
- Validation gate metrics mapping: MEDIUM — BinaryClassificationMetrics field confirmed; `fallback_rate` mapping to `1 - PositiveRecall` is a reasonable interpretation but not explicitly stated in requirements
- model_version hot-update after retrain: MEDIUM — problem identified; solution (IModelVersionProvider) is a design recommendation, not confirmed API

**Research date:** 2026-05-08
**Valid until:** 2026-06-08 (ML.NET 5.0 is stable; no fast-moving changes expected in this domain)

---

## Decisions to Lock at Planning Time

These must be explicit in PLAN frontmatter or CONTEXT.md before coding begins:

1. **"Old" training set path:** `datasets/training-set.jsonl` (recommended). Bootstrap behavior when missing: regenerate 200 seed-42 synthetic samples in memory (no file write; log warning).

2. **PeriodicTimer interval:** 1 hour (from ROADMAP). Make configurable via `Retraining.IntervalHours` in `appsettings.json` (default 1). Allows test overrides without code changes.

3. **Count-trigger threshold:** 500 entries (from ROADMAP). Make configurable via `Retraining.CountThreshold` (default 500). Count-check interval: 5 minutes (configurable via `Retraining.CountCheckMinutes`, default 5).

4. **Held-out validation split ratio:** 80% train / 20% held-out. Use `mlContext.Data.TrainTestSplit(testFraction=0.2, seed=42)`. Seed fixed at 42 (same as bootstrap for consistency across the project).

5. **Validation baseline computation:** Load current `models/router.zip` at retrain time, evaluate on held-out, capture as baseline. Never cache baseline across restarts.

6. **`fallback_rate` definition in validator:** `1.0 - metrics.PositiveRecall`. Confirm with operator or lock at planning time.

7. **Rejection log path:** `logs/retraining-rejections.jsonl`. Written synchronously inside `runRetrain` (no BackgroundService needed — volume is low).

8. **Atomic write strategy:** `File.Move(tmp, modelPath, overwrite=true)`. NOT `File.Replace`. Identical to `ModelBootstrapper` pattern.

9. **Lock primitive:** `SemaphoreSlim(1, 1)` with `Wait(0)` (non-blocking try). NOT `System.Threading.Mutex`.

10. **model_version in-process update after retrain:** Phase 8 must expose an `IModelVersionProvider` with a mutable `CurrentVersion` property. `RetrainingService` calls `provider.Update(computeModelVersion modelPath)` after successful write. `CompositionRoot` resolves `RoutingAlgorithmRegistration` to use `IModelVersionProvider.CurrentVersion` instead of the startup-time hash. This is required for RETRAIN-04 test (DecisionLog `model_version` flip).

11. **Phase 9 file layout constraint:** Phase 8 ONLY writes `models/router.zip`. After successful retrain, copy old `router.zip` to `models/router.zip.prev` BEFORE the rename (for Phase 9 rollback). Never delete other `models/*.zip` files.

12. **Training set update after retrain:** After successful retrain, write merged training samples to `datasets/training-set.jsonl` (overwrites; this is the new "old" dataset for the next cycle). Use atomic write: `training-set.jsonl.tmp` + `File.Move`.

---

## Plan Breakdown Recommendation

Phase 8 maps cleanly to 3 plans in 2 waves. The ROADMAP.md 08-01..08-03 outline is correct; this research refines the content.

### Wave 1 (sequential dependency: 08-01 must complete before 08-02 starts)

**Plan 08-01: DatasetMerger + Retrainer + Validator + ModelRegistry** (pure functional adapters, no BackgroundService)

Files owned:
- NEW: `src/SmartRouter.Cli/Adapters/DatasetMerger.fs` — `readHardCases`, `readTrainingSet`, `merge` (70/30 + rebalance), `saveTrainingSet`
- NEW: `src/SmartRouter.Cli/Adapters/Retrainer.fs` — `retrain` (MLContext → Fit → Save)
- NEW: `src/SmartRouter.Cli/Adapters/Validator.fs` — `computeBaseline`, `validate`, `writeRejectionLog`
- MODIFY: `src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs` — add `saveModelAtomically` helper (extract from `ensureDummyModel` into reusable function)
- NEW: `tests/.../RetrainingTests.fs` — RETRAIN-01 unit tests (merger balance, class stratification), Validator rejection test, Retrainer smoke test (trains on 50 samples, produces valid model)

No DI changes, no BackgroundService. All functions are pure (except file I/O). Testable in isolation.

**Plan 08-02: RetrainingService BackgroundService + DI wiring + appsettings + model_version hot-update**

Files owned:
- NEW: `src/SmartRouter.Cli/Adapters/RetrainingService.fs` — `BackgroundService`, `PeriodicTimer`, count-trigger, `SemaphoreSlim`, try/with isolation
- NEW: `src/SmartRouter.Cli/Adapters/ModelVersionProvider.fs` (or inline in `RetrainingService.fs`) — `IModelVersionProvider` interface + `ModelVersionProvider` implementation
- MODIFY: `src/SmartRouter.Cli/CompositionRoot.fs` — register `RetrainingService` (triple-pattern: AddSingleton + AddHostedService), register `IModelVersionProvider`, update `RoutingAlgorithmRegistration` to use `IModelVersionProvider`
- MODIFY: `src/SmartRouter.Cli/appsettings.json` — add `Retraining` section: `{ "IntervalHours": 1, "CountThreshold": 500, "CountCheckMinutes": 5, "HardCasePath": "datasets/hard-cases.jsonl", "TrainingSetPath": "datasets/training-set.jsonl", "ModelPath": "models/router.zip" }`
- MODIFY: `tests/.../RetrainingTests.fs` — add RETRAIN-02 (trigger tests), RETRAIN-05 (concurrent trigger), RETRAIN-06 (force-throw isolation)

DI triple-pattern (confirmed from Phase 5 and 7 patterns):
```fsharp
services.AddSingleton<RetrainingService>(fun sp -> ...)
services.AddSingleton<IRetrainingService>(fun sp -> sp.GetRequiredService<RetrainingService>() :> IRetrainingService)
services.AddHostedService<RetrainingService>(fun sp -> sp.GetRequiredService<RetrainingService>())
```

Wave 1 dependency: 08-02 depends on 08-01 completing (needs `DatasetMerger`, `Retrainer`, `Validator` to be compilable).

### Wave 2 (depends on Wave 1 complete)

**Plan 08-03: End-to-End Retrain Tests (RETRAIN-03, RETRAIN-04)**

Files owned:
- MODIFY: `tests/.../RetrainingTests.fs` — add RETRAIN-03 (validation-gate rejection), RETRAIN-04 (3-request before/retrain/after DecisionLog model_version flip), hot-reload timing test
- MODIFY: `tests/SmartRouter.Tests/RouterTests.fs` — add `RetrainingTests.tests` to `rootTests`
- MODIFY: `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — add `RetrainingTests.fs` compile entry

RETRAIN-04 test design: inject a controlled `IRetrainingService.RunNow()` method. Test sequence:
1. Start host with temp model dir, `watchForChanges=true`.
2. Send 1 request → record `model_version` from DecisionLog → `modelVersion1`.
3. Call `RetrainingService.RunNowAsync()` with synthetic training data (50 balanced samples) → wait for write.
4. Wait for `PredictionEnginePool` to reload (poll `computeModelVersion(modelPath)` until different from `modelVersion1`, max 5 seconds).
5. Send 1 request → record `model_version` from DecisionLog → `modelVersion2`.
6. Assert `modelVersion1 <> modelVersion2`.

RETRAIN-03 test design: provide training data that is entirely one class → validator must reject → `router.zip` unchanged (hash still equals `modelVersion1`).

---

## Phase 8 → Phase 9 Handoff Constraints

Phase 9 (canary) requires the following, which Phase 8 must not break:

1. **`models/router.zip` must never have a missing-file window.** The canary pool (`models/router-canary.zip`) is registered separately. Both pools must always find their respective files. The `File.Move(overwrite:true)` pattern satisfies this — the target is atomically replaced, never deleted.

2. **`models/router.zip.prev` must exist after each successful retrain.** Phase 9's rollback copies `router.zip.prev` back to `router.zip`. Phase 8 must: (a) if `router.zip` exists before the write, copy it to `router.zip.prev`; (b) then write the new `router.zip`.

3. **`IModelVersionProvider` must be resolvable from DI.** Phase 9's canary cohort logic reads `model_version` to bucket requests. The provider must be injectable into Phase 9's `CanaryService`.

4. **`AddPredictionEnginePool` registration must be extensible.** The current single `FromFile(modelName="router", ...)` registration in `CompositionRoot.fs` must remain. Phase 9 adds a second `.FromFile(modelName="router-canary", ...)` call to the same `AddPredictionEnginePool` chain. Phase 8 must not change the chain signature or move the registration to a place where Phase 9 can't add to it.

5. **`datasets/training-set.jsonl` must be a stable contract.** Phase 9 does NOT modify the training set. Only Phase 8's `RetrainingService` writes to it. Phase 9 reads `model_version` and file hashes, not training data.
