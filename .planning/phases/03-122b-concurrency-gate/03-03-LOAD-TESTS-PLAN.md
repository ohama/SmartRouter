---
phase: 03-122b-concurrency-gate
plan: 03
type: execute
wave: 3
depends_on: ["03-02"]
files_modified:
  - tests/SmartRouter.Tests/LoadTests.fs       # NEW
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
  - tests/SmartRouter.Tests/RouterTests.fs
autonomous: true

must_haves:
  truths:
    - "Twenty concurrent 122B requests against a controlled-latency FakeUpstreamClient maintain at-most-one-in-flight (no concurrent overlap detected via timestamps)"
    - "Burst test confirms QueueDispatcher's throughput cap holds: peak Active122B never exceeds 1 across the entire run"
    - "Load tests live in a separate test module wrapped in `testSequenced` and use Expecto `ptestCaseAsync` so they are SKIPPED in normal `dotnet test` runs (opt-in only)"
    - "Load tests run cleanly when explicitly enabled via Expecto filter (e.g., `dotnet test -- --filter-test-list load`); they pass within reasonable wall-clock time (< 30 s for the burst test)"
    - "RouterTests.fs rootTests includes the load test list; pending markers ensure the suite still reports 0 ignored failures in the default run"
  artifacts:
    - path: tests/SmartRouter.Tests/LoadTests.fs
      provides: "Load tests using ptestCaseAsync — opt-in burst tests that validate throughput cap"
      contains: "ptestCaseAsync"
      min_lines: 80
  key_links:
    - from: tests/SmartRouter.Tests/LoadTests.fs
      to: QueueDispatcher
      via: "FakeUpstreamClient (or a load-tuned variant) + 20 concurrent CompleteAsync calls"
      pattern: "QueueDispatcher\\(fake"
    - from: tests/SmartRouter.Tests/RouterTests.fs
      to: LoadTests.tests
      via: "rootTests entry (TEST-07 explicit list pattern)"
      pattern: "LoadTests\\.tests"
---

