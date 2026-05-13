module SmartRouter.Tests.NamedHttpClientBaseAddressTests

// Regression tests for issue #14 — `services.AddHttpClient(name, fun c -> ...)`
// 2-arg form silently fails F# lambda → Action<HttpClient> conversion, leaving
// BaseAddress null. The .ConfigureHttpClient(...) chain form is the only reliable
// shape in F#. See documentation/howto/wire-fsharp-namedhttpclient-with-configurehttpclient.md
// and CompositionRoot.fs line 214 ("FORBIDDEN") comment.
//
// Strategy: build a real DI provider via configureRequestPipeline (or configureWithoutMl),
// resolve IHttpClientFactory, create each named client by name, assert BaseAddress matches
// the configured endpoint. Sentinel: BaseAddress = null means the 2-arg form silently
// dropped the lambda.

open System
open System.Collections.Generic
open System.Net.Http
open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection

// Same minimal config shape as SessionKeyCascadeTests / StickyEscalationTests, with
// judge enabled so the judge HttpClient is also registered (default is disabled).
let private configPairs (judgeEnabled: bool) : KeyValuePair<string, string> list =
    [   KeyValuePair("Upstreams:Model35B",  "http://127.0.0.1:8000")
        KeyValuePair("Upstreams:Model122B", "http://127.0.0.1:8001")
        KeyValuePair("Routing:Mode",        "selfrouting")
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
        KeyValuePair("Routing:Judge:Enabled",         (if judgeEnabled then "true" else "false"))
        KeyValuePair("Routing:Judge:TimeoutSeconds",  "5")
        KeyValuePair("Routing:Judge:MaxCacheEntries", "10000")
        KeyValuePair("Routing:Session:TtlMinutes",  "30")
        KeyValuePair("Routing:Session:MaxEntries",  "10000")
        KeyValuePair("Routing:SelfRouter:Endpoint",        "")
        KeyValuePair("Routing:SelfRouter:PromptPath",      "prompts/self-router-prompt.md")
        KeyValuePair("Routing:SelfRouter:TimeoutSeconds",  "5")
        KeyValuePair("Routing:SelfRouter:MaxCacheEntries", "10000")
        KeyValuePair("Queue:FairnessK",                   "10")
        KeyValuePair("Queue:MaxConcurrent122B",            "1")
        KeyValuePair("Queue:PerRequestTimeoutSeconds",     "300")
        KeyValuePair("DecisionLog:Directory",     "logs/decisions")
        KeyValuePair("DecisionLog:RetentionDays", "90")
        KeyValuePair("DecisionLog:ChannelCapacity", "10000")
        KeyValuePair("Routing:Health:PollingIntervalSeconds",         "10")
        KeyValuePair("Routing:Health:ConsecutiveFailureThreshold",    "1")
        KeyValuePair("TeacherLabeler:Endpoint",       "http://127.0.0.1:8001")
        KeyValuePair("TeacherLabeler:PromptPath",     "prompts/teacher-prompt.md")
        KeyValuePair("TeacherLabeler:DailyCallCap",   "1000")
        KeyValuePair("TeacherLabeler:TimeoutSeconds", "30")
        KeyValuePair("TeacherLabeler:DatasetsDir",    "datasets")
        KeyValuePair("HardCaseDataset:Path",            "datasets/hard-cases.jsonl")
        KeyValuePair("HardCaseDataset:ChannelCapacity", "1000")
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
        KeyValuePair("Canary:CanaryModelPath",             "models/router-canary.zip")
        KeyValuePair("Canary:PercentageEnabled",           "10")
        KeyValuePair("Canary:RollingWindowSeconds",        "60")
        KeyValuePair("Canary:WatchdogPollIntervalSeconds", "10")
        KeyValuePair("Canary:AutoRollbackThreshold",       "0.10")
        KeyValuePair("Canary:AutoRollbackEnabled",         "true")
        KeyValuePair("Canary:MinBaselineSampleSize",       "50")
        KeyValuePair("feature_management:feature_flags:0:id", "Canary")
        KeyValuePair("feature_management:feature_flags:0:enabled", "true")
        KeyValuePair("Logging:Directory",     "logs/operational")
        KeyValuePair("Logging:RetentionDays", "30")
    ]

