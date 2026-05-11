module SmartRouter.Tests.MLLiveVersionTests

// Issue #12: makeApplyML closes over baselineVersion / canaryVersion strings
// captured at DI factory time. After RetrainingService.PerformRetrain calls
// IModelVersionProvider.Update, the live provider value changes — but the
// closure's captured string does NOT, so RoutingDecision.ModelVersion remains
// stale until process restart.
//
// This file holds two focused tests that exercise the version-flow contract
// without needing real ML model files:
//
//   1. Baseline path: a fresh in-memory IModelVersionProvider is wired via
//      makeApplyML; a routing decision is produced; the decision's ModelVersion
//      matches whatever the provider currently reports.
//
//   2. Live-update path: the same closure is invoked twice with the provider's
//      version mutated between calls. The second call's ModelVersion must match
//      the NEW provider value (NOT the value from the first call).
//
// Pre-fix test 2 fails (closure captures stale "v1" string).
// Post-fix test 2 passes (closure reads live from the provider per call).

open System
open System.Threading
open System.Threading.Tasks
open Expecto

open SmartRouter.Core.Domain
open SmartRouter.Core.MLPorts
open SmartRouter.Core.CanaryPorts
open SmartRouter.Core.RetrainingPorts
open SmartRouter.Core

/// Minimal mutable IModelVersionProvider for tests.
type private TestVersionProvider(initialBaseline: string, initialCanary: string) =
    let mutable baseline = initialBaseline
    let mutable canary   = initialCanary
    interface IModelVersionProvider with
        member _.CurrentVersion = baseline
        member _.CanaryVersion  = canary
        member _.Update(v: string) = baseline <- v
        member _.UpdateCanary(v: string) = canary <- v

/// Fake IEmbedder: returns a fixed 1024-dim zero vector.
type private FakeEmbedder() =
    interface IEmbedder with
        member _.EmbedAsync(_prompt: string, _ct: CancellationToken) =
            Task.FromResult(Array.zeroCreate<float32> 1024)

/// Fake IClassifier: returns a fixed score so routing target is deterministic.
/// Score 0.9 → above default MlThreshold (0.5) → routes to Qwen122B.
type private FakeClassifier() =
    interface IClassifier with
        member _.PredictAsync(_emb, _ct) =
            Task.FromResult({ Score = 0.9f; PredictedLabel = true })

/// NullCanaryGate equivalent (always returns false) — the closed-over version,
/// so the dual-classifier dispatch never picks the canary branch in these tests.
type private AlwaysBaselineGate() =
    interface ICanaryGate with
        member _.IsCanaryAsync(_correlationId, _ct) = Task.FromResult(false)

let private buildRequest (correlationId: string) (content: string) : RouterRequest =
    { Messages       = [ { Role = User; Content = content } ]
      ModelOverride  = None
      Task           = None
      Stream         = false
      Temperature    = None
      TopP           = None
      MaxTokens      = None
      CorrelationId  = correlationId
      SessionId      = ""
      UnknownFields  = Map.empty }

let private testCfg : RoutingConfig =
    { TaskTable   = Map.empty
      MlThreshold = 0.5f }

let tests =
    testList "ml-live-version" [

        // Baseline shape — the closure's RoutingDecision.ModelVersion matches the
        // version the provider was reporting at the time of the call.
        testCase "ML routing populates RoutingDecision.ModelVersion from provider on first call" <| fun () ->
            let vp = TestVersionProvider("ml-aaaaaaaa", "") :> IModelVersionProvider
            let algo =
                ML.makeApplyML
                    (FakeEmbedder() :> IEmbedder)
                    (FakeClassifier() :> IClassifier)
                    (FakeClassifier() :> IClassifier)
                    (AlwaysBaselineGate() :> ICanaryGate)
                    vp
            let req = buildRequest "cid-1" "any prompt"
            let decision = algo testCfg req
            Expect.equal decision.ModelVersion "ml-aaaaaaaa"
                "decision.ModelVersion should match the provider's CurrentVersion at call time"
            Expect.equal decision.Target Qwen122B "score 0.9 > threshold 0.5 → 122B"
            Expect.equal decision.Reason ML "ML routing reason"

        // The bug from #12 — after Update fires, the next routing call must reflect
        // the new version. Pre-fix this fails because makeApplyML captured the string.
        testCase "ML routing reads provider live: post-Update version flows into RoutingDecision (issue #12)" <| fun () ->
            let providerImpl = TestVersionProvider("ml-aaaaaaaa", "")
            let vp = providerImpl :> IModelVersionProvider
            let algo =
                ML.makeApplyML
                    (FakeEmbedder() :> IEmbedder)
                    (FakeClassifier() :> IClassifier)
                    (FakeClassifier() :> IClassifier)
                    (AlwaysBaselineGate() :> ICanaryGate)
                    vp

            // First call: baseline version observed.
            let d1 = algo testCfg (buildRequest "cid-1" "first call")
            Expect.equal d1.ModelVersion "ml-aaaaaaaa" "first call observes initial baseline"

            // Simulate RetrainingService.PerformRetrain landing a new model.
            vp.Update("ml-bbbbbbbb")

            // Second call: must reflect the NEW version, not the captured one.
            let d2 = algo testCfg (buildRequest "cid-2" "second call")
            Expect.equal d2.ModelVersion "ml-bbbbbbbb"
                "post-Update call must observe the NEW baseline version (issue #12 regression test)"

            // Third Update + call — verify monotonic flow, not just one-shot.
            vp.Update("ml-cccccccc")
            let d3 = algo testCfg (buildRequest "cid-3" "third call")
            Expect.equal d3.ModelVersion "ml-cccccccc"
                "subsequent Update calls flow through to subsequent routing decisions"
    ]
