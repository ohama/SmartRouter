---
phase: 11-deployment-documentation
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/SmartRouter.Cli/Endpoints/Models.fs
  - src/SmartRouter.Cli/Program.fs
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - tests/SmartRouter.Tests/ModelsTests.fs
  - tests/SmartRouter.Tests/RouterTests.fs
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
autonomous: true

must_haves:
  truths:
    - "GET /v1/models with both upstreams up returns 200 + a deduplicated data array containing entries from both upstreams (first-seen wins on duplicate id)"
    - "GET /v1/models with one upstream down returns 200 + entries from the reachable upstream only"
    - "GET /v1/models with both upstreams down returns 200 + {\"object\":\"list\",\"data\":[]} (NOT 503)"
    - "Models.fs reuses the existing 'health-probe' named HttpClient — no 12th named client added"
    - "Models.fs calls IHealthProbe.IsReachable BEFORE issuing the HTTP GET so that known-down upstreams short-circuit and do not burn a 5s timeout"
    - "Every JsonElement returned across the JsonDocument 'use doc' boundary is .Clone()-d to extend its lifetime"
    - "RouterTests.rootTests includes ModelsTests.tests; ModelsTests.fs is compiled BEFORE RouterTests.fs in SmartRouter.Tests.fsproj (Expecto auto-discovery is forbidden — PITFALL-26)"
    - "Test count after this plan: 86 pass + 17 ignored without embeddings (was 83+17); 93 + 10 with embeddings (was 90+10)"
    - "Pure-Core invariant preserved (ARCH-01): src/SmartRouter.Core/ has zero new references; no new domain types, no new ports"
  artifacts:
    - path: "src/SmartRouter.Cli/Endpoints/Models.fs"
      provides: "GET /v1/models endpoint — parallel fetch + IHealthProbe gating + dedupe + JsonElement.Clone() lifetime guard"
      min_lines: 60
      contains: ["mapEndpoints", "/v1/models", "health-probe", "IsReachable", ".Clone()", "HashSet"]
    - path: "src/SmartRouter.Cli/Program.fs"
      provides: "Endpoints.Models.mapEndpoints app registration line"
      contains: ["SmartRouter.Cli.Endpoints.Models.mapEndpoints app"]
    - path: "src/SmartRouter.Cli/SmartRouter.Cli.fsproj"
      provides: "Compile entry for Endpoints/Models.fs"
      contains: ["Endpoints/Models.fs"]
    - path: "tests/SmartRouter.Tests/ModelsTests.fs"
      provides: "3 integration tests for /v1/models — both-up+dedupe, one-down, both-down"
      min_lines: 120
      contains: ["module SmartRouter.Tests.ModelsTests", "let tests", "/v1/models", "startFakeUpstream"]
    - path: "tests/SmartRouter.Tests/RouterTests.fs"
      provides: "rootTests includes ModelsTests.tests"
      contains: ["SmartRouter.Tests.ModelsTests.tests"]
    - path: "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
      provides: "ModelsTests.fs compiled before RouterTests.fs"
      contains: ["ModelsTests.fs"]
  key_links:
    - from: "src/SmartRouter.Cli/Endpoints/Models.fs"
      to: "IHealthProbe.IsReachable"
      via: "ctx.RequestServices.GetRequiredService<IHealthProbe>()"
      pattern: "probe\\.IsReachable\\(Qwen35B\\).*probe\\.IsReachable\\(Qwen122B\\)"
    - from: "src/SmartRouter.Cli/Endpoints/Models.fs"
      to: "health-probe named HttpClient"
      via: "IHttpClientFactory.CreateClient"
      pattern: "CreateClient\\(\"health-probe\"\\)"
    - from: "src/SmartRouter.Cli/Endpoints/Models.fs"
      to: "UpstreamOptions"
      via: "IOptions<UpstreamOptions>.Value"
      pattern: "IOptions<UpstreamOptions>"
    - from: "src/SmartRouter.Cli/Program.fs"
      to: "Endpoints/Models.fs"
      via: "Endpoints.Models.mapEndpoints app"
      pattern: "Endpoints\\.Models\\.mapEndpoints"
    - from: "tests/SmartRouter.Tests/RouterTests.fs"
      to: "ModelsTests.fs"
      via: "rootTests list append"
      pattern: "SmartRouter\\.Tests\\.ModelsTests\\.tests"
---

<objective>
Implement GET /v1/models — the single missing endpoint required for clients to discover both upstream Qwen models through the smart-router gateway. The endpoint fetches `/v1/models` from each upstream in parallel, gates each fetch on `IHealthProbe.IsReachable` to avoid burning a 5s timeout on a known-down upstream, deduplicates entries by `id` (first-seen wins), and returns 200 even when both upstreams are down (graceful degradation: empty data array).

