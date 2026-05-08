# Phase 9: Canary Deployment — Context (Locked Decisions)

**Created:** 2026-05-09
**Source:** Locked from `09-RESEARCH.md` open questions + Phase 8 Lock 11.
**Status:** All open questions resolved before Plan 09-01 authoring. Do NOT revisit during execution.

---

## Lock 1 — Auto-rollback signal (RESEARCH §7.3, §11 Pitfall 2 wave-A)

**Decision:** Option B from research. The `CanaryWatchdog` BackgroundService computes the rolling-60s `fallback_rate` metric using `;upstream_error` / `;stream_error` suffix on `routing_reason` as a proxy until Phase 10 makes `fallback_used` a real signal. **`Routing.Canary.AutoRollbackEnabled` defaults to `false` in `appsettings.json`.** Watchdog computes and exposes the metric via `GET /canary` regardless of the flag — only the actual `ICanaryState.SetPercentage(0)` call is gated.

**Rationale:** The auto-rollback wiring ships and is fully testable (tests override `AutoRollbackEnabled = true`), but the production trigger stays disabled until Phase 10's `fallback_used` signal is real. Avoids a v1 false-positive cascade where momentary upstream blips cause spurious rollbacks.

**Operator opt-in:** flip `Routing.Canary.AutoRollbackEnabled` to `true` in `appsettings.json` and restart, OR (future) hit `POST /canary/auto-rollback?enabled=true` (NOT in scope for this phase — config-only).

---

## Lock 2 — Canary file delivery (RESEARCH §3.3, §10 Open Q2)

**Decision:** Option B from research. **The operator manually copies a candidate model to `models/router-canary.zip`** (e.g., `cp models/some-candidate.zip models/router-canary.zip`).

- `POST /canary/promote` atomically moves `models/router-canary.zip` → `models/router.zip` (after copying current `router.zip` to `router.zip.prev`), then updates `IModelVersionProvider.Update(newVersion)` and clears `CanaryVersion`.
- `POST /canary/rollback` sets `ICanaryState.SetPercentage(0)` in-memory (instant, no file change). Operator can manually `rm models/router-canary.zip` afterwards if desired.
- **No `/canary/install` upload endpoint in v1.** Phase 10+ may revisit.

**Rationale:** Minimal scope, no Phase 8 retrofit, no upload endpoint surface. Document the manual-copy step in the plan and in the `GET /canary` response when `canary_file_present = false`.

---

## Lock 3 — Cohort label propagation (RESEARCH §2.5, §6, §10 Open Q4)

**Decision:** Option (b) from research. **Add `ModelVersion: string` field directly to `RoutingDecision` (Core Domain.fs).** `ML.fs makeApplyML` populates it from a per-classifier-instance version string passed at factory construction. `ChatCompletions.buildDecisionLog` reads `decision.ModelVersion` (NOT `versionProvider.CurrentVersion`) for the DecisionLog entry.

**Naming convention:**
- Baseline: `"ml-{8hexchars}"` (e.g., `"ml-a1b2c3d4"`)
- Canary:   `"ml-{8hexchars}-canary"` (e.g., `"ml-e5f6a7b8-canary"`)
- Heuristic (no ML): `"heuristic-v1"`
- Routing-pipeline pre-ML decisions (override / task table): `""` (empty string — buildDecisionLog falls back to `versionProvider.CurrentVersion`)

**Decision flow inside ML.fs makeApplyML:**

```fsharp
let isCanary = ... // single boolean
let (classifier, modelVersion) =
    if isCanary then (canaryClassifier, canaryVersion)
    else (baselineClassifier, baselineVersion)
// ...prediction...
{ Target = target
  Priority = Low
  Reason = ML
  IsFallback = false
  ModelVersion = modelVersion }
```

**Pre-ML stages return `ModelVersion = ""`** — this is the fallthrough sentinel that triggers `buildDecisionLog` to use `versionProvider.CurrentVersion`. This preserves Phase 8's RetrainingService-driven hot-version semantics for non-ML routing paths.

**Rationale:** Eliminates the `IModelVersionProvider` per-request lookup in the ML hot path. ML.fs already knows which classifier it called (canary vs baseline) and which version that is (passed at factory construction). Single source of truth for cohort label.

