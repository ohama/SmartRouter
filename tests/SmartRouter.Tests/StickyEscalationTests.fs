module SmartRouter.Tests.StickyEscalationTests

// Integration tests for Phase 18 sticky escalation (SES-01..09 goal-backward verification).
//
// Strategy: build a minimal in-memory IConfiguration that includes Routing.Session.*
// but OMITS Routing:ML so configureRequestPipeline skips ensureEmbeddingFilesPresent
// (mirrors Phase 17 ModeSwitchTests fixture). Resolve RoutingConfig +
// RoutingAlgorithmRegistration + ISessionStore from the built DI container and exercise
// the sticky behavior via routeRequest directly.
//
// Wrapped with testSequenced per PITFALL-27 — boots a CompositionRoot DI graph which
// registers BackgroundServices (SessionStore eviction loop + DecisionLogWriter).

open System.Collections.Generic
open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open SmartRouter.Core.Domain
open SmartRouter.Core.Routing
open SmartRouter.Cli.Adapters.SessionStore
open SmartRouter.Cli.Adapters.RoutingAlgorithm

// ── Minimal config (Session-aware; no ML section) ────────────────────────────
//
// Identical to ModeSwitchTests.minimalConfigPairs except we add the Phase 18
// Session keys. Routing:ML is intentionally absent so the ML bootstrap is skipped.

let private minimalConfigPairs : KeyValuePair<string, string> list =
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
        KeyValuePair("Routing:Judge:Enabled",         "false")
        KeyValuePair("Routing:Judge:TimeoutSeconds",  "5")
        KeyValuePair("Routing:Judge:MaxCacheEntries", "10000")
        // Phase 18 — Session keys
        KeyValuePair("Routing:Session:TtlMinutes",  "30")
        KeyValuePair("Routing:Session:MaxEntries",  "10000")
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

let private buildConfig () : IConfiguration =
    ConfigurationBuilder()
        .AddInMemoryCollection(minimalConfigPairs :> IEnumerable<KeyValuePair<string, string>>)
        .Build() :> IConfiguration

/// Build DI provider wired via configureRequestPipeline (selfrouting; no ML).
let private buildProvider () : System.IDisposable * ISessionStore * RoutingAlgorithmRegistration * RoutingConfig =
    let services = ServiceCollection()
    let config   = buildConfig ()
    SmartRouter.Cli.CompositionRoot.configureRequestPipeline services config |> ignore
    let sp = services.BuildServiceProvider()
    let store = sp.GetRequiredService<ISessionStore>()
    let regn  = sp.GetRequiredService<RoutingAlgorithmRegistration>()
    let cfg   = sp.GetRequiredService<RoutingConfig>()
    (sp :> System.IDisposable), store, regn, cfg

/// Helper: minimal RouterRequest builder.
let private mkReq (sessionId: string) (content: string) : RouterRequest =
    { Messages      = [ { Role = User; Content = content } ]
      ModelOverride = None
      Task          = None
      Stream        = false
      Temperature   = None
      TopP          = None
      MaxTokens     = None
      CorrelationId = "test-cid"
      SessionId     = sessionId
      UnknownFields = Map.empty }

