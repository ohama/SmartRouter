---
phase: 18-session-store-and-sticky-escalation
plan: "02"
subsystem: routing-adapter
tags: [fsharp, session-store, concurrent-dictionary, sticky-escalation, di, asp-net-core, middleware]

# Dependency graph
requires:
  - phase: 18-01
    provides: RouterRequest.SessionId field; SessionState BCL-only record; RoutingReason.StickyEscalation DU case; formatReason 8-arm exhaustive match

provides:
  - ISessionStore interface + SessionStore ConcurrentDictionary class with 122B-wins merge + LRU eviction stub
  - SessionOptions CLIMutable config binding (TtlMinutes, MaxEntries)
  - CorrelationMiddleware reads X-Session-Id header into ctx.Items[SessionIdKey]
  - mapWireToRequest populates req.SessionId from ctx.Items extraction
  - CompositionRoot SessionStore DI triple-reg in both configureRequestPipeline and configureWithoutMl
  - Phase 17 selfrouting stub replaced with sticky-or-default closure consuming ISessionStore
  - Point B session writes: non-streaming after finalDecision; streaming on normal exit only (skip OCE + error arms)
  - appsettings.json Routing.Session.{TtlMinutes=30, MaxEntries=10000} config keys

affects: [18-03, 19-01, 19-03, 20-01]

# Tech tracking
tech-stack:
  added: []  # BCL only — ConcurrentDictionary, Interlocked, DateTimeOffset; no new NuGet
  patterns:
    - "122B-wins merge function: AddOrUpdate updateValueFactory keeps Qwen122B if either old or new is 122B — non-negotiable concurrent-write safety invariant"
    - "Point B placement: session write AFTER finalDecision resolves (post-judge cascade), BEFORE decisionLogger.Log"
    - "Session write skipped on error/cancellation paths: OCE + general exception (streaming) + upstream Error e (non-streaming)"
    - "SessionOptions CLIMutable binding mirrors JudgeOptions; defensive defaults in class constructor"
    - "IDisposable-inheriting F# class construction: must use 'new SessionStore(...)' not 'SessionStore(...)' (FS0760)"
    - "ISessionStore hoisted above req.Stream branch to serve both streaming and non-streaming Point B"

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/SessionStore.fs
  modified:
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/appsettings.json

key-decisions:
  - "ISessionStore lives in Cli (ARCH-01 preserved): SessionStore uses ConcurrentDictionary + IHostedService + ILogger which are Cli-layer concerns; only SessionState is in Core"
  - "SessionStore DI is unconditional (not mode-gated): benefits both Routing.Mode values; Anti-Pattern 4 from 18-RESEARCH forbids mode-gating"
  - "configureWithoutMl gets same triple-reg for backward-compat: HealthFallbackTests + QualityFallbackTests resolve ISessionStore without DI errors"
  - "Streaming Point B writes decision.Target (not finalDecision — no quality fallback in streaming by Phase 14 design)"
  - "new keyword required: SessionStore inherits BackgroundService which is IDisposable; F# compiler emits FS0760 without new"
  - "ISessionStore resolved above req.Stream branch to avoid duplication; singleton so resolution cost is negligible"

patterns-established:
  - "122B-wins merge: concurrent session writes never lose 122B escalation"
  - "Point B after finalDecision: session reflects what client actually received, not initial routing guess"
  - "Error-path skip: OCE + stream error + upstream error arms never write session store"

# Metrics
duration: 35min
completed: 2026-05-11
---

# Phase 18 Plan 02: SessionStore and Wiring Summary

**ConcurrentDictionary SessionStore with 122B-wins merge wired end-to-end: X-Session-Id header → req.SessionId → sticky-or-default cascade closure → Point B writes after finalDecision resolves**

## Performance

- **Duration:** ~35 min
- **Started:** 2026-05-11T15:50:00Z
- **Completed:** 2026-05-11T16:10:30Z
- **Tasks:** 4
- **Files modified:** 5 + 1 new

## Accomplishments

- SessionStore adapter: `ConcurrentDictionary<string, SessionState>` + `Interlocked.Increment` LRU sequence + count-based eviction on write + 122B-wins `AddOrUpdate` merge + TTL-aware `TryGet` + BackgroundService stub (eviction loop in 18-03)
- End-to-end header threading: CorrelationMiddleware reads `X-Session-Id` → `ctx.Items[SessionIdKey]` → handler extracts → `mapWireToRequest` populates `req.SessionId`
- Phase 17 selfrouting stub replaced with sticky-or-default closure: `sessionStore.TryGet(req.SessionId)` returns `Some { LastModel = Qwen122B }` → `StickyEscalation/High/Qwen122B`; otherwise `Default/Low/Qwen35B`
- Point B writes wired correctly: non-streaming uses `finalDecision.Target` (post-judge cascade); streaming uses `decision.Target` on normal exit only; OCE + error arms skip

