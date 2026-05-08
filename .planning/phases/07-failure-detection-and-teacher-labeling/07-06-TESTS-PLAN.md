---
phase: 07-failure-detection-and-teacher-labeling
plan: 06
type: execute
wave: 3
depends_on: ["07-02", "07-03", "07-04"]
files_modified:
  - tests/SmartRouter.Tests/FailureDetectorTests.fs
  - tests/SmartRouter.Tests/TeacherLabelerTests.fs
  - tests/SmartRouter.Tests/HardCaseDatasetTests.fs
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
  - tests/SmartRouter.Tests/RouterTests.fs
autonomous: true

must_haves:
  truths:
    - "FailureDetectorTests.fs has 6 tests: empty-directory returns []; non-existent-directory returns []; all-fallback-false returns []; one fallback-true entry produces one HardCase; malformed lines skipped (2 valid + 1 garbage + 1 blank → 2 HardCases); multiple JSONL files combined"
    - "TeacherLabelerTests.fs has 6 tests using fake-Kestrel teacher endpoint pattern (mirrors StreamingTests.startTestRouter): ROUTE_35B response → Labeled Route35B; ROUTE_122B response → Labeled Route122B; chatty response with no sentinel → Unparseable; malformed JSON in HTTP body → Unparseable; missing prompt template → Skipped; daily cost cap pre-set to max → Skipped without HTTP call"
    - "HardCaseDatasetTests.fs has 4 tests: AppendAsync writes one valid JSONL line with all fields; same (correlation_id, prompt_hash) pair not appended twice (dedupe); 50 concurrent AppendAsync calls produce 50 valid non-interleaved lines; graceful StopAsync drains in-flight entries"
    - "All three test modules wrapped in testSequenced (Console.SetOut races + temp dir/file isolation)"
    - "Each test uses a fresh temp directory + temp file path to avoid cross-test pollution; cleanup happens in a try/finally block"
    - "Tests.fsproj <Compile> list includes the 3 new files BEFORE RouterTests.fs"
    - "RouterTests.rootTests includes the 3 new test module references"
    - "Total test count after this plan: 50 + 16 = 66 passing + 10 ignored (6 FailureDetectorTests + 6 TeacherLabelerTests + 4 HardCaseDatasetTests; zero new ignored — none of these tests depend on ML models)"
  artifacts:
    - path: "tests/SmartRouter.Tests/FailureDetectorTests.fs"
      provides: "6 testSequenced tests covering FailureDetector JSONL parse + filter + multi-file behavior"
      contains: "FailureDetector"
    - path: "tests/SmartRouter.Tests/TeacherLabelerTests.fs"
      provides: "6 testSequenced tests with fake Kestrel teacher endpoint covering parse, retry, missing template, cost cap"
      contains: "TeacherLabeler"
    - path: "tests/SmartRouter.Tests/HardCaseDatasetTests.fs"
      provides: "4 testSequenced tests covering single write, dedupe, concurrent integrity, graceful drain"
      contains: "HardCaseDatasetWriter"
    - path: "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
      provides: "3 new <Compile> entries in correct order (BEFORE RouterTests.fs)"
      contains: "FailureDetectorTests.fs"
    - path: "tests/SmartRouter.Tests/RouterTests.fs"
      provides: "3 new entries in rootTests list (PITFALL-26 — Expecto auto-discovery is forbidden)"
      contains: "FailureDetectorTests.tests"
  key_links:
    - from: "TeacherLabelerTests fake Kestrel"
      to: "TeacherLabeler real implementation"
      via: "IHttpClientFactory + named 'teacher' client pointed at the fake endpoint"
      pattern: "127\\.0\\.0\\.1:0|UseUrls"
    - from: "HardCaseDatasetTests"
      to: "BackgroundService.StartAsync / StopAsync"
      via: "explicit lifecycle calls (no IHost)"
      pattern: "StartAsync|StopAsync"
    - from: "RouterTests.rootTests"
      to: "3 new testList values"
      via: "explicit list (PITFALL-26: Expecto auto-discovery forbidden)"
      pattern: "FailureDetectorTests\\.tests|TeacherLabelerTests\\.tests|HardCaseDatasetTests\\.tests"
---

<objective>
Ship the test suite for Phase 7 — three new test files (FailureDetectorTests.fs, TeacherLabelerTests.fs, HardCaseDatasetTests.fs) covering all four FAIL requirements, plus the .fsproj wiring and the rootTests update. All 16 new tests (6 + 6 + 4) must pass; existing 50 tests + 10 ignored must remain green. **Wave note:** This plan is Wave 3 (parallel with 07-05) — the test files instantiate adapters directly without DI/CompositionRoot/CLI, so they only need the Wave 2 outputs (07-02 / 07-03 / 07-04).

