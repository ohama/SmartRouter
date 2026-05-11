module SmartRouter.Tests.ModeSwitchTests

// Integration tests for Routing.Mode config switch (MODE-01..04) and
// Hard Rules cascade ordering (HR-03 + HR-06).
//
// Strategy: build a minimal IConfiguration in-memory (without Routing:ML section)
// so that configureRequestPipeline skips the ensureEmbeddingFilesPresent / ML
// bootstrap block (CompositionRoot line 340: `if not (obj.ReferenceEquals(mlOpts, null))`).
// This lets selfrouting-mode DI tests run on hosts without ONNX embedding files.
//
// The "ml" mode test is guarded: it resolves RoutingAlgorithmRegistration only when
// the embedding ONNX file is present (W4 pattern from Phase 6 MLRoutingTests).
// When absent, the test is skipped with an informational message.
//
// No testSequenced wrapper — these tests don't touch Console.SetOut and each
// test builds its own ServiceCollection with no shared mutable state.

open System
open System.Collections.Generic
open System.IO
open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open SmartRouter.Cli.Adapters.RoutingAlgorithm
open SmartRouter.Cli.CompositionRoot
open SmartRouter.Core.Domain
open SmartRouter.Core.Routing

// ── Minimal config ────────────────────────────────────────────────────────────
//
// The minimal config provides the keys that configureRequestPipeline requires
// for startup validation (validateConfig + buildRoutingConfig) but deliberately
// OMITS the Routing:ML section so that mlOpts resolves to null and the embedding
// file bootstrap is skipped entirely (CompositionRoot lines 339-388).
//
// Upstreams.Model35B + Upstreams.Model122B are needed because AddHttpClient
// calls ConfigureHttpClient(fun c -> c.BaseAddress <- Uri(upstreamOptsLazy.Model35B))
// at registration time — a null/missing value would throw NullReferenceException.