## Task Commits

Each task was committed atomically:

1. **Task 1: Create SessionStore.fs adapter** - `7ce4d22` (feat)
2. **Task 2: Extend CorrelationMiddleware + wire mapWireToRequest** - `326f3c4` (feat)
3. **Task 3: SessionStore DI + appsettings.json + sticky-or-default closure** - `8cda4ff` (feat)
4. **Task 4: Point B session writes both branches** - `e450d92` (feat)

## Files Created/Modified

- `src/SmartRouter.Cli/Adapters/SessionStore.fs` — NEW: ISessionStore + SessionStore class + SessionOptions
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — SessionStore.fs inserted between JudgeClient.fs and DecisionLogger.fs
- `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` — SessionIdKey + SessionIdHeader literals; X-Session-Id read into ctx.Items
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — open SessionStore; sessionId extraction; mapWireToRequest signature; Point B writes in both branches
- `src/SmartRouter.Cli/CompositionRoot.fs` — open SessionStore; unconditional SessionStore DI in configureRequestPipeline + configureWithoutMl; selfrouting sticky-or-default closure
- `src/SmartRouter.Cli/appsettings.json` — Routing.Session.{TtlMinutes=30, MaxEntries=10000} block

## Decisions Made

- **122B-wins merge is non-negotiable correctness invariant**: Two concurrent requests on the same session can race. 35B write must never overwrite 122B escalation. Implemented via `AddOrUpdate` `updateValueFactory` checking `if old.LastModel = Qwen122B || model = Qwen122B then Qwen122B else model`.
- **Point B uses finalDecision.Target**: Quality fallback (Phase 14) and judge cascade (Phase 16) can escalate 35B→122B after initial routing. Recording `initialDecision.Target` would silently break sticky continuation when escalation occurred.
- **Streaming error paths skip session write**: If upstream sent error mid-stream (streamError=true) or client disconnected (OCE), client may not have received complete response — recording that model as "sticky" would be misleading.
- **new keyword required for IDisposable-inheriting class**: SessionStore inherits BackgroundService (IDisposable). F# compiler emits FS0760 with TreatWarningsAsErrors=true when `SessionStore(...)` syntax is used; fixed to `new SessionStore(...)`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] FS0760 warning on SessionStore construction in CompositionRoot**

- **Found during:** Task 3 (CompositionRoot DI registration)
- **Issue:** `SessionStore(opts, sp.GetRequiredService<...>())` in Func lambda emits FS0760 because SessionStore inherits BackgroundService (IDisposable). TreatWarningsAsErrors=true promotes this to a compile error.
- **Fix:** Changed to `new SessionStore(opts, sp.GetRequiredService<...>())` in both `configureRequestPipeline` and `configureWithoutMl` registration sites.
- **Files modified:** `src/SmartRouter.Cli/CompositionRoot.fs`
- **Committed in:** `8cda4ff` (Task 3 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 — compile error fix)
**Impact on plan:** Minor correction. The `new` keyword is the correct F# idiom for IDisposable-inheriting constructors. No scope change.

## Issues Encountered

None beyond the FS0760 deviation documented above.

## User Setup Required

None — no external service configuration required. `Routing.Session` defaults ship in `appsettings.json`; SessionStore is in-process with no external dependencies.

## Next Phase Readiness

- **18-03 ready**: `SessionStore.ExecuteAsync` stub compiles and is registered. 18-03 ships the TTL eviction PeriodicTimer, `AddHostedService<SessionStore>` registration in both configure paths, and `SessionStoreTests.fs` (unit tests for TTL eviction, 122B-wins merge, LRU cap).
- **19-01 ready**: `ISessionStore` is resolvable via DI in CompositionRoot's selfrouting closure. Phase 19's `makeSelfRoutingAlgorithm` will receive `ISessionStore` via the same closure capture pattern and insert self-classify BEFORE the sticky check.
- **20-01 ready**: `SessionStore` is the same in-process store that the Phase 20 fingerprint fallback will consult for single-client identity resolution.
- **Sticky behavior is DORMANT until X-Session-Id traffic arrives**: All 137 baseline tests pass unchanged because no test sends X-Session-Id. Sticky escalation first activates in 18-03's integration tests.

---
*Phase: 18-session-store-and-sticky-escalation*
*Completed: 2026-05-11*