Purpose: prove the Wave 2 adapter implementations (FailureDetector, TeacherLabeler, HardCaseDatasetWriter) behave correctly against the contracts defined in CONTEXT/RESEARCH; lock down regressions for Phase 8's downstream consumer.

Output:
- 3 new test files (16 tests total: 6 + 6 + 4)
- Updated Tests.fsproj (3 <Compile> entries before RouterTests.fs)
- Updated RouterTests.rootTests (3 new module references)
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/07-failure-detection-and-teacher-labeling/07-CONTEXT.md
@.planning/phases/07-failure-detection-and-teacher-labeling/07-RESEARCH.md
@src/SmartRouter.Core/RetrainingPorts.fs
@src/SmartRouter.Cli/Adapters/FailureDetector.fs
@src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
@src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
@tests/SmartRouter.Tests/StreamingTests.fs
@tests/SmartRouter.Tests/LoggingTests.fs
@tests/SmartRouter.Tests/RouterTests.fs
</context>

<tasks>

<task type="auto">
  <name>Task 1: FailureDetectorTests.fs (5 tests, fixture-based, no HTTP)</name>
  <files>
    tests/SmartRouter.Tests/FailureDetectorTests.fs
  </files>
  <action>
Create `tests/SmartRouter.Tests/FailureDetectorTests.fs` (module `SmartRouter.Tests.FailureDetectorTests`).

The tests are fixture-based: write known-good JSONL content to a temp file, instantiate `FailureDetector(tempDir)`, call `ExtractHardCases`, assert returned list shape.

```fsharp
module SmartRouter.Tests.FailureDetectorTests

open System
open System.IO
open System.Threading
open Expecto
open SmartRouter.Cli.Adapters.FailureDetector
open SmartRouter.Core.RetrainingPorts

let private mkTempDir () : string =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-tests-failure-" + Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    dir

let private cleanupDir (dir: string) =
    try if Directory.Exists(dir) then Directory.Delete(dir, recursive = true)
    with _ -> ()

/// Build one DecisionLog JSONL line with given fallback_used + correlation_id.
/// Other fields filled with stable test values matching the snake_case wire shape.
let private makeJsonLine (correlationId: string) (fallbackUsed: bool) : string =
    let fallback = if fallbackUsed then "true" else "false"
    sprintf
        "{\"schema_version\":1,\"correlation_id\":\"%s\",\"prompt_hash\":\"abc123\",\"prompt_korean_char_ratio\":0.0,\"routing_algorithm\":\"ml\",\"routing_reason\":\"ml\",\"target\":\"Qwen35B\",\"latency_ms\":42.0,\"fallback_used\":%s,\"model_version\":\"ml-test\",\"task_type\":null,\"timestamp\":\"2026-05-08T00:00:00+00:00\"}"
        correlationId fallback

let private extractHardCases (dir: string) : HardCase list =
    let detector = FailureDetector(dir) :> IFailureDetector
    detector.ExtractHardCases(CancellationToken.None).GetAwaiter().GetResult()

let tests =
    testSequenced (
        testList "FailureDetector" [

            testCase "empty directory returns empty list" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let result = extractHardCases dir
                    Expect.isEmpty result "empty dir → 0 hard cases"
                finally cleanupDir dir

            testCase "non-existent directory returns empty list" <| fun _ ->
                let dir = Path.Combine(Path.GetTempPath(), "smart-router-tests-nonexistent-" + Path.GetRandomFileName())
                let result = extractHardCases dir
                Expect.isEmpty result "missing dir → 0 hard cases"

            testCase "all entries with fallback_used=false returns empty list" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let path = Path.Combine(dir, "2026-05-08.jsonl")
                    let lines = [
                        makeJsonLine "cid-1" false
                        makeJsonLine "cid-2" false
                        makeJsonLine "cid-3" false
                    ]
                    File.WriteAllLines(path, lines)
                    let result = extractHardCases dir
                    Expect.isEmpty result "all-false → 0 hard cases"
                finally cleanupDir dir

            testCase "single fallback_used=true entry returns one HardCase" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let path = Path.Combine(dir, "2026-05-08.jsonl")
                    let lines = [
                        makeJsonLine "cid-1" false
                        makeJsonLine "cid-2" true
                        makeJsonLine "cid-3" false
                    ]
                    File.WriteAllLines(path, lines)
                    let result = extractHardCases dir
                    Expect.equal (List.length result) 1 "exactly one match"
                    Expect.equal result.Head.CorrelationId "cid-2" "correct correlation_id surfaced"
                    Expect.equal result.Head.PromptText None "prompt text always None for log-extracted hard cases (LOG-01 stores hash only)"
                finally cleanupDir dir

            testCase "malformed JSON lines skipped without crashing" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let path = Path.Combine(dir, "2026-05-08.jsonl")
                    let lines = [
                        makeJsonLine "cid-1" true
                        "this is not json at all { ] }"
                        ""                     // blank line — explicitly OK
                        makeJsonLine "cid-2" true
                    ]
                    File.WriteAllLines(path, lines)
                    let result = extractHardCases dir
                    Expect.equal (List.length result) 2 "two valid matches; malformed + blank skipped"
                finally cleanupDir dir

            testCase "multiple JSONL files in directory all read" <| fun _ ->
                let dir = mkTempDir ()
                try
                    File.WriteAllLines(
                        Path.Combine(dir, "2026-05-06.jsonl"),
                        [ makeJsonLine "day-1-cid-1" true
                          makeJsonLine "day-1-cid-2" false ])
                    File.WriteAllLines(
                        Path.Combine(dir, "2026-05-07.jsonl"),
                        [ makeJsonLine "day-2-cid-1" true
                          makeJsonLine "day-2-cid-2" true ])
                    let result = extractHardCases dir
                    Expect.equal (List.length result) 3 "3 fallback_used=true across 2 files"
                finally cleanupDir dir
        ])
```

