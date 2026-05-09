module SmartRouter.Tests.LoggingTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
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
open Serilog
open Serilog.Core
open Serilog.Events

// ── In-memory Serilog sink ────────────────────────────────────────────────────

/// Thread-safe in-memory Serilog sink for test assertions.
/// The Serilog Console sink captures the stderr TextWriter reference at
/// construction time, so post-hoc redirection of stderr has no effect.
/// Using ILogEventSink directly avoids that pitfall and gives synchronous access
/// to captured log events without any I/O indirection.
type private CapturingSink() =
    let events  = ResizeArray<LogEvent>()
    let lockObj = obj ()

    member _.Snapshot() : LogEvent list =
        lock lockObj (fun () -> events |> List.ofSeq)

    member _.Clear() =
        lock lockObj (fun () -> events.Clear())

    interface ILogEventSink with
        member _.Emit(e: LogEvent) =
            lock lockObj (fun () -> events.Add(e))

/// Module-level sink shared across all tests in this module.
/// The testList is sequenced (not parallel) so the single shared instance is safe.
/// Each test calls capturedSink.Clear().
let private capturedSink = CapturingSink()

// ── Helpers ───────────────────────────────────────────────────────────────────

/// Create an isolated temp directory for a single test's JSONL files.
let private mkTempLogDir () : string =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-tests-" + Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    dir

/// Remove an isolated temp log directory; best-effort (no failure on race).
let private cleanupLogDir (dir: string) =
    try
        if Directory.Exists(dir) then Directory.Delete(dir, recursive = true)
    with _ -> ()

