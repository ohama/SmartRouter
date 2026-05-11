# Phase 13 — Service Logging Research + Design

**Drafted:** 2026-05-09
**Author session:** parallel session (write-target restricted to `.planning/preparing/`)
**Scope:** smart-router 가 launchd service 로 영구 동작할 때의 logging 전략. 무엇을 / 어디에 / 얼마만한 크기로 / 어떤 이름으로 — 조사하고 구현 계획까지.

---

## 1. Current state (조사 결과)

### 1.1 Serilog wiring

`src/SmartRouter.Cli/Adapters/Logging.fs` (28 lines, full file inlined):

```fsharp
let levelSwitch: LoggingLevelSwitch = LoggingLevelSwitch(LogEventLevel.Information)
let configure () : unit =
    Log.Logger <-
        LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .WriteTo.Console(
                standardErrorFromLevel = Nullable<LogEventLevel>(LogEventLevel.Verbose),
                outputTemplate = "[{Level:u3}] {Message:lj}{NewLine}{Exception}"
            )
            .CreateLogger()
let shutdown () : unit = Log.CloseAndFlush()
```

**Sink:** Console with `standardErrorFromLevel = Verbose` → **ALL events go to stderr.** Stdout stays clean (OBS-04: stream separation invariant).
**Default level:** `Information` via `LoggingLevelSwitch`. `--trace` CLI flag flips it to `Debug`.
**Output template:** `[{Level:u3}] {Message:lj}{NewLine}{Exception}` — minimal.

### 1.2 NuGet packages

```
Serilog                4.3.1
Serilog.Sinks.Console  6.1.1
Serilog.AspNetCore     10.0.0
```

(`Serilog.Sinks.File`, `Serilog.Settings.Configuration`, `Serilog.Formatting.Compact` are loaded transitively into `bin/` but not directly referenced — confirmed via grep on `Cli.fsproj`.)

### 1.3 `appsettings.json` Serilog section — currently DEAD CONFIG

```json
"Serilog": {
  "MinimumLevel": { "Default": "Information" }
},
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "Microsoft.AspNetCore": "Warning"
  }
}
```

