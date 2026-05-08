# Phase 10: Health + Fallback + graph_indexing No-Fallback — Research

**Researched:** 2026-05-09
**Domain:** F# .NET 10 — health probing, fallback policy, retry resilience, OpenAI-compatible error responses
**Confidence:** HIGH

---

## Executive Summary

Five things to build in Phase 10, all grounded in existing codebase scaffolding:

1. **`HealthService` BackgroundService** — polls `GET /v1/models` on each upstream via `PeriodicTimer`, updates a `ConcurrentDictionary<ModelId, bool>` probe state. Implements `IHealthProbe` (already defined in `Ports.fs`). Registered as triple-reg (concrete + interface + `AddHostedService`).
2. **Fallback policy in `QueueDispatcher`** — before enqueueing a 122B request, check `IHealthProbe.IsReachable(Qwen122B)`. If unreachable AND `task != graph_indexing` → reroute to 35B, set `IsFallback = true`. If unreachable AND `task == graph_indexing` → return `Error GraphIndexingMustFail`. Both `IsFallback` and `GraphIndexingMustFail` are already declared in `Domain.fs`.
3. **Retry policy on `upstream35b`/`upstream122b` named HttpClients** — mirror the existing `"teacher"` client's `AddResilienceHandler` pattern (already in `CompositionRoot.fs`). Non-streaming only; streaming path uses a separate named HttpClient pair `"upstream35b-stream"` / `"upstream122b-stream"` with no retry.
4. **`GET /health` endpoint** — simple JSON snapshot of probe state: `{ qwen35b: { reachable: bool, last_probed_at: ISO8601 }, qwen122b: { ... } }`. Mirrors `/stats` endpoint pattern.
5. **`RoutingReason.FallbackTo35B` DU case** — add to `Domain.fs` so the routing_reason in DecisionLog clearly distinguishes a fallback reroute from normal routing.

**Key infrastructure already in place:** `IHealthProbe` port (Ports.fs), `IsFallback` field (Domain.fs), `GraphIndexingMustFail` DU case (Domain.fs), `AddResilienceHandler` + Polly already imported (CompositionRoot.fs), `PeriodicTimer` BackgroundService pattern (CanaryWatchdog.fs), `ExceptionDispatchInfo.Capture` pattern (howto). No Domain-level changes except `FallbackTo35B`.

**Primary recommendation:** Place the fallback check in `QueueDispatcher`, not in a new decorator layer. QueueDispatcher already owns 122B-specific concerns (semaphore, priority queue); this is the natural place. Adding `IHealthProbe` as a constructor parameter follows the existing DI pattern.

---

## Standard Stack

### Core (all already pinned in SmartRouter.Cli.fsproj)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `Microsoft.Extensions.Http.Resilience` | 10.5.0 | `AddResilienceHandler`, `HttpRetryStrategyOptions`, `DelayBackoffType` | Already pinned in Phase 7; same package for retry |
| `Polly` | via transitive | `RetryPredicateArguments<HttpResponseMessage>`, `DelayBackoffType` | Polly 8 is the underlying resilience engine; already open'd in CompositionRoot |
| `Polly.Retry` | via transitive | `HttpRetryStrategyOptions` type | Already in `open Polly.Retry` in CompositionRoot |
| `Microsoft.Extensions.Hosting` | via ASP.NET | `BackgroundService`, `PeriodicTimer` | Pattern used by CanaryWatchdog, RetrainingService |
| `System.Collections.Concurrent` | BCL | `ConcurrentDictionary<ModelId, bool>` for probe state | Thread-safe read/write without explicit locks |

### No new NuGet packages required for Phase 10.

---

## Architecture Patterns

### Recommended File Structure (new files only)

```
src/SmartRouter.Cli/
├── Adapters/
│   └── HealthService.fs          # IHealthProbe impl + BackgroundService
├── Endpoints/
│   └── Health.fs                 # GET /health endpoint
```

One new adapter, one new endpoint. `Domain.fs` gets `FallbackTo35B`. `QueueDispatcher.fs` gains `IHealthProbe` constructor parameter + pre-enqueue check. `CompositionRoot.fs` gains HealthService triple-reg + retry on upstream named HttpClients. `Program.fs` adds `Health.mapEndpoints app`.

### Pattern 1: HealthService BackgroundService

**What:** Polls each upstream with `PeriodicTimer`; stores `(bool * DateTimeOffset)` per `ModelId` in a `ConcurrentDictionary`. Implements `IHealthProbe.IsReachableAsync` (returns the stored bool without hitting the network on the hot path) plus an additional synchronous `IsReachable(ModelId) -> bool` for QueueDispatcher's fast-path check.

**When to use:** Background probe cadence; probe state is readable from any thread without locks.

**IHealthProbe port extension:** The existing `Ports.fs` definition exposes only `IsReachableAsync`. For QueueDispatcher's fast-path, add a synchronous `IsReachable` member to the port OR keep the async member only and add a concrete sync helper in HealthService. Recommendation: add `IsReachable: ModelId -> bool` as a separate method directly on the `IHealthProbe` interface (BCL-only, safe for ARCH-01).

**Example HealthService skeleton:**

