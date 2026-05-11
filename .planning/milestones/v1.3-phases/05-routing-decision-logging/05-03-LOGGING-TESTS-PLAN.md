---
phase: 05-routing-decision-logging
plan: 03
type: execute
wave: 3
depends_on: [05-01, 05-02]
files_modified:
  - tests/SmartRouter.Tests/LoggingTests.fs
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
  - tests/SmartRouter.Tests/RouterTests.fs
  - tests/SmartRouter.Tests/StreamingTests.fs
autonomous: true

must_haves:
  truths:
    - "100 concurrent identical requests produce exactly 100 valid JSON lines in the dated JSONL file — zero IOException, zero interleaved bytes, all 100 parse cleanly via JsonDocument.Parse"
    - "The correlation_id in the JSONL line for a request matches the correlation_id captured by an in-memory Serilog sink installed inside startTestRouter for that same request — proving LOG-04 propagation through Serilog stderr semantics end-to-end"
    - "The correlation_id in an SSE error event body emitted by the streaming path matches both the JSONL correlation_id AND the in-memory sink correlation_id for that request — proving LOG-04's third source (SSE error body) end-to-end"
    - "Schema completeness: a parsed JSONL line has all 12 fields with correct primitive types (int / string / float / bool / nullable string / ISO timestamp)"
    - "Graceful shutdown: route N requests, immediately call StopAsync, then read the file — file contains all N entries (no in-flight loss)"
    - "Each test isolates its log directory under Path.GetTempPath()/Path.GetRandomFileName() so tests never share files; cleanup runs in finally"
    - "All 5 tests are wrapped in testSequenced (file I/O + Serilog static Logger mutation — testSequenced rule)"
    - "LoggingTests.tests is added to RouterTests.rootTests so the test runner discovers it (no auto-discovery — explicit list)"
    - "StreamingTests.startTestRouter sets DecisionLog:Directory to a per-process temp dir so streaming tests don't pollute the bin/Debug/net10.0/logs/decisions/ directory"
  artifacts:
    - path: "tests/SmartRouter.Tests/LoggingTests.fs"
      provides: "5 tests covering LOG-01 schema completeness, LOG-02 100-concurrent thread-safety, LOG-04 correlation propagation through Serilog (in-memory sink) + JSONL, LOG-03 graceful shutdown drain, LOG-04 SSE error correlation_id"
      contains: "testSequenced"
    - path: "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
      provides: "<Compile Include=\"LoggingTests.fs\" /> in correct order (after StreamingTests.fs, before RouterTests.fs)"
      contains: "LoggingTests.fs"
    - path: "tests/SmartRouter.Tests/RouterTests.fs"
      provides: "rootTests list includes SmartRouter.Tests.LoggingTests.tests"
      contains: "LoggingTests.tests"
    - path: "tests/SmartRouter.Tests/StreamingTests.fs"
      provides: "DecisionLog:Directory override in startTestRouter's AddInMemoryCollection so streaming tests write JSONL into a temp dir cleaned at process exit, not into bin/Debug/net10.0/logs/decisions/"
      contains: "DecisionLog:Directory"
  key_links:
    - from: "LoggingTests.fs concurrency test"
      to: "DecisionLogWriter Channel"
      via: "100 parallel HttpClient.PostAsync → 100 Channel.Writer.TryWrite → 1 BackgroundService consumer → 100 lines in file"
      pattern: "Task\\.WhenAll|Async\\.Parallel"
    - from: "LoggingTests.fs correlation test"
      to: "in-memory Serilog sink + JSONL file read"
      via: "Custom CapturingSink (ILogEventSink) installed as Log.Logger by startTestRouter BEFORE configureServices fires; test reads sink.Events and parses JSONL"
      pattern: "ILogEventSink|CapturingSink"
    - from: "LoggingTests.fs SSE-error test"
      to: "ChatCompletions streaming-error arm"
      via: "Fake upstream returns malformed/early-closed stream → router emits SSE error event with correlation_id → test reads response body, finds `data: {\"error\":...}` line, parses JSON, asserts correlation_id matches"
      pattern: "data:.*error.*correlation_id"
    - from: "LoggingTests.fs graceful-shutdown test"
      to: "BackgroundService.StopAsync drain logic"
      via: "app.StopAsync() awaits drain; file then contains all in-flight entries"
      pattern: "StopAsync"
---

<objective>
Author `tests/SmartRouter.Tests/LoggingTests.fs` with 4 tests that prove the Phase 5 success criteria. This is the verification gate: the JSONL pipeline must demonstrably handle concurrency, propagate correlation IDs, ship the full 12-field schema, and not lose data on shutdown.

Purpose: Phase 5 is Loop B's input. Phases 6-9 depend on these guarantees. A test that proves "100 concurrent requests → 100 valid JSON lines" is the lowest-cost defense against latent regressions.

