module SmartRouter.Cli.Adapters.QueueDispatcher

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open Serilog
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports

// ── Options ───────────────────────────────────────────────────────────────────

/// Configuration bound from appsettings.json "Queue" section.
[<CLIMutable>]
type QueueDispatcherOptions =
    { FairnessK                : int   // after K consecutive High picks, force one Low (default 10)
      MaxConcurrent122B        : int   // MUST be 1 in v1; validated at startup
      PerRequestTimeoutSeconds : int } // per-request timeout from semaphore acquire (default 300)

// ── Stats ─────────────────────────────────────────────────────────────────────

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

// ── Ticket ────────────────────────────────────────────────────────────────────

type private Ticket =
    { Priority   : Priority
      Decision   : RoutingDecision
      Tcs        : TaskCompletionSource<unit>
      Ct         : CancellationToken
      EnqueuedAt : DateTimeOffset }

// ── QueueDispatcher ───────────────────────────────────────────────────────────

/// Wraps any IUpstreamClient with a SemaphoreSlim(1) gate for Qwen 122B.
///
/// Correctness invariants (all four PITFALL mitigations ship atomically):
///   PITFALL-8 (leak on cancel): try/finally Release entered ONLY after enqueue122b succeeds.
///   PITFALL-9 (FIFO bypasses priority): dispatcher acquires semaphore, then signals TCS
///     (sub-pattern A). Individual requests park on tcs.Task, not sem.WaitAsync.
///   PITFALL-10 (starvation): fairness counter K: after K consecutive High picks,
///     forces one Low pick if any Low items are waiting.
///   PITFALL-11 (hung upstream): linked CTS timeout starts AFTER enqueue122b returns
///     (after slot is granted), NOT at enqueue time.
///
/// 35B bypass: match decision.Target with | Qwen35B -> inner.* directly (first line of both
/// IUpstreamClient methods). 35B never touches sem122b or the queues.
///
/// Release discipline:
///   task {} finally: sem122b.Release() is synchronous — always legal in task {} finally.
///   taskSeq {} finally: sem122b.Release() is synchronous — legal in taskSeq {} finally too.
///   Neither uses do! in finally (FS0750 / Phase-2 pitfall does not apply here).
type QueueDispatcher
    ( inner       : IUpstreamClient
    , options     : QueueDispatcherOptions
    , healthProbe : IHealthProbe ) =

    // Startup validation — correctness invariant for current Qwen rig.
    do
        if options.MaxConcurrent122B <> 1 then
            invalidOp
                (sprintf "Queue.MaxConcurrent122B must be 1 in v1 (correctness invariant); got %d"
                    options.MaxConcurrent122B)

    // ── Semaphore + queues ────────────────────────────────────────────────────

    let sem122b   = new SemaphoreSlim(1, 1)
    let highQueue = Queue<Ticket>()
    let lowQueue  = Queue<Ticket>()
    let queueLock = obj()
    let signal    = new SemaphoreSlim(0)
    let mutable consecutiveHighPicks = 0

    // ── Counters ──────────────────────────────────────────────────────────────

    let mutable active122b        = 0
    let mutable active35b         = 0
    let mutable failureCount      = 0L
    let mutable fairnessPicksHigh = 0L
    let mutable fairnessPicksLow  = 0L

    // Rolling-window stats: lock-protected per-second buckets trimmed on read.
    // Latency samples and request timestamps kept for up to 60s.
    let recentLatencies = Queue<DateTimeOffset * float>()
    let recentRequests  = Queue<DateTimeOffset>()
    let statsLock = obj()

    // ── Fairness dequeue ──────────────────────────────────────────────────────

    // After K consecutive High picks, force one Low pick if any Low items are waiting.
    // If no Low items are waiting (forced or not), fall back to High.
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
                // Force-low was requested but no low items exist — take High.
                let t = highQueue.Dequeue()
                consecutiveHighPicks <- consecutiveHighPicks + 1
                Interlocked.Increment(&fairnessPicksHigh) |> ignore
                Some t
            else
                None)

    // ── Dispatcher loop — sub-pattern A ──────────────────────────────────────
    //
    // Sub-pattern A (PITFALL-9): the dispatcher acquires sem122b BEFORE signalling
    // the ticket's TCS. The waiting request never calls WaitAsync on the semaphore —
    // it parks on tcs.Task. This is the only correct way to honor priority over FIFO.
    // CancellationToken.None: the dispatcher loop must NOT be cancelled by a per-request
    // token (that would break dispatch for all other requests).

    let dispatcherLoop () = task {
        while true do
            do! signal.WaitAsync()
            match tryDequeueNext () with
            | None -> ()
            | Some t ->
                if t.Ct.IsCancellationRequested then
                    // Already cancelled while queued — discard, no semaphore acquired.
                    Log.Debug("QueueDispatcher: discarding cancelled queued request")
                    t.Tcs.TrySetCanceled(t.Ct) |> ignore
                else
                    // Acquire the gate. CancellationToken.None: dispatcher must NOT be
                    // cancelled by a per-request token — would break the dispatch loop.
                    do! sem122b.WaitAsync(CancellationToken.None)
                    if t.Ct.IsCancellationRequested then
                        // Cancelled between dequeue and acquire — release immediately.
                        sem122b.Release() |> ignore
                        t.Tcs.TrySetCanceled(t.Ct) |> ignore
                    else
                        // Signal waiter — caller now owns the slot and MUST Release in finally.
                        t.Tcs.TrySetResult() |> ignore
    }

    do Task.Run(fun () -> dispatcherLoop () :> Task) |> ignore

    // ── enqueue122b — parks caller on TCS ─────────────────────────────────────
    //
    // Enqueues the request ticket, signals the dispatcher, then parks on tcs.Task.
    // If ct fires while parked, TrySetCanceled propagates as OperationCanceledException,
    // which unwinds the caller WITHOUT releasing the semaphore (it was never acquired).
    //
    // Key structural guarantee (PITFALL-8):
    //   The try/finally Release in CompleteAsync/StreamAsync is entered ONLY if
    //   enqueue122b returns successfully. If it throws (cancellation), the try is never
    //   entered, so Release() is never called on an unacquired semaphore.

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
            // Cancellation registration — if ct fires while parked, cancel the TCS.
            use _ = ct.Register(fun () -> tcs.TrySetCanceled(ct) |> ignore)
            do! tcs.Task
            // At this point: sem122b is held by the dispatcher and slot is ours.
            // Caller MUST Release in finally.
        }

    // ── Stats recording ───────────────────────────────────────────────────────

    let recordCompletion (durationMs: float) (success: bool) =
        let now = DateTimeOffset.UtcNow
        lock statsLock (fun () ->
            recentLatencies.Enqueue((now, durationMs))
            recentRequests.Enqueue(now)
            // Trim entries older than 60s on write (amortized; also trimmed on read).
            let cutoff = now - TimeSpan.FromSeconds(60.0)
            while recentLatencies.Count > 0 && fst (recentLatencies.Peek()) < cutoff do
                recentLatencies.Dequeue() |> ignore
            while recentRequests.Count > 0 && recentRequests.Peek() < cutoff do
                recentRequests.Dequeue() |> ignore)
        if not success then
            Interlocked.Increment(&failureCount) |> ignore

    // ── Public diagnostic properties (used by tests) ──────────────────────────

    member _.Semaphore       = sem122b
    member _.QueueDepthHigh  = lock queueLock (fun () -> highQueue.Count)
    member _.QueueDepthLow   = lock queueLock (fun () -> lowQueue.Count)
    member _.Active122B      = Volatile.Read(&active122b)
    member _.Active35B       = Volatile.Read(&active35b)
    member _.FairnessPicksHigh = Volatile.Read(&fairnessPicksHigh)
    member _.FairnessPicksLow  = Volatile.Read(&fairnessPicksLow)

    // ── IUpstreamClient ───────────────────────────────────────────────────────

    interface IUpstreamClient with

        /// 35B: bypass queue entirely — no semaphore, no queuing.
        /// 122B: enqueue → wait for slot (sub-pattern A) → linked CTS (timeout starts
        ///        AFTER slot granted) → upstream call → Release in finally.
        member _.CompleteAsync req decision ct =
            task {
                // ── Phase 10: Fallback policy (BEFORE existing match) ────────────────
                let isGraphIndexing =
                    req.Task
                    |> Option.map (fun t -> t.Trim().ToLowerInvariant())
                    |> (=) (Some "graph_indexing")

                let decision =   // shadows the parameter
                    if decision.Target = Qwen122B
                       && not (healthProbe.IsReachable(Qwen122B))
                       && not isGraphIndexing then
                        Log.Warning(
                            "QueueDispatcher.CompleteAsync: 122B unreachable; rerouting task={Task} to 35B (fallback)",
                            req.Task)
                        { decision with
                            Target     = Qwen35B
                            Reason     = FallbackTo35B
                            IsFallback = true }
                    else
                        decision

                // graph_indexing-must-fail short-circuit (defensive — ChatCompletions pre-flight should
                // have already returned 503; this catches non-HTTP callers like integration tests).
                if decision.Target = Qwen122B
                   && isGraphIndexing
                   && not (healthProbe.IsReachable(Qwen122B)) then
                    return Error GraphIndexingMustFail
                else

                match decision.Target with
                | Qwen35B ->
                    // 35B bypass: first line, no gate.
                    Interlocked.Increment(&active35b) |> ignore
                    try
                        return! inner.CompleteAsync req decision ct
                    finally
                        Interlocked.Decrement(&active35b) |> ignore

                | Qwen122B ->
                    let startedAt = DateTimeOffset.UtcNow
                    // Phase 1: queue wait — use raw client ct (no timeout yet).
                    // PITFALL-11: timeout MUST start after enqueue returns, not before.
                    do! enqueue122b decision.Priority decision ct
                    // Slot granted — sem122b is held by us.

                    // Phase 2: linked CTS — timeout starts NOW (after slot is granted).
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
                            // Upstream hung past PerRequestTimeoutSeconds — return explicit error.
                            return Error (ModelUnavailable (Qwen122B, "per-request timeout"))
                    finally
                        // PITFALL-8: Release is synchronous — always legal in task {} finally.
                        // This arm runs on success, exception, and cancellation.
                        sem122b.Release() |> ignore
                        Interlocked.Decrement(&active122b) |> ignore
                        let durMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
                        recordCompletion durMs success
            }

        /// 35B: bypass queue entirely.
        /// 122B: enqueue → wait for slot → linked CTS → stream inner → Release in finally.
        /// The semaphore slot is held for the ENTIRE stream duration (slot-holding requirement).
        /// Release fires when the consumer disposes the enumerator (ChatCompletions.fs disposes
        /// in all three exit arms: normal, cancel, error — verified in Phase 2 VERIFICATION.md).
        member _.StreamAsync req decision ct =
            let isGraphIndexing =
                req.Task
                |> Option.map (fun t -> t.Trim().ToLowerInvariant())
                |> (=) (Some "graph_indexing")

            let decision =   // shadows the parameter (same shape as CompleteAsync)
                if decision.Target = Qwen122B
                   && not (healthProbe.IsReachable(Qwen122B))
                   && not isGraphIndexing then
                    Log.Warning(
                        "QueueDispatcher.StreamAsync: 122B unreachable; rerouting task={Task} to 35B (fallback)",
                        req.Task)
                    { decision with
                        Target     = Qwen35B
                        Reason     = FallbackTo35B
                        IsFallback = true }
                else
                    decision

            // graph_indexing-must-fail: yield single Error and end the sequence.
            if decision.Target = Qwen122B
               && isGraphIndexing
               && not (healthProbe.IsReachable(Qwen122B)) then
                taskSeq {
                    yield Error GraphIndexingMustFail
                }
            else

            match decision.Target with
            | Qwen35B ->
                // 35B bypass: no gate.
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
                    let startedAt = DateTimeOffset.UtcNow
                    // Phase 1: queue wait — no timeout yet (PITFALL-11).
                    do! enqueue122b decision.Priority decision ct
                    // Slot granted.

                    // Phase 2: linked CTS — timeout starts NOW.
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
                                | Ok _    -> ()
                                yield item
                        with
                        | :? OperationCanceledException when timeoutCts.IsCancellationRequested ->
                            success <- false
                            yield Error (ModelUnavailable (Qwen122B, "per-request timeout"))
                    finally
                        // taskSeq {} permits synchronous finally — Release() is sync, safe here.
                        // PITFALL-8: slot released in all exit paths (normal, cancel, error).
                        sem122b.Release() |> ignore
                        Interlocked.Decrement(&active122b) |> ignore
                        let durMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
                        recordCompletion durMs success
                }

    // ── IStatsProvider ────────────────────────────────────────────────────────

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
