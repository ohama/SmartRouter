module SmartRouter.Tests.JudgeIntegrationTests

// Phase 16: integration tests for the 122B-as-judge feature (JDG-01..05).
// Tests cover:
//   JDG-01: BorderlineClassifier 3-way classification (clearly good / borderline entropy / borderline length)
//   JDG-02: JudgeClient prompt template loading + parser (ROUTE_NO wins on collision; missing template → JudgeSkipped)
//   JDG-03: LRU cache hit/miss counters via IJudgeStats
//   JDG-04: Fake-Kestrel judge HTTP counter = 0 for clearly good; = 1 for borderline; cache hit on 2nd identical
//   JDG-05: Trace JSONL judge_called / judge_verdict / judge_latency_ms fields + /stats judge counter fields
//
// Pattern mirrors QualityFallbackTests.fs (Phase 14):
//   - fake Kestrel upstream via startFakeUpstream (copied here as private — QualityFallbackTests helpers are private)
//   - startTestRouterWithJudge builds full router with Routing.Judge.Enabled=true + stub judge handler
//   - testSequenced wraps all tests (Console.SetOut + temp dir hygiene, PITFALL-27)

open System
open System.Collections.Concurrent
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
open Microsoft.Extensions.Http
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open SmartRouter.Cli.Adapters.JudgeClient
open SmartRouter.Cli.Adapters.QualityCheck
open SmartRouter.Cli.Adapters.BorderlineClassifier
open SmartRouter.Cli.Adapters.RoutingAlgorithm
open SmartRouter.Cli.Adapters.TraceLogger

// ── Test helpers ─────────────────────────────────────────────────────────────

/// Default options matching appsettings.json defaults (Phase 15 + Phase 16).
let private mkOpts () : QualityFallbackOptions =
    { Enabled            = true
      MinResponseLength  = 30
      BadKeywords        = [| "TODO"; "I think" |]
      BadFinishReasons   = [| "length"; "content_filter" |]
      EntropyThreshold   = 2.5 }

/// Generates a string of length n with high entropy (>= 4.0) by cycling through
/// many distinct characters. Used as the "clearly good" fixture.
let private mkHighEntropyText (n: int) : string =
    let chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,;:!?"
    let sb = StringBuilder(n)
    for i in 0 .. n - 1 do
        sb.Append(chars.[i % chars.Length]) |> ignore
    sb.ToString()

/// Counting HttpMessageHandler — increments a thread-safe counter on every send,
/// returns a programmable response body (caller sets responseBody ref).
type private CountingJudgeHandler(responseBody: string ref) =
    inherit HttpMessageHandler()
    let mutable count = 0
    member _.CallCount with get () = Interlocked.CompareExchange(&count, 0, 0)
    override _.SendAsync(_req: HttpRequestMessage, _ct: CancellationToken) =
        Interlocked.Increment(&count) |> ignore
        let body = !responseBody
        let resp = new HttpResponseMessage(HttpStatusCode.OK)
        resp.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        Task.FromResult(resp)

// ── Inline copy of startFakeUpstream from QualityFallbackTests ───────────────
// QualityFallbackTests.startFakeUpstream is `private` — not accessible cross-module.
// Per plan note: copy helpers rather than broadening Phase 14 visibility.

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

// ── startTestRouterWithJudge ─────────────────────────────────────────────────
// Builds the smart-router under test with Routing.Judge.Enabled=true and a
// stub CountingJudgeHandler injected as the "judge" named HttpClient primary handler.
// Mirrors QualityFallbackTests.startTestRouter (configureWithoutMl + full manual DI)
// with Phase 16 judge additions. configureWithoutMl registers IJudgeStats NoOp —
// we replace it after with the real JudgeClient.
//
// Returns (httpClient, app, routerPort); caller must stop and dispose app.

