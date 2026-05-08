module SmartRouter.Tests.MLClassifierTests

open System
open System.IO
open System.Threading
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.ML
open SmartRouter.Cli.Adapters.MlNetClassifier
open SmartRouter.Cli.Adapters.ModelBootstrapper

// ── Test temp-dir hygiene ────────────────────────────────────────────────────

let private mkTempDir () : string =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-classifier-tests-" + Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    dir

let private cleanupDir (dir: string) =
    try
        if Directory.Exists dir then Directory.Delete(dir, recursive = true)
    with _ -> ()

// ── Tests ────────────────────────────────────────────────────────────────────

let tests : Test =
    testSequenced <| testList "MLClassifierTests" [

        // ── CLS-02: first-run bootstrap creates router.zip ────────────────────
        testCase "CLS-02: ensureDummyModel creates router.zip when missing (idempotent on second call)" <| fun () ->
            let dir = mkTempDir ()
            try
                let modelPath = Path.Combine(dir, "router.zip")
                Expect.isFalse (File.Exists modelPath) "precondition: file does not exist"

                ensureDummyModel modelPath
                Expect.isTrue  (File.Exists modelPath) "after first call: file exists"
                let firstSize = (FileInfo modelPath).Length

                // Second call is a no-op (already exists)
                ensureDummyModel modelPath
                let secondSize = (FileInfo modelPath).Length
                Expect.equal firstSize secondSize "idempotent: second call did not rewrite the file"
            finally
                cleanupDir dir

        // ── CLS-01: PredictionEnginePool resolves dummy model and predicts on 1024-dim ──
        testCase "CLS-01: PredictionEnginePool loads bootstrapped model and predicts on 1024-dim vector" <| fun () ->
            let dir = mkTempDir ()
            try
                let modelPath = Path.Combine(dir, "router.zip")
                ensureDummyModel modelPath

                // Build a minimal DI container that registers the pool against the temp model
                let services = ServiceCollection()
                services.AddLogging() |> ignore
                services
                    .AddPredictionEnginePool<RouteInput, RoutePrediction>()
                    .FromFile(
                        modelName       = "router",
                        filePath        = modelPath,
                        watchForChanges = false)
                    |> ignore

                use sp = services.BuildServiceProvider()
                let pool = sp.GetRequiredService<PredictionEnginePool<RouteInput, RoutePrediction>>()
                let classifier = MlNetClassifier(pool, "router") :> SmartRouter.Core.MLPorts.IClassifier

                // Random 1024-dim input
                let rng = Random(7)
                let embedding = Array.init 1024 (fun _ -> float32 (rng.NextDouble()))
                let pred = classifier.PredictAsync(embedding, CancellationToken.None).GetAwaiter().GetResult()

                Expect.isTrue (pred.Score >= 0.0f && pred.Score <= 1.0f)
                              (sprintf "Score in [0,1] (got %f)" pred.Score)
            finally
                cleanupDir dir

        // ── CLS-02 helper: computeModelVersion produces 8-hex-char hash ─────
        testCase "CLS-02: computeModelVersion returns 8 lowercase hex chars from router.zip" <| fun () ->
            let dir = mkTempDir ()
            try
                let modelPath = Path.Combine(dir, "router.zip")
                ensureDummyModel modelPath

                let v = computeModelVersion modelPath
                Expect.equal v.Length 8 "8 hex chars (4 bytes of SHA-256)"
                let allLowerHex = v |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
                Expect.isTrue allLowerHex
                              (sprintf "all chars are lowercase hex (got %s)" v)
            finally
                cleanupDir dir
    ]
