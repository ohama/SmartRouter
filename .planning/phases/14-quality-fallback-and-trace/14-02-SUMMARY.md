---
phase: 14
plan: 02
subsystem: trace-logging
tags: [trace, channel, backgroundservice, cli-flag, di-registration, jsonl]

dependency-graph:
  requires: ["14-01"]
  provides: ["TraceLogger BackgroundService", "ITraceLogger DI interface", "--trace-responses CLI flag", "Trace:Enabled config injection"]
  affects: ["14-04 (ChatCompletions ITraceLogger consumer)"]

tech-stack:
  added: []
  patterns:
    - "Triple-registration DI pattern (concrete + IInterface alias + AddHostedService) for conditional BackgroundService"
    - "BoundedChannelFullMode.Wait for trace Channel (vs DecisionLogWriter's DropWrite)"
    - "applyTraceFlagFromArgs per-branch IConfigurationBuilder injection pattern"

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/TraceLogger.fs
  modified:
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/Program.fs
    - src/SmartRouter.Cli/CompositionRoot.fs

decisions:
  - id: TRACE-01
    description: "TraceLoggerOptions fields declared mutable (not just [<CLIMutable>]) — required for Configure<T>(Action<T>) mutation pattern in F#; CLIMutable alone only helps reflection-based JSON binders"
  - id: TRACE-02
    description: "TraceLogger.StopAsync overrides synchronously (no task{} CE) — base.StopAsync called directly to avoid FS0405 (protected member in CE context); drain loop is synchronous; mirrors DecisionLogWriter.StopAsync"
  - id: TRACE-03
    description: "open SmartRouter.Cli.Adapters.Json omitted from TraceLogger.fs — TraceLogger defines its own jsonOpts with SnakeCaseLower + JsonFSharpConverter(); no shared Json.fs exports needed"
  - id: TRACE-04
    description: "applyTraceFlagFromArgs called in BOTH --retrain and main Kestrel branches — consistent API; no-op in retrain path since configureWithoutMl doesn't register ITraceLogger"
  - id: TRACE-05
    description: "ITraceLogger NOT registered when traceEnabled=false — consumers must use GetService<ITraceLogger>() (returns null) not GetRequiredService (throws); ChatCompletions defensive null check is 14-04's responsibility"

metrics:
  duration: "7 min"
  completed: "2026-05-10"
---

# Phase 14 Plan 02: Prompt UID and Trace Logging Infrastructure Summary

**One-liner:** TraceLogger Channel+BackgroundService (BoundedChannelFullMode.Wait, daily JSONL rotation) with `--trace-responses` CLI flag wiring and conditional triple-reg DI in configureRequestPipeline.

## What Was Built

### Task 1: `Adapters/TraceLogger.fs` (NEW, 145 lines)

- `TraceRecord` — 12-field `[<CLIMutable>]` record matching CONTEXT spec exactly: schema_version, correlation_id, prompt_uid (12-hex), prompt_hash (64-hex), prompt_excerpt (<=200 chars), initial_target, initial_response_excerpt (option), fallback_kind (option: "quality"|"availability"|null), final_target, final_response_excerpt, total_latency_ms (float), timestamp (DateTimeOffset)
- `TraceLoggerOptions` — CLIMutable record with `mutable` fields (Directory, ChannelCapacity); mutable required for Configure<T>(Action<T>) mutation pattern
- `ITraceLogger` — interface with `Log: TraceRecord -> unit`; blocks under back-pressure (Channel Wait mode)
- `TraceLogger : BackgroundService` — Channel + single-reader BackgroundService; daily file rotation by UTC date; `logs/trace/YYYY-MM-DD.jsonl`; FileShare.None per-line append; JSONL with SnakeCaseLower + JsonFSharpConverter; StopAsync drains pending records synchronously before base.StopAsync
- `SmartRouter.Cli.fsproj`: Compile entry after DecisionLogWriter.fs

### Task 2: `Program.fs` — `--trace-responses` flag detection

- `applyTraceFlagFromArgs (configBuilder: IConfigurationBuilder) (args: string array) : unit` helper placed alongside `applyLogLevelFromArgs` and `parsePortFromArgs`
- Detects `--trace-responses` in args; injects `Trace:Enabled=true` into the per-branch IConfigurationBuilder via `AddInMemoryCollection`
- `Log.Information` confirms activation at startup (Serilog initialized by bootstrap preamble before both branches)
- Called in `--retrain` branch before `configureWithoutMl` (harmless no-op; configureWithoutMl omits ITraceLogger)
- Called in main Kestrel branch before `configureServices` (= configureRequestPipeline) so CompositionRoot reads the key during DI registration

