# Phase 3: 122B Concurrency Gate - Context

**Gathered:** 2026-05-08
**Status:** Ready for planning

<domain>
## Phase Boundary

Protect Qwen 122B from concurrent overload via a `QueueDispatcher` (Cli adapter wrapping `IUpstreamClient`) that enforces `SemaphoreSlim(1)` + a two-level priority queue (high / low FIFO-within-level), threads cancellation end-to-end, releases the semaphore in all four release paths (success / sync error / async error / cancellation), and exposes `/stats` with live queue depth + counters. 35B-bound traffic bypasses the queue entirely. Streaming AND non-streaming both pass through. Health probing and fallback policy ship in Phase 4, not here.

</domain>

<decisions>
## Implementation Decisions

### Port shape: pass `RoutingDecision` through `IUpstreamClient`
- **User explicitly chose Option A: change `IUpstreamClient.CompleteAsync` and `StreamAsync` to take `decision: RoutingDecision` instead of `target: ModelId`**
- Rationale: `RoutingDecision` already carries `Target` + `Priority` + `Reason` (Core types); single seam; QueueDispatcher dispatches on `decision.Target` + `decision.Priority` without inventing a parallel interface
- This is a port-shape change (Ports.fs in Core); QwenUpstreamClient (Phase 1), ChatCompletions endpoint (Phase 1+2 wiring), and 30 existing tests need their callsites updated to pass `RoutingDecision`
- The change is mechanical: every call to `CompleteAsync req target ct` becomes `CompleteAsync req decision ct`; the endpoint already has `decision` in scope from `Routing.routeRequest routingConfig req`
- Core invariant preserved: `RoutingDecision` is a Core DU; no `IOptions<T>` / Cli types leak into the port

### QueueDispatcher: per-request `TaskCompletionSource` waiter + single dispatcher loop
- Pattern: requests submit a `Ticket { Tcs: TaskCompletionSource<unit>; Priority: Priority; Decision: RoutingDecision; Ct: CancellationToken }` to a queue; a background dispatcher loop drains the queue, acquires the semaphore, signals the Tcs, and the caller proceeds with the upstream call holding the slot
- Two `Queue<Ticket>` instances (high / low) with a fairness counter (K=10) — after 10 consecutive high picks, force one low pick; prevents low-priority starvation
- `FairnessK` exposed in `appsettings.json` (default 10)
- Lives in `src/SmartRouter.Cli/Adapters/QueueDispatcher.fs`; implements both `IUpstreamClient` (the seam Cli composition wires) AND `IStatsProvider` (the seam `/stats` endpoint reads)

### Linked CTS chain
- Per-request `CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, timeoutCts.Token)` where the timeout CTS is configurable (default 300s, matching blueCode 122B cold-start window)
- **Timeout starts AFTER semaphore acquire**, NOT at queue enqueue — queueing time must not burn the timeout budget
- Both inner CTSes disposed in finally; linked CTS disposed last
- All four release paths handled: success / sync exception / async exception / cancellation

### `try/finally Release()` pattern
- `sem.Release()` is synchronous → always legal in `task {}` finally blocks (FS0750 only bans `do!`)
- Phase 2's `DisposeAsync` workaround is NOT needed for semaphore release; just `try ... finally sem.Release() |> ignore`
- For streaming paths: semaphore released in `finally` of the `taskSeq {}` wrapper so the slot lives for the full stream duration; release fires when consumer disposes the enumerator (Phase 2 endpoint already disposes in all exit arms)

### 35B bypass
- First line of `CompleteAsync` and `StreamAsync` in QueueDispatcher: `match decision.Target with | Qwen35B -> inner.CompleteAsync req decision ct | Qwen122B -> queue path`
- 35B traffic never touches semaphore or queue; HttpClient pool is the only concurrency limit

