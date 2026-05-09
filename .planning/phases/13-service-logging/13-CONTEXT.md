# Phase 13 — Service Logging: Context

**Locked:** 2026-05-09 (concurrent session, write-target = `.planning/phases/13-service-logging/`)
**Decisions referenced:** §7 of `13-logging-research-and-design.md`

<domain>
## Phase Boundary

smart-router 가 launchd service 로 영구 동작할 때 운영자가 의지할 수 있는 logging 인프라 구축. 두 stream 분리 (Phase 5 JSONL DecisionLog 는 그대로; 새로 operational rolling log 도입), 듀얼 sink (stderr 유지 + 파일 롤링), `correlation_id` 출력 표시, dead `appsettings.json:Serilog` config 활성화, 전면 `ILogger<T>` 마이그레이션, hot-path 로그 양 감축, startup/shutdown banner, `LogRetentionService` BackgroundService. CLI 의 `--trace` boolean → `--log-level=...` enum.

</domain>

<decisions>
## Locked Decisions

### Q1 — Log file location: configurable, default `logs/operational/` (Option C)

`appsettings.json` 새 키 추가:
```json
"Logging": {
  "LogLevel": { ... },
  "Directory": "logs/operational"   // NEW
}
```

`Logging:Directory` 가 운영자가 회전 로그의 위치를 바꿀 수 있는 단일 surface. WorkingDirectory 기준 상대경로. launchd 환경에서는 `/Users/ohama/llm-system/services/smart-router/logs/operational/` 로 resolve. dev 환경에서는 `./logs/operational/`.

Phase 5 의 `DecisionLog:Directory` 와 같은 패턴 — 일관됨.

### Q2 — Per-file size cap: 50 MB

Serilog 기본값(100MB)보다 작게 잡아 grep 친화성 우선. 30일 보관 × 평균 1-2 파일/일 = ~30-60 파일 보유. 운영자가 `tail -f` / `grep` 할 때 파일 크기 50MB 가 데스크톱 도구로 다루기에 좋음.

`fileSizeLimitBytes: 50_000_000L`

### Q3 — Retention: 30 operational + 90 decision JSONL

Operational rolling: `retainedFileCountLimit: 30` (Serilog 옵션).
Decision JSONL: 90 일. Phase 8 retraining 의 hard-case dataset 입력으로 가치 있음.
Teacher cap counter 파일: 7 일 (Phase 7 로직과 동일 — 별도 정리 불필요).

### Q4 — Decision JSONL retention: implement in Phase 13

`LogRetentionService : BackgroundService` 신규 생성:
- 한 시간 간격 PeriodicTimer (`TimeSpan.FromHours(1)`)
- `logs/decisions/*.jsonl` 90일 초과 파일 삭제
- `datasets/teacher-cap-*.json` 7일 초과 파일 삭제 (Phase 7 패턴 보강)
- 파일명 패턴 매칭: `YYYY-MM-DD` 가 파일명에 있어야 안전 — 파싱 실패 시 skip
- `Logging:RetentionDays` 와 `DecisionLog:RetentionDays` 두 키로 separate 설정 가능

### Q5 — File sink format: text (사람 읽기)

CompactJsonFormatter 안 씀. 표준 Serilog `outputTemplate` 사용. 운영자가 grep + tail 친화적. log shipper (Promtail/Loki) 도입은 별도 phase.

### Q6 — correlation_id rendering: `[{correlation_id}]` 괄호 그룹화

Output template:
```
{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Level:u3}] {SourceContext} [{correlation_id}] {Message:lj}{NewLine}{Exception}
```

Empty value 처리:
- Background-service / startup log: `correlation_id` property 없음 → Serilog 가 `[]` 출력. 약간 어색하지만 grep 패턴은 일관됨 (`[{32-hex}]` vs `[]`).
- 또는 Serilog enricher 로 비어있을 때 `-` 으로 채움 (`[-]` vs `[abc...]`). 더 일관된 visual.

**선택:** 비어있을 때 `[-]`. `Serilog.Enrichers.WithProperty("correlation_id", "-")` 를 default 로 등록 후 `LogContext.PushProperty` 가 push 하면 override 됨. 결과 패턴: `[abc12345...]` (request) / `[-]` (background).

대안 채택 안 함: 조건부 (`{#if correlation_id}[{correlation_id}] {#end}`) — Serilog 표준 template 이 conditional 미지원, 추가 enricher/formatter 필요. 복잡도 ↑.

