# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-05-12 after v2.0 milestone shipped)
See: .planning/MILESTONES.md (v1.3 + v2.0 entries; reverse chronological)

**Core value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.

**Current focus:** v2.1 Hermes-less Session Tiering — Phase 24 gap closure pending before milestone archive.

## Current Position

Milestone: v2.1 Hermes-less Session Tiering — In progress (Phase 24 gap closure pending)
Phase: Phase 24 — TIER-04 ml-mode integration test (gap closure) — NEXT
Plan: 23-01 complete (1/1 plans in Phase 23) — Phase 23 COMPLETE
Status: Audit ran 2026-05-12: 24/24 reqs satisfied, 0 blocking gaps, 4 tech-debt items. Operator chose to close TD-1 (TC-7 ml-mode DI integration test) before archiving v2.1. Phase 24 added to roadmap. Next action: `/gsd:plan-phase 24`.
Last activity: 2026-05-12 — `/gsd:audit-milestone` ran (v2.1-MILESTONE-AUDIT.md created; status=tech_debt). Phase 24 added per operator decision to close TD-1 only. TD-2/3 (v1.3 ModelsTests.fs + configureServices alias) and TD-4 (operator-manual smoke run) remain deferred.

**v2.1 phase summary:**

| Phase | Goal | Requirements | Plans | Status |
|-------|------|--------------|-------|--------|
| 21 — HSP + CFP Primitives | New BCL-only extraction adapters with unit tests | HSP-01..04, CFP-01..04 (8) | 2 | COMPLETE (21-01 ✓, 21-02 ✓) |
| 22 — Cascade Rewire + Migration + OBS | CorrelationMiddleware 3-tier cascade; HMRS-02 deleted; stats counters; smoke script updated | TIER-01..05, OBS-01, MIG-01..06 (12) | 3 | COMPLETE (22-01 ✓, 22-02 ✓, 22-03 ✓) |
| 23 — Documentation | README §10 rewrite; §7 row removal; §8 counter rows; §9.1 review | DOC-01..04 (4) | 1 | COMPLETE (23-01 ✓) |
| 24 — TIER-04 ml-mode integration test (gap closure) | Executable Routing.Mode=ml DI integration test closing TD-1 from v2.1 audit | TD-1 only (no formal REQ-ID) | 1 | Not started |

**Cumulative project state (post-v2.0):**

| Milestone | Phases | Plans | Tests | Tag | Shipped |
|-----------|--------|-------|-------|-----|---------|
| v1.0–v1.3 | 1-16 | 62 | 113 + 16 ignored | `v1.3.0` | 2026-05-11 |
| v2.0 | 17-20 | 12 | 175 + 18 ignored | `milestone-v2.0` | 2026-05-12 |

**Test baseline:** 186 passed + 18 ignored + 0 failed (180 from Plan 22-02 + 6 new SessionKeyCascadeTests added in Plan 22-03).

**Architecture invariants preserved (all 20 phases):**
- ARCH-01: `SmartRouter.Core` BCL-only (no Serilog / HttpClient / Microsoft.ML / ASP.NET Core)
- ARCH-02: `task {}` only (no `async {}`)
- DecisionLog `schema_version=1` (only additive enum values across v2.0: `hard_rule`, `sticky_to_122b`, `self_route`)
- Per-task atomic commits with `{type}({phase}-{plan}): {task-name}` format
- Test framework: Expecto with explicit `rootTests` list (no auto-discovery)

## Performance Metrics

**v2.0 final stats:**
- 4 phases (17, 18, 19, 20) / 12 plans
- 59 commits over ~20 hours (2026-05-11 14:04 → 2026-05-12 10:12)
- 65 files changed (+14,440 / -140)
- 175 tests passing (+62 across v2.0)
- ~17,357 LOC F# (src + tests; +2,400 net)
- Tag: `milestone-v2.0`