let private startTestRouterWithJudge
    (port35b       : int)
    (port122b      : int)
    (tempDir       : string)
    (promptPath    : string)
    (judgeStub     : CountingJudgeHandler)
    : HttpClient * WebApplication * int =

    let traceDir    = Path.Combine(tempDir, "logs", "trace")
    let decisionDir = Path.Combine(tempDir, "logs", "decisions")
    Directory.CreateDirectory(traceDir)    |> ignore
    Directory.CreateDirectory(decisionDir) |> ignore

    let testBuilder = WebApplication.CreateBuilder()
    testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore

    // MUST happen before configureWithoutMl — overrides bind here.
    (testBuilder.Configuration :> IConfigurationBuilder)
        .AddInMemoryCollection([
            // Upstreams
            KeyValuePair("Upstreams:Model35B",  sprintf "http://127.0.0.1:%d" port35b)
            KeyValuePair("Upstreams:Model122B", sprintf "http://127.0.0.1:%d" port122b)
            // Routing
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
            // Quality fallback — enabled
            KeyValuePair("Routing:QualityFallback:Enabled",           "true")
            KeyValuePair("Routing:QualityFallback:MinResponseLength", "30")
            KeyValuePair("Routing:QualityFallback:BadKeywords:0",     "TODO")
            KeyValuePair("Routing:QualityFallback:BadKeywords:1",     "I think")
            KeyValuePair("Routing:QualityFallback:EntropyThreshold",  "2.5")
            // Phase 16 judge — ENABLED (needed for configureWithoutMl IJudgeStats branch selection)
            KeyValuePair("Routing:Judge:Enabled",         "true")
            KeyValuePair("Routing:Judge:Endpoint",        sprintf "http://127.0.0.1:%d" port122b)
            KeyValuePair("Routing:Judge:PromptPath",      promptPath)
            KeyValuePair("Routing:Judge:TimeoutSeconds",  "5")
            KeyValuePair("Routing:Judge:MaxCacheEntries", "100")
            // Queue
            KeyValuePair("Queue:FairnessK",                "10")
            KeyValuePair("Queue:MaxConcurrent122B",        "1")
            KeyValuePair("Queue:PerRequestTimeoutSeconds", "60")
            // DecisionLog → per-test temp dir
            KeyValuePair("DecisionLog:Directory",       decisionDir)
            KeyValuePair("DecisionLog:ChannelCapacity", "1000")
            // Health
            KeyValuePair("Routing:Health:PollingIntervalSeconds",      "60")
            KeyValuePair("Routing:Health:ConsecutiveFailureThreshold", "1")
            // Trace
            KeyValuePair("Trace:Enabled", "true")
            KeyValuePair("Trace:Directory", traceDir)
        ])
    |> ignore

    // Use configureWithoutMl — avoids ML model file dependencies (IEmbedder etc.)
    // Mirrors QualityFallbackTests.startTestRouter pattern exactly.
    SmartRouter.Cli.CompositionRoot.configureWithoutMl
        testBuilder.Services
        testBuilder.Configuration
    |> ignore

    // Stub RoutingAlgorithmRegistration — forces decision.Target = Qwen35B on every routing.
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
    let stubHealthProbe =
        { new SmartRouter.Core.Ports.IHealthProbe with
            member _.IsReachable(_target) = true
            member _.IsReachableAsync(_target) _ct = Task.FromResult(true)
            member _.LastProbedAt(_target) = DateTimeOffset.UtcNow }
    testBuilder.Services.AddSingleton<SmartRouter.Core.Ports.IHealthProbe>(stubHealthProbe)
    |> ignore

    // QueueDispatcherOptions binding
    testBuilder.Services.Configure<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcherOptions>(
        testBuilder.Configuration.GetSection("Queue"))
    |> ignore

    // QwenUpstreamClient + QueueDispatcher
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

    // ICanaryState — required by GET /stats endpoint (CanaryState.GetPercentage()).
    // configureWithoutMl does NOT register ICanaryState. Register a no-op at 0%.
    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.CanaryState.ICanaryState>(fun _sp ->
        { new SmartRouter.Cli.Adapters.CanaryState.ICanaryState with
            member _.GetPercentage()             = 0
            member _.SetPercentage(_pct, _rsn)   = ()
            member _.IsRolledBack                = false
            member _.LastRollbackAt              = None
            member _.LastRollbackReason          = None })
    |> ignore

    // TraceLogger triple-reg (always-on for these judge tests).
    // configureWithoutMl does NOT register TraceLogger; mimics trace-enabled state.
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

    // Phase 16 judge: register named "judge" HttpClient + real JudgeClient.
    // configureWithoutMl already registered IJudgeStats NoOp; we override with the real JudgeClient.
    // The stub CountingJudgeHandler is injected via HttpClientFactoryOptions post-registration.
    testBuilder.Services.AddHttpClient("judge")
        .ConfigureHttpClient(fun c ->
            c.BaseAddress <- Uri(sprintf "http://127.0.0.1:%d" port122b)
            c.Timeout     <- TimeSpan.FromSeconds(5.0))
        |> ignore

    // Override "judge" named-client primary handler with counting stub.
    testBuilder.Services.Configure<HttpClientFactoryOptions>(
        "judge",
        fun (o: HttpClientFactoryOptions) ->
            o.HttpMessageHandlerBuilderActions.Add(fun b ->
                b.PrimaryHandler <- judgeStub :> HttpMessageHandler))
    |> ignore

    // JudgeClient concrete + IJudgeClient alias + IJudgeStats (replaces configureWithoutMl's NoOp).
    let judgeOpts =
        { Endpoint        = sprintf "http://127.0.0.1:%d" port122b
          PromptPath      = promptPath
          TimeoutSeconds  = 5
          MaxCacheEntries = 100 }

    testBuilder.Services.AddSingleton<JudgeClient>(fun sp ->
        JudgeClient(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
            judgeOpts,
            sp.GetRequiredService<ILogger<JudgeClient>>()))
    |> ignore

    testBuilder.Services.AddSingleton<IJudgeClient>(fun sp ->
        sp.GetRequiredService<JudgeClient>() :> IJudgeClient)
    |> ignore

    // Replace the NoOp IJudgeStats that configureWithoutMl registered with the real one.
    // TryAddSingleton won't replace existing; we use a workaround: remove existing and re-add.
    // Simplest approach: add a new registration — DI will use the LAST registration for IJudgeStats.
    testBuilder.Services.AddSingleton<IJudgeStats>(fun sp ->
        sp.GetRequiredService<JudgeClient>() :> IJudgeStats)
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

    httpClient, app, routerPort

// ── Tests ─────────────────────────────────────────────────────────────────────

