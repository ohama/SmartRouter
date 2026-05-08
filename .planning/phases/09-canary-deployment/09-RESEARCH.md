# Phase 9: Canary Deployment — Research

**Researched:** 2026-05-09
**Domain:** Feature flagging (Microsoft.FeatureManagement), dual-model serving (PredictionEnginePool), rolling metrics, admin endpoint
**Confidence:** HIGH

---

## Summary

**What was researched:**
Phase 9 routes a configurable percentage (default 10%) of traffic to a "canary" model candidate. The system uses sticky bucketing by `correlation_id`, logs `model_version` to distinguish cohorts, supports manual promote/rollback via `/canary`, and auto-rolls back when canary's rolling-60s `fallback_rate` exceeds baseline by >10%.

Five domains were investigated: (1) Microsoft.FeatureManagement package selection and TargetingFilter hash mechanics; (2) `ICanaryGate` abstraction to keep Core BCL-only; (3) canary file layout with Phase 8 ownership constraints; (4) dual-model PredictionEnginePool loading; (5) rolling-60s fallback metric computation and auto-rollback trigger.

**Standard approach:**
Skip `PercentageFilter` (stateless random — no sticky bucketing). Use `ContextualTargetingFilter` from `Microsoft.FeatureManagement.AspNetCore` v4.5.0 with `DefaultRolloutPercentage` and a custom `ITargetingContextAccessor` that supplies `correlation_id` as the `UserId`. The TargetingEvaluator computes `SHA-256(userId + "\n" + featureName)` → first 4 bytes → uint32 / uint32.MaxValue × 100 — deterministic and sticky per `correlation_id`.

