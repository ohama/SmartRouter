module SmartRouter.Cli.CompositionRoot

open System
open System.Collections.Generic
open System.Net.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.ML
open Microsoft.Extensions.Options
open Serilog
open SmartRouter.Core.Domain
open SmartRouter.Core.MLPorts
open SmartRouter.Core.Ports
open SmartRouter.Core.Routing
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
    { Algorithm           : string   // "heuristic" (default) | "ml"; null when key absent
      ComplexityThreshold : int
      TimeoutSeconds      : int
      ML                  : MlOptions  // Phase 6: Routing.ML subsection; null when section absent
      Keywords            : string[]
      TaskTable           : Dictionary<string, TaskTableEntry>
      ModelAliases        : Dictionary<string, string> }

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
        // Defensive default: heuristic-only deployments can omit Routing.ML entirely;
        // CLIMutable float32 defaults to 0.0f when JSON key absent.
        if obj.ReferenceEquals(opts.ML, null) then 0.5f
        else if opts.ML.Threshold = 0.0f then 0.5f
        else opts.ML.Threshold

    { ComplexityThreshold = opts.ComplexityThreshold
      Keywords            = List.ofArray opts.Keywords
      TaskTable           = taskMap
      MlThreshold         = mlThreshold }

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

// ── configureServices ────────────────────────────────────────────────────────

