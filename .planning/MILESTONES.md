# Project Milestones: smart-router

## v2.1 Hermes-less Session Tiering (Shipped: 2026-05-12)

**Delivered:** Replace v2.0's network-level IP+UA fingerprint (HMRS-02) with a three-tier extraction cascade — `X-Session-Id` header (unchanged) → system-prompt regex parse of Hermes' built-in `--pass-session-id` line → SHA-256 content fingerprint of conversation prefix — wired into `ChatCompletions.fs resolveSessionCascade`. The fingerprint code path (`FingerprintEnabled` config, `SHA-256(RemoteIp+UA)` middleware block, `HermesFingerprintTests.fs` 8 tests, README §10 PROXY-01 callout) is entirely deleted. Both `Routing.Mode = "selfrouting"` and `"ml"` use the new tiers, verified by TC-7 executable DI assertion (Phase 24 gap closure).

**Phases completed:** 21-24 (4 phases; 7 plans total — including Phase 24 single-plan gap closure)

**Key accomplishments:**

- **Three-tier session-key cascade** — `HermesSessionExtract.extractFromSystemPrompt` adapter (BCL `System.Text.RegularExpressions`; module-level pre-compiled `Regex` with `RegexOptions.Multiline`; pattern `^Session ID:[ \t]*(\S+)` — `[ \t]*` chosen over `\s*` to prevent cross-line match) + `ContentFingerprint.compute` helper (BCL `SHA256.Create()` per call for thread safety; `truncate(s) = s.[..3999]`; lowercase 16-hex via `Array.map (sprintf "%02x")`); both in `SmartRouter.Cli.Adapters` (ARCH-01 preserved); `resolveSessionCascade` in `ChatCompletions.fs:201` placed between `mapWireToRequest` and `routeRequest` per TIER-03
- **HMRS-02 deletion (breaking)** — `Routing.Session.FingerprintEnabled` config key removed; `CorrelationMiddleware.fs` 71 lines net (SHA-256 block gone, `fingerprintEnabled: bool` parameter removed); `HermesFingerprintTests.fs` (FP-01..FP-08; 130 LOC) deleted entirely; `archive/v2.0-network-fingerprint` branch + `v2.0-network-fingerprint` annotated tag both at `d4797e7` preserve pre-deletion snapshot per MIG-06; commit order reversed from REQ numbering (MIG-06 → MIG-03 → MIG-02 → MIG-01) so every intermediate state builds
- **Observability counters** — three new flat snake_case Int64 fields in `/stats`: `session_extraction_source_header`, `session_extraction_source_sysprompt`, `session_extraction_source_content`; `SessionCascadeStats` adapter (`Interlocked.Increment` writers + `Volatile.Read` accessors) DI-registered unconditionally in BOTH `configureRequestPipeline` and `configureWithoutMl` so cascade fires in either `Routing.Mode`
- **Documentation (Phase 23)** — README §10 fully rewritten for v2.1 paradigm with all four `--pass-session-id` operator enablement options (CLI arg, shell alias, env var, wrapper script); §7 `FingerprintEnabled` row removed (pulled forward to Plan 22-03 per CLAUDE.md README-sync rule); §8 three new counter rows added (JSON example + description table + jq monitoring snippet); §9.1 DecisionLog confirmed schema-unchanged (cosmetic "Phase 17–19" → "Phase 17–22" bump applied); zero residual references to `FingerprintEnabled` / `PROXY-01` / `RemoteIp` / `HMRS-FUTURE-01` / `network fingerprint`
- **TC-7 ml-mode DI integration test (Phase 24 gap closure)** — `SessionKeyCascadeTests.fs:264-281` constructs `ServiceCollection` with `Routing:Mode=ml` (omits `Routing:ML` section so `mlOpts=null` at `CompositionRoot.fs:342` skips ML bootstrap and avoids ONNX dependency in CI); asserts `GetRequiredService<ISessionCascadeStats>()` returns non-null via `Expect.isNotNull (box stats)` (`box` required because F# interfaces are non-nullable; mirrors `MLRoutingTests.fs:146` precedent); closes TD-1 from v2.1-MILESTONE-AUDIT.md (upgrades TIER-04 evidence from structural inference to executable assertion)
- **SessionKeyCascadeTests.fs integration test suite** — 7 testCases (TC-1 header-wins / TC-2 sysprompt fallback / TC-3 content fingerprint / TC-4 determinism / TC-5 sticky-through-Tier-2 / TC-6 counter increments / TC-7 ml-mode DI resolution); proves all six E2E flows traceable end-to-end without breaks
- **Schema + architecture invariants preserved** — `DecisionLog schema_version=1` unchanged (no new `routing_reason` values; resolved session_id flows through existing SES-04 channel); ARCH-01 preserved (both new adapters in Cli, not Core); ARCH-02 preserved (synchronous pure functions; `scripts/check-no-async.sh` continues to pass); `TreatWarningsAsErrors=true` build clean

**Stats:**

- 4 phases shipped, 7 plans total (2 + 3 + 1 + 1)
- 34 phase commits across ~4.5 hours (2026-05-12 10:51 → 2026-05-12 15:15 KST)
- 50 files changed (+9,891 / -315 since `milestone-v2.0`)
- 187 tests passed + 18 ignored + 0 failed (was 175+18 at v2.0; +12 net new tests: +7 HSP + +7 CFP + +6 SessionKeyCascadeTests + 1 TC-7 − 8 HermesFingerprintTests − 1 unrelated drift)
- ~17,800 LOC F# (src + tests; +~440 net from v2.0)
- Test additions: 7 HermesSessionExtractTests (HSP-04) + 7 ContentFingerprintTests (CFP-04 — split case (b) into 2 testCases) + 6 SessionKeyCascadeTests (TC-1..TC-6 Phase 22) + 1 TC-7 (Phase 24); test deletions: 8 HermesFingerprintTests

**Git range:** `docs(21): research phase domain` (`ee8e8a0`) → `docs(24): complete tier-04 ml-mode integration test phase` (`1e36409`)

**Tags:** `v2.1.0` (release commit `4ad1e52` after Phase 22), `milestone-v2.1` (final; pre-archive); `v2.0-network-fingerprint` + `archive/v2.0-network-fingerprint` branch (pre-deletion snapshot at `d4797e7`)

**Decimal phases:** None — all phases sequential (21, 22, 23, 24).

**Key decisions:**

- Adapter placement: `HermesSessionExtract` + `ContentFingerprint` in `SmartRouter.Cli.Adapters`, NOT Core (hexagonal invariant is file-placement, not BCL usage)
- HSP regex chose `[ \t]*` over `\s*` (Plan 21-01 auto-fix) — `\s` matches `\n` enabling cross-line collapse; horizontal-whitespace-only constraint required for HSP-04 case (e) malformed-line-no-value
- CFP signature: `compute : RouterRequest -> string` (no option wrapper; empty Messages yields valid hash of `"|||"`)
- TIER-03 placement: Tier 2/3 resolution post-`mapWireToRequest` in `ChatCompletions.fs` scope, NOT in `CorrelationMiddleware` itself (middleware runs pre-body-parse, cannot access `req.Messages`)
- Plan 22-02 commit order reversed from MIG REQ numbering (MIG-06 → MIG-03 → MIG-02 → MIG-01) so every intermediate state builds (HermesFingerprintTests.fs used `SessionOptions.FingerprintEnabled`; field deletion first would break test compile)
- `SessionCascadeStats` DI registration unconditional (in both `configureRequestPipeline` and `configureWithoutMl`); no NoOp pattern needed
- `archive/v2.0-network-fingerprint` annotated tag (not lightweight) per v2.0 milestone formality
- Plan 22-03 chose 6 testCases (TC-1..TC-6), not 8 as RESEARCH §10 projected — TC-7 ml-mode-dormant deferred to Phase 24 gap closure
- Plan 24-01: SC-2 (paired counter test running `resolveSessionCascade` in ml-mode) descoped per research Q11 — `resolveSessionCascade` has no mode branch; TC-1..TC-4 already exhaustively cover all four cascade branches; SC-2 would add zero coverage
- `Expect.isNotNull (box stats)` used in TC-7 — F# interfaces are non-nullable reference types; `box` lifts to `obj` for the null check (matches `MLRoutingTests.fs:146`)

**Issues deferred:**

- TD-2: `ModelsTests.fs` IEmbedder errors (v1.3 carry-over; MODELS-01..03 currently error or are suppressed; register a stub IEmbedder in DI fixture)
- TD-3: `configureServices` backwards-compat alias removal (`CompositionRoot.fs:1369`; blocked on TD-2)
- TD-4: Operator live-rig smoke acceptance — `./scripts/smoke-hermes-session.sh` against live mlx_lm.server rig (operator-manual, not a code gap)
- TD-5: `PITFALL-10` timing race in `QueueTests.fs:239-307` (pre-existing flake; `Async.Sleep 30` barrier insufficient; production logic correct; ~60% isolation failure rate; recommended fix: replace sleep with `Barrier` or `SemaphoreSlim`; predates Phase 21 — last touched in `fac58b2`)
- HMRS-FUTURE-01/02: Hermes Agent custom provider PR for X-Session-Id propagation (operator chose Hermes-less path for v2.1 instead)
- PROXY-01: X-Forwarded-For parsing — moot now that network fingerprint is deleted
- MODE-FUTURE-01: Hot-reload `Routing.Mode` without restart
- SPEC-01..03: Speculative routing (35B drafts while router evaluates)
- DRT-01: Dedicated tiny router model (Qwen2.5-3B)

**Technical debt incurred:**

- None new from v2.1 phases. TD-1 (originally non-blocking carry-over from Phase 22 verifier recommendation) closed in Phase 24.

**What's next:** Open. Candidate threads — (a) close TD-2/TD-3 (ModelsTests.fs + configureServices alias) in a v2.x maintenance window; (b) fix TD-5 PITFALL-10 timing race; (c) operator-run TD-4 smoke; (d) consider HMRS-FUTURE-01 (Hermes-side X-Session-Id PR) if upstream collaboration opportunity opens; (e) explore SPEC-01..03 speculative routing if first-chunk latency becomes a constraint.

---

## v2.0 Self-Routing + Session-Aware (Shipped: 2026-05-12)

**Delivered:** Replace v1.x ML-classifier routing path with a three-layer cascade — keyword Hard Rules (Stage 0) → 35B self-classify (1-token SAFE/UNSAFE, non-streaming only) → sticky session escalation — wiring smart-router below Hermes Agent for debugging-continuity across multi-turn sessions. ML code retained but routing-path dormant; `Routing.Mode` switch preserves one-config rollback.

**Phases completed:** 17-20 (4 phases; 12 plans total)

**Key accomplishments:**

- **Selfrouting paradigm** — `HardRules.fs` BCL-only Stage 0 keyword pre-routing (LLVM/MLIR/compiler/segfault/optimization/concurrency → 122B; Hard Rules wins over explicit override per HR-06); `SelfRouter` adapter (`"selfrouter"` HttpClient, 5s timeout, 1 retry @ 200ms) with operator-tunable `prompts/self-router-prompt.md` SAFE/UNSAFE template; safety-biased parser (ambiguous → UNSAFE)
- **Session-aware sticky escalation** — `RouterRequest.SessionId : string` Core field + `ISessionStore` `ConcurrentDictionary` with 122B-wins `AddOrUpdate` merge + `Interlocked.Increment` LRU + PeriodicTimer TTL eviction BackgroundService; `CorrelationMiddleware` reads `X-Session-Id` header; debugging continuity preserved across multi-turn even after Hermes context compression
- **Routing.Mode config switch** — `Routing.Mode = "selfrouting" | "ml"` (default `"selfrouting"`); CompositionRoot mode-branched DI; ML adapters keep DI registration in both modes so `RetrainingService` accumulates hard cases (MODE-03); `MlDormantTests.fs` skip-guarded integration test prevents ML path silent drift
- **Hermes Agent integration (smart-router side)** — `Routing.Session.FingerprintEnabled` opt-in (default `false`) derives 16-hex SHA-256(RemoteIp + "|" + UA) session key when X-Session-Id absent (loopback single-client interim); README §10 fully rewritten for v2.0 paradigm with explicit "NOT SAFE BEHIND REVERSE PROXIES" warning (PROXY-01 callout); `scripts/smoke-hermes-session.sh` operator-runnable E2E driver; Hermes-side propagation tracked as HMRS-FUTURE-01
- **Streaming-skip pattern** — SR-06 explicit `if req.Stream then skip SelfRouter` mirrors Phase 14 quality-fallback streaming-skip (first-chunk latency budget cannot accommodate classify call); Hard Rules + sticky still apply to streaming
- **Prompt-hash LRU cache** — `selfrouter_cache_hits/_misses/_call_count/_skipped` flat snake_case fields in `/stats` mirror Phase 16 `judge_cache_*` pattern; ConcurrentDictionary keyed by prompt_hash with monotonic int64 access counter, bounded ~10000 entries
- **Schema invariants preserved** — `DecisionLog schema_version=1` unchanged across v2.0; new `routing_reason` values (`hard_rule`, `sticky_to_122b`, `self_route`) are additive enum values; ARCH-01 preserved (HardRules.fs is the only new Core file; everything else in `SmartRouter.Cli.Adapters`)
- **README + CHANGELOG documentation** — README §5.1/§5.6/§5.7/§7/§8/§9.1/§10 fully rewritten for v2.0 cascade and Hermes integration; CHANGELOG `[Unreleased]` promoted to `[2.0.0] - 2026-05-12` with v2.0 paradigm-shift Changed block

**Stats:**

- 4 phases shipped, 12 plans total
- 59 commits across 1 day (2026-05-11 14:04 → 2026-05-12 10:12)
- 65 files changed (+14,440 / -140)
- 175 tests passed + 18 ignored + 0 failed (was 113+16 at v1.3 baseline; +62 new tests)
- ~17,357 LOC F# total (src + tests; was ~15,000 at v1.3)
- Test additions: 16 HardRulesTests + 9 ModeSwitchTests + 8 SessionStoreTests + 5 StickyEscalationTests + 9 SelfRouterTests + 7 SelfRoutingIntegrationTests + 1 skip-guarded MlDormantTests + 8 HermesFingerprintTests

**Git range:** `feat(17-01): add HardRule DU case` → `docs(20): complete hermes-agent-integration phase`

**Tag:** `milestone-v2.0`

**Decimal phases:** None — all phases sequential.

**Key decisions:**

- Paradigm: Selfrouting primary, ML dormant. Phase 12 heuristic retirement pattern reused — code retained, not in routing path.
- Router model: 35B self-route (shared KV cache) NOT separate 7B router server.
- Heuristic scope: Hard Rules keyword-only; NOT full Heuristic.fs revival.
- Phase order: locked by Domain.fs compile dependency (`RouterRequest.SessionId` must exist before `makeSelfRoutingAlgorithm` closure reads it).
- Streaming-skip for self-classify (SR-06) — explicit comment in streaming branch.
- Hard Rules wins over explicit override (HR-06 corrected during 17-03).
- ML adapter unconditional DI registration (MODE-03) — preserves RetrainingService data accumulation.
- Hermes-side X-Session-Id propagation is future work (HMRS-FUTURE-01); v2.0 ships smart-router-side machinery + opt-in fingerprint fallback only.

**Issues deferred:**

- HMRS-FUTURE-01/02: Hermes Agent custom provider PR for X-Session-Id propagation (v2.x).
- PROXY-01: X-Forwarded-For parsing behind reverse proxy (README §10 explicit warning).
- MODE-FUTURE-01: Hot-reload Routing.Mode without restart.
- SPEC-01..03: Speculative routing (35B drafts while router evaluates).
- DRT-01: Dedicated tiny router model (Qwen2.5-3B).
- ROADMAP SC-1/SC-2 (Phase 20): Live-rig acceptance test via `./scripts/smoke-hermes-session.sh` deferred to operator manual verification.
- Phase 19 human-verification: Live mlx_lm round-trip to confirm 35B returns SAFE/UNSAFE tokens for shipped prompt template.

**Technical debt:**

- ModelsTests.fs migration to configureWithoutMl (v1.3 carry-over).
- configureServices backwards-compat alias still in place.

**What's next:** Open. Candidate threads — (a) `~/projs/smart-router-distillation/idea/hermes-session-without-modification.md` proposes complementary Tier 1 (system-prompt session_id parse via Hermes `--pass-session-id` option) + Tier 3 (content-fingerprint vs network-fingerprint) approaches that would extend Phase 20's machinery without Hermes-side PR; (b) HMRS-FUTURE-01 Hermes Agent PR; (c) v2.0 operator-acceptance gates (SC-1/SC-2 live-rig smoke).

---

## v1.3 Quality-Aware ML Routing (Shipped: 2026-05-11)

**Delivered:** F# .NET 10 OpenAI-compatible router for local Qwen 35B / 122B with ML-driven 3-stage routing, quality fallback (35B → 122B retry on bad responses), opt-in 122B-as-judge for borderline cases, self-retraining classifier, and canary deployment — all behind a single localhost:4000 gateway.

**Phases completed:** 1-16 (Phase 17 ML QualityClassifier deferred per operator pivot to v2.0 selfrouting)

**Releases:** v1.0.0 → v1.1.0 → v1.1.1 (fix #13) → v1.2.0 → v1.3.0

**Key accomplishments:**

- **Hexagonal F# core** — `SmartRouter.Core` BCL-only (zero Microsoft.ML / HttpClient / Serilog / ASP.NET Core); adapters confined to `SmartRouter.Cli`; ARCH-01 invariant preserved across 16 phases and 62 plans
- **Live ML routing** — bge-m3 int8 ONNX (1024-dim multilingual) + ML.NET LbfgsLogisticRegression; first-run bootstrap auto-generates dummy classifier; PredictionEnginePool hot-swap; ~50ms p95 inference on Mac M-series CPU
- **Closed-loop retraining** — failure detector + 122B teacher labeler (daily cost cap) + dataset writer (Channel + BackgroundService dedupe) + retraining service (PeriodicTimer + SemaphoreSlim skip-if-busy + held-out validation gate)
- **Canary deployment** — `Microsoft.FeatureManagement.AspNetCore` + `ContextualTargetingFilter` (sticky 10/90 split by correlation_id) + rolling-60s fallback rate watchdog with auto-rollback
- **Production hardening** — HealthService (10s upstream probes; ConsecutiveFailureThreshold) + graph_indexing no-fallback rule (503 instead of silent 35B reroute); launchd plist + deploy scripts; rolling Serilog file sink (50MB cap, 30-day retention)
- **Distillation arc** — quality fallback (Phase 14: 35B response fails heuristic → 122B retry); 5-dimension signal enrichment (Phase 15: finish_reason + case-insensitive + Korean length + Shannon entropy + cheap-first cascade); OPT-IN 122B-as-judge for borderline cases (Phase 16: 1-token verification + LRU cache by (prompt_hash, response_hash))
- **Observability** — structured DecisionLog JSONL (12-field schema; one row per request) + opt-in TraceLog JSONL (16 fields with bad_reason + judge_*); /stats endpoint with 21+ flat snake_case fields; correlation_id propagation across all log streams
- **Heuristic retirement** — Phase 12 removed Routing.Algorithm config + --routing-algorithm CLI flag; archive/heuristic-baseline branch + v0.5-heuristic-baseline tag preserved as historical reference

**Stats:**

- 16 phases shipped, 62 plans total
- ~15,000 LOC F# (src + tests)
- 113 tests passed + 16 ignored + 0 failed (final baseline)
- 329 commits across 4 days (2026-05-07 → 2026-05-11)
- 5 NuGet versioned releases on origin/master
- Test coverage: routing pipeline, SSE streaming, concurrency gate, ML classifier, retraining loop, canary, health/fallback, deployment, quality fallback (Phase 14-16), trace logging, /stats wire

**Git range:** `feat(01-01-SCAFFOLD)` → `feat(16-04-TESTS-AND-DOCS)`

**Tags:** `v1.0.0`, `v1.1.0`, `v1.2.0`, `v1.3.0`, `v0.5-heuristic-baseline`, `v1.3-ml-routing` (archive)

**Decimal phases:** None — all phases sequential.

**Key decisions:**

- Hexagonal architecture with Core BCL-only (ARCH-01) — enforced via CI grep
- `task {}` exclusively (no `async {}`) — `scripts/check-no-async.sh` enforces
- Heuristic routing retired in Phase 12 — ML is sole stage-3 algorithm post-Phase 12
- bge-m3 int8 over bge-small — multilingual Korean+English mixed traffic requires it; int8 quantization saves ~580MB; <50ms p95 latency
- Quality fallback as separate post-routing layer (Phase 14) — not folded into routing classifier
- Phase 16 judge OPT-IN (`Routing.Judge.Enabled=false` default) — adds 122B network call on every borderline case; operator opts in after evaluating Phase 15 `quality_check_hits_*` counters
- Issue #13 (`isBadResponse` on raw envelope, not content) — v1.1.1 patch added `extractAssistantText` helper; degrades safely on malformed JSON

**Issues deferred:**

- Phase 17 ML QualityClassifier (distillation endgame) — replaced by v2.0 selfrouting paradigm
- ROADMAP SC#1/SC#2/SC#3 (host launchd UAT) — deferred to operator manual verification (require live macOS host)
- Streaming branch quality fallback — INTENTIONALLY SKIPPED (chunks already shipped; cannot retract)

**What's next:** v2.0 milestone "Self-Routing + Session-Aware" — Hard Rules + 35B self-classify + sticky escalation + Hermes Agent integration per `.planning/docs/35b-selfrouting.md`. ML code retained but dormant (mirroring Phase 12 heuristic retirement pattern).

---