Purpose: Unblocks ROADMAP success criterion #3 (deduplicated /v1/models response from both upstreams). Also unblocks Phase 11-02 (plist) and 11-03 (README) which both reference this endpoint. With this endpoint shipped, smart-router exposes the OpenAI model-list contract that downstream clients (Hermes, Graphify, generic OpenAI tooling) can introspect.

Output:
- `src/SmartRouter.Cli/Endpoints/Models.fs` (NEW) — the endpoint implementation
- `tests/SmartRouter.Tests/ModelsTests.fs` (NEW) — 3 integration tests
- 1-line edits to `Program.fs`, `SmartRouter.Cli.fsproj`, `RouterTests.fs`, `SmartRouter.Tests.fsproj`
- Test count delta: +3 (83 → 86 without embeddings; 90 → 93 with embeddings)
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md
@.planning/phases/11-deployment-documentation/11-CONTEXT.md
@.planning/phases/11-deployment-documentation/11-RESEARCH.md

# Endpoint shape to mirror (one MapGet, task { ... }, ctx.Response.WriteAsJsonAsync)
@src/SmartRouter.Cli/Endpoints/Health.fs

# Endpoint registration sequence in Program.fs (where to insert the new mapEndpoints call)
@src/SmartRouter.Cli/Program.fs

# Existing JSON-parsing machinery for upstream /v1/models — reuse the pattern (TryGetProperty("data"), iterate, GetString())
@src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs

# UpstreamOptions type definition (Model35B, Model122B fields) lives at the top of QwenUpstreamClient.fs
# (see context above)

# IHealthProbe interface (IsReachable: ModelId -> bool) and ModelId DU
@src/SmartRouter.Core/Domain.fs
@src/SmartRouter.Core/Ports.fs

# Existing fake-upstream test pattern — reuse startFakeUpstream + startApp helpers shape
@tests/SmartRouter.Tests/HealthFallbackTests.fs

# Test runner — rootTests list (PITFALL-26: Expecto auto-discovery forbidden)
@tests/SmartRouter.Tests/RouterTests.fs
</context>

<tasks>

<task type="auto">
  <name>Task 1: Create Endpoints/Models.fs and wire it into Program.fs + SmartRouter.Cli.fsproj</name>
  <files>
    src/SmartRouter.Cli/Endpoints/Models.fs
    src/SmartRouter.Cli/Program.fs
    src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  </files>
  <action>
**Step 1.1 — Create `src/SmartRouter.Cli/Endpoints/Models.fs`** mirroring the
shape of `Endpoints/Health.fs`. Module declaration, `open` block, single
private helper, single `let mapEndpoints`. Use the implementation below
verbatim (it has been validated against the existing Health.fs +
QwenUpstreamClient.fs patterns):

