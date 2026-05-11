---
phase: 03-122b-concurrency-gate
plan: "01"
subsystem: concurrency
tags: [fsharp, semaphore, priority-queue, cancellation, sse, dotnet]

# Dependency graph
requires:
  - phase: 01-foundation
    provides: IUpstreamClient port, RoutingDecision domain type, QwenUpstreamClient adapter, CompositionRoot DI wiring
  - phase: 02-sse-streaming-pass-through
    provides: ChatCompletions.fs streaming endpoint with enumerator disposal in all exit arms (required by QueueDispatcher streaming semaphore release)
provides:
  - IUpstreamClient port shape changed to take decision: RoutingDecision (Target + Priority in one value)
  - QueueDispatcher adapter: SemaphoreSlim(1) gate, two-level priority queue, fairness counter K=10, linked CTS from slot grant, try/finally Release, 35B bypass
  - IStatsProvider interface + implementation in QueueDispatcher (rolling 60s window)
  - DI graph: QwenUpstreamClient concrete + QueueDispatcher wrapping it + IUpstreamClient and IStatsProvider both delegating to dispatcher
  - appsettings.json Queue section: FairnessK=10, MaxConcurrent122B=1, PerRequestTimeoutSeconds=300
  - Startup validation: MaxConcurrent122B != 1 throws clear error
affects:
  - 03-02 (QueueTests.fs — tests target QueueDispatcher directly)
  - 03-03 (load tests use QueueDispatcher)
  - 04-health-probing (uses IUpstreamClient, now takes RoutingDecision)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Sub-pattern A dispatcher: acquires SemaphoreSlim BEFORE signalling TCS, so individual requests park on tcs.Task not sem.WaitAsync (correct priority honoring)"
    - "Two Queue<Ticket> (high/low) + fairness counter — cleaner than PriorityQueue for two-level with forced fairness"
    - "try/finally Release is always legal in task{} and taskSeq{} because Release() is synchronous (only do! is banned by FS0750)"
    - "Linked CTS created AFTER enqueue122b returns — timeout starts at slot grant, never burns during queue wait"
    - "enqueue122b structural guarantee: try/finally is only entered if enqueue succeeds; cancellation in enqueue never calls Release on unacquired semaphore"

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
  modified:
    - src/SmartRouter.Core/Ports.fs
    - src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/Program.fs
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/appsettings.json
    - tests/SmartRouter.Tests/StreamingTests.fs

key-decisions:
  - "Port shape: IUpstreamClient.CompleteAsync and StreamAsync take decision: RoutingDecision (not target: ModelId) — single seam for QueueDispatcher to dispatch on Target + Priority (CONTEXT.md Option A, locked decision)"
  - "Two Queue<Ticket> (high/low) over PriorityQueue<T,int> — explicit fairness logic, no rebuild-on-promote, cleaner FairnessK enforcement"
  - "Sub-pattern A (dispatcher acquires sem, then signals TCS) — only correct way to honor priority over FIFO; individual requests never call sem.WaitAsync"
  - "Linked CTS timeout starts AFTER enqueue122b returns — queue wait must not burn the upstream timeout budget"
  - "QueueDispatcherOptions MaxConcurrent122B validated at both constructor level and startup (friendlier error message at startup)"
  - "StreamingTests.fs required Queue section in AddInMemoryCollection — QueueDispatcherOptions defaults to 0, which the constructor rejects"

patterns-established:
  - "test config: AddInMemoryCollection must include Queue section when configureServices is called"
  - "DI pattern: concrete adapter registered as singleton first; interface registrations delegate via GetRequiredService<ConcreteType>()"

# Metrics
duration: 15min
completed: 2026-05-08
---

# Phase 03 Plan 01: Queue Dispatcher Summary

**SemaphoreSlim(1) concurrency gate for Qwen 122B: sub-pattern A dispatcher loop + two-level priority queue with fairness K=10 + linked CTS from slot grant + synchronous try/finally Release + 35B bypass wired via port-shape change to RoutingDecision**

## Performance

- **Duration:** ~15 min
- **Started:** 2026-05-08T09:50:00Z
- **Completed:** 2026-05-08T10:02:46Z
- **Tasks:** 2
- **Files modified:** 8 + 1 created

## Accomplishments

- Port-shape refactor lands cleanly: `IUpstreamClient.CompleteAsync` and `StreamAsync` now take `decision: RoutingDecision` instead of `target: ModelId`; all callsites updated mechanically; 30/30 existing tests pass
- `QueueDispatcher.fs` (359 lines) ships the complete atomic correctness cluster: SemaphoreSlim(1,1), two `Queue<Ticket>` (high/low), sub-pattern A dispatcher loop, `enqueue122b` with TCS park, fairness counter K from options, linked CTS after slot grant, `try/finally Release` in `CompleteAsync` and `StreamAsync`, 35B bypass on first line of both methods, `IStatsProvider` with rolling 60s window counters
- DI graph updated: `QwenUpstreamClient` concrete singleton → `QueueDispatcher` wraps it → `IUpstreamClient` and `IStatsProvider` both resolve to dispatcher instance; `MaxConcurrent122B` validated at startup

## Task Commits

1. **Task 1: Port-shape change** - `80f480d` (refactor)
2. **Task 2: QueueDispatcher + DI wiring** - `ad5da00` (feat)

**Plan metadata:** (next commit — docs)

## Files Created/Modified