### Q7 — ILogger<T> 전면 마이그레이션

19개 source files 가 `Serilog.Log.X(...)` static API 호출. 모두 `ILogger<T>` 생성자 주입으로 전환:
- 각 class/type 의 ctor 에 `logger: ILogger<{Type}>` parameter 추가.
- 모든 `Log.Information(...)` → `logger.LogInformation(...)` (Microsoft.Extensions.Logging.ILogger 의 메서드).
- DI 등록은 Microsoft.Extensions.Logging 이 `ILogger<T>` 를 자동 등록 (host 가 Serilog 를 provider 로 사용 중).
- F# 에서 `ILogger<T>` 의 generic 사용은 fully qualified type name 명시: `ILogger<HealthService>` 등.
- SourceContext 가 자동 채워짐 → Output template 의 `{SourceContext}` 에 `SmartRouter.Cli.Adapters.HealthService` 같은 fully-qualified type name 출력.

`Program.fs` 의 startup 부분과 `CompositionRoot.fs` 의 DI 등록 단계는 ILogger<T> 를 가져올 수 없는 시점 → 그 부분만 static `Log.Information` 유지. 약 5-7개 emission 만 static 잔존.

**Migration strategy:** 19개 파일 atomic plan 으로 처리 vs 두 plan 으로 분할.

**선택:** 단일 큰 plan (13-02). 모든 ILogger<T> 마이그레이션을 하나의 plan 안에서 atomic 하게. Static Log 잔존하는 파일 (Program.fs, CompositionRoot.fs) 은 명시적 예외. 이유: 분할하면 plan 간 build-state 가 불완전 (어떤 파일은 ILogger 사용, 어떤 파일은 static) — 마지막에 정리하기 어려움. 한 번에 끝내는 게 더 깔끔.

플랜 안에서 task 별로 5-7 파일씩 chunk 가능. Task 단위 atomic commit.

### Q8 — `--log-level=debug|info|warn|error` 로 교체

`Program.fs` 의 `--trace` boolean 삭제. 새 CLI flag:
```
dotnet run -- --log-level=debug
dotnet run -- --log-level debug   # space form
```

Valid values: `verbose`, `debug`, `information`, `warning`, `error`, `fatal` (Serilog `LogEventLevel` 명칭 그대로). 또는 `info` / `warn` 짧은 별칭 허용.

**선택:** Serilog 명칭 그대로 (`verbose`, `debug`, `information`, `warning`, `error`, `fatal`) 6개 valid. 짧은 별칭 (`info`, `warn`) 도 허용해서 운영자 편의. 잘못된 값은 `failwithf "--log-level=%s invalid; valid: verbose|debug|information|warning|error|fatal"`.

`levelSwitch` 는 보존 — runtime 에서 `--log-level` 이 levelSwitch 의 초기 값을 결정. levelSwitch 자체는 미래 확장성 (signal handler 가 SIGUSR1 에 반응해서 level 변경 등) 을 위해 유지.

### Q9 — `smart-router.err` rotation: document only

Serilog 가 자체 rolling 파일에 쓰므로 launchd `StandardErrorPath` (= `smart-router.err`) 는 거의 비어있음. 운영자가 분기에 한 번 `truncate -s 0 smart-router.err` 으로 정리. README 에 안내. 코드 변경 없음.

newsyslog 설정 안 추가 — Serilog 가 reopen-on-truncate 를 보장하지 않으므로 SIGHUP 핸들러 없는 현 코드와 호환 안 됨.

### Q10 — Test count target: 10+ 신규 테스트

LoggingTests.fs 확장 또는 새 LogRotationTests.fs 생성. 다음 항목 커버:

1. Output template — timestamp ISO-8601 형식
2. Output template — `[correlation_id]` 렌더링 (request scope)
3. Output template — `[-]` 렌더링 (background scope)
4. Output template — `{SourceContext}` 가 fully-qualified 타입명
5. Per-category override — `Microsoft.AspNetCore` 가 Warning 이상만 통과
6. Rolling — 50MB 초과 시 `_001.log` suffix 로 새 파일 생성
7. Rolling — 자정 (혹은 fake-clock 으로 day boundary) 넘으면 새 파일
8. Retention — `retainedFileCountLimit: 3` fixture 로 4번째 파일 작성 시 가장 오래된 파일 삭제 확인
9. LogRetentionService — 90일 초과 decision JSONL 파일 삭제
10. LogRetentionService — 7일 초과 teacher-cap-*.json 파일 삭제
11. Startup banner — 모든 expected 필드 (port, version, model_version, etc.) 가 한 줄에 출력
12. CLI `--log-level=debug` — Debug 레벨 emission 이 통과
13. CLI `--log-level=warn` — Information 레벨 emission 이 차단

