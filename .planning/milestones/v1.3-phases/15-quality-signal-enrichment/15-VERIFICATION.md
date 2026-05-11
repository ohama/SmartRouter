---
phase: 15-quality-signal-enrichment
verified: 2026-05-10T14:12:43Z
status: passed
score: 13/13 must-haves verified
re_verification: false
---

# Phase 15: Quality Signal Enrichment — Verification Report

**Phase Goal:** `isBadResponse` 가 모델이 이미 보내주는 신호 (`finish_reason`) 와 더 견고한 휴리스틱 (case-insensitive keywords, refusal 패턴 [opt-in], 한글 응답 길이 보정, Shannon entropy 기반 반복 감지) 을 활용. False negative (반복 루프, 잘린 응답, refusal) 가 큰 폭으로 감소; false positive (정상 답 오판) 도 약간 감소. Phase 14 의 QF-01/QF-02 + 이슈 #13 의 QF-03..08 backward-compat 통과.

**Verified:** 2026-05-10T14:12:43Z
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | QSE-01: finish_reason wiring exists end-to-end | VERIFIED | `extractFinishReason` at QualityCheck.fs:79; `analyzeResponse` param `finishReason: string option` at :165; `ChatCompletions.fs:419` calls it; `BadFinishReasons` in `QualityFallbackOptions` at QualityCheck.fs:38 |
| 2 | QSE-02: case-insensitive keyword matching | VERIFIED | `matchKeyword` uses `StringComparison.OrdinalIgnoreCase` at QualityCheck.fs:147; 2 unit tests confirm (`"todo"` matches `"TODO"`, `"ToDo"` matches `"TODO"`) |
| 3 | QSE-03: refusal patterns are opt-in NOT default | VERIFIED | `appsettings.json:47` has `"BadKeywords": ["TODO", "I think"]` — only 2 defaults, no refusal patterns; README §5.5 and §7 provide explicit opt-in guidance |
| 4 | QSE-04: Korean length boost uses Hangul Syllables only | VERIFIED | `koreanRatio` at QualityCheck.fs:105 uses `c >= '가' && c <= '힣'` — Hangul Syllables (U+AC00..U+D7A3) only; `effectiveLength` at :113 applies `1 + ratio × 0.8`; 2 unit tests pass |
| 5 | QSE-05: Shannon entropy detection | VERIFIED | `charEntropy` at QualityCheck.fs:125; `EntropyThreshold` config key at :39; cascade stage 3 at :196; 2 unit tests pass |
| 6 | QSE-06: Phase 14 QF-01/QF-02 + #13 QF-03..08 backward-compat | VERIFIED | Test suite: 102 passed, 16 ignored, 0 failed — all 8 Phase 14/issue-#13 cases pass |
| 7 | Cascade order: finish_reason → length → entropy → keyword | VERIFIED | `analyzeResponse` body at QualityCheck.fs:172-201: stage1 finish_reason check → early exit or proceed to `extractAssistantText` → stage2 effectiveLength → stage3 entropy → stage4 matchKeyword |
| 8 | TraceLog `bad_reason` 13th field, schema_version=1 | VERIFIED | TraceLogger.fs:16 confirms "13 fields"; `bad_reason` at :44 is field 13; `schema_version=1` unchanged; ChatCompletions.fs:444-448 serializes `"tag=value"` format |
| 9 | /stats exposes 4 quality_check_hits counters | VERIFIED | StatsWire at Stats.fs:37-40 has all 4 flat snake_case fields; QueueDispatcher.fs:126-129 declares 4 `Interlocked` counters; ChatCompletions.fs:434-437 calls `Record*Hit()` on Bad arm |
| 10 | README sync: §5.5, §7, §9.3, /stats | VERIFIED | §5.5 lines 172-177: 5-dimension cheap-first cascade list; §7 lines 278-280: 5 config rows with case-insensitive note + opt-in guidance; §9.3 lines 497-507: `bad_reason` field + jq workflows; /stats lines 388-404: 4 counters shown |
| 11 | CHANGELOG: silent-enable tone + restore-Phase-14 instruction | VERIFIED | CHANGELOG.md lines 8-28: `### Changed` uses "silent enable" tone; provides explicit restore instruction with jsonc snippet; `### Added` lists trace field, /stats counters, config keys |
| 12 | ARCH-01: no SmartRouter.Core changes in Phase 15 | VERIFIED | `git log 8d5408e..HEAD -- src/SmartRouter.Core/` returns empty — zero commits touched Core |
| 13 | Streaming branch INTENTIONALLY SKIPPED | VERIFIED | ChatCompletions.fs:287,295: two "INTENTIONALLY SKIPPED" comments; `analyzeResponse` call at :422 is inside the `else` branch of `if req.Stream` (non-streaming path only) |

