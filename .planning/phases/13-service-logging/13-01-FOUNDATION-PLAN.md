---
phase: 13-service-logging
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - src/SmartRouter.Cli/Adapters/Logging.fs
  - src/SmartRouter.Cli/appsettings.json
  - src/SmartRouter.Cli/Program.fs
autonomous: true

must_haves:
  truths:
    - "SmartRouter.Cli.fsproj has new PackageReference for Serilog.Sinks.File and Serilog.Settings.Configuration"
    - "Adapters/Logging.fs configure(config: IConfiguration) reads Serilog section via .ReadFrom.Configuration(config)"
    - "Adapters/Logging.fs writes to TWO sinks: Console (stderr from Verbose, kept) AND File (rolling daily + 50MB cap + retain 30 + 2s flush)"
    - "Output template includes timestamp + level + SourceContext + [{correlation_id}] + message + exception"
    - "An Enrich.WithProperty(\"correlation_id\", \"-\") default is registered so non-request-scope logs render [-]"
    - "A new setLevel(level: LogEventLevel) function exposes LoggingLevelSwitch.MinimumLevel mutation for 13-04 (CLI --log-level)"
    - "appsettings.json:Serilog has MinimumLevel.Default + MinimumLevel.Override table; Logging:Directory key + Logging:RetentionDays key; DecisionLog:RetentionDays key"
    - "Program.fs calls Logging.configure(builder.Configuration) with the IConfiguration argument (replacing the parameterless configure())"
    - "All existing static Log.Information/Warning/Error/Debug call sites continue to work (backward-compatible — ILogger<T> migration is 13-02)"
    - "dotnet build succeeds; dotnet test passes (no test changes; existing tests unaffected by file-sink addition since CapturingSink is in-process)"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/Logging.fs"
      provides: "configure(IConfiguration), setLevel(LogEventLevel), shutdown(), levelSwitch (kept)"
    - path: "src/SmartRouter.Cli/appsettings.json"
      provides: "Serilog.MinimumLevel.Override table; Logging.Directory + Logging.RetentionDays; DecisionLog.RetentionDays"
---

<objective>
Foundation pass: rewrite `Adapters/Logging.fs` with dual sink (Console stderr-only kept; rolling File new), bind `appsettings.json:Serilog` via `.ReadFrom.Configuration(config)`, add new output template with timestamp+level+SourceContext+[{correlation_id}]+message, register default `correlation_id = "-"` enricher. Add NuGet packages. Update `Program.fs` to pass `IConfiguration` to `configure()`. Add `Logging:Directory`, `Logging:RetentionDays`, `DecisionLog:RetentionDays` config keys.

Existing static `Log.*` callers continue working unchanged. `ILogger<T>` migration is 13-02.
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/13-service-logging/13-CONTEXT.md
@.planning/phases/13-service-logging/13-logging-research-and-design.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Add NuGet packages</name>
  <files>src/SmartRouter.Cli/SmartRouter.Cli.fsproj</files>
  <action>
Add two `<PackageReference>` entries to `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` (within the existing `<ItemGroup>` that lists Serilog packages, around line 53-55):

```xml
<PackageReference Include="Serilog.Sinks.File" Version="6.0.0" />
<PackageReference Include="Serilog.Settings.Configuration" Version="9.0.0" />
```

Verify exact current versions before pinning:
```bash
dotnet add src/SmartRouter.Cli/SmartRouter.Cli.fsproj package Serilog.Sinks.File --dry-run 2>&1 | grep -i "version"
dotnet add src/SmartRouter.Cli/SmartRouter.Cli.fsproj package Serilog.Settings.Configuration --dry-run 2>&1 | grep -i "version"
```

If newer stable available, pin to that. Otherwise use the versions above.

Run `dotnet restore` after edit:
```bash
dotnet restore src/SmartRouter.Cli/SmartRouter.Cli.fsproj
```
  </action>
  <verify>
```bash
grep -c "Serilog\.Sinks\.File\|Serilog\.Settings\.Configuration" src/SmartRouter.Cli/SmartRouter.Cli.fsproj
# expected: 2
dotnet restore src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# expected: Restored.
```
  </verify>
</task>

