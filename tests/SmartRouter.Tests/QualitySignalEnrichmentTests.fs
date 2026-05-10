module SmartRouter.Tests.QualitySignalEnrichmentTests

// Phase 15: unit + integration tests for the 5-dimension quality signal
// enrichment cascade (QSE-01..06). Unit tests call analyzeResponse /
// charEntropy / isBadResponse directly — no Kestrel required. One
// fake-Kestrel integration test proves the finish_reason path fires, records
// bad_reason in the trace log, and increments the stats counter.
//
// Test numbering:
//   QSE-01  finish_reason=length  →  FinishReasonMatch (stage 1)
//   QSE-02  case-insensitive keyword match (stage 4)
//   QSE-03  operator-added refusal pattern opt-in (stage 4 capability test)
//   QSE-04  Korean-aware effective length (stage 2)
//   QSE-05  Shannon entropy detection (stage 3)
//   QSE-06  Phase 14 backward-compat — isBadResponse wrapper

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

open SmartRouter.Core.Domain
open SmartRouter.Cli.Adapters.RoutingAlgorithm
open SmartRouter.Cli.Adapters.CanaryState
open SmartRouter.Cli.Adapters.TraceLogger
open SmartRouter.Cli.Adapters.QualityCheck
open SmartRouter.Cli.Adapters.QueueDispatcher

// ── Test helpers ────────────────────────────────────────────────────────────

/// Build QualityFallbackOptions with Phase 15 fields for unit tests.
let private mkOpts (badKeywords: string array) =
    { Enabled            = true
      MinResponseLength  = 30
      BadKeywords        = badKeywords
      BadFinishReasons   = [| "length"; "content_filter" |]
      EntropyThreshold   = 2.5 }

/// Wrap content in a minimal OpenAI-compatible non-streaming envelope.
let private envelope (content: string) (finishReason: string) =
    sprintf
        """{"choices":[{"finish_reason":"%s","message":{"role":"assistant","content":%s}}]}"""
        finishReason
        (JsonSerializer.Serialize(content))

// ── Integration test helpers ─────────────────────────────────────────────────
//
// Mirrors the pattern from QualityFallbackTests.fs exactly.