```fsharp
// Source: CanaryWatchdog.fs pattern + howto/propagate-cancellation-through-fsharp-task-trywith.md
open System.Runtime.ExceptionServices

type HealthService(httpFactory: IHttpClientFactory, opts: IOptions<UpstreamOptions>) =
    inherit BackgroundService()

    // Probe state: bool = reachable, DateTimeOffset = last probe time.
    let state = ConcurrentDictionary<ModelId, bool * DateTimeOffset>()

    // Initial state: both upstreams treated as reachable during startup grace period.
    do
        state.[Qwen35B]  <- (true, DateTimeOffset.MinValue)
        state.[Qwen122B] <- (true, DateTimeOffset.MinValue)

    let probeOne (target: ModelId) (url: string) (ct: CancellationToken) = task {
        try
            let client = httpFactory.CreateClient("health-probe")
            use! resp = client.GetAsync(url + "/v1/models", ct)
            let reachable = resp.IsSuccessStatusCode
            state.[target] <- (reachable, DateTimeOffset.UtcNow)
            Log.Debug("HealthService: {Target} reachable={R}", target, reachable)
        with
        | :? OperationCanceledException as oce ->
            ExceptionDispatchInfo.Capture(oce).Throw()
        | ex ->
            state.[target] <- (false, DateTimeOffset.UtcNow)
            Log.Warning(ex, "HealthService: probe for {Target} failed", target)
    }

    // IHealthProbe — synchronous fast-path read from ConcurrentDictionary.
    interface IHealthProbe with
        member _.IsReachable(target) =
            match state.TryGetValue(target) with
            | true, (r, _) -> r
            | false, _     -> true  // Unknown → treat as reachable (startup grace)

        member _.IsReachableAsync(target, _ct) =
            let r =
                match state.TryGetValue(target) with
                | true, (r, _) -> r
                | false, _     -> true
            Task.FromResult(r)

    member _.GetState() = state  // for /health endpoint

    override this.ExecuteAsync(stoppingToken) = task {
        let interval = TimeSpan.FromSeconds(float opts.Value.ProbeIntervalSeconds)
        use timer = new PeriodicTimer(interval)
        let mutable running = true
        while running do
            try
                let! ticked = timer.WaitForNextTickAsync(stoppingToken)
                if not ticked then running <- false
                else
                    do! probeOne Qwen35B  opts.Value.Model35B  stoppingToken
                    do! probeOne Qwen122B opts.Value.Model122B stoppingToken
            with
            | :? OperationCanceledException as oce ->
                ExceptionDispatchInfo.Capture(oce).Throw()
            | ex ->
                Log.Warning(ex, "HealthService: unexpected error in probe loop; will retry next tick")
    }
```

**Note on `IHealthProbe` port modification:** The existing `IHealthProbe` in Ports.fs only has `IsReachableAsync`. Phase 10 must add `IsReachable: ModelId -> bool` (synchronous) to the interface for QueueDispatcher's fast-path. This is a BCL-only addition, ARCH-01 safe.

### Pattern 2: Fallback check in QueueDispatcher

**What:** Before `enqueue122b`, check `IHealthProbe.IsReachable(Qwen122B)`. Two outcomes if unreachable:
- Task is NOT `graph_indexing` → reroute decision to 35B with `IsFallback = true`, `Reason = FallbackTo35B`.
- Task IS `graph_indexing` → return `Error GraphIndexingMustFail` immediately (no upstream call, no semaphore touch).

**Decision: where to put the check:** In `QueueDispatcher.CompleteAsync` and `StreamAsync`, BEFORE `enqueue122b`. Option A (QueueDispatcher itself) beats Option B (ChatCompletions handler) because QueueDispatcher already owns 122B-specific shaping and this keeps ChatCompletions unchanged.

**Task detection:** `request.Task` is `string option`. The check is `req.Task = Some "graph_indexing"`. Lock: the exact string is `"graph_indexing"` per appsettings.json and Domain.fs `GraphIndexing` mapping. No case-insensitive comparison needed because `ChatCompletions.mapWireToRequest` already normalizes with `.Trim()` but NOT `.ToLowerInvariant()`. The Routing pipeline lowercases on `tryParseTaskType`. However, by the time QueueDispatcher is called, the task string in `req.Task` is the raw (trimmed) string from the wire, not lowercased. Safest check: `req.Task |> Option.map (fun t -> t.ToLowerInvariant()) = Some "graph_indexing"`.

**Example QueueDispatcher fallback logic:**

```fsharp
// Added to QueueDispatcher constructor: healthProbe: IHealthProbe
// In CompleteAsync, before the match on decision.Target:
let decision, earlyError =
    if decision.Target = Qwen122B && not (healthProbe.IsReachable(Qwen122B)) then
        let isGraphIndexing =
            req.Task |> Option.map (fun t -> t.ToLowerInvariant()) = Some "graph_indexing"
        if isGraphIndexing then
            decision, Some (Error GraphIndexingMustFail)
        else
            // Reroute to 35B
            let fallbackDecision =
                { decision with
                    Target     = Qwen35B
                    Reason     = FallbackTo35B
                    IsFallback = true }
            Log.Warning(
                "QueueDispatcher: 122B unreachable; rerouting {Task} to 35B (fallback)",
                req.Task)
            fallbackDecision, None
    else
        decision, None

match earlyError with
| Some e -> return e
| None ->
    match decision.Target with
    // ... existing 35B bypass / 122B enqueue logic
```

