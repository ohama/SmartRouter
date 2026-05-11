---
phase: 13-service-logging
plan: 05
type: execute
wave: 4
depends_on: ["13-02", "13-03", "13-04"]
files_modified:
  - src/SmartRouter.Cli/Adapters/LogRetentionService.fs (NEW)
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - src/SmartRouter.Cli/CompositionRoot.fs
  - src/SmartRouter.Cli/Program.fs
  - src/SmartRouter.Cli/appsettings.json
autonomous: true

must_haves:
  truths:
    - "src/SmartRouter.Cli/Adapters/LogRetentionService.fs exists; type LogRetentionService inherits BackgroundService; ExecuteAsync runs PeriodicTimer at configurable interval (default 60 minutes)"
    - "LogRetentionService prunes (a) operational logs older than RetentionDays from Logging:Directory; (b) decision JSONL older than DecisionLog:RetentionDays; (c) datasets/teacher-cap-*.json older than 7 days"
    - "LogRetentionService is registered in DI via configureRequestPipeline (NOT configureWithoutMl); triple-registration mirrors DecisionLogWriter (concrete + IInterface alias if any + AddHostedService)"
    - "appsettings.json has Logging:RetentionDays (30) and DecisionLog:RetentionDays (90) (added in 13-01); LogRetentionService PollIntervalMinutes (60), DatasetsDirectory (\"datasets\"), and TeacherCapRetentionDays (7) are hardcoded constants in CompositionRoot's Configure<LogRetentionOptions> action — operator can lift to appsettings.json in a future minor change if tunability proves necessary"
    - "Program.fs emits a startup banner via Log.Information(\"{Banner}\", ...) AFTER app.Build() but BEFORE app.Run(); banner includes port, model.version, canary state, queue config, teacher cap"
    - "Program.fs registers a shutdown banner via IHostApplicationLifetime.ApplicationStopping callback; emits in-flight count + queue depths"
    - "dotnet build clean; dotnet test green; LogRetentionService gracefully drains on StopAsync"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/LogRetentionService.fs"
      provides: "BackgroundService that prunes log + cap files past retention"
      min_lines: 80
---

<objective>
Add three new operational behaviors:
1. **Startup banner** — multi-line INFO emission after host build, before app.Run; gives operator a snapshot of what's running.
2. **Shutdown banner** — INFO emission on `ApplicationStopping`; reports in-flight count + queue depths.
3. **LogRetentionService** — `BackgroundService` that prunes old log files. Three categories: operational rolling logs (30 days), decision JSONL (90 days), teacher-cap-*.json (7 days). PeriodicTimer at 60-minute interval.

This plan depends on 13-02 (ILogger<T> migration) and 13-03 (behavior changes) — both must be complete before this plan starts.
</objective>

<execution_context>
@./.planning/phases/13-service-logging/13-CONTEXT.md
</execution_context>

<context>
@.planning/phases/13-service-logging/13-CONTEXT.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Create LogRetentionService.fs + register in DI + add to .fsproj</name>
  <files>
    - src/SmartRouter.Cli/Adapters/LogRetentionService.fs (NEW)
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/appsettings.json
  </files>
  <action>
**Step 1.** Create `src/SmartRouter.Cli/Adapters/LogRetentionService.fs`:

```fsharp
module SmartRouter.Cli.Adapters.LogRetentionService

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

[<CLIMutable>]
type LogRetentionOptions =
    { OperationalDirectory : string
      OperationalRetentionDays : int
      DecisionDirectory : string
      DecisionRetentionDays : int
      DatasetsDirectory : string
      TeacherCapRetentionDays : int
      PollIntervalMinutes : int }

type LogRetentionService(opts: IOptions<LogRetentionOptions>, logger: ILogger<LogRetentionService>) =
    inherit BackgroundService()

    /// Parse YYYY-MM-DD from a filename. Returns Some date or None.
    let datePattern = Regex(@"(\d{4})-(\d{2})-(\d{2})", RegexOptions.Compiled)
    let private tryParseDate (filename: string) : DateTimeOffset option =
        let m = datePattern.Match(filename)
        if m.Success then
            try
                let y = int m.Groups.[1].Value
                let mo = int m.Groups.[2].Value
                let d = int m.Groups.[3].Value
                Some(DateTimeOffset(DateTime(y, mo, d), TimeSpan.Zero))
            with _ -> None
        else None

    /// Parse YYYYMMDD (Serilog default) from filename. Returns Some date or None.
    let datePatternCompact = Regex(@"(\d{4})(\d{2})(\d{2})", RegexOptions.Compiled)
    let private tryParseDateCompact (filename: string) : DateTimeOffset option =
        let m = datePatternCompact.Match(filename)
        if m.Success then
            try
                let y = int m.Groups.[1].Value
                let mo = int m.Groups.[2].Value
                let d = int m.Groups.[3].Value
                Some(DateTimeOffset(DateTime(y, mo, d), TimeSpan.Zero))
            with _ -> None
        else None

    /// Try both date formats — operational logs use compact; others use dashed.
    let private tryParseAnyDate (filename: string) : DateTimeOffset option =
        tryParseDate filename
        |> Option.orElseWith (fun () -> tryParseDateCompact filename)

    let private pruneFiles (dir: string) (pattern: string) (retainDays: int) =
        if Directory.Exists(dir) then
            let cutoff = DateTimeOffset.UtcNow.AddDays(-float retainDays)
            for path in Directory.EnumerateFiles(dir, pattern) do
                let name = Path.GetFileName(path)
                match tryParseAnyDate name with
                | Some d when d < cutoff ->
                    try
                        File.Delete(path)
                        logger.LogInformation(
                            "LogRetentionService: pruned {Path} (file_date={Date}, cutoff={Cutoff})",
                            path, d, cutoff)
                    with ex ->
                        logger.LogWarning(ex, "LogRetentionService: failed to delete {Path}", path)
                | _ -> ()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            let o = opts.Value
            let interval = TimeSpan.FromMinutes(float o.PollIntervalMinutes)
            use timer = new PeriodicTimer(interval)
            try
                // Run once immediately on startup, then on each tick.
                let runOnce () =
                    pruneFiles o.OperationalDirectory "smart-router-*.log" o.OperationalRetentionDays
                    pruneFiles o.DecisionDirectory "*.jsonl" o.DecisionRetentionDays
                    pruneFiles o.DatasetsDirectory "teacher-cap-*.json" o.TeacherCapRetentionDays
                runOnce ()
                while not stoppingToken.IsCancellationRequested do
                    let! _ = timer.WaitForNextTickAsync(stoppingToken).AsTask()
                    if not stoppingToken.IsCancellationRequested then
                        runOnce ()
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.LogError(ex, "LogRetentionService: outer loop crashed")
        } :> Task

    override _.StopAsync(ct: CancellationToken) =
        logger.LogInformation("LogRetentionService stopping")
        base.StopAsync(ct)
```

**Step 2.** Add to `src/SmartRouter.Cli/SmartRouter.Cli.fsproj`. The new file should be compiled AFTER all type-based adapters (LogRetentionService is a leaf adapter with no consumers in Core). Suggest position: after `HealthService.fs`, before `Endpoints/`:

```xml
<Compile Include="Adapters/LogRetentionService.fs" />
```

