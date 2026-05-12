# Roadmap: smart-router v2.0 — Self-Routing + Session-Aware

## Overview

v2.0 replaces the v1.x ML-classifier routing path with a three-layer cascade — keyword **Hard Rules** → **35B self-classify** (1-token SAFE/UNSAFE) → **sticky session escalation** — and wires Hermes Agent above smart-router for session-aware debugging continuity. ML code (Phases 6-9 + 14-16) is retained in repo but routing-path dormant, gated by a new `Routing.Mode = "selfrouting" | "ml"` config switch (operator can roll back to ML routing with an `appsettings.json` edit + restart, no rebuild). Phase order is driven by `RouterRequest.SessionId` field compile dependency: **Hard Rules (17) → Session Store (18) → SelfRouter (19) → Hermes Integration (20)** — SessionStore must precede SelfRouter because the algorithm closure reads `req.SessionId` at sticky stage 4 before invoking ClassifyAsync at stage 5.

## Milestones

- ✅ **v1.0–v1.3 ML Routing** — Phases 1-16 (shipped 2026-05-11; archived to `.planning/milestones/v1.3-ROADMAP.md`)
- 🚧 **v2.0 Self-Routing + Session-Aware** — Phases 17-20 (current)

## Phases

**Phase Numbering:**
- Integer phases (17, 18, 19, 20): Planned milestone work
- Decimal phases (17.1, 17.2): Urgent insertions (marked with INSERTED)
- v2.0 continues numbering from v1.3's end at Phase 16. Original Phase 17 (ML QualityClassifier) was deferred during v1.3 close, so the number is free for v2.0's Hard Rules phase.

Decimal phases appear between their surrounding integers in numeric order.

- [x] **Phase 17: Hard Rules Layer + Routing.Mode Switch** ✓ — Stage 0 keyword pre-routing + dormant-ML config gate shipped. 3 plans / 3 waves: 17-01 Core (`HardRules.fs` BCL-only + 6 hardcoded keywords + case-insensitive; `RoutingReason.HardRule` 7th DU; `Routing.fs` 4-stage cascade with Stage 0 BEFORE tryModelOverride; `DecisionLogger.formatReason` 7-arm; HR-03 wins per STATE.md decision 5); 17-02 Cli (`Routing.Mode` config + fail-fast validation; `RoutingAlgorithmRegistration` factory mode-branched — `"ml"` = v1.3 verbatim, `"selfrouting"` = stub with `ModelVersion="selfrouting-v1"`; ML adapters unconditionally DI-registered per MODE-03); 17-03 tests + docs (`HardRulesTests.fs` 16 tests, `ModeSwitchTests.fs` 9 tests with mlTestCase skip-guard; README §2/§5.0/§5.1/§7/§9.1; CHANGELOG paradigm pivot; REQUIREMENTS HR-06+MODE-03 wording fixes; ROADMAP SC-2+17-02 description fixes). **137 passed + 17 ignored + 0 failed** (113→+16+8; 1 ml-mode test skip-guarded). gsd-verifier: 18/18 must-haves passed. ARCH-01 preserved. schema_version=1 unchanged.
- [x] **Phase 18: Session Store + Sticky Escalation** — `RouterRequest.SessionId` field, `ISessionStore` adapter with TTL eviction, sticky-122B continuation logic ✓ (3 plans; 150 passed; all 9 SES-* satisfied; ROADMAP SC-1..5 verified)
- [x] **Phase 19: 35B Self-Routing** — Named `selfrouter` HttpClient + 1-token SAFE/UNSAFE classify + prompt-hash LRU cache + cascade integration (non-streaming only) ✓ (4 plans / 16 commits; 167 passed + 18 ignored; all 9 SR-* satisfied; ROADMAP SC-1..5 verified)
- [x] **Phase 20: Hermes Agent Integration + Documentation** ✓ — `X-Session-Id` header convention + IP+UA fingerprint fallback + README/CHANGELOG v2.0 documentation (2 plans / 8 commits; 175 passed + 18 ignored + 0 failed; all 4 HMRS-* satisfied; ROADMAP SC-3/SC-4 verified, SC-1/SC-2 deferred to operator acceptance via `./scripts/smoke-hermes-session.sh`. **v2.0 milestone COMPLETE — 32/32 requirements satisfied; CHANGELOG promoted to [2.0.0] - 2026-05-12.**)

