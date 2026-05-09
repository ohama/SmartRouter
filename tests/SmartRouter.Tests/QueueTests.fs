module SmartRouter.Tests.QueueTests

open System
open System.Collections.Generic
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.DependencyInjection
open Expecto
open FSharp.Control
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports
open SmartRouter.Cli.Adapters.QueueDispatcher

// ── Helpers ──────────────────────────────────────────────────────────────────

let private mkDecision (target: ModelId) (priority: Priority) : RoutingDecision =
    { Target       = target
      Priority     = priority
      Reason       = Default
      IsFallback   = false
      ModelVersion = "" }

let private emptyRequest : RouterRequest =
    { Messages      = [{ Role = User; Content = "test" }]
      ModelOverride  = None
      Task           = None
      Stream         = false
      Temperature    = None
      TopP           = None
      MaxTokens      = None
      CorrelationId  = ""
      UnknownFields  = Map.empty }

let private defaultOpts =
    { FairnessK                = 10
      MaxConcurrent122B        = 1
      PerRequestTimeoutSeconds = 30 }

/// Always-reachable health probe stub for test construction sites.
/// Makes the Phase 10 fallback policy a no-op (all upstreams appear reachable).
let alwaysReachableProbe : IHealthProbe =
    { new IHealthProbe with
        member _.IsReachable _ = true
        member _.IsReachableAsync target _ct = Task.FromResult(true)
        member _.LastProbedAt _ = DateTimeOffset.MinValue }

/// Gate-per-call fake. Each call parks on its own TaskCompletionSource until
/// `ReleaseCall(idx)` fires. Records start/end timestamps. Use this when you
/// want to control EXACTLY when each upstream call completes (e.g., to keep a
/// slot held while assertions run). DO NOT use this for tests that enqueue
/// more requests than you'll release — leftover gates park forever and
/// `Task.WhenAll` will hang.
type FakeUpstreamClient(?defaultLatencyMs: int) =
    let starts    = List<DateTimeOffset>()
    let ends      = List<DateTimeOffset>()
    let callGates = List<TaskCompletionSource<unit>>()
    let lck       = obj()
    let mutable callCount = 0

    member _.CallCount = Volatile.Read(&callCount)
    member _.Starts = lock lck (fun () -> starts |> List.ofSeq)
    member _.Ends   = lock lck (fun () -> ends   |> List.ofSeq)

    member _.ReleaseCall(idx: int) =
        lock lck (fun () ->
            if idx < callGates.Count then
                callGates.[idx].TrySetResult() |> ignore)

    interface IUpstreamClient with
        member _.CompleteAsync req decision ct =
            task {
                let gate =
                    TaskCompletionSource<unit>(
                        TaskCreationOptions.RunContinuationsAsynchronously)
                lock lck (fun () ->
                    callGates.Add(gate)
                    starts.Add(DateTimeOffset.UtcNow)
                    Interlocked.Increment(&callCount) |> ignore)

                match defaultLatencyMs with
                | Some ms when ms > 0 ->
                    do! Task.Delay(ms, ct)
                | _ ->
                    use _ = ct.Register(fun () -> gate.TrySetCanceled(ct) |> ignore)
                    do! gate.Task

                lock lck (fun () -> ends.Add(DateTimeOffset.UtcNow))
                let modelTag =
                    match decision.Target with
                    | Qwen35B  -> "35b"
                    | Qwen122B -> "122b"
                return Ok (sprintf """{"choices":[{"message":{"content":"fake-%s"}}]}""" modelTag)
            }

        member _.StreamAsync req decision ct =
            taskSeq {
                yield Ok "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}"
                yield Ok "data: [DONE]"
            }

/// Auto-completing fake — every call sleeps `latencyMs` then returns Ok. No
/// gates. Use this for tests where every queued request must eventually drain
/// on its own (e.g., fairness, where K+1 highs + 1 low all must complete to
/// observe the K-th forced-low pick). Records start/end timestamps.
type LatencyFake(latencyMs: int) =
    let starts = List<DateTimeOffset>()
    let ends   = List<DateTimeOffset>()
    let lck    = obj()

    member _.Starts = lock lck (fun () -> starts |> List.ofSeq)
    member _.Ends   = lock lck (fun () -> ends   |> List.ofSeq)

    interface IUpstreamClient with
        member _.CompleteAsync req decision ct =
            task {
                lock lck (fun () -> starts.Add(DateTimeOffset.UtcNow))
                do! Task.Delay(latencyMs, ct)
                lock lck (fun () -> ends.Add(DateTimeOffset.UtcNow))
                return Ok """{"choices":[{"message":{"content":"latency"}}]}"""
            }
        member _.StreamAsync req decision ct =
            taskSeq { yield Ok "data: [DONE]" }

