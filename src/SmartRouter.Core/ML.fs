module SmartRouter.Core.ML

open System
open System.Threading
open System.Threading.Tasks
open SmartRouter.Core.Domain
open SmartRouter.Core.MLPorts
open SmartRouter.Core.CanaryPorts
open SmartRouter.Core.RetrainingPorts   // IModelVersionProvider — live version read per call (issue #12)

/// Bridge async I/O calls back to a synchronous closure body.
/// `Task.Run` ensures the awaited task runs OFF the ASP.NET SyncContext —
/// without this wrapper, `.GetAwaiter().GetResult()` deadlocks on Kestrel
/// when the inner task awaits a continuation that needs the SyncContext.
/// CPU-bound ONNX inference does not capture SyncContext, but Task.Run is
/// the canonical guard against that class of pitfall.
let private runSync (taskFactory: unit -> Task<'a>) : 'a =
    Task.Run<'a>(Func<Task<'a>>(taskFactory)).GetAwaiter().GetResult()

/// Real ML routing closure factory. Phase 9 adds canary-cohort dispatch:
///   1. Read req.CorrelationId. If empty → bypass canary (isCanary = false).
///   2. Else → query canaryGate.IsCanaryAsync(correlationId).
///   3. Select (classifier, modelVersion) from THE SAME boolean — RESEARCH §11 Pitfall 8.
///   4. Embed prompt + classify (same shape as Phase 6).
///   5. Emit RoutingDecision with ModelVersion = versionProvider.CurrentVersion or .CanaryVersion.
///
/// Issue #12 fix: takes `IModelVersionProvider` instead of baseline/canary version
/// strings. Pre-fix the strings were captured at DI factory time and never updated
/// after `RetrainingService.Update` / `CanaryService.UpdateCanary` mutated the live
/// values, so DecisionLog rows kept reporting the pre-retrain version. Reading from
/// the provider per call closes the gap.
///
/// Decision rule: prediction.Score >= cfg.MlThreshold → Qwen122B, else Qwen35B.
/// Reason is always `ML`; Priority defaults to `Low`. IsFallback always false in this phase
/// (Phase 10 will set it on real fallback).
let makeApplyML
    (embedder           : IEmbedder)
    (baselineClassifier : IClassifier)
    (canaryClassifier   : IClassifier)
    (canaryGate         : ICanaryGate)
    (versionProvider    : IModelVersionProvider)
    : RoutingAlgorithm =
    fun (cfg: RoutingConfig) (req: RouterRequest) ->
        // RESEARCH §11 Pitfall 8: the SAME isCanary boolean gates classifier selection AND
        // the ModelVersion field below. Keep these two uses visually adjacent.
        let isCanary =
            if String.IsNullOrEmpty(req.CorrelationId) then false
            else runSync (fun () -> canaryGate.IsCanaryAsync(req.CorrelationId, CancellationToken.None))

        // Issue #12: read versions LIVE from the provider — not captured at factory time.
        // RetrainingService.Update flows through here on the very next request after retrain.
        let (classifier, modelVersion) =
            if isCanary then (canaryClassifier, versionProvider.CanaryVersion)
            else             (baselineClassifier, versionProvider.CurrentVersion)

        let prompt =
            req.Messages
            |> List.map (fun m -> m.Content)
            |> String.concat " "

        let embedding =
            runSync (fun () -> embedder.EmbedAsync(prompt, CancellationToken.None))

        let prediction =
            runSync (fun () -> classifier.PredictAsync(embedding, CancellationToken.None))

        let target =
            if prediction.Score >= cfg.MlThreshold then Qwen122B
            else Qwen35B

        { Target       = target
          Priority     = Low
          Reason       = ML
          IsFallback   = false
          ModelVersion = modelVersion }
