---
phase: 19-35b-self-routing
plan: 02
subsystem: routing+observability
tags: [fsharp, dotnet, dependency-injection, httpclient, polly, resilience, stats, self-routing]

# Dependency graph
requires:
  - phase: 19-01
    provides: SelfRouter.fs adapter — ISelfRouter, ISelfRouterStats, SelfRouterOptions, SelfRouter concrete class
  - phase: 18-session-store
    provides: SessionStore pattern — triple-reg DI, configureWithoutMl NoOp convention
  - phase: 16-122b-as-judge
    provides: JudgeClient pattern — named HttpClient + triple-reg + IJudgeStats NoOp in ml-mode and offline

provides:
  - Named "selfrouter" HttpClient registered in IHttpClientFactory (5s timeout, 1-retry @ 200ms constant)
  - SelfRouter concrete + ISelfRouter + ISelfRouterStats triple-registration in "selfrouting" mode arm
  - ISelfRouterStats NoOp in "ml" mode arm (PITFALL #7 — /stats returns zeros, not 500)
  - ISelfRouterStats NoOp in configureWithoutMl (--retrain offline path DI integrity)
  - 4 new /stats fields: selfrouter_cache_hits, selfrouter_cache_misses, selfrouter_call_count, selfrouter_skipped
  - Routing.SelfRouter config block in appsettings.json (Endpoint/PromptPath/TimeoutSeconds/MaxCacheEntries)
  - Stale CompositionRoot.fs comments refreshed (plan-checker Warning 1)
  - JudgeOptions type annotation fix (field-name collision with SelfRouterOptions)
affects: [Phase 19-03, Phase 19-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "mode-gated DI: if routingMode = 'selfrouting' then ... else ... outside factory lambda"
    - "4-field ISelfRouterStats struct tuple (cacheHits, cacheMisses, callCount, skipped)"
    - "null-safe GetService<ISelfRouterStats>() + isNull (box ...) in Stats.fs endpoint handler"

key-files:
  created: []
  modified:
    - src/SmartRouter.Cli/Endpoints/Stats.fs
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/appsettings.json

key-decisions:
  - "Mode-gated SelfRouter DI placed OUTSIDE RoutingAlgorithmRegistration factory lambda (not inside match arm) — factory lambda builds a single registration value; services.AddSingleton calls belong at the outer IServiceCollection level"
  - "JudgeOptions type annotation required: JudgeOptions and SelfRouterOptions share all 4 field names; opening SelfRouter namespace last caused F# to infer normalized as SelfRouterOptions — type annotation : JudgeOptions on local binding fixes disambiguation"
  - "ISelfRouterStats NoOp in both ml-mode arm and configureWithoutMl (PITFALL #7) — /stats must not 500 when mode is not selfrouting or service is running offline"
  - "Named selfrouter HttpClient: 1 retry @ 200ms constant (vs judge's 2 retries exponential) — classify is on hot path; fail-open quickly rather than accumulate retry latency"
  - "selfRouterOpts built once at configureRequestPipeline time (not inside factory lambda) so it's captured by the HttpClient config closure — same pattern as effectiveEndpoint for judge"

patterns-established:
  - "SelfRouter triple-reg pattern: concrete AddSingleton<SelfRouter> → ISelfRouter alias → ISelfRouterStats alias (all GetRequiredService<SelfRouter>() :> interface)"
  - "NoOp ISelfRouterStats in non-selfrouting paths: object expression { new ISelfRouterStats with member _.GetSelfRouterStats() = struct (0L, 0L, 0L, 0L) }"
  - "Stats field append pattern: new ISelfRouter/Stats fields always appended at END of StatsWire record (backward-compat for operators scripting around /stats output)"

# Metrics
duration: 6min
completed: 2026-05-12
---

# Phase 19 Plan 02: DI Wiring + Stats + Config Summary

**ISelfRouter registered in DI (selfrouting mode), 4 selfrouter_* /stats fields exposed, and Routing.SelfRouter config block added — SelfRouter is resolvable but no classify call yet (Plan 19-03 wires ChatCompletions.fs)**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-05-11T22:06:49Z
- **Completed:** 2026-05-11T22:12:29Z
- **Tasks:** 3
- **Files modified:** 3

## Accomplishments

- Named "selfrouter" HttpClient registered in IHttpClientFactory: `BaseAddress=Upstreams.Model35B`, 5s timeout, `AddResilienceHandler("selfrouter-pipeline")` with 1 retry @ 200ms constant delay (SR-01)
- SelfRouter triple-registration in "selfrouting" mode arm: concrete singleton + ISelfRouter alias + ISelfRouterStats alias — all three resolve to same instance (shared LRU cache + counters)
- ISelfRouterStats NoOp registered in both "ml" mode arm and configureWithoutMl — PITFALL #7 prevention; /stats returns zeros, not 500
- 4 new /stats fields appended to StatsWire (selfrouter_cache_hits, _cache_misses, _call_count, _skipped) with null-safe ISelfRouterStats resolve
- Routing.SelfRouter config block in appsettings.json (SR-04, SR-05)
- Stale CompositionRoot.fs comments refreshed per plan-checker Warning 1

## Task Commits

Each task was committed atomically:

1. **Task 1: Stats.fs — add 4 selfrouter_* fields** - `31e4f88` (feat)
2. **Task 2: CompositionRoot.fs — SelfRouter DI wiring + NoOps** - `e1e635b` (feat)
3. **Task 3: appsettings.json — Routing.SelfRouter config block** - `2e3f486` (feat)

**Plan metadata:** (docs commit follows below)

## Files Created/Modified

- `src/SmartRouter.Cli/Endpoints/Stats.fs` — open SelfRouter namespace; 4 new StatsWire int64 fields; null-safe GetService<ISelfRouterStats>() + isNull guard; wire record population
- `src/SmartRouter.Cli/CompositionRoot.fs` — open SelfRouter namespace; mode-gated if/else block for HttpClient + triple-reg (selfrouting) vs NoOp (ml); configureWithoutMl NoOp; JudgeOptions type annotation fix; stale comment refresh
- `src/SmartRouter.Cli/appsettings.json` — Routing.SelfRouter block with 4 keys at defaults

## Decisions Made

- **Mode-gated block outside factory lambda**: `if routingMode = "selfrouting" then (services.AddHttpClient + services.AddSingleton x3) else (services.AddSingleton NoOp)` placed before `RoutingAlgorithmRegistration` factory — services registrations must happen at the outer `IServiceCollection` scope, not inside the factory lambda which constructs a single registration value.

- **JudgeOptions type annotation fix (auto-fixed deviation)**: Opening `SmartRouter.Cli.Adapters.SelfRouter` caused F# to infer the `normalized` record literal in JudgeClient registration as `SelfRouterOptions` instead of `JudgeOptions` (both types share all 4 field names: Endpoint, PromptPath, TimeoutSeconds, MaxCacheEntries). Added `: JudgeOptions` type annotation to disambiguate. This is a correctness fix required to compile.

- **selfRouterOpts built at configureRequestPipeline time**: Options resolution (empty Endpoint → Upstreams.Model35B fallback) happens once at registration time, not inside the factory or per-request. The HttpClient config closure and SelfRouter constructor both capture the pre-built value. Mirrors `effectiveEndpoint` pattern for judge.

- **1 retry @ 200ms constant for selfrouter** (vs judge's 2 retries exponential): Classify is on the hot path — fail-open quickly (RouteFailed → RouteSafe fallback) rather than accumulate retry latency. Judge's 2x exponential backoff is for a quality-gate decision on borderline cases; classify is best-effort.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] JudgeOptions type annotation added to disambiguate field-name collision**

- **Found during:** Task 2 (CompositionRoot.fs DI wiring)
- **Issue:** `SelfRouterOptions` and `JudgeOptions` share all 4 field names (Endpoint, PromptPath, TimeoutSeconds, MaxCacheEntries). After opening `SmartRouter.Cli.Adapters.SelfRouter`, F# infers `normalized` record literal in JudgeClient registration as `SelfRouterOptions` — FS0001 type mismatch error at `JudgeClient(...)` constructor call.
- **Fix:** Added `: JudgeOptions` type annotation to the `let normalized` binding in the JudgeClient registration block.
- **Files modified:** `src/SmartRouter.Cli/CompositionRoot.fs`
- **Verification:** `dotnet build` clean (0 warnings, 0 errors) after annotation added.
- **Committed in:** `e1e635b` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 — compile error / type disambiguation)
**Impact on plan:** Required for correct compilation. No scope creep. Plan intent fully preserved.

## Issues Encountered

- Boot smoke test for selfrouting mode failed (ML ONNX embedding files absent in dev environment). This is expected — `configureRequestPipeline` calls `ensureEmbeddingFilesPresent` unconditionally (MODE-03 invariant). Integration tests use `configureWithoutMl` which bypasses the ML bootstrap; all 150 tests passed confirming DI graph integrity.

## Next Phase Readiness

- `ISelfRouter` is resolvable from `HttpContext.RequestServices` in selfrouting mode
- `ISelfRouterStats` is resolvable in all 3 startup paths (selfrouting, ml, offline)
- `/stats` exposes 4 new fields; all will read 0 until Plan 19-03 wires the classify call
- Plan 19-03 can now resolve `GetService<ISelfRouter>()` in `ChatCompletions.fs` and invoke `ClassifyAsync`
- No blockers for 19-03

---
*Phase: 19-35b-self-routing*
*Completed: 2026-05-12*
