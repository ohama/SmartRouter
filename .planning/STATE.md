# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-05-08)
See: .planning/REQUIREMENTS.md (v2.0 requirements; 32 reqs across MODE/HR/SES/SR/HMRS categories)
See: .planning/ROADMAP.md (v2.0 milestone phases 17-20; created 2026-05-11)

**Core value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.

**Current focus:** v2.0 "Self-Routing + Session-Aware" milestone — ROADMAP.md created. Ready for `/gsd:plan-phase 17`.

## Current Position

Milestone: v2.0 Self-Routing + Session-Aware — IN PLANNING 2026-05-11 (v1.3 ✅ archived; ROADMAP.md complete)
Phase: 17 (next to plan) — Hard Rules Layer + Routing.Mode Switch
Plan: —
Status: Roadmap created. 32 v2.0 requirements mapped to 4 phases (17 Hard Rules + Routing.Mode | 18 Session Store + Sticky | 19 35B Self-Routing | 20 Hermes Integration). Phase ordering driven by `RouterRequest.SessionId` compile dependency (Phase 18 must precede Phase 19). Awaiting `/gsd:plan-phase 17`.
Last activity: 2026-05-11 — v2.0 ROADMAP.md created via /gsd:new-milestone roadmapper.

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

Progress: [████████████████████████████████████████░░░░░] 60 of 60 v1.x plans (Phase 17 ML QualityClassifier deferred). v2.0: 0 of 12 plans.

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

(v1.x execution-level decisions — full plan-by-plan log — archived in `.planning/milestones/v1.3-ROADMAP.md` plan post-mortems.)

### Pending Todos

- v2.0 Phase 17 planning (`/gsd:plan-phase 17`) — next action
- ModelsTests.fs migration to configureWithoutMl (carry-over from v1.3; MODELS-01/02/03 currently erroring with IEmbedder — small mechanical fix, same option-b pattern as HealthFallbackTests)
- Remove configureServices backwards-compat alias after ModelsTests migration

### Blockers/Concerns

- None for v2.0 roadmap. All requirements mapped; all phases have observable success criteria; ARCH-01 preserved (HardRules.fs is the only new Core file; everything else in Cli adapters).
- Phase 20 (Hermes Integration): Real Hermes Agent end-to-end smoke test requires Hermes-side PR (HMRS-FUTURE-01) which is NOT a v2.0 blocker. v2.0 ships smart-router-side fingerprint fallback for interim loopback case.
- Carry-over from v1.3: ModelsTests.fs erroring (non-blocking for v2.0 phase planning; can be addressed alongside any v2.0 plan that touches the test fixture).

## Session Continuity

Last session: 2026-05-11
Stopped at: v2.0 ROADMAP.md created via /gsd:new-milestone roadmapper. 32 v2.0 requirements mapped to 4 phases (17-20). REQUIREMENTS.md traceability table already populated (filled during requirements-gathering phase). STATE.md updated to reflect roadmap-complete state.
Resume file: None. Next action: `/gsd:plan-phase 17` (Hard Rules Layer + Routing.Mode Switch).