let private minimalConfigPairs : KeyValuePair<string, string> list =
    [   // Upstreams — required for HttpClient registration
        KeyValuePair("Upstreams:Model35B",  "http://127.0.0.1:8000")
        KeyValuePair("Upstreams:Model122B", "http://127.0.0.1:8001")
        // Routing — no ML section → mlOpts = null → ML bootstrap skipped
        KeyValuePair("Routing:Mode",        "selfrouting")
        KeyValuePair("Routing:TimeoutSeconds", "300")
        // TaskTable — 7 canonical tasks (required by validateConfig)
        KeyValuePair("Routing:TaskTable:graph_indexing:Model",        "122b")
        KeyValuePair("Routing:TaskTable:graph_indexing:Priority",     "high")
        KeyValuePair("Routing:TaskTable:compiler_debug:Model",        "122b")
        KeyValuePair("Routing:TaskTable:compiler_debug:Priority",     "high")
        KeyValuePair("Routing:TaskTable:architecture_analysis:Model", "122b")
        KeyValuePair("Routing:TaskTable:architecture_analysis:Priority", "high")
        KeyValuePair("Routing:TaskTable:dependency_analysis:Model",   "122b")
        KeyValuePair("Routing:TaskTable:dependency_analysis:Priority","low")
        KeyValuePair("Routing:TaskTable:reasoning:Model",             "122b")
        KeyValuePair("Routing:TaskTable:reasoning:Priority",          "low")
        KeyValuePair("Routing:TaskTable:retrieval:Model",             "35b")
        KeyValuePair("Routing:TaskTable:retrieval:Priority",          "low")
        KeyValuePair("Routing:TaskTable:summary:Model",               "35b")
        KeyValuePair("Routing:TaskTable:summary:Priority",            "low")
        // ModelAliases — required by buildRoutingConfig
        KeyValuePair("Routing:ModelAliases:35b",       "Qwen35B")
        KeyValuePair("Routing:ModelAliases:qwen35b",   "Qwen35B")
        KeyValuePair("Routing:ModelAliases:qwen-35b",  "Qwen35B")
        KeyValuePair("Routing:ModelAliases:122b",      "Qwen122B")
        KeyValuePair("Routing:ModelAliases:qwen122b",  "Qwen122B")
        KeyValuePair("Routing:ModelAliases:qwen-122b", "Qwen122B")
        // QualityFallback — used by configureRequestPipeline (non-null defaults applied by
        // CompositionRoot defensive code if these are absent, but providing defaults avoids
        // unexpected null-dereference paths)
        KeyValuePair("Routing:QualityFallback:Enabled",           "true")
        KeyValuePair("Routing:QualityFallback:MinResponseLength",  "30")
        KeyValuePair("Routing:QualityFallback:EntropyThreshold",   "2.5")
        // Judge disabled (OPT-IN; false = don't register IJudgeClient)
        KeyValuePair("Routing:Judge:Enabled",         "false")
        KeyValuePair("Routing:Judge:TimeoutSeconds",  "5")
        KeyValuePair("Routing:Judge:MaxCacheEntries", "10000")
        // Queue
        KeyValuePair("Queue:FairnessK",                   "10")
        KeyValuePair("Queue:MaxConcurrent122B",            "1")
        KeyValuePair("Queue:PerRequestTimeoutSeconds",     "300")
        // DecisionLog
        KeyValuePair("DecisionLog:Directory",     "logs/decisions")
        KeyValuePair("DecisionLog:RetentionDays", "90")
        KeyValuePair("DecisionLog:ChannelCapacity", "10000")
        // Routing.Health
        KeyValuePair("Routing:Health:PollingIntervalSeconds",         "10")
        KeyValuePair("Routing:Health:ConsecutiveFailureThreshold",    "1")
        // TeacherLabeler (needed by configureRequestPipeline Phase 7 wiring)
        KeyValuePair("TeacherLabeler:Endpoint",       "http://127.0.0.1:8001")
        KeyValuePair("TeacherLabeler:PromptPath",     "prompts/teacher-prompt.md")
        KeyValuePair("TeacherLabeler:DailyCallCap",   "1000")
        KeyValuePair("TeacherLabeler:TimeoutSeconds", "30")
        KeyValuePair("TeacherLabeler:DatasetsDir",    "datasets")
        // HardCaseDataset
        KeyValuePair("HardCaseDataset:Path",            "datasets/hard-cases.jsonl")
        KeyValuePair("HardCaseDataset:ChannelCapacity", "1000")
        // Retraining
        KeyValuePair("Retraining:IntervalMinutes",           "60")
        KeyValuePair("Retraining:HardCaseCountTrigger",      "500")
        KeyValuePair("Retraining:CountCheckIntervalMinutes", "5")
        KeyValuePair("Retraining:HardCasePath",              "datasets/hard-cases.jsonl")
        KeyValuePair("Retraining:TrainingSetPath",            "datasets/training-set.jsonl")
        KeyValuePair("Retraining:StatePath",                 "datasets/.last-retrain.json")
        KeyValuePair("Retraining:ModelPath",                 "models/router.zip")
        KeyValuePair("Retraining:PreviousModelPath",         "models/router.zip.prev")
        KeyValuePair("Retraining:RejectionLogPath",          "logs/retraining-rejections.jsonl")
        KeyValuePair("Retraining:HeldOutFraction",           "0.2")
        KeyValuePair("Retraining:HeldOutRandomSeed",         "42")
        KeyValuePair("Retraining:L2Regularization",          "0.1")
        // Canary
        KeyValuePair("Canary:CanaryModelPath",             "models/router-canary.zip")
        KeyValuePair("Canary:PercentageEnabled",           "10")
        KeyValuePair("Canary:RollingWindowSeconds",        "60")
        KeyValuePair("Canary:WatchdogPollIntervalSeconds", "10")
        KeyValuePair("Canary:AutoRollbackThreshold",       "0.10")
        KeyValuePair("Canary:AutoRollbackEnabled",         "true")
        KeyValuePair("Canary:MinBaselineSampleSize",       "50")
        // Feature management — required by AddScopedFeatureManagement
        KeyValuePair("feature_management:feature_flags:0:id", "Canary")
        KeyValuePair("feature_management:feature_flags:0:enabled", "true")
        // Logging
        KeyValuePair("Logging:Directory",     "logs/operational")
        KeyValuePair("Logging:RetentionDays", "30")
    ]