## Phase Details

### Phase 17: Hard Rules Layer + Routing.Mode Switch

**Goal**: An operator can flip the router into one of two modes via `appsettings.json:Routing.Mode`. In `"selfrouting"` mode (new default), a keyword-driven Stage 0 pre-routing layer forces immediate-122B for known-heavy-keyword prompts (LLVM, MLIR, compiler, segfault, optimization, concurrency) regardless of any other signal. In `"ml"` mode, the v1.x ML routing path remains intact and unchanged. The Hard Rules layer ships in isolation — fully testable without DI, without HTTP, without session state — and locks the cascade ordering spec in code before later phases depend on it.

**Depends on**: Nothing (v2.0 foundation; integrates with shipped v1.3 codebase)

**Requirements**: MODE-01, MODE-02, MODE-03, MODE-04, HR-01, HR-02, HR-03, HR-04, HR-05, HR-06

**Success Criteria** (what must be TRUE):

1. **Hard Rules fire for default keywords (both modes)**: Sending a prompt containing `"LLVM"` (or `"MLIR"`, `"compiler"`, `"segfault"`, `"optimization"`, `"concurrency"` — case-insensitive) to `POST /v1/chat/completions` routes to Qwen 122B with `routing_reason="hard_rule"` in the DecisionLog JSONL row, regardless of whether `Routing.Mode` is `"selfrouting"` or `"ml"`. Verified by inspecting `logs/decisions/YYYY-MM-DD.jsonl`.
2. **Hard Rules wins over explicit override and task table**: Sending `{"model": "35b", "messages": [{"content": "LLVM optimization"}]}` routes to **122B** with `routing_reason="hard_rule"` — Hard Rules wins over model override per STATE.md decision 5 (safety mechanism precedence; same rationale as the corrected HR-06 in REQUIREMENTS.md). Verified by unit tests in `HardRulesTests.fs` + `ModeSwitchTests.fs` that exercise Stage 0 cascade ordering.
3. **Routing.Mode operator-visible**: Operator sets `Routing.Mode="ml"` in `appsettings.json`, restarts via `launchctl kickstart -k gui/$(id -u)/com.ohama.smart-router`, and a non-Hard-Rules-matching request routes through the v1.x ML classifier — verified by `routing_algorithm="ml"` (or `"ml-canary"`) in DecisionLog. Operator sets `Routing.Mode="selfrouting"` and restarts; the same prompt routes through the v2.0 cascade — verified by absence of `routing_algorithm="ml"` in subsequent DecisionLog rows (selfrouting algorithm hook lands in Phase 19; Phase 17 ships the mode-switch wiring + Hard Rules + a stub `selfrouting` algorithm that defers to existing default behavior until Phase 19).
4. **Invalid Routing.Mode fails startup loudly**: Setting `Routing.Mode="invalid"` (or any string outside `{"selfrouting", "ml"}`) prevents Kestrel from binding to `:4000` and emits a descriptive error to stderr + operational log (e.g., `Routing.Mode value 'invalid' is not recognized; valid values are 'selfrouting' or 'ml'`). Verified by integration test launching with bad config.
5. **README §5.5 (routing pipeline) and §7 (config reference) reflect Hard Rules + Routing.Mode**: README §5 documents the new cascade order (Stage 0 = Hard Rules → existing stages). README §7 documents `Routing.Mode` key with both values and the default flip. CHANGELOG `### Changed` notes the v2.0 default flip to `"selfrouting"`. DecisionLog §9.1 lists `"hard_rule"` as a new valid `routing_reason` value with schema_version=1 unchanged (additive).

**Plans**: 3 plans

