---
phase: 03-122b-concurrency-gate
plan: 02
type: execute
wave: 2
depends_on: ["03-01"]
files_modified:
  - src/SmartRouter.Cli/Endpoints/Stats.fs                # NEW
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - src/SmartRouter.Cli/Program.fs
  - tests/SmartRouter.Tests/QueueTests.fs                 # NEW
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
  - tests/SmartRouter.Tests/RouterTests.fs                # extend rootTests
autonomous: true

must_haves:
  truths:
    - "GET /stats returns 200 with snake_case JSON containing timestamp, active_122b, queue_depth_122b_high, queue_depth_122b_low, active_35b, requests_per_sec, avg_latency_ms_60s, failure_count_total, fairness_picks_high, fairness_picks_low — verified via in-process Kestrel test that parses the actual HTTP response body and asserts each snake_case key exists"
    - "GET /stats values change to reflect live dispatcher state (issue requests; counts update)"
    - "Five concurrent 122B requests serialize through the QueueDispatcher — only one upstream call active at any moment (verified via FakeUpstreamClient timestamps)"
    - "When a high-priority 122B request is enqueued behind a queued low-priority request, the high-priority finishes before the low-priority"
    - "Cancelling a queued 122B request before it acquires the semaphore leaves SemaphoreSlim.CurrentCount unchanged (no leak)"
    - "Cancelling a queued 122B request AFTER it has been dequeued by the dispatcher loop and the dispatcher is mid-acquire (sem.WaitAsync(CancellationToken.None)) releases the slot when the dispatcher detects t.Ct.IsCancellationRequested post-acquire — no leak"
    - "Cancelling a 122B request after the upstream call starts (via per-request timeout) releases the slot and the next queued request proceeds"
    - "Concurrent 35B requests do NOT serialize through the QueueDispatcher (35B bypass verified)"
    - "After K consecutive high-priority dispatches, the K-th forced-low pick fires while highs are still queued (PITFALL-10 starvation prevention proven, not just priority ordering)"
    - "QueueTests.fs uses testSequenced and BOTH the gate-based FakeUpstreamClient AND the latency-based LatencyFake (deterministic auto-completion); fairness test uses LatencyFake to avoid gate-deadlock"
    - "All Phase 3 tests pass alongside the Phase 1+2 suite — tests pass count is at least 38 (30 existing + 8 new queue tests including the new post-dequeue cancellation test and the in-process Kestrel /stats HTTP test)"
  artifacts:
    - path: src/SmartRouter.Cli/Endpoints/Stats.fs
      provides: "GET /stats endpoint reading IStatsProvider; emits snake_case JSON via the existing jsonOptions"
      contains: "MapGet(\"/stats\""
    - path: tests/SmartRouter.Tests/QueueTests.fs
      provides: "FakeUpstreamClient (gate-per-call) + LatencyFake (auto-completing) + 8 queue tests + 1 in-process Kestrel /stats HTTP integration test, wrapped in testSequenced"
      contains: "testSequenced"
      min_lines: 350
  key_links:
    - from: src/SmartRouter.Cli/Endpoints/Stats.fs
      to: IStatsProvider
      via: "DI resolution; serialize StatsSnapshot to snake_case JSON"
      pattern: "GetRequiredService<IStatsProvider>|IStatsProvider"
    - from: src/SmartRouter.Cli/Program.fs
      to: Stats endpoint
      via: "Stats.mapEndpoints app"
      pattern: "Stats\\.mapEndpoints"
    - from: tests/SmartRouter.Tests/QueueTests.fs
      to: QueueDispatcher
      via: "FakeUpstreamClient + LatencyFake + QueueDispatcher constructor + assertions on Semaphore.CurrentCount"
      pattern: "QueueDispatcher\\(fake|QueueDispatcher\\(latency"
    - from: tests/SmartRouter.Tests/QueueTests.fs
      to: GET /stats endpoint
      via: "in-process Kestrel WebApplication + HttpClient.GetAsync + System.Text.Json parsing of snake_case JSON keys"
      pattern: "WebApplication\\.CreateBuilder|/stats"
    - from: tests/SmartRouter.Tests/RouterTests.fs
      to: QueueTests.tests
      via: "rootTests list extension (TEST-07 explicit list pattern)"
      pattern: "QueueTests\\.tests"