**Notes on fixture content:**
- The hand-built JSONL string uses snake_case keys exactly matching `DecisionLog` field names — the `JsonNamingPolicy.SnakeCaseLower` policy in FailureDetector's serializer expects this.
- `task_type` is `null` (matches the `string option` → `null` round-trip via JsonFSharpConverter).
- `timestamp` is a valid ISO-8601 string with timezone offset (`DateTimeOffset` requires offset).
- `prompt_korean_char_ratio` uses `0.0` (no Korean content in test fixtures).

**Test count: 6** — matches the must_haves declaration. The 6 cases are: empty dir, non-existent dir, all-fallback-false, single fallback-true, malformed-line skipped, multi-file aggregation.
  </action>
  <verify>
- `dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off` (after Task 4 wires the .fsproj) succeeds with 0 warnings.
- `grep -n "FailureDetector\b" tests/SmartRouter.Tests/FailureDetectorTests.fs` returns at least 2 hits (open + instantiation).
- `grep -nE "(testCase|testList|testSequenced)" tests/SmartRouter.Tests/FailureDetectorTests.fs` returns at least 8 hits (testSequenced + testList + 6 testCase).
  </verify>
  <done>
FailureDetectorTests.fs ships 6 testSequenced tests covering: empty dir, missing dir, all-false, one fallback-true, malformed line skipped, multi-file aggregation. Each test creates and cleans up its own temp dir. No HTTP, no DI container.
  </done>
</task>

<task type="auto">
  <name>Task 2: TeacherLabelerTests.fs (6 tests with fake-Kestrel teacher endpoint)</name>
  <files>
    tests/SmartRouter.Tests/TeacherLabelerTests.fs
  </files>
  <action>
Create `tests/SmartRouter.Tests/TeacherLabelerTests.fs` (module `SmartRouter.Tests.TeacherLabelerTests`). The tests use a fake Kestrel server bound to `127.0.0.1:0` returning canned chat-completion responses — direct mirror of the StreamingTests / LoggingTests fake-upstream pattern.