**v2.0 by Phase summary** (archived in `.planning/milestones/v2.0-ROADMAP.md`):
- Phase 17: Hard Rules + Routing.Mode switch (3 plans; +24 tests; HR-* + MODE-*)
- Phase 18: Session Store + Sticky (3 plans; +13 tests; SES-*)
- Phase 19: 35B Self-Classify Stage 4 (4 plans; +17 tests; SR-*)
- Phase 20: Hermes Integration + Documentation (2 plans; +8 tests; HMRS-*)

**v1.x by Phase summary** (archived in `.planning/milestones/v1.3-ROADMAP.md`):
- Phases 01-03: foundation + streaming + concurrency gate (8 plans)
- Phases 04-09: ML arc (24 plans — seam, logging, real ML, failure detection, retraining, canary)
- Phases 10-12: production hardening (10 plans — health/fallback, deployment+docs, heuristic removal)
- Phases 13-16: distillation arc (15 plans — service logging, quality fallback+trace, signal enrichment, judge)
- Phase 17 (original): ML QualityClassifier — DEFERRED at v1.3 close per operator pivot to v2.0 selfrouting

## Accumulated Context

### Decisions

Full decision logs are in PROJECT.md Key Decisions table. Milestone-level summaries:

- **v1.3** (shipped 2026-05-11): Hexagonal F# Core BCL-only; `task {}` only; bge-m3 int8 multilingual ML; quality fallback + judge OPT-IN; heuristic retirement Phase 12.
- **v2.0** (shipped 2026-05-12): Selfrouting primary (ML dormant); 35B self-route (NOT 7B separate); Hard Rules keyword-only (NOT full Heuristic.fs revival); Hermes ABOVE smart-router with session_id propagation downward; streaming-skip for self-classify (SR-06); Hard Rules wins over explicit override (HR-06).
- **v2.1** (roadmap 2026-05-12): HermesSessionExtract + ContentFingerprint in `SmartRouter.Cli.Adapters` (ARCH-01); Tier 2/3 resolve post-body-parse in ChatCompletions.fs scope (TIER-03); `archive/v2.0-network-fingerprint` tag before deletion (MIG-06); schema_version=1 unchanged (DOC-04).
- **Plan 21-01** (2026-05-12): `[ \t]*` not `\s*` in HSP regex — `\s` includes `\n` enabling cross-line match; `[ \t]*` constrains to same-line horizontal whitespace. Pure Cli adapter pattern (no DI/port) for transformation primitives consumed by later middleware cascade.
- **Plan 21-02** (2026-05-12): `compute : RouterRequest -> string` (no option wrapper — empty Messages yields hash of "|||"); per-call `use sha = SHA256.Create()` for thread-safety; `Array.map (sprintf "%02x")` for lowercase hex (NOT `Convert.ToHexString` which is uppercase). Case (b) uniqueness split into 2 testCases yielding 7 new tests (188 total, not 187 as predicted).
- **Plan 22-01** (2026-05-12): `resolveSessionCascade` must be placed BEFORE `let handler` (not before `let mapEndpoints`) — F# forward-reference. `open SmartRouter.Cli.Adapters.HermesSessionExtract` brings `extractFromSystemPrompt` into direct scope but NOT the module name as qualifier; used fully-qualified `SmartRouter.Cli.Adapters.HermesSessionExtract.extractFromSystemPrompt` to avoid ambiguity. Unconditional DI registration for ISessionCascadeStats in both pipelines (no NoOp pattern needed). StatsWire fields appended at end for stable JSON ordering.
- **Plan 22-02** (2026-05-12): Commit order reversed from requirement numbering (MIG-06 → MIG-03 → MIG-02 → MIG-01) so every intermediate state builds. MIG-03 before MIG-01 because HermesFingerprintTests.fs used SessionOptions.FingerprintEnabled — deleting field first breaks test compile. MIG-02 batches CorrelationMiddleware + Program.fs + LoggingTests in ONE commit (signature change breaks both callers). Annotated tag chosen for v2.0-network-fingerprint (not lightweight) per v2.0 milestone formality. Operator appsettings.json retaining stale FingerprintEnabled key is safe (CLIMutable silently ignores unknown keys).
- **Plan 22-03** (2026-05-12): 6 test cases (TC-1..TC-6), not 8 as RESEARCH §10 projected — TC-7 (ml-mode-dormant) skipped (cascade is mode-independent at helper level); TC-8 merged into TC-6. CHANGELOG sub-section order: ### Removed first (matches v2.1 narrative). README §7 row removal bundled with CHANGELOG commit (Task 3). minimalConfigPairs uses full StickyEscalationTests.fs list (proven with configureRequestPipeline).