let tests : Test =
    testSequenced <| testList "StickyEscalationTests" [

        // ── SC-1: first request 122B → second request sticky ──────────────────

        testCase "first request routes 122B + Point B write → second request stickies to 122B" <| fun () ->
            let sp, store, regn, cfg = buildProvider ()
            use _ = sp

            // Request 1: Hard Rule keyword → 122B.
            let req1 = mkReq "sess-a" "diagnose LLVM segfault"
            let decision1 =
                match routeRequest cfg regn.Algorithm req1 with
                | Ok d -> d
                | Error e -> failtestf "routeRequest req1 failed: %A" e
            Expect.equal decision1.Target Qwen122B "req1 → 122B (Hard Rule)"
            Expect.equal decision1.Reason HardRule "req1 reason = HardRule"

            // Simulate Point B (ChatCompletions writes after finalDecision resolves).
            store.Update("sess-a", decision1.Target)

            // Request 2: trivial prompt, same session → sticky.
            let req2 = mkReq "sess-a" "what is 2+2"
            let decision2 =
                match routeRequest cfg regn.Algorithm req2 with
                | Ok d -> d
                | Error e -> failtestf "routeRequest req2 failed: %A" e
            Expect.equal decision2.Target Qwen122B "req2 → 122B (sticky escalation)"
            Expect.equal decision2.Reason StickyEscalation "req2 reason = StickyEscalation"

        // ── SC-2: stateless no-header preserves v1.x behavior ────────────────

        testCase "empty SessionId requests share no sticky bucket" <| fun () ->
            let sp, store, regn, cfg = buildProvider ()
            use _ = sp

            let req1 = mkReq "" "diagnose LLVM segfault"
            let d1 =
                match routeRequest cfg regn.Algorithm req1 with
                | Ok d -> d
                | Error e -> failtestf "routeRequest req1 failed: %A" e
            Expect.equal d1.Target Qwen122B "Hard Rule still fires"
            store.Update("", d1.Target)   // no-op per Pitfall 7

            let req2 = mkReq "" "what is 2+2"
            let d2 =
                match routeRequest cfg regn.Algorithm req2 with
                | Ok d -> d
                | Error e -> failtestf "routeRequest req2 failed: %A" e
            Expect.equal d2.Target Qwen35B "stateless second request → 35B"
            Expect.equal d2.Reason Default "reason = Default (no sticky)"

        // ── SC-4: quality-fallback writes session (SES-07 verification) ───────
        // Simulate Point B write with finalDecision.Target=Qwen122B (representing
        // a 35B→122B quality-fallback escalation in ChatCompletions.handler).
        // Subsequent request with same SessionId routes to 122B sticky.

        testCase "Point B write of Qwen122B (simulating quality fallback) makes next request sticky" <| fun () ->
            let sp, store, regn, cfg = buildProvider ()
            use _ = sp

            // req1 would route to 35B initially (trivial prompt, no hard rule).
            let req1 = mkReq "sess-qf" "hello world"
            let d1 =
                match routeRequest cfg regn.Algorithm req1 with
                | Ok d -> d
                | Error e -> failtestf "routeRequest req1 failed: %A" e
            Expect.equal d1.Target Qwen35B "req1 initial routing = 35B"

            // Quality fallback escalated finalDecision to 122B.
            // ChatCompletions Point B writes Qwen122B even though initial was 35B.
            store.Update("sess-qf", Qwen122B)

            // Next request should sticky to 122B.
            let req2 = mkReq "sess-qf" "and now another question"
            let d2 =
                match routeRequest cfg regn.Algorithm req2 with
                | Ok d -> d
                | Error e -> failtestf "routeRequest req2 failed: %A" e
            Expect.equal d2.Target Qwen122B "req2 stickies to 122B after quality-fallback Point B"
            Expect.equal d2.Reason StickyEscalation "reason = StickyEscalation"

        // ── Hard Rules beats sticky-to-35B (cascade ordering sanity) ─────────
        // Stage 0 (Hard Rules) must win even when session LastModel=Qwen35B.

        testCase "Hard Rules still fires when session is sticky-to-35B" <| fun () ->
            let sp, store, regn, cfg = buildProvider ()
            use _ = sp

            store.Update("sess-35", Qwen35B)
            let req = mkReq "sess-35" "fix LLVM compiler bug"
            let d =
                match routeRequest cfg regn.Algorithm req with
                | Ok d -> d
                | Error e -> failtestf "routeRequest failed: %A" e
            Expect.equal d.Target Qwen122B "Hard Rule (Stage 0) wins over sticky-to-35B (Stage 3)"
            Expect.equal d.Reason HardRule "reason = HardRule (not Default)"

        // ── Sticky persists across multiple non-keyword requests ──────────────

        testCase "session stays sticky to 122B across multiple trivial requests" <| fun () ->
            let sp, store, regn, cfg = buildProvider ()
            use _ = sp

            store.Update("sess-persist", Qwen122B)
            for i in 1..3 do
                let req = mkReq "sess-persist" (sprintf "question %d" i)
                match routeRequest cfg regn.Algorithm req with
                | Ok d ->
                    Expect.equal d.Target Qwen122B (sprintf "iter %d: target 122B" i)
                    Expect.equal d.Reason StickyEscalation (sprintf "iter %d: reason sticky" i)
                | Error e -> failtestf "iter %d routeRequest failed: %A" i e
    ]
