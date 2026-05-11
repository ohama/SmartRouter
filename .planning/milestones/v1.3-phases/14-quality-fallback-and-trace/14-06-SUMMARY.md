---
phase: 14
plan: 06
subsystem: documentation
tags: [readme, docs, quality-fallback, trace-logging, cold-start, planning-docs]

dependency-graph:
  requires: ["14-04", "14-05"]
  provides:
    - README §5.5 quality fallback subsection
    - README §7 Routing.QualityFallback config table
    - README §9.1 fallback_to_122b routing_reason value
    - README §9.10 trace logging operator guide
    - README §12.6 CLI flags table
    - planning docs Phase 14 reality sync
  affects: []

tech-stack:
  added: []
  patterns: []

key-files:
  created: []
  modified:
    - README.md
    - .planning/docs/cold-start-request-flow.md
    - .planning/docs/distillation-fallback-design-references.md

decisions:
  - id: D1
    decision: "Insert §12.6 CLI flags as new subsection after §12.5 (ConsecutiveFailureThreshold)"
    rationale: "Plan said 'adjust 12.X to whatever is appropriate'; §12.5 was the last subsection so §12.6 is the natural fit"
    alternatives: ["Append to §12.4 manual retrain section"]

metrics:
  duration: "~10 min active work (session had pauses)"
  completed: "2026-05-10"
---

# Phase 14 Plan 06: Documentation Summary

**One-liner**: README updated with quality fallback §5.5, QualityFallback config table, fallback_to_122b routing_reason, trace logging §9.10, --cold-start CLI flag; planning docs updated to remove Phase 13-era caveats.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | README.md updates (5 sections) | be59b89 | README.md |
| 2 | Update .planning/docs/ — remove caveats | 26e1c98 | cold-start-request-flow.md, distillation-fallback-design-references.md |

## What Was Done

### Task 1: README.md (5 targeted edits)

**§5.5 Quality fallback (35B → 122B retry)** — new subsection after §5.4. Documents trigger conditions (all must hold), what happens when fallback fires/doesn't fire, JSON tuning snippet, cost note, and grep command to monitor fallback rate.

**§7 Routing.QualityFallback** — new config table inserted between `Routing.ML` and `Routing.Health`. Three keys: `Enabled` (bool, default true), `MinResponseLength` (int, default 30), `BadKeywords` (string[], default ["TODO", "I think"]).

**§9.1 routing_reason row** — extended to include `fallback_to_35b` (Phase 10 — 122B unreachable) and `fallback_to_122b` (Phase 14 — 35B response failed quality check) alongside existing values.

**§9.10 Trace logging** — new subsection after §9.9. Explains `--trace-responses` CLI flag, `logs/trace/YYYY-MM-DD.jsonl` schema (12 fields including `prompt_uid`, `fallback_kind`, `initial_response_excerpt`), operator grep-by-prompt-UID workflow, and privacy/retention notes.

**§12.6 CLI flags** — new subsection after §12.5. Table with all four flags (`--retrain`, `--log-level`, `--trace-responses`, `--cold-start`) plus `--cold-start` example with log output and recovery command.

### Task 2: Planning docs

**cold-start-request-flow.md** — "사전 정정" section rewritten. Replaced "재시도 경로는 존재하지 않는다" (this retry path doesn't exist) with Phase 14 reality: both fallback directions now exist in code, streaming is still single-decision only, cross-reference to README §5.5 added.

**distillation-fallback-design-references.md** — three updates:
- §2 comparison table: "Quality check 의 존재" row changed from "없음" to "Phase 14 부터 implement" with config key reference
- §3 gap paragraph: reframed from present-tense gap to historical gap; added Phase 14 coexistence description with both routing_reason values and "Failure = Gold Data" conclusion
- §4.2: "향후 Phase 14+ candidate" → "Phase 14 — DONE. `Routing.QualityFallback` 설정 블록으로 동작"

## Decisions Made

| Decision | Choice | Rationale |
|----------|--------|-----------|
| CLI flags subsection number | §12.6 | §12.5 was last existing subsection; §12.6 is natural next |
| distillation gap table update scope | Update §2 table row + §3 paragraph + §4.2 candidate note | Plan specified all three; comprehensive Phase 14 sync |

## Verification Results

```
README checks:
  grep -c "QualityFallback|fallback_to_122b|--trace-responses|--cold-start|prompt_uid" README.md → 21 (expected >=8)
  grep -c "Routing\.QualityFallback" README.md → 6 (expected >=2)
  grep -c "logs/trace/" README.md → 5 (expected >=2)

Planning docs checks:
  grep -c "Phase 14" cold-start-request-flow.md → 4 (expected >=2)
  grep -c "Phase 14" distillation-fallback-design-references.md → 5 (expected >=4)
  grep -c "존재하지 않는다" cold-start-request-flow.md → 0 (expected 0)
```

All verification checks passed.

## CLAUDE.md Sync Rule Compliance

Per CLAUDE.md README sync rule, all 12 areas that touch these implementation areas were updated:
- Area 5 (routing pipeline) — §5.5 new quality fallback subsection
- Area 7 (config keys) — §7 Routing.QualityFallback table
- Area 8 (operational logging) — §9.10 trace logging guide
- Area 9.1 (DecisionLog schema) — routing_reason row updated
- Area 11/12 (operator workflows) — §12.6 CLI flags table

## Deviations from Plan

None — plan executed exactly as written. The only minor interpretation was choosing §12.6 as the CLI flags subsection number (plan said "adjust 12.X as appropriate").

## Next Phase Readiness

Phase 14 is complete. All 6 plans executed:
- 14-01: Core types (QualityFallbackOptions DU, RoutingReason.FallbackTo122B, TraceLog)
- 14-02: TraceLogger adapter
- 14-03: QualityCheck adapter (isBadResponse)
- 14-04: ChatCompletions quality fallback branch + trace emission
- 14-05: Integration tests (QF-01 + QF-02)
- 14-06: Documentation (this plan)

Blockers: None. Test baseline: 82 passed + 16 ignored + 0 failed.