Output:
- LoggingTests.fs with 5 testSequenced tests:
  1. Schema completeness (LOG-01)
  2. 100-concurrent thread-safety (LOG-02)
  3. Correlation ID propagation through Serilog (in-memory sink) and JSONL (LOG-04 sources 1 & 2)
  4. Graceful shutdown drain (LOG-03)
  5. SSE error event body correlation_id propagation (LOG-04 source 3 / OBS-03)
- SmartRouter.Tests.fsproj updated with new compile entry
- RouterTests.rootTests updated with LoggingTests.tests
- StreamingTests.startTestRouter gains a `DecisionLog:Directory` override (per-process temp dir) so streaming tests don't write JSONL files into the test project's bin/Debug/net10.0/logs/decisions/ directory
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md
@.planning/phases/05-routing-decision-logging/05-CONTEXT.md
@.planning/phases/05-routing-decision-logging/05-RESEARCH.md
@.planning/phases/05-routing-decision-logging/05-01-DECISION-LOG-INFRA-PLAN.md
@.planning/phases/05-routing-decision-logging/05-02-ENDPOINT-WIRING-PLAN.md
@tests/SmartRouter.Tests/StreamingTests.fs
@tests/SmartRouter.Tests/RouterTests.fs
@tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
@tests/SmartRouter.Tests/MLRoutingTests.fs
</context>

<tasks>

<task type="auto">
  <name>Task 1: Author LoggingTests.fs with 4 tests + integrate into runner</name>
  <files>
    tests/SmartRouter.Tests/LoggingTests.fs
    tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    tests/SmartRouter.Tests/RouterTests.fs
  </files>
  <action>
**Step 1 — Reuse the StreamingTests fake-upstream + startTestRouter pattern.** Read `tests/SmartRouter.Tests/StreamingTests.fs` lines 100-176 (`startTestRouter` and `startFakeUpstream`) for the established pattern: WebApplication.CreateBuilder, AddInMemoryCollection BEFORE configureServices, mapEndpoints AFTER Build, StartAsync.

Create a similar `startTestRouter` helper in LoggingTests.fs that takes an additional `logDir: string` parameter and includes:
```fsharp
KeyValuePair("DecisionLog:Directory",       logDir)
KeyValuePair("DecisionLog:ChannelCapacity", "10000")
```
in the AddInMemoryCollection. All other Routing/Queue/Upstreams keys remain identical to StreamingTests.startTestRouter (copy them — DRY across test modules is not required here; explicit configuration is more legible).

**CRITICAL — install in-memory Serilog sink BEFORE configureServices fires.** Plan 05-01 wires `Logging.configure()` and `builder.Host.UseSerilog()` inside `Program.fs` only — the test path (`startTestRouter`) calls `configureServices` directly without going through Program.fs's Serilog setup. So `Log.Logger` will be a no-op sink in tests by default, AND the production outputTemplate doesn't render `{Properties}`, AND `Serilog.Sinks.Console` captures `Console.Error` at sink-creation time — making `Console.SetError(stderrBuf)` after-the-fact useless.

Solution: install a custom in-memory ILogEventSink BEFORE the test's `configureServices` runs, then assert against captured `LogEvent`s directly. No NuGet add required.

Add this sink type at the top of LoggingTests.fs (under the `open` block):

```fsharp
open Serilog
open Serilog.Core
open Serilog.Events

type private CapturingSink() =
    let events = ResizeArray<LogEvent>()
    let lockObj = obj()
    member _.Snapshot () : LogEvent list =
        lock lockObj (fun () -> events |> List.ofSeq)
    member _.Clear () =
        lock lockObj (fun () -> events.Clear())
    interface ILogEventSink with
        member _.Emit(e: LogEvent) =
            lock lockObj (fun () -> events.Add(e))

/// Module-level sink so all tests in LoggingTests share one instance.
/// Tests must call `capturedSink.Clear()` at the start of each test (testSequenced ensures
/// no concurrent test execution within this module).
let private capturedSink = CapturingSink()
```

Inside `startTestRouter`, BEFORE the `configureServices` call, install the sink as the global `Log.Logger`:

```fsharp
// Install an in-memory Serilog sink with FromLogContext enrichment so
// LogContext.PushProperty("correlation_id", cid) — set by CorrelationMiddleware —
// is captured in LogEvent.Properties for later inspection.
let testLogger =
    LoggerConfiguration()
        .Enrich.FromLogContext()
        .MinimumLevel.Verbose()
        .WriteTo.Sink(capturedSink :> ILogEventSink)
        .CreateLogger()
Log.Logger <- testLogger
// Wire Serilog into the host pipeline — without this, ASP.NET request-logging events
// don't fan out to Log.Logger.
testBuilder.Host.UseSerilog() |> ignore
```

`testBuilder.Host.UseSerilog()` (no args) reads from the static `Log.Logger`, exactly like `Program.fs` does. The test gets the same logging path as production but writes to memory instead of stderr.

