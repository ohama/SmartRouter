---
phase: 18-session-store-and-sticky-escalation
plan: "03"
subsystem: routing-tests-docs
tags: [fsharp, session-store, ttl-eviction, periodic-timer, background-service, sticky-escalation, expecto, di-integration, readme]

# Dependency graph
requires:
  - phase: 18-01
    provides: RouterRequest.SessionId; SessionState BCL-only record; RoutingReason.StickyEscalation DU case; formatReason 8-arm exhaustive match
  - phase: 18-02
    provides: ISessionStore + SessionStore adapter (ConcurrentDictionary + 122B-wins merge + LRU cap + TTL-aware TryGet); CorrelationMiddleware X-Session-Id; CompositionRoot DI triple-reg; sticky-or-default closure; Point B writes

provides:
  - SessionStore.ExecuteAsync PeriodicTimer 5-min TTL eviction loop (SES-08)
  - AddHostedService<SessionStore> in both configureRequestPipeline + configureWithoutMl (triple-reg complete)
  - SessionStoreTests.fs: 8 unit tests for empty-sessionId no-op, 122B-wins merge (sequential + concurrent), TTL eviction (LastAccessedAt mutation), LRU cap eviction
  - StickyEscalationTests.fs: 5 DI-integration tests (testSequenced) for first-122B-then-sticky, stateless-no-header, quality-fallback-writes-session, Hard-Rules-beats-sticky-35B, sticky-persists
  - README §5.1 Stage 3 annotation + §5.6 sticky session escalation subsection
  - README §7 Routing.Session.{TtlMinutes, MaxEntries} configuration table
  - README §9.1 routing_reason=sticky_to_122b enum value (schema_version=1 unchanged)
  - CHANGELOG [Unreleased] Phase 18 ### Added + ### Notes blocks

affects: [19-01, 19-03, 20-01]

# Tech tracking
tech-stack:
  added: []  # BCL only — PeriodicTimer, DateTimeOffset; no new NuGet
  patterns:
    - "PeriodicTimer eviction loop with OCE catch: simple ':? OperationCanceledException -> go <- false' sufficient when no follow-up do! awaits after WaitForNextTickAsync (ExceptionDispatchInfo.Capture only needed when re-throwing OCE through chain of awaits — Phase 8 lesson)"
    - "TTL eviction unit test pattern: LastAccessedAt mutation via ConcurrentDictionary.TryUpdate eliminates sleep — deterministic, fast, CI-safe"
    - "DI-integration test pattern: minimal in-memory IConfiguration omitting Routing:ML → configureRequestPipeline skips ensureEmbeddingFilesPresent → tests pass on hosts without ONNX files (mirrors Phase 17 ModeSwitchTests)"
    - "testSequenced wrapper for DI-integration tests that boot BackgroundServices (PITFALL-27)"
    - "Public accessor for test-visible dictionary state: member _.Store = store (separate assembly cannot access internal members; public is correct here since SessionStore is a concrete type tests import explicitly)"
    - "README §5.X sub-section addition vs. renumber: §5.6 added after §5.5.5 to avoid invalidating §5.4 (Tuning) and §5.5 anchors per CLAUDE.md"

# Key files
key-files:
  created:
    - tests/SmartRouter.Tests/SessionStoreTests.fs
    - tests/SmartRouter.Tests/StickyEscalationTests.fs
  modified:
    - src/SmartRouter.Cli/Adapters/SessionStore.fs  # ExecuteAsync PeriodicTimer body + public Store accessor
    - src/SmartRouter.Cli/CompositionRoot.fs          # 2× AddHostedService<SessionStore>
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj  # 2 new Compile entries before RouterTests.fs
    - tests/SmartRouter.Tests/RouterTests.fs             # 2 new entries in rootTests list
    - README.md                                          # §5.1 + §5.6 + §7 + §9.1
    - CHANGELOG.md                                       # [Unreleased] Phase 18 block

# Commits
commits:
  - hash: 90000e8
    message: "feat(18-03): SessionStore TTL eviction BackgroundService + HostedService triple-reg"
  - hash: aedc0be
    message: "test(18-03): SessionStoreTests + StickyEscalationTests for goal-backward verification"
  - hash: 2b8ae0b
    message: "docs(18-03): README §5/§7/§9.1 + CHANGELOG for Session Store + Sticky Escalation"

# Decisions
decisions:
  - id: D1
    decision: "Simple OCE catch (go <- false) instead of ExceptionDispatchInfo.Capture for eviction loop"
    rationale: "ExceptionDispatchInfo.Capture only needed when re-throwing OCE through chain of awaits to preserve token reference. The eviction for-loop is synchronous — no follow-up do! awaits after WaitForNextTickAsync."
  - id: D2
    decision: "TTL eviction integration test deferred to operator smoke; unit test uses LastAccessedAt mutation"
    rationale: "A 2-minute sleep in CI would be impractical. Unit-level TryGetValue + TryUpdate pattern is deterministic and equivalent for testing the TTL-aware TryGet path."
  - id: D3
    decision: "Public Store accessor instead of internal + InternalsVisibleTo"
    rationale: "F# internal means same-assembly; tests are in a separate project. Adding InternalsVisibleTo is possible but requires awkward do () placement. SessionStore is a concrete type tests import by design — public accessor is correct."
  - id: D4
    decision: "§5.6 chosen over §5.4 for sticky escalation README subsection"
    rationale: "§5.4 already existed (Tuning). CLAUDE.md forbids renumbering. §5.6 follows §5.5.5 without disrupting existing anchors."