`Logging.configure()` does NOT call `.ReadFrom.Configuration(...)` — the `Serilog` section is read by no one. `Microsoft.Extensions.Logging.Logging.LogLevel` is also not bound to Serilog (Serilog is wired as the host's `ILoggerProvider`, but the `.Logging` section is the framework's filter, not Serilog's).

### 1.4 CorrelationMiddleware — `correlation_id` is captured but NOT rendered

`src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs`:

```fsharp
let correlationMiddleware (ctx: HttpContext) (next: RequestDelegate) : Task =
    task {
        let cid = Guid.NewGuid().ToString("N")           // 32-char hex, no dashes
        ctx.Items.[CorrelationIdKey] <- cid
        use _ = LogContext.PushProperty("correlation_id", cid)
        do! next.Invoke(ctx)
    }
```

`LogContext.PushProperty("correlation_id", cid)` IS pushed into Serilog's structured LogEvent — but the **output template does not include `{correlation_id}`**, so it's stripped at render time. **Hot-path logs are unjoinable to the JSONL DecisionLog by correlation_id from the operator's perspective.** Major DX gap.

### 1.5 launchd plist (Phase 11 design — not yet shipped)

```xml
<key>StandardOutPath</key>
<string>/Users/ohama/llm-system/services/logs/smart-router.log</string>
<key>StandardErrorPath</key>
<string>/Users/ohama/llm-system/services/logs/smart-router.err</string>
<key>WorkingDirectory</key>
<string>/Users/ohama/llm-system/services/smart-router</string>
```

Since Serilog → stderr only, **all logs land in `smart-router.err`. `smart-router.log` stays empty.** launchd has **NO rotation / no size limit / no retention** — `smart-router.err` grows indefinitely. Counterpart qwen36-35b/qwen122b plists likely have the same issue (operator's existing convention).

### 1.6 Existing structured artifacts (separate from Serilog operational log)

| Artifact | Path | Owner | Schema | Rotation |
|---|---|---|---|---|
| Decision log (Phase 5) | `logs/decisions/YYYY-MM-DD.jsonl` | `DecisionLogWriter.fs` | 12 fields incl. `correlation_id`, `prompt_hash`, `routing_algorithm`, `target`, `latency_ms`, `model_version` | by date in filename; **no retention** |
| Hard-case dataset (Phase 7) | `datasets/hard-cases.jsonl` | `HardCaseDatasetWriter.fs` | `correlation_id`, `prompt_hash`, `prompt`, `label`, `label_source`, `teacher_response_excerpt`, `labeled_at` | append-only; **no rotation** |
| Teacher cap counter (Phase 7) | `datasets/teacher-cap-YYYY-MM-DD.json` | `TeacherLabeler.fs` | `{date, count, max}` | by date; auto-rotates |
| Canary state (Phase 9) | `datasets/canary-state.json` | `CanaryService.fs` | `canary_version`, `percentage_enabled` | overwrites |
| Retraining state (Phase 8) | `datasets/retraining-state.json` | `RetrainingService.fs` | `last_run_at`, `last_model_sha`, `samples_seen` | overwrites |

Phase 5 가 이미 routing-decision 의 **machine-readable JSONL** 을 분리해 놓았다. **Phase 13 의 일은 operational log (Serilog) 쪽 보강만** — JSONL 레이어는 이미 well-designed.

### 1.7 Log emission audit (per-file counts)

Total emissions across `src/SmartRouter.Cli/`:

```
INFO    31
WARN    42      (most-emitted level — appropriate for a service)
ERROR    9
DEBUG    7
VERBOSE  1
```

19 / 34 source files emit logs; 15 don't (CanaryGate, CanaryMetrics, CanaryState, CanaryTargetingAccessor, CorrelationMiddleware, DecisionLogger, Json, MlNetClassifier, ModelVersionProvider, RetrainLock, RoutingAlgorithm, plus 4 endpoint files). The non-emitting set is mostly value-types and DI registration helpers — appropriate.

**Hot-path concerns (request rate ≥ 1 QPS):**

| Site | Level | Rate | Concern |
|---|---|---|---|
| `ChatCompletions.fs:282` "Routing target=... reason=... priority=... stream=true" | Information | once per streaming request | At 100 req/min = 144,000/day = ~50MB/day stderr if message + props avg 350 bytes. **Demote to Debug** — same info already in JSONL DecisionLog. |
| `ChatCompletions.fs:367` ditto, non-streaming branch | Information | once per non-stream request | Same. **Demote to Debug.** |
| `HealthService.fs:74` probe success log | Information | every `PollingIntervalSeconds × upstream count` (= 12/min default) | Manageable but redundant. **Move state-change-only**: log only on transitions (was-down → up, was-up → down). |
| `QwenUpstreamClient.fs:207, 286` POST body / stream POST | Debug | per-request | Already Debug; default Information level suppresses. OK. |
| `QueueDispatcher.fs:150, 246, 323` queue lifecycle | Debug+Warning | per-queued request | Warning at queue-full / queue-cancellation = legitimate; OK. |

**Cold-path emissions** (RetrainingService, TeacherLabeler, FailureDetector, CanaryService, BgeM3Embedder warm-up, ModelBootstrapper) all run < 1/hour or 1/day. No volume concern.

---

## 2. Gap analysis

### G1. **No file sink → operator cannot review logs after restart unless launchd captured stderr**

stderr ends up at launchd's `StandardErrorPath` = `smart-router.err` (Phase 11 plan). But:

- **No rotation, no size cap, no retention** — file grows indefinitely.
- launchd does NOT auto-rotate.
- macOS `newsyslog` could rotate it externally, but that's operator overhead and crash-unsafe (writer doesn't reopen on rotation).

### G2. **No `correlation_id` in operational log output template**

Phase 5 `correlation_id` (32-char hex, also in JSONL `correlation_id` field) is captured by `CorrelationMiddleware` into `LogContext` but the output template strips it. **Operator cannot grep operational log for a specific request's events.**

### G3. **No timestamp in output template**

When operator opens `smart-router.err` (or future operational log file) days later, **no clue when each event happened.** launchd's stderr capture does NOT prepend timestamps (unlike systemd's journal).

### G4. **No `SourceContext` (= F# module name) in output template**

Hard to tell which adapter emitted a given line at a glance. Currently disambiguated only by message conventions ("HealthService:", "CanaryService:", etc.) — fragile + manually maintained.

### G5. **`appsettings.json:Serilog` section is dead config**

Operator may edit `Serilog.MinimumLevel.Default` expecting it to take effect. It doesn't.

### G6. **Hot-path Information log volume**

