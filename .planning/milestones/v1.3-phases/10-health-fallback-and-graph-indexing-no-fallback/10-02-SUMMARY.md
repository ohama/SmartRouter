---
phase: 10-health-fallback-and-graph-indexing-no-fallback
plan: 02
subsystem: infra
tags: [health-probe, fallback, background-service, fsharp, polly, http-resilience, named-httpclient]

requires:
  - phase: 10-01
    provides: "RoutingReason.FallbackTo35B DU case; IHealthProbe extended with IsReachable+LastProbedAt; appsettings Routing.Health section; DecisionLogger formatReason cascade"
provides:
  - "HealthService BackgroundService probing both upstreams every PollingIntervalSeconds via /v1/models"
  - "GET /health endpoint returning per-upstream reachability snapshot (HTTP 200, JSON D7 shape)"
  - "5 named HttpClients via .ConfigureHttpClient chain form: upstream35b/122b with retry, -stream variants without, health-probe with 5s timeout"
  - "QueueDispatcher 3-arg ctor: fallback policy in BOTH CompleteAsync + StreamAsync (reroute to 35B or yield GraphIndexingMustFail)"
  - "ChatCompletions pre-flight early-return (503) for graph_indexing-when-122B-unreachable BEFORE SSE headers; shadow-rebind for transparent 35B fallback"
  - "QwenUpstreamClient stream-aware client name selection (4-way map: target x stream-bool)"
affects:
  - "10-03: HealthFallbackTests.fs tests exercise these new paths (HLTH-04..08)"
  - "Phase 7 CanaryWatchdog: AutoRollbackEnabled=true (10-01) + fallback_used=true records from this plan now feed real rollback signal"
  - "Phase 8 RetrainingService: fallback rows in JSONL will appear as Route35B teacher labels via FailureDetector"

tech-stack:
  added: []
  patterns:
    - "HealthService BackgroundService triple-reg DI pattern (concrete + IHealthProbe alias + AddHostedService) — mirrors CanaryService D9"
    - "ExceptionDispatchInfo.Capture(oce).Throw() for OCE through task{} await points (not reraise() which is FS0413)"
    - ".AddHttpClient(name).ConfigureHttpClient(...) chain form for all named clients — 2-arg form forbidden (silent BaseAddress failure)"
    - "Option B early-return + shadow-rebind in F# task{}: if condition then ... return () else (); let var = rebind"
    - "taskSeq yield-and-terminate for graph_indexing fallback: taskSeq { yield Error err } (not return which is invalid in taskSeq)"

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/HealthService.fs
    - src/SmartRouter.Cli/Endpoints/Health.fs
  modified:
    - src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
    - src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/Program.fs
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - tests/SmartRouter.Tests/QueueTests.fs
    - tests/SmartRouter.Tests/LoadTests.fs

key-decisions:
  - "D4 (two-place fallback) implemented: ChatCompletions pre-flight for graph_indexing-503 (BEFORE SSE headers) + QueueDispatcher reroute for non-graph_indexing (defensive belt-and-suspenders)"
  - "D5 (chain form for all 5 named HttpClients): upstream35b/122b have AddResilienceHandler retry; -stream variants and health-probe have NO retry handler"
  - "D9 (triple-reg): HealthService registered as concrete singleton + IHealthProbe alias + AddHostedService — single instance across all three roles"
  - "IsReachableAsync interface member is curried (target -> ct -> Task<bool>), NOT tupled — F# interface implementation uses curried form"
  - "open System.Threading.Tasks required in CompositionRoot.fs for ValueTask in retry predicate ShouldHandle"
  - "HealthService() constructor needs 'new HealthService()' syntax in DI lambda (BackgroundService implements IDisposable)"

patterns-established:
  - "Stream-aware HttpClient selection: resolveProbe(target, stream) -> (probe, clientName, url); CompleteAsync passes false, StreamAsync passes true"
  - "taskSeq yield-Error-and-terminate: taskSeq { yield Error GraphIndexingMustFail } — implicit end-of-block terminates the sequence"
  - "alwaysReachableProbe test stub: { new IHealthProbe with IsReachable _ = true; IsReachableAsync target _ct = Task.FromResult(true); LastProbedAt _ = DateTimeOffset.MinValue }"

duration: 57min
completed: 2026-05-09
---

# Phase 10 Plan 02: Health/Fallback Implementation Summary

**HealthService BackgroundService + /health endpoint + 5-named-HttpClient retry policy + QueueDispatcher/ChatCompletions fallback gates make 122B outages transparent (reroute to 35B) or loud (graph_indexing 503) in real time**

## Performance

- **Duration:** 57 min
- **Started:** 2026-05-09T00:38:23Z
- **Completed:** 2026-05-09T01:35:27Z
- **Tasks:** 4 (Task 1 + Task 2a + Task 2b + Task 3)
- **Files modified:** 10

## Accomplishments

- HealthService BackgroundService probes both upstreams via GET /v1/models every PollingIntervalSeconds (default 10s); ConcurrentDictionary state with ConsecutiveFailureThreshold=1; ExceptionDispatchInfo.Capture for OCE
- GET /health returns D7 JSON shape (HTTP 200 always); last_probed_at="never" until first probe fires
- 5 named HttpClients registered via .ConfigureHttpClient chain form: non-stream clients get Polly AddResilienceHandler retry (3x exponential, 5xx + transient only, NOT 4xx); -stream variants have NO retry (SSE partial output cannot be retried); health-probe has 5s timeout + NO retry + NO BaseAddress
- QueueDispatcher CompleteAsync + StreamAsync both implement fallback: rebind decision to 35B/FallbackTo35B for non-graph_indexing; return/yield GraphIndexingMustFail for graph_indexing-when-122B-unreachable
- ChatCompletions Option B: pre-flight early-return (503 + model_unavailable JSON) BEFORE SSE headers; shadow-rebind for transparent 35B reroute; existing 5+ decisionLogger.Log calls auto-pickup decision.IsFallback=true
- 11 test construction sites updated for QueueDispatcher 3-arg signature; alwaysReachableProbe stub makes fallback a no-op for existing tests

