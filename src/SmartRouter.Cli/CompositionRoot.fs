module SmartRouter.Cli.CompositionRoot

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Logging
open Microsoft.Extensions.ML
open Microsoft.Extensions.Options
open Microsoft.FeatureManagement
open Microsoft.FeatureManagement.FeatureFilters
open Serilog
open SmartRouter.Core.Domain
open SmartRouter.Core.MLPorts
open SmartRouter.Core.Ports
open SmartRouter.Core.Routing
open SmartRouter.Core.CanaryPorts
open SmartRouter.Cli.Adapters.BgeM3Embedder
open SmartRouter.Cli.Adapters.DecisionLogger
open SmartRouter.Cli.Adapters.DecisionLogWriter
open SmartRouter.Cli.Adapters.MlNetClassifier
open SmartRouter.Cli.Adapters.ModelBootstrapper
open SmartRouter.Cli.Adapters.RoutingAlgorithm
open SmartRouter.Cli.Adapters.QwenUpstreamClient
open SmartRouter.Cli.Adapters.QueueDispatcher
open Microsoft.Extensions.Http.Resilience
open Polly
open Polly.Retry
open SmartRouter.Core.RetrainingPorts
open SmartRouter.Cli.Adapters.FailureDetector
open SmartRouter.Cli.Adapters.TeacherLabeler
open SmartRouter.Cli.Adapters.HardCaseDatasetWriter
open SmartRouter.Cli.Adapters.ModelVersionProvider
open SmartRouter.Cli.Adapters.RetrainingService
open SmartRouter.Cli.Adapters.CanaryTargetingAccessor
open SmartRouter.Cli.Adapters.CanaryState
open SmartRouter.Cli.Adapters.CanaryMetrics
open SmartRouter.Cli.Adapters.CanaryGate
open SmartRouter.Cli.Adapters.CanaryWatchdog
open SmartRouter.Cli.Adapters.CanaryService
open SmartRouter.Cli.Adapters.RetrainLock
open SmartRouter.Cli.Adapters.HealthService
open SmartRouter.Cli.Adapters.LogRetentionService
open SmartRouter.Cli.Adapters.TraceLogger
open SmartRouter.Cli.Adapters.QualityCheck

// ── JSON-binding types (Cli-only) ────────────────────────────────────────────

/// A single task table entry as it appears in appsettings.json.
/// Core uses (ModelId * Priority) tuples — this is only the JSON binding shape.
[<CLIMutable>]
type TaskTableEntry =
    { Model    : string   // "35b" | "122b" | any model alias
      Priority : string } // "high" | "low"

/// ML-specific configuration subsection (Routing.ML in appsettings.json).
/// Cli-only binding type — Core never sees this record.
[<CLIMutable>]
type MlOptions =
    { ModelPath          : string
      EmbeddingModelPath : string
      TokenizerPath      : string
      Threshold          : float32
      MaxTokens          : int
      UseCoreMLEP        : bool }

/// Full routing configuration as it appears in appsettings.json "Routing" section.
/// Cli-only: Core uses the pure RoutingConfig record from Domain.fs.
[<CLIMutable>]
type RoutingOptions =
    { TimeoutSeconds  : int
      ML              : MlOptions  // Routing.ML subsection; null when section absent
      TaskTable       : Dictionary<string, TaskTableEntry>
      ModelAliases    : Dictionary<string, string>
      QualityFallback : QualityFallbackOptions   // NEW Phase 14: quality-fallback heuristic options
    }

// ── buildRoutingConfig ───────────────────────────────────────────────────────

/// Translate JSON-bound RoutingOptions -> Core's pure RoutingConfig.
///
/// Called once at composition time. Validates each TaskTable entry as it builds
/// the map; startup fails fast on any malformed JSON (ROUT-05 explicit wiring).
///
/// This is the bridge between the Cli-side configuration (IOptions<T> / JSON shapes)
/// and the Core (pure F# records only). The endpoint handler uses the RoutingConfig
/// directly when calling Routing.routeRequest — editing appsettings.json rebuilds
/// this at startup, changing runtime behavior (CONTEXT.md / ROUT-05 proof).
let buildRoutingConfig (opts: RoutingOptions) : RoutingConfig =
    let taskMap =
        opts.TaskTable
        |> Seq.map (fun (KeyValue(taskName, entry)) ->
            let modelId =
                match tryParseModelAlias entry.Model with
                | Some m -> m
                | None ->
                    let msg = sprintf "appsettings.json Routing.TaskTable[\"%s\"].Model = \"%s\" is not a known model alias" taskName entry.Model
                    raise (InvalidOperationException(msg))
            let priority =
                match entry.Priority.ToLowerInvariant() with
                | "high" -> High
                | "low"  -> Low
                | other  ->
                    let msg = sprintf "appsettings.json Routing.TaskTable[\"%s\"].Priority = \"%s\" must be \"high\" or \"low\"" taskName other
                    raise (InvalidOperationException(msg))
            taskName.ToLowerInvariant(), (modelId, priority))
        |> Map.ofSeq

    let mlThreshold =
        // Defensive default: ML routes use this when Routing.ML.Threshold is missing/zero;
        // CLIMutable float32 defaults to 0.0f when JSON key absent.
        if obj.ReferenceEquals(opts.ML, null) then 0.5f
        else if opts.ML.Threshold = 0.0f then 0.5f
        else opts.ML.Threshold

    { TaskTable   = taskMap
      MlThreshold = mlThreshold }

/// Defensive helper: normalize QualityFallbackOptions from the JSON binding.
/// Called by configureRequestPipeline and configureWithoutMl before injecting
/// into DI. Handles absent-section (null), zero MinResponseLength, null BadKeywords.
let normalizeQualityFallback (opts: RoutingOptions) : QualityFallbackOptions =
    if obj.ReferenceEquals(opts.QualityFallback, null) then
        { Enabled = false; MinResponseLength = 30; BadKeywords = [||] }
    else
        let qf = opts.QualityFallback
        { Enabled           = qf.Enabled
          MinResponseLength = (if qf.MinResponseLength <= 0 then 30 else qf.MinResponseLength)
          BadKeywords       = (if obj.ReferenceEquals(qf.BadKeywords, null) then [||] else qf.BadKeywords) }

// ── validateConfig ───────────────────────────────────────────────────────────

/// Startup validation — cross-checks operator-supplied JSON against the canonical
/// baseline (Routing.canonicalTaskTable) to catch typos and missing tasks.
///
/// Does NOT check Model/Priority validity — buildRoutingConfig already throws on
/// parse failure. This step is a coverage / drift check:
///   1. Warn on unknown task keys (config can lead the code; warn but don't fail).
///   2. Fail-fast if the JSON is missing a canonical task (DU has it; JSON does not).
let validateConfig (opts: RoutingOptions) : unit =
    // 1. Warn on unknown task keys
    for KeyValue(taskName, _) in opts.TaskTable do
        match tryParseTaskType taskName with
        | None ->
            Log.Warning(
                "appsettings.json Routing.TaskTable contains unknown task {Task}; routing will return UnsupportedTask error for it",
                taskName)
        | Some _ -> ()

    // 2. Fail-fast if the JSON is missing a canonical task
    let canonical = canonicalTaskTable |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    let provided  = opts.TaskTable.Keys |> Seq.map (fun k -> k.ToLowerInvariant()) |> Set.ofSeq
    let missing   = Set.difference canonical provided

    if not (Set.isEmpty missing) then
        let missingList = String.concat ", " missing
        let msg =
            sprintf "appsettings.json Routing.TaskTable is missing canonical task(s): %s. Add an entry for each (or remove the canonical task from Core if intentional)." missingList
        raise (InvalidOperationException(msg))

