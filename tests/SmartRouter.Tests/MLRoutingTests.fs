module SmartRouter.Tests.MLRoutingTests

open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open SmartRouter.Core.Domain
open SmartRouter.Core.Heuristic
open SmartRouter.Core.ML
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

let tests : Test =
    testSequenced <| testList "MLRoutingTests" [

        // ── ML-01: Both algorithm functions conform to RoutingAlgorithm ──────────
        testCase "Heuristic.applyHeuristic and ML.applyML both satisfy RoutingAlgorithm" <| fun () ->
            let h : RoutingAlgorithm = applyHeuristic
            let m : RoutingAlgorithm = applyML
            let req = mkReq None None "hello" 1
            let dh = h defaultConfig req
            let dm = m defaultConfig req
            Expect.isTrue (dh.Target = Qwen35B || dh.Target = Qwen122B)
                          "heuristic returns a valid model target"
            Expect.equal dm.Target Qwen35B "ML placeholder always returns Qwen35B"

        // ── ML-02: Placeholder ML.applyML always returns Qwen35B/Low/ML ─────────
        testCase "ML.applyML always returns Qwen35B/Low/ML/IsFallback=false" <| fun () ->
            let inputs =
                [ mkReq None None "hello" 1
                  mkReq None None "very long complex compiler MLIR LLVM dependency graph closure conversion ..." 1
                  mkReq None (Some "graph_indexing") "x" 1 ]
            for req in inputs do
                let d = applyML defaultConfig req
                Expect.equal d.Target Qwen35B "Target = Qwen35B for any input"
                Expect.equal d.Priority Low "Priority = Low"
                Expect.equal d.IsFallback false "IsFallback = false"
                match d.Reason with
                | ML -> ()
                | r -> failtestf "expected Reason = ML, got %A" r

        // ── ML-02/03: routeRequest dispatches the algorithm parameter ─────────────
        //   Same plain prompt → routeRequest with Heuristic.applyHeuristic AND with
        //   ML.applyML; proves the parameter actually drives dispatch by observing that
        //   the two paths produce different Reasons.
        testCase "routeRequest dispatches the algorithm parameter" <| fun () ->
            let req = mkReq None None "hello" 1  // no override, no task → stage 3 reached

            // ML path: must report Reason = ML.
            match routeRequest defaultConfig applyML req with
            | Ok d ->
                match d.Reason with
                | ML -> ()
                | r -> failtestf "expected Reason = ML on ML path, got %A" r
            | Error e -> failtestf "expected Ok on ML path, got Error %A" e

            // Heuristic path: must NOT report Reason = ML (proves the parameter actually drives dispatch).
            match routeRequest defaultConfig applyHeuristic req with
            | Ok d ->
                match d.Reason with
                | Heuristic _ | Default -> ()  // either is acceptable for a trivial prompt
                | ML -> failtest "Heuristic path returned Reason = ML — algorithm parameter was not respected"
                | r -> failtestf "expected Heuristic _ or Default on heuristic path, got %A" r
            | Error e -> failtestf "expected Ok on heuristic path, got Error %A" e

        // ── ML-02/DI: CompositionRoot registers ML.applyML when Routing:Algorithm=ml ──
        //   Uses appsettings.json copied to test bin via <None Include> in fsproj so
        //   validateConfig finds all 7 canonical TaskTable entries + Keywords + ModelAliases.
        testCase "CompositionRoot registers ML.applyML when Routing:Algorithm=ml" <| fun () ->
            let services = ServiceCollection()
            let testConfig =
                ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", optional = false)  // baseline (Algorithm=heuristic) — copied to bin via fsproj
                    .AddInMemoryCollection(dict [ "Routing:Algorithm", "ml" ])  // override to ml
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
        //   Simulates Program.fs: AddJsonFile loads baseline (Algorithm=heuristic) then
        //   AddInMemoryCollection overrides to ml (last-wins ordering). Same bin-copy
        //   appsettings.json so validateConfig passes the full Routing section.
        testCase "CLI --routing-algorithm=ml overrides config Algorithm=heuristic" <| fun () ->
            let services = ServiceCollection()
            let testConfig =
                ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", optional = false)  // baseline: Algorithm=heuristic + full Routing section
                    .AddInMemoryCollection(dict [ "Routing:Algorithm", "ml" ])  // simulates --routing-algorithm=ml CLI override
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