Per §1.7: ChatCompletions logs 1 INFO per request. At 100 req/min × 350 bytes × 24h = **~50 MB/day** of stderr-only data that **duplicates the JSONL DecisionLog row**. Bytes are wasted; rotation pressure increased.

### G7. **No startup banner**

Operator-attaches to a long-running launchd service via `tail -f smart-router.err`. With no startup banner, operator has no quick proof that the binary they're tailing matches what they expect:
- which port?
- which routing algorithm?
- which model_version?
- which embed model SHA?
- which canary percentage?

Currently must inspect HTTP `/stats` or `/canary` endpoints.

### G8. **No graceful-shutdown banner**

Operator can't tell from logs alone whether the process exited cleanly (drain completed) or was SIGKILL'd. Currently reverse-inferred from absence of crash error.

### G9. **No per-category Serilog override**

ASP.NET host startup logs `Microsoft.Hosting.Lifetime: Now listening on http://...:4000` at INFO — useful. But `Microsoft.AspNetCore.Routing.EndpointMiddleware` and similar internal noise also flood stderr at INFO. No selective suppression.

### G10. **No log emission for endpoint hits at /health, /canary, /stats, /v1/models**

These don't emit any log — operator running `tail -f` against a healthy router sees zero traffic indication. (Decision log JSONL only covers `/v1/chat/completions`.) Useful for confirming the router is reachable but currently invisible.

---

## 3. Design proposal

### 3.1 Two log streams (separation)

Phase 5 already established this for the JSONL layer. Phase 13 confirms and adds the operational-log half.

| Stream | Purpose | Format | Audience |
|---|---|---|---|
| **Operational log** (Serilog) | What's happening, why, when, with what error | structured + human text + correlation_id | operator (`tail -f`, post-mortem) |
| **Decision log** (Phase 5 JSONL) | One row per chat-completion request | strict 12-field schema | FailureDetector, dashboards, retraining |
| **Datasets** (Phase 7) | Labeled training data | append-only JSONL | retraining loop |

Phase 13 changes only the operational-log stream.

### 3.2 Sinks: stderr **AND** rolling file (parallel)

Operational log writes to TWO sinks simultaneously:

1. **stderr** (kept) — for `tail -f`, for launchd's stderr capture, for live debug.
2. **rolling file** (NEW) — for retention, post-mortem, log shipping.

Rationale: operator's existing tooling (qwen plists log to flat files) → keep stderr capture. But add a self-rotating file for durability.

### 3.3 File layout (proposal)

```
$WorkingDirectory/logs/                                # ./logs/ relative to launchd WorkingDirectory
├── operational/
│   ├── smart-router-2026-05-09.log                   # daily roll
│   ├── smart-router-2026-05-09_001.log               # size-roll within day (100MB cap)
│   ├── smart-router-2026-05-09_002.log
│   ├── smart-router-2026-05-08.log
│   └── ...                                            # Serilog auto-prunes after retainedFileCountLimit = 30
└── decisions/                                         # existing — Phase 5
    └── 2026-05-09.jsonl
```

Existing launchd-captured `smart-router.{log,err}` stay. Reframe their role:

| File | Owner | Content | Rotation |
|---|---|---|---|
| `smart-router.log` (launchd `StandardOutPath`) | launchd | stdout — should remain empty (OBS-04) — only ever populated if something printf-s by accident | none; if grows, that's a bug |
| `smart-router.err` (launchd `StandardErrorPath`) | launchd | stderr — duplicates Serilog's stderr sink + catches pre-Serilog-init crashes + post-Serilog-flush crashes | none; **operator-managed truncate or external rotation** |
| `logs/operational/smart-router-YYYY-MM-DD[_NNN].log` | Serilog | structured operational log | Serilog rolls daily + at 100MB; retains 30 files |
| `logs/decisions/YYYY-MM-DD.jsonl` | DecisionLogWriter | request-level JSONL | by date in filename; **add retention in Phase 13** |

### 3.4 Naming convention

Consistent prefix-and-date pattern across all logs:

| File | Pattern | Example |
|---|---|---|
| Operational rolling | `smart-router-{Date:yyyy-MM-dd}[_{Counter:000}].log` | `smart-router-2026-05-09.log`, `smart-router-2026-05-09_001.log` |
| Operational fallback | `smart-router.{log,err}` | launchd-managed; reserved for early-init/crash output |
| Decision JSONL | `{Date:yyyy-MM-dd}.jsonl` | `2026-05-09.jsonl` (Phase 5; no change) |
| Hard cases | `hard-cases.jsonl` | append-only (no date suffix) |
| Teacher cap | `teacher-cap-{Date:yyyy-MM-dd}.json` | `teacher-cap-2026-05-09.json` |