---

<objective>
Wire the GET /stats endpoint and prove the QueueDispatcher's atomic correctness cluster works under concurrent load with a deterministic in-process test harness. Stats must reflect live dispatcher state; QueueTests must exercise the four pitfall mitigations from 03-01 (semaphore enforcement, priority ordering, cancellation release, timeout release) plus the 35B bypass — every assertion ties back to a CONC- requirement.

Purpose: Without this plan, Phase 3 has the gate machinery but no observability and no test coverage proving the gate behaves correctly. The CONC-01..06 + REL-05 + API-07 + OBS-02 + TEST-04 requirements all collapse to these two artifacts: `Stats.fs` (endpoint) and `QueueTests.fs` (correctness proof).

Output:
  - `src/SmartRouter.Cli/Endpoints/Stats.fs` — minimal endpoint reading IStatsProvider.
  - `tests/SmartRouter.Tests/QueueTests.fs` — 8 deterministic queue tests (CONC-01 serialization, CONC-02/03 priority preempt, PITFALL-10 starvation prevention via fairness counter, two cancellation tests covering pre-dequeue AND post-dequeue-pre-acquire paths, REL-05 timeout, CONC-04 35B bypass, /stats live IStatsProvider snapshot) + 1 in-process Kestrel HTTP integration test for `GET /stats` (proves snake_case JSON wire shape), all wrapped in `testSequenced`.
  - Updated fsproj + rootTests list so the new file is compiled and discovered.
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
@.planning/phases/03-122b-concurrency-gate/03-01-QUEUE-DISPATCHER-PLAN.md

# Existing source patterns (mimic these styles)
@src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
@src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
@src/SmartRouter.Cli/Adapters/Json.fs
@src/SmartRouter.Cli/Program.fs

# Test patterns to follow
@tests/SmartRouter.Tests/RoutingTests.fs
@tests/SmartRouter.Tests/StreamingTests.fs
@tests/SmartRouter.Tests/RouterTests.fs
</context>

<tasks>

<task type="auto">
  <name>Task 1: Implement GET /stats endpoint (API-07 + OBS-02)</name>
  <files>
    src/SmartRouter.Cli/Endpoints/Stats.fs
    src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    src/SmartRouter.Cli/Program.fs
  </files>
  <action>
Add a minimal Minimal-API endpoint that exposes the QueueDispatcher's `IStatsProvider` snapshot as snake_case JSON. The endpoint must serialize using the existing `jsonOptions` (or an inline anonymous record matching the snake_case wire shape from CONTEXT.md).

**Step 1 — Create `src/SmartRouter.Cli/Endpoints/Stats.fs`:**

```fsharp
module SmartRouter.Cli.Endpoints.Stats

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open SmartRouter.Cli.Adapters.QueueDispatcher
open SmartRouter.Cli.Adapters.Json

/// Wire shape for GET /stats. snake_case to match OpenAI conventions.
/// Built fresh from a StatsSnapshot on every request — no caching.
type private StatsWire =
    { timestamp                 : string
      active_122b               : int
      queue_depth_122b_high     : int
      queue_depth_122b_low      : int
      active_35b                : int
      requests_per_sec          : float
      avg_latency_ms_60s        : float
      failure_count_total       : int64
      fairness_picks_high       : int64
      fairness_picks_low        : int64
      semaphore_available       : int }

let private toWire (s: StatsSnapshot) : StatsWire =
    { timestamp                 = s.Timestamp.ToString("o")
      active_122b               = s.Active122B
      queue_depth_122b_high     = s.QueueDepth122BHigh
      queue_depth_122b_low      = s.QueueDepth122BLow
      active_35b                = s.Active35B
      requests_per_sec          = s.RequestsPerSec
      avg_latency_ms_60s        = s.AvgLatencyMs60s
      failure_count_total       = s.FailureCountTotal
      fairness_picks_high       = s.FairnessPicksHigh
      fairness_picks_low        = s.FairnessPicksLow
      semaphore_available       = s.SemaphoreAvailable }

/// Register GET /stats. Resolves IStatsProvider from DI on each request and
/// serializes a fresh snapshot. No caching: the snapshot is cheap (Volatile.Read +
/// two locks) and operators want live values, not stale ones.
let mapEndpoints (app: WebApplication) =
    app.MapGet("/stats", Func<HttpContext, Task>(fun ctx ->
        task {
            let stats = ctx.RequestServices.GetRequiredService<IStatsProvider>()
            let wire = toWire (stats.GetSnapshot())
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsJsonAsync(wire, jsonOptions, ctx.RequestAborted)
        })) |> ignore
```

