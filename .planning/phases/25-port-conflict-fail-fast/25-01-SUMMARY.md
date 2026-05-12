---
phase: 25-port-conflict-fail-fast
plan: 01
subsystem: startup-fail-fast
tags: [port-probe, tcp, kestrel, operator-ux, launchd, stderr, expecto]

dependency-graph:
  requires: []
  provides:
    - PortProbe adapter (synchronous TCP bind probe, BCL-only)
    - PROBE-04 test suite (4 cases)
    - Program.fs startup hook (loopback-only, before Kestrel bind)
    - README § 13 troubleshooting recipe
  affects:
    - Any future phase that changes Program.fs startup sequence
    - Operator docs (§ 13 is the canonical troubleshooting reference)

tech-stack:
  added: []
  patterns:
    - Fail-fast probe before WebApplication.CreateBuilder / builder.Build()
    - eprintfn to Console.Error for operator-facing startup errors (OBS-04)
    - Environment.Exit(1) before host allocation (no dispose needed)

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/PortProbe.fs
    - tests/SmartRouter.Tests/PortProbeTests.fs
  modified:
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs
    - src/SmartRouter.Cli/Program.fs
    - README.md

decisions:
  - id: D-01
    choice: "new TcpListener(...) syntax required by F# compiler (IDisposable warning as error)"
    rationale: "TreatWarningsAsErrors=true; FS0760 fires on bare TcpListener(addr, port); added `new` keyword"
    alternatives: ["use binding (unnecessary — listener is stopped immediately in tryBind)"]

metrics:
  duration: "16 minutes"
  completed: "2026-05-12"
---

# Phase 25 Plan 01: Port-Conflict Fail-Fast Summary

**One-liner:** TCP loopback port probe runs before Kestrel bind; port conflict exits 1 with 4-line actionable stderr block instead of 30-line SocketException stacktrace.

## What Was Built

### PROBE-01: PortProbe adapter (`src/SmartRouter.Cli/Adapters/PortProbe.fs`)

New 35-LoC synchronous adapter in `SmartRouter.Cli/Adapters/`. Exports:
- `PortConflictError` record: `{ Port: int; Address: string; Reason: string }`
- `tryBind : int -> IPAddress -> Result<unit, PortConflictError>` — attempts `TcpListener.Start()` / `Stop()`, returns `Ok ()` on success, `Error { ... }` on `SocketException`.

BCL-only (no Serilog, no DI, no async/task). Registered in `SmartRouter.Cli.fsproj` with Phase 25 comment, before `SessionCascadeStats.fs`.

**Deviation from plan:** Used `new TcpListener(addr, port)` instead of bare `TcpListener(addr, port)`. The F# compiler (TreatWarningsAsErrors=true) emits FS0760 warning for IDisposable objects constructed without `new`. Fixed immediately (Rule 1 auto-fix). The plan's code snippet did not include `new`; both forms are semantically identical, `new` is required here.

### PROBE-04: Test suite (`tests/SmartRouter.Tests/PortProbeTests.fs`)

4 testCases inside `testSequenced` (socket binding requires sequencing):
- `tryBind returns Ok () for a free port` — picks random high port (20000-60000), retries once on collision
- `tryBind returns Error when port is already bound` — binds blocker TcpListener, confirms Error
- `Error.Port matches the probed port` — confirms `err.Port = port` and `err.Address = "127.0.0.1"`
- `tryBind completes in <100ms on conflict` — Stopwatch measures < 100ms

Wired into both `SmartRouter.Tests.fsproj` (before `RouterTests.fs`) and `RouterTests.fs` rootTests (after `SessionKeyCascadeTests.tests`).

Test count delta: **187 → 191** (Passed: 191, Ignored: 18, Failed: 0).

### PROBE-02 + PROBE-03: Program.fs startup hook (`src/SmartRouter.Cli/Program.fs`)

Added to main Kestrel branch only (NOT the `--retrain` branch):
1. `open System`, `open System.Net`, `open SmartRouter.Cli.Adapters.PortProbe` imports
2. `parseListenUrl : string -> (string * int) option` — parses `Uri.TryCreate` to extract host + port
3. `isLoopbackHost : string -> bool` — returns true for `localhost`, `127.0.0.1`, `::1`, or any `IPAddress.IsLoopback` address
4. Probe call site between `applyTraceFlagFromArgs` and `CompositionRoot.configureServices`:
   - Reads `Kestrel:Endpoints:Http:Url` (post-`--port` override)
   - Non-parseable URL → `Log.Debug` + skip
   - Non-loopback host → `Log.Debug` + skip
   - Loopback host: calls `PortProbe.tryBind port IPAddress.Loopback`
   - On `Ok ()` → continue startup
   - On `Error err` → `Log.Debug` (forensic only) + exactly 4 `eprintfn` lines + `Logging.shutdown()` + `Environment.Exit(1)`

