# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-05-08)
See: .planning/REQUIREMENTS.md (v2.0 requirements; 32 reqs across MODE/HR/SES/SR/HMRS categories)
See: .planning/ROADMAP.md (v2.0 milestone phases 17-20; created 2026-05-11)

**Core value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.

**Current focus:** v2.0 "Self-Routing + Session-Aware" milestone — Phase 19 VERIFIED + CLOSED 2026-05-12. Phase 20 (Hermes Agent Integration + Documentation) is the final v2.0 phase.

## Current Position

Milestone: v2.0 Self-Routing + Session-Aware — IN PROGRESS 2026-05-12 (10 of 12 plans complete)
Phase: 20 — Hermes Agent Integration + Documentation — Not started
Plan: —
Status: Phase 19 verified by gsd-verifier (5/5 ROADMAP must-haves, 9/9 SR-* requirements). 167 passed + 18 ignored + 0 failed. One non-blocking human-verification item recorded in 19-VERIFICATION.md: live mlx_lm.server validation that 35B actually returns SAFE/UNSAFE tokens for the shipped prompt template — prompt-quality validation, not a code correctness gap. Ready to plan Phase 20.
Last activity: 2026-05-12 — Phase 19 closed; verifier passed; ROADMAP/STATE/REQUIREMENTS updated for milestone progression.

**v2.0 phase summary (12 plans across 4 phases):**

| Phase | Goal | Plans | Requirements |
|-------|------|-------|--------------|
| 17 | Hard Rules Layer + Routing.Mode switch (foundation for selfrouting cascade) | 3 | 10 (MODE-01..04 + HR-01..06) |
| 18 | Session Store + Sticky Escalation (`RouterRequest.SessionId` field + TTL eviction) | 3 | 9 (SES-01..09) |
| 19 | 35B Self-Routing (SAFE/UNSAFE classify + LRU cache; streaming-skipped) | 4 | 9 (SR-01..09) |
| 20 | Hermes Agent Integration + Documentation (X-Session-Id + fingerprint fallback + README §10) | 2 | 4 (HMRS-01..04) |

**v2.0 design decisions (locked 2026-05-11 with operator; roadmap reflects):**
1. **Paradigm**: Selfrouting primary, ML dormant. ML code retained in repo but removed from request path. `Routing.Mode = "selfrouting" | "ml"` config switch preserved (Phase 17 ships the gate; Phase 19 ships `MlDormantTests.fs` to prevent drift).
2. **Router model**: 35B self-route (same 35B serves both routing classify + responses; KV cache shared; per selfrouting doc §3). NOT a separate 7B router server.
3. **Heuristic scope**: Hard Rules only (keyword list — LLVM/MLIR/compiler/segfault/optimization/concurrency per doc §6,12). NOT full Phase 12 Heuristic.fs revival.
4. **Architecture**: Hermes Agent (above) → smart-router (below). Smart-router gets session_id propagation from Hermes for sticky escalation. `~/hermes-agent` is the integration target. Hermes-side `X-Session-Id` propagation is future work (HMRS-FUTURE-01).
5. **Cascade order (Phase 17 locks in code)**: Stage 0 Hard Rules → Stage 1 explicit model override → Stage 2 explicit task table → Stage 3 sticky session → Stage 4 self-classify (non-streaming only) → Stage 5 default 35B.
6. **Streaming skip for self-classify (SR-06)**: explicit `if req.Stream then skip` matching Phase 14 quality-fallback streaming-skip pattern. Hard Rules + sticky still apply to streaming.

**Reference docs for v2.0:**
- `.planning/docs/35b-selfrouting.md` — primary design doc
- `.planning/docs/35b-selfrouting-prompt.md` — router prompt design (template for `prompts/self-router-prompt.md`)
- `.planning/docs/quality-check-improvement-options.md` — Tier 3-A (Phase 16 judge implemented) + Tier 4 (deferred = original Phase 17 ML QualityClassifier)
- `.planning/research/SUMMARY.md` — v2.0 research synthesis (HIGH confidence; phase order locked by Domain.fs compile dependency)
- Memory note `v2_selfrouting_pivot.md` — pivot rationale + locked decisions

