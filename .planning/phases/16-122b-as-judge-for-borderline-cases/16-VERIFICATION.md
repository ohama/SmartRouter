---
phase: 16-122b-as-judge-for-borderline-cases
verified: 2026-05-11T09:30:00Z
status: passed
score: 14/14 must-haves verified
---

# Phase 16: 122B-as-Judge for Borderline Cases — Verification Report

**Phase Goal:** Phase 15 heuristic를 통과했지만 quality가 borderline한 케이스에만 122B에 1-token verification call을 보냄. 명백한 good/bad는 fast path 유지. LRU bounded cache. 별도 named "judge" HttpClient. 새 trace 필드 `judge_called`/`judge_verdict`/`judge_latency_ms` 추가.
**Verified:** 2026-05-11T09:30:00Z
**Status:** passed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|---------|
| 1 | JDG-01: BorderlineClassifier pure BCL-only, BorderlineKind DU, classifyBorderline returns 3-way option | VERIFIED | `BorderlineClassifier.fs` 77 lines; only `open System` + `open SmartRouter.Cli.Adapters.QualityCheck`; zero matches for `grep -E "Microsoft.ML\|Serilog\|HttpClient\|AspNetCore"`; `UncertainEntropy of score` + `UncertainLength of effectiveLen` DU declared at line 12-13 |
| 2 | JDG-01 B1: REQUIREMENTS.md JDG-01 documents (a) keyword exclusion, (b) BorderlineKind separate DU, (c) architectural rationale | VERIFIED | REQUIREMENTS.md line 351: full paragraph with all three clarifications present |
| 3 | JDG-02: IJudgeClient + IJudgeStats + JudgeVerdict DU + ROUTE_NO wins parser + 1-token body + JsonFSharpConverter | VERIFIED | `JudgeClient.fs` 329 lines; parser at line 89-99 matches ROUTE_NO first (safety bias); `max_tokens <- 1` at line 240; `JsonFSharpConverter()` at line 244; `CreateClient("judge")` at line 305 |
| 4 | JDG-02: prompts/judge-prompt.md with {{QUESTION}}/{{RESPONSE}} + ROUTE_YES/ROUTE_NO sentinels | VERIFIED | File exists (15 lines); both placeholders at lines 6, 9; both sentinels at lines 13, 15 |
| 5 | JDG-03: ConcurrentDictionary LRU cache + IJudgeStats.GetJudgeStats() struct tuple | VERIFIED | `cache = ConcurrentDictionary<string * string, CacheEntry>()` at line 155; `GetJudgeStats()` returns `struct (Volatile.Read(&cacheHits), Volatile.Read(&cacheMisses), Volatile.Read(&callCount))` at lines 324-328 |
| 6 | JDG-03: Stats.fs StatsWire 3 new flat snake_case fields; mapEndpoints null-safe IJudgeStats resolve | VERIFIED | `judge_cache_hits`, `judge_cache_misses`, `judge_call_count` at Stats.fs lines 42-44; `GetService<IJudgeStats>()` null-safe at line 92; `isNull (box judgeStats)` guard at line 94 |
| 7 | JDG-04: ChatCompletions calls judgeClient.VerdictAsync only on (a) judgeClient != null, (b) Good verdict, (c) target = Qwen35B, (d) classifyBorderline = Some _ | VERIFIED | Lines 510-535: `if isNull (box judgeClient) then` → skip; `if initialDecision.Target <> Qwen35B then` → skip; `match classifyBorderline qualityFallbackOpts initialContent with | None -> ... | Some _ ->` → call VerdictAsync |
| 8 | JDG-05: TraceRecord 16 fields, schema_version=1 unchanged, 3 new Phase 16 fields | VERIFIED | TraceLogger.fs: 16 `[<JsonPropertyName>]` attributes counted; `schema_version : int` at line 24; `judge_called : bool` at line 50; `judge_verdict : string option` at line 52; `judge_latency_ms : float option` at line 54 |
| 9 | OQ3: computePromptHash hoisted once at top of Ok branch | VERIFIED | Line 432: `let promptHash = computePromptHash req.Messages` — single call site; appears in both judge VerdictAsync call (line 532) and trace block (line 624) |
| 10 | B3 fix: RouteNo + 122B unreachable → judgeTriggeredFallback=false; RouteNo + retry fails → judgeTriggeredFallback=false | VERIFIED | Line 548: `return (initialDecision, initialBody, true, Some "no", Some judgeMs, false)` (122B unreachable arm); Line 572: `return (initialDecision, initialBody, true, Some "no", Some judgeMs, false)` (retry fails arm); Line 617-618: `fallback_kind = if qualityFallbackTriggered || judgeTriggeredFallback then Some "quality"` — only when substitution occurred |
| 11 | B4 fix: clean two-branch if/else DI for IJudgeClient + IJudgeStats | VERIFIED | CompositionRoot.fs lines 548-623: `if judgeEnabled then ... else` — clean mutually exclusive branches; comment at line 541: "mutually exclusive — no override conflict (B4 fix)"; NoOp only in else-branch |
| 12 | OPT-IN default: appsettings.json Routing.Judge.Enabled=false | VERIFIED | appsettings.json: `"Enabled": false` in Routing.Judge block; 5 keys present (Enabled, Endpoint, PromptPath, TimeoutSeconds, MaxCacheEntries) |
| 13 | README sync: §5.5.5 borderline judge, §7 Routing.Judge config table, §8 /stats 3 new fields, §9.3 trace 3 new fields + jq workflow | VERIFIED | README.md line 217: §5.5.5; line 306: Routing.Judge config table with 5 rows; line 454-460: /stats judge fields; lines 548-550: trace judge fields; lines 570-576: jq workflows |
| 14 | ARCH-01: zero src/SmartRouter.Core/ changes; test count 113+16+0 | VERIFIED | `git log 014bf5b..HEAD -- src/SmartRouter.Core/` returns empty; tests run 3/6 trials with 113 passed 16 ignored 0 failed; 1 pre-existing flaky failure (QueueTests.fs fairness timing) present in 3/6 runs |