`smart-router-` prefix on operational files makes log-shipping rules trivial (`smart-router-*.log`) and prevents collisions if multiple services share a logs/ dir.

### 3.5 Size / retention policy

| Stream | Per-file cap | Rotation trigger | Retention | Rationale |
|---|---|---|---|---|
| Operational rolling | 100 MB | daily OR size | 30 files | At 50 MB/day baseline + 100MB/day spike headroom = ≤ 3 GB total. Acceptable on a 1TB+ macOS workstation. |
| Decision JSONL | (no cap) | daily by filename | **90 days** (NEW retention policy) | At 1KB × 1500 req/day ≈ 1.5MB/day → 135MB / 90 days. Phase 8 retraining benefits from longer history; older rows accelerate FailureDetector's hard-case discovery. |
| Hard cases | (no cap) | none | **indefinite** | Training data; should not be auto-deleted. Manual archive when > 1GB. |
| Teacher cap | 1KB | daily by filename | **7 days** | After 7 days the counter is irrelevant — cap only enforces today. |
| Crash files (`smart-router.err`) | unbounded | none | operator-managed | Should rarely have content post-Phase 13 (Serilog catches everything). If grows, that's a bug. |

### 3.6 Output template (NEW)

Replace the current `[{Level:u3}] {Message:lj}{NewLine}{Exception}` with:

```
{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Level:u3}] {SourceContext} cid={correlation_id} {Message:lj}{NewLine}{Exception}
```

