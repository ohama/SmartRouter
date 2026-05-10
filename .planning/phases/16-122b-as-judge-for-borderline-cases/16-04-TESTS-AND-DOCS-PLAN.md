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
    - "README.md §5.5 Quality fallback subsection extended with judge step in cascade flow diagram"
    - "README.md §7 has new `Routing.Judge` config table (5 keys: Enabled, Endpoint, PromptPath, TimeoutSeconds, MaxCacheEntries) with OPT-IN guidance"
    - "README.md §9.3 Trace schema documents judge_called / judge_verdict / judge_latency_ms with example jq workflows"
    - "README.md §8 /stats endpoint section documents 3 new judge_* fields"
    - "CHANGELOG.md `[Unreleased] ### Added` entry mentions Phase 16 122B-as-judge feature with OPT-IN clarification"
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
      provides: "Phase 16 sync rule: §5.5 cascade extension + §7 Routing.Judge table + §9.3 trace fields + §8 /stats fields"
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
  key_links:
    - from: "tests/SmartRouter.Tests/RouterTests.fs"
      to: "JudgeIntegrationTests.tests"
      via: "rootTests list append (PITFALL-26 — explicit registration required for Expecto)"
      pattern: "JudgeIntegrationTests\\.tests"
    - from: "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
      to: "JudgeIntegrationTests.fs"
      via: "<Compile> entry placed BEFORE RouterTests.fs"
      pattern: "JudgeIntegrationTests\\.fs"
    - from: "README.md §5.5 cascade diagram"
      to: "ChatCompletions.fs non-streaming Good arm with classifyBorderline + JudgeClient"
      via: "operator-facing description of step 4 (judge call) in 5-step cascade (Phase 15 had 4 steps)"
      pattern: "borderline.*judge|judge.*borderline"
    - from: "README.md §7 Routing.Judge table"
      to: "src/SmartRouter.Cli/appsettings.json:Routing.Judge"
      via: "table rows list all 5 keys with current defaults; OPT-IN badge on Enabled row"
      pattern: "Routing\\.Judge\\.Enabled.*false"
---

<objective>
Add Phase 16 test coverage proving JDG-01..05 are real, then update README + CHANGELOG to satisfy CLAUDE.md sync rule for the affected 12 mandatory areas (5, 7, 8, 9.3) and document the new feature for operators.

Purpose: A phase is "done" only when its observable truths are demonstrated by tests AND documented for operators. Plan 16-04 closes both:
1. Test coverage of all 5 JDG requirements with mix of unit (BorderlineClassifier 3-way; JudgeClient parser; LRU cache; cache key correctness) + integration (fake-Kestrel; judge HTTP call counter; /stats wire integrity; trace JSONL field assertions).
2. README sections updated so operators discover the new opt-in feature without reading source.
3. CHANGELOG `[Unreleased] ### Added` entry advertises the feature with OPT-IN guidance.

Source code is untouched in this plan (all changes are tests + docs). Verification is the deliverable.

Output:
- New `tests/SmartRouter.Tests/JudgeIntegrationTests.fs` with 5-9 testCases covering all 5 JDG requirements
- `SmartRouter.Tests.fsproj` updated with `<Compile>` entry
- `RouterTests.fs` updated with new entry in rootTests list
- README §5.5 cascade extended with judge step
- README §7 with new `Routing.Judge` config table
- README §9.3 trace schema with 3 new judge_* fields
- README §8 /stats with 3 new judge_* fields
- CHANGELOG.md `[Unreleased] ### Added` entry
- `.planning/docs/quality-check-improvement-options.md` updated with "Phase 16 implemented Tier 3-A" note
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
</context>

<tasks>

<task type="auto">
  <name>Task 1: Create JudgeIntegrationTests.fs covering JDG-01..05</name>
  <files>tests/SmartRouter.Tests/JudgeIntegrationTests.fs, tests/SmartRouter.Tests/SmartRouter.Tests.fsproj, tests/SmartRouter.Tests/RouterTests.fs</files>
  <action>