Plans:
- [ ] 17-01-PLAN.md — Core Hard Rules + DU extensions — `SmartRouter.Core/HardRules.fs` BCL-only pure function (`applyHardRules: RouterRequest -> RoutingDecision option`); `RoutingReason.HardRule` 7th DU case; `DecisionLogger.formatReason` exhaustive 7-arm match → `"hard_rule"`; default keyword list hardcoded; unit tests for all 6 keywords + case-insensitivity + no-match passthrough
- [x] 17-02-PLAN.md — Routing.Mode config + cascade wiring — `appsettings.json:Routing.Mode` key with validation (fail-fast on unrecognized value); `CompositionRoot.configureRequestPipeline` two-branch on `Routing.Mode` (selfrouting → stub algorithm returning 35B/Default; ml → existing v1.x path); HR-05 satisfied via `routeRequest` shared call site — both streaming and non-streaming branches in `ChatCompletions.fs` already call `routeRequest`, so Stage 0 fires automatically; no `ChatCompletions.fs` change required in Plan 17-02
- [ ] 17-03-PLAN.md — README + CHANGELOG + integration tests + REQUIREMENTS.md HR-06 wording fix — README §5.5 routing pipeline diagram updated, §7 `Routing.Mode` config reference added; CHANGELOG `[Unreleased] ### Added` (Hard Rules layer) + `### Changed` (v2.0 mode default); integration test fixture mlx_lm fakes + DecisionLog grep for `routing_reason=hard_rule`; baseline test count update

---

### Phase 18: Session Store + Sticky Escalation

**Goal**: When a request includes an `X-Session-Id` HTTP header and a previous request in that session routed to 122B (either by initial routing, Hard Rules, or quality fallback), the current request also routes to 122B — debugging continuity is preserved across the session even if the current prompt looks "easy" to any classifier. Requests without `X-Session-Id` (v1.x backward-compat clients) continue working stateless. The session store self-evicts after 30 minutes of inactivity (TTL) and is bounded at 10,000 entries (LRU). The infrastructure ships ready for Phase 19's SelfRouter to read from + write to.

**Depends on**: Phase 17 (Routing.Mode switch + cascade ordering established; HardRule reason precedes StickyEscalation in cascade)

**Requirements**: SES-01, SES-02, SES-03, SES-04, SES-05, SES-06, SES-07, SES-08, SES-09

**Success Criteria** (what must be TRUE):

1. **Sticky escalation propagates 122B continuity**: Send a first request with header `X-Session-Id: debug-abc123` and a prompt that routes to 122B (e.g., `{"messages": [{"content": "LLVM segfault"}]}` triggers Hard Rules → 122B). Send a second request with the same header and a trivial prompt (`{"messages": [{"content": "hello"}]}`). The second request routes to Qwen 122B with `routing_reason="sticky_to_122b"` in DecisionLog — verified by `jq` over `logs/decisions/YYYY-MM-DD.jsonl`.
2. **v1.x clients without X-Session-Id continue stateless**: Send a sequence of requests with NO `X-Session-Id` header. First request routes to 122B (Hard Rule). Second request with trivial prompt routes to 35B (NOT 122B — no sticky bucket created for empty session_id). Verified by DecisionLog inspection showing `routing_reason="hard_rule"` then a non-`sticky_to_122b` reason on the second row.
3. **Session TTL eviction self-heals**: Send a request with `X-Session-Id: debug-stale` that routes to 122B. Wait past `Routing.Session.TtlMinutes` (default 30, set to 1 minute in integration test). Send a second request with the same header and a trivial prompt. The second request routes to 35B (entry evicted by `SessionTtlEvictionService` BackgroundService) with `routing_reason` reflecting the un-sticky cascade outcome — verified by integration test driving the eviction loop at 5-second interval.
4. **Quality fallback updates session store**: Send a request with `X-Session-Id: debug-fallback` where 35B's response triggers Phase 14 quality fallback → 122B retry. After the response completes, send a second request with the same header and a trivial prompt. The second request routes to Qwen 122B with `routing_reason="sticky_to_122b"` — verified by integration test asserting the post-fallback session entry was written with `LastModel=Qwen122B`.
5. **Concurrent-write 122B-wins merge**: Send two simultaneous requests with the same `X-Session-Id` where one routes to 35B and the other to 122B. The session store ends in a state where `LastModel=Qwen122B` (never overwritten back to 35B by the racing 35B write) — verified by unit test using `Task.WhenAll` on `AddOrUpdate` calls and asserting final state via `ISessionStore.TryGet`.