```fsharp
module SmartRouter.Tests.TeacherLabelerTests

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Http
open SmartRouter.Cli.Adapters.TeacherLabeler
open SmartRouter.Core.RetrainingPorts

// ── Test infra ────────────────────────────────────────────────────────────────

let private mkTempDir () : string =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-tests-teacher-" + Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    dir

let private cleanupDir (dir: string) =
    try if Directory.Exists(dir) then Directory.Delete(dir, recursive = true)
    with _ -> ()

/// Spin up a fake teacher endpoint that returns the given canned response body.
/// Returns (app, port). Caller calls app.StopAsync/.DisposeAsync when done.
let private startFakeTeacher (responseBody: string) (statusCode: int) : Task<WebApplication * int> =
    task {
        let b = WebApplication.CreateBuilder()
        b.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        let app = b.Build()
        app.MapPost("/v1/chat/completions",
            Func<HttpContext, Task>(fun ctx ->
                task {
                    ctx.Response.StatusCode  <- statusCode
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync(responseBody)
                }))
        |> ignore
        do! app.StartAsync()
        let port =
            app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()
                .Addresses
            |> Seq.head
            |> fun a -> a.Split(':') |> Array.last |> int
        return app, port
    }

/// Build a TeacherLabeler instance pointed at the given fake-teacher base URL.
/// IHttpClientFactory is constructed via a minimal ServiceCollection.
let private mkLabeler (baseUrl: string) (promptPath: string) (datasetsDir: string) (cap: int) : ITeacherLabeler =
    let services = ServiceCollection()
    services.AddHttpClient("teacher", fun c ->
        c.BaseAddress <- Uri(baseUrl)
        c.Timeout     <- TimeSpan.FromSeconds(5.0))
        |> ignore
    let sp = services.BuildServiceProvider()
    let factory = sp.GetRequiredService<IHttpClientFactory>()
    let opts = {
        Endpoint        = baseUrl
        PromptPath      = promptPath
        DailyCallCap    = cap
        TimeoutSeconds  = 5
        DatasetsDir     = datasetsDir }
    TeacherLabeler(factory, opts) :> ITeacherLabeler

/// Write a minimal teacher prompt file at the given path.
let private writePromptFile (path: string) =
    Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
    File.WriteAllText(path, "Test teacher prompt.\n\nRespond ONLY ROUTE_35B or ROUTE_122B.\n\nPrompt:\n{{PROMPT}}")

/// Canned successful chat-completions response for a given content string.
let private cannedResponse (content: string) : string =
    sprintf "{\"id\":\"x\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"%s\"}}]}" content

// ── Tests ─────────────────────────────────────────────────────────────────────

let tests =
    testSequenced (
        testList "TeacherLabeler" [

            testCase "ROUTE_35B response → Labeled Route35B" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let promptPath = Path.Combine(dir, "prompt.md")
                    writePromptFile promptPath
                    let serverTask = startFakeTeacher (cannedResponse "ROUTE_35B") 200
                    let app, port = serverTask.GetAwaiter().GetResult()
                    try
                        let baseUrl = sprintf "http://127.0.0.1:%d" port
                        let labeler = mkLabeler baseUrl promptPath dir 1000
                        let result = labeler.LabelAsync("test prompt", "cid-1", CancellationToken.None).GetAwaiter().GetResult()
                        match result with
                        | Labeled (Route35B, _excerpt) -> ()
                        | other -> failtestf "expected Labeled Route35B; got %A" other
                    finally
                        app.StopAsync().GetAwaiter().GetResult()
                        (app :> IDisposable).Dispose()
                finally cleanupDir dir

            testCase "ROUTE_122B response → Labeled Route122B" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let promptPath = Path.Combine(dir, "prompt.md")
                    writePromptFile promptPath
                    let serverTask = startFakeTeacher (cannedResponse "ROUTE_122B") 200
                    let app, port = serverTask.GetAwaiter().GetResult()
                    try
                        let baseUrl = sprintf "http://127.0.0.1:%d" port
                        let labeler = mkLabeler baseUrl promptPath dir 1000
                        let result = labeler.LabelAsync("complex debug case", "cid-1", CancellationToken.None).GetAwaiter().GetResult()
                        match result with
                        | Labeled (Route122B, _) -> ()
                        | other -> failtestf "expected Labeled Route122B; got %A" other
                    finally
                        app.StopAsync().GetAwaiter().GetResult()
                        (app :> IDisposable).Dispose()
                finally cleanupDir dir

            testCase "chatty response with no sentinel → Unparseable" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let promptPath = Path.Combine(dir, "prompt.md")
                    writePromptFile promptPath
                    let chatty = "I think we should consider all the options carefully here."
                    let serverTask = startFakeTeacher (cannedResponse chatty) 200
                    let app, port = serverTask.GetAwaiter().GetResult()
                    try
                        let baseUrl = sprintf "http://127.0.0.1:%d" port
                        let labeler = mkLabeler baseUrl promptPath dir 1000
                        let result = labeler.LabelAsync("ambiguous", "cid-1", CancellationToken.None).GetAwaiter().GetResult()
                        match result with
                        | Unparseable _ -> ()
                        | other -> failtestf "expected Unparseable; got %A" other
                    finally
                        app.StopAsync().GetAwaiter().GetResult()
                        (app :> IDisposable).Dispose()
                finally cleanupDir dir

            testCase "malformed HTTP body → Unparseable" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let promptPath = Path.Combine(dir, "prompt.md")
                    writePromptFile promptPath
                    // Body is not valid OpenAI shape (no choices array)
                    let serverTask = startFakeTeacher """{"this": "is not OpenAI"}""" 200
                    let app, port = serverTask.GetAwaiter().GetResult()
                    try
                        let baseUrl = sprintf "http://127.0.0.1:%d" port
                        let labeler = mkLabeler baseUrl promptPath dir 1000
                        let result = labeler.LabelAsync("anything", "cid-1", CancellationToken.None).GetAwaiter().GetResult()
                        match result with
                        | Unparseable _ -> ()
                        | other -> failtestf "expected Unparseable; got %A" other
                    finally
                        app.StopAsync().GetAwaiter().GetResult()
                        (app :> IDisposable).Dispose()
                finally cleanupDir dir

            testCase "missing prompt template → Skipped" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let promptPath = Path.Combine(dir, "no-such-file.md")  // intentionally missing
                    let serverTask = startFakeTeacher (cannedResponse "ROUTE_35B") 200
                    let app, port = serverTask.GetAwaiter().GetResult()
                    try
                        let baseUrl = sprintf "http://127.0.0.1:%d" port
                        let labeler = mkLabeler baseUrl promptPath dir 1000
                        let result = labeler.LabelAsync("test", "cid-1", CancellationToken.None).GetAwaiter().GetResult()
                        match result with
                        | Skipped reason ->
                            Expect.stringContains reason "prompt template missing" "reason mentions missing template"
                        | other -> failtestf "expected Skipped; got %A" other
                    finally
                        app.StopAsync().GetAwaiter().GetResult()
                        (app :> IDisposable).Dispose()
                finally cleanupDir dir

            testCase "daily cost cap pre-set to max → Skipped without HTTP call" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let promptPath = Path.Combine(dir, "prompt.md")
                    writePromptFile promptPath
                    // Pre-write a cap counter at the configured max
                    let todayUtc = DateTime.UtcNow.ToString("yyyy-MM-dd")
                    let capPath = Path.Combine(dir, sprintf "teacher-cap-%s.json" todayUtc)
                    File.WriteAllText(capPath, sprintf "{\"date\":\"%s\",\"count\":3,\"max\":3}" todayUtc)
                    // Use a server that would FAIL if hit (no route registered for /v1/chat/completions)
                    // — proving the labeler returned Skipped without making the HTTP call.
                    let baseUrl = "http://127.0.0.1:1"  // unreachable address; if the labeler tries to call, we'd see Failed instead of Skipped
                    let labeler = mkLabeler baseUrl promptPath dir 3   // cap == count == 3, so cap-hit
                    let result = labeler.LabelAsync("test", "cid-1", CancellationToken.None).GetAwaiter().GetResult()
                    match result with
                    | Skipped reason ->
                        Expect.stringContains reason "cap" "reason mentions cap hit"
                    | other -> failtestf "expected Skipped (cap hit); got %A" other
                finally cleanupDir dir
        ])
```

