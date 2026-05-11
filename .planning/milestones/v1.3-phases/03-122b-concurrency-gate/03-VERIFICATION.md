---
phase: 03-122b-concurrency-gate
verified: 2026-05-08T10:57:30Z
status: passed
score: 11/11 must-haves verified
---

# Phase 3: 122B Concurrency Gate — Verification Report

**Phase Goal:** At most one 122B request in flight at any time; high-priority tasks preempt
low-priority ones in the queue; cancellation or upstream hang never leaks the semaphore.

**Verified:** 2026-05-08T10:57:30Z
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Build

| Check | Result |
|-------|--------|
| `dotnet build SmartRouter.slnx` | 0 errors, 0 warnings |

---

## Test Suite

| Run | Result |
|-----|--------|
| `dotnet run --project tests/SmartRouter.Tests` | **39 passed, 2 ignored, 0 failed, 0 errored** |

The 2 ignored entries are `ptestCaseAsync` load tests in `LoadTests.fs` — pending by
design, opt-in only.

---

## Success Criteria

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | Five concurrent 122B-routed requests serialize (one active at a time) — QueueTests fake-upstream | ✓ | `QueueTests.fs:126` — `testCaseAsync "five concurrent 122B requests serialize through SemaphoreSlim(1)"` passes |
| 2 | High-priority `graph_indexing` executes before queued low-priority request | ✓ | `QueueTests.fs:164` — `testCaseAsync "high-priority 122B request preempts queued low-priority"` passes |
| 3 | Cancelling a queued request removes it and leaves `SemaphoreSlim.CurrentCount` unchanged | ✓ | `QueueTests.fs:298` — asserts `CurrentCount == 1`; confirmed passing |
| 4 | Hung upstream beyond configured timeout releases semaphore, next request proceeds | ✓ | `QueueTests.fs:391` — `testCaseAsync "PITFALL-11 hung upstream releases the slot via per-request timeout"` passes |
| 5 | `GET /stats` returns queue depth, active count, avg wait time reflecting live state | ✓ | `QueueTests.fs:508` — in-process Kestrel test asserts all 10 snake_case keys; passes |

---

## Must-Haves — Plan 03-01

| Must-Have | Status | Evidence |
|-----------|--------|----------|
| `IUpstreamClient.CompleteAsync` takes `decision: RoutingDecision` (not `target: ModelId`) | ✓ | `Ports.fs` — signature: `-> decision : RoutingDecision` |
| `IUpstreamClient.StreamAsync` takes `decision: RoutingDecision` | ✓ | `Ports.fs` — signature: `-> decision : RoutingDecision` |
| `src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` exists | ✓ | File present |
| `QueueDispatcher` implements both `IUpstreamClient` and `IStatsProvider` | ✓ | `QueueDispatcher.fs:226` (`interface IUpstreamClient`) and `:332` (`interface IStatsProvider`) |
| 35B bypass on first line of `CompleteAsync` and `StreamAsync` | ✓ | `QueueDispatcher.fs:233` — `match decision.Target with | Qwen35B ->` as first pattern in both methods |
| `SemaphoreSlim(1, 1)` field | ✓ | `QueueDispatcher.fs:83` — `let sem122b = new SemaphoreSlim(1, 1)` |
| Two `Queue<Ticket>` (high/low) with fairness counter | ✓ | `QueueDispatcher.fs` — `let highQueue = Queue<Ticket>()` and `let lowQueue = Queue<Ticket>()` |
| Sub-pattern A: dispatcher acquires semaphore THEN signals TCS (caller does not WaitAsync) | ✓ | `QueueDispatcher.fs:152` — `do! sem122b.WaitAsync(...)` in dispatcher loop before `tcs.TrySetResult` |
| Linked CTS: timeout starts AFTER `enqueue122b` returns, NOT at enqueue time | ✓ | `QueueDispatcher.fs:246,250` — `do! enqueue122b ...` then `new CancellationTokenSource(timeout)` |
| `try ... finally sem122b.Release() \|> ignore` | ✓ | `QueueDispatcher.fs:268-271` (CompleteAsync) and `:321-324` (StreamAsync) |
| `MaxConcurrent122B != 1` rejected at startup with clear error | ✓ | `QueueDispatcher.fs:76-78` — `invalidOp (sprintf "Queue.MaxConcurrent122B must be 1 in v1 ..."` |
| `appsettings.json` has `Queue` section with `FairnessK`, `MaxConcurrent122B`, `PerRequestTimeoutSeconds` | ✓ | `appsettings.json` — `"Queue": { "FairnessK": 10, "MaxConcurrent122B": 1, "PerRequestTimeoutSeconds": 300 }` |
| DI: `AddSingleton<IUpstreamClient>` resolves to `QueueDispatcher` wrapping `QwenUpstreamClient` | ✓ | `CompositionRoot.fs` — `sp.GetRequiredService<QueueDispatcher>() :> IUpstreamClient` |
| DI: `AddSingleton<IStatsProvider>` resolves to same `QueueDispatcher` instance | ✓ | `CompositionRoot.fs` — `sp.GetRequiredService<QueueDispatcher>() :> IStatsProvider` |