Create a minimal fake upstream that returns 200 OK with a tiny non-streaming body (no streaming required for these logging tests):
```fsharp
let startFakeUpstream () : Task<WebApplication * int> =
    task {
        let b = WebApplication.CreateBuilder()
        b.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        let app = b.Build()
        // /v1/models — needed for QwenUpstreamClient probe (see Phase 1 lazy probe)
        app.MapGet("/v1/models",
            Func<Task<obj>>(fun () -> task {
                return box {| data = [| {| id = "/local/qwen35b" |} |] |}
            })) |> ignore
        // /v1/chat/completions — fast 200 OK
        app.MapPost("/v1/chat/completions",
            Func<HttpContext, Task>(fun ctx -> task {
                ctx.Response.StatusCode <- 200
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsync("""{"id":"x","choices":[]}""")
            })) |> ignore
        do! app.StartAsync()
        let port =
            app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>().Addresses
            |> Seq.head |> fun a -> a.Split(':') |> Array.last |> int
        return app, port
    }
```

**Step 2 — module + imports.** Open module `SmartRouter.Tests.LoggingTests` with these imports (matching what StreamingTests/MLRoutingTests already use):

```fsharp
module SmartRouter.Tests.LoggingTests

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
```

**Step 3 — temp directory helper.** A per-test isolated log dir:
```fsharp
let private mkTempLogDir () : string =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-tests-" + Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    dir

let private cleanupLogDir (dir: string) =
    try if Directory.Exists(dir) then Directory.Delete(dir, recursive = true) with _ -> ()

let private todaysLogFile (dir: string) : string =
    Path.Combine(dir, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl")
```

**Step 4 — postOnce helper.** A single non-streaming request to the router:
```fsharp
let private postOnce (client: HttpClient) (port: int) : Task<HttpResponseMessage> =
    let url = sprintf "http://127.0.0.1:%d/v1/chat/completions" port
    let req = new HttpRequestMessage(HttpMethod.Post, url)
    req.Content <- new StringContent("""{"messages":[{"role":"user","content":"hi"}]}""",
                                      Encoding.UTF8, "application/json")
    client.SendAsync(req)
```

**Step 5 — teardown helper.** Same `.GetAwaiter().GetResult()` pattern as StreamingTests:
```fsharp
let private teardown (routerApp: WebApplication) (fakeApp: WebApplication) (logDir: string) =
    try routerApp.StopAsync().GetAwaiter().GetResult() with _ -> ()
    try fakeApp.StopAsync().GetAwaiter().GetResult()   with _ -> ()
    try (routerApp :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() with _ -> ()
    try (fakeApp   :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() with _ -> ()
    cleanupLogDir logDir
```

**Step 6 — drain wait helper.** Channel writes are async — give the BackgroundService consumer a moment to drain before reading the file. Use a poll-with-timeout rather than a fixed sleep:
```fsharp
let private waitForLineCount (path: string) (expected: int) (timeoutMs: int) : Task<int> =
    task {
        let sw = Stopwatch.StartNew()
        let mutable count = 0
        while count < expected && sw.ElapsedMilliseconds < int64 timeoutMs do
            if File.Exists(path) then
                count <- File.ReadAllLines(path)
                        |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace(l)))
                        |> Array.length
            if count < expected then do! Task.Delay(50)
        return count
    }
```
(Add `open System.Diagnostics` for Stopwatch.)

**Step 7 — write the 4 tests.** Wrap the entire `testList` in `testSequenced` (every test touches files / starts services).