### Pattern 3: Retry on upstream HttpClients

**What:** Two named HttpClients get `AddResilienceHandler` chains — `"upstream35b"` and `"upstream122b"` for non-streaming; `"upstream35b-stream"` and `"upstream122b-stream"` for streaming (no retry). `QwenUpstreamClient` picks the right client name based on `req.Stream`.

**ShouldHandle predicate (mirrors Phase 7 teacher pattern):**
- `HttpRequestException` → retry (transport error)
- `TaskCanceledException` → retry (timeout)
- HTTP 5xx → retry
- HTTP 4xx → do NOT retry (client error, cost waste)

**Retry config:** `MaxRetryAttempts = 3`, `BackoffType = Exponential`, `Delay = 1s` (attempts at 1s/2s/4s). Each attempt gets its own 300s timeout budget from `HttpClient.Timeout`. Total worst-case: ~905s. Acceptable because user requests already have the QueueDispatcher `PerRequestTimeoutSeconds = 300` outer limit.

**F# binding:** Use `.ConfigureHttpClient(...)` chain, not `AddHttpClient(name, fun c -> ...)` (howto: `wire-fsharp-namedhttpclient-with-configurehttpclient.md`). Then `.AddResilienceHandler(name, fun builder -> ...)`.

**Example:**

```fsharp
// Source: CompositionRoot.fs "teacher" pattern + howto/wire-fsharp-namedhttpclient-with-configurehttpclient.md
services.AddHttpClient("upstream35b")
    .ConfigureHttpClient(fun c ->
        let upstreamOpts = config.GetSection("Upstreams").Get<UpstreamOptions>()
        c.BaseAddress <- Uri(upstreamOpts.Model35B)
        c.Timeout     <- TimeSpan.FromSeconds(300.0))
    .AddResilienceHandler("upstream35b-pipeline",
        fun (builder: Polly.ResiliencePipelineBuilder<HttpResponseMessage>) ->
            let retryOpts = HttpRetryStrategyOptions()
            retryOpts.MaxRetryAttempts <- 3
            retryOpts.BackoffType      <- DelayBackoffType.Exponential
            retryOpts.Delay            <- TimeSpan.FromSeconds(1.0)
            retryOpts.ShouldHandle     <-
                Func<RetryPredicateArguments<HttpResponseMessage>, ValueTask<bool>>(
                    fun args ->
                        let retry =
                            match args.Outcome.Exception with
                            | :? HttpRequestException -> true
                            | :? TaskCanceledException -> true
                            | null ->
                                let resp = args.Outcome.Result
                                not (isNull resp) && int resp.StatusCode >= 500
                            | _ -> false
                        ValueTask.FromResult(retry))
            builder.AddRetry(retryOpts) |> ignore)
    |> ignore

// Streaming clients — same BaseAddress/Timeout, NO retry handler.
services.AddHttpClient("upstream35b-stream")
    .ConfigureHttpClient(fun c ->
        let upstreamOpts = config.GetSection("Upstreams").Get<UpstreamOptions>()
        c.BaseAddress <- Uri(upstreamOpts.Model35B)
        c.Timeout     <- TimeSpan.FromSeconds(300.0))
    |> ignore
// (same for upstream122b / upstream122b-stream)
```

**QwenUpstreamClient client name selection:**

```fsharp
// In resolveProbe, add stream-aware name selection:
let resolveClientName (target: ModelId) (stream: bool) =
    match target, stream with
    | Qwen35B,  false -> "upstream35b"
    | Qwen35B,  true  -> "upstream35b-stream"
    | Qwen122B, false -> "upstream122b"
    | Qwen122B, true  -> "upstream122b-stream"
```

### Pattern 4: GET /health Endpoint

**What:** Returns JSON snapshot of probe state from `IHealthProbe`. Mirrors `/stats` (Stats.fs) pattern.

**Response shape:**

```json
{
  "qwen35b":  { "reachable": true,  "last_probed_at": "2026-05-09T12:00:00.000Z" },
  "qwen122b": { "reachable": false, "last_probed_at": "2026-05-09T12:00:05.000Z" }
}
```

HTTP 200 always (even when upstreams are down — the health endpoint itself is reachable). The consumer reads the `reachable` fields. No HTTP 503 from `/health` itself.

**Example Health endpoint:**

```fsharp
// Source: Stats.fs pattern
module SmartRouter.Cli.Endpoints.Health

let mapEndpoints (app: WebApplication) =
    app.MapGet("/health", Func<HttpContext, Task>(fun ctx ->
        task {
            let probe = ctx.RequestServices.GetRequiredService<IHealthProbe>()
            let r35  = probe.IsReachable(Qwen35B)
            let r122 = probe.IsReachable(Qwen122B)
            // HealthService.GetState() for last_probed_at — needs concrete type resolution
            // OR expose last-probed timestamps via an extended port member.
            let wire = {| qwen35b  = {| reachable = r35;  last_probed_at = "..." |}
                          qwen122b = {| reachable = r122; last_probed_at = "..." |} |}
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsJsonAsync(wire, jsonOptions, ctx.RequestAborted)
        })) |> ignore
```