### `/stats` endpoint shape
- Endpoint: `GET /stats` (already in REQUIREMENTS as API-07)
- Backed by an `IStatsProvider` interface (Cli-only); concrete implementation lives inside `QueueDispatcher` so counters are colocated with the events that update them
- Counters: `Interlocked.Increment` for atomic counts (active, requests/sec, failures); rolling-window averages held under a single object lock
- JSON shape (snake_case to match `/v1/models` proxy convention):
  ```json
  {
    "timestamp": "2026-05-08T08:30:00Z",
    "active_122b": 1,
    "queue_depth_122b_high": 2,
    "queue_depth_122b_low": 5,
    "active_35b": 3,
    "requests_per_sec": 4.2,
    "avg_latency_ms_60s": 1820.5,
    "failure_count_total": 7,
    "fairness_picks_high": 47,
    "fairness_picks_low": 4
  }
  ```

### Starvation mitigation: fairness counter (K=10), no aging for v1
- Two-level priority queue + constant high traffic = low-priority starves
- Mitigation: after 10 consecutive high picks, force one low pick (deterministic, simple, no time-tracking)
- Aging (deadline-based promotion) is explicit v2 per PROJECT.md Out of Scope
- `FairnessK` is a config knob so the operator can tune without recompile

### `appsettings.json` additions for Phase 3
```json
{
  "Queue": {
    "FairnessK": 10,
    "MaxConcurrent122B": 1,
    "PerRequestTimeoutSeconds": 300
  }
}
```
- `FairnessK`: low-priority pick after K consecutive high picks
- `MaxConcurrent122B`: hardcoded to 1 in v1 but exposed for future tuning; values >1 will be rejected at startup with a clear error (correctness invariant for current Qwen rig)
- `PerRequestTimeoutSeconds`: linked-CTS timeout; 300s default

### Tests (Plan 03-02)
- `QueueTests.fs` uses a `FakeUpstreamClient(latencyMs)` (gate-per-call pattern); pure in-process, no Kestrel
- 5 tests minimum: serialization (5 concurrent → only 1 active), priority ordering (high preempts queued low), cancellation release (no semaphore leak), timeout release (hung upstream releases slot), `/stats` reflects live state
- Wrapped in `testSequenced` (Console.SetOut + Interlocked counters race otherwise)

### Load tests (Plan 03-03)
- Keep separate from 03-02 — load tests are slower; running them every CI burns time
- Use Expecto `ptestCaseAsync` so they're skipped in normal `dotnet test` runs; opt-in via env var or filter

### Claude's Discretion
- Exact F# module file names beyond `QueueDispatcher.fs`
- Internal data-structure choice for the high/low queues (`Queue<T>` vs `ConcurrentQueue<T>` — depends on whether the dispatcher is single-threaded; if so, plain `Queue<T>` + lock is enough)
- Internal lock granularity (one lock for the dispatcher state vs separate locks for high/low queues)
- Exact rolling-window implementation for `requests_per_sec` (60s sliding window? 60-bucket ring?)
- Exact failure attribution (count any non-2xx upstream as failure, or only 5xx?)

</decisions>

<specifics>
## Specific Ideas

- Mirror blueCode's commit protocol: per-task atomic commits, never `git add .`
- Mirror Phase 2's `testSequenced` discipline (Console.SetOut races otherwise)
- Cancellation chain identical in shape to Phase 2's: `ctx.RequestAborted` flows in as `ct`, plumbed through every read; Phase 3 just adds the linked timeout CTS layer at the QueueDispatcher boundary
- Operator wants `appsettings.json` to be the single tunable surface; `FairnessK` and `MaxConcurrent122B` go there

</specifics>

<deferred>
## Deferred Ideas

- Aging-based priority promotion (deadline triggers low → high) — explicit v2 per PROJECT.md Out of Scope
- Multi-level priority beyond two levels — explicit v2
- Adaptive `MaxConcurrent122B` based on observed RSS — out of scope; current Qwen rig requires hard 1
- Per-task SLOs (different timeouts for `graph_indexing` vs `summary`) — interesting but out of v1 scope; flag for v1.x

</deferred>

---

*Phase: 03-122b-concurrency-gate*
*Context gathered: 2026-05-08*