# Metrics
metrics:
  duration: "~30 minutes"
  completed: "2026-05-11"
  tests:
    baseline: 137
    new_passing: 13
    total_passing: 150
    ignored: 17
    failed: 0

# Phase 18 CLOSEOUT
closeout:
  all_requirements_satisfied:
    - SES-01: RouterRequest.SessionId field (18-01)
    - SES-02: SessionStore adapter with ConcurrentDictionary + LRU cap (18-02)
    - SES-03: SessionState record + AddOrUpdate 122B-wins merge (18-01 + 18-02)
    - SES-04: CorrelationMiddleware X-Session-Id header threading (18-02)
    - SES-05: mapWireToRequest + sticky cascade stage in routeRequest (18-02)
    - SES-06: StickyEscalation DU case + formatReason arm (18-01)
    - SES-07: Point B writes finalDecision.Target after full cascade (18-02)
    - SES-08: TTL eviction BackgroundService PeriodicTimer (18-03)
    - SES-09: appsettings.json keys + README §7 documentation (18-02 + 18-03)
  roadmap_success_criteria:
    - SC-1: StickyEscalationTests "first request routes 122B + Point B write → second request stickies to 122B"
    - SC-2: StickyEscalationTests "empty SessionId requests share no sticky bucket"
    - SC-3: SessionStoreTests "TryGet returns None for entries older than TtlMinutes" (unit-level TTL)
    - SC-4: StickyEscalationTests "Point B write of Qwen122B (simulating quality fallback) makes next request sticky"
    - SC-5: SessionStoreTests "Concurrent Update: 122B wins over 35B regardless of race order"
  next_phase: Phase 19 — 35B Self-Routing (SR-01..09; SelfRouter inserts self-classify into Stage 3 sticky closure)
---

# Phase 18 Plan 03 Summary: Tests + TTL Eviction + Docs

**One-liner:** TTL eviction PeriodicTimer (SES-08), 13 new passing tests (8 SessionStore unit + 5 StickyEscalation DI-integration), and mandatory README §5/§7/§9.1 sync completing Phase 18.

## What Was Built

**Task 1 — SessionStore.ExecuteAsync TTL eviction + AddHostedService registration**

Replaced the 18-02 stub `ExecuteAsync` (returned `Task.CompletedTask`) with a PeriodicTimer loop that fires every 5 minutes, scans the ConcurrentDictionary for entries with `LastAccessedAt` older than `TtlMinutes`, and removes them atomically via `TryRemove`. OCE catch (`go <- false`) provides clean shutdown. `AddHostedService<SessionStore>` registered in both `configureRequestPipeline` (production) and `configureWithoutMl` (backward-compat), completing the triple-reg pattern.

**Task 2 — SessionStoreTests.fs + StickyEscalationTests.fs**

8 unit tests directly construct `SessionStore` with low TTL/MaxEntries to exercise all invariants deterministically:
- Empty-sessionId no-op (Pitfall 7)
- Basic write/read + unknown-id None
- 122B-wins concurrent merge via `Task.WhenAll` (100×35B + 100×122B races)
- Sequential 122B-wins merge
- TTL eviction via `TryUpdate(LastAccessedAt - 60min)` — no sleep, deterministic
- LRU cap: MaxEntries=3, 4th insert evicts oldest

5 integration tests build a full DI graph via `configureRequestPipeline` with minimal in-memory IConfiguration (no Routing:ML — ML bootstrap skipped per ModeSwitchTests pattern). Tests call `routeRequest` directly to exercise the cascade end-to-end, then assert on `RoutingDecision.Reason` and `Target`.

**Task 3 — README sync (CLAUDE.md mandatory)**

Three trigger areas updated: §5.1 Stage 3 diagram annotated with sticky check; §5.6 new subsection for sticky session escalation (numbered after §5.5.5 to avoid renumbering §5.4 Tuning); §7 Routing.Session configuration table; §9.1 `sticky_to_122b` enum value added. CHANGELOG `[Unreleased]` Phase 18 block added.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing functionality] Public Store accessor instead of internal + InternalsVisibleTo**

- **Found during:** Task 2 compilation (FS0491: member 'Store' is not accessible)
- **Issue:** Plan specified `member internal _.Store` but F# `internal` is assembly-scoped; tests are in a separate project.
- **Fix:** Changed `member internal _.Store/TtlMinutes/MaxEntries` to `member _.Store/TtlMinutes/MaxEntries` (public). Added comment documenting the design intent (test surface, not part of ISessionStore).
- **Files modified:** `src/SmartRouter.Cli/Adapters/SessionStore.fs`
- **Commit:** aedc0be

**2. [Rule 1 - Bug] README §5.4 already existed as "Tuning"**

- **Found during:** Task 3 README writing
- **Issue:** Plan specified adding §5.4 for sticky escalation, but §5.4 "Tuning" already existed from Phase 17.
- **Fix:** Added sticky escalation section as §5.6 (after §5.5.5) preserving all existing anchors per CLAUDE.md "prefer adding sub-section over renumbering" rule.
- **Files modified:** `README.md`
- **Commit:** 2b8ae0b

## Phase 18 Complete

All 9 SES-* requirements satisfied across plans 18-01/02/03. All 5 ROADMAP Success Criteria covered. Test count: 150 passed + 17 ignored + 0 failed. Ready for Phase 19 — 35B Self-Routing.
