# Phase 10: Health + Fallback + graph_indexing No-Fallback — Locked Context

**Locked:** 2026-05-09
**Source:** 10-RESEARCH.md (Open Questions resolved), confirmed by codebase grep before plan authoring.

This document records architectural decisions, file ownership boundaries, and pre-grepped cascade sites so the three Phase 10 plans can be executed without ambiguity. All items below are **locked** — plans must honor them.

---

## Decisions

### D1 — Probe interval: 10 seconds (configurable)

`Routing.Health.PollingIntervalSeconds = 10` in appsettings.json.
Rationale: 10s strikes a balance between detection latency and probe-log noise. Operator can tune per environment via config; no code change required. (Open Q1 → option recommend.)

### D2 — Consecutive-failure threshold: 1 (configurable)

`Routing.Health.ConsecutiveFailureThreshold = 1` in appsettings.json.
Rationale: matches Phase 7 + Phase 9 "simple defaults, evolve later" pattern. Single probe failure marks upstream `unreachable`. Operator can flip to 2 if false-positive flapping is observed in production. (Open Q2 → option A.)
Implementation: HealthService maintains a `ConcurrentDictionary<ModelId, int>` failure counter; `Interlocked.Increment` on probe failure, atomic write to 0 on success; mark unreachable when counter ≥ threshold.

### D3 — AutoRollbackEnabled: flipped to true

Phase 9 shipped `Canary.AutoRollbackEnabled = false` because `fallback_used` was always 0. Phase 10 makes the signal real (REL-03 fires fallback_used=true on real fallbacks), so `AutoRollbackEnabled = true` becomes meaningful. (Open Q4 → recommend lock.)
Trade-off: a probe blip could trigger an unwarranted canary rollback. Damper: D2's threshold (raise to 2 in prod if needed).
Edit: change `appsettings.json` `Canary.AutoRollbackEnabled` from `false` → `true` in **Plan 10-01** (config edit).

### D4 — Fallback check placement: TWO-PLACE split (per research §Pattern 5 LOCKED DECISION revision)