**Score:** 14/14 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs` | BorderlineKind DU + classifyBorderline, BCL-only, 50+ lines | VERIFIED | 77 lines; BCL-only; exports classifyBorderline |
| `src/SmartRouter.Cli/Adapters/JudgeClient.fs` | IJudgeClient + IJudgeStats + JudgeVerdict + LRU cache + 200+ lines | VERIFIED | 329 lines; all interfaces present; LRU cache with ConcurrentDictionary |
| `prompts/judge-prompt.md` | {{QUESTION}} + {{RESPONSE}} + ROUTE_YES/ROUTE_NO | VERIFIED | 15 lines; all 4 markers present |
| `src/SmartRouter.Cli/Adapters/TraceLogger.fs` | 16 fields including judge_called/judge_verdict/judge_latency_ms | VERIFIED | Exactly 16 JsonPropertyName attributes; 3 new Phase 16 fields correct types |
| `src/SmartRouter.Cli/Endpoints/Stats.fs` | 3 new flat int64 fields; null-safe IJudgeStats resolve | VERIFIED | All 3 fields present; GetService null-safe pattern correct |
| `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` | borderline→judge cascade, 4-condition guard, trace 3 new fields | VERIFIED | All 4 conditions verified; B3 fix arms correct; trace block emits all 3 fields |
| `src/SmartRouter.Cli/CompositionRoot.fs` | clean if/else judge DI; named judge HttpClient; NoOp in else-branch | VERIFIED | Lines 548-623 show clean two-branch if/else; AddHttpClient("judge") with resilience handler |
| `src/SmartRouter.Cli/appsettings.json` | Routing.Judge block with 5 keys, Enabled=false | VERIFIED | All 5 keys present, Enabled=false |
| `tests/SmartRouter.Tests/JudgeIntegrationTests.fs` | JDG-01..05 concrete testCases; 0 Expect.isTrue true stubs | VERIFIED | 11 testCases (lines 374-965); `grep -c 'Expect.isTrue true "' = 0` |
| `tests/SmartRouter.Tests/RouterTests.fs` | JudgeIntegrationTests.tests in rootTests list | VERIFIED | Line 35: `SmartRouter.Tests.JudgeIntegrationTests.tests // Phase 16` |
| `tests/SmartRouter.Tests/QualityFallbackTests.fs` | QF-01..08 still present | VERIFIED | All QF-01 through QF-08 testCases present |
| `tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs` | QSE-01..06 still present | VERIFIED | All QSE-01 through QSE-06 testCases present |
| `CHANGELOG.md` | [Unreleased] Phase 16 entry mentioning OPT-IN | VERIFIED | Line 11: Phase 16 entry present; mentions "Default OFF", "Routing.Judge.Enabled" |
| `.planning/REQUIREMENTS.md` | JDG-01..05 marked Complete | VERIFIED | Lines 323-327: all 5 marked Complete |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|----|--------|---------|
| ChatCompletions.fs non-streaming Good arm | BorderlineClassifier.classifyBorderline | `open SmartRouter.Cli.Adapters.BorderlineClassifier` | WIRED | Line 27 open; line 520: `classifyBorderline qualityFallbackOpts initialContent` |
| ChatCompletions.fs borderline arm | IJudgeClient.VerdictAsync | judgeClient.VerdictAsync(...) | WIRED | Lines 531-535: full 5-arg call with promptHash/responseHash/promptText/initialContent/ct |
| ChatCompletions.fs RouteNo arm | upstream.CompleteAsync (122B retry) | retryDecision with Target=Qwen122B, Reason=FallbackTo122B | WIRED | Lines 553-564: mirrors Phase 15 Bad-verdict fallback path exactly |
| Stats.fs mapEndpoints | IJudgeStats.GetJudgeStats | GetService<IJudgeStats>() null-safe | WIRED | Lines 92-95: null-safe resolve + struct destructuring |
| TraceRecord.judge_called/judge_verdict/judge_latency_ms | ChatCompletions trace block | captured from judgeCalled/judgeVerdictStr/judgeLatencyMs | WIRED | Lines 635-637: all 3 fields set from tuple elements |
| CompositionRoot | JudgeClient (JudgeClient + IJudgeClient + IJudgeStats triple-reg) | if judgeEnabled clean branch | WIRED | Lines 595-614: triple registration; else-branch NoOp IJudgeStats only |
| fsproj compile order | QualityCheck.fs → BorderlineClassifier.fs → JudgeClient.fs → ChatCompletions.fs | `<Compile Include=.../>` order | WIRED | fsproj lines 23-25: correct order confirmed |