Progress: [████████████████████████████████████████░░░░░] 60 of 60 v1.x plans (Phase 17 ML QualityClassifier deferred). v2.0: 10 of 12 plans complete (Phases 17, 18, 19 all complete; Phase 20 remaining — final v2.0 phase).

## Performance Metrics

**Velocity (v1.x final):**
- Total plans completed: 60 (16 phases shipped: 01 foundation → 16 122b-as-judge)
- Average duration: ~7-13 min/plan (varies by phase complexity)
- Total execution time: v1.3 milestone shipped over 4 days (2026-05-07 → 2026-05-11)

**v1.x by Phase summary** (archived in .planning/milestones/v1.3-ROADMAP.md):
- Phases 01-03: foundation + streaming + concurrency gate (8 plans)
- Phases 04-09: ML arc (24 plans — seam, logging, real ML, failure detection, retraining, canary)
- Phases 10-12: production hardening (10 plans — health/fallback, deployment+docs, heuristic removal)
- Phases 13-16: distillation arc (15 plans — service logging, quality fallback+trace, signal enrichment, judge)

**v2.0 baseline (post-Phase-16):**
- Tests: 113 passed + 16 ignored + 0 failed
- ARCH-01 invariant preserved across 16 phases / 60 plans
- 5 NuGet versioned releases (v1.0.0 → v1.3.0)

**v2.0 progress (post-17-03, Phase 17 complete):**
- Tests: 137 passed + 17 ignored + 0 failed (+8 ModeSwitchTests passing + 1 skip-guarded ml-mode test)
- HardRules.fs shipped: Stage 0 in routeRequest, cascade order locked (17-01)
- Routing.Mode config switch shipped: appsettings.json + CompositionRoot (17-02)
- ModeSwitchTests (9 tests): DI-integration via minimal config, cascade ordering verified (17-03)
- README §5.0/§7/§9.1 + CHANGELOG [Unreleased] + REQUIREMENTS HR-06/MODE-03 + ROADMAP SC-2/17-02 (17-03)
- Phase 17 COMPLETE: all 10 requirements (MODE-01..04 + HR-01..06) satisfied

**v2.0 progress (post-18-01):**
- Tests: 137 passed + 17 ignored + 0 failed (baseline preserved; no behavior change, only type additions)
- RouterRequest.SessionId : string field added (10th field; "" sentinel = stateless per SES-04)
- SessionState BCL-only record added to Domain.fs (LastModel + LastAccessedAt + mutable LastAccessSeq)
- RoutingReason.StickyEscalation 8th DU case added
- DecisionLogger.formatReason 8-arm exhaustive match; StickyEscalation -> "sticky_to_122b"
- 10 RouterRequest construction sites updated atomically (2 production + 8 test)
- SES-01 satisfied; SES-03 shape satisfied; SES-06 DU+formatReason satisfied (Cli adapter implementation 18-02)

**v2.0 progress (post-18-02):**
- Tests: 137 passed + 17 ignored + 0 failed (baseline preserved; sticky behavior dormant without X-Session-Id)
- SessionStore.fs: ConcurrentDictionary + 122B-wins merge (AddOrUpdate updateValueFactory) + LRU eviction stub + TTL-aware TryGet; BackgroundService ExecuteAsync stub (18-03 ships eviction loop)
- CorrelationMiddleware: SessionIdKey + SessionIdHeader literals; X-Session-Id → ctx.Items[SessionIdKey]
- ChatCompletions: mapWireToRequest(correlationId, sessionId, wire); handler extracts sessionId from ctx.Items; Point B writes in both streaming (normal exit) and non-streaming (post-finalDecision)
- CompositionRoot: SessionStore triple-reg (concrete + ISessionStore) in BOTH configureRequestPipeline AND configureWithoutMl; Phase 17 stub → sticky-or-default closure consuming ISessionStore
- appsettings.json: Routing.Session.{TtlMinutes=30, MaxEntries=10000}
- SES-02/SES-03/SES-04/SES-05/SES-07/SES-09 satisfied; sticky cascade operational (SES-05 Stage 3)
- SES-08 (TTL eviction integration tests) + SES-06 (README §5 sticky doc) deferred to 18-03 by design

