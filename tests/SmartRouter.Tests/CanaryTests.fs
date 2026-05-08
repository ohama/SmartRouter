module SmartRouter.Tests.CanaryTests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Primitives
open Microsoft.FeatureManagement
open Microsoft.FeatureManagement.FeatureFilters
open Serilog
open Serilog.Core
open Serilog.Events
open Expecto

open SmartRouter.Core.CanaryPorts
open SmartRouter.Cli.Adapters.CanaryGate
open SmartRouter.Cli.Adapters.CanaryState
open SmartRouter.Cli.Adapters.CanaryMetrics

// ── Per-process temp dir for canary test output ──────────────────────────────
//
// Mirrors StreamingTests.streamingTestsLogDir pattern.

let private canaryTestsLogDir =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-canary-" + Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    dir

let private canaryTestsModelDir =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-canary-models-" + Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    dir

// ── CapturingSink (Serilog ILogEventSink) ───────────────────────────────────
//
// Mirrors LoggingTests.fs Phase 5 / RetrainingTests.fs Phase 8 pattern.
// Captures formatted log messages so the auto-rollback test can assert on log content.

type CapturingSink() =
    let messages = ResizeArray<string>()
    let lck      = obj()

    member _.Messages =
        lock lck (fun () -> messages |> List.ofSeq)
    member _.Clear() =
        lock lck (fun () -> messages.Clear())

    interface ILogEventSink with
        member _.Emit(le: LogEvent) =
            // RenderMessage produces the formatted message ("CanaryWatchdog: AUTO-ROLLBACK fired ...").
            let msg = le.RenderMessage()
            lock lck (fun () -> messages.Add(msg))

// ── Mock IVariantFeatureManager + minimal IConfiguration setup for unit gate tests ─

/// Builds a real ContextualTargetingFilter-backed FeatureManagement runtime in-memory
/// for the CANARY-01 unit tests. We do NOT use the AspNetCore HTTP path here — we feed
/// TargetingContext directly into IVariantFeatureManager so the test exercises the same
/// SHA-256 hash machinery that production uses.
let private buildFeatureManager (rolloutPercentage: int) : IVariantFeatureManager =
    let configDict =
        dict [
            "feature_management:feature_flags:0:id",                                                          "Canary" :> obj
            "feature_management:feature_flags:0:enabled",                                                     true :> obj
            "feature_management:feature_flags:0:conditions:client_filters:0:name",                            "Microsoft.Targeting" :> obj
            "feature_management:feature_flags:0:conditions:client_filters:0:parameters:Audience:DefaultRolloutPercentage", rolloutPercentage :> obj
        ]
    let cb = ConfigurationBuilder()
    cb.AddInMemoryCollection(configDict |> Seq.map (fun kv -> KeyValuePair(kv.Key, string kv.Value))) |> ignore
    let cfg = cb.Build() :> IConfiguration

    let services = ServiceCollection()
    services.AddSingleton<IConfiguration>(cfg) |> ignore
    services.AddLogging() |> ignore
    services.AddScopedFeatureManagement() |> ignore
    let provider = services.BuildServiceProvider()
    // Resolve IVariantFeatureManager via a scope (AddScopedFeatureManagement registers scoped).
    let scope = provider.CreateScope()
    scope.ServiceProvider.GetRequiredService<IVariantFeatureManager>()

// ── In-process router builder for integration tests ──────────────────────────
//
// `mlEmbeddingFilesPresent` gates the full integration tests: when true, real bge-m3
// is loaded; when false, integration tests are ptestCase (skipped). The unit tests
// (gate-only) do NOT need embeddings and always run.

let private mlEmbeddingFilesPresent =
    File.Exists "models/embed/bge-m3-int8.onnx"
    && File.Exists "models/embed/sentencepiece.bpe.model"
    && File.Exists "models/router.zip"

let private mlIntegTest name body =
    if mlEmbeddingFilesPresent then testCase name body
    else ptestCase name body

// ── W1 fix: deterministic stable correlation_ids ─────────────────────────────

/// Builds a real ContextualTargetingFilter-backed FeatureManagement runtime in-memory
/// W1 fix: deterministic stable correlation_ids so the binomial CI assertion is bit-stable
/// across CI runs (no flake from Guid.NewGuid()'s non-determinism). Use a fixed seed AND
/// derive each id from the index — seeded byte arrays exercise the same Guid("N") format
/// production uses, which avoids any test-vs-prod path divergence in the targeting filter.
let private mkStableCorrelationIds (n: int) (seed: int) : string[] =
    let rng = Random(seed)
    Array.init n (fun _ ->
        let bytes = Array.zeroCreate<byte> 16
        rng.NextBytes(bytes)
        Guid(bytes).ToString("N"))

