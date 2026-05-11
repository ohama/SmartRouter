---
phase: 15-quality-signal-enrichment
subsystem: quality-signal-enrichment
tags: [quality-check, fsharp-du, shannon-entropy, korean-length, finish-reason, stats, trace, readme, changelog]

dependency-graph:
  requires: ["14-04"]
  provides:
    - analyzeResponse 4-stage cascade (finish_reason → length → entropy → keyword)
    - Verdict/BadReason DU structured return
    - isBadResponse Phase 14 backward-compat wrapper
    - IQualityCheckStats interface + 4 counters on QueueDispatcher
    - bad_reason TraceLog field
    - 4 quality_check_hits_* /stats fields
    - README §5.5/§7/§9.3/§8 operator documentation
    - CHANGELOG [Unreleased] silent-enable entry
  affects: ["Phase 16 planner — borderline-band thresholds, prompt-relative length, logprob"]

tech-stack:
  added: []
  patterns:
    - cheap-first cascade with structured Verdict DU
    - zero-means-default config normalization
    - silent-enable defaults (BadFinishReasons/EntropyThreshold)
    - last-registration-wins DI override (test fixture)
    - Interlocked.Increment counters + Volatile.Read in GetSnapshot
    - struct tuple allocation-free GetHits on /stats hot path

key-files:
  created:
    - tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs
    - .planning/phases/15-quality-signal-enrichment/15-SUMMARY.md
  modified:
    - src/SmartRouter.Cli/Adapters/QualityCheck.fs
    - src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
    - src/SmartRouter.Cli/Adapters/TraceLogger.fs
    - src/SmartRouter.Cli/Endpoints/Stats.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/appsettings.json
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs
    - tests/SmartRouter.Tests/QualityFallbackTests.fs
    - README.md
    - CHANGELOG.md
    - .planning/docs/quality-check-improvement-options.md

decisions:
  - "analyzeResponse as primary + isBadResponse as 3-line wrapper: zero churn on Phase 14 tests"
  - "IQualityCheckStats as separate interface on QueueDispatcher (not extending IStatsProvider): cleaner separation"
  - "struct tuple in GetHits: allocation-free on /stats hot path"
  - "Verdict/BadReason DU wire format tag=value with = separator: operator jq split(\"=\")[0]"
  - "Silent-enable defaults: BadFinishReasons=[\"length\",\"content_filter\"], EntropyThreshold=2.5"
  - "Refusal-pattern default deferred: operator opt-in via BadKeywords (CONTEXT.md policy honored)"
  - "Zero-means-default for EntropyThreshold (CLIMutable float defaults to 0.0 when JSON key absent)"
  - "configureWithoutMl uses inline NoOp IQualityCheckStats object expression"
  - "ICanaryState required by /stats endpoint: added to QSE test fixture (not in Phase 14 fixture since it never GETs /stats)"

metrics:
  duration: ~46 min total (15-01: ~12 min, 15-02: ~14 min, 15-03: ~20 min)
  completed: 2026-05-10
---

# Phase 15: Quality Signal Enrichment — Phase Summary

**One-liner:** 4-stage cheap-first quality cascade (finish_reason → Korean-aware length → Shannon entropy → case-insensitive keyword) with structured Verdict DU, bad_reason trace field, 4 /stats counters, and 14 QSE tests proving all 5 detection dimensions end-to-end.

## Plan Summaries

| Plan | Name | Commits | Outcome |
|------|------|---------|---------|
| 15-01 | Config and Domain | 8d5408e, 91a2489 | Verdict/BadReason DUs + 5 helpers + QualityFallbackOptions 5-field extension + silent-enable defaults |
| 15-02 | analyzeResponse and Wiring | 9a17270, d27dffa, 7b89bc6 | analyzeResponse cascade + IQualityCheckStats counters + StatsWire 4 fields + bad_reason trace + ChatCompletions wiring + DI both paths |
| 15-03 | Tests and Docs | da35014, cf97738, 34bc979 | 14 QSE tests (13 unit + 1 integration) + README §5.5/§7/§9.3/§8 + CHANGELOG |

## All Commits

