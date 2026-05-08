---
phase: 05-routing-decision-logging
plan: 01
subsystem: logging
tags: [channel, background-service, serilog, jsonl, correlation-id, sha256, daily-rotation, graceful-shutdown]

# Dependency graph
requires:
  - phase: 01-foundation
    provides: Domain types (Message, RoutingReason, RoutingDecision), CompositionRoot DI pattern, Program.fs ASP.NET pipeline
  - phase: 04-ml-algorithm-seam
    provides: RoutingAlgorithm type, ML.applyML placeholder, DI for algorithm dispatch
provides:
  - DecisionLog record (12 fields: schema_version, correlation_id, prompt_hash, prompt_korean_char_ratio, routing_algorithm, routing_reason, target, latency_ms, fallback_used, model_version, task_type, timestamp)
  - IDecisionLogger interface (fire-and-forget Log method)
  - DecisionLogWriter BackgroundService (Channel single-writer, daily UTC rotation, DropWrite + warning, graceful drain)
  - CorrelationMiddleware (Guid N-format ID per request, HttpContext.Items + Serilog LogContext)
  - CompositionRoot DI registrations (concrete singleton + IDecisionLogger alias + IHostedService alias)
  - appsettings.json DecisionLog section, .gitignore logs/
affects: [05-02-endpoint-wiring, 05-03-logging-tests, 06-ml-routing, 08-retraining-loop, 09-canary]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Channel<T> + BackgroundService single-writer pattern for concurrent JSONL logging"
    - "DI triple-registration pattern: concrete singleton + interface alias + IHostedService alias (same instance)"
    - "app.Use lambda middleware registered FIRST for cross-cutting concerns (correlation ID)"

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/DecisionLogger.fs
    - src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs
    - src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs
  modified:
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/Program.fs
    - src/SmartRouter.Cli/appsettings.json
    - .gitignore

key-decisions:
  - "DropWrite (not DropOldest) per CONTEXT.md constraint — newest entry dropped, warning to stderr"
  - "ChannelClosedException caught alongside OperationCanceledException — both are valid graceful-shutdown signals when TryComplete() fires before stoppingToken cancels"
  - "app.Use with explicit Func<HttpContext, RequestDelegate, Task> cast required for F# lambda to resolve correct overload"
  - "new DecisionLogWriter(...) syntax required because BackgroundService implements IDisposable — F# FS0760 rule"
  - "JsonFSharpConverter from System.Text.Json.Serialization namespace (not FSharp.SystemTextJson prefix) — matches existing Json.fs pattern"

patterns-established:
  - "DI triple-registration: AddSingleton<Concrete>, AddSingleton<IInterface>(sp -> sp.GetRequiredService<Concrete>()), AddHostedService<Concrete>(sp -> ...) — single instance for all three roles"
  - "correlationMiddleware function name is canonical — must_haves grep depends on this exact name"

# Metrics
duration: 8min
completed: 2026-05-08
---

# Phase 05 Plan 01: Decision-Log Infrastructure Summary

**Channel-backed BackgroundService JSONL writer with daily UTC rotation, 32-char Guid correlation middleware FIRST in ASP.NET pipeline, and 12-field DecisionLog schema with SHA-256 prompt hash and Korean char ratio**

## Performance

- **Duration:** 8 min
- **Started:** 2026-05-08T05:56:37Z
- **Completed:** 2026-05-08T06:04:59Z
- **Tasks:** 3
- **Files modified:** 8

## Accomplishments

- 12-field `DecisionLog` record with `schema_version=1` and `prompt_korean_char_ratio` (Loop B Phase 9 cohort signal) — schema complete from day one per CONTEXT.md "add now or pay later" decision
- `DecisionLogWriter` BackgroundService: `Channel.CreateBounded` (10000, DropWrite), single consumer loop, daily UTC file rotation by lazy `DateTime.UtcNow.Date` check per entry, graceful drain with `TryRead` after `OperationCanceledException`/`ChannelClosedException`
- `correlationMiddleware` function (name is canonical for must_haves grep): generates `Guid.NewGuid().ToString("N")` per request, pushes to both `HttpContext.Items["CorrelationId"]` and `Serilog.Context.LogContext.PushProperty("correlation_id", ...)` registered FIRST in pipeline before `UseSerilogRequestLogging`
- All 44 existing tests still pass (0 warnings, 2 pending load tests as before)

## Task Commits

1. **Task 1: DecisionLogger.fs + CorrelationMiddleware.fs** — `7c104a1` (feat)
2. **Task 2: DecisionLogWriter.fs + .fsproj compile order** — `1201281` (feat)
3. **Task 3: DI + middleware + appsettings + .gitignore** — `aba9b4e` (feat)
4. **Deviation fix: ChannelClosedException** — `272b480` (fix)

## Files Created/Modified

- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` — DecisionLog record (12 fields), IDecisionLogger interface, computePromptHash (SHA-256), computeKoreanRatio ([가-힣] Hangul Syllables), formatReason (hand-rolled DU serializer)
- `src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs` — BackgroundService Channel consumer, DropWrite with Serilog warning, daily UTC rotation, graceful drain, FSharpConverter for string option
- `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` — `correlationMiddleware` function (ASP.NET lambda middleware, Guid N-format, Serilog LogContext)
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — 3 new Compile entries in correct F# compile order
- `src/SmartRouter.Cli/CompositionRoot.fs` — DecisionLogWriter concrete singleton + IDecisionLogger alias + IHostedService alias (same instance); uses `new` keyword (FS0760)
- `src/SmartRouter.Cli/Program.fs` — `correlationMiddleware` registered FIRST via `app.Use(Func<...>(...))`, `logs/decisions` directory ensured at startup
- `src/SmartRouter.Cli/appsettings.json` — `DecisionLog` section with Directory + ChannelCapacity
- `.gitignore` — `logs/` entry with comment

## Decisions Made

- **DropWrite (not DropOldest):** CONTEXT.md constraint. Newest entry is dropped and a Serilog warning fires to stderr — visible to operator, never silent.
- **DI triple-registration pattern:** `AddSingleton<DecisionLogWriter>` for concrete instance, `AddSingleton<IDecisionLogger>` + `AddHostedService<DecisionLogWriter>` both alias via `GetRequiredService<DecisionLogWriter>()`. One instance, three roles.
- **`app.Use` with explicit `Func<>` cast:** F# lambda infers incorrect arity without the explicit `System.Func<HttpContext, RequestDelegate, Task>` cast.
- **`new DecisionLogWriter(...)` syntax:** F# FS0760 requires `new Type(...)` when the type implements `IDisposable` — `BackgroundService` does via `IHostedService`'s dispose chain.
- **`JsonFSharpConverter()` from `open System.Text.Json.Serialization`:** Matches existing `Json.fs` pattern — the class lives in the `System.Text.Json.Serialization` namespace, not under `FSharp.SystemTextJson` prefix.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] ChannelClosedException on graceful shutdown**

- **Found during:** Task 3 (smoke run verification after DI + middleware registration)
- **Issue:** `StopAsync` calls `channel.Writer.TryComplete()` before `base.StopAsync` cancels `stoppingToken`. When the channel is empty at shutdown, `ReadAsync` throws `ChannelClosedException` (not `OperationCanceledException`). The consumer loop only caught `OperationCanceledException`, causing a spurious `[ERR] DecisionLogWriter: writer loop crashed` log on every clean shutdown.
- **Fix:** Added `| :? ChannelClosedException -> ()` alongside the existing `OperationCanceledException` catch in the consumer loop. Both are valid graceful-shutdown signals.
- **Files modified:** `src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs`
- **Verification:** Smoke run (`dotnet run` → 3s → kill) shows clean shutdown with no error log; `logs/decisions/` directory created.
- **Committed in:** `272b480` (separate fix commit)

**2. [Rule 2 - Missing] `FSharp.SystemTextJson.JsonFSharpConverter()` namespace error**

- **Found during:** Task 2 (first build attempt)
- **Issue:** Plan template showed `FSharp.SystemTextJson.JsonFSharpConverter()` as a dotted namespace access. This form is not valid — the class is in `System.Text.Json.Serialization` namespace (accessed via `open System.Text.Json.Serialization`), not as a sub-namespace of `FSharp.SystemTextJson`. Build error FS0039.
- **Fix:** Added `open System.Text.Json.Serialization` to DecisionLogWriter.fs imports; use `JsonFSharpConverter()` directly (same as existing `Json.fs` pattern).
- **Files modified:** `src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs`
- **Verification:** Build succeeds with 0 warnings.
- **Committed in:** `1201281` (included in task commit)

---

**Total deviations:** 2 auto-fixed (1 bug, 1 missing critical build fix)
**Impact on plan:** Both auto-fixes necessary for correctness. No scope creep.

## Issues Encountered

- First run of CONC-03 ("PITFALL-10 starvation") failed on the first test run pass — this is a pre-existing flaky timing test known from Phase 3. Passed on second and third run. Not caused by Phase 5 changes.

## User Setup Required

None — no external service configuration required.

## Next Phase Readiness

- **Phase 05-02 (endpoint wiring):** `IDecisionLogger` DI singleton ready; `CorrelationMiddleware.CorrelationIdKey` constant available for `ctx.Items` lookup; `DecisionLogger.computePromptHash`, `computeKoreanRatio`, `formatReason` all available. Plan 05-02 should also wire `RoutingAlgorithmRegistration` (Name + ModelVersion) as planned.
- **Phase 05-03 (logging tests):** `DecisionLogWriter` accepts a `DecisionLogOptions` directly (temp directory injectable via constructor); `WebApplicationFactory` already in test project; `IDecisionLogger` resolvable from DI.
- **No blockers** — all 44 existing tests pass, build is clean (0 warnings), CI checks pass.

---
*Phase: 05-routing-decision-logging*
*Completed: 2026-05-08*
