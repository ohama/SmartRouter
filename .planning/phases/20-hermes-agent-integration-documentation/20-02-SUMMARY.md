---
phase: 20-hermes-agent-integration-documentation
plan: 02
subsystem: documentation
tags: [readme, changelog, requirements, hermes, fingerprint, session, v2.0, milestone-close]

# Dependency graph
requires:
  - phase: 20-hermes-agent-integration-documentation
    plan: 01
    provides: "Routing.Session.FingerprintEnabled shipped; CorrelationMiddleware fingerprint path; 8 HermesFingerprintTests; smoke-hermes-session.sh"

provides:
  - README §10 'Hermes / Graphify Integration' rewritten for v2.0 selfrouting paradigm
  - README §7 new row: Routing.Session.FingerprintEnabled (bool, default false)
  - CHANGELOG Phase 20 block (Added/Changed/Notes) + [Unreleased] → [2.0.0] - 2026-05-12
  - REQUIREMENTS.md HMRS-01..04 closed (checkboxes + traceability table)

affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "README sync rule applied: areas 9 (Configuration Keys) and 10 (Hermes/Graphify) updated together in one commit"
    - "CHANGELOG version promotion: [Unreleased] → [2.0.0] marks milestone close; Phase 20 block appended before promotion"
    - "Requirements closure: checkbox flip [ ] → [x] + traceability table Pending → Complete is a two-site mechanical edit"

key-files:
  created: []
  modified:
    - README.md (§10 rewrite: ~69 net insertions; §7 new Routing.Session.FingerprintEnabled row)
    - CHANGELOG.md (Phase 20 Added/Changed/Notes block; [Unreleased] → [2.0.0] - 2026-05-12)
    - .planning/REQUIREMENTS.md (HMRS-01..04 [x]; traceability Complete; footer updated)

key-decisions:
  - "§10 preserves Hermes jsonc config block verbatim (no Hermes-side change required for v2.0)"
  - "§10 Graphify subsection preserved intact (task-table routing path unaffected by v2.0 paradigm shift)"
  - "Fingerprint fallback proxy warning uses markdown blockquote (NOT footnote) per plan requirement: operator visibility on casual skim"
  - "16-character lowercase hex wording matches code (sprintf '%02x' pattern); prevents Convert.ToHexString uppercase drift"
  - "CHANGELOG: no new empty [Unreleased] placeholder added above [2.0.0] — current CHANGELOG style does not use a standing empty placeholder"
  - "REQUIREMENTS future trackers (HMRS-FUTURE-01/02, PROXY-01, SPEC-*, MODE-FUTURE-01, DRT-01) intentionally NOT modified"

# Metrics
duration: ~7min
completed: 2026-05-12
---

# Phase 20 Plan 02: README/CHANGELOG/REQUIREMENTS Documentation Summary

**README §10 rewritten for v2.0 selfrouting paradigm; CHANGELOG promoted to [2.0.0]; HMRS-01..04 requirements closed — v2.0 milestone READY FOR RELEASE**

## Performance

- **Duration:** ~7 min
- **Started:** 2026-05-12T00:44:10Z
- **Completed:** 2026-05-12T00:50:43Z
- **Tasks:** 3
- **Files modified:** 3 (docs only — no source code, tests, or scripts)

## Accomplishments

