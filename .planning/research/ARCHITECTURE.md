# Architecture Research

**Domain:** F# hexagonal LLM router / gateway
**Researched:** 2026-05-07
**Confidence:** HIGH (based on direct analysis of blueCode codebase + PROJECT.md invariants)

---

## Standard Architecture

### System Overview

```
┌──────────────────────────────────────────────────────────────────────────┐
│                        SmartRouter.Cli                                    │
│                                                                           │
│  ┌─────────────────┐  ┌───────────────────────────────────────────────┐  │
│  │    Endpoints/   │  │                Adapters/                      │  │
│  │                 │  │                                               │  │
│  │ ChatCompletions │  │  QwenUpstreamClient  (IUpstreamClient impl)  │  │
│  │ Health          │  │  QueueDispatcher     (priority + semaphore)  │  │
│  │ Models          │  │  Logging             (Serilog → stderr)      │  │
│  │ Stats           │  │  Json                (STJ options/helpers)   │  │
│  └────────┬────────┘  │  Health              (upstream probe)        │  │
│           │           └───────────────────────────────────────────────┘  │
│           │                           │                                   │
│           └──────────────┬────────────┘                                   │
│                          │  calls ports                                   │
├──────────────────────────┼───────────────────────────────────────────────┤
│                   PORTS (interfaces)                                      │
│            IUpstreamClient   IClock   IHealthProbe                        │
├──────────────────────────┼───────────────────────────────────────────────┤
│                        SmartRouter.Core                                   │
│                                                                           │
│   Domain.fs          RoutingDecision, Request, ModelId, Priority,         │
│                       TaskType, RouterError, RoutingReason                │
│                                                                           │
│   Routing.fs         routeRequest (pure pipeline):                        │
│                         tryModelOverride → tryTaskTable → applyHeuristic  │
│                         → defaultDecision                                 │
│                                                                           │
│   Ports.fs           IUpstreamClient, IClock, IHealthProbe (interfaces)  │
└──────────────────────────────────────────────────────────────────────────┘

External:
  Hermes / Graphify  →  POST localhost:4000/v1/chat/completions
  SmartRouter.Cli    →  HTTP POST localhost:800{0,1}/v1/chat/completions (Qwen 35B / 122B)
```

### Component Responsibilities

| Component | Responsibility | Side of Port |
|-----------|----------------|-------------|
| `SmartRouter.Core/Domain.fs` | All DUs, record types, no IO | Core (pure) |
| `SmartRouter.Core/Routing.fs` | Three-stage routing pipeline, pure functions | Core (pure) |
| `SmartRouter.Core/Ports.fs` | Interface definitions Core depends on | Core boundary |
| `SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` | HTTP forwarding to Qwen 35B / 122B, HF-id fix, model probe | Adapter (implements IUpstreamClient) |
| `SmartRouter.Cli/Adapters/QueueDispatcher.fs` | SemaphoreSlim(1) on 122B, priority queue, cancellation | Adapter (wraps IUpstreamClient) |
| `SmartRouter.Cli/Adapters/HealthAdapter.fs` | Polls upstream /health, exposes reachability | Adapter (implements IHealthProbe) |
| `SmartRouter.Cli/Adapters/Logging.fs` | Serilog → stderr wiring | Adapter |
| `SmartRouter.Cli/Adapters/Json.fs` | STJ options, request/response helpers | Adapter |
| `SmartRouter.Cli/Endpoints/ChatCompletions.fs` | Parses request, calls routing, dispatches to upstream, SSE forward | Adapter (ASP.NET handler) |
| `SmartRouter.Cli/Endpoints/Health.fs` | /health liveness + upstream reachability | Adapter |
| `SmartRouter.Cli/Endpoints/Models.fs` | /v1/models proxy + dedup | Adapter |
| `SmartRouter.Cli/Endpoints/Stats.fs` | /stats counters/gauges | Adapter |
| `SmartRouter.Cli/CompositionRoot.fs` | Wires all adapters, DI singleton registration | Adapter |
| `SmartRouter.Cli/Program.fs` | ASP.NET WebApplication builder, middleware, host | Adapter |
| `SmartRouter.Tests/` | Expecto unit + integration tests | Test harness |

---

## Recommended Project Structure

```
src/
├── SmartRouter.Core/
│   ├── SmartRouter.Core.fsproj
│   ├── Domain.fs           # All DUs and record types
│   ├── Routing.fs          # Pure routing pipeline
│   └── Ports.fs            # IUpstreamClient, IClock, IHealthProbe
│
├── SmartRouter.Cli/
│   ├── SmartRouter.Cli.fsproj
│   ├── Program.fs          # WebApplication builder, route registration
│   ├── CompositionRoot.fs  # DI wiring, singleton lifetimes
│   └── Adapters/
│       ├── QwenUpstreamClient.fs   # HTTP client, HF-id probe, error mapping
│       ├── QueueDispatcher.fs      # SemaphoreSlim(1) + priority queue
│       ├── HealthAdapter.fs        # Upstream health probing
│       ├── Logging.fs              # Serilog configuration
│       └── Json.fs                 # STJ options + wire helpers
│   └── Endpoints/
│       ├── ChatCompletions.fs      # POST /v1/chat/completions handler
│       ├── Health.fs               # GET /health
│       ├── Models.fs               # GET /v1/models
│       └── Stats.fs                # GET /stats
│
tests/
└── SmartRouter.Tests/
    ├── SmartRouter.Tests.fsproj
    ├── RoutingTests.fs             # Pure Core routing decision tests
    ├── HeuristicTests.fs           # Complexity scoring, keyword detection
    ├── QueueTests.fs               # Priority, semaphore enforcement
    ├── IntegrationTests.fs         # Fake upstream Kestrel servers
    ├── StreamingTests.fs           # Chunk ordering, cancellation
    └── RouterTests.fs              # [<EntryPoint>] + explicit rootTests list
```