**EDIT 1 — Create `tests/SmartRouter.Tests/JudgeIntegrationTests.fs`** with 5-9 Expecto testCase entries. Use:
- `Expecto` test pattern from QualitySignalEnrichmentTests.fs (15-03 reference)
- Fake-Kestrel pattern from QualityFallbackTests.fs (14-04 reference)
- `testSequenced` wrapper (PITFALL-26 + Console.SetOut hygiene)
- Unique temp directories per test
- `JsonDocument.Parse` for JSONL assertions

REQUIRED TEST COVERAGE (one or more testCases per requirement):

```fsharp
module SmartRouter.Tests.JudgeIntegrationTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open SmartRouter.Cli.Adapters.QualityCheck
open SmartRouter.Cli.Adapters.BorderlineClassifier
open SmartRouter.Cli.Adapters.JudgeClient

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
            // Use reflection or expose internal? Better: drive through a fake HttpClient.
            // For the unit test, construct a minimal JudgeClient via DI test harness
            // and feed it a response containing both ROUTE_YES and ROUTE_NO.
            let tmpDir = Path.Combine(Path.GetTempPath(), sprintf "judge-test-%s" (Guid.NewGuid().ToString("N")))
            Directory.CreateDirectory(tmpDir) |> ignore
            let promptPath = Path.Combine(tmpDir, "judge-prompt.md")
            File.WriteAllText(promptPath, "Q: {{QUESTION}}\nR: {{RESPONSE}}\nROUTE_YES or ROUTE_NO")
            try
                // Fake HttpMessageHandler returning JSON envelope with both sentinels
                let handler =
                    { new HttpMessageHandler() with
                        member _.SendAsync(req, ct) = ()  // not actually used; placeholder }
                // ... full fake-handler setup omitted for brevity ...
                // Direct unit test of `parseContent` would require visibility change.
                // Instead, drive via fake-Kestrel later in JDG-04.
                Expect.isTrue true "parser collision behavior covered by JDG-04 fake-Kestrel test"
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
            // Construct a JudgeClient with a stub HttpMessageHandler that always returns
            // a parseable ROUTE_YES envelope. First call → cache miss + 1 HTTP call;
            // Second call same key → cache hit + 0 additional HTTP calls.
            let tmpDir = Path.Combine(Path.GetTempPath(), sprintf "judge-test-%s" (Guid.NewGuid().ToString("N")))
            Directory.CreateDirectory(tmpDir) |> ignore
            let promptPath = Path.Combine(tmpDir, "judge-prompt.md")
            File.WriteAllText(promptPath, "Q: {{QUESTION}}\nR: {{RESPONSE}}\nAnswer: ROUTE_YES or ROUTE_NO")
            try
                let mutable httpCallCount = 0
                let stubHandler =
                    { new HttpMessageHandler() with
                        member _.SendAsync(_req: HttpRequestMessage, _ct: CancellationToken) =
                            Interlocked.Increment(&httpCallCount) |> ignore
                            // Minimal valid OpenAI chat-completions envelope
                            let body = """{"choices":[{"message":{"content":"ROUTE_YES"}}]}"""
                            let resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                            resp.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                            Task.FromResult(resp) }

                let services = ServiceCollection()
                services.AddLogging() |> ignore
                // Manual named-client registration with the stub handler
                services.AddHttpClient("judge", fun (c: HttpClient) ->
                    c.BaseAddress <- Uri("http://stub/")
                    c.Timeout     <- TimeSpan.FromSeconds(5.0))
                    .ConfigurePrimaryHttpMessageHandler(fun () -> stubHandler) |> ignore
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
                Expect.equal httpCallCount 1 "first call should hit HTTP"

                // Second call same keys — cache hit
                let v2 = judgeC.VerdictAsync("ph1", "rh1", "q", "r", CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal v2 RouteYes "second call returns cached verdict"
                Expect.equal httpCallCount 1 "second call should NOT hit HTTP (cache hit)"

                let struct (hits, misses, calls) = judgeS.GetJudgeStats()
                Expect.equal hits   1L "cacheHits=1"
                Expect.equal misses 1L "cacheMisses=1"
                Expect.equal calls  1L "callCount=1 (HTTP fired once)"
            finally
                Directory.Delete(tmpDir, true)

        // ── JDG-04: Fake-Kestrel — judge call only on borderline ───────────────

        testCase "JDG-04 judge HTTP NOT called for clearly good 35B response" <| fun _ ->
            // Full fake-Kestrel router with Routing.Judge.Enabled=true; 35B fake returns
            // a clearly-good response (high entropy, length 200). Judge fake counter must = 0.
            // [Implementation mirrors QualityFallbackTests.startTestRouter pattern; pseudocode here for brevity]
            // 1. Start fake 35B Kestrel returning a clearly-good response body.
            // 2. Start fake judge Kestrel that increments a counter on every POST /v1/chat/completions.
            // 3. Configure Router with Routing.Judge.Enabled=true, Endpoint=fake-judge-url,
            //    PromptPath=temp file containing the template, MaxCacheEntries=100.
            // 4. POST a request to the router /v1/chat/completions.
            // 5. Assert: response body contains the fake 35B content.
            // 6. Assert: judge fake counter = 0.
            Expect.isTrue true "covered by router-level integration test below"

        testCase "JDG-04 judge HTTP called once on borderline 35B response; cache hit on second identical request" <| fun _ ->
            // Same setup but 35B fake returns a borderline response (length 35; high entropy).
            // First request → judge counter = 1; second identical request → judge counter still 1.
            Expect.isTrue true "covered by router-level integration test below"

        // ── JDG-05: TraceLog has 3 new judge fields; /stats has 3 new counter fields ─

        testCase "JDG-05 trace JSONL has judge_called=false / verdict=null / latency_ms=null when judge disabled" <| fun _ ->
            // Routing.Judge.Enabled=false → judge_called=false, judge_verdict=null, judge_latency_ms=null
            // schema_version=1 unchanged (additive change).
            Expect.isTrue true "covered by router-level integration test below"

        testCase "JDG-05 /stats wire has judge_cache_hits/_misses/_call_count int64 fields after judge fires" <| fun _ ->
            // After driving 1 cache miss + 1 cache hit, GET /stats returns:
            //   judge_cache_hits = 1, judge_cache_misses = 1, judge_call_count = 1
            Expect.isTrue true "covered by router-level integration test below"

        // ── Router-level integration (combines JDG-04 + JDG-05) ────────────────

        testCase "JDG-04+05 fake-Kestrel: borderline 35B → judge fires → /stats + trace observe correctly" <| fun _ ->
            // Full integration: fake 35B + fake 122B + fake judge + router with Judge.Enabled=true.
            // Drives: clearly-good (no judge), borderline (judge YES), borderline (judge NO → 122B retry).
            // Asserts: judge HTTP counter, /stats wire, trace JSONL field correctness, cache hit on repeat.
            //
            // IMPLEMENTATION NOTE FOR EXECUTOR:
            // - Mirror QualityFallbackTests.fs `startTestRouter` setup.
            // - Inject Routing.Judge.Enabled=true via AddInMemoryCollection.
            // - Use `services.AddHttpClient("judge")` override with stub HttpMessageHandler that
            //   counts calls + returns ROUTE_YES or ROUTE_NO based on a programmable variable.
            // - Drive 4-5 requests; assert all observable surfaces.
            // - Use --trace-responses inject via Trace:Enabled=true in InMemoryCollection.
            Expect.isTrue true "router-level integration: see implementation below"
    ]
```