**Key recommendations:**
- Use `Microsoft.FeatureManagement.AspNetCore` 4.5.0 (not the core package alone; AspNetCore variant adds `WithTargeting` and `ITargetingContextAccessor`).
- Introduce `ICanaryGate : correlationId:string -> Task<bool>` in `SmartRouter.Core` (BCL-only); Cli wraps `IVariantFeatureManager` behind it.
- Canary model file: `models/router-canary.zip` (separate from baseline `router.zip` and Phase 8's `router.zip.prev`). Phase 8 Lock 11 already named this file and prohibited Phase 8 from deleting it.
- Rolling-60s metric: in-memory `ConcurrentQueue<(DateTimeOffset * cohort * bool)>` with trim-by-age, polled every 10s by a `CanaryWatchdog` BackgroundService.
- `fallback_rate` proxy until Phase 10: treat `fallback_used = true` as a fallback signal when available; fall back to response error rate (any 5xx/upstream_error) as the proxy. Phase 10 makes `fallback_used` load-bearing; Phase 9 instruments both signals.

**Primary recommendation:** Implement `ICanaryGate` as a BCL-only Core port backed by a Cli `ContextualTargetingFilter` wrapper. Do NOT use `PercentageFilter` (non-sticky random). Do NOT put `IVariantFeatureManager` in Core.

---

## 1. Microsoft.FeatureManagement — Package Selection and TargetingFilter Mechanics

### 1.1 Package: Use AspNetCore variant

| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.FeatureManagement.AspNetCore` | 4.5.0 | Adds `WithTargeting()`, `ITargetingContextAccessor`, `DefaultHttpTargetingContextAccessor` |
| `Microsoft.FeatureManagement` | 4.5.0 | Core only: `IVariantFeatureManager`, `IFeatureManager`, filters. No targeting helpers. |

**Decision locked:** Use `Microsoft.FeatureManagement.AspNetCore` 4.5.0. The AspNetCore variant includes `ContextualTargetingFilter`, `TargetingContext`, and `WithTargeting<T>()` extension. NuGet page confirms net8.0 target with .NET 10 forward-compatibility (net8.0 TFM is compatible with net10.0 by policy).

**Install:**
```bash
dotnet add src/SmartRouter.Cli/SmartRouter.Cli.fsproj package Microsoft.FeatureManagement.AspNetCore --version 4.5.0
```

### 1.2 Why NOT PercentageFilter

`PercentageFilter` (`Microsoft.Percentage` alias) uses `RandomGenerator.NextDouble() * 100 < settings.Value` — pure random, no user context, no hashing. The same `correlation_id` will get different cohort assignments on different requests. **Sticky bucketing is impossible with PercentageFilter.**

### 1.3 TargetingFilter: hash-based sticky bucketing

`TargetingEvaluator` (confirmed by source code):
- Constructs context identifier: `"{userId}\n{featureName}"` (or `"{userId}\n{featureName}\n{group}"` for group)
- Computes `SHA-256` of UTF-8 bytes
- Takes first 4 bytes as `uint32` via `BitConverter.ToUInt32`
- Computes `percentage = (contextMarker / (double)uint.MaxValue) * 100`
- Checks `percentage >= 0 && percentage < rolloutPercentage` (100% boundary: `percentage >= 0`)

This means: **same `correlation_id` + same feature name → always the same cohort**. Sticky across restarts. Deterministic across hosts.

`DefaultRolloutPercentage: 10` in the targeting audience config → ~10% of `correlation_id` hashes fall below the threshold.

### 1.4 Registration in F# (Minimal API, no MVC)

```fsharp
// CompositionRoot.fs additions
open Microsoft.FeatureManagement
open Microsoft.FeatureManagement.FeatureFilters   // ContextualTargetingFilter

services.AddFeatureManagement()
        .WithTargeting<CorrelationIdTargetingContextAccessor>()
|> ignore

services.AddHttpContextAccessor() |> ignore   // Required by WithTargeting<T>
```

`CorrelationIdTargetingContextAccessor` (Cli, implements `ITargetingContextAccessor`):
```fsharp
// Adapters/CanaryTargetingAccessor.fs
open Microsoft.AspNetCore.Http
open Microsoft.FeatureManagement.FeatureFilters
open SmartRouter.Cli.Adapters.CorrelationMiddleware

type CorrelationIdTargetingContextAccessor(httpContextAccessor: IHttpContextAccessor) =
    interface ITargetingContextAccessor with
        member _.GetContextAsync() =
            let ctx = httpContextAccessor.HttpContext
            let userId =
                if isNull ctx then ""
                else
                    match ctx.Items.TryGetValue(CorrelationIdKey) with
                    | true, (:? string as cid) -> cid
                    | _ -> System.Guid.NewGuid().ToString("N")
            ValueTask<TargetingContext>(TargetingContext(UserId = userId, Groups = [||]))
```

### 1.5 Per-request gate usage

`IVariantFeatureManager.IsEnabledAsync<TargetingContext>(featureName, context)` — BUT this is the AspNetCore-coupled approach. Instead, use the `ICanaryGate` port (see Section 2) to keep Core BCL-only.

### 1.6 Feature flag configuration (appsettings.json)

The new Microsoft schema uses `feature_management.feature_flags` (snake_case):
```json
"feature_management": {
  "feature_flags": [
    {
      "id": "Canary",
      "enabled": true,
      "conditions": {
        "client_filters": [
          {
            "name": "Microsoft.Targeting",
            "parameters": {
              "Audience": {
                "DefaultRolloutPercentage": 10
              }
            }
          }
        ]
      }
    }
  ]
}
```

**Dynamic config update:** `IConfiguration` is reloaded live when `appsettings.json` changes (ASP.NET Core file-reload) — updating `DefaultRolloutPercentage` without restart works. The FeatureManager reads from IConfiguration per-evaluation when registered as scoped (`AddScopedFeatureManagement`). This is required for runtime percentage changes.

**Use `AddScopedFeatureManagement` not `AddFeatureManagement`** because the canary gate is evaluated per-request and must see config changes in real time.

### 1.7 Pitfalls

- `PercentageFilter` is added automatically by `AddFeatureManagement()`. It does NOT provide sticky bucketing. Do not use it.
- The feature name `"Canary"` must not contain colons (`:`) — library restriction.
- `WithTargeting<T>()` requires `IHttpContextAccessor` in the container (call `services.AddHttpContextAccessor()`).
- `AddFeatureManagement` registers services as singletons; `AddScopedFeatureManagement` registers as scoped. For per-request config reload: **use `AddScopedFeatureManagement`**.

---

## 2. F# Integration — ICanaryGate Port (Core BCL-only invariant)

### 2.1 The Problem

`ML.fs` (Core) currently calls classifier synchronously via `runSync`. Phase 9 needs to query the canary gate per-request. `IVariantFeatureManager` is in `Microsoft.FeatureManagement` — a Cli dependency. Putting it in Core violates ARCH-01.

### 2.2 Solution: ICanaryGate port in Core

**Core (`src/SmartRouter.Core/RetrainingPorts.fs` or new `CanaryPorts.fs`):**
```fsharp
// BCL-only — no FeatureManagement reference in Core.
// correlationId → Task<bool>: true = route to canary, false = route to baseline.
type ICanaryGate =
    abstract member IsCanaryAsync : correlationId: string * ct: CancellationToken
        -> Task<bool>
```

**Cli adapter (`Adapters/CanaryGate.fs`):**
```fsharp
open Microsoft.FeatureManagement
open Microsoft.FeatureManagement.FeatureFilters
open SmartRouter.Core.RetrainingPorts   // or CanaryPorts

[<Literal>]
let CanaryFeatureName = "Canary"

type FeatureManagementCanaryGate(featureManager: IVariantFeatureManager) =
    interface ICanaryGate with
        member _.IsCanaryAsync(correlationId, _ct) =
            task {
                let ctx = TargetingContext(UserId = correlationId, Groups = [||])
                let! enabled = featureManager.IsEnabledAsync<TargetingContext>(CanaryFeatureName, ctx)
                return enabled
            }
```

**NullCanaryGate** (for heuristic mode or before any canary model file exists):
```fsharp
type NullCanaryGate() =
    interface ICanaryGate with
        member _.IsCanaryAsync(_, _) = Task.FromResult(false)
```

### 2.3 Where ICanaryGate plugs in

`ML.fs makeApplyML` currently closes over `(embedder, classifier)`. Phase 9 adds `(embedder, baselineClassifier, canaryClassifier, canaryGate)`:

```fsharp
// src/SmartRouter.Core/ML.fs (modified)
let makeApplyML
    (embedder            : IEmbedder)
    (baselineClassifier  : IClassifier)
    (canaryClassifier    : IClassifier)
    (canaryGate          : ICanaryGate)
    : RoutingAlgorithm =
    fun (cfg: RoutingConfig) (req: RouterRequest) ->
        let correlationId = req.CorrelationId   // see Section 2.4

        let isCanary = runSync (fun () -> canaryGate.IsCanaryAsync(correlationId, CancellationToken.None))

        let (classifier, cohortTag) =
            if isCanary then (canaryClassifier, "-canary")
            else (baselineClassifier, "")

        let prompt = req.Messages |> List.map (fun m -> m.Content) |> String.concat " "
        let embedding = runSync (fun () -> embedder.EmbedAsync(prompt, CancellationToken.None))
        let prediction = runSync (fun () -> classifier.PredictAsync(embedding, CancellationToken.None))

        let target = if prediction.Score >= cfg.MlThreshold then Qwen122B else Qwen35B

        { Target     = target
          Priority   = Low
          Reason     = ML
          IsFallback = false }
        // model_version cohort tag is communicated via a separate mechanism — see Section 6
```

### 2.4 RouterRequest needs CorrelationId

`RouterRequest` (Core Domain.fs) currently does not carry `correlation_id`. `ML.fs` can't access `HttpContext`. Options:

**Option A (recommended):** Add `CorrelationId: string` field to `RouterRequest`. `ChatCompletions.fs` already reads `correlationId` from `HttpContext.Items`; it sets `req.CorrelationId` when mapping wire → domain. Core Domain adds a string field (BCL-only; no violation).

**Option B:** Pass `correlationId` as a separate parameter to `makeApplyML` result closure — but `RoutingAlgorithm` type alias is `RoutingConfig -> RouterRequest -> RoutingDecision`; changing the type alias breaks all callers.

**Decision: Option A — add `CorrelationId: string` to `RouterRequest`.**

This is a non-breaking change: existing callers (heuristic path) can set `CorrelationId = ""`. `ChatCompletions.fs` maps wire → domain with the real `correlationId`.

### 2.5 model_version cohort tagging (via RoutingDecision)

`RoutingDecision` currently does not carry model_version. There are two sub-options:

**Sub-option A:** `RoutingDecision` gains a `CanaryCohort: bool` field. `ChatCompletions.buildDecisionLog` reads it to suffix model_version: if `decision.CanaryCohort` then `versionProvider.CanaryVersion` else `versionProvider.BaselineVersion`.

**Sub-option B:** Keep `RoutingDecision` unchanged. Instead: ML.fs injects a per-request version tag via a `ICurrentCohortVersion` port resolved per-request (too complex, too many allocations).

**Decision: Sub-option A.** Add `CanaryCohort: bool` to `RoutingDecision` (Core Domain.fs). Default: `false`. Heuristic path always returns `CanaryCohort = false`.

```fsharp
// src/SmartRouter.Core/Domain.fs (modified RoutingDecision)
type RoutingDecision =
    { Target      : ModelId
      Priority    : RequestPriority
      Reason      : RoutingReason
      IsFallback  : bool
      CanaryCohort: bool }   // NEW: true when canary classifier was used
```

---

## 3. Canary File Layout — Phase 8 Coordination

### 3.1 Lock 11 from Phase 8 CONTEXT.md

Phase 8 explicitly named the canary file as `models/router-canary.zip` and named it as a separate PredictionEnginePool registration:
> "Phase 9 will register `models/router-canary.zip` as a SECOND named pool entry on `AddPredictionEnginePool<RouteInput, RoutePrediction>().FromFile(modelName='router-canary', ...)`"

This is locked. No alternatives to evaluate.

### 3.2 File layout

| File | Owner | Notes |
|------|-------|-------|
| `models/router.zip` | Phase 8 (RetrainingService) | Active baseline; never deleted by Phase 9 |
| `models/router.zip.prev` | Phase 8 (RetrainingService) | Previous baseline; written before each retrain |
| `models/router-canary.zip` | Phase 9 (CanaryService) | Canary candidate; written by admin promote action |

### 3.3 Canary file lifecycle

1. **Install canary:** Operator places `models/router-canary.zip` (e.g., copies a newly trained zip there directly, or Phase 9 exposes a POST /canary/install endpoint that accepts a multipart file). The `PredictionEnginePool("router-canary")` with `watchForChanges:true` detects the new file and loads it.

2. **Canary active:** Phase 9 routes `IsCanaryAsync = true` requests to the canary pool; others to the baseline pool.

3. **Promote:** `POST /canary/promote` → `File.Move("models/router-canary.zip", "models/router.zip", overwrite=true)` (atomically replaces baseline); `IModelVersionProvider.Update(canaryVersion)` called; canary pool now watches `router-canary.zip` which no longer exists — no-file graceful handling (see Section 4).

4. **Rollback (manual or auto):** Set canary gate to 0% in memory (no requests reach canary pool). Canary file is left on disk (can be reactivated by re-enabling the gate).

5. **Phase 8 retrain interaction:** RetrainingService writes new `models/router.zip` during normal operation. Phase 9's baseline pool detects this and reloads automatically (Phase 8 watcher). The canary file is independent and unaffected.

### 3.4 Atomic file operations

- Canary install: `File.Move(tmpPath, "models/router-canary.zip", overwrite=true)` (same `.tmp + File.Move` pattern as Phase 8).
- Canary promote: `File.Copy("models/router.zip", "models/router.zip.prev", overwrite=true)` then `File.Move("models/router-canary.zip", "models/router.zip", overwrite=true)`.

**Coordination during Phase 8 retrain:** If Phase 8's RetrainingService is mid-cycle writing `router.zip.tmp` at the same moment Phase 9 promotes: the `File.Move(canary, router.zip)` clobbers the `.tmp` write-in-progress OR Phase 8's `.tmp → router.zip` clobbers the just-promoted canary. To prevent this race: Phase 9's `CanaryService` must use the same `SemaphoreSlim` that guards Phase 8's `runRetrain`. The cleanest approach: inject `RetrainingService` (or its `SemaphoreSlim`) into `CanaryService`; promotion acquires the semaphore.

---

## 4. PredictionEnginePool Dual-Model Loading

### 4.1 Second pool registration

Phase 8 Lock 11 and RESEARCH.md confirmed the approach:
```fsharp
// CompositionRoot.fs (existing + addition)
services
    .AddPredictionEnginePool<RouteInput, RoutePrediction>()
    .FromFile(
        modelName       = "router",
        filePath        = mlOpts.ModelPath,         // models/router.zip
        watchForChanges = true)
    .FromFile(
        modelName       = "router-canary",
        filePath        = mlOpts.CanaryModelPath,   // models/router-canary.zip
        watchForChanges = true)   // NEW — Phase 9 adds this line
|> ignore
```

Both `modelName = "router"` and `modelName = "router-canary"` are registered on the same `PredictionEnginePool<RouteInput, RoutePrediction>` singleton. `watchForChanges: true` operates independently for each registered path.

### 4.2 MlNetClassifier: Baseline and Canary classifiers

Currently `MlNetClassifier` hardcodes `modelName = "router"`. Phase 9 needs two IClassifier instances:

```fsharp
// Adapters/MlNetClassifier.fs (modified)
type MlNetClassifier(pool: PredictionEnginePool<RouteInput, RoutePrediction>, modelName: string) =
    interface IClassifier with
        member _.PredictAsync(embedding: float32[], _ct: CancellationToken) =
            task {
                let input = { Features = embedding; Label = false }
                let pred  = pool.Predict(modelName = modelName, example = input)
                return { Score = pred.Probability; PredictedLabel = pred.Predicted }
            }
```

CompositionRoot registers two IClassifier-named services or two separate instances:

```fsharp
// Option: use named registrations via keyed services (.NET 8+)
services.AddKeyedSingleton<IClassifier>("baseline", fun sp ->
    let pool = sp.GetRequiredService<PredictionEnginePool<RouteInput, RoutePrediction>>()
    MlNetClassifier(pool, "router") :> IClassifier)
|> ignore

services.AddKeyedSingleton<IClassifier>("canary", fun sp ->
    let pool = sp.GetRequiredService<PredictionEnginePool<RouteInput, RoutePrediction>>()
    MlNetClassifier(pool, "router-canary") :> IClassifier)
|> ignore
```

`makeApplyML` factory receives `(embedder, baselineClassifier, canaryClassifier, canaryGate)` resolved via keyed services in CompositionRoot.

### 4.3 What happens when router-canary.zip does not exist?

`PredictionEnginePool` with `watchForChanges:true` and a missing file at startup: the pool logs a warning and retries with its file-watcher (PR #5351: 50ms × 100 retries = 5s). After 5s, if the file is still missing, the pool enters a "not loaded" state. Any call to `pool.Predict(modelName = "router-canary", ...)` will throw `InvalidOperationException`.

**Phase 9 must guard against this.** The `CanaryGate` must return `false` (baseline) if the canary pool is not loaded. Two strategies:

**Strategy A (recommended):** `ICanaryGate.IsCanaryAsync` catches the pool failure (tries `pool.Predict` → catches exception → returns `false`). But this wastes a prediction call.

**Better Strategy A:** Add an `ICanaryFilePresent : unit -> bool` check BEFORE evaluating the gate: if `File.Exists("models/router-canary.zip")` returns false, short-circuit to baseline without calling the pool.

**Strategy B:** Register `router-canary.zip` as an optional pool entry; catch `InvalidOperationException` from `pool.Predict` in `MlNetClassifier` and re-throw as `CanaryModelNotLoadedException`. `ML.fs` canary branch catches that exception and falls back to baseline.

**Decision: Strategy A-Better.** Check file existence before routing to canary pool. `ICanaryGate` returns false if the canary file does not exist. `CanaryGate.IsCanaryAsync`:
1. If `File.Exists(canaryModelPath)` → false → return false (skip canary entirely)
2. Else → evaluate `IVariantFeatureManager.IsEnabledAsync`

This is safe because file existence check is O(1) and the path is injected from config.

### 4.4 Rollback "unload" mechanism

On rollback (auto or manual):
1. Set `CanaryPercentage = 0` in-memory → `IVariantFeatureManager` returns false for all requests
2. No need to delete `router-canary.zip` — the pool keeps it loaded but no requests reach it
3. Log a structured event: `{event: "canary_rollback", reason: "...", timestamp: ...}`

The canary file can be left on disk for post-mortem analysis. Operator must manually re-enable the gate (either edit `appsettings.json` or hit `/canary/enable?percentage=N`) to restart the canary.

---

## 5. Routing Decision Integration in ML.fs

### 5.1 Updated makeApplyML signature

```fsharp
// src/SmartRouter.Core/ML.fs
let makeApplyML
    (embedder           : IEmbedder)
    (baselineClassifier : IClassifier)
    (canaryClassifier   : IClassifier)
    (canaryGate         : ICanaryGate)
    : RoutingAlgorithm =
    fun (cfg: RoutingConfig) (req: RouterRequest) ->
        let correlationId = req.CorrelationId   // "" for heuristic mode (gate always returns false)

        let isCanary =
            if String.IsNullOrEmpty(correlationId) then false
            else runSync (fun () -> canaryGate.IsCanaryAsync(correlationId, CancellationToken.None))

        let classifier = if isCanary then canaryClassifier else baselineClassifier

        let prompt = req.Messages |> List.map (fun m -> m.Content) |> String.concat " "
        let embedding = runSync (fun () -> embedder.EmbedAsync(prompt, CancellationToken.None))
        let prediction = runSync (fun () -> classifier.PredictAsync(embedding, CancellationToken.None))

        let target = if prediction.Score >= cfg.MlThreshold then Qwen122B else Qwen35B

        { Target      = target
          Priority    = Low
          Reason      = ML
          IsFallback  = false
          CanaryCohort = isCanary }
```

### 5.2 ChatCompletions model_version tagging

`buildDecisionLog` currently reads `versionProvider.CurrentVersion`. With canary, two versions must exist:

`IModelVersionProvider` gains a second getter `CanaryVersion`:
```fsharp
// Core/RetrainingPorts.fs (modify IModelVersionProvider)
type IModelVersionProvider =
    abstract member CurrentVersion  : string with get   // baseline
    abstract member CanaryVersion   : string with get   // canary; empty string if no canary loaded
    abstract member Update          : newVersion: string -> unit
    abstract member UpdateCanary    : newVersion: string -> unit
```

`ChatCompletions.buildDecisionLog` selects based on `decision.CanaryCohort`:
```fsharp
model_version =
    if decision.CanaryCohort then versionProvider.CanaryVersion
    else versionProvider.CurrentVersion
```

Convention for canary version string: `"ml-{sha8}-canary"` vs `"ml-{sha8}"` for baseline. Plain `sha8` for baseline; `sha8-canary` suffix for canary. The `-canary` suffix is the stable convention used across all JSONL cohort queries.

---

## 6. model_version Tagging for Canary Cohort

### 6.1 Naming convention

| Cohort | model_version format | Example |
|--------|---------------------|---------|
| Baseline | `ml-{8hexchars}` | `ml-a1b2c3d4` |
| Canary | `ml-{8hexchars}-canary` | `ml-e5f6a7b8-canary` |
| Heuristic (no canary) | `heuristic-v1` | `heuristic-v1` |

The `-canary` suffix on the version string is the primary cohort discriminator for JSONL group-by queries (CANARY-02). Never use `"canary"` as a standalone version — always pair it with the actual model hash.

### 6.2 CanaryVersion population

`CanaryService` (Phase 9 admin backend) computes the canary version hash from `models/router-canary.zip` on file load:
```fsharp
let canaryVersion = sprintf "ml-%s-canary" (computeModelVersion mlOpts.CanaryModelPath)
versionProvider.UpdateCanary(canaryVersion)
```

On rollback: `versionProvider.UpdateCanary("")` → subsequent DecisionLogs have empty canary version (though no canary requests should be routed at that point).

### 6.3 Cohort comparison query (CANARY-02 verification)

```bash
# Baseline cohort average fallback_rate (fallback_used = true count / total)
jq -s '[.[] | select(.model_version | test("^ml-") | not or (test("-canary$") | not))]' logs/decisions/*.jsonl | ...

# Canary cohort
jq -s '[.[] | select(.model_version | endswith("-canary"))]' logs/decisions/*.jsonl | ...
```

---

## 7. Auto-Rollback Rolling-60s Metric

### 7.1 Metric accumulator design

`CanaryWatchdog` BackgroundService maintains two thread-safe rolling counters:

```fsharp
// Ring buffer: (timestamp, isFallback)
// One queue per cohort.
type RollingCounter() =
    let queue = ConcurrentQueue<struct(DateTimeOffset * bool)>()

    member _.Record(isFallback: bool) =
        queue.Enqueue(struct(DateTimeOffset.UtcNow, isFallback))

    member _.FallbackRate(windowSeconds: float) =
        let cutoff = DateTimeOffset.UtcNow.AddSeconds(-windowSeconds)
        // Trim old entries (best-effort; ConcurrentQueue dequeue-until-old)
        let mutable too_old = true
        while too_old do
            match queue.TryPeek() with
            | true, struct(ts, _) when ts < cutoff -> queue.TryDequeue() |> ignore
            | _ -> too_old <- false
        let entries = queue.ToArray()   // snapshot
        if entries.Length = 0 then 0.0
        else
            let fallbacks = entries |> Array.filter (fun struct(_, f) -> f) |> Array.length
            float fallbacks / float entries.Length
```

One `RollingCounter` for baseline, one for canary. Both fed from the hot path in `ChatCompletions.fs` after upstream response arrives.

### 7.2 Feeding the counters

`ChatCompletions.handler` is the natural place to record outcomes. After the upstream call returns:
- Inject `ICanaryMetrics` (a Core-side or Cli-side port)
- Record: `canaryMetrics.Record(isCanary = decision.CanaryCohort, isFallback = fallbackUsed)`

`ICanaryMetrics` should be a BCL-only Core port (just records into the in-memory counter). Alternatively, it can be a pure Cli-side concern (not in Core) if Phase 9 keeps all metrics logic in the Cli layer.

**Decision: Cli-side only.** `ICanaryMetrics` lives in the Cli adapter layer. `ChatCompletions.fs` resolves it from DI. Core ML.fs does not reference it (no Core change needed for metrics).

### 7.3 fallback_rate proxy until Phase 10

`fallback_used` in `DecisionLog` is currently always `false` (Phase 10 will set it on real fallback events per context note and Phase 8 Lock 1 semantics).

**Phase 9 proxy:** Treat any upstream error (5xx / `upstream_error` in the routing_reason suffix) as a fallback event. `ChatCompletions.handler` already sets `decision.IsFallback` — but this is always `false` in Phase 9.

**Pragmatic resolution:** For Phase 9 auto-rollback testing purposes only, use the `routing_reason` suffix as the proxy:
- If `routing_reason` ends with `;upstream_error` or `;stream_error` → `isFallback = true`
- Otherwise → `isFallback = false`

This proxy is "good enough" for integration test injection (where we deliberately inject fake 5xx responses for the canary upstream). In production, the canary auto-rollback will only fire if the 122B upstream is actually returning errors for canary-routed requests.

The operator question (open question below) is whether this is acceptable or if a more refined proxy is needed before Phase 10 ships.

### 7.4 CanaryWatchdog BackgroundService

```fsharp
// Adapters/CanaryWatchdog.fs
type CanaryWatchdog(metrics: ICanaryMetrics, canaryState: ICanaryState, opts: CanaryOptions) =
    inherit BackgroundService()

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            use timer = new PeriodicTimer(TimeSpan.FromSeconds(float opts.WatchdogPollIntervalSeconds))
            let mutable more = true
            while more do
                try
                    let! ticked = timer.WaitForNextTickAsync(ct)
                    if ticked then
                        let baselineFbRate = metrics.BaselineCounter.FallbackRate(float opts.RollingWindowSeconds)
                        let canaryFbRate   = metrics.CanaryCounter.FallbackRate(float opts.RollingWindowSeconds)
                        let delta = canaryFbRate - baselineFbRate
                        if delta > opts.AutoRollbackThreshold then
                            canaryState.SetPercentage(0)
                            Log.Warning(
                                "CanaryWatchdog: AUTO-ROLLBACK fired. canary_fb_rate={C:F4} baseline_fb_rate={B:F4} delta={D:F4} threshold={T:F2}",
                                canaryFbRate, baselineFbRate, delta, opts.AutoRollbackThreshold)
                with
                | :? OperationCanceledException -> more <- false
                | ex ->
                    Log.Error(ex, "CanaryWatchdog: watchdog loop error")
                    more <- false
        }
```

### 7.5 Auto-rollback config

```json
"Canary": {
  "CanaryModelPath":              "models/router-canary.zip",
  "PercentageEnabled":            10,
  "RollingWindowSeconds":         60,
  "WatchdogPollIntervalSeconds":  10,
  "AutoRollbackThreshold":        0.10
}
```

Test override: `RollingWindowSeconds = 5`, `WatchdogPollIntervalSeconds = 1` → rollback fires within ~5-6 seconds of injecting fake bad responses.

---

## 8. Auto-Rollback Mechanism

### 8.1 ICanaryState: in-memory gate state

`ICanaryState` (Cli-side singleton, not Core):
```fsharp
type ICanaryState =
    abstract member GetPercentage  : unit -> int    // current canary percentage (0 = disabled)
    abstract member SetPercentage  : int -> unit    // sets in-memory percentage (and updates config)
    abstract member IsRolledBack   : bool with get
    abstract member LastRollbackReason : string option with get
```

**Implementation:** `CanaryState` holds `mutable percentage` (initially from `Canary.PercentageEnabled` config) and `mutable rolledBack`. Guarded by `lock` (same `gate = obj()` pattern as `ModelVersionProvider`).

**Config sync:** When `SetPercentage(0)` is called, the in-memory state takes effect immediately. The `FeatureManagement` config section is NOT updated (it reads from IConfiguration). This means the IConfiguration-based `IVariantFeatureManager` still sees `DefaultRolloutPercentage: 10`. 

**Solution:** Do NOT rely on `IVariantFeatureManager` alone for the "disabled" state. The `ICanaryGate.IsCanaryAsync` checks `ICanaryState.GetPercentage() > 0` BEFORE calling the feature manager:

```fsharp
member _.IsCanaryAsync(correlationId, _ct) =
    task {
        if not (File.Exists(canaryModelPath)) then return false
        elif canaryState.GetPercentage() <= 0 then return false   // rolled back in-memory
        else
            let ctx = TargetingContext(UserId = correlationId, Groups = [||])
            let! enabled = featureManager.IsEnabledAsync<TargetingContext>(CanaryFeatureName, ctx)
            return enabled
    }
```

This two-layer gate (in-memory percentage check + TargetingFilter) means rollback is instant (no config file write needed) and the sticky bucketing remains consistent for requests that do get through.

### 8.2 Rollback persistence across restarts

Phase 9 design: **rollback is non-persistent** (no `router-canary.zip.disabled` marker file). On restart, `CanaryState` initializes from `Canary.PercentageEnabled` in `appsettings.json`. If `PercentageEnabled = 10`, canary resumes at 10% after restart. If operator wants persistent disable, they must set `"PercentageEnabled": 0` in `appsettings.json`.

**Rationale:** Restart = retry is the desired semantics for this operational environment. The operator runs the router on a local rig; a restart is a deliberate action. Non-persistent rollback minimizes ceremony.

### 8.3 /canary endpoint: promote and rollback

```
GET  /canary          → CanaryStatus JSON
POST /canary/promote  → promotes canary to baseline (file move + version update)
POST /canary/rollback → sets PercentageEnabled=0 in-memory + logs
POST /canary/enable?percentage=N → re-enables canary at N% (or uses config default)
```

Authentication: loopback-only (same as `/stats`). No auth surface needed (Kestrel binds to `127.0.0.1:4000`).

`CanaryStatus` response:
```json
{
  "percentage_enabled": 10,
  "is_rolled_back": false,
  "baseline_model_version": "ml-a1b2c3d4",
  "canary_model_version": "ml-e5f6a7b8-canary",
  "canary_file_present": true,
  "last_rollback_reason": null,
  "last_rollback_at": null,
  "rolling_60s": {
    "baseline_fallback_rate": 0.02,
    "canary_fallback_rate": 0.03,
    "baseline_request_count": 85,
    "canary_request_count": 12
  }
}
```

**SemaphoreSlim idempotency for /canary/promote:** The promote action must not race with Phase 8's `RetrainingService.runRetrain`. Strategy: `CanaryService` acquires Phase 8's `SemaphoreSlim` before the `File.Move(canary → router.zip)` step. Phase 8's `RetrainingService` injects the `SemaphoreSlim` as a shared dependency.

Alternative (simpler): `CanaryService` injects `RetrainingService` directly; calls a `TryAcquireForPromotion()` method that returns `SemaphoreReleaseHandle`. The simplest: expose the `SemaphoreSlim` as an injectable singleton from `RetrainingService`.

---

## 9. /canary Admin Endpoint

### 9.1 File structure

New files:
```
src/SmartRouter.Cli/Adapters/CanaryTargetingAccessor.fs   — ITargetingContextAccessor impl
src/SmartRouter.Cli/Adapters/CanaryGate.fs               — ICanaryGate Cli adapter
src/SmartRouter.Cli/Adapters/CanaryState.fs              — ICanaryState singleton
src/SmartRouter.Cli/Adapters/CanaryMetrics.fs            — ICanaryMetrics + RollingCounter
src/SmartRouter.Cli/Adapters/CanaryWatchdog.fs           — BackgroundService auto-rollback
src/SmartRouter.Cli/Adapters/CanaryService.fs            — promote/rollback file ops + coordinator
src/SmartRouter.Cli/Endpoints/Canary.fs                  — GET/POST /canary endpoint handlers
src/SmartRouter.Core/CanaryPorts.fs                      — ICanaryGate (BCL-only)
```

### 9.2 Endpoint registration

Mirror `Stats.fs` pattern:
```fsharp
// Endpoints/Canary.fs
let mapEndpoints (app: WebApplication) =
    app.MapGet("/canary",          Func<HttpContext, Task>(fun ctx -> ...)) |> ignore
    app.MapPost("/canary/promote", Func<HttpContext, Task>(fun ctx -> ...)) |> ignore
    app.MapPost("/canary/rollback",Func<HttpContext, Task>(fun ctx -> ...)) |> ignore
    app.MapPost("/canary/enable",  Func<HttpContext, Task>(fun ctx -> ...)) |> ignore
```

### 9.3 DI registration pattern (CanaryService)

`CanaryService` is NOT a BackgroundService (it handles HTTP requests synchronously + file ops). `CanaryWatchdog` IS a BackgroundService.

```fsharp
// CanaryService — double-registration (no IHostedService leg)
services.AddSingleton<CanaryService>(fun sp -> ...)  |> ignore
services.AddSingleton<ICanaryService>(fun sp -> sp.GetRequiredService<CanaryService>() :> ICanaryService) |> ignore

// CanaryWatchdog — triple-registration (BackgroundService)
services.AddSingleton<CanaryWatchdog>(fun sp -> ...) |> ignore
// No ICanaryWatchdog interface alias needed (no consumer other than the host lifecycle)
services.AddHostedService<CanaryWatchdog>(fun sp -> sp.GetRequiredService<CanaryWatchdog>()) |> ignore

// CanaryState — double-registration
services.AddSingleton<CanaryState>(fun sp -> ...) |> ignore
services.AddSingleton<ICanaryState>(fun sp -> sp.GetRequiredService<CanaryState>() :> ICanaryState) |> ignore

// ICanaryGate — double-registration (no concrete class needed if FeatureManagementCanaryGate has no other consumers)
services.AddSingleton<ICanaryGate>(fun sp ->
    let fm = sp.GetRequiredService<IVariantFeatureManager>()
    FeatureManagementCanaryGate(fm, ...) :> ICanaryGate) |> ignore
```

---

## 10. Test Strategy

### 10.1 Statistical split test (CANARY-01)

Test design: 1000 synthetic requests with distinct correlation_ids (e.g., `Guid.NewGuid().ToString("N")`). Each request hits the `ICanaryGate` implementation directly (no HTTP overhead — unit test the gate). Count canary-routed requests. Assert count falls in [80, 120] (95% CI for Binomial(n=1000, p=0.10)).

**Binomial CI:** For n=1000, p=0.10, the 95% CI is approximately [80.5, 119.5]. Use `Expecto.Expect.isTrue (count >= 80 && count <= 120)` or compute the CI explicitly.

**Why this works:** The TargetingEvaluator hash algorithm is deterministic — 1000 fixed `correlation_ids` → fixed split. Over a random sample of UUIDs, the expected 10% holds by the law of large numbers.

**Sticky bucketing test:** Same `correlation_id` evaluated 100 times → always same bool. Trivial assertion: `List.distinct results = [true]` or `[false]`.

### 10.2 model_version tagging test (CANARY-02)

Integration test: start test router with fake canary enabled at 50% (higher split = more consistent test coverage). Send 50 requests with fixed correlation_ids (25 known-canary, 25 known-baseline per the hash pre-computed offline). Read JSONL. Assert: all canary-cohort requests have `model_version` ending in `"-canary"`, all baseline requests do not.

### 10.3 Manual promote/rollback tests (CANARY-03)

Integration test:
1. Start router with canary at 10%.
2. POST /canary/rollback. Assert `GET /canary` returns `percentage_enabled: 0`.
3. Send 100 requests. Assert all JSONL entries have baseline model_version.
4. POST /canary/enable?percentage=100. Assert `GET /canary` returns `percentage_enabled: 100`.
5. Send 100 requests. Assert all JSONL entries have `-canary` model_version.
6. POST /canary/promote. Assert `GET /canary` shows new baseline version (formerly canary hash), no canary suffix.

### 10.4 Auto-rollback test (CANARY-03 auto-rollback)

Test design:
1. Start router with `RollingWindowSeconds=5, WatchdogPollIntervalSeconds=1, AutoRollbackThreshold=0.10`.
2. Configure fake upstream to return 5xx for canary-routed requests, 200 for baseline.
3. Send 100 requests over ~3 seconds (mix of canary + baseline; canary at 50% for better coverage).
4. Wait 6 seconds (beyond the rolling window).
5. Assert `GET /canary` returns `is_rolled_back: true, percentage_enabled: 0`.
6. Assert Serilog log contains `"canary_rollback"` structured event.

**Key technique:** Override `Canary.RollingWindowSeconds` and `Canary.WatchdogPollIntervalSeconds` via `AddInMemoryCollection` in the test host, mirroring `startTestRouter` pattern from `StreamingTests.fs`.

### 10.5 Compile-order additions to SmartRouter.Cli.fsproj

Insert BEFORE `ChatCompletions.fs` (which consumes `ICanaryGate`), AFTER `RetrainingService.fs`:

```xml
<!-- Phase 9 — Canary deployment -->
<Compile Include="Adapters/CanaryTargetingAccessor.fs" />
<Compile Include="Adapters/CanaryState.fs" />
<Compile Include="Adapters/CanaryGate.fs" />
<Compile Include="Adapters/CanaryMetrics.fs" />
<Compile Include="Adapters/CanaryWatchdog.fs" />
<Compile Include="Adapters/CanaryService.fs" />
<Compile Include="Endpoints/Canary.fs" />
```

New Core file: `src/SmartRouter.Core/CanaryPorts.fs` (or append `ICanaryGate` to `RetrainingPorts.fs`; colocated is cleaner since retraining and canary are Loop B concerns).

### 10.6 Test module addition

New file: `tests/SmartRouter.Tests/CanaryTests.fs`

Add to `RouterTests.rootTests`:
```fsharp
SmartRouter.Tests.CanaryTests.tests   // Phase 9
```

---

## 11. Pitfalls

### Pitfall 1: PercentageFilter is NOT sticky

**What goes wrong:** Using `PercentageFilter` with `"Value": 10` assigns the same request to different cohorts on different evaluations. Same `correlation_id` gets routed to canary on one request and baseline on the next. CANARY-01 (binomial CI) will pass (random is approximately correct) but CANARY-02 (sticky bucketing) will fail.

**How to avoid:** Use `ContextualTargetingFilter` with `UserId = correlation_id`. Confirmed: TargetingEvaluator uses SHA-256 hash — deterministic per userId.

### Pitfall 2: AddFeatureManagement vs AddScopedFeatureManagement

**What goes wrong:** `AddFeatureManagement` (singleton) caches feature flag evaluations for the lifetime of the singleton. Config changes (e.g., updating `DefaultRolloutPercentage`) are not picked up until host restart.

**How to avoid:** Use `AddScopedFeatureManagement`. This creates a new `IVariantFeatureManager` per request scope, which reads the current `IConfiguration` value on each evaluation.

**Note:** Scoped registration requires the `IVariantFeatureManager` to be resolved within a scope. In Minimal APIs, each request creates a new DI scope automatically (middleware pipeline). Direct resolution via `ctx.RequestServices.GetRequiredService<IVariantFeatureManager>()` is safe.

### Pitfall 3: IVariantFeatureManager not IFeatureManager

**What goes wrong:** Resolving `IFeatureManager` instead of `IVariantFeatureManager`. In v4.x, `IVariantFeatureManager` supersedes `IFeatureManager` and is the recommended interface. `IFeatureManager.IsEnabledAsync<TContext>` exists on both, but `IVariantFeatureManager` adds cancellation tokens.

**How to avoid:** Use `IVariantFeatureManager` in `FeatureManagementCanaryGate`. Register with `AddScopedFeatureManagement` which registers both.

### Pitfall 4: try/with semicolon trap in CanaryGate

**What goes wrong:** In F# `task {}`, writing:
```fsharp
task {
    try
        let! result = someTask()
        return result  // ← semicolon at end of try block tries to sequence the catch
    with ex -> ...
}
```
The F# compiler may parse this as a try/with inside the computation expression. The existing project pitfall: naked `try ... with` blocks inside `task {}` without explicit `return` can produce confusing type errors.

**How to avoid:** Use explicit `return` and ensure the `with` handler is syntactically at the same level as `try`. Reviewed from existing project howto doc.

### Pitfall 5: ExceptionDispatchInfo for re-raise in task{} nested try/with

**What goes wrong:** `reraise()` is forbidden inside `task {}` nested `try/with` blocks in F#. Attempting to `reraise ()` inside a `task {}` computation expression throws `InvalidOperationException` at runtime (not compile time).

**How to avoid:** Use `ExceptionDispatchInfo.Capture(ex).Throw()` if re-throwing with original stack trace is needed. Per existing project's howto docs — this is an established pattern.

### Pitfall 6: Race between CanaryService.Promote and RetrainingService

**What goes wrong:** `/canary/promote` runs `File.Move(router-canary.zip, router.zip)` while `RetrainingService.runRetrain` is mid-cycle writing `router.zip.tmp → router.zip`. Two concurrent `File.Move(..., router.zip, overwrite=true)` calls → the last-writer wins, losing one update.

**How to avoid:** `CanaryService.Promote` must acquire the same `SemaphoreSlim` used by `RetrainingService.runRetrain`. The cleanest solution: inject `RetrainingService` into `CanaryService` or extract the `SemaphoreSlim` as a separate `IRetrainLock` singleton. The promote action is non-blocking: if the semaphore is busy (retrain in progress), `/canary/promote` returns HTTP 409 Conflict.

### Pitfall 7: PredictionEnginePool startup with missing router-canary.zip

**What goes wrong:** At startup, `AddPredictionEnginePool.FromFile("router-canary", ...)` is registered for a file that doesn't exist yet (Phase 9 fresh install). The pool throws on first prediction.

**How to avoid:** `ICanaryGate.IsCanaryAsync` checks `File.Exists(canaryModelPath)` before calling the FeatureManager. If the file is absent, return `false` immediately. This is fast (O(1) stat syscall on macOS APFS) and safe.

Also: `ensureCanaryModelAbsent` at startup should NOT be called — do not create an empty/dummy canary model. An absent file = no canary. The gate returns false. The pool registration is inert until the file appears.

### Pitfall 8: Cohort label leakage

**What goes wrong:** A request is routed to the baseline pool but `RoutingDecision.CanaryCohort = true` is somehow set (e.g., a coding error where `isCanary` is evaluated but the classifier selection is wrong). DecisionLog records `model_version = "ml-abc-canary"` for a request that actually used the baseline pool.

**How to avoid:** `CanaryCohort = isCanary` must be set from the SAME boolean that gates the classifier selection — not from a separate call. The single `isCanary` variable in `makeApplyML` must gate both `classifier` selection and `CanaryCohort = isCanary`. Code review: the two uses of `isCanary` in `makeApplyML` must be visually adjacent.

### Pitfall 9: AddPredictionEnginePool chain modification

**What goes wrong:** Phase 9 adds `.FromFile("router-canary", ...)` to the existing `.FromFile("router", ...)` chain. If Phase 8's `CompositionRoot` registration was refactored into a local binding without chaining (e.g., `let pool = services.AddPredictionEnginePool<...>() in pool.FromFile(...) |> ignore`), Phase 9 cannot add the second `.FromFile` without access to the original builder object.

**How to avoid:** Phase 8 Lock 11 forbids refactoring the chain. Verify: `CompositionRoot.fs` lines 204-209 show the `.FromFile` call is chained and the builder is not stored separately. Phase 9 appends `.FromFile("router-canary", ...)` to the existing chain.

### Pitfall 10: TargetingContext Groups must not be null

**What goes wrong:** `TargetingContext(UserId = correlationId, Groups = null)` causes `NullReferenceException` inside `TargetingEvaluator` when it iterates `Groups`.

**How to avoid:** Always pass `Groups = [||]` (empty array), never `null`.

---

## Standard Stack

### New NuGet package

| Package | Version | Purpose | Notes |
|---------|---------|---------|-------|
| `Microsoft.FeatureManagement.AspNetCore` | 4.5.0 | TargetingFilter, ITargetingContextAccessor, WithTargeting | New; not currently in SmartRouter.Cli.fsproj |

### Existing packages used (no version change)

| Package | Current Version | Usage in Phase 9 |
|---------|----------------|-----------------|
| `Microsoft.Extensions.ML` | 5.0.0 | Second `.FromFile("router-canary", ...)` pool registration |
| `Microsoft.ML` | 5.0.0 | RouteInput/RoutePrediction schema (unchanged) |
| `Serilog` | 4.3.1 | Structured auto-rollback event logging |
| `System.Security.Cryptography` | BCL | `computeModelVersion` for canary version hash |
| `Microsoft.Extensions.Hosting` | BCL | CanaryWatchdog BackgroundService |

**Installation:**
```bash
dotnet add src/SmartRouter.Cli/SmartRouter.Cli.fsproj package Microsoft.FeatureManagement.AspNetCore --version 4.5.0
```

---

## Architecture Patterns

### Recommended Phase 9 File Layout

```
src/SmartRouter.Core/
└── CanaryPorts.fs           # ICanaryGate (BCL-only)

src/SmartRouter.Cli/Adapters/
├── CanaryTargetingAccessor.fs   # ITargetingContextAccessor -> correlation_id as UserId
├── CanaryState.fs               # ICanaryState: in-memory percentage + rollback flag
├── CanaryGate.fs                # ICanaryGate: file-check + IVariantFeatureManager + ICanaryState
├── CanaryMetrics.fs             # ICanaryMetrics: two RollingCounter (baseline + canary)
├── CanaryWatchdog.fs            # BackgroundService: poll metrics, fire auto-rollback
└── CanaryService.fs             # promote/rollback file ops, /canary handler backend

src/SmartRouter.Cli/Endpoints/
└── Canary.fs                    # GET /canary, POST /canary/promote, /rollback, /enable

tests/SmartRouter.Tests/
└── CanaryTests.fs               # CANARY-01 (binomial), -02 (version tag), -03 (rollback)
```

### appsettings.json additions

```json
"Canary": {
  "CanaryModelPath":             "models/router-canary.zip",
  "PercentageEnabled":           10,
  "RollingWindowSeconds":        60,
  "WatchdogPollIntervalSeconds": 10,
  "AutoRollbackThreshold":       0.10
},
"feature_management": {
  "feature_flags": [
    {
      "id": "Canary",
      "enabled": true,
      "conditions": {
        "client_filters": [
          {
            "name": "Microsoft.Targeting",
            "parameters": {
              "Audience": {
                "DefaultRolloutPercentage": 10
              }
            }
          }
        ]
      }
    }
  ]
}
```

Note: `Canary.PercentageEnabled` and `feature_management.feature_flags[0].conditions.client_filters[0].parameters.Audience.DefaultRolloutPercentage` must be kept in sync by the operator (or by `CanaryService.SetPercentage` which updates both in memory). The `PercentageEnabled` config key is the operator's entry point; the `feature_management` section is the FeatureManagement library's config.

Simpler design: `PercentageEnabled` in `Canary` section is the single source of truth; `CanaryGate` reads this and passes it to the `TargetingContext` / `DefaultRolloutPercentage` dynamically — but `IVariantFeatureManager` reads from `IConfiguration`, not from `ICanaryState`. This is the fundamental tension.

**Resolution:** Use `AddInMemoryCollection` at startup to seed `feature_management` from `Canary.PercentageEnabled`, keeping both in sync. On runtime change (SetPercentage), update the in-memory check (`ICanaryState.GetPercentage()`) which is the primary gate. The FeatureManagement config is only used for the actual hash bucketing when the in-memory gate allows through.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Sticky hash-based traffic split | Custom SHA hash → modulo | `ContextualTargetingFilter` + `TargetingEvaluator` | Already implemented with SHA-256, uint32/maxuint32 bucketing, 100% boundary handling |
| Feature flag config format | Custom JSON schema | `feature_management.feature_flags` (Microsoft Feature Management schema) | Standard, hot-reloadable via `IConfiguration`, documented |
| Per-request scoped feature eval | Manual `IConfiguration.GetSection` + parse | `AddScopedFeatureManagement` + `IVariantFeatureManager.IsEnabledAsync` | Handles config reload, filter evaluation, exception safety |
| Rolling window counter | Timestamped ring buffer with locks | `ConcurrentQueue<struct(DateTimeOffset * bool)>` + TryPeek/TryDequeue for trim | Lock-free for enqueue; snapshot-for-rate is safe with ToArray() |

**Key insight:** Do not implement custom bucketing. The TargetingEvaluator's SHA-256 approach is audited and correct. Rolling metrics also don't need a dedicated ring buffer library — `ConcurrentQueue` with timestamp-based trim is sufficient at the expected rate (<100 req/s).

---

## Open Questions

1. **fallback_rate proxy until Phase 10 is confirmed by operator**
   - What we know: `fallback_used = false` always in Phase 9 (Phase 10 activates it). The auto-rollback trigger needs a fallback signal.
   - What's unclear: Is treating `routing_reason` ending with `;upstream_error` as a proxy acceptable? Or should Phase 9 defer auto-rollback until Phase 10's real signal?
   - Recommendation: Implement the proxy (`routing_reason` suffix check) but add a config option `Canary.AutoRollbackEnabled: false` (default: false) so the watchdog is wired but disabled by default. Enable it manually or in Phase 10 when `fallback_used` is real.
   - **Needs operator decision before planning locked down.**

2. **CanaryModelPath — how the canary zip gets there initially**
   - What we know: Phase 9 doesn't auto-generate a canary model. The operator must provide `models/router-canary.zip`.
   - What's unclear: Is a `/canary/install` endpoint needed (multipart file upload), or is "operator `cp` the file" sufficient for this phase?
   - Recommendation: "Operator cp" is sufficient for Phase 9. The `/canary/install` endpoint is a Phase 10+ concern.
   - **This is Claude's discretion unless operator has a preference.**

3. **Promote race vs Phase 8 retrain — SemaphoreSlim sharing approach**
   - What we know: Both Phase 8 retrain and Phase 9 promote write to `models/router.zip`. Need a shared lock.
   - What's unclear: Should `RetrainingService` expose its `SemaphoreSlim` as an injectable `IRetrainLock` singleton, or should `CanaryService` inject `RetrainingService` directly?
   - Recommendation: Extract `IRetrainLock` as a tiny separate singleton (avoids circular/complex DI wiring). `RetrainingService` and `CanaryService` both inject `IRetrainLock`.
   - **This is Claude's discretion.**

4. **RoutingDecision.CanaryCohort field — Core Domain change impact**
   - What we know: Adding `CanaryCohort: bool` to `RoutingDecision` (Core) requires updating all construction sites (Heuristic.fs, ML.fs, possibly test mock builders).
   - What's unclear: How many call sites construct `RoutingDecision` directly? (Need to grep.)
   - Recommendation: Search `RoutingDecision` construction sites before planning; include a task to update all of them (they should set `CanaryCohort = false`).

---

## Sources

### Primary (HIGH confidence)
- `src/SmartRouter.Cli/CompositionRoot.fs` — existing DI patterns, AddPredictionEnginePool chain structure, directly read
- `src/SmartRouter.Core/ML.fs` — makeApplyML current shape, runSync pattern, directly read
- `src/SmartRouter.Cli/Adapters/MlNetClassifier.fs` — modelName="router" hardcoded, directly read
- `src/SmartRouter.Cli/Adapters/ModelVersionProvider.fs` — mutable string + lock pattern, directly read
- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` — DecisionLog schema (12 fields), directly read
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — handler shape, buildDecisionLog, correlationId extraction, directly read
- `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` — CorrelationIdKey, directly read
- `.planning/phases/08-retraining-loop/08-CONTEXT.md` — Lock 8 (file ownership), Lock 11 (router-canary.zip named), Lock 10 (ARCH-01 BCL-only Core), directly read
- `.planning/phases/08-retraining-loop/08-RESEARCH.md` — PredictionEnginePool dual-model, atomic write, SemaphoreSlim pattern, directly read
- [TargetingEvaluator.cs source](https://github.com/microsoft/FeatureManagement-Dotnet/blob/main/src/Microsoft.FeatureManagement/Targeting/TargetingEvaluator.cs) — SHA-256 hash, uint32/maxuint32 × 100 bucketing, confirmed via WebFetch
- [IFeatureManager.IsEnabledAsync docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.featuremanagement.ifeaturemanager.isenabledasync?view=azure-dotnet) — IsEnabledAsync\<TContext\>(string, TContext) overload, F# signature, directly read
- [Microsoft Feature Flag Management reference](https://learn.microsoft.com/en-us/azure/azure-app-configuration/feature-management-dotnet-reference) — feature_management schema, AddScopedFeatureManagement, WithTargeting, ContextualTargetingFilter, directly read
- [NuGet Microsoft.FeatureManagement.AspNetCore 4.5.0](https://www.nuget.org/packages/Microsoft.FeatureManagement.AspNetCore) — latest stable version 4.5.0, net8.0 target, .NET 10 compatible, directly verified

### Secondary (MEDIUM confidence)
- [PercentageFilter.cs source](https://github.com/microsoft/FeatureManagement-Dotnet/blob/main/src/Microsoft.FeatureManagement/FeatureFilters/PercentageFilter.cs) — pure random, no sticky bucketing confirmed via WebFetch
- [TargetingFilter.cs source](https://github.com/microsoft/FeatureManagement-Dotnet/blob/main/src/Microsoft.FeatureManagement/Targeting/TargetingFilter.cs) — delegates to ContextualTargetingFilter, WebFetch

### Tertiary (LOW confidence)
- [FeatureManagement-Dotnet releases](https://github.com/microsoft/FeatureManagement-Dotnet/releases) — version history, WebSearch
- PredictionEnginePool multi-name registration: inferred from Phase 8 RESEARCH.md + Phase 8 CONTEXT.md Lock 11 explicit naming; not directly re-verified against ML.NET 5.0 source in this research session.

---

## Metadata

**Confidence breakdown:**
- TargetingFilter sticky bucketing mechanism: HIGH — source code hash algorithm confirmed
- PercentageFilter non-sticky: HIGH — source code `RandomGenerator.NextDouble()` confirmed
- IVariantFeatureManager API (IsEnabledAsync\<TContext\>): HIGH — official docs F# signature shown
- ICanaryGate Core port design: HIGH — ARCH-01 constraint clear; BCL-only pattern established by Phase 8
- Canary file layout (router-canary.zip): HIGH — Phase 8 CONTEXT.md Lock 11 explicitly named this file
- PredictionEnginePool dual-model: HIGH — Phase 8 established the pattern; chained .FromFile confirmed
- Rolling-60s metric via ConcurrentQueue: HIGH — standard .NET concurrent collection usage
- fallback_rate proxy design: MEDIUM — Phase 10 changes this signal; proxy approach is pragmatic but depends on operator decision
- Auto-rollback across restart persistence: MEDIUM — "non-persistent" design is a recommendation, not a locked decision

**Research date:** 2026-05-09
**Valid until:** 2026-06-09 (Microsoft.FeatureManagement 4.5.0 is stable; ML.NET 5.0 is stable)