For `last_probed_at`, either (a) expose it via an extended `IHealthProbe` port method `LastProbedAt(ModelId) -> DateTimeOffset` (clean but changes the port), or (b) resolve the concrete `HealthService` from DI and call `GetState()`. Recommendation: add `LastProbedAt: ModelId -> DateTimeOffset` to `IHealthProbe` in Ports.fs (BCL-only, ARCH-01 safe).

### Pattern 5: GraphIndexingMustFail Wire Response

**What:** HTTP 503 + OpenAI-shaped error body. The ChatCompletions handler already handles `Error e` from `routeRequest` with a generic `| Error e ->` branch that returns HTTP 400. Phase 10 adds an explicit branch for `GraphIndexingMustFail` returning 503.

**Wire format:**

```json
{"error": {"message": "Task 'graph_indexing' requires Qwen122B which is currently unreachable; fallback policy does not apply for graph_indexing.", "type": "model_unavailable"}}
```

**ChatCompletions.handler change:** The `GraphIndexingMustFail` error is returned from QueueDispatcher, NOT from `routeRequest`. So the pattern match that needs updating is inside the `| Ok decision ->` branch, in the `upstream.CompleteAsync` result match:

```fsharp
| Error GraphIndexingMustFail ->
    ctx.Response.StatusCode <- 503
    do! ctx.Response.WriteAsJsonAsync(
            {| error = {| message = "Task 'graph_indexing' requires Qwen122B which is currently unreachable; fallback policy does not apply for graph_indexing."
                          ``type`` = "model_unavailable" |} |},
            jsonOptions, ctx.RequestAborted)
    let reason = formatReason decision.Reason + ";graph_indexing_must_fail"
    decisionLogger.Log(buildDecisionLog ...)
```

For streaming, a `GraphIndexingMustFail` error would arrive as the first yielded `Error` item in the `IAsyncEnumerable`; the existing error-yield path in ChatCompletions already handles this but uses HTTP headers-already-sent logic. Since the probe fires before any SSE bytes are written (no bytes from upstream), the headers have NOT been committed yet at the time QueueDispatcher returns `Error GraphIndexingMustFail`. However, the streaming branch already sets SSE headers BEFORE calling `upstream.StreamAsync`. This means for streaming `graph_indexing` requests, the 503 cannot be returned via status code — only via the SSE error event. Two options:

- **Option A (recommended):** Check `graph_indexing` + 122B-unavailable BEFORE setting SSE headers. Move the health check to ChatCompletions handler, after `| Ok decision ->` but before `if req.Stream then`. Return 503 directly in this check. Only proceed to the streaming branch if no early error.
- **Option B:** Accept the SSE-error-in-stream path (same as upstream errors) for streaming graph_indexing; operator sees an SSE error event.

Recommendation: **Option A**. The check is clean: after `| Ok decision ->`, add a pre-flight check:

```fsharp
| Ok decision ->
    // Pre-flight: graph_indexing + 122B unreachable → hard error before any SSE headers
    let healthProbe = ctx.RequestServices.GetRequiredService<IHealthProbe>()
    let isGraphIndexing = req.Task |> Option.map (fun t -> t.ToLowerInvariant()) = Some "graph_indexing"
    if decision.Target = Qwen122B
       && isGraphIndexing
       && not (healthProbe.IsReachable(Qwen122B)) then
        ctx.Response.StatusCode <- 503
        do! ctx.Response.WriteAsJsonAsync(...)
        decisionLogger.Log(... fallback_used = false ...)  // it's a hard error, not a fallback
    else
    // ... existing stream / non-stream branches
```

This keeps QueueDispatcher's fallback check for non-stream rerouting (35B path), and ChatCompletions does the graph_indexing 503 for the streaming case. For non-streaming, QueueDispatcher returns `Error GraphIndexingMustFail` which the existing `| Error e ->` branch catches (updating that branch to add a specific 503 case).

**Note:** This means the `GraphIndexingMustFail` check fires in TWO places — ChatCompletions pre-flight (streaming+non-streaming) and QueueDispatcher (non-streaming belt-and-suspenders). This is acceptable redundancy; the ChatCompletions pre-flight fires first for streaming.

Actually, simpler: put the pre-flight check in ChatCompletions for BOTH stream and non-stream, return 503 before calling upstream at all. Then QueueDispatcher does NOT need to return `GraphIndexingMustFail` — it only handles the 35B reroute for non-graph_indexing tasks. This is cleaner.

**LOCKED DECISION:** Pre-flight check in ChatCompletions handler (Option A), before SSE headers, before calling `upstream`. QueueDispatcher only handles rerouting (not the error path). `IHealthProbe` injected into ChatCompletions handler via `ctx.RequestServices.GetRequiredService<IHealthProbe>()`.

### Anti-Patterns to Avoid

