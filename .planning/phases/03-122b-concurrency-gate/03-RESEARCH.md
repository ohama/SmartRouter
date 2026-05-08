# Phase 3: 122B Concurrency Gate — Research

**Phase:** 03-122b-concurrency-gate
**Researched:** 2026-05-08
**Confidence:** HIGH (grounded in actual Phase 1 + 2 codebase + PITFALLS.md + .NET BCL documentation)

---

## Grounding: What Phase 3 Builds On

### Actual seam (confirmed from source)

`IUpstreamClient` in `Ports.fs` (lines 14–29):

```fsharp
type IUpstreamClient =
    abstract member CompleteAsync :
        req    : RouterRequest
        -> target : ModelId
        -> ct     : CancellationToken
        -> Task<Result<string, RouterError>>

    abstract member StreamAsync :
        req    : RouterRequest
        -> target : ModelId
        -> ct     : CancellationToken
        -> IAsyncEnumerable<Result<string, RouterError>>
```

`QueueDispatcher` receives `target: ModelId` — NOT `RoutingDecision`. The 35B bypass therefore inspects the `target` parameter directly. `Priority` comes from the caller, which already resolved the `RoutingDecision` from `Routing.routeRequest`. The endpoint passes both `target` and `priority` into the queue.

**Critical gap:** `IUpstreamClient` only exposes `target: ModelId`, not `priority: Priority`. The dispatcher needs `Priority` at enqueue time. Solution: the `QueueDispatcher` methods need an extra `priority` parameter, OR the `QueueDispatcher` wraps the interface differently — see Focus Area 1 below.

### CompositionRoot single-line change (confirmed from source)

`CompositionRoot.fs` lines 140–145:
```fsharp
// Phase 1 (current):
services.AddSingleton<IUpstreamClient>(fun sp ->
    QwenUpstreamClient(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IOptions<UpstreamOptions>>())
    :> IUpstreamClient)
```

Phase 3 replaces this with one wrapping call. Everything else in `CompositionRoot.fs` stays the same.

### ChatCompletions.fs — what the endpoint already does

The endpoint already holds the full `RoutingDecision` at dispatch time:
```fsharp
| Ok decision ->
    if req.Stream then
        let ct = ctx.RequestAborted
        let chunks = upstream.StreamAsync req decision.Target ct
```

`decision.Priority` is available here. The endpoint can thread `priority` through the queue. This means `QueueDispatcher` should expose a parallel interface OR the `CompleteAsync`/`StreamAsync` calls embed priority via a different mechanism.

**Recommended solution:** `QueueDispatcher` wraps `IUpstreamClient` but adds an internal `EnqueueAsync` method. The endpoint calls `IUpstreamClient` normally — but the dispatcher infers priority from the `target + routingConfig` lookup... No: that repeats Core logic.

**Cleanest solution (recommended):** Define a separate `IQueueDispatcher` marker interface in the adapter layer that extends `IUpstreamClient` with a priority-aware call. The endpoint casts to `IQueueDispatcher` when it has a `RoutingDecision`, falls back to direct call otherwise. But this leaks adapter details into the endpoint.

**Simplest correct solution (recommended for v1):** `QueueDispatcher` inspects the `ModelId` parameter to gate 122B vs passthrough. For priority, it reads `RoutingDecision.Priority` by requiring the endpoint to pass it through a `AsyncLocal<Priority>` or a thread-local. This is ugly.

**Actually cleanest solution:** Expand `IUpstreamClient` with an optional overload, OR — per PROJECT.md "QueueDispatcher lives in Cli adapter, NOT in Core" — define a concrete `QueueDispatcher` class that the endpoint resolves directly via DI. The endpoint knows about `QueueDispatcher` and calls its priority-aware methods; it still implements `IUpstreamClient` for the non-concurrent path.

**Final recommendation (code-shaped):** See Focus Area 1. The right answer is: `QueueDispatcher` implements `IUpstreamClient`, but internally, the methods accept the full `RoutingDecision` (not just `ModelId`). The endpoint resolves `IUpstreamClient` from DI, but also casts to `IQueueDispatcher` (a Cli-only interface, not in Core) to call the priority-aware overloads. Registration: `AddSingleton<QueueDispatcher>` + `AddSingleton<IUpstreamClient>(sp -> sp.GetRequiredService<QueueDispatcher>())`. The `ChatCompletions` endpoint resolves `QueueDispatcher` directly from DI via `GetRequiredService<QueueDispatcher>()` and calls the priority-aware `CompleteWithDecision` / `StreamWithDecision` methods.

Actually, the simpler path: **thread `Priority` as an `AsyncLocal<Priority>`** set in the endpoint before calling into `IUpstreamClient`. `QueueDispatcher.CompleteAsync` and `StreamAsync` read it. This keeps `IUpstreamClient` unchanged. But `AsyncLocal` is invisible in tests.

**Cleanest for testability (final recommendation):** Add a Cli-only interface `IQueueAwareUpstreamClient` that extends `IUpstreamClient` with priority-aware variants:

```fsharp
// In SmartRouter.Cli (NOT Core)
type IQueueAwareUpstreamClient =
    inherit IUpstreamClient
    abstract member CompleteWithPriorityAsync :
        req      : RouterRequest
        -> target   : ModelId
        -> priority : Priority
        -> ct       : CancellationToken
        -> Task<Result<string, RouterError>>
    abstract member StreamWithPriority :
        req      : RouterRequest
        -> target   : ModelId
        -> priority : Priority
        -> ct       : CancellationToken
        -> IAsyncEnumerable<Result<string, RouterError>>
```