**Cost:** Every `RoutingDecision` construction site must be updated. **See plan 09-01 enumeration.**

---

## Lock 4 — Sticky bucketing mechanism (RESEARCH §1.2, §1.3, §11 Pitfall 1)

**Decision:** **`ContextualTargetingFilter` via `WithTargeting<CanaryTargetingContextAccessor>()`. PercentageFilter is FORBIDDEN.**

`CanaryTargetingContextAccessor` (Cli, implements `Microsoft.FeatureManagement.FeatureFilters.ITargetingContextAccessor`):
- Reads `correlation_id` from `HttpContext.Items[CorrelationMiddleware.CorrelationIdKey]` via `IHttpContextAccessor`.
- Returns it as `TargetingContext.UserId`.
- `Groups = [||]` (empty array; never null — RESEARCH §11 Pitfall 10).
- If no `HttpContext` present (background work paths), returns `UserId = Guid.NewGuid().ToString("N")` so that out-of-request gate evaluations get random buckets.

`TargetingEvaluator` will hash `SHA-256("{correlationId}\nCanary")` → first 4 bytes as `uint32` → `(value / uint.MaxValue) * 100` < `DefaultRolloutPercentage`. Deterministic per `correlation_id`. Tests verify both statistical split (CANARY-01: ~10% over 1000 distinct UUIDs) AND stickiness (same UUID → always same cohort).

**Grep guard (must_haves, Plan 09-02):** `! grep -r 'PercentageFilter' src/` and `! grep -r 'Microsoft.Percentage' src/`.

---

## Lock 5 — NuGet pin (RESEARCH §1.1)

**Decision:** **`Microsoft.FeatureManagement.AspNetCore` version `4.5.0`** (NOT base `Microsoft.FeatureManagement` — AspNetCore variant is required for `WithTargeting<T>()`, `ITargetingContextAccessor`, `ContextualTargetingFilter`).

Single new package added to `src/SmartRouter.Cli/SmartRouter.Cli.fsproj`. Net8.0 TFM is .NET 10 forward-compatible per Microsoft policy.

`AddScopedFeatureManagement` (NOT `AddFeatureManagement`) — RESEARCH §1.6 / §11 Pitfall 2: scoped registration ensures `IConfiguration` reload of `feature_management:feature_flags[0].conditions.client_filters[0].parameters.Audience.DefaultRolloutPercentage` works without restart.

---

## Lock 6 — File ownership boundaries (RESEARCH §3, Phase 8 Lock 11)

**Locked file ownership (no overlap):**

| File | Owner | Operations |
|------|-------|-----------|
| `models/router.zip` | Phase 8 RetrainingService — exclusive write | RetrainingService writes; PredictionEnginePool watches; CanaryService.Promote moves canary → here (under shared lock) |
| `models/router.zip.prev` | Phase 8 RetrainingService — exclusive write | RetrainingService writes before each retrain; CanaryService.Promote also writes (current router.zip → here before clobbering) |
| `models/router-canary.zip` | Phase 9 CanaryService — exclusive write | Operator places via `cp`; PredictionEnginePool watches; CanaryService.Promote moves away from here |

**Lock hierarchy for Promote race (RESEARCH §11 Pitfall 6):**

`CanaryService.PromoteAsync` and `RetrainingService.runRetrain` BOTH write `models/router.zip`. Race-prevention strategy:

1. Extract a new `IRetrainLock` Cli singleton wrapping a single shared `SemaphoreSlim(1, 1)`.
2. Refactor `RetrainingService` to acquire `IRetrainLock` (not its private `semaphore`) inside `tryRunRetrain` (the existing `Wait(0)` skip-if-busy semantic preserved).
3. `CanaryService.PromoteAsync` ALSO acquires `IRetrainLock.Acquire(0)` — if busy, returns HTTP 409 Conflict ("retrain in progress; try again in a moment"). Non-blocking semantics avoid HTTP request piling up.
4. Documented in plan 09-02 task 2.

**Why a shared `IRetrainLock` (not "inject RetrainingService into CanaryService"):** avoids circular DI and keeps the lock as a single discoverable singleton. Easier to test in isolation.

---

## Lock 7 — Rolling-60s metric storage (RESEARCH §7.1, §7.4)

