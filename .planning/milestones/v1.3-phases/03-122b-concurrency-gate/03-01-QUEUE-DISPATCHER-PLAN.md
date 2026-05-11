---
phase: 03-122b-concurrency-gate
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/SmartRouter.Core/Ports.fs
  - src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
  - src/SmartRouter.Cli/Adapters/QueueDispatcher.fs           # NEW
  - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
  - src/SmartRouter.Cli/CompositionRoot.fs
  - src/SmartRouter.Cli/Program.fs
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - src/SmartRouter.Cli/appsettings.json
autonomous: true

must_haves:
  truths:
    - "IUpstreamClient.CompleteAsync and StreamAsync take decision: RoutingDecision (not target: ModelId); existing QwenUpstreamClient and ChatCompletions endpoint compile under the new port shape"
    - "When two 122B requests are issued concurrently, only one runs at a time inside the QueueDispatcher; the second waits until the first releases the slot"
    - "When a high-priority 122B request is enqueued behind a queued low-priority request, the high-priority request runs next after the active slot frees"
    - "35B requests bypass the queue entirely — issuing five concurrent 35B requests through QueueDispatcher does NOT serialize through the SemaphoreSlim"
    - "Cancelling a request before it acquires the semaphore (via ct.Cancel during queue wait) does not leak the semaphore — sem122b.CurrentCount remains unchanged"
    - "Cancelling a request after it acquires the semaphore (or upstream throws/times out) releases the slot in finally — next queued request proceeds"
    - "PerRequestTimeoutSeconds (default 300) starts AFTER semaphore acquire, never burns during queue wait; a hung upstream times out and releases the slot"
    - "After K=10 consecutive high-priority dispatches, if any low-priority items are queued, the dispatcher forces one low-priority pick (fairness counter)"
    - "appsettings.json contains a Queue section with FairnessK, MaxConcurrent122B, PerRequestTimeoutSeconds; startup rejects MaxConcurrent122B values other than 1"
    - "CompositionRoot registers QueueDispatcher as both IUpstreamClient (wrapping QwenUpstreamClient) and IStatsProvider; the endpoint resolves IUpstreamClient and gets the dispatcher transparently"
  artifacts:
    - path: src/SmartRouter.Core/Ports.fs
      provides: "Updated IUpstreamClient with decision: RoutingDecision parameter"
      contains: "decision : RoutingDecision"
    - path: src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
      provides: "QueueDispatcher class implementing IUpstreamClient + IStatsProvider; SemaphoreSlim(1), high/low Queue<Ticket>, dispatcher loop, linked CTS, try/finally Release, fairness counter K, 35B bypass"
      contains: "type QueueDispatcher"
      min_lines: 200
    - path: src/SmartRouter.Cli/CompositionRoot.fs
      provides: "DI registration: QwenUpstreamClient as concrete singleton; QueueDispatcher singleton resolving QwenUpstreamClient; IUpstreamClient and IStatsProvider both backed by QueueDispatcher; Queue section bound to QueueDispatcherOptions"
      contains: "AddSingleton<QueueDispatcher>"
    - path: src/SmartRouter.Cli/appsettings.json
      provides: "Queue section with FairnessK=10, MaxConcurrent122B=1, PerRequestTimeoutSeconds=300"
      contains: "\"Queue\""
  key_links:
    - from: src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
      to: IUpstreamClient.CompleteAsync / StreamAsync
      via: "passes decision (not decision.Target) — port shape change"
      pattern: "upstream\\.(Complete|Stream)Async req decision"
    - from: src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
      to: SemaphoreSlim sem122b
      via: "try/finally Release after enqueue122b returns"
      pattern: "finally\\s+sem122b\\.Release"
    - from: src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
      to: linked CTS
      via: "CreateLinkedTokenSource AFTER enqueue122b (timeout starts at slot grant, not enqueue)"
      pattern: "CreateLinkedTokenSource"
    - from: src/SmartRouter.Cli/CompositionRoot.fs
      to: QueueDispatcher
      via: "AddSingleton<IUpstreamClient>(fun sp -> sp.GetRequiredService<QueueDispatcher>() :> IUpstreamClient)"
      pattern: "GetRequiredService<QueueDispatcher>"
---