## Task Commits

1. **Task 1: HealthService.fs + Health.fs + .fsproj** - `252fc46` (feat)
2. **Task 2a: CompositionRoot DI wiring** - `d222fdb` (chore)
3. **Task 2b: Adapters + Pre-flight + Endpoints** - `ea9e3b7` (feat)
4. **Task 3: Test construction sites** - `e692c67` (test)

## Files Created/Modified

- `src/SmartRouter.Cli/Adapters/HealthService.fs` (NEW) — BackgroundService polling /v1/models per upstream; implements IHealthProbe; ConcurrentDictionary state
- `src/SmartRouter.Cli/Endpoints/Health.fs` (NEW) — GET /health; HTTP 200; JSON shape per D7; "never" for DateTimeOffset.MinValue
- `src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` — 3rd ctor param IHealthProbe; fallback policy in both async methods
- `src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` — resolveProbe extended to (target, stream) 4-way map; -stream clients for SSE
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — pre-flight graph_indexing-503 + shadow-rebind for transparent 35B fallback
- `src/SmartRouter.Cli/CompositionRoot.fs` — 5 named HttpClients (chain form); HealthService triple-reg; QueueDispatcher 3-arg DI
- `src/SmartRouter.Cli/Program.fs` — Health.mapEndpoints app wired
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — HealthService.fs + Endpoints/Health.fs Compile entries added
- `tests/SmartRouter.Tests/QueueTests.fs` — alwaysReachableProbe helper; 9 QueueDispatcher() sites updated
- `tests/SmartRouter.Tests/LoadTests.fs` — alwaysReachableProbe helper; 2 QueueDispatcher() sites updated

## Decisions Made

- IsReachableAsync in IHealthProbe is curried F# (target -> ct -> Task<bool>), not tupled — implementation uses `member _.IsReachableAsync target _ct = Task.FromResult(r)` (detected by FS0856 build error on first attempt with tupled form)
- open System.Threading.Tasks required in CompositionRoot.fs for ValueTask in the Polly retry predicate (CompositionRoot previously opened System only)
- HealthService() constructor requires `new HealthService()` syntax inside DI lambda because BackgroundService inherits IDisposable — F# FS0760 recommends `new` when type implements IDisposable

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] IsReachableAsync curried signature mismatch**
- **Found during:** Task 1 (HealthService.fs initial build)
- **Issue:** Plan template used `member _.IsReachableAsync(target, _ct)` (tuple form); IHealthProbe has curried `target -> ct -> Task<bool>` → FS0856 compile error
- **Fix:** Changed to `member _.IsReachableAsync target _ct = Task.FromResult(r)` (curried form matching port definition)
- **Files modified:** src/SmartRouter.Cli/Adapters/HealthService.fs
- **Verification:** Build succeeded after fix
- **Committed in:** 252fc46 (Task 1 commit)

**2. [Rule 1 - Bug] ValueTask unresolved in CompositionRoot.fs retry predicate**
- **Found during:** Task 2a (CompositionRoot DI wiring build)
- **Issue:** `ValueTask.FromResult(retry)` in retry predicate needed `System.Threading.Tasks` which was not opened — FS0039
- **Fix:** Added `open System.Threading.Tasks` to CompositionRoot.fs open block
- **Files modified:** src/SmartRouter.Cli/CompositionRoot.fs
- **Verification:** Build succeeded after fix
- **Committed in:** d222fdb (Task 2a commit)

**3. [Rule 1 - Bug] HealthService constructor needs 'new' keyword in DI lambda**
- **Found during:** Task 2a (CompositionRoot DI wiring build)
- **Issue:** `HealthService(...)` in AddSingleton lambda → FS0760 warning (TreatWarningsAsErrors=true, treated as error) — BackgroundService implements IDisposable, F# requires `new` for IDisposable ctors in ambiguous contexts
- **Fix:** Changed to `new HealthService(...)`
- **Files modified:** src/SmartRouter.Cli/CompositionRoot.fs
- **Verification:** Build succeeded after fix
- **Committed in:** d222fdb (Task 2a commit)

---

**Total deviations:** 3 auto-fixed (all Rule 1 — build errors with clear fixes)
**Impact on plan:** All auto-fixes were compile errors with deterministic solutions. No scope change.

## Issues Encountered

None beyond the 3 auto-fixed build errors above.

## Next Phase Readiness

- Plan 10-03 (HealthFallbackTests.fs) can now exercise all the new paths: HealthService stub probe, graph_indexing-503 via ChatCompletions pre-flight, non-graph_indexing transparent fallback, /health endpoint JSON shape
- AutoRollbackEnabled=true (10-01) + fallback_used=true records from this plan make CanaryWatchdog rolling-60s metric meaningful for REL-03 validation
- FallbackTo35B routing reason appears in JSONL DecisionLog rows — Phase 7 FailureDetector and Phase 8 RetrainingService self-improvement loop now active for fallback traffic

---
*Phase: 10-health-fallback-and-graph-indexing-no-fallback*
*Completed: 2026-05-09*
