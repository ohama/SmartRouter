---
phase: 13-service-logging
plan: 06
type: execute
wave: 5
depends_on: ["13-03", "13-04", "13-05"]
files_modified:
  - tests/SmartRouter.Tests/LogRotationTests.fs (NEW)
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
  - tests/SmartRouter.Tests/RouterTests.fs
  - README.md
autonomous: true

must_haves:
  truths:
    - "tests/SmartRouter.Tests/LogRotationTests.fs exists with 13+ testCase entries covering: ISO-8601 timestamp, [correlation_id] rendering, [-] default rendering, fully-qualified SourceContext, Microsoft.AspNetCore filter, rolling size suffix, rolling daily, retention oldest-deleted, LogRetentionService 90-day decision JSONL, LogRetentionService 7-day teacher-cap, LogRetentionService keeps within retention, --log-level=debug allows debug, --log-level=warn blocks information, --trace migration error"
    - "All new tests wrapped in testSequenced; each uses unique temp directory via Path.GetTempPath() + Guid.NewGuid().ToString(\"N\"); cleanup in try/finally"
    - "Tests.fsproj includes <Compile Include=\"LogRotationTests.fs\" /> in compile order; placed alongside other test modules"
    - "RouterTests.fs rootTests list includes LogRotationTests.tests"
    - "Total test count: previous baseline + 13 (or however many testCase entries; testList groupings allowed)"
    - "README.md has a new \"Operational Logging\" section explaining: log file locations (Logging:Directory), rotation (50MB/30day), retention, --log-level CLI, smart-router.err quarterly truncation, common operator queries (grep/tail/jq examples)"
    - "dotnet build clean; dotnet test green"
---

<objective>
Add 13+ test cases verifying Phase 13 logging behavior (output template, rolling, retention, per-category override, CLI --log-level, --trace migration error). Add README operator-guide section explaining log files, rotation, retention, and common operator queries.

Final phase: tests prove the design works; README ensures the operator can use it.
</objective>

<execution_context>
@./.planning/phases/13-service-logging/13-CONTEXT.md
</execution_context>

<context>
@.planning/phases/13-service-logging/13-CONTEXT.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Create LogRotationTests.fs with 13+ testCase entries</name>
  <files>
    - tests/SmartRouter.Tests/LogRotationTests.fs (NEW)
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs
  </files>
  <action>
**Step 1.** Create `tests/SmartRouter.Tests/LogRotationTests.fs`. Skeleton:

```fsharp
module SmartRouter.Tests.LogRotationTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.Extensions.Options
open Serilog
open Serilog.Core
open Serilog.Events
open SmartRouter.Cli.Adapters

/// Each test gets its own temp directory; cleanup in finally.
let private withTempDir (fn: string -> 'a) : 'a =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try fn dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

/// Build a test config that points logs to the given temp dir.
let private buildTestConfig (logDir: string) =
    let cfg = ConfigurationBuilder()
                  .AddInMemoryCollection(dict [
                      "Logging:Directory", logDir
                      "Logging:RetentionDays", "30"
                      "DecisionLog:RetentionDays", "90"
                      "Serilog:MinimumLevel:Default", "Verbose"
                      // ... other minimal config ...
                  ])
                  .Build() :> IConfiguration
    cfg

/// CapturingSink — collects LogEvents in-memory for assertions.
/// (Reuse from LoggingTests.fs Phase 5 pattern.)
type private CapturingSink() =
    let events = System.Collections.Concurrent.ConcurrentBag<LogEvent>()
    member _.Events = events :> seq<LogEvent>
    interface ILogEventSink with
        member _.Emit(le: LogEvent) = events.Add(le)

let tests =
    testSequenced (testList "log-rotation" [

        testCase "Output template includes ISO-8601 timestamp with milliseconds" <| fun () ->
            withTempDir <| fun dir ->
                let sink = CapturingSink()
                Log.Logger <-
                    LoggerConfiguration()
                        .Enrich.WithProperty("correlation_id", "-")
                        .WriteTo.Sink(sink :> ILogEventSink)
                        .CreateLogger()
                Log.Information("hello")
                Log.CloseAndFlush()
                let event = Seq.head sink.Events
                let timestampStr = event.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz")
                let regex = Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+\-]\d{2}:\d{2}$")
                Expect.isTrue (regex.IsMatch(timestampStr)) "ISO-8601 timestamp shape"

        testCase "[{correlation_id}] renders abc12345 when LogContext pushes property" <| fun () ->
            withTempDir <| fun dir ->
                // Setup CapturingSink, push correlation_id property, emit, assert event.Properties contains "correlation_id" = "abc12345"
                ...
                ()  // placeholder; executor implements

        testCase "[-] renders when correlation_id property is not pushed" <| fun () ->
            // Setup: emit without LogContext.PushProperty
            // Assert: event.Properties.["correlation_id"].ToString() = "\"-\"" (the default enricher value)
            ...
            ()

        testCase "{SourceContext} is fully-qualified when ILogger<HealthService> is used" <| fun () ->
            // Setup: register LoggerFactory with a CapturingSink-backed Serilog
            // Resolve ILogger<HealthService>; emit; assert event.Properties.["SourceContext"] = "SmartRouter.Cli.Adapters.HealthService"
            ...
            ()

        testCase "Microsoft.AspNetCore.* events filtered to Warning by Override" <| fun () ->
            // Setup: configuration has Serilog:MinimumLevel:Override:Microsoft.AspNetCore = "Warning"
            // Emit Information from "Microsoft.AspNetCore.Routing" SourceContext
            // Assert: event NOT in sink.Events
            ...
            ()

        testCase "rolling file: size cap creates _001 suffix file" <| fun () ->
            // Setup: file sink with fileSizeLimitBytes=1024 (1KB tiny cap)
            // Emit enough Information events to exceed 1KB
            // List files in dir; assert >= 2 files; one with "_001" suffix
            ...
            ()

        testCase "rolling file: day boundary creates new dated file" <| fun () ->
            // Hard to test without fake clock; can use rollingInterval=Hour for a faster test
            ...
            ()

        testCase "retention: retainedFileCountLimit=3 deletes oldest on 4th file creation" <| fun () ->
            // Setup: write 4 mock log files with different mtimes
            // Trigger rolling; assert oldest deleted
            ...
            ()

        testCase "LogRetentionService prunes decision JSONL older than 90 days" <| fun () ->
            // Setup: temp logs/decisions/2025-01-01.jsonl + 2026-04-01.jsonl
            // Run LogRetentionService manually (with stoppingToken to cancel after one tick)
            // Assert: 2025 file deleted; 2026 file retained
            ...
            ()

        testCase "LogRetentionService prunes teacher-cap older than 7 days" <| fun () ->
            // Same pattern with datasets/teacher-cap-{date}.json
            ...
            ()

        testCase "LogRetentionService keeps files within retention" <| fun () ->
            // Setup: file with date 5 days ago
            // Run service; assert file STILL EXISTS
            ...
            ()

        testCase "CLI --log-level=debug enables debug emissions" <| fun () ->
            // Setup: levelSwitch initialized to Information
            // Call Logging.setLevel(LogEventLevel.Debug) (simulating --log-level=debug)
            // Emit Log.Debug; assert event captured
            ...
            ()

        testCase "CLI --log-level=warning blocks Information" <| fun () ->
            // Same setup; setLevel(Warning); emit Log.Information
            // Assert: event NOT in sink.Events (filtered out)
            ...
            ()

        testCase "--trace flag raises migration error" <| fun () ->
            // Run Program.main with args = [|"--trace"|]
            // Assert: throws Exception with message containing "removed in Phase 13"
            ...
            ()
    ])
```

Each `testCase` body is roughly 10-20 lines. Total file: ~250-350 lines.

**Implementation tip:** several tests rely on the same fixture setup (`buildTestConfig` + `CapturingSink` + Logging configuration). Factor common setup into helpers above the testList. Use `withTempDir` from above for isolation.

**Step 2.** Add to `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj`:

```xml
<Compile Include="LogRotationTests.fs" />
```

