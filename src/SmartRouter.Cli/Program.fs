module SmartRouter.Cli.Program

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Serilog
open SmartRouter.Cli.Adapters
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

            // Serilog request logging middleware
            app.UseSerilogRequestLogging() |> ignore

            // Register POST /v1/chat/completions
            ChatCompletions.mapEndpoints app

            app.Run()
            0
        with ex ->
            Log.Fatal(ex, "Host terminated unexpectedly")
            1
    finally
        Logging.shutdown ()
