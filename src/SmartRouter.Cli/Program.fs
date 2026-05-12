module SmartRouter.Cli.Program

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open Microsoft.Extensions.Logging
open System
open System.IO
open System.Net
open Serilog
open Serilog.Events
open SmartRouter.Cli.Adapters
open SmartRouter.Cli.Adapters.CanaryWatchdog
open SmartRouter.Cli.Adapters.CorrelationMiddleware
open SmartRouter.Cli.Adapters.QueueDispatcher
open SmartRouter.Cli.Adapters.RoutingAlgorithm
open SmartRouter.Cli.Adapters.TeacherLabeler
open SmartRouter.Cli.Adapters.PortProbe
open SmartRouter.Cli.CompositionRoot
open SmartRouter.Cli.Endpoints
open SmartRouter.Core.RetrainingPorts

// Phase 13 Q8: map string → Serilog LogEventLevel with short aliases.
let private parseLogLevel (raw: string) : LogEventLevel =
    match raw.ToLowerInvariant() with
    | "verbose" | "vrb"                   -> LogEventLevel.Verbose
    | "debug"   | "dbg"                   -> LogEventLevel.Debug
    | "information" | "info" | "inf"      -> LogEventLevel.Information
    | "warning" | "warn" | "wrn"          -> LogEventLevel.Warning
    | "error"   | "err"                   -> LogEventLevel.Error
    | "fatal"   | "ftl"                   -> LogEventLevel.Fatal
    | other ->
        failwithf
            "--log-level=%s invalid; valid: verbose|debug|information|warning|error|fatal (or vrb/dbg/info/warn/err/ftl aliases)"
            other

/// Issue #3: parse --port flag (1024..65535). Returns None if absent.
/// Caller is responsible for failing fast on out-of-range values.
let private parsePortFromArgs (args: string array) : int option =
    args
    |> Array.tryFindIndex (fun a -> a = "--port" || a.StartsWith("--port="))
    |> Option.map (fun idx ->
        let raw =
            if args.[idx].StartsWith("--port=") then
                let v = args.[idx].["--port=".Length..]
                if v = "" then failwith "--port= requires a value"
                v
            elif idx + 1 < args.Length then
                let v = args.[idx + 1]
                if v.StartsWith("--") then failwith "--port requires a value (e.g., 4001)"
                v
            else failwith "--port requires a value"
        match System.Int32.TryParse(raw) with
        | true, n when n >= 1024 && n <= 65535 -> n
        | true, n ->
            failwithf "--port=%d out of range; must be 1024..65535" n
        | _ ->
            failwithf "--port=%s is not a valid integer" raw)

/// Phase 14 — `--trace-responses` flag detection. When the flag is present,
/// inject `Trace:Enabled=true` into the per-branch IConfigurationBuilder BEFORE
/// configureRequestPipeline / configureWithoutMl runs. CompositionRoot reads
/// this key to conditionally register ITraceLogger (triple-registration pattern:
/// concrete + interface alias + AddHostedService). Has no effect on --retrain
/// branch since configureWithoutMl omits ITraceLogger registration.
let private applyTraceFlagFromArgs (configBuilder: IConfigurationBuilder) (args: string array) : unit =
    if args |> Array.contains "--trace-responses" then
        configBuilder.AddInMemoryCollection(dict [ "Trace:Enabled", "true" ]) |> ignore
        Log.Information("Trace logging enabled via --trace-responses CLI flag; output: logs/trace/{Date}.jsonl")

/// Detect --trace (migration guard) and apply --log-level if present.
/// Called in BOTH the --retrain branch and the main Kestrel branch.
let private applyLogLevelFromArgs (args: string array) : unit =
    // Migration guard: --trace was removed in Phase 13.
    if args |> Array.contains "--trace" then
        failwith "--trace flag was removed in Phase 13; use --log-level=debug instead"

    let logLevelArg =
        args |> Array.tryFindIndex (fun a -> a = "--log-level" || a.StartsWith("--log-level="))
        |> Option.map (fun idx ->
            let raw =
                if args.[idx].StartsWith("--log-level=") then
                    let v = args.[idx].["--log-level=".Length..]
                    if v = "" then failwith "--log-level= requires a value"
                    v
                elif idx + 1 < args.Length then
                    let v = args.[idx + 1]
                    if v.StartsWith("--") then failwith "--log-level requires a value (e.g., debug)"
                    v
                else failwith "--log-level requires a value"
            parseLogLevel raw)

    match logLevelArg with
    | Some level ->
        Logging.setLevel level
        Log.Information("Log level set to {Level} via --log-level CLI flag", level)
    | None -> ()  // levelSwitch default (Information) set by Logging.fs init

