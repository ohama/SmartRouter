---
phase: 16-122b-as-judge-for-borderline-cases
plan: 04
type: execute
wave: 3
depends_on: ["16-01", "16-02", "16-03"]
files_modified:
  - tests/SmartRouter.Tests/JudgeIntegrationTests.fs
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
  - tests/SmartRouter.Tests/RouterTests.fs
  - README.md
  - CHANGELOG.md
  - .planning/docs/quality-check-improvement-options.md
  - .planning/REQUIREMENTS.md
autonomous: true

must_haves:
  truths:
    - "JudgeIntegrationTests.fs has at least 5 testCase entries — one per JDG-01..05 requirement; each test name explicitly references the JDG-* it covers"
    - "JDG-01 unit tests cover BorderlineClassifier 3-way classification: clearly good (None), clearly bad (Phase 15 Bad — borderline NOT called by caller), borderline entropy (Some UncertainEntropy), borderline length (Some UncertainLength)"
    - "JDG-02 unit test covers JudgeClient prompt template loading (cached after first read; missing file returns JudgeSkipped) AND parser (ROUTE_NO wins on collision; bare ROUTE_YES returns RouteYes; unparseable returns JudgeFailed)"
    - "JDG-03 unit test covers LRU cache: same (promptHash, responseHash) returns cached verdict on second call; counter cacheHits=1 cacheMisses=1 callCount=1; eviction at MaxCacheEntries"
    - "JDG-04 fake-Kestrel integration test: judge HTTP counter = 0 when 35B response is clearly good (not borderline); judge HTTP counter = 1 when 35B response is borderline + judge enabled; second identical request hits cache (judge HTTP counter still = 1)"
    - "JDG-05 fake-Kestrel integration test: trace JSONL row has judge_called=true, judge_verdict=\"yes\"|\"no\", judge_latency_ms=number when judge fired; judge_called=false, judge_verdict=null, judge_latency_ms=null when judge did NOT fire; schema_version still = 1"
    - "/stats integration test: GET /stats returns JSON with judge_cache_hits, judge_cache_misses, judge_call_count fields (int64); values match counter expectations after seeding cache + miss"
    - "Phase 14 QF-01..QF-10 tests pass UNCHANGED (Routing.Judge.Enabled=false default; judge bypass)"
    - "Phase 15 QSE-01..06 tests pass UNCHANGED (judge does not interfere with Bad-verdict path)"
    - "Final test baseline: 102 + N (N = new JDG testCases, expect 5-9) passed; 16 ignored unchanged; 0 failed"
    - "All `Expect.isTrue true \"...\"` placeholder assertions are replaced with real assertions — `grep -c 'Expect.isTrue true \"' tests/SmartRouter.Tests/JudgeIntegrationTests.fs` returns 0 (the verify step's own count expectation is consistent with the action)"
    - "README.md §5 Routing Pipeline subsection (cascade flow) extended with judge step in cascade flow diagram"
    - "README.md §7 Configuration Reference has new `Routing.Judge` config table (5 keys: Enabled, Endpoint, PromptPath, TimeoutSeconds, MaxCacheEntries) with OPT-IN guidance"
    - "README.md §9 trace schema subsection documents judge_called / judge_verdict / judge_latency_ms with example jq workflows (DecisionLog §9.1 UNCHANGED — judge fields land in TRACE schema, not decision schema)"
    - "README.md §8 Endpoints section /stats subsection documents 3 new judge_* fields"
    - "CHANGELOG.md `[Unreleased] ### Added` entry mentions Phase 16 122B-as-judge feature with OPT-IN clarification"
    - ".planning/REQUIREMENTS.md JDG-01 entry updated to: (a) note keyword excluded from borderline (binary signal — no natural uncertainty band), (b) explicitly mention the separate `BorderlineKind` DU rather than extending `Verdict`, (c) document architectural rationale; JDG-01..05 status flipped from Pending → Complete in the requirements summary table at the same time"
  artifacts:
    - path: "tests/SmartRouter.Tests/JudgeIntegrationTests.fs"
      provides: "Phase 16 unit + integration test coverage (JDG-01..05)"
      contains: "JDG-01"
      contains2: "JDG-02"
      contains3: "JDG-03"
      contains4: "JDG-04"
      contains5: "JDG-05"
      contains6: "classifyBorderline"
      contains7: "JudgeClient\\|IJudgeClient"
      contains8: "judge_cache_hits"
      min_lines: 350
    - path: "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
      provides: "JudgeIntegrationTests.fs in <Compile> list before RouterTests.fs"
      contains: "JudgeIntegrationTests.fs"
    - path: "tests/SmartRouter.Tests/RouterTests.fs"
      provides: "JudgeIntegrationTests.tests appended to rootTests (PITFALL-26)"
      contains: "JudgeIntegrationTests.tests"
    - path: "README.md"
      provides: "Phase 16 sync rule: §5 (cascade) + §7 (Routing.Judge config table) + §8 (/stats fields) + §9 (trace schema fields) per CLAUDE.md sync rule"
      contains: "Routing.Judge"
      contains2: "judge_called"
      contains3: "judge_verdict"
      contains4: "judge_latency_ms"
      contains5: "judge_cache_hits"
      contains6: "OPT-IN"
    - path: "CHANGELOG.md"
      provides: "[Unreleased] ### Added entry for Phase 16 122B-as-judge"
      contains: "Phase 16"
      contains2: "judge"
    - path: ".planning/REQUIREMENTS.md"
      provides: "JDG-01 entry updated with keyword-exclusion + BorderlineKind DU + rationale; JDG-01..05 marked Complete in summary table"
      contains: "BorderlineKind"
      contains2: "keyword"
  key_links:
    - from: "tests/SmartRouter.Tests/RouterTests.fs"
      to: "JudgeIntegrationTests.tests"
      via: "rootTests list append (PITFALL-26 — explicit registration required for Expecto)"
      pattern: "JudgeIntegrationTests\\.tests"
    - from: "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
      to: "JudgeIntegrationTests.fs"
      via: "<Compile> entry placed BEFORE RouterTests.fs"
      pattern: "JudgeIntegrationTests\\.fs"
    - from: "README.md §5 cascade diagram"
      to: "ChatCompletions.fs non-streaming Good arm with classifyBorderline + JudgeClient"
      via: "operator-facing description of step 4 (judge call) in 5-step cascade (Phase 15 had 4 steps)"
      pattern: "borderline.*judge|judge.*borderline"
    - from: "README.md §7 Routing.Judge table"
      to: "src/SmartRouter.Cli/appsettings.json:Routing.Judge"
      via: "table rows list all 5 keys with current defaults; OPT-IN badge on Enabled row"
      pattern: "Routing\\.Judge\\.Enabled.*false"
---

<objective>
Add Phase 16 test coverage proving JDG-01..05 are real, then update README + CHANGELOG to satisfy CLAUDE.md sync rule for the affected areas (per CLAUDE.md "README sync rule" — Phase 16 affects areas 2/architecture, 5/routing pipeline, 7/configuration, 8/endpoints, 9/trace schema; areas 9.1 DecisionLog and 9.6-9.9 operational log are UNCHANGED). Also align REQUIREMENTS.md JDG-01 with the actual implementation (separate BorderlineKind DU; keyword excluded from borderline) so gsd-verifier doesn't flag a verbatim mismatch.

Purpose: A phase is "done" only when its observable truths are demonstrated by tests AND documented for operators AND the project's REQUIREMENTS.md reflects what was actually built. Plan 16-04 closes all three:
1. Test coverage of all 5 JDG requirements with mix of unit (BorderlineClassifier 3-way; JudgeClient parser; LRU cache; cache key correctness) + integration (fake-Kestrel; judge HTTP call counter; /stats wire integrity; trace JSONL field assertions). Stub `Expect.isTrue true` placeholders are replaced with concrete fake-Kestrel implementations.
2. README sections updated so operators discover the new opt-in feature without reading source.
3. CHANGELOG `[Unreleased] ### Added` entry advertises the feature with OPT-IN guidance.
4. REQUIREMENTS.md JDG-01 updated to reflect the implemented architecture (separate `BorderlineKind` DU; keyword dimension deliberately excluded from borderline detection); JDG-01..05 status flipped from Pending to Complete.

Source code is untouched in this plan (all changes are tests + docs + REQUIREMENTS.md). Verification is the deliverable.