Place AFTER `LoggingTests.fs` (so Phase 5 logging tests still come first; Phase 13 builds on them).

**Step 3.** Update `tests/SmartRouter.Tests/RouterTests.fs` `rootTests` list:

```fsharp
let rootTests =
    testList "smart-router" [
        ...
        LoggingTests.tests
        LogRotationTests.tests   // NEW
        FailureDetectorTests.tests
        ...
    ]
```

Order: alongside LoggingTests for thematic grouping.
  </action>
  <verify>
```bash
test -f tests/SmartRouter.Tests/LogRotationTests.fs && echo OK || echo MISSING
grep -c "testCase\|ptestCase" tests/SmartRouter.Tests/LogRotationTests.fs
# expected: >= 13
grep -c "LogRotationTests\.tests" tests/SmartRouter.Tests/RouterTests.fs
# expected: 1
grep -c "LogRotationTests\.fs" tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
# expected: 1
dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj 2>&1 | tail -3
# Build succeeded.
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --filter "FullyQualifiedName~LogRotation" --no-restore 2>&1 | tail -5
# expected: ~13 passed
```
  </verify>
</task>

<task type="auto">
  <name>Task 2: README — Operational Logging section</name>
  <files>README.md</files>
  <action>
Add a new section to `README.md`. Place after existing operator-guide content (Deployment, Running, etc.) and before any future-roadmap section.

```markdown
## Operational Logging

smart-router emits two parallel log streams:

| Stream | Purpose | Format | Audience |
|---|---|---|---|
| Operational | What's happening; structured events with timestamp, level, source, correlation_id | text | operator (`tail -f`, post-mortem) |
| Decision | One row per chat-completion request | strict JSONL schema | FailureDetector, dashboards, retraining |

### Operational log files

Default location: `./logs/operational/` (or `WorkingDirectory + logs/operational/` under launchd, configurable via `appsettings.json:Logging:Directory`).

```
logs/operational/
├── smart-router-20260509.log       # daily roll
├── smart-router-20260509_001.log   # size-roll within day (50MB cap)
└── ...                              # auto-pruned after 30 days
```

(Filename pattern: `smart-router-{Date:yyyyMMdd}[_{Counter:000}].log`. Serilog default; the `_NNN` suffix appears when a single day's file exceeds 50MB.)

Each log line:

```
2026-05-09T14:32:11.123+09:00 [INF] SmartRouter.Cli.Adapters.HealthService [-] HealthService: Qwen35B reachable (transitioned from down)
2026-05-09T14:32:11.456+09:00 [INF] SmartRouter.Cli.Endpoints.ChatCompletions [abc12345...] /v1/chat/completions request received
2026-05-09T14:32:11.789+09:00 [WRN] SmartRouter.Cli.Adapters.QueueDispatcher [abc12345...] QueueDispatcher: 122B queue depth=8 (high water)
```

- `[-]` = log not associated with a specific HTTP request (background services, startup)
- `[abc12345...]` = correlation_id (32-char hex) — joinable with the same `correlation_id` field in JSONL DecisionLog

### Decision log files

Default location: `./logs/decisions/`. One file per day (`YYYY-MM-DD.jsonl`); each line is a JSON object with the 12-field schema (correlation_id, prompt_hash, target, latency_ms, etc.). See [Decision Log Schema](docs/decision-log.md) for full field details.

Retained 90 days by default; configurable via `appsettings.json:DecisionLog:RetentionDays`.

### Log levels

Default level: Information. Override via CLI:

```bash
dotnet run --project src/SmartRouter.Cli -- --log-level=debug
dotnet run --project src/SmartRouter.Cli -- --log-level=warn
```

Valid values: `verbose`, `debug`, `information`, `warning`, `error`, `fatal`. Short aliases: `info`, `warn`, `dbg`, `vrb`, `err`, `ftl`.

(The pre-Phase-13 `--trace` flag was removed; use `--log-level=debug` instead.)

Per-category overrides in `appsettings.json:Serilog:MinimumLevel:Override` — currently filters ASP.NET host noise to Warning; tweak as needed.

### Common operator queries

```bash
# Tail live operational log (cd to install dir first)
tail -f logs/operational/smart-router-$(date +%Y%m%d).log

