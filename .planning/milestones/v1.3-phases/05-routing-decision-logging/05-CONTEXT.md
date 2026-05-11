# Phase 5: Routing-Decision Logging - Context

**Gathered:** 2026-05-08
**Status:** Ready for planning

<domain>
## Phase Boundary

Per-request structured JSONL log written by a single-writer `BackgroundService` consumer of a bounded `Channel<DecisionLog>`. Every routing decision (heuristic or ML) emits one line to `logs/decisions/YYYY-MM-DD.jsonl` (UTC date) with the locked schema (12 fields). Correlation ID middleware generates a per-request id, propagates through Serilog `LogContext` and `HttpContext.Items`, and lands in both stderr Serilog output and the JSONL file. Daily file rotation is lazy (writer reopens when `DateTime.UtcNow.Date` changes between lines). Graceful shutdown drains the channel before host exits — no in-flight log loss. Schema completeness is critical because Phase 8 retraining and Phase 9 canary cohort comparison both consume this format.

</domain>

<decisions>
## Implementation Decisions

### Schema: 12 fields (10 locked + 2 added 2026-05-08)
Field list (all required unless noted):

| Field | Type | Source |
|---|---|---|
| `schema_version` | int (currently `1`) | constant |
| `correlation_id` | string (Guid or short id) | middleware-generated |
| `prompt_hash` | string (SHA-256 hex of concatenated message contents) | computed at endpoint |
| `prompt_korean_char_ratio` | float 0..1 | regex count `[가-힣]` / total chars |
| `routing_algorithm` | string (`heuristic` \| `ml`) | injected via `algorithmName: string` from CompositionRoot |
| `routing_reason` | string (`ExplicitModelOverride` \| `ExplicitTask` \| `Heuristic` \| `Default` \| `ML`) | from `RoutingDecision.Reason` |
| `target` | string (`Qwen35B` \| `Qwen122B`) | from `RoutingDecision.Target` |
| `latency_ms` | float | end-of-handler clock minus start clock |
| `fallback_used` | bool (always `false` in this phase) | reserved for Phase 10 |
| `model_version` | string (`heuristic-v1` or `ml-v0-placeholder`) | injected from CompositionRoot per algorithm |
| `task_type` | string nullable (the request's `task` field if present) | from `RouterRequest.Task` |
| `timestamp` | ISO 8601 UTC string | clock at log-write time |

**Operator decision 2026-05-08:** add `schema_version` (future-proofing, almost free) and `prompt_korean_char_ratio` (cohort signal for Phase 9 canary's bge-m3 validation). Both included from start; "add now or pay later" is operator's chosen direction.

### `prompt_hash` algorithm: SHA-256 (BCL, no NuGet)
- Hash full concatenated message contents (matches what the heuristic scores)
- ~0.05ms per 4KB prompt on Apple Silicon — not a hotspot
- One-way; raw prompt content does NOT land in the log file (privacy)
- If profiling later shows hotspot, swap to xxHash; LOG schema isn't sensitive to algorithm choice (just a string)

### `algorithmName: string` injected alongside `RoutingAlgorithm` (researcher's open question resolved)
- `RoutingAlgorithm` is a function type and can't be inspected for its name at runtime
- `CompositionRoot` knows which function it registered → injects a paired `algorithmName: string` ("heuristic" or "ml") into a record alongside the function
- One concrete shape: register `AddSingleton<RoutingAlgorithmRegistration>` where `RoutingAlgorithmRegistration = { Algorithm: RoutingAlgorithm; Name: string; ModelVersion: string }`. Endpoint resolves the registration and reads both function and name.
- Alternative: separate `IStringResource` for algorithm name. The record approach is simpler.

### Channel + BackgroundService pattern
- `Channel.CreateBounded<DecisionLog>(BoundedChannelOptions(10000))` — bounded; back-pressure semantics on full
- Single consumer `BackgroundService` task drains the channel and writes to current day's file
- Writer holds one `FileStream` open per day (append mode); flush per write (line atomicity guaranteed for short JSON lines under PIPE_BUF, but explicit flush gives crash safety)
- Drop policy on full channel: `BoundedChannelFullMode.DropWrite` + log a warning to stderr ("decision log channel full; dropped 1 decision; check disk I/O"). Operator-visible signal; no silent loss.
- Daily rotation: writer checks `DateTime.UtcNow.Date` per line; if changed since last write, close current file + open new dated file. Edge cases (clock skew, midnight boundary) handled by per-line check.

### blueCode `JsonlSink.fs` — adapt, not copy
- blueCode's pattern: single `StreamWriter` + `AutoFlush`. Thread-safe for blueCode's single-threaded session model but NOT for concurrent HTTP requests in smart-router.
- Smart-router writes from many threads → must funnel through Channel + single-writer BackgroundService.
- The line-by-line JSON serialization + `WriteLine` mechanic carries over. The producer-consumer separation is what's new.

### Correlation ID middleware
- ASP.NET Core `app.Use(...)` lambda registered FIRST in the pipeline (before any routing)
- Generates `Guid.NewGuid().ToString("N")` (32-char hex; fits in headers) per request
- Stash in `HttpContext.Items["CorrelationId"]` AND in `Serilog.Context.LogContext.PushProperty("correlation_id", id)` — Serilog's `LogContext` flows through async/await
- All Serilog log lines for this request automatically carry `correlation_id`
- Endpoint reads `ctx.Items["CorrelationId"]` to populate DecisionLog.CorrelationId
- The Guid-N format (no dashes) is short enough to grep cleanly in stderr

### Logging happens at endpoint exit (not middleware, not QueueDispatcher)
- `ChatCompletions.handler` captures `started = clock.UtcNow()` at top
- Builds `DecisionLog` after response (success path) OR after error envelope (error path)
- Enqueues via injected `IDecisionLogger.Log` (fire-and-forget, never blocks request)
- Streaming case: log AFTER stream completion (when consumer fully drains the IAsyncEnumerable). Latency includes streaming duration.
- All exit points must log: success, routing error (HTTP 400 unknown task), upstream error, cancellation. Plan must enumerate them and verify with tests.

### Graceful shutdown flush
- `BackgroundService.StopAsync` waits for the consumer loop to drain the channel
- Pattern: `_channel.Writer.Complete()` on stop signal → consumer reads remaining items → loop exits → `StopAsync` returns
- Verified by start-route-stop-read test sequence (LoggingTests.fs)

### `logs/decisions/` directory
- Created at startup if missing
- `.gitignore` adds `logs/` (no log files committed; matches blueCode pattern)
- Retention: out of scope for v1 (operator manages disk; document in Phase 11 README)

### Test isolation
- All Phase 5 logging tests wrapped in `testSequenced` (per blueCode + smart-router pattern)
- Each test uses a temp directory for `logs/decisions/` to avoid cross-test pollution
- Concurrency test: 100 parallel requests via `Task.WhenAll`; assert file has 100 valid JSON lines (parse each)

### Claude's Discretion
- Exact channel capacity (10000 is a starting point; may tune)
- Exact log format for the dropped-channel warning to stderr
- Whether to use `System.Threading.Channels.Channel` or `Microsoft.Extensions.Channels` (former is in BCL — pick that)
- Internal field naming (snake_case JSON keys vs PascalCase F# record fields handled by per-record JsonOptions, like Phase 3's StatsWire pattern)
- Whether `prompt_korean_char_ratio` rounds to N decimal places or full float precision

</decisions>

<specifics>
## Specific Ideas

- Mirror Phase 3's snake_case-via-private-record pattern: `DecisionLogWire` record with snake_case fields for JSON output; `DecisionLog` record with idiomatic F# PascalCase for in-process use
- `prompt_korean_char_ratio` regex: `[가-힣]` (Hangul Syllables block) — does NOT include Jamo (combining chars) or Compatibility Jamo. If user wants broader Korean detection, regex can be extended in Phase 6+ without schema change.
- `model_version` literals locked for Phase 4-5 boundary: `"heuristic-v1"` and `"ml-v0-placeholder"`. Phase 6 will redefine ML model_version to include file hash.

</specifics>

<deferred>
## Deferred Ideas

- Log rotation by size (in addition to date) — not needed at expected v1 traffic
- Compression of older log files (gzip) — Phase 11 deploy concern
- Log retention policy / automatic cleanup — operator concern; document in README Phase 11
- Streaming chunk-level logging (per-chunk timestamps) — out of scope; LOG-01 captures per-request only
- Privacy hashing of `correlation_id` — already SHA-256 hashed; no further obfuscation needed
- Schema migration tooling — not needed yet; `schema_version=1` exists; Phase 8 readers branch on it if v2 lands

</deferred>

---

*Phase: 05-routing-decision-logging*
*Context gathered: 2026-05-08*