// ── CANARY-01 unit tests ─────────────────────────────────────────────────────

let private canary01_statisticalSplit =
    testCase "CANARY-01: 1000 deterministic correlation_ids at 10% rollout — count in [80, 120]" <| fun () ->
        let fm = buildFeatureManager 10
        let stateImpl = CanaryState(10) :> ICanaryState

        // Use a real models/router.zip stand-in (will be created if absent — ensureDummyModel
        // path; we just need ANY existing file for File.Exists). Use the canaryTestsModelDir
        // scratch path with a tiny placeholder.
        let canaryModelPath = Path.Combine(canaryTestsModelDir, "router-canary.zip")
        File.WriteAllText(canaryModelPath, "placeholder")   // File.Exists check passes

        let gate = FeatureManagementCanaryGate(fm, stateImpl, canaryModelPath) :> ICanaryGate

        // W1: SEEDED deterministic correlation_ids — same N across runs => same canaryCount.
        // Seed 42 is arbitrary but FIXED. Changing the seed re-generates a new fixed series
        // (which would still satisfy [80, 120] for any well-distributed RNG, but we lock the
        // seed for bit-stability).
        let cids = mkStableCorrelationIds 1000 42

        let mutable canaryCount = 0
        for cid in cids do
            let isCanary = gate.IsCanaryAsync(cid, CancellationToken.None).GetAwaiter().GetResult()
            if isCanary then canaryCount <- canaryCount + 1

        // Binomial 95% CI for n=1000, p=0.10: roughly [80, 120].
        // Using normal approximation: mean=100, std=sqrt(1000*0.1*0.9)≈9.49; 95% CI ±18.6.
        // Tightening to [80, 120] gives a comfortable margin (rejected only on truly
        // broken gate behavior, e.g., always-true / always-false / non-sticky).
        // With deterministic seed=42, the count is bit-stable across CI runs — no flake.
        Expect.isGreaterThanOrEqual canaryCount 80  (sprintf "canary count %d below lower CI 80 (seed=42)" canaryCount)
        Expect.isLessThanOrEqual canaryCount 120 (sprintf "canary count %d above upper CI 120 (seed=42)" canaryCount)

let private canary01_stickyBucket =
    testCase "CANARY-01: 100 evaluations of same correlation_id return identical bucket" <| fun () ->
        let fm = buildFeatureManager 10
        let stateImpl = CanaryState(10) :> ICanaryState

        let canaryModelPath = Path.Combine(canaryTestsModelDir, "router-canary-sticky.zip")
        File.WriteAllText(canaryModelPath, "placeholder")

        let gate = FeatureManagementCanaryGate(fm, stateImpl, canaryModelPath) :> ICanaryGate

        let cid = Guid.NewGuid().ToString("N")
        let results =
            [ for _ in 1 .. 100 ->
                gate.IsCanaryAsync(cid, CancellationToken.None).GetAwaiter().GetResult() ]
        let distinctCount = results |> List.distinct |> List.length
        Expect.equal distinctCount 1 "All 100 evaluations of the same correlation_id must return the same bucket"

let private canary01_emptyCorrelationIdShortCircuit =
    testCase "CANARY-01: empty correlation_id always returns false (no canary route)" <| fun () ->
        let fm = buildFeatureManager 100   // even at 100% rollout
        let stateImpl = CanaryState(100) :> ICanaryState

        let canaryModelPath = Path.Combine(canaryTestsModelDir, "router-canary-empty.zip")
        File.WriteAllText(canaryModelPath, "placeholder")

        let gate = FeatureManagementCanaryGate(fm, stateImpl, canaryModelPath) :> ICanaryGate

        let result = gate.IsCanaryAsync("", CancellationToken.None).GetAwaiter().GetResult()
        Expect.isFalse result "Empty correlation_id must short-circuit to false (RESEARCH §1.4)"

