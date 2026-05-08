# Phase 5: Routing-Decision Logging — Research

**Researched:** 2026-05-08
**Domain:** F# .NET 10 — thread-safe JSONL logging, Channel-backed BackgroundService, ASP.NET Core correlation-ID middleware, daily file rotation
**Confidence:** HIGH

---

## Summary

Phase 5 delivers the JSONL decision log that is Loop B's sole input. Every field added later requires consumer-side migration or backfill, so the schema must be complete and versioned on day one.

The blueCode `JsonlSink.fs` is the prior art — a single-file `StreamWriter` with `AutoFlush=true` that is opened once and disposed on shutdown. It is correct for single-threaded sessions but **not thread-safe for concurrent HTTP requests**. Phase 5 must replace this pattern with a `Channel<DecisionLog>`-backed `BackgroundService`: one channel, one writer task draining it, zero race conditions. The channel pattern is the standard .NET solution for high-throughput concurrent append logging; it avoids both `File.AppendAllText` (IOException / interleaved bytes under concurrency) and Serilog's `Sinks.File` (opaque file handle lifecycle, harder to test).

Key recommendations: use `SHA-256` for `prompt_hash` (BCL, no NuGet, good enough for deduplication; can swap later if profiling shows hotspot); add `schema_version: 1` (negligible cost, essential for Phase 8/9 consumers forking on schema evolution); add `prompt_korean_char_ratio` (single regex, enables Phase 9 cohort comparison that justifies the bge-m3 migration). Log after response in the endpoint — only then is `latency_ms` complete.

**Primary recommendation:** Channel-backed BackgroundService writer; SHA-256 prompt hash; include `schema_version` and `prompt_korean_char_ratio` from day one; log at endpoint exit (Option A).

---

## DECISION REQUIRED: Two Schema Fields

### `schema_version` — RECOMMEND INCLUDE

Add `"schema_version": 1` (int) to every log line.

**Pro:** Phase 8 retraining loop reads `decisions/*.jsonl`. If Phase 7 or 9 adds a field, consumers need to know which lines have it. A version field lets the reader fork: `if schema_version < 2 then use_fallback_for_new_field`. Cost: one int field per line.

**Con:** Minor JSON noise. No real downside.

**Verdict: Include.** The incremental cost is ~15 bytes per line. The alternative is an ad-hoc "check if field exists" pattern scattered across all consumers — brittle and invisible.

### `prompt_korean_char_ratio` — RECOMMEND INCLUDE

Add `"prompt_korean_char_ratio": float` (0.0–1.0, computed once per request).

```fsharp
let koreanCharRatio (text: string) : float =
    if text.Length = 0 then 0.0
    else
        let koreanCount =
            text |> Seq.filter (fun c -> c >= '가' && c <= '힣') |> Seq.length
        float koreanCount / float text.Length
```

**Pro:** The distillation research (`design-two-loop-router.md`) identifies mixed Korean/English traffic as the migration trigger for bge-m3. Phase 9 canary compares cohorts: "did int8 bge-m3 improve routing on Korean-heavy prompts specifically?" Without this field, cohort comparison requires re-parsing raw prompts — impossible if they were discarded or hashed. Computation cost: one O(N) scan per request; negligible vs. LLM latency.

**Con:** Schema width grows slightly. Privacy: ratio only, not content — no issue.

**Verdict: Include.** The routing operator's traffic is Korean+English mixed. This is the one field that distinguishes cohorts for the Phase 9 canary. Omitting it now means either (a) backfilling old log files (unreliable), or (b) running the canary without cohort data (useless). Add it now.

---

## Locked Schema (LOG-01 + two new fields)

```fsharp
// Cli-only. Lives in SmartRouter.Cli, NOT SmartRouter.Core (pure-Core invariant).
type DecisionLog =
    { schema_version         : int              // always 1 in Phase 5
      correlation_id         : string           // Guid.NewGuid().ToString("N") from middleware
      prompt_hash            : string           // SHA-256 hex of concatenated messages (lowercase hex)
      routing_algorithm      : string           // "heuristic" | "ml"
      routing_reason         : string           // string representation of RoutingReason DU
      target                 : string           // "Qwen35B" | "Qwen122B"
      latency_ms             : float            // (clock.UtcNow() - started).TotalMilliseconds
      fallback_used          : bool             // always false in Phase 5; IsFallback from RoutingDecision
      model_version          : string           // "heuristic-v1" | "ml-v0-placeholder"
      task_type              : string option    // req.Task (None if not set)
      prompt_korean_char_ratio : float          // 0.0–1.0; ratio of Hangul chars in all messages
      timestamp              : DateTimeOffset } // DateTimeOffset.UtcNow at log-write time
```

**Field notes:**

