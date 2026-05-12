module SmartRouter.Tests.SessionKeyCascadeTests

// Integration tests for Phase 22 three-tier session cascade (TIER-01..05 + OBS-01).
//
// Strategy: TC-1..TC-4 + TC-6 are pure-function tests against the module-level
// helper `SmartRouter.Cli.Endpoints.ChatCompletions.resolveSessionCascade` and
// direct instantiation of `SmartRouter.Cli.Adapters.SessionCascadeStats`. TC-5
// (sticky escalation through Tier 2 key) reuses the StickyEscalationTests
// buildProvider pattern (configureRequestPipeline DI graph + routeRequest call).
//
// Wrapped in testSequenced (PITFALL-27): TC-5 builds a real DI graph that
// registers BackgroundServices (SessionStore eviction loop + DecisionLogWriter);
// serial execution prevents interleaved console/temp-file state. TC-1..TC-4,
// TC-6 are pure and would not require testSequenced individually, but they
// share the module's testList — wrap the whole list.

open System.Collections.Generic
open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open SmartRouter.Core.Domain
open SmartRouter.Core.Routing
open SmartRouter.Cli.Adapters.SessionStore
open SmartRouter.Cli.Adapters.RoutingAlgorithm
open SmartRouter.Cli.Adapters.SessionCascadeStats

// ── Test helpers ──────────────────────────────────────────────────────────

/// Build a RouterRequest with arbitrary fields; defaults match the v2.0 stateless
/// shape. Caller specifies sessionId (Tier 1) and Messages (for Tier 2/3 input).
let private mkReq (sessionId: string) (messages: Message list) : RouterRequest =
    { Messages       = messages
      ModelOverride  = None
      Task           = None
      Stream         = false
      Temperature    = None
      TopP           = None
      MaxTokens      = None
      CorrelationId  = ""
      SessionId      = sessionId
      UnknownFields  = Map.empty }

/// Realistic Hermes emission line (per run_agent.py:5764-5766 + Plan 21-01 fixtures).
let private hermesSystemContent (sid: string) =
    sprintf "Conversation started: 2026-05-12T15:30:00+00:00\nSession ID: %s\nModel: qwen-35b" sid

// ── DI fixture for TC-5 (reused from StickyEscalationTests buildProvider) ───
//
// Identical to StickyEscalationTests.fs minimalConfigPairs + buildProvider.
// Build a minimal IConfiguration that includes Routing.Session.* + Routing.SelfRouter.*
// but OMITS Routing:ML so configureRequestPipeline skips ensureEmbeddingFilesPresent.

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

let private buildConfig () : IConfiguration =
    ConfigurationBuilder()
        .AddInMemoryCollection(minimalConfigPairs :> IEnumerable<KeyValuePair<string, string>>)
        .Build() :> IConfiguration

let private buildProvider () : System.IDisposable * ISessionStore * RoutingAlgorithmRegistration * RoutingConfig =
    let services = ServiceCollection()
    let config   = buildConfig ()
    SmartRouter.Cli.CompositionRoot.configureRequestPipeline services config |> ignore
    let sp = services.BuildServiceProvider()
    let store = sp.GetRequiredService<ISessionStore>()
    let regn  = sp.GetRequiredService<RoutingAlgorithmRegistration>()
    let cfg   = sp.GetRequiredService<RoutingConfig>()
    (sp :> System.IDisposable), store, regn, cfg

// ── Tests ─────────────────────────────────────────────────────────────────