**선택:** 13개 테스트 (목록 위). 일부는 작은 sub-test 로 묶어 testCase 수는 8-10개로 통합 가능 (testList 안에 sub-list). 최종 testCase 수 ≥ 10개.

`testSequenced` 래핑 필수 (Console.SetOut + 파일 I/O race). Temp directory unique per test (`Path.GetTempPath() + Guid.NewGuid().ToString("N")`).

</decisions>

<specifics>
## Specific Implementation Notes

### Plan structure

| Wave | Plan | What |
|---|---|---|
| 1 | 13-01 | Foundation: Logging.fs rewrite (ReadFrom.Configuration + dual sink + new template + correlation_id default `-`); appsettings.json `Serilog.MinimumLevel.Override` table + `Logging:Directory` + `Logging:RetentionDays` + `DecisionLog:RetentionDays`; new NuGet packages (Serilog.Sinks.File, Serilog.Settings.Configuration). Static `Log.*` callers continue working. |
| 2 | 13-02 | ILogger<T> 전면 마이그레이션: 19 files, ~89 emission sites. Atomic build-green. Tasks chunked by 5-7 files. |
| 3 (parallel) | 13-03 | Behavior: hot-path 2 ChatCompletions Information → Debug; HealthService transition-only logging; endpoint-hit DEBUG logs in Health/Stats/Canary/Models endpoints. Depends on 13-02. |
| 3 (parallel) | 13-04 | CLI: `--log-level=enum` 도입 (replaces `--trace`); validation; `levelSwitch` 초기값 설정. Depends on 13-01 (uses levelSwitch already in Logging.fs). |
| 4 | 13-05 | Startup banner + shutdown banner + LogRetentionService BackgroundService. Depends on 13-02 (uses ILogger<LogRetentionService>) + 13-03 (hot-path settled). |
| 5 | 13-06 | Tests + README. 13+ test cases in LogRotationTests.fs (new) or extended LoggingTests.fs. README "Operational logging" section. Depends on Wave 4. |

### NuGet additions (in 13-01)

```xml
<PackageReference Include="Serilog.Sinks.File" Version="6.0.0" />
<PackageReference Include="Serilog.Settings.Configuration" Version="9.0.0" />
```

(Versions: pin to current stable; executor verifies via `dotnet list` or NuGet UI before locking.)

`Serilog.Formatting.Compact` is NOT added — Q5 chose text format.
`Serilog.Enrichers.Environment` 는 추가 안 함 — `SourceContext` 는 ILogger<T> 가 자동 enrich (Microsoft.Extensions.Logging.ILogger.LogX 호출 시 generic 타입 인자에서 SourceContext 자동 캡처).

### `Logging.fs` rewrite (13-01)

```fsharp
module SmartRouter.Cli.Adapters.Logging

open System
open Microsoft.Extensions.Configuration
open Serilog
open Serilog.Core
open Serilog.Events

let levelSwitch: LoggingLevelSwitch = LoggingLevelSwitch(LogEventLevel.Information)

/// Initialize Serilog. Read sink config from IConfiguration.
let configure (config: IConfiguration) : unit =
    let logDir =
        let raw = config.["Logging:Directory"]
        if String.IsNullOrWhiteSpace(raw) then "logs/operational" else raw

    let template =
        "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Level:u3}] {SourceContext} [{correlation_id}] {Message:lj}{NewLine}{Exception}"

    Log.Logger <-
        LoggerConfiguration()
            .ReadFrom.Configuration(config)              // appsettings.json:Serilog
            .MinimumLevel.ControlledBy(levelSwitch)      // CLI --log-level overrides
            .Enrich.FromLogContext()
            .Enrich.WithProperty("correlation_id", "-")  // default for non-request scope
            .WriteTo.Console(
                standardErrorFromLevel = Nullable<LogEventLevel>(LogEventLevel.Verbose),
                outputTemplate = template
            )
            .WriteTo.File(
                path = System.IO.Path.Combine(logDir, "smart-router-.log"),
                rollingInterval = RollingInterval.Day,
                fileSizeLimitBytes = Nullable<int64>(50_000_000L),
                rollOnFileSizeLimit = true,
                retainedFileCountLimit = Nullable<int>(30),
                flushToDiskInterval = Nullable<TimeSpan>(TimeSpan.FromSeconds(2.0)),
                shared = false,
                outputTemplate = template
            )
            .CreateLogger()

let setLevel (level: LogEventLevel) : unit =
    levelSwitch.MinimumLevel <- level

let shutdown () : unit = Log.CloseAndFlush()
```

