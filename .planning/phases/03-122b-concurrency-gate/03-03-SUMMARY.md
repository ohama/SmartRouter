---
phase: 03-122b-concurrency-gate
plan: 03
subsystem: testing
tags: [expecto, ptestCaseAsync, load-tests, concurrency, QueueDispatcher, F#]

# Dependency graph
requires:
  - phase: 03-122b-concurrency-gate/03-02
    provides: QueueDispatcher with IStatsProvider, QueueTests.fs with FakeUpstreamClient + LatencyFake patterns
provides:
  - tests/SmartRouter.Tests/LoadTests.fs with two ptestCaseAsync burst tests (TEST-06)
  - Opt-in load test mechanism via Expecto pending marker
affects: [future-phases, CI-matrix-documentation]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "ptestCaseAsync opt-in: load tests use Expecto pending marker so default dotnet test skips them; operator flips p->t for one-off runs"
    - "LatencyFakeLoad: private re-declaration of LatencyFake in LoadTests.fs to avoid cross-module coupling; mirrors QueueTests.LatencyFake pattern"
    - "testSequenced wrapper: LoadTests.tests wrapped in testSequenced to prevent concurrent interference with other test modules"

key-files:
  created:
    - tests/SmartRouter.Tests/LoadTests.fs
  modified:
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs

key-decisions:
  - "LatencyFakeLoad re-declared private in LoadTests.fs (not imported from QueueTests) — avoids cross-module coupling; future refactor can extract to Common.fs"
  - "Task.Run lambda cast to :> Task to resolve F# overload ambiguity for Task<Result<_,_>> return type"
  - "ptestCaseAsync chosen over env-var gate — Expecto pending is idiomatic; tooling-friendly (reported as 'ignored' not 'skipped')"

patterns-established:
  - "Load test opt-in: ptestCaseAsync for all burst tests; flip to testCaseAsync for one-off runs"

# Metrics
duration: 3min
completed: 2026-05-08
---

# Phase 3 Plan 03: Load Tests Summary

**Opt-in burst test suite for QueueDispatcher: 20-concurrent serialization cap proof + mixed-priority fairness at scale, using Expecto ptestCaseAsync so default CI stays at 39 tests**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-05-08T01:49:01Z
- **Completed:** 2026-05-08T01:52:40Z
- **Tasks:** 1
- **Files modified:** 3 (1 created, 2 modified)

## Accomplishments

- Created `LoadTests.fs` with two opt-in burst tests proving the QueueDispatcher throughput cap holds under realistic load
- Default `dotnet test` unchanged: 39 passed, 2 ignored, 0 failed
- Verified burst test passes when explicitly enabled: 20-concurrent run completes in ~4s wall-clock (well within 30s budget)

## Load Test Details

### Test 1: 20 concurrent 122B requests maintain at-most-one-in-flight (TEST-06)
- **What it proves:** Strict serialization — each request's start timestamp >= previous request's end timestamp; peak concurrency = 1 across entire burst
- **Configuration:** n=20, latencyMs=50, MaxConcurrent122B=1, FairnessK=10
- **Wall-clock when enabled:** ~4s (20 * 50ms serialized = 1000ms min + dispatcher overhead; well under 30s budget)
- **Assertions:** timestamp ordering for all 19 adjacent pairs; total duration bounds

### Test 2: Mixed-priority burst respects priority order under load (CONC-02 + CONC-03 at scale)
- **What it proves:** With 10 lows enqueued before 10 highs, FairnessK=10 ensures all highs complete before the last low; fairness counter assertions confirm FairnessPicksHigh=10 and FairnessPicksLow>=10
- **Configuration:** 10 lows + 10 highs + 1 occupy = 21 total, latencyMs=30, FairnessK=10
- **Wall-clock when enabled:** ~21 * 30ms = ~630ms serialized
- **Assertions:** maxHighIdx < maxLowIdx (all highs before last low); FairnessPicksHigh == 10; FairnessPicksLow >= 10

## Task Commits

1. **Task 1: Author opt-in LoadTests.fs with ptestCaseAsync burst tests** - `4bb0a82` (test)

**Plan metadata:** (docs commit — see below)

## Files Created/Modified

- `tests/SmartRouter.Tests/LoadTests.fs` — 156 lines; two ptestCaseAsync load tests; private LatencyFakeLoad helper; testSequenced wrapper
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — Added `<Compile Include="LoadTests.fs" />` between QueueTests.fs and RouterTests.fs
- `tests/SmartRouter.Tests/RouterTests.fs` — Appended `SmartRouter.Tests.LoadTests.tests` to rootTests list

## Decisions Made

1. **LatencyFakeLoad re-declared private** — The plan calls for re-declaring a minimal LatencyFake variant in LoadTests.fs rather than importing from QueueTests.fs. This avoids cross-module coupling where a test infrastructure rename would break load tests. Named `LatencyFakeLoad` (not `LatencyFake`) to avoid name clash since both files are in scope during compilation.

2. **Task.Run lambda cast to `:> Task`** — Compiler could not resolve `Task.Run<TResult>(Func<TResult>)` vs `Task.Run<TResult>(Func<Task<TResult>>)` overload when the lambda returns `Task<Result<string,RouterError>>`. Added `:> Task` cast inside the lambda body (same pattern used in QueueTests.fs) to resolve ambiguity.

3. **ptestCaseAsync opt-in mechanism** — Expecto's pending marker is used rather than an env-var gate or separate test project. This keeps load tests co-located with unit tests, makes them easy to find, and produces a clear "2 ignored" in default CI output rather than a confusing "0 run" silence.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Task.Run overload ambiguity in burst test**
- **Found during:** Task 1 (initial build)
- **Issue:** `Task.Run(fun () -> dispatcher.CompleteAsync ...)` failed with FS0041 — F# compiler could not resolve between `Func<TResult>` and `Func<Task<TResult>>` overloads for `Task<Result<string,RouterError>>` return type
- **Fix:** Added `:> Task` cast inside the lambda: `Task.Run(fun () -> dispatcher.CompleteAsync ... :> Task)` — resolves overload unambiguously; `Task.WhenAll` simplified to single call
- **Files modified:** tests/SmartRouter.Tests/LoadTests.fs
- **Verification:** `dotnet build SmartRouter.slnx` clean (0 errors, 0 warnings)
- **Committed in:** 4bb0a82 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 bug/overload resolution)
**Impact on plan:** Single-line fix; no scope creep; identical semantics to plan intent.

