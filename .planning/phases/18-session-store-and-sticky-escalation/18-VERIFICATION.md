---
phase: 18-session-store-and-sticky-escalation
verified: 2026-05-11T16:42:00Z
status: passed
score: 5/5 must-haves verified
---

# Phase 18: Session Store + Sticky Escalation Verification Report

**Phase Goal:** When a request includes an `X-Session-Id` HTTP header and a previous request in that session routed to 122B, the current request also routes to 122B. Requests without `X-Session-Id` continue working stateless. Session store self-evicts after 30-minute TTL, bounded at 10,000 entries. Infrastructure ready for Phase 19 SelfRouter.
**Verified:** 2026-05-11T16:42:00Z
**Status:** PASSED
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Sticky escalation propagates 122B continuity | VERIFIED | `StickyEscalationTests.fs` SC-1: Hard Rule routes req1 to 122B, Point B write recorded, req2 with same session ID routes to 122B with `StickyEscalation`. Algorithm closure in CompositionRoot lines 506-527 reads `sessionStore.TryGet(req.SessionId)` and returns `{ Target=Qwen122B; Reason=StickyEscalation }` when `LastModel=Qwen122B`. |
| 2 | v1.x clients without X-Session-Id continue stateless | VERIFIED | `StickyEscalationTests.fs` SC-2: empty SessionId `""` → `store.Update("", ...)` is a no-op (Pitfall 7 guard in SessionStore line 115). Second trivial request routes to 35B with `Default` reason. `CorrelationMiddleware` coalesces absent/whitespace header to `""` sentinel (line 51). |
| 3 | Session TTL eviction self-heals | VERIFIED | `SessionStoreTests.fs` TTL test: entry's `LastAccessedAt` backdated 60 minutes; `TryGet` returns `None`. `SessionStore.ExecuteAsync` (lines 131-154) uses `task {}` PeriodicTimer 5-minute sweep, removing entries older than `ttlMinutes`. Registered as `AddHostedService` in both `configureRequestPipeline` (line 441) and `configureWithoutMl` (line 1138). |
| 4 | Quality fallback updates session store | VERIFIED | `StickyEscalationTests.fs` SC-4: req1 routes to 35B; Point B write of `Qwen122B` (simulating quality fallback); req2 routes to 122B with `StickyEscalation`. ChatCompletions non-streaming Point B (line 632-633) writes `finalDecision.Target` (not `initialDecision.Target`) — correctly captures escalation result. |
| 5 | Concurrent-write 122B-wins merge | VERIFIED | `SessionStoreTests.fs`: 100 concurrent 35B writes + 100 concurrent 122B writes on same session via `Task.WaitAll`; final `LastModel` asserts to `Qwen122B`. `AddOrUpdate` factory (SessionStore lines 85-93) explicitly: if old or new is `Qwen122B`, keep `Qwen122B`. Test suite: 150 passed, 17 ignored, 0 failed. |