Naming pattern `smart-router-.log` ← Serilog 가 `-{Date:yyyyMMdd}` 또는 `-{Date:yyyyMMdd}_NNN` 자동 삽입. 결과: `smart-router-20260509.log`, `smart-router-20260509_001.log`. ISO 8601 dash 형식 (`smart-router-2026-05-09.log`) 을 원하면 Serilog 의 path token 변경 필요 — sink 옵션 `path = "smart-router-{Date}.log"` 가 정확한 placeholder; 위 코드는 `-` 만 trailing 으로 둔 형태. **executor 가 실제 출력 파일명 확인 후 README에 정확히 기록.**

### `appsettings.json` 변경 (13-01)

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
},
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "Microsoft.AspNetCore": "Warning"
  },
  "Directory": "logs/operational",
  "RetentionDays": 30
},
"DecisionLog": {
  "Directory": "logs/decisions",
  "RetentionDays": 90,                        // NEW
  "ChannelCapacity": 1000
}
```

### ILogger<T> migration patterns (13-02)

For each emitting type:

```fsharp
// BEFORE (CanaryService.fs 예시)
type CanaryService(state: CanaryStateStore, retrainLock: IRetrainLock, opts: IOptions<CanaryOptions>) =
    let mutable watcher : FileSystemWatcher = null
    interface IHostedService with
        member _.StartAsync(ct) = task {
            Log.Information("CanaryService: starting")
            ...
        }

// AFTER
type CanaryService(
    state: CanaryStateStore,
    retrainLock: IRetrainLock,
    opts: IOptions<CanaryOptions>,
    logger: ILogger<CanaryService>) =      // NEW
    let mutable watcher : FileSystemWatcher = null
    interface IHostedService with
        member _.StartAsync(ct) = task {
            logger.LogInformation("CanaryService: starting")    // NEW
            ...
        }
