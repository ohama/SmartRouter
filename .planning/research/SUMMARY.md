# Project Research Summary

**Project:** smart-router v2.0 — Self-Routing + Session-Aware
**Domain:** LLM routing proxy — subsequent milestone on top of shipped v1.3.0
**Researched:** 2026-05-11
**Confidence:** HIGH

## Executive Summary

smart-router v2.0 replaces the ML-classifier routing path (Phases 6–11) with a three-layer cascade: keyword Hard Rules → 35B self-classify → sticky session escalation. The 35B model already serving inference is reused for 1-token routing classification (`max_tokens=4`, `temperature=0`), eliminating the need for a separate router server or new NuGet packages. The ML code is retained but made dormant behind a `Routing.Mode = "selfrouting" | "ml"` config flag — an operator can roll back to ML routing with a config edit and restart, no rebuild required. The v1.3.0 quality fallback and 122B-as-judge (Phases 14–16) are unaffected and remain in the post-routing path.

The recommended build order is derived entirely from actual Domain.fs type dependencies: Hard Rules first (no new DI, fully testable in isolation), then Session Store (which must exist before SelfRouter can read the SessionId field that CorrelationMiddleware will populate), then SelfRouter (wires the classify call into the RoutingAlgorithmRegistration mode switch), then Hermes integration (header convention + smoke test). The single most important architecture decision is that `RouterRequest` must gain a `SessionId: string` field (added the same way `CorrelationId` was added in Phase 9), and that field must be populated before the SelfRouter closure can check the sticky escalation store.

The primary risk is cascade ordering. All four researchers converged on a canonical six-stage order (model override → task table → Hard Rules → sticky → self-classify → default), but they differed on the exact position of the sticky check relative to self-classify. The pitfalls researcher's analysis is authoritative: sticky must run at stage 4 (before self-classify at stage 5) so that an already-escalated session never burns a classify token. A secondary conflict exists on whether self-classify applies to streaming requests: the features researcher said yes (pre-dispatch, not post-dispatch), and the pitfalls researcher flagged the latency cost of a classify call before the first SSE chunk. The resolved position: Hard Rules always apply to streaming (0ms keyword scan), but self-classify is skipped for streaming requests — the sticky escalation check still applies to streaming, providing continuity without the round-trip cost.

## Key Findings

### Recommended Stack

No new NuGet packages are required for v2.0. All new functionality is implemented with the existing dependency set: `ConcurrentDictionary<K,V>` (BCL) for the session store, `IHttpClientFactory` (already registered) for the self-classify HTTP call, `Microsoft.Extensions.Http.Resilience` 10.5.0 (already in Cli.fsproj) for the retry policy on the "selfrouter" named client, and `SHA-256` (BCL, already used in ChatCompletions.fs) for prompt-hash caching. ML packages (ML.NET 5.0.0, ONNX Runtime 1.25.1) remain compiled but dormant.

The session store uses `ConcurrentDictionary<string, SessionState>` with a BackgroundService-driven TTL eviction loop (default 30 minutes, configurable via `Routing:Session:TtlMinutes`). This is the established pattern from JudgeClient.fs (Phase 16) — manual LRU + `Interlocked` counters — extended with TTL-based eviction rather than count-based eviction, because session age is the right eviction axis.

