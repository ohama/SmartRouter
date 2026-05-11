module SmartRouter.Tests.MLRoutingTests

open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open SmartRouter.Core.Domain
open SmartRouter.Core.Routing
open SmartRouter.Cli.CompositionRoot

/// Minimal request builder — mirrors RoutingTests.fs private helper.
/// Copied here so MLRoutingTests has no private-visibility dependency on another test module.
let private mkReq (task: string option) (model: string option) (content: string) (msgs: int) : RouterRequest =
    let messages =
        [ for _ in 1 .. msgs ->
            { Role = User; Content = content } ]
    { Messages      = messages
      ModelOverride  = model
      Task           = task
      Stream         = false
      Temperature    = None
      TopP           = None
      MaxTokens      = None
      CorrelationId  = ""
      SessionId      = ""
      UnknownFields  = Map.empty }

let private defaultConfig : RoutingConfig = defaultRoutingConfig

/// Gate for tests that require the real bge-m3 ONNX + tokenizer files.
/// Returns `testCase` when files are present; `ptestCase` (pending/skipped) otherwise.
let private mlEmbeddingFilesPresent =
    System.IO.File.Exists "models/embed/bge-m3-int8.onnx"
    && System.IO.File.Exists "models/embed/sentencepiece.bpe.model"

let private mlTestCase name body =
    if mlEmbeddingFilesPresent then testCase name body
    else ptestCase name body   // pending — print "skipped: download-models.sh not run"