- `schema_version`: int, always `1`. Next breaking schema change bumps to `2`.
- `prompt_hash`: SHA-256 of `String.concat "" (messages |> List.map _.Content)` — full conversation, not just last user message. Rationale: the heuristic uses all messages for scoring; the hash should cover the same input. Hex-encoded, lowercase, 64 chars.
- `routing_algorithm`: derived from which algorithm function was invoked — "heuristic" when `Heuristic.applyHeuristic` ran, "ml" when `ML.applyML` ran. Determined at the endpoint, not in Core.
- `routing_reason`: `sprintf "%A" decision.Reason` or a hand-rolled serializer. Use a dedicated `formatReason` function, not `%A`, to avoid F# DU reflection strings in the log (`Heuristic 3` not `Heuristic(3)`).
- `model_version`: static string in Phase 5. Heuristic path: `"heuristic-v1"`. ML path: `"ml-v0-placeholder"`. Phase 6 real value: short hash of `router.zip`. This lives in the Cli adapter layer (not Core).
- `fallback_used`: maps from `decision.IsFallback`. Always `false` in Phase 5 (IsFallback is only true when 122B unavailable + task is non-graph-indexing).
- `timestamp`: UTC at the moment the log entry is enqueued (after the response is sent).

---

## Standard Stack

### Core (all already in the project)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `System.Threading.Channels` | BCL (.NET 10) | `Channel<DecisionLog>` — bounded MPSC queue | Built-in; no NuGet; correct for producer-consumer logging |
| `System.Security.Cryptography.SHA256` | BCL | `prompt_hash` computation | BCL; no NuGet; 64-char hex output; production-adequate speed |
| `System.Text.Json` | BCL | JSONL serialization | Already used in project; `JsonSerializer.Serialize` produces valid JSON per line |
| `Microsoft.Extensions.Hosting.BackgroundService` | via `Microsoft.AspNetCore.App` | Single-writer consumer loop | Built-in; `IHostedService` lifecycle ties to ASP.NET host start/stop |
| `FSharp.SystemTextJson` | 1.4.36 (already pinned) | F# records + option serialization | Already in `SmartRouter.Cli.fsproj`; required for `string option` fields |
| Serilog | 4.3.1 (already pinned) | Correlation ID in `LogContext` | Already configured in `Adapters/Logging.fs`; `LogContext.PushProperty` for structured logging |

### New NuGet required
None. All needed libraries are BCL or already referenced.

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `SHA-256` (BCL) | xxHash / Blake3 (NuGet) | xxHash is 10-50x faster but requires a NuGet dependency. For prompts up to 32KB, SHA-256 takes ~0.1ms — not a hotspot vs. LLM latency. Switch only if profiling shows real impact. |
| `Channel<T>` + `BackgroundService` | `Serilog.Sinks.File` + `CompactJsonFormatter` | Serilog sink works but the file handle lifecycle is opaque and harder to test. Channel gives explicit control over shutdown drain, bounded-queue drop policy, and file rotation without Serilog internals. |
| `Channel<T>` + `BackgroundService` | `File.AppendAllText` | FORBIDDEN. Not thread-safe. Concurrent calls produce IOException or interleaved bytes. Confirmed pitfall in distillation research §3.2 #2 and PITFALLS.md. |
| `StreamWriter` per-line + `AutoFlush` (blueCode pattern) | Channel + single writer | blueCode's `JsonlSink` is correct for single-writer sessions. For concurrent HTTP, it needs external locking or — better — the Channel pattern where a single background consumer holds the StreamWriter exclusively. |

---

## Architecture Patterns

### Recommended File Structure (new files only)

```
src/SmartRouter.Cli/
├── Adapters/
│   ├── DecisionLogger.fs          # IDecisionLogger interface + DecisionLog record
│   ├── DecisionLogWriter.fs       # BackgroundService: Channel consumer, file writer, rotation
│   └── CorrelationMiddleware.fs   # ASP.NET middleware: inject correlation_id per request
tests/SmartRouter.Tests/
└── LoggingTests.fs                # 100-concurrent, correlation-ID, shutdown-flush, schema tests
```

**Compile order in `SmartRouter.Cli.fsproj`:**
```
Adapters/Json.fs
Adapters/Logging.fs
Adapters/DecisionLogger.fs         ← NEW (declares IDecisionLogger, DecisionLog)
Adapters/DecisionLogWriter.fs      ← NEW (BackgroundService; depends on DecisionLogger)
Adapters/CorrelationMiddleware.fs  ← NEW (depends on nothing project-specific)
Adapters/QwenUpstreamClient.fs
Adapters/QueueDispatcher.fs
Endpoints/ChatCompletions.fs       ← MODIFIED (inject IDecisionLogger, log at exit)
Endpoints/Stats.fs
CompositionRoot.fs                 ← MODIFIED (register services)
Program.fs                         ← MODIFIED (add middleware + model_version to config)
```

---

### Pattern 1: `DecisionLog` record + `IDecisionLogger` interface