```fsharp
let tests : Test =
    testSequenced <| testList "LoggingTests" [

        // ── Test 1: Schema completeness (LOG-01) ─────────────────────────
        testCase "JSONL line has all 12 schema fields with correct types" <| fun () ->
            (task {
                let logDir = mkTempLogDir ()
                let! fakeApp, fakePort = startFakeUpstream ()
                let! routerApp, routerPort = startTestRouter fakePort logDir
                try
                    use client = new HttpClient()
                    use! response = postOnce client routerPort
                    Expect.equal (int response.StatusCode) 200 "router returned 200"
                    let logFile = todaysLogFile logDir
                    let! count = waitForLineCount logFile 1 5000
                    Expect.equal count 1 "exactly 1 JSONL line"

                    let line = File.ReadAllLines(logFile) |> Array.head
                    use doc = JsonDocument.Parse(line)
                    let root = doc.RootElement

                    // 12 required fields:
                    Expect.equal (root.GetProperty("schema_version").GetInt32()) 1
                                  "schema_version = 1"
                    Expect.isFalse
                        (String.IsNullOrEmpty(root.GetProperty("correlation_id").GetString()))
                        "correlation_id non-empty"
                    Expect.equal (root.GetProperty("prompt_hash").GetString().Length) 64
                                  "prompt_hash is 64-char SHA-256 hex"
                    Expect.isTrue
                        (let r = root.GetProperty("prompt_korean_char_ratio").GetDouble()
                         r >= 0.0 && r <= 1.0)
                        "prompt_korean_char_ratio in [0,1]"
                    Expect.equal (root.GetProperty("routing_algorithm").GetString())
                                  "heuristic"
                                  "routing_algorithm = heuristic (default)"
                    Expect.isFalse
                        (String.IsNullOrEmpty(root.GetProperty("routing_reason").GetString()))
                        "routing_reason non-empty"
                    let target = root.GetProperty("target").GetString()
                    Expect.isTrue (target = "Qwen35B" || target = "Qwen122B")
                                   "target is one of {Qwen35B, Qwen122B}"
                    Expect.isTrue (root.GetProperty("latency_ms").GetDouble() >= 0.0)
                                   "latency_ms >= 0"
                    Expect.isFalse (root.GetProperty("fallback_used").GetBoolean())
                                    "fallback_used = false in Phase 5"
                    Expect.equal (root.GetProperty("model_version").GetString())
                                  "heuristic-v1"
                                  "model_version = heuristic-v1 for default config"
                    // task_type may be null (Some/None) — just confirm property exists
                    Expect.isTrue (root.TryGetProperty("task_type") |> fst)
                                   "task_type property present"
                    // timestamp parses as a date
                    let ts = root.GetProperty("timestamp").GetString()
                    let parsed, _ = DateTimeOffset.TryParse(ts)
                    Expect.isTrue parsed "timestamp parses as ISO 8601"
                finally
                    teardown routerApp fakeApp logDir
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // ── Test 2: Concurrency safety (LOG-02) ──────────────────────────
        testCase "100 concurrent requests produce 100 valid JSON lines" <| fun () ->
            (task {
                let logDir = mkTempLogDir ()
                let! fakeApp, fakePort = startFakeUpstream ()
                let! routerApp, routerPort = startTestRouter fakePort logDir
                try
                    use client = new HttpClient()
                    let tasks =
                        [ for _ in 1 .. 100 -> postOnce client routerPort ]
                        |> List.toArray
                    let! responses = Task.WhenAll(tasks)
                    let okCount =
                        responses
                        |> Array.filter (fun r -> int r.StatusCode = 200)
                        |> Array.length
                    Expect.equal okCount 100 "all 100 responses returned 200"

                    let logFile = todaysLogFile logDir
                    let! count = waitForLineCount logFile 100 15000
                    Expect.equal count 100 "exactly 100 JSONL lines"

                    // Every line parses as JSON — proves no interleaved bytes
                    let lines = File.ReadAllLines(logFile)
                                |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace(l)))
                    for l in lines do
                        try use _ = JsonDocument.Parse(l) in ()
                        with ex -> failtestf "line did not parse as JSON: %s\n%s" l ex.Message

                    // Every line has 12 distinct correlation_ids
                    let cids =
                        lines
                        |> Array.map (fun l ->
                            use doc = JsonDocument.Parse(l)
                            doc.RootElement.GetProperty("correlation_id").GetString())
                        |> Array.distinct
                    Expect.equal cids.Length 100 "100 distinct correlation_ids"
                finally
                    teardown routerApp fakeApp logDir
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // ── Test 3: Correlation ID propagation through Serilog + JSONL (LOG-04) ───
        testCase "correlation_id in JSONL matches correlation_id in Serilog LogEvents" <| fun () ->
            (task {
                capturedSink.Clear()
                let logDir = mkTempLogDir ()
                let! fakeApp, fakePort = startFakeUpstream ()
                let! routerApp, routerPort = startTestRouter fakePort logDir
                try
                    use client = new HttpClient()
                    use! response = postOnce client routerPort
                    Expect.equal (int response.StatusCode) 200 "200 OK"
                    let logFile = todaysLogFile logDir
                    let! count = waitForLineCount logFile 1 5000
                    Expect.equal count 1 "1 JSONL line"

                    let line = File.ReadAllLines(logFile) |> Array.head
                    use doc = JsonDocument.Parse(line)
                    let jsonlCid = doc.RootElement.GetProperty("correlation_id").GetString()

                    // The in-memory sink is synchronous: by the time the response returned,
                    // every log event for this request has been emitted. NO Task.Delay needed.
                    let events = capturedSink.Snapshot()

                    // Find the LogEvent whose Properties dictionary contains
                    // correlation_id = jsonlCid. The CorrelationMiddleware pushed this
                    // onto LogContext, and FromLogContext enrichment captures it.
                    let matched =
                        events
                        |> List.exists (fun e ->
                            match e.Properties.TryGetValue("correlation_id") with
                            | true, value ->
                                // ScalarValue prints the string with surrounding quotes;
                                // strip them before comparing.
                                let s = value.ToString().Trim('"')
                                s = jsonlCid
                            | _ -> false)
                    Expect.isTrue matched
                        (sprintf
                            "expected at least one Serilog LogEvent.Properties with correlation_id=%s; got %d events"
                            jsonlCid (List.length events))
                finally
                    teardown routerApp fakeApp logDir
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // ── Test 4: Graceful shutdown flush (LOG-03) ──────────────────────
        testCase "graceful shutdown drains channel — no in-flight log loss" <| fun () ->
            (task {
                let logDir = mkTempLogDir ()
                let! fakeApp, fakePort = startFakeUpstream ()
                let! routerApp, routerPort = startTestRouter fakePort logDir
                try
                    use client = new HttpClient()
                    // Fire 10 requests, await all completions (responses sent, BUT
                    // DecisionLog enqueue is fire-and-forget so writer may not have flushed).
                    let tasks =
                        [ for _ in 1 .. 10 -> postOnce client routerPort ] |> List.toArray
                    let! _ = Task.WhenAll(tasks)

                    // Stop the router IMMEDIATELY — graceful shutdown must drain the channel.
                    do! routerApp.StopAsync()

                    // Now read the file. All 10 entries must be present.
                    let logFile = todaysLogFile logDir
                    let lines =
                        if File.Exists(logFile) then
                            File.ReadAllLines(logFile)
                            |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace(l)))
                        else
                            [||]
                    Expect.equal lines.Length 10
                        "graceful shutdown drained all 10 in-flight DecisionLog entries"
                finally
                    teardown routerApp fakeApp logDir
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // ── Test 5: SSE error event body carries correlation_id (LOG-04 / OBS-03) ─
        testCase "SSE error event body contains correlation_id matching JSONL + Serilog" <| fun () ->
            (task {
                capturedSink.Clear()
                let logDir = mkTempLogDir ()
                // A malformed fake upstream: returns HTTP 200 + a tiny chunk that is NOT a
                // valid SSE frame, then closes. This trips the streaming-error arm in
                // ChatCompletions.fs which emits `data: {"error":...,"correlation_id":"..."}`.
                // Implementation detail of the malformed response: write a single non-SSE
                // chunk then close — QwenUpstreamClient surfaces this as a parsing error
                // inside the streaming enumerator.
                let! fakeApp, fakePort = startFakeStreamingErrorUpstream ()
                let! routerApp, routerPort = startTestRouter fakePort logDir
                try
                    use client = new HttpClient()
                    let url = sprintf "http://127.0.0.1:%d/v1/chat/completions" routerPort
                    let req = new HttpRequestMessage(HttpMethod.Post, url)
                    // stream=true triggers the SSE path
                    req.Content <- new StringContent(
                                    """{"messages":[{"role":"user","content":"hi"}],"stream":true}""",
                                    Encoding.UTF8, "application/json")
                    use! response = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                    let! body = response.Content.ReadAsStringAsync()

                    // Find the SSE error frame: a line starting with `data: ` whose payload
                    // parses as JSON containing `error`.
                    let dataLines =
                        body.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.filter (fun l -> l.StartsWith("data: "))
                    let errLine =
                        dataLines
                        |> Array.tryPick (fun l ->
                            let payload = l.Substring(6)  // strip "data: "
                            try
                                use d = JsonDocument.Parse(payload)
                                let mutable err = Unchecked.defaultof<JsonElement>
                                if d.RootElement.TryGetProperty("error", &err) then Some err
                                else None
                            with _ -> None)
                    let err =
                        match errLine with
                        | Some e -> e
                        | None -> failtestf "no SSE error frame found in body: %s" body

                    let mutable cidProp = Unchecked.defaultof<JsonElement>
                    Expect.isTrue (err.TryGetProperty("correlation_id", &cidProp))
                        "SSE error event body must contain correlation_id (LOG-04)"
                    let sseCid = cidProp.GetString()
                    Expect.isFalse (String.IsNullOrEmpty(sseCid)) "correlation_id non-empty"

                    // The same correlation_id must appear in the JSONL DecisionLog for this request.
                    let logFile = todaysLogFile logDir
                    let! _ = waitForLineCount logFile 1 5000
                    let line = File.ReadAllLines(logFile) |> Array.head
                    use doc = JsonDocument.Parse(line)
                    let jsonlCid = doc.RootElement.GetProperty("correlation_id").GetString()
                    Expect.equal sseCid jsonlCid
                        "SSE error correlation_id must match JSONL correlation_id"

                    // And the same correlation_id must appear in the in-memory Serilog sink.
                    let events = capturedSink.Snapshot()
                    let matched =
                        events
                        |> List.exists (fun e ->
                            match e.Properties.TryGetValue("correlation_id") with
                            | true, v -> v.ToString().Trim('"') = sseCid
                            | _ -> false)
                    Expect.isTrue matched
                        (sprintf "Serilog sink must capture correlation_id=%s" sseCid)
                finally
                    teardown routerApp fakeApp logDir
            } |> Async.AwaitTask |> Async.RunSynchronously)
    ]
```