- `src/SmartRouter.Core/Ports.fs` — IUpstreamClient: `target: ModelId` → `decision: RoutingDecision` for both methods
- `src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` — NEW: QueueDispatcherOptions, StatsSnapshot, IStatsProvider, QueueDispatcher (IUpstreamClient + IStatsProvider)
- `src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` — Updated method signatures; `let target = decision.Target` shim at top of each method body; interface delegation updated
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — Two callsites pass `decision` (not `decision.Target`) to upstream
- `src/SmartRouter.Cli/CompositionRoot.fs` — DI swap: QwenUpstreamClient concrete + QueueDispatcher singleton + IUpstreamClient + IStatsProvider
- `src/SmartRouter.Cli/Program.fs` — Startup validation for MaxConcurrent122B; added opens
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — Compile entry for QueueDispatcher.fs added after QwenUpstreamClient.fs
- `src/SmartRouter.Cli/appsettings.json` — Queue section: FairnessK=10, MaxConcurrent122B=1, PerRequestTimeoutSeconds=300
- `tests/SmartRouter.Tests/StreamingTests.fs` — Queue section added to AddInMemoryCollection in startTestRouter (deviation fix)

## Decisions Made

- Port shape: `IUpstreamClient` takes `decision: RoutingDecision` (CONTEXT.md Option A, locked). QueueDispatcher dispatches on `decision.Target` and `decision.Priority` without a separate interface or AsyncLocal.
- Two `Queue<Ticket>` (high/low) over `PriorityQueue<T,int>`: cleaner two-level fairness logic; no rebuild-on-promote complexity; FairnessK enforcement is explicit and cheap.
- Sub-pattern A: dispatcher acquires `sem122b` BEFORE signalling the ticket's TCS. Individual requests park on `tcs.Task`, not `sem.WaitAsync`. This is the only correct way to honor priority over FIFO (PITFALL-9).
- `try/finally Release` discipline: `sem122b.Release()` is synchronous → always legal in `task {}` and `taskSeq {}` finally blocks (FS0750 only bans `do!`). No workaround needed (unlike Phase 2's `DisposeAsync` issue).
- Linked CTS created AFTER `enqueue122b` returns: timeout starts at slot grant, queue wait never burns the 300s budget (PITFALL-11 variant).

## Pitfall → Code Mitigations

| Pitfall | Code Location | Mitigation |
|---------|--------------|------------|
| PITFALL-8 (semaphore leak on cancel/throw) | `QueueDispatcher.fs` L239–270, L291–327 | `try/finally sem122b.Release()` entered ONLY after `enqueue122b` succeeds; cancellation in enqueue unwinds without entering try |
| PITFALL-9 (FIFO bypasses priority) | `dispatcherLoop`, `enqueue122b` | Dispatcher acquires `sem122b`, then signals TCS (sub-pattern A); requests park on `tcs.Task`, never on `sem.WaitAsync` |
| PITFALL-10 (starvation) | `tryDequeueNext`, `consecutiveHighPicks` | After `FairnessK` consecutive High picks, forces one Low pick if any Low items waiting |
| PITFALL-11 (hung upstream, timeout burns in queue) | `CompleteAsync` L250–257, `StreamAsync` L302–310 | `timeoutCts` and `linkedCts` created AFTER `do! enqueue122b` returns; timeout only counts from slot grant |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] StreamingTests.fs Queue section in AddInMemoryCollection**

- **Found during:** Task 2 verification (running all tests)
- **Issue:** `configureServices` now binds `QueueDispatcherOptions` from the `Queue` config section. The test helper `startTestRouter` uses `AddInMemoryCollection` without the Queue section, so `MaxConcurrent122B` defaults to 0. The `QueueDispatcher` constructor throws `InvalidOperationException` → DI resolution fails → all 8 streaming tests return HTTP 500 instead of the expected response.
- **Fix:** Added `Queue:FairnessK`, `Queue:MaxConcurrent122B`, `Queue:PerRequestTimeoutSeconds` to `AddInMemoryCollection` in `startTestRouter`.
- **Files modified:** `tests/SmartRouter.Tests/StreamingTests.fs`
- **Verification:** All 30 tests pass (30/0/0/0 in Expecto output)
- **Committed in:** `ad5da00` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (Rule 2 — missing config blocks 8 streaming tests)
**Impact on plan:** Essential fix; no scope creep. The QueueDispatcher DI integration test pattern now requires Queue section in any in-process test that calls `configureServices`.

## Issues Encountered

None beyond the StreamingTests deviation above.

## Next Phase Readiness

- `QueueDispatcher` is live in the DI graph; all traffic now flows through it (35B bypasses, 122B queued).
- `IStatsProvider` registered — ready for Plan 03-02 `/stats` endpoint.
- `QueueDispatcher` public properties (`Semaphore`, `QueueDepthHigh`, `QueueDepthLow`, `Active122B`, `FairnessPicksHigh/Low`) expose test observability — `QueueTests.fs` in Plan 03-02 can resolve the concrete `QueueDispatcher` from DI to inspect live state.
- **Plan 03-02** dependency: `QueueTests.fs` needs a `FakeUpstreamClient` with gate-per-call control. The test helper pattern is sketched in RESEARCH.md Focus Area 9. The key difference from StreamingTests is no Kestrel — pure in-process QueueDispatcher unit tests.

---
*Phase: 03-122b-concurrency-gate*
*Completed: 2026-05-08*