**v2.0 progress (post-18-03, Phase 18 COMPLETE):**
- Tests: 150 passed + 17 ignored + 0 failed (+8 SessionStoreTests + 5 StickyEscalationTests)
- SessionStore.ExecuteAsync: PeriodicTimer 5-min TTL eviction loop with OCE shutdown + log-and-continue
- AddHostedService<SessionStore> registered in both configureRequestPipeline + configureWithoutMl (triple-reg complete)
- SessionStoreTests.fs: empty-sessionId no-op, 122B-wins concurrent (Task.WhenAll), 122B-wins sequential, TTL eviction (TryUpdate mutation), LRU cap
- StickyEscalationTests.fs (testSequenced): first-122B-then-sticky, stateless-no-header, quality-fallback-writes-session, Hard-Rules-beats-sticky-35B, sticky-persists
- README §5.1 updated + §5.6 sticky session escalation + §7 Routing.Session table + §9.1 sticky_to_122b
- CHANGELOG [Unreleased] Phase 18 Added + Notes blocks
- Phase 18 COMPLETE: all 9 SES-* requirements satisfied; all 5 ROADMAP Success Criteria covered

**v2.0 progress (post-19-01):**
- Tests: 150 passed + 17 ignored + 0 failed (baseline preserved; adapter inert — no request path wiring yet)
- RoutingReason.SelfRoute: 9th DU case added to Domain.fs (atomic pair with formatReason 9th arm)
- formatReason: `| SelfRoute -> "self_route"` — schema_version=1 unchanged (additive enum value)
- SelfRouter.fs: 339-line adapter — ISelfRouter (ClassifyAsync + PromptVersion) + ISelfRouterStats + LRU cache
- Safety-biased parser: `hasUnsafe` checked BEFORE `hasSafe` (SAFE ⊂ UNSAFE — load-bearing order, PITFALL #1)
- PromptVersion: SHA-256 hex8 prefix of prompt file at construction time (`"selfrouting-84e243ae"` for initial prompt)
- JsonFSharpConverter in buildBody for anonymous record serialization (PITFALL #2 — preserved from JudgeClient pattern)
- Cache key: single string (promptHash) vs JudgeClient tuple (no response to hash in self-classify)
- prompts/self-router-prompt.md: SAFE/UNSAFE classifier prompt; retry/fix/continue workflows in UNSAFE section (PITFALL #6)
- SmartRouter.Cli.fsproj: SelfRouter.fs registered after DecisionLogger.fs (compile order)
- SR-01/SR-02/SR-03/SR-04/SR-07 satisfied; DI registration (SR-01 named client) + cascade wiring (SR-06) deferred to 19-02/19-03 by design

**v2.0 progress (post-19-02):**
- Tests: 150 passed + 17 ignored + 0 failed (baseline preserved; SelfRouter registered but not yet called)
- Named "selfrouter" HttpClient registered: BaseAddress=Upstreams.Model35B, Timeout=5s, AddResilienceHandler 1 retry @ 200ms constant (SR-01)
- SelfRouter triple-registration in "selfrouting" mode arm: concrete + ISelfRouter alias + ISelfRouterStats alias (same instance — shared LRU cache + counters)
- ISelfRouterStats NoOp in "ml" mode arm: /stats returns selfrouter_* = 0, not 500 (PITFALL #7)
- ISelfRouterStats NoOp in configureWithoutMl: --retrain offline DI graph integrity preserved
- Stats.fs: 4 new StatsWire fields (selfrouter_cache_hits/_misses/_call_count/_skipped) + null-safe ISelfRouterStats resolve (SR-05)
- appsettings.json: Routing.SelfRouter block added (Endpoint="", PromptPath, TimeoutSeconds=5, MaxCacheEntries=10000)
- Stale CompositionRoot.fs comments refreshed: "Phase 19 will replace... closure" → "Phase 19 self-classify lives in ChatCompletions.fs"
- Auto-fixed: JudgeOptions type annotation on normalized binding (SelfRouterOptions field-name collision with JudgeOptions)
- SR-01 (named client), SR-04 (cache stats exposed), SR-05 (4 snake_case /stats fields) satisfied
- SR-06 (cascade wiring in ChatCompletions.fs) deferred to 19-03 by design

**v2.0 progress (post-19-03):**
- Tests: 167 passed + 18 ignored + 0 failed (+17 new: SelfRouterTests 9 + SelfRoutingIntegrationTests 7 + MlDormantTests 1 skip-guarded)
- ISelfRouter.ClassifyAsync wired into ChatCompletions.fs non-streaming branch: `let! decision = task { if decision.Reason = Default then ... }` after Phase 10 health rebind
- SR-06 streaming-skip: explicit comment block in streaming branch; structural zero ClassifyAsync calls verified
- RouteSafe → { Target=Qwen35B, Priority=Low, Reason=SelfRoute, ModelVersion=selfRouter.PromptVersion }
- RouteUnsafe → { Target=Qwen122B, Priority=High, Reason=SelfRoute, ModelVersion=selfRouter.PromptVersion }
- RouteSkipped/RouteFailed → fail-open (decision unchanged, Default=35B)
- ISelfRouter null (Routing.Mode="ml") → GetService returns null; isNull (box selfRouter) guard fail-opens
- SelfRouterTests.fs: 9 unit tests — parser safety bias (SAFE⊂UNSAFE), cache hit/miss, RouteFailed not cached, RouteSkipped, PromptVersion
- SelfRoutingIntegrationTests.fs: 7 DI integration tests — SC-1/2/3/4 + fail-open + singleton identity
- MlDormantTests.fs: 1 test (skip-guarded) — Routing.Mode="ml" DI boots cleanly post-Phase-19
- SR-06, SR-08, SR-09 satisfied; ROADMAP SC-1/2/3/4/5 all testable from dotnet test

**v2.0 progress (post-19-04, Phase 19 COMPLETE):**
- Tests: 167 passed + 18 ignored + 0 failed (docs-only wave; baseline preserved)
- README §5.7 NEW: Stage 4 self-classify mechanics (35B → SAFE/UNSAFE classify, streaming-skip, LRU cache, fail-open, operator tuning, rollback via Routing.Mode="ml")
- README §5.1 updated: "Four-stage decision" → "Six-stage decision" (Phases 17-19 cascade); §5.6 stale "Phase 19 will insert" forward-reference replaced; §2 stale Phase 17 stub description updated
- README §7: Routing.SelfRouter.{Endpoint, PromptPath, TimeoutSeconds=5, MaxCacheEntries=10000} config block added
- README §8: Phase 19 selfrouter_* counter table (4 fields with semantics matching Stats.fs exactly)
- README §9.1: routing_reason=self_route + routing_algorithm=selfrouting + model_version=selfrouting-{hex8} documented; schema_version=1 reaffirmed
- CHANGELOG [Unreleased] Phase 19 Added + Notes blocks appended after Phase 18
- All source-of-truth cross-checks passed: 4 stats field names, 4 config key names, 1 routing_reason enum value — all match source files
- Phase 19 COMPLETE: all 9 SR-* requirements satisfied (SR-01..09); all 5 ROADMAP SCs verifiable by operators

**v2.0 summary (phases 17-19 complete; phase 20 next):**
- Phase 17: Hard Rules Layer + Routing.Mode switch (10 reqs; 3 plans; 8 tests added; v1.x ML dormant)
- Phase 18: Session Store + Sticky Escalation (9 reqs; 3 plans; 13 tests added; X-Session-Id opt-in)
- Phase 19: 35B Self-Classify Stage 4 (9 reqs; 4 plans; 17 tests added; streaming-skip; LRU cache; operators can tune via prompts/self-router-prompt.md)
- Phase 20: Hermes Agent Integration + Documentation — NEXT (4 reqs; 2 plans; README §10 update + fingerprint fallback)

*Velocity metrics will be updated as v2.0 plans complete (anticipated 2-5 days for 12 plans based on v1.x cadence)*

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
v2.0 milestone-level decisions (locked 2026-05-11):

- **v2.0 paradigm pivot**: ML routing dormant, selfrouting primary. `.planning/docs/35b-selfrouting.md` is the authoritative design doc. ML code retained for future `Routing.Mode="ml"` re-activation; mirrors Phase 12 heuristic retirement pattern (code preserved, not in routing path).
- **Phase order locked by Domain.fs compile dependency**: 17 → 18 → 19 → 20. SessionStore (Phase 18) must precede SelfRouter (Phase 19) because `RouterRequest.SessionId` field must exist before `makeSelfRoutingAlgorithm` closure can read it. Research-SUMMARY.md confirms this is non-negotiable (Architecture researcher HIGH confidence over Stack/Features researchers' SelfRouter-first proposal).
- **Streaming branch intentionally skipped for self-classify**: Mirrors Phase 14 quality fallback streaming-skip (chunks already shipped; first-chunk latency budget cannot accommodate classify round-trip). Hard Rules (0ms keyword check) + sticky escalation still apply to streaming. Explicit `if req.Stream then skip SelfRouter` with code comment is required per SR-06.
- **schema_version=1 unchanged**: All v2.0 additions are additive enum values on `routing_reason` (`hard_rule`, `sticky_to_122b`, `self_route`) — no field removals, no type changes. Same for DecisionLog and TraceLog.
- **Hard Rules NOT operator-configurable**: Keyword list hardcoded in `HardRules.fs` (LLVM, MLIR, compiler, segfault, optimization, concurrency). Safety mechanism should not be misconfigurable. README §5.5 documents source-edit requirement (HR-02; resolved gap from Stack vs Architecture researcher conflict).
- **Hermes-side X-Session-Id propagation is future work**: v2.0 ships smart-router-side machinery only. Hermes Agent PR tracked as HMRS-FUTURE-01/02. Fingerprint fallback (HMRS-02) is opt-in (`Routing.Session.FingerprintEnabled=false` default) for loopback single-client interim case.

**19-01 execution decisions (2026-05-12):**
- **Safety-biased parser: UNSAFE before SAFE**: `"SAFE"` is a substring of `"UNSAFE"`. Checking `hasSafe` first would cause `"UNSAFE"` model responses to match SAFE and silently route to 35B (SC-2 failure). The `| true, _ -> RouteUnsafe` match arm MUST precede `| false, true -> RouteSafe`. This is the single most dangerous implementation error in Phase 19.
- **max_tokens=8 (not 4)**: RESEARCH §9 PITFALL #5 recommends 8 over 4 to absorb whitespace/punctuation drift from mlx_lm.server while keeping classify fast.
- **Single-string cache key**: `promptHash: string` vs JudgeClient's `(promptHash, responseHash)` tuple. Self-classify has no response to include in the cache key — the substitution is correct and intentional.
- **PromptVersion at construction time**: `SHA256.ComputeHash(File.ReadAllBytes(promptPath))` in the class initializer. Captures the prompt state when the service started; operator runtime edits do not change the version until next restart.
- **factory.CreateClient("selfrouter") only**: `IUpstreamClient` routes through `QueueDispatcher` holding the 122B `SemaphoreSlim(1)`. Using it for classify calls would make the classifier compete with real inference traffic.
- **No open DecisionLogger in SelfRouter.fs**: The plan's skeleton comment said `open DecisionLogger // for computePromptHash`, but `computePromptHash` is called by the caller (ChatCompletions.fs in 19-03), not SelfRouter itself. SelfRouter receives `promptHash` as a parameter — same pattern as JudgeClient receives pre-computed hashes. No import needed.

**19-03 execution decisions (2026-05-12):**
- **Self-classify call site in ChatCompletions.fs (not algorithm closure):** `RoutingAlgorithm` is synchronous (`RoutingConfig -> RouterRequest -> RoutingDecision`); calling async `ClassifyAsync` inside it would require `.GetAwaiter().GetResult()` (deadlock risk). Call site mirrors Phase 14 QualityFallback pattern — after `routeRequest` returns in the non-streaming branch.
- **`decision.Reason = Default` gate:** Only Default-reason decisions proceed to self-classify. HardRule/StickyEscalation/ExplicitModelOverride/ExplicitTask/ML/FallbackTo* already have decided targets and short-circuit.
- **GetService<ISelfRouter>() null-safe:** ISelfRouter not registered in ml-mode. `GetService` returns null; `isNull (box selfRouter)` guard fail-opens. Matches IJudgeClient pattern at line ~455.
- **RouteSafe/RouteUnsafe set ModelVersion = selfRouter.PromptVersion:** Threads SHA-256 hex8 prompt hash into DecisionLog.model_version so operators can correlate routing decisions with prompt template versions (SC-1 requirement).
- **FS0760 in test code:** `new StubHandler(...)` required (not `StubHandler(...)`) for types inheriting IDisposable under TreatWarningsAsErrors. Caught at build time.
- **MlDormantTests uses minimal ml-config with Routing:ML section:** Unlike selfrouting-mode tests that omit Routing:ML, the ml-mode test needs the ML section to not throw in ensureEmbeddingFilesPresent. W4 skip guard prevents execution when ONNX absent.

**19-02 execution decisions (2026-05-12):**
- **Mode-gated DI block outside factory lambda**: `if routingMode = "selfrouting" then (AddHttpClient + AddSingleton x3) else (AddSingleton NoOp)` placed before `RoutingAlgorithmRegistration` factory. services.AddSingleton calls are IServiceCollection-level operations — they must not appear inside a factory lambda that constructs a single value. This is the key structural constraint for all mode-gated DI in this codebase.
- **JudgeOptions/SelfRouterOptions field-name collision**: Both CLIMutable records share all 4 field names (Endpoint, PromptPath, TimeoutSeconds, MaxCacheEntries). After opening SmartRouter.Cli.Adapters.SelfRouter, F# disambiguates record literals by last-opened namespace → infers normalized as SelfRouterOptions instead of JudgeOptions. Fix: `: JudgeOptions` type annotation on `let normalized`. Required whenever two CLIMutable records share field names and both modules are opened.
- **selfRouterOpts built at registration time**: Options resolution happens once outside the factory (and outside any per-request path). The SelfRouter constructor receives the already-normalized options struct, not raw IOptions<T>. Matches judge's `effectiveEndpoint` pattern.
- **1-retry constant vs 2-retry exponential for selfrouter**: Classify is on the non-streaming hot path; fail-open quickly (RouteFailed → RouteSafe caller handling) rather than accumulate retry latency. Judge's exponential backoff suits a quality-gate call on borderline cases; classifier is best-effort.

**18-02 execution decisions (2026-05-11):**
- **122B-wins merge uses AddOrUpdate updateValueFactory**: Concurrent 35B write must never overwrite 122B escalation. `if old.LastModel = Qwen122B || model = Qwen122B then Qwen122B else model` is the non-negotiable merge formula.
- **Point B uses finalDecision.Target, not initialDecision.Target**: Quality fallback (Phase 14) and judge cascade (Phase 16) can escalate 35B→122B post-routing. Session must record what client actually received.
- **new keyword required for IDisposable-inheriting F# class**: `SessionStore` inherits `BackgroundService` (IDisposable). F# compiler emits FS0760 (promoted to error by TreatWarningsAsErrors) without `new SessionStore(...)` syntax.
- **ISessionStore hoisted above req.Stream branch**: Both streaming and non-streaming Point B writes need ISessionStore; resolving once above the branch avoids duplication.
- **Streaming error paths skip session write**: OCE (client disconnected) + general exception arms must NOT write session — client may not have received complete response.
- **configureWithoutMl gets same triple-reg**: HealthFallbackTests + QualityFallbackTests use configureWithoutMl; ISessionStore must be resolvable to avoid DI graph failures in backward-compat test paths.

**18-01 execution decisions (2026-05-11):**
- **SessionId is `string` not `option`**: Empty string sentinel matches CorrelationId convention (Phase 9). `""` means stateless per SES-04 / 18-RESEARCH Pitfall 7; avoids Option wrapper allocation on every request.
- **SessionState in Core (Domain.fs) not Cli**: Data shape is BCL-only. `ISessionStore` + `SessionStore` (Serilog/IHostedService) go in Cli adapter (18-02). ARCH-01 preserved.
- **ChatCompletions mapWireToRequest keeps SessionId="" hardcoded**: sessionId parameter threading deferred to 18-02 which extends CorrelationMiddleware to extract X-Session-Id header.
- **No catch-all arm on formatReason**: Exhaustive match enforced by FS0025/TreatWarningsAsErrors; Phase 19 SelfRoute DU case will force a 9th arm at compile time.
- **Task 1 and Task 2 committed separately**: Task 1 leaves Cli in intentional broken state (FS0025 at formatReason); Task 2 resolves it. Atomic pair pattern established for DU case + formatReason arm additions.

**17-03 execution decisions (2026-05-11):**
- **Minimal in-memory config (no Routing:ML section) for ModeSwitch DI tests**: Production appsettings.json includes Routing:ML section → mlOpts non-null → ensureEmbeddingFilesPresent throws FileNotFoundException (ONNX files absent). Solution: minimal in-memory dict omitting Routing:ML so mlOpts=null and ML bootstrap is skipped. ML-mode test skip-guarded with File.Exists(onnxEmbedPath) — W4 pattern.
- **§2 Architecture updated (deviation Rule 2)**: §2 said "3 stages, pure → 1. model override → 2. task table → 3. ML". Updated to "4 stages" with Stage 0 Hard Rules and mode-dependent Stage 3. Plan only specified §5/§7/§9.1; §2 was stale and had to be fixed.

**17-02 execution decisions (2026-05-11):**
- **Direct config read chosen for Routing.Mode**: `config.["Routing:Mode"]` mirrors Phase 16 Judge pattern; avoids CLIMutable RoutingOptions extension + test fixture churn across MLRoutingTests/CanaryTests.
- **Stub selfrouting algorithm for Phase 17**: Phase 17 placeholder returns Qwen35B/Default; Phase 19 replaces with real `makeSelfRoutingAlgorithm`. Operators wanting ML interim can set `Routing.Mode="ml"`.
- **`Reason=Default` in stub**: `SelfRoute` DU case ships in Phase 19; `Default` is correct interim value in DecisionLog for selfrouting-mode non-matched prompts.
- **ML adapter DI unchanged (MODE-03)**: RetrainingService accumulates hard cases in both modes; full ML DI gate would starve dataset and break re-activation capability.

**17-01 execution decisions (2026-05-11):**
- **Cascade Stage 0 locked in code**: `routeRequest` now 4-stage; Hard Rules fires before `tryModelOverride`. Any future stage insertion (18: sticky, 19: self-classify) must be Stage 3/4 respectively — Stage 0 is immutable.
- **`HardRule` DU case has no payload**: Target=Qwen122B and Priority=High are invariant for keyword matches. No need for a keyword-name payload (not logged to DecisionLog at this resolution).
- **No `open` needed in Routing.fs for HardRules**: `HardRules.applyHardRules` resolves via module name alone; both files are in `SmartRouter.Core` namespace scope.
- **16 HardRulesTests all pure**: No `testSequenced` wrapper needed (no Console.SetOut, no temp dirs, no Kestrel).
- **HR-06 wording fix deferred to 17-03**: REQUIREMENTS.md HR-06 says "explicit override bypasses Hard Rules" which is incorrect. The fix (Hard Rules wins per STATE.md decision 5) lands in Plan 17-03 Task 4. Tests in 17-01 already verify the correct behavior.

(v1.x execution-level decisions — full plan-by-plan log — archived in `.planning/milestones/v1.3-ROADMAP.md` plan post-mortems.)

### Pending Todos

- v2.0 Phase 20 Plans 01-02 (`/gsd:execute-phase 20-01`) — next action (Hermes Agent Integration: X-Session-Id propagation + fingerprint fallback + README §10)
- ModelsTests.fs migration to configureWithoutMl (carry-over from v1.3; MODELS-01/02/03 currently erroring with IEmbedder — small mechanical fix, same option-b pattern as HealthFallbackTests)
- Remove configureServices backwards-compat alias after ModelsTests migration

### Blockers/Concerns

- None for v2.0 roadmap. All requirements mapped; all phases have observable success criteria; ARCH-01 preserved (HardRules.fs is the only new Core file; everything else in Cli adapters).
- Phase 20 (Hermes Integration): Real Hermes Agent end-to-end smoke test requires Hermes-side PR (HMRS-FUTURE-01) which is NOT a v2.0 blocker. v2.0 ships smart-router-side fingerprint fallback for interim loopback case.
- Carry-over from v1.3: ModelsTests.fs erroring (non-blocking for v2.0 phase planning; can be addressed alongside any v2.0 plan that touches the test fixture).

## Session Continuity

Last session: 2026-05-12
Stopped at: Completed 19-04-PLAN.md — Phase 19 COMPLETE. README §5.7/§7/§8/§9.1 + CHANGELOG [Unreleased] Phase 19 documentation. 167 + 18 + 0 preserved. All 9 SR-* requirements satisfied.
Resume file: None. Next action: `/gsd:execute-phase 20-01` (Hermes Agent Integration).