**Test 5 fake upstream — `startFakeStreamingErrorUpstream`.** Add this helper alongside `startFakeUpstream` near the top of LoggingTests.fs. It must (a) accept POST /v1/chat/completions with stream=true, (b) respond HTTP 200 with `Content-Type: text/event-stream`, (c) write malformed bytes then close — triggering the streaming-error arm in `ChatCompletions.fs`. The exact malformed shape that trips the parsing error depends on `QwenUpstreamClient.fs`'s streaming enumerator. A safe choice that has worked in StreamingTests-style fakes: write `data: {NOT_JSON}\n\n` and close — when the router tries to forward, the JSON parse exception in the enumerator throws into the unexpected-ex arm.

```fsharp
let private startFakeStreamingErrorUpstream () : Task<WebApplication * int> =
    task {
        let b = WebApplication.CreateBuilder()
        b.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        let app = b.Build()
        app.MapGet("/v1/models",
            Func<Task<obj>>(fun () -> task {
                return box {| data = [| {| id = "/local/qwen35b" |} |] |}
            })) |> ignore
        app.MapPost("/v1/chat/completions",
            Func<HttpContext, Task>(fun ctx -> task {
                ctx.Response.StatusCode <- 200
                ctx.Response.ContentType <- "text/event-stream"
                let bytes = Encoding.UTF8.GetBytes("data: {malformed_json\n\n")
                do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length)
                do! ctx.Response.Body.FlushAsync()
                // Close abruptly: returning ends the response.
            })) |> ignore
        do! app.StartAsync()
        let port =
            app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>().Addresses
            |> Seq.head |> fun a -> a.Split(':') |> Array.last |> int
        return app, port
    }
```