<objective>
Implement the atomic 122B concurrency gate: change the IUpstreamClient port shape to take `decision: RoutingDecision` (carrying Target+Priority+Reason in one value), then build the QueueDispatcher adapter that wraps any IUpstreamClient with a SemaphoreSlim(1) gate, two-level priority queue (high/low FIFO with fairness counter K=10), per-request linked CTS (client-abort + 300s timeout starting at slot grant), and try/finally Release discipline that cannot leak the semaphore on cancellation, exception, or timeout. 35B traffic bypasses the queue entirely.

Purpose: This is the SECOND ATOMIC CORRECTNESS CLUSTER. SemaphoreSlim + linked CTS + try/finally Release + queue dispatch + cancellation handling MUST all ship together. Half-built concurrency control deadlocks the queue and causes [METAL] OOM crashes on the 122B server.

Output:
  - Updated `IUpstreamClient` port (Core) with `decision: RoutingDecision` instead of `target: ModelId`.
  - `QueueDispatcher.fs` (Cli adapter): the core gate.
  - Mechanical callsite updates in `QwenUpstreamClient.fs` and `ChatCompletions.fs` (extract `decision.Target` inside the inner client; endpoint passes `decision` straight through).
  - DI wiring in `CompositionRoot.fs` swaps the IUpstreamClient registration to QueueDispatcher.
  - `appsettings.json` gains a `Queue` section.
  - `Program.fs` validates `MaxConcurrent122B = 1` at startup.
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md
@.planning/REQUIREMENTS.md
@.planning/phases/03-122b-concurrency-gate/03-CONTEXT.md
@.planning/phases/03-122b-concurrency-gate/03-RESEARCH.md
@.planning/research/PITFALLS.md

# Source files this plan modifies
@src/SmartRouter.Core/Ports.fs
@src/SmartRouter.Core/Domain.fs
@src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
@src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
@src/SmartRouter.Cli/CompositionRoot.fs
@src/SmartRouter.Cli/Program.fs
@src/SmartRouter.Cli/SmartRouter.Cli.fsproj
@src/SmartRouter.Cli/appsettings.json

# Phase 2 verification — confirms the streaming endpoint disposal pattern QueueDispatcher relies on
@.planning/phases/02-sse-streaming-pass-through/02-VERIFICATION.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Port-shape change — `IUpstreamClient` takes `decision: RoutingDecision`</name>
  <files>
    src/SmartRouter.Core/Ports.fs
    src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
    src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
  </files>
  <action>
This is the LOCKED product decision from CONTEXT.md (Option A). Change `IUpstreamClient` so both methods take `decision: RoutingDecision` rather than `target: ModelId`. The change is mechanical: every internal use of `target` becomes `decision.Target`, and the endpoint already has `decision` in scope from `Routing.routeRequest`.

**Step 1 — `src/SmartRouter.Core/Ports.fs`:** Update both abstract members:

```fsharp
type IUpstreamClient =
    /// Non-streaming call: returns the full response body string.
    /// Takes the full RoutingDecision so adapters that wrap this port
    /// (QueueDispatcher) can dispatch on Target + Priority without a separate
    /// interface. Adapters that only care about Target read decision.Target.
    abstract member CompleteAsync :
        req      : RouterRequest
        -> decision : RoutingDecision
        -> ct       : CancellationToken
        -> Task<Result<string, RouterError>>

    /// Streaming call. Same RoutingDecision parameter — adapters wrap this
    /// to gate on decision.Target / decision.Priority.
    abstract member StreamAsync :
        req      : RouterRequest
        -> decision : RoutingDecision
        -> ct       : CancellationToken
        -> IAsyncEnumerable<Result<string, RouterError>>
```

(`IClock` and `IHealthProbe` are unchanged.) ARCH-01 is preserved — `RoutingDecision` is already a Core DU defined in `Domain.fs`.

**Step 2 — `src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs`:** Update both methods. Inside the body, every reference to `target` becomes `decision.Target`. Replace:
  - `member _.CompleteAsync (req: RouterRequest) (target: ModelId) (ct: CancellationToken)` →
    `member _.CompleteAsync (req: RouterRequest) (decision: RoutingDecision) (ct: CancellationToken)` (then `let target = decision.Target` as the very first line of the body so the rest of the method stays unchanged).
  - Same for `member _.StreamAsync` (extract `let target = decision.Target` at the top of the `taskSeq {}` body).
  - Update the `interface IUpstreamClient with` block:
    ```fsharp
    interface IUpstreamClient with
        member this.CompleteAsync req decision ct = this.CompleteAsync req decision ct
        member this.StreamAsync   req decision ct = this.StreamAsync   req decision ct
    ```