```fsharp
module SmartRouter.Cli.Endpoints.Models

open System
open System.Collections.Generic
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports
open SmartRouter.Cli.Adapters.Json                  // jsonOptions
open SmartRouter.Cli.Adapters.QwenUpstreamClient    // UpstreamOptions

/// Fetch /v1/models from one upstream and return the parsed model entries.
/// Returns [] on any failure (HTTP non-success, network error, malformed JSON).
///
/// IMPORTANT — JsonElement.Clone() is mandatory:
/// `use doc = JsonDocument.Parse(json)` owns the underlying memory for every
/// JsonElement extracted from `doc.RootElement`. When `use doc` goes out of
/// scope (function returns), those elements become INVALID and any later
/// access produces undefined behavior (silent data corruption — not an
/// exception). Calling `.Clone()` copies each element into independently-
/// owned memory that survives doc disposal. Without this, the aggregation
/// loop below dereferences freed memory.
let private fetchModels
    (client: HttpClient)
    (baseUrl: string)
    (ct: CancellationToken)
    : Task<JsonElement list> =
    task {
        try
            let url = baseUrl.TrimEnd('/') + "/v1/models"
            use! resp = client.GetAsync(url, ct)
            if not resp.IsSuccessStatusCode then
                return []
            else
                let! json = resp.Content.ReadAsStringAsync(ct)
                use doc = JsonDocument.Parse(json)
                match doc.RootElement.TryGetProperty("data") with
                | true, data when data.ValueKind = JsonValueKind.Array ->
                    let acc = ResizeArray<JsonElement>()
                    for i in 0 .. data.GetArrayLength() - 1 do
                        let entry = data.[i]
                        match entry.TryGetProperty("id") with
                        | true, idEl when idEl.ValueKind = JsonValueKind.String ->
                            let id = idEl.GetString()
                            if not (String.IsNullOrEmpty id) then
                                // Clone() — see comment above. NON-NEGOTIABLE.
                                acc.Add(entry.Clone())
                        | _ -> ()
                    return List.ofSeq acc
                | _ -> return []
        with _ -> return []
    }

let mapEndpoints (app: WebApplication) =
    app.MapGet(
        "/v1/models",
        Func<HttpContext, Task>(fun ctx ->
            task {
                let probe   = ctx.RequestServices.GetRequiredService<IHealthProbe>()
                let opts    = ctx.RequestServices.GetRequiredService<IOptions<UpstreamOptions>>().Value
                let factory = ctx.RequestServices.GetRequiredService<IHttpClientFactory>()
                // L13 — reuse the existing "health-probe" named client (5s timeout, no retry,
                // no BaseAddress). DO NOT add a 12th named client.
                let client  = factory.CreateClient("health-probe")
                let ct      = ctx.RequestAborted

                // L11 + research §2 — IsReachable is a sync fast-path. If an upstream is
                // already known-down, skip its fetch entirely (don't burn the 5s timeout).
                let task35  =
                    if probe.IsReachable(Qwen35B)  then fetchModels client opts.Model35B  ct
                    else Task.FromResult []
                let task122 =
                    if probe.IsReachable(Qwen122B) then fetchModels client opts.Model122B ct
                    else Task.FromResult []

                let! both = Task.WhenAll([| task35; task122 |])
                let combined = (both.[0] @ both.[1])

                // L12 — dedupe by id; first-seen wins.
                let seen = HashSet<string>()
                let deduped =
                    [ for entry in combined do
                          match entry.TryGetProperty("id") with
                          | true, idEl when idEl.ValueKind = JsonValueKind.String ->
                              let id = idEl.GetString()
                              if not (String.IsNullOrEmpty id) && seen.Add(id) then
                                  yield entry
                          | _ -> () ]

                // L11 — both-down case: 200 + empty data array (NOT 503).
                let body = {| ``object`` = "list"; data = deduped |}
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsJsonAsync(body, jsonOptions, ct)
            } :> Task)) |> ignore
```

Locked references: L11–L14 from `11-CONTEXT.md`. The `.Clone()` call is
NON-NEGOTIABLE; without it the JsonElements held in `acc` after `use doc`
goes out of scope are dangling references into freed JsonDocument memory.