If during execution Test 5 finds the streaming arm in the router does NOT enter the error branch (e.g., if the router silently skips malformed frames), adjust the malformed-payload to one that definitively triggers it: e.g., respond HTTP 502 from the fake — the router's non-streaming Error arm DOES emit an SSE-shaped error frame in ONE place; if the streaming arm is unreachable in tests, the Plan 05-02 Step 7 fix covers the non-streaming 502 SSE path too. The asserter is the same: parse the response body, find the `data: {"error":...}` line, assert correlation_id.

**Step 8 — fsproj integration.** Add to `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` `<ItemGroup>` section. **Order matters:** insert AFTER `MLRoutingTests.fs` and BEFORE `RouterTests.fs` (RouterTests.fs is the entry point and references all test modules):

```xml
<Compile Include="LoggingTests.fs" />
```

Final `<Compile>` order:
```xml
<Compile Include="RoutingTests.fs" />
<Compile Include="StreamingTests.fs" />
<Compile Include="QueueTests.fs" />
<Compile Include="LoadTests.fs" />
<Compile Include="MLRoutingTests.fs" />
<Compile Include="LoggingTests.fs" />
<Compile Include="RouterTests.fs" />
```

**Step 9 — RouterTests.rootTests.** Append `SmartRouter.Tests.LoggingTests.tests` to the `rootTests` list:

```fsharp
let rootTests : Test list =
    [
        SmartRouter.Tests.RoutingTests.tests
        SmartRouter.Tests.StreamingTests.tests
        SmartRouter.Tests.QueueTests.tests
        SmartRouter.Tests.LoadTests.tests
        SmartRouter.Tests.MLRoutingTests.tests
        SmartRouter.Tests.LoggingTests.tests
    ]
```