let private canary01_missingFileShortCircuit =
    testCase "CANARY-01: missing canary file always returns false even at 100% rollout" <| fun () ->
        let fm = buildFeatureManager 100
        let stateImpl = CanaryState(100) :> ICanaryState

        // Path that DEFINITELY does not exist
        let nonexistentPath = Path.Combine(canaryTestsModelDir, "definitely-does-not-exist-" + Guid.NewGuid().ToString("N") + ".zip")
        Expect.isFalse (File.Exists nonexistentPath) "Setup: file must not exist"

        let gate = FeatureManagementCanaryGate(fm, stateImpl, nonexistentPath) :> ICanaryGate

        for _ in 1 .. 50 do
            let cid = Guid.NewGuid().ToString("N")
            let result = gate.IsCanaryAsync(cid, CancellationToken.None).GetAwaiter().GetResult()
            Expect.isFalse result "Missing canary file must short-circuit to false (RESEARCH §4.3, Pitfall 7)"

let private canary01_zeroPercentageShortCircuit =
    testCase "CANARY-01: ICanaryState percentage = 0 always returns false even when feature flag at 100%" <| fun () ->
        let fm = buildFeatureManager 100
        let stateImpl = CanaryState(0) :> ICanaryState   // rolled-back state at construction

        let canaryModelPath = Path.Combine(canaryTestsModelDir, "router-canary-zero.zip")
        File.WriteAllText(canaryModelPath, "placeholder")

        let gate = FeatureManagementCanaryGate(fm, stateImpl, canaryModelPath) :> ICanaryGate

        for _ in 1 .. 50 do
            let cid = Guid.NewGuid().ToString("N")
            let result = gate.IsCanaryAsync(cid, CancellationToken.None).GetAwaiter().GetResult()
            Expect.isFalse result "ICanaryState.GetPercentage = 0 must short-circuit to false (Lock 8 in-memory rollback)"

// ── Fake upstream for integration tests ──────────────────────────────────────
//
// Returns 200 OK by default; configurable to return 5xx for ALL requests
// (auto-rollback test) by switching the failureMode flag.

type FakeUpstreamMode =
    | All200
    | All500
    | CanaryRequests500

let startFakeUpstream (mode: FakeUpstreamMode) : Task<WebApplication * int> =
    task {
        let fakeBuilder = WebApplication.CreateBuilder()
        fakeBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        fakeBuilder.Services.AddRouting() |> ignore
        let fakeApp = fakeBuilder.Build()

        fakeApp.MapGet("/v1/models", Func<IResult>(fun () ->
            Results.Json({| data = [| {| id = "/fake/model" |} |] |}))) |> ignore

        fakeApp.MapPost("/v1/chat/completions", Func<HttpContext, Task>(fun ctx ->
            task {
                let cohortHeader =
                    match ctx.Request.Headers.TryGetValue("X-Test-Cohort") with
                    | true, v when v.Count > 0 -> v.[0]
                    | _ -> ""
                let shouldFail =
                    match mode with
                    | All200             -> false
                    | All500             -> true
                    | CanaryRequests500  -> cohortHeader = "canary"
                if shouldFail then
                    ctx.Response.StatusCode <- 502
                    do! ctx.Response.WriteAsync("upstream simulated failure", ctx.RequestAborted)
                else
                    ctx.Response.ContentType <- "application/json"
                    let body = """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}"""
                    do! ctx.Response.WriteAsync(body, ctx.RequestAborted)
            })) |> ignore

        do! fakeApp.StartAsync()
        let port =
            fakeApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
            |> Seq.head
            |> fun a -> a.Split(':') |> Array.last |> int
        return fakeApp, port
    }

// Build the SmartRouter test harness. Configurable via overrides record.
type CanaryHarnessOverrides = {
    PercentageEnabled            : int
    AutoRollbackEnabled          : bool
    RollingWindowSeconds         : int
    WatchdogPollIntervalSeconds  : int
    AutoRollbackThreshold        : float
    MinBaselineSampleSize        : int
    CanaryModelExists            : bool   // if true, copies models/router.zip to a temp router-canary.zip path
    CapturingSink                : CapturingSink option
}

let private defaultOverrides = {
    PercentageEnabled           = 50
    AutoRollbackEnabled         = false
    RollingWindowSeconds        = 60
    WatchdogPollIntervalSeconds = 10
    AutoRollbackThreshold       = 0.10
    MinBaselineSampleSize       = 50
    CanaryModelExists           = true
    CapturingSink               = None
}