**Decision:** **Two `ConcurrentQueue<struct(DateTimeOffset * bool)>` instances (baseline + canary).** Producers (the ChatCompletions handler post-response) call `metrics.Record(isCanary, isFallback)`; consumer (`CanaryWatchdog`) polls every 10s, trims by age, computes per-cohort fallback rate and delta.

**Trim algorithm:** dequeue-while-too-old. `ConcurrentQueue.TryPeek` → if older than `cutoff = now - RollingWindowSeconds`, `TryDequeue` and continue. Stop when oldest entry is fresh enough.

**Snapshot algorithm:** after trim, `ToArray()` snapshot, count where second tuple element is `true`, divide by total.

**Minimum sample size:** **50 baseline events** before auto-rollback is computed. Below this, watchdog returns 0.0 cohort delta (avoid noisy small-N comparisons that would trigger spurious rollbacks).

**Test override config** (CanaryTests.fs uses these): `RollingWindowSeconds=5`, `WatchdogPollIntervalSeconds=1`, `AutoRollbackThreshold=0.10`, `MinBaselineSampleSize=10` (overrides production 50 for tests).

---

## Lock 8 — `/canary` endpoint shape (RESEARCH §8.3, §9.2)

**Loopback-only** (matches `/stats` — Kestrel binds to `127.0.0.1:4000`; no auth surface). All four endpoints register on `WebApplication`:

```
GET  /canary           → 200 OK with JSON status (always)
POST /canary/promote   → 200 OK if router-canary.zip moved to router.zip
                        404 Not Found if no canary file present
                        409 Conflict if RetrainingService holds the shared SemaphoreSlim
                        500 on file IO failure
POST /canary/rollback  → 200 OK; idempotent (sets percentage=0 in-memory)
POST /canary/enable?percentage=N  → 200 OK; updates ICanaryState.SetPercentage(N) in [0..100]
                                    400 if N out of range or not integer
```

**`GET /canary` response shape:**

```json
{
  "percentage_enabled": 10,
  "is_rolled_back": false,
  "auto_rollback_enabled": false,
  "baseline_model_version": "ml-a1b2c3d4",
  "canary_model_version": "ml-e5f6a7b8-canary",
  "canary_file_present": true,
  "last_rollback_reason": null,
  "last_rollback_at": null,
  "rolling_60s": {
    "baseline_fallback_rate": 0.02,
    "canary_fallback_rate":   0.03,
    "baseline_request_count": 85,
    "canary_request_count":   12,
    "delta":                  0.01
  }
}
```

snake_case wire shape (matches `/stats` convention).

---

## Lock 9 — IModelVersionProvider extension (RESEARCH §5.2)

**Decision:** Extend `IModelVersionProvider` (Core) with `CanaryVersion` getter and `UpdateCanary` setter:

```fsharp
type IModelVersionProvider =
    abstract member CurrentVersion : string with get   // baseline (Phase 8)
    abstract member CanaryVersion  : string with get   // canary (Phase 9; "" when no canary loaded)
    abstract member Update         : newVersion: string -> unit          // baseline write (Phase 8)
    abstract member UpdateCanary   : newVersion: string -> unit          // canary write (Phase 9)
```

**Cli `ModelVersionProvider`** gets a second `mutable canary` field with the same `lock gate (...)` pattern. **Same instance** services both — no new DI registrations needed beyond what Phase 8 already wired (the existing concrete + interface alias double-reg covers both).

**Initial value:** `CanaryVersion = ""` at construction. `CanaryService` computes it on canary file detection: `sprintf "ml-%s-canary" (computeModelVersion canaryModelPath)`.

**Detection mechanism:** `CanaryService` implements `IHostedService` and owns a `FileSystemWatcher` rooted at `Path.GetDirectoryName(canaryModelPath)` with filter `Path.GetFileName(canaryModelPath)`. On `Created` / `Changed` / `Deleted` / `Renamed` events, the watcher handler recomputes `computeModelVersion canaryModelPath` if the file exists (and calls `versionProvider.UpdateCanary(v)`) or clears it (and calls `versionProvider.UpdateCanary("")`). The handler also fires once at `StartAsync` so initial state reflects the file as it was at boot. `StopAsync` disposes the watcher inside try/with.