**Plans**: 3 plans

Plans:
- [ ] 18-01-PLAN.md — Core domain extension + DU — `SmartRouter.Core/Domain.fs` `RouterRequest` gains `SessionId: string` field (empty = stateless); `RoutingReason.StickyEscalation` 8th DU case → `"sticky_to_122b"`; `formatReason` exhaustive match cascaded; all existing test construction sites updated for the new required field (mirrors Phase 9 `CorrelationId` field addition pattern)
- [ ] 18-02-PLAN.md — SessionStore adapter + middleware wiring — `SmartRouter.Cli.Adapters.SessionStore` (`ConcurrentDictionary<string, SessionState>` with `AddOrUpdate` 122B-wins merge function + `Interlocked.Increment` monotonic access counter + bounded ~10,000 LRU eviction); `CorrelationMiddleware` extended to read `X-Session-Id` header into `HttpContext.Items[SessionIdKey]`; `ChatCompletions.fs` `mapWireToRequest` populates `req.SessionId` from `ctx.Items`; sticky cascade stage AFTER Hard Rules + explicit overrides + (future) self-classify, BEFORE QueueDispatcher; quality fallback Point B writes session store after `decisionLogger.Log`; `appsettings.json:Routing.Session.{TtlMinutes, MaxEntries}` config keys (mutable fields per Phase 13-05 lesson)
- [ ] 18-03-PLAN.md — TTL eviction BackgroundService + tests + README — `SessionTtlEvictionService` BackgroundService (PeriodicTimer 5min interval; ExceptionDispatchInfo.Capture for OCE through task{} await points per Phase 8 pattern); `SessionStoreTests.fs` unit tests (concurrent-write 122B-wins merge, TTL eviction, LRU bound, empty-session_id stateless path, header round-trip via `CorrelationMiddleware`); `StickyEscalationTests.fs` integration tests (sticky-after-hard-rule, stateless-no-header, ttl-evicts, quality-fallback-writes-session); README §7 `Routing.Session.*` config keys documented; §9.1 `routing_reason="sticky_to_122b"` listed (schema_version=1 unchanged)

---

### Phase 19: 35B Self-Routing (Stage 5 Self-Classify)

**Goal**: For non-streaming requests that pass through Hard Rules without a match AND don't have an active sticky session, the router calls the 35B model itself with a tiny "SAFE for me?" prompt (`max_tokens=4`, `temperature=0`, `stream=false`) and routes based on the 1-token verdict — `SAFE` → 35B, `UNSAFE` (or ambiguous parse) → 122B. Repeated prompts hit a prompt-hash LRU cache (no HTTP call). Streaming requests INTENTIONALLY SKIP self-classify (latency budget cannot accommodate a classify round-trip before first SSE chunk; Hard Rules + sticky still apply). The `Routing.Mode="ml"` path is exercised by a dormant integration test to prevent drift.

**Depends on**: Phase 18 (RouterRequest.SessionId field; sticky cascade position established — sticky runs BEFORE self-classify so escalated sessions never burn a classify token)

**Requirements**: SR-01, SR-02, SR-03, SR-04, SR-05, SR-06, SR-07, SR-08, SR-09

**Success Criteria** (what must be TRUE):