/// In-process Cli WebApplication + fake upstream pointing at fakePort.
/// Returns (routerApp, routerPort, decisionLogDir, canaryModelPath) so tests can
/// assert on JSONL output and clean up files.
let private startCanaryRouter
    (fakePort        : int)
    (overrides       : CanaryHarnessOverrides)
    : Task<WebApplication * int * string * string> =
    task {
        // Per-test temp directories
        let logDir = Path.Combine(canaryTestsLogDir, "test-" + Path.GetRandomFileName())
        Directory.CreateDirectory(logDir) |> ignore
        let modelDir = Path.Combine(canaryTestsModelDir, "test-" + Path.GetRandomFileName())
        Directory.CreateDirectory(modelDir) |> ignore
        let canaryModelPath = Path.Combine(modelDir, "router-canary.zip")

        if overrides.CanaryModelExists && File.Exists "models/router.zip" then
            File.Copy("models/router.zip", canaryModelPath, overwrite = true)

        // Wire CapturingSink if provided
        match overrides.CapturingSink with
        | Some sink ->
            let logger =
                LoggerConfiguration()
                    .MinimumLevel.Verbose()
                    .WriteTo.Sink(sink :> ILogEventSink)
                    .CreateLogger()
            Log.Logger <- logger
        | None -> ()

        let testBuilder = WebApplication.CreateBuilder()
        testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        testBuilder.Host.UseSerilog() |> ignore

        (testBuilder.Configuration :> IConfigurationBuilder)
            .AddInMemoryCollection([
                KeyValuePair("Upstreams:Model35B",  sprintf "http://127.0.0.1:%d" fakePort)
                KeyValuePair("Upstreams:Model122B", sprintf "http://127.0.0.1:%d" fakePort)

                // ML mode required for canary
                KeyValuePair("Routing:Algorithm",            "ml")
                KeyValuePair("Routing:ComplexityThreshold", "3")
                KeyValuePair("Routing:TimeoutSeconds",       "300")
                KeyValuePair("Routing:Keywords:0",           "recursive")
                KeyValuePair("Routing:ML:ModelPath",         "models/router.zip")
                KeyValuePair("Routing:ML:EmbeddingModelPath","models/embed/bge-m3-int8.onnx")
                KeyValuePair("Routing:ML:TokenizerPath",     "models/embed/sentencepiece.bpe.model")
                KeyValuePair("Routing:ML:Threshold",         "0.5")
                KeyValuePair("Routing:ML:MaxTokens",         "512")
                // Task table — required for validateConfig
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
                // Queue
                KeyValuePair("Queue:FairnessK",                "10")
                KeyValuePair("Queue:MaxConcurrent122B",        "1")
                KeyValuePair("Queue:PerRequestTimeoutSeconds", "60")
                // DecisionLog → per-test temp dir
                KeyValuePair("DecisionLog:Directory",       logDir)
                KeyValuePair("DecisionLog:ChannelCapacity", "1000")

                // ── Canary section overrides ──
                KeyValuePair("Canary:CanaryModelPath",              canaryModelPath)
                KeyValuePair("Canary:PercentageEnabled",            string overrides.PercentageEnabled)
                KeyValuePair("Canary:RollingWindowSeconds",         string overrides.RollingWindowSeconds)
                KeyValuePair("Canary:WatchdogPollIntervalSeconds",  string overrides.WatchdogPollIntervalSeconds)
                KeyValuePair("Canary:AutoRollbackThreshold",        string overrides.AutoRollbackThreshold)
                KeyValuePair("Canary:AutoRollbackEnabled",          (if overrides.AutoRollbackEnabled then "true" else "false"))
                KeyValuePair("Canary:MinBaselineSampleSize",        string overrides.MinBaselineSampleSize)

                // feature_management section (Phase 9 Lock 13)
                KeyValuePair("feature_management:feature_flags:0:id",                                                          "Canary")
                KeyValuePair("feature_management:feature_flags:0:enabled",                                                     "true")
                KeyValuePair("feature_management:feature_flags:0:conditions:client_filters:0:name",                            "Microsoft.Targeting")
                KeyValuePair("feature_management:feature_flags:0:conditions:client_filters:0:parameters:Audience:DefaultRolloutPercentage", string overrides.PercentageEnabled)
            ])
        |> ignore

        SmartRouter.Cli.CompositionRoot.configureServices testBuilder.Services testBuilder.Configuration
        |> ignore

        let app = testBuilder.Build()
        SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints app
        SmartRouter.Cli.Endpoints.Stats.mapEndpoints app
        SmartRouter.Cli.Endpoints.Canary.mapEndpoints app

        do! app.StartAsync()

        let port =
            app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
            |> Seq.head
            |> fun a -> a.Split(':') |> Array.last |> int

        return app, port, logDir, canaryModelPath
    }

