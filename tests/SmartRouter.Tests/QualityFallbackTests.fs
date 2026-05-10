module SmartRouter.Tests.QualityFallbackTests

// Phase 14: integration tests for the quality-based fallback path (35B response →
// quality check → 122B retry on bad response). Verification is done by parsing
// the JSONL logs (logs/decisions/<date>.jsonl + logs/trace/<date>.jsonl) — the
// same way an operator would diagnose a real fallback in production.
//
// Tests are forced through 35B by injecting a stub RoutingAlgorithmRegistration
// (no real ML model files required). Fake-Kestrel 35B + 122B upstreams return
// canned responses controlled per-test.

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
open SmartRouter.Cli.Adapters.TraceLogger
open SmartRouter.Cli.Adapters.QualityCheck

// ── Fake upstream ─────────────────────────────────────────────────────────────
//
// Starts a Kestrel server on 127.0.0.1:0 (OS-assigned port).
// `respond` is invoked per request — caller controls what is returned.
// Returns (port, IDisposable) — call Dispose() to stop.

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

// ── startTestRouter ─────────────────────────────────────────────────────────
//
// Builds the smart-router under test pointed at the given fake upstreams + temp dirs.
// Stub RoutingAlgorithmRegistration forces decision.Target = Qwen35B regardless of
// prompt content (no real ML model files needed).
// Stub IHealthProbe returns true for both upstreams.
// TraceLogger triple-reg (configureWithoutMl does not do it; mimics trace-enabled state).
// QualityFallbackOptions registered as standalone singleton (per 14-04 deviation).
//
// Returns (HttpClient, IDisposable, routerBaseUrl).