- **Adding retry to streaming HttpClients:** SSE streaming is not idempotent post-first-byte. Two named HttpClients per upstream (stream vs. non-stream) prevent this.
- **Checking `IHealthProbe` with `await` in the hot sync path:** Use the synchronous `IsReachable` method, not `IsReachableAsync`, in QueueDispatcher and ChatCompletions to avoid unnecessary task overhead.
- **Treating probe intervals as exact:** `PeriodicTimer` fires on schedule; a slow probe does not block the next tick. Probe calls use their own timeout (separate CTS or `HttpClient.Timeout`).
- **Allowing `GraphIndexingMustFail` to be handled by the generic `| Error e ->` 400 branch:** It needs an explicit 503 branch.
- **Using `AddHttpClient(name, fun c -> ...)` two-argument form in F#:** Silent BaseAddress failure. Always use `.AddHttpClient(name).ConfigureHttpClient(fun c -> ...)` chain (howto verified).

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Retry with exponential backoff | Custom retry loop | `AddResilienceHandler` + `HttpRetryStrategyOptions` | Already in codebase (Phase 7 teacher pattern); handles jitter, ShouldHandle predicate, per-attempt timeout |
| Thread-safe probe state | `lock` + `Dictionary` | `ConcurrentDictionary<ModelId, bool * DateTimeOffset>` | Lock-free reads; concurrent probe writes are safe via `[target] <-` (atomic dictionary assignment) |
| Background polling | `Task.Run` + `Thread.Sleep` | `BackgroundService` + `PeriodicTimer` | Pattern already used by CanaryWatchdog and RetrainingService; integrates with ASP.NET host lifecycle |
| Cancellation propagation in `task {}` nested try/with | `reraise()` | `ExceptionDispatchInfo.Capture(oce).Throw()` | `reraise()` is FS0413 in `task{}`; howto documented in propagate-cancellation-through-fsharp-task-trywith.md |

---

## Common Pitfalls

### Pitfall 1: Streaming retry — partial SSE delivery

**What goes wrong:** Retry fires after first SSE chunk is already delivered; client receives duplicate or garbled output.
**Why it happens:** `AddResilienceHandler` retries the entire `HttpClient.SendAsync` call; for streaming, the response body is already partially consumed.
**How to avoid:** Two named HttpClients: `"upstream35b-stream"` / `"upstream122b-stream"` with NO retry handler. `QwenUpstreamClient.StreamAsync` uses these names. `QwenUpstreamClient.CompleteAsync` uses `"upstream35b"` / `"upstream122b"` (with retry).
**Warning signs:** Client sees `data: {...}data: {...}` duplicate chunks; SSE stream terminates mid-way with a malformed event.

### Pitfall 2: F# `AddHttpClient(name, fun c -> ...)` silent failure

**What goes wrong:** `BaseAddress` is not set; all requests go to `localhost:80`.
**Why it happens:** F# lambda to `Action<HttpClient>` overload binding is unreliable with multiple overloads. Compile succeeds, runtime fails silently on connection refused.
**How to avoid:** Always use `services.AddHttpClient(name).ConfigureHttpClient(fun c -> ...)` — the chain form has only one `ConfigureHttpClient` overload. (howto: `wire-fsharp-namedhttpclient-with-configurehttpclient.md`)
**Warning signs:** Integration tests see "connection refused" or "absolute URI required".

### Pitfall 3: `IHealthProbe.IsReachable` synchronous design in interface

**What goes wrong:** `IHealthProbe` in Ports.fs currently only has `IsReachableAsync`. If QueueDispatcher uses `IsReachableAsync(...).GetAwaiter().GetResult()` to get a sync result, it blocks a thread pool thread and can cause deadlocks under load.
**Why it happens:** The existing port signature was designed for async use.
**How to avoid:** Add `IsReachable: ModelId -> bool` as a synchronous member to `IHealthProbe` in Ports.fs. `HealthService` implements it via a synchronous `ConcurrentDictionary` read. All callers on the hot path use the sync member.

### Pitfall 4: `RoutingReason.FallbackTo35B` — `formatReason` exhaustive match

**What goes wrong:** Adding `FallbackTo35B` DU case without updating `formatReason` in `DecisionLogger.fs` fails to compile (no `| _ ->` catch-all, per codebase pattern) or returns the wrong string.
**Why it happens:** `RoutingReason` match in `formatReason` is exhaustive by design; adding a new DU case is a compile error if not all handlers are updated.
**How to avoid:** When adding `FallbackTo35B` to `Domain.fs`, immediately update `formatReason` in `DecisionLogger.fs` with `| FallbackTo35B -> "fallback_to_35b"`.
**Warning signs:** `FS0025` incomplete pattern match compilation error.

### Pitfall 5: `DecisionLog.fallback_used` — only set on Path A (reroute), not Path B (upstream error)

**What goes wrong:** Setting `fallback_used = true` for any upstream error muddies the ML training signal — FailureDetector would collect `hard cases` for non-fallback errors, confusing teacher labeling.
**Why it happens:** Phase 9 CanaryWatchdog uses `;upstream_error` suffix as a proxy for fallback signal; Phase 10 makes `fallback_used` the canonical signal.
**How to avoid:** `IsFallback = true` only when the fallback policy fires (122B unreachable → rerouted to 35B). Path B (upstream returned error after retry exhausted) is captured via `;upstream_error` routing_reason suffix by ChatCompletions — do NOT set `IsFallback = true` for it.
**Warning signs:** FailureDetector suddenly finding large numbers of `hard cases` after Phase 10; all are upstream errors, not policy fallbacks.

### Pitfall 6: Graph_indexing task string comparison