let tests : Test =
    testSequenced <| testList "SessionKeyCascadeTests" [

        // TC-1 — TIER-01 + TIER-05a: header wins over sysprompt
        testCase "TC-1: header wins when both header and sysprompt are present" <| fun () ->
            let req = mkReq "explicit-header-id" [
                { Role = System; Content = hermesSystemContent "sysprompt-id-IGNORED" }
                { Role = User;   Content = "what is 2+2" }
            ]
            let resolvedId, source =
                SmartRouter.Cli.Endpoints.ChatCompletions.resolveSessionCascade req
            Expect.equal source "header"
                "TIER-01: explicit header wins over sysprompt parse"
            Expect.equal resolvedId "explicit-header-id"
                "header value is used as session key"

        // TC-2 — TIER-01 + TIER-02 + TIER-05b: sysprompt wins when header empty
        testCase "TC-2: sysprompt parse wins when header is empty and Session ID line present" <| fun () ->
            let req = mkReq "" [
                { Role = System; Content = hermesSystemContent "20260512T1530_abc" }
                { Role = User;   Content = "trivial prompt" }
            ]
            let resolvedId, source =
                SmartRouter.Cli.Endpoints.ChatCompletions.resolveSessionCascade req
            Expect.equal source "sysprompt"
                "TIER-02: empty header + Session ID line -> Tier 2 wins"
            Expect.equal resolvedId "20260512T1530_abc"
                "captured session id is the resolved key"

        // TC-3 — TIER-01 + TIER-05c: content fingerprint when neither header nor sysprompt
        testCase "TC-3: content fingerprint wins when both header and sysprompt are absent" <| fun () ->
            let req = mkReq "" [
                { Role = System; Content = "You are a helpful assistant.\nNo Session ID line." }
                { Role = User;   Content = "what is 2+2" }
            ]
            let resolvedId, source =
                SmartRouter.Cli.Endpoints.ChatCompletions.resolveSessionCascade req
            Expect.equal source "content"
                "TIER-03: empty header + no Session ID line -> Tier 3 wins"
            Expect.equal resolvedId.Length 16
                "content fingerprint output is exactly 16 chars"
            Expect.isTrue (resolvedId |> Seq.forall (fun c -> System.Char.IsDigit c || (c >= 'a' && c <= 'f')))
                "content fingerprint output is lowercase hex"

        // TC-4 — TIER-05e: determinism — same RouterRequest twice -> identical result
        testCase "TC-4: deterministic — same RouterRequest produces identical cascade result" <| fun () ->
            let req = mkReq "" [
                { Role = System; Content = "deterministic system" }
                { Role = User;   Content = "deterministic user query" }
            ]
            let id1, src1 = SmartRouter.Cli.Endpoints.ChatCompletions.resolveSessionCascade req
            let id2, src2 = SmartRouter.Cli.Endpoints.ChatCompletions.resolveSessionCascade req
            Expect.equal id1 id2 "Tier 3 is deterministic"
            Expect.equal src1 src2 "extraction source is deterministic"
            Expect.equal src1 "content" "no header / no sysprompt -> content tier"

        // TC-5 — TIER-05d: sticky escalation continues working through Tier 2 keys.
        // Strategy: simulate Tier 2 resolution by using a session key that would
        // come from a sysprompt-derived value. Stage A: write directly to the
        // store (emulating Point B write after a 122B routing decision).
        // Stage B: route a trivial request with the same Tier 2-derived key ->
        // expect StickyEscalation reason.
        testCase "TC-5: sticky_to_122b reason fires for Tier 2-derived session key" <| fun () ->
            let disposable, store, regn, cfg = buildProvider()
            try
                // Stage A: emulate Point B write that would happen after a 122B routing
                // decision (e.g., from a Hard Rule trigger). Direct store write is the
                // minimal simulation — exercising the full handler chain requires Kestrel.
                let sid = "20260512T1530_abc"
                store.Update(sid, Qwen122B)

                // Stage B: a trivial follow-up request with the same Tier 2 key.
                let req = mkReq sid [
                    { Role = User; Content = "what was the last thing you said" }
                ]
                let result = routeRequest cfg regn.Algorithm req
                match result with
                | Ok decision ->
                    Expect.equal decision.Target Qwen122B
                        "TIER-05d: sticky lookup escalates Tier 2-keyed request to 122B"
                    Expect.equal decision.Reason StickyEscalation
                        "TIER-05d: routing_reason is StickyEscalation (sticky_to_122b)"
                | Error e ->
                    failtestf "TC-5 routing failed: %A" e
            finally
                disposable.Dispose()

        // TC-6 — OBS-01: SessionCascadeStats Interlocked counter increments correctly.
        // Instantiate SessionCascadeStats directly (it is a public class); call
        // RecordHeader 1x, RecordSysprompt 2x, RecordContent 0x; assert GetStats returns
        // struct (1L, 2L, 0L). This verifies the Interlocked.Increment + Volatile.Read
        // pattern from Plan 22-01.
        testCase "TC-6: SessionCascadeStats counters increment correctly" <| fun () ->
            let stats = SessionCascadeStats() :> ISessionCascadeStats
            stats.RecordHeader()
            stats.RecordSysprompt()
            stats.RecordSysprompt()
            let struct (h, s, c) = stats.GetStats()
            Expect.equal h 1L "RecordHeader called 1x -> headerCount = 1"
            Expect.equal s 2L "RecordSysprompt called 2x -> syspromptCount = 2"
            Expect.equal c 0L "RecordContent called 0x -> contentCount = 0"
    ]