let private postChatRequest (client: HttpClient) (port: int) (correlationId: string) (cohortHeader: string) : Task<HttpStatusCode> =
    task {
        let body =
            {| messages = [| {| role = "user"; content = "test prompt" |} |]
               stream   = false |}
        use req = new HttpRequestMessage(HttpMethod.Post, sprintf "http://127.0.0.1:%d/v1/chat/completions" port)
        req.Content <- JsonContent.Create(body)
        if not (String.IsNullOrEmpty(correlationId)) then
            req.Headers.Add("X-Correlation-Id", correlationId)
        if not (String.IsNullOrEmpty(cohortHeader)) then
            req.Headers.Add("X-Test-Cohort", cohortHeader)
        let! resp = client.SendAsync(req)
        return resp.StatusCode
    }

let private getCanaryStatus (client: HttpClient) (port: int) : Task<JsonElement> =
    task {
        let! resp = client.GetAsync(sprintf "http://127.0.0.1:%d/canary" port)
        let! body = resp.Content.ReadAsStringAsync()
        let doc = JsonDocument.Parse(body)
        return doc.RootElement.Clone()
    }

let private postCanary (client: HttpClient) (port: int) (path: string) : Task<HttpStatusCode * string> =
    task {
        use req = new HttpRequestMessage(HttpMethod.Post, sprintf "http://127.0.0.1:%d%s" port path)
        let! resp = client.SendAsync(req)
        let! body = resp.Content.ReadAsStringAsync()
        return resp.StatusCode, body
    }

let private readJsonl (logDir: string) : string list =
    Threading.Thread.Sleep(500)   // give DecisionLogWriter BackgroundService time to flush
    Directory.EnumerateFiles(logDir, "*.jsonl")
    |> Seq.collect (fun f -> File.ReadAllLines(f))
    |> Seq.filter (fun s -> not (String.IsNullOrWhiteSpace s))
    |> List.ofSeq

// ── CANARY-02: cohort tagging in JSONL DecisionLog ──────────────────────────

let private canary02_modelVersionTagging =
    mlIntegTest "CANARY-02: DecisionLog model_version distinguishes canary from baseline" (fun () ->
        task {
            let! fakeApp, fakePort = startFakeUpstream All200
            let overrides = { defaultOverrides with PercentageEnabled = 50 }
            let! routerApp, routerPort, logDir, _canaryPath = startCanaryRouter fakePort overrides
            try
                use client = new HttpClient()
                // Send 50 requests with distinct correlation_ids
                let mutable sent = 0
                for _ in 1 .. 50 do
                    let cid = Guid.NewGuid().ToString("N")
                    let! _ = postChatRequest client routerPort cid ""
                    sent <- sent + 1

                let lines = readJsonl logDir
                Expect.isGreaterThanOrEqual lines.Length 30
                    (sprintf "expected >= 30 JSONL entries, got %d (sent=%d)" lines.Length sent)

                // W2 fix: parse each line as JSON via JsonDocument; do NOT rely on string.Contains
                // (fragile to whitespace, ordering, escaping). Each line is a single JSONL record.
                let parseModelVersion (line: string) : string option =
                    try
                        use doc = JsonDocument.Parse(line)
                        match doc.RootElement.TryGetProperty("model_version") with
                        | true, prop when prop.ValueKind = JsonValueKind.String -> Some (prop.GetString())
                        | _ -> None
                    with _ -> None

                let parsedVersions =
                    lines
                    |> List.choose parseModelVersion

                let canaryVersions =
                    parsedVersions
                    |> List.filter (fun v -> v.StartsWith("ml-", StringComparison.Ordinal) && v.EndsWith("-canary", StringComparison.Ordinal))
                let baselineVersions =
                    parsedVersions
                    |> List.filter (fun v -> v.StartsWith("ml-", StringComparison.Ordinal) && not (v.EndsWith("-canary", StringComparison.Ordinal)))

                // At 50% with ~50 requests, expect roughly half in each cohort.
                // Loose assertions: both cohorts have at least 5 entries (would only fail
                // if all bucketed into one cohort, indicating a non-sticky or broken gate).
                Expect.isGreaterThanOrEqual canaryVersions.Length 5
                    (sprintf "canary cohort had only %d entries — expected >=5 at 50%% rollout over %d requests" canaryVersions.Length lines.Length)
                Expect.isGreaterThanOrEqual baselineVersions.Length 5
                    (sprintf "baseline cohort had only %d entries — expected >=5 at 50%% rollout over %d requests" baselineVersions.Length lines.Length)
            finally
                routerApp.StopAsync().GetAwaiter().GetResult()
                fakeApp.StopAsync().GetAwaiter().GetResult()
        } |> Async.AwaitTask |> Async.RunSynchronously)