`ChatCompletions.fs` resolves `IQueueAwareUpstreamClient` from DI instead of `IUpstreamClient`. `QueueDispatcher` implements `IQueueAwareUpstreamClient`. The `IUpstreamClient` methods delegate to the priority-aware variants with `Low` as the default priority (safe fallback for callers that don't know about priority). `CompositionRoot` registers `QueueDispatcher` as both `IUpstreamClient` and `IQueueAwareUpstreamClient`.

This keeps Core clean, makes tests explicit about priority, and avoids `AsyncLocal`.

---

## Focus Area 1: QueueDispatcher Implementation Pattern

### Recommended Pattern: Per-Request TCS Waiter + Single Dispatcher Loop

The recommended pattern uses one background dispatcher loop that drains a `PriorityQueue<QueueEntry, int>` and signals individual requests via `TaskCompletionSource`. Each request parks on its own TCS, not on `SemaphoreSlim.WaitAsync` directly. This is the only way to implement true priority ordering (PITFALL-9: raw `SemaphoreSlim.WaitAsync` is FIFO and bypasses any external priority structure).

**Key insight from PITFALL-9:** The `SemaphoreSlim` is acquired by the *dispatcher loop*, not by individual request tasks. Individual request tasks wait on their `TCS.Task`. The dispatcher dequeues the highest-priority waiting request and signals its TCS. Only then does the dispatcher (or the signaled request) acquire the semaphore.

**Two sub-patterns within this approach:**

**Sub-pattern A (dispatcher acquires, then signals):**
```
Dispatcher loop:
  1. Peek at highest-priority queued item
  2. Acquire SemaphoreSlim(1)           ← blocks until slot free
  3. Dequeue the item
  4. Signal item.Tcs.SetResult()        ← unblocks the waiting request
  5. The request runs its upstream call
  6. Request calls Release() in finally
```

**Sub-pattern B (dispatcher signals, request acquires):**
```
Dispatcher loop:
  1. Dequeue highest-priority item
  2. Signal item.Tcs.SetResult()        ← unblocks the waiting request
  3. The request calls SemaphoreSlim.WaitAsync(ct)   ← blocks
  4. Request runs upstream call
  5. Request calls Release() in finally
```

Sub-pattern B reintroduces the FIFO problem if two requests are signaled in quick succession. Sub-pattern A is correct but means the dispatcher holds the semaphore while the request runs. **Sub-pattern A is recommended.**

**Alternative (two-channel approach, NOT recommended):**
`Channel<QueueEntry>` × 2 (high/low), with a dispatcher that selects from the high channel first. Naturally thread-safe, but introduces starvation if high-priority channel is never empty (PITFALL-10). Harder to implement aging. Rejected for v1.

### F# Code Skeleton (Recommended: Sub-pattern A)

```fsharp
module SmartRouter.Cli.Adapters.QueueDispatcher

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open Serilog
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports

// ── Options ──────────────────────────────────────────────────────────────────

[<CLIMutable>]
type QueueDispatcherOptions =
    { TimeoutSeconds    : int    // per-request timeout from semaphore acquire (default: 300)
      MaxWaitMs         : int    // aging threshold: promote to High after this many ms (default: 30000)
      FairnessK         : int    // after K consecutive High picks, force one Low (default: 10)
    }

// ── Queue entry ───────────────────────────────────────────────────────────────

type private QueueEntry =
    { RequestId  : Guid
      Priority   : int           // 0 = High, 1 = Low (PriorityQueue min-heap: smaller = higher priority)
      EnqueuedAt : DateTimeOffset
      Tcs        : TaskCompletionSource<unit>
      Ct         : CancellationToken }

// ── QueueDispatcher ───────────────────────────────────────────────────────────

type QueueDispatcher(inner: IUpstreamClient, options: QueueDispatcherOptions) =

    // One semaphore for all 122B calls. SemaphoreSlim(1,1) = at most 1 in flight.
    let sem122b = new SemaphoreSlim(1, 1)

    // The priority queue. NOT thread-safe — protected by queueLock.
    // PriorityQueue<entry, priority-int> is a min-heap: 0 (High) dequeued before 1 (Low).
    let pq      = PriorityQueue<QueueEntry, int>()
    let queueLock = obj()

    // Signaling semaphore: dispatcher loop sleeps here; enqueue wakes it.
    // Initial count=0 so dispatcher parks until first request arrives.
    let signal  = new SemaphoreSlim(0)

    // Counters for /stats
    let mutable activeCount = 0
    let mutable totalEnqueued = 0L
    let mutable totalCompleted = 0L
    let mutable consecutiveHighPicks = 0

    // ── Dispatcher loop (background) ─────────────────────────────────────────

    let dispatcherLoop () = task {
        while true do
            // Park until something is enqueued
            do! signal.WaitAsync()

            // Pick the next entry, respecting priority + aging + fairness
            let entry =
                lock queueLock (fun () ->
                    if pq.Count = 0 then None
                    else
                        // Aging: scan queue; promote any entry waiting > MaxWaitMs to High
                        // Note: PriorityQueue does not support in-place update, so promotion
                        // is implemented by rebuilding the queue after aging scan.
                        // For v1 simplicity, use fairness counter instead (see Focus Area 8).

                        // Fairness: if consecutiveHighPicks >= FairnessK, force a Low pick
                        let forceLow = consecutiveHighPicks >= options.FairnessK

                        // Peek at top item
                        let mutable entry, prio = pq.Peek()
                        if forceLow && prio = 0 then
                            // Try to find a Low-priority item; if none exists, take the High
                            // Rebuilding queue is expensive — use a separate low queue instead
                            // (see PriorityQueue note below). For v1 simplicity: do not force
                            // if no Low items exist.
                            entry <- pq.Dequeue()
                        else
                            entry <- pq.Dequeue()

                        if entry.Priority = 0 then
                            consecutiveHighPicks <- consecutiveHighPicks + 1
                        else
                            consecutiveHighPicks <- 0

                        Some entry)

            match entry with
            | None -> ()
            | Some e ->
                // Check if this entry's CT already fired (client disconnected while queued)
                if e.Ct.IsCancellationRequested then
                    // Discard silently — no semaphore acquired, no Release needed
                    Log.Information("QueueDispatcher: discarding cancelled queued request {Id}", e.RequestId)
                else
                    // Acquire the semaphore — this blocks until the current 122B call finishes
                    do! sem122b.WaitAsync(CancellationToken.None)
                    // NOTE: We use CancellationToken.None here because the dispatcher must
                    // not be cancelled by a per-request token. If e.Ct fires AFTER acquire,
                    // the upstream call will propagate it and still Release() in finally.
                    Interlocked.Increment(&activeCount) |> ignore
                    // Signal the waiting request — it will call the upstream and Release()
                    e.Tcs.TrySetResult() |> ignore
    }

    // Start dispatcher loop as a background fire-and-forget Task
    do Task.Run(fun () -> dispatcherLoop () :> Task) |> ignore

    // ── 122B enqueue helper ───────────────────────────────────────────────────

    let enqueue122b (priority: Priority) (ct: CancellationToken) : Task<unit> =
        task {
            let prio = match priority with High -> 0 | Low -> 1
            let tcs  = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let entry =
                { RequestId  = Guid.NewGuid()
                  Priority   = prio
                  EnqueuedAt = DateTimeOffset.UtcNow
                  Tcs        = tcs
                  Ct         = ct }

            lock queueLock (fun () ->
                pq.Enqueue(entry, prio)
                Interlocked.Increment(&totalEnqueued) |> ignore)

            signal.Release() |> ignore

            // Park here until dispatcher grants the slot (signals our TCS)
            // If ct fires while parked, TrySetCanceled causes the task to throw
            // OperationCanceledException here, unwinding the caller's task.
            use reg = ct.Register(fun () -> tcs.TrySetCanceled(ct) |> ignore)
            do! tcs.Task
            // At this point: sem122b is held by the dispatcher and the slot is ours.
            // The dispatcher has already called sem122b.WaitAsync. We must Release() in finally.
        }

    // ── IUpstreamClient implementation ────────────────────────────────────────

    member _.QueueDepth = lock queueLock (fun () -> pq.Count)
    member _.ActiveCount = activeCount
    member _.Semaphore = sem122b

    member _.CompleteWithPriorityAsync
        (req: RouterRequest)
        (target: ModelId)
        (priority: Priority)
        (ct: CancellationToken) : Task<Result<string, RouterError>> =
        task {
            match target with
            | Qwen35B ->
                // 35B: no gate, call directly
                return! inner.CompleteAsync req target ct
            | Qwen122B ->
                // 1. Build linked CTS: client abort OR per-request timeout
                use timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(float options.TimeoutSeconds))
                use linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
                let linkedCt   = linkedCts.Token

                // 2. Wait for dispatcher to grant the slot
                //    If linkedCt fires while queued: OperationCanceledException — no semaphore held
                do! enqueue122b priority linkedCt

                // 3. Semaphore is now held. Run upstream in try/finally.
                try
                    Interlocked.Increment(&activeCount) |> ignore
                    return! inner.CompleteAsync req target linkedCt
                finally
                    sem122b.Release() |> ignore
                    Interlocked.Decrement(&activeCount) |> ignore
                    Interlocked.Increment(&totalCompleted) |> ignore
        }

    member this.StreamWithPriority
        (req: RouterRequest)
        (target: ModelId)
        (priority: Priority)
        (ct: CancellationToken) : IAsyncEnumerable<Result<string, RouterError>> =
        match target with
        | Qwen35B ->
            inner.StreamAsync req target ct
        | Qwen122B ->
            taskSeq {
                use timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(float options.TimeoutSeconds))
                use linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
                let linkedCt   = linkedCts.Token

                do! enqueue122b priority linkedCt
                // Semaphore held at this point
                try
                    let mutable released = false
                    try
                        for item in inner.StreamAsync req target linkedCt do
                            yield item
                    finally
                        // F# taskSeq finally runs synchronously — Release() is sync, so this is safe
                        if not released then
                            released <- true
                            sem122b.Release() |> ignore
                            Interlocked.Decrement(&activeCount) |> ignore
                with ex ->
                    sem122b.Release() |> ignore
                    Interlocked.Decrement(&activeCount) |> ignore
                    yield Error (ModelUnavailable (Qwen122B, ex.Message))
            }

    // IUpstreamClient fallback implementations (use Low priority — safe default)
    interface IUpstreamClient with
        member this.CompleteAsync req target ct =
            this.CompleteWithPriorityAsync req target Low ct
        member this.StreamWithPriority req target ct =
            this.StreamWithPriority req target Low ct
```

**Important note on the `StreamAsync`/`StreamWithPriority` try/finally in `taskSeq {}`:** The F# `taskSeq {}` computation expression (from `FSharp.Control.TaskSeq`) supports `try/finally` with synchronous `finally` blocks. `sem122b.Release()` is synchronous, so `finally` is safe here. This is different from `task {}` where `do!` in `finally` is rejected (FS0750). The `taskSeq {}` limitation only applies to `do!` (async operations) in `finally` — synchronous calls are fine.

**On `task {}` try/finally (Focus Area 4 crossover):** In `CompleteWithPriorityAsync`, the `try/finally` wraps a `task {}` expression. The `finally` block calls `sem122b.Release()` synchronously — this is always safe in `task {}` because `finally` allows synchronous operations; only `do!` (async awaits) inside `finally` are rejected by the compiler.

---

## Focus Area 2: Priority Queue Data Structure

### Recommended: `System.Collections.Generic.PriorityQueue<T, int>` + External Lock

**Rationale:**
- In-box since .NET 6. No package needed. `SmartRouter.Core.fsproj` stays unchanged.
- Min-heap by priority value: `0` = High, `1` = Low. Lower number dequeued first.
- NOT thread-safe by default — requires `lock queueLock` on all `Enqueue`/`Dequeue`/`TryDequeue`/`Peek` calls. Lock is held only during queue operations (microseconds), not during upstream calls.

**F# type definitions:**

```fsharp
// Priority encoding (int → PriorityQueue min-heap semantic)
let private highPrio = 0
let private lowPrio  = 1

let private priorityToInt = function
    | High -> highPrio
    | Low  -> lowPrio

// The queue instance
let private pq       = PriorityQueue<QueueEntry, int>()
let private queueLock = obj()

// Thread-safe enqueue
let private enqueueEntry (entry: QueueEntry) =
    lock queueLock (fun () ->
        pq.Enqueue(entry, priorityToInt entry.Priority))

// Thread-safe dequeue (returns None if empty)
let private tryDequeueEntry () =
    lock queueLock (fun () ->
        let mutable item = Unchecked.defaultof<QueueEntry>
        let mutable prio = 0
        if pq.TryDequeue(&item, &prio) then Some item
        else None)

// Queue depth (for /stats)
let private queueDepth () =
    lock queueLock (fun () -> pq.Count)
```

**Why not two `Queue<T>` instances (high + low):** More code for same result. The `PriorityQueue` unifies aging/promotion (change priority int) and priority in one structure. The lock cost is identical.

**Why not `MailboxProcessor`:** F# agents are thread-safe and can implement priority via internal inspection of the mailbox, but they don't integrate cleanly with `task {}` patterns. The `PostAndAsyncReply` roundtrip adds latency. The `PriorityQueue + lock` approach is simpler and performs better.

---

## Focus Area 3: Linked CTS Chain

### Code Shape

```fsharp
// Per-request CTS chain. Timeout counts from SEMAPHORE ACQUIRE (see rationale below).
// Built inside the dispatch call, not at enqueue time.

// WRONG (timeout burns during queue wait):
// use timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(300.0))
// do! enqueue122b priority timeoutCts.Token   // timeout may expire in queue

// CORRECT (timeout starts after slot granted):
member _.CompleteWithPriorityAsync req target priority ct =
    task {
        // Phase 1: queue wait — use raw client ct only (no timeout yet)
        do! enqueue122b priority ct
        // Slot granted. Now start the timeout clock.

        // Phase 2: upstream call — linked (client abort OR timeout)
        use timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(float options.TimeoutSeconds))
        use linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
        try
            return! inner.CompleteAsync req target linkedCts.Token
        finally
            sem122b.Release() |> ignore
    }
```

**Rationale for "timeout starts at semaphore acquire, not at enqueue":**
- Queue wait time is bounded by other requests' completion times. If 4 requests are queued ahead and each takes 60s, a 300s timeout would expire in the queue before the request ever runs.
- The timeout's purpose is "upstream hang protection" — it should only burn while the upstream is being called.
- PROJECT.md: "Per-request timeout configurable, default 300s (matches blueCode 122B cold-start window)" — this is clearly a per-call timeout, not a per-queue-entry timeout.
- Open question for user: Should queue wait time also be bounded? If yes, add a separate `MaxQueueWaitMs` option that fires at enqueue time (separate `CancellationTokenSource` linked into the enqueue wait). For v1, recommend: no separate queue-wait timeout; just cancellation propagation from client disconnect.

**Disposal (all four CTS objects must be disposed):**

```fsharp
// use binding in task {} disposes on scope exit, INCLUDING exceptions.
// CancellationTokenSource.Dispose() is synchronous — valid in finally.
use timeoutCts = new CancellationTokenSource(...)      // disposed at scope exit
use linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(...)  // disposed at scope exit
```

**`CancellationTokenSource.CreateLinkedTokenSource` note:** The linked CTS's token is cancelled when ANY of the source tokens fires. But the linked CTS does NOT dispose the source CTSes — each must be disposed independently. `use` bindings ensure this.

---

## Focus Area 4: `try/finally Release()` — All Four Cases

### The Core Problem

F# `task {}` compiler rejects `do! asyncOp` inside `finally` blocks (error FS0750: "async" keyword not allowed inside a finalizer). `sem122b.Release()` is synchronous and returns `int` — it is NOT a `do!` operation. It is always legal in `finally`:

```fsharp
// LEGAL in task {} finally:
finally
    sem122b.Release() |> ignore   // synchronous — OK

// ILLEGAL in task {} finally:
finally
    do! someAsyncOperation()      // FS0750 — async not allowed in finally
```

This was the Phase 2 pitfall with `enumerator.DisposeAsync()` — the solution was to call it explicitly in each arm (`with` + normal path), not in `finally`. For semaphore release, there is NO such problem because `Release()` is synchronous.

### Four Release Cases

**Case 1: Upstream call throws synchronously (e.g., immediate `ArgumentException`):**
```fsharp
do! enqueue122b priority ct    // slot granted, sem held
try
    return! inner.CompleteAsync req target linkedCt   // throws synchronously
finally
    sem122b.Release() |> ignore   // fires — correct
```

**Case 2: Upstream call throws asynchronously (e.g., `HttpRequestException` mid-await):**
```fsharp
do! enqueue122b priority ct    // slot granted, sem held
try
    let! result = inner.CompleteAsync req target linkedCt   // awaited, then throws
    return result
finally
    sem122b.Release() |> ignore   // fires after exception propagates from await — correct
```

**Case 3: Cancellation fires during upstream call (client disconnect or timeout):**
```fsharp
do! enqueue122b priority ct    // slot granted, sem held
try
    return! inner.CompleteAsync req target linkedCt   // linkedCt fires → OperationCanceledException
finally
    sem122b.Release() |> ignore   // fires — correct; sem count returns to 1
```

**Case 4: Cancellation fires during semaphore queue wait (before acquire):**
```fsharp
do! enqueue122b priority ct   // ct fires → tcs.TrySetCanceled → OperationCanceledException thrown here
// The line below is NEVER REACHED — no semaphore was acquired
try
    return! inner.CompleteAsync req target linkedCt
finally
    sem122b.Release() |> ignore   // NOT REACHED — correct; no Release needed
```

**The critical structural guarantee:** The `try/finally` block wrapping the upstream call is entered ONLY if `enqueue122b` completed successfully (i.e., the semaphore was acquired by the dispatcher and the TCS was signaled). If `enqueue122b` throws (cancellation), the `try/finally` is never entered, so `Release()` is never called on an unacquired semaphore. This is the correct semantics.

**Verified pattern from PITFALL-8:**
```
await enqueue122b(priority, ct)    ← if this throws, no Release needed
try                                ← only entered if enqueue succeeded (slot acquired)
    call upstream
finally
    sem.Release()                  ← always runs if try block was entered
```

---

## Focus Area 5: Streaming Through the Queue

### The Slot-Holding Requirement

A 122B streaming response must hold the semaphore slot for the ENTIRE duration of the stream. Releasing the slot after the first yield (or after headers arrive) would allow a second 122B request to start while the first is still generating, which triggers `[METAL] Insufficient Memory`.

### F# Code Shape for `StreamWithPriority`

```fsharp
member this.StreamWithPriority
    (req: RouterRequest)
    (target: ModelId)
    (priority: Priority)
    (ct: CancellationToken) : IAsyncEnumerable<Result<string, RouterError>> =
    match target with
    | Qwen35B ->
        inner.StreamAsync req target ct   // no gate
    | Qwen122B ->
        taskSeq {
            // Phase 1: queue wait (no timeout yet — timeout starts after slot)
            do! enqueue122b priority ct
            // Semaphore now held. Start timeout clock.
            use timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(float options.TimeoutSeconds))
            use linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
            let linkedCt   = linkedCts.Token
            try
                // Iterate inner stream — yield each chunk to caller
                // The semaphore is held for every iteration until the sequence completes
                let mutable hasError = false
                for item in inner.StreamAsync req target linkedCt do
                    if not hasError then
                        match item with
                        | Ok _    -> yield item
                        | Error _ -> yield item; hasError <- true
            finally
                // taskSeq {} allows synchronous finally — Release() is synchronous, safe here
                sem122b.Release() |> ignore
                Interlocked.Decrement(&activeCount) |> ignore
        }
```

### Disposal: Does the Endpoint Dispose Properly?

Confirmed from `ChatCompletions.fs` (Phase 2 implementation):
```fsharp
let enumerator = chunks.GetAsyncEnumerator(ct)
try
    // ... loop ...
    do! enumerator.DisposeAsync()       // normal path (line 201)
with
| :? OperationCanceledException ->
    do! enumerator.DisposeAsync()       // cancel path (line 208)
| ex ->
    do! enumerator.DisposeAsync()       // error path (line 211)
```

All three exit arms call `DisposeAsync()`. This triggers disposal of the `taskSeq {}` enumerator from `StreamWithPriority`, which exits the `try/finally` block and calls `sem122b.Release()`. The semaphore is guaranteed to be released in all exit paths.

**Pitfall to document:** If a consumer calls `StreamWithPriority` and then abandons the enumerator without calling `DisposeAsync`, the semaphore is leaked. The current `ChatCompletions.fs` implementation disposes in all arms, so this is not a risk in the current codebase. Future callers must follow the same pattern.

---

## Focus Area 6: 35B Bypass

### Confirmed Port Shape

`IUpstreamClient.CompleteAsync` signature (from `Ports.fs`):
```fsharp
abstract member CompleteAsync :
    req    : RouterRequest
    -> target : ModelId
    -> ct     : CancellationToken
    -> Task<Result<string, RouterError>>
```

The `target: ModelId` parameter is the dispatch key. `QueueDispatcher` checks it:

```fsharp
match target with
| Qwen35B  -> return! inner.CompleteAsync req target ct    // no gate
| Qwen122B -> (* enqueue + semaphore path *)
```

This is correct and clean. The bypass is a single `match` at the top of each method.

**Priority for 35B:** Priority is meaningless for 35B (no concurrency gate). The `CompleteWithPriorityAsync` / `StreamWithPriority` methods accept a `priority` parameter but ignore it for 35B. The `IUpstreamClient` fallback implementations default to `Low` — for 35B this is irrelevant.

---

## Focus Area 7: `/stats` Endpoint

### Interface and Backing Type

```fsharp
// In SmartRouter.Cli (NOT Core — /stats is wire-shape concern, ARCH-06)

/// Stats data snapshot — returned by GET /stats
type StatsSnapshot =
    { QueueDepth         : int
      ActiveCount        : int      // currently running 122B calls
      SemaphoreAvailable : int      // SemaphoreSlim.CurrentCount (should be 0 or 1)
      TotalEnqueued      : int64
      TotalCompleted     : int64
      AvgWaitMs          : float    // rolling average wait from enqueue to slot grant
      AvgDurationMs      : float    // rolling average upstream call duration
      FailureCount       : int64 }

/// Provider interface — DI singleton, implemented by QueueDispatcher
/// (QueueDispatcher already holds all the counters, so it naturally implements this)
type IStatsProvider =
    abstract member GetSnapshot : unit -> StatsSnapshot
```

**JSON shape returned by `GET /stats`:**

```json
{
  "queue_depth": 2,
  "active_count": 1,
  "semaphore_available": 0,
  "total_enqueued": 47,
  "total_completed": 45,
  "avg_wait_ms": 1230.5,
  "avg_duration_ms": 18400.0,
  "failure_count": 2,
  "timestamp": "2026-05-08T00:00:00Z"
}
```

**Naming convention:** snake_case for JSON fields (OpenAI convention). `snake_case` is the standard for OpenAI-compatible APIs. Add `timestamp` for debugging (when was the snapshot taken).

**Counter implementation (thread-safe):**

```fsharp
// Inside QueueDispatcher (which implements IStatsProvider)
let mutable private totalEnqueued  = 0L
let mutable private totalCompleted = 0L
let mutable private failureCount   = 0L

// Rolling average — use a lock-protected circular buffer (simple: just track sum+count)
let mutable private waitSumMs   = 0.0
let mutable private waitCount   = 0L
let mutable private durSumMs    = 0.0
let mutable private durCount    = 0L
let private statsLock = obj()

// On each request completion:
let private recordCompletion (waitMs: float) (durMs: float) =
    lock statsLock (fun () ->
        waitSumMs   <- waitSumMs + waitMs
        waitCount   <- waitCount + 1L
        durSumMs    <- durSumMs + durMs
        durCount    <- durCount + 1L)

interface IStatsProvider with
    member _.GetSnapshot() =
        let depth, active, sem = 
            lock queueLock (fun () -> pq.Count),
            Volatile.Read(&activeCount),
            sem122b.CurrentCount
        let avgWait, avgDur =
            lock statsLock (fun () ->
                (if waitCount > 0L then waitSumMs / float waitCount else 0.0),
                (if durCount  > 0L then durSumMs  / float durCount  else 0.0))
        { QueueDepth         = depth
          ActiveCount        = active
          SemaphoreAvailable = sem
          TotalEnqueued      = Volatile.Read(&totalEnqueued)
          TotalCompleted     = Volatile.Read(&totalCompleted)
          AvgWaitMs          = avgWait
          AvgDurationMs      = avgDur
          FailureCount       = Volatile.Read(&failureCount) }
```

**Stats endpoint registration:**

```fsharp
// In Endpoints/Stats.fs
app.MapGet("/stats", Func<IStatsProvider, IResult>(fun stats ->
    Results.Json(stats.GetSnapshot(), jsonOptions))) |> ignore
```

**DI registration (CompositionRoot):**
```fsharp
// Register QueueDispatcher as both IUpstreamClient and IStatsProvider
services.AddSingleton<QueueDispatcher>(fun sp ->
    QueueDispatcher(
        sp.GetRequiredService<QwenUpstreamClient>(),
        sp.GetRequiredService<IOptions<QueueDispatcherOptions>>().Value)) |> ignore

services.AddSingleton<IUpstreamClient>(fun sp ->
    sp.GetRequiredService<QueueDispatcher>() :> IUpstreamClient) |> ignore

services.AddSingleton<IStatsProvider>(fun sp ->
    sp.GetRequiredService<QueueDispatcher>() :> IStatsProvider) |> ignore
```

**Note:** `QwenUpstreamClient` must also be registered as a concrete singleton (not just as `IUpstreamClient`) so `QueueDispatcher` can resolve it for wrapping.

---

## Focus Area 8: Starvation Prevention

### The Problem

Constant high-priority traffic (e.g., a `graph_indexing` batch job) blocks low-priority items indefinitely. PROJECT.md `Out of Scope` defers "multi-level priority queue beyond two levels with aging / starvation prevention" to v2, but the SUMMARY.md notes "aging/promotion for low-priority starvation prevention (30s default threshold)" as a Phase 3 deliverable.

### Three Mitigation Options

**(a) Aging/promotion:** After `MaxWaitMs` ms in queue, promote to High. Requires scanning the queue periodically. `PriorityQueue<T,int>` does not support in-place update — must rebuild (copy all items, enqueue with new priority). Expensive for large queues; for this single-process loopback router with at most ~10 concurrent requests, it is fine.

**(b) Max-wait cap:** After `MaxWaitMs` ms in queue, cancel the entry with a `QueueWaitTimeout` error. Returns 503 to the caller immediately. Simpler than promotion; callers must handle 503. Not ideal for Graphify batch jobs that have no retry loop.

**(c) Fairness counter:** After `K` consecutive High picks, force one Low pick if any Low items are waiting. Requires a separate data structure to track Low items (or scan the PriorityQueue). Simple to implement if separate `Queue<QueueEntry>` instances are maintained for High and Low.

### Recommendation for v1

**Implement option (c) with K=10** using TWO separate `Queue<QueueEntry>` (FIFO within level) rather than a single `PriorityQueue`. This simplifies the fairness logic:

```fsharp
let private highQueue = Queue<QueueEntry>()
let private lowQueue  = Queue<QueueEntry>()
let private queueLock = obj()
let mutable private consecutiveHighPicks = 0

let private dequeueNext () =
    lock queueLock (fun () ->
        let forceLow = consecutiveHighPicks >= options.FairnessK && lowQueue.Count > 0
        if highQueue.Count > 0 && not forceLow then
            let e = highQueue.Dequeue()
            consecutiveHighPicks <- consecutiveHighPicks + 1
            Some e
        elif lowQueue.Count > 0 then
            let e = lowQueue.Dequeue()
            consecutiveHighPicks <- 0
            Some e
        elif highQueue.Count > 0 then
            let e = highQueue.Dequeue()
            consecutiveHighPicks <- consecutiveHighPicks + 1
            Some e
        else None)
```

This is more transparent than a `PriorityQueue` for a two-level system, avoids the rebuild-on-promote complexity, and makes the fairness logic explicit. **Recommend using two queues for v1 over `PriorityQueue<T,int>`.**

**Open question for user:** Should we implement aging (option a) in addition to fairness, or defer to v2? The PROJECT.md defers "aging / starvation prevention" to v2 explicitly. With K=10 fairness and low traffic, starvation is unlikely. Recommend: fairness counter only for v1, document the v2 aging hook. If user disagrees, aging can be added to the dispatcher loop on each `signal.WaitAsync()` wake.

---

## Focus Area 9: `QueueTests.fs` Pattern

### Fake `IUpstreamClient` with Controlled Latency

```fsharp
module SmartRouter.Tests.QueueTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Expecto
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports
open SmartRouter.Cli.Adapters.QueueDispatcher

// ── Fake upstream ─────────────────────────────────────────────────────────────

/// Controllable fake upstream. Each call records start/end timestamps.
/// Latency is injected via a TaskCompletionSource per call, allowing tests
/// to control when each "upstream response" arrives.
type FakeUpstreamClient(defaultLatencyMs: int) =
    let starts    = List<DateTimeOffset>()
    let ends      = List<DateTimeOffset>()
    let callGates = List<TaskCompletionSource<unit>>()
    let mutable callCount = 0
    let lock = obj()

    member _.CallCount = Volatile.Read(&callCount)
    member _.Starts = starts |> List.ofSeq
    member _.Ends   = ends   |> List.ofSeq

    /// Release the Nth in-flight call (0-indexed)
    member _.ReleaseCall(idx: int) =
        lock lock (fun () ->
            if idx < callGates.Count then
                callGates.[idx].TrySetResult() |> ignore)

    interface IUpstreamClient with
        member _.CompleteAsync req target ct =
            task {
                let gate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                lock lock (fun () ->
                    callGates.Add(gate)
                    starts.Add(DateTimeOffset.UtcNow)
                    Interlocked.Increment(&callCount) |> ignore)

                if defaultLatencyMs > 0 then
                    do! Task.Delay(defaultLatencyMs, ct)
                else
                    // Wait for explicit release
                    do! gate.Task

                lock lock (fun () -> ends.Add(DateTimeOffset.UtcNow))
                return Ok $"""{{\"choices\":[{{\"message\":{{\"content\":\"fake-{target}\"}}}}]}}"""
            }

        member _.StreamAsync req target ct =
            FSharp.Control.taskSeq {
                do! Task.Delay(defaultLatencyMs, ct)
                yield Ok "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}"
                yield Ok "data: [DONE]"
            }
```

### Test Skeletons

```fsharp
let defaultOpts = { TimeoutSeconds = 30; MaxWaitMs = 30000; FairnessK = 10 }

let tests = testSequenced <| testList "queue" [

    testCaseAsync "five concurrent 122B requests serialize" <| async {
        // Fake upstream: explicit-release mode (gate per call)
        let fake = FakeUpstreamClient(0)
        let dispatcher = QueueDispatcher(fake, defaultOpts)

        // Launch 5 concurrent requests — all targeting 122B
        let tasks =
            [1..5] |> List.map (fun _ ->
                Task.Run(fun () ->
                    (dispatcher :> IUpstreamClient).CompleteAsync
                        { emptyRequest with Messages = [{ Role = User; Content = "test" }] }
                        Qwen122B
                        CancellationToken.None))

        // Give tasks time to queue
        do! Async.Sleep 50

        // Release them one at a time, verifying only one runs at a time
        for i in 0..4 do
            Expect.equal (fake.CallCount - i) 1 $"call {i}: only one upstream call active"
            fake.ReleaseCall(i)
            do! Async.Sleep 10

        let! _ = Task.WhenAll(tasks) |> Async.AwaitTask
        Expect.equal fake.CallCount 5 "all 5 calls completed"
        // Verify serial: each start > previous end
        let starts = fake.Starts
        let ends   = fake.Ends
        for i in 1..4 do
            Expect.isTrue (starts.[i] >= ends.[i-1])
                          $"call {i} started before call {i-1} ended (concurrent overlap)"
    }

    testCaseAsync "high-priority executes before low-priority when queued" <| async {
        let fake = FakeUpstreamClient(0)
        let dispatcher = QueueDispatcher(fake, defaultOpts)
        let completionOrder = List<string>()

        // Start a request to occupy the slot
        let occupyGate = TaskCompletionSource<unit>()
        let occupyTask =
            Task.Run(fun () ->
                task {
                    // This request will hold the slot while low + high are queued
                    let! r = dispatcher.CompleteWithPriorityAsync
                                 emptyRequest Qwen122B Low CancellationToken.None
                    return r
                } :> Task)

        do! Async.Sleep 20  // let occupy acquire slot

        // Enqueue low-priority request
        let lowTask =
            Task.Run(fun () ->
                task {
                    let! _ = dispatcher.CompleteWithPriorityAsync
                                 emptyRequest Qwen122B Low CancellationToken.None
                    lock completionOrder (fun () -> completionOrder.Add("low"))
                } :> Task)

        do! Async.Sleep 10

        // Enqueue high-priority request AFTER low
        let highTask =
            Task.Run(fun () ->
                task {
                    let! _ = dispatcher.CompleteWithPriorityAsync
                                 emptyRequest Qwen122B High CancellationToken.None
                    lock completionOrder (fun () -> completionOrder.Add("high"))
                } :> Task)

        do! Async.Sleep 10

        // Release the occupying request — high should run next
        fake.ReleaseCall(0)
        do! Async.Sleep 10
        fake.ReleaseCall(1)  // second call (should be high)
        do! Async.Sleep 10
        fake.ReleaseCall(2)  // third call (should be low)

        do! Task.WhenAll([occupyTask; lowTask; highTask]) |> Async.AwaitTask
        Expect.equal (completionOrder |> List.ofSeq) ["high"; "low"]
                     "high-priority completed before low-priority"
    }

    testCaseAsync "cancellation before acquire does not leak semaphore" <| async {
        let fake = FakeUpstreamClient(0)
        let dispatcher = QueueDispatcher(fake, defaultOpts)
        use cts = new CancellationTokenSource()

        // Occupy the slot
        let occupyTask =
            Task.Run(fun () ->
                dispatcher.CompleteWithPriorityAsync emptyRequest Qwen122B Low CancellationToken.None
                :> Task)
        do! Async.Sleep 20

        // Enqueue a request then cancel it while queued
        let cancelledTask =
            Task.Run(fun () ->
                task {
                    try
                        let! _ = dispatcher.CompleteWithPriorityAsync
                                     emptyRequest Qwen122B Low cts.Token
                        ()
                    with :? OperationCanceledException -> ()
                } :> Task)

        do! Async.Sleep 20
        cts.Cancel()  // cancel while queued — before semaphore acquired
        do! Async.Sleep 20

        // Semaphore count must be 0 (slot is occupied by occupy request, not leaked)
        Expect.equal dispatcher.Semaphore.CurrentCount 0
                     "semaphore count 0 (not leaked by cancelled queued request)"

        // Release occupy — semaphore returns to 1
        fake.ReleaseCall(0)
        do! Task.WhenAll([occupyTask; cancelledTask]) |> Async.AwaitTask
        Expect.equal dispatcher.Semaphore.CurrentCount 1
                     "semaphore count 1 after occupying request completes"
    }

    testCaseAsync "hung upstream releases on timeout" <| async {
        // Fake upstream that hangs forever (no release)
        let fake = FakeUpstreamClient(0)  // gate mode — never released
        let shortTimeoutOpts = { defaultOpts with TimeoutSeconds = 1 }
        let dispatcher = QueueDispatcher(fake, shortTimeoutOpts)

        let! result =
            task {
                return! dispatcher.CompleteWithPriorityAsync
                            emptyRequest Qwen122B Low CancellationToken.None
            } |> Async.AwaitTask

        // Should return error (timeout), not hang
        match result with
        | Error (ModelUnavailable _) -> ()
        | Error (InvalidRequest "client cancelled") ->
            () // OperationCanceledException from timeout maps here
        | other -> failtest $"expected error from timeout, got {other}"

        // Semaphore must be released — next request can proceed
        do! Async.Sleep 50
        Expect.equal dispatcher.Semaphore.CurrentCount 1
                     "semaphore released after timeout"
    }

    testCaseAsync "QueueDepth and ActiveCount reflect live state" <| async {
        let fake = FakeUpstreamClient(0)
        let dispatcher = QueueDispatcher(fake, defaultOpts)

        Expect.equal dispatcher.QueueDepth 0 "initially empty"
        Expect.equal dispatcher.ActiveCount 0 "initially idle"

        // Start a request (will occupy the slot)
        let t1 = Task.Run(fun () ->
            dispatcher.CompleteWithPriorityAsync emptyRequest Qwen122B Low CancellationToken.None :> Task)
        do! Async.Sleep 20

        Expect.equal dispatcher.ActiveCount 1 "one active after slot granted"
        Expect.equal dispatcher.QueueDepth 0 "queue empty (slot is held, not queued)"

        // Enqueue a second (will wait)
        let t2 = Task.Run(fun () ->
            dispatcher.CompleteWithPriorityAsync emptyRequest Qwen122B Low CancellationToken.None :> Task)
        do! Async.Sleep 20

        Expect.equal dispatcher.QueueDepth 1 "one queued while slot is held"

        fake.ReleaseCall(0)
        do! Async.Sleep 20
        Expect.equal dispatcher.ActiveCount 1 "second request now active"
        Expect.equal dispatcher.QueueDepth 0 "queue drained"

        fake.ReleaseCall(1)
        do! Task.WhenAll([t1; t2]) |> Async.AwaitTask
        Expect.equal dispatcher.ActiveCount 0 "idle after completion"
    }
]
```

**Notes:**
- All tests use `testSequenced` (wraps the entire `testList`) — prevents parallel execution of tests that share the `QueueDispatcher` singleton state.
- `emptyRequest` is a test helper:
  ```fsharp
  let emptyRequest = { Messages = [{ Role = User; Content = "test" }]; ModelOverride = None; Task = None; Stream = false; Temperature = None; TopP = None; MaxTokens = None; UnknownFields = Map.empty }
  ```
- These tests do NOT spin up a Kestrel server. They are pure in-process `QueueDispatcher` tests.
- The `testCaseAsync` CE (from Expecto) requires returning `Async<unit>`. Use `Async.AwaitTask` to bridge `Task<_>` returns.

---

## Focus Area 10: Plan 03-03 Load Tests

**Recommendation: Keep as a separate plan (03-03), clearly marked as optional/slow.**

Rationale:
- Load tests with N concurrent requests + controlled latency run for seconds. They should not be in the default `dotnet test` run alongside unit tests.
- Expecto's `testSequenced` will serialize them, making the full test run slow.
- Use a separate `testList "load-tests"` category guarded by an environment variable or a separate test binary target, or mark with `ptestCase` (pending) in CI.
- The load test validates that the queue depth counter tracks correctly under burst, and that 122B throughput cap holds (no more than 1 active at a time under 20 concurrent submissions).

**Load test shape:**
```fsharp
// 03-03: load test skeleton
// Tag as slow: ptestCaseAsync to skip in normal CI
ptestCaseAsync "20 concurrent 122B requests maintain at-most-one in flight" <| async {
    let fake = FakeUpstreamClient(50)  // 50ms per call
    let dispatcher = QueueDispatcher(fake, defaultOpts)
    let tasks =
        [1..20] |> List.map (fun _ ->
            Task.Run(fun () ->
                dispatcher.CompleteWithPriorityAsync emptyRequest Qwen122B Low CancellationToken.None
                :> Task))
    do! Task.WhenAll(tasks) |> Async.AwaitTask
    // During the run, peak ActiveCount should never have exceeded 1
    // (verifiable via timestamps recorded in FakeUpstreamClient)
    let starts = fake.Starts
    let ends   = fake.Ends
    for i in 1..19 do
        Expect.isTrue (starts.[i] >= ends.[i-1])
                      $"request {i} started before {i-1} ended"
}
```

---

## Focus Area 11: Phase 3 Pitfall Summary

All 7 pitfalls that the planner must address atomically:

### P1: SemaphoreSlim Leak on Cancellation (PITFALL-8)

**Risk:** If the upstream call throws (cancellation or exception) and `Release()` is not in a `try/finally`, `sem122b.CurrentCount` stays at 0 forever. All subsequent 122B requests queue indefinitely.

**Prevention:** `try/finally` wrapping the upstream call, entered ONLY after semaphore is acquired. The structural guarantee: `do! enqueue122b...` then `try ... finally sem.Release()`. If `enqueue122b` throws (cancellation before acquire), the `try` is never entered.

**Test:** Cancel mid-flight; verify `CurrentCount` returns to 1 immediately.

### P2: FIFO Semaphore Bypasses Priority (PITFALL-9)

**Risk:** If requests call `SemaphoreSlim.WaitAsync(ct)` directly, `SemaphoreSlim` queues continuations in FIFO order. High-priority requests that arrive after low-priority requests wait in line — priority is silently ignored.

**Prevention:** Dispatcher loop + per-request TCS pattern. Individual requests park on `tcs.Task`, not on `sem.WaitAsync`. The dispatcher dequeues by priority.

**Test:** Enqueue 1 low then 1 high while slot is occupied; verify high completes first.

### P3: Starvation Under Constant High-Priority Load (PITFALL-10)

**Risk:** Graphify batch `graph_indexing` jobs submit constant high-priority requests. Low-priority `dependency_analysis` or heuristic-routed requests queue forever.

**Prevention:** Fairness counter K=10. After 10 consecutive High picks, force one Low pick. Two separate `Queue<T>` instances (high / low) make this explicit and cheap.

**Open question for user:** K=10 is a recommendation. If the Graphify workload produces bursts of more than 10 high-priority requests at a time with low-priority requests waiting, K should be tuned via `/stats avg_wait_ms`. Recommend: expose `FairnessK` as a configurable option in `QueueDispatcherOptions` (from `appsettings.json`).

### P4: Deadlock if Upstream Hangs Without Timeout (PITFALL-11)

**Risk:** mlx_lm.server accepts the connection, writes headers, then stalls mid-stream (observed during `[METAL] near-miss events`). `HttpClient.Timeout` does not fire for a stalled mid-stream connection — it only covers the initial connection. The `SemaphoreSlim` is held indefinitely.

**Prevention:** Per-request `CancellationTokenSource` with 300s timeout, linked after semaphore acquire. Covers the full streaming duration. `HttpClient.Timeout = 330s` as a backstop (fires 30s after the per-request CT, providing a secondary guard).

**Test:** Fake upstream that never returns; assert timeout fires within `TimeoutSeconds + buffer` and semaphore returns to 1.

### P5: F# `task {}` Finally Block Limitation (FS0750)

**Risk:** `do! asyncOp()` inside `finally` blocks in `task {}` is rejected at compile time with FS0750. Phase 2 hit this with `enumerator.DisposeAsync()` — the fix was explicit calls in each arm. For semaphore release, `Release()` is synchronous, so this is NOT an issue. But `StreamWithPriority` uses `taskSeq {}` which has different rules.

**Prevention:** In `task {}`: `finally` block with synchronous `sem.Release() |> ignore` — always legal. In `taskSeq {}`: `finally` block with synchronous `sem.Release() |> ignore` — also legal (taskSeq allows synchronous finally). Never put `do!` in either type of `finally`.

**Test:** Compile-time verification — if `try/finally` compiles, the pattern is correct.

### P6: Per-Request Timeout Starts from Semaphore Acquire, Not from Enqueue (PITFALL-11 variant)

**Risk:** If the `CancellationTokenSource(300s)` is created before `enqueue122b`, the timeout burns during queue wait time. With 4 requests ahead at 60s each, the timeout expires in the queue before the request ever runs.

**Prevention:** Create `timeoutCts` AFTER `enqueue122b` returns (after slot is granted). The timeout only counts from when the upstream call starts.

**Code shape:** `do! enqueue122b priority ct` → `use timeoutCts = new CancellationTokenSource(...)` → `use linkedCts = ...` → `try upstream.CompleteAsync ... linkedCts.Token finally Release()`.

### P7: Streaming Releases Slot Only After Enumerator Dispose (PITFALL-8 streaming variant)

**Risk:** `StreamWithPriority` holds the semaphore for the lifetime of the `IAsyncEnumerable`. If the consumer does not call `DisposeAsync()` on the enumerator (e.g., abandons the stream), the semaphore is never released.

**Prevention:** The `ChatCompletions.fs` endpoint (Phase 2) already calls `DisposeAsync()` in all three exit arms (normal, cancel, error). This is verified in the Phase 2 VERIFICATION.md at lines 201, 208, 211. Future callers must follow the same pattern. Document this in the `StreamWithPriority` XML doc comment.

---

## Open Questions for User

| # | Question | Recommendation | Impact |
|---|----------|----------------|--------|
| 1 | Should queue wait time be bounded (separate `MaxQueueWaitMs` timeout)? | No for v1. Client CT propagates disconnect; operator controls queue depth via `/stats`. | LOW — add in v2 if `/stats` shows chronic waits |
| 2 | Fairness counter K=10: correct for expected Graphify workload? | Expose as `FairnessK` in `appsettings.json`. Tune post-launch via `/stats avg_wait_ms`. | LOW — configurable at runtime |
| 3 | Should `/stats` include per-task-type counters (how many `graph_indexing` vs `reasoning`)? | Defer to v2. v1 `/stats` covers queue health; task breakdown is analytics. | LOW |
| 4 | `IQueueAwareUpstreamClient` vs `AsyncLocal<Priority>` vs `QueueDispatcher` resolved directly: which DI pattern? | `IQueueAwareUpstreamClient` (explicit, testable). Recommendation flagged in Focus Area 1. | HIGH — planner must choose before Plan 03-01 |
| 5 | Two `Queue<T>` (high/low) vs single `PriorityQueue<T,int>`: final recommendation? | Two queues for v1. Cleaner fairness logic, no rebuild-on-promote complexity. Confirm before Plan 03-01. | MEDIUM — affects QueueDispatcher implementation |

---

## Plan Split Recommendation

The ROADMAP plans 03-01 / 03-02 / 03-03 are the right granularity:

- **03-01: `QueueDispatcher.fs`** — the core concurrency gate. All four pitfall mitigations must be in this single plan. Do not defer linked CTS or try/finally to 03-02.
- **03-02: `/stats` endpoint + `QueueTests.fs`** — observability + correctness tests. Tests depend on `QueueDispatcher` existing. `/stats` depends on `IStatsProvider`.
- **03-03: Load tests** — separate, optional, marked slow (`ptestCaseAsync`). Do not run on every CI pass.

**Critical:** 03-01 is the atomic unit. The semaphore, priority queue, linked CTS, and try/finally Release are all load-bearing correctness requirements that must ship together. Splitting any of these into 03-02 leaves Phase 3 in a broken state between plans.

---

## CompositionRoot Change (Single Line)

Phase 3 changes exactly ONE registration in `CompositionRoot.fs`:

**Before (Phase 1):**
```fsharp
services.AddSingleton<IUpstreamClient>(fun sp ->
    QwenUpstreamClient(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IOptions<UpstreamOptions>>())
    :> IUpstreamClient)
```

**After (Phase 3):**
```fsharp
// Register the concrete HTTP client as a named singleton (not IUpstreamClient)
services.AddSingleton<QwenUpstreamClient>(fun sp ->
    QwenUpstreamClient(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IOptions<UpstreamOptions>>()))
    |> ignore

// Register dispatcher wrapping it — exposes both IUpstreamClient and IStatsProvider
services.AddSingleton<QueueDispatcher>(fun sp ->
    QueueDispatcher(
        sp.GetRequiredService<QwenUpstreamClient>() :> IUpstreamClient,
        sp.GetRequiredService<IOptions<QueueDispatcherOptions>>().Value))
    |> ignore

services.AddSingleton<IUpstreamClient>(fun sp ->
    sp.GetRequiredService<QueueDispatcher>() :> IUpstreamClient)
    |> ignore

services.AddSingleton<IStatsProvider>(fun sp ->
    sp.GetRequiredService<QueueDispatcher>() :> IStatsProvider)
    |> ignore
```

Add `QueueDispatcherOptions` binding:
```fsharp
services.Configure<QueueDispatcherOptions>(config.GetSection("Queue")) |> ignore
```

Add to `appsettings.json`:
```json
"Queue": {
  "TimeoutSeconds": 300,
  "MaxWaitMs": 30000,
  "FairnessK": 10
}
```

---

*Research completed: 2026-05-08*
*Phase 3 is ready for planning: all patterns are code-shaped, pitfalls mapped, tests sketched.*
