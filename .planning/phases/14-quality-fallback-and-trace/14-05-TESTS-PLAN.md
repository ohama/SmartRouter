---
phase: 14-quality-fallback-and-trace
plan: 05
type: execute
wave: 5
depends_on: ["14-01", "14-02", "14-04"]
files_modified:
  - tests/SmartRouter.Tests/QualityFallbackTests.fs
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
  - tests/SmartRouter.Tests/RouterTests.fs
autonomous: true

must_haves:
  truths:
    - "tests/SmartRouter.Tests/QualityFallbackTests.fs exists with at least 2 testCase entries: scenario A (35B-only success) + scenario B (35B bad → 122B retry)"
    - "Both tests wrapped in `testSequenced (testList \"quality-fallback\" [...])` per project convention"
    - "Each test uses unique temp directories for logs/decisions/, logs/trace/, models/, datasets/ to avoid race with other tests"
    - "Test fixtures use `configureWithoutMl` + manual stub registration (RoutingAlgorithmRegistration with Algorithm = test-stub forcing decision.Target = Qwen35B); no real ML model files needed"
    - "Test fixtures register fake fake-Kestrel 35B + 122B upstreams returning canned responses controlled by test"
    - "Test fixtures register a real ITraceLogger pointed at the test temp directory (Trace:Enabled=true via in-memory config)"
    - "Test 1 (35B-only): fake 35B returns good response; assert decision_log.routing_reason = \"ml\", trace.fallback_kind = null, trace.final_target = \"Qwen35B\""
    - "Test 2 (quality fallback): fake 35B returns \"TODO: implement\"; fake 122B returns good response; assert decision_log.routing_reason = \"fallback_to_122b\", decision_log.fallback_used = true, trace.fallback_kind = \"quality\", trace.initial_target = \"Qwen35B\", trace.final_target = \"Qwen122B\", trace.initial_response_excerpt is non-null and contains \"TODO\""
    - "Both tests verify by parsing the JSONL files (logs/decisions and logs/trace) using JsonDocument.Parse — NOT string-Contains shortcuts"
    - "Test count: 80 → 82 passed; dotnet build clean; dotnet test green"
  artifacts:
    - path: "tests/SmartRouter.Tests/QualityFallbackTests.fs"
      provides: "Two integration tests covering 35B-only success and quality-fallback paths via log inspection"
      min_lines: 200
---

<objective>
Two integration tests verifying the quality fallback path end-to-end via log inspection. Tests use fake-Kestrel 35B + 122B and inspect JSONL logs (DecisionLog + TraceLog) to assert correct routing, target attribution, and trace excerpts.

Per user-requested workflow: tests use the cold-start CLI path semantics (test fixtures explicitly clear router.zip via stub algorithm) AND the prompt UID + trace JSONL infrastructure (--trace-responses enabled in fixture).
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/14-quality-fallback-and-trace/14-CONTEXT.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Create QualityFallbackTests.fs with 2 testCase entries</name>
  <files>
    - tests/SmartRouter.Tests/QualityFallbackTests.fs (NEW)
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs
  </files>
  <action>
**Step 1.** Create `tests/SmartRouter.Tests/QualityFallbackTests.fs`. Skeleton:

```fsharp
module SmartRouter.Tests.QualityFallbackTests

// Phase 14: integration tests for the quality-based fallback path (35B response →
// quality check → 122B retry on bad response). Verification is done by parsing
// the JSONL logs (logs/decisions/<date>.jsonl + logs/trace/<date>.jsonl) — the
// same way an operator would diagnose a real fallback in production.
//
// Tests are forced through 35B by injecting a stub RoutingAlgorithmRegistration
// (no real ML model files required). Fake-Kestrel 35B + 122B upstreams return
// canned responses controlled per-test.

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Expecto

open SmartRouter.Core.Domain
open SmartRouter.Cli.Adapters.RoutingAlgorithm
open SmartRouter.Cli.Adapters.TraceLogger

/// Per-test temp directory; cleanup via try/finally.
let private withTempDir (fn: string -> 'a) : 'a =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-qf-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    Directory.CreateDirectory(Path.Combine(dir, "logs/decisions")) |> ignore
    Directory.CreateDirectory(Path.Combine(dir, "logs/trace")) |> ignore
    Directory.CreateDirectory(Path.Combine(dir, "models")) |> ignore
    Directory.CreateDirectory(Path.Combine(dir, "datasets")) |> ignore
    try fn dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

/// Fake-Kestrel upstream returning a canned chat-completions response body.
let private startFakeUpstream (responseBody: string) : int * IDisposable =
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
    let app = builder.Build()
    let handler =
        RequestDelegate(fun ctx ->
            task {
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsync(responseBody)
            } :> Task)
    app.Run(handler) |> ignore
    app.StartAsync().GetAwaiter().GetResult()
    let port =
        app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            .Addresses
        |> Seq.head
        |> fun addr -> addr.Split(':') |> Array.last |> int
    let dispose =
        { new IDisposable with
            member _.Dispose() = app.StopAsync().GetAwaiter().GetResult() }
    port, dispose

/// Build the smart-router under test pointed at the given fake upstreams + temp dirs.
/// Stub RoutingAlgorithmRegistration forces decision.Target = Qwen35B regardless of
/// prompt content (no real ML model files needed).
let private startRouter
    (tempDir: string)
    (model35bUrl: string)
    (model122bUrl: string)
    : HttpClient * IDisposable * string =
    
    let testBuilder = WebApplication.CreateBuilder()
    testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore

    (testBuilder.Configuration :> IConfigurationBuilder)
        .AddInMemoryCollection([
            KeyValuePair("Upstreams:Model35B",  model35bUrl)
            KeyValuePair("Upstreams:Model122B", model122bUrl)
            KeyValuePair("Routing:TimeoutSeconds", "30")
            // Force quality fallback active with default heuristic
            KeyValuePair("Routing:QualityFallback:Enabled",            "true")
            KeyValuePair("Routing:QualityFallback:MinResponseLength",  "30")
            KeyValuePair("Routing:QualityFallback:BadKeywords:0",      "TODO")
            KeyValuePair("Routing:QualityFallback:BadKeywords:1",      "I think")
            KeyValuePair("Routing:TaskTable:graph_indexing:Model",     "122b")
            KeyValuePair("Routing:TaskTable:graph_indexing:Priority",  "high")
            KeyValuePair("Routing:TaskTable:retrieval:Model",          "35b")
            KeyValuePair("Routing:TaskTable:retrieval:Priority",       "low")
            KeyValuePair("Routing:ModelAliases:35b",  "Qwen35B")
            KeyValuePair("Routing:ModelAliases:122b", "Qwen122B")
            KeyValuePair("Queue:FairnessK", "10")
            KeyValuePair("Queue:MaxConcurrent122B", "1")
            KeyValuePair("Queue:PerRequestTimeoutSeconds", "60")
            KeyValuePair("DecisionLog:Directory", Path.Combine(tempDir, "logs/decisions"))
            KeyValuePair("DecisionLog:ChannelCapacity", "1000")
            // --trace-responses equivalent: enable trace logger
            KeyValuePair("Trace:Enabled", "true")
            KeyValuePair("Routing:Health:PollingIntervalSeconds", "60")
            KeyValuePair("Routing:Health:ConsecutiveFailureThreshold", "1")
        ]) |> ignore
    
    SmartRouter.Cli.CompositionRoot.configureWithoutMl
        testBuilder.Services testBuilder.Configuration |> ignore
    
    // Stub: force decision.Target = Qwen35B + Reason = ML on every routing.
    let stubAlgorithm : RoutingAlgorithm =
        fun _cfg _req ->
            { Target       = Qwen35B
              Priority     = Low
              Reason       = ML
              IsFallback   = false
              ModelVersion = "test-stub" }
    let stubReg : RoutingAlgorithmRegistration =
        { Algorithm    = stubAlgorithm
          Name         = "ml"
          ModelVersion = "test-stub" }
    testBuilder.Services.AddSingleton<RoutingAlgorithmRegistration>(stubReg) |> ignore

    // Stub IHealthProbe: both upstreams reachable.
    let stubHealthProbe =
        { new SmartRouter.Core.Ports.IHealthProbe with
            member _.IsReachableAsync(_, _) = Task.FromResult true
            member _.IsReachable(_) = true
            member _.LastProbedAt(_) = DateTimeOffset.UtcNow }
    testBuilder.Services.AddSingleton<SmartRouter.Core.Ports.IHealthProbe>(stubHealthProbe) |> ignore

    // Trace logger registration. configureWithoutMl does NOT register it; emulate
    // the configureRequestPipeline conditional (Trace:Enabled=true → triple-reg).
    //
    // Note: this test fixture registers ITraceLogger UNCONDITIONALLY (always-on
    // tracing for the test scenarios). Production CompositionRoot conditionally
    // registers based on `Trace:Enabled` config key. The trace-DISABLED code path
    // in ChatCompletions.fs (the `if not (isNull (box traceLogger)) then ...`
    // branch in 14-04) is covered IMPLICITLY by the existing 80 tests in
    // RouterTests.fs (none of which register ITraceLogger). This QualityFallbackTests
    // fixture only exercises the trace-enabled scenarios.
    testBuilder.Services.Configure<TraceLoggerOptions>(fun (opts: TraceLoggerOptions) ->
        opts.Directory       <- Path.Combine(tempDir, "logs/trace")
        opts.ChannelCapacity <- 1000) |> ignore
    testBuilder.Services.AddSingleton<TraceLogger>() |> ignore
    testBuilder.Services.AddSingleton<ITraceLogger>(
        fun sp -> sp.GetRequiredService<TraceLogger>() :> ITraceLogger) |> ignore
    testBuilder.Services.AddHostedService<TraceLogger>(
        fun sp -> sp.GetRequiredService<TraceLogger>()) |> ignore

    let app = testBuilder.Build()
    SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints app
    SmartRouter.Cli.Endpoints.Stats.mapEndpoints app

    app.StartAsync().GetAwaiter().GetResult()
    let routerPort =
        app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            .Addresses
        |> Seq.head
        |> fun addr -> addr.Split(':') |> Array.last |> int
    
    let httpClient = new HttpClient()
    let dispose =
        { new IDisposable with
            member _.Dispose() =
                httpClient.Dispose()
                app.StopAsync().GetAwaiter().GetResult() }
    httpClient, dispose, sprintf "http://127.0.0.1:%d" routerPort

/// Compute prompt_hash[:12] from a single prompt string.
///
/// **Single-message-only equivalence**: production `computePromptHash`
/// (DecisionLogger.fs) hashes `messages |> List.map (m.Content) |> String.concat ""`.
/// This helper hashes a raw string. They produce IDENTICAL results when the test
/// uses a single-message payload `{"messages":[{"role":"user","content":"<prompt>"}]}`,
/// because the production concat of one element is just that element. If a test is
/// extended to multi-message conversations, replicate the full production concat
/// logic before calling SHA-256.
let private computePromptUid (prompt: string) : string =
    use sha = System.Security.Cryptography.SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(prompt)
    let hash = sha.ComputeHash(bytes)
    hash
    |> Array.take 6
    |> Array.map (fun b -> sprintf "%02x" b)
    |> String.concat ""

/// Find a JSONL row matching the predicate.
let private findRow (jsonlPath: string) (predicate: JsonElement -> bool) : JsonElement option =
    let lines = File.ReadAllLines(jsonlPath)
    lines
    |> Array.tryPick (fun line ->
        if String.IsNullOrWhiteSpace(line) then None
        else
            let doc = JsonDocument.Parse(line)
            if predicate doc.RootElement then Some doc.RootElement else None)

let tests =
    testSequenced (testList "quality-fallback" [

        // Scenario A — 35B returns good response; quality fallback NOT triggered.
        // Expected: DecisionLog routing_reason="ml", target=Qwen35B, fallback_used=false
        //           Trace fallback_kind=null, final_target="Qwen35B"
        testCase "QF-01: 35B good response — no fallback fires; logs reflect single-call routing" <| fun () ->
            withTempDir <| fun tempDir ->
                let goodResponse = """{"choices":[{"message":{"role":"assistant","content":"Recursion is a function calling itself with smaller inputs until a base case is reached."}}]}"""
                let badResponse = """{"choices":[{"message":{"role":"assistant","content":"this should not be called"}}]}"""
                let port35b, d35b   = startFakeUpstream goodResponse
                let port122b, d122b = startFakeUpstream badResponse
                try
                    let client, dr, baseUrl =
                        startRouter tempDir
                            (sprintf "http://127.0.0.1:%d" port35b)
                            (sprintf "http://127.0.0.1:%d" port122b)
                    try
                        let promptText = "explain recursion"
                        let uid = computePromptUid promptText
                        let body =
                            sprintf """{"messages":[{"role":"user","content":"%s"}],"stream":false}""" promptText
                        let resp =
                            client.PostAsync(
                                baseUrl + "/v1/chat/completions",
                                new StringContent(body, Encoding.UTF8, "application/json"))
                                .GetAwaiter().GetResult()
                        Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK"
                        let respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        Expect.stringContains respBody "Recursion is a function" "35B response forwarded"
                        
                        // Wait for async log writes to flush.
                        Thread.Sleep(500)
                        
                        let decisionLogPath = Path.Combine(tempDir, "logs/decisions",
                            sprintf "%s.jsonl" (DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")))
                        let decisionRow = findRow decisionLogPath (fun e -> e.GetProperty("prompt_hash").GetString().StartsWith(uid))
                        Expect.isSome decisionRow "DecisionLog row exists for this prompt"
                        let dr = decisionRow.Value
                        Expect.equal (dr.GetProperty("target").GetString()) "Qwen35B" "target = Qwen35B"
                        Expect.equal (dr.GetProperty("routing_reason").GetString()) "ml" "routing_reason = ml (no fallback)"
                        Expect.isFalse (dr.GetProperty("fallback_used").GetBoolean()) "fallback_used = false"
                        
                        let traceLogPath = Path.Combine(tempDir, "logs/trace",
                            sprintf "%s.jsonl" (DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")))
                        let traceRow = findRow traceLogPath (fun e -> e.GetProperty("prompt_uid").GetString() = uid)
                        Expect.isSome traceRow "Trace row exists for this prompt"
                        let tr = traceRow.Value
                        Expect.equal (tr.GetProperty("initial_target").GetString()) "Qwen35B" "trace initial_target"
                        Expect.equal (tr.GetProperty("final_target").GetString()) "Qwen35B" "trace final_target"
                        Expect.equal (tr.GetProperty("fallback_kind").ValueKind) JsonValueKind.Null "trace fallback_kind = null"
                    finally dr.Dispose()
                finally d35b.Dispose() ; d122b.Dispose()

        // Scenario B — 35B returns "TODO: implement" (bad keyword); quality fallback fires;
        // 122B returns good response.
        // Expected: DecisionLog routing_reason="fallback_to_122b", target=Qwen122B, fallback_used=true
        //           Trace fallback_kind="quality", initial_target="Qwen35B", initial_response_excerpt non-null,
        //                  final_target="Qwen122B", final_response_excerpt non-null
        testCase "QF-02: 35B 'TODO' response triggers quality fallback to 122B; logs show both" <| fun () ->
            withTempDir <| fun tempDir ->
                let bad35bResponse = """{"choices":[{"message":{"role":"assistant","content":"TODO: implement this"}}]}"""
                let good122bResponse = """{"choices":[{"message":{"role":"assistant","content":"Recursion is a function calling itself, terminating at a base case."}}]}"""
                let port35b, d35b   = startFakeUpstream bad35bResponse
                let port122b, d122b = startFakeUpstream good122bResponse
                try
                    let client, dr, baseUrl =
                        startRouter tempDir
                            (sprintf "http://127.0.0.1:%d" port35b)
                            (sprintf "http://127.0.0.1:%d" port122b)
                    try
                        let promptText = "explain recursion in detail"
                        let uid = computePromptUid promptText
                        let body =
                            sprintf """{"messages":[{"role":"user","content":"%s"}],"stream":false}""" promptText
                        let resp =
                            client.PostAsync(
                                baseUrl + "/v1/chat/completions",
                                new StringContent(body, Encoding.UTF8, "application/json"))
                                .GetAwaiter().GetResult()
                        Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK"
                        let respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        Expect.stringContains respBody "Recursion is a function" "122B (good) response forwarded — not 35B's TODO"
                        Expect.isFalse (respBody.Contains("TODO: implement")) "35B's bad response NOT forwarded to client"
                        
                        Thread.Sleep(500)
                        
                        let decisionLogPath = Path.Combine(tempDir, "logs/decisions",
                            sprintf "%s.jsonl" (DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")))
                        let decisionRow = findRow decisionLogPath (fun e -> e.GetProperty("prompt_hash").GetString().StartsWith(uid))
                        Expect.isSome decisionRow "DecisionLog row exists"
                        let dr = decisionRow.Value
                        Expect.equal (dr.GetProperty("target").GetString()) "Qwen122B" "target = Qwen122B (fallback final)"
                        Expect.equal (dr.GetProperty("routing_reason").GetString()) "fallback_to_122b" "routing_reason = fallback_to_122b"
                        Expect.isTrue (dr.GetProperty("fallback_used").GetBoolean()) "fallback_used = true"
                        
                        let traceLogPath = Path.Combine(tempDir, "logs/trace",
                            sprintf "%s.jsonl" (DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")))
                        let traceRow = findRow traceLogPath (fun e -> e.GetProperty("prompt_uid").GetString() = uid)
                        Expect.isSome traceRow "Trace row exists"
                        let tr = traceRow.Value
                        Expect.equal (tr.GetProperty("initial_target").GetString()) "Qwen35B" "trace initial_target = Qwen35B"
                        Expect.equal (tr.GetProperty("final_target").GetString()) "Qwen122B" "trace final_target = Qwen122B"
                        Expect.equal (tr.GetProperty("fallback_kind").GetString()) "quality" "trace fallback_kind = quality"
                        let initialExcerpt = tr.GetProperty("initial_response_excerpt").GetString()
                        Expect.stringContains initialExcerpt "TODO" "trace initial_response_excerpt contains TODO"
                        let finalExcerpt = tr.GetProperty("final_response_excerpt").GetString()
                        Expect.stringContains finalExcerpt "Recursion" "trace final_response_excerpt contains 122B response"
                    finally dr.Dispose()
                finally d35b.Dispose() ; d122b.Dispose()
    ])
```

