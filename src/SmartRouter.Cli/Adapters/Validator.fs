module SmartRouter.Cli.Adapters.Validator

open System
open System.IO
open System.Text
open System.Text.Json
open Microsoft.ML
open Microsoft.ML.Data
open Serilog
open SmartRouter.Cli.Adapters.Retrainer
open SmartRouter.Cli.Adapters.MlNetClassifier   // RouteInput (read-back schema)

/// Validation outcome.
/// Accepted: model passes both gates; modelVersion is the SHA prefix of the new file.
/// Rejected: model fails accuracy or fallback_rate gate; reason is a human-readable string
///   that goes into both the rejection log and the live Serilog stream.
type ValidationResult =
    | Accepted of accuracy: float * fallbackRate: float
    | Rejected of reason: string

/// Compute baseline metrics by loading current router.zip and evaluating on the same held-out set.
/// Returns (baselineAccuracy, baselineFallbackRate) where:
///   baselineFallbackRate = 1.0 - PositiveRecall (CONTEXT.md Lock 1)
///
/// If router.zip does not exist (shouldn't happen post-bootstrap), returns (0.0, 1.0)
/// — meaning "no baseline; any working model passes" — and logs Warning.

let computeBaseline
    (mlContext  : MLContext)
    (modelPath  : string)
    (heldOutDV  : IDataView)
    : float * float =

    if not (File.Exists modelPath) then
        Log.Warning("Validator.computeBaseline: {ModelPath} missing; using neutral baseline (acc=0, fbRate=1)", modelPath)
        0.0, 1.0
    else
        let mutable schemaUsed = Unchecked.defaultof<DataViewSchema>
        let baselineModel = mlContext.Model.Load(modelPath, &schemaUsed)
        let predictions = baselineModel.Transform(heldOutDV)
        let metrics =
            mlContext.BinaryClassification.Evaluate(
                data            = predictions,
                labelColumnName = "Label",
                scoreColumnName = "Score")
        // CONTEXT.md Lock 1: fallback_rate := 1.0 - PositiveRecall
        metrics.Accuracy, (1.0 - metrics.PositiveRecall)

/// Run validation on a freshly-trained model against a held-out IDataView.
/// Compares newModel metrics against baseline metrics computed via computeBaseline.
/// Reject when newAcc < baselineAcc OR newFbRate > baselineFbRate (strictly).

let validate
    (mlContext       : MLContext)
    (newModel        : ITransformer)
    (heldOutDV       : IDataView)
    (baselineAcc     : float)
    (baselineFbRate  : float)
    : ValidationResult =

    let predictions = newModel.Transform(heldOutDV)
    let metrics =
        mlContext.BinaryClassification.Evaluate(
            data            = predictions,
            labelColumnName = "Label",
            scoreColumnName = "Score")

    let newAcc    = metrics.Accuracy
    let newFbRate = 1.0 - metrics.PositiveRecall

    if newAcc < baselineAcc then
        Rejected (sprintf "accuracy regression: %.4f < baseline %.4f" newAcc baselineAcc)
    elif newFbRate > baselineFbRate then
        Rejected (sprintf "fallback_rate increase: %.4f > baseline %.4f (1 - PositiveRecall)" newFbRate baselineFbRate)
    else
        Accepted (newAcc, newFbRate)

/// Append a JSONL rejection record to logs/retraining-rejections.jsonl.
/// Synchronous write (no BackgroundService — volume is low; rejections happen rarely).
/// Schema:
///   { schema_version: 1, timestamp: <ISO8601>, reason: <string>,
///     baseline_accuracy: <float>, baseline_fallback_rate: <float>,
///     candidate_samples: <int> }
///
/// Atomic via FileMode.Append + sw.Flush() (matches DecisionLogWriter pattern).

let writeRejectionLog
    (rejectionLogPath  : string)
    (reason            : string)
    (baselineAcc       : float)
    (baselineFbRate    : float)
    (candidateSamples  : int)
    : unit =

    let dir = Path.GetDirectoryName(rejectionLogPath)
    if not (String.IsNullOrEmpty(dir)) && not (Directory.Exists(dir)) then
        Directory.CreateDirectory(dir) |> ignore

    // Build the record as a Dictionary so we don't pay the F# record + STJ converter setup cost
    // for a single line. Snake_case is enforced by literal key names.
    let record = System.Collections.Generic.Dictionary<string, obj>()
    record.["schema_version"]         <- box 1
    record.["timestamp"]              <- box (DateTimeOffset.UtcNow.ToString("o"))
    record.["reason"]                 <- box reason
    record.["baseline_accuracy"]      <- box baselineAcc
    record.["baseline_fallback_rate"] <- box baselineFbRate
    record.["candidate_samples"]      <- box candidateSamples

    let line = JsonSerializer.Serialize(record)
    use stream = new FileStream(rejectionLogPath, FileMode.Append, FileAccess.Write, FileShare.None)
    use writer = new StreamWriter(stream, Encoding.UTF8)
    writer.WriteLine(line)
    writer.Flush()
    Log.Warning(
        "Validator: rejected new model — {Reason} (baselineAcc={BAcc}, baselineFbRate={BFb})",
        reason, baselineAcc, baselineFbRate)