This minimizes diff. The internal `target` symbol still resolves cleanly because `decision.Target` is bound to the same name on the first line.

**Step 3 — `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs`:** Two callsite changes:
  - Line ~158 (streaming branch): `let chunks = upstream.StreamAsync req decision.Target ct` → `let chunks = upstream.StreamAsync req decision ct`
  - Line ~219 (non-streaming branch): `let! result = upstream.CompleteAsync req decision.Target ctx.RequestAborted` → `let! result = upstream.CompleteAsync req decision ctx.RequestAborted`

No other endpoint changes — `decision` is already in scope from the `| Ok decision ->` arm of the match on `routeRequest routingConfig req`.

**Step 4 — Build check:** Run `dotnet build src/SmartRouter.slnx` (or the project files individually) to confirm zero compilation errors. The change is isolated; if anything else won't compile, the diff is wrong.

**Why bundle this with the dispatcher and not split:** The port-shape change is the prerequisite for QueueDispatcher to inspect `decision.Priority` cleanly without an `AsyncLocal` hack or a parallel interface. CompositionRoot also changes in Task 2 to wire the dispatcher; doing the port change separately would require landing a build in a state where the dispatcher does not yet exist but Ports.fs has a new shape — same diff, just sequenced through an extra commit. Tests will be updated in Plan 03-02 (RoutingTests.fs and StreamingTests.fs do NOT mock IUpstreamClient directly — both use real Routing.routeRequest output and a fake Kestrel respectively — so the port change does NOT break existing tests; verify by running `dotnet test`).
  </action>
  <verify>
```
cd /Users/ohama/projs/smart-router
dotnet build SmartRouter.slnx 2>&1 | grep -E "(error|warning FS)"  # MUST be empty
dotnet test 2>&1 | tail -20                                          # 30/30 still pass
grep -n "decision : RoutingDecision\|decision: RoutingDecision" src/SmartRouter.Core/Ports.fs   # 2 hits
grep -n "upstream\.\(Complete\|Stream\)Async req decision" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs   # 2 hits, both pass `decision` not `decision.Target`
grep -n "let target = decision\.Target" src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs   # 2 hits (one in CompleteAsync, one in StreamAsync)
./scripts/check-no-async.sh                                          # PASS
```
  </verify>
  <done>