**Step 2 — `src/SmartRouter.Cli/SmartRouter.Cli.fsproj`:** Add `Endpoints/Stats.fs` to the `<Compile>` list AFTER `Endpoints/ChatCompletions.fs` and BEFORE `CompositionRoot.fs`:

```xml
<Compile Include="Endpoints/ChatCompletions.fs" />
<Compile Include="Endpoints/Stats.fs" />     <!-- NEW -->
<Compile Include="CompositionRoot.fs" />
```

**Step 3 — `src/SmartRouter.Cli/Program.fs`:** Register the endpoint after `ChatCompletions.mapEndpoints app`:

```fsharp
ChatCompletions.mapEndpoints app
Stats.mapEndpoints app          // NEW
```

The IStatsProvider DI registration is already in place from 03-01. No CompositionRoot change needed here.

**Why a snake_case wire record (not direct StatsSnapshot serialization):** F# record default JSON serialization with `jsonOptions` (which uses `JsonStringEnumConverter` and the FSharpConverter-aware setup) emits PascalCase property names — does not match the OpenAI snake_case convention CONTEXT.md fixes. A separate `StatsWire` record with explicit lowercase fields makes the wire shape declarative and grep-able.
  </action>
  <verify>
```
cd /Users/ohama/projs/smart-router
dotnet build SmartRouter.slnx 2>&1 | grep -E "(error|warning FS)"   # MUST be empty
ls src/SmartRouter.Cli/Endpoints/Stats.fs                            # exists
grep -n 'MapGet("/stats"' src/SmartRouter.Cli/Endpoints/Stats.fs     # 1 hit
grep -n "Stats.mapEndpoints" src/SmartRouter.Cli/Program.fs          # 1 hit
grep -n "Endpoints/Stats.fs" src/SmartRouter.Cli/SmartRouter.Cli.fsproj  # 1 hit
grep -nE '"timestamp"|timestamp\s*=' src/SmartRouter.Cli/Endpoints/Stats.fs   # multiple hits
grep -nE 'active_122b|queue_depth_122b_high|queue_depth_122b_low|active_35b' src/SmartRouter.Cli/Endpoints/Stats.fs   # all 4 fields present
```
  </verify>
  <done>
- `src/SmartRouter.Cli/Endpoints/Stats.fs` exists.
- The endpoint resolves `IStatsProvider` from DI and serializes a snake_case JSON snapshot.
- `Stats.mapEndpoints app` is called from Program.fs after `ChatCompletions.mapEndpoints app`.
- `dotnet build` clean.
- All ten StatsWire fields (`timestamp`, `active_122b`, `queue_depth_122b_high`, `queue_depth_122b_low`, `active_35b`, `requests_per_sec`, `avg_latency_ms_60s`, `failure_count_total`, `fairness_picks_high`, `fairness_picks_low`, plus `semaphore_available` for diagnostics) are present.
  </done>
</task>

<task type="auto">
  <name>Task 2: Author `QueueTests.fs` — 8 queue tests + in-process Kestrel /stats HTTP test</name>
  <files>
    tests/SmartRouter.Tests/QueueTests.fs
    tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    tests/SmartRouter.Tests/RouterTests.fs
  </files>
  <action>
Author the QueueTests module exercising every CONC- requirement and REL-05 / OBS-02 / API-07. The test file uses TWO complementary fakes:
1. `FakeUpstreamClient` — gate-per-call (TaskCompletionSource); each call parks until `ReleaseCall(idx)` fires. Used by tests that need to gate a specific number of calls deterministically.
2. `LatencyFake(latencyMs)` — auto-completing fake that returns Ok after `latencyMs`. Used by the fairness test (which would deadlock with a gated fake — see Blocker 2 below) and any test where every queued call must eventually drain on its own.

