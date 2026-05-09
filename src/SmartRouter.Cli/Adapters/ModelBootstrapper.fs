module SmartRouter.Cli.Adapters.ModelBootstrapper

open System
open System.IO
open Microsoft.Extensions.Logging
open Microsoft.ML
open SmartRouter.Cli.Adapters.MlNetClassifier   // RouteInput schema

/// Hard-fail if embedding model files are missing. Must be called BEFORE
/// BgeM3Embedder is constructed to give the operator a clear error message.
let ensureEmbeddingFilesPresent (logger: ILogger) (onnxPath: string) (tokenizerPath: string) : unit =
    let missing = [
        if not (File.Exists onnxPath)      then yield onnxPath
        if not (File.Exists tokenizerPath) then yield tokenizerPath
    ]
    if not (List.isEmpty missing) then
        let lines = String.concat ", " missing
        let msg =
            sprintf
                "Required ML embedding files missing: %s. Run scripts/download-models.sh and retry."
                lines
        // Log Critical so it shows up clearly even if unchecked exceptions get swallowed mid-host
        logger.LogCritical("{Message}", msg)
        raise (FileNotFoundException(msg))

/// Idempotent: writes models/router.zip ONLY if missing.
/// Generates 200 random 1024-dim samples with balanced labels and trains an LR
/// model. Routing will be ~50/50 until Phase 7-8 retrain on real data.
/// Atomic write via temp+rename so PredictionEnginePool's watcher never sees partial.
let ensureDummyModel (logger: ILogger) (modelPath: string) : unit =
    if not (File.Exists modelPath) then
        logger.LogWarning(
            "No ML classifier model found at {ModelPath}. Generating random dummy 1024-dim model. " +
            "Routing will be ~50/50 until Phase 7-8 generate real training data.",
            modelPath)
        let dir = Path.GetDirectoryName(modelPath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        let mlContext = MLContext(seed = Nullable<int>(42))
        let rng      = Random(42)
        let samples = [|
            for i in 0 .. 199 ->
                { Features = Array.init 1024 (fun _ -> float32 (rng.NextDouble() * 0.002 - 0.001))
                  Label    = i % 2 = 0 }
        |]
        let dataView = mlContext.Data.LoadFromEnumerable(samples)
        let pipeline =
            mlContext.BinaryClassification.Trainers.LbfgsLogisticRegression(
                labelColumnName   = "Label",
                featureColumnName = "Features",
                l2Regularization  = 1.0f)
        let model = pipeline.Fit(dataView)

        let tmp = modelPath + ".tmp"
        mlContext.Model.Save(model, dataView.Schema, tmp)
        if File.Exists modelPath then File.Delete modelPath
        File.Move(tmp, modelPath)
        logger.LogInformation("Dummy classifier model written to {ModelPath}", modelPath)

/// Compute model_version: SHA-256 of router.zip, first 8 hex chars (4 bytes).
/// Returns "unknown" if file missing — should not happen post-ensureDummyModel.
let computeModelVersion (modelPath: string) : string =
    if File.Exists modelPath then
        use sha    = System.Security.Cryptography.SHA256.Create()
        use stream = File.OpenRead(modelPath)
        let hash   = sha.ComputeHash(stream)
        hash
        |> Array.take 4
        |> Array.map (fun b -> sprintf "%02x" b)
        |> String.concat ""
    else
        "unknown"