```fsharp
// src/SmartRouter.Cli/Adapters/DecisionLogger.fs
module SmartRouter.Cli.Adapters.DecisionLogger

open System
open SmartRouter.Core.Domain

/// Compute SHA-256 hex of concatenated message content.
/// Full conversation, not just last user message — matches what the heuristic scores.
let computePromptHash (messages: Message list) : string =
    let text = messages |> List.map (fun m -> m.Content) |> String.concat ""
    use sha = System.Security.Cryptography.SHA256.Create()
    let bytes = System.Text.Encoding.UTF8.GetBytes(text)
    let hash  = sha.ComputeHash(bytes)
    hash |> Array.map (sprintf "%02x") |> String.concat ""

/// Ratio of Hangul syllable block characters in all messages.
/// Used for Phase 9 canary cohort comparison (Korean vs. non-Korean traffic).
let computeKoreanRatio (messages: Message list) : float =
    let text = messages |> List.map (fun m -> m.Content) |> String.concat ""
    if text.Length = 0 then 0.0
    else
        let n = text |> Seq.filter (fun c -> c >= '가' && c <= '힣') |> Seq.length
        float n / float text.Length

/// Human-readable routing reason — avoids F# DU %A reflection strings.
let formatReason (reason: RoutingReason) : string =
    match reason with
    | ExplicitModelOverride alias -> sprintf "explicit_model:%s" alias
    | ExplicitTask taskType       -> sprintf "explicit_task:%A" taskType
    | Heuristic score             -> sprintf "heuristic:score=%d" score
    | Default                     -> "default"
    | ML                          -> "ml"

/// One JSONL line per routing decision. Cli-only — pure F# record, no Core references beyond Domain.
/// Schema version 1. All fields required by LOG-01 plus prompt_korean_char_ratio and schema_version.
[<CLIMutable>]
type DecisionLog =
    { schema_version           : int
      correlation_id           : string
      prompt_hash              : string
      routing_algorithm        : string
      routing_reason           : string
      target                   : string
      latency_ms               : float
      fallback_used            : bool
      model_version            : string
      task_type                : string option
      prompt_korean_char_ratio : float
      timestamp                : DateTimeOffset }

/// Injected into endpoint. Enqueues a log entry; never blocks the hot path.
type IDecisionLogger =
    abstract member Log : DecisionLog -> unit
```

---

### Pattern 2: `Channel<DecisionLog>` + `BackgroundService` writer

```fsharp
// src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs
module SmartRouter.Cli.Adapters.DecisionLogWriter

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Channels
open Microsoft.Extensions.Hosting
open Serilog
open SmartRouter.Cli.Adapters.DecisionLogger

/// Options — bound from appsettings.json "DecisionLog" section or defaults.
[<CLIMutable>]
type DecisionLogOptions =
    { Directory   : string  // default "logs/decisions"
      ChannelCapacity : int // default 10000; BoundedChannelFullMode.DropOldest on overflow }

/// BackgroundService that drains Channel<DecisionLog> and writes to YYYY-MM-DD.jsonl.
/// Single writer — no locking on file handle. Rotates file on UTC date change.
type DecisionLogWriter(options: DecisionLogOptions) =
    inherit BackgroundService()

    // Bounded channel — DropOldest on overflow. Writer never blocks the request path.
    let channel =
        Channel.CreateBounded<DecisionLog>(
            BoundedChannelOptions(
                options.ChannelCapacity,
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = false,   // multiple endpoint tasks produce
                SingleReader = true))   // only this BackgroundService consumes

    // JSON options for JSONL serialization — FSharp.SystemTextJson for string option
    let jsonOpts =
        let o = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
        o.Converters.Add(FSharp.SystemTextJson.JsonFSharpConverter())
        o

    /// Enqueue without blocking. Called from the hot request path.
    member _.Enqueue(entry: DecisionLog) : unit =
        if not (channel.Writer.TryWrite(entry)) then
            // Channel full (DropOldest handles it, but log the overflow for visibility)
            Log.Warning("DecisionLogWriter: channel full, entry dropped for correlation_id={CorrelationId}", entry.correlation_id)

    interface IDecisionLogger with
        member this.Log(entry) = this.Enqueue(entry)

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            let dir = options.Directory
            Directory.CreateDirectory(dir) |> ignore

            let mutable currentDate  = DateTimeOffset.MinValue.Date
            let mutable writer : StreamWriter option = None

            let openWriter (date: DateTime) =
                writer |> Option.iter (fun w -> w.Flush(); w.Dispose())
                let path = Path.Combine(dir, date.ToString("yyyy-MM-dd") + ".jsonl")
                let sw = new StreamWriter(path, append = true, encoding = Encoding.UTF8)
                sw.AutoFlush <- false    // we flush explicitly per-line for atomicity
                currentDate <- date
                writer <- Some sw
                sw

            try
                while not stoppingToken.IsCancellationRequested do
                    let! entry = channel.Reader.ReadAsync(stoppingToken)
                    let today = DateTimeOffset.UtcNow.Date
                    let sw =
                        if today <> currentDate then openWriter today
                        else writer |> Option.defaultWith (fun () -> openWriter today)

                    let line = JsonSerializer.Serialize(entry, jsonOpts)
                    sw.WriteLine(line)
                    sw.Flush()  // per-line flush: atomic line in OS buffer; avoids torn writes
            with
            | :? OperationCanceledException -> ()  // graceful shutdown
            | ex ->
                Log.Error(ex, "DecisionLogWriter: unexpected error in writer loop")

            // Drain remaining items after cancellation signal
            let mutable remaining = true
            while remaining do
                match channel.Reader.TryRead() with
                | true, entry ->
                    try
                        let today = DateTimeOffset.UtcNow.Date
                        let sw =
                            if today <> currentDate then openWriter today
                            else writer |> Option.defaultWith (fun () -> openWriter today)
                        let line = JsonSerializer.Serialize(entry, jsonOpts)
                        sw.WriteLine(line)
                        sw.Flush()
                    with ex ->
                        Log.Warning(ex, "DecisionLogWriter: error during shutdown drain")
                | false, _ -> remaining <- false

            // Dispose the StreamWriter cleanly
            writer |> Option.iter (fun w -> w.Flush(); w.Dispose())
        }

    /// Signal the channel writer as complete — stops any waiting ReadAsync after draining.
    override _.StopAsync(cancellationToken: CancellationToken) =
        channel.Writer.TryComplete() |> ignore
        base.StopAsync(cancellationToken)
```