```

DI 등록은 자동 — Host 가 `services.AddLogging(..)` 을 implicitly 등록하고, ILogger<T> 가 그 안에서 어떤 T 든 자동 resolve.

**F# generic syntax tip:** `ILogger<CanaryService>` 의 `<>` 은 F# 의 type parameter. `open Microsoft.Extensions.Logging` 이후에 사용. Serilog 의 ILogger 와 충돌 가능 → namespace 명시 또는 `open` 정리:

```fsharp
open Microsoft.Extensions.Logging   // brings ILogger<T> + LogX extension methods
// remove `open Serilog` from files using ILogger<T> — would shadow
```

Serilog 의 static `Log` 는 여전히 `SmartRouter.Cli.Adapters.Logging` 모듈을 통해 접근 가능 (Program.fs / CompositionRoot.fs 등 startup 단계).

### Files to migrate (13-02 task list)

19 files, 약 89 emission sites:

| File | I+W+E+D | Type to inject |
|---|---|---|
| Adapters/HealthService.fs | 8 (3+3+0+2) | `ILogger<HealthService>` |
| Adapters/CanaryService.fs | 11 (5+5+1+0) | `ILogger<CanaryService>` |
| Adapters/RetrainingService.fs | 12 (6+4+2+0) | `ILogger<RetrainingService>` |
| Adapters/TeacherLabeler.fs | 7 (1+6+0+0) | `ILogger<TeacherLabeler>` |
| Adapters/HardCaseDatasetWriter.fs | 7 (1+3+2+1) | `ILogger<HardCaseDatasetWriter>` |
| Adapters/DecisionLogWriter.fs | 4 (0+2+2+0) | `ILogger<DecisionLogWriter>` |
| Adapters/CanaryWatchdog.fs | 2 (0+1+1+0) | `ILogger<CanaryWatchdog>` |
| Adapters/QueueDispatcher.fs | 3 (0+2+0+1) | `ILogger<QueueDispatcher>` |
| Adapters/QwenUpstreamClient.fs | 6 (1+3+0+2) | `ILogger<QwenUpstreamClient>` |
| Adapters/Validator.fs | 2 (0+2+0+0) | `ILogger<Validator>` (or function-level helper) |
| Adapters/DatasetMerger.fs | 5 (2+3+0+0) | `ILogger<DatasetMerger>` |
| Adapters/ModelBootstrapper.fs | 2 (1+1+0+0) | `ILogger<ModelBootstrapper>` (or function-level) |
| Adapters/BgeM3Embedder.fs | 2 (1+1+0+0) | `ILogger<BgeM3Embedder>` |
| Adapters/Retrainer.fs | 1 (1+0+0+0) | `ILogger<Retrainer>` (or function-level) |
| Adapters/FailureDetector.fs | 5 (3+2+0+0) | `ILogger<FailureDetector>` |
| Endpoints/ChatCompletions.fs | 5 (3+1+1+0) | endpoint handler — DI 통해 `HttpContext.RequestServices.GetRequiredService<ILogger<...>>()` resolve |
| **Static Log 잔존:** | | |
| CompositionRoot.fs | 1 (0+1+0+0) | static `Log.*` 유지 (DI 단계 — ILogger<T> 미존재) |
| Program.fs | 5 (3+2+0+0) | static `Log.*` 유지 (startup 단계) |

총 89개 emission. Static 잔존: ~6 (CompositionRoot 1 + Program 5). 마이그레이션 대상: ~83.

`Validator.fs`, `Retrainer.fs`, `DatasetMerger.fs`, `ModelBootstrapper.fs` 는 `module` (not `type`) — function-level emission. ILogger<T> 를 함수 인자로 받을지 module-level let bind 할지 결정:
- **선택 (CONTEXT):** 이 4개는 `ILogger` (non-generic) 를 첫 함수 인자로 받음. SourceContext 는 `Log.ForContext("SourceContext", "SmartRouter.Cli.Adapters.Validator")` 식으로 caller 가 명시 OR 그대로 두고 SourceContext 는 비워둠. **Pragmatic:** 비워둠. function-level 모듈은 ILogger<T> 등록 어색 → ILogger 인자 받고 SourceContext 는 운영자가 module 이름 전후 컨텍스트로 추론.

### `--log-level` parsing (13-04)

```fsharp
// Program.fs CLI 파싱 추가
let parseLogLevel (raw: string) : LogEventLevel =
    match raw.ToLowerInvariant() with
    | "verbose" | "vrb" | "trace"       -> LogEventLevel.Verbose
    | "debug" | "dbg"                   -> LogEventLevel.Debug
    | "information" | "info" | "inf"    -> LogEventLevel.Information
    | "warning" | "warn" | "wrn"        -> LogEventLevel.Warning
    | "error" | "err"                   -> LogEventLevel.Error
    | "fatal" | "ftl"                   -> LogEventLevel.Fatal
    | other -> failwithf "--log-level=%s invalid; valid: verbose|debug|information|warning|error|fatal (or info/warn aliases)" other

// args 파싱:
let logLevelArg =
    args |> Array.tryFindIndex (fun a -> a = "--log-level" || a.StartsWith("--log-level="))
    |> Option.map (fun idx ->
        let raw =
            if args.[idx].StartsWith("--log-level=") then args.[idx].["--log-level=".Length..]
            elif idx + 1 < args.Length then args.[idx + 1]
            else failwith "--log-level requires a value"
        parseLogLevel raw)

// 적용:
match logLevelArg with
| Some level -> Logging.setLevel level
| None -> ()  // levelSwitch 기본값 (Information) 유지

// --trace 처리: 명시적으로 alias 또는 deprecate.
// CONTEXT 결정: deprecate (삭제); --trace 사용자에게는 startup 시 warning 메시지.
```

`--trace` 호환성: 완전 삭제 (operator 가 의도적으로 마이그레이션 강제). 단, args 에서 `--trace` 발견 시 명시적 에러로 안내:
```fsharp
if args |> Array.contains "--trace" then
    failwith "--trace flag was removed in Phase 13; use --log-level=debug instead"
