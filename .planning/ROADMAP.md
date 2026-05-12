# Roadmap: smart-router v2.2 — Operator Fail-Fast on Port Conflict

## Overview

v2.2 is a single-phase minimal milestone addressing one operator pain point captured during v2.1 archive: smart-router fails ungracefully (raw `SocketException` / `AddressAlreadyInUse` stacktrace) when port `:4000` is already bound at startup. This traps operators in launchd KeepAlive retry loops and can mask silent misrouting when a different process answers Hermes Agent on `:4000`.

The fix adds a single startup probe via `TcpListener.Start()` BEFORE Kestrel binds, emits a clear actionable message to stderr (with `lsof` + `launchctl unload` next-step commands), and `Environment.Exit(1)`. New `SmartRouter.Cli.Adapters.PortProbe` adapter (BCL-only, follows ARCH-01 Cli-adapter placement) + Expecto tests + README §13 Troubleshooting recipe.

Other deferred items (TD-2/3/4/5, HMRS-FUTURE-01, MODE-FUTURE-01, SPEC-01..03, DRT-01, DB-01/02) stay explicitly out of v2.2 — a future minor milestone may sweep tech debt.

## Milestones

- ✅ **v1.0–v1.3 ML Routing** — Phases 1-16 (shipped 2026-05-11; archived to `.planning/milestones/v1.3-ROADMAP.md`)
- ✅ **v2.0 Self-Routing + Session-Aware** — Phases 17-20 (shipped 2026-05-12; archived to `.planning/milestones/v2.0-ROADMAP.md`)
- ✅ **v2.1 Hermes-less Session Tiering** — Phases 21-24 (shipped 2026-05-12; archived to `.planning/milestones/v2.1-ROADMAP.md`)
- 🚧 **v2.2 Operator Fail-Fast on Port Conflict** — Phase 25 (in progress)

## Phases

**Phase Numbering:**
- Integer phases (25): Planned v2.2 milestone work
- Decimal phases (25.1, 25.2): Urgent insertions — none used yet
- v2.2 continues numbering from v2.1's end at Phase 24

