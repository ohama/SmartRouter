---
phase: 02-sse-streaming-pass-through
plan: 02
type: execute
wave: 2
depends_on:
  - "02-01"
files_modified:
  - tests/SmartRouter.Tests/StreamingTests.fs
  - tests/SmartRouter.Tests/RouterTests.fs
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
autonomous: true

must_haves:
  truths:
    - "TTFB test passes: against a fake upstream that delays 100 ms between chunks, the first SSE event reaches the test client well under 2 s — proving STRM-01 (ResponseHeadersRead actually streams) and STRM-03 (per-chunk FlushAsync is firing)."
    - "Chunk ordering test passes: 10 chunks emitted as `data: 0` ... `data: 9` are received by the client in order with no reordering — proving STRM-02."
    - "100-chunk integrity test passes: 100 chunks emitted by the fake upstream are received exactly, in order, byte-equal to upstream output — no duplication, no loss, no split events."
    - "Mid-stream cancellation test passes: the test client cancels after receiving 5 chunks; (a) no `ObjectDisposedException` propagates from the router; (b) the fake upstream's `ctx.RequestAborted` fires within one chunk interval — proving STRM-05 (client disconnect aborts upstream) and STRM-06 (HttpResponseMessage held until copy completes)."
    - "[DONE] forwarded test passes: when the fake upstream emits `data: [DONE]\\n\\n` as its final event, the client receives exactly one `data: [DONE]` and it is the final element."
    - "[DONE] injected test passes: when the fake upstream omits `[DONE]`, the client still receives exactly one `data: [DONE]` as the final element — proving Strategy D (STRM-07)."
    - "Header assertion test passes: response headers include `Content-Type: text/event-stream`, `Cache-Control: no-cache`, NO `Content-Length` header (Kestrel uses chunked transfer encoding) — proving STRM-04."
    - "Routing-before-streaming test passes: a `stream=true` request with `task=foobar` returns HTTP 400 + JSON error body, NOT an SSE response — proving routing happens before SSE headers are set."
  artifacts:
    - path: "tests/SmartRouter.Tests/StreamingTests.fs"
      provides: "Expecto testList wrapped in testSequenced; covers all 8 must-have truths above; uses a real Kestrel fake upstream on http://127.0.0.1:0 (OS-assigned port), not TestServer."
      min_lines: 200
      contains: "testSequenced"
    - path: "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
      provides: "Compile entry for StreamingTests.fs (must precede RouterTests.fs); ProjectReference to SmartRouter.Cli; Microsoft.AspNetCore.App framework reference for Kestrel + WebApplication APIs."
      contains: "StreamingTests.fs"
    - path: "tests/SmartRouter.Tests/RouterTests.fs"
      provides: "rootTests list updated to include `SmartRouter.Tests.StreamingTests.tests`."
      contains: "StreamingTests.tests"
  key_links:
    - from: "StreamingTests.fs (test client)"
      to: "SmartRouter.Cli WebApplication built in-process with Kestrel-on-random-port"
      via: "WebApplication.CreateBuilder + AddInMemoryCollection override of Upstreams:Model35B / Model122B"
      pattern: "AddInMemoryCollection"
    - from: "Router under test (Kestrel)"
      to: "Fake upstream Kestrel on 127.0.0.1:0"
      via: "HTTP POST to fakeBaseUrl + /v1/chat/completions, configured via test config override"
      pattern: "127\\.0\\.0\\.1:0|UseUrls"
    - from: "Mid-stream cancellation test"
      to: "Fake upstream RequestAborted hook"
      via: "ctx.RequestAborted.Register sets a TaskCompletionSource flag; assertion awaits the flag with a short timeout"
      pattern: "RequestAborted.Register|RequestAborted\\.Token\\.Register"
    - from: "startTestRouter helper"
      to: "ChatCompletions route registration"
      via: "SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints app"
      pattern: "mapEndpoints"
---

<objective>
Write `StreamingTests.fs` that proves all 5 SSE pitfalls are mitigated end-to-end and satisfies REQ TEST-03.