**Key design decisions:**
- `BoundedChannelFullMode.DropOldest`: under disk pressure, oldest (least relevant) entries are dropped. Logging a warning to Serilog stderr when this happens makes it visible without crashing.
- `SingleReader = true`: hint to Channel to optimize the reader path.
- `sw.AutoFlush <- false` + explicit `sw.Flush()` after each line: per-line OS buffer flush ensures line atomicity (readers of the file see complete lines) without the overhead of `fsync` per line. On Linux/macOS, a buffered `StreamWriter.Flush()` issues a single `write()` syscall per line — atomic for lines under PIPE_BUF (4096 bytes). All decision log lines are well under 512 bytes.
- Daily rotation: `DateTimeOffset.UtcNow.Date` compared per entry. UTC avoids midnight ambiguity from timezone shifts. The old file is flushed and disposed before the new one is opened — no torn line at the boundary.
- Shutdown drain: `ExecuteAsync` drains the channel after `OperationCanceledException` so no entries are lost on graceful shutdown (`app.StopAsync` → `BackgroundService.StopAsync` → channel drain → file close).

---

### Pattern 3: Correlation ID middleware

```fsharp
// src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs
module SmartRouter.Cli.Adapters.CorrelationMiddleware

open System
open Microsoft.AspNetCore.Http
open Serilog.Context

/// Key used in HttpContext.Items.
/// String constant avoids boxing allocation on repeated access.
[<Literal>]
let CorrelationIdKey = "CorrelationId"

/// Minimal ASP.NET Core middleware.
/// Runs before the endpoint. Generates a correlation ID per request:
///   1. Stores in HttpContext.Items["CorrelationId"] — endpoint reads it for DecisionLog.
///   2. Pushes to Serilog LogContext — all Serilog log lines for this request carry correlation_id.
/// Use app.Use (lambda form) rather than IMiddleware — avoids DI registration overhead for stateless middleware.
let correlationMiddleware (next: RequestDelegate) =
    fun (ctx: HttpContext) ->
        let cid = Guid.NewGuid().ToString("N")  // 32-char lowercase hex, no dashes
        ctx.Items.[CorrelationIdKey] <- cid
        use _ = LogContext.PushProperty("correlation_id", cid)
        next.Invoke(ctx)
```

**Wire in Program.fs** (before `UseSerilogRequestLogging` and before endpoint mapping):

```fsharp
app.Use(fun ctx (next: Func<Task>) ->
    CorrelationMiddleware.correlationMiddleware next ctx) |> ignore
app.UseSerilogRequestLogging() |> ignore
ChatCompletions.mapEndpoints app
```

**Why app.Use lambda form, not IMiddleware:** `IMiddleware` requires DI registration as a scoped or transient service; this middleware is stateless and the lambda form is simpler for F#. The `use _ = LogContext.PushProperty(...)` call returns an `IDisposable`; `use _` binds it so it is disposed when `next.Invoke` returns — correct scope for per-request LogContext.

---

### Pattern 4: ChatCompletions.fs integration (Option A — log at endpoint exit)

**Why Option A:** Only after `routeRequest` returns AND the upstream response is fully sent do we know `latency_ms`. Middleware (Option B) doesn't have the routing decision. QueueDispatcher (Option C) doesn't have routing algorithm or reason details.

**Exact wiring:**

```fsharp
// In handler function signature — add IDecisionLogger and IClock (or use DateTimeOffset directly)
let handler
    (routingConfig  : RoutingConfig)
    (algorithm      : RoutingAlgorithm)
    (algorithmName  : string)        // "heuristic" | "ml" — passed from CompositionRoot
    (modelVersion   : string)        // "heuristic-v1" | "ml-v0-placeholder" — static in Phase 5
    (decisionLogger : IDecisionLogger)
    (upstream       : IUpstreamClient)
    (ctx            : HttpContext) : Task =
    task {
        let started = DateTimeOffset.UtcNow
        let correlationId =
            match ctx.Items.TryGetValue(CorrelationMiddleware.CorrelationIdKey) with
            | true, v -> string v
            | _       -> Guid.NewGuid().ToString("N")  // fallback if middleware wasn't registered

        // ... existing request parsing and routing ...

        match routeRequest routingConfig algorithm req with
        | Error e ->
            // Log error path too — latency_ms still valid, target = "unknown"
            let latencyMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds
            decisionLogger.Log {
                schema_version           = 1
                correlation_id           = correlationId
                prompt_hash              = DecisionLogger.computePromptHash req.Messages
                routing_algorithm        = algorithmName
                routing_reason           = sprintf "error:%A" e
                target                   = "unknown"
                latency_ms               = latencyMs
                fallback_used            = false
                model_version            = modelVersion
                task_type                = req.Task
                prompt_korean_char_ratio = DecisionLogger.computeKoreanRatio req.Messages
                timestamp                = DateTimeOffset.UtcNow }
            ctx.Response.StatusCode <- 400
            // ... existing error response ...

        | Ok decision ->
            // Execute upstream call (streaming or non-streaming) ...
            // After response is fully sent:
            let latencyMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds
            decisionLogger.Log {
                schema_version           = 1
                correlation_id           = correlationId
                prompt_hash              = DecisionLogger.computePromptHash req.Messages
                routing_algorithm        = algorithmName
                routing_reason           = DecisionLogger.formatReason decision.Reason
                target                   = sprintf "%A" decision.Target
                latency_ms               = latencyMs
                fallback_used            = decision.IsFallback
                model_version            = modelVersion
                task_type                = req.Task
                prompt_korean_char_ratio = DecisionLogger.computeKoreanRatio req.Messages
                timestamp                = DateTimeOffset.UtcNow }
    }
```

