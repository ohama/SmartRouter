module SmartRouter.Core.ML

open System.Threading
open System.Threading.Tasks
open SmartRouter.Core.Domain
open SmartRouter.Core.MLPorts

/// Bridge async I/O calls back to a synchronous closure body.
/// `Task.Run` ensures the awaited task runs OFF the ASP.NET SyncContext —
/// without this wrapper, `.GetAwaiter().GetResult()` deadlocks on Kestrel
/// when the inner task awaits a continuation that needs the SyncContext.
/// CPU-bound ONNX inference does not capture SyncContext, but Task.Run is
/// the canonical guard against that class of pitfall.
let private runSync (taskFactory: unit -> Task<'a>) : 'a =
    Task.Run<'a>(System.Func<Task<'a>>(taskFactory)).GetAwaiter().GetResult()

/// Real ML routing closure factory. Closes over an embedder + classifier and
/// returns a synchronous `RoutingAlgorithm` matching ML-01 (Phase 4 contract).
/// Decision rule: prediction.Score >= cfg.MlThreshold → Qwen122B, else Qwen35B.
/// Reason is always `ML`; Priority defaults to `Low` (122B priority assignment is
/// task-driven, not classifier-driven, and the ML path has no task signal).
/// IsFallback is always false in this phase (Phase 10 will set it on health-fallback).
let makeApplyML
    (embedder   : IEmbedder)
    (classifier : IClassifier)
    : RoutingAlgorithm =
    fun (cfg: RoutingConfig) (req: RouterRequest) ->
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

        { Target     = target
          Priority   = Low
          Reason     = ML
          IsFallback = false }