Per-token expansion:
- `{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz}` — ISO-8601 with milliseconds and timezone offset (matches DecisionLog's `timestamp` field shape — joinable by eyeball).
- `[{Level:u3}]` — fixed-width 3-char level (`INF`, `WRN`, `ERR`, `DBG`, `VRB`).
- `{SourceContext}` — F# module name, e.g. `SmartRouter.Cli.Adapters.HealthService`. Set automatically by Serilog when `Log.ForContext<HealthService>()` is used; with the static `Log.*` API, `SourceContext` is empty unless a custom enricher injects it.
- `cid={correlation_id}` — present when `LogContext.PushProperty("correlation_id", cid)` is in scope (request path); empty for background-service / startup logs. Serilog's `:lj` formatter writes empty strings cleanly.
- `{Message:lj}` — `:lj` = literal-and-JSON for embedded properties (current).
- `{NewLine}{Exception}` — current.

**Two output templates** (one per sink): same template for both stderr and file is fine. If we want compact JSON in the file (better for log shippers like Promtail/Loki) we can use `Serilog.Formatting.Compact.CompactJsonFormatter` — but at this scale (single host, manual review), human-readable text is more useful. **Recommend human-readable text in both sinks.** Defer JSON-formatted file sink to a follow-on if log-shipping needs arise.

### 3.7 Per-category log levels (replace dead config)

Replace `MinimumLevel.ControlledBy(levelSwitch)` with:

```fsharp
let configure (config: IConfiguration) : unit =
    Log.Logger <-
        LoggerConfiguration()
            .ReadFrom.Configuration(config)
            .MinimumLevel.ControlledBy(levelSwitch)    // levelSwitch overrides config — for --trace runtime flip
            .WriteTo.Console(...)
            .WriteTo.File(...)
            .Enrich.FromLogContext()
            .CreateLogger()
```

`appsettings.json` becomes:

```json
"Serilog": {
  "MinimumLevel": {
    "Default": "Information",
    "Override": {
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.AspNetCore.HttpLogging": "Information",
      "Microsoft.Extensions.Hosting": "Warning",
      "Microsoft.Hosting.Lifetime": "Information",
      "System.Net.Http": "Warning"
    }
  }
}
```

This:
- Suppresses the `EndpointMiddleware`/`Routing` chatter (Microsoft.AspNetCore at WARN)
- Keeps the `Now listening on...` startup line (Microsoft.Hosting.Lifetime at INFO)
- Silences our own `HttpClient` diagnostic logs unless retry-handler kicks (System.Net.Http at WARN)

### 3.8 Hot-path log demotion

Two ChatCompletions emissions move from INFO → DEBUG:

```fsharp
// ChatCompletions.fs:282 (streaming branch)  → Log.Debug  (was Log.Information)
Log.Debug("Routing target={Target} reason={Reason} priority={Priority} stream=true", ...)

// ChatCompletions.fs:367 (non-streaming)     → Log.Debug
Log.Debug("Routing target={Target} reason={Reason} priority={Priority}", ...)
```

Rationale: same info already in JSONL DecisionLog — operational log doesn't need duplicate at INFO. At DEBUG, operator running `--trace` still sees them.

### 3.9 HealthService transition-only logging

Current behavior: `Log.Information(... reachable)` every probe success. Change to:

```fsharp
// Only log on state transitions:
//   was-down → up   → INFO "HealthService: {Target} reachable (transitioned from down)"
//   was-up   → down → WARN "HealthService: {Target} unreachable (transitioned from up)"
//   was-down → down → DEBUG "HealthService: {Target} still unreachable"
//   was-up   → up   → DEBUG "HealthService: {Target} still reachable"
```

Reduces probe-loop chatter from 12/min to ~0 in steady state.

### 3.10 Startup banner (NEW)

After `WebApplication.Build()` succeeds and DI is fully wired, emit a single multi-line INFO event before `app.Run()`:

```
INF SmartRouter starting
    version          = 1.0.0+{git-sha-short}
    listen           = http://localhost:4000
    routing.algorithm = ml
    routing.threshold = 0.50
    model.version    = ml-{8-hex}
    canary.version   = ml-{8-hex}-canary OR (none)
    canary.percent   = 10
    embed.model      = bge-m3
    embed.sha        = {sha256-first-8-hex}
    queue.maxconc.122B = 1
    queue.fairnessK  = 10
    teacher.cap.daily = 1000
    log.dir          = ./logs/operational
```

Multi-line via Serilog's `{Message:l}` literal embedding or just `\n` in the message. Operator gets a complete startup snapshot in one grep.

Symmetric **shutdown banner** on `IHostApplicationLifetime.ApplicationStopping`:

```
INF SmartRouter stopping
    in-flight requests = 0
    queue.depth.high   = 0
    queue.depth.low    = 0
    drain.waited.ms    = 0
```

### 3.11 Endpoint-hit logging (NEW)

Each non-chat-completion endpoint emits one DEBUG line per hit (suppressed at default INFO; visible with `--trace`):

```fsharp
// in Endpoints/Health.fs (NEW logging)
Log.Debug("/health hit; status=200")

// Endpoints/Stats.fs
Log.Debug("/stats hit; queue_depth_high={H} active_122b={A}", h, a)

// Endpoints/Canary.fs
Log.Debug("/canary {Method} hit; result={Status}", method, status)

// Endpoints/Models.fs
Log.Debug("/v1/models hit; upstreams_reachable={N}", n)
```

(Avoid INFO; high-frequency probes from health checkers should NOT flood the log.)

### 3.12 Convention enforcement

All adapters already follow `"{Component}: {Action}"` message convention except a few:

| File | Current | Proposed |
|---|---|---|
| `BgeM3Embedder.fs:98` | `"BgeM3Embedder warm-up failed; continuing"` | OK (already prefixed) |
| `ModelBootstrapper.fs:32` | `"... model file missing at {Path} ..."` | Prefix: `"ModelBootstrapper: model file missing at {Path}..."` |
| `CompositionRoot.fs:137` | (one-off warning) | Prefix: `"CompositionRoot: ..."` |
| `Validator.fs:35, 114` | `"Validator.computeBaseline: ..."` | OK (uses dotted form — keep) |
| ASP.NET-host emissions | not under our control | leave to per-category override |

Phase 13 audits and normalizes all 80+ message strings to the `"{Component}: {Action}"` form (mechanical edits, no semantic change).

---

## 4. Open decisions (need user lock-in before planning)

### Q1. Where do operational rolling logs live — relative or absolute path?

- **Option A (relative): `logs/operational/`** under `WorkingDirectory` (= `/Users/ohama/llm-system/services/smart-router/logs/operational/` under launchd, `./logs/operational/` for dev). Symmetric with existing `logs/decisions/`. Easy testing.
- **Option B (absolute): `/Users/ohama/llm-system/services/logs/`** alongside the existing launchd-captured `smart-router.{log,err}`. More uniform with operator's qwen plists' log convention. But requires hardcoded path or config key.
- **Option C (configurable): `Logging:Directory` in appsettings.json**, defaults to `logs/operational`. Most flexible, modest extra config surface.

**Recommendation: C** — pattern matches existing `DecisionLog:Directory` config key (Phase 5 LOG-03).

### Q2. Per-file size cap

- 50 MB / 100 MB / 250 MB?
- 100 MB is Serilog community default; covers ~30 minutes of high QPS spike or 1 day baseline.
- 250 MB cuts file count but takes longer to grep; less friendly to log shippers.

**Recommendation: 100 MB.**

### Q3. Retention (`retainedFileCountLimit`)

- 30 / 60 / 90 days?
- 30 days is the Serilog default; balances disk usage with reasonable post-mortem window.

**Recommendation: 30 days operational, 90 days decision JSONL.** Decision JSONL is more valuable per-byte (Phase 8 retraining input).

### Q4. Decision JSONL retention enforcement — implement now or defer?

Phase 5 ships decision JSONL with **no retention** (open issue). Phase 13 could add a small `LogRetentionService : BackgroundService` that prunes:
- `logs/decisions/*.jsonl` older than 90 days
- `datasets/teacher-cap-*.json` older than 7 days

OR defer to its own Phase / accept indefinite growth (it's only ~135 MB / 90 days so not urgent).

**Recommendation: implement now, in Phase 13.** Cheap to ship, prevents future surprise.

### Q5. Compact JSON vs human-readable text for the file sink

- Text is operator-grep-friendly, smaller, slightly more brittle (custom parsers).
- JSON (`CompactJsonFormatter`) is log-shipper-friendly, ~2x larger, parseable.

**Recommendation: text for v1.** Add JSON sink as a future option behind a config flag if/when log shipping (Promtail, Vector, etc.) becomes a need.

### Q6. Format of `correlation_id` in output template

- `cid={correlation_id}` (current proposal) — readable, easy to grep.
- `[{correlation_id}]` — visually grouped.
- `cid=...` only when present, omit when empty (background services / startup) — requires conditional template (Serilog supports via `{#if correlation_id}cid={correlation_id} {#end}` or by emitting empty as `cid=-`).

**Recommendation: `cid={correlation_id}` always-present, empty renders as `cid=`.** Simple grep pattern. Empty value is itself informative ("this log is not request-scoped").

### Q7. Should we adopt `ILogger<T>` (Microsoft.Extensions.Logging) instead of static `Log.*` for new code?

Currently 0 uses of `ILogger<T>`; all 89 emissions go through `Serilog.Log.Information/...`. Static API is simpler but loses automatic `SourceContext` (must manually add `Log.ForContext<...>()`).

- **Option A (keep static):** simplest; adds Serilog enricher to inject `SourceContext` from caller's stack (slow) OR accepts empty `SourceContext` for static calls.
- **Option B (migrate to `ILogger<T>`):** idiomatic .NET; auto-`SourceContext`; constructor parameter on each adapter; ~80 file edits.
- **Option C (hybrid):** keep static for one-off / startup; use `ILogger<T>` for adapters that have heavy logging (HealthService, RetrainingService, CanaryService, ChatCompletions endpoint).

**Recommendation: A + per-call `Log.ForContext<...>()` for sites that warrant SourceContext** — minimal migration, matches existing F#-static idiom. If `SourceContext` is genuinely useful later we can do C in a follow-on. For now treat `SourceContext` as nice-to-have, not load-bearing.

### Q8. `--trace` flag → flips levelSwitch to Debug. Keep or replace with `--log-level=debug`?

- Current: `--trace` is a Boolean.
- Alternative: `--log-level=debug|info|warn|error` symmetric with the (about-to-be-deleted in Phase 12) `--routing-algorithm`.

**Recommendation: keep `--trace` as the simple boolean.** Less surface area; matches operator habit. If finer control needed later, add `--log-level` then.

### Q9. Does the launchd `StandardErrorPath` get rotated by external tooling, or do we keep growth bounded by operator-only intervention?

- Once Serilog ships its own rolling file sink, **`smart-router.err` should rarely have non-empty content** (Serilog flushes everything to its own files; stderr only catches pre-init / crash output).
- Operator can `truncate -s 0 smart-router.err` once per quarter as housekeeping.
- Or add a `newsyslog.d` config — but that's macOS-only ops infrastructure; not worth the complexity for what should be a near-empty file.

**Recommendation: do nothing.** Document in README that `smart-router.err` is reserved for crash output and stays small in normal operation; operator truncates manually if needed. This is a documentation deliverable, not a code one.

### Q10. Test fixtures — how do we test new logging behavior?

Existing `LoggingTests.fs` (Phase 5 LOG-04) uses `CapturingSink: ILogEventSink` to intercept Serilog events in-process. Same pattern works for Phase 13 additions.

- Verify timestamp format renders correctly.
- Verify `correlation_id` appears in output (for request-path tests).
- Verify rolling file sink writes to expected path with expected name pattern.
- Verify retention deletes oldest files when count exceeded (use a fixture with `retainedFileCountLimit = 3`).
- Verify dead-config rebind: `appsettings.json:Serilog.MinimumLevel.Override` actually filters `Microsoft.AspNetCore.*` lines.

**Recommendation: Phase 13 ships ~6 new test cases** in LoggingTests.fs (or a new LogRotationTests.fs).

---

## 5. Risks

### R1. **Disk fill on first deploy if size-cap miscalculated**

Default `fileSizeLimitBytes: 100MB` × `retainedFileCountLimit: 30` × dual-sink overhead can balloon if hot-path INFO survives unexpected spike. Mitigation: ship the hot-path demotion (§3.8) IN THE SAME PLAN as the file sink — they must atomic.

### R2. **Serilog FileSink open-handle on launchd-stop**

If Kestrel shuts down before Serilog flushes, in-flight log events are lost. Mitigation: `Log.CloseAndFlush()` in `Program.fs` shutdown path (already exists — `Logging.shutdown()`); add explicit `flushToDiskInterval = TimeSpan.FromSeconds(2)` so worst-case loss is 2s of buffered events.

### R3. **Concurrent Serilog file write under launchd auto-restart**

If launchd kills/restarts within `flushToDiskInterval`, two processes briefly hold the same file. Mitigation: `shared = false` (default) means second process can't open → it falls back to size-roll counter (`smart-router-2026-05-09_001.log`). Acceptable.

### R4. **`appsettings.json` migration breaks existing deployments**

Adding `Microsoft.AspNetCore`/`System.Net.Http` overrides changes effective log levels for already-deployed routers. Mitigation: document in README; Phase 13's `LogRetentionService` first run also emits "logging upgraded to vN" startup banner so operators know.

### R5. **Test fixture flakiness on file I/O races**

Existing LoggingTests use `testSequenced` for Console.SetOut races. New rolling-file tests should also `testSequenced` AND use unique temp directories per testCase — `Path.GetTempPath() + Guid.NewGuid().ToString("N")` pattern.

### R6. **`SourceContext` enricher misuse**

`Log.ForContext<T>()` returns a child logger; if assigned to a `let` binding at module level, it's frozen at module-load time (fine). If created per-call, it's allocation-heavy (avoid in hot path). Mitigation: convention — `let private logger = Log.ForContext<MyType>()` at module top, or use the static `Log.*` API for low-frequency emissions.

### R7. **`correlation_id` empty in background services**

Anywhere outside the request pipeline, `correlation_id` is empty string. Output template renders `cid=` with empty value. Acceptable UX; operator learns "empty cid = not a request log line".

### R8. **`LogContext.PushProperty` scope leaking across awaits**

Already in use (CorrelationMiddleware); F# `task {}` honors `AsyncLocal<T>` correctly. No new risk introduced by Phase 13.

---

## 6. Phase 13 plan sketch (waves)

This is a draft for the eventual `gsd-planner`. Numbers + plan names indicative.

### Wave 1: foundation
- **13-01: Serilog config rebind + new packages**
  - Add NuGet refs: `Serilog.Sinks.File`, `Serilog.Settings.Configuration`, `Serilog.Enrichers.Environment` (for `SourceContext`).
  - Rewrite `Adapters/Logging.fs` to:
    - `configure(config: IConfiguration)` reads `appsettings.json:Serilog`
    - dual sink: stderr (kept) + rolling file (NEW)
    - new output template with timestamp + level + SourceContext + correlation_id + message
    - `levelSwitch` retained as runtime override
  - Update `Program.fs` to pass `IConfiguration` to `configure`.
  - Update `appsettings.json:Serilog` section with full `MinimumLevel.Override` table.
  - **No call site changes yet** — backward-compatible.

### Wave 2: behavior changes (parallel)
- **13-02: Hot-path demotion**
  - `ChatCompletions.fs:282, 367` Information → Debug.
  - HealthService transition-only logic in `HealthService.fs`.
- **13-03: Convention normalization**
  - Audit all 89 emission sites, prefix with `"{Component}: "` where missing.
  - Add `Log.ForContext<T>()` private logger for files with ≥ 5 emissions (HealthService, CanaryService, RetrainingService, TeacherLabeler, ChatCompletions).
- **13-04: Endpoint-hit DEBUG logs**
  - Add one `Log.Debug` per endpoint in `Endpoints/{Health,Stats,Canary,Models}.fs`.

### Wave 3: new behaviors
- **13-05: Startup + shutdown banners**
  - Multi-line INFO emission after `app.Build()` and `app.StartAsync()` returns.
  - `IHostApplicationLifetime.ApplicationStopping` → shutdown banner.
- **13-06: Log retention service**
  - `LogRetentionService : BackgroundService` runs hourly.
  - Prunes `logs/decisions/*.jsonl` older than 90 days.
  - Prunes `datasets/teacher-cap-*.json` older than 7 days.
  - DI registered alongside other BackgroundServices.

### Wave 4: tests + docs
- **13-07: Tests**
  - Extend `LoggingTests.fs` (or new `LogRotationTests.fs`) with 6+ test cases covering output format, rolling, retention, per-category override, startup banner.
- **13-08: Operator docs**
  - README section: log file locations, levels, common operator queries (`grep -E 'WRN|ERR' logs/operational/*.log`, etc.).

### Verification (phase-level)
- 0 grep hits for `appsettings.json:Serilog` reading without `.ReadFrom.Configuration` (dead config eliminated).
- New files appear at `logs/operational/smart-router-{date}.log` after startup.
- `correlation_id` visible in operational log lines for request-path emissions.
- `dotnet test` green; new test count ~92–94 passed.
- `dotnet build` clean with `TreatWarningsAsErrors=true`.
- Operator running `tail -f logs/operational/*.log` sees: (1) startup banner, (2) zero entries during quiet idle, (3) request lines with `cid=...` on `/v1/chat/completions` traffic, (4) shutdown banner on Ctrl-C.

---

## 7. Decisions needed from user

1. **Q1 (log file location):** A (relative) / B (absolute under launchd logs/) / **C (configurable, default `logs/operational/`)** ← author rec
2. **Q2 (per-file size cap):** 50 / **100** / 250 MB ← author rec
3. **Q3 (retention):** **30 days operational, 90 days decision JSONL** ← author rec, or different
4. **Q4 (decision JSONL retention enforcement):** **implement in Phase 13** / defer to Phase 14+ ← author rec
5. **Q5 (file sink format):** **text** / JSON ← author rec
6. **Q6 (correlation_id rendering):** **`cid={correlation_id}` always-present** / `[cid]` / conditional
7. **Q7 (`ILogger<T>` migration):** **stay static + `Log.ForContext<T>()` per-module** / migrate everything / hybrid
8. **Q8 (`--trace` flag):** **keep as boolean** / replace with `--log-level=...`
9. **Q9 (`smart-router.err` rotation):** **document only, no code** / add newsyslog config
10. **Q10 (test count target):** **6 new test cases in LoggingTests.fs** / different

Lock these → I can draft the actual Phase 13 PLAN files into `.planning/preparing/` for the canonical session to execute later.

---

## 8. Recommended path (author's view, single-paragraph summary)

Add `Serilog.Sinks.File` + `Serilog.Settings.Configuration` packages; rewrite `Logging.fs` to read `appsettings.json:Serilog` and write to BOTH stderr (kept) AND a rolling daily/100MB file at `logs/operational/smart-router-{date}.log` (configurable via `Logging:Directory`); update output template to include timestamp + level + SourceContext + correlation_id + message; demote two ChatCompletions Information lines to Debug to cut hot-path volume by half; add transition-only logging to HealthService; emit a startup banner with version/port/algorithm/model_version/canary state and a symmetric shutdown banner; add a tiny `LogRetentionService : BackgroundService` that prunes decision JSONL > 90 days and teacher-cap files > 7 days; ship 6 new test cases and a README operator-guide section. ~10 file edits, no Core changes (operational logging is purely Cli concern), no breaking config changes (existing `Serilog.MinimumLevel.Default` keeps working — it just now ALSO accepts `Override`). Operator gets: `tail -f`-able live stderr (unchanged), durable rotated history, grep-by-correlation-id, suppressed ASP.NET noise, and a one-line proof-of-startup confirming what's actually running.