let private startTestRouter
    (model35bPort    : int)
    (model122bPort   : int)
    (tempDir         : string)
    : HttpClient * IDisposable * string =

    let traceDir    = Path.Combine(tempDir, "logs", "trace")
    let decisionDir = Path.Combine(tempDir, "logs", "decisions")
    Directory.CreateDirectory(traceDir)    |> ignore
    Directory.CreateDirectory(decisionDir) |> ignore

    let testBuilder = WebApplication.CreateBuilder()
    testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore

    // MUST happen before configureWithoutMl — overrides bind here
    (testBuilder.Configuration :> IConfigurationBuilder)
        .AddInMemoryCollection([
            // Upstreams — two separate ports
            KeyValuePair("Upstreams:Model35B",  sprintf "http://127.0.0.1:%d" model35bPort)
            KeyValuePair("Upstreams:Model122B", sprintf "http://127.0.0.1:%d" model122bPort)
            // Routing section — required for buildRoutingConfig + validateConfig to succeed
            KeyValuePair("Routing:TimeoutSeconds", "300")
            // Task table — all 7 canonical tasks required by validateConfig
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
            // Model aliases
            KeyValuePair("Routing:ModelAliases:35b",       "Qwen35B")
            KeyValuePair("Routing:ModelAliases:122b",      "Qwen122B")
            KeyValuePair("Routing:ModelAliases:qwen-35b",  "Qwen35B")
            KeyValuePair("Routing:ModelAliases:qwen35b",   "Qwen35B")
            KeyValuePair("Routing:ModelAliases:qwen-122b", "Qwen122B")
            KeyValuePair("Routing:ModelAliases:qwen122b",  "Qwen122B")
            // Quality fallback — enabled with bad keywords
            KeyValuePair("Routing:QualityFallback:Enabled",           "true")
            KeyValuePair("Routing:QualityFallback:MinResponseLength", "30")
            KeyValuePair("Routing:QualityFallback:BadKeywords:0",     "TODO")
            KeyValuePair("Routing:QualityFallback:BadKeywords:1",     "I think")
            // Queue
            KeyValuePair("Queue:FairnessK",                "10")
            KeyValuePair("Queue:MaxConcurrent122B",        "1")
            KeyValuePair("Queue:PerRequestTimeoutSeconds", "60")
            // DecisionLog → per-test temp dir
            KeyValuePair("DecisionLog:Directory",       decisionDir)
            KeyValuePair("DecisionLog:ChannelCapacity", "1000")
            // Health — probe interval doesn't matter; we stub IHealthProbe
            KeyValuePair("Routing:Health:PollingIntervalSeconds",      "60")
            KeyValuePair("Routing:Health:ConsecutiveFailureThreshold", "1")
            // --trace-responses equivalent: enable trace logger
            KeyValuePair("Trace:Enabled", "true")
        ])
    |> ignore

    SmartRouter.Cli.CompositionRoot.configureWithoutMl
        testBuilder.Services
        testBuilder.Configuration
    |> ignore

    // Stub RoutingAlgorithmRegistration — forces decision.Target = Qwen35B on every routing.
    // configureWithoutMl does NOT register RoutingAlgorithmRegistration, so this is sole registration.
    let testStubAlgorithm : SmartRouter.Core.Domain.RoutingAlgorithm =
        fun _cfg _req ->
            { Target       = SmartRouter.Core.Domain.Qwen35B
              Priority     = SmartRouter.Core.Domain.Low
              Reason       = SmartRouter.Core.Domain.ML
              IsFallback   = false
              ModelVersion = "test-stub" }

    let testStubReg : RoutingAlgorithmRegistration =
        { Algorithm    = testStubAlgorithm
          Name         = "ml"
          ModelVersion = "test-stub" }

    testBuilder.Services.AddSingleton<RoutingAlgorithmRegistration>(testStubReg)
    |> ignore

    testBuilder.Services.AddSingleton<SmartRouter.Core.Domain.RoutingAlgorithm>(
        System.Func<IServiceProvider, SmartRouter.Core.Domain.RoutingAlgorithm>(fun sp ->
            sp.GetRequiredService<RoutingAlgorithmRegistration>().Algorithm))
    |> ignore

    // Stub IHealthProbe — both upstreams always reachable.
    // Quality fallback uses healthProbe.IsReachable(Qwen122B) before retry.
    // We stub true so the 122B retry always proceeds in QF-02.
    let stubHealthProbe =
        { new SmartRouter.Core.Ports.IHealthProbe with
            member _.IsReachable(_target) = true
            member _.IsReachableAsync(_target) _ct = Task.FromResult(true)
            member _.LastProbedAt(_target) = DateTimeOffset.UtcNow }
    testBuilder.Services.AddSingleton<SmartRouter.Core.Ports.IHealthProbe>(stubHealthProbe)
    |> ignore

    // QueueDispatcherOptions binding — required by QueueDispatcher ctor.
    testBuilder.Services.Configure<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcherOptions>(
        testBuilder.Configuration.GetSection("Queue"))
    |> ignore

    // QwenUpstreamClient + QueueDispatcher — required by ChatCompletions handler.
    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient>(fun sp ->
        SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<SmartRouter.Cli.Adapters.QwenUpstreamClient.UpstreamOptions>>(),
            sp.GetRequiredService<ILogger<SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient>>()))
    |> ignore

    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcher>(fun sp ->
        SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcher(
            sp.GetRequiredService<SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient>()
                :> SmartRouter.Core.Ports.IUpstreamClient,
            sp.GetRequiredService<IOptions<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcherOptions>>().Value,
            sp.GetRequiredService<SmartRouter.Core.Ports.IHealthProbe>(),
            sp.GetRequiredService<ILogger<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcher>>()))
    |> ignore

    testBuilder.Services.AddSingleton<SmartRouter.Core.Ports.IUpstreamClient>(fun sp ->
        sp.GetRequiredService<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcher>()
            :> SmartRouter.Core.Ports.IUpstreamClient)
    |> ignore

    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.QueueDispatcher.IStatsProvider>(fun sp ->
        sp.GetRequiredService<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcher>()
            :> SmartRouter.Cli.Adapters.QueueDispatcher.IStatsProvider)
    |> ignore

    // IModelVersionProvider — required by ChatCompletions.handler (reads per-request).
    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.ModelVersionProvider.ModelVersionProvider>(fun _sp ->
        SmartRouter.Cli.Adapters.ModelVersionProvider.ModelVersionProvider("test-stub"))
    |> ignore

    testBuilder.Services.AddSingleton<SmartRouter.Core.RetrainingPorts.IModelVersionProvider>(fun sp ->
        sp.GetRequiredService<SmartRouter.Cli.Adapters.ModelVersionProvider.ModelVersionProvider>()
            :> SmartRouter.Core.RetrainingPorts.IModelVersionProvider)
    |> ignore

    // ICanaryGate + ICanaryMetrics — required by ChatCompletions.handler.
    testBuilder.Services.TryAddSingleton<SmartRouter.Core.CanaryPorts.ICanaryGate>(fun _sp ->
        SmartRouter.Cli.Adapters.CanaryGate.NullCanaryGate() :> SmartRouter.Core.CanaryPorts.ICanaryGate)
    |> ignore

    testBuilder.Services.TryAddSingleton<SmartRouter.Cli.Adapters.CanaryMetrics.ICanaryMetrics>(fun _sp ->
        SmartRouter.Cli.Adapters.CanaryMetrics.NoOpCanaryMetrics() :> SmartRouter.Cli.Adapters.CanaryMetrics.ICanaryMetrics)
    |> ignore

    // Phase 14: QualityFallbackOptions standalone singleton (per 14-04 deviation).
    // configureWithoutMl already registers this from RoutingOptions config binding;
    // we do NOT re-register here — configureWithoutMl handles it.

    // Phase 14: TraceLogger triple-reg (always-on for test scenarios).
    // configureWithoutMl does NOT register TraceLogger; production CompositionRoot
    // registers it conditionally on Trace:Enabled=true. This fixture mimics that
    // enabled state unconditionally (all quality fallback tests use trace).
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

