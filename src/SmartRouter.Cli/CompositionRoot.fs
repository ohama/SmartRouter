module SmartRouter.Cli.CompositionRoot

open System
open System.Collections.Generic
open System.Net.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Serilog
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports
open SmartRouter.Core.Routing
open SmartRouter.Cli.Adapters.DecisionLogger
open SmartRouter.Cli.Adapters.DecisionLogWriter
open SmartRouter.Cli.Adapters.RoutingAlgorithm
open SmartRouter.Cli.Adapters.QwenUpstreamClient
open SmartRouter.Cli.Adapters.QueueDispatcher

// ── JSON-binding types (Cli-only) ────────────────────────────────────────────

/// A single task table entry as it appears in appsettings.json.
/// Core uses (ModelId * Priority) tuples — this is only the JSON binding shape.
[<CLIMutable>]
type TaskTableEntry =
    { Model    : string   // "35b" | "122b" | any model alias
      Priority : string } // "high" | "low"

/// Full routing configuration as it appears in appsettings.json "Routing" section.
/// Cli-only: Core uses the pure RoutingConfig record from Domain.fs.
[<CLIMutable>]
type RoutingOptions =
    { Algorithm           : string   // "heuristic" (default) | "ml"; null when key absent
      ComplexityThreshold : int
      TimeoutSeconds      : int
      MlThreshold         : float32  // Phase 6: ML decision threshold; defaults to 0.5 when absent
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

    { ComplexityThreshold = opts.ComplexityThreshold
      Keywords            = List.ofArray opts.Keywords
      TaskTable           = taskMap
      MlThreshold         = if opts.MlThreshold = 0.0f then 0.5f else opts.MlThreshold }

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

    // RoutingAlgorithmRegistration as a DI singleton — pairs the algorithm function
    // with its name and model_version so the endpoint can populate DecisionLog.
    // null | "" | "heuristic" -> applyHeuristic / "heuristic" / "heuristic-v1"
    // "ml"                    -> applyML        / "ml"        / "ml-v0-placeholder"
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
                { Algorithm    = SmartRouter.Core.ML.applyML
                  Name         = "ml"
                  ModelVersion = "ml-v0-placeholder" }
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

    services
