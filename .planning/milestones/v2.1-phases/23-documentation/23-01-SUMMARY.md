---
phase: 23-documentation
plan: 01
subsystem: documentation
tags: [readme, hermes, session-cascade, three-tier, stats-counters, v2.1]

# Dependency graph
requires:
  - phase: 22-cascade-rewire-migration-and-observability
    provides: "Three-tier session cascade implementation (TIER-01..05, OBS-01, MIG-01..06); StatsWire session_extraction_source_* fields; FingerprintEnabled row removed from §7 in commit 938c8ac"
provides:
  - "README §10 rewritten for v2.1 three-tier session cascade (DOC-01)"
  - "README §7 verified intact — FingerprintEnabled absent, TtlMinutes+MaxEntries present (DOC-02)"
  - "README §8 /stats gains three Phase 22 cascade counter rows + JSON example fields + jq snippet (DOC-03)"
  - "README §9.1 cosmetic phase-range bump Phase 17–19 → Phase 17–22 applied (DOC-04)"
affects: ["operator-docs", "README.md"]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "README §8 counter table: Phase-tagged block pattern matches existing Phase 15/16/19 blocks"
    - "README §10 tier description: Operator-facing numbered tier pattern with counter cross-reference"

key-files:
  created:
    - ".planning/phases/23-documentation/23-01-SUMMARY.md"
  modified:
    - "README.md"
    - ".planning/STATE.md"

key-decisions:
  - "§9.1 cosmetic bump applied: 'Phase 17–19' → 'Phase 17–22' (low cost, improves accuracy per RESEARCH Q1 recommendation)"
  - "Tier 1 description in §8 table corrected vs RESEARCH draft: explicitly states stock Hermes --pass-session-id fires Tier 2 NOT Tier 1 (per RESEARCH Pitfall 5)"
  - "Smoke test reference retained in §10 (./scripts/smoke-hermes-session.sh) — script was updated in commit 1842491 and remains accurate per RESEARCH Q3"
  - "§10 enablement sub-section uses ##### heading level to nest under #### Session continuity heading"

patterns-established:
  - "Documentation-only plan pattern: all verification is grep-based (no dotnet test), tasks produce 3-4 commits"

# Metrics
duration: ~4min
completed: 2026-05-12
---

# Phase 23 Plan 01: Documentation Summary

**README.md synced to v2.1 three-tier session cascade: §10 rewritten (Fingerprint fallback deleted, four --pass-session-id options added), §8 gains three cascade counter rows, §9.1 phase range bumped to 17–22**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-05-12T05:15:05Z
- **Completed:** 2026-05-12T05:19:05Z
- **Tasks:** 4 (Tasks 1–4 complete)
- **Files modified:** 1 (README.md); planning artifacts: SUMMARY.md, STATE.md

## Accomplishments

- README §10 rewritten for v2.1: heading updated, intro paragraph describes three-tier cascade, X-Session-Id section replaced with tier-by-tier description, Fingerprint fallback sub-section entirely deleted, four --pass-session-id enablement options documented, Hermes config block and Graphify sub-section preserved verbatim.
- README §8 /stats updated: three new fields in JSON example (session_extraction_source_header, session_extraction_source_sysprompt, session_extraction_source_content), Phase 22 counter table added after Phase 19 block, jq monitoring snippet added.
- README §7 verified intact — DOC-02 complete since commit 938c8ac; FingerprintEnabled row absent; TtlMinutes and MaxEntries rows present.
- README §9.1 cosmetic phase-range bump applied: "Phase 17–19" → "Phase 17–22" (one-line edit).

## Task Commits

Each task was committed atomically:

1. **Task 1: Verify §7 and review §9.1** — `9d9f525` (docs)
2. **Task 2: Rewrite README §10 for v2.1 three-tier session cascade** — `157c49f` (docs)
3. **Task 3: Add Phase 22 cascade counter rows to §8 /stats** — `c1635fa` (docs)
4. **Task 4: Cross-cutting drift sweep + SUMMARY + STATE** — (this commit)

## Files Created/Modified

- `README.md` — §10 rewrite (Fingerprint fallback deleted, three-tier cascade + enablement options added); §8 three counter rows + JSON example fields + jq snippet; §9.1 phase-range bump
- `.planning/phases/23-documentation/23-01-SUMMARY.md` — created (this file)
- `.planning/STATE.md` — Phase 23 marked complete; v2.1 milestone closed

## Decisions Made