**What goes wrong:** Case mismatch — `req.Task = Some "Graph_Indexing"` would not trigger the must-fail path.
**Why it happens:** `req.Task` carries the raw (trimmed) task string from the wire without lowercasing.
**How to avoid:** Use `req.Task |> Option.map (fun t -> t.ToLowerInvariant()) = Some "graph_indexing"` in both the ChatCompletions pre-flight check and any QueueDispatcher check.
**Warning signs:** Integration test with `task: "Graph_Indexing"` unexpectedly falls back to 35B instead of returning 503.

### Pitfall 7: QueueDispatcher constructor signature cascade

**What goes wrong:** Adding `healthProbe: IHealthProbe` to `QueueDispatcher` constructor requires updating `CompositionRoot.fs` factory lambda. Missing this causes a runtime `TypeLoadException` or DI resolution failure.
**Why it happens:** F# class constructors are positional; adding a new required parameter breaks all construction sites.
**How to avoid:** Enumerate all QueueDispatcher construction sites before editing: `CompositionRoot.fs` line 347. Only one site (production DI). Tests use `QueueDispatcher(inner, opts)` directly — these must add the health probe argument too (use a `FakeHealthProbe` that always returns `true` for non-failure tests).
**Warning signs:** Build fails with FS type error on QueueDispatcher construction; or tests pass but production DI fails at startup.

### Pitfall 8: HealthService probe named HttpClient — same name collision

**What goes wrong:** Using `"upstream35b"` or `"upstream122b"` as the probe HttpClient name picks up the retry handler registered for those clients, causing probe failures to trigger retries (wasted latency, noisy logs).
**Why it happens:** Named HttpClients share handlers by name; probe calls would go through the retry pipeline.
**How to avoid:** Register a separate `"health-probe"` named HttpClient with a short timeout (5s) and NO retry. Probe calls use `httpFactory.CreateClient("health-probe")`.

### Pitfall 9: `task{}` try/with + `reraise()` in HealthService.ExecuteAsync

**What goes wrong:** `FS0413: A 'rethrow' statement may only be used directly in a handler of a try-with`.
**Why it happens:** `reraise()` is illegal in `task{}` CE (state machine transformation moves with-clause outside catch handler boundary).
**How to avoid:** Use `ExceptionDispatchInfo.Capture(oce).Throw()` for `OperationCanceledException`. (howto: `propagate-cancellation-through-fsharp-task-trywith.md`)

### Pitfall 10: Initial probe state before first tick

**What goes wrong:** If initial state is "unreachable", the router rejects ALL requests for the first probe interval (up to 10s). Operator restarts the router and gets 503s for 10s.
**Why it happens:** ConcurrentDictionary default value is false for bool; if state is absent, IsReachable returns false.
**How to avoid:** Initialize both upstreams to `(true, DateTimeOffset.MinValue)` in HealthService constructor ("startup grace period" — treat as reachable until proven otherwise). First real probe fires within `ProbeIntervalSeconds`.

---

## Domain Changes Required

### Domain.fs — add `FallbackTo35B` to `RoutingReason`

```fsharp
type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string
    | ExplicitTask          of taskType: TaskType
    | Heuristic             of score: int
    | Default
    | ML
    | FallbackTo35B   // NEW Phase 10: 122B unavailable, rerouted to 35B
```

**Construction site cascade:** The `FallbackTo35B` case is only constructed in QueueDispatcher (the reroute path). No Core files construct it. `formatReason` in `DecisionLogger.fs` must add `| FallbackTo35B -> "fallback_to_35b"`.

### Ports.fs — extend `IHealthProbe`

```fsharp
type IHealthProbe =
    /// Synchronous fast-path: reads cached probe state. No IO.
    abstract member IsReachable :
        target : ModelId
        -> bool

    /// Async variant for callers that prefer async context (e.g., /health endpoint).
    abstract member IsReachableAsync :
        target : ModelId
        -> ct   : CancellationToken
        -> Task<bool>

    /// Timestamp of last probe for the given target.
    abstract member LastProbedAt :
        target : ModelId
        -> DateTimeOffset
```

All three are BCL-only (no HttpClient, no Serilog), ARCH-01 safe.

---

## appsettings.json changes

Add a new `"Health"` section:

```json
"Health": {
  "ProbeIntervalSeconds": 10
}
```

Default 10s. Configurable.

---

## DI Registration Changes (CompositionRoot.fs)

### New registrations (unconditional — health applies in both heuristic and ML mode):

```fsharp
// Health probe named HttpClient — short timeout, no retry
services.AddHttpClient("health-probe")
    .ConfigureHttpClient(fun c ->
        c.Timeout <- TimeSpan.FromSeconds(5.0))
    |> ignore

// HealthService — triple-reg (concrete + IHealthProbe + AddHostedService)
services.AddSingleton<HealthService>(fun sp ->
    HealthService(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IOptions<UpstreamOptions>>(),
        sp.GetRequiredService<IOptions<HealthOptions>>()))
|> ignore

services.AddSingleton<IHealthProbe>(fun sp ->
    sp.GetRequiredService<HealthService>() :> IHealthProbe)
|> ignore

services.AddHostedService<HealthService>(fun sp ->
    sp.GetRequiredService<HealthService>())
|> ignore
```

### Modified registrations:

Existing `upstream35b` / `upstream122b` named HttpClients gain retry pipelines. Add `upstream35b-stream` / `upstream122b-stream` stream-only clients (no retry).