/// Register all DI services for the router.
///
/// Key registrations:
///   - IOptions<UpstreamOptions>  bound from "Upstreams" section
///   - IOptions<RoutingOptions>   bound from "Routing" section
///   - Named HttpClients "upstream35b" / "upstream122b" with 300s timeout (CONC-07)
///   - RoutingConfig as DI singleton (the ROUT-05 wiring proof: endpoint resolves this
///     and passes to Routing.routeRequest; editing appsettings.json + restart rebuilds it)
///   - IUpstreamClient as DI singleton (ARCH-06: stateless; lazy probe cache is lifetime)
let configureServices (services: IServiceCollection) (config: IConfiguration) : IServiceCollection =
    // Bind option types from configuration sections
    services
        .Configure<UpstreamOptions>(config.GetSection("Upstreams"))
        .Configure<RoutingOptions>(config.GetSection("Routing"))
        |> ignore

    // Named HttpClients per upstream — 300s timeout covers 122B cold starts (CONC-07 / PITFALL-12)
    services.AddHttpClient("upstream35b", fun c ->
        let upstreamOpts = config.GetSection("Upstreams").Get<UpstreamOptions>()
        c.BaseAddress <- Uri(upstreamOpts.Model35B)
        c.Timeout     <- TimeSpan.FromSeconds(300.0))
        |> ignore

    services.AddHttpClient("upstream122b", fun c ->
        let upstreamOpts = config.GetSection("Upstreams").Get<UpstreamOptions>()
        c.BaseAddress <- Uri(upstreamOpts.Model122B)
        c.Timeout     <- TimeSpan.FromSeconds(300.0))
        |> ignore

    // RoutingConfig as a DI singleton — built once at composition time from RoutingOptions.
    // The endpoint retrieves this and passes it into Routing.routeRequest. THIS is the wiring
    // that satisfies ROUT-05: editing appsettings.json + restart rebuilds this singleton,
    // which changes runtime routing behavior without recompiling.
    services.AddSingleton<RoutingConfig>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<RoutingOptions>>().Value
        buildRoutingConfig opts)
        |> ignore

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
    // DEVIATION (Rule 3 — blocking fix): guard the ML block on Routing.Algorithm = "ml".
    // The --retrain offline path (Program.fs) reuses configureServices but does not need
    // IEmbedder/IClassifier; without this guard, ensureEmbeddingFilesPresent always fires
    // even in heuristic mode or when --retrain bypasses the Kestrel host. Guard makes the
    // ML wiring conditional on intent, matching the RoutingAlgorithmRegistration "ml" branch.
    let routingAlgoStr =
        let routingOpts = config.GetSection("Routing").Get<RoutingOptions>()
        if obj.ReferenceEquals(routingOpts, null) then "heuristic"
        else if String.IsNullOrWhiteSpace(routingOpts.Algorithm) then "heuristic"
        else routingOpts.Algorithm
    let mlOpts = config.GetSection("Routing:ML").Get<MlOptions>()
    if not (obj.ReferenceEquals(mlOpts, null)) && routingAlgoStr = "ml" then
        ensureEmbeddingFilesPresent mlOpts.EmbeddingModelPath mlOpts.TokenizerPath
        ensureDummyModel mlOpts.ModelPath

        services
            .AddPredictionEnginePool<RouteInput, RoutePrediction>()
            .FromFile(
                modelName       = "router",
                filePath        = mlOpts.ModelPath,
                watchForChanges = true)
            |> ignore

        // BgeM3Embedder — singleton; warm-up runs at construction.
        services.AddSingleton<IEmbedder>(fun _sp ->
            new BgeM3Embedder(
                mlOpts.EmbeddingModelPath,
                mlOpts.TokenizerPath,
                mlOpts.MaxTokens) :> IEmbedder)
            |> ignore

        services.AddSingleton<IClassifier>(fun sp ->
            let pool = sp.GetRequiredService<PredictionEnginePool<RouteInput, RoutePrediction>>()
            MlNetClassifier(pool) :> IClassifier)
            |> ignore

    // RoutingAlgorithmRegistration as a DI singleton — pairs the algorithm function
    // with its name and model_version so the endpoint can populate DecisionLog.
    // null | "" | "heuristic" -> applyHeuristic / "heuristic" / "heuristic-v1"
    // "ml"                    -> makeApplyML closure / "ml" / "ml-{8hexchars}"
    // other                   -> InvalidOperationException at startup (fail-fast)
    services.AddSingleton<RoutingAlgorithmRegistration>(
        Func<IServiceProvider, RoutingAlgorithmRegistration>(fun sp ->
            let opts = sp.GetRequiredService<IOptions<RoutingOptions>>().Value
            match opts.Algorithm with
            | null | "" | "heuristic" ->
                { Algorithm    = SmartRouter.Core.Heuristic.applyHeuristic
                  Name         = "heuristic"
                  ModelVersion = "heuristic-v1" }
            | "ml" ->
                // ML branch — resolve adapters once; close over them in the makeApplyML factory.
                let embedder   = sp.GetRequiredService<IEmbedder>()
                let classifier = sp.GetRequiredService<IClassifier>()
                let mlPath     = opts.ML.ModelPath
                let modelHash  = computeModelVersion mlPath
                { Algorithm    = SmartRouter.Core.ML.makeApplyML embedder classifier
                  Name         = "ml"
                  ModelVersion = sprintf "ml-%s" modelHash }
            | other ->
                let msg =
                    sprintf
                        "appsettings.json Routing.Algorithm = \"%s\" is invalid; valid values: \"heuristic\", \"ml\""
                        other
                raise (System.InvalidOperationException(msg))))
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
            sp.GetRequiredService<IOptions<UpstreamOptions>>()))
        |> ignore

    // QueueDispatcher wraps QwenUpstreamClient — registered as concrete singleton plus
    // two interface registrations (IUpstreamClient for the endpoint; IStatsProvider for /stats).
    services.AddSingleton<QueueDispatcher>(fun sp ->
        QueueDispatcher(
            sp.GetRequiredService<QwenUpstreamClient>() :> IUpstreamClient,
            sp.GetRequiredService<IOptions<QueueDispatcherOptions>>().Value))
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
        new DecisionLogWriter({ Directory = dir; ChannelCapacity = cap }))
    |> ignore

    services.AddSingleton<IDecisionLogger>(fun sp ->
        sp.GetRequiredService<DecisionLogWriter>() :> IDecisionLogger)
    |> ignore

    services.AddHostedService<DecisionLogWriter>(fun sp ->
        sp.GetRequiredService<DecisionLogWriter>())
    |> ignore

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
        FailureDetector(dir))
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
        TeacherLabeler(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(), normalized))
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
        new HardCaseDatasetWriter({ Path = p; ChannelCapacity = cap }))
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
    // "ml-{8hexchars}" in ML mode; "heuristic-v1" in heuristic mode.
    // After each successful retrain, RetrainingService.Update flips this value;
    // the next ChatCompletions request emits the new model_version.
    services.AddSingleton<ModelVersionProvider>(fun sp ->
        let routingOpts =
            sp.GetRequiredService<IOptions<RoutingOptions>>().Value
        let initial =
            // Heuristic mode has no model file — use the static "heuristic-v1" string.
            // ML mode: load router.zip and compute SHA prefix (matches RoutingAlgorithmRegistration).
            match routingOpts.Algorithm with
            | "ml" when not (obj.ReferenceEquals(routingOpts.ML, null)) ->
                sprintf "ml-%s" (computeModelVersion routingOpts.ML.ModelPath)
            | _ ->
                "heuristic-v1"
        ModelVersionProvider(initial))
    |> ignore

    services.AddSingleton<IModelVersionProvider>(fun sp ->
        sp.GetRequiredService<ModelVersionProvider>() :> IModelVersionProvider)
    |> ignore

    // RetrainingService — double-registration pattern (concrete AddSingleton + AddHostedService factory).
    // Guarded on routingAlgoStr = "ml" because the constructor requires IEmbedder (only registered
    // in ML mode above). Heuristic mode needs no retraining.
    // No IInterface alias since RetrainingService has no interface consumer — only IHostedService
    // machinery and the test-only RunNowAsync seam consume it directly.
    if routingAlgoStr = "ml" then
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
                sp.GetRequiredService<IModelVersionProvider>()))
        |> ignore

        services.AddHostedService<RetrainingService>(fun sp ->
            sp.GetRequiredService<RetrainingService>())
        |> ignore

    services
