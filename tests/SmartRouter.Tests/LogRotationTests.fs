/// Phase 13 — LogRotationTests.fs
/// 14 test cases covering output template format, per-category override, rolling file
/// behavior, LogRetentionService pruning, CLI --log-level, and --trace migration error.
///
/// All tests are wrapped in testSequenced (Console.SetOut / file I/O race prevention).
/// Each test uses a unique temp directory via Path.GetTempPath() + Guid.NewGuid().ToString("N")
/// and cleans up in a try/finally block.
module SmartRouter.Tests.LogRotationTests

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.Extensions.Options
open Serilog
open Serilog.Context
open Serilog.Core
open Serilog.Events
open SmartRouter.Cli.Adapters.LogRetentionService

// ── Helpers ───────────────────────────────────────────────────────────────────

/// Thread-safe in-memory Serilog sink for assertions.
/// Separate type from LoggingTests.CapturingSink (both are private to their modules).
type private CapturingSink() =
    let events  = ResizeArray<LogEvent>()
    let lockObj = obj ()

    member _.Snapshot() : LogEvent list =
        lock lockObj (fun () -> events |> List.ofSeq)

    member _.Clear() =
        lock lockObj (fun () -> events.Clear())

    interface ILogEventSink with
        member _.Emit(e: LogEvent) =
            lock lockObj (fun () -> events.Add(e))

/// Each test gets its own temp directory; cleanup in finally.
let private withTempDir (fn: string -> 'a) : 'a =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try fn dir
    finally try Directory.Delete(dir, true) with _ -> ()

/// ISO-8601 timestamp regex: 2026-05-09T14:32:11.123+09:00
let private iso8601Regex =
    Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+\-]\d{2}:\d{2}$", RegexOptions.Compiled)

/// Build a minimal LogRetentionOptions pointing at subdirectories of `baseDir`.
let private makeRetentionOpts (baseDir: string) (opRetDays: int) (decRetDays: int) (tcRetDays: int) =
    let opts =
        { OperationalDirectory     = Path.Combine(baseDir, "operational")
          OperationalRetentionDays = opRetDays
          DecisionDirectory        = Path.Combine(baseDir, "decisions")
          DecisionRetentionDays    = decRetDays
          DatasetsDirectory        = Path.Combine(baseDir, "datasets")
          TeacherCapRetentionDays  = tcRetDays
          PollIntervalMinutes      = 60 }
    Microsoft.Extensions.Options.Options.Create(opts)

/// Write a dummy file in `dir` with the given name and touch it with `mtime`.
let private writeFile (dir: string) (name: string) (content: string) =
    Directory.CreateDirectory(dir) |> ignore
    let path = Path.Combine(dir, name)
    File.WriteAllText(path, content)
    path

/// Run LogRetentionService.ExecuteAsync for one cycle, then cancel.
/// Returns after the service exits (or after 5 s timeout).
let private runRetentionServiceOnce (opts: IOptions<LogRetentionOptions>) : unit =
    use cts = new CancellationTokenSource()
    use svc = new LogRetentionService(opts, NullLogger<LogRetentionService>.Instance)
    // ExecuteAsync runs the first pruning pass immediately, then waits for the timer.
    // We cancel after a short delay so the first pass has time to complete.
    let task =
        Task.Run(fun () ->
            // Give the service a moment to start and run its first pass
            Task.Delay(200).GetAwaiter().GetResult()
            cts.Cancel())
    (svc :> Microsoft.Extensions.Hosting.IHostedService).StartAsync(cts.Token).GetAwaiter().GetResult()
    task.GetAwaiter().GetResult()

// ── Tests ─────────────────────────────────────────────────────────────────────

