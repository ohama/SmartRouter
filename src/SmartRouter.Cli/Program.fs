module SmartRouter.Cli.Program

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Serilog
open SmartRouter.Cli.Adapters
open SmartRouter.Cli.Adapters.CorrelationMiddleware
open SmartRouter.Cli.Adapters.QueueDispatcher
open SmartRouter.Cli.CompositionRoot
open SmartRouter.Cli.Endpoints

[<EntryPoint>]
let main args =
    // Configure Serilog BEFORE WebApplication.CreateBuilder so even host startup
    // logs (e.g., "Now listening on...") go to stderr — OBS-04.
    Logging.configure ()

    try
        try
            let builder = WebApplication.CreateBuilder(args)

            // Wire Serilog as the ASP.NET host logger
            builder.Host.UseSerilog() |> ignore

            // Parse --routing-algorithm CLI flag (last-wins). Supports:
            //   --routing-algorithm=ml        (equals form)
            //   --routing-algorithm ml        (space form)
            // Reject empty/invalid values at startup before any service registration.
            let routingAlgorithmOverride : string option =
                args
                |> Array.tryFindIndexBack (fun a ->
                    a = "--routing-algorithm" || a.StartsWith("--routing-algorithm="))
                |> Option.map (fun idx ->
                    let arg = args.[idx]
                    if arg.StartsWith("--routing-algorithm=") then
                        let v = arg.["--routing-algorithm=".Length..]
                        if v = "" then
                            failwith "--routing-algorithm= requires a value (heuristic or ml)"
                        v
                    elif idx + 1 < args.Length then
                        let v = args.[idx + 1]
                        if v.StartsWith("--") then
                            failwith "--routing-algorithm requires a value (heuristic or ml)"
                        v
                    else
                        failwith "--routing-algorithm requires a value (heuristic or ml)")

            // Validate the value and inject into configuration BEFORE configureServices.
            match routingAlgorithmOverride with
            | None -> ()
            | Some v ->
                match v with
                | "heuristic" | "ml" -> ()
                | other ->
                    failwithf
                        "--routing-algorithm=%s is invalid; valid values: heuristic, ml"
                        other
                (builder.Configuration :> IConfigurationBuilder)
                    .AddInMemoryCollection(
                        dict [ "Routing:Algorithm", v ])
                |> ignore

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

            app.Run()
            0
        with ex ->
            Log.Fatal(ex, "Host terminated unexpectedly")
            1
    finally
        Logging.shutdown ()