Purpose: Without these tests, the streaming code from Plan 02-01 is shipped on faith. The tests are the contract that prevents future refactors from silently regressing TTFB, chunk ordering, [DONE] handling, or mid-stream cancellation. Phase 2 cannot exit until these tests run green against the real Plan 02-01 code.

Output:
- A new `tests/SmartRouter.Tests/StreamingTests.fs` Expecto module containing one `testList "streaming"` wrapped in `testSequenced`, with 8 tests covering TTFB, chunk ordering, 100-chunk integrity, mid-stream cancellation, [DONE] forwarded, [DONE] injected, header assertions, and routing-before-streaming.
- A test-side helper that spins up a fake upstream Kestrel server on `http://127.0.0.1:0` (OS-assigned port) and exposes its base URL.
- A test-side helper that builds an in-process `WebApplication` for SmartRouter.Cli with `Upstreams:Model35B` / `Upstreams:Model122B` overridden via `AddInMemoryCollection` to point at the fake upstream.
- `SmartRouter.Tests.fsproj` updated to compile `StreamingTests.fs` BEFORE `RouterTests.fs`, add a `<ProjectReference>` to `SmartRouter.Cli`, and add a `<FrameworkReference Include="Microsoft.AspNetCore.App" />` so Kestrel/WebApplication APIs are available.
- `RouterTests.fs` updated to append `SmartRouter.Tests.StreamingTests.tests` to the `rootTests` list (explicit registration — Expecto auto-discovery is forbidden per PITFALL-26).
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md
@.planning/REQUIREMENTS.md
@.planning/research/PITFALLS.md
@.planning/phases/02-sse-streaming-pass-through/02-RESEARCH.md
@.planning/phases/02-sse-streaming-pass-through/02-01-STREAMING-IMPL-PLAN.md

# Source files this plan reads (to understand the WebApplication shape it must mount in-process)
@src/SmartRouter.Cli/CompositionRoot.fs
@src/SmartRouter.Cli/Program.fs
@src/SmartRouter.Cli/Endpoints/ChatCompletions.fs

# Existing test files (this plan adds StreamingTests.fs and edits these two)
@tests/SmartRouter.Tests/RouterTests.fs
@tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
</context>

<tasks>

<task type="auto">
  <name>Task 1: Wire test project for streaming tests (fsproj + rootTests + stub StreamingTests.fs)</name>
  <files>
tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
tests/SmartRouter.Tests/RouterTests.fs
tests/SmartRouter.Tests/StreamingTests.fs
  </files>
  <action>
**Why a stub file in this task:** The fsproj edit below adds `<Compile Include="StreamingTests.fs" />` and the RouterTests edit references `SmartRouter.Tests.StreamingTests.tests`. Without a real file on disk, `dotnet build` fails on Task 1 in isolation. To keep the build green between Task 1 and Task 2, this task ALSO creates a minimal stub `StreamingTests.fs` (empty testList). Task 2 then replaces the stub body with the real 8 tests.

**Create `tests/SmartRouter.Tests/StreamingTests.fs` (stub — Task 2 will overwrite the body):**

```fsharp
namespace SmartRouter.Tests

module StreamingTests =
    open Expecto

    let tests : Test = testSequenced (testList "streaming" [])
```