- **README §10** fully rewritten: replaces v1.x ML routing description with v2.0 selfrouting cascade (Stage 0 Hard Rules → Stage 1 explicit override → Stage 2 task table → Stage 3 sticky → Stage 4 self-classify → Stage 5 default 35B); includes `#### X-Session-Id header opt-in`, `#### Fingerprint fallback` with prominent `> **WARNING — NOT SAFE BEHIND REVERSE PROXIES.**` blockquote, HMRS-FUTURE-01/PROXY-01 callouts, and `scripts/smoke-hermes-session.sh` operator verification reference; Hermes jsonc config block preserved verbatim; Graphify subsection preserved with graph_indexing no-fallback rule intact; §11 boundary and section numbering unchanged.
- **README §7** gained new `Routing.Session.FingerprintEnabled` row (bool, default `false`, reverse-proxy warning, §10 cross-reference) immediately after `MaxEntries`, matching the config key shipped in 20-01.
- **CHANGELOG** Phase 20 block (### Added / ### Changed / ### Notes) appended; `[Unreleased]` promoted to `[2.0.0] - 2026-05-12` marking v2.0 milestone close; Phase 17/18/19 blocks preserved verbatim.
- **REQUIREMENTS.md** HMRS-01..04 checkboxes flipped `[ ]` → `[x]`; traceability table rows updated Pending → Complete; HMRS-FUTURE-01, HMRS-FUTURE-02, PROXY-01, SPEC-*, MODE-FUTURE-01, DRT-01 untouched; footer updated `2026-05-12 after Phase 20 closure`.
- **Test baseline preserved:** 175 passed / 18 ignored / 0 failed (docs-only plan; no code regression).

## Task Commits

1. **Task 1: Rewrite README §10 for v2.0 + add §7 FingerprintEnabled row** — `ffb523f` (docs)
2. **Task 2: CHANGELOG Phase 20 block + promote [Unreleased] to [2.0.0]** — `7aaa1b0` (docs)
3. **Task 3: Mark HMRS-01..04 complete in REQUIREMENTS.md** — `c14e494` (docs)

**Plan metadata commit:** (staged after SUMMARY.md + STATE.md updates)

## Files Modified

- `README.md` — §10 rewrite (Hermes Agent v2.0 + X-Session-Id + Fingerprint fallback + Graphify preserved) + §7 new Routing.Session.FingerprintEnabled row
- `CHANGELOG.md` — Phase 20 Added/Changed/Notes block appended; [Unreleased] → [2.0.0] - 2026-05-12
- `.planning/REQUIREMENTS.md` — HMRS-01..04 [x]; traceability Complete; footer Phase 20 closure

## README Sync Rule Audit (CLAUDE.md)

Areas touched:
- **Area 9 (Configuration Keys → §7)**: `Routing.Session.FingerprintEnabled` row added — matches `src/SmartRouter.Cli/appsettings.json` exactly
- **Area 10 (Hermes / Graphify Integration → §10)**: fully rewritten for v2.0

Areas NOT touched (confirmed unchanged):
- Area 1 (purpose/scope): §1 unchanged
- Area 2 (architecture): §2 unchanged (updated in Phase 19)
- Area 3 (public surface): no new CLI flags
- Area 4 (build/deploy): §4/§11 unchanged
- Area 5 (endpoints): §8 unchanged
- Area 6 (routing pipeline): §5 unchanged (updated in Phase 19)
- Area 7 (DecisionLog schema): §9.1 unchanged (schema_version=1; no new fields)
- Area 8 (operational log): §9.6-9.9 unchanged
- Area 11 (Hermes integration — already §10 above)
- Area 12 (operator workflows / troubleshooting): §12/§13 unchanged

## v2.0 Milestone Closeout

**v2.0 "Self-Routing + Session-Aware" milestone: COMPLETE**

All 32 requirements satisfied across 4 phases and 12 plans:
- Phase 17: Hard Rules Layer + Routing.Mode switch — 10 reqs (MODE-01..04 + HR-01..06) ✓
- Phase 18: Session Store + Sticky Escalation — 9 reqs (SES-01..09) ✓
- Phase 19: 35B Self-Classify Stage 4 — 9 reqs (SR-01..09) ✓
- Phase 20: Hermes Agent Integration + Documentation — 4 reqs (HMRS-01..04) ✓

7 future trackers retained for v2.x work: HMRS-FUTURE-01/02, PROXY-01, SPEC-01/02/03, MODE-FUTURE-01, DRT-01.

**Operator acceptance check:** Run `./scripts/smoke-hermes-session.sh` against live router to verify sticky escalation end-to-end (SC-1 manual acceptance).

## Decisions Made

- **§10 Hermes jsonc config unchanged**: No Hermes-side config change is required for v2.0 basic integration. The config block is preserved verbatim to avoid operator confusion.
- **Proxy warning as blockquote (not footnote)**: `> **WARNING — NOT SAFE BEHIND REVERSE PROXIES.**` is at the top of the fingerprint subsection, visible on casual skim. The plan required this explicitly (RESEARCH §Pitfall 3 — prominent warning box).
- **16-character lowercase hex wording**: Precise wording matches `sprintf "%02x"` code pattern; prevents future implementers from using `Convert.ToHexString` (uppercase) and breaking consistency with DecisionLogger.fs/SelfRouter.fs.
- **No empty [Unreleased] placeholder**: Current CHANGELOG style does not maintain a standing empty `[Unreleased]` above the current version. Convention: next phase that needs CHANGELOG adds `[Unreleased]` when it first adds entries.

## Deviations from Plan

None — plan executed exactly as written. All three tasks completed without auto-fixes or scope deviations. The CHANGELOG separator count was 3 (not >=4 as the verify comment implied), but this matches the actual project convention — version-level headers (`## [1.3.0]`, etc.) are not separated by `---` in this project's style.

## Issues Encountered

None.

## Next Phase Readiness

- v2.0 milestone COMPLETE: all 32 requirements satisfied; CHANGELOG promotes to 2.0.0; REQUIREMENTS.md fully closed
- Phase 20 COMPLETE: both plans (20-01 code + 20-02 docs) committed and verified
- No next v2.0 phase — v2.0 is the final milestone in the current ROADMAP
- Future work tracked: HMRS-FUTURE-01 (Hermes X-Session-Id propagation), HMRS-FUTURE-02 (live Hermes smoke test), PROXY-01 (reverse-proxy X-Forwarded-For), SPEC-01/02/03 (speculative routing), MODE-FUTURE-01 (hot-reload Mode switch), DRT-01 (dedicated router model)

---
*Phase: 20-hermes-agent-integration-documentation*
*Completed: 2026-05-12*