### Task 3: `CompositionRoot.fs` — conditional ITraceLogger registration

- `open SmartRouter.Cli.Adapters.TraceLogger` added to imports
- `configureRequestPipeline` only (not `configureWithoutMl`)
- Reads `Trace:Enabled` from `config.["Trace:Enabled"]`; case-insensitive comparison
- When `true`: `Configure<TraceLoggerOptions>` action (Directory="logs/trace", ChannelCapacity=1000) + triple-reg (concrete AddSingleton<TraceLogger> + ITraceLogger alias + AddHostedService<TraceLogger>)
- When `false`: ITraceLogger not registered; consumers must GetService<ITraceLogger>() which returns null

## Decisions Made

| Decision | Detail |
|----------|--------|
| TRACE-01 | TraceLoggerOptions fields declared `mutable` (not just CLIMutable) — F# Configure<T>(Action<T>) requires it |
| TRACE-02 | TraceLogger.StopAsync overrides synchronously — avoids FS0405 in task{} CE; drain is sync |
| TRACE-03 | No `open SmartRouter.Cli.Adapters.Json` — TraceLogger creates its own jsonOpts |
| TRACE-04 | applyTraceFlagFromArgs in both branches for consistent API |
| TRACE-05 | ITraceLogger absent when disabled — GetService (nullable) not GetRequiredService |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] TraceLoggerOptions mutable fields**

- **Found during:** Task 1 (design review pre-implementation)
- **Issue:** Plan template used plain `[<CLIMutable>]` with immutable fields. F# compiler enforces field immutability in source code; `Configure<T>(Action<T>)` mutation pattern silently fails without `mutable` keyword. This is documented in STATE.md decision 13-05.
- **Fix:** Declared both `Directory` and `ChannelCapacity` as `mutable` fields
- **Files modified:** `src/SmartRouter.Cli/Adapters/TraceLogger.fs`
- **Commit:** 4fccb6e

**2. [Rule 1 - Bug] TraceLogger.StopAsync override — FS0405**

- **Found during:** Task 1 build (first compile attempt)
- **Issue:** Plan template used `task { ... do! base.StopAsync(ct) } :> Task` inside StopAsync override. F# FS0405 forbids accessing protected members (`base.*`) from inside CE lambdas. Build failed with 3 errors.
- **Fix:** Rewrote StopAsync as synchronous override (channel.Writer.TryComplete() + sync drain loop + `base.StopAsync(ct)` called directly outside any CE), mirroring the DecisionLogWriter.StopAsync pattern
- **Files modified:** `src/SmartRouter.Cli/Adapters/TraceLogger.fs`
- **Commit:** 4fccb6e

## Verification Results

```
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj -> 0 warnings, 0 errors
dotnet run tests (--sequenced) -> 80 passed, 16 ignored, 0 failed
grep TraceLogger|ITraceLogger CompositionRoot.fs -> 12 hits (>= 3 expected)
grep Trace:Enabled|traceEnabled CompositionRoot.fs -> 4 hits (>= 1 expected)
grep --trace-responses|applyTraceFlagFromArgs|Trace:Enabled Program.fs -> 11 hits (>= 3 expected)
TraceLogger.fs BoundedChannelFullMode.Wait -> 3 hits (>= 1 expected)
TraceLogger.fs type count (ITraceLogger|TraceLogger|TraceRecord) -> 4 hits (>= 3 expected)
```

## Next Phase Readiness

- **14-03** (quality fallback) can proceed: ITraceLogger interface defined; CompositionRoot triple-reg ready
- **14-04** (ChatCompletions integration): must use `sp.GetService<ITraceLogger>()` (nullable) not GetRequiredService; defensive null check required; prompt_uid = first 12 hex chars of prompt_hash
- No blockers for subsequent plans

## Commits

| Hash | Description |
|------|-------------|
| 4fccb6e | feat(14-02): add TraceLogger BackgroundService |
| 8b83006 | feat(14-02): wire --trace-responses flag in Program.fs |
| 73e94f7 | feat(14-02): conditional ITraceLogger registration in CompositionRoot |