**Notes:**
- Fake-teacher pattern follows `LoggingTests.startFakeUpstream` / `StreamingTests.startTestRouter` exactly (port 0 + IServerAddressesFeature lookup).
- The labeler is built with a minimal `ServiceCollection` containing only `AddHttpClient("teacher", ...)`. No CompositionRoot — this isolates the test from the full DI tree.
- The cost-cap test uses an unreachable address (`127.0.0.1:1`) as a tripwire: if the labeler accidentally makes the HTTP call, the result would be `Failed` not `Skipped`. The test asserts `Skipped` — confirming the cap pre-check fired BEFORE any HTTP traffic.
- Each test creates its own temp dir + isolated cap counter file → no cross-test pollution.
- `failtestf` is Expecto's structured-failure helper.

**Test count: 6.**

**Note on retry test omission:** The original RESEARCH.md sketched a "retries on 503 and succeeds on third attempt" test. That test requires a Polly-aware fake server (counts attempts, fails N times then succeeds). With `AddStandardResilienceHandler` configured in production CompositionRoot but a plain `AddHttpClient` in this test (no resilience handler), the retry policy isn't actually exercised — the test would prove HTTP failure handling, not retry behavior. Skipping the retry test is intentional: the resilience handler is library code (Polly-tested upstream), and a fake-server retry test in our codebase would mostly exercise our test infra, not our adapter logic. If a future operator asks for retry test coverage, the cleanest path is an integration test that uses the full CompositionRoot DI (Plan 07-05's named-client registration) and counts retries via a Kestrel-side counter — Phase 8 is the natural home for that.
  </action>
  <verify>
- `dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off` (after Task 4 wires .fsproj) succeeds with 0 warnings.
- `grep -nE "(testCase|testList|testSequenced)" tests/SmartRouter.Tests/TeacherLabelerTests.fs` returns at least 8 hits.
- `grep -n "ROUTE_35B\|ROUTE_122B" tests/SmartRouter.Tests/TeacherLabelerTests.fs` returns at least 2 hits.
- `grep -n "127\\.0\\.0\\.1:0" tests/SmartRouter.Tests/TeacherLabelerTests.fs` returns 1 hit (fake-Kestrel base URL).
  </verify>
  <done>