### PROBE-05: README § 13 recipe (`README.md`)

New `### Port 4000 is already in use` sub-section inserted as FIRST sub-section under `## 13. Troubleshooting` (line 1001, before `### model_unavailable`). Contains:
- Symptom block with verbatim 4-line ERROR block (byte-for-byte match with `Program.fs` eprintfn lines)
- Diagnosis: `lsof` command + 3 typical culprits
- Resolution: launchd unload, kill PID, `--port` override
- Note on loopback-only probe behavior

## The 4-Line ERROR Block (as shipped)

```
ERROR: Port 4000 is already in use. Smart Router cannot start.
Likely culprit: another smart-router instance, or a different process bound to :4000.
To investigate: `lsof -iTCP:4000 -sTCP:LISTEN -n -P`
To stop a stuck launchd instance: `launchctl unload ~/Library/LaunchAgents/com.ohama.smartrouter.plist`
```

## Architectural Invariants Preserved

- **ARCH-01:** `PortProbe.fs` lives in `SmartRouter.Cli/Adapters/`. `SmartRouter.Core` unchanged.
- **ARCH-02:** `scripts/check-no-async.sh` passes. `PortProbe.fs` uses no `async`/`task`.
- **OBS-04:** Operator-facing 4 lines go to `Console.Error` via `eprintfn`. No Serilog dependency on the error path.
- **TreatWarningsAsErrors:** 0 errors, 0 warnings across solution build.

## Test Count Delta

| Phase | Passed | Ignored | Failed |
|-------|--------|---------|--------|
| Pre-Phase-25 (v2.1) | 187 | 18 | 0 |
| Post-Phase-25 | **191** | 18 | 0 |

Delta: +4 (PROBE-04 PortProbe testCases).

## Atomic Commits

| Order | Hash | Type | Description |
|-------|------|------|-------------|
| 1 | `0a6a212` | feat | add PortProbe adapter |
| 2 | `699e3ba` | test | add PortProbe tests (PROBE-04) |
| 3 | `3b1a4d9` | feat | wire port probe into startup |
| 4 | `b3f2ed9` | docs | document port-conflict troubleshooting |

## Operator-Facing Impact

Before Phase 25, a double-loaded launchd plist produced a 30-line Kestrel `SocketException` stacktrace in `~/llm-system/services/logs/smart-router.err`. The root cause (port already in use) was buried in the stacktrace. An operator's first read of the log required scrolling through .NET internals to find the port number.

After Phase 25, the same event produces exactly:
```
ERROR: Port 4000 is already in use. Smart Router cannot start.
Likely culprit: another smart-router instance, or a different process bound to :4000.
To investigate: `lsof -iTCP:4000 -sTCP:LISTEN -n -P`
To stop a stuck launchd instance: `launchctl unload ~/Library/LaunchAgents/com.ohama.smartrouter.plist`
```
Exit code 1. Process terminates in < 500ms (before Kestrel ever allocates). The actionable command is on line 3; the resolution is on line 4.

## Deviations from Plan

### Auto-Fixed Issues

**1. [Rule 1 - Bug] `new TcpListener(addr, port)` syntax required**

- **Found during:** Task 1 (first build attempt)
- **Issue:** Plan's code snippet used `TcpListener(addr, port)` without `new`. F# compiler emits FS0760 ("use `new Type(args)`") for IDisposable objects. With `TreatWarningsAsErrors=true` this is a build error.
- **Fix:** Changed to `new TcpListener(addr, port)` in `PortProbe.fs` and `new TcpListener(...)` in `PortProbeTests.fs`.
- **Files modified:** `PortProbe.fs`, `PortProbeTests.fs`
- **Commits:** Within Task 1 and Task 2 (no separate fix commit needed; caught before staging)

### No Other Deviations

Plan executed exactly as specified for Tasks 2, 3, and 4.

## TD-5 (PITFALL-10) Observation

The pre-existing `QueueTests.fs:239-307` timing race (TD-5) did not manifest during this plan's test runs. All 3 test runs completed with 191 passed / 0 failed. The flake remains a deferred concern per STATE.md.