1. **Self-routing decides on a non-streaming "easy" prompt**: Send `POST /v1/chat/completions` with `{"messages": [{"content": "what is 2+2"}], "stream": false}` and no `X-Session-Id`. The router invokes the 35B self-classify call (named `"selfrouter"` HttpClient, 5s timeout, `max_tokens=4`, `temperature=0`); the 35B returns `SAFE`; the request serves on 35B. Verified by DecisionLog row with `routing_reason="self_route"` AND `routing_algorithm="selfrouting"` AND `model_version` reflecting the v2.0 selfrouter prompt hash.
2. **Self-routing escalates an ambiguous prompt**: Send `{"messages": [{"content": "design a distributed consensus algorithm for byzantine fault tolerance"}]}`. The 35B self-classify returns `UNSAFE` (or ambiguous output — safety-biased parser treats parse-fail as UNSAFE); the request serves on 122B. Verified by DecisionLog `routing_reason="self_route"` AND the response was generated by 122B (model_id field).
3. **Streaming requests SKIP self-classify**: Send a request with `"stream": true` and a non-Hard-Rules prompt. The DecisionLog row has `routing_reason` NOT equal to `"self_route"` (skipped; falls back to existing stage-6 default). Hard Rules still apply if keywords match (streaming + LLVM → Hard Rule → 122B). Verified by integration test asserting `routing_reason ∉ {"self_route"}` for streaming non-keyword requests and `routing_reason="hard_rule"` for streaming keyword requests.
4. **Prompt-hash cache hits skip the HTTP call**: Send the same prompt twice (non-streaming). The first request increments `selfrouter_call_count` and `selfrouter_cache_misses` in `GET /stats`; the second request increments `selfrouter_cache_hits` but NOT `selfrouter_call_count`. Verified by `/stats` snapshot before and after.
5. **ML dormant integration test stays green**: With `Routing.Mode="ml"`, the existing v1.x ML routing path remains functional — `MlDormantTests.fs` exercises a request through the ML classifier and asserts `routing_algorithm="ml"` (or `"ml-canary"`) in DecisionLog. The test runs in CI to prevent dormant ML code from silently breaking across v2.x phases.

**Plans**: 4 plans

Plans:
- [ ] 19-01: SelfRouter adapter + DU — `SmartRouter.Cli.Adapters.SelfRouter` (named `"selfrouter"` HttpClient via `.ConfigureHttpClient` chain pointing to `Upstreams.Model35B`; 5s timeout; 1 retry at 200ms via `AddResilienceHandler`; mirrors JudgeClient architecture from Phase 16); `SelfRouteVerdict` DU (`RouteSafe | RouteUnsafe | RouteSkipped of string | RouteFailed of string`) with safety-biased parser (ambiguous → `RouteUnsafe`); `prompts/self-router-prompt.md` operator-tunable template with `{{PROMPT}}` placeholder + SAFE/UNSAFE instruction (per `.planning/docs/35b-selfrouting-prompt.md`); `RoutingReason.SelfRoute` 9th DU case → `"self_route"`; `formatReason` cascaded
- [ ] 19-02: Cache + Stats + DI wiring — `ConcurrentDictionary<string, CachedVerdict>` prompt-hash LRU (`Interlocked.Increment` monotonic access counter; bounded ~10,000 entries; mirrors Phase 16 `JudgeClient` cache pattern); `IStatsProvider`/`StatsWire` 4 new flat snake_case fields (`selfrouter_cache_hits`, `_misses`, `_call_count`, `_skipped`); `CompositionRoot.configureRequestPipeline` registers `SelfRouter` only when `Routing.Mode="selfrouting"`; `appsettings.json:Routing.SelfRouter.*` config keys (Endpoint defaulting to Upstreams.Model35B at DI time, Enabled, CacheMaxEntries)
- [ ] 19-03: Cascade integration + streaming skip + tests — `ChatCompletions.fs` non-streaming branch: after Hard Rules + explicit overrides + sticky-check → call SelfRouter → branch on verdict (`RouteSafe` → Qwen35B / `RouteUnsafe` → Qwen122B / `RouteSkipped|RouteFailed` → fail-open to existing v1.x default 35B); streaming branch: explicit `if req.Stream then skip` comment matching Phase 14 pattern (Hard Rules + sticky still apply); `MlDormantTests.fs` integration test boots with `Routing.Mode="ml"` and asserts ML path wires through DI without errors; `SelfRouterTests.fs` unit tests (parse-safety-bias, cache hit/miss, ambiguous → UNSAFE); `SelfRoutingIntegrationTests.fs` end-to-end fake-Kestrel with fake `selfrouter` 35B
- [ ] 19-04: README + CHANGELOG + sticky+selfrouter interplay verification — README §5.5 routing pipeline updated to full v2.0 cascade (Stage 0 Hard Rules → Stage 1 explicit override → Stage 2 task table → Stage 3 sticky → Stage 4 self-classify → Stage 5 default 35B); §7 `Routing.SelfRouter.*` config keys documented; §9.1 `routing_reason="self_route"` listed (schema_version=1 unchanged); CHANGELOG `[Unreleased] ### Added` (selfrouting paradigm); `routing_algorithm="selfrouting"` documented in §9.1