TeacherLabelerTests.fs ships 6 testSequenced tests using fake-Kestrel teacher endpoint: ROUTE_35B parse, ROUTE_122B parse, chatty Unparseable, malformed-body Unparseable, missing-prompt-template Skipped, cost-cap Skipped without HTTP call. Each test isolates temp directories and HTTP clients.
  </done>
</task>

<task type="auto">
  <name>Task 3: HardCaseDatasetTests.fs (4 tests covering Channel + dedupe + concurrency + drain)</name>
  <files>
    tests/SmartRouter.Tests/HardCaseDatasetTests.fs
  </files>
  <action>
Create `tests/SmartRouter.Tests/HardCaseDatasetTests.fs` (module `SmartRouter.Tests.HardCaseDatasetTests`).

```fsharp
module SmartRouter.Tests.HardCaseDatasetTests

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open SmartRouter.Cli.Adapters.HardCaseDatasetWriter
open SmartRouter.Core.RetrainingPorts

let private mkTempDir () : string =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-tests-dataset-" + Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    dir

let private cleanupDir (dir: string) =
    try if Directory.Exists(dir) then Directory.Delete(dir, recursive = true)
    with _ -> ()

let private mkEntry (cid: string) (hash: string) (label: int) : HardCaseEntry =
    { SchemaVersion          = 1
      CorrelationId          = cid
      PromptHash             = hash
      PromptText             = "test prompt"
      Label                  = label
      Source                 = "test"
      TeacherResponseExcerpt = Some "ROUTE_35B"
      LabeledAt              = DateTimeOffset.UtcNow
      PromptKoreanCharRatio  = 0.0
      RoutingAlgorithm       = "ml"
      Target                 = if label = 0 then "Qwen35B" else "Qwen122B" }

/// Build, start, drive, and stop a HardCaseDatasetWriter against a temp file path.
/// Returns the (closed) lines from the file after StopAsync drains.
let private runWith (path: string) (capacity: int) (act: IHardCaseDatasetWriter -> Task<unit>) : string list =
    let opts = { Path = path; ChannelCapacity = capacity }
    let writer = new HardCaseDatasetWriter(opts)
    writer.StartAsync(CancellationToken.None).GetAwaiter().GetResult()
    try
        (act (writer :> IHardCaseDatasetWriter)).GetAwaiter().GetResult()
    finally
        writer.StopAsync(CancellationToken.None).GetAwaiter().GetResult()
        (writer :> IDisposable).Dispose()
    if File.Exists(path) then
        File.ReadAllLines(path)
        |> Array.toList
        |> List.filter (fun s -> not (String.IsNullOrWhiteSpace(s)))
    else
        []

let tests =
    testSequenced (
        testList "HardCaseDatasetWriter" [

            testCase "AppendAsync writes one valid JSONL line" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let path = Path.Combine(dir, "ds.jsonl")
                    let entry = mkEntry "cid-1" "hash-1" 0
                    let lines = runWith path 100 (fun w ->
                        task {
                            do! w.AppendAsync(entry, CancellationToken.None)
                        })
                    Expect.equal (List.length lines) 1 "exactly one line written"
                    // Validate the written line parses as JSON and has expected fields.
                    use doc = JsonDocument.Parse(lines.Head)
                    Expect.equal (doc.RootElement.GetProperty("correlation_id").GetString()) "cid-1" "correlation_id roundtrip"
                    Expect.equal (doc.RootElement.GetProperty("prompt_hash").GetString())    "hash-1" "prompt_hash roundtrip"
                    Expect.equal (doc.RootElement.GetProperty("schema_version").GetInt32())  1        "schema_version=1"
                    Expect.equal (doc.RootElement.GetProperty("label").GetInt32())            0        "label=0 (Route35B)"
                    Expect.equal (doc.RootElement.GetProperty("source").GetString())          "test"   "source field present"
                finally cleanupDir dir

            testCase "duplicate (correlation_id, prompt_hash) not appended twice" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let path = Path.Combine(dir, "ds.jsonl")
                    let entry1 = mkEntry "cid-1" "hash-1" 0
                    let entry2 = mkEntry "cid-1" "hash-1" 1   // same dedupe key, different label — still dropped
                    let lines = runWith path 100 (fun w ->
                        task {
                            do! w.AppendAsync(entry1, CancellationToken.None)
                            do! w.AppendAsync(entry2, CancellationToken.None)
                        })
                    Expect.equal (List.length lines) 1 "dedupe drops the second write"
                finally cleanupDir dir

            testCase "50 concurrent AppendAsync calls produce 50 valid non-interleaved JSONL lines" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let path = Path.Combine(dir, "ds.jsonl")
                    let lines =
                        runWith path 100 (fun w ->
                            task {
                                let appends =
                                    [| for i in 1 .. 50 ->
                                        let e = mkEntry (sprintf "cid-%d" i) (sprintf "hash-%d" i) (i % 2)
                                        (w.AppendAsync(e, CancellationToken.None) :> Task) |]
                                do! Task.WhenAll(appends)
                            })
                    Expect.equal (List.length lines) 50 "all 50 entries persisted"
                    // Each line must parse as JSON — proves no interleaving.
                    let allParse =
                        lines
                        |> List.forall (fun ln ->
                            try
                                use _doc = JsonDocument.Parse(ln)
                                true
                            with _ -> false)
                    Expect.isTrue allParse "every line parses as JSON (no interleaving)"
                finally cleanupDir dir

            testCase "graceful StopAsync drains in-flight entries" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let path = Path.Combine(dir, "ds.jsonl")
                    let opts = { Path = path; ChannelCapacity = 100 }
                    let writer = new HardCaseDatasetWriter(opts)
                    writer.StartAsync(CancellationToken.None).GetAwaiter().GetResult()
                    try
                        // Burst 10 entries
                        let iface = writer :> IHardCaseDatasetWriter
                        for i in 1 .. 10 do
                            let e = mkEntry (sprintf "cid-%d" i) (sprintf "hash-%d" i) 0
                            iface.AppendAsync(e, CancellationToken.None).GetAwaiter().GetResult()
                    finally
                        // StopAsync MUST drain the channel
                        writer.StopAsync(CancellationToken.None).GetAwaiter().GetResult()
                        (writer :> IDisposable).Dispose()
                    let lines =
                        if File.Exists(path) then
                            File.ReadAllLines(path) |> Array.filter (String.IsNullOrWhiteSpace >> not) |> Array.toList
                        else []
                    Expect.equal (List.length lines) 10 "all 10 entries flushed on graceful shutdown"
                finally cleanupDir dir
        ])
```