This stub is intentionally empty — it lets the build/test pipeline succeed after Task 1 (zero streaming tests run, but Phase-1's 22 tests still pass). Task 2 will replace the file with the 8 real tests.

**`SmartRouter.Tests.fsproj` updates:**

1. Add a `<FrameworkReference Include="Microsoft.AspNetCore.App" />` so the test project can call `WebApplication.CreateBuilder()`, `app.MapPost`, `IServer`, `IServerAddressesFeature`, etc. Without this the test cannot host Kestrel in-process. Confirm by reading the existing `SmartRouter.Cli.fsproj` — that project uses `Microsoft.NET.Sdk.Web` which auto-references the framework; the test project uses `Microsoft.NET.Sdk` so the framework reference must be explicit.

2. Add a `<ProjectReference Include="..\..\src\SmartRouter.Cli\SmartRouter.Cli.fsproj" />` so tests can call `SmartRouter.Cli.CompositionRoot.configureServices`, `SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints`, and `SmartRouter.Cli.Adapters.QwenUpstreamClient.UpstreamOptions`. (The existing project already has a ProjectReference to `SmartRouter.Core`; keep it; add the Cli reference next to it.)

3. Add `<Compile Include="StreamingTests.fs" />` to the `<ItemGroup>` BEFORE `<Compile Include="RouterTests.fs" />`. F# compile order matters: RouterTests.fs's `rootTests` list must be able to reference `SmartRouter.Tests.StreamingTests.tests`, so the streaming module must compile first.

Final fsproj `<ItemGroup>` order for `<Compile>`:
```
<Compile Include="RoutingTests.fs" />
<Compile Include="StreamingTests.fs" />
<Compile Include="RouterTests.fs" />
```

**`RouterTests.fs` updates:**

Append `SmartRouter.Tests.StreamingTests.tests` to the `rootTests` list. The current list is:
```fsharp
let rootTests : Test list =
    [
        SmartRouter.Tests.RoutingTests.tests
        // SmartRouter.Tests.IntegrationTests.tests   // <- added in later phases
    ]
```

Update to:
```fsharp
let rootTests : Test list =
    [
        SmartRouter.Tests.RoutingTests.tests
        SmartRouter.Tests.StreamingTests.tests
    ]
```

Remove the stale `IntegrationTests` placeholder comment if you want; not load-bearing.

DO NOT:
- Add `[<Tests>]` attributes anywhere — explicit registration only (PITFALL-26).
- Reorder Compile entries other than placing StreamingTests between RoutingTests and RouterTests.
- Pull in additional packages — `Microsoft.AspNetCore.Mvc.Testing 10.0.7` is already present; the FrameworkReference adds the platform APIs needed without adding a NuGet package.
  </action>
  <verify>
1. `dotnet build /Users/ohama/projs/smart-router/SmartRouter.slnx` exits 0 with zero warnings (the stub StreamingTests.fs created in this task makes the build green standalone — no atomic-with-Task-2 caveat).
2. `dotnet test /Users/ohama/projs/smart-router/tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — 22 Phase-1 tests pass, plus 0 streaming tests (empty list); total 22/22.
3. `grep -n "FrameworkReference Include=\"Microsoft.AspNetCore.App\"" tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — exactly one match.
4. `grep -n "ProjectReference Include=\".*SmartRouter.Cli.fsproj\"" tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — exactly one match.
5. `grep -n "StreamingTests.fs" tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — exactly one match, on a line before `RouterTests.fs`.
6. `grep -n "StreamingTests.tests" tests/SmartRouter.Tests/RouterTests.fs` — exactly one match in the rootTests list.
7. `grep -n "testSequenced" tests/SmartRouter.Tests/StreamingTests.fs` — exactly one match in the stub (sets the contract Task 2 will preserve).
  </verify>
  <done>
- fsproj has the new FrameworkReference, ProjectReference to Cli, and Compile entry for StreamingTests.fs in the correct position (between RoutingTests.fs and RouterTests.fs).
- RouterTests.fs's rootTests list contains `SmartRouter.Tests.StreamingTests.tests`.
- A stub `StreamingTests.fs` exists exporting `let tests : Test = testSequenced (testList "streaming" [])` so the build is green after Task 1 in isolation.
- `dotnet build` exits 0; Phase-1 22/22 tests still pass.
  </done>
</task>

<task type="auto">
  <name>Task 2: Author StreamingTests.fs covering TTFB, ordering, 100-chunk integrity, cancellation, [DONE] (forwarded + injected), headers, routing-before-streaming</name>
  <files>tests/SmartRouter.Tests/StreamingTests.fs</files>
  <action>
**Overwrite the Task 1 stub** at `tests/SmartRouter.Tests/StreamingTests.fs` (created with empty testList) with the real 8-test module. Keep the same module path (`module SmartRouter.Tests.StreamingTests`) and the same exported binding (`let tests : Test = testSequenced (testList "streaming" [ ... ])`) so `rootTests` in `RouterTests.fs` continues to compile.

Pattern 7 in `02-RESEARCH.md` is the authoritative skeleton — match it. Required structure:

**Helpers (private inside the module):**

1. `startFakeUpstream` — spins up a `WebApplication` with `WebHost.UseUrls("http://127.0.0.1:0")` (OS picks the port). Maps:
   - `GET /v1/models` → returns `{ "data": [ { "id": "/fake/model" } ] }` so the router's lazy probe in `QwenUpstreamClient` sees a path-like id and skips the HF-id trap.
   - `POST /v1/chat/completions` → emits N SSE chunks with optional inter-chunk delay; each chunk is `sprintf "data: {\"choices\":[{\"delta\":{\"content\":\"%d\"}}]}\n\n" i`; optionally appends `data: [DONE]\n\n`. The handler must `WriteAsync` + `FlushAsync` after each chunk; pass `ctx.RequestAborted` as `ct` so `Task.Delay(delayMs, ctx.RequestAborted)` cancels the upstream simulation when the router disconnects.
   - For the cancellation test, accept an optional `TaskCompletionSource<bool>` that the handler sets when `ctx.RequestAborted.IsCancellationRequested` becomes true (or via `ctx.RequestAborted.Register(fun () -> tcs.TrySetResult(true) |> ignore)`).
   Returns `(WebApplication, port: int)` after `app.StartAsync()` completes. Read the assigned port via `IServer` → `IServerAddressesFeature` → `Addresses |> Seq.head` → parse `:PORT` from the URL.

2. `startTestRouter` — builds the SmartRouter.Cli `WebApplication` in-process pointing at the fake upstream(s).

   **CRITICAL ordering — `AddInMemoryCollection` MUST run BEFORE `configureServices`:** Configuration overrides bind into `IOptions<UpstreamOptions>` at the moment `configureServices` registers the options binding. If the order is reversed, `UpstreamOptions` binds to the production defaults (`localhost:8000` / `localhost:8001`) and tests will connect to real models — every streaming test fails or hangs.

   Required step order:
   - **Step 1:** `let testBuilder = WebApplication.CreateBuilder()` + `testBuilder.WebHost.UseUrls("http://127.0.0.1:0")` (random port for the router under test).
   - **Step 2 (FIRST):** `testBuilder.Configuration.AddInMemoryCollection([ KeyValuePair("Upstreams:Model35B", $"http://127.0.0.1:{fakePort}"); KeyValuePair("Upstreams:Model122B", $"http://127.0.0.1:{fakePort}") ])` — point both upstreams at the same fake (the routing target picks 35B vs 122B but both resolve to the same fake server in tests).
   - **Step 3 (THEN):** `SmartRouter.Cli.CompositionRoot.configureServices(testBuilder.Services, testBuilder.Configuration)` — must run AFTER the in-memory overrides are added so `IOptions<UpstreamOptions>` binds to fake URLs. Read `CompositionRoot.fs` first to see the exact API; if it does not expose this function shape, replicate the registrations (Options binding for `UpstreamOptions`, named HttpClients with 300s timeout, `AddSingleton<IUpstreamClient, QwenUpstreamClient>()`, `AddSingleton<RoutingConfig>(...)`).
   - **Step 4:** `let app = testBuilder.Build()`, then `SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints app` to register the `POST /v1/chat/completions` route. Without this call the router WebApplication has zero routes and every test 404s.
   - **Step 5:** `app.StartAsync()`, then resolve the assigned router port the same way as the fake upstream.
   - Returns `(WebApplication, routerPort: int)`.

   Add a comment in the helper at the AddInMemoryCollection line: `// MUST happen before configureServices — overrides bind here`.

3. `readSseChunks` — given an `HttpResponseMessage`, opens a `StreamReader` over `response.Content.ReadAsStream()`, reads line-by-line with `ReadLineAsync(ct)`, collects `data: ...` lines into a `List<string>`, stops when it sees `data: [DONE]` or hits EOF. Returns `string list`.

4. `postStreamRequest` — `HttpClient` helper that sends `POST /v1/chat/completions` with `{"messages":[{"role":"user","content":"hi"}],"stream":true}` (or with a `task` field for the routing-before-streaming test). Use `client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)` so the test client also streams (otherwise it buffers and TTFB cannot be measured). Returns `HttpResponseMessage`.

**Tests (8 total, all inside `testList "streaming"` wrapped in `testSequenced`):**

Each test should follow the shape: `testCase "name" (fun () -> task { ... } |> Async.AwaitTask |> Async.RunSynchronously)` (or use Expecto's `testCaseAsync` / `testTask` if the user's Expecto version supports it — RoutingTests.fs Phase-1 patterns are the reference).

1. **`TTFB under 2 s`** (STRM-01, STRM-03): fake upstream emits 5 chunks at 100 ms spacing, `emitDone=true`. Test starts a `Stopwatch`; sends the request; reads the FIRST line via `ReadLineAsync`. Asserts `stopwatch.ElapsedMilliseconds < 2000`. (In practice TTFB will be ~100–200 ms; the 2 s threshold is the ROADMAP success criterion.)

2. **`chunks arrive in order`** (STRM-02): fake upstream emits 10 chunks `0..9`, no delay, `emitDone=true`. Test reads all chunks and asserts the contents arrive in order: chunk index `i` matches the `i`-th line received.

3. **`100-chunk integrity`** (STRM-02 + reliability): fake upstream emits 100 chunks `0..99`, no delay, `emitDone=true`. Test reads all chunks; asserts count == 100 (excluding `[DONE]`); asserts each line is byte-equal to the corresponding fake-upstream emission; asserts no duplicates and no missing indices.

4. **`mid-stream cancellation aborts upstream`** (STRM-05, STRM-06, PITFALL-4): fake upstream emits 100 chunks at 50 ms spacing, `emitDone=true`. Wire a `TaskCompletionSource<bool>` into the fake upstream so its `ctx.RequestAborted.Register` sets the flag. Test creates a `CancellationTokenSource`; reads 5 chunks; calls `cts.Cancel()`; awaits `tcs.Task` with a **5 s** timeout. (5s gives 20x headroom over the 5×50ms = 250ms emission window — protects against contended CI flakes; cancellation propagation in the happy path completes well under 1 s.) Asserts: (a) `tcs.Task.IsCompleted` is true within the timeout (proves STRM-05 — fake upstream's RequestAborted fired); (b) no `ObjectDisposedException` was logged (proves PITFALL-4 — `use _ = resp` keeps response alive). For (b), capture stderr via `Serilog` test sink or simply assert no exception escaped the request loop.

5. **`[DONE] forwarded when upstream emits it`** (STRM-07 forward-path): fake upstream emits 5 chunks + `data: [DONE]\n\n`. Test reads all chunks; asserts the LAST line received is `data: [DONE]` AND that exactly one `[DONE]` appears (no double-injection).

6. **`[DONE] injected when upstream omits it`** (STRM-07 inject-path / Strategy D): fake upstream emits 5 chunks WITHOUT `[DONE]`. Test reads all chunks; asserts the last line received is `data: [DONE]` and exactly one `[DONE]` appears (the router synthesized it).

7. **`response headers are SSE`** (STRM-04, PITFALL-6): fake upstream emits 1 chunk + `[DONE]`. Test inspects `response.Content.Headers.ContentType.MediaType` == `"text/event-stream"`. Inspects `response.Headers.GetValues("Cache-Control")` contains `"no-cache"`. Asserts `response.Content.Headers.ContentLength` is null OR not set (Kestrel uses chunked transfer encoding when streaming). Optionally inspects `response.Headers.GetValues("X-Accel-Buffering")` contains `"no"` if it was set on the response (defensive header from Plan 02-01).

8. **`routing error returns HTTP 400 not SSE`** (TEST-03 / order-of-operations): test sends `{"task":"foobar","stream":true,"messages":[...]}`. Asserts `response.StatusCode == 400`. Asserts `response.Content.Headers.ContentType.MediaType == "application/json"` (NOT `text/event-stream`). Asserts the JSON body has `error.message` containing `"foobar"` or `"unknown task"`. This proves the routing branch runs before any SSE header is set.

**Sequencing & teardown:**
- Wrap the full `testList "streaming"` in `testSequenced` (PITFALL-27): the tests start Kestrel servers on random ports — they MUST NOT run in parallel due to port contention and resource exhaustion under concurrent test runners.
- Each `testCase` must dispose both the fake upstream `WebApplication` and the router `WebApplication` in a `try/finally` (or `use` if the F# disposal pattern works cleanly). Otherwise a failed assertion leaks port bindings to subsequent tests.
- Use a generous per-test timeout (e.g., 30 s) so a hung test fails fast rather than blocking the runner.

**Module API surface:**
- `module SmartRouter.Tests.StreamingTests`
- `let tests : Test = testSequenced (testList "streaming" [ ... ])` — exported, referenced by `rootTests` in RouterTests.fs.

DO NOT:
- Use `TestServer` / `WebApplicationFactory` for the fake upstream — `02-RESEARCH.md` Pattern 7 explicitly rules that out: TestServer's in-process pipeline doesn't expose a real socket and SSE backpressure / FlushAsync timing differs from a real TCP connection. TTFB tests need real Kestrel.
- Run tests in parallel — `testSequenced` is mandatory.
- Add `[<Tests>]` attributes (explicit `rootTests` registration only — PITFALL-26).
- Leak `WebApplication` instances — every test must dispose both apps.
- Add log calls in the fake upstream chunk loop (matches OBS-04 production rule).
- Skip the routing-before-streaming test (#8) — that one explicitly proves the order-of-operations invariant from Plan 02-01.
  </action>
  <verify>
1. **Build clean:** `dotnet build /Users/ohama/projs/smart-router/SmartRouter.slnx` exits 0 with zero warnings (TreatWarningsAsErrors=true is on `SmartRouter.Cli` only; the test project's defaults still need a clean build).

2. **All tests run green:**
   ```
   dotnet test /Users/ohama/projs/smart-router/tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
   ```
   Expected output: at least 22 (Phase-1 RoutingTests) + 8 (Phase-2 StreamingTests) = 30 tests pass; 0 failed; 0 errored; 0 ignored.

3. **Pitfall-mitigation grep map (each line below must return ≥1 match):**
   - `grep -n "TTFB\\|< *2000" tests/SmartRouter.Tests/StreamingTests.fs` (STRM-01 TTFB threshold)
   - `grep -n "ReadLineAsync\\|chunks arrive in order" tests/SmartRouter.Tests/StreamingTests.fs` (STRM-02)
   - `grep -n "100" tests/SmartRouter.Tests/StreamingTests.fs` (100-chunk integrity test)
   - `grep -n "RequestAborted" tests/SmartRouter.Tests/StreamingTests.fs` (STRM-05 — cancellation hook on fake upstream)
   - `grep -nE 'data: \\[DONE\\]|\\[DONE\\]' tests/SmartRouter.Tests/StreamingTests.fs` (STRM-07 — both forwarded and injected paths)
   - `grep -n "text/event-stream" tests/SmartRouter.Tests/StreamingTests.fs` (STRM-04 header assertion)
   - `grep -n "testSequenced" tests/SmartRouter.Tests/StreamingTests.fs` (PITFALL-27 — sequenced tests)
   - `grep -n "127\\.0\\.0\\.1:0\\|UseUrls" tests/SmartRouter.Tests/StreamingTests.fs` (real Kestrel on OS-assigned port, not TestServer)
   - `grep -n "mapEndpoints" tests/SmartRouter.Tests/StreamingTests.fs` — must return ≥1 match. Without `ChatCompletions.mapEndpoints app` the router WebApplication has no routes registered and ALL 8 streaming tests silently 404.

4. **Optional manual TTFB sanity:** with the test running, capture the elapsed times in stderr (Serilog) for visual confirmation TTFB is in the 100–500 ms range, not the 2 s ceiling.
  </verify>
  <done>
- `tests/SmartRouter.Tests/StreamingTests.fs` exists, exports `let tests : Test`, contains 8 testCases inside one `testList "streaming"` wrapped in `testSequenced`.
- All 8 tests pass against the Plan 02-01 streaming code with a real Kestrel fake upstream on `127.0.0.1:0`.
- Each of the 5 SSE pitfalls has at least one dedicated test that fails if its mitigation is removed:
  - PITFALL-2 / STRM-01 → TTFB test (would explode past 2 s if upstream buffered).
  - PITFALL-3 / STRM-03 → TTFB test + chunk ordering (would clump if FlushAsync is missing).
  - PITFALL-4 / STRM-06 → mid-stream cancellation test (would log ObjectDisposedException if `use _ = resp` is missing).
  - PITFALL-6 / STRM-04 → header assertion test (would not see `text/event-stream` if headers set after first write).
  - PITFALL-19 / STRM-07 → both [DONE]-forwarded AND [DONE]-injected tests.
- TEST-03 satisfied: chunk ordering, mid-stream cancellation, [DONE] propagation tests all pass; routing-error-before-streaming test pins the order-of-operations invariant.
  </done>
</task>

</tasks>

<verification>
**Build + full test run:**
```
dotnet build /Users/ohama/projs/smart-router/SmartRouter.slnx
# expect: 0 warnings, 0 errors

dotnet test /Users/ohama/projs/smart-router/tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
# expect: 30+ tests pass (22 routing + 8 streaming), 0 failed, 0 errored, 0 ignored
```

**Pitfall-mitigation map (each pitfall → specific test):**
- PITFALL-2 / STRM-01 (ResponseHeadersRead): test 1 (TTFB under 2 s) — fails fast if upstream buffers the body
- PITFALL-3 / STRM-03 (FlushAsync per chunk): test 1 + test 2 — chunks would batch without per-chunk flush
- PITFALL-4 / STRM-06 (HttpResponseMessage disposal scope): test 4 (mid-stream cancellation) — fails with ObjectDisposedException if `use _ = resp` is missing
- PITFALL-6 / STRM-04 (SSE headers before first write): test 7 (header assertion)
- PITFALL-19 / STRM-07 ([DONE] sentinel): test 5 (forwarded) + test 6 (injected) — covers both Strategy D paths
- STRM-02 (chunk ordering, never split events): test 2 + test 3 (10 + 100 chunks in order)
- STRM-05 (client disconnect aborts upstream): test 4 — asserts fake upstream's RequestAborted hook fires within 5 s of test client cancel (20x headroom over 250 ms emission window)

**Phase 2 atomic-cluster confirmation:** With both Plan 02-01 (streaming impl) and Plan 02-02 (streaming tests) complete, all 5 SSE pitfalls have BOTH a code-level mitigation AND a test that proves the mitigation works. Phase 2 ships as one atomic correctness unit.
</verification>

<success_criteria>
- 8 streaming tests exist and pass against the Plan 02-01 implementation.
- The 5 SSE pitfalls each have at least one test that would fail if the mitigation regressed.
- TEST-03 fully satisfied: chunk ordering verified, mid-stream cancellation verified, [DONE] propagation verified (both forwarded and injected paths).
- `testSequenced` wraps the whole streaming testList; tests use real Kestrel on `127.0.0.1:0`, not TestServer.
- Total test count ≥ 30 (22 Phase-1 + 8 Phase-2 streaming); 0 failed; 0 errored.
</success_criteria>

<output>
After completion, create `.planning/phases/02-sse-streaming-pass-through/02-02-SUMMARY.md` documenting:
- Final test list with what each test asserts and which REQ-ID / pitfall it pins.
- Fake upstream architecture (Kestrel on `127.0.0.1:0`, `/v1/models` + `/v1/chat/completions` mapped, RequestAborted hook for cancellation test).
- Test runtime (per-test approximate; total streaming testList runtime).
- Any deviations from `02-RESEARCH.md` Pattern 7 with rationale.
- Pre-Phase-3 readiness: Phase 2 exits when this plan is green; Phase 3 (concurrency) can begin.
</output>