Pattern-match RESEARCH.md Focus Area 9 for FakeUpstreamClient. Wrap the entire `testList` in `testSequenced` (Console.SetOut + Interlocked counters race otherwise). Most tests are in-process pure F# — the dedicated `/stats` HTTP test starts an in-process Kestrel WebApplication using the same harness pattern as StreamingTests.fs.

**Step 1 — Create `tests/SmartRouter.Tests/QueueTests.fs`:**

```fsharp
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
    { Target     = target
      Priority   = priority
      Reason     = Default
      IsFallback = false }

let private emptyRequest : RouterRequest =
    { Messages      = [{ Role = User; Content = "test" }]
      ModelOverride  = None
      Task           = None
      Stream         = false
      Temperature    = None
      TopP           = None
      MaxTokens      = None
      UnknownFields  = Map.empty }

let private defaultOpts =
    { FairnessK                = 10
      MaxConcurrent122B        = 1
      PerRequestTimeoutSeconds = 30 }

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
            let dispatcher = QueueDispatcher(fake, defaultOpts) :> IUpstreamClient
            let dec        = mkDecision Qwen122B Low

            let tasks =
                [1..5]
                |> List.map (fun _ ->
                    Task.Run(fun () ->
                        dispatcher.CompleteAsync emptyRequest dec CancellationToken.None))
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
        testCaseAsync "high-priority 122B request preempts queued low-priority (CONC-02 / PITFALL-9)" <| async {
            let fake       = FakeUpstreamClient()
            let qd         = QueueDispatcher(fake, defaultOpts)
            let dispatcher = qd :> IUpstreamClient
            let order      = List<string>()
            let orderLck   = obj()

            // Occupy the slot with a Low-priority request that will hold until released
            let occupy =
                Task.Run(fun () ->
                    task {
                        let! _ = dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B Low) CancellationToken.None
                        lock orderLck (fun () -> order.Add("occupy"))
                    } :> Task)
            do! Async.Sleep 50   // occupy acquires the slot

            // Enqueue a Low-priority — will sit at the back
            let lowTask =
                Task.Run(fun () ->
                    task {
                        let! _ = dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B Low) CancellationToken.None
                        lock orderLck (fun () -> order.Add("low"))
                    } :> Task)
            do! Async.Sleep 30

            // Enqueue a High-priority AFTER low — must run BEFORE low
            let highTask =
                Task.Run(fun () ->
                    task {
                        let! _ = dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B High) CancellationToken.None
                        lock orderLck (fun () -> order.Add("high"))
                    } :> Task)
            do! Async.Sleep 30

            // Release in chronological-call order (NOT logical order):
            // call 0 = occupy, call 1 = high (next slot grant), call 2 = low (last)
            fake.ReleaseCall(0); do! Async.Sleep 60
            fake.ReleaseCall(1); do! Async.Sleep 60
            fake.ReleaseCall(2); do! Async.Sleep 60

            let! _ = Task.WhenAll([occupy; lowTask; highTask]) |> Async.AwaitTask
            let observed = lock orderLck (fun () -> order |> List.ofSeq)
            Expect.equal observed ["occupy"; "high"; "low"]
                "high-priority was dispatched before low-priority despite enqueueing later"
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
            let qd         = QueueDispatcher(latency, opts)
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
            do! Async.Sleep 10   // brief — let occupy grab the slot

            // Enqueue 4 highs + 1 low SIMULTANEOUSLY (no inter-enqueue sleeps for ordering;
            // all of them are racing into the queue while occupy holds the slot).
            // Per checker Blocker 6: structural assertion, NOT sleep-driven ordering.
            let high1 = mkLabelled "high1" High
            let high2 = mkLabelled "high2" High
            let high3 = mkLabelled "high3" High
            let low1  = mkLabelled "low1"  Low
            let high4 = mkLabelled "high4" High

            // Wait until all six have actually enqueued (not just kicked off Task.Run).
            // Poll the queue depths instead of sleeping a fixed amount.
            let mutable elapsed = 0
            while qd.QueueDepthHigh + qd.QueueDepthLow < 5 && elapsed < 2000 do
                do! Async.Sleep 20
                elapsed <- elapsed + 20
            Expect.equal (qd.QueueDepthHigh + qd.QueueDepthLow) 5
                "all 5 waiters enqueued before occupy releases (4 highs + 1 low)"

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
            let qd         = QueueDispatcher(fake, defaultOpts)
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
            let qd         = QueueDispatcher(fake, defaultOpts)
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
            let qd         = QueueDispatcher(fake, { defaultOpts with PerRequestTimeoutSeconds = 1 })
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
            let qd         = QueueDispatcher(fake, defaultOpts)
            let dispatcher = qd :> IUpstreamClient

            // Fire five 35B calls concurrently — they must all start before any release
            let tasks =
                [1..5]
                |> List.map (fun _ ->
                    Task.Run(fun () ->
                        dispatcher.CompleteAsync emptyRequest (mkDecision Qwen35B Low) CancellationToken.None))

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
            let qd         = QueueDispatcher(fake, defaultOpts)
            let stats      = qd :> IStatsProvider

            let s0 = stats.GetSnapshot()
            Expect.equal s0.Active122B 0          "initially zero active 122B"
            Expect.equal s0.QueueDepth122BHigh 0  "initially empty high queue"
            Expect.equal s0.QueueDepth122BLow  0  "initially empty low queue"
            Expect.equal s0.SemaphoreAvailable 1  "initial semaphore count is 1"

            // Occupy slot
            let dispatcher = qd :> IUpstreamClient
            let occupy =
                Task.Run(fun () ->
                    dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B Low) CancellationToken.None
                    :> Task)
            do! Async.Sleep 50

            let s1 = stats.GetSnapshot()
            Expect.equal s1.Active122B 1            "one active after slot grant"
            Expect.equal s1.SemaphoreAvailable 0    "semaphore taken"

            // Enqueue a low-priority (will queue)
            let waiter =
                Task.Run(fun () ->
                    dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B Low) CancellationToken.None
                    :> Task)
            do! Async.Sleep 50

            let s2 = stats.GetSnapshot()
            Expect.equal s2.QueueDepth122BLow 1 "one low-priority queued while slot held"

            fake.ReleaseCall(0); do! Async.Sleep 50
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
            let qd   = QueueDispatcher(fake, defaultOpts)

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
```