let tests =
    testSequenced <| testList "JudgeIntegration" [

        // ── JDG-01: BorderlineClassifier 3-way classification ──────────────────

        testCase "JDG-01 classifyBorderline returns None for clearly good response (high entropy + long length)" <| fun _ ->
            let opts = mkOpts ()
            // length 200 >> 30 * 1.5 = 45; entropy ~5+ (diverse chars)
            let content = mkHighEntropyText 200
            let result = classifyBorderline opts content
            Expect.isNone result "clearly good response should NOT be flagged as borderline"

        testCase "JDG-01 classifyBorderline returns Some UncertainLength when length in [30, 45)" <| fun _ ->
            let opts = mkOpts ()
            // Length 35: above MinResponseLength 30, below 30*1.5=45 → length band edge.
            // High entropy (use diverse chars) so we don't accidentally trigger entropy band first.
            let content = mkHighEntropyText 35
            let result = classifyBorderline opts content
            match result with
            | Some (UncertainLength n) ->
                Expect.isTrue (n >= 30 && n < 45) (sprintf "effective length %d should be in [30, 45)" n)
            | other ->
                failtestf "expected Some (UncertainLength _), got %A" other

        testCase "JDG-01 classifyBorderline returns Some UncertainEntropy when entropy in [2.5, 3.5)" <| fun _ ->
            let opts = mkOpts ()
            // Construct text with entropy in [2.5, 3.5):
            //   8 distinct chars with equal frequency: entropy = log2(8) = 3.0 ∈ [2.5, 3.5).
            //   Length = 8 × 20 = 160 chars >> 45 (length band upper), so length band won't trigger first.
            //   Entropy 3.0 >= EntropyThreshold (2.5) AND < EntropyThreshold + 1.0 (3.5) → borderline.
            let content = String.replicate 20 "abcdefgh"  // length 160; entropy = log2(8) = 3.0
            let result = classifyBorderline opts content
            match result with
            | Some (UncertainEntropy e) ->
                Expect.isTrue (e >= 2.5 && e < 3.5) (sprintf "entropy %f should be in [2.5, 3.5)" e)
            | other ->
                failtestf "expected Some (UncertainEntropy _), got %A" other

        // ── JDG-02: JudgeClient parser semantics + prompt template loading ─────

        testCase "JDG-02 parser: ROUTE_NO wins on collision (safety bias)" <| fun _ ->
            // Response body contains BOTH ROUTE_YES and ROUTE_NO. Per parseContent, ROUTE_NO must win.
            let tmpDir = Path.Combine(Path.GetTempPath(), sprintf "judge-test-%s" (Guid.NewGuid().ToString("N")))
            Directory.CreateDirectory(tmpDir) |> ignore
            let promptPath = Path.Combine(tmpDir, "judge-prompt.md")
            File.WriteAllText(promptPath, "Q: {{QUESTION}}\nR: {{RESPONSE}}\nROUTE_YES or ROUTE_NO")
            try
                let collisionBody =
                    """{"choices":[{"message":{"content":"ROUTE_YES and also ROUTE_NO are both here"}}]}"""
                let bodyRef = ref collisionBody
                let stub = new CountingJudgeHandler(bodyRef)
                let services = ServiceCollection()
                services.AddLogging() |> ignore
                // Use chain form (not 2-arg form) — 2-arg AddHttpClient in F# silently drops BaseAddress
                services.AddHttpClient("judge")
                    .ConfigureHttpClient(fun c ->
                        c.BaseAddress <- Uri("http://stub/")
                        c.Timeout     <- TimeSpan.FromSeconds(5.0))
                    .ConfigurePrimaryHttpMessageHandler(fun () -> stub :> HttpMessageHandler) |> ignore
                let provider = services.BuildServiceProvider()
                let httpFactory = provider.GetRequiredService<IHttpClientFactory>()
                let logger = provider.GetRequiredService<ILogger<JudgeClient>>()
                let opts =
                    { Endpoint        = "http://stub/"
                      PromptPath      = promptPath
                      TimeoutSeconds  = 5
                      MaxCacheEntries = 100 }
                let judge = JudgeClient(httpFactory, opts, logger) :> IJudgeClient
                let result = judge.VerdictAsync("ph1", "rh1", "q", "r", CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal result RouteNo "ROUTE_NO must win on collision (safety bias)"
                Expect.equal stub.CallCount 1 "exactly 1 HTTP call (no retry on success)"
            finally
                Directory.Delete(tmpDir, true)

        testCase "JDG-02 JudgeClient returns JudgeSkipped when prompt template file missing" <| fun _ ->
            let tmpDir = Path.Combine(Path.GetTempPath(), sprintf "judge-test-%s" (Guid.NewGuid().ToString("N")))
            Directory.CreateDirectory(tmpDir) |> ignore
            try
                let missingPath = Path.Combine(tmpDir, "does-not-exist.md")
                let services = ServiceCollection()
                services.AddLogging() |> ignore
                // Use chain form — 2-arg form silently drops BaseAddress in F# (not needed here but consistent)
                services.AddHttpClient("judge")
                    .ConfigureHttpClient(fun c ->
                        c.BaseAddress <- Uri("http://127.0.0.1:1")  // unreachable; never called when template missing
                        c.Timeout     <- TimeSpan.FromSeconds(5.0)) |> ignore
                let provider = services.BuildServiceProvider()
                let httpFactory = provider.GetRequiredService<IHttpClientFactory>()
                let logger = provider.GetRequiredService<ILogger<JudgeClient>>()
                let opts = { Endpoint = "http://127.0.0.1:1"; PromptPath = missingPath; TimeoutSeconds = 5; MaxCacheEntries = 100 }
                let judge = JudgeClient(httpFactory, opts, logger) :> IJudgeClient
                let task = judge.VerdictAsync("p1", "r1", "question", "response", CancellationToken.None)
                let result = task.GetAwaiter().GetResult()
                match result with
                | JudgeSkipped reason ->
                    Expect.stringContains reason "template" (sprintf "JudgeSkipped reason should mention 'template', got: %s" reason)
                | other ->
                    failtestf "expected JudgeSkipped, got %A" other
            finally
                Directory.Delete(tmpDir, true)

        // ── JDG-03: LRU cache hit/miss + counters ──────────────────────────────

        testCase "JDG-03 cache hit on second identical (promptHash, responseHash); IJudgeStats counters reflect" <| fun _ ->
            // First call → cache miss + 1 HTTP call; Second call same key → cache hit + 0 additional HTTP calls.
            let tmpDir = Path.Combine(Path.GetTempPath(), sprintf "judge-test-%s" (Guid.NewGuid().ToString("N")))
            Directory.CreateDirectory(tmpDir) |> ignore
            let promptPath = Path.Combine(tmpDir, "judge-prompt.md")
            File.WriteAllText(promptPath, "Q: {{QUESTION}}\nR: {{RESPONSE}}\nAnswer: ROUTE_YES or ROUTE_NO")
            try
                let yesBody = """{"choices":[{"message":{"content":"ROUTE_YES"}}]}"""
                let bodyRef = ref yesBody
                let stub = new CountingJudgeHandler(bodyRef)

                let services = ServiceCollection()
                services.AddLogging() |> ignore
                // Use chain form — 2-arg form silently drops BaseAddress in F#
                services.AddHttpClient("judge")
                    .ConfigureHttpClient(fun c ->
                        c.BaseAddress <- Uri("http://stub/")
                        c.Timeout     <- TimeSpan.FromSeconds(5.0))
                    .ConfigurePrimaryHttpMessageHandler(fun () -> stub :> HttpMessageHandler) |> ignore
                let provider = services.BuildServiceProvider()
                let httpFactory = provider.GetRequiredService<IHttpClientFactory>()
                let logger = provider.GetRequiredService<ILogger<JudgeClient>>()
                let opts = { Endpoint = "http://stub/"; PromptPath = promptPath; TimeoutSeconds = 5; MaxCacheEntries = 100 }
                let judge = JudgeClient(httpFactory, opts, logger)
                let judgeC = judge :> IJudgeClient
                let judgeS = judge :> IJudgeStats

                // First call — cache miss
                let v1 = judgeC.VerdictAsync("ph1", "rh1", "q", "r", CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal v1 RouteYes "first call returns parsed verdict"
                Expect.equal stub.CallCount 1 "first call should hit HTTP"

                // Second call same keys — cache hit
                let v2 = judgeC.VerdictAsync("ph1", "rh1", "q", "r", CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal v2 RouteYes "second call returns cached verdict"
                Expect.equal stub.CallCount 1 "second call should NOT hit HTTP (cache hit)"

                let struct (hits, misses, calls) = judgeS.GetJudgeStats()
                Expect.equal hits   1L "cacheHits=1"
                Expect.equal misses 1L "cacheMisses=1"
                Expect.equal calls  1L "callCount=1 (HTTP fired once)"
            finally
                Directory.Delete(tmpDir, true)

        // ── JDG-04: Fake-Kestrel — judge call only on borderline ───────────────

        testCase "JDG-04 judge HTTP NOT called for clearly good 35B response" <| fun _ ->
            // 35B fake returns clearly-good response (high entropy, length 200 → not borderline).
            // Judge fake counter must = 0.
            let tempDir = Path.Combine(Path.GetTempPath(), "judge-04-good-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir) |> ignore
            let promptPath = Path.Combine(tempDir, "judge-prompt.md")
            File.WriteAllText(promptPath, "Q: {{QUESTION}}\nR: {{RESPONSE}}\nAnswer: ROUTE_YES or ROUTE_NO")
            try
                // Clearly-good response: 200 chars high-entropy text (well above 30*1.5=45 borderline edge,
                // entropy ~5+ well above 2.5+1.0=3.5 borderline edge).
                let goodContent = mkHighEntropyText 200
                let goodBody =
                    sprintf """{"id":"chatcmpl-35b","choices":[{"message":{"role":"assistant","content":%s},"finish_reason":"stop"}]}"""
                        (JsonSerializer.Serialize(goodContent))

                let port35b, d35b = startFakeUpstream (fun ctx -> task {
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
                        do! ctx.Response.WriteAsync("""{"id":"chatcmpl-122b","choices":[{"message":{"role":"assistant","content":"122B should NOT be called"},"finish_reason":"stop"}]}""")
                })

                let yesBody = """{"choices":[{"message":{"content":"ROUTE_YES"}}]}"""
                let bodyRef = ref yesBody
                let judgeStub = new CountingJudgeHandler(bodyRef)

                try
                    let httpClient, app, _routerPort =
                        startTestRouterWithJudge port35b port122b tempDir promptPath judgeStub
                    try
                        let body = """{"messages":[{"role":"user","content":"explain"}],"stream":false}"""
                        let resp =
                            httpClient.PostAsync(
                                "/v1/chat/completions",
                                new StringContent(body, Encoding.UTF8, "application/json"))
                                .GetAwaiter().GetResult()
                        Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK"

                        Thread.Sleep(300) // flush

                        // Clearly-good response → judge MUST NOT be called
                        Expect.equal judgeStub.CallCount 0 "judge HTTP counter must be 0 for clearly-good response"
                    finally
                        try httpClient.Dispose() with _ -> ()
                        try app.StopAsync().GetAwaiter().GetResult() with _ -> ()
                        try (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() with _ -> ()
                finally
                    d35b.Dispose() ; d122b.Dispose()
            finally
                try Directory.Delete(tempDir, true) with _ -> ()

        testCase "JDG-04 judge HTTP called once on borderline 35B response; cache hit on second identical request" <| fun _ ->
            // 35B fake returns borderline response (effective length 35 → in [30, 45) length band).
            // First request → judge counter = 1; second identical request → judge counter still 1 (cache hit).
            let tempDir = Path.Combine(Path.GetTempPath(), "judge-04-borderline-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir) |> ignore
            let promptPath = Path.Combine(tempDir, "judge-prompt.md")
            File.WriteAllText(promptPath, "Q: {{QUESTION}}\nR: {{RESPONSE}}\nAnswer: ROUTE_YES or ROUTE_NO")
            try
                // Borderline content: 35 chars high-entropy → length band edge [30, 45).
                let borderlineContent = mkHighEntropyText 35
                let borderlineBody =
                    sprintf """{"id":"chatcmpl-35b","choices":[{"message":{"role":"assistant","content":%s},"finish_reason":"stop"}]}"""
                        (JsonSerializer.Serialize(borderlineContent))

                let port35b, d35b = startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen35b"}]}""")
                    else
                        do! ctx.Response.WriteAsync(borderlineBody)
                })
                let port122b, d122b = startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen122b"}]}""")
                    else
                        do! ctx.Response.WriteAsync("""{"choices":[{"message":{"content":"122B output"}}]}""")
                })

                let yesBody = """{"choices":[{"message":{"content":"ROUTE_YES"}}]}"""
                let bodyRef = ref yesBody
                let judgeStub = new CountingJudgeHandler(bodyRef)

                try
                    let httpClient, app, _routerPort =
                        startTestRouterWithJudge port35b port122b tempDir promptPath judgeStub
                    try
                        let body = """{"messages":[{"role":"user","content":"explain quickly"}],"stream":false}"""

                        // First request → cache miss, 1 HTTP call to judge stub
                        let r1 =
                            httpClient.PostAsync(
                                "/v1/chat/completions",
                                new StringContent(body, Encoding.UTF8, "application/json"))
                                .GetAwaiter().GetResult()
                        Expect.equal r1.StatusCode HttpStatusCode.OK "first request 200 OK"
                        Expect.equal judgeStub.CallCount 1 "borderline first request → judge HTTP counter = 1"

                        // Second identical request → cache hit, NO additional HTTP call
                        let r2 =
                            httpClient.PostAsync(
                                "/v1/chat/completions",
                                new StringContent(body, Encoding.UTF8, "application/json"))
                                .GetAwaiter().GetResult()
                        Expect.equal r2.StatusCode HttpStatusCode.OK "second request 200 OK"
                        Expect.equal judgeStub.CallCount 1 "second identical request → judge HTTP counter still = 1 (cache hit)"
                    finally
                        try httpClient.Dispose() with _ -> ()
                        try app.StopAsync().GetAwaiter().GetResult() with _ -> ()
                        try (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() with _ -> ()
                finally
                    d35b.Dispose() ; d122b.Dispose()
            finally
                try Directory.Delete(tempDir, true) with _ -> ()

        // ── JDG-05: TraceLog + /stats judge fields ─────────────────────────────

        testCase "JDG-05 trace JSONL has judge_called=false / verdict=null / latency_ms=null when judge disabled" <| fun _ ->
            // Routing.Judge.Enabled=false (omitted from config) →
            // judge_called=false, judge_verdict=null, judge_latency_ms=null.
            // schema_version=1 unchanged (additive change).
            let tempDir = Path.Combine(Path.GetTempPath(), "judge-05-disabled-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir) |> ignore
            let traceDir    = Path.Combine(tempDir, "logs", "trace")
            let decisionDir = Path.Combine(tempDir, "logs", "decisions")
            Directory.CreateDirectory(traceDir)    |> ignore
            Directory.CreateDirectory(decisionDir) |> ignore
            try
                // Clearly-good response — just needs to pass quality check without triggering fallback
                let goodBody = """{"id":"chatcmpl-35b","choices":[{"message":{"role":"assistant","content":"This is a clearly good response with plenty of content to satisfy the length minimum and entropy check requirement for this test."},"finish_reason":"stop"}]}"""
                let port35b, d35b = startFakeUpstream (fun ctx -> task {
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
                        do! ctx.Response.WriteAsync("""{"choices":[{"message":{"content":"122B"}}]}""")
                })
                try
                    // Build router with Routing.Judge.Enabled NOT set (defaults to false).
                    // Mirror startTestRouterWithJudge but omit the Routing:Judge:Enabled key.
                    let testBuilder = WebApplication.CreateBuilder()
                    testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
                    (testBuilder.Configuration :> IConfigurationBuilder)
                        .AddInMemoryCollection([
                            KeyValuePair("Upstreams:Model35B",  sprintf "http://127.0.0.1:%d" port35b)
                            KeyValuePair("Upstreams:Model122B", sprintf "http://127.0.0.1:%d" port122b)
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
                            KeyValuePair("Routing:QualityFallback:Enabled",           "true")
                            KeyValuePair("Routing:QualityFallback:MinResponseLength", "30")
                            KeyValuePair("Routing:QualityFallback:BadKeywords:0",     "TODO")
                            KeyValuePair("Routing:QualityFallback:BadKeywords:1",     "I think")
                            KeyValuePair("Routing:QualityFallback:EntropyThreshold",  "2.5")
                            // Routing.Judge.Enabled deliberately ABSENT → defaults to false
                            KeyValuePair("Queue:FairnessK",                "10")
                            KeyValuePair("Queue:MaxConcurrent122B",        "1")
                            KeyValuePair("Queue:PerRequestTimeoutSeconds", "60")
                            KeyValuePair("DecisionLog:Directory",       decisionDir)
                            KeyValuePair("DecisionLog:ChannelCapacity", "1000")
                            KeyValuePair("Routing:Health:PollingIntervalSeconds",      "60")
                            KeyValuePair("Routing:Health:ConsecutiveFailureThreshold", "1")
                            KeyValuePair("Trace:Enabled", "true")
                            KeyValuePair("Trace:Directory", traceDir)
                        ])
                    |> ignore

                    // Use configureWithoutMl — judge disabled (Routing.Judge.Enabled absent → false).
                    SmartRouter.Cli.CompositionRoot.configureWithoutMl
                        testBuilder.Services
                        testBuilder.Configuration
                    |> ignore

                    // Stub RoutingAlgorithmRegistration
                    let stubAlg : SmartRouter.Core.Domain.RoutingAlgorithm =
                        fun _cfg _req ->
                            { Target = SmartRouter.Core.Domain.Qwen35B; Priority = SmartRouter.Core.Domain.Low
                              Reason = SmartRouter.Core.Domain.ML; IsFallback = false; ModelVersion = "test-stub" }
                    let stubReg : RoutingAlgorithmRegistration =
                        { Algorithm = stubAlg; Name = "ml"; ModelVersion = "test-stub" }
                    testBuilder.Services.AddSingleton<RoutingAlgorithmRegistration>(stubReg) |> ignore
                    testBuilder.Services.AddSingleton<SmartRouter.Core.Domain.RoutingAlgorithm>(
                        System.Func<IServiceProvider, SmartRouter.Core.Domain.RoutingAlgorithm>(fun sp ->
                            sp.GetRequiredService<RoutingAlgorithmRegistration>().Algorithm)) |> ignore

                    // Stub IHealthProbe
                    let stubProbe =
                        { new SmartRouter.Core.Ports.IHealthProbe with
                            member _.IsReachable(_t) = true
                            member _.IsReachableAsync(_t) _ct = Task.FromResult(true)
                            member _.LastProbedAt(_t) = DateTimeOffset.UtcNow }
                    testBuilder.Services.AddSingleton<SmartRouter.Core.Ports.IHealthProbe>(stubProbe) |> ignore

                    // Queue
                    testBuilder.Services.Configure<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcherOptions>(
                        testBuilder.Configuration.GetSection("Queue")) |> ignore
                    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient>(fun sp ->
                        SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient(
                            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
                            sp.GetRequiredService<IOptions<SmartRouter.Cli.Adapters.QwenUpstreamClient.UpstreamOptions>>(),
                            sp.GetRequiredService<ILogger<SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient>>())) |> ignore
                    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcher>(fun sp ->
                        SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcher(
                            sp.GetRequiredService<SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient>()
                                :> SmartRouter.Core.Ports.IUpstreamClient,
                            sp.GetRequiredService<IOptions<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcherOptions>>().Value,
                            sp.GetRequiredService<SmartRouter.Core.Ports.IHealthProbe>(),
                            sp.GetRequiredService<ILogger<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcher>>())) |> ignore
                    testBuilder.Services.AddSingleton<SmartRouter.Core.Ports.IUpstreamClient>(fun sp ->
                        sp.GetRequiredService<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcher>()
                            :> SmartRouter.Core.Ports.IUpstreamClient) |> ignore
                    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.QueueDispatcher.IStatsProvider>(fun sp ->
                        sp.GetRequiredService<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcher>()
                            :> SmartRouter.Cli.Adapters.QueueDispatcher.IStatsProvider) |> ignore

                    // IModelVersionProvider
                    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.ModelVersionProvider.ModelVersionProvider>(fun _ ->
                        SmartRouter.Cli.Adapters.ModelVersionProvider.ModelVersionProvider("test-stub")) |> ignore
                    testBuilder.Services.AddSingleton<SmartRouter.Core.RetrainingPorts.IModelVersionProvider>(fun sp ->
                        sp.GetRequiredService<SmartRouter.Cli.Adapters.ModelVersionProvider.ModelVersionProvider>()
                            :> SmartRouter.Core.RetrainingPorts.IModelVersionProvider) |> ignore

                    // ICanaryGate + ICanaryMetrics
                    testBuilder.Services.TryAddSingleton<SmartRouter.Core.CanaryPorts.ICanaryGate>(fun _ ->
                        SmartRouter.Cli.Adapters.CanaryGate.NullCanaryGate() :> SmartRouter.Core.CanaryPorts.ICanaryGate) |> ignore
                    testBuilder.Services.TryAddSingleton<SmartRouter.Cli.Adapters.CanaryMetrics.ICanaryMetrics>(fun _ ->
                        SmartRouter.Cli.Adapters.CanaryMetrics.NoOpCanaryMetrics() :> SmartRouter.Cli.Adapters.CanaryMetrics.ICanaryMetrics) |> ignore

                    // ICanaryState — required by GET /stats endpoint
                    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.CanaryState.ICanaryState>(fun _ ->
                        { new SmartRouter.Cli.Adapters.CanaryState.ICanaryState with
                            member _.GetPercentage()           = 0
                            member _.SetPercentage(_p, _r)     = ()
                            member _.IsRolledBack              = false
                            member _.LastRollbackAt            = None
                            member _.LastRollbackReason        = None }) |> ignore

                    // TraceLogger triple-reg
                    testBuilder.Services.Configure<TraceLoggerOptions>(fun (o: TraceLoggerOptions) ->
                        o.Directory <- traceDir; o.ChannelCapacity <- 1000) |> ignore
                    testBuilder.Services.AddSingleton<TraceLogger>(fun sp ->
                        new TraceLogger(
                            sp.GetRequiredService<IOptions<TraceLoggerOptions>>(),
                            sp.GetRequiredService<ILogger<TraceLogger>>())) |> ignore
                    testBuilder.Services.AddSingleton<ITraceLogger>(fun sp ->
                        sp.GetRequiredService<TraceLogger>() :> ITraceLogger) |> ignore
                    testBuilder.Services.AddHostedService<TraceLogger>(fun sp ->
                        sp.GetRequiredService<TraceLogger>()) |> ignore

                    let app = testBuilder.Build()
                    SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints app
                    SmartRouter.Cli.Endpoints.Stats.mapEndpoints app
                    app.StartAsync().GetAwaiter().GetResult()

                    let routerPort =
                        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
                        |> Seq.head
                        |> fun a -> a.Split(':') |> Array.last |> int

                    use httpClient = new HttpClient()
                    httpClient.BaseAddress <- Uri(sprintf "http://127.0.0.1:%d" routerPort)

                    try
                        let body = """{"messages":[{"role":"user","content":"hello"}],"stream":false}"""
                        let resp = httpClient.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult()
                        Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK"

                        Thread.Sleep(500) // flush

                        let today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
                        let tracePath = Path.Combine(traceDir, today + ".jsonl")
                        Expect.isTrue (File.Exists(tracePath)) "trace file exists"
                        let line = File.ReadAllLines(tracePath) |> Array.head
                        use doc = JsonDocument.Parse(line)
                        let root = doc.RootElement
                        Expect.equal (root.GetProperty("schema_version").GetInt32()) 1 "schema_version still = 1 (additive)"
                        Expect.isFalse (root.GetProperty("judge_called").GetBoolean()) "judge_called=false when disabled"
                        Expect.equal (root.GetProperty("judge_verdict").ValueKind) JsonValueKind.Null "judge_verdict=null when disabled"
                        Expect.equal (root.GetProperty("judge_latency_ms").ValueKind) JsonValueKind.Null "judge_latency_ms=null when disabled"
                    finally
                        try app.StopAsync().GetAwaiter().GetResult() with _ -> ()
                        try (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() with _ -> ()
                finally d35b.Dispose() ; d122b.Dispose()
            finally
                try Directory.Delete(tempDir, true) with _ -> ()

        testCase "JDG-05 /stats wire has judge_cache_hits/_misses/_call_count int64 fields after judge fires" <| fun _ ->
            // After driving 1 cache miss + 1 cache hit, GET /stats returns:
            //   judge_cache_hits=1, judge_cache_misses=1, judge_call_count=1
            let tempDir = Path.Combine(Path.GetTempPath(), "judge-05-stats-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir) |> ignore
            let promptPath = Path.Combine(tempDir, "judge-prompt.md")
            File.WriteAllText(promptPath, "Q: {{QUESTION}}\nR: {{RESPONSE}}\nAnswer: ROUTE_YES or ROUTE_NO")
            try
                let borderlineContent = mkHighEntropyText 35
                let borderlineBody =
                    sprintf """{"id":"chatcmpl-35b","choices":[{"message":{"role":"assistant","content":%s},"finish_reason":"stop"}]}"""
                        (JsonSerializer.Serialize(borderlineContent))

                let port35b, d35b = startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen35b"}]}""")
                    else
                        do! ctx.Response.WriteAsync(borderlineBody)
                })
                let port122b, d122b = startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen122b"}]}""")
                    else
                        do! ctx.Response.WriteAsync("""{"choices":[{"message":{"content":"122B"}}]}""")
                })

                let yesBody = """{"choices":[{"message":{"content":"ROUTE_YES"}}]}"""
                let bodyRef = ref yesBody
                let judgeStub = new CountingJudgeHandler(bodyRef)

                try
                    let httpClient, app, _routerPort =
                        startTestRouterWithJudge port35b port122b tempDir promptPath judgeStub
                    try
                        let body = """{"messages":[{"role":"user","content":"explain quickly"}],"stream":false}"""
                        httpClient.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult() |> ignore  // miss
                        httpClient.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult() |> ignore  // hit

                        Thread.Sleep(200) // flush

                        let statsResp = httpClient.GetAsync("/stats").GetAwaiter().GetResult()
                        Expect.equal statsResp.StatusCode HttpStatusCode.OK "/stats 200 OK"
                        let statsBody = statsResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        use doc = JsonDocument.Parse(statsBody)
                        let root = doc.RootElement
                        Expect.equal (root.GetProperty("judge_cache_hits").GetInt64()) 1L "judge_cache_hits = 1 after one cache hit"
                        Expect.equal (root.GetProperty("judge_cache_misses").GetInt64()) 1L "judge_cache_misses = 1 after one miss"
                        Expect.equal (root.GetProperty("judge_call_count").GetInt64()) 1L "judge_call_count = 1 (HTTP fired exactly once)"
                    finally
                        try httpClient.Dispose() with _ -> ()
                        try app.StopAsync().GetAwaiter().GetResult() with _ -> ()
                        try (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() with _ -> ()
                finally
                    d35b.Dispose() ; d122b.Dispose()
            finally
                try Directory.Delete(tempDir, true) with _ -> ()

        // ── Router-level integration (combines JDG-04 + JDG-05) ────────────────

        testCase "JDG-04+05 fake-Kestrel: borderline 35B → judge fires → trace+/stats observe consistent fields" <| fun _ ->
            // Combines JDG-04 (judge HTTP counter behavior) + JDG-05 (trace JSONL + /stats fields).
            // Drives ONE borderline request, asserts ALL observable surfaces in one go:
            //   - judge HTTP counter = 1
            //   - trace JSONL: judge_called=true, judge_verdict="yes", judge_latency_ms is a non-negative float
            //   - /stats: judge_call_count=1, judge_cache_misses=1, judge_cache_hits=0
            // schema_version = 1 (unchanged — additive).
            let tempDir = Path.Combine(Path.GetTempPath(), "judge-04-05-combined-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir) |> ignore
            let promptPath = Path.Combine(tempDir, "judge-prompt.md")
            File.WriteAllText(promptPath, "Q: {{QUESTION}}\nR: {{RESPONSE}}\nAnswer: ROUTE_YES or ROUTE_NO")
            try
                let borderlineContent = mkHighEntropyText 35
                let borderlineBody =
                    sprintf """{"id":"chatcmpl-35b","choices":[{"message":{"role":"assistant","content":%s},"finish_reason":"stop"}]}"""
                        (JsonSerializer.Serialize(borderlineContent))

                let port35b, d35b = startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen35b"}]}""")
                    else
                        do! ctx.Response.WriteAsync(borderlineBody)
                })
                let port122b, d122b = startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen122b"}]}""")
                    else
                        do! ctx.Response.WriteAsync("""{"choices":[{"message":{"content":"122B"}}]}""")
                })

                let yesBody = """{"choices":[{"message":{"content":"ROUTE_YES"}}]}"""
                let bodyRef = ref yesBody
                let judgeStub = new CountingJudgeHandler(bodyRef)

                try
                    let httpClient, app, _routerPort =
                        startTestRouterWithJudge port35b port122b tempDir promptPath judgeStub
                    let traceDir = Path.Combine(tempDir, "logs", "trace")
                    try
                        let body = """{"messages":[{"role":"user","content":"explain quickly"}],"stream":false}"""
                        let resp =
                            httpClient.PostAsync(
                                "/v1/chat/completions",
                                new StringContent(body, Encoding.UTF8, "application/json"))
                                .GetAwaiter().GetResult()
                        Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK"

                        Thread.Sleep(500) // flush

                        // 1. Judge HTTP counter
                        Expect.equal judgeStub.CallCount 1 "judge HTTP counter = 1 (one borderline request)"

                        // 2. Trace JSONL row
                        let today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
                        let tracePath = Path.Combine(traceDir, today + ".jsonl")
                        Expect.isTrue (File.Exists(tracePath)) "trace file exists"
                        let line = File.ReadAllLines(tracePath) |> Array.last
                        use doc = JsonDocument.Parse(line)
                        let root = doc.RootElement
                        Expect.equal (root.GetProperty("schema_version").GetInt32()) 1 "schema_version still 1"
                        Expect.isTrue (root.GetProperty("judge_called").GetBoolean()) "judge_called=true"
                        Expect.equal (root.GetProperty("judge_verdict").GetString()) "yes" "judge_verdict=yes (ROUTE_YES)"
                        let lat = root.GetProperty("judge_latency_ms").GetDouble()
                        Expect.isTrue (lat >= 0.0) (sprintf "judge_latency_ms is a non-negative float (got %f)" lat)

                        // 3. /stats wire
                        let statsResp = httpClient.GetAsync("/stats").GetAwaiter().GetResult()
                        let statsBody = statsResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        use sdoc = JsonDocument.Parse(statsBody)
                        let sroot = sdoc.RootElement
                        Expect.equal (sroot.GetProperty("judge_cache_hits").GetInt64()) 0L "judge_cache_hits=0 (single request, no prior cache)"
                        Expect.equal (sroot.GetProperty("judge_cache_misses").GetInt64()) 1L "judge_cache_misses=1"
                        Expect.equal (sroot.GetProperty("judge_call_count").GetInt64()) 1L "judge_call_count=1"
                    finally
                        try httpClient.Dispose() with _ -> ()
                        try app.StopAsync().GetAwaiter().GetResult() with _ -> ()
                        try (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() with _ -> ()
                finally
                    d35b.Dispose() ; d122b.Dispose()
            finally
                try Directory.Delete(tempDir, true) with _ -> ()
    ]