// ── CANARY-03: manual transitions (rollback / enable / promote / promote-no-canary) ─

let private canary03_manualRollbackEnable =
    mlIntegTest "CANARY-03: POST /canary/rollback then /canary/enable round-trip" (fun () ->
        task {
            let! fakeApp, fakePort = startFakeUpstream All200
            let! routerApp, routerPort, _logDir, _canaryPath = startCanaryRouter fakePort defaultOverrides
            try
                use client = new HttpClient()

                // Initial: percentage_enabled = 50, is_rolled_back = false
                let! before = getCanaryStatus client routerPort
                Expect.equal (before.GetProperty("percentage_enabled").GetInt32()) 50 "initial percentage = 50"
                Expect.isFalse (before.GetProperty("is_rolled_back").GetBoolean()) "initial is_rolled_back = false"

                // Rollback
                let! status, _body = postCanary client routerPort "/canary/rollback"
                Expect.equal status HttpStatusCode.OK "rollback returns 200"

                let! afterRollback = getCanaryStatus client routerPort
                Expect.equal (afterRollback.GetProperty("percentage_enabled").GetInt32()) 0 "after rollback percentage = 0"
                Expect.isTrue (afterRollback.GetProperty("is_rolled_back").GetBoolean()) "after rollback is_rolled_back = true"

                // Enable at 100
                let! status2, _body2 = postCanary client routerPort "/canary/enable?percentage=100"
                Expect.equal status2 HttpStatusCode.OK "enable returns 200"

                let! afterEnable = getCanaryStatus client routerPort
                Expect.equal (afterEnable.GetProperty("percentage_enabled").GetInt32()) 100 "after enable percentage = 100"
                Expect.isFalse (afterEnable.GetProperty("is_rolled_back").GetBoolean()) "after enable is_rolled_back = false"
            finally
                routerApp.StopAsync().GetAwaiter().GetResult()
                fakeApp.StopAsync().GetAwaiter().GetResult()
        } |> Async.AwaitTask |> Async.RunSynchronously)

let private canary03_promoteSuccess =
    mlIntegTest "CANARY-03: POST /canary/promote moves canary to baseline; canary_file_present becomes false" (fun () ->
        task {
            let! fakeApp, fakePort = startFakeUpstream All200
            let! routerApp, routerPort, _logDir, canaryPath = startCanaryRouter fakePort defaultOverrides
            try
                use client = new HttpClient()
                // Pre: canary file present
                let! before = getCanaryStatus client routerPort
                Expect.isTrue (before.GetProperty("canary_file_present").GetBoolean()) "canary file present before promote"
                Expect.isTrue (File.Exists canaryPath) "canary file on disk before promote"

                let! status, body = postCanary client routerPort "/canary/promote"
                Expect.equal status HttpStatusCode.OK (sprintf "promote returns 200 (got %A): %s" status body)
                Expect.isTrue (body.Contains("new_baseline_version")) "promote response contains new_baseline_version"

                let! after = getCanaryStatus client routerPort
                Expect.isFalse (after.GetProperty("canary_file_present").GetBoolean()) "canary file absent after promote"
                Expect.isFalse (File.Exists canaryPath) "canary file gone from disk after promote"
            finally
                routerApp.StopAsync().GetAwaiter().GetResult()
                fakeApp.StopAsync().GetAwaiter().GetResult()
        } |> Async.AwaitTask |> Async.RunSynchronously)

let private canary03_promoteNoCanaryFile =
    mlIntegTest "CANARY-03: POST /canary/promote returns 404 when no canary file" (fun () ->
        task {
            let! fakeApp, fakePort = startFakeUpstream All200
            let overrides = { defaultOverrides with CanaryModelExists = false }
            let! routerApp, routerPort, _logDir, _canaryPath = startCanaryRouter fakePort overrides
            try
                use client = new HttpClient()
                let! status, _body = postCanary client routerPort "/canary/promote"
                Expect.equal status HttpStatusCode.NotFound "promote with no canary file returns 404"
            finally
                routerApp.StopAsync().GetAwaiter().GetResult()
                fakeApp.StopAsync().GetAwaiter().GetResult()
        } |> Async.AwaitTask |> Async.RunSynchronously)

