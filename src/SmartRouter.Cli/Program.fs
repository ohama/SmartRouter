module SmartRouter.Cli.Program

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open Microsoft.Extensions.Logging
open Serilog
open SmartRouter.Cli.Adapters
open SmartRouter.Cli.Adapters.CorrelationMiddleware
open SmartRouter.Cli.Adapters.QueueDispatcher
open SmartRouter.Cli.CompositionRoot
open SmartRouter.Cli.Endpoints

[<EntryPoint>]
let main args =
    try
        try
            // Phase 7: --retrain CLI command — runs the offline labeling pipeline and exits.
            // Does NOT start the Kestrel host. Useful for operator-driven manual retraining
            // and CI-friendly testing. Phase 8's BackgroundService composes the same DI
            // singletons on a PeriodicTimer.
            if args |> Array.contains "--retrain" then
                let retrainBuilder = Host.CreateApplicationBuilder(args)
                retrainBuilder.Configuration
                               .SetBasePath(System.IO.Directory.GetCurrentDirectory())
                               .AddJsonFile("appsettings.json", optional = false)
                    |> ignore
                // Configure Serilog from IConfiguration now that appsettings.json is loaded.
                Logging.configure(retrainBuilder.Configuration)
                // configureWithoutMl skips IEmbedder/IClassifier/RoutingAlgorithmRegistration/ML model checks.
                // The retrain pipeline needs IFailureDetector + ITeacherLabeler + IHardCaseDatasetWriter only.
                CompositionRoot.configureWithoutMl retrainBuilder.Services retrainBuilder.Configuration |> ignore
                use host = retrainBuilder.Build()
                do host.StartAsync().GetAwaiter().GetResult()
                try
                    let detector = host.Services.GetRequiredService<SmartRouter.Core.RetrainingPorts.IFailureDetector>()
                    let labeler  = host.Services.GetRequiredService<SmartRouter.Core.RetrainingPorts.ITeacherLabeler>()
                    let writer   = host.Services.GetRequiredService<SmartRouter.Core.RetrainingPorts.IHardCaseDatasetWriter>()
                    let logger   = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Retrain")
                    let ct = System.Threading.CancellationToken.None

                    let hardCases =
                        detector.ExtractHardCases(ct).GetAwaiter().GetResult()

                    logger.LogInformation("Retrain: extracted {N} hard case(s)", List.length hardCases)

                    let mutable labeled  = 0
                    let mutable skipped  = 0
                    let mutable failed   = 0
                    for hc in hardCases do
                        match hc.PromptText with
                        | None ->
                            logger.LogInformation(
                                "Retrain: skipping correlation_id={Cid} — prompt text not in logs (LOG-01 schema; Phase 8 BackgroundService passes inline)",
                                hc.CorrelationId)
                            skipped <- skipped + 1
                        | Some pt ->
                            let result = labeler.LabelAsync(pt, hc.CorrelationId, ct).GetAwaiter().GetResult()
                            match result with
                            | SmartRouter.Core.RetrainingPorts.Labeled (label, excerpt) ->
                                let labelInt =
                                    match label with
                                    | SmartRouter.Core.RetrainingPorts.Route35B  -> 0
                                    | SmartRouter.Core.RetrainingPorts.Route122B -> 1
                                let target =
                                    match label with
                                    | SmartRouter.Core.RetrainingPorts.Route35B  -> "Qwen35B"
                                    | SmartRouter.Core.RetrainingPorts.Route122B -> "Qwen122B"
                                let entry : SmartRouter.Core.RetrainingPorts.HardCaseEntry =
                                    { SchemaVersion          = 1
                                      CorrelationId          = hc.CorrelationId
                                      PromptHash             = hc.PromptHash
                                      PromptText             = pt
                                      Label                  = labelInt
                                      Source                 = "teacher"
                                      TeacherResponseExcerpt = Some excerpt
                                      LabeledAt              = System.DateTimeOffset.UtcNow
                                      PromptKoreanCharRatio  = hc.PromptKoreanCharRatio
                                      RoutingAlgorithm       = hc.RoutingAlgorithm
                                      Target                 = target }
                                writer.AppendAsync(entry, ct).GetAwaiter().GetResult()
                                labeled <- labeled + 1
                            | SmartRouter.Core.RetrainingPorts.Unparseable raw ->
                                logger.LogWarning("Retrain: unparseable response for {Cid}: {Raw}", hc.CorrelationId, raw)
                                failed <- failed + 1
                            | SmartRouter.Core.RetrainingPorts.Skipped reason ->
                                logger.LogInformation("Retrain: skipped {Cid} — {Reason}", hc.CorrelationId, reason)
                                skipped <- skipped + 1
                            | SmartRouter.Core.RetrainingPorts.Failed err ->
                                logger.LogWarning("Retrain: failed {Cid} — {Err}", hc.CorrelationId, err)
                                failed <- failed + 1

                    if List.isEmpty hardCases then
                        printfn "Retrain: 0 hard cases found; run scripts/seed-hard-cases.fsx for synthetic data."
                    else
                        printfn "Retrain: %d labeled, %d skipped, %d failed of %d total"
                            labeled skipped failed (List.length hardCases)
                finally
                    host.StopAsync().GetAwaiter().GetResult()
                Logging.shutdown ()
                exit 0

            let builder = WebApplication.CreateBuilder(args)

            // Configure Serilog from IConfiguration (dual sink: Console stderr + rolling File).
            // Must run AFTER builder creation so IConfiguration (appsettings.json) is available.
            // Pre-init crash window writes to stderr via the outer `with` eprintfn fallback.
            Logging.configure(builder.Configuration)

            // Wire Serilog as the ASP.NET host logger
            builder.Host.UseSerilog() |> ignore

            // Phase 9 Lock 13: keep Canary.PercentageEnabled in sync with feature_management's
            // DefaultRolloutPercentage so operator edits to one section reflect in the other at startup.
            // Skip silently if Canary section absent from appsettings.json.
            let canaryPctRaw = builder.Configuration.["Canary:PercentageEnabled"]
            match System.Int32.TryParse(if isNull canaryPctRaw then "" else canaryPctRaw) with
            | true, n when n >= 0 && n <= 100 ->
                (builder.Configuration :> IConfigurationBuilder)
                    .AddInMemoryCollection(
                        dict [ "feature_management:feature_flags:0:conditions:client_filters:0:parameters:Audience:DefaultRolloutPercentage", string n ])
                |> ignore
            | _ -> ()

            // Register all DI services: named HttpClients, IUpstreamClient, RoutingConfig
            CompositionRoot.configureServices builder.Services builder.Configuration
            |> ignore

            let app = builder.Build()

            // Validate routing config now that DI container is built (fails fast on typos)
            let routingOpts = app.Services.GetRequiredService<IOptions<RoutingOptions>>().Value
            CompositionRoot.validateConfig routingOpts

            // Validate queue options — MaxConcurrent122B must be 1 in v1 (correctness invariant).
            // QueueDispatcher constructor also throws, but this gives a friendlier startup message.
            let queueOpts = app.Services.GetRequiredService<IOptions<QueueDispatcherOptions>>().Value
            if queueOpts.MaxConcurrent122B <> 1 then
                failwithf
                    "appsettings.json Queue.MaxConcurrent122B must be 1 in v1 (correctness invariant); got %d"
                    queueOpts.MaxConcurrent122B

            // Ensure logs directory exists at startup so DecisionLogWriter never races on first write
            System.IO.Directory.CreateDirectory("logs/decisions") |> ignore

            // Correlation ID middleware — runs FIRST in the pipeline so every downstream
            // log line and the JSONL DecisionLog entry carry the same correlation_id.
            app.Use(System.Func<HttpContext, RequestDelegate, System.Threading.Tasks.Task>(fun ctx next ->
                CorrelationMiddleware.correlationMiddleware ctx next)) |> ignore

            // Serilog request logging middleware
            app.UseSerilogRequestLogging() |> ignore

            // Register POST /v1/chat/completions
            ChatCompletions.mapEndpoints app
            // Register GET /stats (OBS-02 / API-07)
            Stats.mapEndpoints app
            // Register GET /canary + POST /canary/{promote,rollback,enable} — Phase 9
            Canary.mapEndpoints app
            // Register GET /health — Phase 10 (API-05)
            SmartRouter.Cli.Endpoints.Health.mapEndpoints app
            // Register GET /v1/models — Phase 11 (parallel fetch + dedupe + IHealthProbe gating)
            SmartRouter.Cli.Endpoints.Models.mapEndpoints app

            app.Run()
            0
        with ex ->
            // If Logging.configure has already run, this reaches Serilog.
            // If it failed before configure(), eprintfn ensures launchd captures it in smart-router.err.
            eprintfn "Fatal: %s" ex.Message
            Log.Fatal(ex, "Host terminated unexpectedly")
            1
    finally
        Logging.shutdown ()
