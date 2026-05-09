module SmartRouter.Cli.Adapters.ModelBootstrapper

open System
open System.IO
open Microsoft.Extensions.Logging
open Microsoft.ML
open SmartRouter.Cli.Adapters.MlNetClassifier   // RouteInput schema

/// Issue #9: resolve a relative model path CWD-independently. Tries, in order:
///   1. The path as given (absolute → use as-is; relative → CWD-relative).
///   2. Path joined to AppContext.BaseDirectory (the binary's directory).
///   3. Walk up to 5 levels of parents from AppContext.BaseDirectory looking for
///      a `models/` sibling that contains the relative path.
///
/// First existing file wins. If none exist, returns the original path so the
/// caller's existing missing-file diagnostic still fires with a recognizable
/// path. Works for both file paths and directory-prefixed paths.
let resolveModelPath (configPath: string) : string =
    if Path.IsPathRooted(configPath) then
        configPath
    else
        // 3. Walk up parents of AppContext.BaseDirectory looking for `models/`.
        //    Typical depth: bin/Debug/net10.0/ → up 3 to project dir → up 1 to src/ → up 1 to repo root.
        let walkUp =
            let mutable dir = DirectoryInfo(AppContext.BaseDirectory)
            [ for _ in 1 .. 5 do
                if not (isNull dir) then
                    yield Path.Combine(dir.FullName, configPath)
                    dir <- dir.Parent ]
        let candidates =
            // 1. CWD-relative (back-compat for launchd / explicit cd-into-repo-root flows)
            // 2. Binary-relative (covers `dotnet run --project src/SmartRouter.Cli` from repo root,
            //    where CWD ends up at src/SmartRouter.Cli/ but bin/Debug/net10.0/ is the actual base)
            [ Path.GetFullPath(configPath)
              Path.Combine(AppContext.BaseDirectory, configPath) ]
            @ walkUp
        candidates
        |> List.tryFind File.Exists
        |> Option.defaultValue (Path.GetFullPath(configPath))

/// Hard-fail if embedding model files are missing. Must be called BEFORE
/// BgeM3Embedder is constructed to give the operator a clear error message.
///
/// As of issue #9, paths are resolved CWD-independently via `resolveModelPath`
/// — operators running `dotnet run --project src/SmartRouter.Cli` from the repo
/// root no longer need a symlink to make `models/` resolvable.
let ensureEmbeddingFilesPresent (logger: ILogger) (onnxPath: string) (tokenizerPath: string) : unit =
    let onnx      = resolveModelPath onnxPath
    let tokenizer = resolveModelPath tokenizerPath
    let missing = [
        if not (File.Exists onnx)      then yield onnxPath
        if not (File.Exists tokenizer) then yield tokenizerPath
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
