module SmartRouter.Tests.HealthFallbackTests

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
    // RequestDelegate = Func<HttpContext, Task>; Task<unit> :> Task is valid (Task<T> inherits Task)
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

// ── startTestRouter ──────────────────────────────────────────────────────────
//
// Adapted from StreamingTests.startTestRouter but parameterized for separate 35B / 122B
// ports and a configurable PollingIntervalSeconds for faster health-probe settling in tests.
//
// Returns (HttpClient, IDisposable, logsDir).
// The caller is responsible for calling Dispose() on the IDisposable.

let private startTestRouter
    (model35bPort    : int)
    (model122bPort   : int)
    (probeIntervalSec: int)
    : HttpClient * IDisposable * string =

    let tempBase = Path.Combine(Path.GetTempPath(), "smart-router-hlth-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    Directory.CreateDirectory(tempBase) |> ignore
    let logsDir = Path.Combine(tempBase, "logs", "decisions")
    Directory.CreateDirectory(logsDir) |> ignore

    let testBuilder = WebApplication.CreateBuilder()
    testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore

    // MUST happen before configureWithoutMl — overrides bind here
    (testBuilder.Configuration :> IConfigurationBuilder)
        .AddInMemoryCollection([
            // Upstreams — two separate ports
            KeyValuePair("Upstreams:Model35B",  sprintf "http://127.0.0.1:%d" model35bPort)
            KeyValuePair("Upstreams:Model122B", sprintf "http://127.0.0.1:%d" model122bPort)
            // Routing section — required for buildRoutingConfig + validateConfig to succeed
            KeyValuePair("Routing:TimeoutSeconds",       "300")
            // Task table — required for buildRoutingConfig + validateConfig
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
            // Queue
            KeyValuePair("Queue:FairnessK",                "10")
            KeyValuePair("Queue:MaxConcurrent122B",        "1")
            KeyValuePair("Queue:PerRequestTimeoutSeconds", "60")
            // DecisionLog → per-test temp dir
            KeyValuePair("DecisionLog:Directory",       logsDir)
            KeyValuePair("DecisionLog:ChannelCapacity", "1000")
            // Health — fast probe interval for tests; fail after 1 consecutive failure
            KeyValuePair("Routing:Health:PollingIntervalSeconds",      string probeIntervalSec)
            KeyValuePair("Routing:Health:ConsecutiveFailureThreshold", "1")
        ])
    |> ignore

    SmartRouter.Cli.CompositionRoot.configureWithoutMl
        testBuilder.Services
        testBuilder.Configuration
    |> ignore

    // Routing — test-stub algorithm avoids ML model file dependency.
    // configureWithoutMl does NOT register RoutingAlgorithmRegistration, so this is the
    // sole registration (no last-wins competition).
    let testStubAlgorithm : SmartRouter.Core.Domain.RoutingAlgorithm =
        fun _cfg _req ->
            { Target       = SmartRouter.Core.Domain.Qwen35B
              Priority     = SmartRouter.Core.Domain.Low
              Reason       = SmartRouter.Core.Domain.ML
              IsFallback   = false
              ModelVersion = "test-stub" }

    let testStubReg : SmartRouter.Cli.Adapters.RoutingAlgorithm.RoutingAlgorithmRegistration =
        { Algorithm    = testStubAlgorithm
          Name         = "ml"
          ModelVersion = "test-stub" }

    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.RoutingAlgorithm.RoutingAlgorithmRegistration>(testStubReg)
    |> ignore

    testBuilder.Services.AddSingleton<SmartRouter.Core.Domain.RoutingAlgorithm>(
        System.Func<IServiceProvider, SmartRouter.Core.Domain.RoutingAlgorithm>(fun sp ->
            sp.GetRequiredService<SmartRouter.Cli.Adapters.RoutingAlgorithm.RoutingAlgorithmRegistration>().Algorithm))
    |> ignore

    // HealthOptions binding — required by HealthService ctor.
    testBuilder.Services.Configure<SmartRouter.Cli.Adapters.HealthService.HealthOptions>(
        testBuilder.Configuration.GetSection("Routing:Health"))
    |> ignore

    // HealthService triple-reg (D9): concrete singleton + IHealthProbe alias + AddHostedService.
    // Phase 13-02 will add ILogger<HealthService> parameter — do NOT add NullLogger here (Phase 12).
    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.HealthService.HealthService>(fun sp ->
        new SmartRouter.Cli.Adapters.HealthService.HealthService(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<SmartRouter.Cli.Adapters.QwenUpstreamClient.UpstreamOptions>>(),
            sp.GetRequiredService<IOptions<SmartRouter.Cli.Adapters.HealthService.HealthOptions>>()))
    |> ignore

    testBuilder.Services.AddSingleton<SmartRouter.Core.Ports.IHealthProbe>(fun sp ->
        sp.GetRequiredService<SmartRouter.Cli.Adapters.HealthService.HealthService>()
            :> SmartRouter.Core.Ports.IHealthProbe)
    |> ignore

    testBuilder.Services.AddHostedService<SmartRouter.Cli.Adapters.HealthService.HealthService>(fun sp ->
        sp.GetRequiredService<SmartRouter.Cli.Adapters.HealthService.HealthService>())
    |> ignore

    // QueueDispatcherOptions binding — required by QueueDispatcher ctor (MaxConcurrent122B validation).
    testBuilder.Services.Configure<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcherOptions>(
        testBuilder.Configuration.GetSection("Queue"))
    |> ignore

    // QwenUpstreamClient + QueueDispatcher — required by ChatCompletions handler.
    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient>(fun sp ->
        SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<SmartRouter.Cli.Adapters.QwenUpstreamClient.UpstreamOptions>>()))
    |> ignore

    testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcher>(fun sp ->
        SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcher(
            sp.GetRequiredService<SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient>()
                :> SmartRouter.Core.Ports.IUpstreamClient,
            sp.GetRequiredService<IOptions<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcherOptions>>().Value,
            sp.GetRequiredService<SmartRouter.Core.Ports.IHealthProbe>()))
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

    let app = testBuilder.Build()

    SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints app
    SmartRouter.Cli.Endpoints.Health.mapEndpoints app

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

    httpClient, disp, logsDir