Final shape (simpler than the research's first draft):

| Location | Trigger | Action |
|----------|---------|--------|
| **ChatCompletions pre-flight** (BEFORE SSE headers, BEFORE upstream call) | `decision.Target = Qwen122B` AND `task = "graph_indexing"` AND `122B unreachable` | Return HTTP 503 + OpenAI-shaped error JSON. `fallback_used = false` (it's a hard error, not a fallback). NO SSE headers set. |
| **QueueDispatcher (CompleteAsync + StreamAsync)** | `decision.Target = Qwen122B` AND `task ≠ "graph_indexing"` AND `122B unreachable` | Mutate decision: `Target = Qwen35B`, `IsFallback = true`, `Reason = FallbackTo35B`. Falls through to normal 35B path. |
| **QueueDispatcher (CompleteAsync + StreamAsync)** | `decision.Target = Qwen122B` AND `task = "graph_indexing"` AND `122B unreachable` | Belt-and-suspenders: return `Error GraphIndexingMustFail`. (Defensive — ChatCompletions pre-flight should already have caught this. Keeps QueueDispatcher self-consistent.) |

ChatCompletions logs `fallback_used = decision.IsFallback` as it does today; QueueDispatcher mutates `decision.IsFallback` to true on reroute, so the log row carries the signal automatically.

### D5 — Streaming retry: separate named HttpClients

Two streaming clients (`upstream35b-stream`, `upstream122b-stream`) registered without `AddResilienceHandler`. Existing `upstream35b` / `upstream122b` GAIN `AddResilienceHandler` retry pipelines.

Retry config (mirrors Phase 7 teacher pattern):
```fsharp
retryOpts.MaxRetryAttempts <- 3
retryOpts.BackoffType      <- DelayBackoffType.Exponential
retryOpts.Delay            <- TimeSpan.FromSeconds(1.0)
retryOpts.ShouldHandle     <-
    // HttpRequestException → retry
    // TaskCanceledException → retry
    // 5xx HTTP → retry
    // 4xx HTTP → DO NOT retry (cost waste, client error)
```

`QwenUpstreamClient.resolveProbe` extends to a 4-way map (target × stream-bool → client-name). F# binding rule: use `.AddHttpClient(name).ConfigureHttpClient(fun c -> ...)` chain (howto: wire-fsharp-namedhttpclient-with-configurehttpclient.md). `AddHttpClient(name, fun c -> ...)` two-argument form is forbidden — silent BaseAddress failure.

### D6 — graph_indexing string comparison: lowercase-normalized

Check is `req.Task |> Option.map (fun t -> t.ToLowerInvariant().Trim()) = Some "graph_indexing"`.
Rationale: `req.Task` carries trimmed-but-not-lowercased raw string from the wire. ML and heuristic classifiers normalize separately; the pre-flight check normalizes here.

No new constant module — the literal `"graph_indexing"` appears in exactly two new places (ChatCompletions pre-flight + QueueDispatcher belt-and-suspenders), and both are within the same plan (10-02). The existing `appsettings.json` `Routing.TaskTable` keys also use `"graph_indexing"`. Adding a constant module would require Domain.fs update and cascade to existing callers — defer until a third call site materializes.

### D7 — /health endpoint shape

```json
{
  "qwen35b":  { "reachable": true,  "last_probed_at": "2026-05-09T12:00:00.000Z" },
  "qwen122b": { "reachable": false, "last_probed_at": "2026-05-09T12:00:05.000Z" }
}
```

HTTP 200 always. Body reflects state. Loopback only via Kestrel binding (matches existing `/stats` and `/canary`). NOT 503 — that's reserved for actual request errors.

`last_probed_at` is ISO-8601 UTC. If a probe has never run for a target (`DateTimeOffset.MinValue`), serialize as `"never"` string.

### D8 — Probe target endpoint: `/v1/models`

GET `<upstream_base>/v1/models` (mlx_lm.server convention). Treat 200 OK as `Reachable`, anything else (timeout, connection refused, non-2xx) as `Unreachable`.

Probe HttpClient: register a SEPARATE named client `"health-probe"` with 5s timeout, NO retry, NO BaseAddress. Each `probeOne` call constructs a full URL `upstreamBase + "/v1/models"`. Sharing `"upstream35b"` / `"upstream122b"` would couple probe failures to retry budget — forbidden.

### D9 — HealthService DI: triple-reg

```fsharp
services.AddSingleton<HealthService>(fun sp -> HealthService(...))            // concrete
services.AddSingleton<IHealthProbe>(fun sp -> sp.GetRequiredService<HealthService>() :> IHealthProbe)  // alias
services.AddHostedService<HealthService>(fun sp -> sp.GetRequiredService<HealthService>())  // hosted
```

Mirrors HardCaseDatasetWriter (Phase 7) and CanaryService (Phase 9) pattern.

### D10 — IsFallback field: ALREADY EXISTS

Confirmed via grep `src/SmartRouter.Core/Domain.fs:83:      IsFallback   : bool`. Phase 9 added it as an explicit bool field. Phase 10 sets it to `true` in QueueDispatcher when fallback fires; reads it in ChatCompletions for `fallback_used` log column.

Construction sites (already initializing `IsFallback = false` — no edits needed):
- `src/SmartRouter.Core/Routing.fs` lines 25, 52, 55, 58, 61, 64, 67, 70, 89
- `src/SmartRouter.Core/Heuristic.fs:44`
- `src/SmartRouter.Core/ML.fs:66`
- `tests/SmartRouter.Tests/LoadTests.fs:19`
- `tests/SmartRouter.Tests/QueueTests.fs:26`

### D11 — IHealthProbe port: extend with TWO methods

Confirmed via grep `Ports.fs:44-48` — port has only `IsReachableAsync` today. Phase 10 ADDS:
- `IsReachable: ModelId -> bool`        (synchronous fast-path; QueueDispatcher + ChatCompletions hot path)
- `LastProbedAt: ModelId -> DateTimeOffset` (for /health endpoint body)

Both are BCL-only (no HttpClient, no Serilog). ARCH-01 preserved.

### D12 — RoutingReason DU cascade: add FallbackTo35B (Plan 10-01)

Confirmed via grep:
- `src/SmartRouter.Core/Domain.fs:31-36` — DU declaration (5 cases: ExplicitModelOverride, ExplicitTask, Heuristic, Default, ML). ADD `| FallbackTo35B`.
- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs:30-34` — `formatReason` match. ADD `| FallbackTo35B -> "fallback_to_35b"`. **`TreatWarningsAsErrors=true` makes this mandatory.**
- `tests/SmartRouter.Tests/MLRoutingTests.fs:167, 190` — `| ML -> ()` match. ADD `| FallbackTo35B -> ()` to keep these tests exhaustive.
- `tests/SmartRouter.Tests/RoutingTests.fs:42, 65, 139, 151, 176, 201, 212` — these match SPECIFIC cases (`ExplicitModelOverride alias -> ...`); they have implicit catch-all `| _ -> failtest "..."` patterns. **Verify each handler's catch-all explicitly.** If any uses bare `| _` it's safe; if any uses an exhaustive match, add `| FallbackTo35B`.

**Plan 10-01 grep recipe (must run before authoring code):**
```bash
grep -rn "match.*RoutingReason\|RoutingReason\." src/ tests/ | grep -v "^Binary"
grep -rn "| ML\b\|| Default\b\|| Heuristic " src/ tests/
```

### D13 — RouterError.GraphIndexingMustFail: ALREADY EXISTS

Confirmed via grep `src/SmartRouter.Core/Domain.fs:66`. Phase 1 declared it in anticipation. No DU edit needed in Plan 10-01.

The only place it would be matched is the `| Error e ->` branch in ChatCompletions (line 219, 340 area). Phase 10's pre-flight check returns 503 BEFORE `routeRequest` errors propagate, so the existing `| Error e ->` branch handles GraphIndexingMustFail (currently as 400) only as a defensive fallback when ChatCompletions pre-flight is bypassed. Plan 10-02 adds an explicit match arm that elevates GraphIndexingMustFail to 503 for symmetry.

### D14 — Phase 10 file ownership (no Phase 8/9 file conflicts)

Phase 10 creates ONE new adapter, ONE new endpoint, modifies a small set of existing files:

**Newly created (owned by Phase 10 only):**
- `src/SmartRouter.Cli/Adapters/HealthService.fs`
- `src/SmartRouter.Cli/Endpoints/Health.fs`
- `tests/SmartRouter.Tests/HealthFallbackTests.fs`

**Modified (Plan 10-01 — Foundation):**
- `src/SmartRouter.Core/Domain.fs` (add FallbackTo35B DU case)
- `src/SmartRouter.Core/Ports.fs` (extend IHealthProbe with IsReachable + LastProbedAt)
- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` (formatReason + FallbackTo35B case)
- `src/SmartRouter.Cli/appsettings.json` (Routing.Health section + AutoRollbackEnabled flip)
- `tests/SmartRouter.Tests/MLRoutingTests.fs` (extend `| ML -> ()` matches; if exhaustive)

**Modified (Plan 10-02 — Implementation):**
- `src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` (add IHealthProbe ctor param + fallback policy)
- `src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` (extend `resolveProbe` to 4-way client-name map)
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` (add pre-flight graph_indexing-must-fail check)
- `src/SmartRouter.Cli/CompositionRoot.fs` (register HealthService triple-reg + 4 named HttpClients with retry on non-stream + health-probe client + QueueDispatcher gets IHealthProbe param)
- `src/SmartRouter.Cli/Program.fs` (wire `Health.mapEndpoints app`)
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` (add HealthService.fs + Health.fs Compile entries)
- `tests/SmartRouter.Tests/QueueTests.fs` + `tests/SmartRouter.Tests/LoadTests.fs` (add FakeHealthProbe to QueueDispatcher constructor sites, listed below)

**QueueDispatcher constructor cascade (12 sites):**
- `src/SmartRouter.Cli/CompositionRoot.fs:347` (production)
- `tests/SmartRouter.Tests/LoadTests.fs:75, 111`
- `tests/SmartRouter.Tests/QueueTests.fs:130, 168, 234, 302, 350, 397, 416, 445, 512`

Each test file gets an `let alwaysReachableProbe = { new IHealthProbe with member _.IsReachable _ = true; member _.IsReachableAsync(_,_) = Task.FromResult true; member _.LastProbedAt _ = DateTimeOffset.MinValue }` helper at the top, used everywhere.

**Modified (Plan 10-03 — Tests):** only `tests/SmartRouter.Tests/HealthFallbackTests.fs` (NEW) and `tests/SmartRouter.Tests/RouterTests.fs` (register the new test list).

No Phase 8/9 file is touched (Retrainer, DatasetMerger, Validator, RetrainingService, CanaryService, CanaryWatchdog, CanaryGate, CanaryState, CanaryMetrics, RetrainLock, ModelVersionProvider, FailureDetector, TeacherLabeler, HardCaseDatasetWriter — all untouched).

### D15 — Test count baseline

Current: 78 pass + 17 ignored without embedding files (85 + 10 with). Plan 10-01 + Plan 10-02 must NOT change test count (they add no test code, only domain + impl). Plan 10-03 adds 5+ new integration tests on top.

Target after Phase 10: **≥83 pass + 17 ignored without embeddings** (≥90 + 10 with).

Plan 10-03's tests (HLTH-04 through HLTH-08, plus optional unit tests) bring this to expected 84-86 + 17. If unit tests for the pre-flight check helper are also added, count climbs further.

### D16 — Compile order

`HealthService.fs` and `Health.fs` are added to `SmartRouter.Cli.fsproj` between Phase 9 adapters and Endpoints:

```xml
<!-- Phase 9 — Canary deployment (Task 2 — watcher adapters) -->
<Compile Include="Adapters/CanaryWatchdog.fs" />
<Compile Include="Adapters/CanaryService.fs" />
<!-- Phase 10 — Health + Fallback -->
<Compile Include="Adapters/HealthService.fs" />
<Compile Include="Endpoints/ChatCompletions.fs" />
<Compile Include="Endpoints/Stats.fs" />
<Compile Include="Endpoints/Canary.fs" />
<Compile Include="Endpoints/Health.fs" />              <!-- new -->
<Compile Include="CompositionRoot.fs" />
```

`HealthService.fs` does not depend on `QueueDispatcher.fs`; can be placed anywhere AFTER `Adapters/Json.fs` and `Adapters/Logging.fs` and BEFORE `CompositionRoot.fs`. Plan 10-02 places it as shown above.

`Health.fs` (endpoint) must come AFTER `HealthService.fs` (depends on `IHealthProbe` from Core, but compile order in F# is type-resolution-dependent — endpoint reads `IHealthProbe` via DI, no direct dependency on `HealthService.fs`). Place with other Endpoints.

---

## Pitfalls (recap from research, must-honor)

1. **Streaming retry — partial SSE delivery.** Two-named-clients pattern (D5).
2. **F# `AddHttpClient(name, fun c -> ...)` silent BaseAddress failure.** Use `.ConfigureHttpClient(...)` chain (D5).
3. **`IHealthProbe` async-only.** D11 adds sync member.
4. **`formatReason` exhaustive match.** D12 — must add `FallbackTo35B` case.
5. **`fallback_used` only on Path A.** Hard errors and upstream errors do NOT set it (D4).
6. **Graph_indexing case mismatch.** Lowercase normalize at the check site (D6).
7. **QueueDispatcher constructor cascade.** D14 lists 12 sites + helper for tests.
8. **HealthService probe HttpClient sharing retry.** D8 — separate `"health-probe"` client.
9. **`reraise()` in `task{}` try/with.** Use `ExceptionDispatchInfo.Capture(oce).Throw()` (per howto).
10. **Initial probe state.** Treat as reachable until first probe completes (D9 startup grace; HealthService constructor sets `(true, DateTimeOffset.MinValue)` for both targets).

---

## Execution sequence (waves)

| Wave | Plan | Reason |
|------|------|--------|
| 1 | 10-01 — Foundation | Touches Core (Domain.fs, Ports.fs) and DecisionLogger.fs (formatReason cascade). No runtime behavior change. Build green required before 10-02. |
| 2 | 10-02 — Implementation | Adds HealthService.fs, Health.fs, modifies QueueDispatcher.fs / ChatCompletions.fs / QwenUpstreamClient.fs / CompositionRoot.fs / Program.fs / .fsproj / appsettings.json. Depends on 10-01 (FallbackTo35B DU case + extended IHealthProbe + Routing.Health config). |
| 3 | 10-03 — Tests | Adds HealthFallbackTests.fs with 5 integration tests (HLTH-04..08) + RouterTests.fs registration. Depends on 10-02 (fake-Kestrel pattern hits real router with HealthService running). |

Strict serial — no parallelism within Phase 10 because every plan modifies code on the path of the next.
