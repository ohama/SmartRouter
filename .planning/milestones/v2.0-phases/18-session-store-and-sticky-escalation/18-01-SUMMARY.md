---
phase: 18-session-store-and-sticky-escalation
plan: "01"
subsystem: routing-core
tags: [domain-types, session, routing-reason, decision-logger, fsharp-du]

# Dependency graph
requires:
  - phase: 17-01
    provides: routeRequest 4-stage cascade order locked; Stage 0 Hard Rules; 7-case RoutingReason DU baseline
  - phase: 17-03
    provides: Phase 17 complete; 137+17+0 test baseline; ModeSwitchTests verified cascade ordering
provides:
  - RouterRequest.SessionId : string field (10th field; "" sentinel = stateless request)
  - SessionState BCL-only record in Core (LastModel + LastAccessedAt + mutable LastAccessSeq)
  - RoutingReason.StickyEscalation 8th DU case
  - DecisionLogger.formatReason 8-arm exhaustive match emitting "sticky_to_122b"
affects: [18-02, 18-03, 19-01]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "BCL-only Core type pattern: SessionState in Domain.fs; Cli adapter (ISessionStore) in 18-02 — ARCH-01 preserved"
    - "Empty-string sentinel for optional string fields: SessionId=\"\" = stateless, mirrors CorrelationId=\"\" convention from Phase 9"
    - "Atomic 10-site construction cascade: F# strict-record requires all construction sites updated before build; same pattern as Phase 9 19-site CorrelationId cascade"
    - "No catch-all arm on formatReason: FS0025 forced by TreatWarningsAsErrors=true ensures Phase 19 SelfRoute addition cannot silently emit 'unknown'"

key-files:
  created: []
  modified:
    - src/SmartRouter.Core/Domain.fs
    - src/SmartRouter.Cli/Adapters/DecisionLogger.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - tests/SmartRouter.Tests/HardRulesTests.fs
    - tests/SmartRouter.Tests/MLRoutingTests.fs
    - tests/SmartRouter.Tests/MLLiveVersionTests.fs
    - tests/SmartRouter.Tests/LoadTests.fs
    - tests/SmartRouter.Tests/QueueTests.fs
    - tests/SmartRouter.Tests/ModeSwitchTests.fs

key-decisions:
  - "SessionId is string not option: empty string sentinel matches CorrelationId precedent (Phase 9); avoids wrapper allocation on every request; '' means stateless per SES-04 / Pitfall 7"
  - "SessionState in Core (Domain.fs) not Cli: ISessionStore and SessionStore class carry Serilog/IHostedService; only the data shape belongs in Core for ARCH-01"
  - "mutable LastAccessSeq on SessionState: mirrors Phase 16 JudgeClient.CacheEntry pattern; Interlocked.Increment in Cli adapter not in Core"
  - "ChatCompletions mapWireToRequest still has SessionId=\"\" hardcoded: sessionId parameter threading deferred to 18-02 which wires X-Session-Id from CorrelationMiddleware"
  - "No catch-all arm on formatReason exhaustive match: compiler enforces 9-arm addition when Phase 19 ships SelfRoute DU case"

patterns-established:
  - "SessionState as BCL-only Core type: ARCH-01 preserved via Cli adapter encapsulation (18-02 ships ISessionStore + SessionStore)"
  - "DU case + formatReason arm added atomically in same commit: prevents transient FS0025 build failure between Core and Cli changes"

# Metrics
duration: ~10min
completed: 2026-05-11
---

# Phase 18 Plan 01: Core Domain and DU Summary

**RoutingReason DU extended to 8 cases (StickyEscalation), RouterRequest gains SessionId : string field, BCL-only SessionState record added — compile-time foundation for Phase 18 session store and sticky escalation**

## Performance

- **Duration:** ~10 min
- **Started:** 2026-05-11T15:40:00Z
- **Completed:** 2026-05-11T15:55:00Z
- **Tasks:** 3
- **Files modified:** 9

## Accomplishments

