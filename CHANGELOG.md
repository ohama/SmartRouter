# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [2.2.1] - 2026-05-13

Hotfix release closing issue [#14](https://github.com/ohama/SmartRouter/issues/14):
named `HttpClient` registrations using the `services.AddHttpClient(name, fun c -> ...)`
2-arg form were silently dropping `BaseAddress` and `Timeout` assignments due
to F# overload resolution not reliably converting the lambda to
`Action<HttpClient>`. The forbidden pattern (documented at `CompositionRoot.fs:214`
and in `documentation/howto/wire-fsharp-namedhttpclient-with-configurehttpclient.md`)
was present in 4 call sites spanning the entire v2.0+ lifetime.

**Impact:** The default `Routing.Mode="selfrouting"` Stage 4 self-classify path
was broken in every shipped v2.0.0 → v2.2.0 release. Every request that reached
Stage 4 silently fell through to Default routing because `selfrouter`
`BaseAddress` was null and every call returned `InvalidOperationException:
An invalid request URI was provided`. The cascade degraded gracefully so
production observability (`/stats`, `DecisionLog`) did not surface the issue;
it was found by a downstream consumer building an E2E test harness.

The `judge` (Phase 16, opt-in via `Routing.Judge.Enabled=true`) and `teacher`
(ML retraining accumulation, `--retrain` CLI mode) paths were also broken in
the same way — every request was silently failing instead of reaching the
configured endpoint.

### Fixed

- **`selfrouter` HttpClient `BaseAddress` (issue #14, `CompositionRoot.fs:485`).**
  The 2-arg `services.AddHttpClient("selfrouter", fun c -> ...)` registration
  is replaced with the `.ConfigureHttpClient(...)` chain form. Stage 4
  self-classify calls now actually reach `Upstreams.Model35B` (default
  `http://127.0.0.1:8000`) instead of failing with relative-URI errors.
- **`judge` HttpClient `BaseAddress` (`CompositionRoot.fs:763`).** Same
  pattern, same fix. Operators with `Routing.Judge.Enabled=true` will now
  see judge calls succeed against `Upstreams.Model122B` (default
  `http://127.0.0.1:8001`).
- **`teacher` HttpClient `BaseAddress` (`CompositionRoot.fs:829`,
  `configureRequestPipeline` branch).** ML retraining hard-case accumulation
  now reaches the teacher endpoint configured in `TeacherLabeler:Endpoint`.
- **`teacher` HttpClient `BaseAddress` (`CompositionRoot.fs:1278`,
  `configureWithoutMl` branch).** The `--retrain` CLI mode now reaches the
  teacher endpoint instead of failing.

### Added

- **`tests/SmartRouter.Tests/NamedHttpClientBaseAddressTests.fs`** — 8-testCase
  regression suite that builds a real DI provider for both
  `configureRequestPipeline` and `configureWithoutMl`, resolves
  `IHttpClientFactory`, creates each named client, and asserts `BaseAddress`
  is non-null and matches the configured endpoint. Covers canonical
  good cases (`upstream35b`, `upstream122b`, streaming variants) plus all
  4 previously-broken sites. The existing howto doc + the FORBIDDEN warning
  comment at `CompositionRoot.fs:214` were documentation safeguards;
  this test is the missing CI gate that would have caught the original
  drift. Test count: 191 → 199 passing.

### Notes

- **Operator-visible behavior is now what the README has always claimed.**
  Phase 19+ documentation describes `selfrouter` Stage 4 classify as a live
  routing stage; the code now matches that description. No README update is
  required because the README was already accurate — the bug was silent
  divergence between code and docs.
- **No new dependencies, no schema changes.** `DecisionLog schema_version=1`
  unchanged; `/stats` flat snake_case fields unchanged; ARCH-01 (Core
  BCL-only) and ARCH-02 (`task {}` only) preserved.
- **TD-5 (`PITFALL-10` timing race in `QueueTests.fs`) still intermittent.**
  Pre-existing flake, unrelated to v2.2.1. Production logic in
  `QueueDispatcher` is correct (Fairness counter assertions always pass; only
  the FIFO completion-order assertion for `high4` is flaky). Tracked for a
  future minor milestone.
- **Recommendation for operators upgrading from v2.0.0–v2.2.0:** If you have
  been running `Routing.Mode="selfrouting"` (the default since v2.0), the
  Stage 4 self-classify path has effectively been a no-op. Routing decisions
  prior to this release were:
  1. Hard Rules (Stage 0) — worked correctly
  2. Explicit model override (Stage 1) — worked correctly
  3. Explicit task table (Stage 2) — worked correctly
  4. Sticky session (Stage 3) — worked correctly
  5. ~~35B self-classify (Stage 4)~~ — silently broken, fell through
  6. Default 35B (Stage 5) — worked correctly (caught the fall-through)

  After upgrading, you should see Stage 4 selfrouter actually engage. Watch
  the new `selfrouter_*` `/stats` counters to confirm the cache is being
  populated. If you were previously OK with Default-35B routing, no
  observable change. If you wanted Stage 4 to upgrade ambiguous prompts to
  122B based on the SAFE/UNSAFE classify, that will now happen.

## [2.2.0] - 2026-05-13

v2.2 milestone — Operator Fail-Fast on Port Conflict. Single-phase minimal
milestone scoped from operator pain captured during v2.1 archive: smart-router
previously failed ungracefully with a raw `SocketException` /
`AddressAlreadyInUse` stacktrace when `:4000` was already bound at startup,
trapping operators in launchd KeepAlive retry loops and risking silent
misrouting when a different process answered Hermes Agent on `:4000`. v2.2
adds a deterministic startup probe that intercepts the conflict before Kestrel
binds and emits a 4-line actionable error to stderr.

### Added

- **`SmartRouter.Cli.Adapters.PortProbe.tryBind : int -> IPAddress -> Result<unit, PortConflictError>`**.
  Synchronous BCL helper (`new TcpListener(addr, port)` → `Start()` →
  immediate `Stop()` → `Ok ()`) that returns `Error { Port; Address; Reason }`
  when the OS rejects the bind. Catches all `SocketException` cases (not just
  `AddressAlreadyInUse`) so any bind-rejection — permission denied, address
  not available — produces a clean operator-facing message instead of a
  stacktrace. Lives in `src/SmartRouter.Cli/Adapters/PortProbe.fs` (ARCH-01:
  `System.Net.Sockets` is BCL but the adapter placement is the invariant —
  `SmartRouter.Core` untouched).
- **Startup port-conflict detection** in `src/SmartRouter.Cli/Program.fs`
  (line 287, between `WebApplication.CreateBuilder` and `builder.Build()`).
  Resolves the listen port from the merged `Kestrel:Endpoints:Http:Url`
  configuration so the existing `--port` CLI override automatically composes,
  gates on `IPAddress.Loopback` only (non-loopback URLs are skipped with
  `Log.Debug`; smart-router is loopback-only by constraint), and runs the
  probe BEFORE Kestrel attempts its own bind. The probe is wired into the
  main Kestrel branch only — `--retrain` and other no-port-bind branches are
  unaffected.
- **4-line operator-facing stderr error block** on conflict, via `eprintfn`
  (OBS-04 stream separation — `Console.Error` not Serilog, so the message
  appears even if Serilog has not initialized or has a misconfigured sink),
  followed by `Environment.Exit(1)`. The block names the actual conflicting
  port, suggests `lsof -iTCP:{port} -sTCP:LISTEN -n -P` for investigation,
  and `launchctl unload ~/Library/LaunchAgents/com.ohama.smartrouter.plist`
  for stuck launchd instances. Exits within ~500ms — well before Kestrel
  would otherwise throw `AddressAlreadyInUse` with a 30-line stacktrace.
- **`tests/SmartRouter.Tests/PortProbeTests.fs`** — 4 Expecto testCases
  covering (a) `tryBind` `Ok` on free port; (b) `tryBind` `Error` when a
  test pre-stages a `TcpListener` on the same port; (c) the `Error` record's
  `Port` field matches the probed port; (d) probe completes in <100ms.
  Wired into both `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` `<Compile>`
  entries and `tests/SmartRouter.Tests/RouterTests.fs` `rootTests` list per
  ARCH testing invariant (explicit registration; never auto-discovery).
- **README §13 Troubleshooting recipe** — new "Port 4000 is already in use"
  entry as the first sub-section of §13. Operators grepping either the
  runtime error string or the symptom phrase both find the recipe. Diagnosis
  + resolution commands match the runtime stderr block verbatim.

### Changed

- **Test baseline:** 187 passed → 191 passed (+4 PROBE-04 testCases).
  Total: 191 passed + 18 ignored + 0 failed.
- **Architecture invariants line in STATE.md** updated from "preserved across
  all 24 phases" to "preserved across all 25 phases". ARCH-01 (Core BCL-only),
  ARCH-02 (`task {}` only), DecisionLog `schema_version=1`, per-task atomic
  commits, and Expecto explicit `rootTests` all intact across the v2.2 work.

### Notes

- **No behavior change on healthy startup.** When `:4000` is free, the probe
  is invisible: it binds and releases in <10ms before control passes to
  Kestrel, which then binds the same port as before. Existing operator
  workflows, `dotnet run`, launchd plist, and `~/llm-system/services/`
  deployment patterns are unchanged.
- **One auto-fix during execution:** F# compiler required
  `new TcpListener(addr, port)` (FS0760 warning-as-error for IDisposable
  construction without `new`). Fixed pre-commit; no behavior change.
- **TD-5 (`PITFALL-10` timing race in `QueueTests.fs`) did not manifest**
  during Phase 25 verifier or final post-completion test runs. The pre-existing
  flake remains tracked but was quiet this session. Production logic in
  `QueueDispatcher` is unchanged; v2.2 did not touch concurrency code.
- **Carry-over deferred items** (unchanged from v2.1): TD-2 (`ModelsTests.fs`
  IEmbedder), TD-3 (`configureServices` alias removal, blocked on TD-2),
  TD-4 (operator live-rig smoke run). HMRS-FUTURE-01, MODE-FUTURE-01,
  SPEC-01..03, DRT-01, DB-01/02 also remain deferred.

## [2.1.1] - 2026-05-12

Phase 23 documentation pass + Phase 24 TIER-04 gap closure. Closes the v2.1
milestone (4/4 phases shipped; `milestone-v2.1` tag). No production code
behavior changes — README operator-facing accuracy + executable assertion for
the previously structural-only ml-mode DI guarantee.

### Added

- **`tests/SmartRouter.Tests/SessionKeyCascadeTests.fs` TC-7** —
  `"TC-7: ISessionCascadeStats resolves non-null in Routing.Mode=\"ml\" DI provider"`.
  Constructs a `ServiceCollection` from `minimalConfigPairs` with `Routing:Mode`
  overridden to `"ml"` (and no `Routing:ML` section so `mlOpts=null` at
  `CompositionRoot.fs:342` skips the ML bootstrap and avoids the ONNX file
  dependency in CI), calls `configureRequestPipeline`, builds a provider, and
  asserts `provider.GetRequiredService<ISessionCascadeStats>()` returns non-null
  via `Expect.isNotNull (box stats)` (`box` is required because F# interfaces are
  non-nullable; matches `MLRoutingTests.fs:146` precedent). Closes TD-1 from
  `.planning/milestones/v2.1-MILESTONE-AUDIT.md` — upgrades TIER-04 evidence from
  structural inference to executable assertion. Test count: 186 → 187 passed.

### Changed

- **README §10** (Hermes / Graphify Integration) rewritten end-to-end for the
  v2.1 three-tier paradigm. Operator guide now covers all four
  `--pass-session-id` enablement options (CLI arg, shell alias, env var,
  wrapper script) plus content-fingerprint fallback semantics. Stock-Hermes
  behavior distinction noted: stock `--pass-session-id` fires Tier 2, not
  Tier 1. (DOC-01)
- **README §8** (`/stats` field reference) — three new rows documented:
  `session_extraction_source_header`, `session_extraction_source_sysprompt`,
  `session_extraction_source_content`. Added to both the JSON example and the
  description table, plus a jq monitoring snippet for tracking tier distribution
  over time. (DOC-03)
- **README §9.1** (DecisionLog schema reference) — schema confirmed unchanged
  in v2.1 (session_id propagation uses the existing SES-04 channel; no new
  `routing_reason` values). Cosmetic "Phase 17–19" → "Phase 17–22" phase-range
  bump applied for accuracy. (DOC-04)
- **README drift sweep** — confirmed zero residual references to
  `FingerprintEnabled`, `PROXY-01`, `RemoteIp`, `HMRS-FUTURE-01`, and "network
  fingerprint" across the entire README. The §7 `FingerprintEnabled` row
  removal (DOC-02) was committed early in Plan 22-03 (`938c8ac`); §10 callout
  removal completed in `157c49f`.
- **Test baseline** updated 186 → 187 passing (+1 from TC-7); 18 ignored
  unchanged; 0 failed.

### Notes

- **No production code changes.** All Phase 23 work is documentation; Phase 24
  adds one test case. Operator behavior is identical to v2.1.0.
- **Pre-existing test flake surfaced (TD-5).** During Phase 24 verification
  the `PITFALL-10` test in `tests/SmartRouter.Tests/QueueTests.fs` (lines
  239-307) was observed failing ~60% of the time when run in isolation. Root
  cause analysis (audit integration checker) confirmed this is a test-side
  timing assumption flaw — the `Async.Sleep 30` enqueue barrier races against
  the `LatencyFake(30)` occupy slot release. Production logic in
  `QueueDispatcher` is correct (Fairness counter assertions always pass; only
  the FIFO completion order assertion for `high4` is flaky). The flake
  predates Phase 21 (last touched in `fac58b2`); not introduced or worsened
  by v2.1. Tracked in `.planning/STATE.md` as TD-5 for future fix (replace
  sleep barrier with `Barrier` or `SemaphoreSlim` guaranteeing all 5 tasks
  called `EnqueueAsync` before occupy releases).
- **Deferred carry-over tech debt** (unchanged from v2.1.0): TD-2
  (`ModelsTests.fs` IEmbedder errors), TD-3 (`configureServices` alias
  removal, blocked on TD-2), TD-4 (operator live-rig smoke run).

## [2.1.0] - 2026-05-12

Phase 22 — Hermes-less Session Tiering. Replaces the v2.0 IP+UA network
fingerprint with a multi-tier session extraction: explicit `X-Session-Id`
header -> Hermes system-prompt parse -> content fingerprint.

### Removed

- **`Routing.Session.FingerprintEnabled` config key** (`appsettings.json`).
  Opt-in IP+UA fingerprint introduced in Phase 20 (v2.0) is superseded by the
  system-prompt and content-fingerprint tiers. **Breaking change** for any
  operator who had `FingerprintEnabled=true` — the key is silently ignored
  at startup in v2.1 (CLIMutable binding tolerates extra JSON keys). Update
  `appsettings.json` by removing the key for hygiene; the system continues
  to boot if you don't. If `--pass-session-id` is not available on your
  Hermes version, content fingerprint (Tier 3) provides equivalent sticky-
  bucket continuity without IP address coupling.
- **IP+UA network fingerprint** (`SHA-256(RemoteIpAddress + "|" + User-Agent)`
  block in `CorrelationMiddleware`). Deleted with `FingerprintEnabled` — no
  equivalent behavior in v2.1 (superseded by Tier 2/3).
- **`PROXY-01` reverse-proxy warning** (README §10). Moot — network fingerprint
  code that required `X-Forwarded-For` parsing is gone. The README §10
  PROXY-01 callout removal is part of Phase 23 (DOC-01).
- **`HermesFingerprintTests.fs`** (FP-01..FP-08, 8 tests, 130 lines). Replaced
  by `HermesSessionExtractTests.fs` (Plan 21-01) + `ContentFingerprintTests.fs`
  (Plan 21-02) + `SessionKeyCascadeTests.fs` (Plan 22-03).

### Added

- **Hermes system-prompt session parse (Tier 2).**
  `SmartRouter.Cli.Adapters.HermesSessionExtract.extractFromSystemPrompt` reads
  a `Session ID: <id>` line from the first `System` message when Hermes is
  started with `--pass-session-id`. Pre-compiled
  `Regex("^Session ID:[ \t]*(\S+)", Multiline)` — no per-request allocation.
  Falls through when line absent (graceful for operators who have not enabled
  `--pass-session-id`).
- **Content fingerprint session key (Tier 3).**
  `SmartRouter.Cli.Adapters.ContentFingerprint.compute` derives a 16-character
  lowercase hex session key from `SHA-256(system_msg + "|||" + first_user_msg)
  [0..15]`. Deterministic and collision-resistant — the same conversation start
  always maps to the same session bucket. Inputs truncated to 4000 characters
  before hashing.
- **Three-tier cascade in `ChatCompletions.fs` request handler.** Session key
  resolution priority: `X-Session-Id` header (Tier 1, populated by
  `CorrelationMiddleware`) -> Tier 2 system-prompt parse -> Tier 3 content
  fingerprint. First non-empty wins; `ctx.Items[SessionIdKey]` and
  `req.SessionId` carry the resolved key into the routing algorithm and the
  Phase 18 sticky `SessionStore.Update` writes. Sticky escalation
  (`routing_reason="sticky_to_122b"`) works across all three tier paths.
- **`session_extraction_source_header`, `session_extraction_source_sysprompt`,
  `session_extraction_source_content`** — three new `int64` fields on
  `GET /stats`. Each `Interlocked.Increment`s once per request based on which
  tier resolved the session key. Owned by the new
  `SmartRouter.Cli.Adapters.SessionCascadeStats` singleton (registered in both
  `configureRequestPipeline` AND `configureWithoutMl`). Null-safe resolution
  in `Stats.fs` mirrors the Phase 19 `selfRouterStats` pattern.
- **`archive/v2.0-network-fingerprint` git branch + `v2.0-network-fingerprint`
  annotated git tag.** Preserves the pre-v2.1 commit (last commit before
  Plan 22-02 deletions) for archaeological reference. Mirrors the v1.x
  `archive/heuristic-baseline` + `v0.5-heuristic-baseline` dual-preservation
  pattern.

### Changed

- **`CorrelationMiddleware.correlationMiddleware` signature.**
  `fingerprintEnabled: bool` first parameter **removed**. Callers
  (`Program.fs:280` and `LoggingTests.fs:318`) drop the argument.
  `Program.fs` startup-time `app.Configuration.["Routing:Session:FingerprintEnabled"]`
  read also deleted.
- **`SessionOptions` record.** `FingerprintEnabled : bool` field removed; record
  now has only `TtlMinutes` and `MaxEntries`. CLIMutable binding tolerates
  operators' legacy JSON keys.
- **Session key resolution moved from header-only (Tier 1) to 3-tier cascade.**
  Requests without `X-Session-Id` header now always receive a non-empty session
  key (Tier 2 or Tier 3), enabling sticky escalation for Hermes operators who
  have not yet enabled `--pass-session-id`. Per-request cost: ~1-2 us for
  Tier 3's SHA-256 of <=8KB of conversation prefix (acceptable; Phase 21
  performance review).

### Notes

- DecisionLog `schema_version=1` unchanged. Session key resolution happens
  upstream of the routing decision. No new `routing_reason` values. The
  resolved key flows through the existing `SES-04` channel into the existing
  `session_id` DecisionLog field (which is not currently emitted — tracked as
  future schema-version work).
- Both `Routing.Mode = "selfrouting"` and `"ml"` use the new 3-tier cascade —
  enforced by unconditional `ISessionCascadeStats` DI registration in both
  `configureRequestPipeline` AND `configureWithoutMl` (TIER-04 invariant).
- Operators with `Routing.Session.FingerprintEnabled=true` in their legacy
  `appsettings.json` will see the key silently ignored at startup (CLIMutable
  binding does not throw on unknown JSON keys). No graceful-ignore handler
  required; ASP.NET Core configuration binding tolerance is the established
  behavior.
- README §7 row for `Routing.Session.FingerprintEnabled` is removed in this
  release. README §8 (new `/stats` counter rows) and §10 (Hermes Integration
  rewrite for v2.1 `--pass-session-id` operator guide; PROXY-01 callout
  removal) are documented in Phase 23 (DOC-01..04).
- Test count: 188 (v2.0 + Phase 21) -> 180 (Phase 22 Plan 22-02 deletes 8 FP
  tests) -> 186 (Phase 22 Plan 22-03 adds 6 `SessionKeyCascadeTests`). Final:
  186 passed + 18 ignored + 0 failed.

---

## [2.0.0] - 2026-05-12

Phase 17 — Hard Rules layer + Routing.Mode switch. v2.0 paradigm pivot:
the routing primary path becomes keyword Hard Rules → (Phase 18 sticky) →
(Phase 19 35B self-classify); the v1.x ML classifier remains compiled and
re-activatable with a one-line `appsettings.json` edit + restart.

### Added

- **Stage 0 Hard Rules pre-routing.** A pure keyword scan (`LLVM`, `MLIR`, `compiler`, `segfault`, `optimization`, `concurrency` — case-insensitive) runs **before** model override, task table, and the routing algorithm. Any keyword match routes immediately to Qwen 122B with `routing_reason="hard_rule"` and `priority=High`. Applies to both streaming and non-streaming branches. The keyword list is hardcoded in `src/SmartRouter.Core/HardRules.fs` — not operator-configurable, by design (safety mechanism). DecisionLog `routing_reason` gains the additive value `"hard_rule"`; schema_version=1 unchanged.
- **`Routing.Mode` config key (`appsettings.json`).** New string key with values `"selfrouting"` (v2.0 default) or `"ml"` (v1.x rollback). Invalid values fail startup with `InvalidOperationException` before Kestrel binds. Operator can flip modes via config edit + `launchctl kickstart -k gui/$(id -u)/com.ohama.smart-router` — no rebuild required.
- **`routing_algorithm="selfrouting"` value in DecisionLog.** New value alongside `"ml"` / `"ml-canary"`; emitted when `Routing.Mode="selfrouting"` is active. `model_version="selfrouting-v1"` for the Phase 17 stub; Phase 19 will adopt a prompt-hash-derived version.

### Changed

- **Default routing paradigm flipped from ML to selfrouting.** With `Routing.Mode="selfrouting"` (the new default), Phase 17 ships a STUB algorithm that returns Qwen 35B / `routing_reason="default"` for prompts that miss Hard Rules + model override + task table. Phase 19 replaces the stub with the real 35B SAFE/UNSAFE self-classify call. Operators who want v1.x ML routing behavior in the interim should set `Routing.Mode="ml"` in `appsettings.json`. ML adapters (`BgeM3Embedder`, `MlNetClassifier`, `RetrainingService`, `CanaryService`) remain DI-registered and running in **both** modes — `RetrainingService` continues accumulating hard cases so ML can be re-activated without retraining from scratch.
- **Routing pipeline grew from 3 stages to 4.** `Routing.routeRequest` now invokes Stage 0 Hard Rules before the existing model override + task table + algorithm stages. The cascade order is `Hard Rules → model override → task table → algorithm` (Phase 18 inserts sticky escalation in subsequent work).

### Notes

- DecisionLog `schema_version` remains **1**. All Phase 17 additions are additive enum values (`hard_rule`, `selfrouting`) on existing string fields — no field removals, no type changes.
- `configureServices` backwards-compat alias is preserved and inherits the new `Routing.Mode` behavior unchanged.
- Phase 14 quality fallback (35B → 122B retry on quality-bad responses), Phase 15 quality signal enrichment, and Phase 16 borderline judge (`Routing.Judge.Enabled`) all work unchanged in both `selfrouting` and `ml` modes.

---

### Added (Phase 18 — Session Store + Sticky Escalation)

- **`X-Session-Id` HTTP request header opt-in for session-aware routing.** Requests sharing
  a session ID get debugging continuity: once any request in the session routes to Qwen 122B
  (via initial routing, Hard Rule, OR quality-fallback escalation), subsequent requests in
  the same session route to 122B with `routing_reason="sticky_to_122b"`. Empty or absent
  header preserves v1.x stateless behavior — no sticky bucket is created.
- **`RouterRequest.SessionId : string` Core domain field.** 10th field; `""` sentinel means
  stateless (Phase 9 `CorrelationId` cascade pattern). Set from `X-Session-Id` header in
  `CorrelationMiddleware`; null/whitespace coalesces to empty string (Pitfall 7: never let
  stateless clients share one sticky bucket).
- **`SmartRouter.Core.Domain.SessionState`** BCL-only record — `LastModel`, `LastAccessedAt`,
  mutable `LastAccessSeq`. Stored in Cli adapter; Core domain-only so Phase 19 self-routing
  closure can pattern-match on `LastModel` without dragging Cli deps into Core (ARCH-01).
- **`SmartRouter.Cli.Adapters.SessionStore`** adapter — `ConcurrentDictionary`-backed store
  with `AddOrUpdate` 122B-wins concurrent merge (non-negotiable correctness invariant: a
  racing 35B write can never overwrite a 122B escalation), LRU cap on write, and TTL-aware
  `TryGet`.
- **`SessionTtlEvictionService` BackgroundService** (PeriodicTimer 5-minute sweep) removing
  entries older than `Routing.Session.TtlMinutes`. Triple-reg pattern (concrete singleton +
  `ISessionStore` alias + `AddHostedService`) mirrors `DecisionLogWriter`.
- **`Routing.Session.TtlMinutes`** and **`Routing.Session.MaxEntries`** `appsettings.json`
  keys (defaults 30 minutes, 10000 entries). CLIMutable binding; defensive defaults applied
  at consumption (`<= 0` → 30/10000).
- **`RoutingReason.StickyEscalation`** 8th DU case → DecisionLog `routing_reason=
  "sticky_to_122b"` (`schema_version=1` unchanged — additive enum value).
- **`CorrelationMiddleware` extension** reading `X-Session-Id` into
  `HttpContext.Items[SessionIdKey]`; null/whitespace coalesces to empty string sentinel.
- **Point B write** in `ChatCompletions` — `finalDecision.Target` written to session store
  after the full cascade (quality fallback + judge) resolves, so a 35B→122B escalation
  is correctly recorded and the next request in the session stickies to 122B (SES-07).
- **`SessionStoreTests.fs`** (unit) and **`StickyEscalationTests.fs`** (DI-integration)
  cover all 5 Phase 18 ROADMAP Success Criteria.

### Notes (Phase 18)

- DecisionLog `schema_version` stays at `1`. `sticky_to_122b` is an additive enum value
  on the existing `routing_reason` string field — no schema migration required.
- Session store is in-memory only. Restart clears all sessions. See §5.6.
- `X-Session-Id` header propagation from Hermes Agent is future work (HMRS-FUTURE-01;
  Phase 20 ships smart-router-side machinery + an opt-in fingerprint fallback).

---

### Added (Phase 19 — 35B Self-Routing, Stage 4 self-classify)

- **v2.0 self-routing paradigm (Stage 4 self-classify).** For non-streaming requests that
  reach the routing default stage without being decided by Hard Rules, an explicit override,
  or sticky session, the router now calls the 35B model itself via a dedicated
  `"selfrouter"` named HttpClient (5s timeout, 1 retry at 200ms, `max_tokens=8`,
  `temperature=0`) to classify the prompt as SAFE (route to 35B) or UNSAFE (escalate to
  122B). Streaming requests skip Stage 4 entirely — Hard Rules (Stage 0) + sticky
  escalation (Stage 3) still apply to streaming. Operators can rollback to v1.x ML routing
  by setting `Routing.Mode="ml"` in `appsettings.json` + restart (no rebuild required).
- **Operator-tunable classify prompt.** `prompts/self-router-prompt.md` shipped with the
  repo. Operator edits `{{PROMPT}}`-based template to refine SAFE/UNSAFE criteria; changes
  take effect on next restart. See README §5.7 and §7.
- **Prompt-hash LRU cache.** Identical prompts hit a 10,000-entry per-process cache
  (keyed by SHA-256 of the full conversation content); cache hits skip the HTTP round-trip.
  Bounded by `Routing.SelfRouter.MaxCacheEntries` (default 10000). Restart clears the cache.
- **DecisionLog enum values:** `routing_reason="self_route"` (Stage 4 verdict);
  `routing_algorithm="selfrouting"` (v2.0 cascade). `model_version` is set to
  `"selfrouting-{hex8}"` for self-routed decisions, where `{hex8}` is the first 8 hex chars
  of SHA-256 of `prompts/self-router-prompt.md` at startup — operators can detect
  prompt-template drift between restarts by watching this field in the DecisionLog.
  **`schema_version=1` unchanged** — all Phase 19 additions are additive enum values only;
  no field removals, no type changes.
- **`/stats` endpoint fields:** `selfrouter_cache_hits`, `selfrouter_cache_misses`,
  `selfrouter_call_count`, `selfrouter_skipped` (all `int64`, process-lifetime). Returns 0
  for all four when `Routing.Mode="ml"`. See README §8.
- **Configuration keys.** `Routing.SelfRouter.Endpoint` (default `""` → derives from
  `Upstreams.Model35B`), `Routing.SelfRouter.PromptPath` (default
  `"prompts/self-router-prompt.md"`), `Routing.SelfRouter.TimeoutSeconds` (default `5`),
  `Routing.SelfRouter.MaxCacheEntries` (default `10000`). All in `appsettings.json`; restart
  required after changes. See README §7.
- **ML dormant integration test.** `tests/SmartRouter.Tests/MlDormantTests.fs` boots
  `Routing.Mode="ml"` and asserts `RoutingAlgorithmRegistration.Name="ml"` — prevents silent
  v1.x ML-path regression across v2.x phases. Skip-guarded on hosts without ONNX embedding
  files (W4 pattern; confirmed by `File.Exists` guard).

### Notes (Phase 19)

- DecisionLog `schema_version` stays at `1`. All Phase 19 additions (`self_route` routing
  reason, `selfrouting` routing algorithm) are additive enum values on existing string fields.
  Readers that ignore unknown `routing_reason` / `routing_algorithm` values remain
  forward-compatible.
- Stage 4 self-classify uses the same 35B model as inference. The `"selfrouter"` named
  HttpClient has its own connection pool + timeout — it does not compete with the 122B
  `SemaphoreSlim(1)` gate or the 300s inference timeout.
- Fail-open: any classify failure (HTTP timeout, template missing, ambiguous response) falls
  through to Stage 5 default (35B). No request is dropped; `selfrouter_skipped` or
  `selfrouter_call_count` movements in `/stats` signal failures.

---

### Added (Phase 20 — Hermes Integration + Documentation)

- **Fingerprint fallback session key (opt-in).** When `Routing.Session.FingerprintEnabled=true`
  (default `false`) and no `X-Session-Id` header is present, `CorrelationMiddleware` derives a
  session key from `SHA-256(RemoteIpAddress + "|" + User-Agent)` truncated to 16 lowercase hex
  characters. Enables sticky escalation continuity for the loopback single-client development
  scenario (Hermes Agent on the same host before it ships X-Session-Id propagation). See
  README §10. **Not safe behind reverse proxies** — `X-Forwarded-For` is not parsed; tracked
  as PROXY-01 for v2.x work.
- **`Routing.Session.FingerprintEnabled` config key** (`appsettings.json`). Boolean, default
  `false`. Opt-in only — preserves v1.x stateless behavior unless explicitly enabled. See
  README §7.
- **`scripts/smoke-hermes-session.sh`.** Operator-runnable smoke test verifying X-Session-Id
  session propagation end-to-end with curl + DecisionLog grep assertion. No Hermes Agent
  dependency — curl drives both requests directly.
- **`HermesFingerprintTests.fs`** integration tests covering fingerprint-enabled and
  fingerprint-disabled paths (8 test cases FP-1..FP-8 using `DefaultHttpContext`).

### Changed (Phase 20)

- **README §10 "Hermes Integration" fully rewritten for v2.0.** Replaces the v1.x ML routing
  description (stage 3 ML classifier) with the v2.0 selfrouting paradigm (keyword Hard Rules +
  35B self-classify + sticky session). Documents X-Session-Id opt-in, fingerprint fallback
  caveats, and Hermes-side propagation as future v2.x work (HMRS-FUTURE-01).

### Notes (Phase 20)

- `schema_version=1` unchanged. No new DecisionLog fields — fingerprint-derived session IDs
  participate in the existing `routing_reason="sticky_to_122b"` flow.
- Hermes Agent code is NOT modified in v2.0. Smart-router ships the session-aware
  infrastructure; Hermes propagating `X-Session-Id` is tracked as HMRS-FUTURE-01 (post-v2.0).
- REQUIREMENTS.md HMRS-FUTURE-01, HMRS-FUTURE-02, PROXY-01 retained as v2.x trackers
  (no change to those entries).
- Test baseline: 175 passed / 18 ignored / 0 failed (was 167 / 18 / 0 in Phase 19).

## [1.3.0] - 2026-05-11

122B-as-judge release. Borderline 35B responses (entropy/length band
edge) can now be verified by a 1-token call to 122B before falling
through to a full retry. Disabled by default — operators opt in after
inspecting Phase 1.2.0's `quality_check_hits_*` counters to see whether
the heuristic is firing too often or not often enough.

### Added

- **122B-as-Judge for Borderline Cases (OPT-IN).** When `Routing.Judge.Enabled = true`, 35B responses that pass the heuristic but fall in the entropy/length band edge get a 1-token verification call to 122B (`ROUTE_YES`/`ROUTE_NO`). Cached by `(prompt_hash, response_hash)` LRU (default 10000 entries). Streaming responses bypass the judge entirely. **Default OFF** — judge adds a 122B network call on every borderline case, so opt in after evaluating borderline rate via the existing `quality_check_hits_*` /stats counters.
- TraceLog fields `judge_called` / `judge_verdict` / `judge_latency_ms` (schema_version=1 unchanged — additive).
- `/stats` fields `judge_cache_hits` / `judge_cache_misses` / `judge_call_count` (all int64; process-lifetime; resolved null-safe so /stats keeps working when judge is disabled).
- Config block `Routing.Judge.*` with 5 keys: `Enabled` (bool, default `false`), `Endpoint` (string, default `""` — derives from `Upstreams.Model122B`), `PromptPath` (default `prompts/judge-prompt.md`), `TimeoutSeconds`, `MaxCacheEntries`.
- Operator-tunable judge prompt at `prompts/judge-prompt.md` with `{{QUESTION}}` and `{{RESPONSE}}` placeholders + `ROUTE_YES`/`ROUTE_NO` sentinels.

## [1.2.0] - 2026-05-10

Quality signal enrichment release. The fallback heuristic now reads
five signals instead of two — most notably `finish_reason="length"`
(catches truncated responses) and Shannon entropy (catches token loops).
Existing config still works; new behaviors activate silently with
sensible defaults.

### Changed

- **Quality fallback now triggers on 5 dimensions instead of 2 (silent enable).** Existing `Routing.QualityFallback` config (`Enabled`, `MinResponseLength`, `BadKeywords`) is unchanged. Two new config keys with defaults activate automatically:
  - `finish_reason="length"` or `"content_filter"` now triggers fallback (new Stage 1). Operators on mlx_lm will see more 35B→122B retries when 35B hits its token limit.
  - Shannon entropy detection (default threshold 2.5) catches token-loop responses like `"the the the..."` (new Stage 3).
  - Korean-aware effective length: Hangul-syllable content is inflated by `koreanRatio × 0.8` before comparing to `MinResponseLength` — Korean responses are less likely to false-positive as "too short" (Stage 2 refinement).
  - `BadKeywords` matching is now **case-insensitive** (was case-sensitive in Phase 14). The keyword `"TODO"` now matches `"todo"`, `"TODO"`, `"Todo"`, etc.
  - Detection cascade is cheap-first (finish_reason → length → entropy → keyword) with early exit at first match.
- Operators wanting Phase 14's narrower trigger behavior can restore it by setting:
  ```jsonc
  "Routing": { "QualityFallback": { "BadFinishReasons": [], "EntropyThreshold": 0.01 } }
  ```
  (`BadFinishReasons: []` disables finish_reason checks; `EntropyThreshold: 0.01` requires near-zero entropy to fire — effectively disabled.)

### Added

- TraceLog field `bad_reason` (string | null) — records which quality check fired and why. Format: `"tag=value"` (e.g. `"finish_reason=length"`, `"length=12"`, `"entropy=1.85"`, `"keyword=TODO"`). `null` when response judged good or fallback was availability-driven. Operator jq: `jq -r 'select(.bad_reason != null) | .bad_reason | split("=")[0]'` to aggregate by detection tag.
- `/stats` endpoint exposes 4 new process-lifetime counters (all `int64`): `quality_check_hits_finish_reason`, `quality_check_hits_length`, `quality_check_hits_entropy`, `quality_check_hits_keyword`. Use to identify which check dominates in production.
- Config keys `Routing.QualityFallback.BadFinishReasons` (string[]; default `["length","content_filter"]`) and `Routing.QualityFallback.EntropyThreshold` (float; default `2.5`).

## [1.1.1] - 2026-05-10

Patch release fixing a critical bug in the v1.1.0 quality fallback path.

### Fixed

- **Quality fallback (35B → 122B retry) was broken for real model
  responses.** The `isBadResponse` heuristic was checking the raw
  OpenAI-compatible JSON envelope (which is always longer than 30
  characters and rarely contains literal `"TODO"` / `"I think"`),
  not the inner `choices[0].message.content`. As a result,
  length-based fallback never fired in production, and keyword-based
  fallback only fired when the model's content happened to embed the
  keyword string. Now parses the response and applies the heuristic
  to `message.content` directly. Malformed responses degrade safely
  (treated as empty content → fallback fires). (#13)

## [1.1.0] - 2026-05-10

Quality fallback release. The router now retries on 122B when 35B's
response is low-quality (non-streaming only), captures both responses in
an opt-in trace log for end-to-end debugging, and ships a cold-start
recovery path for corrupted models. README rewritten for operator focus.

### Added

#### Quality fallback (35B → 122B retry)
- Non-streaming requests routed to 35B are auto-retried on 122B when the
  35B response fails a quality heuristic (length below threshold, or a
  configurable bad-keyword match like `"TODO"` or `"I think"`). The
  client sees only the 122B response — the bad 35B response never leaks
- DecisionLog: `routing_reason="fallback_to_122b"` with `fallback_used=true`
- Streaming requests are intentionally exempt (chunks already shipped)
- Kill switch: `Routing.QualityFallback.Enabled=false`

#### Trace log (end-to-end debugging)
- `--trace-responses` CLI flag enables a per-request JSONL log at
  `logs/trace/YYYY-MM-DD.jsonl` (off by default; opt-in only)
- Each row joins by `prompt_uid` (first 12 hex of `prompt_hash`) and
  carries: `initial_target`, `initial_response_excerpt`, `fallback_kind`
  (`"quality"` | `"availability"` | null), `final_target`, `final_response_excerpt`
- Operator workflow: `jq 'select(.prompt_uid == "...")'` to see exactly
  what 35B said, why fallback fired (or didn't), and what 122B said

#### Cold-start recovery
- `--cold-start` CLI flag backs up `models/router.zip` and `datasets/*`
  with a timestamp suffix, then regenerates a fresh dummy classifier on
  the same startup. Recovery: rename the backup back into place

#### Configuration keys
- `Routing.QualityFallback.{Enabled, MinResponseLength, BadKeywords}`
- `Trace.{Enabled, Directory, ChannelCapacity}`

### Changed

- README condensed from 1310 to 669 lines. New features (quality
  fallback, trace log, cold-start, CLI flags) given dedicated sections;
  verbose architectural prose and per-endpoint duplicated examples
  trimmed. All 12 mandatory sections (config keys, endpoints, schemas,
  operations, etc.) preserved
- CHANGELOG rewritten from user perspective for v1.0.0 entry — internal
  phase references removed; entries organized by user-visible feature
  area

## [1.0.0] - 2026-05-10

First production release. F# .NET 10 gateway routing OpenAI-compatible chat
completion requests between Qwen 35B (latency-focused) and Qwen 122B
(quality-focused) running locally as `mlx_lm.server` instances.

### Added

#### Core routing
- 3-stage decision pipeline: explicit model override → task table → ML classifier
- 7 task types with model + priority mapping (`graph_indexing`,
  `compiler_debug`, `architecture_analysis`, `dependency_analysis`,
  `reasoning`, `retrieval`, `summary`)
- ML routing via bge-m3 int8 embeddings (multilingual; Korean + English) +
  ML.NET LbfgsLogisticRegression classifier; threshold tunable via
  `Routing.ML.Threshold` (default 0.5)
- First-run bootstrap auto-generates a dummy classifier when
  `models/router.zip` is missing — router never throws on cold start

#### Streaming + concurrency
- SSE streaming pass-through with mid-stream cancellation; the upstream call
  aborts within one chunk interval when the client disconnects
- Strategy-D `[DONE]` sentinel injection if upstream omits it
- Concurrency cap on 122B (`SemaphoreSlim(1)`) with two-level priority queue
  (high/low) + fairness counter; high-priority requests preempt low-priority
  ones in the queue
- 35B requests bypass the queue

#### Reliability
- Quality fallback: when 35B response fails a configurable heuristic, the
  router automatically retries the same prompt on 122B and forwards 122B's
  response (non-streaming only — streaming chunks already shipped to client
  cannot be retracted)
- Health-probe-based fallback: when 122B is unreachable, requests transparently
  reroute to 35B (`routing_reason: "fallback_to_35b"` in the decision log).
  Exception: `task: "graph_indexing"` returns HTTP 503 instead, never silently
  downgrading
- Per-upstream health probing every 10 seconds via `GET /v1/models`;
  configurable `ConsecutiveFailureThreshold`
- Transient retry policy on non-streaming requests (no retry on streaming —
  partial SSE output cannot be replayed)

#### Auto-retraining loop
- Background service detects failed routing decisions
  (`fallback_used=true`), asks a teacher model (122B by default) to relabel
  the prompt, accumulates labeled samples in `datasets/hard-cases.jsonl`,
  retrains the ML classifier, and atomically swaps in the new model when
  validation gates pass
- Classifier hot-swap via `PredictionEnginePool` with `watchForChanges:true`
  — in-flight requests complete on the previous model
- Daily cost cap on teacher calls (`TeacherLabeler.DailyCallCap`; default 1000)
- Manual offline retraining via `dotnet run -- --retrain`

#### Canary deployment
- When `models/router-canary.zip` is dropped into the models directory, 10%
  of traffic is routed to it (sticky per `correlation_id`); other 90% stays
  on the baseline
- Auto-rollback when canary fallback rate exceeds baseline by more than
  10% over a rolling 60-second window (`AutoRollbackEnabled`; default true)
- Manual promote / rollback / enable via `POST /canary/{promote,rollback,enable}`
- Decision log `model_version` distinguishes baseline (`ml-{sha}`) from
  canary (`ml-{sha}-canary`) so cohort comparison is a simple group-by

#### Endpoints
- `POST /v1/chat/completions` — main routing endpoint (OpenAI-compatible;
  streaming + non-streaming)
- `GET /v1/models` — deduplicated model list from both upstreams (returns
  HTTP 200 + empty array when both are down, never 503)
- `GET /health`, `GET /healthz` — per-upstream reachability + last probe
  timestamp
- `GET /stats` — queue depth, active counts, throughput, baseline + canary
  model versions, canary percentage, canary active flag (single-endpoint
  scrape for monitoring)
- `GET /canary`, `POST /canary/{promote,rollback,enable}` — canary admin
- `X-Correlation-Id` response header on every response (joinable to
  decision-log rows)

#### Logging
- Structured decision log at `logs/decisions/YYYY-MM-DD.jsonl` (one row
  per request; 12 fields including `correlation_id`, `prompt_hash`,
  `target`, `latency_ms`, `model_version`, `fallback_used`, `routing_reason`)
- Operational log: rolling daily files at `logs/operational/smart-router-{Date}.log`
  (50 MB cap, 30-day retention, automatic rotation)
- Trace log (opt-in via `--trace-responses`) at `logs/trace/YYYY-MM-DD.jsonl`
  with prompt + initial-response + final-response excerpts joinable by
  prompt UID for end-to-end debugging
- Auto-pruning background service for all three log streams +
  `datasets/teacher-cap-*.json` files

#### Configuration (`appsettings.json`)
- `Upstreams.{Model35B, Model122B}` — base URLs for the mlx_lm servers
- `Routing.ML.{ModelPath, EmbeddingModelPath, TokenizerPath, Threshold, MaxTokens}`
- `Routing.QualityFallback.{Enabled, MinResponseLength, BadKeywords}`
- `Routing.Health.{PollingIntervalSeconds, ConsecutiveFailureThreshold}`
- `Routing.TaskTable` (per-task model + priority)
- `Routing.ModelAliases` (`auto`, `35b`, `122b`)
- `Queue.{MaxConcurrent122B, FairnessK, PerRequestTimeoutSeconds}`
- `Canary.{CanaryModelPath, PercentageEnabled, RollingWindowSeconds, AutoRollbackThreshold, AutoRollbackEnabled, MinBaselineSampleSize}`
- `Logging.{Directory, RetentionDays}`
- `DecisionLog.{Directory, RetentionDays, ChannelCapacity}`
- `TeacherLabeler.{Endpoint, PromptPath, DailyCallCap, TimeoutSeconds}`
- `Serilog.MinimumLevel.{Default, Override}` (per-source filtering)

#### CLI flags
- `--port=N` — override listen port (1024..65535; default 4000)
- `--log-level=verbose|debug|information|warning|error|fatal` (or short
  aliases `info`, `warn`, `dbg`, `vrb`, `err`, `ftl`)
- `--trace-responses` — enable trace log
- `--cold-start` — back up existing model + dataset files with timestamp
  suffix and start fresh; recovery is `mv backup file` and restart
- `--retrain` — run offline retraining pipeline once and exit

#### Deployment
- launchd LaunchAgent definition at `deploy/com.ohama.smart-router.plist`
  with `KeepAlive`, `RunAtLoad`, and 30-second restart throttle
- `scripts/deploy.sh` — `dotnet publish -c Release` + asset copy to install dir
- `scripts/install-launchd.sh` — copies the plist to
  `~/Library/LaunchAgents/`; manual `launchctl load -w` documented
- `scripts/download-models.sh` — fetches bge-m3 int8 ONNX from
  HuggingFace via `hf` CLI

### Changed

- Listen port + bind address: `127.0.0.1` only (loopback) — no firewall
  surprises
- Model file paths resolve CWD-independently — falls back through binary
  location and parent-directory walk, so `dotnet run` works without a
  manual symlink
- ASP.NET Core middleware noise filtered to Warning by default; smart-router's
  own emissions stay at Information
- Hot-path routing-decision log emission demoted to Debug (the same data
  is always in the JSONL decision log)

### Fixed

- Every `/v1/chat/completions` request returning HTTP 500 due to a DI
  lifetime mismatch in the canary feature manager
- Build failure on .NET 10 SDK with `TreatWarningsAsErrors=true`
- Decision log `model_version` stale after in-process retraining swap;
  now reflects the new model on the very next request
- Model embedding file download script broken by `huggingface-cli`
  deprecation; switched to `hf` CLI and corrected the source path
- README missing the prerequisites section for the bge-m3 ONNX files

### Removed

- Heuristic routing — the ML classifier is the only stage-3 algorithm.
  Historical snapshot preserved at git branch `archive/heuristic-baseline`
  and tag `v0.5-heuristic-baseline`
- `Routing.Algorithm` configuration key — no replacement; ML always runs
- `--routing-algorithm` CLI flag — no replacement
- `--trace` boolean CLI flag — replaced by `--log-level=debug` (legacy
  flag now raises a clear migration error)

### Requirements

- macOS arm64 (Apple Silicon) — mlx_lm runs Metal kernels
- .NET 10 SDK
- Two `mlx_lm.server` instances (Qwen 3.6 35B at port 8000, Qwen 3.5 122B
  at port 8001)
- bge-m3 int8 ONNX files (~547 MB total) under `models/embed/` —
  fetched via `./scripts/download-models.sh` (requires Python
  `huggingface_hub`)

## [v0.5-heuristic-baseline] - 2026-05-08

Snapshot of the heuristic routing implementation, preserved as a historical
reference. ML routing replaced the heuristic in v1.0.0; this tag remains
on `archive/heuristic-baseline` for rollback or comparison.
