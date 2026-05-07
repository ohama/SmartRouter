module SmartRouter.Cli.Adapters.Logging

open System
open Serilog
open Serilog.Core
open Serilog.Events

/// Module-level switch controlling Serilog's minimum level. Mutable post-
/// configure() — Program.fs flips it to Debug when --trace is set (CLI-07).
/// Default: Information (suppresses Log.Debug step-level events).
/// Research § Pattern 4 + Pitfall 7: must be a module-level binding so the
/// call to configure() below can reference it; mutating after startup is
/// the whole point of LoggingLevelSwitch.
let levelSwitch: LoggingLevelSwitch = LoggingLevelSwitch(LogEventLevel.Information)

/// Initialize the static Serilog Log.Logger. Must run ONCE at process start
/// BEFORE any Log.* call — Serilog's default logger is a silent no-op.
///
/// Configuration:
///   - Minimum level: CONTROLLED BY levelSwitch (default Information;
///     Program flips to Debug for --trace).
///   - Sink: Console, but stderr for ALL events (standardErrorFromLevel =
///     Verbose) — keeps log output off stdout where Spectre and printfn live
///     (OBS-02 / research § Pattern 6).
let configure () : unit =
    Log.Logger <-
        LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .WriteTo.Console(
                standardErrorFromLevel = System.Nullable<LogEventLevel>(LogEventLevel.Verbose),
                outputTemplate = "[{Level:u3}] {Message:lj}{NewLine}{Exception}"
            )
            .CreateLogger()

/// Flush and dispose the global logger. Call in Program.fs before exit to
/// ensure all pending events are written.
let shutdown () : unit = Log.CloseAndFlush()