**Step 1.2 — Edit `src/SmartRouter.Cli/SmartRouter.Cli.fsproj`** to add the
new file. Add the line below as a sibling under the existing
`<Compile Include="Endpoints/...">` block, AFTER `Endpoints/Health.fs` and
BEFORE `CompositionRoot.fs` / `Program.fs` (compile order matters in F#):

```xml
    <Compile Include="Endpoints/Models.fs" />
```

The current order is: ChatCompletions.fs → Stats.fs → Canary.fs → Health.fs.
Insert Models.fs AFTER Health.fs (last endpoint, before CompositionRoot.fs).

**Step 1.3 — Edit `src/SmartRouter.Cli/Program.fs`** to register the new
endpoint. Find the existing block of `mapEndpoints` calls (look for
`SmartRouter.Cli.Endpoints.Health.mapEndpoints app` around line 203).
Add ONE new line immediately after it, with the same indentation:

```fsharp
            SmartRouter.Cli.Endpoints.Models.mapEndpoints app
```

NO other changes to Program.fs. Do not modify CompositionRoot.fs — Models.fs
needs ZERO new DI registrations (everything it consumes — `IHealthProbe`,
`IOptions<UpstreamOptions>`, `IHttpClientFactory` with the `health-probe`
client — was already wired by Phase 1 + Phase 10).

**Step 1.4 — Build the solution** to verify the new endpoint compiles
cleanly under `TreatWarningsAsErrors=true`:

```bash
cd /Users/ohama/projs/smart-router && dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -30
```

Expected: `Build succeeded` with 0 errors. If the build fails:
- FS0039 "value or namespace not defined" → check `open` order; `Models.fs`
  needs `open SmartRouter.Cli.Adapters.QwenUpstreamClient` for
  `UpstreamOptions`.
- FS3261 / nullness warning → keep the `String.IsNullOrEmpty id` guard
  exactly as written; it absorbs nullable JsonElement.GetString() returns.
- FS0708 "the type ... not defined" on `IHealthProbe` → check
  `open SmartRouter.Core.Ports` is present.
- FS0046 "type ... has been used in an invalid way" on `Func<HttpContext, Task>`
  → ensure `open System.Threading.Tasks` is at the top.

**Step 1.5 — Sanity-check the route is reachable in a quick smoke test
(optional but recommended)** using an instance with both upstreams down:

```bash
cd /Users/ohama/projs/smart-router && (cd src/SmartRouter.Cli && dotnet run --no-build &) ; \
  sleep 4 && curl -s http://127.0.0.1:4000/v1/models && \
  pkill -f "dotnet run" 2>/dev/null || true
```

Expected JSON body: `{"object":"list","data":[]}` (200 status).

If both qwen upstreams are LIVE (running on operator host), the body will
contain real model entries — that is also acceptable. The integration tests
in Task 2 are the canonical correctness check.
  </action>
  <verify>
```bash
# Endpoint file exists and contains the locked elements
test -f src/SmartRouter.Cli/Endpoints/Models.fs
grep -q 'module SmartRouter.Cli.Endpoints.Models' src/SmartRouter.Cli/Endpoints/Models.fs
grep -q '"/v1/models"' src/SmartRouter.Cli/Endpoints/Models.fs
grep -q 'CreateClient("health-probe")' src/SmartRouter.Cli/Endpoints/Models.fs
grep -q 'IsReachable(Qwen35B)' src/SmartRouter.Cli/Endpoints/Models.fs
grep -q 'IsReachable(Qwen122B)' src/SmartRouter.Cli/Endpoints/Models.fs
grep -q 'entry.Clone()' src/SmartRouter.Cli/Endpoints/Models.fs
grep -q 'HashSet<string>' src/SmartRouter.Cli/Endpoints/Models.fs
grep -q '``object`` = "list"' src/SmartRouter.Cli/Endpoints/Models.fs

# fsproj registers the file, in correct order (after Health.fs, before Program.fs)
grep -q 'Endpoints/Models.fs' src/SmartRouter.Cli/SmartRouter.Cli.fsproj
awk '/Endpoints\/Health\.fs/{h=NR} /Endpoints\/Models\.fs/{m=NR} /Program\.fs/{p=NR} END{exit !(h<m && m<p)}' src/SmartRouter.Cli/SmartRouter.Cli.fsproj

# Program.fs registers the endpoint
grep -q 'SmartRouter.Cli.Endpoints.Models.mapEndpoints' src/SmartRouter.Cli/Program.fs

# Pure-Core invariant — no Core changes
git diff --stat src/SmartRouter.Core/ | grep -q '0 insertions' || \
  ! git diff --name-only src/SmartRouter.Core/ | grep -q '\.fs$'

# Build clean
cd /Users/ohama/projs/smart-router && dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3 | grep -q 'Build succeeded'
```
  </verify>
  <done>
- `src/SmartRouter.Cli/Endpoints/Models.fs` exists, ≥60 lines, contains all
  locked elements (`/v1/models`, `health-probe`, `IsReachable(Qwen35B)`,
  `IsReachable(Qwen122B)`, `entry.Clone()`, `HashSet<string>`,
  ``object`` = "list"`).
- `SmartRouter.Cli.fsproj` includes `Endpoints/Models.fs` after Health.fs and
  before Program.fs.
- `Program.fs` registers the endpoint via `Endpoints.Models.mapEndpoints app`.
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` succeeds.
- `src/SmartRouter.Core/` has zero new lines (pure-Core invariant preserved).
  </done>
</task>

<task type="auto">
  <name>Task 2: Create ModelsTests.fs and wire it into RouterTests.rootTests + SmartRouter.Tests.fsproj</name>
  <files>
    tests/SmartRouter.Tests/ModelsTests.fs
    tests/SmartRouter.Tests/RouterTests.fs
    tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
  </files>
  <action>
**Step 2.1 — Create `tests/SmartRouter.Tests/ModelsTests.fs`** with three
Expecto integration tests covering the L11/L12 contract.

Approach (mirrors `HealthFallbackTests.fs` exactly — same `startFakeUpstream`
helper pattern, same `startApp`-style harness with full router DI):

- Each test spins up two fake Kestrel upstreams on `127.0.0.1:0` (or one
  fake + one dead port for the one-down case; or two dead ports for the
  both-down case).
- Each fake upstream's `GET /v1/models` handler returns the JSON specified
  by the test.
- A test harness boots the full router app via
  `SmartRouter.Cli.CompositionRoot.configureServices` + `app.Build()`, with
  upstream URLs injected through `AddInMemoryCollection`. Health probing is
  set to a fast interval (1s) and the test waits long enough for the probe
  to mark dead-port upstreams as unreachable BEFORE issuing the
  `/v1/models` request. Alternatively, override `IHealthProbe` with a stub
  in DI (cleaner; preferred).
- Each test then issues `GET http://127.0.0.1:{routerPort}/v1/models` and
  asserts on the parsed JSON body.

**Recommended approach for IHealthProbe override**: rather than wait for the
real `HealthService` to converge, register a stub `IHealthProbe` AFTER
`configureServices` is called. Use `services.AddSingleton<IHealthProbe>(stub)`
— since DI uses last-registration-wins, this overrides the
`HealthService` registration for the test process. Pattern shown in test
code below.

Use this implementation as the starting point — it is aligned with the
HealthFallbackTests harness and follows the same dispose/cleanup discipline.
You may simplify or restructure if needed, but the three test scenarios and
the assertions on each are LOCKED:

```fsharp
module SmartRouter.Tests.ModelsTests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Expecto
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports

// ── Fake upstream helper (mirrors HealthFallbackTests.startFakeUpstream) ──
let private startFakeUpstream (respond: HttpContext -> Task<unit>) : int * IDisposable =
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
    builder.Logging.ClearProviders() |> ignore
    let app = builder.Build()
    app.Run(RequestDelegate(fun ctx -> task { do! respond ctx } :> Task)) |> ignore
    app.StartAsync().GetAwaiter().GetResult()
    let port =
        app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>().Addresses
        |> Seq.head
        |> fun a -> a.Split(':') |> Array.last |> int
    let dispose =
        { new IDisposable with
            member _.Dispose() = app.StopAsync().GetAwaiter().GetResult() }
    port, dispose

/// Acquire a port that is guaranteed unused (TcpListener bind+release pattern from HLTH-03).
let private acquireDeadPort () : int =
    let listener = TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

/// Models.fs reads IsReachable BEFORE issuing the HTTP GET. To make tests
/// deterministic without waiting for the real HealthService probe cycle to
/// converge, we replace IHealthProbe with a stub keyed on a per-target dict.
type private StubHealthProbe(reachable35: bool, reachable122: bool) =
    interface IHealthProbe with
        member _.IsReachableAsync(target, ct) =
            match target with
            | Qwen35B  -> Task.FromResult reachable35
            | Qwen122B -> Task.FromResult reachable122
        member _.IsReachable(target) =
            match target with
            | Qwen35B  -> reachable35
            | Qwen122B -> reachable122
        member _.LastProbedAt(_target) = DateTimeOffset.UtcNow

/// Build the full router app pointed at the given upstream URLs, with the
/// stub IHealthProbe overriding HealthService.
let private startApp
    (model35bUrl: string)
    (model122bUrl: string)
    (reachable35: bool)
    (reachable122: bool)
    : HttpClient * IDisposable * string =

    let tempBase = Path.Combine(Path.GetTempPath(), "smart-router-models-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    Directory.CreateDirectory(tempBase) |> ignore
    let logsDir = Path.Combine(tempBase, "logs", "decisions")
    Directory.CreateDirectory(logsDir) |> ignore

    let testBuilder = WebApplication.CreateBuilder()
    testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore

    (testBuilder.Configuration :> IConfigurationBuilder)
        .AddInMemoryCollection([
            KeyValuePair("Upstreams:Model35B",  model35bUrl)
            KeyValuePair("Upstreams:Model122B", model122bUrl)
            KeyValuePair("Routing:Algorithm",            "heuristic")
            KeyValuePair("Routing:ComplexityThreshold", "3")
            KeyValuePair("Routing:TimeoutSeconds",       "300")
            KeyValuePair("Routing:Keywords:0",           "recursive")
            KeyValuePair("Routing:TaskTable:graph_indexing:Model",           "122b")
            KeyValuePair("Routing:TaskTable:graph_indexing:Priority",        "high")
            KeyValuePair("Routing:TaskTable:compiler_debug:Model",           "122b")
            KeyValuePair("Routing:TaskTable:compiler_debug:Priority",        "high")
            KeyValuePair("Routing:TaskTable:architecture_analysis:Model",    "122b")
            KeyValuePair("Routing:TaskTable:architecture_analysis:Priority", "high")
            KeyValuePair("Routing:TaskTable:dependency_analysis:Model",      "122b")
            KeyValuePair("Routing:TaskTable:dependency_analysis:Priority",   "low")
            KeyValuePair("Routing:TaskTable:reasoning:Model",                "122b")
            KeyValuePair("Routing:TaskTable:reasoning:Priority",             "low")
            KeyValuePair("Routing:TaskTable:retrieval:Model",                "35b")
            KeyValuePair("Routing:TaskTable:retrieval:Priority",             "low")
            KeyValuePair("Routing:TaskTable:summary:Model",                  "35b")
            KeyValuePair("Routing:TaskTable:summary:Priority",               "low")
            KeyValuePair("Routing:ModelAliases:35b",       "Qwen35B")
            KeyValuePair("Routing:ModelAliases:122b",      "Qwen122B")
            KeyValuePair("Queue:FairnessK",                "10")
            KeyValuePair("Queue:MaxConcurrent122B",        "1")
            KeyValuePair("Queue:PerRequestTimeoutSeconds", "60")
            KeyValuePair("DecisionLog:Directory",       logsDir)
            KeyValuePair("DecisionLog:ChannelCapacity", "1000")
            KeyValuePair("Routing:Health:PollingIntervalSeconds",      "60")  // slow — stub overrides anyway
            KeyValuePair("Routing:Health:ConsecutiveFailureThreshold", "1")
        ])
    |> ignore

    SmartRouter.Cli.CompositionRoot.configureServices
        testBuilder.Services
        testBuilder.Configuration
    |> ignore

    // Override IHealthProbe with stub AFTER configureServices (last-registration-wins).
    testBuilder.Services.AddSingleton<IHealthProbe>(StubHealthProbe(reachable35, reachable122) :> IHealthProbe) |> ignore

    let app = testBuilder.Build()

    SmartRouter.Cli.Endpoints.Models.mapEndpoints app

    app.StartAsync().GetAwaiter().GetResult()

    let routerPort =
        app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>().Addresses
        |> Seq.head
        |> fun a -> a.Split(':') |> Array.last |> int

    let httpClient = new HttpClient()
    let dispose =
        { new IDisposable with
            member _.Dispose() =
                httpClient.Dispose()
                app.StopAsync().GetAwaiter().GetResult()
                try Directory.Delete(tempBase, true) with _ -> () }
    httpClient, dispose, sprintf "http://127.0.0.1:%d" routerPort

let private modelsHandler (json: string) : HttpContext -> Task<unit> =
    fun ctx -> task {
        if ctx.Request.Path.Value.EndsWith("/v1/models") then
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsync(json)
        else
            ctx.Response.StatusCode <- 404
    }

let tests =
    testList "models endpoint" [

        testCase "MODELS-01: both upstreams up — deduplicated merge" <| fun () ->
            let json35  = """{"object":"list","data":[{"id":"model-shared","object":"model","created":1715000000,"owned_by":"local"},{"id":"model-35b-only","object":"model","created":1715000000,"owned_by":"local"}]}"""
            let json122 = """{"object":"list","data":[{"id":"model-shared","object":"model","created":1715000000,"owned_by":"local"},{"id":"model-122b-only","object":"model","created":1715000000,"owned_by":"local"}]}"""
            let port35,  d35  = startFakeUpstream (modelsHandler json35)
            let port122, d122 = startFakeUpstream (modelsHandler json122)
            try
                let client, da, baseUrl = startApp (sprintf "http://127.0.0.1:%d" port35) (sprintf "http://127.0.0.1:%d" port122) true true
                try
                    let resp = client.GetAsync(baseUrl + "/v1/models").GetAwaiter().GetResult()
                    Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK"
                    let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    use doc = JsonDocument.Parse(body)
                    Expect.equal (doc.RootElement.GetProperty("object").GetString()) "list" "object=list"
                    let data = doc.RootElement.GetProperty("data")
                    Expect.equal data.GetArrayLength() 3 "3 unique entries (model-shared deduped)"
                    let ids = [ for i in 0 .. data.GetArrayLength() - 1 -> data.[i].GetProperty("id").GetString() ]
                    Expect.contains ids "model-shared"      "shared id present"
                    Expect.contains ids "model-35b-only"    "35b-only id present"
                    Expect.contains ids "model-122b-only"   "122b-only id present"
                finally da.Dispose()
            finally d35.Dispose() ; d122.Dispose()

        testCase "MODELS-02: one upstream down — returns reachable upstream's models" <| fun () ->
            let json122 = """{"object":"list","data":[{"id":"model-122b","object":"model","created":1715000000,"owned_by":"local"}]}"""
            let deadPort35 = acquireDeadPort()
            let port122, d122 = startFakeUpstream (modelsHandler json122)
            try
                // 35B is unreachable per stub — Models.fs short-circuits and never connects to deadPort35.
                let client, da, baseUrl = startApp (sprintf "http://127.0.0.1:%d" deadPort35) (sprintf "http://127.0.0.1:%d" port122) false true
                try
                    let resp = client.GetAsync(baseUrl + "/v1/models").GetAwaiter().GetResult()
                    Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK"
                    let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    use doc = JsonDocument.Parse(body)
                    let data = doc.RootElement.GetProperty("data")
                    Expect.equal data.GetArrayLength() 1 "only 122b's model returned"
                    Expect.equal (data.[0].GetProperty("id").GetString()) "model-122b" "id matches"
                finally da.Dispose()
            finally d122.Dispose()

        testCase "MODELS-03: both upstreams down — 200 + empty data array" <| fun () ->
            let dead35  = acquireDeadPort()
            let dead122 = acquireDeadPort()
            // Both unreachable per stub.
            let client, da, baseUrl = startApp (sprintf "http://127.0.0.1:%d" dead35) (sprintf "http://127.0.0.1:%d" dead122) false false
            try
                let resp = client.GetAsync(baseUrl + "/v1/models").GetAwaiter().GetResult()
                Expect.equal resp.StatusCode HttpStatusCode.OK "200 (NOT 503) — graceful degradation"
                let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                use doc = JsonDocument.Parse(body)
                Expect.equal (doc.RootElement.GetProperty("object").GetString()) "list" "object=list"
                let data = doc.RootElement.GetProperty("data")
                Expect.equal data.GetArrayLength() 0 "empty data array"
            finally da.Dispose()
    ]
```

Notes for the implementer:
- The `StubHealthProbe` pattern is the cleanest way to make these tests
  deterministic without depending on probe-cycle convergence timing. It
  replaces the real `HealthService`-backed probe registration via
  last-wins DI.
- `acquireDeadPort` is the same pattern HLTH-03 uses (HealthFallbackTests.fs
  Phase 10 lock); it guarantees an unused port without TIME_WAIT issues.
- If `IHealthProbe`'s exact signature differs (extra abstract members, etc.)
  — match the real definition in `src/SmartRouter.Core/Ports.fs`. The stub
  must implement EVERY abstract member.
- If `Microsoft.Extensions.Logging.LoggingBuilder.ClearProviders()` causes a
  resolution issue, drop the `builder.Logging.ClearProviders()` line — it is
  cosmetic (suppresses Kestrel startup logs in test output).

**Step 2.2 — Edit `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj`** to
register the new file. Add the line below as a sibling of the other
`<Compile Include="...">` entries, AFTER `HealthFallbackTests.fs` and
BEFORE `RouterTests.fs` (so `RouterTests.rootTests` can reference it):

```xml
    <!-- Phase 11 -->
    <Compile Include="ModelsTests.fs" />
```

**Step 2.3 — Edit `tests/SmartRouter.Tests/RouterTests.fs`** to append the
new test list to `rootTests`. The current `rootTests` is a `let rootTests : Test list = [ ... ]`
that ends with `SmartRouter.Tests.HealthFallbackTests.tests`. Add ONE new
line BEFORE the closing `]`:

```fsharp
        SmartRouter.Tests.ModelsTests.tests   // Phase 11
```

The exact whitespace and trailing comma should match the surrounding entries
(check the existing file; entries are typically separated by trailing
newlines without commas in F# list literals — follow whatever pattern is
already there).

**Step 2.4 — Run the test suite** to confirm all three new tests pass and
no existing tests regressed:

```bash
cd /Users/ohama/projs/smart-router && dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj 2>&1 | tail -20
```

Expected counts:
- Without embedding models present: **86 passed, 17 ignored, 0 failed**
  (was 83+17 — net +3 from this plan).
- With embedding models present: **93 passed, 10 ignored, 0 failed**
  (was 90+10 — net +3 from this plan).

If a test fails:
- MODELS-01 fails with `data.GetArrayLength() = 4` instead of 3 → dedupe
  isn't working; check the `seen.Add(id)` guard in Models.fs.
- MODELS-02 fails with timeout/exception → IHealthProbe stub isn't being
  registered after `configureServices`; check the `AddSingleton` order in
  `startApp`.
- MODELS-03 fails with status 503 → graceful-degradation path is wrong;
  Models.fs must always return 200 + empty data, never 503.
- All three fail with NullRefException on `entry.Clone()` → `acc.Add(entry)`
  was used instead of `acc.Add(entry.Clone())`. Re-read Step 1.1 — Clone()
  is non-negotiable.
  </action>
  <verify>
```bash
# Test file exists and contains the locked elements
test -f tests/SmartRouter.Tests/ModelsTests.fs
grep -q 'module SmartRouter.Tests.ModelsTests' tests/SmartRouter.Tests/ModelsTests.fs
grep -q 'MODELS-01' tests/SmartRouter.Tests/ModelsTests.fs
grep -q 'MODELS-02' tests/SmartRouter.Tests/ModelsTests.fs
grep -q 'MODELS-03' tests/SmartRouter.Tests/ModelsTests.fs
grep -q 'startFakeUpstream' tests/SmartRouter.Tests/ModelsTests.fs
grep -q 'acquireDeadPort' tests/SmartRouter.Tests/ModelsTests.fs
grep -q 'StubHealthProbe' tests/SmartRouter.Tests/ModelsTests.fs

# fsproj registers test file in correct order (after HealthFallbackTests.fs, before RouterTests.fs)
grep -q 'ModelsTests.fs' tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
awk '/HealthFallbackTests\.fs/{h=NR} /ModelsTests\.fs/{m=NR} /RouterTests\.fs/{r=NR} END{exit !(h<m && m<r)}' tests/SmartRouter.Tests/SmartRouter.Tests.fsproj

# rootTests appends ModelsTests.tests
grep -q 'SmartRouter.Tests.ModelsTests.tests' tests/SmartRouter.Tests/RouterTests.fs

# Test suite runs clean. Use grep against summary line; expect 86 or 93 passed.
cd /Users/ohama/projs/smart-router && \
  dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj 2>&1 | tee /tmp/phase11-01-test-output.txt | tail -3 | grep -E 'Passed:.+(86|93).+Failed:.+0'
```
  </verify>
  <done>
- `tests/SmartRouter.Tests/ModelsTests.fs` exists, ≥120 lines, contains
  three test cases tagged MODELS-01 / MODELS-02 / MODELS-03.
- `SmartRouter.Tests.fsproj` registers ModelsTests.fs in the correct
  compile order (after HealthFallbackTests.fs, before RouterTests.fs).
- `RouterTests.rootTests` includes `SmartRouter.Tests.ModelsTests.tests`.
- `dotnet test` shows 86 passed (or 93 with embedding files) + 0 failed.
- Net +3 test cases vs. Phase 10 baseline.
  </done>
</task>

</tasks>

<verification>
Phase 11-01 verification — combined check:

```bash
# Pure-Core invariant: no Core changes in this plan
git diff --name-only src/SmartRouter.Core/ | wc -l | tr -d ' '   # → 0

# Endpoint registered in Program.fs
grep -c 'SmartRouter.Cli.Endpoints.Models.mapEndpoints' src/SmartRouter.Cli/Program.fs   # → 1

# JsonElement.Clone() is present (CRITICAL: silent data corruption guard)
grep -c '\.Clone()' src/SmartRouter.Cli/Endpoints/Models.fs   # → ≥1

# health-probe client reused (NOT a 12th named client)
grep -c 'CreateClient("health-probe")' src/SmartRouter.Cli/Endpoints/Models.fs   # → 1
grep -c 'CreateClient("models-probe")' src/SmartRouter.Cli/Endpoints/Models.fs   # → 0 (anti-pattern)

# IsReachable gating present for both targets
grep -c 'IsReachable(Qwen35B)'  src/SmartRouter.Cli/Endpoints/Models.fs   # → 1
grep -c 'IsReachable(Qwen122B)' src/SmartRouter.Cli/Endpoints/Models.fs   # → 1

# Build clean, tests pass
cd /Users/ohama/projs/smart-router && dotnet build  2>&1 | tail -3 | grep -q 'Build succeeded'
cd /Users/ohama/projs/smart-router && dotnet test   2>&1 | tail -3 | grep -E 'Passed:.+(86|93).+Failed:.+0'
```
</verification>

<success_criteria>
- [ ] `Endpoints/Models.fs` implements GET /v1/models with parallel fetch,
      IHealthProbe gating, dedupe by id, JsonElement.Clone() lifetime
      guard, and 200+empty-data on both-down.
- [ ] No new named HttpClient. No new DI registrations. No new domain
      types. No new ports. No Core file edits.
- [ ] `Program.fs` registers `Endpoints.Models.mapEndpoints app`.
- [ ] `SmartRouter.Cli.fsproj` includes `Endpoints/Models.fs` in correct
      compile order.
- [ ] `ModelsTests.fs` contains 3 tests (MODELS-01 dedupe, MODELS-02
      one-down, MODELS-03 both-down).
- [ ] `SmartRouter.Tests.fsproj` includes `ModelsTests.fs` before
      `RouterTests.fs`.
- [ ] `RouterTests.rootTests` appends `ModelsTests.tests`.
- [ ] `dotnet test` shows 86+ passed, 0 failed (or 93+ with embeddings).
</success_criteria>

<output>
After completion, create `.planning/phases/11-deployment-documentation/11-01-MODELS-ENDPOINT-SUMMARY.md`
covering:
- Files created / edited (with line counts).
- Final test counts (with vs without embedding models present).
- Confirmation of L11–L14 lock compliance (200 on both-down, dedupe by id,
  health-probe reuse, JsonElement.Clone()).
- Pure-Core invariant confirmation (`git diff --stat src/SmartRouter.Core/`
  output).
- Any deviations from the planned implementation and the reason.
- Any howto entries that should be authored (likely: a howto for the
  `JsonElement.Clone()` lifetime trap if not already documented).
</output>