**Streaming case:** Log AFTER the `enumerator.DisposeAsync()` call in the finally block's normal exit path. The streaming loop completes when the upstream has sent the last byte and the downstream has acknowledged it. For the error path (OperationCanceledException, unexpected exception), also log — with a `latency_ms` reflecting time to failure rather than time to completion.

---

### Pattern 5: `model_version` placement

`model_version` is a Cli concern, not Core. Core knows nothing about file hashes or algorithm versioning.

In Phase 5, it is a static string determined at startup:

```fsharp
// In CompositionRoot.fs — alongside RoutingAlgorithm registration
let algorithmName, modelVersion =
    match opts.Algorithm with
    | null | "" | "heuristic" -> "heuristic", "heuristic-v1"
    | "ml"                    -> "ml",        "ml-v0-placeholder"
    | other                   -> failwithf "invalid algorithm: %s" other

services.AddSingleton<string * string>(algorithmName, modelVersion) |> ignore
// or more explicitly:
services.AddSingleton<AlgorithmMeta>({ Name = algorithmName; Version = modelVersion }) |> ignore
```

In Phase 6, `model_version` for ML will be `sprintf "ml-%s" (shortHashOfFile "models/router.zip")` — the Cli adapter reads the zip, computes `SHA-256[0..7]`, returns `"ml-abc12345"`. The seam is already designed: `model_version` is a string injected at startup.

---

### Pattern 6: CompositionRoot.fs changes

```fsharp
// Register DecisionLogWriter as BackgroundService + IDecisionLogger
services.AddSingleton<DecisionLogWriter>(fun sp ->
    let opts = { Directory = "logs/decisions"; ChannelCapacity = 10000 }
    DecisionLogWriter(opts))
|> ignore

services.AddSingleton<IDecisionLogger>(fun sp ->
    sp.GetRequiredService<DecisionLogWriter>() :> IDecisionLogger)
|> ignore

// Register as IHostedService so ASP.NET starts/stops it with the app
services.AddHostedService<DecisionLogWriter>(fun sp ->
    sp.GetRequiredService<DecisionLogWriter>())
|> ignore
```

**Note:** Register as a concrete singleton first, then expose as `IDecisionLogger` and `IHostedService` using the same instance. This ensures `ChatCompletions` endpoint, `mapEndpoints`, and the host lifetime all share one `DecisionLogWriter`.

---

### Pattern 7: Graceful shutdown

The BackgroundService pattern ties to `IHostedService`. When `app.StopAsync()` fires:

1. ASP.NET Core stops accepting new requests.
2. `IHostedService.StopAsync(CancellationToken)` is called on every registered hosted service.
3. `DecisionLogWriter.StopAsync` calls `channel.Writer.TryComplete()` and `base.StopAsync(...)`.
4. `base.StopAsync` cancels the `stoppingToken` passed to `ExecuteAsync`.
5. The consumer loop catches `OperationCanceledException`, then drains remaining channel items.
6. File is flushed and disposed in the drain-complete path.

ASP.NET Core's default shutdown timeout is 5 seconds (`builder.Host.ConfigureHostOptions(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30))`). For the 10-entry graceful-shutdown test, this is ample. Consider raising to 30s in production to allow the channel to drain fully under high traffic.

---

### Pattern 8: blueCode `JsonlSink.fs` analysis — adapt, not copy

**blueCode pattern:**
- `StreamWriter` opened once per session, `AutoFlush = true`
- `WriteStep` called on the single session writer thread
- `IDisposable.Dispose()` flushes and closes

**Why not copy verbatim:**
- blueCode's sessions are single-threaded (one agent at a time). SmartRouter handles concurrent HTTP requests — the same `StreamWriter` would be shared across multiple `task {}` computations racing on `writer.WriteLine`.
- `AutoFlush = true` is correct but the locking is implicit in blueCode because writes are sequential.
- The Channel + BackgroundService pattern gives the same "single file writer" invariant without any locking, because the writer is moved to a background task that is the sole consumer.

**What to keep from blueCode:**
- `append = true` on `StreamWriter` constructor — essential for rotation (file may already exist if process restarts mid-day)
- `encoding = Encoding.UTF8` — explicit, not platform-default
- `writer.Flush()` + `writer.Dispose()` in the shutdown path
- `Directory.CreateDirectory(dir) |> ignore` on startup

