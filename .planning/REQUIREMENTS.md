# Requirements: Smart Router — v2.0 Self-Routing + Session-Aware

**Defined:** 2026-05-11
**Core Value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.
**Milestone:** v2.0 (paradigm shift from v1.x ML routing to Hard Rules + 35B self-classify + sticky escalation)

## v2.0 Requirements

Requirements for this milestone. Each maps to a roadmap phase.

### Cross-cutting (Routing.Mode switch)

These ship in Phase 17 setup as the foundation for ML dormancy.

- [ ] **MODE-01**: `appsettings.json:Routing.Mode` config key (string; default `"selfrouting"`); accepted values `"selfrouting"` | `"ml"`; invalid value fails startup with descriptive error
- [ ] **MODE-02**: `CompositionRoot.configureRequestPipeline` branches on `Routing.Mode`: `"selfrouting"` → register Hard Rules + SelfRouter + SessionStore (replaces ML stage 3); `"ml"` → register ML classifier (existing v1.x behavior preserved)
- [ ] **MODE-03**: ML adapters (`BgeM3Embedder`, `MlNetClassifier`, `RetrainingService`, `CanaryService`) remain compiled and DI-registered in BOTH modes; `RoutingAlgorithmRegistration.Algorithm` function branches on `Routing.Mode` so ML adapters are not invoked in the routing path when `Routing.Mode=selfrouting` (but `RetrainingService` `BackgroundService` still runs to accumulate hard cases so ML can be re-activated via config edit + restart without retraining from scratch)
- [ ] **MODE-04**: README §7 documents the `Routing.Mode` key with both modes; CHANGELOG `### Changed` notes the v2.0 default flip

### Hard Rules Layer (Phase 17 — HR-*)

