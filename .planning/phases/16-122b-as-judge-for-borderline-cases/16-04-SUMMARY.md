---
phase: 16
plan: 04
subsystem: tests-and-docs
tags: [judge, borderline, fake-kestrel, integration-tests, readme, changelog, requirements]

dependency-graph:
  requires: ["16-01", "16-02", "16-03"]
  provides: ["JudgeIntegrationTests.fs (JDG-01..05)", "README §5.5.5/§7/§8/§9.3", "CHANGELOG [Unreleased]", "REQUIREMENTS.md JDG-01..05 Complete"]
  affects: ["16-SUMMARY.md"]

tech-stack:
  added: []
  patterns:
    - "fake-Kestrel judge integration test (configureWithoutMl + manual DI + ICanaryState stub)"
    - "CountingJudgeHandler (HttpMessageHandler subclass + Interlocked counter)"
    - "AddHttpClient chain form (not 2-arg) to set BaseAddress without silent F# failure"

key-files:
  created:
    - tests/SmartRouter.Tests/JudgeIntegrationTests.fs
  modified:
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs
    - README.md
    - CHANGELOG.md
    - .planning/docs/quality-check-improvement-options.md
    - .planning/REQUIREMENTS.md

decisions:
  - id: A-16-04-1
    choice: "configureWithoutMl + full manual DI for judge integration tests (not configureRequestPipeline)"
    rationale: "configureRequestPipeline requires ML model files (IEmbedder) unavailable in test env; configureWithoutMl mirrors exact QualityFallbackTests pattern"
  - id: A-16-04-2
    choice: "ICanaryState stub required for /stats endpoint in judge tests"
    rationale: "configureWithoutMl does not register ICanaryState; Stats.mapEndpoints calls GetRequiredService<ICanaryState>; added full 5-member stub"
  - id: A-16-04-3
    choice: "AddHttpClient chain form (not 2-arg) in unit tests for JudgeClient"
    rationale: "2-arg AddHttpClient(name, fun c -> ...) silently drops BaseAddress in F# (ARCH pitfall); chain form .AddHttpClient().ConfigureHttpClient(...) is required"
  - id: A-16-04-4
    choice: "JDG-01 entropy fixture: String.replicate 20 'abcdefgh' (entropy = log2(8) = 3.0)"
    rationale: "Original fixture 'abcabc'×30 has entropy log2(3)=1.585 which is BELOW EntropyThreshold(2.5); classifyBorderline returned None (correct — below threshold means Bad, not borderline)"

metrics:
  duration: "~40 min"
  completed: "2026-05-11"

test-baseline:
  before: "102 passed, 16 ignored, 0 failed"
  after: "113 passed, 16 ignored, 0 failed"
  new-tests: 11
---

# Phase 16 Plan 04: Tests and Docs Summary

**One-liner:** Concrete fake-Kestrel JDG-01..05 test suite (11 testCases, 0 stubs) + README §5.5.5/§7/§8/§9.3 sync + CHANGELOG + REQUIREMENTS.md alignment.

## Tasks Completed

| Task | Name | Commits | Files |
|---|---|---|---|
| 1 | Create JudgeIntegrationTests.fs (JDG-01..05 + integration) | a0c1c8c | JudgeIntegrationTests.fs, SmartRouter.Tests.fsproj, RouterTests.fs |
| 2a | README §5.5/§7/§8/§9.3 update | dae2604 | README.md |
| 2b | CHANGELOG + quality-check-improvement-options.md | afba9f2 | CHANGELOG.md, .planning/docs/quality-check-improvement-options.md |
| 2c | REQUIREMENTS.md JDG-01 alignment + JDG-01..05 Complete | 6264825 | .planning/REQUIREMENTS.md |

## Test Coverage

### JDG-01: BorderlineClassifier 3-way classification (3 testCases)
- `None` for clearly good (high entropy + long length)
- `Some UncertainLength` for length in [30, 45)
- `Some UncertainEntropy` for entropy in [2.5, 3.5)

### JDG-02: JudgeClient parser + template loading (2 testCases)
- ROUTE_NO wins on collision (safety bias) — verified via CountingJudgeHandler
- JudgeSkipped when prompt template file missing

### JDG-03: LRU cache hit/miss + counters (1 testCase)
- Same (promptHash, responseHash) → cache hit on 2nd call
- IJudgeStats: hits=1, misses=1, callCount=1 verified

### JDG-04: Fake-Kestrel judge HTTP counter (2 testCases)
- Clearly-good 35B response → judge HTTP counter = 0
- Borderline 35B response → judge counter = 1; 2nd identical → cache hit (counter still 1)

### JDG-05: Trace JSONL + /stats (2 testCases)
- judge disabled: judge_called=false, judge_verdict=null, judge_latency_ms=null; schema_version=1
- After miss+hit: /stats judge_cache_hits=1, judge_cache_misses=1, judge_call_count=1

