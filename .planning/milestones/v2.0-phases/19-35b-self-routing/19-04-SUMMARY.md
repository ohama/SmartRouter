---
phase: 19-35b-self-routing
plan: "04"
subsystem: documentation
tags: [readme, changelog, self-routing, stage4, selfrouter, docs]

# Dependency graph
requires:
  - phase: 19-01
    provides: RoutingReason.SelfRoute DU case, formatReason self_route arm, SelfRouter.fs adapter, prompts/self-router-prompt.md
  - phase: 19-02
    provides: DI wiring (named "selfrouter" HttpClient, SelfRouter triple-reg, ISelfRouterStats NoOp, 4 /stats fields, Routing.SelfRouter config block)
  - phase: 19-03
    provides: ChatCompletions.fs cascade wiring, SR-06 streaming-skip, 17 new tests
provides:
  - "README.md §5.7 — Stage 4 self-classify documented for operators"
  - "README.md §7 — Routing.SelfRouter.{Endpoint,PromptPath,TimeoutSeconds,MaxCacheEntries} config table"
  - "README.md §8 — 4 selfrouter_* /stats fields documented with semantics"
  - "README.md §9.1 — routing_reason=self_route + routing_algorithm=selfrouting + model_version=selfrouting-{hex8} documented; schema_version=1 invariant reaffirmed"
  - "CHANGELOG.md [Unreleased] Phase 19 Added block"
  - "Phase 19 operator-discoverable end-to-end"
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "README §5.x sub-section additive pattern: each Phase adds the next §5.N without renumbering"
    - "CHANGELOG [Unreleased] phase-block pattern: Phase N's Added/Notes blocks appended chronologically under [Unreleased]"

key-files:
  created: []
  modified:
    - README.md
    - CHANGELOG.md

key-decisions:
  - "No behavior changes — pure documentation. dotnet build clean (0 warnings, 0 errors); dotnet test 167 passed + 18 ignored + 0 failed unchanged."
  - "§5.1 renamed from 'Four-stage decision' to 'Six-stage decision' to reflect Phases 17-19 additions"
  - "§5.6 stale 'Phase 19 will insert' forward-reference replaced with completed reality"
  - "§2 Architecture description updated to reflect real Phase 19 delivery (was still describing Phase 17 stub)"
  - "schema_version=1 invariant explicitly reaffirmed in §9.1 routing_reason row text AND schema_version table row"

patterns-established:
  - "Cross-check: all 4 Stats.fs field names match README §8 exactly (confirmed by grep)"
  - "Cross-check: all 4 appsettings.json key names match README §7 exactly (confirmed by grep)"
  - "Cross-check: DecisionLogger.fs formatReason arm matches README §9.1 routing_reason value (confirmed by grep)"

# Metrics
duration: 15min
completed: 2026-05-12
---

# Phase 19 Plan 04: README + CHANGELOG Documentation Summary

**README §5.7/§7/§8/§9.1 + CHANGELOG [Unreleased] documenting v2.0 Stage 4 35B self-classify cascade for operators — Phase 19 complete.**

## Performance

- **Duration:** ~15 min
- **Completed:** 2026-05-12
- **Tasks:** 2/2
- **Files modified:** 2

## Accomplishments

- README updated across 4 mandatory CLAUDE.md sync areas: §5 routing pipeline (new §5.7 Stage 4 doc + §5.1 six-stage diagram), §7 config reference (Routing.SelfRouter.* block with 4 keys), §8 endpoints (/stats Phase 19 counter block with 4 selfrouter_* fields), §9.1 DecisionLog schema (routing_reason=self_route, routing_algorithm=selfrouting, model_version=selfrouting-{hex8}, schema_version=1 reaffirmed).
- CHANGELOG [Unreleased] Phase 19 Added block appended with full operator-facing coverage: paradigm mechanics, prompt tunability, LRU cache, enum values, /stats fields, config keys, MlDormantTests safeguard, fail-open behavior, schema_version=1 invariant.
- All field-name cross-checks passed: 4 selfrouter_* names match Stats.fs StatsWire exactly; 4 Routing.SelfRouter.* keys match appsettings.json exactly; routing_reason value matches DecisionLogger.fs formatReason arm exactly.
- Build and test baseline preserved: 167 passed + 18 ignored + 0 failed (docs-only wave, no behavior change).

## Task Commits

Each task was committed atomically:

1. **Task 1: README §5.7/§7/§8/§9.1** — `edd0a67` (docs)
2. **Task 2: CHANGELOG [Unreleased] Phase 19** — `6978b99` (docs)

**Plan metadata commit:** (see below, combined with SUMMARY.md + STATE.md)

## Files Created/Modified

- `README.md` — §5.1 cascade diagram updated (six-stage), §5.6 stale forward-reference fixed, §5.7 added (Stage 4 self-classify mechanics + streaming-skip + fail-open + operator tuning + rollback), §7 Routing.SelfRouter block added, §8 Phase 19 selfrouter_* counter table added, §9.1 routing_reason and routing_algorithm enum values + model_version semantics updated
- `CHANGELOG.md` — [Unreleased] Phase 19 Added block + Notes block appended after Phase 18 Notes

## Decisions Made