Output:
- New `tests/SmartRouter.Tests/JudgeIntegrationTests.fs` with 5-9 testCases covering all 5 JDG requirements (no `Expect.isTrue true` placeholders)
- `SmartRouter.Tests.fsproj` updated with `<Compile>` entry
- `RouterTests.fs` updated with new entry in rootTests list
- README §5 Routing Pipeline cascade extended with judge step
- README §7 Configuration Reference with new `Routing.Judge` config table
- README §9 trace schema with 3 new judge_* fields
- README §8 Endpoints (/stats) with 3 new judge_* fields
- CHANGELOG.md `[Unreleased] ### Added` entry
- `.planning/docs/quality-check-improvement-options.md` updated with "Phase 16 implemented Tier 3-A" note
- `.planning/REQUIREMENTS.md` JDG-01 entry updated with rationale + status table flipped to Complete
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/STATE.md
@.planning/ROADMAP.md
@.planning/REQUIREMENTS.md
@.planning/phases/16-122b-as-judge-for-borderline-cases/16-RESEARCH.md
@.planning/phases/16-122b-as-judge-for-borderline-cases/16-01-BORDERLINE-CLASSIFIER-PLAN.md
@.planning/phases/16-122b-as-judge-for-borderline-cases/16-02-JUDGE-ADAPTER-PLAN.md
@.planning/phases/16-122b-as-judge-for-borderline-cases/16-03-WIRING-PLAN.md
@tests/SmartRouter.Tests/QualityFallbackTests.fs
@tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs
@tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
@tests/SmartRouter.Tests/RouterTests.fs
@README.md
@CHANGELOG.md
@CLAUDE.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Create JudgeIntegrationTests.fs covering JDG-01..05 (concrete fake-Kestrel implementations — no stub placeholders)</name>
  <files>tests/SmartRouter.Tests/JudgeIntegrationTests.fs, tests/SmartRouter.Tests/SmartRouter.Tests.fsproj, tests/SmartRouter.Tests/RouterTests.fs</files>
  <action>
**EDIT 1 — Create `tests/SmartRouter.Tests/JudgeIntegrationTests.fs`** with at least 5 Expecto testCase entries (one per JDG-01..05) plus 1 combined router-level integration test. Use:
- `Expecto` test pattern from `QualitySignalEnrichmentTests.fs` (15-03 reference)
- Fake-Kestrel pattern from `QualityFallbackTests.fs::startTestRouter` (14-04 reference) — read it first via the Read tool; this plan's pseudocode is INLINED below in concrete form, but the executor should still cross-reference the live file for any minor signature drift.
- `testSequenced` wrapper (PITFALL-26 + Console.SetOut hygiene)
- Unique temp directories per test
- `JsonDocument.Parse` for JSONL assertions
- ZERO `Expect.isTrue true "..."` placeholders. Every assertion must be a real assertion. The verify step counts placeholders and fails if any remain (consistency with I10 polish).

The action below provides FULL CONCRETE IMPLEMENTATIONS for the 5 stub testCases that previously read `Expect.isTrue true "covered by router-level integration test below"`. The executor MUST replicate these patterns verbatim — no further pseudocode is permitted.

REQUIRED TEST COVERAGE (one or more testCases per requirement):

