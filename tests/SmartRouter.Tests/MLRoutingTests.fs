module SmartRouter.Tests.MLRoutingTests

open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open SmartRouter.Core.Domain
open SmartRouter.Core.Heuristic
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

        // ── ML-01: Both algorithm functions conform to RoutingAlgorithm ──────────
        testCase "Heuristic.applyHeuristic and ML.makeApplyML closure both satisfy RoutingAlgorithm" <| fun () ->
            let h : RoutingAlgorithm = applyHeuristic
            let fakeEmb =
                { new SmartRouter.Core.MLPorts.IEmbedder with
                    member _.EmbedAsync(_, _) =
                        System.Threading.Tasks.Task.FromResult(Array.create 1024 0.1f) }
            let fakeCls =
                { new SmartRouter.Core.MLPorts.IClassifier with
                    member _.PredictAsync(_, _) =
                        System.Threading.Tasks.Task.FromResult(
                            { Score = 0.4f; PredictedLabel = false }
                            : SmartRouter.Core.MLPorts.ClassifierPrediction) }
            let m : RoutingAlgorithm = SmartRouter.Core.ML.makeApplyML fakeEmb fakeCls
            let req = mkReq None None "hello" 1
            let dh = h defaultConfig req
            let dm = m defaultConfig req
            Expect.isTrue (dh.Target = Qwen35B || dh.Target = Qwen122B)
                          "heuristic returns a valid model target"
            Expect.equal dm.Reason ML "ML closure reports Reason = ML"
            Expect.isTrue (dm.Target = Qwen35B || dm.Target = Qwen122B)
                          "ML closure returns a valid model target"

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

            // score below threshold → 35B
            let lowAlgo = SmartRouter.Core.ML.makeApplyML fakeEmb (mkClassifier 0.3f)
            let dLow = lowAlgo cfg req
            Expect.equal dLow.Target Qwen35B  "score 0.3 < 0.5 → 35B"
            Expect.equal dLow.Reason ML        "Reason = ML"
            Expect.equal dLow.Priority Low     "Priority = Low"
            Expect.equal dLow.IsFallback false "IsFallback = false"

            // score at/above threshold → 122B
            let highAlgo = SmartRouter.Core.ML.makeApplyML fakeEmb (mkClassifier 0.8f)
            let dHigh = highAlgo cfg req
            Expect.equal dHigh.Target Qwen122B "score 0.8 ≥ 0.5 → 122B"
            Expect.equal dHigh.Reason ML       "Reason = ML"

        // ── ML-02/03: routeRequest dispatches the algorithm parameter ─────────────
        testCaseAsync "routeRequest dispatches the algorithm parameter" <| async {
            // Fake ports — embed returns deterministic vector; classify returns
            // Score=0.9 (above threshold) so the ML path lands on Qwen122B with Reason=ML.
            let fakeEmbedder =
                { new SmartRouter.Core.MLPorts.IEmbedder with
                    member _.EmbedAsync(_prompt, _ct) =
                        System.Threading.Tasks.Task.FromResult(Array.create 1024 0.5f) }
            let fakeClassifier =
                { new SmartRouter.Core.MLPorts.IClassifier with
                    member _.PredictAsync(_emb, _ct) =
                        System.Threading.Tasks.Task.FromResult(
                            { Score = 0.9f; PredictedLabel = true }
                            : SmartRouter.Core.MLPorts.ClassifierPrediction) }
            let cfg    = { defaultConfig with MlThreshold = 0.5f }
            let mlAlgo = SmartRouter.Core.ML.makeApplyML fakeEmbedder fakeClassifier
            let req    = mkReq None None "a plain prompt" 1

            // ML path with high score → routes 122B (Reason = ML)
            let mlResult = routeRequest cfg mlAlgo req
            match mlResult with
            | Ok d ->
                Expect.equal d.Target Qwen122B "ml path: Score 0.9 ≥ 0.5 → 122B"
                Expect.equal d.Reason ML        "ml path: Reason = ML"
                Expect.equal d.Priority Low     "ml path: Priority = Low"
                Expect.equal d.IsFallback false "ml path: IsFallback = false"
            | Error e -> failtestf "expected Ok from ml path, got Error %A" e

            // Heuristic path on same prompt: Reason should differ from ML — proves
            // routeRequest is honoring the algorithm parameter (not hard-coded to ML).
            let heuristicResult = routeRequest cfg applyHeuristic req
            match heuristicResult with
            | Ok { Reason = Heuristic _ } | Ok { Reason = Default } -> ()
            | Ok d -> failtestf "expected Heuristic or Default reason on heuristic path, got %A" d.Reason
            | Error e -> failtestf "expected Ok from heuristic path, got Error %A" e
        }

        // ── ML-02/DI: CompositionRoot registers makeApplyML when Routing:Algorithm=ml ──
        //   Uses appsettings.json copied to test bin via <None Include> in fsproj so
        //   validateConfig finds all 7 canonical TaskTable entries + Keywords + ModelAliases.
        //   NOTE: When Routing.Algorithm=ml, ensureEmbeddingFilesPresent runs FIRST —
        //   if models/embed/* are missing this test will raise FileNotFoundException.
        //   Therefore: gate with mlTestCase (pending/skip when files absent).
        mlTestCase "CompositionRoot registers makeApplyML when Routing:Algorithm=ml" <| fun () ->
            let services = ServiceCollection()
            let testConfig =
                ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", optional = false)  // baseline — copied to bin via fsproj
                    .AddInMemoryCollection(dict [
                        "Routing:Algorithm", "ml"
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

        // ── ML-03/CLI: CLI --routing-algorithm=ml override beats config heuristic ──
        //   Simulates Program.fs: AddJsonFile loads baseline then AddInMemoryCollection
        //   overrides to ml (last-wins ordering).
        mlTestCase "CLI --routing-algorithm=ml overrides config Algorithm=heuristic" <| fun () ->
            let services = ServiceCollection()
            let testConfig =
                ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", optional = false)  // baseline: Algorithm=heuristic
                    .AddInMemoryCollection(dict [
                        "Routing:Algorithm", "ml"   // simulates --routing-algorithm=ml CLI override
                        "DecisionLog:Directory", "logs/decisions-test-ml-cli"
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
            | r -> failtestf "expected Reason = ML from CLI-overridden router, got %A" r

    ]