- §5.1 renamed "Four-stage decision" → "Six-stage decision" to avoid stale misleading count
- §2 Architecture description updated from Phase 17 stub description to real Phase 19 delivered behavior
- schema_version=1 invariant documented in two places in §9.1 for operator clarity: in the routing_reason table row, and in the schema_version table row itself
- Retry policy (1 retry at 200ms) noted as NOT operator-configurable in §7 Routing.SelfRouter block — matches implementation and prevents operator confusion about why there's no retry config key

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] §5.1 section title said "Four-stage decision" — stale after Phase 17 + 18**

- **Found during:** Task 1 (reading §5.1)
- **Issue:** Phase 17 added Stage 0, Phase 18 added Stage 3 sticky — §5.1 title was already wrong before Phase 19's Stage 4. Phase 19 makes it a 6-stage cascade.
- **Fix:** Renamed section to "Six-stage decision" and updated cascade diagram to show all 6 stages
- **Files modified:** README.md
- **Committed in:** edd0a67 (Task 1 commit)

**2. [Rule 1 - Bug] §5.6 had stale forward-reference "Phase 19 will insert..."**

- **Found during:** Task 1 (reading §5.6)
- **Issue:** End of §5.6 said "Phase 19 will insert 35B self-classify BEFORE the sticky check inside Stage 3" — this was a future-tense note that should be completed now that Phase 19 has shipped. Also contained an architectural error (self-classify runs AFTER sticky, not before or inside Stage 3).
- **Fix:** Replaced with completed-tense cross-reference to §5.7
- **Files modified:** README.md
- **Committed in:** edd0a67 (Task 1 commit)

**3. [Rule 1 - Bug] §2 Architecture still described Phase 17 stub routing**

- **Found during:** Task 1 (reading §2 architecture paragraph)
- **Issue:** Architecture section said "Phase 19 ships real SAFE/UNSAFE self-classify" (still in future tense). Phase 19 has shipped — the description was stale.
- **Fix:** Updated §2 architecture paragraph to describe the completed Phase 19 delivery using past/present tense
- **Files modified:** README.md
- **Committed in:** edd0a67 (Task 1 commit)

---

**Total deviations:** 3 auto-fixed (all Rule 1 — stale documentation bugs)
**Impact on plan:** All fixes necessary to keep README accurate. No scope creep.

## Issues Encountered

None — documentation-only wave with well-defined source of truth (appsettings.json, Stats.fs, DecisionLogger.fs for cross-check).

## Cross-Check Verification Results

All source-of-truth cross-checks passed:

| Check | README value | Source file value | Status |
|---|---|---|---|
| `selfrouter_cache_hits` | present in §8 | Stats.fs StatsWire field | OK |
| `selfrouter_cache_misses` | present in §8 | Stats.fs StatsWire field | OK |
| `selfrouter_call_count` | present in §8 | Stats.fs StatsWire field | OK |
| `selfrouter_skipped` | present in §8 | Stats.fs StatsWire field | OK |
| `Routing.SelfRouter.Endpoint` | `""` default in §7 | appsettings.json | OK |
| `Routing.SelfRouter.PromptPath` | `"prompts/self-router-prompt.md"` in §7 | appsettings.json | OK |
| `Routing.SelfRouter.TimeoutSeconds` | `5` default in §7 | appsettings.json | OK |
| `Routing.SelfRouter.MaxCacheEntries` | `10000` default in §7 | appsettings.json | OK |
| `self_route` routing_reason | present in §9.1 | DecisionLogger.fs formatReason | OK |
| `schema_version=1` | reaffirmed in §9.1 | — no migration introduced — | OK |

## Phase 19 ROADMAP Success Criteria — Operator Verifiability

After this plan, all 5 Phase 19 ROADMAP Success Criteria are verifiable by operators reading the README:

- **SC-1:** curl easy non-streaming prompt → DecisionLog has `routing_reason="self_route"`, `routing_algorithm="selfrouting"`, target=35B → documented in §5.7 + §9.1
- **SC-2:** curl ambiguous non-streaming prompt → DecisionLog has `routing_reason="self_route"`, target=122B → documented in §5.7 + §9.1
- **SC-3:** curl streaming prompt → DecisionLog `routing_reason ≠ "self_route"` → streaming-skip documented in §5.7
- **SC-4:** curl same prompt twice → `/stats` `selfrouter_call_count=1`, `selfrouter_cache_misses=1`, `selfrouter_cache_hits=1` → documented in §8 + §5.7
- **SC-5:** `dotnet test --filter "MlDormant"` passes/skips cleanly → documented in CHANGELOG Phase 19 Notes

## Next Phase Readiness

- Phase 19 is COMPLETE. All 4 plans (19-01..04) executed; all 9 SR-* requirements satisfied.
- Phase 20 (Hermes Agent Integration + Documentation) is next. Phase 20 owns: X-Session-Id propagation from Hermes, fingerprint fallback (HMRS-01/02), `X-Correlation-Id` response header documentation (HMRS-03/04), and README §10 Hermes Integration update.
- No blockers for Phase 20. ARCH-01 preserved throughout Phase 19.

---
*Phase: 19-35b-self-routing*
*Completed: 2026-05-12*