<task type="auto">
  <name>Task 2: Rewrite Adapters/Logging.fs with dual sink + ReadFrom.Configuration + new template</name>
  <files>src/SmartRouter.Cli/Adapters/Logging.fs</files>
  <action>
Full rewrite of `src/SmartRouter.Cli/Adapters/Logging.fs`:

```fsharp
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
/// LoggingLevelSwitch overrides config — used by CLI --log-level.
/// Two sinks: Console (stderr from Verbose; kept for tail-f) + File (rolling).
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
```

Notes:
- `correlation_id = "-"` enricher MUST come BEFORE `WriteTo.Console`/`WriteTo.File` so the property is present on every event. `LogContext.PushProperty("correlation_id", cid)` (in CorrelationMiddleware) overrides the default at request scope.
- File path uses Serilog's date-token convention: `smart-router-.log` becomes `smart-router-20260509.log`, `smart-router-20260509_001.log` (size-roll within day). If executor wants ISO-8601 dashes (`smart-router-2026-05-09.log`), use `path = Path.Combine(logDir, "smart-router-{Date}.log")` — Serilog will substitute `{Date}` → `20260509`. Either form is acceptable; document the actual pattern in plan summary.
- `Path.Combine` resolves relative paths from the process WorkingDirectory at runtime. Under launchd, WorkingDirectory = `/Users/ohama/llm-system/services/smart-router/` per plist; under dev, `./logs/operational/`.
- `Directory.CreateDirectory` is idempotent — creates if missing.
  </action>
  <verify>
```bash
grep -c "ReadFrom\.Configuration\|WriteTo\.File\|WriteTo\.Console\|setLevel\|levelSwitch" src/SmartRouter.Cli/Adapters/Logging.fs
# expected: >= 5 (each function/method present)
grep -c "outputTemplate\|correlation_id" src/SmartRouter.Cli/Adapters/Logging.fs
# expected: >= 3 (template + enricher property)
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# expected: Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 3: appsettings.json — add Override table + Logging:Directory + retention keys</name>
  <files>src/SmartRouter.Cli/appsettings.json</files>
  <action>
Edit `src/SmartRouter.Cli/appsettings.json`. Three changes:

**Edit 1.** Replace existing `Serilog` section with full Override table:

```json
"Serilog": {
  "MinimumLevel": {
    "Default": "Information",
    "Override": {
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.AspNetCore.HttpLogging": "Information",
      "Microsoft.Extensions.Hosting": "Warning",
      "Microsoft.Hosting.Lifetime": "Information",
      "System.Net.Http": "Warning"
    }
  }
}
```

**Edit 2.** Replace existing `Logging` section to add `Directory` + `RetentionDays`:

```json
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "Microsoft.AspNetCore": "Warning"
  },
  "Directory": "logs/operational",
  "RetentionDays": 30
}
```

(`Logging:LogLevel` is the Microsoft.Extensions.Logging filter — keep for compatibility with framework. `Directory` and `RetentionDays` are Phase 13 additions.)

**Edit 3.** Update existing `DecisionLog` section to add `RetentionDays`:

```json
"DecisionLog": {
  "Directory": "logs/decisions",
  "RetentionDays": 90,
  "ChannelCapacity": 1000
}
```

(`Directory` and `ChannelCapacity` already exist from Phase 5; just add `RetentionDays`.)

JSON syntax — no trailing commas, valid object structure.
  </action>
  <verify>
```bash
python3 -c "import json; c=json.load(open('src/SmartRouter.Cli/appsettings.json')); print('Logging:Directory =',c['Logging'].get('Directory')); print('Logging:RetentionDays =',c['Logging'].get('RetentionDays')); print('DecisionLog:RetentionDays =',c['DecisionLog'].get('RetentionDays')); print('Serilog Override =', list(c['Serilog']['MinimumLevel']['Override'].keys()))"
# expected:
# Logging:Directory = logs/operational
# Logging:RetentionDays = 30
# DecisionLog:RetentionDays = 90
# Serilog Override = ['Microsoft.AspNetCore', 'Microsoft.AspNetCore.HttpLogging', 'Microsoft.Extensions.Hosting', 'Microsoft.Hosting.Lifetime', 'System.Net.Http']
```
  </verify>
</task>

<task type="auto">
  <name>Task 4: Program.fs — pass IConfiguration to Logging.configure()</name>
  <files>src/SmartRouter.Cli/Program.fs</files>
  <action>
Edit `src/SmartRouter.Cli/Program.fs`. Find the call to `Logging.configure ()` (currently line 18, before `try` block):

```fsharp
// BEFORE
[<EntryPoint>]
let main args =
    Logging.configure ()
    try ...