---

### Phase 20: Hermes Agent Integration + Documentation

**Goal**: Smart-router is ready to receive `X-Session-Id` from Hermes Agent (when Hermes ships its propagation PR — tracked as future work, NOT a v2.0 blocker). For the loopback single-client case where Hermes does NOT yet send the header, smart-router optionally derives a session key from `RemoteIpAddress + User-Agent` SHA-256 prefix (16 hex) when `Routing.Session.FingerprintEnabled=true` (default `false`; opt-in to preserve v1.x stateless behavior). README §10 fully rewrites the Hermes Integration section for v2.0 paradigm. CHANGELOG documents the v2.0 paradigm shift + Hermes-side wiring as future work. No Hermes Agent code modifications ship in v2.0.

**Depends on**: Phase 19 (full selfrouting pipeline must exist before integration is meaningful; sticky escalation needs SessionStore + SelfRouter both operational)

**Requirements**: HMRS-01, HMRS-02, HMRS-03, HMRS-04

**Success Criteria** (what must be TRUE):

1. **X-Session-Id header propagation works end-to-end**: Send a request with `X-Session-Id: hermes-session-xyz` that routes to 122B (Hard Rule). Send a second request with the same header. The second request routes to Qwen 122B with `routing_reason="sticky_to_122b"`. Verified by curl loop against running smart-router instance (smoke test script in `scripts/smoke-hermes-session.sh`).
2. **Fingerprint fallback is opt-in and works for loopback**: With `Routing.Session.FingerprintEnabled=true` and no `X-Session-Id` header, send two requests from the same client (same `RemoteIpAddress + User-Agent`) where the first matches Hard Rules → 122B. The second request routes to 122B with `routing_reason="sticky_to_122b"` (fingerprint-derived session_id matched). With `FingerprintEnabled=false` (default), the same sequence routes the second request to 35B (no fingerprint session created). Verified by integration test with both config settings.
3. **README §10 fully rewritten for v2.0**: README §10 "Hermes Integration" describes the selfrouting paradigm (replacing v1.x ML routing), explains `X-Session-Id` header opt-in, documents the fingerprint fallback caveats (loopback only; not safe behind reverse proxies — tracked as future PROXY-01), and notes that Hermes-side propagation is future (v2.x) work tracked as a Hermes Agent PR. CHANGELOG `[Unreleased] ### Added` mentions v2.0 Hermes integration (session-aware capability available; Hermes-side wiring pending); `### Changed` notes the paradigm shift from ML routing to selfrouting.
4. **Smoke test script exists and runs without a Hermes Agent dependency**: `scripts/smoke-hermes-session.sh` (or equivalent) drives the X-Session-Id round-trip against a running smart-router instance using curl, asserts the second request's DecisionLog row has `routing_reason="sticky_to_122b"`, and documents the limitation that real Hermes Agent integration requires a separate Hermes-side PR. The script is operator-runnable.

**Plans**: TBD (estimated 2 plans)

