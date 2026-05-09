module SmartRouter.Cli.Adapters.Retrainer

open System
open System.IO
open Microsoft.Extensions.Logging
open Microsoft.ML
open Microsoft.ML.Data

/// ML.NET training schema. [<CLIMutable>] required — ML.NET uses reflection-based prop set.
/// Same shape as MlNetClassifier.RouteInput so saved model schema matches the live pool.
[<CLIMutable>]
type TrainSample =
    { [<VectorType(1024)>]
      Features : float32[]
      Label    : bool }   // true = Route122B (positive class)

/// Train an ML.NET LbfgsLogisticRegression on the provided IDataView and atomically write
/// the model to modelPath. Returns the trained ITransformer so the caller (Plan 08-02
/// RetrainingService) can pass it directly to Validator.validate WITHOUT re-loading from disk.
///
/// **CONTEXT.md Lock 5 — caller-driven train/test split:** The caller is responsible for
/// constructing the MLContext, building the IDataView, and (when validation is needed)
/// performing the TrainTestSplit. Retrainer trains on whatever IDataView is passed in;
/// for production retraining this MUST be `split.TrainSet` so the held-out portion
/// (`split.TestSet`) is genuinely unseen by both candidate AND baseline.
///
/// **Why MLContext is a parameter, not internally constructed:** the same MLContext must
/// be used for the TrainTestSplit (caller), training (Retrainer), and evaluation
/// (Validator) so the pipeline schema/seed is consistent. Threading a single MLContext
/// through avoids subtle reseeding bugs and matches ML.NET's documented ownership model.
///
/// CONTEXT.md Lock 8: atomic write via .tmp + File.Move(overwrite=true).
/// MUST NOT call File.Delete(modelPath) before the move — creates a window where
/// PredictionEnginePool sees a missing file.

let retrain
    (logger            : ILogger)
    (mlContext         : MLContext)
    (trainView         : IDataView)
    (modelPath         : string)
    (l2Regularization  : float32)
    : ITransformer =

    let pipeline =
        mlContext.BinaryClassification.Trainers.LbfgsLogisticRegression(
            labelColumnName   = "Label",
            featureColumnName = "Features",
            l2Regularization  = l2Regularization)

    let model = pipeline.Fit(trainView)

    let dir = Path.GetDirectoryName(modelPath)
    if not (String.IsNullOrEmpty(dir)) && not (Directory.Exists(dir)) then
        Directory.CreateDirectory(dir) |> ignore

    let tmp = modelPath + ".tmp"
    mlContext.Model.Save(model, trainView.Schema, tmp)
    // Atomic — single rename(2) syscall on Unix; PredictionEnginePool's watcher reloads.
    File.Move(tmp, modelPath, overwrite = true)
    logger.LogInformation("Retrainer: model written to {ModelPath}", modelPath)

    model
