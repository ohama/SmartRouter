# Stack Research

**Domain:** F# .NET 10 OpenAI-compatible LLM reverse proxy / API gateway
**Researched:** 2026-05-07
**Confidence:** HIGH (core framework choices locked by blueCode precedent + project constraints); MEDIUM on exact NuGet versions post-August 2025 cutoff (NuGet live verification was unavailable — versions flagged below)

---

## Recommended Stack

### Core Framework

| Technology | Version | Purpose | Why Recommended |
|------------|---------|---------|-----------------|
| F# / .NET 10 | 10.x | Language + runtime | Fixed by project constraint; matches blueCode `net10.0` |
| ASP.NET Core Minimal API | (built into .NET 10) | HTTP endpoint hosting | Fixed by project constraint. Raw `WebApplication.MapPost` — see F# Minimal API choice below. |
| System.Text.Json | (built into .NET 10) | JSON serialization | Fixed by project constraint; zero-allocation, source-gen capable. blueCode already uses it with `FSharp.SystemTextJson`. |
| `task {}` CE | (F# stdlib) | Concurrency primitive | Fixed by blueCode invariant — `async {}` is banned in Core by CI grep. `task {}` compiles to `ValueTask`-based state machines with lower allocation. |
| `SemaphoreSlim` | (BCL) | 122B concurrency cap | Single chokepoint at application layer before the mlx_lm.server serializes anyway. Cheapest correct option. |

### HTTP Client

| Library | Version | Purpose | Why Recommended |
|---------|---------|---------|-----------------|
| `Microsoft.Extensions.Http` (IHttpClientFactory) | (built into .NET 10) | Named HttpClient pool per upstream | HttpClientFactory solves DNS refresh + socket exhaustion. Named clients (`"35b"`, `"122b"`) isolate timeouts — 300s for both (matches blueCode Phase 20-01 value). |
| `Microsoft.Extensions.Http.Resilience` | ~9.0.x | Retry + timeout pipeline | **Use this, not Polly directly** — see Polly vs Resilience section below. |

### Serialization

| Library | Version | Purpose | Why Recommended |
|---------|---------|---------|-----------------|
| `FSharp.SystemTextJson` | 1.4.36 (verified in blueCode `/src/BlueCode.Cli/BlueCode.Cli.fsproj`) | F# DU / option / list round-trip via STJ | Required: STJ's built-in F# support is absent; this converter handles `MessageRole` (System\|User\|Assistant) as bare strings, F# lists as JSON arrays, option as nullable. Same package + version as blueCode — do not diverge. |

### Logging

| Library | Version | Purpose | Why Recommended |
|---------|---------|---------|-----------------|
| `Serilog` | 4.3.1 (verified in blueCode) | Structured logging core | Fixed by blueCode convention. Static `Log.Logger` initialized once at startup. |
| `Serilog.AspNetCore` | ~9.0.x (see confidence note) | ASP.NET Core request logging integration | Hooks into `ILogger<T>` DI pipeline and adds request-level enrichment. `app.UseSerilogRequestLogging()` replaces default ASP.NET Core request log. |
| `Serilog.Sinks.Console` | 6.1.1 (verified in blueCode) | stderr sink | `standardErrorFromLevel = Verbose` routes ALL events to stderr. Matches blueCode `Logging.fs` exactly — copy that file verbatim. |

### Functional Utilities

| Library | Version | Purpose | Why Recommended |
|---------|---------|---------|-----------------|
| `FsToolkit.ErrorHandling` | ~4.x (see confidence note) | `result {}` / `taskResult {}` CE, `Result.map`, etc. | Idiomatic F# error-handling without exception spam. Use for routing pipeline composition. Pure Core can use it (no I/O dependency). |
| `FSharp.Control.TaskSeq` | ~0.4.x (see confidence note) | `IAsyncEnumerable` / `taskSeq {}` for SSE chunk iteration | Required for the SSE streaming pass-through path — iterating upstream SSE chunks as an `IAsyncEnumerable<string>` without buffering. See SSE section. |

### Testing

| Library | Version | Purpose | Why Recommended |
|---------|---------|---------|-----------------|
| `Expecto` | 10.2.1 (verified in blueCode `/tests/BlueCode.Tests/BlueCode.Tests.fsproj`) | Test framework | Fixed by project constraint; matches blueCode. Use explicit `rootTests` list — auto-discovery is unreliable (blueCode CLAUDE.md warning, burned 4 executors). |
| `Microsoft.AspNetCore.Mvc.Testing` | ~10.0.x | TestServer + integration tests | The `WebApplicationFactory<T>` pattern provides an in-process Kestrel instance for integration tests. Wire fake upstream servers as additional Kestrel-on-random-port instances (see fake upstream pattern below). |

### Configuration

| Library | Version | Purpose | Why Recommended |
|---------|---------|---------|-----------------|
| `Microsoft.Extensions.Configuration.Json` | (built into ASP.NET Core host) | `appsettings.json` loading | Already included via `WebApplication.CreateBuilder`. Use the options pattern (`IOptions<RoutingOptions>`, `IOptions<UpstreamOptions>`) — strongly typed, validated at startup, DI-friendly. No extra package needed. |

---

## Key Technical Decisions (with rationale)

### 1. F# Minimal API: raw `WebApplication.MapPost` — NOT Falco, NOT Saturn

**Decision:** Use raw ASP.NET Core Minimal API (`WebApplication.MapPost`, `WebApplication.MapGet`).

**Rationale:**
- This project is a gateway / proxy, not a web app with views, routing complexity, or form handling. The route surface is tiny: one `POST`, two `GET`s, one `GET /stats`.
- Falco and Saturn add meaningful abstraction overhead. Falco's value is HTML view composition and its `Request.mapJson` helpers; Saturn adds computation expression routing. Neither adds value for 4 endpoints.
- Raw Minimal API works perfectly with F# — request binding is `Async.AwaitTask` / `task {}` friendly, DI injection via handler parameters is clean, and the streaming response path is straightforward (`Results.Stream` / `HttpContext.Response.Body`).
- blueCode uses neither Falco nor Saturn (it's a CLI tool). Staying on raw Minimal API keeps the mental model consistent.
- **What NOT to use:** Falco (unnecessary complexity for 4 endpoints), Saturn (same), Giraffe (heavier than raw Minimal API for this use case), Nancy (abandoned).

### 2. Polly vs `Microsoft.Extensions.Http.Resilience`

**Decision:** Use `Microsoft.Extensions.Http.Resilience` (`AddResilienceHandler`), not Polly v7/v8 directly.

**Rationale:**
- `Microsoft.Extensions.Http.Resilience` is the .NET 8+ official replacement for `Microsoft.Extensions.Http.Polly`. It wraps Polly v8 internally but integrates with `IHttpClientFactory` via `AddResilienceHandler` — first-class in the DI pipeline.
- Polly v8 (direct) requires manual wiring; the Resilience package gives `StandardResilienceOptions` (retry + circuit breaker + timeout + rate limiter composited) with sensible defaults.
- For this project, the relevant pipeline: retry (2 retries, exponential backoff, `chat/completions` is effectively idempotent for transient errors) + timeout (300s outer timeout, matching blueCode). Do NOT use the automatic circuit-breaker component — PROJECT.md explicitly rejects circuit breaker as a distinct mechanism (retry + health probing is sufficient for v1).
- **What NOT to use:** `Microsoft.Extensions.Http.Polly` — this is the legacy .NET 6-era package, deprecated in favor of `Microsoft.Extensions.Http.Resilience`.

**Configuration sketch:**
```fsharp
services.AddHttpClient("35b", fun c ->
    c.BaseAddress <- Uri("http://127.0.0.1:8000")
    c.Timeout <- TimeSpan.FromSeconds(300.0))
    .AddResilienceHandler("35b-retry", fun builder ->
        builder.AddRetry(RetryStrategyOptions(
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromMilliseconds(500.0),
            ShouldHandle = fun args ->
                ValueTask.FromResult(args.Outcome.Exception :? HttpRequestException)))
        |> ignore)
|> ignore
```

### 3. SSE Streaming Pass-Through

**Decision:** Use raw `HttpClient.SendAsync` with `HttpCompletionOption.ResponseHeadersRead` + manual `Stream.CopyToAsync` (or `FSharp.Control.TaskSeq`-based chunk iteration). Do NOT use a higher-level SSE library.

**Rationale:**
- The router's job is to forward upstream SSE chunks unchanged. It does not parse SSE events; it does not transform them. It only needs to:
  1. Open the upstream response stream immediately (not buffer the full body).
  2. Write each chunk to the downstream response stream.
  3. Propagate backpressure (don't read faster than downstream can consume).
  4. Abort the upstream request when the downstream client disconnects (CancellationToken from `HttpContext.RequestAborted`).
- `HttpCompletionOption.ResponseHeadersRead` is the correct flag: it returns the `HttpResponseMessage` as soon as headers arrive, before the body is buffered. The body stream is then available as `response.Content.ReadAsStreamAsync()`.
- For the simplest correct implementation: copy upstream body stream to downstream response body with `Stream.CopyToAsync(downstreamStream, ct)`. This is a single call, handles backpressure via the stream abstraction, and propagates cancellation.
- `FSharp.Control.TaskSeq` is useful if the router needs to inspect/log individual SSE lines (e.g. log chunk count, detect `[DONE]` sentinel). For pure pass-through, `CopyToAsync` is sufficient and allocates less.
- Do NOT buffer the complete SSE response and send it at the end — this defeats streaming entirely and blows memory for long 122B generations.
- Do NOT use `Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets` or any Kestrel-specific streaming API — the abstraction via `HttpContext.Response.Body` is correct and portable.

**SSE forwarding pattern:**
```fsharp
// In the MapPost handler:
let ct = httpContext.RequestAborted  // propagates client disconnect

// Forward headers
httpContext.Response.ContentType <- "text/event-stream"
httpContext.Response.Headers["Cache-Control"] <- "no-cache"
httpContext.Response.Headers["X-Accel-Buffering"] <- "no"

// Open upstream without buffering body
use req = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
req.Content <- new StringContent(forwardedBody, Encoding.UTF8, "application/json")

use! upstreamResp =
    httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)

// Stream body directly to downstream
use! upstreamStream = upstreamResp.Content.ReadAsStreamAsync(ct)
do! upstreamStream.CopyToAsync(httpContext.Response.Body, ct)
```

### 4. Priority Queue for 122B

**Decision:** Implement a two-level (high/low) priority queue using `System.Collections.Generic.PriorityQueue<T, int>` (BCL, .NET 6+) protected by a `SemaphoreSlim(1)` + a `TaskCompletionSource`-based waiter pattern.

**Rationale:**
- `PriorityQueue<T, int>` is in the BCL since .NET 6. No extra package needed.
- The pattern: requests that need the semaphore enqueue a `TaskCompletionSource` (their "ticket") with priority int (0 = high, 1 = low). The semaphore holder dequeues and completes the next TCS when it releases. This is a standard "async priority semaphore" pattern.
- Do NOT use `System.Threading.Channels` for this — Channels are FIFO and do not support priority ordering. PROJECT.md explicitly rejects Channels/TPL Dataflow for v1.
- The DI container holds the queue+semaphore as a singleton (thread-safe access coordinated by a `lock` on the queue object when enqueuing/dequeuing).

### 5. Structured Logging

**Decision:** Copy `Adapters/Logging.fs` from blueCode verbatim. Add `Serilog.AspNetCore` for request-level enrichment.

**blueCode source:** `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/Logging.fs`

**Pattern (already settled):**
```fsharp
// From blueCode Logging.fs — copy verbatim:
Log.Logger <-
    LoggerConfiguration()
        .MinimumLevel.ControlledBy(levelSwitch)
        .WriteTo.Console(
            standardErrorFromLevel = System.Nullable<LogEventLevel>(LogEventLevel.Verbose),
            outputTemplate = "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger()
```

Add `Serilog.AspNetCore` to the host builder for per-request correlation IDs:
```fsharp
builder.Host.UseSerilog() |> ignore
// Then in pipeline:
app.UseSerilogRequestLogging() |> ignore
```

Per-request correlation IDs: use `ILogger`'s structured property enrichment (`Log.ForContext("RequestId", requestId)`) rather than a separate middleware package.

### 6. Configuration

**Decision:** Use the standard options pattern via `appsettings.json` — no extra package. Built into ASP.NET Core host.

```json
{
  "Upstreams": {
    "Model35B": "http://127.0.0.1:8000",
    "Model122B": "http://127.0.0.1:8001"
  },
  "Routing": {
    "ComplexityThreshold": 10,
    "TimeoutSeconds": 300,
    "Max122BConcurrency": 1
  }
}
```

Define F# record types in Core (pure — no I/O), bind in Cli `CompositionRoot` via `IOptions<T>`. Validated at startup with `ValidateOnStart()`.

### 7. Health Probing

**Decision:** Use `Microsoft.Extensions.Diagnostics.HealthChecks` (built into ASP.NET Core) + a custom `IHealthCheck` per upstream that fires a lightweight `GET /v1/models` probe. No extra package.

The `probeModelInfoAsync` pattern from blueCode (`QwenHttpClient.fs` lines 398–439) is the right model: a single HTTP probe with fallback on failure, using the same `IHttpClientFactory`-managed client.

### 8. Testing: Fake Upstream Pattern

**Decision:** For integration tests with fake upstream servers, use `Microsoft.AspNetCore.TestHost` (in-process `TestServer`) for the router itself, plus Kestrel-on-random-port for fake upstream LLM servers.

**Pattern:**
```fsharp
// In test setup: spin up a fake 35B server on a random port
let fakeUpstreamBuilder = WebApplication.CreateBuilder()
fakeUpstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore  // OS assigns port
let fakeUpstream = fakeUpstreamBuilder.Build()
fakeUpstream.MapPost("/v1/chat/completions", fun () ->
    // Return deterministic SSE or JSON response
    Results.Ok({| choices = [| {| message = {| content = "fake response" |} |} |] |}))
do! fakeUpstream.StartAsync(ct)
let fakePort = (fakeUpstream.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()
                    .Addresses |> Seq.head).Split(':') |> Array.last |> int
// Then wire router WebApplicationFactory to point at fakePort
```

**Important (`testSequenced` rule from blueCode):** Any test module touching `Console.SetOut`/`Console.SetError` globals must wrap its `testList` in `testSequenced`. Same rule as blueCode.

**Important (rootTests):** New test modules must be added to BOTH the `.fsproj` `<Compile>` list AND the explicit `rootTests` list in the test entrypoint. blueCode CLAUDE.md: "Four executors have hit this pitfall."

### 9. launchd plist: `dotnet run` vs self-contained binary

**Decision:** Publish as a self-contained binary for the launchd plist. Use `dotnet run` only during development.

**Rationale:**
- `dotnet run` triggers a rebuild check and starts the SDK toolchain. In a launchd context (auto-start on login, respawn after crash), you want the binary to start immediately without SDK involvement.
- `dotnet publish -c Release -r osx-arm64 --self-contained` produces a single binary at `bin/Release/net10.0/osx-arm64/publish/SmartRouter`. The launchd plist `ProgramArguments` points directly to this binary.
- Match the pattern of the existing `com.ohama.qwen122b.plist` exactly (PROJECT.md states this is the target pattern).
- For development: `dotnet run --project src/SmartRouter.Cli/SmartRouter.Cli.fsproj` is fine.

**plist skeleton:**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>com.ohama.smart-router</string>
  <key>ProgramArguments</key>
  <array>
    <string>/Users/ohama/projs/smart-router/bin/Release/net10.0/osx-arm64/publish/SmartRouter</string>
  </array>
  <key>EnvironmentVariables</key>
  <dict>
    <key>ASPNETCORE_URLS</key>
    <string>http://127.0.0.1:4000</string>
    <key>DOTNET_ENVIRONMENT</key>
    <string>Production</string>
  </dict>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <true/>
  <key>StandardErrorPath</key>
  <string>/Users/ohama/llm-system/services/logs/smart-router.err</string>
  <key>StandardOutPath</key>
  <string>/Users/ohama/llm-system/services/logs/smart-router.out</string>
</dict>
</plist>
```

Note: Serilog → stderr, so structured logs land in `smart-router.err`. Application output (none expected for a gateway) would go to `smart-router.out`.

---

## Complete Package List

### `SmartRouter.Cli.fsproj` (the executable)

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>SmartRouter</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <!-- F# + STJ interop (same version as blueCode) -->
    <PackageReference Include="FSharp.SystemTextJson" Version="1.4.36" />

    <!-- Serilog (same versions as blueCode) -->
    <PackageReference Include="Serilog" Version="4.3.1" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.1.1" />
    <PackageReference Include="Serilog.AspNetCore" Version="9.0.0" />
    <!-- Confidence: HIGH on 4.3.1 + 6.1.1 (verified in blueCode). Serilog.AspNetCore
         9.0.0 released 2024-11; check NuGet for 10.x if available by build time. -->

    <!-- Resilience (replaces Polly direct) -->
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.4.0" />
    <!-- Confidence: MEDIUM — 9.x series is current as of Aug 2025 training cutoff;
         verify at build time: dotnet package search Microsoft.Extensions.Http.Resilience -->

    <!-- Functional error handling -->
    <PackageReference Include="FsToolkit.ErrorHandling" Version="4.19.0" />
    <!-- Confidence: MEDIUM — 4.x series current as of cutoff; verify at build time -->

    <!-- Async enumerable / SSE iteration (needed for chunk-level logging/inspection) -->
    <PackageReference Include="FSharp.Control.TaskSeq" Version="0.4.3" />
    <!-- Confidence: MEDIUM — 0.4.x current as of cutoff; verify at build time -->
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\SmartRouter.Core\SmartRouter.Core.fsproj" />
  </ItemGroup>
</Project>
```

### `SmartRouter.Tests.fsproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <!-- Test framework (same version as blueCode) -->
    <PackageReference Include="Expecto" Version="10.2.1" />

    <!-- Integration test infrastructure -->
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
    <!-- Confidence: MEDIUM — 10.0.x will ship with .NET 10 GA; verify at build time -->
  </ItemGroup>
</Project>
```

---

## Alternatives Considered

| Category | Recommended | Alternative | Why Not |
|----------|-------------|-------------|---------|
| Web framework | Raw Minimal API | Falco | Falco's value is HTML view composition + request helper ergonomics. For 4 endpoints on a proxy, the abstraction cost is not justified. |
| Web framework | Raw Minimal API | Saturn | Saturn adds computation expression routing ergonomics but requires Giraffe as a base. Overhead exceeds benefit for a 4-endpoint gateway. |
| Web framework | Raw Minimal API | Giraffe | Heavier than raw Minimal API; function composition model is elegant but not worth onboarding cost for this use case. |
| Resilience | `Microsoft.Extensions.Http.Resilience` | Polly v8 direct | Polly v8 direct requires manual `ResiliencePipeline` wiring outside `IHttpClientFactory`. The Resilience package is the official .NET 8+ integration layer and less code. |
| Resilience | `Microsoft.Extensions.Http.Resilience` | `Microsoft.Extensions.Http.Polly` | Legacy .NET 6-era package. Deprecated in favor of `Microsoft.Extensions.Http.Resilience`. Do not use. |
| SSE forwarding | `HttpClient` + `ResponseHeadersRead` + `CopyToAsync` | SignalR / gRPC streaming | Complete mismatch with the SSE wire protocol that Hermes and the upstream mlx_lm.server use. |
| SSE forwarding | `HttpClient` + `ResponseHeadersRead` + `CopyToAsync` | YARP (Yet Another Reverse Proxy) | YARP is a full reverse proxy framework with routing tables, transforms, load balancing. Massive overkill for a two-backend purpose-built router. Smart Router needs to inspect and conditionally transform requests before forwarding — YARP's transform pipeline is harder to compose with routing logic than writing it directly. |
| SSE forwarding | `HttpClient` + `ResponseHeadersRead` + `CopyToAsync` | Higher-level SSE library (e.g. `ServerSentEvents`) | Router doesn't parse SSE events — it forwards them verbatim. A parsing library adds allocation with no benefit for pure pass-through. |
| Priority queue | `System.Collections.Generic.PriorityQueue` + TCS waiters | `System.Threading.Channels` | Channels are FIFO; no priority support. PROJECT.md explicitly rejects Channels for v1. |
| Priority queue | `System.Collections.Generic.PriorityQueue` + TCS waiters | TPL Dataflow `BufferBlock` | No priority support; adds Microsoft.Tpl.Dataflow dependency. PROJECT.md rejects Dataflow for v1. |
| JSON | `System.Text.Json` + `FSharp.SystemTextJson` | Newtonsoft.Json | Newtonsoft is performance-heavy, not source-gen compatible, and not the project constraint. `System.Text.Json` is the .NET canonical. blueCode already uses STJ. |
| Config | `IOptions<T>` + `appsettings.json` | Custom TOML / YAML config | TOML/YAML require extra packages. `appsettings.json` is the ASP.NET Core standard and is already wired into the host builder. |
| Tests | Expecto | xUnit + FsUnit | Project constraint (matches blueCode). Original brief mentioned xUnit/FsUnit — PROJECT.md explicitly overrides this to Expecto-only. |

---

## What NOT to Use

| Avoid | Why | Use Instead |
|-------|-----|-------------|
| `Newtonsoft.Json` | Performance overhead, not source-gen capable, not project standard | `System.Text.Json` + `FSharp.SystemTextJson` |
| `Microsoft.Extensions.Http.Polly` | Deprecated legacy package (pre-.NET 8) | `Microsoft.Extensions.Http.Resilience` |
| `async {}` in Core | Banned by blueCode CI (`scripts/check-no-async.sh`); `task {}` compiles to more efficient state machines | `task {}` exclusively |
| `System.Threading.Channels` | FIFO only — no priority support for the 122B queue | `PriorityQueue<T,int>` + TCS waiter pattern |
| TPL Dataflow | PROJECT.md explicit rejection for v1; overkill for two-backend serialization | `SemaphoreSlim(1)` + `PriorityQueue` |
| YARP (Microsoft.ReverseProxy) | Full reverse proxy framework — harder to compose with the routing logic than direct HttpClient | Raw `HttpClient.SendAsync` with `ResponseHeadersRead` |
| Falco / Saturn / Giraffe | Web framework abstractions not justified for 4-endpoint gateway | Raw `WebApplication.MapPost` / `MapGet` |
| Expecto auto-discovery (`[<Tests>]`) | Unreliable (burned 4 executors in blueCode); explicit `rootTests` list is the only safe pattern | Explicit `rootTests` list in test entrypoint |
| `git add -A` / `git add .` | Can sweep in `.claude/` and other intentionally-untracked files | `git add <specific-file>` |
| Static mutable state (module-level `let mutable`) | Not testable; not DI-friendly; races in integration tests | DI-scoped singletons for queue + semaphore + counters |
| Docker / docker-compose | Out of scope (PROJECT.md); Mac-only launchd pattern | launchd plist |

---

## blueCode Precedents (do not re-research these)

These questions are already answered by blueCode. Match them exactly:

| Topic | Decision | Source |
|-------|----------|--------|
| Logging pattern (Serilog config, stderr routing) | Copy `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/Logging.fs` verbatim | `Logging.fs` |
| `FSharp.SystemTextJson` version + options config | `1.4.36`, `WithUnionUnwrapFieldlessTags(true)` | `BlueCode.Cli.fsproj`, `Json.fs` |
| `Serilog` + `Serilog.Sinks.Console` versions | `4.3.1`, `6.1.1` | `BlueCode.Cli.fsproj` |
| `Expecto` version | `10.2.1` | `BlueCode.Tests.fsproj` |
| HttpClient timeout | 300s (covers 122B cold-start up to 240s) | `QwenHttpClient.fs` line 35 |
| `tryParseModelId` HF-fallback trap defense | Prefer `id` starting with `/`; fall back to `data[0]` | `QwenHttpClient.fs` lines 328–356 |
| `task {}` not `async {}` in Core | Enforced by CI grep | `CLAUDE.md` + `scripts/check-no-async.sh` |
| Expecto `testSequenced` for Console.SetOut tests | Required to prevent stdout capture races | `CLAUDE.md` |
| Explicit `rootTests` list (not auto-discovery) | Required; auto-discovery unreliable | `CLAUDE.md` |
| Serilog sampling params defaults | `temperature=0.7, top_p=0.8, top_k=20, presence_penalty=0.0` | `QwenHttpClient.fs` line 48–49 |
| `task {}` CE in Core tests | All Core test functions must use `task {}` not `async {}` | `CLAUDE.md` |

---

## Version Confidence Summary

| Package | Version | Confidence | How Verified |
|---------|---------|------------|--------------|
| `FSharp.SystemTextJson` | 1.4.36 | HIGH | Verified in blueCode `BlueCode.Cli.fsproj` |
| `Serilog` | 4.3.1 | HIGH | Verified in blueCode `BlueCode.Cli.fsproj` |
| `Serilog.Sinks.Console` | 6.1.1 | HIGH | Verified in blueCode `BlueCode.Cli.fsproj` |
| `Expecto` | 10.2.1 | HIGH | Verified in blueCode `BlueCode.Tests.fsproj` |
| `Serilog.AspNetCore` | 9.0.0 | MEDIUM | Training knowledge (2024-11 release); verify with `dotnet package search` at build time |
| `Microsoft.Extensions.Http.Resilience` | 9.4.0 | MEDIUM | Training knowledge; verify at build time — may be 9.x or 10.x by May 2026 |
| `FsToolkit.ErrorHandling` | 4.19.0 | MEDIUM | Training knowledge; verify at build time |
| `FSharp.Control.TaskSeq` | 0.4.3 | MEDIUM | Training knowledge; verify at build time |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.0 | MEDIUM | Will ship with .NET 10 GA; verify at build time |

**Version verification command (run at project scaffold time):**
```bash
dotnet package search Serilog.AspNetCore --take 1
dotnet package search Microsoft.Extensions.Http.Resilience --take 1
dotnet package search FsToolkit.ErrorHandling --take 1
dotnet package search FSharp.Control.TaskSeq --take 1
dotnet package search Microsoft.AspNetCore.Mvc.Testing --take 1
```

---

## Sources

- `/Users/ohama/projs/blueCode/src/BlueCode.Cli/BlueCode.Cli.fsproj` — verified package versions (Serilog, FSharp.SystemTextJson)
- `/Users/ohama/projs/blueCode/tests/BlueCode.Tests/BlueCode.Tests.fsproj` — verified Expecto version
- `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/QwenHttpClient.fs` — HttpClient timeout, SSE pattern, probe pattern
- `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/Logging.fs` — Serilog configuration pattern (copy verbatim)
- `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/Json.fs` — FSharp.SystemTextJson options configuration
- `/Users/ohama/projs/blueCode/CLAUDE.md` — invariants: task {}, testSequenced, rootTests, stderr routing
- `/Users/ohama/projs/smart-router/.planning/PROJECT.md` — project constraints, key decisions, out-of-scope items
- .NET 10 / ASP.NET Core docs (training knowledge, Aug 2025 cutoff) — Minimal API, IHttpClientFactory, PriorityQueue, HealthChecks
- `Microsoft.Extensions.Http.Resilience` docs (training knowledge) — Polly v8 integration pattern

---
*Stack research for: Smart Router — F# .NET 10 OpenAI-compatible LLM gateway*
*Researched: 2026-05-07*