- **§9.1 cosmetic bump applied:** Research Q1 recommended updating "Phase 17–19" → "Phase 17–22". Applied as low-cost cosmetic improvement. Wording changed to "All v2.0–v2.1 additions (Phase 17–22)".
- **Tier 1 row in §8 table:** RESEARCH draft said "Operator-initiated via `hermes --pass-session-id` or direct header injection." Corrected to explicitly state stock Hermes `--pass-session-id` fires Tier 2 NOT Tier 1 (per RESEARCH Pitfall 5). This keeps §8 consistent with §10 rewrite.
- **Smoke test reference kept in §10:** Research Q3 recommended keeping it. The script (./scripts/smoke-hermes-session.sh) was updated in Phase 22 commit 1842491 and is still accurate.

## Deviations from Plan

None — plan executed exactly as written. Line numbers for §10 sections matched research prediction (781–875). The only deviation was a content correction to the §8 Tier 1 table row (explicitly clarifying that Hermes `--pass-session-id` fires Tier 2, not Tier 1), which was described in the plan itself (Task 3, "Correction note on Tier 1 wording").

## Verification Grep Results (Task 4 drift sweep)

Full plan-level verification suite — all passing:

| Check | Expected | Result |
|-------|----------|--------|
| `FingerprintEnabled` | 0 | 0 |
| `PROXY-01` | 0 | 0 |
| `RemoteIp` | 0 | 0 |
| `HMRS-FUTURE-01` | 0 | 0 |
| `HMRS-FUTURE` | 0 | 0 |
| `NOT SAFE BEHIND REVERSE PROXIES` | 0 | 0 |
| `network fingerprint` | 0 | 0 |
| `v2.0 ships smart-router-side` | 0 | 0 |
| `three-tier` | >=1 | 3 |
| `pass-session-id` | >=4 | 12 |
| `HERMES_TUI_PASS_SESSION_ID` | >=1 | 2 |
| `hermes-router` | >=1 | 2 |
| `Session ID:` | >=1 | 6 |
| `session_extraction_source_header` | >=3 | 4 |
| `session_extraction_source_sysprompt` | >=3 | 4 |
| `session_extraction_source_content` | >=3 | 4 |
| `Phase 22 — session cascade counters` | >=1 | 1 |
| `session_extraction_source_sys_prompt` (wrong spelling) | 0 | 0 |
| `session_extraction_source_system_prompt` (wrong spelling) | 0 | 0 |
| `TtlMinutes` | >=1 | 3 |
| `MaxEntries` | >=1 | 2 |
| `Phase 17–22` | >=1 | 1 |
| `Phase 17–19` | 0 | 0 |
| `Hermes config` | >=1 | 2 |
| `### Graphify` | >=1 | 1 |

## DOC Requirements Status

| Requirement | Status | Evidence |
|-------------|--------|---------|
| DOC-01: §10 rewrite for v2.1 three-tier cascade | PASS | §10 describes all three tiers, four --pass-session-id options; zero stale markers (FingerprintEnabled, PROXY-01, RemoteIp, HMRS-FUTURE-01, "NOT SAFE BEHIND REVERSE PROXIES") |
| DOC-02: §7 FingerprintEnabled row removed | PASS | grep returns 0 for FingerprintEnabled in §7 (lines 441–448); row removed in commit 938c8ac (Phase 22 Plan 03); verified idempotent in Task 1 |
| DOC-03: §8 three cascade counter rows | PASS | session_extraction_source_header/sysprompt/content appear 4x each (JSON example, table, jq snippet, §10 tier description); wrong-spelling guards: 0 |
| DOC-04: §9.1 reviewed; cosmetic bump applied | PASS | schema_version=1 unchanged; no new session_id field; cosmetic bump "Phase 17–19" → "Phase 17–22" applied in Task 1 commit 9d9f525 |

## Issues Encountered

None.

## Next Phase Readiness

Phase 23 is the final phase of the v2.1 Hermes-less Session Tiering milestone. All requirements DOC-01..04 are complete. The v2.1 milestone is ready for closure via `/gsd:audit-milestone` and `/gsd:complete-milestone`.

Carry-over items (non-blocking, from prior phases):
- ModelsTests.fs migration to configureWithoutMl (MODELS-01/02/03)
- Remove configureServices backwards-compat alias after ModelsTests migration
- Operator acceptance of v2.0 SC-1/SC-2 (live rig smoke test)

---
*Phase: 23-documentation*
*Completed: 2026-05-12*