// ── CANARY-03: auto-rollback ────────────────────────────────────────────────

let private canary03_autoRollback =
    mlIntegTest "CANARY-03 auto-rollback: canary fb_rate > baseline + threshold fires SetPercentage(0)" (fun () ->
        task {
            let sink = CapturingSink()
            let! fakeApp, fakePort = startFakeUpstream All200
            let overrides =
                { defaultOverrides with
                    PercentageEnabled            = 50
                    AutoRollbackEnabled          = true
                    RollingWindowSeconds         = 5
                    WatchdogPollIntervalSeconds  = 1
                    AutoRollbackThreshold        = 0.10
                    MinBaselineSampleSize        = 10
                    CapturingSink                = Some sink }
            let! routerApp, routerPort, _logDir, _canaryPath = startCanaryRouter fakePort overrides
            try
                use client = new HttpClient()
                // Directly inject synthetic metrics to drive the watchdog:
                // 30 baseline events (all success) + 30 canary events (all fail).
                // canary_fb_rate=1.0, baseline_fb_rate=0.0, delta=1.0 > threshold=0.10.
                // The watchdog polls every 1s with a 5s window; all events are fresh.
                let metrics = routerApp.Services.GetRequiredService<ICanaryMetrics>()
                // 30 baseline events, all success (fb=false)
                for _ in 1 .. 30 do
                    metrics.Record(isCanary = false, isFallback = false)
                // 30 canary events, all failed (fb=true) — fb_rate = 1.0 vs baseline 0.0
                // delta = 1.0 > threshold 0.10 → auto-rollback should fire
                for _ in 1 .. 30 do
                    metrics.Record(isCanary = true, isFallback = true)

                // Wait for watchdog to poll (PollInterval=1s, Window=5s, both events are
                // fresh so they all count). 4-second wait gives >=3 polls.
                do! Task.Delay(4000)

                let! status = getCanaryStatus client routerPort
                Expect.isTrue (status.GetProperty("is_rolled_back").GetBoolean())
                    "after canary fb_rate exceeds baseline + threshold, watchdog must fire SetPercentage(0)"
                Expect.equal (status.GetProperty("percentage_enabled").GetInt32()) 0
                    "percentage_enabled = 0 after auto-rollback"

                let captured = sink.Messages |> List.filter (fun m -> m.Contains("AUTO-ROLLBACK"))
                Expect.isGreaterThanOrEqual captured.Length 1
                    (sprintf "Serilog log must contain 'AUTO-ROLLBACK' message; captured %d messages, none matching"
                        sink.Messages.Length)
            finally
                routerApp.StopAsync().GetAwaiter().GetResult()
                fakeApp.StopAsync().GetAwaiter().GetResult()
                Log.CloseAndFlush()
        } |> Async.AwaitTask |> Async.RunSynchronously)

let private canary03_autoRollbackDisabledByDefault =
    mlIntegTest "CANARY-03 auto-rollback: AutoRollbackEnabled=false -> no rollback even at high delta" (fun () ->
        task {
            let! fakeApp, fakePort = startFakeUpstream All200
            let overrides =
                { defaultOverrides with
                    AutoRollbackEnabled          = false      // explicit; this is also the default
                    RollingWindowSeconds         = 5
                    WatchdogPollIntervalSeconds  = 1
                    AutoRollbackThreshold        = 0.10
                    MinBaselineSampleSize        = 10 }
            let! routerApp, routerPort, _logDir, _canaryPath = startCanaryRouter fakePort overrides
            try
                use client = new HttpClient()
                let metrics = routerApp.Services.GetRequiredService<ICanaryMetrics>()
                for _ in 1 .. 30 do
                    metrics.Record(isCanary = false, isFallback = false)
                for _ in 1 .. 30 do
                    metrics.Record(isCanary = true, isFallback = true)

                do! Task.Delay(4000)

                let! status = getCanaryStatus client routerPort
                Expect.isFalse (status.GetProperty("is_rolled_back").GetBoolean())
                    "AutoRollbackEnabled=false MUST NOT trigger rollback even when delta > threshold (CONTEXT.md Lock 1)"
            finally
                routerApp.StopAsync().GetAwaiter().GetResult()
                fakeApp.StopAsync().GetAwaiter().GetResult()
        } |> Async.AwaitTask |> Async.RunSynchronously)