/// Phase 25 — extract (host, port) from a Kestrel URL string like
/// "http://127.0.0.1:4000". Returns None when the URL fails to parse or
/// doesn't contain a port. The port-probe step is best-effort: if we
/// can't parse the URL, we skip the probe and let Kestrel surface its
/// own error (no regression from pre-Phase-25 behavior).
let private parseListenUrl (urlStr: string) : (string * int) option =
    match Uri.TryCreate(urlStr, UriKind.Absolute) with
    | true, uri when uri.Port > 0 -> Some (uri.Host, uri.Port)
    | _ -> None

/// Phase 25 — is the host string a loopback address? Probe only loopback
/// listeners (PROBE-02). Non-loopback URLs (`0.0.0.0`, `*`, public IPs,
/// hostnames other than localhost) are skipped with a debug log line.
let private isLoopbackHost (host: string) : bool =
    match host with
    | "localhost" | "127.0.0.1" | "::1" -> true
    | other ->
        match IPAddress.TryParse(other) with
        | true, ip -> IPAddress.IsLoopback(ip)
        | _ -> false

[<EntryPoint>]
let main args =
    try
        try
            // Phase 14: --cold-start MUST run BEFORE configureServices (ensureDummyModel
            // sees the post-backup state). Build a minimal bootstrap config to drive
            // Logging.configure once; per-branch builders re-read the same appsettings.json.
            let bootstrapConfig =
                ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional = true)
                    .Build() :> IConfiguration
            Logging.configure(bootstrapConfig)
            applyLogLevelFromArgs args

            if args |> Array.contains "--cold-start" then
                use coldStartFactory =
                    LoggerFactory.Create(fun b ->
                        b.AddSerilog(Log.Logger, dispose = false) |> ignore)
                let coldStartLogger = coldStartFactory.CreateLogger("ColdStart")
                Adapters.ColdStart.runColdStartBackup coldStartLogger System.Environment.CurrentDirectory
                // Backup complete; startup continues — ensureDummyModel sees no router.zip
                // and generates a fresh dummy model. Process does NOT exit here.

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
                // Logging.configure and applyLogLevelFromArgs already called in the Phase 14
                // bootstrap preamble above; no need to repeat here.
                // configureWithoutMl skips IEmbedder/IClassifier/RoutingAlgorithmRegistration/ML model checks.
                // The retrain pipeline needs IFailureDetector + ITeacherLabeler + IHardCaseDatasetWriter only.
                // Phase 14: inject Trace:Enabled=true if --trace-responses present (no-op in retrain path;
                // configureWithoutMl does not register ITraceLogger, but the key is harmless to inject).
                applyTraceFlagFromArgs (retrainBuilder.Configuration :> IConfigurationBuilder) args
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

            // Logging.configure and applyLogLevelFromArgs already called in the Phase 14
            // bootstrap preamble above. builder.Configuration (appsettings.json) has the same
            // values as bootstrapConfig — both read the same file from the same basePath.

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

            // Issue #3: --port CLI override (precedence above appsettings.json).
            // Inject into IConfiguration so the existing Kestrel:Endpoints:Http:Url binding
            // picks it up. Bind address stays 127.0.0.1 (OPS-04 — no firewall surprises).
            match parsePortFromArgs args with
            | Some port ->
                (builder.Configuration :> IConfigurationBuilder)
                    .AddInMemoryCollection(
                        dict [ "Kestrel:Endpoints:Http:Url", sprintf "http://127.0.0.1:%d" port ])
                |> ignore
                Log.Information("Listening port overridden to {Port} via --port CLI flag", port)
            | None -> ()

            // Phase 14: inject Trace:Enabled=true if --trace-responses present.
            // Must run BEFORE configureServices so CompositionRoot.configureRequestPipeline
            // can read Trace:Enabled from builder.Configuration.
            applyTraceFlagFromArgs (builder.Configuration :> IConfigurationBuilder) args

            // Phase 25 — fail-fast port-conflict probe (PROBE-02 + PROBE-03).
            // Run AFTER --port CLI override merge and AFTER trace-flag merge so
            // the probe sees the final effective listen URL. Run BEFORE
            // CompositionRoot.configureServices and BEFORE builder.Build() so
            // Kestrel never attempts to bind. On conflict: stderr write + exit 1.
            let listenUrlForProbe =
                builder.Configuration.["Kestrel:Endpoints:Http:Url"]
                |> Option.ofObj |> Option.defaultValue "http://localhost:4000"
            match parseListenUrl listenUrlForProbe with
            | None ->
                Log.Debug("Port probe skipped: listen URL {Url} did not parse", listenUrlForProbe)
            | Some (host, _) when not (isLoopbackHost host) ->
                Log.Debug("Port probe skipped: host {Host} is not loopback (only 127.0.0.1/::1/localhost are probed)", host)
            | Some (_, port) ->
                match PortProbe.tryBind port IPAddress.Loopback with
                | Ok () -> ()  // port free; continue to Kestrel build
                | Error err ->
                    // PROBE-03: write to Console.Error directly (NOT through
                    // Serilog — must be visible even if logging hasn't initialized
                    // or hits its own error). Fixed 4-line format per REQUIREMENTS.md.
                    // No stacktrace. err.Reason is intentionally NOT included in
                    // the operator-facing block — it lives in the debug log only.
                    Log.Debug("Port probe rejected port {Port}: {Reason}", err.Port, err.Reason)
                    eprintfn "ERROR: Port %d is already in use. Smart Router cannot start." err.Port
                    eprintfn "Likely culprit: another smart-router instance, or a different process bound to :%d." err.Port
                    eprintfn "To investigate: `lsof -iTCP:%d -sTCP:LISTEN -n -P`" err.Port
                    eprintfn "To stop a stuck launchd instance: `launchctl unload ~/Library/LaunchAgents/com.ohama.smartrouter.plist`"
                    Logging.shutdown ()  // flush any pending log writes before exit
                    Environment.Exit(1)

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
            // Phase 22 (MIG-02): fingerprintEnabled parameter removed; v2.0 IP+UA fingerprint
            // superseded by the three-tier session cascade in ChatCompletions.fs.
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

            // Phase 13 — startup banner. Operators reading the operational log see a
            // single proof-of-startup snapshot: which port, model version, canary state,
            // queue config, etc. Helps confirm the binary they tail matches expectations.
            let startupBanner =
                let regn         = app.Services.GetRequiredService<RoutingAlgorithmRegistration>()
                let vp           = app.Services.GetRequiredService<IModelVersionProvider>()
                let queueOpts    = app.Services.GetRequiredService<IOptions<QueueDispatcherOptions>>().Value
                let teacherOpts  = app.Services.GetRequiredService<IOptions<TeacherLabelerOptions>>().Value
                let canaryOpts   = app.Services.GetRequiredService<IOptions<CanaryOptions>>().Value
                let logging      = builder.Configuration.GetSection("Logging")
                let logDir       = logging.["Directory"] |> Option.ofObj |> Option.defaultValue "logs/operational"
                let listenUrl    = builder.Configuration.["Kestrel:Endpoints:Http:Url"]
                                   |> Option.ofObj |> Option.defaultValue "http://localhost:4000"
                let canaryVer    = vp.CanaryVersion
                let canaryVerStr = if System.String.IsNullOrWhiteSpace(canaryVer) then "(none)" else canaryVer
                sprintf "SmartRouter starting\n    listen            = %s\n    routing.algorithm = %s\n    model.version     = %s\n    canary.version    = %s\n    canary.percent    = %d\n    queue.maxconc.122B = %d\n    queue.fairnessK   = %d\n    teacher.cap.daily = %d\n    log.dir           = %s"
                    listenUrl regn.Name regn.ModelVersion canaryVerStr
                    canaryOpts.PercentageEnabled queueOpts.MaxConcurrent122B queueOpts.FairnessK
                    teacherOpts.DailyCallCap logDir
            let bannerLogger =
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup")
            bannerLogger.LogInformation("{Banner}", startupBanner)

            // Phase 13 — shutdown banner via ApplicationStopping.
            // Fires synchronously on graceful shutdown; Logging.shutdown() in the outer
            // finally block flushes Serilog so the banner reaches the rolling file sink.
            let lifetime       = app.Services.GetRequiredService<IHostApplicationLifetime>()
            let shutdownLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Shutdown")
            lifetime.ApplicationStopping.Register(fun () ->
                let statsProvider = app.Services.GetRequiredService<IStatsProvider>()
                let snap          = statsProvider.GetSnapshot()
                let inFlight      = snap.Active35B + snap.Active122B
                let queueDepth    = snap.QueueDepth122BHigh + snap.QueueDepth122BLow
                shutdownLogger.LogInformation(
                    "SmartRouter stopping; in-flight={InFlight} queue.depth.total={QueueDepth} queue.depth.high={H} queue.depth.low={L}",
                    inFlight, queueDepth, snap.QueueDepth122BHigh, snap.QueueDepth122BLow))
            |> ignore

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