let tests : Test =
    testSequenced <| testList "MLRoutingTests" [

        // ── ML-02: makeApplyML threshold contract ────────────────────────────────
        testCase "ML.makeApplyML returns Qwen122B when score >= threshold, Qwen35B otherwise" <| fun () ->
            let fakeEmb =
                { new SmartRouter.Core.MLPorts.IEmbedder with
                    member _.EmbedAsync(_, _) =
                        System.Threading.Tasks.Task.FromResult(Array.create 1024 0.1f) }
            let mkClassifier (score: float32) =
                { new SmartRouter.Core.MLPorts.IClassifier with
                    member _.PredictAsync(_, _) =
                        System.Threading.Tasks.Task.FromResult(
                            { Score = score; PredictedLabel = score >= 0.5f }
                            : SmartRouter.Core.MLPorts.ClassifierPrediction) }
            let cfg = { defaultConfig with MlThreshold = 0.5f }
            let req = mkReq None None "hello" 1

            let nullGate =
                { new SmartRouter.Core.CanaryPorts.ICanaryGate with
                    member _.IsCanaryAsync(_, _) =
                        System.Threading.Tasks.Task.FromResult(false) }

            // Issue #12: makeApplyML now takes IModelVersionProvider (live read) instead of
            // captured strings. Inline stub returns "baseline-v1" baseline + "" canary.
            let stubVp =
                { new SmartRouter.Core.RetrainingPorts.IModelVersionProvider with
                    member _.CurrentVersion = "baseline-v1"
                    member _.CanaryVersion  = ""
                    member _.Update(_)      = ()
                    member _.UpdateCanary(_)= () }

            // score below threshold → 35B
            let lowAlgo = SmartRouter.Core.ML.makeApplyML fakeEmb (mkClassifier 0.3f) (mkClassifier 0.3f) nullGate stubVp
            let dLow = lowAlgo cfg req
            Expect.equal dLow.Target Qwen35B  "score 0.3 < 0.5 → 35B"
            Expect.equal dLow.Reason ML        "Reason = ML"
            Expect.equal dLow.Priority Low     "Priority = Low"
            Expect.equal dLow.IsFallback false "IsFallback = false"

            // score at/above threshold → 122B
            let highAlgo = SmartRouter.Core.ML.makeApplyML fakeEmb (mkClassifier 0.8f) (mkClassifier 0.8f) nullGate stubVp
            let dHigh = highAlgo cfg req
            Expect.equal dHigh.Target Qwen122B "score 0.8 ≥ 0.5 → 122B"
            Expect.equal dHigh.Reason ML       "Reason = ML"

        // ── ML-02/DI: CompositionRoot registers makeApplyML (ml is the only algorithm) ──
        //   Uses appsettings.json copied to test bin via <None Include> in fsproj so
        //   validateConfig finds all 7 canonical TaskTable entries + Keywords + ModelAliases.
        //   NOTE: ensureEmbeddingFilesPresent runs FIRST —
        //   if models/embed/* are missing this test will raise FileNotFoundException.
        //   Therefore: gate with mlTestCase (pending/skip when files absent).
        mlTestCase "CompositionRoot registers makeApplyML as the routing algorithm" <| fun () ->
            let services = ServiceCollection()
            let testConfig =
                ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", optional = false)  // baseline — copied to bin via fsproj
                    .AddInMemoryCollection(dict [
                        "DecisionLog:Directory", "logs/decisions-test-ml-di"
                    ])
                    .Build()
            configureServices services testConfig |> ignore
            use sp = services.BuildServiceProvider()
            let algorithm = sp.GetRequiredService<RoutingAlgorithm>()
            let runtimeConfig = sp.GetRequiredService<RoutingConfig>()
            let req = mkReq None None "hello" 1
            let decision = algorithm runtimeConfig req
            match decision.Reason with
            | ML -> ()
            | r -> failtestf "expected Reason = ML from ml-configured DI, got %A" r

        // ── Phase 6: model_version is hash-based, not the placeholder ────────────────
        testCase "Phase 6: RoutingAlgorithmRegistration.ModelVersion = sprintf \"ml-%s\" (8 hex chars)" <| fun () ->
            // Skip when embedding files are absent — DI-based test goes through the real ml-branch wiring
            if not (System.IO.File.Exists "models/embed/bge-m3-int8.onnx"
                    && System.IO.File.Exists "models/embed/sentencepiece.bpe.model") then
                skiptest "embedding files missing — run scripts/download-models.sh"

            let services = ServiceCollection()
            let testConfig =
                ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", optional = false)
                    .Build()
            configureServices services testConfig |> ignore
            use sp = services.BuildServiceProvider()
            let reg = sp.GetRequiredService<SmartRouter.Cli.Adapters.RoutingAlgorithm.RoutingAlgorithmRegistration>()
            Expect.equal reg.Name "ml" "Name = ml"
            Expect.isTrue (reg.ModelVersion.StartsWith "ml-") (sprintf "starts with ml- (got %s)" reg.ModelVersion)
            Expect.equal reg.ModelVersion.Length 11 "ml- + 8 hex chars = 11 chars total"
            Expect.notEqual reg.ModelVersion "ml-v0-placeholder" "no longer the Phase 4 placeholder"

        // ── Phase 6: DI smoke — IEmbedder + IClassifier resolve from ml-branch ───────
        testCase "Phase 6: DI ml-branch resolves IEmbedder + IClassifier without throwing" <| fun () ->
            if not (System.IO.File.Exists "models/embed/bge-m3-int8.onnx"
                    && System.IO.File.Exists "models/embed/sentencepiece.bpe.model") then
                skiptest "embedding files missing — run scripts/download-models.sh"

            let services = ServiceCollection()
            let testConfig =
                ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", optional = false)
                    .Build()
            configureServices services testConfig |> ignore
            use sp = services.BuildServiceProvider()
            let emb = sp.GetRequiredService<SmartRouter.Core.MLPorts.IEmbedder>()
            let cls = sp.GetRequiredService<SmartRouter.Core.MLPorts.IClassifier>()
            // GetRequiredService throws if not registered — reaching here proves both resolved
            Expect.isNotNull (box emb) "IEmbedder resolves"
            Expect.isNotNull (box cls) "IClassifier resolves"

    ]