# Find all warning + error events from today
grep -E '\[(WRN|ERR)\]' logs/operational/smart-router-$(date +%Y%m%d).log

# Trace a specific request by correlation_id (across both streams)
CID=abc12345
grep "\[$CID\]" logs/operational/smart-router-*.log
grep "\"correlation_id\":\"$CID\"" logs/decisions/*.jsonl

# Count requests by target (last 24h)
grep -h target logs/decisions/$(date +%Y-%m-%d).jsonl | jq -r '.target' | sort | uniq -c

# Count fallback events (last 7 days)
for f in logs/decisions/$(date -v -6d +%Y-%m-%d).jsonl logs/decisions/$(date -v -5d +%Y-%m-%d).jsonl ... ; do
  jq 'select(.fallback_used == true)' < "$f" | wc -l
done

# Find requests that timed out
grep "TaskCanceledException\|cancelled" logs/operational/smart-router-*.log
```

### launchd-captured stderr

If you run smart-router under launchd, `/Users/ohama/llm-system/services/logs/smart-router.err` receives anything that hits stderr (Serilog also writes there). Once Serilog is initialized (~50ms after process start), the rolling file at `logs/operational/` is the authoritative log; `smart-router.err` is mostly empty in steady state.

Quarterly housekeeping (or whenever it grows): `truncate -s 0 /Users/ohama/llm-system/services/logs/smart-router.err`. Serilog will continue writing to its own files unaffected.

### Retention

- Operational rolling: **30 days** (Logging:RetentionDays). At ~50MB/day baseline, retains ≤ 1.5GB.
- Decision JSONL: **90 days** (DecisionLog:RetentionDays). ~1MB/day baseline = ≤ 90MB.
- Teacher cap counters (`datasets/teacher-cap-*.json`): **7 days**.

`LogRetentionService` (BackgroundService) prunes hourly. Operator action not required.
```

Adjust paths (`/Users/ohama/llm-system/services/...`) to match the operator's actual install path or use `~/llm-system/...` shorthand.

If `docs/decision-log.md` doesn't exist, either link to a placeholder or remove that link reference.
  </action>
  <verify>
```bash
grep -c "Operational Logging\|smart-router-20260509\|--log-level=debug" README.md
# expected: >= 3
grep -c "logs/operational/\|logs/decisions/" README.md
# expected: >= 2
```
  </verify>
</task>

</tasks>

<verification>
- [x] LogRotationTests.fs with 13+ testCase entries; all wrapped in testSequenced
- [x] Tests.fsproj + RouterTests.rootTests updated
- [x] Test count baseline + 13
- [x] README "Operational Logging" section with file layout, levels, common queries
- [x] dotnet build clean; dotnet test green
</verification>

---

## Phase 13 deliverables (post-execution)

After all 6 plans execute successfully:

- `Adapters/Logging.fs` rewritten with dual sink (Console stderr + rolling File), `ReadFrom.Configuration` binding, new output template
- `Adapters/LogRetentionService.fs` (NEW) — BackgroundService prunes operational/decision/teacher-cap files
- All 19 emitting source files migrated to `ILogger<T>` constructor injection (or `ILogger` function parameter)
- `appsettings.json:Serilog` activated with per-category Override table; `Logging:Directory` + `Logging:RetentionDays` + `DecisionLog:RetentionDays` keys added
- Hot-path log volume cut by demoting 2 ChatCompletions emissions; HealthService transitions only
- 4 endpoint files emit DEBUG hit signals
- `--log-level=enum` CLI flag replaces `--trace`; legacy `--trace` raises migration error
- Startup banner emits port + model_version + canary state + queue config
- Shutdown banner emits in-flight + queue depths
- 13+ new test cases in LogRotationTests.fs
- README "Operational Logging" section with operator query examples
- Build clean; ARCH-01 preserved (Logging is Cli-only); test count baseline + 13