// ── configureRequestPipeline ─────────────────────────────────────────────────

/// Register all DI services for the production HTTP request pipeline.
///
/// Key registrations:
///   - IOptions<UpstreamOptions>  bound from "Upstreams" section
///   - IOptions<RoutingOptions>   bound from "Routing" section
///   - Named HttpClients "upstream35b" / "upstream122b" with 300s timeout (CONC-07)
///   - RoutingConfig as DI singleton (the ROUT-05 wiring proof: endpoint resolves this
///     and passes to Routing.routeRequest; editing appsettings.json + restart rebuilds it)
///   - IUpstreamClient as DI singleton (ARCH-06: stateless; lazy probe cache is lifetime)
///   - Full ML wiring: IEmbedder, IClassifier, RoutingAlgorithmRegistration (unconditional)
///   - Phase 9: CanaryGate, CanaryMetrics, CanaryService, RetrainingService BackgroundServices
///   - Phase 10: HealthService BackgroundService
///
/// For the --retrain offline path, call configureWithoutMl instead.
let configureRequestPipeline (services: IServiceCollection) (config: IConfiguration) : IServiceCollection =
    // Bind option types from configuration sections
    services
        .Configure<UpstreamOptions>(config.GetSection("Upstreams"))
        .Configure<RoutingOptions>(config.GetSection("Routing"))
        .Configure<CanaryOptions>(config.GetSection("Canary"))     // NEW Phase 9
        |> ignore

    // ── Phase 10: 5 named HttpClients via .ConfigureHttpClient chain form ────────
    // The 2-arg AddHttpClient(name, fun c -> ...) form is FORBIDDEN in F# — silent BaseAddress
    // failure pitfall (see documentation/howto/wire-fsharp-namedhttpclient-with-configurehttpclient.md).
    // All five clients use the AddHttpClient(name).ConfigureHttpClient(...) chain form.

    let buildUpstreamRetry () =
        let opts = HttpRetryStrategyOptions()
        opts.MaxRetryAttempts <- 3
        opts.BackoffType      <- DelayBackoffType.Exponential
        opts.Delay            <- TimeSpan.FromSeconds(1.0)
        opts.ShouldHandle     <-
            Func<RetryPredicateArguments<HttpResponseMessage>, ValueTask<bool>>(fun args ->
                let retry =
                    match args.Outcome.Exception with
                    | :? HttpRequestException -> true
                    | :? TaskCanceledException -> true
                    | null ->
                        let resp = args.Outcome.Result
                        not (isNull resp) && int resp.StatusCode >= 500
                    | _ -> false
                ValueTask.FromResult(retry))
        opts

    let upstreamOptsLazy = config.GetSection("Upstreams").Get<UpstreamOptions>()

    // upstream35b — non-streaming, with retry (300s timeout for 122B cold starts CONC-07)
    services.AddHttpClient("upstream35b")
        .ConfigureHttpClient(fun c ->
            c.BaseAddress <- Uri(upstreamOptsLazy.Model35B)
            c.Timeout     <- TimeSpan.FromSeconds(300.0))
        .AddResilienceHandler("upstream35b-pipeline", fun (builder: ResiliencePipelineBuilder<HttpResponseMessage>) ->
            builder.AddRetry(buildUpstreamRetry ()) |> ignore)
        |> ignore

    // upstream122b — non-streaming, with retry
    services.AddHttpClient("upstream122b")
        .ConfigureHttpClient(fun c ->
            c.BaseAddress <- Uri(upstreamOptsLazy.Model122B)
            c.Timeout     <- TimeSpan.FromSeconds(300.0))
        .AddResilienceHandler("upstream122b-pipeline", fun (builder: ResiliencePipelineBuilder<HttpResponseMessage>) ->
            builder.AddRetry(buildUpstreamRetry ()) |> ignore)
        |> ignore

    // upstream35b-stream — streaming, NO retry (SSE not idempotent — partial output cannot be retried)
    services.AddHttpClient("upstream35b-stream")
        .ConfigureHttpClient(fun c ->
            c.BaseAddress <- Uri(upstreamOptsLazy.Model35B)
            c.Timeout     <- TimeSpan.FromSeconds(300.0))
        |> ignore

    // upstream122b-stream — streaming, NO retry
    services.AddHttpClient("upstream122b-stream")
        .ConfigureHttpClient(fun c ->
            c.BaseAddress <- Uri(upstreamOptsLazy.Model122B)
            c.Timeout     <- TimeSpan.FromSeconds(300.0))
        |> ignore

    // health-probe — short timeout, NO retry, NO BaseAddress (probe URL is absolute per D8)
    services.AddHttpClient("health-probe")
        .ConfigureHttpClient(fun c ->
            c.Timeout <- TimeSpan.FromSeconds(5.0))
        |> ignore

    // Bind Routing.Health → HealthOptions
    services.Configure<HealthOptions>(config.GetSection("Routing:Health")) |> ignore

    // HealthService triple-reg (D9): concrete singleton + IHealthProbe alias + AddHostedService.
    // Same instance for all three roles — DO NOT use three separate factory lambdas.
    services.AddSingleton<HealthService>(fun sp ->
        new HealthService(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<UpstreamOptions>>(),
            sp.GetRequiredService<IOptions<HealthOptions>>(),
            sp.GetRequiredService<ILogger<HealthService>>()))
    |> ignore

    services.AddSingleton<IHealthProbe>(fun sp ->
        sp.GetRequiredService<HealthService>() :> IHealthProbe)
    |> ignore

    services.AddHostedService<HealthService>(fun sp ->
        sp.GetRequiredService<HealthService>())
    |> ignore

    // RoutingConfig as a DI singleton — built once at composition time from RoutingOptions.
    // The endpoint retrieves this and passes it into Routing.routeRequest. THIS is the wiring
    // that satisfies ROUT-05: editing appsettings.json + restart rebuilds this singleton,
    // which changes runtime routing behavior without recompiling.
    services.AddSingleton<RoutingConfig>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<RoutingOptions>>().Value
        buildRoutingConfig opts)
        |> ignore

    // Phase 14: QualityFallbackOptions as a standalone DI singleton.
    // ChatCompletions.fs compiles before CompositionRoot.fs, so it cannot reference
    // RoutingOptions directly. Registering QualityFallbackOptions separately lets the
    // endpoint handler resolve it via GetRequiredService<QualityFallbackOptions>() with
    // no compile-order issue (QualityCheck.fs is compiled before ChatCompletions.fs).
    services.AddSingleton<QualityFallbackOptions>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<RoutingOptions>>().Value
        normalizeQualityFallback opts)
        |> ignore

    // ── Phase 9 unconditional fallback defaults (Step 1.0) ───────────────────
    //
    // ICanaryGate: NullCanaryGate fallback (Step 1.2 below overrides via plain AddSingleton).
    // ICanaryMetrics: NoOpCanaryMetrics fallback (Step 1.2 below overrides via plain AddSingleton).
    // These ensure ChatCompletions.handler resolves both interfaces even in configureWithoutMl —
    // ChatCompletions does NOT branch on routing algo; it always calls metrics.Record(...) and
    // (transitively, via the ML closure) consults ICanaryGate. Default mode gets a no-op pair.
    //
    // TryAddSingleton skips registration if the type is already present; last-registration-wins:
    // Step 1.2 calls plain AddSingleton which APPENDS a second descriptor — GetRequiredService<T>
    // returns the LAST registered → FeatureManagementCanaryGate overrides NullCanaryGate.
    services.TryAddSingleton<ICanaryGate>(fun _sp -> NullCanaryGate() :> ICanaryGate) |> ignore
    services.TryAddSingleton<ICanaryMetrics>(fun _sp -> NoOpCanaryMetrics() :> ICanaryMetrics) |> ignore

    // ── ML wiring ──────────────────────────────────────────────────────────────
    //
    // Order matters:
    //   1. ensureEmbeddingFilesPresent — fail-fast with operator-friendly error if files missing
    //   2. ensureDummyModel            — generate router.zip if missing (idempotent)
    //   3. AddPredictionEnginePool     — registers pool; file is guaranteed to exist
    //
    // Steps 1+2 run synchronously at configure time so the pool registration has
    // guaranteed file presence. Both are no-ops on second startup.
    //
    // ML wiring is unconditional — configureRequestPipeline always wires ML.
    // The --retrain offline path calls configureWithoutMl which skips this entire block.
    let mlOpts = config.GetSection("Routing:ML").Get<MlOptions>()
    if not (obj.ReferenceEquals(mlOpts, null)) then
        // Bootstrap calls run at startup before the DI container is built.
        // Use NullLogger here — the startup Serilog static sink captures fatal errors.
        let bootLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance :> Microsoft.Extensions.Logging.ILogger
        ensureEmbeddingFilesPresent bootLogger mlOpts.EmbeddingModelPath mlOpts.TokenizerPath
        ensureDummyModel bootLogger mlOpts.ModelPath

        let canaryOpts = config.GetSection("Canary").Get<CanaryOptions>()
        let canaryModelPath =
            if obj.ReferenceEquals(canaryOpts, null) || String.IsNullOrWhiteSpace(canaryOpts.CanaryModelPath)
            then "models/router-canary.zip"
            else canaryOpts.CanaryModelPath

        services
            .AddPredictionEnginePool<RouteInput, RoutePrediction>()
            .FromFile(
                modelName       = "router",
                filePath        = mlOpts.ModelPath,
                watchForChanges = true)
            .FromFile(
                modelName       = "router-canary",
                filePath        = canaryModelPath,
                watchForChanges = true)
            |> ignore

        // BgeM3Embedder — singleton; warm-up runs at construction.
        services.AddSingleton<IEmbedder>(fun sp ->
            new BgeM3Embedder(
                mlOpts.EmbeddingModelPath,
                mlOpts.TokenizerPath,
                mlOpts.MaxTokens,
                sp.GetRequiredService<ILogger<BgeM3Embedder>>()) :> IEmbedder)
            |> ignore

        // Phase 9: keyed classifiers (baseline + canary) from the same pool.
        services.AddKeyedSingleton<IClassifier>("baseline", System.Func<IServiceProvider, obj, IClassifier>(fun sp _key ->
            let pool = sp.GetRequiredService<PredictionEnginePool<RouteInput, RoutePrediction>>()
            MlNetClassifier(pool, "router") :> IClassifier))
            |> ignore

        services.AddKeyedSingleton<IClassifier>("canary", System.Func<IServiceProvider, obj, IClassifier>(fun sp _key ->
            let pool = sp.GetRequiredService<PredictionEnginePool<RouteInput, RoutePrediction>>()
            MlNetClassifier(pool, "router-canary") :> IClassifier))
            |> ignore

        // Backwards-compat: provide a non-keyed IClassifier resolution for any
        // existing tests that might resolve GetRequiredService<IClassifier>().
        services.AddSingleton<IClassifier>(fun sp ->
            sp.GetRequiredKeyedService<IClassifier>("baseline"))
            |> ignore

    // RoutingAlgorithmRegistration as a DI singleton — pairs the ML algorithm function
    // with its name and model_version so the endpoint can populate DecisionLog.
    // Always ML: makeApplyML closure / "ml" / "ml-{8hexchars}"
    services.AddSingleton<RoutingAlgorithmRegistration>(
        Func<IServiceProvider, RoutingAlgorithmRegistration>(fun sp ->
            // ML branch — resolve adapters once; close over them in the makeApplyML factory.
            // Phase 9 Plan 09-02: real FeatureManagementCanaryGate + dual-classifier dispatch.
            // Issue #12: makeApplyML now takes IModelVersionProvider directly so the closure
            // reads live versions per call (was: closed-over strings, never updated).
            let opts               = sp.GetRequiredService<IOptions<RoutingOptions>>().Value
            let embedder           = sp.GetRequiredService<IEmbedder>()
            let baselineClassifier = sp.GetRequiredKeyedService<IClassifier>("baseline")
            let canaryClassifier   = sp.GetRequiredKeyedService<IClassifier>("canary")
            let canaryGate         = sp.GetRequiredService<ICanaryGate>()
            let vp                 = sp.GetRequiredService<IModelVersionProvider>()
            let mlPath             = opts.ML.ModelPath
            let baselineVersion    = sprintf "ml-%s" (computeModelVersion mlPath)
            let canaryOpts2        = sp.GetRequiredService<IOptions<CanaryOptions>>().Value
            let canaryPath =
                if obj.ReferenceEquals(canaryOpts2, null) || String.IsNullOrWhiteSpace(canaryOpts2.CanaryModelPath)
                then "models/router-canary.zip" else canaryOpts2.CanaryModelPath
            let canaryVersion =
                if File.Exists(canaryPath)
                then sprintf "ml-%s-canary" (computeModelVersion canaryPath)
                else ""
            // Seed the provider with the on-disk values so the first request observes them
            // before any retrain / canary swap fires. Subsequent updates from
            // RetrainingService and CanaryService flow through unchanged.
            vp.Update(baselineVersion)
            vp.UpdateCanary(canaryVersion)
            { Algorithm    = SmartRouter.Core.ML.makeApplyML
                                embedder
                                baselineClassifier
                                canaryClassifier
                                canaryGate
                                vp
              Name         = "ml"
              ModelVersion = baselineVersion }))
    |> ignore

    // Backwards-compatible alias: register the bare RoutingAlgorithm function so
    // any existing test or component that resolves RoutingAlgorithm directly still works.
    // MLRoutingTests Tests 4+5 currently resolve GetRequiredService<RoutingAlgorithm>() —
    // this preserves that resolution and lets those tests continue to pass without changes.
    services.AddSingleton<RoutingAlgorithm>(
        Func<IServiceProvider, RoutingAlgorithm>(fun sp ->
            sp.GetRequiredService<RoutingAlgorithmRegistration>().Algorithm))
    |> ignore

    // Bind Queue section to QueueDispatcherOptions
    services.Configure<QueueDispatcherOptions>(config.GetSection("Queue")) |> ignore

    // Register the concrete HTTP client as a named singleton (not as IUpstreamClient —
    // that is now the dispatcher's job). QueueDispatcher resolves this by concrete type.
    services.AddSingleton<QwenUpstreamClient>(fun sp ->
        QwenUpstreamClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<UpstreamOptions>>(),
            sp.GetRequiredService<ILogger<QwenUpstreamClient>>()))
        |> ignore

    // QueueDispatcher wraps QwenUpstreamClient — registered as concrete singleton plus
    // two interface registrations (IUpstreamClient for the endpoint; IStatsProvider for /stats).
    // Phase 10: third constructor argument IHealthProbe for fallback policy (D14).
    services.AddSingleton<QueueDispatcher>(fun sp ->
        QueueDispatcher(
            sp.GetRequiredService<QwenUpstreamClient>() :> IUpstreamClient,
            sp.GetRequiredService<IOptions<QueueDispatcherOptions>>().Value,
            sp.GetRequiredService<IHealthProbe>(),
            sp.GetRequiredService<ILogger<QueueDispatcher>>()))
        |> ignore

    services.AddSingleton<IUpstreamClient>(fun sp ->
        sp.GetRequiredService<QueueDispatcher>() :> IUpstreamClient)
        |> ignore

    services.AddSingleton<IStatsProvider>(fun sp ->
        sp.GetRequiredService<QueueDispatcher>() :> IStatsProvider)
        |> ignore

    // Bind DecisionLog options from "DecisionLog" section in appsettings.json.
    services.Configure<DecisionLogOptions>(config.GetSection("DecisionLog")) |> ignore

    // DecisionLogWriter — concrete singleton.
    // Same instance exposed as IDecisionLogger (for endpoint injection) AND
    // IHostedService (for ASP.NET host lifecycle: StartAsync/StopAsync).
    // DO NOT use three separate AddSingleton<DecisionLogWriter> — that creates three instances.
    services.AddSingleton<DecisionLogWriter>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<DecisionLogOptions>>().Value
        // Defensive defaults if config keys absent or blank
        let dir = if String.IsNullOrWhiteSpace(opts.Directory) then "logs/decisions" else opts.Directory
        let cap = if opts.ChannelCapacity <= 0 then 10000 else opts.ChannelCapacity
        new DecisionLogWriter({ Directory = dir; ChannelCapacity = cap },
            sp.GetRequiredService<ILogger<DecisionLogWriter>>()))
    |> ignore

    services.AddSingleton<IDecisionLogger>(fun sp ->
        sp.GetRequiredService<DecisionLogWriter>() :> IDecisionLogger)
    |> ignore

    services.AddHostedService<DecisionLogWriter>(fun sp ->
        sp.GetRequiredService<DecisionLogWriter>())
    |> ignore

    // ── Phase 14: TraceLogger (optional; --trace-responses CLI flag enables) ──
    // Reads Trace:Enabled from IConfiguration (injected by applyTraceFlagFromArgs in Program.fs).
    // When true: triple-reg pattern (concrete + IInterface alias + AddHostedService) —
    // single instance for all three roles, mirroring DecisionLogWriter.
    // When false: ITraceLogger is NOT registered; consumers use GetService<ITraceLogger>()
    // (returns null) instead of GetRequiredService (would throw). 14-04 owns the consumer-side
    // defensive null check in ChatCompletions.fs.
    // Note: --trace-responses has no effect when combined with --retrain (offline pipeline
    // does not go through ChatCompletions; configureWithoutMl omits this block entirely).
    let traceEnabled =
        let raw = config.["Trace:Enabled"]
        not (isNull raw) && raw.Equals("true", StringComparison.OrdinalIgnoreCase)
    if traceEnabled then
        services.Configure<TraceLoggerOptions>(fun (o: TraceLoggerOptions) ->
            o.Directory       <- "logs/trace"
            o.ChannelCapacity <- 1000) |> ignore
        services.AddSingleton<TraceLogger>(fun sp ->
            new TraceLogger(
                sp.GetRequiredService<IOptions<TraceLoggerOptions>>(),
                sp.GetRequiredService<ILogger<TraceLogger>>())) |> ignore
        services.AddSingleton<ITraceLogger>(fun sp ->
            sp.GetRequiredService<TraceLogger>() :> ITraceLogger) |> ignore
        services.AddHostedService<TraceLogger>(fun sp ->
            sp.GetRequiredService<TraceLogger>()) |> ignore

    // ── Phase 7: Failure detection + teacher labeling ─────────────────────────
    services.Configure<TeacherLabelerOptions>(config.GetSection("TeacherLabeler")) |> ignore
    services.Configure<HardCaseDatasetOptions>(config.GetSection("HardCaseDataset")) |> ignore

    // Named HttpClient "teacher" — independent of the QueueDispatcher-gated upstream clients.
    // Routing teacher calls through QueueDispatcher would starve real inference traffic
    // of the 122B SemaphoreSlim(1) slot (Pitfall 5 from RESEARCH.md).
    //
    // Resilience handler: 3 retry attempts, exponential 1s/2s/4s, transient errors only.
    // HttpClient.Timeout = TeacherLabeler:TimeoutSeconds (default 30s) — applied per attempt.
    services.AddHttpClient("teacher", fun (c: System.Net.Http.HttpClient) ->
        let opts = config.GetSection("TeacherLabeler").Get<TeacherLabelerOptions>()
        let endpoint = if String.IsNullOrWhiteSpace(opts.Endpoint) then "http://127.0.0.1:8001" else opts.Endpoint
        let timeoutSec = if opts.TimeoutSeconds <= 0 then 30 else opts.TimeoutSeconds
        c.BaseAddress <- Uri(endpoint)
        c.Timeout     <- TimeSpan.FromSeconds(float timeoutSec))
        .AddResilienceHandler("teacher-pipeline", fun (builder: Polly.ResiliencePipelineBuilder<System.Net.Http.HttpResponseMessage>) ->
            // Use AddResilienceHandler (NOT AddStandardResilienceHandler) so we can
            // make the 4xx-skip explicit per FAIL-02 ("transient errors only").
            // RESEARCH.md Pattern 5 — explicit ShouldHandle predicate:
            //   - HttpRequestException → retry (transport errors)
            //   - TaskCanceledException → retry (timeout / per-attempt cancellation)
            //   - 5xx HTTP responses   → retry
            //   - 4xx HTTP responses   → DO NOT retry (logic errors; teacher-side rejection)
            //   - any other exception  → do not retry (fail fast)
            let opts = config.GetSection("TeacherLabeler").Get<TeacherLabelerOptions>()
            let timeoutSec = if opts.TimeoutSeconds <= 0 then 30 else opts.TimeoutSeconds
            let retryOpts = HttpRetryStrategyOptions()
            retryOpts.MaxRetryAttempts <- 3
            retryOpts.BackoffType      <- DelayBackoffType.Exponential
            retryOpts.Delay            <- TimeSpan.FromSeconds(1.0)
            retryOpts.ShouldHandle     <-
                Func<RetryPredicateArguments<System.Net.Http.HttpResponseMessage>, System.Threading.Tasks.ValueTask<bool>>(
                    fun args ->
                        let retry =
                            match args.Outcome.Exception with
                            | :? System.Net.Http.HttpRequestException -> true
                            | :? System.Threading.Tasks.TaskCanceledException -> true
                            | null ->
                                let resp = args.Outcome.Result
                                not (isNull resp) && int resp.StatusCode >= 500
                            | _ -> false
                        System.Threading.Tasks.ValueTask.FromResult(retry))
            builder.AddRetry(retryOpts) |> ignore
            builder.AddTimeout(TimeSpan.FromSeconds(float timeoutSec)) |> ignore)
        |> ignore

    // FailureDetector — reads from the same logs/decisions/ directory as DecisionLogWriter writes to.
    services.AddSingleton<FailureDetector>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<DecisionLogOptions>>().Value
        let dir = if String.IsNullOrWhiteSpace(opts.Directory) then "logs/decisions" else opts.Directory
        FailureDetector(dir, sp.GetRequiredService<ILogger<FailureDetector>>()))
    |> ignore

    services.AddSingleton<IFailureDetector>(fun sp ->
        sp.GetRequiredService<FailureDetector>() :> IFailureDetector)
    |> ignore

    // TeacherLabeler — uses IHttpClientFactory + named "teacher" client (registered above).
    services.AddSingleton<TeacherLabeler>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<TeacherLabelerOptions>>().Value
        // Defensive defaults if config absent
        let normalized =
            { Endpoint        = if String.IsNullOrWhiteSpace(opts.Endpoint)    then "http://127.0.0.1:8001"         else opts.Endpoint
              PromptPath      = if String.IsNullOrWhiteSpace(opts.PromptPath)  then "prompts/teacher-prompt.md"     else opts.PromptPath
              DailyCallCap    = if opts.DailyCallCap   <= 0                    then 1000                            else opts.DailyCallCap
              TimeoutSeconds  = if opts.TimeoutSeconds <= 0                    then 30                              else opts.TimeoutSeconds
              DatasetsDir     = if String.IsNullOrWhiteSpace(opts.DatasetsDir) then "datasets"                      else opts.DatasetsDir }
        TeacherLabeler(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(), normalized,
            sp.GetRequiredService<ILogger<TeacherLabeler>>()))
    |> ignore

    services.AddSingleton<ITeacherLabeler>(fun sp ->
        sp.GetRequiredService<TeacherLabeler>() :> ITeacherLabeler)
    |> ignore

    // HardCaseDatasetWriter — concrete singleton + IHardCaseDatasetWriter alias + AddHostedService.
    // Same instance for all three roles (DO NOT use three separate AddSingleton<HardCaseDatasetWriter>
    // — that creates three instances, each with its own Channel and BackgroundService loop).
    services.AddSingleton<HardCaseDatasetWriter>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<HardCaseDatasetOptions>>().Value
        let p   = if String.IsNullOrWhiteSpace(opts.Path) then "datasets/hard-cases.jsonl" else opts.Path
        let cap = if opts.ChannelCapacity <= 0 then 1000 else opts.ChannelCapacity
        new HardCaseDatasetWriter({ Path = p; ChannelCapacity = cap },
            sp.GetRequiredService<ILogger<HardCaseDatasetWriter>>()))
    |> ignore

    services.AddSingleton<IHardCaseDatasetWriter>(fun sp ->
        sp.GetRequiredService<HardCaseDatasetWriter>() :> IHardCaseDatasetWriter)
    |> ignore

    services.AddHostedService<HardCaseDatasetWriter>(fun sp ->
        sp.GetRequiredService<HardCaseDatasetWriter>())
    |> ignore

    // ── Phase 8: Retraining loop ──────────────────────────────────────────────
    services.Configure<RetrainingOptions>(config.GetSection("Retraining")) |> ignore

    // IModelVersionProvider — double-registration (concrete + interface alias).
    // RetrainingService updates the concrete; ChatCompletions reads the interface.
    // Same instance for both roles (DO NOT use two separate factory lambdas —
    // that creates two instances and the Update call from RetrainingService would
    // mutate the concrete one while ChatCompletions reads the alias one, so
    // DecisionLog never picks up the new model_version).
    // Mirrors HardCaseDatasetWriter pattern (lines above), minus the AddHostedService
    // leg since ModelVersionProvider is not a BackgroundService.
    //
    // Initial value matches what RoutingAlgorithmRegistration computed for model_version:
    // "ml-{8hexchars}" from router.zip SHA prefix.
    // After each successful retrain, RetrainingService.Update flips this value;
    // the next ChatCompletions request emits the new model_version.
    services.AddSingleton<ModelVersionProvider>(fun sp ->
        let routingOpts =
            sp.GetRequiredService<IOptions<RoutingOptions>>().Value
        let initial =
            // ML is the only path: load router.zip and compute SHA prefix
            // (matches RoutingAlgorithmRegistration registration above).
            if not (obj.ReferenceEquals(routingOpts.ML, null)) then
                sprintf "ml-%s" (computeModelVersion routingOpts.ML.ModelPath)
            else
                "ml-unknown"
        ModelVersionProvider(initial))
    |> ignore

    services.AddSingleton<IModelVersionProvider>(fun sp ->
        sp.GetRequiredService<ModelVersionProvider>() :> IModelVersionProvider)
    |> ignore

    // RetrainingService — double-registration pattern (concrete AddSingleton + AddHostedService factory).
    // Requires IEmbedder (registered above in the ML wiring block).
    // No IInterface alias since RetrainingService has no interface consumer — only IHostedService
    // machinery and the test-only RunNowAsync seam consume it directly.
    // ── Phase 9: Canary deployment (Step 1.2) ────────────────────────────────
    services.AddHttpContextAccessor() |> ignore

    services
        .AddScopedFeatureManagement()
        .WithTargeting<CanaryTargetingContextAccessor>()
        |> ignore

    // RetrainLock — shared between Phase 8 RetrainingService and Phase 9 CanaryService
    // (CONTEXT.md Lock 6). Double-reg: concrete + interface alias.
    services.AddSingleton<RetrainLock>(fun _sp -> new RetrainLock()) |> ignore
    services.AddSingleton<IRetrainLock>(fun sp -> sp.GetRequiredService<RetrainLock>() :> IRetrainLock) |> ignore

    // CanaryState — double-reg.
    services.AddSingleton<CanaryState>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<CanaryOptions>>().Value
        let initial = if obj.ReferenceEquals(opts, null) then 10 else opts.PercentageEnabled
        CanaryState(initial))
        |> ignore
    services.AddSingleton<ICanaryState>(fun sp -> sp.GetRequiredService<CanaryState>() :> ICanaryState) |> ignore

    // CanaryMetrics — double-reg. Plain AddSingleton OVERRIDES the (1.0) NoOpCanaryMetrics
    // fallback via last-registration-wins for GetRequiredService<ICanaryMetrics>.
    services.AddSingleton<CanaryMetrics>(fun _sp -> CanaryMetrics()) |> ignore
    services.AddSingleton<ICanaryMetrics>(fun sp -> sp.GetRequiredService<CanaryMetrics>() :> ICanaryMetrics) |> ignore

    // CanaryGate — plain AddSingleton OVERRIDES the (1.0) NullCanaryGate fallback via
    // last-registration-wins for GetRequiredService<ICanaryGate>.
    //
    // Issue #2: `IVariantFeatureManager` from Microsoft.FeatureManagement is registered
    // as Scoped. Resolving a scoped service from this singleton's captured root provider
    // fails ASP.NET Core's `ValidateScopes` check (Development default) and surfaces as
    // HTTP 500 on every chat-completion request. Pass `IServiceScopeFactory` (singleton-
    // safe) instead; the gate creates a fresh scope per call and resolves the FM there.
    services.AddSingleton<ICanaryGate>(fun sp ->
        let scopeFactory = sp.GetRequiredService<IServiceScopeFactory>()
        let st = sp.GetRequiredService<ICanaryState>()
        let opts = sp.GetRequiredService<IOptions<CanaryOptions>>().Value
        let path =
            if obj.ReferenceEquals(opts, null) || String.IsNullOrWhiteSpace(opts.CanaryModelPath)
            then "models/router-canary.zip"
            else opts.CanaryModelPath
        FeatureManagementCanaryGate(scopeFactory, st, path) :> ICanaryGate)
        |> ignore

    // CanaryWatchdog — triple-reg (concrete + AddHostedService factory).
    services.AddSingleton<CanaryWatchdog>(fun sp ->
        let m  = sp.GetRequiredService<ICanaryMetrics>()
        let s  = sp.GetRequiredService<ICanaryState>()
        let opts = sp.GetRequiredService<IOptions<CanaryOptions>>().Value
        // Defensive defaults for missing/zero keys.
        let normalized =
            { CanaryModelPath              = if obj.ReferenceEquals(opts, null) || String.IsNullOrWhiteSpace(opts.CanaryModelPath)
                                             then "models/router-canary.zip" else opts.CanaryModelPath
              PercentageEnabled            = if obj.ReferenceEquals(opts, null) then 10 else opts.PercentageEnabled
              RollingWindowSeconds         = if obj.ReferenceEquals(opts, null) || opts.RollingWindowSeconds <= 0 then 60 else opts.RollingWindowSeconds
              WatchdogPollIntervalSeconds  = if obj.ReferenceEquals(opts, null) || opts.WatchdogPollIntervalSeconds <= 0 then 10 else opts.WatchdogPollIntervalSeconds
              AutoRollbackThreshold        = if obj.ReferenceEquals(opts, null) || opts.AutoRollbackThreshold <= 0.0 then 0.10 else opts.AutoRollbackThreshold
              AutoRollbackEnabled          = if obj.ReferenceEquals(opts, null) then false else opts.AutoRollbackEnabled
              MinBaselineSampleSize        = if obj.ReferenceEquals(opts, null) || opts.MinBaselineSampleSize <= 0 then 50 else opts.MinBaselineSampleSize }
        new CanaryWatchdog(m, s, normalized,
            sp.GetRequiredService<ILogger<CanaryWatchdog>>()))
        |> ignore
    services.AddHostedService<CanaryWatchdog>(fun sp -> sp.GetRequiredService<CanaryWatchdog>())
        |> ignore

    // CanaryService — triple-reg: concrete + ICanaryService + IHostedService (CONTEXT.md Lock 9)
    services.AddSingleton<CanaryService>(fun sp ->
        let st = sp.GetRequiredService<ICanaryState>()
        let m  = sp.GetRequiredService<ICanaryMetrics>()
        let vp = sp.GetRequiredService<IModelVersionProvider>()
        let rl = sp.GetRequiredService<IRetrainLock>()
        let opts = sp.GetRequiredService<IOptions<CanaryOptions>>().Value
        let normalized =
            { CanaryModelPath              = if obj.ReferenceEquals(opts, null) || String.IsNullOrWhiteSpace(opts.CanaryModelPath)
                                             then "models/router-canary.zip" else opts.CanaryModelPath
              PercentageEnabled            = if obj.ReferenceEquals(opts, null) then 10 else opts.PercentageEnabled
              RollingWindowSeconds         = if obj.ReferenceEquals(opts, null) || opts.RollingWindowSeconds <= 0 then 60 else opts.RollingWindowSeconds
              WatchdogPollIntervalSeconds  = if obj.ReferenceEquals(opts, null) || opts.WatchdogPollIntervalSeconds <= 0 then 10 else opts.WatchdogPollIntervalSeconds
              AutoRollbackThreshold        = if obj.ReferenceEquals(opts, null) || opts.AutoRollbackThreshold <= 0.0 then 0.10 else opts.AutoRollbackThreshold
              AutoRollbackEnabled          = if obj.ReferenceEquals(opts, null) then false else opts.AutoRollbackEnabled
              MinBaselineSampleSize        = if obj.ReferenceEquals(opts, null) || opts.MinBaselineSampleSize <= 0 then 50 else opts.MinBaselineSampleSize }
        // Baseline + previous-model paths come from the existing Retraining options
        // (Phase 8 owns those paths; Phase 9 only writes router.zip via promote-under-lock).
        let retrainOpts = sp.GetRequiredService<IOptions<RetrainingOptions>>().Value
        let baselinePath =
            if obj.ReferenceEquals(retrainOpts, null) || String.IsNullOrWhiteSpace(retrainOpts.ModelPath)
            then "models/router.zip" else retrainOpts.ModelPath
        let previousPath =
            if obj.ReferenceEquals(retrainOpts, null) || String.IsNullOrWhiteSpace(retrainOpts.PreviousModelPath)
            then "models/router.zip.prev" else retrainOpts.PreviousModelPath
        CanaryService(st, m, vp, rl, normalized, baselinePath, previousPath,
            sp.GetRequiredService<ILogger<CanaryService>>()))
        |> ignore
    services.AddSingleton<ICanaryService>(fun sp -> sp.GetRequiredService<CanaryService>() :> ICanaryService) |> ignore
    // CanaryService is also IHostedService (owns FileSystemWatcher for router-canary.zip
    // post-startup arrival — CONTEXT.md Lock 9). Triple-reg: concrete + ICanaryService + IHostedService.
    services.AddHostedService<CanaryService>(fun sp -> sp.GetRequiredService<CanaryService>())
        |> ignore

    services.AddSingleton<RetrainingService>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<RetrainingOptions>>().Value
        // Defensive defaults — any missing/zero key falls back to CONTEXT.md Lock 6 values.
        let normalized =
            { IntervalMinutes           = if opts.IntervalMinutes           <= 0    then 60    else opts.IntervalMinutes
              HardCaseCountTrigger      = if opts.HardCaseCountTrigger      <= 0    then 500   else opts.HardCaseCountTrigger
              CountCheckIntervalMinutes = if opts.CountCheckIntervalMinutes <= 0    then 5     else opts.CountCheckIntervalMinutes
              HardCasePath              = if String.IsNullOrWhiteSpace(opts.HardCasePath)      then "datasets/hard-cases.jsonl"        else opts.HardCasePath
              TrainingSetPath           = if String.IsNullOrWhiteSpace(opts.TrainingSetPath)   then "datasets/training-set.jsonl"      else opts.TrainingSetPath
              StatePath                 = if String.IsNullOrWhiteSpace(opts.StatePath)         then "datasets/.last-retrain.json"      else opts.StatePath
              ModelPath                 = if String.IsNullOrWhiteSpace(opts.ModelPath)         then "models/router.zip"               else opts.ModelPath
              PreviousModelPath         = if String.IsNullOrWhiteSpace(opts.PreviousModelPath) then "models/router.zip.prev"          else opts.PreviousModelPath
              RejectionLogPath          = if String.IsNullOrWhiteSpace(opts.RejectionLogPath)  then "logs/retraining-rejections.jsonl" else opts.RejectionLogPath
              HeldOutFraction           = if opts.HeldOutFraction           <= 0.0  then 0.2   else opts.HeldOutFraction
              HeldOutRandomSeed         = if opts.HeldOutRandomSeed         <= 0    then 42    else opts.HeldOutRandomSeed
              L2Regularization          = if opts.L2Regularization          <= 0.0f then 0.1f  else opts.L2Regularization }
        new RetrainingService(
            normalized,
            sp.GetRequiredService<IEmbedder>(),
            sp.GetRequiredService<IModelVersionProvider>(),
            sp.GetRequiredService<IRetrainLock>(),
            sp.GetRequiredService<ILogger<RetrainingService>>()))
    |> ignore

    services.AddHostedService<RetrainingService>(fun sp ->
        sp.GetRequiredService<RetrainingService>())
    |> ignore

    // ── Phase 13: Log retention ───────────────────────────────────────────────
    // PollIntervalMinutes (60), DatasetsDirectory ("datasets"), and TeacherCapRetentionDays (7)
    // are hardcoded constants — operator can lift to appsettings.json in a future minor change.
    // OperationalDirectory + OperationalRetentionDays read from Logging section (added 13-01).
    // DecisionDirectory + DecisionRetentionDays read from DecisionLog section.
    services.Configure<LogRetentionOptions>(fun (o: LogRetentionOptions) ->
        let logging    = config.GetSection("Logging")
        let decisionLog = config.GetSection("DecisionLog")
        o.OperationalDirectory     <- logging.["Directory"]     |> Option.ofObj |> Option.defaultValue "logs/operational"
        o.OperationalRetentionDays <- logging.["RetentionDays"] |> Option.ofObj |> Option.bind (fun s -> match Int32.TryParse(s) with | true, n -> Some n | _ -> None) |> Option.defaultValue 30
        o.DecisionDirectory        <- decisionLog.["Directory"]     |> Option.ofObj |> Option.defaultValue "logs/decisions"
        o.DecisionRetentionDays    <- decisionLog.["RetentionDays"] |> Option.ofObj |> Option.bind (fun s -> match Int32.TryParse(s) with | true, n -> Some n | _ -> None) |> Option.defaultValue 90
        o.DatasetsDirectory        <- "datasets"  // hardcoded; lift to config if needed
        o.TeacherCapRetentionDays  <- 7           // hardcoded; lift to config if needed
        o.PollIntervalMinutes      <- 60          // hardcoded; lift to config if needed
    ) |> ignore
    services.AddHostedService<LogRetentionService>() |> ignore

    services