**Score:** 13/13 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/SmartRouter.Cli/Adapters/QualityCheck.fs` | BadReason DU + Verdict DU + all helpers + analyzeResponse cascade | VERIFIED | 212 lines; all types and functions present and wired |
| `src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` | IQualityCheckStats interface + 4 Interlocked counters + GetHits | VERIFIED | Interface at :57-64; counters at :126-129; implementation at :455-471 |
| `src/SmartRouter.Cli/Endpoints/Stats.fs` | 4 flat snake_case quality_check_hits_* fields in StatsWire | VERIFIED | Lines 37-40 + mapping at 58-61 |
| `src/SmartRouter.Cli/Adapters/TraceLogger.fs` | bad_reason as 13th TraceRecord field; schema_version=1 | VERIFIED | Lines 43-44; header comment confirms 13 fields |
| `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` | analyzeResponse in non-streaming only; Verdict pattern-match; bad_reason emit; RecordHit calls | VERIFIED | Lines 419-448 (non-streaming); lines 287,295 confirm streaming skip |
| `src/SmartRouter.Cli/CompositionRoot.fs` | normalizeQualityFallback with BadFinishReasons + EntropyThreshold defaults; IQualityCheckStats in both paths | VERIFIED | normalizeQualityFallback at :132; IQualityCheckStats at :473 (configureRequestPipeline) and :907 (configureWithoutMl NoOp) |
| `src/SmartRouter.Cli/appsettings.json` | All 5 QualityFallback keys present | VERIFIED | Lines 45-49: Enabled, MinResponseLength, BadKeywords, BadFinishReasons, EntropyThreshold |
| `tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs` | 14 test cases covering 5 dimensions + cascade + integration | VERIFIED | 14 testCase entries confirmed (QSE-01×3, QSE-02×2, QSE-03×1, QSE-04×2, QSE-05×2, QSE-06×2, Cascade×1, QSE-INT×1) |
| `tests/SmartRouter.Tests/QualityFallbackTests.fs` | QF-01..08 all pass unchanged | VERIFIED | All 8 QF tests present; test suite confirms 102 passed 0 failed |
| `tests/SmartRouter.Tests/RouterTests.fs` | rootTests includes QualitySignalEnrichmentTests.tests | VERIFIED | Line 34: `SmartRouter.Tests.QualitySignalEnrichmentTests.tests` in rootTests |
| `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` | QualityFallbackTests before QualitySignalEnrichmentTests before RouterTests | VERIFIED | Lines 33, 35, 36: compile order correct |
| `README.md` | §5.5 cascade, §7 config table, §9.3 bad_reason, /stats counters | VERIFIED | All 4 sections updated (see Truth #10 above) |
| `CHANGELOG.md` | [Unreleased] Changed + Added with correct tone | VERIFIED | Lines 8-28: silent-enable tone; restore instruction; 3 Added items |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| ChatCompletions.fs | QualityCheck.analyzeResponse | extractFinishReason + analyzeResponse call | WIRED | ChatCompletions.fs:419-422 extracts finish_reason and calls analyzeResponse with it |
| ChatCompletions.fs | IQualityCheckStats.Record*Hit | Pattern match on Verdict DU | WIRED | Lines 434-437: all 4 Bad cases mapped to correct counter method |
| ChatCompletions.fs | TraceRecord.bad_reason | sprintf "tag=value" on Verdict arms | WIRED | Lines 443-448: all 4 Bad arms + Good → None |
| QueueDispatcher | StatsSnapshot.QualityCheckHits | Volatile.Read on 4 mutable longs | WIRED | Lines 449-453 in GetSnapshot return |
| Stats.fs | StatsWire.quality_check_hits_* | snapshotToWireFields reads QualityCheckHits | WIRED | Lines 58-61 |
| CompositionRoot | IQualityCheckStats | AddSingleton wrapping QueueDispatcher (real) + NoOp (offline) | WIRED | Lines 473-474 and 907-915 |
| analyzeResponse | BadFinishReasons default | normalizeQualityFallback silent-enable | WIRED | CompositionRoot.fs:133-140 applies defaults when null/empty |
| analyzeResponse | EntropyThreshold default | normalizeQualityFallback silent-enable | WIRED | CompositionRoot.fs:151-152 applies 2.5 when ≤0 |

---

### Requirements Coverage

| Requirement | Status | Evidence |
|-------------|--------|----------|
| QSE-01 finish_reason wiring | SATISFIED | extractFinishReason + BadFinishReasons + ChatCompletions wiring all verified |
| QSE-02 case-insensitive keywords | SATISFIED | OrdinalIgnoreCase in matchKeyword; 2 unit tests pass |
| QSE-03 refusal opt-in not default | SATISFIED | appsettings.json keeps 2-keyword default; README opt-in guidance present |
| QSE-04 Korean length boost (Hangul only) | SATISFIED | '가'..'힣' range; effectiveLength formula; 2 unit tests pass |
| QSE-05 Shannon entropy | SATISFIED | charEntropy + EntropyThreshold + cascade stage 3; 2 unit tests pass |
| QSE-06 backward-compat | SATISFIED | 102 passed 0 failed; QF-01..08 all present and passing |

---

### Anti-Patterns Found

None. Grep scan of Phase 15 modified files found:

- Zero TODO/FIXME/XXX in production code
- No placeholder text in output paths
- No empty return stubs in quality check cascade or ChatCompletions wiring
- `isBadResponse` backward-compat wrapper is a legitimate thin wrapper (documented as intentional, not a stub)

---

### Test Suite Result

```
102 tests run — 102 passed, 16 ignored, 0 failed. Success!
```

Run command: `dotnet run --project tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -c Release -- --sequenced`

Expected count from prompt: **102 passed + 16 ignored + 0 failed** — MATCHES EXACTLY.

---

### Human Verification Required

None required. All 13 must-haves verified programmatically.

Items that could benefit from operator-level spot-check (not blockers):

1. **Entropy threshold calibration** — The default `EntropyThreshold: 2.5` is verified present in code but real-world false positive rate depends on Qwen 35B response distribution. A quick `curl /stats | jq .quality_check_hits_entropy` after 30 min of production traffic would confirm the threshold isn't too aggressive. This is a tuning concern, not a correctness gap.

2. **Korean boost multiplier in practice** — The `0.8` multiplier and `'가'..'힣'` range are code-correct per CONTEXT.md decision. A bilingual (Korean + English) real request through the running service would confirm the effective-length calculation doesn't cause unexpected fallback behavior.

These are operational validation items, not required for phase sign-off.

---

### Gaps Summary

No gaps. All 13 must-haves pass at all three verification levels (exists, substantive, wired).

---

_Verified: 2026-05-10T14:12:43Z_
_Verifier: Claude (gsd-verifier)_
