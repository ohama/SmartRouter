module SmartRouter.Cli.Adapters.Logging

open System
open System.IO
open Microsoft.Extensions.Configuration
open Serilog
open Serilog.Core
open Serilog.Events

/// Module-level switch controlling Serilog's minimum level.
/// CLI --log-level (Phase 13, plan 13-04) flips this; default Information.
let levelSwitch: LoggingLevelSwitch = LoggingLevelSwitch(LogEventLevel.Information)

/// Output template — request-scope logs render [{correlation_id}], background logs render [-].
let private outputTemplate =
    "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Level:u3}] {SourceContext} [{correlation_id}] {Message:lj}{NewLine}{Exception}"

/// Initialize the static Serilog Log.Logger from IConfiguration.
/// Reads appsettings.json:Serilog (MinimumLevel.Default + Override).
/// LoggingLevelSwitch overrides config — used by CLI --log-level (Plan 13-04).
/// Two sinks: Console (stderr from Verbose; kept for tail -f OBS-04) + File (rolling daily).
let configure (config: IConfiguration) : unit =
    let logDir =
        let raw = config.["Logging:Directory"]
        if String.IsNullOrWhiteSpace(raw) then "logs/operational" else raw

    Directory.CreateDirectory(logDir) |> ignore

    let filePath = Path.Combine(logDir, "smart-router-.log")

    Log.Logger <-
        LoggerConfiguration()
            .ReadFrom.Configuration(config)
            .MinimumLevel.ControlledBy(levelSwitch)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("correlation_id", "-")
            .WriteTo.Console(
                standardErrorFromLevel = Nullable<LogEventLevel>(LogEventLevel.Verbose),
                outputTemplate = outputTemplate
            )
            .WriteTo.File(
                path = filePath,
                rollingInterval = RollingInterval.Day,
                fileSizeLimitBytes = Nullable<int64>(50_000_000L),
                rollOnFileSizeLimit = true,
                retainedFileCountLimit = Nullable<int>(30),
                flushToDiskInterval = Nullable<TimeSpan>(TimeSpan.FromSeconds(2.0)),
                shared = false,
                outputTemplate = outputTemplate
            )
            .CreateLogger()

/// Runtime level override (called by Program.fs when --log-level is parsed).
/// Plan 13-04 wires the CLI parser to this.
let setLevel (level: LogEventLevel) : unit =
    levelSwitch.MinimumLevel <- level

/// Flush + dispose. Call before process exit.
let shutdown () : unit = Log.CloseAndFlush()