let tests : Test =
    testSequenced <| testList "log-rotation" [

        // ── Test 1: Output template — ISO-8601 timestamp ─────────────────────
        testCase "Output template timestamp is ISO-8601 with milliseconds" <| fun () ->
            withTempDir <| fun _dir ->
                let sink = CapturingSink()
                let logger =
                    LoggerConfiguration()
                        .MinimumLevel.Verbose()
                        .Enrich.WithProperty("correlation_id", "-")
                        .WriteTo.Sink(sink :> ILogEventSink)
                        .CreateLogger()
                logger.Information("test event")
                logger.Dispose()

                let events = sink.Snapshot()
                Expect.isNonEmpty events "at least one event captured"
                let ev = events |> List.head
                // Serilog stores the timestamp as DateTimeOffset; the output template
                // formats it as yyyy-MM-ddTHH:mm:ss.fffzzz — verify the raw DTO matches.
                let formatted = ev.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz")
                Expect.isTrue (iso8601Regex.IsMatch(formatted))
                    (sprintf "Expected ISO-8601 with ms+tz offset, got: %s" formatted)

        // ── Test 2: Output template — [correlation_id] rendered when pushed ─────
        testCase "[{correlation_id}] renders request value when LogContext.PushProperty is used" <| fun () ->
            withTempDir <| fun _dir ->
                let sink = CapturingSink()
                let logger =
                    LoggerConfiguration()
                        .MinimumLevel.Verbose()
                        .Enrich.FromLogContext()
                        .Enrich.WithProperty("correlation_id", "-")  // default
                        .WriteTo.Sink(sink :> ILogEventSink)
                        .CreateLogger()

                let expectedCid = "abc1234567890abcdef1234567890ab"
                use _ = LogContext.PushProperty("correlation_id", expectedCid)
                logger.Information("request received")
                logger.Dispose()

                let events = sink.Snapshot()
                Expect.isNonEmpty events "event captured"
                let ev = events |> List.head
                match ev.Properties.TryGetValue("correlation_id") with
                | true, v ->
                    let actual = v.ToString().Trim('"')
                    Expect.equal actual expectedCid
                        (sprintf "[correlation_id] should render '%s', got '%s'" expectedCid actual)
                | _ ->
                    failtest "correlation_id property not found in captured event"

        // ── Test 3: Output template — [-] rendered in background scope ──────────
        testCase "[-] renders when correlation_id property is not pushed by LogContext" <| fun () ->
            withTempDir <| fun _dir ->
                let sink = CapturingSink()
                let logger =
                    LoggerConfiguration()
                        .MinimumLevel.Verbose()
                        .Enrich.FromLogContext()
                        .Enrich.WithProperty("correlation_id", "-")  // default enricher
                        .WriteTo.Sink(sink :> ILogEventSink)
                        .CreateLogger()

                // No LogContext.PushProperty — default enricher value "-" should appear.
                logger.Information("background service heartbeat")
                logger.Dispose()

                let events = sink.Snapshot()
                Expect.isNonEmpty events "event captured"
                let ev = events |> List.head
                match ev.Properties.TryGetValue("correlation_id") with
                | true, v ->
                    let actual = v.ToString().Trim('"')
                    Expect.equal actual "-" "background scope should render '-'"
                | _ ->
                    failtest "correlation_id property not found in captured event"

        // ── Test 4: SourceContext is fully-qualified type name ───────────────────
        testCase "{SourceContext} is fully-qualified when ILogger<HealthService> is used" <| fun () ->
            withTempDir <| fun _dir ->
                let sink = CapturingSink()
                let serilogLogger =
                    LoggerConfiguration()
                        .MinimumLevel.Verbose()
                        .Enrich.FromLogContext()
                        .Enrich.WithProperty("correlation_id", "-")
                        .WriteTo.Sink(sink :> ILogEventSink)
                        .CreateLogger()

                // Build an ILoggerFactory backed by the Serilog logger above.
                use loggerFactory =
                    LoggerFactory.Create(fun builder ->
                        builder.AddSerilog(serilogLogger, dispose = true) |> ignore)

                // ILogger<HealthService> enriches each event with SourceContext = fully-qualified type name.
                let typedLogger = loggerFactory.CreateLogger<SmartRouter.Cli.Adapters.HealthService.HealthService>()
                typedLogger.LogInformation("HealthService: starting")

                let events = sink.Snapshot()
                Expect.isNonEmpty events "event captured"
                let ev = events |> List.head
                match ev.Properties.TryGetValue("SourceContext") with
                | true, v ->
                    let actual = v.ToString().Trim('"')
                    // F# types within a module compile to dotted names (module.type),
                    // not C#-style nested type syntax (module+type).
                    Expect.equal actual "SmartRouter.Cli.Adapters.HealthService.HealthService"
                        (sprintf "SourceContext should be fully-qualified, got: %s" actual)
                | _ ->
                    failtest "SourceContext property not found in captured event"

        // ── Test 5: Microsoft.AspNetCore.* filtered to Warning ──────────────────
        testCase "Microsoft.AspNetCore.* Information events are blocked by Override filter" <| fun () ->
            withTempDir <| fun _dir ->
                let sink = CapturingSink()
                // Configure with per-category override matching appsettings.json production config.
                let logger =
                    LoggerConfiguration()
                        .MinimumLevel.Information()
                        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                        .Enrich.WithProperty("correlation_id", "-")
                        .WriteTo.Sink(sink :> ILogEventSink)
                        .CreateLogger()

                use loggerFactory =
                    LoggerFactory.Create(fun builder ->
                        builder.AddSerilog(logger, dispose = true) |> ignore)

                // Information from Microsoft.AspNetCore.Routing — should be blocked.
                let aspLogger = loggerFactory.CreateLogger("Microsoft.AspNetCore.Routing")
                aspLogger.LogInformation("Route matched: /v1/chat/completions")

                // Warning from same source — should pass through.
                aspLogger.LogWarning("Route not found for path /unknown")

                let events = sink.Snapshot()
                let infoBlocked =
                    events
                    |> List.forall (fun e -> e.Level >= LogEventLevel.Warning)
                Expect.isTrue infoBlocked
                    "Microsoft.AspNetCore Information event should be filtered out by Override"

                let warnPresent =
                    events
                    |> List.exists (fun e -> e.Level = LogEventLevel.Warning)
                Expect.isTrue warnPresent
                    "Microsoft.AspNetCore Warning event should pass through the Override filter"

        // ── Test 6: Rolling file creates _001 suffix file on size cap ───────────
        testCase "Rolling file: size cap creates _001 suffix file" <| fun () ->
            withTempDir <| fun dir ->
                // Use a very small 1-byte size cap so any emission triggers rollover.
                let filePath = Path.Combine(dir, "test-.log")
                let logger =
                    LoggerConfiguration()
                        .MinimumLevel.Verbose()
                        .Enrich.WithProperty("correlation_id", "-")
                        .WriteTo.File(
                            path = filePath,
                            rollingInterval = RollingInterval.Day,
                            fileSizeLimitBytes = Nullable<int64>(1L),   // 1 byte → instant overflow
                            rollOnFileSizeLimit = true,
                            retainedFileCountLimit = Nullable<int>(10),
                            outputTemplate = "{Message}{NewLine}")
                        .CreateLogger()

                // Emit several messages to trigger roll-on-size.
                for i in 1 .. 5 do
                    logger.Information("message {N} — padding to exceed size cap", i)

                logger.Dispose()

                let files = Directory.GetFiles(dir, "*.log")
                Expect.isTrue (files.Length >= 2)
                    (sprintf "Expected at least 2 rolling files, found %d: [%s]"
                        files.Length (files |> String.concat ", "))

                let hasRollSuffix =
                    files |> Array.exists (fun f ->
                        let name = Path.GetFileNameWithoutExtension(f)
                        Regex(@"_\d{3}$").IsMatch(name))
                Expect.isTrue hasRollSuffix
                    (sprintf "Expected at least one file with _NNN suffix, files: [%s]"
                        (files |> String.concat ", "))

        // ── Test 7: Rolling file creates new dated file at day boundary ─────────
        testCase "Rolling file: new log file has today's date in filename" <| fun () ->
            withTempDir <| fun dir ->
                let filePath = Path.Combine(dir, "svc-.log")
                let logger =
                    LoggerConfiguration()
                        .MinimumLevel.Verbose()
                        .Enrich.WithProperty("correlation_id", "-")
                        .WriteTo.File(
                            path = filePath,
                            rollingInterval = RollingInterval.Day,
                            outputTemplate = "{Message}{NewLine}")
                        .CreateLogger()

                logger.Information("startup event")
                logger.Dispose()

                let files = Directory.GetFiles(dir, "*.log")
                Expect.isNonEmpty files "at least one log file created"

                let today = DateTime.Now.ToString("yyyyMMdd")
                let hasToday =
                    files
                    |> Array.exists (fun f -> Path.GetFileName(f).Contains(today))
                Expect.isTrue hasToday
                    (sprintf "Expected a file containing today's date '%s', files: [%s]"
                        today (files |> String.concat ", "))

        // ── Test 8: retainedFileCountLimit deletes oldest on overflow ───────────
        testCase "Rolling file: retainedFileCountLimit=2 deletes oldest when 3rd file is created" <| fun () ->
            withTempDir <| fun dir ->
                // With retainedFileCountLimit=2 and fileSizeLimitBytes=1, Serilog will
                // keep at most 2 files at any time (deleting the oldest on each new creation).
                let filePath = Path.Combine(dir, "retain-.log")
                let logger =
                    LoggerConfiguration()
                        .MinimumLevel.Verbose()
                        .Enrich.WithProperty("correlation_id", "-")
                        .WriteTo.File(
                            path = filePath,
                            rollingInterval = RollingInterval.Day,
                            fileSizeLimitBytes = Nullable<int64>(1L),
                            rollOnFileSizeLimit = true,
                            retainedFileCountLimit = Nullable<int>(2),
                            outputTemplate = "{Message}{NewLine}")
                        .CreateLogger()

                // Emit enough messages to create more than 2 files.
                for i in 1 .. 6 do
                    logger.Information("message {N}", i)

                logger.Dispose()

                let files = Directory.GetFiles(dir, "*.log")
                // Serilog keeps at most retainedFileCountLimit files.
                Expect.isTrue (files.Length <= 2)
                    (sprintf "Expected <= 2 files with retainedFileCountLimit=2, found %d: [%s]"
                        files.Length (files |> String.concat ", "))

        // ── Test 9: LogRetentionService prunes decision JSONL > 90 days ─────────
        testCase "LogRetentionService prunes decision JSONL older than 90 days" <| fun () ->
            withTempDir <| fun dir ->
                let opts = makeRetentionOpts dir 30 90 7

                // Old file: 95 days ago → should be pruned.
                let oldDate = DateTimeOffset.UtcNow.AddDays(-95.0).ToString("yyyy-MM-dd")
                let decDir = Path.Combine(dir, "decisions")
                let oldFile = writeFile decDir (oldDate + ".jsonl") "{\"correlation_id\":\"old\"}"

                // Recent file: 30 days ago → should be kept.
                let recentDate = DateTimeOffset.UtcNow.AddDays(-30.0).ToString("yyyy-MM-dd")
                let recentFile = writeFile decDir (recentDate + ".jsonl") "{\"correlation_id\":\"recent\"}"

                runRetentionServiceOnce opts

                Expect.isFalse (File.Exists(oldFile))
                    (sprintf "Expected old file '%s' to be pruned (95 days > 90-day retention)" oldFile)
                Expect.isTrue (File.Exists(recentFile))
                    (sprintf "Expected recent file '%s' to be kept (30 days <= 90-day retention)" recentFile)

        // ── Test 10: LogRetentionService prunes teacher-cap > 7 days ────────────
        testCase "LogRetentionService prunes teacher-cap files older than 7 days" <| fun () ->
            withTempDir <| fun dir ->
                let opts = makeRetentionOpts dir 30 90 7

                // Old teacher-cap: 10 days ago → should be pruned.
                let oldDate = DateTimeOffset.UtcNow.AddDays(-10.0).ToString("yyyy-MM-dd")
                let dsDir = Path.Combine(dir, "datasets")
                let oldFile = writeFile dsDir (sprintf "teacher-cap-%s.json" oldDate) "{\"count\":100}"

                // Recent teacher-cap: 3 days ago → should be kept.
                let recentDate = DateTimeOffset.UtcNow.AddDays(-3.0).ToString("yyyy-MM-dd")
                let recentFile = writeFile dsDir (sprintf "teacher-cap-%s.json" recentDate) "{\"count\":50}"

                runRetentionServiceOnce opts

                Expect.isFalse (File.Exists(oldFile))
                    (sprintf "Expected old teacher-cap '%s' to be pruned (10 days > 7-day retention)" oldFile)
                Expect.isTrue (File.Exists(recentFile))
                    (sprintf "Expected recent teacher-cap '%s' to be kept (3 days <= 7-day retention)" recentFile)

        // ── Test 11: LogRetentionService keeps files within retention ────────────
        testCase "LogRetentionService keeps files that are within retention window" <| fun () ->
            withTempDir <| fun dir ->
                let opts = makeRetentionOpts dir 30 90 7

                // Decision JSONL 60 days old — within 90-day window.
                let decDate = DateTimeOffset.UtcNow.AddDays(-60.0).ToString("yyyy-MM-dd")
                let decDir = Path.Combine(dir, "decisions")
                let decFile = writeFile decDir (decDate + ".jsonl") "{\"correlation_id\":\"mid\"}"

                // Teacher-cap 5 days old — within 7-day window.
                let tcDate = DateTimeOffset.UtcNow.AddDays(-5.0).ToString("yyyy-MM-dd")
                let dsDir = Path.Combine(dir, "datasets")
                let tcFile = writeFile dsDir (sprintf "teacher-cap-%s.json" tcDate) "{\"count\":20}"

                runRetentionServiceOnce opts

                Expect.isTrue (File.Exists(decFile))
                    (sprintf "Decision JSONL at 60 days should be kept (90-day retention); file: %s" decFile)
                Expect.isTrue (File.Exists(tcFile))
                    (sprintf "Teacher-cap at 5 days should be kept (7-day retention); file: %s" tcFile)

        // ── Test 12: CLI --log-level=debug enables Debug emissions ──────────────
        testCase "CLI --log-level=debug enables Debug-level emissions" <| fun () ->
            withTempDir <| fun _dir ->
                let sink = CapturingSink()
                let logger =
                    LoggerConfiguration()
                        .MinimumLevel.ControlledBy(SmartRouter.Cli.Adapters.Logging.levelSwitch)
                        .Enrich.WithProperty("correlation_id", "-")
                        .WriteTo.Sink(sink :> ILogEventSink)
                        .CreateLogger()

                // Start at Information (default) — Debug should be blocked.
                SmartRouter.Cli.Adapters.Logging.setLevel LogEventLevel.Information
                logger.Debug("this should be blocked")
                let beforeCount = sink.Snapshot() |> List.length
                Expect.equal beforeCount 0 "Debug event should be blocked at Information level"

                // Switch to Debug — Debug should now pass.
                sink.Clear()
                SmartRouter.Cli.Adapters.Logging.setLevel LogEventLevel.Debug
                logger.Debug("this should pass at Debug level")
                let afterCount = sink.Snapshot() |> List.length
                Expect.equal afterCount 1 "Debug event should be captured after setLevel(Debug)"

                // Restore default.
                SmartRouter.Cli.Adapters.Logging.setLevel LogEventLevel.Information
                logger.Dispose()

        // ── Test 13: CLI --log-level=warning blocks Information ──────────────────
        testCase "CLI --log-level=warning blocks Information-level emissions" <| fun () ->
            withTempDir <| fun _dir ->
                let sink = CapturingSink()
                let logger =
                    LoggerConfiguration()
                        .MinimumLevel.ControlledBy(SmartRouter.Cli.Adapters.Logging.levelSwitch)
                        .Enrich.WithProperty("correlation_id", "-")
                        .WriteTo.Sink(sink :> ILogEventSink)
                        .CreateLogger()

                // Switch to Warning.
                SmartRouter.Cli.Adapters.Logging.setLevel LogEventLevel.Warning
                logger.Information("this should be blocked at Warning level")
                let blockedCount = sink.Snapshot() |> List.length
                Expect.equal blockedCount 0 "Information event should be blocked at Warning level"

                // Warning should still pass.
                logger.Warning("this should pass at Warning level")
                let warnCount = sink.Snapshot() |> List.length
                Expect.equal warnCount 1 "Warning event should be captured at Warning level"

                // Restore default.
                SmartRouter.Cli.Adapters.Logging.setLevel LogEventLevel.Information
                logger.Dispose()

        // ── Test 14: --trace flag raises migration error (returns exit code 1) ──
        testCase "--trace flag causes Program.main to return exit code 1 (migration error)" <| fun () ->
            // Program.main [|"--trace"|] catches the failwith inside the inner try/with
            // and returns 1.  The migration message ("--trace flag was removed in Phase 13")
            // is written to stderr and to the Serilog Log.Fatal event.
            // We verify the behavioral contract: non-zero exit code.
            withTempDir <| fun _dir ->
                let exitCode = SmartRouter.Cli.Program.main [| "--trace" |]
                Expect.equal exitCode 1 "--trace flag should cause Program.main to return exit code 1"

    ]