### Structure Rationale

- **SmartRouter.Core/**: Zero dependency on ASP.NET, HttpClient, Serilog. Enforced by project reference — Core `.fsproj` has no NuGet packages beyond FsToolkit.ErrorHandling.
- **Adapters/ vs Endpoints/**: Adapters implement ports or provide infrastructure (HTTP client, queue). Endpoints are ASP.NET Minimal API handlers that orchestrate adapters. Both are adapter-side but separated by concern: Endpoints are HTTP-entry-facing, Adapters are infrastructure-facing.
- **QueueDispatcher.fs as separate adapter**: The semaphore and priority queue are not inside `QwenUpstreamClient` — they are a distinct adapter wrapping any `IUpstreamClient`. This preserves testability: you can inject a fake `IUpstreamClient` and test queue ordering without needing a real HTTP server.
- **RouterTests.fs with explicit rootTests**: Mirrors blueCode pattern to avoid Expecto auto-discovery unreliability (burned 4 executors in blueCode across v1.0 + v1.1).

---

## Core Domain Types

These are the concrete F# type signatures Core needs. Everything below lives in `SmartRouter.Core/Domain.fs`.

```fsharp
module SmartRouter.Core.Domain

open System

/// The two local Qwen models the router can target.
/// DU forces exhaustive match — adding a third model is a compile error in
/// all downstream functions until they handle the new case.
type ModelId =
    | Qwen35B   // localhost:8000, fast, lower quality
    | Qwen122B  // localhost:8001, slow, higher quality; concurrency cap = 1

/// Request priority for the 122B queue.
/// High: graph_indexing, compiler_debug, architecture_analysis
/// Low: dependency_analysis, reasoning, and heuristic-routed requests
type Priority =
    | High
    | Low

/// Why the routing decision was made. Carried in RoutingDecision for logging
/// and stats; purely informational — adapters log it, Core produces it.
type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string
    | ExplicitTask          of taskType: TaskType
    | Heuristic             of score: int
    | Default

/// Graphify task identifiers. DU membership is the authoritative task list.
/// Adding a task requires updating the routing table in Routing.fs — exhaustive match.
type TaskType =
    | GraphIndexing
    | CompilerDebug
    | ArchitectureAnalysis
    | DependencyAnalysis
    | Reasoning
    | Retrieval
    | Summary

/// Complete routing decision returned by Core.
/// Priority is included because it is a *property of the decision*, not an
/// adapter concern. The QueueDispatcher reads Priority to place the request
/// in the correct queue tier. Core decides Priority; adapter enforces it.
type RoutingDecision =
    { Target   : ModelId
      Priority : Priority
      Reason   : RoutingReason
      /// true = 122B unavailable and we fell back to 35B.
      /// false = normal routing.
      /// Never true for graph_indexing (that path must error).
      IsFallback : bool }

/// Incoming request from a consumer (Hermes / Graphify).
/// All fields are optional except Messages.
/// UnknownFields carries any unrecognized JSON keys so the adapter can
/// forward them upstream verbatim (PROJECT.md: "Preserve unknown fields").
type RouterRequest =
    { Messages     : Message list
      ModelOverride : string option   // "35b" | "122b" | any alias
      Task          : string option   // raw task string; Routing.fs parses to TaskType
      Stream        : bool
      Temperature   : float option
      TopP          : float option
      MaxTokens     : int option
      UnknownFields : Map<string, System.Text.Json.JsonElement> }

/// Errors the Core routing layer can produce.
/// Does NOT include HTTP errors — those are adapter-side (UpstreamError).
type RouterError =
    | InvalidRequest    of detail: string
    | UnsupportedTask   of raw: string
    | ModelUnavailable  of ModelId * detail: string
    | GraphIndexingMustFail           // graph_indexing with 122B unavailable: loud fail required

/// LLM wire message (same shape as blueCode; needed by IUpstreamClient port).
type MessageRole = System | User | Assistant

type Message = { Role: MessageRole; Content: string }
```

---

## Core Routing Types and Pipeline

These live in `SmartRouter.Core/Routing.fs`. Every function is pure — no IO, no logging.

```fsharp
module SmartRouter.Core.Routing

open SmartRouter.Core.Domain

// ── Stage 1: explicit model override ─────────────────────────────────────────

/// Parses a model alias string to ModelId.
/// Returns Some ModelId on match, None on unknown alias.
/// Pure; no mutation.
let tryParseModelAlias (s: string) : ModelId option =
    match s.ToLowerInvariant() with
    | "35b" | "qwen35b" | "qwen-35b" -> Some Qwen35B
    | "122b" | "qwen122b" | "qwen-122b" -> Some Qwen122B
    | _ -> None

/// Stage 1: if the request carries a recognizable model alias, return a
/// decision immediately. Short-circuits stages 2 and 3.
let tryModelOverride (req: RouterRequest) : RoutingDecision option =
    req.ModelOverride
    |> Option.bind tryParseModelAlias
    |> Option.map (fun model ->
        { Target     = model
          Priority   = if model = Qwen122B then Low else Low
          Reason     = ExplicitModelOverride(req.ModelOverride |> Option.defaultValue "")
          IsFallback = false })

// ── Stage 2: explicit task table ─────────────────────────────────────────────

/// Parse raw task string to TaskType. Returns None on unknown task.
/// Adapter should surface UnsupportedTask error for unknown strings.
let tryParseTaskType (raw: string) : TaskType option =
    match raw.ToLowerInvariant() with
    | "graph_indexing"        -> Some GraphIndexing
    | "compiler_debug"        -> Some CompilerDebug
    | "architecture_analysis" -> Some ArchitectureAnalysis
    | "dependency_analysis"   -> Some DependencyAnalysis
    | "reasoning"             -> Some Reasoning
    | "retrieval"             -> Some Retrieval
    | "summary"               -> Some Summary
    | _                       -> None

/// Authoritative task→model+priority table. Exhaustive over TaskType DU.
/// NEVER add | _ -> here — adding a TaskType case must be a compile error.
let taskToDecision: TaskType -> RoutingDecision =
    function
    | GraphIndexing ->
        { Target = Qwen122B; Priority = High
          Reason = ExplicitTask GraphIndexing; IsFallback = false }
    | CompilerDebug ->
        { Target = Qwen122B; Priority = High
          Reason = ExplicitTask CompilerDebug; IsFallback = false }
    | ArchitectureAnalysis ->
        { Target = Qwen122B; Priority = High
          Reason = ExplicitTask ArchitectureAnalysis; IsFallback = false }
    | DependencyAnalysis ->
        { Target = Qwen122B; Priority = Low
          Reason = ExplicitTask DependencyAnalysis; IsFallback = false }
    | Reasoning ->
        { Target = Qwen122B; Priority = Low
          Reason = ExplicitTask Reasoning; IsFallback = false }
    | Retrieval ->
        { Target = Qwen35B; Priority = Low
          Reason = ExplicitTask Retrieval; IsFallback = false }
    | Summary ->
        { Target = Qwen35B; Priority = Low
          Reason = ExplicitTask Summary; IsFallback = false }

/// Stage 2: if the request carries a recognized task string, return the
/// authoritative table decision. Returns None if task field absent or unknown.
let tryTaskTable (req: RouterRequest) : Result<RoutingDecision option, RouterError> =
    match req.Task with
    | None -> Ok None
    | Some raw ->
        match tryParseTaskType raw with
        | Some tt -> Ok(Some(taskToDecision tt))
        | None    -> Error(UnsupportedTask raw)

// ── Stage 3: heuristic ───────────────────────────────────────────────────────

/// Complexity keywords that nudge toward 122B.
/// Mirrors Graphify-relevant terms; extend without signature change.
let private complexKeywords =
    [ "recursive"; "dependency"; "lowering"; "mlir"; "llvm"; "compiler"
      "architecture"; "type inference"; "graph relation"; "closure conversion"
      "cross-file"; "multi-file"; "reasoning"; "inference"; "optimization"
      "refactor"; "redesign"; "abstract"; "formal"; "proof" ]

/// Compute a numeric complexity score from the request.
/// Pure: deterministic from input alone.
let scoreComplexity (req: RouterRequest) : int =
    let allText =
        req.Messages
        |> List.map (fun m -> m.Content)
        |> String.concat " "
        |> fun s -> s.ToLowerInvariant()

    let totalChars = allText.Length
    let msgCount   = req.Messages |> List.length
    let hasCode    = allText.Contains("```")

    let keywordScore =
        complexKeywords
        |> List.filter allText.Contains
        |> List.length

    let lengthScore =
        if   totalChars > 8000 then 4
        elif totalChars > 4000 then 2
        elif totalChars > 2000 then 1
        else 0

    let msgScore   = if msgCount > 6 then 2 elif msgCount > 3 then 1 else 0
    let codeScore  = if hasCode then 1 else 0

    keywordScore + lengthScore + msgScore + codeScore

/// Stage 3: heuristic routing.
/// Threshold = 3: below → 35B (aggressive 35B preference for ambiguous cases).
/// Hermes path always lands here (no task field).
let applyHeuristic (req: RouterRequest) : RoutingDecision =
    let score = scoreComplexity req
    let target = if score >= 3 then Qwen122B else Qwen35B
    { Target     = target
      Priority   = Low
      Reason     = Heuristic score
      IsFallback = false }

// ── Pipeline entry point ──────────────────────────────────────────────────────

/// Three-stage pure routing pipeline.
/// Returns Ok RoutingDecision or Error RouterError.
/// No IO. No logging. No clock.
let routeRequest (req: RouterRequest) : Result<RoutingDecision, RouterError> =
    match tryModelOverride req with
    | Some decision -> Ok decision
    | None ->
        match tryTaskTable req with
        | Error e          -> Error e
        | Ok (Some decision) -> Ok decision
        | Ok None          -> Ok (applyHeuristic req)
```

---

## Core Ports

These live in `SmartRouter.Core/Ports.fs`. All interfaces; no implementations.

```fsharp
module SmartRouter.Core.Ports

open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open SmartRouter.Core.Domain

/// Upstream LLM server contract.
/// The adapter layer implements this; Core only calls it via RoutingDecision.Target.
/// Returns streaming body as an async sequence — see SSE Seam section below.
/// For non-streaming calls, the sequence emits exactly one element (the full body).
type IUpstreamClient =
    /// Non-streaming call: returns the full response body string.
    abstract member CompleteAsync:
        req: RouterRequest
        -> target: ModelId
        -> ct: CancellationToken
        -> Task<Result<string, RouterError>>

    /// Streaming call: returns a sequence of raw SSE chunks (byte arrays or strings).
    /// Sequence is lazy — each element is read as it arrives from the upstream server.
    /// The endpoint handler writes each chunk to HttpContext.Response as it arrives.
    abstract member StreamAsync:
        req: RouterRequest
        -> target: ModelId
        -> ct: CancellationToken
        -> IAsyncEnumerable<Result<string, RouterError>>

/// Clock abstraction — needed by Stats adapter to record timestamps.
/// Core does not currently call IClock, but it is defined here so adapters
/// can depend on it via DI without touching System.DateTime directly.
type IClock =
    abstract member UtcNow: unit -> System.DateTimeOffset

/// Upstream health probe — called by HealthAdapter, not by Core routing.
/// Defined in Ports.fs so it can be injected into the Health endpoint
/// without creating a dependency on the adapter assembly.
type IHealthProbe =
    abstract member IsReachableAsync:
        target: ModelId
        -> ct: CancellationToken
        -> Task<bool>
```

---

## Architectural Patterns

### Pattern 1: QueueDispatcher Wraps IUpstreamClient

**What:** A separate adapter `QueueDispatcher` holds the `SemaphoreSlim(1)` for 122B and the two-level priority queue. It implements `IUpstreamClient` and wraps the real `QwenUpstreamClient` (also `IUpstreamClient`). The endpoint sees only `IUpstreamClient`; it does not know about the queue.

**Why here and not inside QwenUpstreamClient:** Separation of concerns. `QwenUpstreamClient` is responsible for HTTP mechanics (HF-id probe, error mapping, streaming). `QueueDispatcher` is responsible for concurrency policy. Swapping the concurrency policy doesn't touch the HTTP layer and vice versa. Also: unit tests for queue ordering don't need a real HTTP server — inject a fake `IUpstreamClient`.

**DI wiring in CompositionRoot:**
```fsharp
let qwenClient    = QwenUpstreamClient.create config
let queueDispatch = QueueDispatcher.create qwenClient  // wraps it
// Endpoints receive queueDispatch (IUpstreamClient)
```

**Queue structure:**
```fsharp
// Inside QueueDispatcher.fs (adapter, not Core)
type private QueueItem =
    { Request    : RouterRequest
      Target     : ModelId
      Priority   : Priority
      Ct         : CancellationToken
      Completion : TaskCompletionSource<Result<string, RouterError>> }

// Two-level FIFO: high items dequeued before low items
type private PriorityQueue =
    { High : Queue<QueueItem>
      Low  : Queue<QueueItem> }

// The one semaphore for 122B
let private sem122B = new SemaphoreSlim(1, 1)
```

**Priority comes from Core:** `RoutingDecision.Priority` is set by `Routing.routeRequest`. The endpoint extracts it from the decision and passes it when enqueuing. Core declares the priority; the adapter enforces it.

### Pattern 2: SSE Pass-Through — The Streaming Seam

**The problem:** Core must never see `HttpResponseMessage` or `HttpContext`. But streaming SSE requires piping bytes from the upstream response body directly to the downstream response stream. How does streaming cross the port boundary cleanly?

**Solution:** `IUpstreamClient.StreamAsync` returns `IAsyncEnumerable<Result<string, RouterError>>`. Each element is one SSE chunk (the raw `data: {...}\n\n` line as a string). The endpoint handler consumes the async enumerable and writes each chunk to `HttpContext.Response` immediately, flushing after each write.

**Core's role:** Zero. Core calls `routeRequest`, returns a `RoutingDecision`. Core never touches streaming. The endpoint does this:

```fsharp
// ChatCompletions.fs (Endpoint, adapter side)
let handler (routing: IRoutingService) (upstream: IUpstreamClient)
            (ctx: HttpContext) : Task =
    task {
        // 1. Parse request
        let! body = ctx.Request.ReadFromJsonAsync<RouterRequestWire>(...)
        let req = mapWireToRequest body

        // 2. Route (pure, no IO)
        match SmartRouter.Core.Routing.routeRequest req with
        | Error (UnsupportedTask raw) ->
            ctx.Response.StatusCode <- 400
            do! ctx.Response.WriteAsJsonAsync({| error = $"unknown task: {raw}" |})
        | Error (GraphIndexingMustFail) ->
            ctx.Response.StatusCode <- 503
            do! ctx.Response.WriteAsJsonAsync({| error = "graph_indexing: 122B unavailable, no fallback" |})
        | Error e ->
            ctx.Response.StatusCode <- 400
            do! ctx.Response.WriteAsJsonAsync({| error = string e |})
        | Ok decision ->

        // 3. Log decision (Serilog — adapter side only)
        log.Information("Routing {Target} reason={Reason} priority={Priority}",
                        decision.Target, decision.Reason, decision.Priority)

        // 4. Dispatch
        if req.Stream then
            ctx.Response.ContentType <- "text/event-stream"
            ctx.Response.Headers["Cache-Control"] <- "no-cache"
            ctx.Response.Headers["X-Accel-Buffering"] <- "no"
            let ct = ctx.RequestAborted

            let chunks = upstream.StreamAsync(req, decision.Target, ct)
            let mutable enumerator = chunks.GetAsyncEnumerator(ct)
            try
                let mutable go = true
                while go do
                    let! hasNext = enumerator.MoveNextAsync()
                    if not hasNext then
                        go <- false
                    else
                        match enumerator.Current with
                        | Ok chunk ->
                            do! ctx.Response.WriteAsync(chunk, ct)
                            do! ctx.Response.Body.FlushAsync(ct)
                        | Error e ->
                            // log and break; partial SSE already sent
                            log.Error("Upstream stream error: {E}", e)
                            go <- false
            finally
                do! enumerator.DisposeAsync()
        else
            match! upstream.CompleteAsync(req, decision.Target, ctx.RequestAborted) with
            | Ok body ->
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsync(body, ctx.RequestAborted)
            | Error e ->
                ctx.Response.StatusCode <- 502
                do! ctx.Response.WriteAsJsonAsync({| error = string e |})
    }
```

**In QwenUpstreamClient.StreamAsync:** Uses `HttpCompletionOption.ResponseHeadersRead` so the response body is not buffered. Reads the response stream line-by-line and yields each non-empty SSE line as an element.

```fsharp
// QwenUpstreamClient.fs (Adapter)
member _.StreamAsync(req, target, ct) =
    // Returns IAsyncEnumerable<Result<string, RouterError>>
    asyncSeq {
        let url = targetToUrl target
        use reqMsg = buildHttpRequest req url
        use! resp = httpClient.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, ct)
        if not resp.IsSuccessStatusCode then
            yield Error(ModelUnavailable(target, $"HTTP {int resp.StatusCode}"))
        else
            use stream = resp.Content.ReadAsStream()
            use reader = new StreamReader(stream)
            let mutable isDone = false
            while not isDone && not ct.IsCancellationRequested do
                let! line = reader.ReadLineAsync(ct)  // or ReadLineAsync()
                match line with
                | null -> isDone <- true
                | ""   -> ()   // skip blank lines between chunks
                | s    -> yield Ok s
    }
    // Note: asyncSeq from FSharp.Control.TaskSeq or manual IAsyncEnumerable implementation
```

**Key constraint:** `HttpResponseMessage` never crosses the port boundary. The streaming enumerable carries only `string` (the raw SSE line). The port signature `IAsyncEnumerable<Result<string, RouterError>>` is purely F# types — no ASP.NET or HttpClient types.

### Pattern 3: Fallback Policy in Endpoint, Not Core

**What:** When 122B is unavailable, Core's routing decision still says `Target = Qwen122B`. The endpoint (or QueueDispatcher) checks `IHealthProbe.IsReachableAsync` and decides whether to fall back to 35B or return an error.

**Why not in Core:** The health probe is I/O (network call). Core is pure. The fallback rule is:
- `graph_indexing` + 122B unavailable → return 503 (no fallback)
- All other 122B routes + 122B unavailable → reroute to 35B + mark `IsFallback = true`

**Implementation choice:** This lives in the `ChatCompletions` endpoint handler (or in `QueueDispatcher.CompleteAsync`). The cleanest place is `QueueDispatcher` because it already owns the 122B availability concern and wraps all upstream calls. QueueDispatcher checks health before enqueuing for 122B; if unhealthy and the reason is `GraphIndexingMustFail`, it returns the error immediately.

### Pattern 4: Stats Counters in a Singleton Adapter

**What:** Mutable counters (requests/sec, queue depth, active requests, average latency) live in a DI-registered singleton `StatsCollector` class. Endpoints increment counters; the Stats endpoint reads them.

**Why not in Core:** Mutable state is inherently side-effecting. Atomic counter increments, `Interlocked` operations, and `Stopwatch` are adapter concerns.

**Interface in Core (optional):** If Core ever needed to emit timing data, an `IStatsCollector` port could be defined. For v1, Core produces no timing data — all timing is measured in the adapter layer (endpoint handler wraps the upstream call with `Stopwatch`).

---

## Data Flow

### Request Lifecycle (Text Sequence Diagram)

```
Hermes / Graphify
    │
    │  POST /v1/chat/completions  (OpenAI-compat JSON, stream=true)
    ▼
ChatCompletions.fs  (Endpoint handler)
    │
    │  1. Parse raw JSON body → RouterRequest
    │     (UnknownFields map preserves non-OpenAI keys for upstream forwarding)
    │
    │  2. SmartRouter.Core.Routing.routeRequest(req)
    │     Pure function, no IO:
    │       tryModelOverride → None
    │         tryTaskTable  → None (Hermes has no task field)
    │           applyHeuristic → RoutingDecision { Target=Qwen35B; Priority=Low; Reason=Heuristic(2) }
    │     Returns: Ok RoutingDecision
    │
    │  3. Log decision via Serilog (adapter side)
    │
    │  4. req.Stream = true → set Content-Type: text/event-stream, no-cache headers
    │
    │  5. IUpstreamClient.StreamAsync(req, decision.Target, ctx.RequestAborted)
    │        ← this is QueueDispatcher wrapping QwenUpstreamClient
    ▼
QueueDispatcher.StreamAsync
    │
    │  decision.Target = Qwen35B → skip semaphore, call directly
    │  (35B has no concurrency cap; HttpClient connection pool is the natural limit)
    │
    │  decision.Target = Qwen122B:
    │    a. Check IHealthProbe.IsReachableAsync(Qwen122B)
    │       → if unreachable and reason=GraphIndexing → return Error GraphIndexingMustFail immediately
    │       → if unreachable and other → reroute to Qwen35B, IsFallback=true
    │    b. Enqueue QueueItem { Priority = decision.Priority, Ct = ct, ... }
    │    c. Worker loop: dequeue high items first, then low items
    │    d. sem122B.WaitAsync(ct)    ← blocks here if another 122B call is in flight
    │       If ct fires (client disconnected): sem not acquired, item discarded
    ▼
QwenUpstreamClient.StreamAsync
    │
    │  1. probeModelInfo (lazy, fires once per process per port)
    │     → GET localhost:800x/v1/models
    │     → tryParseModelId: prefer path-starting-with-"/" to avoid HF tokenizer fallback
    │
    │  2. Build POST body:
    │     → messages, model=<local-path-id>, stream=true, temperature, top_p, max_tokens
    │     → UnknownFields forwarded verbatim
    │
    │  3. httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
    │     → response headers arrive, body stream not yet consumed
    │
    │  4. Yield IAsyncEnumerable<Result<string, RouterError>>:
    │     while not done:
    │       reader.ReadLineAsync(ct) → raw SSE line string
    │       yield Ok lineString
    │     on ct cancel: IAsyncEnumerable terminates (OperationCanceledException caught)
    │     on stream end: null ReadLine → sequence completes
    ▼
QueueDispatcher (back in the calling context)
    │
    │  After sequence completes (normal or cancelled):
    │    sem122B.Release()     ← releases semaphore unconditionally (finally block)
    ▼
ChatCompletions.fs (Endpoint, consuming the IAsyncEnumerable)
    │
    │  foreach chunk in enumerable:
    │    ctx.Response.WriteAsync(chunk, ct)
    │    ctx.Response.Body.FlushAsync(ct)
    │    (if ct fires mid-stream → WriteAsync/FlushAsync throw OperationCanceledException
    │     → caught by endpoint handler → upstream enumerable abandoned → GC disposes enumerator
    │     → QueueDispatcher finally block releases semaphore)
    ▼
Hermes / Graphify (SSE stream consumed)
```

### Cancellation Propagation

`CancellationToken` flows from `HttpContext.RequestAborted` through:

1. `ChatCompletions.fs` handler: passes `ctx.RequestAborted` as `ct` to all async calls
2. `QueueDispatcher.StreamAsync`: passes `ct` to `sem122B.WaitAsync(ct)` — if client disconnects while waiting, the wait is cancelled, the item is dropped, semaphore not acquired
3. `QwenUpstreamClient.StreamAsync`: passes `ct` to `httpClient.SendAsync(...)` and to each `ReadLineAsync(ct)` call — if client disconnects mid-stream, the HTTP call is aborted
4. `QueueDispatcher` finally block: `sem122B.Release()` fires unconditionally — semaphore always released even on cancellation

This matches the blueCode `postAsync` pattern where `TaskCanceledException` with `ex.CancellationToken = ct` maps to `UserCancelled`. For the router, the analogous path is: ct fires → `OperationCanceledException` propagates through the async enumerable → endpoint catches or the enumerable simply stops → `QueueDispatcher.finally` releases semaphore.

---

## Scalability Considerations

This router is single-host, two-model, loopback-only. Traditional scalability axes don't apply. The meaningful concerns are:

| Concern | Approach | Why |
|---------|----------|-----|
| 122B concurrency | SemaphoreSlim(1) | mlx_lm.server serializes at the metal layer; application-layer semaphore is cheaper and more explicit |
| 35B concurrency | HttpClient connection pool (default ~10) | 35B is fast; parallel calls are safe; pool prevents runaway file descriptors |
| Queue depth | Unbounded in v1 (monitor via /stats) | Add bounded queue + 429 response if /stats shows chronic depth > N |
| Memory | Stateless per request; queue + counters are small | No session state in router; consumers own conversation history |
| Cold start latency | 300s HttpClient timeout (mirrors blueCode Phase 20-01) | 122B cold-start observed up to 240s after launchctl kickstart |

---

## Anti-Patterns

### Anti-Pattern 1: Queue and Semaphore Inside QwenUpstreamClient

**What people do:** Put `SemaphoreSlim` and the priority queue directly inside `QwenUpstreamClient.StreamAsync`.

**Why it's wrong:** Couples HTTP mechanics with concurrency policy. Cannot test queue ordering without a real HTTP server. Cannot swap the HTTP client without reimplementing the queue. Violates single responsibility.

**Do this instead:** `QueueDispatcher` wraps `IUpstreamClient`. Queue and semaphore are in `QueueDispatcher`. `QwenUpstreamClient` only does HTTP.

### Anti-Pattern 2: Returning HttpResponseMessage Across the Port Boundary

**What people do:** `IUpstreamClient.StreamAsync` returns `Task<HttpResponseMessage>`.

**Why it's wrong:** `HttpResponseMessage` is an `HttpClient` type. Returning it through the port boundary means Core (or whoever consumes the port) must reference `System.Net.Http`. The hexagonal invariant is broken — Core must never reference HTTP client types.

**Do this instead:** `IAsyncEnumerable<Result<string, RouterError>>` — pure F# types. The adapter materializes the response into the enumerable; the port contract is clean.

### Anti-Pattern 3: Logging in Core

**What people do:** Pass `ILogger` into `routeRequest` for observability.

**Why it's wrong:** Logging is a side effect. `routeRequest` is a pure function. Adding a logger makes it untestable without a real logger and breaks the hexagonal invariant.

**Do this instead:** `RoutingDecision` carries `RoutingReason` and `Priority`. The endpoint reads these after the pure call and logs them via Serilog. No logger in Core.

### Anti-Pattern 4: `async {}` in Core

**What people do:** Write `async { let! x = ... }` in `Domain.fs` or `Routing.fs`.

**Why it's wrong:** `async {}` is banned in Core (blueCode CI enforces this via `scripts/check-no-async.sh`; mirror this in smart-router). Core functions are either pure (no CE at all) or use `task {}` at the port boundary.

**Do this instead:** Pure routing functions return plain values. Port interfaces return `Task<_>`. Core code that orchestrates ports uses `task {}`.

### Anti-Pattern 5: Materializing the Full SSE Response Before Forwarding

**What people do:** `ReadAsStringAsync()` on the upstream response, then write the whole body at once.

**Why it's wrong:** Defeats the purpose of streaming. The client (Hermes) sees no output until the entire 122B response finishes. 122B responses can take 45–240 seconds. Interactive experience is destroyed.

**Do this instead:** `HttpCompletionOption.ResponseHeadersRead` + `StreamReader.ReadLineAsync` loop + `Response.WriteAsync` + `Body.FlushAsync` per chunk.

### Anti-Pattern 6: Fallback Policy Hard-Coded in Core

**What people do:** `routeRequest` calls `IHealthProbe.IsReachableAsync` and rewrites the target in-place.

**Why it's wrong:** Health probe is I/O. Core must be pure. `routeRequest` taking an `IHealthProbe` means it cannot be tested without a fake implementation and it is no longer a pure function.

**Do this instead:** Core returns a `RoutingDecision { Target = Qwen122B }`. The adapter layer (QueueDispatcher or endpoint handler) checks health and applies fallback. The fallback is a policy decision in the adapter, informed by the Core decision.

---

## Integration Points

### External Services

| Service | Integration Pattern | Notes |
|---------|---------------------|-------|
| Qwen 35B (localhost:8000) | `QwenUpstreamClient` POST via HttpClientFactory | HF-id trap: copy `tryParseModelId` from blueCode verbatim |
| Qwen 122B (localhost:8001) | `QwenUpstreamClient` POST via HttpClientFactory + `QueueDispatcher` semaphore | 300s timeout; `ResponseHeadersRead` for streaming |
| Hermes Agent | Passive (consumer of `/v1/chat/completions`) | No task field; routing is pure heuristic |
| Graphify | Passive (consumer of `/v1/chat/completions`) | Sends `task` field; task table is authoritative |

### Internal Boundaries

| Boundary | Communication | Rule |
|----------|---------------|------|
| Endpoint → Core | Direct function call (`routeRequest`) | Synchronous, pure; no async crossing this boundary |
| Endpoint → Adapter | Via `IUpstreamClient` interface | Always async `Task<_>` or `IAsyncEnumerable<_>` |
| Core → Adapter | Via port interfaces (`IUpstreamClient`, `IClock`) | Core never calls adapters directly; only via interfaces |
| CompositionRoot → DI | `services.AddSingleton<IUpstreamClient>(queueDispatcher)` | `QueueDispatcher` registered as the `IUpstreamClient` singleton |

---

## Build Order

Build order respects dependency graph: Core types must exist before adapters can reference them; adapters must exist before endpoints; all must exist before tests.

| Phase | Ships | Rationale |
|-------|-------|-----------|
| 1 | `SmartRouter.Core` (Domain + Routing + Ports) | Foundation; all downstream depends on this. Pure types and functions; no external dependencies. |
| 2 | `SmartRouter.Cli/Adapters/Json.fs` + `Logging.fs` | Infrastructure adapters needed by all other adapters. Copied from blueCode with minimal changes. |
| 3 | `SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` | HTTP adapter. Implements `IUpstreamClient`. Depends on Core types + Json/Logging adapters. Copy HF-id probe logic from blueCode verbatim. |
| 4 | `SmartRouter.Cli/Adapters/QueueDispatcher.fs` | Concurrency adapter. Wraps `QwenUpstreamClient`. SemaphoreSlim(1) + priority queue. Requires `QwenUpstreamClient` to exist first. |
| 5 | `SmartRouter.Cli/Adapters/HealthAdapter.fs` | Health probe adapter. Depends on `QwenUpstreamClient` (or its own HTTP client). |
| 6 | `SmartRouter.Cli/Endpoints/` (all four) | Endpoint handlers. Depend on all adapters being wired. `ChatCompletions.fs` is the most complex — complete last within this phase. |
| 7 | `SmartRouter.Cli/CompositionRoot.fs` + `Program.fs` | DI wiring and host builder. Depends on all adapters and endpoints. |
| 8 | `SmartRouter.Tests/` (Core unit tests first) | Core routing tests require only Phase 1. Integration tests require Phases 3–7. Run Core tests to validate pure logic before adapter complexity is introduced. |

---

## blueCode Reuse vs Replacement

| blueCode Component | SmartRouter Treatment | Rationale |
|--------------------|-----------------------|-----------|
| `QwenHttpClient.fs` → `tryParseModelId` | **Copy verbatim** into `QwenUpstreamClient.fs` | HF-id trap is load-bearing. Identical mlx_lm.server behavior on both ports. |
| `QwenHttpClient.fs` → `probeModelInfoAsync` | **Copy, adapt** (remove blueCode logging labels) | Same probe logic; just rename. |
| `QwenHttpClient.fs` → `postAsync` error mapping | **Copy, adapt** for streaming (`ResponseHeadersRead`) | Error mapping logic is identical; streaming path is new. |
| `Adapters/Json.fs` | **Copy verbatim** | STJ options are project-agnostic. |
| `Adapters/Logging.fs` | **Copy verbatim** | Serilog → stderr wiring is identical. |
| `CompositionRoot.fs` structure | **Model pattern, rewrite content** | DI wiring pattern is the same; domain is different (no AgentLoop, no ToolExecutor). |
| `Core/Domain.fs` | **Rewrite** | blueCode's domain is an agent loop (Tool, Step, AgentState). SmartRouter's domain is a routing gateway (RouterRequest, RoutingDecision, Priority). No overlap. |
| `Core/Router.fs` pattern | **Mirror pattern, rewrite content** | Pure functions, exhaustive matches, no `| _ ->`. blueCode's `classifyIntent`/`intentToModel` shape becomes `routeRequest`/`taskToDecision`. |
| `Core/Ports.fs` | **Rewrite** | blueCode ports: `ILlmClient`, `IToolExecutor`. SmartRouter ports: `IUpstreamClient`, `IClock`, `IHealthProbe`. Same hexagonal discipline, different interfaces. |
| Test pattern (explicit `rootTests`) | **Copy pattern** | Critical. Expecto auto-discovery is unreliable. Mirror `RouterTests.fs` entrypoint with explicit list. |
| `task {}` over `async {}` enforcement | **Mirror** | Same CI grep check. `scripts/check-no-async.sh` to be created in smart-router. |

---

## Sources

- Direct analysis: `/Users/ohama/projs/blueCode/src/BlueCode.Core/Domain.fs`
- Direct analysis: `/Users/ohama/projs/blueCode/src/BlueCode.Core/Ports.fs`
- Direct analysis: `/Users/ohama/projs/blueCode/src/BlueCode.Core/Router.fs`
- Direct analysis: `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/QwenHttpClient.fs`
- Direct analysis: `/Users/ohama/projs/blueCode/src/BlueCode.Cli/CompositionRoot.fs`
- Direct analysis: `/Users/ohama/projs/blueCode/CLAUDE.md`
- Direct analysis: `/Users/ohama/projs/smart-router/.planning/PROJECT.md`

---
*Architecture research for: SmartRouter — F# hexagonal LLM router*
*Researched: 2026-05-07*
