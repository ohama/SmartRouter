module SmartRouter.Tests.LoadTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
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
    { Messages      = [{ Role = User; Content = "burst" }]
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
      PerRequestTimeoutSeconds = 60 }

/// Latency-driven fake — every call sleeps `latencyMs`, then returns Ok.
/// No external gates needed; the run completes deterministically in ~ N * latencyMs.
/// Re-declared here (private) to avoid pulling in the entire QueueTests module.
/// A future refactor can extract shared helpers into a Common.fs.
type private LatencyFakeLoad(latencyMs: int) =
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
                return Ok """{"choices":[{"message":{"content":"burst"}}]}"""
            }
        member _.StreamAsync req decision ct =
            taskSeq { yield Ok "data: [DONE]" }

// ── Tests ─────────────────────────────────────────────────────────────────────

let tests =
    testSequenced <| testList "load" [

        // ── Burst test: 20 concurrent 122B requests, throughput cap holds ──
        // ptestCaseAsync => skipped in normal `dotnet test` runs (pending).
        // To run explicitly:
        //   Option A: flip `ptestCaseAsync` to `testCaseAsync` for a one-off run
        //   Option B: dotnet test -- --filter-test-list load (Expecto CLI args)
        ptestCaseAsync "20 concurrent 122B requests maintain at-most-one-in-flight (TEST-06)" <| async {
            let latencyMs  = 50
            let n          = 20
            let fake       = LatencyFakeLoad(latencyMs)
            let qd         = QueueDispatcher(fake, defaultOpts)
            let dispatcher = qd :> IUpstreamClient

            let dec = mkDecision Qwen122B Low
            let tasks =
                [1..n]
                |> List.map (fun _ ->
                    Task.Run(fun () ->
                        dispatcher.CompleteAsync emptyRequest dec CancellationToken.None
                        :> Task))

            let! _ = Task.WhenAll(tasks) |> Async.AwaitTask
            let starts = fake.Starts
            let ends   = fake.Ends
            Expect.equal starts.Length n "all bursts reached upstream"
            Expect.equal ends.Length   n "all bursts finished"

            // Strict serialization: the i-th call must start at or after the (i-1)-th call ends.
            // This is the throughput cap proof — peak concurrency = 1.
            for i in 1 .. n - 1 do
                Expect.isTrue (starts.[i] >= ends.[i-1])
                    (sprintf "request %d started before %d ended (concurrency cap violated)" i (i-1))

            // Total wall-clock should be ~ n * latencyMs (modulo dispatcher overhead).
            let totalMs = (ends.[n-1] - starts.[0]).TotalMilliseconds
            Expect.isGreaterThanOrEqual totalMs (float (n * latencyMs - 50))
                "total duration consistent with serialization"
            Expect.isLessThan totalMs (float (n * latencyMs * 4))
                "total duration not pathologically slow (dispatcher overhead within bounds)"
        }

        // ── Mixed-priority burst: 10 highs + 10 lows; highs all complete before lows ──
        // ptestCaseAsync => skipped in normal `dotnet test` runs (pending).
        ptestCaseAsync "mixed-priority burst respects priority order under load (CONC-02 + CONC-03 at scale)" <| async {
            let latencyMs  = 30
            let fake       = LatencyFakeLoad(latencyMs)
            let qd         = QueueDispatcher(fake, defaultOpts)
            let dispatcher = qd :> IUpstreamClient
            let order      = List<string>()
            let orderLck   = obj()

            // Occupy first slot to force everyone else to queue.
            let occupy =
                Task.Run(fun () ->
                    task {
                        let! _ = dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B Low) CancellationToken.None
                        lock orderLck (fun () -> order.Add("occupy"))
                    } :> Task)
            do! Async.Sleep 30

            // Enqueue 10 lows then 10 highs.
            // (Lows enqueue first so the priority queue must reorder.)
            let mkOne label prio =
                Task.Run(fun () ->
                    task {
                        let! _ = dispatcher.CompleteAsync emptyRequest (mkDecision Qwen122B prio) CancellationToken.None
                        lock orderLck (fun () -> order.Add(label))
                    } :> Task)
            let lows  = [ for i in 0..9 -> mkOne (sprintf "low%d"  i) Low  ]
            do! Async.Sleep 100
            let highs = [ for i in 0..9 -> mkOne (sprintf "high%d" i) High ]

            // Drain all 21 (1 occupy + 10 lows + 10 highs)
            let! _ = Task.WhenAll(occupy :: lows @ highs) |> Async.AwaitTask
            let observed = lock orderLck (fun () -> order |> List.ofSeq)

            // After "occupy", with FairnessK=10, the dispatcher picks 10 highs in a row,
            // then forces a low (fairness pivot). So the strict invariant is:
            //   ALL highs appear before the LAST low.
            // (There are exactly 10 highs and 10 lows; with K=10 all highs complete
            //  before the fairness pivot fires for any low.)
            let highIndices = [for i in 0..9 -> observed |> List.findIndex ((=) (sprintf "high%d" i))]
            let lowIndices  = [for i in 0..9 -> observed |> List.findIndex ((=) (sprintf "low%d"  i))]
            let maxHighIdx = List.max highIndices
            let maxLowIdx  = List.max lowIndices
            Expect.isLessThan maxHighIdx maxLowIdx
                "all 10 high-priority requests completed before the last low-priority request"

            // FairnessPicksHigh should be exactly 10 (one per high),
            // FairnessPicksLow >= 10 (one per low, plus the initial "occupy" was low).
            Expect.equal qd.FairnessPicksHigh 10L "exactly 10 high picks recorded"
            Expect.isGreaterThanOrEqual qd.FairnessPicksLow 10L "at least 10 low picks recorded"
        }
    ]