**Verdict: Adapt, not copy.** The Channel wrapper is the key difference. The StreamWriter internals (append, UTF-8, flush-on-dispose) carry over directly.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Thread-safe file append | Manual `lock` around `StreamWriter.WriteLine` | `Channel<T>` + single BackgroundService writer | Lock creates serial bottleneck + deadlock risk under cancellation |
| Prompt hash | Custom rolling hash | `SHA256.Create()` (BCL) | BCL; correct; no NuGet; auditable |
| Daily file rotation | Timer-based scheduled rotation | Lazy reopen on date change per-entry | Timer fires off-schedule under CPU load; per-entry check is free |
| Correlation ID generation | Sequential counter | `Guid.NewGuid().ToString("N")` | Globally unique; no coordination needed; 32-char hex is compact |
| JSON serialization | Custom JSONL writer | `JsonSerializer.Serialize` + `FSharp.SystemTextJson` converter | Already in project; handles `string option` → `null` / absent correctly |

---

## Common Pitfalls

### Pitfall 1: `File.AppendAllText` is NOT thread-safe
**What goes wrong:** Two concurrent requests both call `File.AppendAllText(path, line)`. Both open the file, both write, one gets `IOException` or the bytes interleave mid-line producing invalid JSON.
**How to avoid:** Channel + BackgroundService. The file is touched by exactly one task — the background consumer.

### Pitfall 2: `channel.Reader.ReadAsync` blocks until an item is available — graceful shutdown must cancel it
**What goes wrong:** `BackgroundService.StopAsync` fires, `stoppingToken` is cancelled, but the writer task is blocked on `ReadAsync(stoppingToken)`. With cancellation token passed, `ReadAsync` throws `OperationCanceledException` — correct. Without it, the app hangs.
**How to avoid:** Always pass `stoppingToken` to `ReadAsync`. Then catch `OperationCanceledException` and drain with `TryRead`.

### Pitfall 3: File handle leak if writer task crashes
**What goes wrong:** `writer.WriteLine(line)` throws (disk full, permissions). The exception propagates without disposing the `StreamWriter`. Handle is leaked; future lines to the same file may fail or be corrupted.
**How to avoid:** Wrap the writer loop body in `try/with`. Any exception logs to Serilog stderr and attempts a clean close of the writer before re-entering the loop (or exiting).

### Pitfall 4: Bounded channel `DropOldest` silently drops entries
**What goes wrong:** Under disk pressure, the channel fills. New entries drop the oldest. Monitoring shows fewer entries than requests. Phase 8 sees a gap in the log and cannot determine if it's a genuine gap or data loss.
**How to avoid:** Log a Serilog warning to stderr every time `TryWrite` returns `false`. This makes overflow visible in the operational log without crashing the app.

### Pitfall 5: `LogContext.PushProperty` disposal scope
**What goes wrong:** Correlation ID middleware pushes a property but the `IDisposable` is not properly disposed. The property leaks onto subsequent requests that happen to share a thread-pool thread.
**How to avoid:** `use _ = LogContext.PushProperty(...)` — `use _` ensures disposal when the `next.Invoke` task completes.

### Pitfall 6: Logging at the wrong lifecycle point (streaming path)
**What goes wrong:** Log is written before `enumerator.DisposeAsync()` completes. `latency_ms` captures time to first byte, not time to last byte. Phase 8 sees systematically short latencies for streaming requests.
**How to avoid:** Enqueue the DecisionLog in the finally block *after* the `DisposeAsync()` call. The finally block always executes on stream end (normal, cancel, error) — cover all three cases.

### Pitfall 7: Forgetting error paths in `ChatCompletions.fs`
**What goes wrong:** The routing error branch (`Error e`) and the upstream error branch (`Error 502`) return without logging. Phase 8 sees only successful routes; fallback rate appears 0%.
**How to avoid:** Add `decisionLogger.Log` at ALL exit points of `handler`: routing error (`Error (UnsupportedTask ...)`), routing error (`Error e`), upstream error (502), streaming error, streaming cancellation, non-streaming success, non-streaming error. Use a helper function `buildDecisionLog` to reduce duplication.

### Pitfall 8: `sha256.ComputeHash` on the hot path — minimize allocation
**What goes wrong:** Every request allocates a `SHA256` instance, a UTF-8 byte array, and a 32-byte hash array. Under high concurrency this is noticeable.
**How to avoid:** `use sha = SHA256.Create()` inside the computation — SHA256 is not thread-safe so don't share it. The `use` ensures disposal. The allocation is acceptable (a few KB per request vs. LLM latency in the seconds range). If profiling reveals it is a hotspot, switch to `SHA256.HashData(span)` (BCL static, no allocation) or xxHash (NuGet).

### Pitfall 9: Missing `.gitignore` entry for log files
**What goes wrong:** `logs/decisions/*.jsonl` accumulates in git working directory. Someone runs `git add .` and commits raw decision logs.
**How to avoid:** Add `logs/` to `.gitignore` in this phase. Add a comment: `# Decision logs — contains hashed prompt data; do not commit`.