// ── Tests ────────────────────────────────────────────────────────────────────

let tests =
    testSequenced <| testList "queue" [

        // ── Test 1: serialization (CONC-01) — gate fake to inspect each call individually ──
        testCaseAsync "five concurrent 122B requests serialize through SemaphoreSlim(1) (CONC-01)" <| async {
            let fake       = FakeUpstreamClient()
            let dispatcher = QueueDispatcher(fake, defaultOpts, alwaysReachableProbe) :> IUpstreamClient
            let dec        = mkDecision Qwen122B Low

            let runOne () =
                task { return! dispatcher.CompleteAsync emptyRequest dec CancellationToken.None }
            let tasks = [1..5] |> List.map (fun _ -> runOne ())
            do! Async.Sleep 80   // allow all five to enqueue

            // For each call, only one should be active in the upstream
            for i in 0..4 do
                Expect.equal (fake.CallCount - i) 1
                    (sprintf "iteration %d: only one upstream call should be in flight" i)
                fake.ReleaseCall(i)
                do! Async.Sleep 30

            let! _ = Task.WhenAll(tasks |> List.map (fun t -> t :> Task)) |> Async.AwaitTask
            Expect.equal fake.CallCount 5 "all five upstream calls completed"

            // Strict serialization: each call's start must be >= previous call's end
            let starts = fake.Starts
            let ends   = fake.Ends
            for i in 1..4 do
                Expect.isTrue (starts.[i] >= ends.[i-1])
                    (sprintf "call %d started before call %d ended (concurrent overlap)" i (i-1))
        }

        // ── Test 2: priority preempt (CONC-02 / PITFALL-9) ──
        // Design: occupy holds the slot. HIGH is enqueued, dispatcher dequeues it and
        // blocks on sem.WaitAsync. LOW is enqueued (signal accumulates, dispatcher still
        // blocked on sem for HIGH). When occupy releases: HIGH gets the slot (it was
        // dequeued first and is waiting). When HIGH releases: dispatcher dequeues LOW.
        // Result: occupy → HIGH → LOW (high runs before low). This proves PITFALL-9:
        // even though LOW is in the queue, HIGH runs first because the dispatcher
        // selected HIGH from the priority queue BEFORE LOW's signal was processed.
        //
        // Gate sequence: 0=occupy, 1=HIGH, 2=LOW
        testCaseAsync "high-priority 122B request preempts queued low-priority (CONC-02 / PITFALL-9)" <| async {
            let fake       = FakeUpstreamClient()
            let qd         = QueueDispatcher(fake, defaultOpts, alwaysReachableProbe)
            let dispatcher = qd :> IUpstreamClient
            let order      = List<string>()
            let orderLck   = obj()

            // Occupy takes the slot
            let occupy =
                Task.Run(fun () ->
                    task {
                        let! _ = dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B Low) CancellationToken.None
                        lock orderLck (fun () -> order.Add("occupy"))
                    } :> Task)

            // Poll until occupy is in the upstream gate (CallCount=1)
            let mutable elapsed = 0
            while fake.CallCount < 1 && elapsed < 2000 do
                do! Async.Sleep 10
                elapsed <- elapsed + 10
            Expect.equal fake.CallCount 1 "occupy must be in upstream before enqueueing"

            // Enqueue HIGH first — dispatcher dequeues HIGH, blocks on sem (occupy holds it)
            let highTask =
                Task.Run(fun () ->
                    task {
                        let! _ = dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B High) CancellationToken.None
                        lock orderLck (fun () -> order.Add("high"))
                    } :> Task)

            // Poll until HIGH is dequeued (QueueDepthHigh drops to 0 = HIGH is in-flight
            // waiting for sem) AND dispatcher is blocked on sem (CurrentCount=0)
            elapsed <- 0
            while (qd.QueueDepthHigh > 0) && elapsed < 2000 do
                do! Async.Sleep 10
                elapsed <- elapsed + 10

            // Now enqueue LOW — signal accumulates (dispatcher is stuck on sem for HIGH)
            let lowTask =
                Task.Run(fun () ->
                    task {
                        let! _ = dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B Low) CancellationToken.None
                        lock orderLck (fun () -> order.Add("low"))
                    } :> Task)
            do! Async.Sleep 30  // let LOW enqueue and its signal accumulate

            // Release occupy → HIGH gets slot (call 1); then LOW gets slot (call 2)
            fake.ReleaseCall(0)   // occupy done → HIGH acquires sem
            do! Async.Sleep 60
            fake.ReleaseCall(1)   // HIGH done → LOW acquires sem
            do! Async.Sleep 60
            fake.ReleaseCall(2)   // LOW done

            let! _ = Task.WhenAll([occupy; highTask; lowTask]) |> Async.AwaitTask
            let observed = lock orderLck (fun () -> order |> List.ofSeq)
            Expect.equal observed ["occupy"; "high"; "low"]
                "high-priority was dispatched before low-priority (CONC-02 / PITFALL-9)"
        }

        // ── Test 3: PITFALL-10 STARVATION PREVENTION (the K-th forced-low pick fires) ──
        // Restructured per checker Blocker 1+2: 4 highs + 1 low, K=3, LatencyFake (no gate
        // deadlock). The K-th pick MUST be a low (forced) while highs are still queued.
        // This proves PITFALL-10 (starvation prevention), not just priority ordering.
        testCaseAsync "PITFALL-10 starvation: K-th forced-low pick fires while highs still queued (CONC-03)" <| async {
            // K=3: after 3 consecutive high picks, the dispatcher MUST force a low pick
            // even though more highs are queued. This is the starvation-prevention invariant.
            let opts       = { defaultOpts with FairnessK = 3 }
            let latency    = LatencyFake(30)        // auto-completing — no gate deadlock
            let qd         = QueueDispatcher(latency, opts, alwaysReachableProbe)
            let dispatcher = qd :> IUpstreamClient
            let order      = List<string>()
            let orderLck   = obj()

            let mkLabelled label prio =
                Task.Run(fun () ->
                    task {
                        let! _ = dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B prio) CancellationToken.None
                        lock orderLck (fun () -> order.Add(label))
                    } :> Task)

            // Occupy the slot with one in-flight call so the next 5 must queue
            let occupy = mkLabelled "occupy" Low
            // Wait until occupy has entered the upstream (Starts.Length=1 means occupy is
            // in Task.Delay and holds the semaphore). LatencyFake records Starts before delay.
            let mutable elapsed = 0
            while latency.Starts.Length < 1 && elapsed < 2000 do
                do! Async.Sleep 5
                elapsed <- elapsed + 5
            Expect.equal latency.Starts.Length 1 "occupy must be in upstream (started in LatencyFake)"

            // Enqueue 4 highs + 1 low SIMULTANEOUSLY (no inter-enqueue sleeps for ordering;
            // all of them are racing into the queue while occupy holds the slot).
            let high1 = mkLabelled "high1" High
            let high2 = mkLabelled "high2" High
            let high3 = mkLabelled "high3" High
            let low1  = mkLabelled "low1"  Low
            let high4 = mkLabelled "high4" High

            // Wait a brief moment for all 5 tasks to get scheduled and enqueue.
            // We don't assert a specific depth since the dispatcher may dequeue some
            // before we poll. The correctness proof is in the COMPLETION ORDER below.
            do! Async.Sleep 30

            // Now drain — LatencyFake completes each call after 30ms. occupy finishes,
            // then the dispatcher picks: high (1), high (2), high (3) — K=3 reached —
            // FORCED low (low1) — high (4). After all six complete:
            let! _ = Task.WhenAll([occupy; high1; high2; high3; low1; high4]) |> Async.AwaitTask
            let observed = lock orderLck (fun () -> order |> List.ofSeq)

            // Assert the dispatch positions:
            //   position 0: occupy (always first — already running)
            //   positions 1..3: highs in some order (FIFO within high queue) — NOT low1
            //   position 4: low1 (the K-th forced-low pick — THE INVARIANT THAT PROVES PITFALL-10)
            //   position 5: high4 (only remaining waiter)
            Expect.equal observed.Length 6 "all six waiters completed"
            Expect.equal observed.[0] "occupy" "occupy ran first"

            let low1Idx  = observed |> List.findIndex ((=) "low1")
            let high4Idx = observed |> List.findIndex ((=) "high4")

            // The CRITICAL assertion: low1 ran at position 4 (zero-indexed) — i.e., AFTER
            // exactly 3 highs ran first, BEFORE the 4th high. Without fairness, low1 would
            // sit at position 5 (after all 4 highs). This is the K-th forced-low pick.
            Expect.equal low1Idx 4
                "low1 ran at position 4 (index 4) — the K-th forced-low pick fired AFTER 3 highs and BEFORE high4 — PITFALL-10 mitigation proven"
            Expect.isLessThan low1Idx high4Idx
                "low1 ran BEFORE high4 — fairness pivot occurred while highs were still queued"

            // Counter assertions: 3 highs picked normally + 1 high after the forced-low pick = 4 high picks; 1 forced-low pick.
            Expect.equal qd.FairnessPicksHigh 4L "exactly 4 high picks recorded"
            Expect.isGreaterThanOrEqual qd.FairnessPicksLow 1L "at least 1 forced-low pick recorded"
        }

        // ── Test 4: PITFALL-8 cancellation while still QUEUED (pre-dequeue) ──
        testCaseAsync "PITFALL-8 cancellation while queued does NOT leak the semaphore (CONC-05)" <| async {
            let fake       = FakeUpstreamClient()
            let qd         = QueueDispatcher(fake, defaultOpts, alwaysReachableProbe)
            let dispatcher = qd :> IUpstreamClient
            use cts        = new CancellationTokenSource()

            // Occupy the slot
            let occupy =
                Task.Run(fun () ->
                    dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B Low) CancellationToken.None
                    :> Task)
            do! Async.Sleep 50

            // Enqueue a request with a cancellable CT — it will park in the queue
            let cancelled =
                Task.Run(fun () ->
                    task {
                        try
                            let! _ = dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B Low) cts.Token
                            ()
                        with :? OperationCanceledException -> ()
                    } :> Task)
            do! Async.Sleep 50

            // Cancel WHILE STILL QUEUED — semaphore should not have been acquired by this request
            cts.Cancel()
            do! Async.Sleep 50

            // Slot is occupied by `occupy`; semaphore count is 0 — but NOT leaked.
            // The cancelled queued request never entered the try/finally, so Release was never wrongly called.
            Expect.equal qd.Semaphore.CurrentCount 0
                "semaphore is held by the occupying request only — cancelled queued request never acquired it"

            // Release occupy: semaphore returns to 1
            fake.ReleaseCall(0)
            let! _ = Task.WhenAll([occupy; cancelled]) |> Async.AwaitTask
            do! Async.Sleep 30
            Expect.equal qd.Semaphore.CurrentCount 1
                "semaphore returned to 1 after occupy completed (no cancellation leak)"
        }

        // ── Test 5: NEW per checker Blocker 5 — cancellation AFTER dequeue, mid-acquire ──
        // This is the OTHER cancellation path: ticket has been dequeued by the dispatcher
        // loop and the dispatcher is parked on `sem.WaitAsync(CancellationToken.None)`.
        // The client CT then fires. When `occupy` releases the semaphore, the dispatcher
        // acquires it for the cancelled ticket, sees `t.Ct.IsCancellationRequested = true`,
        // releases the semaphore immediately, and signals TrySetCanceled on the ticket.
        // The semaphore must end at CurrentCount=1 with no leak.
        testCaseAsync "PITFALL-8 cancellation AFTER dequeue mid-acquire releases the slot cleanly" <| async {
            let fake       = FakeUpstreamClient()
            let qd         = QueueDispatcher(fake, defaultOpts, alwaysReachableProbe)
            let dispatcher = qd :> IUpstreamClient
            use victimCts  = new CancellationTokenSource()

            // 1. Occupy the slot — sem.CurrentCount goes to 0.
            let occupy =
                Task.Run(fun () ->
                    dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B Low) CancellationToken.None
                    :> Task)
            do! Async.Sleep 50  // let occupy acquire the slot

            // 2. Submit `victim` — it parks in the queue. The dispatcher loop wakes (via
            //    signal.Release in enqueue), tryDequeueNext returns the victim ticket,
            //    and the dispatcher then calls sem.WaitAsync(CancellationToken.None) —
            //    BLOCKED because occupy still holds the slot. This is the "dequeued
            //    but not yet acquired" state.
            let victim =
                Task.Run(fun () ->
                    task {
                        try
                            let! _ = dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B Low) victimCts.Token
                            ()
                        with :? OperationCanceledException -> ()
                    } :> Task)
            do! Async.Sleep 50  // dispatcher loop dequeues victim, blocks on sem.WaitAsync

            // 3. Cancel the victim's CT WHILE the dispatcher is mid-acquire.
            victimCts.Cancel()
            do! Async.Sleep 30  // let the cancellation propagate

            // 4. Release occupy — sem.WaitAsync(None) on the dispatcher returns; the
            //    dispatcher checks t.Ct.IsCancellationRequested (true), releases sem
            //    immediately, signals TrySetCanceled. CurrentCount returns to 1.
            fake.ReleaseCall(0)
            let! _ = Task.WhenAll([occupy; victim]) |> Async.AwaitTask
            do! Async.Sleep 50

            Expect.equal qd.Semaphore.CurrentCount 1
                "post-dequeue-pre-acquire cancellation: semaphore released cleanly (no leak)"
            Expect.isTrue victim.IsCompleted "victim task completed (cancellation observed)"
        }

        // ── Test 6: PITFALL-11 hung upstream releases via per-request timeout (REL-05) ──
        testCaseAsync "PITFALL-11 hung upstream releases the slot via per-request timeout (REL-05)" <| async {
            // FakeUpstreamClient with no released gates — every call hangs forever
            let fake       = FakeUpstreamClient()
            // Short timeout so the test runs quickly
            let qd         = QueueDispatcher(fake, { defaultOpts with PerRequestTimeoutSeconds = 1 }, alwaysReachableProbe)
            let dispatcher = qd :> IUpstreamClient

            let! result =
                dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B Low) CancellationToken.None
                |> Async.AwaitTask

            match result with
            | Error (ModelUnavailable (Qwen122B, _)) -> ()  // expected
            | other -> failtestf "expected ModelUnavailable from timeout, got %A" other

            do! Async.Sleep 50
            Expect.equal qd.Semaphore.CurrentCount 1
                "semaphore released after timeout — next request can proceed"
        }

        // ── Test 7: 35B bypass (CONC-04) ──
        testCaseAsync "35B requests bypass the queue and run concurrently (CONC-04)" <| async {
            let fake       = FakeUpstreamClient()
            let qd         = QueueDispatcher(fake, defaultOpts, alwaysReachableProbe)
            let dispatcher = qd :> IUpstreamClient

            // Fire five 35B calls concurrently — they must all start before any release
            let runOne35B () =
                task { return! dispatcher.CompleteAsync emptyRequest (mkDecision Qwen35B Low) CancellationToken.None }
            let tasks = [1..5] |> List.map (fun _ -> runOne35B ())

            // Wait until all five have entered the upstream (CallCount=5 BEFORE any release)
            let mutable elapsed = 0
            while fake.CallCount < 5 && elapsed < 2000 do
                do! Async.Sleep 50
                elapsed <- elapsed + 50
            Expect.equal fake.CallCount 5
                "all five 35B calls reached upstream concurrently (queue bypass confirmed)"

            // 122B semaphore must remain available the entire time
            Expect.equal qd.Semaphore.CurrentCount 1
                "122B semaphore was never touched by 35B traffic"

            // Release all gates and join
            for i in 0..4 do fake.ReleaseCall(i)
            let! _ = Task.WhenAll(tasks |> List.map (fun t -> t :> Task)) |> Async.AwaitTask
            ()
        }

        // ── Test 8: IStatsProvider snapshot reflects live queue state (in-process) ──
        testCaseAsync "IStatsProvider.GetSnapshot reflects live queue state (OBS-02)" <| async {
            let fake       = FakeUpstreamClient()
            let qd         = QueueDispatcher(fake, defaultOpts, alwaysReachableProbe)
            let stats      = qd :> IStatsProvider
            let dispatcher = qd :> IUpstreamClient

            let s0 = stats.GetSnapshot()
            Expect.equal s0.Active122B 0          "initially zero active 122B"
            Expect.equal s0.QueueDepth122BHigh 0  "initially empty high queue"
            Expect.equal s0.QueueDepth122BLow  0  "initially empty low queue"
            Expect.equal s0.SemaphoreAvailable 1  "initial semaphore count is 1"

            // Occupy slot — same pattern as Tests 4 and 5 (direct upcast, no wrapper task)
            let occupy =
                Task.Run(fun () ->
                    dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B Low) CancellationToken.None
                    :> Task)

            // Wait for occupy to acquire slot and enter upstream
            do! Async.Sleep 200

            let s1 = stats.GetSnapshot()
            Expect.equal s1.Active122B 1            "one active after slot grant"
            Expect.equal s1.SemaphoreAvailable 0    "semaphore taken"

            // Enqueue a low-priority waiter (will queue because occupy holds the slot).
            // The dispatcher loop dequeues the waiter almost immediately and blocks on
            // sem122b.WaitAsync — so QueueDepthLow drops back to 0 within microseconds.
            // We do NOT assert QueueDepthLow=1 here (inherently racy window). Instead we
            // give the waiter a moment to enqueue and be picked up by the dispatcher loop,
            // then verify it is still NOT yet in the upstream (fake.CallCount still 1).
            let waiter =
                Task.Run(fun () ->
                    dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B Low) CancellationToken.None
                    :> Task)

            do! Async.Sleep 50  // let waiter enqueue and dispatcher dequeue it onto sem.WaitAsync

            // s2: occupy still holds the slot; waiter is blocked on sem.WaitAsync (not yet upstream).
            let s2 = stats.GetSnapshot()
            Expect.equal s2.Active122B 1           "occupy still holds the slot"
            Expect.equal s2.SemaphoreAvailable 0   "semaphore still taken by occupy"
            Expect.equal fake.CallCount 1           "waiter not yet in upstream (blocked on sem)"

            // Release occupy (call 0), then waiter gets slot (call 1)
            fake.ReleaseCall(0)
            // Poll until waiter enters upstream (fake.CallCount=2)
            let mutable elapsed = 0
            while fake.CallCount < 2 && elapsed < 2000 do
                do! Async.Sleep 10
                elapsed <- elapsed + 10
            fake.ReleaseCall(1)
            let! _ = Task.WhenAll([occupy; waiter]) |> Async.AwaitTask
            do! Async.Sleep 50

            let s3 = stats.GetSnapshot()
            Expect.equal s3.Active122B 0          "idle after both completed"
            Expect.equal s3.QueueDepth122BLow 0   "queue drained"
            Expect.equal s3.SemaphoreAvailable 1  "semaphore returned to 1"
        }

        // ── Test 9: NEW per checker Blocker 4 — IN-PROCESS KESTREL /stats HTTP TEST ──
        // This test starts an actual WebApplication with the QueueDispatcher + Stats
        // endpoint wired in via DI, issues `GET /stats`, and asserts the JSON wire
        // shape uses snake_case keys. Without this test, a regression in StatsWire
        // (e.g., accidental PascalCase emission) would not be caught.
        // Pattern matches StreamingTests.fs — same in-process Kestrel approach.
        testCaseAsync "GET /stats returns 200 with snake_case JSON wire shape (API-07)" <| async {
            let fake = FakeUpstreamClient()
            let qd   = QueueDispatcher(fake, defaultOpts, alwaysReachableProbe)

            // Build a minimal in-process app: register the QueueDispatcher as
            // IStatsProvider in DI, then map the Stats endpoint.
            let builder = WebApplication.CreateBuilder()
            builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
            builder.Services.AddSingleton<IStatsProvider>(qd :> IStatsProvider) |> ignore
            let app = builder.Build()
            SmartRouter.Cli.Endpoints.Stats.mapEndpoints app

            do! app.StartAsync() |> Async.AwaitTask
            try
                // Discover the OS-assigned port
                let addresses =
                    app.Services
                       .GetRequiredService<IServer>()
                       .Features
                       .Get<IServerAddressesFeature>()
                       .Addresses
                let port =
                    addresses
                    |> Seq.head
                    |> fun a -> a.Split(':') |> Array.last |> int

                use client = new HttpClient()
                let url = sprintf "http://127.0.0.1:%d/stats" port
                use! response = client.GetAsync(url) |> Async.AwaitTask
                Expect.equal (int response.StatusCode) 200 "GET /stats returns 200"

                let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                use doc = JsonDocument.Parse(body)
                let root = doc.RootElement

                // Assert each REQUIRED snake_case key exists in the JSON object.
                // If StatsWire accidentally emits PascalCase, these lookups fail.
                let requiredKeys =
                    [ "timestamp"
                      "active_122b"
                      "queue_depth_122b_high"
                      "queue_depth_122b_low"
                      "active_35b"
                      "requests_per_sec"
                      "avg_latency_ms_60s"
                      "failure_count_total"
                      "fairness_picks_high"
                      "fairness_picks_low" ]
                for key in requiredKeys do
                    let mutable prop = Unchecked.defaultof<JsonElement>
                    Expect.isTrue (root.TryGetProperty(key, &prop))
                        (sprintf "/stats JSON missing required snake_case key '%s' — actual body: %s" key body)
            finally
                app.StopAsync().GetAwaiter().GetResult()
                (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
        }
    ]