Plans:
- [x] 20-01: Fingerprint fallback + smoke test — `Routing.Session.FingerprintEnabled` config key (default `false`); `CorrelationMiddleware` or `mapWireToRequest` helper: `resolveSessionKey` preferring explicit `X-Session-Id` header, falling back to `SHA-256(RemoteIpAddress + "|" + User-Agent).[0..15]` when fingerprint enabled AND header absent; `scripts/smoke-hermes-session.sh` curl loop + DecisionLog grep assertion; integration test `HermesFingerprintTests.fs` covering both header-explicit and fingerprint paths
- [x] 20-02: README §10 rewrite + CHANGELOG + REQUIREMENTS.md closure — README §10 "Hermes Integration" rewritten for v2.0 (selfrouting paradigm description, X-Session-Id opt-in, fingerprint caveats, Hermes-side PR tracked as future work); README §7 `Routing.Session.FingerprintEnabled` documented; CHANGELOG `[Unreleased] ### Added` (Hermes integration) + `### Changed` (paradigm shift) finalized for v2.0.0 release; REQUIREMENTS.md HMRS-FUTURE-01/02 retained as v2.x tracker entries

## Progress

**Execution Order:**
Phases execute in numeric order: 17 → 18 → 19 → 20. Within each phase, plans execute in numeric order. Phase 20 is the last phase of v2.0.

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 17. Hard Rules + Routing.Mode | v2.0 | 3/3 | ✓ Complete | 2026-05-11 |
| 18. Session Store + Sticky | v2.0 | 0/3 | Not started | - |
| 19. 35B Self-Routing | v2.0 | 0/4 | Not started | - |
| 20. Hermes Integration | v2.0 | 2/2 | ✓ Complete | 2026-05-12 |

## Coverage

v2.0 requirement coverage: **32 / 32 mapped** (no orphans, no duplicates).

| Phase | Requirements |
|-------|--------------|
| Phase 17 | MODE-01, MODE-02, MODE-03, MODE-04, HR-01, HR-02, HR-03, HR-04, HR-05, HR-06 (10 reqs) |
| Phase 18 | SES-01, SES-02, SES-03, SES-04, SES-05, SES-06, SES-07, SES-08, SES-09 (9 reqs) |
| Phase 19 | SR-01, SR-02, SR-03, SR-04, SR-05, SR-06, SR-07, SR-08, SR-09 (9 reqs) |
| Phase 20 | HMRS-01, HMRS-02, HMRS-03, HMRS-04 (4 reqs) |

## Architectural Invariants (preserved across v2.0)

- **ARCH-01** (Core BCL-only): `HardRules.fs` is the only new Core file (BCL-only pure function). All other v2.0 adapters (`SessionStore`, `SelfRouter`, `SessionTtlEvictionService`, fingerprint helper) live in `SmartRouter.Cli.Adapters`. No new Serilog/HttpClient/Microsoft.ML imports in Core.
- **ARCH-02** (`task {}` only): All async code uses `task {}` CE. `scripts/check-no-async.sh` continues to enforce.
- **DecisionLog schema_version=1 unchanged**: All new `routing_reason` values (`hard_rule`, `sticky_to_122b`, `self_route`) are additive enum values; no field removals; no type changes. Same for `TraceLog`.
- **Streaming branch INTENTIONALLY SKIPPED for self-classify** (new in SR-06; mirrors Phase 14 quality fallback streaming-skip pattern): explicit `if req.Stream then skip SelfRouter` with code comment. Hard Rules + sticky still apply to streaming.
- **Backward-compat**: v1.x clients without `X-Session-Id` header continue working stateless (empty session_id = no sticky read, no sticky write — Pitfall 3 from research).
- **ML dormancy**: ML code (Phase 6-9, 14-16 adapters) retained in repo; `Routing.Mode="ml"` re-activates the v1.x routing path with one config edit + restart. `MlDormantTests.fs` (Phase 19) prevents silent drift.

---

*Roadmap created: 2026-05-11 via `/gsd:new-milestone` → roadmapper after research + REQUIREMENTS.md.*
*v2.0 milestone "Self-Routing + Session-Aware" — Phases 17-20.*