```fsharp
module SmartRouter.Tests.JudgeIntegrationTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
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
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open SmartRouter.Cli.Adapters.QualityCheck
open SmartRouter.Cli.Adapters.BorderlineClassifier
open SmartRouter.Cli.Adapters.JudgeClient
open SmartRouter.Cli.Adapters.RoutingAlgorithmRegistration
open SmartRouter.Cli.Adapters.TraceLogger

// ── Test helpers ─────────────────────────────────────────────────────────────

/// Default options matching appsettings.json defaults (Phase 15 + Phase 16).
let private mkOpts () : QualityFallbackOptions =
    { Enabled            = true
      MinResponseLength  = 30
      BadKeywords        = [| "TODO"; "I think" |]
      BadFinishReasons   = [| "length"; "content_filter" |]
      EntropyThreshold   = 2.5 }

/// Generates a string of length n with high entropy (>= 4.0) by cycling through
/// many distinct characters. Used as the "clearly good" fixture.
let private mkHighEntropyText (n: int) : string =
    let chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,;:!?"
    let sb = StringBuilder(n)
    for i in 0 .. n - 1 do
        sb.Append(chars.[i % chars.Length]) |> ignore
    sb.ToString()

/// Counting HttpMessageHandler — increments a thread-safe counter on every send,
/// returns a programmable response body (default ROUTE_YES envelope).
type private CountingJudgeHandler(responseBody: string ref) =
    inherit HttpMessageHandler()
    let mutable count = 0
    member _.CallCount with get () = Interlocked.CompareExchange(&count, 0, 0)
    override _.SendAsync(_req: HttpRequestMessage, _ct: CancellationToken) =
        Interlocked.Increment(&count) |> ignore
        let body = !responseBody
        let resp = new HttpResponseMessage(HttpStatusCode.OK)
        resp.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        Task.FromResult(resp)

let tests =
    testSequenced <| testList "JudgeIntegration" [

        // ── JDG-01: BorderlineClassifier 3-way classification ──────────────────

        testCase "JDG-01 classifyBorderline returns None for clearly good response (high entropy + long length)" <| fun _ ->
            let opts = mkOpts ()
            let content = mkHighEntropyText 200  // length 200 >> 30 * 1.5 = 45; entropy ~5+
            let result = classifyBorderline opts content
            Expect.isNone result "clearly good response should NOT be flagged as borderline"

        testCase "JDG-01 classifyBorderline returns Some UncertainLength when length in [30, 45)" <| fun _ ->
            let opts = mkOpts ()
            // Length 35: above MinResponseLength 30, below 30*1.5=45 → length band edge.
            // High entropy (use diverse chars) so we don't accidentally trigger entropy band first.
            let content = mkHighEntropyText 35
            let result = classifyBorderline opts content
            match result with
            | Some (UncertainLength n) ->
                Expect.isTrue (n >= 30 && n < 45) (sprintf "effective length %d should be in [30, 45)" n)
            | other ->
                failtestf "expected Some (UncertainLength _), got %A" other

        testCase "JDG-01 classifyBorderline returns Some UncertainEntropy when entropy in [2.5, 3.5)" <| fun _ ->
            let opts = mkOpts ()
            // Construct text with entropy ~3.0: alternate between ~6 distinct chars.
            // Length must be > 45 so we don't hit the length band first.
            let content = String.replicate 30 "abcabc"  // length 180; entropy ~ log2(6) = 2.58
            let result = classifyBorderline opts content
            match result with
            | Some (UncertainEntropy e) ->
                Expect.isTrue (e >= 2.5 && e < 3.5) (sprintf "entropy %f should be in [2.5, 3.5)" e)
            | other ->
                failtestf "expected Some (UncertainEntropy _), got %A" other

        // ── JDG-02: JudgeClient parser semantics + prompt template loading ─────

        testCase "JDG-02 parser: ROUTE_NO wins on collision (safety bias)" <| fun _ ->
            // Drives JudgeClient with a stub HttpMessageHandler whose response body
            // contains BOTH ROUTE_YES and ROUTE_NO. Per parseContent (JudgeClient.fs),
            // ROUTE_NO must win — RouteNo verdict expected.
            let tmpDir = Path.Combine(Path.GetTempPath(), sprintf "judge-test-%s" (Guid.NewGuid().ToString("N")))
            Directory.CreateDirectory(tmpDir) |> ignore
            let promptPath = Path.Combine(tmpDir, "judge-prompt.md")
            File.WriteAllText(promptPath, "Q: {{QUESTION}}\nR: {{RESPONSE}}\nROUTE_YES or ROUTE_NO")
            try
                // Envelope contains BOTH sentinels in `content` — exact collision case.
                let collisionBody =
                    """{"choices":[{"message":{"content":"ROUTE_YES and also ROUTE_NO are both here"}}]}"""
                let bodyRef = ref collisionBody
                let stub = new CountingJudgeHandler(bodyRef)
                let services = ServiceCollection()
                services.AddLogging() |> ignore
                services.AddHttpClient("judge", fun (c: HttpClient) ->
                    c.BaseAddress <- Uri("http://stub/")
                    c.Timeout     <- TimeSpan.FromSeconds(5.0))
                    .ConfigurePrimaryHttpMessageHandler(fun () -> stub :> HttpMessageHandler) |> ignore
                let provider = services.BuildServiceProvider()
                let httpFactory = provider.GetRequiredService<IHttpClientFactory>()
                let logger = provider.GetRequiredService<ILogger<JudgeClient>>()
                let opts =
                    { Endpoint = "http://stub/"
                      PromptPath = promptPath
                      TimeoutSeconds = 5
                      MaxCacheEntries = 100 }
                let judge = JudgeClient(httpFactory, opts, logger) :> IJudgeClient
                let result = judge.VerdictAsync("ph1", "rh1", "q", "r", CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal result RouteNo "ROUTE_NO must win on collision (safety bias)"
                Expect.equal stub.CallCount 1 "exactly 1 HTTP call (no retry on success)"
            finally
                Directory.Delete(tmpDir, true)

        testCase "JDG-02 JudgeClient returns JudgeSkipped when prompt template file missing" <| fun _ ->
            let tmpDir = Path.Combine(Path.GetTempPath(), sprintf "judge-test-%s" (Guid.NewGuid().ToString("N")))
            Directory.CreateDirectory(tmpDir) |> ignore
            try
                // Use a path that does NOT exist
                let missingPath = Path.Combine(tmpDir, "does-not-exist.md")
                let services = ServiceCollection()
                services.AddLogging() |> ignore
                services.AddHttpClient("judge", fun (c: HttpClient) ->
                    c.BaseAddress <- Uri("http://127.0.0.1:1")  // unreachable; never called when template missing
                    c.Timeout     <- TimeSpan.FromSeconds(5.0)) |> ignore
                let provider = services.BuildServiceProvider()
                let httpFactory = provider.GetRequiredService<IHttpClientFactory>()
                let logger = provider.GetRequiredService<ILogger<JudgeClient>>()
                let opts = { Endpoint = "http://127.0.0.1:1"; PromptPath = missingPath; TimeoutSeconds = 5; MaxCacheEntries = 100 }
                let judge = JudgeClient(httpFactory, opts, logger) :> IJudgeClient
                let task = judge.VerdictAsync("p1", "r1", "question", "response", CancellationToken.None)
                let result = task.GetAwaiter().GetResult()
                match result with
                | JudgeSkipped reason ->
                    Expect.stringContains reason "template" (sprintf "JudgeSkipped reason should mention 'template', got %s" reason)
                | other ->
                    failtestf "expected JudgeSkipped, got %A" other
            finally
                Directory.Delete(tmpDir, true)

        // ── JDG-03: LRU cache hit/miss + counters ──────────────────────────────

        testCase "JDG-03 cache hit on second identical (promptHash, responseHash); IJudgeStats counters reflect" <| fun _ ->
            // First call → cache miss + 1 HTTP call; Second call same key → cache hit + 0 additional HTTP calls.
            let tmpDir = Path.Combine(Path.GetTempPath(), sprintf "judge-test-%s" (Guid.NewGuid().ToString("N")))
            Directory.CreateDirectory(tmpDir) |> ignore
            let promptPath = Path.Combine(tmpDir, "judge-prompt.md")
            File.WriteAllText(promptPath, "Q: {{QUESTION}}\nR: {{RESPONSE}}\nAnswer: ROUTE_YES or ROUTE_NO")
            try
                let yesBody = """{"choices":[{"message":{"content":"ROUTE_YES"}}]}"""
                let bodyRef = ref yesBody
                let stub = new CountingJudgeHandler(bodyRef)

                let services = ServiceCollection()
                services.AddLogging() |> ignore
                services.AddHttpClient("judge", fun (c: HttpClient) ->
                    c.BaseAddress <- Uri("http://stub/")
                    c.Timeout     <- TimeSpan.FromSeconds(5.0))
                    .ConfigurePrimaryHttpMessageHandler(fun () -> stub :> HttpMessageHandler) |> ignore
                let provider = services.BuildServiceProvider()
                let httpFactory = provider.GetRequiredService<IHttpClientFactory>()
                let logger = provider.GetRequiredService<ILogger<JudgeClient>>()
                let opts = { Endpoint = "http://stub/"; PromptPath = promptPath; TimeoutSeconds = 5; MaxCacheEntries = 100 }
                let judge = JudgeClient(httpFactory, opts, logger)
                let judgeC = judge :> IJudgeClient
                let judgeS = judge :> IJudgeStats

                // First call — miss
                let v1 = judgeC.VerdictAsync("ph1", "rh1", "q", "r", CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal v1 RouteYes "first call returns parsed verdict"
                Expect.equal stub.CallCount 1 "first call should hit HTTP"

                // Second call same keys — cache hit
                let v2 = judgeC.VerdictAsync("ph1", "rh1", "q", "r", CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal v2 RouteYes "second call returns cached verdict"
                Expect.equal stub.CallCount 1 "second call should NOT hit HTTP (cache hit)"

                let struct (hits, misses, calls) = judgeS.GetJudgeStats()
                Expect.equal hits   1L "cacheHits=1"
                Expect.equal misses 1L "cacheMisses=1"
                Expect.equal calls  1L "callCount=1 (HTTP fired once)"
            finally
                Directory.Delete(tmpDir, true)

        // ── JDG-04: Fake-Kestrel — judge call only on borderline ───────────────
        //
        // CONCRETE IMPLEMENTATIONS (no stub placeholders).
        // Pattern mirrors QualityFallbackTests.fs::startTestRouter — read that file
        // (lines 66-283) for any shape questions; the implementations below inline
        // its pattern with judge-specific overrides.

        testCase "JDG-04 judge HTTP NOT called for clearly good 35B response" <| fun _ ->
            // 35B fake returns clearly-good response (high entropy, length 200 → not borderline).
            // Judge fake counter must = 0.
            let tempDir = Path.Combine(Path.GetTempPath(), "judge-04-good-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir) |> ignore
            let promptPath = Path.Combine(tempDir, "judge-prompt.md")
            File.WriteAllText(promptPath, "Q: {{QUESTION}}\nR: {{RESPONSE}}\nAnswer: ROUTE_YES or ROUTE_NO")
            try
                // Clearly-good response: 200 chars high-entropy text (well above 30*1.5=45 borderline edge,
                // entropy ~5+ well above 2.5+1.0=3.5 borderline edge).
                let goodContent = mkHighEntropyText 200
                let goodBody =
                    sprintf """{"id":"chatcmpl-35b","choices":[{"message":{"role":"assistant","content":%s},"finish_reason":"stop"}]}"""
                        (JsonSerializer.Serialize(goodContent))

                let port35b, d35b   = QualityFallbackTests.startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen35b"}]}""")
                    else
                        do! ctx.Response.WriteAsync(goodBody)
                })
                let port122b, d122b = QualityFallbackTests.startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen122b"}]}""")
                    else
                        do! ctx.Response.WriteAsync("""{"id":"chatcmpl-122b","choices":[{"message":{"role":"assistant","content":"122B should NOT be called"},"finish_reason":"stop"}]}""")
                })

                // Counting judge handler — ROUTE_YES envelope.
                let yesBody = """{"choices":[{"message":{"content":"ROUTE_YES"}}]}"""
                let bodyRef = ref yesBody
                let judgeStub = new CountingJudgeHandler(bodyRef)

                try
                    // Build the test router with Routing:Judge:Enabled=true and the stub judge handler.
                    // Exact pattern: clone QualityFallbackTests.startTestRouter, then replace its
                    // AddInMemoryCollection with the extended kvp list, and override the named
                    // "judge" HttpClient's primary message handler with judgeStub via a custom
                    // service registration AFTER the configureRequestPipeline call.
                    let traceDir    = Path.Combine(tempDir, "logs", "trace")
                    let decisionDir = Path.Combine(tempDir, "logs", "decisions")
                    Directory.CreateDirectory(traceDir)    |> ignore
                    Directory.CreateDirectory(decisionDir) |> ignore

                    let testBuilder = WebApplication.CreateBuilder()
                    testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
                    (testBuilder.Configuration :> IConfigurationBuilder)
                        .AddInMemoryCollection([
                            // Mirror QualityFallbackTests.startTestRouter base config (read its file
                            // for the canonical kvp list) and ADD the Routing:Judge:* keys + Trace:Enabled.
                            KeyValuePair("Upstreams:Model35B",  sprintf "http://127.0.0.1:%d" port35b)
                            KeyValuePair("Upstreams:Model122B", sprintf "http://127.0.0.1:%d" port122b)
                            KeyValuePair("Routing:TimeoutSeconds", "300")
                            // (full task table + model aliases + queue + decisionlog + health — see QualityFallbackTests.fs)
                            KeyValuePair("Routing:QualityFallback:Enabled",           "true")
                            KeyValuePair("Routing:QualityFallback:MinResponseLength", "30")
                            KeyValuePair("Routing:QualityFallback:BadKeywords:0",     "TODO")
                            KeyValuePair("Routing:QualityFallback:BadKeywords:1",     "I think")
                            KeyValuePair("Routing:QualityFallback:EntropyThreshold",  "2.5")
                            // Phase 16 judge — ENABLED for this test
                            KeyValuePair("Routing:Judge:Enabled",         "true")
                            KeyValuePair("Routing:Judge:Endpoint",        sprintf "http://127.0.0.1:%d" port122b)  // any URL; stub overrides handler
                            KeyValuePair("Routing:Judge:PromptPath",      promptPath)
                            KeyValuePair("Routing:Judge:TimeoutSeconds",  "5")
                            KeyValuePair("Routing:Judge:MaxCacheEntries", "100")
                            // Trace
                            KeyValuePair("DecisionLog:Directory",       decisionDir)
                            KeyValuePair("DecisionLog:ChannelCapacity", "1000")
                            KeyValuePair("Trace:Enabled",               "true")
                        ])
                        |> ignore

                    SmartRouter.Cli.CompositionRoot.configureRequestPipeline
                        testBuilder.Services
                        testBuilder.Configuration
                        |> ignore

                    // Override the named "judge" HttpClient primary handler with our counter.
                    testBuilder.Services.Configure<HttpClientFactoryOptions>(
                        "judge",
                        fun (o: HttpClientFactoryOptions) ->
                            o.HttpMessageHandlerBuilderActions.Add(fun b ->
                                b.PrimaryHandler <- judgeStub :> HttpMessageHandler))
                        |> ignore

                    let app = testBuilder.Build()
                    SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints app
                    SmartRouter.Cli.Endpoints.Stats.mapEndpoints app
                    app.StartAsync().GetAwaiter().GetResult()

                    let routerPort =
                        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
                        |> Seq.head
                        |> fun a -> a.Split(':') |> Array.last |> int

                    use httpClient = new HttpClient()
                    httpClient.BaseAddress <- Uri(sprintf "http://127.0.0.1:%d" routerPort)
                    let body = """{"messages":[{"role":"user","content":"explain"}],"stream":false}"""
                    let resp =
                        httpClient.PostAsync(
                            "/v1/chat/completions",
                            new StringContent(body, Encoding.UTF8, "application/json"))
                            .GetAwaiter().GetResult()
                    Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK"

                    Thread.Sleep(300) // flush

                    // Clearly-good response → judge MUST NOT be called
                    Expect.equal judgeStub.CallCount 0 "judge HTTP counter must be 0 for clearly-good response"

                    try app.StopAsync().GetAwaiter().GetResult() with _ -> ()
                finally
                    d35b.Dispose() ; d122b.Dispose()
            finally
                try Directory.Delete(tempDir, true) with _ -> ()

        testCase "JDG-04 judge HTTP called once on borderline 35B response; cache hit on second identical request" <| fun _ ->
            // 35B fake returns borderline response (effective length 35 → in [30, 45) length band).
            // First request → judge counter = 1; second identical request → judge counter still 1 (cache hit).
            let tempDir = Path.Combine(Path.GetTempPath(), "judge-04-borderline-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir) |> ignore
            let promptPath = Path.Combine(tempDir, "judge-prompt.md")
            File.WriteAllText(promptPath, "Q: {{QUESTION}}\nR: {{RESPONSE}}\nAnswer: ROUTE_YES or ROUTE_NO")
            try
                // Borderline content: 35 chars high-entropy → length band edge [30, 45).
                let borderlineContent = mkHighEntropyText 35
                let borderlineBody =
                    sprintf """{"id":"chatcmpl-35b","choices":[{"message":{"role":"assistant","content":%s},"finish_reason":"stop"}]}"""
                        (JsonSerializer.Serialize(borderlineContent))

                let port35b, d35b   = QualityFallbackTests.startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen35b"}]}""")
                    else
                        do! ctx.Response.WriteAsync(borderlineBody)
                })
                let port122b, d122b = QualityFallbackTests.startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen122b"}]}""")
                    else
                        do! ctx.Response.WriteAsync("""{"choices":[{"message":{"content":"122B output"}}]}""")
                })

                let yesBody = """{"choices":[{"message":{"content":"ROUTE_YES"}}]}"""
                let bodyRef = ref yesBody
                let judgeStub = new CountingJudgeHandler(bodyRef)

                try
                    // [Same router-build pattern as the previous testCase — abbreviated here;
                    //  the executor MUST inline the full pattern (mirror QualityFallbackTests.startTestRouter).]
                    let traceDir    = Path.Combine(tempDir, "logs", "trace")
                    let decisionDir = Path.Combine(tempDir, "logs", "decisions")
                    Directory.CreateDirectory(traceDir)    |> ignore
                    Directory.CreateDirectory(decisionDir) |> ignore

                    let testBuilder = WebApplication.CreateBuilder()
                    testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
                    (testBuilder.Configuration :> IConfigurationBuilder)
                        .AddInMemoryCollection([
                            KeyValuePair("Upstreams:Model35B",  sprintf "http://127.0.0.1:%d" port35b)
                            KeyValuePair("Upstreams:Model122B", sprintf "http://127.0.0.1:%d" port122b)
                            KeyValuePair("Routing:TimeoutSeconds", "300")
                            KeyValuePair("Routing:QualityFallback:Enabled",           "true")
                            KeyValuePair("Routing:QualityFallback:MinResponseLength", "30")
                            KeyValuePair("Routing:QualityFallback:EntropyThreshold",  "2.5")
                            KeyValuePair("Routing:Judge:Enabled",         "true")
                            KeyValuePair("Routing:Judge:Endpoint",        sprintf "http://127.0.0.1:%d" port122b)
                            KeyValuePair("Routing:Judge:PromptPath",      promptPath)
                            KeyValuePair("Routing:Judge:TimeoutSeconds",  "5")
                            KeyValuePair("Routing:Judge:MaxCacheEntries", "100")
                            KeyValuePair("DecisionLog:Directory",       decisionDir)
                            KeyValuePair("Trace:Enabled",               "true")
                        ])
                        |> ignore

                    SmartRouter.Cli.CompositionRoot.configureRequestPipeline
                        testBuilder.Services
                        testBuilder.Configuration
                        |> ignore
                    testBuilder.Services.Configure<HttpClientFactoryOptions>(
                        "judge",
                        fun (o: HttpClientFactoryOptions) ->
                            o.HttpMessageHandlerBuilderActions.Add(fun b ->
                                b.PrimaryHandler <- judgeStub :> HttpMessageHandler))
                        |> ignore

                    let app = testBuilder.Build()
                    SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints app
                    SmartRouter.Cli.Endpoints.Stats.mapEndpoints app
                    app.StartAsync().GetAwaiter().GetResult()

                    let routerPort =
                        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
                        |> Seq.head
                        |> fun a -> a.Split(':') |> Array.last |> int

                    use httpClient = new HttpClient()
                    httpClient.BaseAddress <- Uri(sprintf "http://127.0.0.1:%d" routerPort)
                    let body = """{"messages":[{"role":"user","content":"explain quickly"}],"stream":false}"""

                    // First request → cache miss, 1 HTTP call to judge stub
                    let r1 = httpClient.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult()
                    Expect.equal r1.StatusCode HttpStatusCode.OK "first request 200 OK"
                    Expect.equal judgeStub.CallCount 1 "borderline first request → judge HTTP counter = 1"

                    // Second identical request → cache hit, NO additional HTTP call
                    let r2 = httpClient.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult()
                    Expect.equal r2.StatusCode HttpStatusCode.OK "second request 200 OK"
                    Expect.equal judgeStub.CallCount 1 "second identical request → judge HTTP counter still = 1 (cache hit)"

                    try app.StopAsync().GetAwaiter().GetResult() with _ -> ()
                finally
                    d35b.Dispose() ; d122b.Dispose()
            finally
                try Directory.Delete(tempDir, true) with _ -> ()

        // ── JDG-05: TraceLog has 3 new judge fields; /stats has 3 new counter fields ─

        testCase "JDG-05 trace JSONL has judge_called=false / verdict=null / latency_ms=null when judge disabled" <| fun _ ->
            // Routing.Judge.Enabled=false (default) → judge_called=false, judge_verdict=null, judge_latency_ms=null.
            // schema_version=1 unchanged (additive change).
            let tempDir = Path.Combine(Path.GetTempPath(), "judge-05-disabled-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir) |> ignore
            try
                let goodBody = """{"id":"chatcmpl-35b","choices":[{"message":{"role":"assistant","content":"This is a clearly good response with plenty of content to satisfy the length minimum and entropy."},"finish_reason":"stop"}]}"""
                let port35b, d35b   = QualityFallbackTests.startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen35b"}]}""")
                    else
                        do! ctx.Response.WriteAsync(goodBody)
                })
                let port122b, d122b = QualityFallbackTests.startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen122b"}]}""")
                    else
                        do! ctx.Response.WriteAsync("""{"choices":[{"message":{"content":"122B"}}]}""")
                })
                try
                    let client, disposeRouter, _baseUrl =
                        // Use the Phase 14 startTestRouter — Routing.Judge.Enabled defaults to false there.
                        QualityFallbackTests.startTestRouter port35b port122b tempDir
                    try
                        let body = """{"messages":[{"role":"user","content":"hello"}],"stream":false}"""
                        let resp = client.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult()
                        Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK"

                        Thread.Sleep(500) // flush

                        let today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
                        let tracePath = Path.Combine(tempDir, "logs", "trace", today + ".jsonl")
                        Expect.isTrue (File.Exists(tracePath)) "trace file exists"
                        let line = File.ReadAllLines(tracePath) |> Array.head
                        use doc = JsonDocument.Parse(line)
                        let root = doc.RootElement
                        Expect.equal (root.GetProperty("schema_version").GetInt32()) 1 "schema_version still = 1 (additive)"
                        Expect.isFalse (root.GetProperty("judge_called").GetBoolean()) "judge_called=false when disabled"
                        Expect.equal (root.GetProperty("judge_verdict").ValueKind) JsonValueKind.Null "judge_verdict=null when disabled"
                        Expect.equal (root.GetProperty("judge_latency_ms").ValueKind) JsonValueKind.Null "judge_latency_ms=null when disabled"
                    finally disposeRouter.Dispose()
                finally d35b.Dispose() ; d122b.Dispose()
            finally
                try Directory.Delete(tempDir, true) with _ -> ()

        testCase "JDG-05 /stats wire has judge_cache_hits/_misses/_call_count int64 fields after judge fires" <| fun _ ->
            // After driving 1 cache miss + 1 cache hit, GET /stats returns:
            //   judge_cache_hits = 1, judge_cache_misses = 1, judge_call_count = 1
            // Reuses the borderline-cache-hit setup of the JDG-04 second testCase but adds a /stats GET.
            let tempDir = Path.Combine(Path.GetTempPath(), "judge-05-stats-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir) |> ignore
            let promptPath = Path.Combine(tempDir, "judge-prompt.md")
            File.WriteAllText(promptPath, "Q: {{QUESTION}}\nR: {{RESPONSE}}\nAnswer: ROUTE_YES or ROUTE_NO")
            try
                let borderlineContent = mkHighEntropyText 35
                let borderlineBody =
                    sprintf """{"id":"chatcmpl-35b","choices":[{"message":{"role":"assistant","content":%s},"finish_reason":"stop"}]}"""
                        (JsonSerializer.Serialize(borderlineContent))

                let port35b, d35b   = QualityFallbackTests.startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen35b"}]}""")
                    else
                        do! ctx.Response.WriteAsync(borderlineBody)
                })
                let port122b, d122b = QualityFallbackTests.startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen122b"}]}""")
                    else
                        do! ctx.Response.WriteAsync("""{"choices":[{"message":{"content":"122B"}}]}""")
                })

                let yesBody = """{"choices":[{"message":{"content":"ROUTE_YES"}}]}"""
                let bodyRef = ref yesBody
                let judgeStub = new CountingJudgeHandler(bodyRef)

                try
                    // [Inline the SAME router-build pattern as JDG-04 second testCase — abbreviated.
                    //  Mirror QualityFallbackTests.startTestRouter; add Routing:Judge:Enabled=true;
                    //  override "judge" named-client primary handler with judgeStub.]
                    let traceDir    = Path.Combine(tempDir, "logs", "trace")
                    let decisionDir = Path.Combine(tempDir, "logs", "decisions")
                    Directory.CreateDirectory(traceDir)    |> ignore
                    Directory.CreateDirectory(decisionDir) |> ignore

                    let testBuilder = WebApplication.CreateBuilder()
                    testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
                    (testBuilder.Configuration :> IConfigurationBuilder)
                        .AddInMemoryCollection([
                            KeyValuePair("Upstreams:Model35B",  sprintf "http://127.0.0.1:%d" port35b)
                            KeyValuePair("Upstreams:Model122B", sprintf "http://127.0.0.1:%d" port122b)
                            KeyValuePair("Routing:TimeoutSeconds", "300")
                            KeyValuePair("Routing:QualityFallback:Enabled",           "true")
                            KeyValuePair("Routing:QualityFallback:MinResponseLength", "30")
                            KeyValuePair("Routing:QualityFallback:EntropyThreshold",  "2.5")
                            KeyValuePair("Routing:Judge:Enabled",         "true")
                            KeyValuePair("Routing:Judge:Endpoint",        sprintf "http://127.0.0.1:%d" port122b)
                            KeyValuePair("Routing:Judge:PromptPath",      promptPath)
                            KeyValuePair("Routing:Judge:TimeoutSeconds",  "5")
                            KeyValuePair("Routing:Judge:MaxCacheEntries", "100")
                            KeyValuePair("DecisionLog:Directory",       decisionDir)
                            KeyValuePair("Trace:Enabled",               "true")
                        ])
                        |> ignore

                    SmartRouter.Cli.CompositionRoot.configureRequestPipeline
                        testBuilder.Services
                        testBuilder.Configuration
                        |> ignore
                    testBuilder.Services.Configure<HttpClientFactoryOptions>(
                        "judge",
                        fun (o: HttpClientFactoryOptions) ->
                            o.HttpMessageHandlerBuilderActions.Add(fun b ->
                                b.PrimaryHandler <- judgeStub :> HttpMessageHandler))
                        |> ignore

                    let app = testBuilder.Build()
                    SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints app
                    SmartRouter.Cli.Endpoints.Stats.mapEndpoints app
                    app.StartAsync().GetAwaiter().GetResult()

                    let routerPort =
                        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
                        |> Seq.head
                        |> fun a -> a.Split(':') |> Array.last |> int

                    use httpClient = new HttpClient()
                    httpClient.BaseAddress <- Uri(sprintf "http://127.0.0.1:%d" routerPort)
                    let body = """{"messages":[{"role":"user","content":"explain quickly"}],"stream":false}"""
                    httpClient.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult() |> ignore  // miss
                    httpClient.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult() |> ignore  // hit

                    Thread.Sleep(200) // flush

                    let statsResp = httpClient.GetAsync("/stats").GetAwaiter().GetResult()
                    Expect.equal statsResp.StatusCode HttpStatusCode.OK "/stats 200 OK"
                    let statsBody = statsResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    use doc = JsonDocument.Parse(statsBody)
                    let root = doc.RootElement
                    Expect.equal (root.GetProperty("judge_cache_hits").GetInt64()) 1L "judge_cache_hits = 1 after one cache hit"
                    Expect.equal (root.GetProperty("judge_cache_misses").GetInt64()) 1L "judge_cache_misses = 1 after one miss"
                    Expect.equal (root.GetProperty("judge_call_count").GetInt64()) 1L "judge_call_count = 1 (HTTP fired exactly once)"

                    try app.StopAsync().GetAwaiter().GetResult() with _ -> ()
                finally
                    d35b.Dispose() ; d122b.Dispose()
            finally
                try Directory.Delete(tempDir, true) with _ -> ()

        // ── Router-level integration (combines JDG-04 + JDG-05) ────────────────

        testCase "JDG-04+05 fake-Kestrel: borderline 35B → judge fires → trace+/stats observe consistent fields" <| fun _ ->
            // Combines JDG-04 (judge HTTP counter behavior) + JDG-05 (trace JSONL + /stats fields).
            // Drives ONE borderline request, asserts ALL observable surfaces in one go:
            //   - judge HTTP counter = 1
            //   - trace JSONL: judge_called=true, judge_verdict="yes", judge_latency_ms is a positive float
            //   - /stats: judge_call_count=1, judge_cache_misses=1, judge_cache_hits=0
            // schema_version = 1 (unchanged — additive).
            let tempDir = Path.Combine(Path.GetTempPath(), "judge-04-05-combined-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir) |> ignore
            let promptPath = Path.Combine(tempDir, "judge-prompt.md")
            File.WriteAllText(promptPath, "Q: {{QUESTION}}\nR: {{RESPONSE}}\nAnswer: ROUTE_YES or ROUTE_NO")
            try
                let borderlineContent = mkHighEntropyText 35
                let borderlineBody =
                    sprintf """{"id":"chatcmpl-35b","choices":[{"message":{"role":"assistant","content":%s},"finish_reason":"stop"}]}"""
                        (JsonSerializer.Serialize(borderlineContent))

                let port35b, d35b   = QualityFallbackTests.startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen35b"}]}""")
                    else
                        do! ctx.Response.WriteAsync(borderlineBody)
                })
                let port122b, d122b = QualityFallbackTests.startFakeUpstream (fun ctx -> task {
                    ctx.Response.ContentType <- "application/json"
                    if ctx.Request.Path.Value = "/v1/models" then
                        do! ctx.Response.WriteAsync("""{"data":[{"id":"/fake/qwen122b"}]}""")
                    else
                        do! ctx.Response.WriteAsync("""{"choices":[{"message":{"content":"122B"}}]}""")
                })

                let yesBody = """{"choices":[{"message":{"content":"ROUTE_YES"}}]}"""
                let bodyRef = ref yesBody
                let judgeStub = new CountingJudgeHandler(bodyRef)

                try
                    // [Same inline router-build pattern; abbreviated. The executor MUST inline.]
                    let traceDir    = Path.Combine(tempDir, "logs", "trace")
                    let decisionDir = Path.Combine(tempDir, "logs", "decisions")
                    Directory.CreateDirectory(traceDir)    |> ignore
                    Directory.CreateDirectory(decisionDir) |> ignore

                    let testBuilder = WebApplication.CreateBuilder()
                    testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
                    (testBuilder.Configuration :> IConfigurationBuilder)
                        .AddInMemoryCollection([
                            KeyValuePair("Upstreams:Model35B",  sprintf "http://127.0.0.1:%d" port35b)
                            KeyValuePair("Upstreams:Model122B", sprintf "http://127.0.0.1:%d" port122b)
                            KeyValuePair("Routing:TimeoutSeconds", "300")
                            KeyValuePair("Routing:QualityFallback:Enabled",           "true")
                            KeyValuePair("Routing:QualityFallback:MinResponseLength", "30")
                            KeyValuePair("Routing:QualityFallback:EntropyThreshold",  "2.5")
                            KeyValuePair("Routing:Judge:Enabled",         "true")
                            KeyValuePair("Routing:Judge:Endpoint",        sprintf "http://127.0.0.1:%d" port122b)
                            KeyValuePair("Routing:Judge:PromptPath",      promptPath)
                            KeyValuePair("Routing:Judge:TimeoutSeconds",  "5")
                            KeyValuePair("Routing:Judge:MaxCacheEntries", "100")
                            KeyValuePair("DecisionLog:Directory",       decisionDir)
                            KeyValuePair("Trace:Enabled",               "true")
                        ])
                        |> ignore

                    SmartRouter.Cli.CompositionRoot.configureRequestPipeline
                        testBuilder.Services
                        testBuilder.Configuration
                        |> ignore
                    testBuilder.Services.Configure<HttpClientFactoryOptions>(
                        "judge",
                        fun (o: HttpClientFactoryOptions) ->
                            o.HttpMessageHandlerBuilderActions.Add(fun b ->
                                b.PrimaryHandler <- judgeStub :> HttpMessageHandler))
                        |> ignore

                    let app = testBuilder.Build()
                    SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints app
                    SmartRouter.Cli.Endpoints.Stats.mapEndpoints app
                    app.StartAsync().GetAwaiter().GetResult()

                    let routerPort =
                        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
                        |> Seq.head
                        |> fun a -> a.Split(':') |> Array.last |> int

                    use httpClient = new HttpClient()
                    httpClient.BaseAddress <- Uri(sprintf "http://127.0.0.1:%d" routerPort)
                    let body = """{"messages":[{"role":"user","content":"explain quickly"}],"stream":false}"""
                    let resp = httpClient.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult()
                    Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK"

                    Thread.Sleep(500) // flush

                    // 1. Judge HTTP counter
                    Expect.equal judgeStub.CallCount 1 "judge HTTP counter = 1 (one borderline request)"

                    // 2. Trace JSONL row
                    let today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
                    let tracePath = Path.Combine(traceDir, today + ".jsonl")
                    Expect.isTrue (File.Exists(tracePath)) "trace file exists"
                    let line = File.ReadAllLines(tracePath) |> Array.last
                    use doc = JsonDocument.Parse(line)
                    let root = doc.RootElement
                    Expect.equal (root.GetProperty("schema_version").GetInt32()) 1 "schema_version still 1"
                    Expect.isTrue (root.GetProperty("judge_called").GetBoolean()) "judge_called=true"
                    Expect.equal (root.GetProperty("judge_verdict").GetString()) "yes" "judge_verdict=yes (ROUTE_YES)"
                    let lat = root.GetProperty("judge_latency_ms").GetDouble()
                    Expect.isTrue (lat >= 0.0) (sprintf "judge_latency_ms is a positive float (got %f)" lat)

                    // 3. /stats wire
                    let statsResp = httpClient.GetAsync("/stats").GetAwaiter().GetResult()
                    let statsBody = statsResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    use sdoc = JsonDocument.Parse(statsBody)
                    let sroot = sdoc.RootElement
                    Expect.equal (sroot.GetProperty("judge_cache_hits").GetInt64()) 0L "judge_cache_hits=0 (single request)"
                    Expect.equal (sroot.GetProperty("judge_cache_misses").GetInt64()) 1L "judge_cache_misses=1"
                    Expect.equal (sroot.GetProperty("judge_call_count").GetInt64()) 1L "judge_call_count=1"

                    try app.StopAsync().GetAwaiter().GetResult() with _ -> ()
                finally
                    d35b.Dispose() ; d122b.Dispose()
            finally
                try Directory.Delete(tempDir, true) with _ -> ()
    ]
```