**Notes:**
- The helper `runWith` builds a writer on a per-test temp file, calls `StartAsync` to launch the BackgroundService consumer loop, awaits the producer-side `AppendAsync` calls, then calls `StopAsync` which signals end-of-stream and drains. After that, the file is fully flushed and closed; reading the lines is safe.
- The 50-concurrent test fires `AppendAsync` x 50 in parallel via `Task.WhenAll`. Even at `ChannelCapacity = 100` the channel is well below saturation — back-pressure path is exercised in spirit but not extremis. (The test is for correctness of write atomicity, not for rate-limiting behavior.)
- The dedupe test uses identical `(correlation_id, prompt_hash)` for both entries — the second drop tests the dedupe HashSet path. Different labels in the second entry confirm the dedupe is keyed on `(cid, hash)` only, NOT on the entry payload.
- Each test wraps with `try/finally` to clean up the temp dir.

**Test count: 4.**
  </action>
  <verify>
- `dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off` (after Task 4) succeeds with 0 warnings.
- `grep -nE "(testCase|testList|testSequenced)" tests/SmartRouter.Tests/HardCaseDatasetTests.fs` returns at least 6 hits.
- `grep -n "AppendAsync" tests/SmartRouter.Tests/HardCaseDatasetTests.fs` returns at least 4 hits.
  </verify>
  <done>
HardCaseDatasetTests.fs ships 4 testSequenced tests: single-write JSONL roundtrip, dedupe drops duplicate, 50-concurrent integrity, graceful drain on StopAsync. Each test uses isolated temp dirs.
  </done>
</task>

<task type="auto">
  <name>Task 4: Tests.fsproj wiring + RouterTests.rootTests update</name>
  <files>
    tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    tests/SmartRouter.Tests/RouterTests.fs
  </files>
  <action>
**Step 1 — `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj`:** Insert three new `<Compile>` entries AFTER `LoggingTests.fs` and BEFORE `RouterTests.fs`. The order between the three new files is irrelevant (no inter-deps); alphabetical is fine.

```xml
<Compile Include="LoggingTests.fs" />
<!-- Phase 7 -->
<Compile Include="FailureDetectorTests.fs" />
<Compile Include="TeacherLabelerTests.fs" />
<Compile Include="HardCaseDatasetTests.fs" />
<Compile Include="RouterTests.fs" />
```