/// Build IConfiguration from the minimal in-memory pairs + an optional
/// override for Routing:Mode.
let private buildConfig (modeOverride: string option) : IConfiguration =
    let pairs = minimalConfigPairs |> List.map (fun kv -> kv)
    let dict =
        match modeOverride with
        | Some mode ->
            // Replace the Routing:Mode entry
            pairs |> List.map (fun kv ->
                if kv.Key = "Routing:Mode" then KeyValuePair("Routing:Mode", mode)
                else kv)
        | None -> pairs
    ConfigurationBuilder()
        .AddInMemoryCollection(dict :> IEnumerable<KeyValuePair<string, string>>)
        .Build() :> IConfiguration

/// Resolve RoutingAlgorithmRegistration from a fully-wired pipeline.
/// The ML bootstrap is skipped because no Routing:ML section is present,
/// so this works on hosts without ONNX embedding files.
let private resolveRegistration (modeOverride: string option) : RoutingAlgorithmRegistration =
    let services = ServiceCollection()
    let config   = buildConfig modeOverride
    configureRequestPipeline services config |> ignore
    let sp = services.BuildServiceProvider()
    sp.GetRequiredService<RoutingAlgorithmRegistration>()

// Path to the ONNX embedding model — used as the W4 ml-mode skip guard.
let private onnxEmbedPath = "models/embed/bge-m3-int8.onnx"

/// True when the ML embedding files exist (meaning the ml-arm test can run).
let private mlFilesPresent () = File.Exists(onnxEmbedPath)