- Domain.fs extended: RouterRequest gains `SessionId : string` (10th field, "" sentinel = stateless), `SessionState` BCL-only record added, `RoutingReason.StickyEscalation` becomes 8th DU case
- DecisionLogger.formatReason updated to 8-arm exhaustive match; `StickyEscalation -> "sticky_to_122b"` — no catch-all; FS0025 will enforce 9th arm when Phase 19 ships SelfRoute
- All 10 RouterRequest construction sites (2 production in ChatCompletions.fs + 8 test files) updated atomically with `SessionId = ""`; solution builds clean; 137 + 17 + 0 baseline preserved
- SES-01 (RouterRequest.SessionId field), SES-03 (SessionState record shape), and SES-06 (StickyEscalation DU + formatReason arm) partially covered — Cli SessionStore adapter lands in 18-02

## Task Commits

Each task was committed atomically:

1. **Task 1: Extend Domain.fs with SessionId field + SessionState record + StickyEscalation DU case** - `9b15db6` (feat)
2. **Task 2: Add 8th formatReason arm in DecisionLogger.fs** - `83d0319` (feat)
3. **Task 3: Add SessionId = "" to all 10 RouterRequest construction sites atomically** - `d2ae1bf` (refactor)

**Plan metadata:** (docs commit follows)

## Files Created/Modified

- `src/SmartRouter.Core/Domain.fs` — StickyEscalation 8th DU case; RouterRequest.SessionId field; SessionState record with LastModel + LastAccessedAt + mutable LastAccessSeq
- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` — formatReason 8-arm exhaustive match; StickyEscalation -> "sticky_to_122b"
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — 2 RouterRequest construction sites carry SessionId = "" (mapWireToRequest + null-body emptyReq)
- `tests/SmartRouter.Tests/HardRulesTests.fs` — mkReq helper updated with SessionId = ""
- `tests/SmartRouter.Tests/MLRoutingTests.fs` — mkReq helper updated with SessionId = ""
- `tests/SmartRouter.Tests/MLLiveVersionTests.fs` — buildRequest helper updated with SessionId = ""
- `tests/SmartRouter.Tests/LoadTests.fs` — emptyRequest updated with SessionId = ""
- `tests/SmartRouter.Tests/QueueTests.fs` — emptyRequest updated with SessionId = ""
- `tests/SmartRouter.Tests/ModeSwitchTests.fs` — 3 inline RouterRequest records updated with SessionId = ""

## Decisions Made

- **SessionId is `string` not `option`**: Empty string sentinel matches `CorrelationId` convention established in Phase 9. Avoids Option wrapper allocation on every request. `""` means stateless per SES-04 / 18-RESEARCH Pitfall 7.
- **SessionState lives in Core (Domain.fs)**: The data shape is BCL-only (`ModelId` + `DateTimeOffset` + `int64`). ARCH-01 preserved — `ISessionStore` interface and `SessionStore` class (carrying Serilog/IHostedService) go in Cli adapter (18-02).
- **mutable LastAccessSeq**: Mirrors Phase 16 JudgeClient.CacheEntry pattern. Mutation via `Interlocked.Increment` happens in the Cli adapter (18-02), not in Core — ARCH-01 preserved.
- **ChatCompletions mapWireToRequest keeps SessionId = "" hardcoded**: Parameter threading for `sessionId` deferred to 18-02 which extends CorrelationMiddleware to extract X-Session-Id and threads it into mapWireToRequest.
- **No catch-all on formatReason**: Exhaustive match is the safety mechanism. Phase 19's `SelfRoute` DU case will force a 9th arm — intentional compile-time gate.
- **Task 2 and Task 1 committed separately**: Task 1 leaves Cli in intentional broken state (FS0025); Task 2 fixes it. Separation documents the dependency cleanly in git history.

## Deviations from Plan

None — plan executed exactly as written. All 10 construction sites identified by research were present at expected locations. No unexpected match sites on RoutingReason found.

## Issues Encountered

None. The flaky 1-failure on first test run was a timing issue in integration tests (TraceLogger drain write race); second run confirmed 137 + 17 + 0 baseline.

## User Setup Required

None — no external service configuration required.

## Next Phase Readiness

- **18-02 can proceed**: `RouterRequest.SessionId` field exists; `SessionState` record in Core; `StickyEscalation` DU case ready for use in cascade Stage 3. Plan 18-02 adds `ISessionStore` interface + `SessionStore` ConcurrentDictionary adapter + CorrelationMiddleware X-Session-Id extraction + Stage 3 closure wiring in CompositionRoot.
- **No blockers**: ARCH-01 preserved; 137-test baseline intact; schema_version=1 unchanged.

---
*Phase: 18-session-store-and-sticky-escalation*
*Completed: 2026-05-11*