`QueueDispatcher` constructor gains `IHealthProbe` parameter:

```fsharp
services.AddSingleton<QueueDispatcher>(fun sp ->
    QueueDispatcher(
        sp.GetRequiredService<QwenUpstreamClient>() :> IUpstreamClient,
        sp.GetRequiredService<IOptions<QueueDispatcherOptions>>().Value,
        sp.GetRequiredService<IHealthProbe>()))    // NEW
    |> ignore
```

### .fsproj compile order (new entries):

```xml
<!-- Phase 10 — Health + Fallback -->
<Compile Include="Adapters/HealthService.fs" />    <!-- before Endpoints -->
<Compile Include="Endpoints/Health.fs" />
```

`HealthService.fs` must come before `QueueDispatcher.fs` in compile order? No — `QueueDispatcher` depends on `IHealthProbe` from Ports.fs (Core), not on `HealthService.fs`. So order is: `HealthService.fs` can go after `QueueDispatcher.fs` or before it — it only needs to be before `CompositionRoot.fs`. Place it with other Phase 10 adapters, after CanaryService.fs.

---

## Test Plan (TEST-05)

### Unit tests (no fake-Kestrel, no live upstream)

**HLTH-01: HealthService probe state transitions**

Test that `IsReachable` returns correct value after fake `HttpMessageHandler` responses:
- Handler returns 200 → `IsReachable(Qwen35B)` = true
- Handler returns 503 → `IsReachable(Qwen35B)` = false
- Handler throws `HttpRequestException` → `IsReachable(Qwen35B)` = false
- Initial state (before first probe) → `IsReachable(*)` = true (grace period)

Use a `DelegatingHandler` fake that returns a configurable `HttpResponseMessage`.

**HLTH-02: FallbackTo35B routing_reason in formatReason**

Unit test: `formatReason FallbackTo35B = "fallback_to_35b"`.

**HLTH-03: graph_indexing must-fail — pure pre-flight check**

Unit test: call the pre-flight helper directly (extract as a free function) with `task = Some "graph_indexing"`, `target = Qwen122B`, `isReachable = false` → returns `GraphIndexingMustFail`.

### Integration tests (fake-Kestrel upstream, in-process router)

All integration tests follow the pattern established in `StreamingTests.fs` and `CanaryTests.fs`: `startFakeUpstream` + `startCanaryRouter` equivalent.

**HLTH-04: 122B-unavailable fallback for non-graph_indexing task**

1. Start fake-Kestrel for 35B (returns canned response).
2. Start in-process router with 122B pointing to a non-listening port.
3. Wait for HealthService probe to detect 122B unreachable (up to `ProbeIntervalSeconds + 1s`).
4. POST `/v1/chat/completions` with `task: "reasoning"`.
5. Assert: HTTP 200, response body from 35B.
6. Assert: DecisionLog has `fallback_used = true`, `routing_reason` contains `"fallback_to_35b"`, `target = "Qwen35B"`.

**HLTH-05: graph_indexing must-fail when 122B down**