Plan-level execution decisions archived per-phase in `.planning/milestones/v2.0-phases/*/`.

### Pending Todos (carry-over)

- **ModelsTests.fs migration to configureWithoutMl** (carry-over from v1.3; MODELS-01/02/03 currently error with IEmbedder — small mechanical fix, same option-b pattern as HealthFallbackTests). Non-blocking for next milestone.
- **Remove configureServices backwards-compat alias** after ModelsTests migration. Non-blocking.
- **Operator acceptance** of v2.0 SC-1/SC-2: run `./scripts/smoke-hermes-session.sh` against live mlx_lm.server rig to confirm X-Session-Id sticky-122B and fingerprint round-trip. Non-blocking for next milestone scoping.

### v2.1 Scope (locked 2026-05-12)

**In:**
- Tier 2 system-prompt parse (A): regex `^Session ID:\s*(\S+)` from `system` message; only matches when operator runs Hermes with `--pass-session-id`
- Tier 3 content fingerprint (B): SHA-256(system_message + "|||" + first_user_message)[0..15]; survives multi-turn + continuation
- CorrelationMiddleware multi-tier cascade: X-Session-Id header → Tier 2 → Tier 3
- HMRS-02 IP+UA fingerprint code removal (adapter logic, FingerprintEnabled config, 8 tests, README §10 PROXY-01 warning)
- `/stats` session_extraction_source_* counters
- README §10 rewrite (Hermes Integration v2.1 paradigm + operator `--pass-session-id` guide)
- Both Routing.Mode values use the new tiers

**Deferred (NOT in v2.1):**
- Approach C (SQLite state.db direct read) — same-machine coupling
- HMRS-FUTURE-01 (Hermes-side custom-provider PR to send X-Session-Id) — operator wants Hermes-less approach instead
- PROXY-01 — moot once network fingerprint deleted
- MODE-FUTURE-01 (Routing.Mode hot-reload), SPEC-01..03 (speculative routing), DRT-01 (dedicated tiny router)

Source doc: `~/projs/smart-router-distillation/idea/hermes-session-without-modification.md`.

### Blockers/Concerns

- None blocking Phase 24 start.
- TD-1 (TC-7 ml-mode DI integration test): selected for Phase 24 gap closure. Test does not yet exist; structural DI proof from Phase 22 audit is sound but the operator chose to upgrade to executable assertion before archiving v2.1.
- Carry-over from v1.3: ModelsTests.fs IEmbedder errors (non-blocking; deferred past v2.1).
- TD-3 (configureServices backwards-compat alias removal): blocked on TD-2; deferred past v2.1.
- TD-4 (operator live-rig smoke acceptance): operator-manual, not a code gap.

### Plan 23-01 Decisions

- **§9.1 cosmetic bump applied:** "Phase 17–19" → "Phase 17–22" (low cost, improves accuracy).
- **Tier 1 §8 description corrected:** Explicitly states stock Hermes --pass-session-id fires Tier 2 NOT Tier 1 (per RESEARCH Pitfall 5; keeps §8 consistent with §10).
- **Smoke test reference kept in §10:** Script updated in commit 1842491 (Phase 22-02); still accurate.

## Session Continuity

Last session: 2026-05-12
Stopped at: Plan 23-01 complete (4/4 tasks, 3 content commits + 1 metadata: 9d9f525, 157c49f, c1635fa + docs). Phase 23 COMPLETE. v2.1 milestone CLOSED. README §10 rewritten for three-tier cascade; §8 three counter rows added; §9.1 phase-range bumped.
Resume file: None. Next action: `/gsd:audit-milestone` and `/gsd:complete-milestone` to archive v2.1.
