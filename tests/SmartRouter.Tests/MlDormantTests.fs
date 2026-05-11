module SmartRouter.Tests.MlDormantTests

// Phase 19 (19-03): ML dormant integration test (SR-09 / SC-5).
//
// Tests that Routing.Mode="ml" DI registration still boots cleanly after the
// Phase 19 SelfRoute DU case was added to Domain.fs.
//
// Strategy: resolve RoutingAlgorithmRegistration from a fully-wired DI container
// with Routing.Mode="ml" and assert:
//   - regn.Name = "ml"
//   - regn.ModelVersion starts with "ml-"
//
// This catches any DU match exhaustiveness failures or registration-order drift.
//
// W4 skip guard (mirrors ModeSwitchTests.fs lines 212-218): the ML factory needs
// IEmbedder (BgeM3Embedder) which loads ONNX files. When the embedding ONNX file
// is absent on the host (e.g., CI without ML model files), the test is SKIPPED
// (not FAILED). Both outcomes satisfy SC-5.
//
// No testSequenced wrapper — resolveRegistration builds its own ServiceCollection
// per test with no shared mutable state; no Console.SetOut or temp-file writes.

open System
open System.Collections.Generic
open System.IO
open Expecto
open Microsoft.Extensions.Configuration
open SmartRouter.Cli.Adapters.RoutingAlgorithm
open SmartRouter.Cli.CompositionRoot
open Microsoft.Extensions.DependencyInjection

// ── W4 skip guard (same probe as ModeSwitchTests.fs line 155-158) ─────────────

/// Path to the ONNX embedding model — mirrors ModeSwitchTests.fs onnxEmbedPath.
let private onnxEmbedPath = "models/embed/bge-m3-int8.onnx"

/// True when the ML embedding files exist (means the ml-arm DI can complete).
let private mlFilesPresent () : bool = File.Exists(onnxEmbedPath)

// ── Minimal config for ml-mode DI bootstrap ───────────────────────────────────
//
// Identical to ModeSwitchTests.minimalConfigPairs except Routing:Mode is overridden
// to "ml" and Routing:ML section is added (required by ensureEmbeddingFilesPresent).
// When the ONNX files are absent the W4 skip guard prevents test execution entirely.

let private mlConfigPairs : KeyValuePair<string, string> list =
    [   KeyValuePair("Upstreams:Model35B",  "http://127.0.0.1:8000")
        KeyValuePair("Upstreams:Model122B", "http://127.0.0.1:8001")
        KeyValuePair("Routing:Mode",        "ml")
        KeyValuePair("Routing:TimeoutSeconds", "300")
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
        KeyValuePair("Routing:ModelAliases:35b",       "Qwen35B")
        KeyValuePair("Routing:ModelAliases:qwen35b",   "Qwen35B")
        KeyValuePair("Routing:ModelAliases:qwen-35b",  "Qwen35B")
        KeyValuePair("Routing:ModelAliases:122b",      "Qwen122B")
        KeyValuePair("Routing:ModelAliases:qwen122b",  "Qwen122B")
        KeyValuePair("Routing:ModelAliases:qwen-122b", "Qwen122B")
        KeyValuePair("Routing:QualityFallback:Enabled",           "true")
        KeyValuePair("Routing:QualityFallback:MinResponseLength",  "30")
        KeyValuePair("Routing:QualityFallback:EntropyThreshold",   "2.5")
        KeyValuePair("Routing:Judge:Enabled",         "false")
        KeyValuePair("Routing:Judge:TimeoutSeconds",  "5")
        KeyValuePair("Routing:Judge:MaxCacheEntries", "10000")
        // ML section — required by ensureEmbeddingFilesPresent / ML factory bootstrap
        KeyValuePair("Routing:ML:ModelPath",      "models/router.zip")
        KeyValuePair("Routing:ML:EmbedModelPath", "models/embed/bge-m3-int8.onnx")
        KeyValuePair("Routing:ML:Threshold",      "0.5")
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
        // TeacherLabeler
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
        // Feature management
        KeyValuePair("feature_management:feature_flags:0:id", "Canary")
        KeyValuePair("feature_management:feature_flags:0:enabled", "true")
        // Logging
        KeyValuePair("Logging:Directory",     "logs/operational")
        KeyValuePair("Logging:RetentionDays", "30")
    ]

let private resolveRegistration () : RoutingAlgorithmRegistration =
    let config =
        ConfigurationBuilder()
            .AddInMemoryCollection(mlConfigPairs :> IEnumerable<KeyValuePair<string, string>>)
            .Build() :> IConfiguration
    let services = ServiceCollection()
    configureRequestPipeline services config |> ignore
    let sp = services.BuildServiceProvider()
    sp.GetRequiredService<RoutingAlgorithmRegistration>()

let mlDormantTests : Test =
    testList "ML Dormant — Routing.Mode=ml continues to wire across v2.x" [

        testCase "Routing.Mode=\"ml\" + SelfRoute DU case exists → ML path still boots cleanly" <| fun () ->
            if mlFilesPresent () then
                let regn = resolveRegistration ()
                Expect.equal regn.Name "ml" "RoutingAlgorithmRegistration.Name = ml"
                Expect.stringStarts regn.ModelVersion "ml-" "ModelVersion starts with ml-"
            else
                skiptest "Skipping ML dormant test: ONNX embedding files absent on this host"
    ]