```

### Startup banner (13-05)

`Program.fs` 의 `app.Run()` 직전 (모든 DI resolve 끝난 후):

```fsharp
let banner =
    let sp = app.Services
    let regn = sp.GetRequiredService<RoutingAlgorithmRegistration>()
    let versionProvider = sp.GetRequiredService<IModelVersionProvider>()
    let queueOpts = sp.GetRequiredService<IOptions<QueueDispatcherOptions>>().Value
    let teacherOpts = sp.GetRequiredService<IOptions<TeacherLabelerOptions>>().Value
    sprintf "SmartRouter starting
    listen           = http://localhost:%d
    routing.algorithm = %s
    model.version    = %s
    canary.version   = %s
    canary.percent   = %d
    queue.maxconc.122B = %d
    queue.fairnessK  = %d
    teacher.cap.daily = %d
    log.dir          = %s"
        port regn.Name versionProvider.CurrentVersion
        (defaultArg versionProvider.CanaryVersion "(none)")
        canaryPercent queueOpts.MaxConcurrent122B queueOpts.FairnessK
        teacherOpts.DailyCallCap logDir

Log.Information("{Banner}", banner)
```

`{Banner}` 가 다중행 string 으로 한 LogEvent. Output template 의 `{Message:lj}` 가 multi-line 정상 출력. (l/j formatter 가 multi-line 보존.)

shutdown banner: `IHostApplicationLifetime.ApplicationStopping` 핸들러:

```fsharp
let lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>()
lifetime.ApplicationStopping.Register(fun () ->
    let inFlight = (* count, e.g. via QueueDispatcher.GetStats *)
    Log.Information("SmartRouter stopping; in-flight={InFlight}", inFlight)
) |> ignore
```

### `LogRetentionService` (13-05)

```fsharp
module SmartRouter.Cli.Adapters.LogRetentionService
open System
open System.IO
open System.Threading
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

type LogRetentionOptions =
    { OperationalDirectory : string  // mirror Logging:Directory
      OperationalRetentionDays : int   // 30
      DecisionDirectory : string  // logs/decisions
      DecisionRetentionDays : int   // 90
      DatasetsDirectory : string  // datasets
      TeacherCapRetentionDays : int   // 7
      PollIntervalMinutes : int }   // 60

type LogRetentionService(opts: IOptions<LogRetentionOptions>, logger: ILogger<LogRetentionService>) =
    inherit BackgroundService()

    let pruneFiles (dir: string) (pattern: string) (retainDays: int) (parseDate: string -> DateTimeOffset option) =
        if Directory.Exists(dir) then
            let cutoff = DateTimeOffset.UtcNow.AddDays(-float retainDays)
            for path in Directory.EnumerateFiles(dir, pattern) do
                let name = Path.GetFileName(path)
                match parseDate name with
                | Some d when d < cutoff ->
                    try
                        File.Delete(path)
                        logger.LogInformation("LogRetentionService: pruned {Path} (date={Date}, cutoff={Cutoff})", path, d, cutoff)
                    with ex ->
                        logger.LogWarning(ex, "LogRetentionService: failed to delete {Path}", path)
                | _ -> ()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            // 1시간 간격
            use timer = new PeriodicTimer(TimeSpan.FromMinutes(float opts.Value.PollIntervalMinutes))
            try
                while not stoppingToken.IsCancellationRequested do
                    let parseOperationalDate (n: string) =
                        // smart-router-2026-05-09.log or smart-router-2026-05-09_001.log
                        ...
                    let parseDecisionDate (n: string) =
                        // 2026-05-09.jsonl
                        ...
                    let parseTeacherCapDate (n: string) =
                        // teacher-cap-2026-05-09.json
                        ...
                    pruneFiles opts.Value.OperationalDirectory "smart-router-*.log" opts.Value.OperationalRetentionDays parseOperationalDate
                    pruneFiles opts.Value.DecisionDirectory "*.jsonl" opts.Value.DecisionRetentionDays parseDecisionDate
                    pruneFiles opts.Value.DatasetsDirectory "teacher-cap-*.json" opts.Value.TeacherCapRetentionDays parseTeacherCapDate
                    do! timer.WaitForNextTickAsync(stoppingToken).AsTask() :> Task
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.LogError(ex, "LogRetentionService: outer loop crashed")
        } :> Task