```

The trick: `Logging.configure(config: IConfiguration)` now takes an argument, but at this early stage `WebApplication.CreateBuilder` hasn't been called yet. Solution: build a minimal IConfiguration first OR move the `configure` call AFTER builder creation.

**Recommended: move `configure` call AFTER builder creation:**

```fsharp
// AFTER
[<EntryPoint>]
let main args =
    try
        try
            // Phase 7 --retrain CLI command (existing)
            if args |> Array.contains "--retrain" then
                let retrainBuilder = Host.CreateApplicationBuilder(args)
                ...
                Logging.configure(retrainBuilder.Configuration)   // NEW
                ...
                CompositionRoot.configureWithoutMl retrainBuilder.Services retrainBuilder.Configuration |> ignore
                ...
            else
                let builder = WebApplication.CreateBuilder(args)
                ...
                Logging.configure(builder.Configuration)   // NEW — moved here
                ...
                let app = builder.Build()
                ...
                app.Run()
                0
        with
        | ex ->
            // Pre-Logging.configure exceptions: stderr fallback
            eprintfn "Fatal: %s" ex.Message
            1
    finally
        Logging.shutdown ()
```

The pre-init crash window now writes to stderr via `eprintfn` (launchd captures it in `smart-router.err`). Once `Logging.configure` runs, all subsequent exceptions go through Serilog.

**Important:** `WebApplication.CreateBuilder(args)` already starts the host's internal Microsoft.Extensions.Logging; that's fine — Serilog `.UseSerilog()` (host extension) will replace it as the logger provider once the host builds. The `Log.Logger` static is set by our `configure()` and used by `host.UseSerilog()` extension via the `Serilog.AspNetCore` package (already pinned).

Add `app.Host.UseSerilog()` if not already present (it's needed so ASP.NET host-level logging routes through Serilog):
```fsharp
builder.Host.UseSerilog() |> ignore
```

Insert after `Logging.configure(builder.Configuration)`.
  </action>
  <verify>
```bash
grep -c "Logging\.configure" src/SmartRouter.Cli/Program.fs
# expected: 2 (--retrain branch + main branch)
grep -c "Logging\.configure(.*[Cc]onfiguration)" src/SmartRouter.Cli/Program.fs
# expected: 2 (both calls pass an IConfiguration)
grep -c "UseSerilog" src/SmartRouter.Cli/Program.fs
# expected: >= 1
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# expected: Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 5: Solution build + manual smoke (read-only)</name>
  <files>(no edits)</files>
  <action>
Run a full build and a brief manual smoke:

```bash
dotnet build 2>&1 | tail -5
# expected: Build succeeded.

# Verify file sink creates files at expected location:
mkdir -p /tmp/sr-13-01-smoke/logs/operational
cd /tmp/sr-13-01-smoke
# (Skip actual run — would need a full host. Instead verify Logging.fs structure compiles.)
```

Or run tests to confirm CapturingSink-based tests still work:

```bash
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --filter "FullyQualifiedName~LoggingTests" --no-restore 2>&1 | tail -5
# expected: passes
```
  </action>
  <verify>
```bash
dotnet build 2>&1 | grep -E "Build succeeded|error"
# expected: "Build succeeded." present; no "error" lines
```
  </verify>
</task>

</tasks>

<verification>
- [x] Serilog.Sinks.File + Serilog.Settings.Configuration NuGet packages added
- [x] Adapters/Logging.fs: configure(IConfiguration) + dual sink + new template + correlation_id default + setLevel + shutdown
- [x] appsettings.json: Serilog Override table + Logging:Directory + Logging:RetentionDays + DecisionLog:RetentionDays
- [x] Program.fs: Logging.configure called with IConfiguration after builder creation; UseSerilog wired
- [x] Existing static Log.* call sites unchanged and still working (89 emissions across 19 files unaffected — 13-02 migrates to ILogger<T>)
- [x] Build clean; existing tests pass
</verification>