// ── CANARY-04: FileSystemWatcher post-startup file lifecycle (Lock 9) ────────

let private canary04_fileSystemWatcher =
    mlIntegTest "CANARY-04: FileSystemWatcher detects post-startup canary file lifecycle (Lock 9)" (fun () ->
        task {
            let! fakeApp, fakePort = startFakeUpstream All200
            // Boot router WITHOUT canary file present — watcher must arm on the empty
            // directory at StartAsync and pick up the file when it lands later.
            let overrides = { defaultOverrides with CanaryModelExists = false }
            let! routerApp, routerPort, _logDir, canaryPath = startCanaryRouter fakePort overrides
            try
                use client = new HttpClient()
                // Brief settle so CanaryService.IHostedService.StartAsync has completed
                // and the FileSystemWatcher has armed (EnableRaisingEvents <- true).
                do! Task.Delay(200)

                // 1. PRE: GET /canary returns canary_model_version = "" (no file).
                let! pre = getCanaryStatus client routerPort
                let preVer = pre.GetProperty("canary_model_version").GetString()
                Expect.equal preVer "" "canary_model_version is empty before file lands"
                Expect.isFalse (File.Exists canaryPath) "canary file absent at boot (CanaryModelExists=false)"

                // 2. POST-CREATE: Write a stand-in router-canary.zip; poll up to 2s for
                //    canary_model_version to flip to a non-empty `-canary` suffixed value.
                //    computeModelVersion SHA256-hashes the file content; any non-empty
                //    bytes produce a stable `ml-<8hex>-canary` tag (we never load it as
                //    an ML model — Lock 9 only requires the watcher to fire).
                File.WriteAllBytes(canaryPath, Array.zeroCreate 64)

                let mutable seenVersion = ""
                let mutable iters = 0
                while seenVersion = "" && iters < 20 do
                    do! Task.Delay(100)
                    let! doc = getCanaryStatus client routerPort
                    let v = doc.GetProperty("canary_model_version").GetString()
                    if v.EndsWith("-canary", StringComparison.Ordinal) then seenVersion <- v
                    iters <- iters + 1
                Expect.notEqual seenVersion ""
                    (sprintf "FileSystemWatcher must update canary_model_version within 2s of file Create (waited %d iters of 100ms)" iters)
                Expect.stringContains seenVersion "ml-" "canary_model_version has ml- prefix"
                Expect.isTrue (seenVersion.EndsWith("-canary", StringComparison.Ordinal))
                    (sprintf "canary_model_version ends with -canary; got %s" seenVersion)

                // 3. POST-DELETE: Remove the file; poll up to 2s for canary_model_version
                //    to clear back to "". Confirms the Deleted event handler is wired
                //    and calls versionProvider.UpdateCanary("").
                File.Delete(canaryPath)
                let mutable cleared = false
                iters <- 0
                while not cleared && iters < 20 do
                    do! Task.Delay(100)
                    let! doc = getCanaryStatus client routerPort
                    let v = doc.GetProperty("canary_model_version").GetString()
                    if v = "" then cleared <- true
                    iters <- iters + 1
                Expect.isTrue cleared
                    (sprintf "FileSystemWatcher must clear canary_model_version within 2s of file Delete (waited %d iters of 100ms)" iters)
            finally
                routerApp.StopAsync().GetAwaiter().GetResult()
                fakeApp.StopAsync().GetAwaiter().GetResult()
        } |> Async.AwaitTask |> Async.RunSynchronously)

// ── tests root ────────────────────────────────────────────────────────────────

let tests =
    testSequenced (
        testList "canary" [
            // Unit tests (CANARY-01)
            canary01_statisticalSplit
            canary01_stickyBucket
            canary01_emptyCorrelationIdShortCircuit
            canary01_missingFileShortCircuit
            canary01_zeroPercentageShortCircuit
            // Integration tests (CANARY-02)
            canary02_modelVersionTagging
            // Integration tests (CANARY-03 manual)
            canary03_manualRollbackEnable
            canary03_promoteSuccess
            canary03_promoteNoCanaryFile
            // Integration tests (CANARY-03 auto)
            canary03_autoRollback
            canary03_autoRollbackDisabledByDefault
            // Integration test (CANARY-04 FileSystemWatcher / Lock 9)
            canary04_fileSystemWatcher
        ])