- [x] **Phase 25: Port-conflict fail-fast at startup** ✓ — `SmartRouter.Cli.Adapters.PortProbe.tryBind` (36 lines; synchronous BCL `TcpListener` + `SocketException` catch → `Result<unit, PortConflictError>`) wired at `Program.fs:287` (between `WebApplication.CreateBuilder` line 235 and `builder.Build()` line 307; outside `--retrain` branch at line 233); 4× `eprintfn` + `Environment.Exit(1)` on Error (OBS-04 stream separation: no Serilog on that path); reads merged `Kestrel:Endpoints:Http:Url` so existing `--port` CLI override works automatically; loopback-only via `isLoopbackHost` (non-loopback URLs skip with `Log.Debug`). 4 PROBE-04 testCases in new `PortProbeTests.fs` (free-port Ok, in-use Error, Error.Port match, <100ms timing) wired into both fsproj and `RouterTests.fs:46 rootTests`. README §13 Troubleshooting recipe inserted at lines 1001/1006 (verbatim error string + lsof + launchctl unload). 1 plan / 4 task commits + 1 metadata commit (0a6a212, 699e3ba, 3b1a4d9, b3f2ed9, 51118ab). Test baseline 187 → 191 passed. Runtime smoke verified: staged TcpListener on :18888, `dotnet run -- --port 18888` produced exact 4-line block + exit 1 with no Kestrel output. All 8 must-haves verified by gsd-verifier. One auto-fixed deviation: `new TcpListener(addr, port)` syntax required (F# FS0760 warning-as-error).

## Phase Details

### Phase 25: Port-conflict fail-fast at startup

**Goal**: When smart-router starts and the configured loopback listen port (default `127.0.0.1:4000`) is already bound, the application emits a clear single-block stderr message naming the port + suggesting `lsof` and `launchctl unload` next steps, then exits with code 1 BEFORE Kestrel attempts to bind. Operators reading `~/llm-system/services/logs/smart-router.err` (or `dotnet run` stderr) see a 4-line actionable message instead of a 30-line `SocketException`/`AddressAlreadyInUse` stacktrace. When the port is free, startup behavior is unchanged.

**Depends on**: Nothing (pure additive — new adapter, one startup hook, new test module, README §13 addition; no existing code semantics modified)

**Requirements**: PROBE-01, PROBE-02, PROBE-03, PROBE-04, PROBE-05

**Success Criteria** (what must be TRUE):

1. **Probe-correctness Ok path**: `PortProbe.tryBind 0 IPAddress.Loopback` succeeds for an OS-assigned free port; the function returns `Ok ()` in <100ms.

2. **Probe-correctness Error path with correct port**: Pre-staging a `use listener = TcpListener(IPAddress.Loopback, port)` + `listener.Start()` on an OS-assigned high port, then calling `PortProbe.tryBind port IPAddress.Loopback` returns `Error err` where `err.Port = port`. Test framework: Expecto in `tests/SmartRouter.Tests/PortProbeTests.fs`, wired into `RouterTests.fs rootTests` (per ARCH testing invariant: explicit `rootTests`, never auto-discovery).

3. **Startup wiring intercepts before Kestrel**: Running `dotnet run --project src/SmartRouter.Cli/` when another process is bound to `127.0.0.1:4000` produces exactly the 4-line ERROR message on stderr (no Serilog wrapping, no stacktrace) and the process exits with code `1` within ~500ms (well before Kestrel's normal `AddressAlreadyInUse` exception path).

4. **No regression on healthy startup**: Running `dotnet run` against an unbound port boots Kestrel normally; existing 187-test baseline remains 187 passing + 18 ignored + 0 failed PLUS the new PROBE-04 tests (final: 187 + N PROBE tests).

5. **README §13 Troubleshooting recipe exists**: `grep -n 'Port 4000 is already in use' README.md` returns a match in the §13 Troubleshooting section. The recipe is searchable both by error string (operators who grep the stderr message find it) and by symptom phrasing ("smart-router exits at startup").

**Plans**: 1 plan (estimated)

Plans:
- [x] 25-01-PLAN.md — PortProbe adapter + Program.fs wire + PortProbeTests + README §13 (PROBE-01..05) ✓ 2026-05-12

---

## Progress

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 25. Port-conflict fail-fast at startup | v2.2 | 1/1 | ✓ Complete | 2026-05-12 |

## Coverage

v2.2 requirement coverage: **5 / 5 mapped** (no orphans, no duplicates).

| Phase | Requirements |
|-------|--------------|
| Phase 25 | PROBE-01, PROBE-02, PROBE-03, PROBE-04, PROBE-05 (5 reqs) |

## Architectural Invariants (carried into v2.2)

- **ARCH-01** (Core BCL-only): `PortProbe.fs` lives in `SmartRouter.Cli.Adapters` — NOT in `SmartRouter.Core`. `System.Net.Sockets` is BCL but the adapter placement is the invariant. Pattern mirrors v2.1 (`HermesSessionExtract.fs`, `ContentFingerprint.fs` in Cli) and v2.0 (`SessionStore.fs`, `SelfRouter.fs` in Cli; only `HardRules.fs` was Core-BCL).
- **ARCH-02** (`task {}` only): `PortProbe.tryBind` is a synchronous pure function; no async work introduced. `scripts/check-no-async.sh` continues to pass.
- **DecisionLog `schema_version=1` unchanged**: Port probe runs upstream of any request — no DecisionLog rows are emitted. Schema untouched.
- **Stream separation** (OBS-04): The startup ERROR message goes to `Console.Error` (`stderr`) NOT through Serilog. This matches the rule that `stdout` is reserved for application output and ensures visibility even if Serilog hasn't initialized or has a misconfigured sink.
- **Loopback-only invariant**: Probe targets `IPAddress.Loopback` (127.0.0.1) only. Non-loopback URLs in config are skipped with a debug log line — smart-router is loopback-only by constraint.
- **README sync rule (CLAUDE.md §13)**: New Troubleshooting recipe in §13 — README update lands in the same commit set as the code change per project convention.
- **Per-task atomic commits** with `{type}({phase}-{plan}): {task-name}` format. Test framework: Expecto with explicit `rootTests` list (no auto-discovery; PortProbeTests.fs entry added to `rootTests` in `RouterTests.fs`).

---

## Milestone Summary

**Phase count:** 1 (Phase 25)
**Total plans:** 1 (estimated)
**Requirement coverage:** 5/5

**Why a single-phase minimal milestone:**

1. **Operator pain is concrete and narrow**: launchd retry loops + silent misrouting on `:4000` collision. The fix is mechanically small (a probe + a print + Exit(1)).
2. **No domain unknowns**: `TcpListener.Start()` is a standard .NET pattern; no research needed.
3. **Sweeping other TD into this milestone would inflate scope**: TD-2/3/5 each warrant their own attention (test infrastructure, alias removal, concurrency-test fix). TD-4 is operator-manual. Bundling them with port-conflict would dilute focus and stretch a 1-day-ish fix into a multi-phase milestone with no quality gain.
4. **Captured from operator pain, not roadmap planning**: The trigger was a todo file written 2026-05-12 right after v2.1 archive, not a roadmap discussion. The minimal milestone shape matches the trigger.

**Key decisions locked for v2.2:**

1. **Probe technique**: `TcpListener(addr, port).Start()` + immediate `Stop()` (NOT `IPGlobalProperties.GetActiveTcpListeners()`). Reason: actually attempts the bind we care about; less prone to TOCTOU drift between probe and Kestrel bind.
2. **No fallback**: Loud failure beats silent quality regression — smart-router pairs with Hermes Agent / Graphify on `:4000` by convention; auto-fallback to a random port would silently disconnect them.
3. **stderr direct, not Serilog**: The startup error must be visible even if Serilog hasn't initialized. Matches OBS-04 separation invariant.
4. **No retry**: Single-shot probe. If launchd-restart-race observed in practice, revisit.
5. **Loopback-only probe**: Non-loopback URLs in multi-URL configs are skipped with debug log. Smart-router is loopback-only by constraint.

**Out-of-scope items (carried from REQUIREMENTS.md):**

- Auto-fallback to a different port on conflict
- Continuous port-health monitoring at runtime
- Probe non-loopback interfaces
- Retry the probe N times before failing
- Switch to `IPGlobalProperties.GetActiveTcpListeners()` instead of `TcpListener.Start()`
- Suggest a different port automatically
- Add port-conflict telemetry to `/stats`
- Touch any other deferred TD items (TD-2/3/4/5) — explicit non-goals for this milestone

---

*Roadmap created: 2026-05-12 — minimal v2.2 milestone scoped directly from `.planning/todos/pending/2026-05-12-fail-fast-on-port-conflict-at-startup.md`. Research and parallel-domain investigation skipped (`/gsd:new-milestone` Phase 7 "Skip research" path) because the scope is mechanical, the .NET TCP probe pattern is standard, and the operator pain is well-understood from the todo capture.*