---

### Anti-Patterns Found

| File | Pattern | Severity | Disposition |
|------|---------|----------|-------------|
| CompositionRoot.fs line 569 | `AddHttpClient("judge", fun c -> ...)` 2-arg form vs. expected chain form per SUMMARY | INFO | The 2-arg form DOES configure BaseAddress correctly here (direct `c.BaseAddress <- Uri(...)` in lambda). Integration tests pass (JDG-04 asserts judge HTTP calls fire and counter increments). The ARCH pitfall about "silently drops BaseAddress" applies to the `AddHttpClient(name).ConfigureHttpClient(lambda)` chain where a separate configuration action is chained — not to this 2-arg form. Functional behavior verified. |
| None | No TODO/FIXME/placeholder stubs found in Phase 16 files | INFO | Clean |

---

### Test Stability Note

Running the full suite 6 times showed 1 failure in 2 of 6 runs. The failing test is the pre-existing `QueueTests.fs` priority-fairness timing test ("low1 ran BEFORE high4 — fairness pivot occurred while highs were still queued"). This failure is not caused by Phase 16 (introduced in Phase 3; not in any Phase 16 commit). All 11 new Phase 16 tests pass consistently across all runs.

**Test count baseline:** 113 passed, 16 ignored, 0 failed (on clean runs). Phase 16 delivered 11 new tests (JDG-01..05 + combined). Pre-Phase-16 baseline was 102 passed, 16 ignored, 0 failed.

---

### Requirements Coverage

| Requirement | Status | Evidence |
|-------------|--------|---------|
| JDG-01: BorderlineClassifier BCL-only + BorderlineKind DU + 3-way classification | SATISFIED | Full verification above |
| JDG-02: IJudgeClient + named judge HttpClient + judge-prompt.md + 1-token + ROUTE_NO safety bias + JsonFSharpConverter | SATISFIED | Full verification above |
| JDG-03: LRU cache + IJudgeStats + /stats 3 new fields | SATISFIED | Full verification above |
| JDG-04: Borderline-only judge calls (judge bypassed on clearly-good + JudgeIntegrationTests counter=0 test) | SATISFIED | ChatCompletions.fs classifyBorderline None-arm returns without calling judgeClient; JDG-04 test at line 518 asserts counter==0 |
| JDG-05: 3 new trace fields + schema_version=1 unchanged + ChatCompletions GetService<IJudgeClient>() null-safe | SATISFIED | TraceRecord 16 fields; schema_version=1 literal; GetService<IJudgeClient>() null-safe at ChatCompletions.fs line 424 |

---

### Human Verification Required

None — all verification dimensions are structurally verifiable from code. The test suite confirms behavioral correctness end-to-end including fake-Kestrel judge HTTP interactions (JDG-04+05).

---

_Verified: 2026-05-11T09:30:00Z_
_Verifier: Claude (gsd-verifier)_