NOTES FOR EXECUTOR:
- The `stubHandler` HttpMessageHandler pattern requires `member _.SendAsync(req, ct)` as `override` — the abstract class is `HttpMessageHandler.SendAsync : HttpRequestMessage * CancellationToken -> Task<HttpResponseMessage>`. F# object expression syntax may need explicit `override _.SendAsync` for protected member access; if the simple object-expression form fails compilation, use a small concrete class instead.
- The fake-Kestrel router setup in JDG-04+05 should mirror `QualityFallbackTests.fs:startTestRouter` lines 70-280 — read that file as your reference. Update the `AddInMemoryCollection` config block to include:
  ```
  KeyValuePair("Routing:Judge:Enabled", "true")
  KeyValuePair("Routing:Judge:Endpoint", fakeJudgeUrl)
  KeyValuePair("Routing:Judge:PromptPath", tempPromptPath)
  KeyValuePair("Routing:Judge:TimeoutSeconds", "5")
  KeyValuePair("Routing:Judge:MaxCacheEntries", "100")
  KeyValuePair("Trace:Enabled", "true")
  KeyValuePair("Trace:Directory", tempTraceDir)
  ```
- Number of tests: at least 5 unique testCase entries; the placeholder testCases above (`Expect.isTrue true`) MUST be replaced with real assertions in the executor's implementation. If the executor finds the integration test pattern too complex for a single test, splitting JDG-04 + JDG-05 into separate router-level tests is acceptable — final count 5-9 testCases.
- Use `JsonDocument.Parse` for JSONL assertions (same pattern as QualityFallbackTests.fs lines 386, 461). `JsonElement.TryGetProperty("judge_called", &outElement)` for field existence; `.GetBoolean()` / `.GetString()` / `.GetDouble()` / `.ValueKind = JsonValueKind.Null` for value extraction.
- For the /stats integration, use `HttpClient.GetAsync(routerUrl + "/stats")` from inside the test; parse with `JsonDocument.Parse` and assert `judge_cache_hits.GetInt64() = 1` etc.

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