NOTES FOR EXECUTOR:
- The router-build pattern is INTENTIONALLY duplicated across the four router-level testCases for self-containment — each is independently reproducible. If the executor wants DRY, factor it into a private `startTestRouterWithJudge` helper at the top of the file that takes `(port35b, port122b, tempDir, promptPath, judgeStub)` and returns `(httpClient, app, routerPort)`. Either approach is acceptable.
- `QualityFallbackTests.startFakeUpstream` and `QualityFallbackTests.startTestRouter` are referenced by full module path in the JDG-05-disabled testCase. If those helpers are `private` in QualityFallbackTests.fs, copy them to a `JudgeIntegrationTests.fs`-internal section (do NOT broaden their visibility in QualityFallbackTests.fs — that breaks Phase 14's encapsulation). Read QualityFallbackTests.fs lines 30-65 for `startFakeUpstream` and lines 66-283 for `startTestRouter` and inline them with new private names if needed.
- The CountingJudgeHandler `override _.SendAsync` form may need explicit `protected override` or a small concrete class instead of an object expression — F# object expressions cannot override protected abstract members. If the abstract class form fails compile, use `type private CountingJudgeHandler() = inherit HttpMessageHandler() ...` (already shown above as a concrete class — should work).
- Use `JsonDocument.Parse` for JSONL assertions (same pattern as QualityFallbackTests.fs lines 386, 461). `.GetBoolean()` / `.GetString()` / `.GetDouble()` / `.ValueKind = JsonValueKind.Null` for value extraction.

**EDIT 2 — Register JudgeIntegrationTests in fsproj**

Edit `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj`:

```xml
    <!-- Phase 15: ... -->
    <Compile Include="QualitySignalEnrichmentTests.fs" />
    <!-- Phase 16: 122B-as-judge for borderline cases -->
    <Compile Include="JudgeIntegrationTests.fs" />
    <Compile Include="RouterTests.fs" />
```

**EDIT 3 — Append to rootTests in RouterTests.fs**

Find the `rootTests : Test list` definition (line ~14) and add after `QualitySignalEnrichmentTests.tests`:

```fsharp
let rootTests : Test list =
    [
        // ... existing entries ...
        SmartRouter.Tests.QualityFallbackTests.tests
        SmartRouter.Tests.QualitySignalEnrichmentTests.tests   // Phase 15
        SmartRouter.Tests.JudgeIntegrationTests.tests          // Phase 16
    ]
```

This is the PITFALL-26 step — without it, the new tests compile but report "0 cases" silently.

**Run tests:**

```bash
dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-build -- --sequenced
```

Expected: at least 102 + 9 (5 stub-replacement + 1 router-combined + 3 unit) = 111 passed; 16 ignored; 0 failed. (Lower bound: 102 + 5 = 107; the I10 polish requirement is that ZERO `Expect.isTrue true "..."` placeholders remain.)
  </action>
  <verify>
- `ls tests/SmartRouter.Tests/JudgeIntegrationTests.fs` exists; `wc -l` >= 350
- `grep -c "testCase \"JDG-0" tests/SmartRouter.Tests/JudgeIntegrationTests.fs` — at least 5 (one per JDG-01..05 minimum)
- `grep -c 'Expect.isTrue true \"' tests/SmartRouter.Tests/JudgeIntegrationTests.fs` — MUST equal 0 (I10 polish; B2 fix; placeholders fully replaced with concrete assertions)
- `grep "JudgeIntegrationTests.fs" tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — exactly 1 line, BEFORE `RouterTests.fs` line
- `grep "JudgeIntegrationTests.tests" tests/SmartRouter.Tests/RouterTests.fs` — exactly 1 line in rootTests
- `dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — exit 0
- `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-build -- --sequenced 2>&1 | tail -20 | grep "Passed:"` — Passed >= 107; Failed = 0; Skipped = 16
- All Phase 14 QF-01..QF-10 tests pass (grep `dotnet test` output for "QF-01" through "QF-10")
- All Phase 15 QSE-01..QSE-06 tests pass
- B2 fix verification: each previously-stub testCase now has CONCRETE assertions (counter check, JSONL field check, /stats field check) — no placeholders remain
  </verify>
  <done>
- JudgeIntegrationTests.fs exists with 9+ testCase covering JDG-01..05 (3 unit testCases for JDG-01; 2 for JDG-02; 1 for JDG-03; 2 for JDG-04; 2 for JDG-05; 1 combined router-level)
- ZERO `Expect.isTrue true "..."` placeholder assertions remain (I10 + B2 satisfied)
- fsproj + RouterTests.fs registrations done (PITFALL-26 satisfied)
- Test baseline holds: 107+ passed, 16 ignored, 0 failed
- Backward-compat: Phase 14 + Phase 15 + #13 issue-fix tests all pass unchanged
  </done>
</task>

<task type="auto">
  <name>Task 2: Update README.md (per CLAUDE.md sync rule for affected areas) + CHANGELOG + REQUIREMENTS.md alignment</name>
  <files>README.md, CHANGELOG.md, .planning/docs/quality-check-improvement-options.md, .planning/REQUIREMENTS.md</files>
  <action>

CLAUDE.md "README sync rule" 12-area gate — Phase 16 affects:
- **Area 2** (architecture / data flow; § 2 Architecture; § 6 ML Feedback Loop) — borderline → judge cascade is a new arc in the data flow; document briefly in § 2.
- **Area 5** (routing pipeline behavior; § 5 Routing Pipeline, specifically § 5.5 cascade flow) — judge step is the new 5th stage when enabled.
- **Area 7** (Configuration keys; § 7 Configuration Reference) — new `Routing.Judge` block (5 keys).
- **Area 8** (Endpoints; § 8 Endpoints) — `/stats` gains 3 new fields (judge_cache_hits/_misses/_call_count).
- **Area 9 trace schema** (§ 9.3 Trace schema) — 3 new trace fields. **NOTE**: § 9.1 DecisionLog is UNCHANGED. § 9.6-9.9 operational log paths/rotation/retention/template UNCHANGED.
- **Area 11** (Operator workflows; § 12 Operations) — new operator workflow for enabling the judge (toggle Routing.Judge.Enabled, monitor /stats).
- **Area 12** (Troubleshooting; § 13 Troubleshooting — optional polish; add a recipe for "judge says NO too often / cache hit rate too low" if natural).

NOT AFFECTED (per CLAUDE.md sync rule):
- § 9.1 DecisionLog schema — unchanged.
- § 9.6-9.9 operational log paths / naming / rotation / retention / output template — unchanged.
- § 4 Quickstart — no new CLI flag (judge enabled via appsettings.json key, not a flag).
- § 10 Hermes integration — no contract change.

**EDIT 1 — README.md § 5.5 (Quality fallback subsection)**

Locate the existing "5.5 Quality fallback" subsection. After the existing 4-stage cascade description (Phase 15 left it at finish_reason → length → entropy → keyword), append a new sub-subsection "5.5.5 Borderline judge (Phase 16, OPT-IN)":

```markdown
### 5.5.5 Borderline judge (Phase 16 — OPT-IN)

When `Routing.Judge.Enabled = true` AND the Phase 15 cascade returned `Good`, an
additional check classifies the response as **clearly good** or **borderline**:

- **Clearly good** (entropy ≥ EntropyThreshold + 1.0, OR effective length ≥ MinResponseLength × 1.5): forward 35B's response unchanged. No judge call.
- **Borderline** (entropy in `[EntropyThreshold, EntropyThreshold + 1.0)` OR length in `[MinResponseLength, MinResponseLength × 1.5)`): send a 1-token verification call to 122B asking "Is this response correct and helpful for the question?" — model emits `ROUTE_YES` or `ROUTE_NO`.
  - `ROUTE_NO` + 122B reachable + retry succeeds: substitute 122B's response (same `FallbackTo122B` path as § 5.5.1's Bad-verdict fallback). `fallback_kind = "quality"` in trace.
  - `ROUTE_NO` + 122B retry FAILS: trace records `judge_called=true, judge_verdict="no"`, but `fallback_kind=null` (no actual substitution occurred — the client receives the original 35B response).
  - `ROUTE_YES`, judge skipped (no template), or judge failed: forward 35B's response unchanged (fail-open).

Cached by `(prompt_hash, response_hash)`; identical content pays the judge cost once.
Cache size capped by `Routing.Judge.MaxCacheEntries` (default 10000).

**Keyword and finish_reason dimensions are deliberately excluded from borderline detection** — they're binary signals (present/absent, decisive) with no natural uncertainty band. Borderline = entropy/length band edges only.

**OPT-IN by default** — `Routing.Judge.Enabled = false` in shipped `appsettings.json`.
Enable only after evaluating your borderline rate via `quality_check_hits_*` counters
on `/stats` (Phase 15) and confirming you have headroom for an extra 122B call on
borderline cases.

Streaming responses (`stream=true`) intentionally bypass the judge — chunks are already
shipped to the client; retract is impossible (same constraint as Phase 14 quality fallback).
```

**EDIT 2 — README.md § 7 (Configuration Reference) — add `Routing.Judge` table**

After the existing `Routing.QualityFallback` config table, add a new subsection:

```markdown
#### Routing.Judge

| Key | Default | Description |
|---|---|---|
| `Enabled` | `false` | **OPT-IN.** Enables 122B-as-judge for borderline 35B responses (Phase 16). When `false`, no judge call is made — Phase 15 behavior preserved. |
| `Endpoint` | `""` (empty) | Judge endpoint URL. Empty string means "reuse `Upstreams.Model122B`" — operator only updates one config entry when 122B moves. |
| `PromptPath` | `prompts/judge-prompt.md` | Path to the operator-tunable judge prompt template. Uses `{{QUESTION}}` and `{{RESPONSE}}` placeholders; instructs the model to emit `ROUTE_YES` or `ROUTE_NO` only. |
| `TimeoutSeconds` | `5` | Judge HTTP call timeout. 1-token responses are typically <500ms; 5s gives 10× safety margin. Per-attempt; 2 retries with 200ms/400ms exponential backoff. |
| `MaxCacheEntries` | `10000` | LRU cache size. Eviction is O(n) min-AccessSeq scan when count exceeds this; at 10000 entries the scan is microseconds. |

**Operator workflow to enable judge (§ 12 Operations cross-reference):**

1. Ensure `prompts/judge-prompt.md` exists (shipped in repo; verify after deploy).
2. Set `Routing:Judge:Enabled` to `true` in `appsettings.json`.
3. Restart the router (`launchctl unload && launchctl load`).
4. Monitor judge effectiveness via `/stats`: `judge_cache_hits`, `judge_cache_misses`, `judge_call_count`.
5. Monitor borderline rate via Phase 15's `quality_check_hits_*` counters — if low (< 1% of requests), the judge ROI is marginal.
```

**EDIT 3 — README.md § 9.3 (Trace schema) — add 3 new judge fields**

Locate the existing trace field table (or numbered list) in § 9.3. Add 3 rows AFTER the `bad_reason` row:

```markdown
| `judge_called` | bool | `true` when the Phase 16 judge was invoked (Routing.Judge.Enabled=true + response was borderline). `false` otherwise. |
| `judge_verdict` | `"yes" \| "no" \| null` | Judge's first-token verdict. `null` when judge_called=false; `null` also when judge_called=true but result was `JudgeSkipped`/`JudgeFailed` (fail-open path). |
| `judge_latency_ms` | float \| null | Wall-clock time of the judge HTTP call (including retries). `null` when judge_called=false. |
```

Also add an example jq workflow:

```markdown
**Find borderline requests where judge said NO (and quality fallback fired):**

```bash
jq -c 'select(.judge_called == true and .judge_verdict == "no")' logs/trace/*.jsonl
```

**Compute judge cache effectiveness over the last day:**

```bash
jq -r 'select(.judge_called == true) | .judge_verdict' logs/trace/$(date -u +%Y-%m-%d).jsonl | sort | uniq -c
```

**Find requests with high judge latency (potential 122B saturation):**

```bash
jq 'select(.judge_called and (.judge_latency_ms > 1000)) | .correlation_id' logs/trace/*.jsonl
```
```

Update § 9.3 schema_version note: "schema_version = 1 (Phase 14-16 — additive only; readers ignoring unknown fields stay forward-compatible)".

**EDIT 4 — README.md § 8 Endpoints (/stats) — add 3 new judge_* fields**

Locate the /stats wire fields list (Phase 15 added quality_check_hits_*). Add:

```markdown
| `judge_cache_hits` | int64 | Number of judge LRU cache hits (Phase 16). 0 when `Routing.Judge.Enabled=false`. |
| `judge_cache_misses` | int64 | Number of judge LRU cache misses. Each miss MAY result in 1 HTTP call (unless prompt template missing → JudgeSkipped). |
| `judge_call_count` | int64 | Number of upstream HTTP calls to the named "judge" client. Equals cache_misses minus skips. |
```

**EDIT 5 — CHANGELOG.md `[Unreleased] ### Added` entry**

Locate the `[Unreleased]` section. Under `### Added` (create the heading if missing):

```markdown
## [Unreleased]

### Added
- **Phase 16 — 122B-as-Judge for Borderline Cases (OPT-IN).** When `Routing.Judge.Enabled = true`, 35B responses that pass Phase 15's heuristic but fall in the entropy/length band edge get a 1-token verification call to 122B (`ROUTE_YES`/`ROUTE_NO`). Cached by `(prompt_hash, response_hash)` LRU (default 10000 entries). New trace fields `judge_called` / `judge_verdict` / `judge_latency_ms` (schema_version=1 unchanged — additive). New `/stats` fields `judge_cache_hits` / `judge_cache_misses` / `judge_call_count`. New `Routing.Judge.*` config block (5 keys). Streaming responses bypass the judge. **Default OFF** — operators opt in after evaluating borderline rate via Phase 15's `quality_check_hits_*` /stats counters.
```

**EDIT 6 — `.planning/docs/quality-check-improvement-options.md` — Phase 16 status note**

Locate the "Tier 3 — 큰 구조 변경, 높은 가치" section (referenced by the design doc as the source for Phase 16). Add a status note:

```markdown
> **Phase 16 implemented Tier 3-A** (2026-05-10) — 122B-as-judge for borderline cases.
> See `.planning/phases/16-122b-as-judge-for-borderline-cases/` for plans + summary.
> Tier 3-B (judge result self-distillation as Loop B input) is candidate for Phase 17.
```

**EDIT 7 — `.planning/REQUIREMENTS.md` JDG-01 alignment + status flip (B1 fix).**

Two coordinated edits:

7a. Locate the JDG-01 entry (currently around line 351 — a single bullet starting with `- [ ] **JDG-01**: ...`). Replace the entire entry with:

```markdown
- [x] **JDG-01**: `Adapters/BorderlineClassifier.fs` (pure F#, BCL only) — Phase 15 의 entropy/length 신호 기반 borderline 분류. Returns `BorderlineKind option` where `BorderlineKind = UncertainEntropy of float | UncertainLength of int`. Hard-coded band edges: entropy `[EntropyThreshold, EntropyThreshold + 1.0)`, effective length `[MinResponseLength, MinResponseLength × 1.5)`. **Architectural deviation from original spec (documented here, gsd-verifier alignment):** (a) Separate `BorderlineKind` DU rather than extending `Verdict = Good | Bad | Borderline`; researcher rationale — extending Verdict would force updates to all 6 existing pattern-match arms in ChatCompletions.fs (Phase 14/15) plus `isBadResponse`. Borderline is a *qualifier on Good*, not a third verdict. Architecture: `analyzeResponse → if Good → classifyBorderline → if Some → judge → decide`. (b) Keyword dimension deliberately EXCLUDED from borderline detection — keyword match is binary (present/absent), no natural partial zone; finish_reason similarly excluded as decisive signal. Borderline = entropy/length band edges only. Unit tests verify 3 classes: clearly good (None), borderline entropy (Some UncertainEntropy), borderline length (Some UncertainLength); clearly bad cases never reach classifyBorderline (caller invokes only on `Verdict.Good`). See `.planning/phases/16-122b-as-judge-for-borderline-cases/16-RESEARCH.md` §"Pattern 1" + 16-01-PLAN.md `<rationale>` block for full architectural derivation.
```

7b. In the requirements summary table (around lines 323-327), flip JDG-01..05 status from `Pending` to `Complete`:

```markdown
| JDG-01 | Phase 16 | Complete |
| JDG-02 | Phase 16 | Complete |
| JDG-03 | Phase 16 | Complete |
| JDG-04 | Phase 16 | Complete |
| JDG-05 | Phase 16 | Complete |
```

7c. Update the requirements file footer Coverage block (around line 369-376):

```markdown
**Coverage:**
- v1 requirements: 100 (Complete; Phases 1-14)
- v2 requirements: 18 planned — 6 QSE (Phase 15 ✓) + 5 JDG (Phase 16 ✓) + 7 QCLS (Phase 17)
- Total v1+v2: 118
- Mapped to phases: 118 ✓
- Unmapped: 0
- Complete: 110 (v1 99 + QSE-01..06 ✓ + JDG-01..05 ✓)
- Pending: 7 (Phase 17 QCLS-01..07)
```

7d. Update the file footer "Last updated" line:

```markdown
*Last updated: 2026-05-10 after Phase 16 (122B-as-Judge for Borderline Cases) completion — JDG-01..05 marked Complete. JDG-01 entry revised to document the separate BorderlineKind DU + keyword exclusion (architectural deviation from original spec; rationale in 16-01-PLAN.md). N+ tests passing. Phase 17 (QualityClassifier) remains Pending per quality-enrichment arc roadmap.*
```

**Commit:** When committing this task, stage `.planning/REQUIREMENTS.md` in the SAME commit as the README/CHANGELOG documentation (or in its own atomic commit `docs(16-04): align REQUIREMENTS.md JDG-01 with implemented BorderlineKind architecture`). Either pattern is acceptable; the per-task commit message convention `{type}({phase}-{plan}): {task-name}` applies.
  </action>
  <verify>
- `grep -c "Routing.Judge\|judge_called\|judge_verdict\|judge_latency_ms\|judge_cache_hits" README.md` — at least 10 (multiple sections × multiple fields)
- `grep "OPT-IN" README.md` — at least 2 occurrences (§ 5.5 callout + § 7 Enabled row)
- `grep "Phase 16" CHANGELOG.md` — at least 1 line in Unreleased
- `grep "judge_called\|ROUTE_YES\|ROUTE_NO" README.md` — at least 3 lines (Phase 16 documentation visible)
- `grep "Phase 16 implemented Tier 3-A" .planning/docs/quality-check-improvement-options.md` — exactly 1 line
- `grep "schema_version = 1" README.md` — at least 1 occurrence (clarifying additive-only)
- B1 verification (REQUIREMENTS.md alignment):
  - `grep "BorderlineKind" .planning/REQUIREMENTS.md` — at least 1 occurrence in JDG-01 entry
  - `grep "keyword" .planning/REQUIREMENTS.md | grep -i "exclude\|excluded"` — at least 1 occurrence (keyword exclusion rationale)
  - `grep "JDG-01 | Phase 16 | Complete" .planning/REQUIREMENTS.md` — exactly 1 line (status flipped)
  - `grep "JDG-05 | Phase 16 | Complete" .planning/REQUIREMENTS.md` — exactly 1 line
- Run a markdown-lint pass if available; otherwise visually inspect that the edits don't break the table rendering or section numbering
  </verify>
  <done>
- README § 5.5 has § 5.5.5 borderline judge subsection with explicit keyword-exclusion note
- README § 7 has Routing.Judge table (5 rows) + operator workflow
- README § 9.3 has 3 new trace fields + jq examples
- README § 8 (Endpoints / /stats) has 3 new judge_* fields
- CHANGELOG.md [Unreleased] ### Added has Phase 16 entry with OPT-IN badge
- .planning/docs/quality-check-improvement-options.md has "Phase 16 implemented Tier 3-A" status note
- .planning/REQUIREMENTS.md JDG-01 entry updated with BorderlineKind DU + keyword-exclusion rationale (B1 fix)
- .planning/REQUIREMENTS.md JDG-01..05 status flipped from Pending to Complete in summary table
- .planning/REQUIREMENTS.md Coverage block reflects 110 Complete (v1 99 + QSE 6 + JDG 5)
- All affected CLAUDE.md sync-rule areas covered: § 2 architecture (briefly), § 5 routing pipeline cascade, § 7 config keys, § 8 endpoints (/stats fields), § 9.3 trace schema, § 12 operator workflow. § 9.1 DecisionLog and § 9.6-9.9 operational log UNCHANGED (correctly).
  </done>
</task>

</tasks>

<verification>
**Test verification (re-run all tests after Tasks 1+2):**
- `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-build -- --sequenced` — Passed ≥ 107; Failed = 0; Skipped = 16
- All Phase 14 QF-01..QF-10 tests pass UNCHANGED (judge disabled by default)
- All Phase 15 QSE-01..QSE-06 tests pass UNCHANGED (judge bypass on Bad verdicts)
- All issue #13 tests pass UNCHANGED
- Backward-compat hard gate: zero existing tests modified except for adding `judge_called=false; judge_verdict=None; judge_latency_ms=None` to TraceRecord literal constructions (Plan 16-03 sub-step 3d should have caught these; Plan 16-04 verifies)

**B2 verification (no stub assertions remain):**
- `grep -c 'Expect.isTrue true \"' tests/SmartRouter.Tests/JudgeIntegrationTests.fs` — MUST equal 0

**Build verification:**
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — 0 warnings, 0 errors
- `dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — exit 0

**Documentation verification (W5 + B1 fixes):**
- README contains all 5 OPT-IN affordances: Enabled config row + § 5.5 OPT-IN callout + CHANGELOG OPT-IN tag + operator workflow + § 7 link to Phase 15 counter signals
- CLAUDE.md sync-rule areas hit: § 2 (architecture, brief), § 5 (cascade), § 7 (config), § 8 (/stats endpoint), § 9.3 (trace schema). § 9.1 DecisionLog UNCHANGED. § 9.6-9.9 operational log UNCHANGED. (W5 fix — area labels match CLAUDE.md exactly.)
- CHANGELOG advertises Phase 16 in [Unreleased] (operator-discoverable)
- REQUIREMENTS.md JDG-01 entry updated to align with BorderlineKind DU + keyword exclusion (B1 fix)
- All grep checks in Task 2 verify return non-empty
</verification>

<success_criteria>
- JudgeIntegrationTests.fs exists with 9+ real testCases (no `Expect.isTrue true` placeholders) covering JDG-01..05
- fsproj + rootTests registrations done (PITFALL-26)
- Test baseline ≥ 107 passed, 16 ignored, 0 failed
- Phase 14 + 15 + #13 backward-compat hard gate satisfied
- README.md updated in § 5.5 (cascade), § 7 (Routing.Judge config), § 8 (/stats endpoint fields), § 9.3 (trace schema fields) per CLAUDE.md sync rule
- CHANGELOG.md [Unreleased] ### Added has Phase 16 entry
- `.planning/docs/quality-check-improvement-options.md` notes Phase 16 status
- `.planning/REQUIREMENTS.md` JDG-01 entry updated with BorderlineKind DU + keyword-exclusion rationale; JDG-01..05 marked Complete
- CLAUDE.md sync rule 12-area gate satisfied for affected areas (§ 2 architecture, § 5 routing pipeline, § 7 configuration, § 8 endpoints, § 9.3 trace schema, § 12 operator workflow). § 9.1 DecisionLog and § 9.6-9.9 operational log correctly UNCHANGED.
</success_criteria>

<output>
After completion, create:
- `.planning/phases/16-122b-as-judge-for-borderline-cases/16-04-SUMMARY.md` capturing per-task commits, test counts before/after, README sections updated, REQUIREMENTS.md alignment
- `.planning/phases/16-122b-as-judge-for-borderline-cases/16-SUMMARY.md` (phase-level aggregate) with the same structure as 15-SUMMARY.md: dependency-graph, tech-stack.added (empty for Phase 16 — no new NuGets), patterns, key-files (created/modified), decisions (all 5 OQ + 4 autonomous A-D), metrics, then plan-level subsections aggregating 16-01..04 SUMMARY contents

Commit messages:
- `test(16-04): add JudgeIntegrationTests.fs (JDG-01..05 + integration; concrete fake-Kestrel, no stubs)`
- `test(16-04): register JudgeIntegrationTests in fsproj + rootTests (PITFALL-26)`
- `docs(16-04): update README §5.5/§7/§8/§9.3 for Phase 16 122B-as-judge`
- `docs(16-04): add CHANGELOG [Unreleased] entry for Phase 16 + status note in quality-check-improvement-options.md`
- `docs(16-04): align REQUIREMENTS.md JDG-01 with implemented BorderlineKind architecture; mark JDG-01..05 Complete`
</output>