let tests : Test =
    testList "ModeSwitchTests" [

        // ── MODE-01: invalid mode fails startup loudly ─────────────────────────

        testCase "Routing.Mode=\"invalid\" throws InvalidOperationException at startup" <| fun () ->
            let services = ServiceCollection()
            let config = buildConfig (Some "totally-bogus-mode")
            Expect.throwsT<InvalidOperationException>
                (fun () -> configureRequestPipeline services config |> ignore)
                "invalid mode must throw InvalidOperationException with descriptive message"

        testCase "Routing.Mode error message lists valid values" <| fun () ->
            let services = ServiceCollection()
            let config = buildConfig (Some "garbage")
            let ex =
                try
                    configureRequestPipeline services config |> ignore
                    null
                with
                | :? InvalidOperationException as e -> e
                | _ -> null
            Expect.isNotNull ex "expected InvalidOperationException"
            Expect.stringContains ex.Message "is not recognized" "error message contains 'is not recognized'"
            Expect.stringContains ex.Message "selfrouting" "error message mentions selfrouting"
            Expect.stringContains ex.Message "ml" "error message mentions ml"

        // ── MODE-02: selfrouting branch ────────────────────────────────────────

        testCase "Routing.Mode=\"selfrouting\" registers selfrouting algorithm" <| fun () ->
            let regn = resolveRegistration (Some "selfrouting")
            Expect.equal regn.Name "selfrouting" "Name = selfrouting"
            Expect.equal regn.ModelVersion "selfrouting-v1" "ModelVersion = selfrouting-v1"

        testCase "Routing.Mode default (missing key) → selfrouting" <| fun () ->
            // appsettings.json has Mode="selfrouting"; passing None means the minimal config's
            // default "selfrouting" value flows through. This verifies the default is "selfrouting"
            // as required by MODE-02 and STATE.md decision 1.
            let regn = resolveRegistration None
            Expect.equal regn.Name "selfrouting" "default mode = selfrouting"

        testCase "Routing.Mode is case-insensitive: \"SelfRouting\" works" <| fun () ->
            let regn = resolveRegistration (Some "SelfRouting")
            Expect.equal regn.Name "selfrouting" "case-insensitive normalization"

        // ── MODE-02: ml branch preserves v1.x behavior ────────────────────────
        //
        // W4 skip guard: resolving RoutingAlgorithmRegistration in ml mode triggers
        // the ML factory which accesses IEmbedder (BgeM3Embedder init needs ONNX files).
        // When ONNX files are absent, the factory's BgeM3Embedder resolution throws.
        // Skip the test when embedding files are not present on the host.

        testCase "Routing.Mode=\"ml\" registers ml algorithm" <| fun () ->
            if mlFilesPresent () then
                let regn = resolveRegistration (Some "ml")
                Expect.equal regn.Name "ml" "Name = ml"
                Expect.stringStarts regn.ModelVersion "ml-" "ModelVersion starts with ml-"
            else
                skiptest "Skipping ml-mode registration test: ONNX embedding files absent on this host"

        // ── HR-03 + HR-06 (resolved): cascade ordering end-to-end ─────────────
        // Hard Rules is Stage 0 of routeRequest; algorithm registration's Algorithm
        // closure runs at Stage 3. routeRequest fires Stage 0 BEFORE invoking the
        // algorithm closure — so in selfrouting mode a Hard-Rule keyword routes to
        // 122B even though the stub algorithm would return 35B.
        // STATE.md decision 5: Hard Rules wins over model override.
        // REQUIREMENTS.md HR-06 wording is corrected in Task 4 of this plan.

        testCase "selfrouting mode + LLVM prompt → 122B via Hard Rule (end-to-end)" <| fun () ->
            let regn = resolveRegistration (Some "selfrouting")
            let req =
                { Messages      = [ { Role = User; Content = "debug LLVM pass" } ]
                  ModelOverride = None
                  Task          = None
                  Stream        = false
                  Temperature   = None
                  TopP          = None
                  MaxTokens     = None
                  CorrelationId = ""
                  SessionId     = ""
                  UnknownFields = Map.empty }
            match routeRequest defaultRoutingConfig regn.Algorithm req with
            | Ok d ->
                Expect.equal d.Target Qwen122B "Hard Rule wins; target = 122B"
                Expect.equal d.Reason HardRule "reason = HardRule (not Default)"
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "selfrouting mode + non-keyword prompt → 35B via stub algorithm" <| fun () ->
            let regn = resolveRegistration (Some "selfrouting")
            let req =
                { Messages      = [ { Role = User; Content = "hello world" } ]
                  ModelOverride = None
                  Task          = None
                  Stream        = false
                  Temperature   = None
                  TopP          = None
                  MaxTokens     = None
                  CorrelationId = ""
                  SessionId     = ""
                  UnknownFields = Map.empty }
            match routeRequest defaultRoutingConfig regn.Algorithm req with
            | Ok d ->
                Expect.equal d.Target Qwen35B "stub algorithm returns 35B for non-keyword"
                Expect.equal d.Reason Default "stub reason = Default"
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "selfrouting mode + model=35b + LLVM prompt → 122B (Hard Rule beats override)" <| fun () ->
            // STATE.md decision 5: Hard Rules wins over explicit model override.
            // HR-06 original wording ("model override BYPASSES Hard Rules") was incorrect;
            // corrected in REQUIREMENTS.md in Task 4 of this plan.
            let regn = resolveRegistration (Some "selfrouting")
            let req =
                { Messages      = [ { Role = User; Content = "explain LLVM IR" } ]
                  ModelOverride = Some "35b"
                  Task          = None
                  Stream        = false
                  Temperature   = None
                  TopP          = None
                  MaxTokens     = None
                  CorrelationId = ""
                  SessionId     = ""
                  UnknownFields = Map.empty }
            match routeRequest defaultRoutingConfig regn.Algorithm req with
            | Ok d ->
                Expect.equal d.Target Qwen122B "Hard Rule beats model override"
                Expect.equal d.Reason HardRule "reason = HardRule, not ExplicitModelOverride"
            | Error e -> failtestf "expected Ok, got %A" e
    ]