Expected: at least 102 + 5 = 107 passed; 16 ignored; 0 failed.
  </action>
  <verify>
- `ls tests/SmartRouter.Tests/JudgeIntegrationTests.fs` exists; `wc -l` >= 350
- `grep -c "testCase \"JDG-0" tests/SmartRouter.Tests/JudgeIntegrationTests.fs` — at least 5 (one per JDG-01..05 minimum)
- `grep -c "Expect.isTrue true \"" tests/SmartRouter.Tests/JudgeIntegrationTests.fs` — must be 0 or very few (placeholders replaced with real assertions; 0 ideal)
- `grep "JudgeIntegrationTests.fs" tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — exactly 1 line, BEFORE `RouterTests.fs` line
- `grep "JudgeIntegrationTests.tests" tests/SmartRouter.Tests/RouterTests.fs` — exactly 1 line in rootTests
- `dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — exit 0
- `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-build -- --sequenced 2>&1 | tail -20 | grep "Passed:"` — Passed >= 107; Failed = 0; Skipped = 16
- All Phase 14 QF-01..QF-10 tests pass (grep `dotnet test` output for "QF-01" through "QF-10")
- All Phase 15 QSE-01..QSE-06 tests pass
  </verify>
  <done>
- JudgeIntegrationTests.fs exists with 5+ testCase covering JDG-01..05 (at minimum)
- All placeholder `Expect.isTrue true` assertions replaced with real ones
- fsproj + RouterTests.fs registrations done (PITFALL-26 satisfied)
- Test baseline holds: 107+ passed, 16 ignored, 0 failed
- Backward-compat: Phase 14 + Phase 15 + #13 issue-fix tests all pass unchanged
  </done>
</task>

<task type="auto">
  <name>Task 2: Update README.md (§5.5 cascade + §7 Routing.Judge table + §9.3 trace fields + §8 /stats fields) and CHANGELOG</name>
  <files>README.md, CHANGELOG.md, .planning/docs/quality-check-improvement-options.md</files>
  <action>
**EDIT 1 — README.md §5.5 (Quality fallback subsection)**