Notes per checker findings:

- **Blocker 1+2+3 (PITFALL-10 starvation actually proven via 4H+1L with K=3 + LatencyFake):** Test 3 enqueues 4 highs + 1 low with K=3 against `LatencyFake(30)`. After occupy releases, the dispatcher picks high1, high2, high3 (consecutiveHighPicks reaches K=3), then FORCES a low pick (low1 at position 4) before high4 (position 5). The strict assertion `low1Idx == 4` proves the K-th forced-low pick fired while highs were still queued — without this, the original test only proved priority ordering. `LatencyFake` eliminates the gate-deadlock that would have hung `Task.WhenAll`.
- **Blocker 4 (Kestrel /stats HTTP test):** Test 9 starts an in-process WebApplication, GETs `/stats`, and asserts each snake_case key is present in the JSON. Mirrors StreamingTests.fs harness pattern.
- **Blocker 5 (post-dequeue cancellation):** Test 5 adds the missing release-on-cancel-after-dequeue path. Victim is dequeued (dispatcher blocked on sem.WaitAsync), cancelled mid-acquire; when occupy releases, the dispatcher acquires for victim, sees CT cancelled, releases. CurrentCount returns to 1.
- **Blocker 6 (deterministic fairness via LatencyFake, not inter-enqueue Async.Sleep):** Test 3 polls `qd.QueueDepthHigh + qd.QueueDepthLow` to confirm all five waiters enqueued before draining. Sleep is used only between `occupy.Run` and the enqueue burst — NOT between individual enqueues for ordering purposes.

**Step 2 — `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj`:** Add `QueueTests.fs` to the `<Compile>` list AFTER `StreamingTests.fs` and BEFORE `RouterTests.fs`:

```xml
<Compile Include="RoutingTests.fs" />
<Compile Include="StreamingTests.fs" />
<Compile Include="QueueTests.fs" />          <!-- NEW -->
<Compile Include="RouterTests.fs" />
```

**Step 3 — `tests/SmartRouter.Tests/RouterTests.fs`:** Append `QueueTests.tests` to `rootTests`:

```fsharp
let rootTests : Test list =
    [
        SmartRouter.Tests.RoutingTests.tests
        SmartRouter.Tests.StreamingTests.tests
        SmartRouter.Tests.QueueTests.tests   // NEW
    ]
```

**Step 4 — Verification:** `dotnet test` should report at least 39 passing tests (30 prior + 9 new in this file: serialization, priority preempt, PITFALL-10 starvation, pre-dequeue cancellation, post-dequeue-pre-acquire cancellation, REL-05 timeout, 35B bypass, IStatsProvider snapshot, in-process Kestrel /stats HTTP).

**Why the in-process Kestrel /stats test is now mandatory (was previously deferred):** Per checker Blocker 4, asserting `IStatsProvider.GetSnapshot()` directly does NOT prove that the wire JSON emitted by `GET /stats` uses snake_case keys. A regression that flipped `StatsWire` to PascalCase (e.g., losing the `[<JsonPropertyName>]`-equivalent setup) would pass the Snapshot test but break every downstream consumer. The HTTP test is cheap (~one Kestrel start) and closes the gap.
  </action>
  <verify>
```
cd /Users/ohama/projs/smart-router
dotnet build SmartRouter.slnx 2>&1 | grep -E "(error|warning FS)"   # MUST be empty
dotnet test 2>&1 | tail -10                                          # >= 39 passed (30 prior + 9 new)
ls tests/SmartRouter.Tests/QueueTests.fs                             # exists
wc -l tests/SmartRouter.Tests/QueueTests.fs                          # >= 350
grep -c "testCaseAsync " tests/SmartRouter.Tests/QueueTests.fs       # >= 9
grep -n "testSequenced" tests/SmartRouter.Tests/QueueTests.fs        # 1 hit (wraps testList)
grep -n "FakeUpstreamClient" tests/SmartRouter.Tests/QueueTests.fs   # multiple hits
grep -n "LatencyFake" tests/SmartRouter.Tests/QueueTests.fs          # >= 2 hits (type def + fairness test)
grep -n "WebApplication.CreateBuilder" tests/SmartRouter.Tests/QueueTests.fs   # 1 hit (Kestrel /stats test)
grep -nE 'low1Idx\s*==?\s*4|Expect\.equal low1Idx 4' tests/SmartRouter.Tests/QueueTests.fs   # 1 hit (PITFALL-10 strict invariant)
grep -nE 'requiredKeys|"active_122b"|"queue_depth_122b_high"' tests/SmartRouter.Tests/QueueTests.fs   # multiple hits (snake_case key assertions)
grep -n "QueueTests.tests" tests/SmartRouter.Tests/RouterTests.fs    # 1 hit (rootTests entry)
grep -n "QueueTests.fs" tests/SmartRouter.Tests/SmartRouter.Tests.fsproj   # 1 hit
./scripts/check-no-async.sh                                           # PASS
```

Optional manual `/stats` smoke after a build:
```
# In one terminal:
dotnet run --project src/SmartRouter.Cli/SmartRouter.Cli.fsproj
# In another:
curl -s http://127.0.0.1:4000/stats | python3 -m json.tool
# Should print the 11-field snake_case JSON snapshot (with active_122b=0, queue_depth_122b_high=0, etc.)
```
  </verify>
  <done>
- `tests/SmartRouter.Tests/QueueTests.fs` contains BOTH `FakeUpstreamClient` (gate-per-call) AND `LatencyFake` (auto-completing) helpers, wrapped in a `testSequenced` test list with 9 tests covering:
  - CONC-01 serialization (Test 1)
  - CONC-02 / PITFALL-9 priority preempt (Test 2)
  - CONC-03 / PITFALL-10 starvation prevention proven via K-th forced-low pick (Test 3) — uses LatencyFake to avoid gate-deadlock; strict assertion `low1Idx == 4`
  - CONC-05 cancellation while still queued, pre-dequeue (Test 4)
  - PITFALL-8 cancellation AFTER dequeue mid-acquire (Test 5) — NEW per checker Blocker 5
  - REL-05 / PITFALL-11 hung upstream timeout (Test 6)
  - CONC-04 35B bypass (Test 7)
  - OBS-02 IStatsProvider snapshot semantics (Test 8)
  - API-07 in-process Kestrel `GET /stats` returns 200 with snake_case JSON (Test 9) — NEW per checker Blocker 4