// ── Compute prompt_uid ───────────────────────────────────────────────────────
//
// **Single-message-only equivalence**: production `computePromptHash`
// (DecisionLogger.fs) hashes `messages |> List.map (m.Content) |> String.concat ""`.
// This helper hashes a raw string directly. They produce IDENTICAL results when the
// test uses a single-message payload `{"messages":[{"role":"user","content":"<prompt>"}]}`,
// because the production concat of one element is just that element.

let private computePromptUid (prompt: string) : string =
    use sha = System.Security.Cryptography.SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(prompt)
    let hash  = sha.ComputeHash(bytes)
    hash
    |> Array.take 6
    |> Array.map (fun b -> sprintf "%02x" b)
    |> String.concat ""

// ── JSONL row finder ─────────────────────────────────────────────────────────

/// Find a JSONL row matching the predicate. Returns None if file missing or no match.
let private findRow (jsonlPath: string) (predicate: JsonElement -> bool) : JsonElement option =
    if not (File.Exists(jsonlPath)) then None
    else
        File.ReadAllLines(jsonlPath)
        |> Array.tryPick (fun line ->
            if String.IsNullOrWhiteSpace(line) then None
            else
                let doc = JsonDocument.Parse(line)
                if predicate doc.RootElement then Some (doc.RootElement.Clone()) else None)

// ── Tests ─────────────────────────────────────────────────────────────────────

