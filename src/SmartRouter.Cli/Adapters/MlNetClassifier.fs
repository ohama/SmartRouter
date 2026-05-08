module SmartRouter.Cli.Adapters.MlNetClassifier

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.ML
open Microsoft.ML.Data
open SmartRouter.Core.MLPorts

/// ML.NET schema types — must be `[<CLIMutable>]` so reflection-based prop set works.
[<CLIMutable>]
type RouteInput =
    { [<VectorType(1024)>]
      Features : float32[]
      Label    : bool }

[<CLIMutable>]
type RoutePrediction =
    { [<ColumnName("PredictedLabel")>]
      Predicted   : bool
      Score       : float32
      Probability : float32 }

/// IClassifier wrapper around PredictionEnginePool.
/// Pool is singleton (registered via AddPredictionEnginePool); this adapter
/// is also singleton; both share lifetime so watchForChanges hot-reload (Phase 8) works.
type MlNetClassifier(pool: PredictionEnginePool<RouteInput, RoutePrediction>) =
    interface IClassifier with
        member _.PredictAsync(embedding: float32[], _ct: CancellationToken) : Task<ClassifierPrediction> =
            task {
                let input = { Features = embedding; Label = false }
                let pred  = pool.Predict(modelName = "router", example = input)
                return
                    { Score          = pred.Probability
                      PredictedLabel = pred.Predicted }
            }