- `IUpstreamClient` in Ports.fs takes `decision: RoutingDecision` for both methods.
- QwenUpstreamClient compiles with `let target = decision.Target` shim at top of each method body.
- ChatCompletions.fs passes `decision` (not `decision.Target`) to both upstream method calls.
- `dotnet build` succeeds with zero errors / zero warnings.
- Existing 30 tests still pass (port change is mechanical; tests don't mock IUpstreamClient directly).
- `check-no-async.sh` still passes (no Core async added).
  </done>
</task>

<task type="auto">
  <name>Task 2: Implement `QueueDispatcher.fs` (concurrency gate, all four pitfall mitigations atomically)</name>
  <files>
    src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
    src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    src/SmartRouter.Cli/CompositionRoot.fs
    src/SmartRouter.Cli/Program.fs
    src/SmartRouter.Cli/appsettings.json
  </files>
  <action>
Build the QueueDispatcher adapter that wraps `IUpstreamClient`. This is THE atomic correctness cluster — all of (a) SemaphoreSlim(1), (b) two-level priority queue with fairness counter, (c) linked CTS with timeout starting after semaphore acquire, (d) try/finally Release on every exit path, (e) 35B bypass — must ship in this single task. Splitting any of these leaves the build in a deadlock-prone state.

**Step 1 — Create `src/SmartRouter.Cli/Adapters/QueueDispatcher.fs`** (NEW file). Follow CONTEXT.md and RESEARCH.md Focus Area 1 sub-pattern A (dispatcher acquires semaphore, then signals waiter). Use TWO `Queue<Ticket>` instances (high/low) per RESEARCH.md Focus Area 8 — cleaner than `PriorityQueue<T,int>` for two-level + fairness counter. Module structure:

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

[<CLIMutable>]
type QueueDispatcherOptions =
    { FairnessK                : int   // default 10
      MaxConcurrent122B        : int   // MUST be 1 in v1; validated at startup
      PerRequestTimeoutSeconds : int } // default 300

/// Stats snapshot returned by GET /stats. JSON shape uses snake_case
/// (OpenAI convention). All counters are read under appropriate locks /
/// Volatile.Read so the snapshot is internally consistent.
type StatsSnapshot =
    { Timestamp           : DateTimeOffset
      Active122B          : int
      QueueDepth122BHigh  : int
      QueueDepth122BLow   : int
      Active35B           : int
      RequestsPerSec      : float
      AvgLatencyMs60s     : float
      FailureCountTotal   : int64
      FairnessPicksHigh   : int64
      FairnessPicksLow    : int64
      SemaphoreAvailable  : int }

/// Cli-only port — implemented by QueueDispatcher; consumed by /stats endpoint.
type IStatsProvider =
    abstract member GetSnapshot : unit -> StatsSnapshot

type private Ticket =
    { Priority   : Priority
      Decision   : RoutingDecision
      Tcs        : TaskCompletionSource<unit>
      Ct         : CancellationToken
      EnqueuedAt : DateTimeOffset }

type QueueDispatcher(inner: IUpstreamClient, options: QueueDispatcherOptions) =
    do if options.MaxConcurrent122B <> 1 then
        invalidOp $"Queue.MaxConcurrent122B must be 1 in v1 (correctness invariant); got {options.MaxConcurrent122B}"

    let sem122b   = new SemaphoreSlim(1, 1)
    let highQueue = Queue<Ticket>()
    let lowQueue  = Queue<Ticket>()
    let queueLock = obj()
    let signal    = new SemaphoreSlim(0)
    let mutable consecutiveHighPicks = 0

    // Counters
    let mutable active122b      = 0
    let mutable active35b       = 0
    let mutable failureCount    = 0L
    let mutable fairnessPicksHigh = 0L
    let mutable fairnessPicksLow  = 0L
    // Rolling-window stats: simple lock-protected sum/count over a 60s window
    // (concrete impl: bucket per second; trim on read). Acceptable for v1.
    let recentLatencies = Queue<DateTimeOffset * float>()
    let recentRequests  = Queue<DateTimeOffset>()
    let statsLock = obj()

    // Dequeue with fairness: high preferred unless K consecutive highs already picked
    // and a low is available, in which case force one low.
    let tryDequeueNext () : Ticket option =
        lock queueLock (fun () ->
            let forceLow =
                consecutiveHighPicks >= options.FairnessK && lowQueue.Count > 0
            if highQueue.Count > 0 && not forceLow then
                let t = highQueue.Dequeue()
                consecutiveHighPicks <- consecutiveHighPicks + 1
                Interlocked.Increment(&fairnessPicksHigh) |> ignore
                Some t
            elif lowQueue.Count > 0 then
                let t = lowQueue.Dequeue()
                consecutiveHighPicks <- 0
                Interlocked.Increment(&fairnessPicksLow) |> ignore
                Some t
            elif highQueue.Count > 0 then
                let t = highQueue.Dequeue()
                consecutiveHighPicks <- consecutiveHighPicks + 1
                Interlocked.Increment(&fairnessPicksHigh) |> ignore
                Some t
            else None)

    // Background dispatcher loop — single instance.
    // Sub-pattern A: acquires semaphore, then signals waiter.
    let dispatcherLoop () = task {
        while true do
            do! signal.WaitAsync()
            match tryDequeueNext () with
            | None -> ()
            | Some t ->
                if t.Ct.IsCancellationRequested then
                    // Already cancelled while queued — discard, no semaphore acquired
                    t.Tcs.TrySetCanceled(t.Ct) |> ignore
                else
                    // Acquire the gate. CancellationToken.None: dispatcher must NOT
                    // be cancelled by a per-request token (would break dispatch loop).
                    do! sem122b.WaitAsync(CancellationToken.None)
                    if t.Ct.IsCancellationRequested then
                        // Cancelled between dequeue and acquire — release immediately
                        sem122b.Release() |> ignore
                        t.Tcs.TrySetCanceled(t.Ct) |> ignore
                    else
                        t.Tcs.TrySetResult() |> ignore
                        // Caller now owns the slot; caller releases in finally.
    }

    do Task.Run(fun () -> dispatcherLoop () :> Task) |> ignore

    // Enqueue helper — parks caller on Tcs.Task; cancellation flows through Ct.Register.
    let enqueue122b (priority: Priority) (decision: RoutingDecision) (ct: CancellationToken) : Task<unit> =
        task {
            let tcs =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            let ticket =
                { Priority   = priority
                  Decision   = decision
                  Tcs        = tcs
                  Ct         = ct
                  EnqueuedAt = DateTimeOffset.UtcNow }
            lock queueLock (fun () ->
                match priority with
                | High -> highQueue.Enqueue(ticket)
                | Low  -> lowQueue.Enqueue(ticket))
            signal.Release() |> ignore
            // Cancellation while parked — propagates as OperationCanceledException
            use _ = ct.Register(fun () -> tcs.TrySetCanceled(ct) |> ignore)
            do! tcs.Task
        }

    let recordCompletion (waitMs: float) (durationMs: float) (success: bool) =
        let now = DateTimeOffset.UtcNow
        lock statsLock (fun () ->
            recentLatencies.Enqueue((now, durationMs))
            recentRequests.Enqueue(now)
            // Trim older than 60s
            let cutoff = now - TimeSpan.FromSeconds(60.0)
            while recentLatencies.Count > 0 && fst (recentLatencies.Peek()) < cutoff do
                recentLatencies.Dequeue() |> ignore
            while recentRequests.Count > 0 && recentRequests.Peek() < cutoff do
                recentRequests.Dequeue() |> ignore)
        if not success then
            Interlocked.Increment(&failureCount) |> ignore

    // ── Public properties (used by tests) ──────────────────────────────────
    member _.Semaphore     = sem122b
    member _.QueueDepthHigh = lock queueLock (fun () -> highQueue.Count)
    member _.QueueDepthLow  = lock queueLock (fun () -> lowQueue.Count)
    member _.Active122B    = Volatile.Read(&active122b)
    member _.Active35B     = Volatile.Read(&active35b)
    member _.FairnessPicksHigh = Volatile.Read(&fairnessPicksHigh)
    member _.FairnessPicksLow  = Volatile.Read(&fairnessPicksLow)

    interface IUpstreamClient with
        member _.CompleteAsync req decision ct =
            task {
                match decision.Target with
                | Qwen35B ->
                    Interlocked.Increment(&active35b) |> ignore
                    try
                        return! inner.CompleteAsync req decision ct
                    finally
                        Interlocked.Decrement(&active35b) |> ignore
                | Qwen122B ->
                    let enqueuedAt = DateTimeOffset.UtcNow
                    // Phase 1: queue wait — use raw client ct (no timeout yet).
                    do! enqueue122b decision.Priority decision ct
                    // Slot granted — sem122b is held by us.
                    let waitMs = (DateTimeOffset.UtcNow - enqueuedAt).TotalMilliseconds
                    let startedAt = DateTimeOffset.UtcNow
                    // Phase 2: linked CTS — timeout starts NOW, not at enqueue.
                    use timeoutCts =
                        new CancellationTokenSource(
                            TimeSpan.FromSeconds(float options.PerRequestTimeoutSeconds))
                    use linkedCts =
                        CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
                    Interlocked.Increment(&active122b) |> ignore
                    let mutable success = false
                    try
                        try
                            let! result = inner.CompleteAsync req decision linkedCts.Token
                            match result with
                            | Ok _    -> success <- true
                            | Error _ -> success <- false
                            return result
                        with
                        | :? OperationCanceledException when timeoutCts.IsCancellationRequested ->
                            return Error (ModelUnavailable (Qwen122B, "per-request timeout"))
                    finally
                        sem122b.Release() |> ignore
                        Interlocked.Decrement(&active122b) |> ignore
                        let durMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
                        recordCompletion waitMs durMs success
            }

        member _.StreamAsync req decision ct =
            match decision.Target with
            | Qwen35B ->
                taskSeq {
                    Interlocked.Increment(&active35b) |> ignore
                    try
                        for item in inner.StreamAsync req decision ct do
                            yield item
                    finally
                        Interlocked.Decrement(&active35b) |> ignore
                }
            | Qwen122B ->
                taskSeq {
                    let enqueuedAt = DateTimeOffset.UtcNow
                    do! enqueue122b decision.Priority decision ct
                    let waitMs = (DateTimeOffset.UtcNow - enqueuedAt).TotalMilliseconds
                    let startedAt = DateTimeOffset.UtcNow
                    use timeoutCts =
                        new CancellationTokenSource(
                            TimeSpan.FromSeconds(float options.PerRequestTimeoutSeconds))
                    use linkedCts =
                        CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
                    Interlocked.Increment(&active122b) |> ignore
                    let mutable success = true
                    try
                        try
                            for item in inner.StreamAsync req decision linkedCts.Token do
                                match item with
                                | Error _ -> success <- false
                                | _ -> ()
                                yield item
                        with
                        | :? OperationCanceledException when timeoutCts.IsCancellationRequested ->
                            success <- false
                            yield Error (ModelUnavailable (Qwen122B, "per-request timeout"))
                    finally
                        // taskSeq {} permits synchronous finally — Release() is sync.
                        sem122b.Release() |> ignore
                        Interlocked.Decrement(&active122b) |> ignore
                        let durMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
                        recordCompletion waitMs durMs success
                }

    interface IStatsProvider with
        member _.GetSnapshot () =
            let now = DateTimeOffset.UtcNow
            let avgLatency, requestsPerSec =
                lock statsLock (fun () ->
                    let cutoff = now - TimeSpan.FromSeconds(60.0)
                    while recentLatencies.Count > 0 && fst (recentLatencies.Peek()) < cutoff do
                        recentLatencies.Dequeue() |> ignore
                    while recentRequests.Count > 0 && recentRequests.Peek() < cutoff do
                        recentRequests.Dequeue() |> ignore
                    let avg =
                        if recentLatencies.Count = 0 then 0.0
                        else (recentLatencies |> Seq.sumBy snd) / float recentLatencies.Count
                    let rps = float recentRequests.Count / 60.0
                    avg, rps)
            let depthH, depthL =
                lock queueLock (fun () -> highQueue.Count, lowQueue.Count)
            { Timestamp           = now
              Active122B          = Volatile.Read(&active122b)
              QueueDepth122BHigh  = depthH
              QueueDepth122BLow   = depthL
              Active35B           = Volatile.Read(&active35b)
              RequestsPerSec      = requestsPerSec
              AvgLatencyMs60s     = avgLatency
              FailureCountTotal   = Volatile.Read(&failureCount)
              FairnessPicksHigh   = Volatile.Read(&fairnessPicksHigh)
              FairnessPicksLow    = Volatile.Read(&fairnessPicksLow)
              SemaphoreAvailable  = sem122b.CurrentCount }
```

Key correctness notes the executor MUST preserve:

1. **Sub-pattern A (PITFALL-9):** the dispatcher loop calls `sem122b.WaitAsync(CancellationToken.None)` BEFORE signalling the ticket's TCS. The waiting request never calls `WaitAsync` on the semaphore — it parks on `tcs.Task`. This is the only correct way to honor priority over FIFO.
2. **Linked CTS timing (PITFALL-11 variant):** `use timeoutCts = new CancellationTokenSource(...)` is created AFTER `enqueue122b` returns — never before. Queue wait MUST NOT burn the timeout budget.
3. **Try/finally Release (PITFALL-8):** the `try/finally` is entered ONLY after `enqueue122b` succeeds. If `enqueue122b` throws (cancellation while parked), the `try` is never entered, so `Release()` is never called on an unacquired semaphore.
4. **`taskSeq {}` finally is synchronous:** `sem122b.Release()` is sync (returns `int`); placing it in `taskSeq {}` finally is legal. Do NOT put `do!` in either `task {}` or `taskSeq {}` finally (FS0750 + the same issue Phase 2 hit).
5. **35B bypass:** the `match decision.Target with` is the FIRST line of both methods. 35B never touches `sem122b`, never enqueues, never goes through the dispatcher loop. Active35B is incremented for `/stats` only.
6. **MaxConcurrent122B validation:** the constructor `do` block throws `invalidOp` if the value is not 1.

**Step 2 — `src/SmartRouter.Cli/SmartRouter.Cli.fsproj`:** Add the new file to the `<Compile>` list AFTER `Adapters/QwenUpstreamClient.fs` and BEFORE `Endpoints/ChatCompletions.fs`:

```xml
<Compile Include="Adapters/QwenUpstreamClient.fs" />
<Compile Include="Adapters/QueueDispatcher.fs" />     <!-- NEW -->
<Compile Include="Endpoints/ChatCompletions.fs" />
```

**Step 3 — `src/SmartRouter.Cli/CompositionRoot.fs`:** Update DI registrations. Replace the existing `services.AddSingleton<IUpstreamClient>(...)` block with:

```fsharp
// Bind Queue section to QueueDispatcherOptions
services.Configure<QueueDispatcherOptions>(config.GetSection("Queue")) |> ignore

// Register concrete QwenUpstreamClient (not as IUpstreamClient — that's the dispatcher's job)
services.AddSingleton<QwenUpstreamClient>(fun sp ->
    QwenUpstreamClient(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IOptions<UpstreamOptions>>()))
    |> ignore

// QueueDispatcher wraps QwenUpstreamClient — registered as concrete singleton plus
// two interface registrations (IUpstreamClient for the endpoint; IStatsProvider for /stats)
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

Add `open SmartRouter.Cli.Adapters.QueueDispatcher` at the top of the file.

**Step 4 — `src/SmartRouter.Cli/Program.fs`:** Validate `QueueDispatcherOptions` after building the app, alongside the existing `validateConfig` call:

```fsharp
let queueOpts = app.Services.GetRequiredService<IOptions<QueueDispatcherOptions>>().Value
if queueOpts.MaxConcurrent122B <> 1 then
    failwithf
        "appsettings.json Queue.MaxConcurrent122B must be 1 in v1 (correctness invariant); got %d"
        queueOpts.MaxConcurrent122B
```

(The `QueueDispatcher` constructor also throws on bad values — this just gives a friendlier error before any HTTP call. Add `open SmartRouter.Cli.Adapters.QueueDispatcher` near the existing opens.)

**Step 5 — `src/SmartRouter.Cli/appsettings.json`:** Add the `Queue` section after the `Routing` section:

```json
  },
  "Queue": {
    "FairnessK": 10,
    "MaxConcurrent122B": 1,
    "PerRequestTimeoutSeconds": 300
  },
  "Serilog": {
```

**Step 6 — Build + test verification:** `dotnet build` must pass with zero warnings (TreatWarningsAsErrors is on). `dotnet test` must still pass — the existing 30 tests do not directly exercise QueueDispatcher (those come in 03-02), but they DO go through the wired-up DI graph in StreamingTests, so a broken DI registration will fail loudly.
  </action>
  <verify>
```
cd /Users/ohama/projs/smart-router
dotnet build SmartRouter.slnx 2>&1 | grep -E "(error|warning FS)"   # MUST be empty
dotnet test 2>&1 | tail -10                                          # 30/30 pass (port change + DI swap don't break existing)
ls -la src/SmartRouter.Cli/Adapters/QueueDispatcher.fs               # exists
wc -l src/SmartRouter.Cli/Adapters/QueueDispatcher.fs                # >= 200 lines
grep -n "SemaphoreSlim(1, 1)" src/SmartRouter.Cli/Adapters/QueueDispatcher.fs   # 1 hit
grep -n "CreateLinkedTokenSource" src/SmartRouter.Cli/Adapters/QueueDispatcher.fs   # >= 2 hits (Complete + Stream)
grep -nE "finally\s*$" src/SmartRouter.Cli/Adapters/QueueDispatcher.fs            # >= 2 hits (try/finally Release)
grep -n "sem122b\.Release()" src/SmartRouter.Cli/Adapters/QueueDispatcher.fs     # >= 3 hits
grep -n "FairnessK" src/SmartRouter.Cli/Adapters/QueueDispatcher.fs              # multiple hits (option + dequeue logic)
grep -n "MaxConcurrent122B" src/SmartRouter.Cli/Adapters/QueueDispatcher.fs      # 1+ hits (validation)
grep -nE 'Qwen35B\s*->\s*$' src/SmartRouter.Cli/Adapters/QueueDispatcher.fs      # bypass arms (2 — one per method)
grep -n "AddSingleton<QueueDispatcher>" src/SmartRouter.Cli/CompositionRoot.fs   # 1 hit
grep -n '"Queue"' src/SmartRouter.Cli/appsettings.json                            # 1 hit
grep -nE '"FairnessK"\s*:\s*10' src/SmartRouter.Cli/appsettings.json              # 1 hit
grep -nE '"MaxConcurrent122B"\s*:\s*1' src/SmartRouter.Cli/appsettings.json       # 1 hit
grep -nE '"PerRequestTimeoutSeconds"\s*:\s*300' src/SmartRouter.Cli/appsettings.json   # 1 hit
./scripts/check-no-async.sh                                                       # PASS (Core stays pure)
```
  </verify>
  <done>
- `src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` exists with `QueueDispatcher`, `QueueDispatcherOptions`, `StatsSnapshot`, `IStatsProvider` types.
- `SemaphoreSlim(1, 1)` field, two `Queue<Ticket>` (high/low), single dispatcher loop using sub-pattern A, `enqueue122b` parks caller on `TaskCompletionSource`, fairness counter K from options gates low-priority pick after K high picks.
- Linked CTS created AFTER `enqueue122b` returns in both `CompleteAsync` and `StreamAsync` 122B branches.
- Both methods bypass to `inner.*Async req decision ct` directly when `decision.Target = Qwen35B`.
- `try/finally sem122b.Release() |> ignore` wraps the upstream call in both methods; `taskSeq {}` finally release is synchronous (legal).
- `MaxConcurrent122B <> 1` rejected at constructor and at startup in Program.fs.
- `appsettings.json` has the `Queue` section with the three keys.
- `CompositionRoot.fs` registers `QwenUpstreamClient` concrete + `QueueDispatcher` singleton + `IUpstreamClient` and `IStatsProvider` both delegating to QueueDispatcher.
- `dotnet build` clean. `dotnet test` still 30/30 (existing tests pass through the new DI graph).
- `check-no-async.sh` passes (no Core changes besides the Ports.fs port-shape update).
  </done>
</task>

</tasks>

<verification>
After both tasks complete:

```bash
cd /Users/ohama/projs/smart-router
dotnet build SmartRouter.slnx                                                # zero errors, zero warnings
dotnet test 2>&1 | tail -5                                                    # 30/30 pass
./scripts/check-no-async.sh                                                   # OK
grep -nE 'CompleteAsync\s*:\s*RouterRequest\s*->\s*decision\s*:\s*RoutingDecision' src/SmartRouter.Core/Ports.fs   # match
grep -n "QueueDispatcher" src/SmartRouter.Cli/SmartRouter.Cli.fsproj          # 1 hit (Compile entry)
grep -c "decision\.Target" src/SmartRouter.Cli/Adapters/QueueDispatcher.fs    # >= 4 (bypass + dispatcher checks)
```

The phase is in a deployable state: a 122B request now goes Cli → ChatCompletions → IUpstreamClient (= QueueDispatcher) → enqueue → dispatcher signals → semaphore acquire → inner.CompleteAsync (QwenUpstreamClient) → release in finally. A 35B request short-circuits at the dispatcher's first match arm.
</verification>

<success_criteria>
- IUpstreamClient port shape changed: both methods take `decision: RoutingDecision`.
- QueueDispatcher.fs created with SemaphoreSlim(1), two-level priority queue, fairness counter, linked CTS (timeout from acquire), try/finally Release, 35B bypass, IStatsProvider counters.
- CompositionRoot wires QueueDispatcher as IUpstreamClient + IStatsProvider; QwenUpstreamClient is now an internal singleton wrapped by the dispatcher.
- appsettings.json has Queue section; Program.fs validates MaxConcurrent122B = 1.
- All existing tests (30/30) still pass; build and check-no-async.sh stay green.
- The ATOMIC CLUSTER ships in one plan: semaphore + queue + linked CTS + Release discipline + cancellation handling are coupled at compile time (one Adapters file, one DI registration) — there is no half-way state.
</success_criteria>

<output>
After completion, create `.planning/phases/03-122b-concurrency-gate/03-01-SUMMARY.md` documenting:
- Final IUpstreamClient signature
- QueueDispatcher type signature and key invariants (sub-pattern A, linked CTS timing, fairness K, MaxConcurrent122B validation)
- DI graph after the swap
- Pitfalls 8/9/10/11 each mapped to the line(s) of code that mitigate them
- Any deviations from the plan (e.g., if PriorityQueue<T,int> was used instead of two Queue<Ticket>, or if rolling-window stats implementation differs)
</output>
</content>
</invoke>