(Exact compile-order is flexible since LogRetentionService doesn't depend on or get consumed by any other Cli adapter.)

**Step 3.** Register in DI. Edit `src/SmartRouter.Cli/CompositionRoot.fs`. Add to `configureRequestPipeline` (NOT `configureWithoutMl` — retention is a production-only concern):

```fsharp
services.Configure<LogRetentionOptions>(fun (opts: LogRetentionOptions) ->
    let logging = config.GetSection("Logging")
    let decisionLog = config.GetSection("DecisionLog")
    opts.OperationalDirectory <- logging.["Directory"] |> Option.ofObj |> Option.defaultValue "logs/operational"
    opts.OperationalRetentionDays <- logging.["RetentionDays"] |> Option.ofObj |> Option.bind (fun s -> match Int32.TryParse(s) with true, n -> Some n | _ -> None) |> Option.defaultValue 30
    opts.DecisionDirectory <- decisionLog.["Directory"] |> Option.ofObj |> Option.defaultValue "logs/decisions"
    opts.DecisionRetentionDays <- decisionLog.["RetentionDays"] |> Option.ofObj |> Option.bind (fun s -> match Int32.TryParse(s) with true, n -> Some n | _ -> None) |> Option.defaultValue 90
    opts.DatasetsDirectory <- "datasets"   // TODO: make configurable if needed
    opts.TeacherCapRetentionDays <- 7      // TODO: make configurable if needed
    opts.PollIntervalMinutes <- 60         // TODO: make configurable if needed
) |> ignore
services.AddHostedService<LogRetentionService>() |> ignore
```

(If the executor wants to make all 7 fields configurable via appsettings.json, do so via a single `Logging:Retention` section. For now hardcoding the latter 3 is acceptable — Phase 13 doesn't depend on operator tunability of those.)

**Step 4.** appsettings.json — confirm Logging:Directory + Logging:RetentionDays + DecisionLog:RetentionDays exist (added in 13-01). No new keys needed unless executor opts to make TeacherCap retention configurable; in that case add:

```json
"Logging": {
  ...,
  "Retention": {
    "DatasetsDirectory": "datasets",
    "TeacherCapRetentionDays": 7,
    "PollIntervalMinutes": 60
  }
}
```

This is optional polish; the basic plan uses hardcoded values for the three sub-fields.

---

**Cross-phase note (Phase 12 dependency):**

Phase 12-02 (선행 phase, 이미 실행됨 가정) 가 `CompositionRoot.fs` 의 `configureServices` 를 두 함수로 분할했음:
- `configureRequestPipeline` — full ML wiring; production HTTP service path
- `configureWithoutMl` — `--retrain` offline + tests; ML init 제외

LogRetentionService 등록은 **`configureRequestPipeline` 에만** 들어감. `configureWithoutMl` 에는 추가하지 말 것 — 이유:
- `--retrain` 은 one-shot offline pipeline; 백그라운드 retention 불필요.
- Test fixtures 는 in-process 이라 자동 cleanup 안 됨; manual housekeeping.
- Production path 만 launchd 환경에서 장기 동작; 이때만 retention 필요.

**작업 위치 힌트:**
`configureRequestPipeline` 함수 본문에서 다른 `AddHostedService<...>` 등록 (HealthService, RetrainingService, CanaryService, DecisionLogWriter 등) 클러스터 옆에 배치. DI registration 순서는 무관하지만 가독성 위해 logging-related 등록 (LogRetentionService) 을 다른 hosted services 와 함께.

**Verify:**
```bash
# LogRetentionService 가 configureRequestPipeline 에만 있는지 확인
grep -c "LogRetentionService" src/SmartRouter.Cli/CompositionRoot.fs
# expected: 1-2 (옵션 register + AddHostedService) — configureWithoutMl 에는 0

# configureWithoutMl 함수 본문에 LogRetentionService 등장 안 하는지 확인 (수동 검토):
sed -n '/let configureWithoutMl/,/^let /p' src/SmartRouter.Cli/CompositionRoot.fs | grep -c "LogRetentionService"
# expected: 0
```
  </action>
  <verify>
```bash
test -f src/SmartRouter.Cli/Adapters/LogRetentionService.fs && echo OK || echo MISSING
grep -c "LogRetentionService\|AddHostedService<LogRetentionService>" src/SmartRouter.Cli/SmartRouter.Cli.fsproj src/SmartRouter.Cli/CompositionRoot.fs
# expected: >= 2
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 2: Startup banner in Program.fs (main branch)</name>
  <files>src/SmartRouter.Cli/Program.fs</files>
  <action>
Edit `src/SmartRouter.Cli/Program.fs` main branch. After `let app = builder.Build()` and AFTER `Logging.configure` + `applyLogLevelFromArgs args`, AND BEFORE `app.Run()`:

```fsharp
// Phase 13 startup banner.
let banner =
    let regn = app.Services.GetRequiredService<RoutingAlgorithmRegistration>()
    let versionProvider = app.Services.GetRequiredService<IModelVersionProvider>()
    let queueOpts = app.Services.GetRequiredService<IOptions<QueueDispatcherOptions>>().Value
    let teacherOpts = app.Services.GetRequiredService<IOptions<TeacherLabelerOptions>>().Value
    let canaryOpts = app.Services.GetRequiredService<IOptions<CanaryOptions>>().Value
    let logging = builder.Configuration.GetSection("Logging")
    let logDir = logging.["Directory"] |> Option.ofObj |> Option.defaultValue "logs/operational"
    let port =
        let urls = builder.Configuration.["Kestrel:Endpoints:Http:Url"]
                   |> Option.ofObj
                   |> Option.defaultValue "http://localhost:4000"
        urls
    let canaryVersion = versionProvider.CanaryVersion |> Option.defaultValue "(none)"
    sprintf "SmartRouter starting
    listen           = %s
    routing.algorithm = %s
    model.version    = %s
    canary.version   = %s
    canary.percent   = %d
    queue.maxconc.122B = %d
    queue.fairnessK  = %d
    teacher.cap.daily = %d
    log.dir          = %s"
        port regn.Name versionProvider.CurrentVersion canaryVersion
        canaryOpts.PercentageEnabled queueOpts.MaxConcurrent122B queueOpts.FairnessK
        teacherOpts.DailyCallCap logDir

let bannerLogger =
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup")
bannerLogger.LogInformation("{Banner}", banner)
```

Use the static `Log.Information` form OR the bannerLogger from `ILoggerFactory.CreateLogger("Startup")`. Recommended: ILoggerFactory.CreateLogger so SourceContext is `Startup`.

The multi-line string emits a single LogEvent with `\n` separators; Serilog's `{Message:lj}` format renders multi-line content correctly.

**Edit comment:** Add a comment above the banner emission explaining its purpose:
```fsharp
// Phase 13 — startup banner. Operators reading the operational log see a
// single proof-of-startup snapshot: which port, model version, canary state,
// queue config, etc. Helps confirm the binary they tail matches expectations.
```
  </action>
  <verify>
```bash
grep -c "SmartRouter starting\|startupLogger\|bannerLogger" src/SmartRouter.Cli/Program.fs
# expected: >= 2
grep -c "model\.version\|canary\.version\|queue\.maxconc" src/SmartRouter.Cli/Program.fs
# expected: >= 3
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 3: Shutdown banner via ApplicationStopping</name>
  <files>src/SmartRouter.Cli/Program.fs</files>
  <action>
Edit `src/SmartRouter.Cli/Program.fs`. After the startup banner emission, register a callback for ApplicationStopping:

```fsharp
let lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>()
let shutdownLogger =
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Shutdown")

lifetime.ApplicationStopping.Register(fun () ->
    let queueDispatcher = app.Services.GetRequiredService<QueueDispatcher>()
    let stats = queueDispatcher.GetStats()
    let inFlight = stats.Active35B + stats.Active122B
    let queueDepth = stats.QueueDepthHigh + stats.QueueDepthLow
    shutdownLogger.LogInformation(
        "SmartRouter stopping; in-flight={InFlight} queue.depth.total={QueueDepth} queue.depth.high={H} queue.depth.low={L}",
        inFlight, queueDepth, stats.QueueDepthHigh, stats.QueueDepthLow)
) |> ignore
```

(The exact API names — `Active35B`, `Active122B`, `QueueDepthHigh`, etc. — depend on the QueueDispatcher.GetStats() return type. Use whatever fields actually exist; if `Stats` snapshot lacks an in-flight counter, omit that field from the banner.)

The `lifetime.ApplicationStopping.Register` callback fires synchronously on graceful shutdown. The Logger emits a single LogEvent that's flushed by `Logging.shutdown()` in the outer `finally` block.

**Edit Program.fs `finally` block.** Confirm `Logging.shutdown()` is called in the outermost `finally`:
```fsharp
[<EntryPoint>]
let main args =
    try
        try
            // ... all the bootstrap + retrain/main branches ...
            0
        with ex ->
            eprintfn "Fatal: %s" ex.Message
            1
    finally
        Logging.shutdown ()  // flushes Serilog including the shutdown banner
```
  </action>
  <verify>
```bash
grep -c "ApplicationStopping\|SmartRouter stopping\|shutdownLogger" src/SmartRouter.Cli/Program.fs
# expected: >= 2
grep -c "Logging\.shutdown" src/SmartRouter.Cli/Program.fs
# expected: >= 1
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# Build succeeded.
```
  </verify>
</task>

</tasks>

<verification>
- [x] LogRetentionService.fs exists; BackgroundService registered in DI via configureRequestPipeline
- [x] Startup banner emits all expected fields (port, version, canary, queue, teacher cap, log dir)
- [x] Shutdown banner emits in-flight + queue depths via ApplicationStopping callback
- [x] Logging.shutdown in outer finally
- [x] Build clean; tests green
</verification>