### Pitfall 10: Schema field `task_type` is `string option` — STJ serialization
**What goes wrong:** `System.Text.Json` without `FSharp.SystemTextJson` serializes `None` as `{}` (empty object) not `null`. Phase 8 parser sees `{}` and fails to parse.
**How to avoid:** `FSharp.SystemTextJson` is already in the project and handles `string option` → `null` when the value is `None`. Confirm the converter is registered on the `JsonSerializerOptions` used in `DecisionLogWriter`.

---

## Test Patterns (`LoggingTests.fs`)

All tests use `testSequenced` because they touch file I/O and `Console.SetOut`/`Console.SetError` (via Serilog).

### Test 1: Schema completeness
```fsharp
// Send one request; parse the JSONL line; assert every required field is present and typed correctly.
// Use JsonDocument.Parse to assert field presence by name — catches missing-field serialization bugs.
test "schema completeness — all LOG-01 fields present" {
    // ... spin up WebApplicationFactory, send one request, read the log file ...
    let doc = JsonDocument.Parse(line)
    let root = doc.RootElement
    Expect.equal (root.GetProperty("schema_version").GetInt32()) 1 "schema_version"
    Expect.isSome (root.TryGetProperty "correlation_id" |> Option.ofPair) "correlation_id present"
    Expect.isSome (root.TryGetProperty "prompt_hash"    |> Option.ofPair) "prompt_hash present"
    // ... assert each field ...
}
```

### Test 2: 100-concurrent concurrency safety
```fsharp
// Spin up in-process Kestrel (WebApplicationFactory). Fire 100 parallel requests.
// Assert file has exactly 100 valid JSON lines. Assert no IOException.
testCase "100-concurrent — no interleaved bytes, exactly 100 lines" <| fun () ->
    // Use Async.Parallel / Task.WhenAll with 100 HttpClient.PostAsync calls
    // After all complete, read the file, split by newline, assert count = 100
    // Parse each line: JsonDocument.Parse must not throw
    let lines = File.ReadAllLines(logPath) |> Array.filter (fun l -> l.Trim() <> "")
    Expect.equal lines.Length 100 "exactly 100 lines"
    lines |> Array.iter (fun l -> JsonDocument.Parse(l) |> ignore)  // no parse exception
```

### Test 3: Correlation ID propagation
```fsharp
// Capture Serilog stderr output (via TestCorrelator or by redirecting the Serilog sink).
// Assert that the correlation_id in the JSONL line matches the correlation_id in the Serilog request log.
testCase "correlation_id in JSONL matches Serilog stderr for same request" <| fun () ->
    // ... send one request, capture both streams ...
    Expect.equal jsonlCorrelationId serilogCorrelationId "correlation IDs match"
```

### Test 4: Graceful shutdown flush
```fsharp
// Start app, fire 10 requests, immediately call StopAsync.
// Read log file; assert all 10 entries are present (no entries lost in channel).
testCase "graceful shutdown — all 10 in-flight entries flushed" <| fun () ->
    // ... use WebApplicationFactory with manual stop ...
    let lines = File.ReadAllLines(logPath) |> Array.filter (fun l -> l.Trim() <> "")
    Expect.isGreaterThanOrEqual lines.Length 10 "all 10 entries present after StopAsync"
```

### Test 5: Daily rotation
```fsharp
// Inject a mock clock that returns Day 1 for first 5 requests and Day 2 for next 5.
// Assert two separate files are created, each with 5 entries.
// (Or use a settable system-time shim; simpler: pass IClock into DecisionLogWriter constructor.)
```

**Note on test infrastructure:** `WebApplicationFactory<Program>` is already in the test project (`Microsoft.AspNetCore.Mvc.Testing` referenced). The test sets a custom `ASPNETCORE_ENVIRONMENT` or overrides configuration to point log output to a `Path.GetTempPath()` directory, then reads it back.

---

## `appsettings.json` additions

```json
"DecisionLog": {
  "Directory": "logs/decisions",
  "ChannelCapacity": 10000
}
```

The `logs/decisions` path is relative to the working directory (process CWD at startup — typically the project output dir for dev, or the binary directory for `launchd`). No absolute path required; the `Directory.CreateDirectory` call in `DecisionLogWriter` creates it if absent.

---

## Phase 5 Pitfall Summary (quick reference)

| # | Pitfall | Prevention |
|---|---------|------------|
| P1 | `File.AppendAllText` concurrency | Channel + single BackgroundService writer |
| P2 | `ReadAsync` hang on shutdown | Pass `stoppingToken`; drain with `TryRead` after cancel |
| P3 | File handle leak on writer crash | `try/with` in consumer loop; dispose in all paths |
| P4 | Silent channel overflow | Log Serilog warning on `TryWrite` false |
| P5 | `LogContext` property leak | `use _ = LogContext.PushProperty(...)` |
| P6 | latency_ms wrong for streaming | Log after `enumerator.DisposeAsync()` |
| P7 | Error paths skip logging | `decisionLogger.Log` at ALL ChatCompletions exit points |
| P8 | SHA-256 allocation under load | `use sha = SHA256.Create()` per call; upgrade to `SHA256.HashData` span if needed |
| P9 | Log files committed to git | Add `logs/` to `.gitignore` |
| P10 | `string option` → `{}` in JSON | Confirm `FSharp.SystemTextJson` converter on writer's `JsonSerializerOptions` |