Locate the existing "5.5 Quality fallback" subsection. After the existing 4-stage cascade description (Phase 15 left it at finish_reason → length → entropy → keyword), append a new "Step 5: Borderline judge (Phase 16, OPT-IN)" paragraph:

```markdown
### 5.5.5 Borderline judge (Phase 16 — OPT-IN)

When `Routing.Judge.Enabled = true` AND the Phase 15 cascade returned `Good`, an
additional check classifies the response as **clearly good** or **borderline**:

- **Clearly good** (entropy ≥ EntropyThreshold + 1.0, OR effective length ≥ MinResponseLength × 1.5): forward 35B's response unchanged. No judge call.
- **Borderline** (entropy in `[EntropyThreshold, EntropyThreshold + 1.0)` OR length in `[MinResponseLength, MinResponseLength × 1.5)`): send a 1-token verification call to 122B asking "Is this response correct and helpful for the question?" — model emits `ROUTE_YES` or `ROUTE_NO`.
  - `ROUTE_NO` + 122B reachable: retry on 122B (same `FallbackTo122B` path as Step 5.5.1's Bad-verdict fallback). `fallback_kind = "quality"` in trace.
  - `ROUTE_YES`, judge skipped (no template), or judge failed: forward 35B's response unchanged (fail-open).

Cached by `(prompt_hash, response_hash)`; identical content pays the judge cost once.
Cache size capped by `Routing.Judge.MaxCacheEntries` (default 10000).

**OPT-IN by default** — `Routing.Judge.Enabled = false` in shipped `appsettings.json`.
Enable only after evaluating your borderline rate via `quality_check_hits_*` counters
on `/stats` (Phase 15) and confirming you have headroom for an extra 122B call on
borderline cases.

Streaming responses (`stream=true`) intentionally bypass the judge — chunks are already
shipped to the client; retract is impossible (same constraint as Phase 14 quality fallback).
```

**EDIT 2 — README.md §7 (Configuration Reference) — add `Routing.Judge` table**

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

**Operator workflow to enable judge:**

1. Ensure `prompts/judge-prompt.md` exists (shipped in repo; verify after deploy).
2. Set `Routing:Judge:Enabled` to `true` in `appsettings.json`.
3. Restart the router (`launchctl unload && launchctl load`).
4. Monitor judge effectiveness via `/stats`: `judge_cache_hits`, `judge_cache_misses`, `judge_call_count`.
5. Monitor borderline rate via Phase 15's `quality_check_hits_*` counters — if low (< 1% of requests), the judge ROI is marginal.
```

**EDIT 3 — README.md §9.3 (Trace schema) — add 3 new judge fields**

Locate the existing trace field table (or numbered list) in §9.3. Add 3 rows AFTER the `bad_reason` row:

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

Update §9.3 schema_version note: "schema_version = 1 (Phase 14-16 — additive only; readers ignoring unknown fields stay forward-compatible)".

**EDIT 4 — README.md §8 (or wherever /stats is documented) — add 3 new judge_* fields**

Locate the existing /stats wire fields list (Phase 15 added quality_check_hits_*). Add:

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
  </action>
  <verify>
- `grep -c "Routing.Judge\|judge_called\|judge_verdict\|judge_latency_ms\|judge_cache_hits" README.md` — at least 10 (multiple sections × multiple fields)
- `grep "OPT-IN" README.md` — at least 2 occurrences (§5.5 callout + §7 Enabled row)
- `grep "Phase 16" CHANGELOG.md` — at least 1 line in Unreleased
- `grep "judge_called\|ROUTE_YES\|ROUTE_NO" README.md` — at least 3 lines (Phase 16 documentation visible)
- `grep "Phase 16 implemented Tier 3-A" .planning/docs/quality-check-improvement-options.md` — exactly 1 line
- `grep "schema_version = 1" README.md` — at least 1 occurrence (clarifying additive-only)
- Run a markdown-lint pass if available; otherwise visually inspect that the edits don't break the table rendering or section numbering
  </verify>
  <done>
- README §5.5 has Step 5.5.5 borderline judge subsection
- README §7 has Routing.Judge table (5 rows) + operator workflow
- README §9.3 has 3 new trace fields + jq examples
- README §8 (or /stats section) has 3 new judge_* fields
- CHANGELOG.md [Unreleased] ### Added has Phase 16 entry with OPT-IN badge
- .planning/docs/quality-check-improvement-options.md has "Phase 16 implemented Tier 3-A" status note
- All 6 areas of CLAUDE.md sync rule satisfied (areas 5, 7, 8, 9.1 (schema implicit via §9.3 trace doc), 11 (operator workflow), 12 (CHANGELOG))
  </done>
</task>

</tasks>

<verification>
**Test verification (re-run all tests after Tasks 1+2):**
- `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-build -- --sequenced` — Passed ≥ 107; Failed = 0; Skipped = 16
- All Phase 14 QF-01..QF-10 tests pass UNCHANGED (judge disabled by default)
- All Phase 15 QSE-01..QSE-06 tests pass UNCHANGED (judge bypass on Bad verdicts)
- All issue #13 tests pass UNCHANGED
- Backward-compat hard gate: zero existing tests modified except for adding `judge_called=false; judge_verdict=None; judge_latency_ms=None` to TraceRecord literal constructions if any (Plan 16-03 should have caught these; Plan 16-04 verifies)

**Build verification:**
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — 0 warnings, 0 errors
- `dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — exit 0