## Issues Encountered

None beyond the overload ambiguity auto-fix above.

## Opt-in Mechanism for Operators / CI Matrix

**Default run (`dotnet test`):**
- 39 tests pass, 2 ignored (load tests), 0 failed
- Load tests appear as "ignored" in Expecto output — not failures

**To run load tests:**
- Option A (recommended for one-off): Edit `LoadTests.fs`, change `ptestCaseAsync` to `testCaseAsync`, run `dotnet test`, then revert
- Option B (Expecto CLI args): `dotnet run --project tests/SmartRouter.Tests -- --filter-test-list load`

**CI matrix note:** If a future CI job wants to run load tests on a schedule (e.g., nightly), it can use Option B without modifying source. The `testSequenced` wrapper ensures load tests do not interfere with concurrent test execution.

## Next Phase Readiness

Phase 3 (122B Concurrency Gate) is complete:
- Plan 03-01: QueueDispatcher implementation (CONC-01 through CONC-05 + PITFALL-8/9/10/11)
- Plan 03-02: GET /stats endpoint + 9 QueueTests (OBS-02, API-07)
- Plan 03-03: Load tests — TEST-06 satisfied (burst validates throughput cap)

All 39 tests pass; all Phase 3 must-haves satisfied. Ready to proceed to Phase 4 (health probing + graph_indexing no-fallback rule).

---
*Phase: 03-122b-concurrency-gate*
*Completed: 2026-05-08*