**Step 2 — `tests/SmartRouter.Tests/RouterTests.fs`:** Append three new entries to the `rootTests` list (PITFALL-26 — Expecto auto-discovery is forbidden):

```fsharp
let rootTests : Test list =
    [
        SmartRouter.Tests.RoutingTests.tests
        SmartRouter.Tests.StreamingTests.tests
        SmartRouter.Tests.QueueTests.tests
        SmartRouter.Tests.LoadTests.tests
        SmartRouter.Tests.MLRoutingTests.tests
        SmartRouter.Tests.MLEmbeddingTests.tests
        SmartRouter.Tests.MLClassifierTests.tests
        SmartRouter.Tests.LoggingTests.tests
        SmartRouter.Tests.FailureDetectorTests.tests   // Phase 7
        SmartRouter.Tests.TeacherLabelerTests.tests    // Phase 7
        SmartRouter.Tests.HardCaseDatasetTests.tests   // Phase 7
    ]
```

DO NOT modify the entry-point `main` function. The `rootTests` list is the only edit.
  </action>
  <verify>
- `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off` reports total test count = 66 passing + 10 ignored. Specifically:
  - Phase 1-6 baseline: 50 pass + 10 ignored
  - Phase 7 new tests: +16 (6 FailureDetector + 6 TeacherLabeler + 4 HardCaseDataset = 16).
- `grep -nE "(FailureDetectorTests|TeacherLabelerTests|HardCaseDatasetTests)" tests/SmartRouter.Tests/RouterTests.fs` returns at least 3 hits.
- `grep -nE "FailureDetectorTests\.fs|TeacherLabelerTests\.fs|HardCaseDatasetTests\.fs" tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` returns 3 hits, all between LoggingTests.fs and RouterTests.fs.
  </verify>
  <done>
Tests.fsproj has the 3 new Compile entries in correct order. RouterTests.rootTests includes 3 new module references. `dotnet test` runs all rootTests and reports 66 pass + 10 ignored (no regression on existing 50 + 10).
  </done>
</task>

</tasks>

<verification>
**Plan-level verification:**

1. **Full build is clean:**
   ```bash
   dotnet build SmartRouter.slnx -nologo --tl:off
   ```
   Expected: 0 errors, 0 warnings.

2. **Full test run passes:**
   ```bash
   dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off
   ```
   Expected: 66 passed, 10 ignored, 0 failed.

3. **All three new test modules referenced in rootTests:**
   ```bash
   grep -nE "FailureDetectorTests\.tests|TeacherLabelerTests\.tests|HardCaseDatasetTests\.tests" tests/SmartRouter.Tests/RouterTests.fs
   ```
   Expected: 3 hits.

4. **Phase 7 tests are present and discoverable:**
   ```bash
   dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off --filter "FullyQualifiedName~FailureDetector|FullyQualifiedName~TeacherLabeler|FullyQualifiedName~HardCaseDatasetWriter"
   ```
   Expected: 16 tests passed (6 + 6 + 4).

5. **No new ignored tests (none of these depend on ML models):**
   ```bash
   dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off 2>&1 | grep -E "ignored|skipped"
   ```
   Expected: count remains at 10 (LoadTests.fs pending tests + ML model-gated tests).
</verification>

<success_criteria>
- 3 new test files (16 tests total) all green
- Tests.fsproj has 3 new <Compile> entries in correct order before RouterTests.fs
- RouterTests.rootTests includes 3 new module references
- All 50 existing tests still pass + 10 ignored
- 0 build warnings (TreatWarningsAsErrors=true)
- testSequenced wrapper used in all 3 new test modules
- Each test isolates temp dirs/files in try/finally
</success_criteria>

<output>
After completion, create `.planning/phases/07-failure-detection-and-teacher-labeling/07-06-SUMMARY.md` listing the 5 files modified, any test deviations from the plan (especially around the omitted retry test), and the test count delta (expected: 50 → 66).
</output>

## REQ-ID coverage in this plan

- **FAIL-01**: FailureDetectorTests.fs — 6 tests prove JSONL parse + filter + multi-file behavior + malformed-line tolerance.
- **FAIL-02**: TeacherLabelerTests.fs — fake-Kestrel teacher endpoint; ROUTE_35B / ROUTE_122B parse, Unparseable, missing template, cost cap. 30s timeout exercised implicitly via the 5s test client (faster fail than production 30s).
- **FAIL-03**: TeacherLabelerTests.fs cost-cap test — pre-set counter at max; assert Skipped without HTTP call (proven via unreachable address).
- **FAIL-04**: HardCaseDatasetTests.fs — single write, dedupe, 50-concurrent integrity, graceful drain.