let private buildConfig (judgeEnabled: bool) : IConfiguration =
    ConfigurationBuilder()
        .AddInMemoryCollection(configPairs judgeEnabled :> IEnumerable<KeyValuePair<string, string>>)
        .Build() :> IConfiguration

let private buildMainProvider () : ServiceProvider =
    let services = ServiceCollection()
    SmartRouter.Cli.CompositionRoot.configureRequestPipeline services (buildConfig true) |> ignore
    services.BuildServiceProvider()

let private buildWithoutMlProvider () : ServiceProvider =
    let services = ServiceCollection()
    SmartRouter.Cli.CompositionRoot.configureWithoutMl services (buildConfig false) |> ignore
    services.BuildServiceProvider()

let private assertBaseAddress (sp: ServiceProvider) (clientName: string) (expected: string) =
    let factory = sp.GetRequiredService<IHttpClientFactory>()
    use client  = factory.CreateClient(clientName)
    Expect.isNotNull (box client.BaseAddress)
        (sprintf "BaseAddress for HttpClient '%s' must NOT be null — 2-arg AddHttpClient(name, fun c -> ...) silently fails in F#; use .ConfigureHttpClient chain form (#14)" clientName)
    Expect.equal (string client.BaseAddress) expected
        (sprintf "BaseAddress for HttpClient '%s' did not match configured endpoint" clientName)

let tests : Test =
    testSequenced <| testList "NamedHttpClientBaseAddressTests" [

        // Canonical good cases (already on chain form before #14) — these would regress
        // if anyone ever copy-pasted the 2-arg form back in.
        testCase "TC-1: upstream35b BaseAddress matches Upstreams.Model35B (chain form, canonical)" <| fun () ->
            use sp = buildMainProvider ()
            assertBaseAddress sp "upstream35b" "http://127.0.0.1:8000/"

        testCase "TC-2: upstream122b BaseAddress matches Upstreams.Model122B (chain form, canonical)" <| fun () ->
            use sp = buildMainProvider ()
            assertBaseAddress sp "upstream122b" "http://127.0.0.1:8001/"

        testCase "TC-3: upstream35b-stream BaseAddress matches Upstreams.Model35B (chain form, canonical)" <| fun () ->
            use sp = buildMainProvider ()
            assertBaseAddress sp "upstream35b-stream" "http://127.0.0.1:8000/"

        testCase "TC-4: upstream122b-stream BaseAddress matches Upstreams.Model122B (chain form, canonical)" <| fun () ->
            use sp = buildMainProvider ()
            assertBaseAddress sp "upstream122b-stream" "http://127.0.0.1:8001/"

        // Issue #14 regression cases — these were ALL using the forbidden 2-arg form
        // (selfrouter line 485, judge line 763, teacher lines 829 + 1278). The reporter
        // hit selfrouter; investigation found three more identical bugs.
        testCase "TC-5: #14 selfrouter BaseAddress matches Upstreams.Model35B (empty config → fallback)" <| fun () ->
            use sp = buildMainProvider ()
            assertBaseAddress sp "selfrouter" "http://127.0.0.1:8000/"

        testCase "TC-6: #14 judge BaseAddress matches Upstreams.Model122B (empty config → fallback)" <| fun () ->
            use sp = buildMainProvider ()
            assertBaseAddress sp "judge" "http://127.0.0.1:8001/"

        testCase "TC-7: #14 teacher BaseAddress matches TeacherLabeler:Endpoint (configureRequestPipeline branch)" <| fun () ->
            use sp = buildMainProvider ()
            assertBaseAddress sp "teacher" "http://127.0.0.1:8001/"

        // The configureWithoutMl branch has its own teacher registration (line 1278) that
        // had the same bug. Operators hit this path via the `--retrain` CLI flag.
        testCase "TC-8: #14 teacher BaseAddress matches TeacherLabeler:Endpoint (configureWithoutMl --retrain branch)" <| fun () ->
            use sp = buildWithoutMlProvider ()
            assertBaseAddress sp "teacher" "http://127.0.0.1:8001/"
    ]