let private startFakeUpstream (respond: HttpContext -> Task<unit>) : int * IDisposable =
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
    builder.Logging.ClearProviders() |> ignore
    let app = builder.Build()
    app.Run(fun (ctx: HttpContext) -> respond ctx :> Task) |> ignore
    app.StartAsync().GetAwaiter().GetResult()
    let port =
        app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            .Addresses
        |> Seq.head
        |> fun a -> a.Split(':') |> Array.last |> int
    let disp =
        { new IDisposable with
            member _.Dispose() =
                try app.StopAsync().GetAwaiter().GetResult() with _ -> ()
                try (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() with _ -> () }
    port, disp

/// Starts the smart-router test instance with Phase 15 QualityFallback config.
/// Key difference from QualityFallbackTests.startTestRouter:
///   - BadFinishReasons set to ["length","content_filter"]
///   - EntropyThreshold set to 2.5
///   - IQualityCheckStats OVERRIDDEN to QueueDispatcher-backed real instance
///     (configureWithoutMl registers a NoOp; we override with last-reg-wins)
let private startTestRouter
    (model35bPort  : int)
    (model122bPort : int)
    (tempDir       : string)
    : HttpClient * IDisposable * string =

    let traceDir    = Path.Combine(tempDir, "logs", "trace")
    let decisionDir = Path.Combine(tempDir, "logs", "decisions")
    Directory.CreateDirectory(traceDir)    |> ignore
    Directory.CreateDirectory(decisionDir) |> ignore

    let testBuilder = WebApplication.CreateBuilder()
    testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore

    (testBuilder.Configuration :> IConfigurationBuilder)
        .AddInMemoryCollection([
            KeyValuePair("Upstreams:Model35B",  sprintf "http://127.0.0.1:%d" model35bPort)
            KeyValuePair("Upstreams:Model122B", sprintf "http://127.0.0.1:%d" model122bPort)
            KeyValuePair("Routing:TimeoutSeconds", "300")
            KeyValuePair("Routing:TaskTable:graph_indexing:Model",           "122b")
            KeyValuePair("Routing:TaskTable:graph_indexing:Priority",        "high")
            KeyValuePair("Routing:TaskTable:compiler_debug:Model",           "122b")
            KeyValuePair("Routing:TaskTable:compiler_debug:Priority",        "high")
            KeyValuePair("Routing:TaskTable:architecture_analysis:Model",    "122b")
            KeyValuePair("Routing:TaskTable:architecture_analysis:Priority", "high")
            KeyValuePair("Routing:TaskTable:dependency_analysis:Model",      "122b")
            KeyValuePair("Routing:TaskTable:dependency_analysis:Priority",   "low")
            KeyValuePair("Routing:TaskTable:reasoning:Model",                "122b")
            KeyValuePair("Routing:TaskTable:reasoning:Priority",             "low")
            KeyValuePair("Routing:TaskTable:retrieval:Model",                "35b")
            KeyValuePair("Routing:TaskTable:retrieval:Priority",             "low")
            KeyValuePair("Routing:TaskTable:summary:Model",                  "35b")
            KeyValuePair("Routing:TaskTable:summary:Priority",               "low")
            KeyValuePair("Routing:ModelAliases:35b",       "Qwen35B")
            KeyValuePair("Routing:ModelAliases:122b",      "Qwen122B")
            KeyValuePair("Routing:ModelAliases:qwen-35b",  "Qwen35B")
            KeyValuePair("Routing:ModelAliases:qwen35b",   "Qwen35B")
            KeyValuePair("Routing:ModelAliases:qwen-122b", "Qwen122B")
            KeyValuePair("Routing:ModelAliases:qwen122b",  "Qwen122B")
            // Phase 15 quality fallback — all 5 keys present
            KeyValuePair("Routing:QualityFallback:Enabled",           "true")
            KeyValuePair("Routing:QualityFallback:MinResponseLength", "30")
            KeyValuePair("Routing:QualityFallback:BadKeywords:0",     "TODO")
            KeyValuePair("Routing:QualityFallback:BadKeywords:1",     "I think")
            KeyValuePair("Routing:QualityFallback:BadFinishReasons:0","length")
            KeyValuePair("Routing:QualityFallback:BadFinishReasons:1","content_filter")
            KeyValuePair("Routing:QualityFallback:EntropyThreshold",  "2.5")
            KeyValuePair("Queue:FairnessK",                "10")
            KeyValuePair("Queue:MaxConcurrent122B",        "1")
            KeyValuePair("Queue:PerRequestTimeoutSeconds", "60")
            KeyValuePair("DecisionLog:Directory",       decisionDir)
            KeyValuePair("DecisionLog:ChannelCapacity", "1000")
            KeyValuePair("Routing:Health:PollingIntervalSeconds",      "60")
            KeyValuePair("Routing:Health:ConsecutiveFailureThreshold", "1")
            KeyValuePair("Trace:Enabled", "true")
        ])
    |> ignore

    SmartRouter.Cli.CompositionRoot.configureWithoutMl
        testBuilder.Services
        testBuilder.Configuration
    |> ignore

    // Stub RoutingAlgorithmRegistration — always routes to Qwen35B (no real ML model).
    let testStubAlgorithm : SmartRouter.Core.Domain.RoutingAlgorithm =
        fun _cfg _req ->
            { Target       = Qwen35B
              Priority     = Low
              Reason       = ML
              IsFallback   = false
              ModelVersion = "test-stub" }

    let testStubReg : RoutingAlgorithmRegistration =
        { Algorithm    = testStubAlgorithm
          Name         = "ml"
          ModelVersion = "test-stub" }

    testBuilder.Services.AddSingleton<RoutingAlgorithmRegistration>(testStubReg) |> ignore
    testBuilder.Services.AddSingleton<SmartRouter.Core.Domain.RoutingAlgorithm>(
        System.Func<IServiceProvider, SmartRouter.Core.Domain.RoutingAlgorithm>(fun sp ->
            sp.GetRequiredService<RoutingAlgorithmRegistration>().Algorithm))
    |> ignore

    // Stub IHealthProbe — both upstreams always reachable.
    let stubHealthProbe =
        { new SmartRouter.Core.Ports.IHealthProbe with
            member _.IsReachable(_target)  = true
            member _.IsReachableAsync(_target) _ct = Task.FromResult(true)
            member _.LastProbedAt(_target) = DateTimeOffset.UtcNow }
    testBuilder.Services.AddSingleton<SmartRouter.Core.Ports.IHealthProbe>(stubHealthProbe) |> ignore

    testBuilder.Services.Configure<QueueDispatcherOptions>(
        testBuilder.Configuration.GetSection("Queue"))
    |> ignore

    // Manual QueueDispatcher registration (mirrors QualityFallbackTests pattern).
    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient>(fun sp ->
        SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<SmartRouter.Cli.Adapters.QwenUpstreamClient.UpstreamOptions>>(),
            sp.GetRequiredService<ILogger<SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient>>()))
    |> ignore

    testBuilder.Services.AddSingleton<QueueDispatcher>(fun sp ->
        QueueDispatcher(
            sp.GetRequiredService<SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient>()
                :> SmartRouter.Core.Ports.IUpstreamClient,
            sp.GetRequiredService<IOptions<QueueDispatcherOptions>>().Value,
            sp.GetRequiredService<SmartRouter.Core.Ports.IHealthProbe>(),
            sp.GetRequiredService<ILogger<QueueDispatcher>>()))
    |> ignore

    testBuilder.Services.AddSingleton<SmartRouter.Core.Ports.IUpstreamClient>(fun sp ->
        sp.GetRequiredService<QueueDispatcher>() :> SmartRouter.Core.Ports.IUpstreamClient)
    |> ignore

    testBuilder.Services.AddSingleton<IStatsProvider>(fun sp ->
        sp.GetRequiredService<QueueDispatcher>() :> IStatsProvider)
    |> ignore

    // Phase 15 — override NoOp IQualityCheckStats from configureWithoutMl with
    // real QueueDispatcher-backed implementation (last-registration-wins).
    testBuilder.Services.AddSingleton<IQualityCheckStats>(fun sp ->
        sp.GetRequiredService<QueueDispatcher>() :> IQualityCheckStats)
    |> ignore

    // IModelVersionProvider
    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.ModelVersionProvider.ModelVersionProvider>(fun _sp ->
        SmartRouter.Cli.Adapters.ModelVersionProvider.ModelVersionProvider("test-stub"))
    |> ignore

    testBuilder.Services.AddSingleton<SmartRouter.Core.RetrainingPorts.IModelVersionProvider>(fun sp ->
        sp.GetRequiredService<SmartRouter.Cli.Adapters.ModelVersionProvider.ModelVersionProvider>()
            :> SmartRouter.Core.RetrainingPorts.IModelVersionProvider)
    |> ignore

    // ICanaryGate + ICanaryMetrics
    testBuilder.Services.TryAddSingleton<SmartRouter.Core.CanaryPorts.ICanaryGate>(fun _sp ->
        SmartRouter.Cli.Adapters.CanaryGate.NullCanaryGate() :> SmartRouter.Core.CanaryPorts.ICanaryGate)
    |> ignore

    testBuilder.Services.TryAddSingleton<SmartRouter.Cli.Adapters.CanaryMetrics.ICanaryMetrics>(fun _sp ->
        SmartRouter.Cli.Adapters.CanaryMetrics.NoOpCanaryMetrics() :> SmartRouter.Cli.Adapters.CanaryMetrics.ICanaryMetrics)
    |> ignore

    // ICanaryState — required by /stats endpoint (canary_percentage, canary_active fields).
    testBuilder.Services.AddSingleton<CanaryState>(fun _ -> CanaryState(0)) |> ignore
    testBuilder.Services.AddSingleton<ICanaryState>(fun sp ->
        sp.GetRequiredService<CanaryState>() :> ICanaryState)
    |> ignore

    // TraceLogger triple-reg (always enabled for QSE tests)
    testBuilder.Services.Configure<TraceLoggerOptions>(fun (o: TraceLoggerOptions) ->
        o.Directory       <- traceDir
        o.ChannelCapacity <- 1000)
    |> ignore

    testBuilder.Services.AddSingleton<TraceLogger>(fun sp ->
        new TraceLogger(
            sp.GetRequiredService<IOptions<TraceLoggerOptions>>(),
            sp.GetRequiredService<ILogger<TraceLogger>>()))
    |> ignore

    testBuilder.Services.AddSingleton<ITraceLogger>(fun sp ->
        sp.GetRequiredService<TraceLogger>() :> ITraceLogger)
    |> ignore

    testBuilder.Services.AddHostedService<TraceLogger>(fun sp ->
        sp.GetRequiredService<TraceLogger>())
    |> ignore

    let app = testBuilder.Build()
    SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints app
    SmartRouter.Cli.Endpoints.Stats.mapEndpoints app

    app.StartAsync().GetAwaiter().GetResult()

    let routerPort =
        app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            .Addresses
        |> Seq.head
        |> fun a -> a.Split(':') |> Array.last |> int

    let httpClient = new HttpClient()
    httpClient.BaseAddress <- Uri(sprintf "http://127.0.0.1:%d" routerPort)

    let disp =
        { new IDisposable with
            member _.Dispose() =
                try httpClient.Dispose() with _ -> ()
                try app.StopAsync().GetAwaiter().GetResult() with _ -> ()
                try (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() with _ -> () }

    httpClient, disp, sprintf "http://127.0.0.1:%d" routerPort

/// Mirrors QualityFallbackTests.computePromptUid — same SHA-256 of raw string.
let private computePromptUid (prompt: string) : string =
    use sha = System.Security.Cryptography.SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(prompt)
    let hash  = sha.ComputeHash(bytes)
    hash
    |> Array.take 6
    |> Array.map (fun b -> sprintf "%02x" b)
    |> String.concat ""

/// Find a JSONL row matching the predicate (mirrors QualityFallbackTests.findRow).
let private findRow (jsonlPath: string) (predicate: JsonElement -> bool) : JsonElement option =
    if not (File.Exists(jsonlPath)) then None
    else
        File.ReadAllLines(jsonlPath)
        |> Array.tryPick (fun line ->
            if String.IsNullOrWhiteSpace(line) then None
            else
                let doc = JsonDocument.Parse(line)
                if predicate doc.RootElement then Some (doc.RootElement.Clone()) else None)

// ── Unit tests ───────────────────────────────────────────────────────────────

let tests =
    testSequenced <| testList "quality-signal-enrichment" [

        // QSE-01: Stage 1 — finish_reason fires first regardless of other content quality.

        testCase "QSE-01: finish_reason='length' triggers Bad(FinishReasonMatch) regardless of content quality" <| fun () ->
            let opts = mkOpts [||]
            // Long, normal-entropy content that would pass stages 2-4.
            let longGoodContent = "This is a normal length response with plenty of content and good entropy across many varied characters and words."
            let body = envelope longGoodContent "length"
            let verdict = analyzeResponse opts (Some "length") body
            match verdict with
            | Bad (FinishReasonMatch fr) ->
                Expect.equal fr "length" "FinishReasonMatch should carry the matched finish_reason value"
            | other ->
                failtestf "Expected Bad(FinishReasonMatch \"length\"), got %A" other

        testCase "QSE-01: finish_reason='stop' does NOT trigger (not in BadFinishReasons)" <| fun () ->
            let opts = mkOpts [||]
            let longGoodContent = "This is a normal length response with plenty of content and good entropy across many varied characters and words."
            let body = envelope longGoodContent "stop"
            let verdict = analyzeResponse opts (Some "stop") body
            Expect.equal verdict Good "finish_reason='stop' is normal completion — must not trigger"

        testCase "QSE-01: finish_reason='content_filter' triggers Bad(FinishReasonMatch)" <| fun () ->
            let opts = mkOpts [||]
            let longGoodContent = "This is a normal length response with plenty of content and good entropy across many varied characters and words."
            let body = envelope longGoodContent "content_filter"
            let verdict = analyzeResponse opts (Some "content_filter") body
            match verdict with
            | Bad (FinishReasonMatch fr) ->
                Expect.equal fr "content_filter" "content_filter is in BadFinishReasons by default"
            | other ->
                failtestf "Expected Bad(FinishReasonMatch \"content_filter\"), got %A" other

        // QSE-02: Stage 4 — keyword matching is now case-insensitive (Phase 15 change).

        testCase "QSE-02: BadKeyword 'TODO' matches lowercase 'todo' in content (case-insensitive since Phase 15)" <| fun () ->
            let opts = mkOpts [| "TODO" |]
            // Content length and entropy pass; lowercase "todo" should still match
            let body = envelope "This is a placeholder response that just says todo right here in the middle of the text body." "stop"
            let verdict = analyzeResponse opts None body
            match verdict with
            | Bad (KeywordMatch kw) ->
                Expect.equal kw "TODO" "KeywordMatch reports the configured keyword, not the matched casing"
            | other ->
                failtestf "Expected Bad(KeywordMatch \"TODO\"), got %A" other

        testCase "QSE-02: BadKeyword matching is case-insensitive — mixed-case 'ToDo' matches 'TODO'" <| fun () ->
            let opts = mkOpts [| "TODO" |]
            let body = envelope "This is a placeholder where the ToDo item is tracked in the system for later work." "stop"
            let verdict = analyzeResponse opts None body
            match verdict with
            | Bad (KeywordMatch kw) ->
                Expect.equal kw "TODO" "Mixed-case variant matches case-insensitively"
            | other ->
                failtestf "Expected Bad(KeywordMatch \"TODO\") for mixed-case match, got %A" other

        // QSE-03: Operator refusal pattern opt-in capability.

        testCase "QSE-03: operator-added refusal pattern 'I cannot' is matched when configured (case-insensitive)" <| fun () ->
            let opts = mkOpts [| "I cannot" |]
            // Refusal patterns are NOT in the default BadKeywords — operator must opt in.
            // This test asserts the capability, not the default behavior.
            let body = envelope "I cannot help with that request because it falls outside my supported scope here." "stop"
            let verdict = analyzeResponse opts None body
            match verdict with
            | Bad (KeywordMatch kw) ->
                Expect.equal kw "I cannot" "Operator-added refusal pattern matches via opt-in"
            | other ->
                failtestf "Expected Bad(KeywordMatch \"I cannot\"), got %A" other

        // QSE-04: Stage 2 — Korean-aware effective length.

        testCase "QSE-04: Korean response of ~28 chars passes MinResponseLength=30 via Korean-aware effective length" <| fun () ->
            let opts = mkOpts [||]
            // ~28 Hangul chars; effectiveLength = int(28 * (1 + 1.0 * 0.8)) = int(28 * 1.8) = 50 → passes 30
            let korean = "안녕하세요. 잘 지내고 있어요. 오늘은 좋은 하루입니다."
            let body = envelope korean "stop"
            let verdict = analyzeResponse opts None body
            Expect.equal verdict Good "Korean content has boosted effective length — passes MinResponseLength=30"

        testCase "QSE-04: pure ASCII 28-char content does NOT pass (no Korean boost applied)" <| fun () ->
            let opts = mkOpts [||]
            // 28 ASCII chars; effectiveLength = int(28 * (1 + 0 * 0.8)) = 28 < 30 → fails
            let body = envelope "Twenty eight chars exctly!!" "stop"
            let verdict = analyzeResponse opts None body
            match verdict with
            | Bad (LengthBelow n) ->
                Expect.isLessThan n 30 "ASCII content has no Korean boost; effective length < 30"
            | other ->
                failtestf "Expected Bad(LengthBelow _) for 28-char ASCII, got %A" other

        // QSE-05: Stage 3 — Shannon entropy detection.

        testCase "QSE-05: low-entropy repetitive content triggers Bad(LowEntropy)" <| fun () ->
            let opts = mkOpts [||]
            // "the the the..." — 4 unique chars; expected entropy ~1.9 < 2.5 threshold
            let repetitive = String.replicate 30 "the "   // 120 chars, well above MinResponseLength
            let body = envelope repetitive "stop"
            let verdict = analyzeResponse opts None body
            match verdict with
            | Bad (LowEntropy score) ->
                Expect.isLessThan score 2.5 "Repetitive content entropy is below threshold 2.5"
            | other ->
                failtestf "Expected Bad(LowEntropy _) for repetitive text, got %A" other

        testCase "QSE-05: charEntropy of normal English text is above 4.0" <| fun () ->
            let normal = "This is a perfectly normal English sentence with diverse character variety and many unique letters."
            let h = charEntropy normal
            Expect.isGreaterThan h 4.0 "Normal text has high Shannon entropy"

        // QSE-06: Phase 14 backward-compat — isBadResponse wrapper semantics unchanged.

        testCase "QSE-06: isBadResponse Phase 14 wrapper still returns true for 'TODO' content (keyword match)" <| fun () ->
            let opts = mkOpts [| "TODO" |]
            let body = envelope "This is a long enough response. TODO: implement details later in a follow-up." "stop"
            Expect.isTrue (isBadResponse opts body) "isBadResponse wrapper preserves Phase 14 keyword behavior"

        testCase "QSE-06: isBadResponse Phase 14 wrapper returns false for good response" <| fun () ->
            let opts = mkOpts [| "TODO" |]
            let body = envelope "This is a perfectly complete and correct response without any quality issues at all." "stop"
            Expect.isFalse (isBadResponse opts body) "isBadResponse wrapper preserves Phase 14 good-path behavior"

        // Cascade ordering: finish_reason fires before content checks.

        testCase "Cascade: stage-1 finish_reason wins over short content AND keyword (early exit)" <| fun () ->
            let opts = mkOpts [| "TODO" |]
            // Content also fails length AND has TODO — but finish_reason stage fires first.
            let body = envelope "Short. TODO" "length"
            let verdict = analyzeResponse opts (Some "length") body
            match verdict with
            | Bad (FinishReasonMatch _) ->
                ()   // pass — stage 1 won
            | Bad (LengthBelow _) ->
                failtestf "Stage 2 should not reach when stage 1 fires first"
            | Bad (KeywordMatch _) ->
                failtestf "Stage 4 should not reach when stage 1 fires first"
            | other ->
                failtestf "Expected Bad(FinishReasonMatch _) for cascade stage-1 win, got %A" other

        // ── Integration test ─────────────────────────────────────────────────────
        // End-to-end: fake 35B returns finish_reason="length" → fallback fires →
        // trace bad_reason = "finish_reason=length" → /stats counter incremented.

        testCase "QSE-INT: finish_reason='length' → fallback → bad_reason in trace → stats counter incremented" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), "smart-router-qse-int-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir) |> ignore
            try
                // 35B returns a long-enough, good-entropy, keyword-free response BUT with
                // finish_reason="length" — stage-1 fires first.
                let bad35bBody =
                    """{"id":"chatcmpl-35b","choices":[{"message":{"role":"assistant","content":"This response appears complete but was actually truncated by the model at the token limit here."},"finish_reason":"length"}]}"""
                // 122B returns a proper good response.
                let good122bBody =
                    """{"id":"chatcmpl-122b","choices":[{"message":{"role":"assistant","content":"Recursion is a programming technique where a function calls itself with a smaller subproblem, terminating at a base case."},"finish_reason":"stop"}]}"""

                let port35b, d35b = startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen35b"}]}""")
                    else
                        do! ctx.Response.WriteAsync(bad35bBody)
                })
                let port122b, d122b = startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen122b"}]}""")
                    else
                        do! ctx.Response.WriteAsync(good122bBody)
                })
                try
                    let client, disposeRouter, _baseUrl =
                        startTestRouter port35b port122b tempDir
                    try
                        let promptText = "explain recursion with phase 15 quality check"
                        let uid = computePromptUid promptText
                        let body = sprintf """{"messages":[{"role":"user","content":"%s"}],"stream":false}""" promptText
                        let resp =
                            client.PostAsync(
                                "/v1/chat/completions",
                                new StringContent(body, Encoding.UTF8, "application/json"))
                                .GetAwaiter().GetResult()

                        // 1. HTTP 200 + 122B response forwarded (not 35B's truncated body)
                        Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK from router"
                        let respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        Expect.stringContains respBody "Recursion is a programming" "122B response forwarded — quality fallback fired"
                        Expect.isFalse (respBody.Contains("finish_reason\":\"length")) "35B's truncated response not forwarded"

                        // 2. Wait for async trace log writes to flush.
                        Thread.Sleep(600)

                        let today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")

                        // 3. Trace log: bad_reason = "finish_reason=length"
                        let traceDir  = Path.Combine(tempDir, "logs", "trace")
                        let tracePath = Path.Combine(traceDir, today + ".jsonl")
                        let traceRow =
                            findRow tracePath (fun e ->
                                match e.TryGetProperty("prompt_uid") with
                                | true, pu ->
                                    pu.ValueKind = JsonValueKind.String && pu.GetString() = uid
                                | _ -> false)
                        Expect.isSome traceRow "Trace row exists for this prompt"
                        let tr = traceRow.Value

                        // bad_reason field must be "finish_reason=length"
                        match tr.TryGetProperty("bad_reason") with
                        | true, br ->
                            Expect.equal br.ValueKind JsonValueKind.String "bad_reason is a string (not null)"
                            Expect.equal (br.GetString()) "finish_reason=length" "bad_reason carries 'finish_reason=length' tag=value"
                        | _ ->
                            failtest "bad_reason field missing from trace row"

                        Expect.equal (tr.GetProperty("fallback_kind").GetString()) "quality" "trace fallback_kind = quality"
                        Expect.equal (tr.GetProperty("initial_target").GetString()) "Qwen35B" "trace initial_target = Qwen35B"
                        Expect.equal (tr.GetProperty("final_target").GetString()) "Qwen122B" "trace final_target = Qwen122B"

                        // 4. /stats: quality_check_hits_finish_reason >= 1
                        let statsResp =
                            client.GetAsync("/stats").GetAwaiter().GetResult()
                        Expect.equal statsResp.StatusCode HttpStatusCode.OK "/stats 200 OK"
                        let statsBody = statsResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        use statsDoc = JsonDocument.Parse(statsBody)
                        let root = statsDoc.RootElement

                        match root.TryGetProperty("quality_check_hits_finish_reason") with
                        | true, v ->
                            Expect.isGreaterThanOrEqual (v.GetInt64()) 1L "quality_check_hits_finish_reason >= 1 after fallback"
                        | _ ->
                            failtest "quality_check_hits_finish_reason field missing from /stats response"

                        // Other counters must be 0 (only finish_reason fired)
                        match root.TryGetProperty("quality_check_hits_length") with
                        | true, v -> Expect.equal (v.GetInt64()) 0L "quality_check_hits_length = 0 (stage 1 won)"
                        | _ -> failtest "quality_check_hits_length field missing from /stats"

                        match root.TryGetProperty("quality_check_hits_entropy") with
                        | true, v -> Expect.equal (v.GetInt64()) 0L "quality_check_hits_entropy = 0 (stage 1 won)"
                        | _ -> failtest "quality_check_hits_entropy field missing from /stats"

                        match root.TryGetProperty("quality_check_hits_keyword") with
                        | true, v -> Expect.equal (v.GetInt64()) 0L "quality_check_hits_keyword = 0 (stage 1 won)"
                        | _ -> failtest "quality_check_hits_keyword field missing from /stats"

                    finally disposeRouter.Dispose()
                finally d35b.Dispose() ; d122b.Dispose()
            finally
                try Directory.Delete(tempDir, true) with _ -> ()
    ]
