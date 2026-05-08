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

/// IClassifier wrapper around PredictionEnginePool with a configurable modelName.
/// Phase 6 wired this with a hardcoded "router". Phase 9 makes it parameterizable so
/// the same wrapper can serve baseline and canary registrations from the same pool.
type MlNetClassifier(pool: PredictionEnginePool<RouteInput, RoutePrediction>, modelName: string) =
    interface IClassifier with
        member _.PredictAsync(embedding: float32[], _ct: CancellationToken) : Task<ClassifierPrediction> =
            task {
                let input = { Features = embedding; Label = false }
                let pred  = pool.Predict(modelName = modelName, example = input)
                return
                    { Score          = pred.Probability
                      PredictedLabel = pred.Predicted }
            }
