# Requirements: Smart Router — v2.2 Operator Fail-Fast on Port Conflict

**Defined:** 2026-05-12
**Core Value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.
**Milestone:** v2.2 (minimal — single feature: fail fast at startup when the listen port is already bound)

## v2.2 Requirements

5 requirements in one category. All map to Phase 25.

### Port Probe + Fail-Fast (PROBE-*)

- [ ] **PROBE-01**: `SmartRouter.Cli.Adapters.PortProbe.tryBind : int -> IPAddress -> Result<unit, PortConflictError>` exists. Implementation: `let listener = TcpListener(addr, port)` → `listener.Start()` → `listener.Stop()` → `Ok ()`. Catches `SocketException` (or specifically `SocketError.AddressAlreadyInUse`) and returns `Error { Port = port; Address = addr.ToString(); Reason = ex.Message }`. Lives in `SmartRouter.Cli/Adapters/PortProbe.fs` (ARCH-01 — `System.Net.Sockets` is BCL but adapter placement is the invariant; Core stays untouched).

- [ ] **PROBE-02**: `Program.fs` (or an equivalent startup hook before `app.Run()`) extracts the listen port from configuration. Source priority matches ASP.NET Core defaults: CLI `--urls` argument (if present) → `appsettings.json` `Kestrel:Endpoints:*` or top-level `urls` key → default `http://localhost:4000`. Calls `PortProbe.tryBind` with `IPAddress.Loopback` (127.0.0.1). Multi-URL configs (rare in smart-router; loopback-only convention) probe each loopback port; non-loopback URLs (`0.0.0.0`, public IP) are skipped with a debug-level log line.

- [ ] **PROBE-03**: On `Error err`, write to `Console.Error` (NOT through Serilog — this message must be visible even if Serilog hasn't initialized or has a misconfigured sink) and call `Environment.Exit(1)`. Exact format:
  ```
  ERROR: Port {err.Port} is already in use. Smart Router cannot start.
  Likely culprit: another smart-router instance, or a different process bound to :{err.Port}.
  To investigate: `lsof -iTCP:{err.Port} -sTCP:LISTEN -n -P`
  To stop a stuck launchd instance: `launchctl unload ~/Library/LaunchAgents/com.ohama.smartrouter.plist`
  ```
  No stacktrace. No `try/catch` swallowing the probe error. Probe failure is terminal — Kestrel never gets a chance to throw its own `AddressAlreadyInUse`.

- [ ] **PROBE-04**: Expecto tests in new test module `tests/SmartRouter.Tests/PortProbeTests.fs` (wired into `RouterTests.fs rootTests` per ARCH testing invariant). Cases:
  - (a) `tryBind` returns `Ok ()` for a free high port (e.g., `0` for OS-assigned, then re-probe an actually-free port).
  - (b) `tryBind` returns `Error _` when test pre-stages a `TcpListener` on a chosen high port (port `0` first to get an OS-assigned port, then probe that exact port).
  - (c) The `Error` record's `Port` field matches the probed port.
  - (d) Probe is fast — `tryBind` completes in <100ms in (a) and (b) cases.
  - Tests use random high ports (>10000) to avoid colliding with operator services. Listener disposal via F# `use`.

- [ ] **PROBE-05**: README §13 Troubleshooting gains a new recipe matching the runtime error string (so operators grepping either way find it):
  - Symptom: "smart-router exits immediately with `Port 4000 is already in use`"
  - Diagnosis: same `lsof -iTCP:4000 -sTCP:LISTEN -n -P` command as the error message
  - Resolution: kill the conflicting process, or `launchctl unload ~/Library/LaunchAgents/com.ohama.smartrouter.plist` if it's a stuck launchd instance
  - CLAUDE.md README-sync rule trigger: §13 (Troubleshooting recipes change).

## Future Requirements

Tracked but not in v2.2 roadmap. All deferred from v2.1 milestone audit and prior decisions.

### Test infrastructure carry-over (v1.3)

- **MODELS-01..03 (TD-2)**: `ModelsTests.fs` IEmbedder registration fix — register stub IEmbedder in `configureWithoutMl` DI fixture so MODELS-01..03 stop erroring.
- **TD-3**: Remove `configureServices` backwards-compat alias at `CompositionRoot.fs:1369` after TD-2 lands.
- **TD-5**: Replace `Async.Sleep 30` enqueue barrier in `QueueTests.fs:239-307` PITFALL-10 with `Barrier`/`SemaphoreSlim` (production logic correct; test-side timing race).

### Operator manual gates

- **TD-4**: Run `./scripts/smoke-hermes-session.sh` against live mlx_lm.server rig (operator action, not a code gap).

### Deferred features (no formal REQ-ID yet)

- **HMRS-FUTURE-01/02**: Hermes Agent custom provider PR for X-Session-Id propagation.
- **MODE-FUTURE-01**: Hot-reload `Routing.Mode` without restart (FileSystemWatcher pattern).
- **SPEC-01..03**: Speculative routing (35B drafts while router evaluates).
- **DRT-01**: Dedicated tiny router model (Qwen2.5-3B as separate server).
- **DB-01/02**: SQLite `~/.hermes/state.db` direct read (Approach C from `hermes-session-without-modification.md`).

## Out of Scope

Explicitly excluded for v2.2.

| Feature | Reason |
|---------|--------|
| Auto-fallback to a different port on conflict | Loud failure is the right operational signal for a launchd-managed service. Random-port fallback would hide config drift and silently disconnect Hermes / Graphify which assume `:4000`. |
| Continuous port-health monitoring at runtime | `/health` endpoint already covers liveness; the probe is a startup-only check. Continuous probing would add log noise. |
| Probe non-loopback interfaces (`0.0.0.0`, public IPs) | Smart-router is loopback-only by constraint (PROJECT.md). External interface probing is moot. |
| Retry the probe N times before failing | Race conditions between launchd restart and a previous process dying are rare on a single-host setup; one shot is enough. If observed in practice, revisit. |
| Use `IPGlobalProperties.GetActiveTcpListeners()` instead of `TcpListener.Start()` | Both work; `TcpListener.Start()` is more direct (actually attempts the bind we care about) and less prone to TOCTOU drift between probe and Kestrel bind. |
| Suggest a different port automatically | Smart-router pairs with Hermes Agent and the future Graphify on `:4000` by convention. Suggesting alternatives would invite operator confusion. |
| Add port-conflict telemetry to `/stats` | If the process exits at startup, `/stats` is never reachable. The stderr message + non-zero exit is the operator signal. |
| Touch any other deferred TD items (TD-2/3/4/5) | v2.2 is intentionally narrow — single feature, single phase, ~3 tasks. Future minor milestones can sweep tech debt. |

## Traceability

Assigned by roadmapper 2026-05-12.

| Requirement | Phase | Status |
|-------------|-------|--------|
| PROBE-01 | Phase 25 | Pending |
| PROBE-02 | Phase 25 | Pending |
| PROBE-03 | Phase 25 | Pending |
| PROBE-04 | Phase 25 | Pending |
| PROBE-05 | Phase 25 | Pending |

**Coverage:**
- v2.2 requirements: 5 total (5 PROBE)
- Mapped to phases: 5/5 (Phase 25)
- Unmapped: 0 ✓
- Future: 5+ deferred items tracked above

---
*Requirements defined: 2026-05-12.*
*Source: `.planning/todos/pending/2026-05-12-fail-fast-on-port-conflict-at-startup.md` (captured 2026-05-12 during v2.1 archive; promoted to v2.2 minimal milestone).*