<objective>
Add an opt-in load test module that validates the 122B throughput cap holds under burst (20+ concurrent submissions). Tests use `ptestCaseAsync` (Expecto's "pending" form) so they are skipped in normal CI runs and only execute when an operator explicitly opts in via filter — keeping `dotnet test` fast for the iterative loop.

Purpose: TEST-06 ("Load tests measure latency under contention and validate 122B throughput cap holds under burst") is the only Phase 3 requirement not satisfied by 03-02. A burst test catches regressions where a refactor accidentally drops the SemaphoreSlim or weakens the queue dispatch path — the unit tests in 03-02 verify correctness for small N (5 concurrent), this plan verifies it scales.

Output:
  - `tests/SmartRouter.Tests/LoadTests.fs` — opt-in burst tests.
  - fsproj + rootTests updates (with pending markers, default `dotnet test` is unchanged).
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/REQUIREMENTS.md
@.planning/phases/03-122b-concurrency-gate/03-CONTEXT.md
@.planning/phases/03-122b-concurrency-gate/03-RESEARCH.md
@.planning/phases/03-122b-concurrency-gate/03-02-STATS-AND-QUEUE-TESTS-PLAN.md

# Patterns to mimic
@tests/SmartRouter.Tests/QueueTests.fs    # FakeUpstreamClient, testSequenced
@tests/SmartRouter.Tests/RouterTests.fs   # rootTests list
</context>

<tasks>

<task type="auto">
  <name>Task 1: Author opt-in `LoadTests.fs` with ptestCaseAsync burst test (TEST-06)</name>
  <files>
    tests/SmartRouter.Tests/LoadTests.fs
    tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    tests/SmartRouter.Tests/RouterTests.fs
  </files>
  <action>
Author a small load test module that reuses the QueueTests `FakeUpstreamClient` infrastructure but tunes for burst load. All tests use `ptestCaseAsync` (Expecto's "pending" variant) so they show up as "pending" in default test output and do NOT cause CI failures, but execute when filtered in.

**Step 1 — Create `tests/SmartRouter.Tests/LoadTests.fs`:**

```fsharp
module SmartRouter.Tests.LoadTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Expecto
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports
open SmartRouter.Cli.Adapters.QueueDispatcher

// Reuse the FakeUpstreamClient + helpers from QueueTests. We re-declare the
// minimal helpers here to avoid pulling in the entire QueueTests module
// (which is wrapped in `testSequenced` and has its own scoping). Operators
// who want to share the helper can extract it into a Common.fs in a future
// refactor.

let private mkDecision (target: ModelId) (priority: Priority) : RoutingDecision =
    { Target = target; Priority = priority; Reason = Default; IsFallback = false }

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
type private LatencyFake(latencyMs: int) =
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
            FSharp.Control.taskSeq { yield Ok "data: [DONE]" }

let tests =
    testSequenced <| testList "load" [

        // ── Burst test: 20 concurrent 122B requests, throughput cap holds ──
        // ptestCaseAsync ⇒ skipped in normal `dotnet test` runs.
        // To run explicitly:
        //   dotnet test --filter "TestCategory=load" (or use Expecto CLI args)
        ptestCaseAsync "20 concurrent 122B requests maintain at-most-one-in-flight (TEST-06)" <| async {
            let latencyMs  = 50
            let n          = 20
            let fake       = LatencyFake(latencyMs)
            let qd         = QueueDispatcher(fake, defaultOpts)
            let dispatcher = qd :> IUpstreamClient

            let dec = mkDecision Qwen122B Low
            let tasks =
                [1..n]
                |> List.map (fun _ ->
                    Task.Run(fun () ->
                        dispatcher.CompleteAsync emptyRequest dec CancellationToken.None))

            let! _ = Task.WhenAll(tasks |> List.map (fun t -> t :> Task)) |> Async.AwaitTask
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
        ptestCaseAsync "mixed-priority burst respects priority order under load (CONC-02 + CONC-03 at scale)" <| async {
            let latencyMs  = 30
            let fake       = LatencyFake(latencyMs)
            let qd         = QueueDispatcher(fake, defaultOpts)
            let dispatcher = qd :> IUpstreamClient
            let order      = List<string>()
            let orderLck   = obj()

            // Occupy first to force everyone else to queue
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

            // Drain all 21
            let! _ = Task.WhenAll(occupy :: lows @ highs) |> Async.AwaitTask
            let observed = lock orderLck (fun () -> order |> List.ofSeq)

            // After "occupy", with FairnessK=10, the dispatcher picks 10 highs in a row,
            // then forces a low (fairness pivot). So expected order: occupy, high0..high9,
            // then lows interspersed. The strict invariant: every high appears before
            // the (FairnessK+1)-th low — i.e., before low number 10 ... but there are
            // only 10 lows. So the invariant is: ALL highs appear before the LAST low.
            let highIndices = [for i in 0..9 -> observed |> List.findIndex ((=) (sprintf "high%d" i))]
            let lowIndices  = [for i in 0..9 -> observed |> List.findIndex ((=) (sprintf "low%d"  i))]
            let maxHighIdx = List.max highIndices
            let maxLowIdx  = List.max lowIndices
            Expect.isLessThan maxHighIdx maxLowIdx
                "all 10 high-priority requests completed before the last low-priority request"

            // FairnessPicksHigh should be exactly 10 (one per high), FairnessPicksLow >= 10 (one per low).
            Expect.equal qd.FairnessPicksHigh 10L "exactly 10 high picks recorded"
            Expect.isGreaterThanOrEqual qd.FairnessPicksLow 10L "at least 10 low picks recorded"
        }
    ]
```

The two key Expecto facts used:
1. `ptestCaseAsync` ⇒ pending. In default runs, Expecto reports "20 tests run, 2 ignored" — the load tests are not failures.
2. `Expect.isLessThan / isGreaterThan / isGreaterThanOrEqual` are standard Expecto assertions; `Expect.isLessThanOrEqual` is also available if needed.

To run explicitly:
```
# Option A: Expecto CLI filter
dotnet test --logger:"console;verbosity=detailed" -- --filter-test-list load
# Option B: temporarily change `ptestCaseAsync` to `testCaseAsync` for a one-off run
```

**Step 2 — `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj`:** Add `LoadTests.fs` to the `<Compile>` list AFTER `QueueTests.fs` and BEFORE `RouterTests.fs`:

```xml
<Compile Include="QueueTests.fs" />
<Compile Include="LoadTests.fs" />     <!-- NEW -->
<Compile Include="RouterTests.fs" />
```

**Step 3 — `tests/SmartRouter.Tests/RouterTests.fs`:** Append `LoadTests.tests` to `rootTests`:

```fsharp
let rootTests : Test list =
    [
        SmartRouter.Tests.RoutingTests.tests
        SmartRouter.Tests.StreamingTests.tests
        SmartRouter.Tests.QueueTests.tests
        SmartRouter.Tests.LoadTests.tests   // NEW — pending tests, skipped by default
    ]
```

**Step 4 — Verification:** `dotnet test` should report two extra ignored tests (the load tests are pending) and no new failures. Total: 36 passed, 2 ignored (ish — depending on how many tests 03-02 ended up with).

**Why ptestCaseAsync over an env-var gate:** Expecto's pending mechanism is the idiomatic way to mark tests as opt-in. Env-var gates require runtime branching that mixes "skipped" with "passed" in test output. The pending marker is explicit and tooling-friendly (`dotnet test` reports the count of ignored tests).

**Why two tests in this plan, not five:** TEST-06 is "validate 122B throughput cap holds under burst" + "measure latency under contention." Two tests are sufficient: one strict-serialization burst (the cap proof) and one mixed-priority burst (cap + priority preserved at scale). Adding more is gold-plating; the gate is a single semaphore, not a complex algorithm.
  </action>
  <verify>
```
cd /Users/ohama/projs/smart-router
dotnet build SmartRouter.slnx 2>&1 | grep -E "(error|warning FS)"   # MUST be empty
dotnet test 2>&1 | tail -10                                          # default run: 36+ passed, 2 ignored, 0 failed
ls tests/SmartRouter.Tests/LoadTests.fs                              # exists
wc -l tests/SmartRouter.Tests/LoadTests.fs                           # >= 80
grep -c "ptestCaseAsync" tests/SmartRouter.Tests/LoadTests.fs        # >= 2
grep -n "LoadTests.tests" tests/SmartRouter.Tests/RouterTests.fs     # 1 hit (rootTests entry)
grep -n "LoadTests.fs" tests/SmartRouter.Tests/SmartRouter.Tests.fsproj   # 1 hit
./scripts/check-no-async.sh                                           # PASS
```

Optional opt-in load run (manual smoke; skip in normal verification):
```
# Temporarily flip ptestCaseAsync → testCaseAsync, then:
dotnet test --filter "FullyQualifiedName~load" 2>&1 | tail -10
# Should pass within ~ 30 s wall-clock for the 20-concurrent burst test.
```
  </verify>
  <done>
- `tests/SmartRouter.Tests/LoadTests.fs` contains at least two `ptestCaseAsync` tests: a strict-serialization burst (20 concurrent) and a mixed-priority burst (10 high + 10 low).
- fsproj includes `LoadTests.fs`; RouterTests.fs rootTests includes `SmartRouter.Tests.LoadTests.tests`.
- Default `dotnet test` reports 0 failures; the two load tests appear as "ignored" / "pending" in the summary.
- `check-no-async.sh` clean.
- Manual opt-in run (after flipping `p` to `t`) completes within ~ 30 s and passes both tests, demonstrating the cap holds under realistic burst.
  </done>
</task>

</tasks>

<verification>
Final phase 3 wave 3 checks:

```bash
cd /Users/ohama/projs/smart-router
dotnet build SmartRouter.slnx                                                  # zero errors / warnings
dotnet test 2>&1 | tail -5                                                      # all phase 1+2+3 pass; load tests pending

ls tests/SmartRouter.Tests/LoadTests.fs                                          # exists
grep -c "ptestCaseAsync" tests/SmartRouter.Tests/LoadTests.fs                    # >= 2
grep -n "SmartRouter.Tests.LoadTests" tests/SmartRouter.Tests/RouterTests.fs    # 1
```
</verification>

<success_criteria>
- LoadTests.fs exists with >= 2 `ptestCaseAsync` tests; both pass when explicitly enabled (flip `p` → `t`).
- Default `dotnet test` is unchanged in pass count vs 03-02; load tests appear as ignored/pending only.
- TEST-06 (load tests validate 122B throughput cap holds under burst) is satisfied: the strict-serialization burst with 20 concurrent submissions proves peak concurrency = 1 across the run.
- No regressions: full Phase 1+2+3 suite still passes.
</success_criteria>

<output>
After completion, create `.planning/phases/03-122b-concurrency-gate/03-03-SUMMARY.md` documenting:
- The two load test names + what each proves
- Wall-clock duration of each load test when explicitly enabled
- Any tuning of latencyMs or burst size from the plan defaults
- Note on `ptestCaseAsync` opt-in mechanism for future operators / CI matrix
</output>
</content>
</invoke>