**Core technologies (v2.0 additions):**
- `ConcurrentDictionary<string, SessionState>` (BCL) — session store backing; proven in JudgeClient, no extra DI needed
- Named `"selfrouter"` HttpClient (IHttpClientFactory, existing) — isolates classify call from production inference path; 5s timeout, 1-retry (not the inference client's 300s / 3-retry profile)
- `HardRulesConfig` record + pure `applyHardRules` function (BCL, Core) — keyword scan; no IO, no DI; lives in SmartRouter.Core to enable unit testing without ASP.NET scaffolding
- `Routing.Mode` config key (appsettings.json) — `"selfrouting"` (v2.0 default) or `"ml"` (rollback); zero-rebuild mode switch
- `RouterRequest.SessionId: string` field (Domain.fs, Core) — mirrors Phase 9 `CorrelationId` addition; populated by CorrelationMiddleware from `X-Session-Id` header; empty string = stateless request, sticky skipped

**What NOT to use:**
- `QueueDispatcher` / `IUpstreamClient` for the classify call — consumes the 122B `SemaphoreSlim(1)` gate (same pitfall documented for TeacherLabeler, Phase 7)
- `IHostedService` / `PeriodicTimer` for write-time eviction on the classify cache (write-time eviction from JudgeClient is correct for the classify cache; TTL BackgroundService is correct for the session store)
- `async {}` anywhere — ARCH-02 enforces `task {}` exclusively
- `IMemoryCache` for session store — adds a background GC thread; inconsistent with the JudgeClient manual pattern already in the codebase

### Expected Features

**Must have (v2.0 launch, all P1):**
- Hard Rules keyword pre-routing (LLVM, MLIR, compiler, segfault, optimization, concurrency) — stage 0, case-insensitive `String.Contains`, applies to ALL requests including streaming; routes immediately to 122B
- 35B self-classify call — `max_tokens=4`, `temperature=0`, `stream=false` to the "selfrouter" named client; SAFE → 35B, UNSAFE or parse-failure → 122B (conservative bias mirrors Phase 7/16 patterns); **skipped for streaming requests** (latency cost before first chunk; Hard Rules + sticky still apply to streaming)
- Prompt-hash classify cache — SHA-256 of concatenated message content → bounded LRU `ConcurrentDictionary`, 5,000 entries; cache hit skips the 35B classify call; invalidated on restart only
- Sticky session escalation — `ConcurrentDictionary<session_id, SessionState>` with TTL eviction (30 min default); once a session serves 122B, all subsequent requests in that session go 122B regardless of self-classify verdict; updated after quality fallback completes (post-routing), not after initial routing decision
- `X-Session-Id` header extraction in CorrelationMiddleware — empty/absent = stateless, sticky skipped; populates `RouterRequest.SessionId`; NOT a body field (keeps session state router-internal)
- `RoutingReason` DU extensions — `HardRule`, `SelfRoute`, `StickyEscalation` new cases; `TreatWarningsAsErrors` enforces exhaustive match at compile time
- `Routing.Mode = "selfrouting"` default, `"ml"` preserved — `RoutingAlgorithmRegistration` factory branches on mode; all ML DI registrations remain unconditional
- Quality fallback (Phase 14) writes session store on 122B upgrade — sticky must reflect the actually-served model, not the initially-routed model

**Should have (v2.x, P2):**
- Hermes Agent end-to-end smoke test — depends on Hermes-side change to propagate `X-Session-Id`; manual UAT against `~/hermes-agent`
- Self-router prompt hash logged at startup — `SelfRouter: loaded prompt hash={Hash}`; included in `/stats`; audit trail for prompt drift
- `self_router_classify_latency_ms` in TraceRecord — monitor for classify call being blocked behind in-flight 35B generations

**Defer (v3+):**
- Speculative routing — enormous SSE cancellation complexity; only if latency profiling proves a problem
- Persistent session store (Redis/SQLite) — over-engineering for single-host local setup
- Dedicated 7B router model — only if 35B classification quality degrades under observed traffic

**Explicitly excluded anti-features:**
- CoT reasoning in the routing prompt — `max_tokens=4` enforces brevity; a thinking router has already failed
- Per-message classification — multiplies token cost; sticky covers continuation case
- Hard-reject on missing session ID — breaks v1.x backward compat; session ID is optional

### Architecture Approach

v2.0 adds four new components that slot cleanly into the existing hexagonal structure. `HardRules.fs` is a pure Core function (no IO, no DI), called as Stage 0 in `Routing.routeRequest`. `SessionStore.fs` is a Cli adapter implementing `ISessionStore` with triple-reg DI (concrete singleton + interface alias + BackgroundService leg for TTL eviction). `SelfRouter.fs` is a Cli adapter implementing `ISelfRouter`, mirroring `JudgeClient.fs` exactly — named HttpClient, LRU cache, fail-open to UNSAFE on timeout/parse failure. Session ID flows through `CorrelationMiddleware` → `HttpContext.Items["SessionId"]` → `RouterRequest.SessionId` (new field, not a body field).

**Major components (v2.0 new or extended):**
1. `HardRules.fs` (Core) — pure keyword scan; `applyHardRules : RouterRequest -> RoutingDecision option`; Stage 0 in `Routing.routeRequest`
2. `SessionStore.fs` (Cli Adapter) — `ConcurrentDictionary<string, SessionState>`; triple-reg (singleton + ISessionStore + BackgroundService TTL); `AddOrUpdate` with 122B-wins merge for concurrent-write safety
3. `SelfRouter.fs` (Cli Adapter) — `ISelfRouter.ClassifyAsync`; named "selfrouter" HttpClient; SHA-256 prompt-hash LRU cache (5,000 entries); 5s timeout, 1-retry; mirrors JudgeClient architecture exactly
4. `CorrelationMiddleware.fs` (extended) — reads `X-Session-Id` header → `ctx.Items[SessionIdKey]`; empty/absent = stateless
5. `RouterRequest` (Domain.fs, extended) — gains `SessionId: string` field; populated in `mapWireToRequest` from `ctx.Items`; parallel to `CorrelationId`
6. `RoutingAlgorithmRegistration` (CompositionRoot, extended) — `Routing.Mode` branch: `"selfrouting"` arm creates `makeSelfRoutingAlgorithm` closure; `"ml"` arm unchanged

**Named HttpClient additions (v2.0):**

| Client | Target | Timeout | Retry | Purpose |
|--------|--------|---------|-------|---------|
| selfrouter | 35B port 8000 | 5s | 1x 200ms | Routing self-classify |

(all v1.x named clients unchanged: upstream35b, upstream122b, upstream35b-stream, upstream122b-stream, health-probe, teacher, judge)

### Critical Pitfalls

1. **Cascade ordering is load-bearing** — Canonical order: model override (1) → task table (2) → Hard Rules (3) → sticky (4) → self-classify (5) → default 35B (6). Sticky at position 4 (before self-classify at 5) avoids burning a classify token on an already-escalated session. Hard Rules at position 3 (before sticky at 4) ensures a keyword-match forces 122B regardless of session state. This order is non-negotiable.

2. **Self-classify must NOT route through QueueDispatcher** — Use the named "selfrouter" HttpClient posting directly to 35B. Routing through `QueueDispatcher` consumes the `SemaphoreSlim(1)` 122B gate and starves real inference traffic. Same enforcement pattern as TeacherLabeler (Phase 7) and JudgeClient (Phase 16).

3. **Absent `X-Session-Id` must NOT create a session store entry** — Empty/missing header = stateless request: skip sticky read, skip sticky write. If empty string were used as a session key, all v1.x clients would share one sticky bucket, causing the first 122B routing to permanently escalate every sessionless request to 122B.

4. **Session store must be updated after quality fallback completes** — Call `SessionStore.Update(sessionId, finalDecision.Target)` after the Phase 14 quality fallback / judge cascade resolves, using `finalDecision.Target`. Updating before fallback records the wrong model and breaks sticky correctness for continued debugging sessions.

5. **Dormant ML code drift** — New `RoutingReason` DU cases are F#-compiler-enforced via exhaustive match (`TreatWarningsAsErrors`). The real risk is runtime: new `RoutingConfig` fields in v2.x that the ML adapter handles incorrectly without an integration test. Add `MlDormantTests.fs` that exercises `Routing.Mode = "ml"` in CI.

6. **Self-classify skipped for streaming (resolved conflict)** — Hard Rules (0ms) and sticky escalation apply to streaming. Self-classify round-trip is skipped for `stream=true` to avoid classify latency before the first SSE chunk. This must be an explicit `if req.Stream then ... else ...` branch in the Phase 19 implementation.

## Implications for Roadmap

Based on research, suggested phase structure:

### Phase 17: Hard Rules (Stage 0 Keyword Pre-Routing)

**Rationale:** Fully independent — no new DI, no new HttpClient, no new field in `RouterRequest`. Only changes: `HardRules.fs` (new Core pure function), `Domain.fs` (three new `RoutingReason` DU cases that Phases 18–19 will also need), `Routing.fs` (Stage 0 call), tests. All existing tests pass unchanged because `applyHardRules` returns `None` for non-matching requests. Ships in isolation and locks the cascade ordering spec in code before later phases depend on it.

**Delivers:** Stage 0 keyword pre-routing; new `RoutingReason` DU cases; DecisionLog schema_version bump (first new DU case); cascade ordering spec as code.

**Addresses:** Hard Rules table-stakes feature; establishes cascade ordering that all subsequent phases reference.

**Avoids:** Cascade ordering bugs (Pitfall 8) — written once in `Routing.routeRequest`, inherited by all later phases.

**Research flag:** Standard patterns. No `/gsd:research-phase` needed.

---

### Phase 18: Session Store + SessionId Wiring

**Rationale:** Must precede SelfRouter (Phase 19). `RouterRequest.SessionId` must exist as a typed field before the `makeSelfRoutingAlgorithm` closure can read it. The Stack/Features researchers proposed SelfRouter first; the Architecture researcher's analysis of the actual `RouterRequest` record is authoritative — the field dependency is a hard compile-order requirement, not a preference. Session Store infrastructure can ship and be tested independently (sticky does nothing until Phase 19 writes 122B entries, but the threading and null-session guard can be verified now).

**Delivers:** `SessionStore.fs` (ISessionStore + triple-reg DI with BackgroundService TTL); `RouterRequest.SessionId: string` field; CorrelationMiddleware extension (X-Session-Id header → Items → mapWireToRequest); ChatCompletions Point B update (sessionStore.Update after quality fallback); null-session guard; `/stats` session_store_entry_count.

**Addresses:** Sticky session escalation infrastructure; Hermes backward-compat null-session guard; session unbounded growth (TTL BackgroundService, 30 min default).

**Avoids:** Race condition Pitfall 4 (AddOrUpdate with 122B-wins merge); null-session Pitfall 7; sticky-never-resets Pitfall 9 (TTL); session update timing Anti-Pattern 4.

**Research flag:** Standard patterns — proven by JudgeClient LRU + triple-reg DI. No `/gsd:research-phase` needed.

---

### Phase 19: SelfRouter (35B Self-Classify, Stage 3)

**Rationale:** Depends on Phase 18 (`RouterRequest.SessionId` field). Adds the "selfrouter" named HttpClient, `SelfRouter.fs` adapter, `ISelfRouter` port, `RoutingAlgorithmRegistration` mode switch, and `prompts/selfrouter-prompt.md`. Sticky escalation becomes active in this phase — the algorithm closure reads `SessionStore.TryGet` at stage 4 (sticky) before invoking `ISelfRouter.ClassifyAsync` at stage 5.

**Delivers:** Full v2.0 selfrouting path end-to-end; `Routing.Mode` config switch (default "selfrouting", "ml" preserved); selfrouter-prompt.md in git; prompt hash logged at startup; `self_router_classify_latency_ms` in TraceRecord; streaming branch explicitly skips self-classify; `MlDormantTests.fs` integration test for ML path CI coverage.

**Addresses:** Primary routing decision (P1); prompt-hash cache (P1); ML dormancy config pattern; DecisionLog `routing_algorithm = "selfrouting"` JSONL field.

**Avoids:** QueueDispatcher bypass (Anti-Pattern 1 — named client, not IUpstreamClient); format drift (Pitfall 3 — substring Contains, UNSAFE wins on ambiguity); overconfidence on continuation prompts (Pitfall 1 — continuation keywords in UNSAFE examples block); ML dormant drift (Pitfall 11 — dormant integration test).

**Research flag:** Moderate complexity (HTTP parsing, LRU cache, mode switch wiring) but JudgeClient is the direct template. No `/gsd:research-phase` needed; implementation plan must explicitly call out streaming self-classify skip.

---

### Phase 20: Hermes Integration + Smoke Test

**Rationale:** All infrastructure from Phases 17–19 must be complete. Phase 20 validates end-to-end: confirms session_id header propagation path, runs smoke test against `~/hermes-agent`, and updates README §5/§7/§9.1.

**Important limitation:** The current Hermes Agent (`~/hermes-agent`) does NOT send `X-Session-Id` — confirmed by Stack researcher reading `plugins/model-providers/custom/__init__.py` directly. Phase 20 ships IP+User-Agent fingerprint session key as interim fallback for local loopback deployment, with the `resolveSessionKey` helper preferring the explicit header when present (future-proofing for a Hermes PR). A Hermes Agent PR is tracked as post-v2.0 work — it is NOT a v2.0 blocker.

**Delivers:** `resolveSessionKey` helper (X-Session-Id preferred, IP+UA fingerprint fallback); smoke test against `~/hermes-agent`; README §5 (routing pipeline + in-memory session semantics), §7 (new config keys: `Routing:Mode`, `Routing:SelfRouter:*`, `Routing:Session:*`), §9.1 (DecisionLog schema); Hermes PR tracked as future work.

**Addresses:** Hermes integration (P2); operator documentation for restart-clears-sessions behavior; backward-compat with v1.x Hermes clients.

**Avoids:** Anti-pattern of forwarding session_id to upstream in classify request body; null-session Pitfall 7.

**Research flag:** Hermes wire format confirmed (no X-Session-Id currently). The IP+UA fingerprint strategy should be validated against actual Hermes session lifecycle before committing. Consider whether to defer sticky-for-Hermes until the Hermes PR lands and ship Phase 20 as documentation + smoke test only.

---

### Phase Ordering Rationale

- **17 → 18 → 19 → 20** is driven by the `RouterRequest.SessionId` field dependency. SelfRouter's algorithm closure reads `req.SessionId` — this field must exist in the record before Phase 19 can compile cleanly. Phase 18 adds the field.
- Hard Rules (17) ships first because it is fully independent, provides immediate safety value, and locks the cascade ordering spec in code.
- The Stack and Features researchers proposed SelfRouter before SessionStore. The Architecture researcher's direct analysis of Domain.fs compile dependencies is authoritative. **Phase 18 (SessionStore) precedes Phase 19 (SelfRouter).**
- Phase 20 (Hermes) is last because it validates the full stack end-to-end and depends on all three previous phases being complete.

### Research Flags

Phases with standard patterns (no `/gsd:research-phase` needed):
- **Phase 17 (Hard Rules):** Pure Core function, no external APIs, no new technology.
- **Phase 18 (Session Store):** Proven by JudgeClient LRU cache + existing triple-reg DI registrations.
- **Phase 19 (SelfRouter):** JudgeClient is the direct implementation template; same HTTP call / LRU cache / fail-open pattern.

Phases needing planning-time validation:
- **Phase 20 (Hermes Integration):** IP+UA fingerprint session key strategy should be reviewed against actual Hermes session lifecycle. Consider whether to ship Phase 20 as infrastructure + documentation only (deferring sticky-for-Hermes until the Hermes PR lands).

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | Derived from direct codebase reads (JudgeClient.fs, TeacherLabeler.fs, CompositionRoot.fs, STATE.md). No new packages required. |
| Features | HIGH | Derived from primary design docs (35b-selfrouting.md, 35b-selfrouting-prompt.md) + locked decisions in PROJECT.md/STATE.md. Streaming/self-classify conflict resolved. |
| Architecture | HIGH | Derived from actual source files (ChatCompletions.fs, Domain.fs, Routing.fs, Ports.fs, Cli.fsproj compile order). Implementation patterns are direct clones of Phase 16 JudgeClient. |
| Pitfalls | HIGH | All critical pitfalls grounded in v1.x codebase incidents (Phase 7 pitfall 5, Phase 16 JudgeClient TOCTOU, Phase 14 streaming skip) + design doc §7,10,16. |

**Overall confidence:** HIGH

### Gaps to Address

- **Hermes session key strategy:** The IP+UA fingerprint is an interim workaround. Phase 20 planning should decide: ship fingerprint-based session identity, or defer sticky-for-Hermes until the Hermes PR lands and ship Phase 20 as documentation + smoke test only.

- **Streaming + self-classify resolved:** Self-classify is SKIPPED for `stream=true` requests. Hard Rules and sticky escalation still apply. This must be an explicit branch in the Phase 19 implementation plan (`if req.Stream then skip self-classify`).

- **ML dormant test scope:** `MlDormantTests.fs` should be added in Phase 19 (alongside the mode switch wiring) to prevent dormant ML code drift across v2.x phases. The Phase 19 plan should include this test.

- **DecisionLog schema_version bump:** First new `RoutingReason` case ships in Phase 17. Confirm the current schema_version value in Domain.fs / DecisionLogger.fs and increment it in Phase 17. README §9.1 must be updated in Phase 17, not deferred to Phase 20.

- **Hard Rules configurability:** Stack researcher recommends `appsettings.json` array; Architecture researcher recommends hardcoding. Resolved: hardcode in `HardRules.fs` for v2.0 (safety mechanism should not be accidentally misconfigured by operators). Document the source-edit requirement in README §5.

## Sources

### Primary (HIGH confidence — direct codebase reads)
- `src/SmartRouter.Cli/Adapters/JudgeClient.fs` — LRU cache pattern, named HttpClient, fail-open verdicts, parse-failure handling (SelfRouter implementation template)
- `src/SmartRouter.Cli/Adapters/TeacherLabeler.fs` — QueueDispatcher bypass enforcement (named client, not IUpstreamClient)
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — existing cascade structure; Point B placement for session store update; streaming branch
- `src/SmartRouter.Cli/CompositionRoot.fs` — DI registration patterns, triple-reg, RoutingAlgorithmRegistration factory
- `src/SmartRouter.Core/Domain.fs` — RouterRequest record, RoutingReason DU, exhaustive match enforcement via TreatWarningsAsErrors
- `src/SmartRouter.Core/Routing.fs` — routeRequest pipeline stages (current v1.x baseline)
- `src/SmartRouter.Core/Ports.fs` — IUpstreamClient, IHealthProbe port patterns
- `.planning/STATE.md` — locked decisions: selfrouting pivot, Hard Rules scope, 35B self-route, Routing.Mode flag
- `.planning/docs/35b-selfrouting.md` — design rationale, §6-7 hard rules, §10 router-must-not-think, §16 sticky escalation, §17 speculative routing
- `.planning/docs/35b-selfrouting-prompt.md` — SAFE-for-35B framing, continuation-aware UNSAFE examples, max_tokens recommendation

### Primary (HIGH confidence — external source reads)
- `https://raw.githubusercontent.com/NousResearch/hermes-agent/main/plugins/model-providers/custom/__init__.py` — confirmed: no X-Session-Id header, no session_id body field; extra_body carries only `options.num_ctx` + `think=False`
- `https://raw.githubusercontent.com/NousResearch/hermes-agent/main/run_agent.py` — confirmed: self.session_id is local-only trajectory logging; stream=True default; no session propagation to provider endpoints

### Secondary (MEDIUM confidence — phase post-mortems)
- `.planning/milestones/v1.3-phases/16-122b-as-judge-for-borderline-cases/16-RESEARCH.md` — named HttpClient pitfalls, LRU cache TOCTOU, judge timeout sizing
- `.planning/milestones/v1.3-phases/16-122b-as-judge-for-borderline-cases/16-SUMMARY.md` — AddHttpClient 2-arg form silently drops BaseAddress in F#; Expecto rootTests explicit list
- `.planning/milestones/v1.3-phases/14-quality-fallback-and-trace/14-CONTEXT.md` — streaming branch intentionally skipped for quality fallback (confirms pre-routing vs post-routing distinction)

---
*Research completed: 2026-05-11*
*Ready for roadmap: yes*