---

## Must-Haves — Plan 03-02

| Must-Have | Status | Evidence |
|-----------|--------|----------|
| `src/SmartRouter.Cli/Endpoints/Stats.fs` exists | ✓ | File present |
| `mapEndpoints` exposes `GET /stats` | ✓ | `Stats.fs:43` — `app.MapGet("/stats", ...)` |
| `tests/SmartRouter.Tests/QueueTests.fs` with 9 tests, all wrapped in `testSequenced` | ✓ | 9 `testCaseAsync` entries; `QueueTests.fs:` top-level `testSequenced <\| testList "queue" [...]` |
| Test 3 (fairness): `LatencyFake(30)`, 4H+1L, K=3, asserts `low1Idx == 4` AND `low1Idx < high4Idx` | ✓ | `QueueTests.fs:227` — `LatencyFake(30)`, `low1Idx` and `high4Idx` assertions present; passes |
| Test 5 (post-dequeue cancellation): asserts `Semaphore.CurrentCount == 1` | ✓ | `QueueTests.fs:346` — asserts `CurrentCount == 1`; passes |
| Test 9 (in-process Kestrel): GET `/stats`, `JsonDocument.TryGetProperty` for all 10 snake_case keys | ✓ | `QueueTests.fs:508` — 10 keys asserted: `timestamp`, `active_122b`, `queue_depth_122b_high`, `queue_depth_122b_low`, `active_35b`, `requests_per_sec`, `avg_latency_ms_60s`, `failure_count_total`, `fairness_picks_high`, `fairness_picks_low`; passes |

---

## Must-Haves — Plan 03-03

| Must-Have | Status | Evidence |
|-----------|--------|----------|
| `tests/SmartRouter.Tests/LoadTests.fs` with `ptestCaseAsync` (pending by default) | ✓ | `LoadTests.fs` — 2 `ptestCaseAsync` entries; both ignored in default run |
| Default `dotnet test` count: 39 passed, 2 ignored | ✓ | `dotnet run --project tests/SmartRouter.Tests` → `39 passed, 2 ignored, 0 failed` |

---

## CI / Purity Check

| Check | Status | Evidence |
|-------|--------|----------|
| `./scripts/check-no-async.sh` | ✓ | `OK: no async {} expressions in src/SmartRouter.Core` |
| `SmartRouter.Core` imports no infrastructure namespaces (Serilog, AspNetCore, HttpClient, Extensions.Options) | ✓ | `grep` found no such `open` statements in any `.fs` under `src/SmartRouter.Core/` |

---

## Requirements Coverage

| REQ-ID | Description | Status |
|--------|-------------|--------|
| CONC-01 | At most one 122B request in flight | ✓ SATISFIED — `SemaphoreSlim(1,1)` + serialization test passes |
| CONC-02 | High-priority preempts queued low-priority | ✓ SATISFIED — preemption test passes |
| CONC-03 | Fairness: forced low pick after K consecutive highs | ✓ SATISFIED — starvation/K=3 test passes |
| CONC-04 | 35B requests bypass queue, run concurrently | ✓ SATISFIED — bypass test passes |
| CONC-05 | Cancellation of queued request does not leak semaphore | ✓ SATISFIED — CurrentCount asserted |
| CONC-06 | Post-dequeue mid-acquire cancellation releases slot cleanly | ✓ SATISFIED — CurrentCount == 1 after cancel |
| REL-05 | Hung upstream releases semaphore via timeout | ✓ SATISFIED — PITFALL-11 timeout test passes |
| API-07 | `GET /stats` returns snake_case JSON with required fields | ✓ SATISFIED — 10 keys verified in-process |
| OBS-02 | Live stats snapshot reflects queue depth and active count | ✓ SATISFIED — IStatsProvider.GetSnapshot test passes |
| TEST-04 | QueueTests with controlled fake-upstream latency | ✓ SATISFIED — 9 sequenced tests, all passing |
| TEST-06 | Load tests (pending, opt-in) | ✓ SATISFIED — `ptestCaseAsync` in LoadTests.fs |

---

## Human Verification

None required. All protocol semantics are fully verified by in-process Expecto tests with
controlled fake-upstream latency. Live-curl against real Qwen upstream was out of scope per
phase caveat and is treated as informational only.

---

_Verified: 2026-05-08T10:57:30Z_
_Verifier: Claude (gsd-verifier)_