1. Same setup as HLTH-04 (122B at non-listening port).
2. POST `/v1/chat/completions` with `task: "graph_indexing"`.
3. Assert: HTTP 503, response body `{"error": {"message": "...", "type": "model_unavailable"}}`, NO model output.
4. Assert: DecisionLog has `fallback_used = false` (it's a hard error, not a fallback).

**HLTH-06: retry on transient upstream error**

1. Start fake-Kestrel that returns 503 on the first POST, 200 on the second.
2. Start in-process router with 35B pointing to this fake upstream.
3. POST `/v1/chat/completions` (non-streaming, no task → routes to 35B or 35B-forced).
4. Assert: HTTP 200, response from fake (second call).
5. Assert: elapsed time reflects retry delay (≥1s due to exponential backoff).

**HLTH-07: streaming requests do NOT retry**

1. Start fake-Kestrel for 35B that returns 503.
2. POST `/v1/chat/completions` with `stream: true`.
3. Assert: SSE error event is emitted (no retry retry), response is NOT a success.
4. Verify: check registered `"upstream35b-stream"` has no `ResilienceHandler` by asserting request count at fake-Kestrel = 1 (only one attempt).

**HLTH-08: GET /health returns correct reachability**

1. Start fake-Kestrel for both 35B and 122B.
2. Start in-process router.
3. Wait for first probe cycle.
4. GET `/health` → assert both reachable = true.
5. Stop fake 122B.
6. Wait for probe cycle.
7. GET `/health` → assert `qwen122b.reachable = false`, `qwen35b.reachable = true`.

**testSequenced:** All integration tests use `testSequenced` (shared Serilog, tempDir, port contention).

**RouterTests.fs registration:**

```fsharp
SmartRouter.Tests.HealthTests.tests   // Phase 10
```

---

## `fallback_used` Activation — The Signal That Unlocks Phase 8

Phase 7's `FailureDetector` filters JSONL on `fallback_used = true`. Phase 8's `RetrainingService` uses these hard cases for teacher labeling. Until Phase 10, this filter always returns 0 records (FailureDetector.fs line 83: "fallback_used is always false until Phase 10 ships").

After Phase 10:
- When 122B is down and a request (not `graph_indexing`) is rerouted to 35B → `IsFallback = true` → `fallback_used = true` in JSONL → FailureDetector returns real hard cases → teacher labeling fires → ML training loop becomes self-improving.
- `graph_indexing` errors set `fallback_used = false` (they're hard errors, not learning opportunities).
- Upstream errors (retry exhausted) set `fallback_used = false` (captured via `;upstream_error` suffix for CanaryWatchdog).

This is the phase where the ML feedback loop closes.

---

## Open Questions for Planner / Operator

1. **Probe interval default (10s)?** — 10s means 122B detection latency up to 10s. During that window, requests queue on 122B (SemaphoreSlim). If operator stops 122B, the first in-flight request gets a timeout (300s), not an immediate fallback. Consider 5s for faster detection. **Recommendation: default 10s, configurable. Not critical to change for Phase 10.**

2. **Consecutive-failure threshold before flipping to Unreachable?** — A single probe failure (network blip) immediately flips to `false`. This means a 1s network hiccup triggers 10s of fallback traffic to 35B. Mitigate: require 2 consecutive failures to flip to Unreachable. Add `ConsecutiveFailureThreshold` config (default 1 = current behavior, 2 = more resilient). **Recommendation: defer this complexity; start with threshold=1. Document as a known limitation.**

3. **HealthService port `BaseAddress` for probe client?** — The `"health-probe"` HttpClient does NOT have a BaseAddress (it probes different hosts). Each `probeOne` call constructs a full URL. Confirm this is correct. **Yes — probe GET calls use `url + "/v1/models"` as the full absolute URL; BaseAddress stays null on this client. This is correct.**

4. **AutoRollbackEnabled — activate in Phase 10?** — Phase 9 ships `AutoRollbackEnabled = false` by default (CanaryWatchdog). Phase 10 CONTEXT.md Lock 1 says "activate in Phase 10". Now that `fallback_used = true` records are real, CanaryWatchdog can use them. **Lock: set `AutoRollbackEnabled` default to `true` in appsettings.json in Phase 10. Operator may override to false.**

5. **FallbackTo35B RoutingReason — add to RoutingReason DU?** — Currently `IsFallback = true` is the semantic signal in logs; `RoutingReason.FallbackTo35B` would make the routing_reason string self-describing. Without it, the reason string stays whatever the original routing said (e.g., `explicit_task:Reasoning`) with `fallback_used = true` as the separate signal. **Recommendation: add `FallbackTo35B` DU case for clarity. The routing_reason in JSONL becomes `"fallback_to_35b"` instead of an ambiguous `"ml"` or `"heuristic:score=5"`.**

---

## Sources

### Primary (HIGH confidence)

- `/Users/ohama/projs/smart-router/src/SmartRouter.Core/Domain.fs` — confirmed `IsFallback`, `GraphIndexingMustFail`, `RoutingReason` DU, no `FallbackTo35B` yet
- `/Users/ohama/projs/smart-router/src/SmartRouter.Core/Ports.fs` — confirmed `IHealthProbe` exists with only `IsReachableAsync`; needs `IsReachable` + `LastProbedAt` additions
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` — confirmed constructor signature, enqueue122b pattern, fallback insertion point
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` — confirmed 4 named clients needed (stream vs non-stream); `resolveProbe` is the extension point
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/CompositionRoot.fs` — confirmed `AddResilienceHandler` + `HttpRetryStrategyOptions` already in use (Phase 7 teacher pattern); QueueDispatcher constructor wiring
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — confirmed `metricCohort` + `IsFallback` usage; pre-flight check insertion point identified
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Endpoints/Stats.fs` — confirmed /health endpoint pattern
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/CanaryWatchdog.fs` — confirmed `PeriodicTimer` BackgroundService pattern
- `/Users/ohama/projs/smart-router/documentation/howto/wire-fsharp-namedhttpclient-with-configurehttpclient.md` — confirmed `.AddHttpClient(name).ConfigureHttpClient(...)` chain requirement
- `/Users/ohama/projs/smart-router/documentation/howto/propagate-cancellation-through-fsharp-task-trywith.md` — confirmed `ExceptionDispatchInfo.Capture(oce).Throw()` pattern for BackgroundService
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — confirmed `Microsoft.Extensions.Http.Resilience` v10.5.0, no new packages needed

### Secondary (HIGH confidence — official docs)

- `https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience` — confirmed `AddResilienceHandler` API, `HttpRetryStrategyOptions` fields (`MaxRetryAttempts`, `BackoffType`, `Delay`, `ShouldHandle`), 4xx non-retry convention, streaming POST retry warning
- Microsoft.Extensions.Http.Resilience v10.5.0 — confirmed: same `AddResilienceHandler` API shape as Phase 7 usage; no breaking changes from v9 to v10

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all libraries already in project; no new packages
- Architecture: HIGH — all patterns verified against existing codebase (Phase 7 teacher, CanaryWatchdog, Stats endpoint)
- Pitfalls: HIGH — 9 of 10 pitfalls are documented in existing howtos or observed in prior phases
- Open questions: MEDIUM — items 1-2 are operator preferences; items 3-5 have recommendations

**Research date:** 2026-05-09
**Valid until:** 2026-06-09 (stable stack; Microsoft.Extensions.Http.Resilience API is stable)