- [ ] **HR-01**: `SmartRouter.Core/HardRules.fs` BCL-only module with `applyHardRules: RouterRequest -> RoutingDecision option`; pure function; case-insensitive `String.Contains(kw, StringComparison.OrdinalIgnoreCase)` over keyword list
- [ ] **HR-02**: Default keyword list hardcoded in HardRules.fs: `["LLVM"; "MLIR"; "compiler"; "segfault"; "optimization"; "concurrency"]` (per `.planning/docs/35b-selfrouting.md` §6,12); NOT exposed as config (operator safety mechanism shouldn't be misconfigurable)
- [ ] **HR-03**: Hard Rules cascade position: Stage 0 — runs BEFORE explicit model override / explicit task / self-classify; any keyword match → immediate-122B with `RoutingDecision { Target=Qwen122B; Reason=HardRule; Priority=High }`
- [ ] **HR-04**: `RoutingReason.HardRule` DU case added (7th case after FallbackTo122B); `DecisionLogger.formatReason` exhaustive 7-arm match → `"hard_rule"` JSONL value; schema_version=1 unchanged
- [ ] **HR-05**: Hard Rules applies to BOTH streaming and non-streaming branches in `ChatCompletions.fs` (0ms keyword check; no chunk-shipped concern unlike quality fallback)
- [ ] **HR-06**: Unit tests verify 6 default keywords trigger; case-insensitive matching ("llvm", "Compiler" match); no-match passes through; **Hard Rules wins over explicit model override AND explicit task field** (Stage 0 cascade position confirmed per HR-03 and STATE.md design decision 5 — a request with `{"model": "35b", "messages": [{"content": "LLVM ..."}]}` MUST route to 122B with `routing_reason="hard_rule"`, NOT 35B with `routing_reason="explicit_model"`). Hard Rules is a safety mechanism; bypass is intentionally not supported.

> **Note (Phase 17 wording fix):** The original HR-06 text said "explicit model override AND explicit task field both BYPASS Hard Rules." That contradicted HR-03's Stage-0 cascade position and STATE.md decision 5. Updated 2026-05-11 during Phase 17 planning to reflect the locked design: Hard Rules wins. The bypass language was misleading — there is no operator-facing way to disable Hard Rules per-request short of removing the keyword from `HardRules.fs` and rebuilding.

### Session Store + Sticky Escalation (Phase 18 — SES-*)

- [ ] **SES-01**: `SmartRouter.Core/Domain.fs` `RouterRequest` record gains `SessionId: string` field (empty string = no session, stateless path); `RoutingDecision` unchanged
- [ ] **SES-02**: `SmartRouter.Cli.Adapters.SessionStore` adapter — `ConcurrentDictionary<string, SessionState>` + LRU eviction (`Interlocked.Increment` access counter; bounded ~10000 entries; mirrors Phase 16 JudgeClient cache pattern) + 30-minute TTL per entry
- [ ] **SES-03**: `SessionState` record: `{ LastModel: ModelTarget; LastAccessedAt: DateTimeOffset; LastAccessSeq: int64 }`; `AddOrUpdate` with merge function — if either old or new is `Qwen122B` → keep `Qwen122B` (prevents escalation loss under concurrent writes)
- [ ] **SES-04**: `CorrelationMiddleware` extended to read `X-Session-Id` HTTP header; store in `HttpContext.Items[SessionIdKey]`; null-safe (header absent → empty session_id, stateless path)
- [ ] **SES-05**: `ChatCompletions.fs` `mapWireToRequest` populates `req.SessionId` from `ctx.Items`; sticky escalation cascade stage: AFTER Hard Rules + explicit overrides + self-classify, BEFORE QueueDispatcher — if `session.LastModel == Qwen122B` then override final decision to `Qwen122B` with `Reason=StickyEscalation`
- [ ] **SES-06**: `RoutingReason.StickyEscalation` DU case (8th); `formatReason` arm `"sticky_to_122b"`; schema_version=1 unchanged
- [ ] **SES-07**: Quality fallback (Phase 14-16) writes session store on 35B → 122B substitution — if `finalDecision.Target == Qwen122B` AND `req.SessionId != ""` → `sessionStore.Update(req.SessionId, Qwen122B)` AFTER decisionLogger.Log; prevents continuation requests reverting to 35B
- [ ] **SES-08**: TTL cleanup BackgroundService — PeriodicTimer (5min interval) iterates session store, removes entries older than `Session:TtlMinutes` (default 30); ExceptionDispatchInfo.Capture for OCE through task{} await points (Phase 8 pattern)
- [ ] **SES-09**: `appsettings.json:Session.{TtlMinutes, MaxEntries}` config keys (defaults 30, 10000); CLIMutable + explicit `mutable` field declarations (Phase 13-05 + 14-02 lesson)

### 35B Self-Routing (Phase 19 — SR-*)

- [ ] **SR-01**: `SmartRouter.Cli.Adapters.SelfRouter` adapter — named `"selfrouter"` HttpClient pointing to `Upstreams.Model35B` (NOT reusing `upstream35b` due to 300s vs 5s timeout difference); separate HttpClientFactory registration; 5s timeout; 1 retry at 200ms via AddResilienceHandler
- [ ] **SR-02**: `prompts/self-router-prompt.md` operator-tunable template with `{{PROMPT}}` placeholder + SAFE/UNSAFE instruction (per `.planning/docs/35b-selfrouting-prompt.md` §3); `max_tokens=4-8`, `temperature=0`, `stream=false` request body parameters
- [ ] **SR-03**: `SelfRouteVerdict` DU (4 cases): `RouteSafe | RouteUnsafe | RouteSkipped of reason: string | RouteFailed of error: string`; parser is safety-biased — anything ambiguous → `RouteUnsafe` (NOT `RouteSafe`)
- [ ] **SR-04**: LRU cache by `prompt_hash` (Phase 5 helper) — `ConcurrentDictionary<string, CachedVerdict>` + monotonic access counter + bounded ~10000 entries; cache hit fast-path (no HTTP call)
- [ ] **SR-05**: `IStatsProvider` extension — `selfrouter_cache_hits` / `_misses` / `_call_count` / `_skipped` flat snake_case fields in `StatsWire` (mirrors Phase 16 `judge_cache_*` pattern); `Interlocked.Increment` counters
- [ ] **SR-06**: Streaming branch SKIPS self-classify — `if req.Stream then skip SelfRouter` (Pitfalls researcher: first-chunk latency budget cannot accommodate classify call); Hard Rules + sticky still apply for streaming
- [ ] **SR-07**: `RoutingReason.SelfRoute` DU case (9th); `formatReason` arm `"self_route"`; schema_version=1 unchanged
- [ ] **SR-08**: `ChatCompletions.fs` cascade integration — non-streaming branch only — after Hard Rules + explicit overrides → call SelfRouter → `RouteSafe` → Qwen35B / `RouteUnsafe` → Qwen122B / `RouteSkipped|RouteFailed` → fail-open to existing v1.x fallback default (35B)
- [ ] **SR-09**: ML dormant integration test — single test verifies `Routing.Mode="ml"` boot path still wires ML classifier; routes test request through ML path; prevents future Routing.Mode toggle from finding broken dormant code (Pitfalls researcher)

### Hermes Agent Integration (Phase 20 — HMRS-*)

- [ ] **HMRS-01**: `X-Session-Id` header opt-in — `CorrelationMiddleware` reads header (already specified in SES-04); session_id propagates through SES-05 → sticky escalation
- [ ] **HMRS-02**: Fallback fingerprint session key — when `X-Session-Id` header absent AND `Session.FingerprintEnabled=true` (config; default `false`), derive session key from `RemoteIpAddress + User-Agent` SHA-256 prefix (16 hex); loopback single-client use case; documented limitation for reverse-proxy deployments
- [ ] **HMRS-03**: README §10 "Hermes Integration" section rewritten for v2.0 — describes selfrouting paradigm replacing ML routing, explains `X-Session-Id` header opt-in, fingerprint fallback caveats, and notes that Hermes-side `X-Session-Id` propagation is future (v2.x) work tracked as Hermes Agent PR; current v2.0 ships smart-router-side machinery only
- [ ] **HMRS-04**: CHANGELOG `[Unreleased] ### Added` v2.0 entry mentions Hermes integration (session-aware capability available; Hermes-side wiring pending) + ### Changed entry for paradigm shift

## Future Requirements

Tracked but not in current roadmap.

### Speculative Routing (selfrouting doc §17)

- **SPEC-01**: 35B starts draft generation while router evaluates complexity in parallel
- **SPEC-02**: Mid-generation cancel + switch to 122B when complexity detected
- **SPEC-03**: Streaming-compatible speculative path (cancel before first chunk shipped)

### Hermes-side X-Session-Id propagation (v2.x)

- **HMRS-FUTURE-01**: Hermes Agent custom provider plugin extended to send `X-Session-Id` header (Hermes session.id propagated downward)
- **HMRS-FUTURE-02**: Smoke test against live Hermes Agent verifies session_id round-trip; sticky escalation continuity confirmed

### Routing.Mode runtime switch (currently restart-required)

- **MODE-FUTURE-01**: Hot-reload `Routing.Mode` config without restart (FileSystemWatcher pattern from Phase 9 canary)

### Reverse-proxy support

- **PROXY-01**: `X-Forwarded-For` header parsing for session fingerprint when smart-router runs behind nginx/Caddy

### Dedicated tiny router model (selfrouting doc §19)

- **DRT-01**: Optional Qwen2.5-3B (or smaller) router model as separate inference server — selectable via `Routing.Mode="dedicated_router"`; current v2.0 uses 35B self-route only

## Out of Scope

Explicitly excluded.

| Feature | Reason |
|---------|--------|
| Full Phase 12 Heuristic.fs revival (prompt length / complexity score / message count) | Operator decision 2026-05-11: Hard Rules keyword-only; richer heuristics burned 122B-vs-35B routing in v0.5 |
| ML QualityClassifier (original Phase 17 in v1.x roadmap) | Distillation endgame deferred — v2.0 paradigm shift makes ML routing dormant; classifier on dormant signal makes no sense |
| Hermes Agent code modification | v2.0 ships smart-router side only; Hermes PR is post-v2.0 (HMRS-FUTURE-01) |
| Speculative routing in v2.0 | selfrouting doc §17 marked "another advanced option" — defer to SPEC-* requirements |
| Dedicated 7B router server | Operator decision 2026-05-11: 35B self-route avoids extra server overhead; doc §19 future upgrade path |
| Full operator UAT smoke test against live Hermes | Operator-driven; not part of v2.0 deliverable; X-Session-Id round-trip needs Hermes PR first |
| Removing/deleting ML code | Retained in repo for `Routing.Mode="ml"` re-activation; Phase 12 heuristic retirement pattern (file retained, not in routing path) |

## Traceability

Filled by roadmapper during ROADMAP.md creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| MODE-01 | Phase 17 | Complete |
| MODE-02 | Phase 17 | Complete |
| MODE-03 | Phase 17 | Complete |
| MODE-04 | Phase 17 | Complete |
| HR-01 | Phase 17 | Complete |
| HR-02 | Phase 17 | Complete |
| HR-03 | Phase 17 | Complete |
| HR-04 | Phase 17 | Complete |
| HR-05 | Phase 17 | Complete |
| HR-06 | Phase 17 | Complete |
| SES-01 | Phase 18 | Complete |
| SES-02 | Phase 18 | Complete |
| SES-03 | Phase 18 | Complete |
| SES-04 | Phase 18 | Complete |
| SES-05 | Phase 18 | Complete |
| SES-06 | Phase 18 | Complete |
| SES-07 | Phase 18 | Complete |
| SES-08 | Phase 18 | Complete |
| SES-09 | Phase 18 | Complete |
| SR-01 | Phase 19 | Complete |
| SR-02 | Phase 19 | Complete |
| SR-03 | Phase 19 | Complete |
| SR-04 | Phase 19 | Complete |
| SR-05 | Phase 19 | Complete |
| SR-06 | Phase 19 | Complete |
| SR-07 | Phase 19 | Complete |
| SR-08 | Phase 19 | Complete |
| SR-09 | Phase 19 | Complete |
| HMRS-01 | Phase 20 | Pending |
| HMRS-02 | Phase 20 | Pending |
| HMRS-03 | Phase 20 | Pending |
| HMRS-04 | Phase 20 | Pending |

**Coverage:**
- v2.0 requirements: 32 total (4 MODE + 6 HR + 9 SES + 9 SR + 4 HMRS)
- Mapped to phases: 32 ✓
- Unmapped: 0
- Future: 7 (3 SPEC + 2 HMRS-FUTURE + 1 MODE-FUTURE + 1 PROXY + 1 DRT)

---
*Requirements defined: 2026-05-11*
*Last updated: 2026-05-11 after v2.0 milestone initialization via /gsd:new-milestone (research → requirements → roadmap pipeline; selfrouting paradigm replacing v1.x ML routing arc; Hermes Agent integration scaffolding).*