**Documentation verification:**
- README contains all 5 OPT-IN affordances: Enabled config row + §5.5 OPT-IN callout + CHANGELOG OPT-IN tag + operator workflow + §7 link to Phase 15 counter signals
- CHANGELOG advertises Phase 16 in [Unreleased] (operator-discoverable)
- All grep checks in Task 2 verify return non-empty
</verification>

<success_criteria>
- JudgeIntegrationTests.fs exists with 5+ real testCases (no `Expect.isTrue true` placeholders) covering JDG-01..05
- fsproj + rootTests registrations done (PITFALL-26)
- Test baseline ≥ 107 passed, 16 ignored, 0 failed
- Phase 14 + 15 + #13 backward-compat hard gate satisfied
- README.md updated in §5.5, §7, §8 (or wherever /stats lives), §9.3 with all Phase 16 fields + OPT-IN badges + operator workflow
- CHANGELOG.md [Unreleased] ### Added has Phase 16 entry
- `.planning/docs/quality-check-improvement-options.md` notes Phase 16 status
- CLAUDE.md sync rule 12-area gate satisfied for areas 5 (cascade behavior), 7 (config keys), 8 (operational logging — N/A; not changed), 9.1 (DecisionLog — N/A unchanged), 9.3 (TraceLog), 9.6-9.9 (operational log — N/A), 11 (operator workflows), 12 (CHANGELOG)
</success_criteria>

<output>
After completion, create:
- `.planning/phases/16-122b-as-judge-for-borderline-cases/16-04-SUMMARY.md` capturing per-task commits, test counts before/after, README sections updated
- `.planning/phases/16-122b-as-judge-for-borderline-cases/16-SUMMARY.md` (phase-level aggregate) with the same structure as 15-SUMMARY.md: dependency-graph, tech-stack.added (empty for Phase 16 — no new NuGets), patterns, key-files (created/modified), decisions (all 5 OQ + 4 autonomous A-D), metrics, then plan-level subsections aggregating 16-01..04 SUMMARY contents

Commit messages:
- `test(16-04): add JudgeIntegrationTests.fs (JDG-01..05 + integration)`
- `test(16-04): register JudgeIntegrationTests in fsproj + rootTests (PITFALL-26)`
- `docs(16-04): update README §5.5/§7/§8/§9.3 for Phase 16 122B-as-judge`
- `docs(16-04): add CHANGELOG [Unreleased] entry for Phase 16 + status note in quality-check-improvement-options.md`
</output>