| Hash    | Plan  | Message |
|---------|-------|---------|
| 8d5408e | 15-01 | feat(15-01): add Verdict DU + helpers + extend QualityFallbackOptions |
| 91a2489 | 15-01 | feat(15-01): extend appsettings.json defaults for finish_reason + entropy |
| 81da915 | 15-01 | docs(15-01): complete config-and-domain plan |
| 9a17270 | 15-02 | feat(15-02): add analyzeResponse cascade + isBadResponse backward-compat wrapper |
| d27dffa | 15-02 | feat(15-02): add IQualityCheckStats + 4 counters to QueueDispatcher; extend StatsWire |
| 7b89bc6 | 15-02 | feat(15-02): wire analyzeResponse + bad_reason trace + IQualityCheckStats hits in ChatCompletions; register DI in both composition paths |
| 666ff88 | 15-02 | docs(15-02): complete analyzeresponse-and-wiring plan |
| da35014 | 15-03 | test(15-03): add QualitySignalEnrichmentTests for 5 detection dimensions + integration test |
| cf97738 | 15-03 | docs(15-03): update README §5.5/§7/§9.3 + /stats with Phase 15 quality signal enrichment |
| 34bc979 | 15-03 | docs(15-03): add CHANGELOG [Unreleased] entry + Phase 15 status note in quality-check-improvement-options.md |

## Test Baseline

**Phase 14 baseline:** 88 passed + 16 ignored + 0 failed
**Phase 15 final:** 102 tested (88 + 14 new QSE) — 102 passed + 16 ignored + 0 failed

Note: PITFALL-10 (QueueTests starvation test) is a pre-existing flaky test under ThreadPool pressure. It passed on the final Phase 15 run; may occasionally show as 1 failure in parallel mode. Use `--sequenced` for reliable counts.

## Phase Acceptance Criteria

| Criterion | Status |
|---|---|
| QSE-01..06 unit tests pass (all 5 dimensions covered) | PASS |
| Phase 14 QF-01..QF-08 baseline preserved unchanged | PASS |
| Fake-Kestrel integration test: bad_reason + stats counter | PASS |
| README §5.5 updated (5-dimension cascade) | PASS |
| README §7 updated (BadFinishReasons + EntropyThreshold rows) | PASS |
| README §9.3 updated (bad_reason field + jq workflows) | PASS |
| README §8 /stats updated (4 quality_check_hits_* fields) | PASS |
| CHANGELOG [Unreleased] silent-enable entry | PASS |
| ARCH-01: no forbidden imports in QualityCheck.fs | PASS |
| ARCH-02: no async{} in Core | PASS |

## Phase 15 CONTEXT.md Decision Tracking

| Decision | Disposition |
|---|---|
| finish_reason check: wired (QSE-01) | DONE — Stage 1 of cascade |
| Case-insensitive keywords: wired (QSE-02) | DONE — Stage 4 OrdinalIgnoreCase |
| Refusal-pattern default: NOT expanded | DONE — operator opt-in via BadKeywords; documented in README §5.5 + §7 |
| Korean-aware length: wired (QSE-04) | DONE — Stage 2 koreanRatio × 0.8 multiplier |
| Shannon entropy: wired (QSE-05) | DONE — Stage 3 charEntropy < EntropyThreshold |
| Silent-enable defaults | DONE — BadFinishReasons/EntropyThreshold default active without config |
| analyzeResponse as primary, isBadResponse as wrapper | DONE — Phase 14 tests unchanged |
| IQualityCheckStats as separate interface | DONE — struct tuple GetHits allocation-free |
| bad_reason tag=value wire format | DONE — operator jq split("=")[0] pattern |

## Open Items for Phase 16

- Entropy band edge (2.5..3.5 borderline zone) — Phase 16 owns this threshold range
- Prompt-relative length (Tier 2-B) — not implemented
- Logprob threshold (Tier 2-C) — not implemented; requires mlx_lm logprob support verification
- 122B-as-judge (Tier 3) — Phase 16+ candidate
- QualityClassifier ML model (Tier 4) — Phase 16+ candidate; reuses Loop B infrastructure

## Blockers / Concerns

None. Phase 15 complete. Ready for `/gsd:verify-phase 15`.