```

### Test scenarios (13-06)

`tests/SmartRouter.Tests/LogRotationTests.fs` (NEW) 또는 `LoggingTests.fs` 확장:

1. `output_template_renders_iso8601_timestamp` — CapturingSink 로 emit; `{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz}` 결과 정규식 매칭
2. `output_template_renders_correlation_id_braces` — request scope 시뮬레이션 `LogContext.PushProperty("correlation_id", "abc12345")`; output 에 `[abc12345]`
3. `output_template_renders_dash_when_no_correlation_id` — background scope; output 에 `[-]`
4. `source_context_is_fully_qualified_type_name` — `ILogger<HealthService>` emit; output 에 `SmartRouter.Cli.Adapters.HealthService`
5. `microsoft_aspnetcore_filtered_to_warning` — Microsoft.AspNetCore.Routing 가 INFO 로 emit 했을 때 차단; WARN 통과
6. `rolling_size_creates_underscore_suffix_file` — fixture: 50KB cap (작은 값으로 테스트); 50KB 초과 emit 시 `_001` suffix 파일 출현
7. `rolling_day_creates_new_dated_file` — fake clock 으로 day boundary; 새 날짜 파일
8. `retention_deletes_oldest_when_count_exceeded` — `retainedFileCountLimit: 3` fixture; 4번째 파일 작성 시 oldest 삭제
9. `LogRetentionService_prunes_decision_jsonl_over_90_days` — fake datestamps; LogRetentionService 가 90+ 일 파일 삭제
10. `LogRetentionService_prunes_teacher_cap_over_7_days`
11. `LogRetentionService_keeps_files_within_retention` — boundary case
12. `cli_log_level_debug_passes_debug_emissions`
13. `cli_log_level_warn_blocks_information`
14. `--trace_flag_errors_with_migration_message`

총 14개 testCase. 모두 `testSequenced`. 임시 디렉토리 unique-per-test (`Path.GetTempPath() + Guid.NewGuid().ToString("N")`).

### Constraint inheritance

- ARCH-01 (Pure-Core BCL-only): 변동 없음. Logging 은 모두 Cli adapter.
- TreatWarningsAsErrors=true: 유지.
- Per-task atomic commits: 유지.
- F# `task {}` only convention: LogRetentionService 가 task {} 사용.
- Phase 5 LOG-04 OBS-04 (stderr-only Serilog console sink): file sink 추가 후에도 Console sink 의 stderr-only 설정 유지.

### Claude's discretion

- Test 분할 (LoggingTests.fs 확장 vs LogRotationTests.fs 신규): 새 파일 권장 (분리된 책임).
- function-level module (Validator/Retrainer/DatasetMerger/ModelBootstrapper) 의 ILogger 처리: 함수 인자 vs `[<ThreadStatic>]` 모듈 변수 — 함수 인자 권장 (명시적, testable).
- `--log-level` 의 short alias 채택 여부: 권장 그대로 (verbose/debug/info/warn/error/fatal + info/warn 별칭).
- Startup banner 의 정확한 필드 목록: 위 예시 + 운영자 피드백 가능. executor 가 운영 시점에 useful 한 추가 필드 (e.g., decision-log-dir, hard-case-dataset-path) 자유 추가.
- Output template 에서 SourceContext 가 너무 길어 지저분하면 `{SourceContext:l}` 다음에 `:l` 적용 + namespace 부분 trim 유틸리티: pragmatic, executor 결정.

</specifics>

<deferred>
## Deferred Ideas

- **Compact JSON file sink**: log shipping 도입 시 (Promtail/Loki/Vector). 별도 phase.
- **`--log-level` 의 per-category override CLI** (e.g., `--log-level=Microsoft.AspNetCore=debug`): 복잡도 비합리적. appsettings.json 의 Override 로 충분.
- **SIGHUP 핸들러로 로그 reopen**: newsyslog 호환을 위해 필요할 수 있으나 Q9=document only 로 비활성. 로그 shipping 도입 시 재고.
- **Distributed tracing (OpenTelemetry)**: 다른 phase. 현재 correlation_id 단일 hop 만 다룸.
- **Audit log (operator action 기록)** vs operational log: 분리 필요해지면 별도 sink. 지금은 Serilog 단일 stream 으로 충분.
- **HTTP request access log** (Apache common log format): Microsoft.AspNetCore.Hosting 의 W3CLogger 를 검토 — Phase 14+.
- **`smart-router.err` SIGHUP-reopen 지원**: Q9 document only. 필요 시 SIGHUP 핸들러 추가.

</deferred>

---

*Phase: 13-service-logging*
*Context locked: 2026-05-09*
*Source: §7 of 13-logging-research-and-design.md*