**Step 2.** Add Compile entry to `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj`. Place after `MLLiveVersionTests.fs`:

```xml
<Compile Include="MLLiveVersionTests.fs" />
<!-- Phase 14: quality fallback integration tests -->
<Compile Include="QualityFallbackTests.fs" />
<Compile Include="RouterTests.fs" />
```

**Step 3.** Add to `tests/SmartRouter.Tests/RouterTests.fs` `rootTests`:

```fsharp
SmartRouter.Tests.MLLiveVersionTests.tests    // Issue #12
SmartRouter.Tests.QualityFallbackTests.tests  // Phase 14
```
  </action>
  <verify>
```bash
test -f tests/SmartRouter.Tests/QualityFallbackTests.fs && echo OK
grep -c "testCase\|ptestCase" tests/SmartRouter.Tests/QualityFallbackTests.fs
# expected: >= 2
grep -c "QualityFallbackTests\.tests" tests/SmartRouter.Tests/RouterTests.fs
# expected: 1
grep -c "QualityFallbackTests\.fs" tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
# expected: 1
dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj 2>&1 | tail -3
# expected: Build succeeded.
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --filter "FullyQualifiedName~QualityFallback" --no-restore 2>&1 | tail -5
# expected: 2 passed
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-restore 2>&1 | grep "EXPECTO!" | tail -1
# expected: 82 passed (was 80 + 2)
```
  </verify>
</task>

</tasks>

<verification>
- [x] QualityFallbackTests.fs 2 testCase (QF-01 35B-good + QF-02 quality-fallback)
- [x] testSequenced + 매 테스트 unique temp dir
- [x] startFakeUpstream + startRouter helpers
- [x] computePromptUid (prompt → SHA-256[:12])
- [x] DecisionLog + Trace 둘 다 JsonDocument.Parse 로 검증 (string-Contains 회피)
- [x] 80 → 82 passed
</verification>