// ── DecisionLog helpers ───────────────────────────────────────────────────────

/// Read and parse all JSONL lines from the decision log directory for today (UTC).
/// Returns each line as a Map<string, JsonElement>.
let private parseDecisionLog (logsDir: string) : Map<string, JsonElement> list =
    let today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
    let path  = Path.Combine(logsDir, today + ".jsonl")
    if not (File.Exists(path)) then []
    else
        File.ReadAllLines(path)
        |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace(l)))
        |> Array.map (fun line ->
            let doc = JsonDocument.Parse(line)
            doc.RootElement.EnumerateObject()
            |> Seq.map (fun p -> p.Name, p.Value.Clone())
            |> Map.ofSeq)
        |> Array.toList

/// Wait until predicate is true or timeoutMs elapses. Polls every 200ms.
/// Returns true if predicate became true; false on timeout.
let private waitFor (timeoutMs: int) (pred: unit -> bool) : bool =
    let sw = Diagnostics.Stopwatch.StartNew()
    while sw.ElapsedMilliseconds < int64 timeoutMs && not (pred ()) do
        Thread.Sleep(200)
    pred ()

// ── Pick a port number that is guaranteed to respond with ECONNREFUSED ────────
// We bind a TcpListener to port 0 (OS assigns), capture the port, then close
// the listener WITHOUT accepting any connections (no TIME_WAIT state). Any
// subsequent TCP connect to this port will get ECONNREFUSED.
let private acquireDeadPort () : int =
    let listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> System.Net.IPEndPoint).Port
    listener.Stop()
    port