**Score:** 5/5 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/SmartRouter.Core/Domain.fs` | RouterRequest.SessionId field, SessionState record, RoutingReason.StickyEscalation | VERIFIED | Line 61: `SessionId : string`; lines 72-75: `SessionState` BCL-only record; line 39: `\| StickyEscalation`. No Microsoft.Extensions/Serilog/ConcurrentDictionary imports (ARCH-01 preserved). |
| `src/SmartRouter.Cli/Adapters/SessionStore.fs` | ISessionStore + SessionStore + 122B-wins AddOrUpdate + TTL TryGet + LRU eviction | VERIFIED | 162 lines, substantive. `ISessionStore` interface (lines 17-26). `SessionStore` inherits `BackgroundService` (line 55). 122B-wins merge (lines 85-93). TTL check in TryGet (lines 101-110). LRU eviction on write (lines 65-72). |
| `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` | SessionIdKey/SessionIdHeader literals + X-Session-Id read into Items | VERIFIED | 62 lines. `SessionIdKey = "SessionId"` (line 17). `SessionIdHeader = "X-Session-Id"` (line 28). Header read with null/whitespace coalescing to `""` (lines 49-53). |
| `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` | mapWireToRequest takes sessionId; Point B write after finalDecision | VERIFIED | `mapWireToRequest (correlationId: string) (sessionId: string)` (line 94). `SessionId = sessionId` (line 119). Point B non-streaming (lines 632-633): `sessionStore.Update(req.SessionId, finalDecision.Target)`. Point B streaming (lines 409-410): `sessionStore.Update(req.SessionId, decision.Target)`. |
| `src/SmartRouter.Cli/CompositionRoot.fs` | SessionStore singleton + ISessionStore alias + AddHostedService in both configure functions; sticky closure | VERIFIED | Both `configureRequestPipeline` (lines 430-442) and `configureWithoutMl` (lines 1128-1139) register triple-pattern. Sticky algorithm closure (lines 506-527) reads `sessionStore.TryGet(req.SessionId)`. |
| `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` | formatReason with StickyEscalation → "sticky_to_122b" | VERIFIED | Line 39: `\| StickyEscalation -> "sticky_to_122b"`. Exhaustive 8-arm match (no catch-all). Build succeeds with 0 warnings under TreatWarningsAsErrors=true. |
| `src/SmartRouter.Cli/appsettings.json` | Routing.Session.TtlMinutes=30, MaxEntries=10000 | VERIFIED | Lines 15-18: `"Session": { "TtlMinutes": 30, "MaxEntries": 10000 }` present in `Routing` section. |
| `tests/SmartRouter.Tests/SessionStoreTests.fs` | Unit tests including Task.WhenAll concurrent 122B-wins | VERIFIED | 102 lines, 8 test cases. Includes: empty-sessionId no-op, basic write/read, concurrent 100+100 Task.WaitAll, sequential merge, TTL expiry, LRU cap eviction. |
| `tests/SmartRouter.Tests/StickyEscalationTests.fs` | Integration tests: sticky-after-hard-rule, stateless-no-header, quality-fallback, hard-rules-beats-sticky, multi-request persistence | VERIFIED | 243 lines, 5 integration test cases via full DI graph (configureRequestPipeline). All 5 Phase 18 ROADMAP success criteria exercised. |
| `README.md` | §5.6 sticky cascade, §7 Routing.Session config, §9.1 sticky_to_122b in routing_reason | VERIFIED | §5.6 exists at line 272. Routing.Session table at lines 388-395. `sticky_to_122b` in `routing_reason` enum at line 575. `schema_version=1` unchanged. |
| `CHANGELOG.md` | [Unreleased] ### Added Phase 18 entry | VERIFIED | Lines 34-74: `### Added (Phase 18 — Session Store + Sticky Escalation)` with full description of all Phase 18 additions. |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `CorrelationMiddleware` | `HttpContext.Items["SessionId"]` | `ctx.Items.[SessionIdKey] <- sessionId` | WIRED | Line 53 of CorrelationMiddleware.fs |
| `ChatCompletions.handler` | `RouterRequest.SessionId` | `ctx.Items.TryGetValue(SessionIdKey)` + `mapWireToRequest correlationId sessionId` | WIRED | Lines 219-221, 247 of ChatCompletions.fs |
| `CompositionRoot selfrouting closure` | `ISessionStore.TryGet` | `sessionStore.TryGet(req.SessionId)` | WIRED | Line 508 of CompositionRoot.fs |
| `ChatCompletions.handler` (non-streaming Point B) | `ISessionStore.Update` | `sessionStore.Update(req.SessionId, finalDecision.Target)` | WIRED | Lines 632-633 of ChatCompletions.fs — correctly uses `finalDecision.Target` |
| `ChatCompletions.handler` (streaming Point B) | `ISessionStore.Update` | `sessionStore.Update(req.SessionId, decision.Target)` | WIRED | Lines 409-410 of ChatCompletions.fs — streaming has no quality fallback so `decision.Target` is final |
| `SessionStore.ExecuteAsync` | TTL eviction loop | `BackgroundService` + `AddHostedService<SessionStore>` | WIRED | Registered in both pipeline functions; `task {}` pattern (ARCH-02 compliant) |
| `DecisionLogger.formatReason` | `RoutingReason.StickyEscalation` | 8-arm exhaustive match | WIRED | Line 39 of DecisionLogger.fs |

### Requirements Coverage