---

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|------------------|--------|
| `File.AppendAllText` (blueCode distillation stub) | `Channel<T>` + `BackgroundService` | Thread-safe; no IOException; bounded memory |
| Per-session `StreamWriter` (blueCode `JsonlSink`) | Single background consumer holding `StreamWriter` | Same invariant (one writer) generalized to concurrent producers |
| No schema versioning | `schema_version` field | Forward compatibility for Phase 8/9 |
| No language cohort signal | `prompt_korean_char_ratio` | Phase 9 canary can compare Korean vs. non-Korean routing accuracy |

---

## Open Questions

1. **`IClock` abstraction for testable timestamps**
   - What we know: `DateTimeOffset.UtcNow` is fine for production; tests that assert `timestamp` field correctness need a controllable clock.
   - What's unclear: Does the planner want a full `IClock` interface injected into `DecisionLogWriter`, or just accept that `timestamp` tests verify "recent" rather than exact value?
   - Recommendation: inject a simple `unit -> DateTimeOffset` delegate (not a full interface) — simpler than a port, testable enough. For Phase 5, `Func<DateTimeOffset>` passed to `DecisionLogWriter` constructor is sufficient.

2. **SHA-256 performance under load**
   - What we know: SHA-256 on a 4KB prompt takes ~0.05ms on Apple Silicon. At 100 req/s this is ~5ms/s overhead.
   - What's unclear: actual prompt sizes in production.
   - Recommendation: Use BCL SHA-256 in Phase 5. If profiling in Phase 9 shows >1% of latency, switch to `SHA256.HashData(ReadOnlySpan<byte>)` (zero-alloc) or xxHash (NuGet).

3. **`routing_algorithm` determination in ChatCompletions.fs**
   - What we know: The algorithm function is injected as `RoutingAlgorithm` (a function type). We can't inspect a function to determine its name.
   - Recommendation: Inject `algorithmName : string` alongside `RoutingAlgorithm` from `CompositionRoot`. The two are created together; pass both. See Pattern 4 above.

---

## Sources

### Primary (HIGH confidence — verified in project codebase)
- `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/JsonlSink.fs` — prior art JSONL writer; adapt not copy
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/Logging.fs` — existing Serilog setup; `LogContext.PushProperty` pattern confirmed available
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/CompositionRoot.fs` — DI registration pattern; `AddSingleton` + interface alias pattern established
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — exact injection and handler shape; all exit points confirmed
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Program.fs` — middleware registration point confirmed
- `/Users/ohama/projs/smart-router/src/SmartRouter.Core/Domain.fs` — `RoutingDecision.IsFallback`, `RoutingReason` DU cases confirmed
- `/Users/ohama/projs/smart-router/src/SmartRouter.Core/ML.fs` — `applyML` placeholder confirmed (`"ml-v0-placeholder"` version string appropriate)
- `/Users/ohama/projs/smart-router/tests/SmartRouter.Tests/RouterTests.fs` — `rootTests` pattern; `testSequenced` requirement confirmed
- `/Users/ohama/projs/smart-router/tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — test project already has `Microsoft.AspNetCore.Mvc.Testing`; `WebApplicationFactory` available

### Secondary (HIGH confidence — distillation research)
- `/Users/ohama/projs/smart-router-distillation/docs/auto-retraining-research.md` §3.2 #2 — `File.AppendAllText` concurrency trap; `Serilog` + `Channel<LogRecord>` recommendation confirmed
- `/Users/ohama/projs/smart-router-distillation/documentation/howto/design-two-loop-router.md` — JSONL as Loop A → Loop B bridge; schema completeness criticality
- `/Users/ohama/projs/smart-router-distillation/documentation/howto/spot-stubs-hiding-ops-traps.md` — `File.Append*` without lock as a known ops trap

### Tertiary (.NET BCL documentation — confirmed by project usage)
- `System.Threading.Channels.Channel<T>` — `CreateBounded`, `BoundedChannelFullMode.DropOldest`, `TryWrite`, `ReadAsync`, `TryRead`
- `Microsoft.Extensions.Hosting.BackgroundService` — `ExecuteAsync`, `StopAsync`, `stoppingToken` lifecycle
- `System.Security.Cryptography.SHA256` — `Create()`, `ComputeHash(byte[])`, `SHA256.HashData(ReadOnlySpan<byte>)` (zero-alloc static, .NET 5+)
- `Serilog.Context.LogContext.PushProperty` — returns `IDisposable`; `use _` for scope

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all libraries BCL or already in project; no new NuGet required
- Architecture: HIGH — Channel + BackgroundService pattern is the standard .NET concurrent-logging pattern; derived from project's existing patterns
- Pitfalls: HIGH — File.AppendAllText danger confirmed in distillation research; channel overflow + shutdown drain patterns are documented .NET behavior
- Schema: HIGH — LOG-01 fields locked in REQUIREMENTS.md; two new fields (schema_version, prompt_korean_char_ratio) have clear rationale and negligible cost

**Research date:** 2026-05-08
**Valid until:** Stable (BCL patterns; .NET 10 LTS; no fast-moving dependencies)