// ── configureWithoutMl ───────────────────────────────────────────────────────

/// Register the subset of DI services needed for the --retrain offline path.
///
/// INCLUDES (common infrastructure):
///   - IOptions<UpstreamOptions> + named HttpClients (used by TeacherLabeler)
///   - IOptions<RoutingOptions> (needed by validateConfig in Program.fs)
///   - RoutingConfig (needed by validateConfig)
///   - Phase 7 retrain ports: IFailureDetector, ITeacherLabeler, IHardCaseDatasetWriter
///   - Phase 8 RetrainingOptions + IModelVersionProvider (needed by DatasetMerger)
///   - DecisionLogOptions + DecisionLogWriter triple-registration
///   - Health-probe HttpClient (for UpstreamOptions binding completeness)
///
/// EXCLUDES (ML request path only):
///   - ensureEmbeddingFilesPresent / ensureDummyModel
///   - BgeM3Embedder (IEmbedder)
///   - PredictionEnginePool / MlNetClassifier (IClassifier)
///   - makeApplyML / RoutingAlgorithmRegistration
///   - HealthService BackgroundService (probing only useful for live request path)
///   - CanaryService / CanaryMetrics / CanaryGate / CanaryWatchdog (canary needs request path)
///   - RetrainingService BackgroundService (--retrain runs synchronously; no PeriodicTimer)
///   - AddHttpContextAccessor / AddScopedFeatureManagement (no HTTP context in offline mode)
///   - RetrainLock (no concurrent retrain in --retrain mode; background service absent)
///   - IUpstreamClient / QueueDispatcher / IStatsProvider (no request routing in --retrain)
let configureWithoutMl (services: IServiceCollection) (config: IConfiguration) : IServiceCollection =
    // Option types
    services
        .Configure<UpstreamOptions>(config.GetSection("Upstreams"))
        .Configure<RoutingOptions>(config.GetSection("Routing"))
        |> ignore

    // Named HttpClients — TeacherLabeler needs "teacher"; FailureDetector reads local JSONL only.
    let upstreamOptsLazy = config.GetSection("Upstreams").Get<UpstreamOptions>()

    let buildUpstreamRetry () =
        let opts = HttpRetryStrategyOptions()
        opts.MaxRetryAttempts <- 3
        opts.BackoffType      <- DelayBackoffType.Exponential
        opts.Delay            <- TimeSpan.FromSeconds(1.0)
        opts.ShouldHandle     <-
            Func<RetryPredicateArguments<HttpResponseMessage>, ValueTask<bool>>(fun args ->
                let retry =
                    match args.Outcome.Exception with
                    | :? HttpRequestException -> true
                    | :? TaskCanceledException -> true
                    | null ->
                        let resp = args.Outcome.Result
                        not (isNull resp) && int resp.StatusCode >= 500
                    | _ -> false
                ValueTask.FromResult(retry))
        opts

    services.AddHttpClient("upstream35b")
        .ConfigureHttpClient(fun c ->
            c.BaseAddress <- Uri(upstreamOptsLazy.Model35B)
            c.Timeout     <- TimeSpan.FromSeconds(300.0))
        .AddResilienceHandler("upstream35b-pipeline", fun (builder: ResiliencePipelineBuilder<HttpResponseMessage>) ->
            builder.AddRetry(buildUpstreamRetry ()) |> ignore)
        |> ignore

    services.AddHttpClient("upstream122b")
        .ConfigureHttpClient(fun c ->
            c.BaseAddress <- Uri(upstreamOptsLazy.Model122B)
            c.Timeout     <- TimeSpan.FromSeconds(300.0))
        .AddResilienceHandler("upstream122b-pipeline", fun (builder: ResiliencePipelineBuilder<HttpResponseMessage>) ->
            builder.AddRetry(buildUpstreamRetry ()) |> ignore)
        |> ignore

    services.AddHttpClient("upstream35b-stream")
        .ConfigureHttpClient(fun c ->
            c.BaseAddress <- Uri(upstreamOptsLazy.Model35B)
            c.Timeout     <- TimeSpan.FromSeconds(300.0))
        |> ignore

    services.AddHttpClient("upstream122b-stream")
        .ConfigureHttpClient(fun c ->
            c.BaseAddress <- Uri(upstreamOptsLazy.Model122B)
            c.Timeout     <- TimeSpan.FromSeconds(300.0))
        |> ignore

    services.AddHttpClient("health-probe")
        .ConfigureHttpClient(fun c ->
            c.Timeout <- TimeSpan.FromSeconds(5.0))
        |> ignore

    // RoutingConfig (needed by validateConfig)
    services.AddSingleton<RoutingConfig>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<RoutingOptions>>().Value
        buildRoutingConfig opts)
        |> ignore

    // Phase 14: QualityFallbackOptions standalone singleton (mirrors configureRequestPipeline).
    services.AddSingleton<QualityFallbackOptions>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<RoutingOptions>>().Value
        normalizeQualityFallback opts)
        |> ignore

    // DecisionLog — same triple-reg as configureRequestPipeline.
    services.Configure<DecisionLogOptions>(config.GetSection("DecisionLog")) |> ignore

    services.AddSingleton<DecisionLogWriter>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<DecisionLogOptions>>().Value
        let dir = if String.IsNullOrWhiteSpace(opts.Directory) then "logs/decisions" else opts.Directory
        let cap = if opts.ChannelCapacity <= 0 then 10000 else opts.ChannelCapacity
        new DecisionLogWriter({ Directory = dir; ChannelCapacity = cap },
            sp.GetRequiredService<ILogger<DecisionLogWriter>>()))
    |> ignore

    services.AddSingleton<IDecisionLogger>(fun sp ->
        sp.GetRequiredService<DecisionLogWriter>() :> IDecisionLogger)
    |> ignore

    services.AddHostedService<DecisionLogWriter>(fun sp ->
        sp.GetRequiredService<DecisionLogWriter>())
    |> ignore

    // Phase 7: retrain ports
    services.Configure<TeacherLabelerOptions>(config.GetSection("TeacherLabeler")) |> ignore
    services.Configure<HardCaseDatasetOptions>(config.GetSection("HardCaseDataset")) |> ignore

    services.AddHttpClient("teacher", fun (c: System.Net.Http.HttpClient) ->
        let opts = config.GetSection("TeacherLabeler").Get<TeacherLabelerOptions>()
        let endpoint = if String.IsNullOrWhiteSpace(opts.Endpoint) then "http://127.0.0.1:8001" else opts.Endpoint
        let timeoutSec = if opts.TimeoutSeconds <= 0 then 30 else opts.TimeoutSeconds
        c.BaseAddress <- Uri(endpoint)
        c.Timeout     <- TimeSpan.FromSeconds(float timeoutSec))
        .AddResilienceHandler("teacher-pipeline", fun (builder: Polly.ResiliencePipelineBuilder<System.Net.Http.HttpResponseMessage>) ->
            let opts = config.GetSection("TeacherLabeler").Get<TeacherLabelerOptions>()
            let timeoutSec = if opts.TimeoutSeconds <= 0 then 30 else opts.TimeoutSeconds
            let retryOpts = HttpRetryStrategyOptions()
            retryOpts.MaxRetryAttempts <- 3
            retryOpts.BackoffType      <- DelayBackoffType.Exponential
            retryOpts.Delay            <- TimeSpan.FromSeconds(1.0)
            retryOpts.ShouldHandle     <-
                Func<RetryPredicateArguments<System.Net.Http.HttpResponseMessage>, System.Threading.Tasks.ValueTask<bool>>(
                    fun args ->
                        let retry =
                            match args.Outcome.Exception with
                            | :? System.Net.Http.HttpRequestException -> true
                            | :? System.Threading.Tasks.TaskCanceledException -> true
                            | null ->
                                let resp = args.Outcome.Result
                                not (isNull resp) && int resp.StatusCode >= 500
                            | _ -> false
                        System.Threading.Tasks.ValueTask.FromResult(retry))
            builder.AddRetry(retryOpts) |> ignore
            builder.AddTimeout(TimeSpan.FromSeconds(float timeoutSec)) |> ignore)
        |> ignore

    services.AddSingleton<FailureDetector>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<DecisionLogOptions>>().Value
        let dir = if String.IsNullOrWhiteSpace(opts.Directory) then "logs/decisions" else opts.Directory
        FailureDetector(dir, sp.GetRequiredService<ILogger<FailureDetector>>()))
    |> ignore

    services.AddSingleton<IFailureDetector>(fun sp ->
        sp.GetRequiredService<FailureDetector>() :> IFailureDetector)
    |> ignore

    services.AddSingleton<TeacherLabeler>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<TeacherLabelerOptions>>().Value
        let normalized =
            { Endpoint        = if String.IsNullOrWhiteSpace(opts.Endpoint)    then "http://127.0.0.1:8001"         else opts.Endpoint
              PromptPath      = if String.IsNullOrWhiteSpace(opts.PromptPath)  then "prompts/teacher-prompt.md"     else opts.PromptPath
              DailyCallCap    = if opts.DailyCallCap   <= 0                    then 1000                            else opts.DailyCallCap
              TimeoutSeconds  = if opts.TimeoutSeconds <= 0                    then 30                              else opts.TimeoutSeconds
              DatasetsDir     = if String.IsNullOrWhiteSpace(opts.DatasetsDir) then "datasets"                      else opts.DatasetsDir }
        TeacherLabeler(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(), normalized,
            sp.GetRequiredService<ILogger<TeacherLabeler>>()))
    |> ignore

    services.AddSingleton<ITeacherLabeler>(fun sp ->
        sp.GetRequiredService<TeacherLabeler>() :> ITeacherLabeler)
    |> ignore

    services.AddSingleton<HardCaseDatasetWriter>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<HardCaseDatasetOptions>>().Value
        let p   = if String.IsNullOrWhiteSpace(opts.Path) then "datasets/hard-cases.jsonl" else opts.Path
        let cap = if opts.ChannelCapacity <= 0 then 1000 else opts.ChannelCapacity
        new HardCaseDatasetWriter({ Path = p; ChannelCapacity = cap },
            sp.GetRequiredService<ILogger<HardCaseDatasetWriter>>()))
    |> ignore

    services.AddSingleton<IHardCaseDatasetWriter>(fun sp ->
        sp.GetRequiredService<HardCaseDatasetWriter>() :> IHardCaseDatasetWriter)
    |> ignore

    services.AddHostedService<HardCaseDatasetWriter>(fun sp ->
        sp.GetRequiredService<HardCaseDatasetWriter>())
    |> ignore

    // Phase 8: RetrainingOptions + IModelVersionProvider (DatasetMerger needs the embedder embed
    // callback; the provider tracks current model version for --retrain output logs).
    services.Configure<RetrainingOptions>(config.GetSection("Retraining")) |> ignore

    services.AddSingleton<ModelVersionProvider>(fun _sp ->
        // In --retrain mode there is no live PredictionEnginePool and we don't check router.zip.
        // Use a placeholder version; RetrainingService (if it runs) will update it after retrain.
        ModelVersionProvider("ml-retrain"))
    |> ignore

    services.AddSingleton<IModelVersionProvider>(fun sp ->
        sp.GetRequiredService<ModelVersionProvider>() :> IModelVersionProvider)
    |> ignore

    services

// ── configureServices (backwards-compat alias) ───────────────────────────────

// Compatibility alias: existing test fixtures (StreamingTests, LoggingTests,
// HealthFallbackTests) still call configureServices. They will be migrated to
// call configureWithoutMl explicitly in 12-05. After 12-05, this alias may be removed.
let configureServices (services: IServiceCollection) (config: IConfiguration) : IServiceCollection =
    configureRequestPipeline services config