| Requirement | Status | Evidence |
|-------------|--------|----------|
| SES-01: RouterRequest.SessionId field | IMPLEMENTED | Domain.fs line 61 |
| SES-02: SessionStore ConcurrentDictionary + LRU + TTL | IMPLEMENTED | SessionStore.fs; LRU cap in doUpdate; TTL-aware TryGet |
| SES-03: SessionState record + 122B-wins AddOrUpdate merge | IMPLEMENTED | Domain.fs lines 72-75 (BCL-only record); SessionStore.fs lines 85-93 (merge logic) |
| SES-04: CorrelationMiddleware reads X-Session-Id header | IMPLEMENTED | CorrelationMiddleware.fs lines 49-53 |
| SES-05: mapWireToRequest populates SessionId; sticky cascade in Stage 3 | IMPLEMENTED | ChatCompletions.fs line 119; CompositionRoot.fs selfrouting closure lines 506-527. Note: requirement text says "AFTER self-classify" — Phase 19 self-classify not yet present, but stub position is correct for Phase 18. |
| SES-06: RoutingReason.StickyEscalation 8th DU case; formatReason "sticky_to_122b" | IMPLEMENTED | Domain.fs line 39; DecisionLogger.fs line 39 |
| SES-07: Quality fallback writes session store; finalDecision.Target | IMPLEMENTED | ChatCompletions.fs lines 632-633. Minor: requirement says write happens "AFTER decisionLogger.Log" but code writes to session BEFORE decisionLogger.Log (lines 632-637). Ordering is functionally correct — no causality dependency. |
| SES-08: TTL BackgroundService PeriodicTimer 5-minute sweep | IMPLEMENTED | SessionStore.fs lines 130-154; `task {}` compliant (ARCH-02); OperationCanceledException handled |
| SES-09: appsettings.json Session.{TtlMinutes,MaxEntries} keys | IMPLEMENTED | appsettings.json lines 15-18; CLIMutable SessionOptions with explicit mutable fields |

### Anti-Patterns Found

None found in key Phase 18 files. No TODO/FIXME/placeholder stubs. No empty implementations. Build: 0 warnings, 0 errors (TreatWarningsAsErrors=true enforced). Test run: 150 passed, 17 ignored, 0 failed.

### Architectural Invariants

| Invariant | Status | Evidence |
|-----------|--------|---------|
| ARCH-01: Core BCL-only | PRESERVED | Domain.fs has no `open` statements — BCL types only (`System.DateTimeOffset`, etc.). `SessionState` and `SessionId` are BCL-only types. ConcurrentDictionary held in Cli adapter only. |
| ARCH-02: task{} only | PRESERVED | SessionStore.ExecuteAsync line 131 uses `task {`. No `async {}` literals found in SessionStore.fs. |
| TreatWarningsAsErrors / FS0025 | PRESERVED | formatReason is an 8-arm exhaustive match with no catch-all; build succeeded 0 warnings. |
| schema_version=1 | PRESERVED | README §9.1 states schema_version=1 unchanged; `sticky_to_122b` is additive enum value. |

### Deviations from Plan Claims

1. **SES-07 ordering deviation (non-functional):** The requirement text says `sessionStore.Update` should happen "AFTER decisionLogger.Log". The implementation writes to the session store BEFORE `decisionLogger.Log` (ChatCompletions.fs lines 632-637). There is no functional dependency between the two operations, and all tests pass. This is a spec/implementation text divergence with no observable behavior difference.

2. **SES-05 self-classify placement:** The requirement mentions the sticky stage runs "AFTER self-classify". Phase 19 self-classify is not yet implemented (by design for Phase 18). The sticky check correctly occupies the pre-self-classify position in the algorithm closure, ready for Phase 19 to prepend self-classify before it.

3. **SessionTtlEvictionService naming:** The requirement/plan references `SessionTtlEvictionService` as a distinct BackgroundService class name; the implementation folds TTL eviction into the `SessionStore` class itself (which inherits `BackgroundService`). This is functionally equivalent and matches the 18-03 plan's stated "triple-reg pattern mirrors DecisionLogWriter" approach.

### Human Verification Required

None. All Phase 18 behaviors are fully verifiable via code inspection and automated tests. Test run confirms 150 passed, 0 failed.

---

_Verified: 2026-05-11T16:42:00Z_
_Verifier: Claude (gsd-verifier)_