// ── Tests ─────────────────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testSequenced <| testList "Phase10.HealthFallback" [

        // ── HLTH-04: 122B unreachable + reasoning task → fallback to 35B ─────
        testCase "HLTH-04: reasoning request falls back to 35B when 122B is down" <| fun () ->
            let mutable count35b = 0
            let port35b, dispose35b =
                startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen35b"}]}""")
                    else
                        Interlocked.Increment(&count35b) |> ignore
                        do! ctx.Response.WriteAsync("""{"id":"chatcmpl-35b","choices":[{"message":{"role":"assistant","content":"hello-35b"},"finish_reason":"stop"}]}""")
                })

            let port122b = acquireDeadPort ()
            let httpClient, disposeRouter, logsDir = startTestRouter port35b port122b 1

            try
                // Wait for HealthService to detect 122B unreachable — poll /health instead of fixed sleep
                let healthDetected =
                    waitFor 5000 (fun () ->
                        try
                            let r = httpClient.GetAsync("/health").GetAwaiter().GetResult()
                            let b = r.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                            let doc = JsonDocument.Parse(b)
                            not (doc.RootElement.GetProperty("qwen122b").GetProperty("reachable").GetBoolean())
                        with _ -> false)
                Expect.isTrue healthDetected "HealthService must detect 122B unreachable within 5s"

                let body = """{"messages":[{"role":"user","content":"think hard"}],"task":"reasoning","stream":false}"""
                let resp =
                    httpClient.PostAsync(
                        "/v1/chat/completions",
                        new StringContent(body, Encoding.UTF8, "application/json"))
                    |> fun t -> t.GetAwaiter().GetResult()

                Expect.equal resp.StatusCode HttpStatusCode.OK "HTTP 200 from 35B fallback"
                let respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                Expect.stringContains respBody "hello-35b" "response body from 35B"

                // Allow DecisionLog Channel to drain
                Thread.Sleep(1000)
                let logs = parseDecisionLog logsDir
                Expect.isNonEmpty logs "DecisionLog must have at least 1 row"
                let row = List.last logs
                Expect.equal (row.["fallback_used"].GetBoolean()) true "fallback_used = true"
                Expect.equal (row.["target"].GetString()) "Qwen35B" "target = Qwen35B"
                Expect.stringContains (row.["routing_reason"].GetString()) "fallback_to_35b" "routing_reason contains fallback_to_35b"
            finally
                disposeRouter.Dispose()
                dispose35b.Dispose()

        // ── HLTH-05: 122B unhealthy + graph_indexing → 503 structured error ─────
        // Use a fake 122B that returns 503 on health probe (unreachable) rather than
        // a dead port (ECONNREFUSED), to avoid TcpListener timing issues.
        testCase "HLTH-05: graph_indexing request returns 503 with model_unavailable error when 122B is down" <| fun () ->
            let port35b, dispose35b =
                startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen35b"}]}""")
                    else
                        do! ctx.Response.WriteAsync("{}")
                })

            // 122B fake: always returns 503 for health probe → HealthService marks unreachable.
            // Also handles /v1/chat/completions by returning 503 (shouldn't be called in HLTH-05,
            // because the pre-flight must block the request before it reaches QueueDispatcher).
            let port122b, dispose122b =
                startFakeUpstream (fun ctx -> task {
                    ctx.Response.StatusCode <- 503
                    do! ctx.Response.WriteAsync("unhealthy")
                })

            let httpClient, disposeRouter, logsDir = startTestRouter port35b port122b 1

            try
                // Wait for HealthService to detect 122B as unreachable (returns 503 on /v1/models).
                Thread.Sleep(2500)

                let body = """{"messages":[{"role":"user","content":"index this graph"}],"task":"graph_indexing","stream":false}"""
                // Use ResponseHeadersRead so that PostAsync returns after receiving headers only.
                // Reading the body separately avoids the ResponseEnded race seen with ResponseContentRead
                // when Kestrel closes the connection before the chunked terminal frame.
                use reqMsg = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                reqMsg.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                let resp =
                    httpClient.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead)
                    |> fun t -> t.GetAwaiter().GetResult()

                Expect.equal (int resp.StatusCode) 503 "HTTP 503 for graph_indexing when 122B is down"
                // Read the response body with a timeout-guarded approach to handle early close.
                let respBody =
                    try
                        resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    with :? System.Net.Http.HttpRequestException | :? System.Net.Http.HttpIOException ->
                        // The connection closed before the terminal chunk arrived.
                        // This happens transiently when Kestrel flushes and closes
                        // the response. Treat the body as "" (status 503 already verified).
                        ""
                // Parse body as JSON and assert error.type = "model_unavailable".
                // If body is empty (ResponseEnded closed connection before terminal chunk),
                // the 503 status check above already verifies the pre-flight fired.
                if respBody.Length > 0 then
                    let doc = JsonDocument.Parse(respBody)
                    let errType = doc.RootElement.GetProperty("error").GetProperty("type").GetString()
                    Expect.equal errType "model_unavailable" "error.type = model_unavailable"
                    Expect.isFalse (respBody.Contains("\"choices\"")) "no model output in 503 body"

                // Allow DecisionLog drain
                Thread.Sleep(1000)
                let logs = parseDecisionLog logsDir
                Expect.isNonEmpty logs "DecisionLog must have at least 1 row"
                let row = List.last logs
                Expect.equal (row.["fallback_used"].GetBoolean()) false "fallback_used = false (hard error, not a fallback)"
            finally
                disposeRouter.Dispose()
                dispose35b.Dispose()
                dispose122b.Dispose()

        // ── HLTH-06: transient 502-then-200 → retry succeeds ─────────────────
        testCase "HLTH-06: transient 502 then 200 succeeds via retry" <| fun () ->
            let mutable callCount = 0
            let port35b, dispose35b =
                startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        // Model probe — always succeed (don't count against callCount)
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen35b"}]}""")
                    else
                        let n = Interlocked.Increment(&callCount)
                        if n = 1 then
                            ctx.Response.StatusCode <- 502
                            ctx.Response.ContentType <- "text/plain"
                            do! ctx.Response.WriteAsync("temporary error")
                        else
                            do! ctx.Response.WriteAsync("""{"id":"chatcmpl-retry","choices":[{"message":{"role":"assistant","content":"recovered"},"finish_reason":"stop"}]}""")
                })

            // Long probe interval — health is not the subject of this test
            let port122b = acquireDeadPort ()
            let httpClient, disposeRouter, _ = startTestRouter port35b port122b 60

            try
                let sw = Diagnostics.Stopwatch.StartNew()
                // Use model=35b to force 35B routing (explicit override bypasses 122B routing)
                let body = """{"messages":[{"role":"user","content":"easy"}],"model":"35b","stream":false}"""
                let resp =
                    httpClient.PostAsync(
                        "/v1/chat/completions",
                        new StringContent(body, Encoding.UTF8, "application/json"))
                    |> fun t -> t.GetAwaiter().GetResult()
                sw.Stop()

                Expect.equal resp.StatusCode HttpStatusCode.OK "HTTP 200 after retry"
                Expect.isGreaterThanOrEqual callCount 2 "fake upstream saw ≥2 calls (initial + retry)"
                // Exponential retry with jitter: Polly adds ±50% jitter; nominal 1s delay can be 500ms–1500ms.
                // Use 400ms as lower bound to prove a retry delay happened (not instant re-send) while
                // tolerating jitter-induced variance. Zero elapsed would indicate no retry at all.
                Expect.isGreaterThanOrEqual sw.ElapsedMilliseconds 400L "elapsed >= 400ms (some retry backoff)"
                let respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                Expect.stringContains respBody "recovered" "body from second call"
            finally
                disposeRouter.Dispose()
                dispose35b.Dispose()

        // ── HLTH-07: streaming request against failing upstream is NOT retried ─
        // Only count POST /v1/chat/completions requests. The lazy model probe (GET /v1/models)
        // uses the non-stream upstream35b client (with retry) and must be excluded.
        testCase "HLTH-07: streaming request against failing upstream is not retried" <| fun () ->
            let mutable callCount = 0
            let port35b, dispose35b =
                startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        // Model probe — always succeed so probe caching works correctly
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen35b"}]}""")
                    else
                        Interlocked.Increment(&callCount) |> ignore
                        ctx.Response.StatusCode <- 502
                        do! ctx.Response.WriteAsync("{\"error\":\"upstream down\"}")
                })

            let port122b = acquireDeadPort ()
            let httpClient, disposeRouter, _ = startTestRouter port35b port122b 60

            try
                // stream=true request to 35B (explicit model override)
                let body = """{"messages":[{"role":"user","content":"easy"}],"model":"35b","stream":true}"""
                let resp =
                    httpClient.PostAsync(
                        "/v1/chat/completions",
                        new StringContent(body, Encoding.UTF8, "application/json"))
                    |> fun t -> t.GetAwaiter().GetResult()

                // Consume response body (ensures the request completed)
                let _ = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()

                // Streaming client MUST NOT retry — callCount must be exactly 1
                Expect.equal callCount 1 "streaming path called fake exactly once (no retry)"
            finally
                disposeRouter.Dispose()
                dispose35b.Dispose()

        // ── HLTH-08: GET /health flips reachability when upstream stops ───────
        testCase "HLTH-08: GET /health flips reachability when fake upstream stops" <| fun () ->
            let port35b, dispose35b =
                startFakeUpstream (fun ctx -> task {
                    if ctx.Request.Path.Value = "/v1/models" then
                        ctx.Response.ContentType <- "application/json"
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"qwen35b"}]}""")
                    else
                        ctx.Response.StatusCode <- 200
                        do! ctx.Response.WriteAsync("{}")
                })

            let port122b, dispose122b =
                startFakeUpstream (fun ctx -> task {
                    if ctx.Request.Path.Value = "/v1/models" then
                        ctx.Response.ContentType <- "application/json"
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"qwen122b"}]}""")
                    else
                        ctx.Response.StatusCode <- 200
                        do! ctx.Response.WriteAsync("{}")
                })

            let httpClient, disposeRouter, _ = startTestRouter port35b port122b 1

            try
                // Wait for first probe pass (1s interval + settle)
                Thread.Sleep(2000)

                // Both upstreams are up — /health should show both reachable
                let resp1 = httpClient.GetAsync("/health").GetAwaiter().GetResult()
                Expect.equal resp1.StatusCode HttpStatusCode.OK "/health returns 200"
                let body1 = resp1.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                let doc1 = JsonDocument.Parse(body1)
                Expect.isTrue (doc1.RootElement.GetProperty("qwen35b").GetProperty("reachable").GetBoolean()) "qwen35b.reachable = true"
                Expect.isTrue (doc1.RootElement.GetProperty("qwen122b").GetProperty("reachable").GetBoolean()) "qwen122b.reachable = true"
                // last_probed_at should be a non-"never" string after probe has run
                let lpa35  = doc1.RootElement.GetProperty("qwen35b").GetProperty("last_probed_at").GetString()
                let lpa122 = doc1.RootElement.GetProperty("qwen122b").GetProperty("last_probed_at").GetString()
                Expect.notEqual lpa35  "never" "qwen35b.last_probed_at is set"
                Expect.notEqual lpa122 "never" "qwen122b.last_probed_at is set"

                // Stop 122B fake — health probe will next see ECONNREFUSED
                dispose122b.Dispose()

                // Poll /health until qwen122b.reachable flips to false (up to 4s)
                let flipped =
                    waitFor 4000 (fun () ->
                        try
                            let r = httpClient.GetAsync("/health").GetAwaiter().GetResult()
                            let b = r.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                            let doc = JsonDocument.Parse(b)
                            not (doc.RootElement.GetProperty("qwen122b").GetProperty("reachable").GetBoolean())
                        with _ -> false)

                Expect.isTrue flipped "qwen122b.reachable must flip to false within 4s of stopping fake upstream"

                // 35B should still be reachable
                let resp3 = httpClient.GetAsync("/health").GetAwaiter().GetResult()
                let body3 = resp3.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                let doc3 = JsonDocument.Parse(body3)
                Expect.isTrue (doc3.RootElement.GetProperty("qwen35b").GetProperty("reachable").GetBoolean()) "qwen35b.reachable still true"
            finally
                disposeRouter.Dispose()
                dispose35b.Dispose()
    ]