**Why a watcher (not just a startup scan):** the router boots BEFORE the operator copies `router-canary.zip` into place (Plan-09-CONTEXT step `cp candidate.zip models/router-canary.zip` is post-boot). Without the watcher, `GET /canary` would return `canary_model_version: ""` until restart or `POST /canary/promote`. The watcher closes the lag. Pattern: identical to `PredictionEnginePool.FromFile(..., watchForChanges = true)` (Phase 8 Lock 11) but at our application layer — independent of ML.NET's internal file watcher.

**DI:** `CanaryService` is triple-registered — `AddSingleton<CanaryService>` + `AddSingleton<ICanaryService>` (alias) + `AddHostedService<CanaryService>(sp -> sp.GetRequiredService<CanaryService>())`. The third registration is what gives the .NET host control of `StartAsync` / `StopAsync` (so the watcher is armed at boot and disposed on shutdown).

**Verification path:** `tests/SmartRouter.Tests/CanaryTests.fs::canary04_fileSystemWatcher` (Plan 09-03 Task 2) boots the router with `CanaryModelExists = false`, writes a stand-in `router-canary.zip` AFTER `StartAsync`, and asserts `GET /canary` reports `canary_model_version` ending in `-canary` within ~2s; then deletes the file and asserts the field clears within ~2s. This is the bit-level proof that the watcher arms post-startup and the Created/Deleted handlers route through `versionProvider.UpdateCanary`.

---

## Lock 10 — DI registration patterns for new singletons

| Service | Registration | Why |
|---------|--------------|-----|
| `IRetrainLock` | Double-reg (concrete + interface) — singleton | Shared between RetrainingService + CanaryService |
| `ICanaryGate` | Single registration (no consumer expects concrete) | Only ML.fs makeApplyML closure consumes |
| `ICanaryState` | Double-reg (concrete + interface) | CanaryService updates concrete; ICanaryState consumed by gate + watchdog + endpoint |
| `ICanaryMetrics` | Double-reg (concrete + interface) | ChatCompletions records via interface; CanaryWatchdog reads via interface; concrete only for testing/diagnostics |
| `CanaryWatchdog` | Triple-reg (concrete + AddHostedService) | BackgroundService — same pattern as RetrainingService (Phase 8 §08-02 Plan task 3 lock) |
| `CanaryService` | Triple-reg (concrete + ICanaryService + IHostedService) | Endpoint resolves via interface; tests resolve via concrete for direct method invocation; host drives Start/Stop for FileSystemWatcher lifecycle (see Lock 9) |
| `IHttpContextAccessor` | `services.AddHttpContextAccessor()` | Required by `WithTargeting<T>()` per RESEARCH §1.4 |
| `CanaryTargetingContextAccessor` | Single-reg as `ITargetingContextAccessor` (Microsoft FM contract) | Resolved by FeatureManagement library internally |

---

## Lock 11 — `RouterRequest.CorrelationId` field

**Decision:** **Add `CorrelationId: string` field to `RouterRequest` (Core Domain.fs).** Default `""` for non-HTTP construction sites.

**Why required:** `ML.fs makeApplyML` is pure Core code; it has no `HttpContext` access. The canary gate needs the `correlation_id` for sticky bucketing. Adding it to `RouterRequest` (BCL-only string field) preserves ARCH-01 and threads the value cleanly from `ChatCompletions.mapWireToRequest` into the ML closure.

**Defensive empty-string handling in makeApplyML:** if `req.CorrelationId = ""` → bypass canary gate (return baseline cohort). This handles tests that construct `RouterRequest` without a wire path.

**Construction site impact:** all 8 `RouterRequest` construction sites must add `CorrelationId = ""` (or actual value in ChatCompletions). **See plan 09-01 enumeration.**

---

## Lock 12 — Compile order in SmartRouter.Cli.fsproj

Phase 9 adds **8 new `<Compile>` entries total** to `src/SmartRouter.Cli/SmartRouter.Cli.fsproj`: 7 in the `Adapters/` cluster and 1 in the `Endpoints/` cluster.

**Adapters/ cluster — 7 entries.** Inserted AFTER `Adapters/RetrainingService.fs` (Phase 8 line) and BEFORE the Endpoints/ cluster:

```xml
<!-- Phase 9 — Canary deployment (Adapters) -->
<Compile Include="Adapters/CanaryTargetingAccessor.fs" />
<Compile Include="Adapters/CanaryState.fs" />
<Compile Include="Adapters/CanaryMetrics.fs" />
<Compile Include="Adapters/RetrainLock.fs" />
<Compile Include="Adapters/CanaryGate.fs" />
<Compile Include="Adapters/CanaryWatchdog.fs" />
<Compile Include="Adapters/CanaryService.fs" />
```

**Endpoints/ cluster — 1 entry.** Inserted AFTER `Endpoints/Stats.fs` (mirrors the natural reading order GET /stats → GET /canary; the F# compile-order requirement only forces "any file that opens Canary or its types comes after Canary.fs". `ChatCompletions.fs` does not open `Canary` (the `/canary` endpoint module is independent of the chat-completions handler), so `Endpoints/Canary.fs` can live anywhere in the Endpoints/ cluster without breaking compilation):

```xml
<Compile Include="Endpoints/ChatCompletions.fs" />
<Compile Include="Endpoints/Stats.fs" />
<Compile Include="Endpoints/Canary.fs" />        <!-- NEW Phase 9 -->
```

**Total grep verification:**

```bash
grep -cE 'Compile Include="(Adapters/(CanaryTargetingAccessor|CanaryState|CanaryMetrics|RetrainLock|CanaryGate|CanaryWatchdog|CanaryService)|Endpoints/Canary)\.fs"' src/SmartRouter.Cli/SmartRouter.Cli.fsproj
# Expect: 8
```

**Order rationale within Adapters/ cluster:** `CanaryTargetingAccessor` and `CanaryState` are leaves (no inter-deps among Canary files); `CanaryMetrics` is a leaf; `RetrainLock` is a leaf; `CanaryGate` depends on `CanaryState`; `CanaryWatchdog` depends on `CanaryMetrics + CanaryState`; `CanaryService` depends on `CanaryState + RetrainLock + CanaryWatchdog (CanaryOptions type)`. So within the Adapters/ cluster: leaves first (TargetingAccessor, State, Metrics, RetrainLock), then Gate, then Watchdog, then Service.

**`Endpoints/Canary.fs` placement (after Stats.fs):** Originally the planning narrative claimed all 8 Phase-9 entries lived "between RetrainingService.fs and ChatCompletions.fs", which was inconsistent with the natural Endpoints/ cluster grouping (the actual layout puts Canary.fs after Stats.fs). The constraint is total-count-of-8 plus the within-cluster dependency order — NOT a "between markers" range. Verifier should grep -c the 8 file names, not awk-range them.

**RetrainLock.fs placement:** Could be earlier (Phase 8 area) since RetrainingService.fs would benefit from refactoring to use it — but for this phase, RetrainingService.fs is also modified to consume `IRetrainLock` (Lock 6), and Phase-9-introduced files come after RetrainingService.fs. Acceptable; the .fsproj diff is small.

**Core compile order:** Add `CanaryPorts.fs` after `RetrainingPorts.fs` and before `Ports.fs` in `src/SmartRouter.Core/SmartRouter.Core.fsproj`. `CanaryPorts.fs` contains `ICanaryGate`. Domain.fs is unchanged structurally except for the two new fields (Lock 3, Lock 11).

---

## Lock 13 — appsettings.json sections

Two new sections at top level (alongside `Retraining`, `DecisionLog`, etc.):

```json
"Canary": {
  "CanaryModelPath":              "models/router-canary.zip",
  "PercentageEnabled":            10,
  "RollingWindowSeconds":         60,
  "WatchdogPollIntervalSeconds":  10,
  "AutoRollbackThreshold":        0.10,
  "AutoRollbackEnabled":          false,
  "MinBaselineSampleSize":        50
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

`Canary.PercentageEnabled` and `feature_management:feature_flags[0]:conditions:client_filters[0]:parameters:Audience:DefaultRolloutPercentage` MUST be kept in sync at startup. CompositionRoot reads `Canary.PercentageEnabled` and writes both layers via `AddInMemoryCollection` BEFORE `AddScopedFeatureManagement` registration. Runtime updates flow through `ICanaryState.SetPercentage` (in-memory; the FeatureManagement config remains static after startup — the in-memory `ICanaryState.GetPercentage > 0` check is the primary gate).

---

## Lock 14 — Test count target

Phase 8 baseline: **73 pass + 10 ignored, 0 failed.**

Phase 9 contract:
- After 09-01: 73 pass + 10 ignored, 0 failed. **No test count delta — Plan 09-01 is foundation only (domain field additions + DI).** Plan 09-01's `<verify>` includes `dotnet test --no-build -- --sequenced` proving the existing suite still passes.
- After 09-02: 73 pass + 10 ignored, 0 failed. **No new tests — Plan 09-02 ships implementation that the existing test suite must not break.**
- After 09-03: ~80 pass + 10 ignored, 0 failed. **Plan 09-03 adds CanaryTests.fs covering CANARY-01..CANARY-03 (~7 new tests).**

Tests that DO NOT require Routing.Algorithm = "ml" must continue to use the heuristic path (StreamingTests, RoutingTests, QueueTests, LoadTests, LoggingTests). Plans 09-01 and 09-02 must NOT cause those tests to depend on canary infrastructure. Specifically: **the canary DI block in CompositionRoot must be guarded on `Routing.Algorithm = "ml"`** — heuristic mode does not register `IClassifier`, has no canary classifier, and the `ICanaryGate` should resolve to a `NullCanaryGate` (always returns false).

**`NullCanaryGate`** (single-reg in heuristic mode): one-line implementation `member _.IsCanaryAsync(_, _) = Task.FromResult(false)`. RESEARCH §2.2.

---

## Lock 15 — Rolling metric proxy semantics

**`isFallback` for the rolling-60s metric is determined by the routing_reason suffix as recorded in DecisionLog:**

```fsharp
let isFallback =
    decision.IsFallback                                      // true when Phase 10 sets it
    || routingReasonSuffix.EndsWith(";upstream_error")       // proxy until Phase 10
    || routingReasonSuffix.EndsWith(";stream_error")         // SSE error suffix
```

Computed inside `ChatCompletions.handler` after the response is fully written, then passed to `metrics.Record(isCanary = decision.ModelVersion.EndsWith("-canary"), isFallback = computedAbove)`.

`isCanary` is derived from the `-canary` suffix on `decision.ModelVersion` (Lock 3) — single source of truth, no separate flag.

---

## Lock 16 — RoutingDecision construction sites (enumerated)

**RoutingDecision construction sites (must add `ModelVersion = ""` or actual value):**

| File | Line(s) | Context | Value to set |
|------|---------|---------|--------------|
| `src/SmartRouter.Core/Routing.fs` | 22-25 | `tryModelOverride` (Stage 1) | `ModelVersion = ""` (fallthrough; ChatCompletions falls back to provider) |
| `src/SmartRouter.Core/Routing.fs` | 50-69 | `taskToDecision` (7 cases) | `ModelVersion = ""` (each case) |
| `src/SmartRouter.Core/Routing.fs` | 88 | `tryTaskTable` (Stage 2 dynamic) | `ModelVersion = ""` |
| `src/SmartRouter.Core/Heuristic.fs` | 41-44 | `applyHeuristic` | `ModelVersion = ""` (heuristic doesn't know which ML version is live) |
| `src/SmartRouter.Core/ML.fs` | 43-46 | `makeApplyML` | `ModelVersion = if isCanary then canaryVersion else baselineVersion` (real load-bearing site) |
| `tests/SmartRouter.Tests/QueueTests.fs` | 22-26 | `mkDecision` test helper | `ModelVersion = ""` |
| `tests/SmartRouter.Tests/LoadTests.fs` | 15-19 | `mkDecision` test helper | `ModelVersion = ""` |

**RouterRequest construction sites (must add `CorrelationId = ""` or actual value):**

| File | Line(s) | Context | Value to set |
|------|---------|---------|--------------|
| `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` | 76-83 | `mapWireToRequest` (real wire path) | `CorrelationId = correlationId` (passed in from handler scope; ChatCompletions threads HttpContext correlation ID through) |
| `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` | 163-171 | null-body synthetic empty request | `CorrelationId = ""` |
| `tests/SmartRouter.Tests/MLRoutingTests.fs` | 17-24 | `mkReq` test helper | `CorrelationId = ""` |
| `tests/SmartRouter.Tests/RoutingTests.fs` | 18-25 | `mkReq` test helper | `CorrelationId = ""` |
| `tests/SmartRouter.Tests/QueueTests.fs` | 28-36 | `emptyRequest` | `CorrelationId = ""` |
| `tests/SmartRouter.Tests/LoadTests.fs` | 21-29 | `emptyRequest` | `CorrelationId = ""` |

**ChatCompletions threading:** `mapWireToRequest` must accept `correlationId: string` parameter and set the field. `handler` passes the `correlationId` (already extracted at line ~153) through.

---

## Lock 17 — Pitfalls to enforce in Plan 09-02

From RESEARCH §11:

1. **PercentageFilter is FORBIDDEN** — grep guard in plan must_haves.
2. **`AddScopedFeatureManagement` not `AddFeatureManagement`** — plan task action specifies this verbatim.
3. **`IVariantFeatureManager` not `IFeatureManager`** — plan task action specifies this verbatim.
4. **try/with semicolon trap (CanaryWatchdog cleanup)** — consult `documentation/howto/handle-fsharp-try-with-semicolon-trap.md`.
5. **`ExceptionDispatchInfo.Capture(oce).Throw()` for OperationCanceledException through task{}** in CanaryWatchdog — consult `documentation/howto/propagate-cancellation-through-fsharp-task-trywith.md`.
6. **`IRetrainLock` race for /canary/promote** — Lock 6 above; plan task action specifies acquire-or-409.
7. **Missing `router-canary.zip` at startup** — `ICanaryGate` checks `File.Exists(canaryModelPath)` BEFORE feature evaluation (RESEARCH §4.3 Strategy A-Better).
8. **Cohort label leakage** — single `isCanary` boolean gates BOTH classifier selection AND `decision.ModelVersion` assignment (RESEARCH §11 Pitfall 8). Code review check: the two uses must be visually adjacent in `makeApplyML`.
9. **PredictionEnginePool chain modification** — Phase 8 left it un-refactored; Phase 9 appends `.FromFile("router-canary", ...)` directly. `watchForChanges = true` per file (RESEARCH §4.1).
10. **TargetingContext.Groups must be `[||]` not null** (RESEARCH §11 Pitfall 10).

---

## Lock 18 — Test isolation strategy for CanaryTests.fs

CanaryTests.fs builds an in-process router via `startTestRouter`-style helper extended for canary:

- `Routing.Algorithm = "ml"` (canary requires ML mode by Lock 14).
- Override appsettings keys `Canary.PercentageEnabled = 50` (higher split → faster statistical confidence in tests).
- Override `Canary.RollingWindowSeconds = 5`, `Canary.WatchdogPollIntervalSeconds = 1`, `Canary.AutoRollbackThreshold = 0.10`, `Canary.AutoRollbackEnabled = true`, `Canary.MinBaselineSampleSize = 10` (test override).
- `feature_management:feature_flags:0:conditions:client_filters:0:parameters:Audience:DefaultRolloutPercentage = 50`.

CANARY-01 statistical test: 1000 distinct correlation_ids → bucket count in [80, 120] for `PercentageEnabled = 10`. Test uses **two flavors**:

- **Unit test** of `ICanaryGate` directly (no HTTP overhead) — invokes 1000 evaluations with synthetic UUIDs.
- **Integration test** sticky-bucket assertion (10 evaluations of same correlation_id always return same value).

CANARY-02: cohort tagging visible in DecisionLog JSONL.
CANARY-03: integration tests for promote/rollback/auto-rollback transitions.

CanaryTests.fs uses `testSequenced` because it touches the file system (`models/router-canary.zip`) and FeatureManagement's `IConfiguration` reload is process-scoped. Adopting RetrainingTests.fs's pattern: per-test temp directories, fake embedder, explicit `StartAsync`/`StopAsync` lifecycle.

---

## Open items intentionally deferred

- `/canary/install` upload endpoint — Phase 10+.
- Persistent rollback marker (survives restart) — RESEARCH §8.2; non-persistent is the operator-friendly default.
- Real `fallback_used = true` signal — Phase 10 makes the rolling metric production-trustworthy.
- Canary version comparison metrics beyond fallback_rate (latency, throughput) — out of scope; cohort comparison via JSONL group-by is the documented path (CANARY-02).