/// Return the expected JSONL file path for today's UTC date.
/// Compute ONCE at test start and pass around; do NOT call repeatedly inside a test
/// (avoids midnight-UTC edge case where the date rolls between calls).
let private todaysLogFile (dir: string) : string =
    Path.Combine(dir, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl")

// ── Fake upstream helpers ─────────────────────────────────────────────────────

/// Spin up a minimal fake upstream that returns HTTP 200 for all chat requests.
/// GET /v1/models returns a single-entry data array so QwenUpstreamClient's lazy
/// probe succeeds and does not raise ModelUnavailable.
let private startFakeUpstream () : Task<WebApplication * int> =
    task {
        let b = WebApplication.CreateBuilder()
        b.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        let app = b.Build()

        app.MapGet("/v1/models",
            Func<IResult>(fun () ->
                Results.Json({| data = [| {| id = "/local/qwen35b" |} |] |})))
        |> ignore

        app.MapPost("/v1/chat/completions",
            Func<HttpContext, Task>(fun ctx ->
                task {
                    ctx.Response.StatusCode  <- 200
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync("""{"id":"x","choices":[]}""")
                }))
        |> ignore

        do! app.StartAsync()

        let port =
            app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                .Addresses
            |> Seq.head
            |> fun a -> a.Split(':') |> Array.last |> int

        return app, port
    }

/// Fake upstream for Test 5 (SSE error path).
/// Returns HTTP 502 for POST /v1/chat/completions.
/// QwenUpstreamClient.StreamAsync checks resp.IsSuccessStatusCode and yields
///   Error (ModelUnavailable(...))
/// which causes the ChatCompletions streaming handler's Error arm to fire and
/// emit `data: {"error":{"message":"...","type":"upstream_error","correlation_id":"<cid>"}}\n\n`
/// (LOG-04 / OBS-03).
let private startFakeStreamingErrorUpstream () : Task<WebApplication * int> =
    task {
        let b = WebApplication.CreateBuilder()
        b.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        let app = b.Build()

        app.MapGet("/v1/models",
            Func<IResult>(fun () ->
                Results.Json({| data = [| {| id = "/local/qwen35b" |} |] |})))
        |> ignore

        app.MapPost("/v1/chat/completions",
            Func<HttpContext, Task>(fun ctx ->
                task {
                    // HTTP 502 — QwenUpstreamClient.StreamAsync detects non-2xx and yields
                    // Error (ModelUnavailable(...)), triggering the streaming error arm.
                    ctx.Response.StatusCode  <- 502
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync("""{"error":"bad gateway"}""")
                }))
        |> ignore

        do! app.StartAsync()

        let port =
            app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                .Addresses
            |> Seq.head
            |> fun a -> a.Split(':') |> Array.last |> int

        return app, port
    }

// ── Router helper ─────────────────────────────────────────────────────────────

/// Build and start an in-process router pointing at the given fake upstream port.
///
/// CRITICAL ordering (mirrors StreamingTests.startTestRouter):
///   1. CreateBuilder + UseUrls
///   2. AddInMemoryCollection (BEFORE configureServices — last-write-wins config layering)
///   3. Install CapturingSink as global Log.Logger + wire UseSerilog() BEFORE configureServices
///   4. configureServices — reads config and registers all DI singletons
///   5. Build + mapEndpoints + StartAsync
///
/// The CapturingSink receives every Serilog event, including those enriched by
/// CorrelationMiddleware's LogContext.PushProperty("correlation_id", cid).
let private startTestRouter (fakePort: int) (logDir: string) : Task<WebApplication * int> =
    task {
        capturedSink.Clear()

        let testBuilder = WebApplication.CreateBuilder()
        testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore

        // Configuration MUST be injected before configureWithoutMl reads it.
        (testBuilder.Configuration :> IConfigurationBuilder)
            .AddInMemoryCollection([
                KeyValuePair("Upstreams:Model35B",  sprintf "http://127.0.0.1:%d" fakePort)
                KeyValuePair("Upstreams:Model122B", sprintf "http://127.0.0.1:%d" fakePort)
                // Routing section — required for buildRoutingConfig + validateConfig to succeed
                KeyValuePair("Routing:TimeoutSeconds",       "300")
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
                // Queue section — required for QueueDispatcherOptions
                KeyValuePair("Queue:FairnessK",                "10")
                KeyValuePair("Queue:MaxConcurrent122B",        "1")
                KeyValuePair("Queue:PerRequestTimeoutSeconds", "300")
                // Decision log — write to the per-test isolated temp directory
                KeyValuePair("DecisionLog:Directory",       logDir)
                KeyValuePair("DecisionLog:ChannelCapacity", "10000")
            ])
        |> ignore

        // Install the in-memory Serilog sink BEFORE configureWithoutMl fires.
        // Logging.configure() is NOT called here — that would clobber the test sink.
        // testBuilder.Host.UseSerilog() (no-arg overload) reads from the static Log.Logger.
        let testLogger =
            LoggerConfiguration()
                .Enrich.FromLogContext()          // captures correlation_id pushed by CorrelationMiddleware
                .MinimumLevel.Verbose()
                .WriteTo.Sink(capturedSink :> ILogEventSink)
                .CreateLogger()
        Log.Logger <- testLogger
        testBuilder.Host.UseSerilog() |> ignore

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
            System.Func<System.IServiceProvider, SmartRouter.Core.Domain.RoutingAlgorithm>(fun sp ->
                sp.GetRequiredService<SmartRouter.Cli.Adapters.RoutingAlgorithm.RoutingAlgorithmRegistration>().Algorithm))
        |> ignore

        // IHealthProbe — stub (always reachable); required by QueueDispatcher ctor.
        let alwaysReachable =
            { new SmartRouter.Core.Ports.IHealthProbe with
                member _.IsReachable(_target) = true
                member _.IsReachableAsync _target _ct = System.Threading.Tasks.Task.FromResult(true)
                member _.LastProbedAt(_target) = System.DateTimeOffset.MinValue }

        testBuilder.Services.AddSingleton<SmartRouter.Core.Ports.IHealthProbe>(alwaysReachable)
        |> ignore

        // QueueDispatcherOptions binding — required by QueueDispatcher ctor (MaxConcurrent122B validation).
        testBuilder.Services.Configure<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcherOptions>(
            testBuilder.Configuration.GetSection("Queue"))
        |> ignore

        // QwenUpstreamClient + QueueDispatcher — required by ChatCompletions handler.
        testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient>(fun sp ->
            SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient(
                sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SmartRouter.Cli.Adapters.QwenUpstreamClient.UpstreamOptions>>()))
        |> ignore

        testBuilder.Services.AddSingleton<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcher>(fun sp ->
            SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcher(
                sp.GetRequiredService<SmartRouter.Cli.Adapters.QwenUpstreamClient.QwenUpstreamClient>()
                    :> SmartRouter.Core.Ports.IUpstreamClient,
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SmartRouter.Cli.Adapters.QueueDispatcher.QueueDispatcherOptions>>().Value,
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

        // Correlation middleware must run first in the pipeline (mirrors Program.fs).
        app.Use(
            System.Func<HttpContext, RequestDelegate, System.Threading.Tasks.Task>(
                fun ctx next ->
                    SmartRouter.Cli.Adapters.CorrelationMiddleware.correlationMiddleware ctx next))
        |> ignore

        SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints app

        do! app.StartAsync()

        let routerPort =
            app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                .Addresses
            |> Seq.head
            |> fun a -> a.Split(':') |> Array.last |> int

        return app, routerPort
    }

// ── Teardown ──────────────────────────────────────────────────────────────────

/// Stop and dispose both apps, then remove the temp log directory.
/// Called from finally blocks — synchronous; no do! allowed in finally.
let private teardown (routerApp: WebApplication) (fakeApp: WebApplication) (logDir: string) =
    try routerApp.StopAsync().GetAwaiter().GetResult() with _ -> ()
    try fakeApp.StopAsync().GetAwaiter().GetResult()   with _ -> ()
    try (routerApp :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() with _ -> ()
    try (fakeApp   :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() with _ -> ()
    cleanupLogDir logDir

// ── Request helper ────────────────────────────────────────────────────────────

/// POST one non-streaming request to the router's /v1/chat/completions endpoint.
let private postOnce (client: HttpClient) (port: int) : Task<HttpResponseMessage> =
    let url = sprintf "http://127.0.0.1:%d/v1/chat/completions" port
    let req = new HttpRequestMessage(HttpMethod.Post, url)
    req.Content <-
        new StringContent(
            """{"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8,
            "application/json")
    client.SendAsync(req)

// ── Poll helper ───────────────────────────────────────────────────────────────

/// Poll the JSONL file until it contains at least `expected` non-blank lines
/// or the timeout elapses.  Returns the actual line count.
/// Uses 50 ms sleep between polls so the BackgroundService consumer has time to drain.
let private waitForLineCount (path: string) (expected: int) (timeoutMs: int) : Task<int> =
    task {
        let sw    = Stopwatch.StartNew()
        let mutable count = 0
        while count < expected && sw.ElapsedMilliseconds < int64 timeoutMs do
            if File.Exists(path) then
                count <-
                    File.ReadAllLines(path)
                    |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace(l)))
                    |> Array.length
            if count < expected then
                do! Task.Delay(50)
        return count
    }

// ── Tests ─────────────────────────────────────────────────────────────────────

let tests : Test =
    testSequenced <| testList "LoggingTests" [

        // ── Test 1: Schema completeness (LOG-01 / OBS-01) ─────────────────────
        // Send one request; parse the resulting JSONL line; assert all 12 fields
        // are present and have the correct primitive types.
        testCase "JSONL line has all 12 schema fields with correct types" <| fun () ->
            (task {
                let logDir   = mkTempLogDir ()
                let logFile  = todaysLogFile logDir
                let! fakeApp,   fakePort   = startFakeUpstream ()
                let! routerApp, routerPort = startTestRouter fakePort logDir
                try
                    use client = new HttpClient()
                    use! response = postOnce client routerPort
                    Expect.equal (int response.StatusCode) 200 "router returned 200"

                    let! count = waitForLineCount logFile 1 5000
                    Expect.equal count 1 "exactly 1 JSONL line written"

                    let line = File.ReadAllLines(logFile) |> Array.head
                    use doc  = JsonDocument.Parse(line)
                    let root = doc.RootElement

                    // Field 1: schema_version (int)
                    Expect.equal (root.GetProperty("schema_version").GetInt32()) 1
                                  "schema_version = 1"

                    // Field 2: correlation_id (non-empty string)
                    Expect.isFalse
                        (String.IsNullOrEmpty(root.GetProperty("correlation_id").GetString()))
                        "correlation_id is non-empty"

                    // Field 3: prompt_hash (64-char SHA-256 hex)
                    Expect.equal (root.GetProperty("prompt_hash").GetString().Length) 64
                                  "prompt_hash is a 64-char hex string"

                    // Field 4: prompt_korean_char_ratio (float in [0,1])
                    let ratio = root.GetProperty("prompt_korean_char_ratio").GetDouble()
                    Expect.isTrue (ratio >= 0.0 && ratio <= 1.0)
                                   "prompt_korean_char_ratio in [0.0, 1.0]"

                    // Field 5: routing_algorithm (string)
                    Expect.equal (root.GetProperty("routing_algorithm").GetString())
                                  "ml"
                                  "routing_algorithm = ml (test-stub registration)"

                    // Field 6: routing_reason (non-empty string)
                    Expect.isFalse
                        (String.IsNullOrEmpty(root.GetProperty("routing_reason").GetString()))
                        "routing_reason is non-empty"

                    // Field 7: target (Qwen35B or Qwen122B)
                    let target = root.GetProperty("target").GetString()
                    Expect.isTrue (target = "Qwen35B" || target = "Qwen122B")
                                   "target is one of {Qwen35B, Qwen122B}"

                    // Field 8: latency_ms (float >= 0)
                    Expect.isTrue (root.GetProperty("latency_ms").GetDouble() >= 0.0)
                                   "latency_ms >= 0.0"

                    // Field 9: fallback_used (bool)
                    Expect.isFalse (root.GetProperty("fallback_used").GetBoolean())
                                    "fallback_used = false in Phase 5 (no fallback wired)"

                    // Field 10: model_version (string)
                    Expect.equal (root.GetProperty("model_version").GetString())
                                  "test-stub"
                                  "model_version = test-stub (test-stub registration)"

                    // Field 11: task_type (nullable string — property must exist; null is OK)
                    Expect.isTrue (root.TryGetProperty("task_type") |> fst)
                                   "task_type property present (may be null)"

                    // Field 12: timestamp (parseable ISO-8601 string)
                    let ts     = root.GetProperty("timestamp").GetString()
                    let parsed, _ = DateTimeOffset.TryParse(ts)
                    Expect.isTrue parsed (sprintf "timestamp '%s' parses as ISO 8601" ts)
                finally
                    teardown routerApp fakeApp logDir
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // ── Test 2: 100-concurrent thread safety (LOG-02) ─────────────────────
        // Fire 100 parallel requests; assert the JSONL file has exactly 100 lines
        // that each parse as valid JSON and carry distinct correlation_ids.
        testCase "100 concurrent requests produce 100 valid JSON lines" <| fun () ->
            (task {
                let logDir   = mkTempLogDir ()
                let logFile  = todaysLogFile logDir
                let! fakeApp,   fakePort   = startFakeUpstream ()
                let! routerApp, routerPort = startTestRouter fakePort logDir
                try
                    use client = new HttpClient()
                    let tasks =
                        [| for _ in 1 .. 100 -> postOnce client routerPort |]
                    let! responses = Task.WhenAll(tasks)

                    let okCount =
                        responses
                        |> Array.filter (fun r -> int r.StatusCode = 200)
                        |> Array.length
                    Expect.equal okCount 100 "all 100 responses returned HTTP 200"

                    let! count = waitForLineCount logFile 100 15000
                    Expect.equal count 100 "exactly 100 JSONL lines written"

                    // Every line must parse as valid JSON — proves no interleaved bytes.
                    let lines =
                        File.ReadAllLines(logFile)
                        |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace(l)))
                    for l in lines do
                        try use _ = JsonDocument.Parse(l) in ()
                        with ex ->
                            failtestf "JSONL line did not parse as JSON:\n%s\n%s" l ex.Message

                    // All 100 correlation_ids must be distinct.
                    let cids =
                        lines
                        |> Array.map (fun l ->
                            use doc = JsonDocument.Parse(l)
                            doc.RootElement.GetProperty("correlation_id").GetString())
                        |> Array.distinct
                    Expect.equal cids.Length 100 "100 distinct correlation_ids (no duplicates)"
                finally
                    teardown routerApp fakeApp logDir
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // ── Test 3: Correlation ID propagation — Serilog in-memory sink + JSONL (LOG-04) ──
        // Sends one request; reads the JSONL correlation_id; scans the CapturingSink
        // for a LogEvent whose Properties["correlation_id"] matches.
        // The CapturingSink is synchronous — by the time client.SendAsync returns,
        // every Serilog event for the request has been emitted.  NO Task.Delay needed.
        testCase "correlation_id in JSONL matches correlation_id in Serilog LogEvents" <| fun () ->
            (task {
                capturedSink.Clear()
                let logDir   = mkTempLogDir ()
                let logFile  = todaysLogFile logDir
                let! fakeApp,   fakePort   = startFakeUpstream ()
                let! routerApp, routerPort = startTestRouter fakePort logDir
                try
                    use client = new HttpClient()
                    use! response = postOnce client routerPort
                    Expect.equal (int response.StatusCode) 200 "router returned 200"

                    let! count = waitForLineCount logFile 1 5000
                    Expect.equal count 1 "1 JSONL line written"

                    let line      = File.ReadAllLines(logFile) |> Array.head
                    use doc       = JsonDocument.Parse(line)
                    let jsonlCid  = doc.RootElement.GetProperty("correlation_id").GetString()

                    // CapturingSink.Emit is synchronous; the response has returned, so every
                    // Serilog event for this request is already in the snapshot.
                    let events = capturedSink.Snapshot()

                    // CorrelationMiddleware.correlationMiddleware calls
                    //   LogContext.PushProperty("correlation_id", cid)
                    // and the testLogger is configured with .Enrich.FromLogContext() so the
                    // property is promoted to LogEvent.Properties for all events in scope.
                    // ScalarValue.ToString() includes surrounding quotes — strip them.
                    let matched =
                        events
                        |> List.exists (fun e ->
                            match e.Properties.TryGetValue("correlation_id") with
                            | true, v -> v.ToString().Trim('"') = jsonlCid
                            | _       -> false)

                    Expect.isTrue matched
                        (sprintf
                            "expected a Serilog LogEvent with correlation_id=%s in %d captured events"
                            jsonlCid
                            (List.length events))
                finally
                    teardown routerApp fakeApp logDir
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // ── Test 4: Graceful shutdown drain (LOG-03) ──────────────────────────
        // Fire 10 requests, await responses (DecisionLog enqueue is fire-and-forget
        // relative to the HTTP response), then immediately call StopAsync.
        // BackgroundService.StopAsync must drain the channel before stopping.
        // All 10 entries must be present in the JSONL file after StopAsync returns.
        testCase "graceful shutdown drains channel — no in-flight log loss" <| fun () ->
            (task {
                let logDir  = mkTempLogDir ()
                let logFile = todaysLogFile logDir
                let! fakeApp,   fakePort   = startFakeUpstream ()
                let! routerApp, routerPort = startTestRouter fakePort logDir
                try
                    use client = new HttpClient()
                    let tasks =
                        [| for _ in 1 .. 10 -> postOnce client routerPort |]
                    let! _ = Task.WhenAll(tasks)

                    // Stop immediately — StopAsync must drain in-flight channel entries.
                    do! routerApp.StopAsync()

                    let lines =
                        if File.Exists(logFile) then
                            File.ReadAllLines(logFile)
                            |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace(l)))
                        else
                            [||]

                    Expect.equal lines.Length 10
                        "graceful shutdown drained all 10 in-flight DecisionLog entries"
                finally
                    teardown routerApp fakeApp logDir
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // ── Test 5: SSE error event body contains correlation_id (LOG-04 / OBS-03) ──
        // A malformed fake upstream triggers the streaming error arm in ChatCompletions.fs
        // which emits `data: {"error":{...,"correlation_id":"<cid>"}}\n\n`.
        // Test reads the response body, locates that frame, parses the JSON, and asserts
        // the correlation_id matches both the JSONL DecisionLog and the Serilog sink.
        testCase "SSE error event body contains correlation_id matching JSONL + Serilog" <| fun () ->
            (task {
                capturedSink.Clear()
                let logDir  = mkTempLogDir ()
                let logFile = todaysLogFile logDir
                let! fakeApp,   fakePort   = startFakeStreamingErrorUpstream ()
                let! routerApp, routerPort = startTestRouter fakePort logDir
                try
                    use client = new HttpClient()
                    let url = sprintf "http://127.0.0.1:%d/v1/chat/completions" routerPort
                    let req = new HttpRequestMessage(HttpMethod.Post, url)
                    // stream=true routes through the SSE streaming branch.
                    req.Content <-
                        new StringContent(
                            """{"messages":[{"role":"user","content":"hi"}],"stream":true}""",
                            Encoding.UTF8,
                            "application/json")
                    use! response = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                    let! body = response.Content.ReadAsStringAsync()

                    // Locate the SSE error frame: a "data: " line whose payload is JSON
                    // containing an "error" property.
                    let dataLines =
                        body.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.filter (fun l -> l.StartsWith("data: "))

                    let errElement =
                        dataLines
                        |> Array.tryPick (fun l ->
                            let payload = l.Substring(6)   // strip "data: "
                            try
                                use d = JsonDocument.Parse(payload)
                                let mutable errEl = Unchecked.defaultof<JsonElement>
                                if d.RootElement.TryGetProperty("error", &errEl)
                                then Some (errEl.Clone())   // Clone needed: doc disposed at end of try block
                                else None
                            with _ -> None)

                    let errEl =
                        match errElement with
                        | Some e -> e
                        | None   -> failtestf "no SSE error frame found in body:\n%s" body

                    // The error object must contain correlation_id.
                    let mutable cidProp = Unchecked.defaultof<JsonElement>
                    Expect.isTrue (errEl.TryGetProperty("correlation_id", &cidProp))
                        "SSE error event must contain correlation_id (LOG-04 / OBS-03)"

                    let sseCid = cidProp.GetString()
                    Expect.isFalse (String.IsNullOrEmpty(sseCid)) "SSE correlation_id is non-empty"

                    // The JSONL DecisionLog for this request must carry the same correlation_id.
                    let! _ = waitForLineCount logFile 1 5000
                    let line     = File.ReadAllLines(logFile) |> Array.head
                    use doc      = JsonDocument.Parse(line)
                    let jsonlCid = doc.RootElement.GetProperty("correlation_id").GetString()
                    Expect.equal sseCid jsonlCid
                        "SSE error correlation_id must match JSONL correlation_id"

                    // The in-memory Serilog sink must also contain the same correlation_id.
                    let events  = capturedSink.Snapshot()
                    let matched =
                        events
                        |> List.exists (fun e ->
                            match e.Properties.TryGetValue("correlation_id") with
                            | true, v -> v.ToString().Trim('"') = sseCid
                            | _       -> false)
                    Expect.isTrue matched
                        (sprintf "Serilog sink must capture correlation_id=%s" sseCid)
                finally
                    teardown routerApp fakeApp logDir
            } |> Async.AwaitTask |> Async.RunSynchronously)

    ]
