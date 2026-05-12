# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-05-12 after v2.0 milestone shipped)
See: .planning/MILESTONES.md (v1.3 + v2.0 entries; reverse chronological)

**Core value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.

**Current focus:** v2.0 Self-Routing + Session-Aware **SHIPPED 2026-05-12**. Next milestone not yet scoped.

## Current Position

Milestone: — (next milestone TBD; v2.0 shipped 2026-05-12)
Phase: — (to be defined by `/gsd:new-milestone`)
Plan: —
Status: Ready to start next milestone. v2.0 archived to `.planning/milestones/v2.0-*`. 175 tests passing baseline; ARCH-01 / ARCH-02 / schema_version=1 invariants preserved across 20 phases / 74 plans (v1.0 → v2.0).
Last activity: 2026-05-12 — v2.0 milestone archived (ROADMAP + REQUIREMENTS + 4 phase dirs + research → `milestones/v2.0-*`); MILESTONES.md / PROJECT.md / STATE.md updated; git tag `milestone-v2.0` pending.

**Cumulative project state (post-v2.0):**

| Milestone | Phases | Plans | Tests | Tag | Shipped |
|-----------|--------|-------|-------|-----|---------|
| v1.0–v1.3 | 1-16 | 62 | 113 + 16 ignored | `v1.3.0` | 2026-05-11 |
| v2.0 | 17-20 | 12 | 175 + 18 ignored | `milestone-v2.0` | 2026-05-12 |

**Test baseline:** 175 passed + 18 ignored + 0 failed (was 113+16 at v1.3 baseline; +62 tests across v2.0).

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

Plan-level execution decisions archived per-phase in `.planning/milestones/v2.0-phases/*/`.

### Pending Todos (carry-over)

- **ModelsTests.fs migration to configureWithoutMl** (carry-over from v1.3; MODELS-01/02/03 currently error with IEmbedder — small mechanical fix, same option-b pattern as HealthFallbackTests). Non-blocking for next milestone.
- **Remove configureServices backwards-compat alias** after ModelsTests migration. Non-blocking.
- **Operator acceptance** of v2.0 SC-1/SC-2: run `./scripts/smoke-hermes-session.sh` against live mlx_lm.server rig to confirm X-Session-Id sticky-122B and fingerprint round-trip. Non-blocking for next milestone scoping.

### Next Milestone Candidates

Not yet scoped. Candidate threads:

1. **System-prompt session_id extraction (Tier 1)** — `~/projs/smart-router-distillation/idea/hermes-session-without-modification.md` proposes parsing Hermes' `--pass-session-id` system-prompt line (`Session ID: <uuid>`) via regex. Zero Hermes code change; 1 operator flag. Would complement v2.0's HMRS-02 network-level fingerprint with a more accurate Tier 1 path.
2. **Content-based fingerprint (Tier 3)** — Same doc points out network-level fingerprint (RemoteIp+UA, what v2.0 shipped) has NAT/loopback collision risk; system+first-user content fingerprint is more conversation-aligned and survives multi-turn / continuation requests.
3. **HMRS-FUTURE-01** — Hermes Agent custom provider PR to send `X-Session-Id` header (Hermes-side wiring).
4. **PROXY-01** — `X-Forwarded-For` parsing for reverse-proxy deployments.
5. **MODE-FUTURE-01** — Hot-reload `Routing.Mode` without restart (FileSystemWatcher).
6. **SPEC-01..03** — Speculative routing (35B drafts while router evaluates).
7. **DRT-01** — Dedicated tiny router model (Qwen2.5-3B as separate inference server).

Next step: `/gsd:new-milestone` to scope and plan.

### Blockers/Concerns

- None blocking next milestone start.
- Carry-over from v1.3: ModelsTests.fs IEmbedder errors (non-blocking; tracked above).
- v2.0 SC-1/SC-2 live-rig acceptance: deferred to operator manual run; not a blocker for next milestone scoping.

## Session Continuity

Last session: 2026-05-12
Stopped at: v2.0 milestone archived. ROADMAP + REQUIREMENTS + 4 phase dirs + research moved to `.planning/milestones/v2.0-*`. MILESTONES.md prepended with v2.0 entry; PROJECT.md evolved (v2.0 → Validated; Key Decisions outcomes ✓ Good); STATE.md reset.
Resume file: None. Next action: `/gsd:new-milestone` after scope decision.