### Combined (1 testCase)
- Borderline → judge fires → trace judge_called=true / verdict="yes" / latency_ms >= 0.0
- /stats: judge_cache_hits=0, judge_cache_misses=1, judge_call_count=1

## README Sections Updated (CLAUDE.md sync rule)

- **§5.5.5** (new subsection): Borderline judge cascade behavior, OPT-IN guidance, keyword/finish_reason exclusion note
- **§7 Routing.Judge** (new subsection): 5-key config table + operator workflow (5 steps)
- **§8 /stats** (new counters): judge_cache_hits, judge_cache_misses, judge_call_count
- **§9.3 Trace schema** (new fields): judge_called, judge_verdict, judge_latency_ms + jq workflows + schema_version=1 note

CLAUDE.md 12-area gate satisfied: §2 (brief architecture note), §5 (cascade), §7 (config), §8 (endpoints), §9.3 (trace schema). §9.1 DecisionLog and §9.6-9.9 operational log CORRECTLY UNCHANGED.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] QualityFallbackTests.startTestRouter is private**

- **Found during:** Task 1 JDG-05 test (plan said to use `QualityFallbackTests.startTestRouter`)
- **Issue:** `startTestRouter` is `let private` in QualityFallbackTests.fs — not accessible cross-module
- **Fix:** Rewrote JDG-05 disabled test with inline `configureWithoutMl` + full manual DI (same registrations as QualityFallbackTests.startTestRouter)
- **Files modified:** JudgeIntegrationTests.fs

**2. [Rule 1 - Bug] configureRequestPipeline requires ML model files (IEmbedder)**

- **Found during:** Task 1 first test run
- **Issue:** `configureRequestPipeline` registers ML pipeline (IEmbedder, IClassifier) which requires model files; test environment lacks these
- **Fix:** Switched all integration test router builds to `configureWithoutMl` + manual DI registrations (mirrors QualityFallbackTests pattern)
- **Files modified:** JudgeIntegrationTests.fs

**3. [Rule 1 - Bug] ICanaryState not registered by configureWithoutMl**

- **Found during:** Task 1 test run — /stats endpoint threw InvalidOperationException
- **Issue:** Stats.mapEndpoints calls `GetRequiredService<ICanaryState>()` but `configureWithoutMl` does not register it
- **Fix:** Added full ICanaryState stub (5 members) to both `startTestRouterWithJudge` and JDG-05 disabled test inline setup
- **Files modified:** JudgeIntegrationTests.fs

**4. [Rule 1 - Bug] 2-arg AddHttpClient silently drops BaseAddress in F#**

- **Found during:** Task 1 JDG-02/JDG-03 test run — HttpClient threw "invalid request URI" for relative path
- **Issue:** 2-arg `services.AddHttpClient("judge", fun c -> c.BaseAddress <- ...)` silently fails to set BaseAddress (ARCH pitfall documented in CompositionRoot.fs)
- **Fix:** Replaced with chain form `.AddHttpClient("judge").ConfigureHttpClient(...).ConfigurePrimaryHttpMessageHandler(...)`
- **Files modified:** JudgeIntegrationTests.fs

**5. [Rule 1 - Bug] JDG-01 entropy fixture computed wrong entropy**

- **Found during:** Task 1 test run — JDG-01 entropy test failed with "got None"
- **Issue:** `String.replicate 30 "abcabc"` has entropy = log2(3) = 1.585, which is BELOW EntropyThreshold(2.5); `classifyBorderline` correctly returns None (response would be Bad before reaching borderline check). Fixture was wrong, not the code.
- **Fix:** Changed fixture to `String.replicate 20 "abcdefgh"` (8 distinct chars, entropy = log2(8) = 3.0 ∈ [2.5, 3.5))
- **Files modified:** JudgeIntegrationTests.fs

## Decisions Made

| Decision | Choice | Rationale |
|---|---|---|
| Test router DI pattern | configureWithoutMl + manual registration | No ML model files in test env; exact mirror of Phase 14 QualityFallbackTests |
| ICanaryState stub | Full 5-member stub | /stats endpoint requires GetRequiredService<ICanaryState>; configureWithoutMl omits it |
| Named HttpClient registration | Chain form only | 2-arg F# form silently drops BaseAddress (ARCH-pitfall; CompositionRoot comment) |
| Entropy fixture | "abcdefgh"×20 (log2(8)=3.0) | "abcabc"×30 produces entropy 1.585 (below threshold); borderline requires [2.5, 3.5) |

## Next Phase Readiness

Phase 16 complete. Phase 17 (QualityClassifier distillation endgame) can begin — prerequisites: Phase 16 trace fields (judge_verdict) available for QCLS-03 dataset extraction.