let tests =
    testSequenced <| testList "quality-fallback" [

        // QF-01 — 35B returns good response; quality fallback NOT triggered.
        // Expected:
        //   DecisionLog: routing_reason="ml", target="Qwen35B", fallback_used=false
        //   Trace: initial_target="Qwen35B", final_target="Qwen35B", fallback_kind=null,
        //          initial_response_excerpt=null
        testCase "QF-01: 35B good response — no fallback fires; logs reflect single-call routing" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), "smart-router-qf01-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir) |> ignore
            try
                let goodBody = """{"id":"chatcmpl-35b","choices":[{"message":{"role":"assistant","content":"Recursion is a function calling itself with smaller inputs until a base case is reached."},"finish_reason":"stop"}]}"""
                let port35b, d35b   = startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen35b"}]}""")
                    else
                        do! ctx.Response.WriteAsync(goodBody)
                })
                let port122b, d122b = startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen122b"}]}""")
                    else
                        do! ctx.Response.WriteAsync("""{"id":"chatcmpl-122b","choices":[{"message":{"role":"assistant","content":"122B should not be called"},"finish_reason":"stop"}]}""")
                })
                try
                    let client, disposeRouter, _baseUrl =
                        startTestRouter port35b port122b tempDir
                    try
                        let promptText = "explain recursion"
                        let uid = computePromptUid promptText
                        let body = sprintf """{"messages":[{"role":"user","content":"%s"}],"stream":false}""" promptText
                        let resp =
                            client.PostAsync(
                                "/v1/chat/completions",
                                new StringContent(body, Encoding.UTF8, "application/json"))
                                .GetAwaiter().GetResult()
                        Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK"
                        let respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        Expect.stringContains respBody "Recursion is a function" "35B response forwarded"

                        // Wait for async log writes to flush.
                        Thread.Sleep(500)

                        let today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
                        let decisionDir  = Path.Combine(tempDir, "logs", "decisions")
                        let decisionPath = Path.Combine(decisionDir, today + ".jsonl")
                        let decisionRow =
                            findRow decisionPath (fun e ->
                                let ph = e.GetProperty("prompt_hash")
                                ph.ValueKind = JsonValueKind.String && ph.GetString().StartsWith(uid))
                        Expect.isSome decisionRow "DecisionLog row exists for this prompt"
                        let dr = decisionRow.Value
                        Expect.equal (dr.GetProperty("target").GetString()) "Qwen35B" "target = Qwen35B"
                        Expect.equal (dr.GetProperty("routing_reason").GetString()) "ml" "routing_reason = ml (no fallback)"
                        Expect.isFalse (dr.GetProperty("fallback_used").GetBoolean()) "fallback_used = false"

                        let traceDir  = Path.Combine(tempDir, "logs", "trace")
                        let tracePath = Path.Combine(traceDir, today + ".jsonl")
                        let traceRow =
                            findRow tracePath (fun e ->
                                let pu = e.GetProperty("prompt_uid")
                                pu.ValueKind = JsonValueKind.String && pu.GetString() = uid)
                        Expect.isSome traceRow "Trace row exists for this prompt"
                        let tr = traceRow.Value
                        Expect.equal (tr.GetProperty("initial_target").GetString()) "Qwen35B" "trace initial_target = Qwen35B"
                        Expect.equal (tr.GetProperty("final_target").GetString()) "Qwen35B" "trace final_target = Qwen35B"
                        Expect.equal (tr.GetProperty("fallback_kind").ValueKind) JsonValueKind.Null "trace fallback_kind = null (no fallback)"
                    finally disposeRouter.Dispose()
                finally d35b.Dispose() ; d122b.Dispose()
            finally
                try Directory.Delete(tempDir, true) with _ -> ()

        // QF-02 — 35B returns "TODO: implement this" (bad keyword); quality fallback fires;
        //         122B returns good response.
        // Expected:
        //   DecisionLog: routing_reason="fallback_to_122b", target="Qwen122B", fallback_used=true
        //   Trace: initial_target="Qwen35B", final_target="Qwen122B", fallback_kind="quality",
        //          initial_response_excerpt contains "TODO", final_response_excerpt contains "Recursion"
        testCase "QF-02: 35B 'TODO' response triggers quality fallback to 122B; logs show both" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), "smart-router-qf02-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir) |> ignore
            try
                let bad35bBody  = """{"id":"chatcmpl-35b","choices":[{"message":{"role":"assistant","content":"TODO: implement this"},"finish_reason":"stop"}]}"""
                let good122bBody = """{"id":"chatcmpl-122b","choices":[{"message":{"role":"assistant","content":"Recursion is a function calling itself, terminating at a base case."},"finish_reason":"stop"}]}"""
                let port35b, d35b   = startFakeUpstream (fun ctx -> task {
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
                        let promptText = "explain recursion in detail"
                        let uid = computePromptUid promptText
                        let body = sprintf """{"messages":[{"role":"user","content":"%s"}],"stream":false}""" promptText
                        let resp =
                            client.PostAsync(
                                "/v1/chat/completions",
                                new StringContent(body, Encoding.UTF8, "application/json"))
                                .GetAwaiter().GetResult()
                        Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK"
                        let respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        Expect.stringContains respBody "Recursion is a function" "122B (good) response forwarded — not 35B's TODO"
                        Expect.isFalse (respBody.Contains("TODO: implement")) "35B's bad response NOT forwarded to client"

                        // Wait for async log writes to flush.
                        Thread.Sleep(500)

                        let today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
                        let decisionDir  = Path.Combine(tempDir, "logs", "decisions")
                        let decisionPath = Path.Combine(decisionDir, today + ".jsonl")
                        let decisionRow =
                            findRow decisionPath (fun e ->
                                let ph = e.GetProperty("prompt_hash")
                                ph.ValueKind = JsonValueKind.String && ph.GetString().StartsWith(uid))
                        Expect.isSome decisionRow "DecisionLog row exists"
                        let dr = decisionRow.Value
                        Expect.equal (dr.GetProperty("target").GetString()) "Qwen122B" "target = Qwen122B (fallback final)"
                        Expect.equal (dr.GetProperty("routing_reason").GetString()) "fallback_to_122b" "routing_reason = fallback_to_122b"
                        Expect.isTrue (dr.GetProperty("fallback_used").GetBoolean()) "fallback_used = true"

                        let traceDir  = Path.Combine(tempDir, "logs", "trace")
                        let tracePath = Path.Combine(traceDir, today + ".jsonl")
                        let traceRow =
                            findRow tracePath (fun e ->
                                let pu = e.GetProperty("prompt_uid")
                                pu.ValueKind = JsonValueKind.String && pu.GetString() = uid)
                        Expect.isSome traceRow "Trace row exists"
                        let tr = traceRow.Value
                        Expect.equal (tr.GetProperty("initial_target").GetString()) "Qwen35B" "trace initial_target = Qwen35B"
                        Expect.equal (tr.GetProperty("final_target").GetString()) "Qwen122B" "trace final_target = Qwen122B"
                        Expect.equal (tr.GetProperty("fallback_kind").GetString()) "quality" "trace fallback_kind = quality"
                        let initialExcerpt = tr.GetProperty("initial_response_excerpt").GetString()
                        Expect.stringContains initialExcerpt "TODO" "trace initial_response_excerpt contains TODO"
                        let finalExcerpt = tr.GetProperty("final_response_excerpt").GetString()
                        Expect.stringContains finalExcerpt "Recursion" "trace final_response_excerpt contains 122B response"
                    finally disposeRouter.Dispose()
                finally d35b.Dispose() ; d122b.Dispose()
            finally
                try Directory.Delete(tempDir, true) with _ -> ()
    ]