**Step 10 — Update `tests/SmartRouter.Tests/StreamingTests.fs` `startTestRouter` to isolate its log directory.** Plan 05-01 wires `DecisionLogWriter` as a hosted service that starts whenever `WebApplication.CreateBuilder` is built and started. After Plan 05-01, every StreamingTests run creates JSONL files in `tests/SmartRouter.Tests/bin/Debug/net10.0/logs/decisions/YYYY-MM-DD.jsonl` (because that's where `DecisionLog:Directory` defaults to "logs/decisions" relative to test CWD). Add a per-process temp dir override into the existing `AddInMemoryCollection` block (line ~117 in the current file):

```fsharp
let private streamingTestsLogDir =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-streaming-" + Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    dir
```

(declare it once at module scope, just below the `module` line, so the temp dir persists for the whole streaming test run; cleanup is best-effort via OS temp cleanup or an `AppDomain.CurrentDomain.ProcessExit` handler if the team wants strict cleanup — for now, OS-temp is sufficient).

Then inside `startTestRouter`'s `AddInMemoryCollection` list, append:

```fsharp
KeyValuePair("DecisionLog:Directory",       streamingTestsLogDir)
KeyValuePair("DecisionLog:ChannelCapacity", "10000")
```

This change is non-breaking (no test asserts the directory path) and prevents log-file pollution under `tests/SmartRouter.Tests/bin/Debug/`.

**Note on QueueTests.fs:** QueueTests's `/stats` endpoint test (line ~514) builds its own minimal `WebApplication.CreateBuilder` and ONLY registers `IStatsProvider` + `Stats.mapEndpoints` — it does NOT call `CompositionRoot.configureServices` and therefore does NOT start `DecisionLogWriter`. No change needed for QueueTests.fs.

**Step 11 — known gotchas to address proactively:**

- **The `startTestRouter` helper from StreamingTests is private to that module.** Do NOT try to import it; copy the relevant subset into LoggingTests.fs (per existing project convention — see MLRoutingTests's `mkReq` private helper noting "no private-visibility dependency on another test module").
- **Test 3 (correlation propagation) — synchronous sink, no Task.Delay needed.** The custom `CapturingSink` in Step 1 implements `ILogEventSink.Emit` synchronously under a lock. By the time `client.SendAsync` returns the response, every Serilog event for the request has been emitted. Do NOT add a `Task.Delay(100)`; it's moot.
- **Mutating `Log.Logger` is process-global.** Because `testSequenced` wraps the entire testList, no two LoggingTests run concurrently — the shared `capturedSink` is safe. Each test calls `capturedSink.Clear()` at the start.
- **The fake upstream doesn't implement /v1/models probe properly:** QwenUpstreamClient lazy-probes `/v1/models` to discover the upstream model id. The fake's `/v1/models` returns `data: [{ id: "/local/qwen35b" }]` so the probe succeeds. Without this, the first request from the router throws `ModelUnavailable` and tests fail. (The existing StreamingTests has its own fake; the shape it expects is documented in the QwenUpstreamClient lazy probe — verify with `head -200 src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` if needed.)
- **Per-test isolation:** each test creates a fresh `mkTempLogDir()` so concurrent test runs don't corrupt each other's files. `cleanupLogDir` runs in `finally`.
- **Time-of-day rollover during a test:** if a test starts at 23:59:59 UTC, the first entry might land in day-N's file and later entries in day-N+1's. The `todaysLogFile` helper uses `DateTime.UtcNow.ToString("yyyy-MM-dd")` AT READ TIME, which may mismatch. Mitigation: compute `todaysLogFile` ONCE at test start and use it for the read; if it's empty, also check the next-day file (Test 4 graceful shutdown is most exposed). Acceptable simplification: don't run tests across midnight UTC. Document this as a known edge in the SUMMARY.
  </action>
  <verify>
- `dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off` succeeds with 0 warnings.
- `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off --filter LoggingTests` runs exactly 5 tests and ALL 5 pass.
- `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off` (no filter) shows 49 passed (44 prior + 5 new). The 2 LoadTests pending tests remain ignored.
- `grep -n "LoggingTests" tests/SmartRouter.Tests/SmartRouter.Tests.fsproj tests/SmartRouter.Tests/RouterTests.fs` returns 2 hits — fsproj <Compile> entry + rootTests entry.
- `grep -c "testCase" tests/SmartRouter.Tests/LoggingTests.fs` returns 5.
- `grep -c "testSequenced" tests/SmartRouter.Tests/LoggingTests.fs` returns 1 (single wrap around testList).
- `grep -n "ILogEventSink\|CapturingSink" tests/SmartRouter.Tests/LoggingTests.fs` returns ≥3 hits (sink type def + interface impl + module-level instance) — proves Test 3 uses the in-memory sink approach (Blocker 2 fix).
- `grep -n "Console\.SetError" tests/SmartRouter.Tests/LoggingTests.fs` returns NOTHING — the broken stderr-capture approach must be gone.
- `grep -n "DecisionLog:Directory" tests/SmartRouter.Tests/StreamingTests.fs` returns ≥1 hit (StreamingTests log isolation, Warning 5 fix).
  </verify>
  <done>
LoggingTests.fs has 5 testSequenced tests covering schema completeness, 100-concurrent thread safety, correlation propagation through Serilog (in-memory sink) + JSONL, graceful shutdown, and SSE error correlation_id propagation. fsproj has the new compile entry in correct order. RouterTests.rootTests includes LoggingTests.tests. StreamingTests.fs's startTestRouter writes JSONL to a temp dir (no log pollution). `dotnet test` reports 49 passed (44 + 5); 0 build warnings.
  </done>
</task>

</tasks>

<verification>
**Plan-level verification:**

1. **Build is clean:**
   ```bash
   dotnet build SmartRouter.slnx -nologo --tl:off
   ```
   Expected: 0 errors, 0 warnings.

2. **All tests pass — 49 total:**
   ```bash
   dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off
   ```
   Expected: 49 passed (RoutingTests 22 + StreamingTests 8 + QueueTests 9 + MLRoutingTests 5 + LoggingTests 5); 2 LoadTests still ignored.

3. **All 5 LoggingTests pass individually:**
   ```bash
   dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off --filter LoggingTests
   ```
   Expected: 5 passed.

4. **Phase 5 success criteria — direct mapping:**
   - SC #1 (12-field schema): LoggingTests Test 1
   - SC #2 (100-concurrent valid lines): LoggingTests Test 2
   - SC #3 (correlation_id in stderr-equivalent AND JSONL): LoggingTests Test 3 (in-memory Serilog sink stands in for stderr — same Serilog event pipeline, captured directly)
   - SC #4 (graceful shutdown flush): LoggingTests Test 4
   - LOG-04 SSE-error body clause: LoggingTests Test 5

5. **No File.AppendAllText regression:**
   ```bash
   grep -RIn "File\.AppendAllText" src/ tests/
   ```
   Expected: NO output. (Inherited from Plan 05-01; confirms regression-free.)

6. **Test isolation — temp dirs cleaned up:**
   After running tests, no `smart-router-tests-*` directories remain in `$TMPDIR`:
   ```bash
   ls $TMPDIR/smart-router-tests-* 2>&1 | wc -l
   ```
   Expected: 0 (or matches "No such file or directory").
</verification>

<success_criteria>
- LoggingTests.fs exists with exactly 5 testCase entries inside a single testSequenced testList (Task 1)
- LoggingTests.fs uses a custom in-memory `ILogEventSink` (CapturingSink) for correlation capture; no Console.SetError anywhere
- SmartRouter.Tests.fsproj has the new <Compile> entry in correct position (Task 1)
- RouterTests.rootTests includes the new tests (Task 1)
- StreamingTests.startTestRouter overrides DecisionLog:Directory to a per-process temp dir (Task 1 Step 10)
- All 49 tests pass: 44 prior + 5 new (Task 1)
- 0 build warnings (Task 1)
- Each LoggingTest uses an isolated temp directory and cleans up in finally
- Phase 5 success criteria #1-4 all proven by Tests 1-4; LOG-04 SSE-error body clause proven by Test 5
</success_criteria>

<output>
After completion, create `.planning/phases/05-routing-decision-logging/05-03-SUMMARY.md` listing:
- Files modified (4: LoggingTests.fs, SmartRouter.Tests.fsproj, RouterTests.fs, StreamingTests.fs)
- Test count delta (44 → 49)
- Phase 5 SC mapping (Test 1→SC1, Test 2→SC2, Test 3→SC3, Test 4→SC4, Test 5→LOG-04 SSE clause)
- Note that Test 3 uses an in-memory ILogEventSink (CapturingSink) rather than Console.SetError — explain why (Serilog console sink captures Console.Error reference at sink creation time + production outputTemplate doesn't render {Properties})
- Note that StreamingTests.fs's startTestRouter now writes JSONL to a temp dir
- Confirmation that File.AppendAllText is still not present anywhere
</output>

## Existing-test impact

| Test file | Tests | Needs change in 05-03? | Why / Why not |
|-----------|-------|------------------------|---------------|
| RoutingTests.fs | 22 | NO | Pure routing tests; no DI; not affected by logging infra. |
| MLRoutingTests.fs | 5 | NO | Tests 4+5 still resolve `RoutingAlgorithm` (kept by Plan 05-02 alias). No code change needed. |
| StreamingTests.fs | 8 | YES (1-block edit, isolation only) | Add `DecisionLog:Directory` override + module-level temp dir constant. Functional tests unchanged — isolation prevents log pollution under bin/Debug. |
| QueueTests.fs | 9 | NO | QueueDispatcher tests don't use `configureServices`; the /stats test (line 514) builds a minimal WebApp registering only IStatsProvider + Stats.mapEndpoints — DecisionLogWriter is never resolved or started. No isolation needed. |
| LoadTests.fs | 2 (pending) | NO | Pending by default; same story as QueueTests. |
| LoggingTests.fs | 5 | NEW | Created by this plan. |
| RouterTests.fs | (rootTests) | YES (1-line edit) | Append `SmartRouter.Tests.LoggingTests.tests` to the rootTests list. |

## REQ-ID coverage in this plan

- **LOG-01** (12-field schema): Test 1 parses a JSONL line and asserts every field individually.
- **LOG-02** (thread-safe single-writer; 100 concurrent → 100 valid lines): Test 2 fires 100 parallel HttpClient.PostAsync, asserts file has exactly 100 lines that each parse as JSON, asserts 100 distinct correlation_ids.
- **LOG-03** (graceful shutdown drain — no in-flight log loss): Test 4 fires 10 requests, immediately calls StopAsync, asserts all 10 entries are in the file. Daily rotation is exercised implicitly by the file path computation but not directly tested across midnight (acceptable Phase 5 scope per CONTEXT.md "out of scope" — operator concern documented in Phase 11 README).
- **LOG-04** (correlation propagation through Serilog stderr AND JSONL AND SSE error events): all three sources covered. Test 3 installs an in-memory `ILogEventSink` (CapturingSink) that captures the same `LogEvent`s Serilog would write to stderr in production — and asserts the JSONL `correlation_id` matches a `LogEvent.Properties["correlation_id"]`. Test 5 sends a streaming request to a fake upstream that yields a malformed SSE chunk, parses the `data: {"error":...}` frame from the response body, and asserts the `correlation_id` field matches both the JSONL and Serilog captures. **All three sources verified by tests in this phase, per REQUIREMENTS.md LOG-04.**
- **OBS-01** (per-request structured log includes selected model, routing reason, latency, etc.): Test 1 asserts every relevant field is present and typed.
- **OBS-03** (correlation id propagated through logs and SSE-error events): Test 3 covers logs; Test 5 covers the SSE-error event body via end-to-end assertion.