- fsproj includes `QueueTests.fs` in compile order; `RouterTests.fs` rootTests includes `SmartRouter.Tests.QueueTests.tests`.
- `dotnet test` passes >= 39 tests; no regressions in Phase 1+2 tests.
- `check-no-async.sh` clean.
- /stats endpoint reachable via curl on a running instance and returns the 11-field snake_case JSON; in-process Kestrel test asserts the same shape automatically.
  </done>
</task>

</tasks>

<verification>
Final phase 3 wave 2 checks:

```bash
cd /Users/ohama/projs/smart-router
dotnet build SmartRouter.slnx                                                  # zero errors / warnings
dotnet test 2>&1 | tail -5                                                      # >= 39 passed, 0 failed

# Endpoint visible to DI:
grep -nE 'IStatsProvider|GetSnapshot' src/SmartRouter.Cli/Endpoints/Stats.fs   # both present
# All four atomic-cluster pitfalls each have at least one dedicated test:
grep -nE 'PITFALL-8|PITFALL-9|PITFALL-10|PITFALL-11' tests/SmartRouter.Tests/QueueTests.fs   # 4+ hits
grep -nE 'CONC-0[1-6]|REL-05|API-07|OBS-02' tests/SmartRouter.Tests/QueueTests.fs   # 8+ test names mention requirements
# Both fakes present:
grep -nE 'type FakeUpstreamClient|type LatencyFake' tests/SmartRouter.Tests/QueueTests.fs   # 2 hits
# Kestrel /stats test wires the actual endpoint:
grep -nE 'Stats\.mapEndpoints|/stats' tests/SmartRouter.Tests/QueueTests.fs   # multiple hits
```
</verification>

<success_criteria>
- GET /stats endpoint responds with 11-field snake_case JSON; the in-process Kestrel test (Test 9) asserts every required snake_case key by parsing the actual HTTP response body.
- QueueTests.fs has 9 tests. Each of the four atomic-cluster pitfalls has at least one dedicated test:
  - PITFALL-8 (leak on cancellation) — Tests 4 (pre-dequeue) AND 5 (post-dequeue mid-acquire)
  - PITFALL-9 (FIFO bypasses priority) — Test 2 (priority preempt)
  - PITFALL-10 (starvation under constant high) — Test 3 (K-th forced-low pick fires while highs queued; `low1Idx == 4` strict assertion)
  - PITFALL-11 (upstream hang without timeout) — Test 6 (timeout release)
- 35B bypass (CONC-04), IStatsProvider snapshot (OBS-02), and the in-process Kestrel /stats wire-shape test (API-07) round out the coverage.
- Test count grows from 30 to at least 39; no regressions in Phase 1+2.
- All Phase 3 requirements except TEST-06 (load tests, deferred to 03-03) are now demonstrated by automated tests.
</success_criteria>

<output>
After completion, create `.planning/phases/03-122b-concurrency-gate/03-02-SUMMARY.md` documenting:
- Final /stats JSON shape (with example output captured from a curl AND the parsed JsonDocument keys verified by Test 9)
- Test list: each test name → which CONC-xx / REL-05 / API-07 / OBS-02 / TEST-04 requirement it proves AND which PITFALL-8/9/10/11 it mitigates
- Confirmation that the K-th forced-low pick (PITFALL-10) was observed at the expected position (zero-indexed 4) in Test 3
- Any test ordering / sleep tunings the executor adjusted from the plan (especially the polling loop in Test 3 that waits for queue depth to reach 5)
- Confirmation that the in-process Kestrel /stats HTTP test is INCLUDED in this plan (no longer deferred — see checker Blocker 4)
</output>
</content>
</invoke>