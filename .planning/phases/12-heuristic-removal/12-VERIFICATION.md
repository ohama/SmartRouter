---
phase: 12-heuristic-removal
verified: 2026-05-09T08:24:31Z
status: passed
score: 7/7 must-haves verified
re_verification: false
---

# Phase 12: Heuristic Removal — Verification Report

**Phase Goal:** Routing decision (35B vs 122B) 을 내리는 heuristic 코드 경로를 완전 제거. ML routing 이 유일한 경로가 된다. archive 브랜치/태그/git history 는 untouched.
**Verified:** 2026-05-09T08:24:31Z
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Heuristic.fs and all its functions (applyHeuristic, scoreComplexity, canonicalKeywords) are permanently gone | VERIFIED | File absent; grep `applyHeuristic\|scoreComplexity\|canonicalKeywords` → 0 hits across src/ and tests/ |
| 2 | RoutingReason.Heuristic DU case is deleted; DecisionLogger.fs formatReason is exhaustive with 5 cases | VERIFIED | Domain.fs RoutingReason has only ExplicitModelOverride/ExplicitTask/Default/ML/FallbackTo35B; DecisionLogger.fs formatReason covers all 5; grep `"| Heuristic "` → 0 hits |
| 3 | Routing.Algorithm config key, RoutingOptions.Algorithm field, routingAlgoStr variable, heuristic dispatch arm all gone | VERIFIED | RoutingOptions record: TimeoutSeconds/ML/TaskTable/ModelAliases only; appsettings.json Routing section: no Algorithm/ComplexityThreshold/Keywords; grep `Routing\.Algorithm\|Routing:Algorithm` → 0 hits |
| 4 | --routing-algorithm CLI flag entirely deleted from Program.fs; --retrain calls configureWithoutMl | VERIFIED | grep `routing-algorithm` → 0 hits; Program.fs line 36 calls `CompositionRoot.configureWithoutMl` directly |
| 5 | configureServices split into configureRequestPipeline + configureWithoutMl (Q1=B); 3 test fixtures use test-stub RoutingAlgorithmRegistration (Q2=B) | VERIFIED | CompositionRoot.fs lines 164/710 define both functions; StreamingTests, LoggingTests, HealthFallbackTests all call `configureWithoutMl` and inject `testStubReg` post-config; LoggingTests routing_algorithm assertion = "ml" |
| 6 | RoutingTests.fs deleted; Tests.fsproj entry removed; RouterTests.fs rootTests entry removed (Q5) | VERIFIED | File absent; SmartRouter.Tests.fsproj has no RoutingTests.fs Compile entry; RouterTests.fs rootTests has only MLRoutingTests.tests |
| 7 | archive/heuristic-baseline branch + v0.5-heuristic-baseline tag preserved (Q7) | VERIFIED | `git branch -a` shows `archive/heuristic-baseline`; `git tag --list` shows `v0.5-heuristic-baseline` |

**Score:** 7/7 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/SmartRouter.Core/Heuristic.fs` | ABSENT | ABSENT | `test ! -f` → OK |
| `tests/SmartRouter.Tests/RoutingTests.fs` | ABSENT | ABSENT | `test ! -f` → OK |
| `scripts/check-routing-isolation.sh` | ABSENT | ABSENT | `test ! -f` → OK |
| `src/SmartRouter.Core/Domain.fs` | RoutingReason 5-case DU, no ComplexityThreshold/Keywords in RoutingConfig | VERIFIED | Confirmed by read; RoutingConfig = {TaskTable; MlThreshold} |
| `src/SmartRouter.Core/Routing.fs` | No canonicalKeywords, defaultRoutingConfig uses only TaskTable+MlThreshold | VERIFIED | grep `canonicalKeywords` → 0 hits |
| `src/SmartRouter.Core/SmartRouter.Core.fsproj` | No Heuristic.fs Compile entry; no new non-BCL deps (ARCH-01) | VERIFIED | fsproj read: compile order Domain→MLPorts→CanaryPorts→ML→Routing→RetrainingPorts→Ports; only FsToolkit.ErrorHandling package reference |
| `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` | formatReason: no Heuristic arm; exhaustive 5-case match | VERIFIED | Lines 30-36 confirmed; TreatWarningsAsErrors=true build clean |
| `src/SmartRouter.Cli/CompositionRoot.fs` | configureRequestPipeline + configureWithoutMl defined; no routingAlgoStr; no heuristic dispatch | VERIFIED | Lines 164/710 confirmed; grep for routingAlgoStr/heuristic dispatch arm → 0 hits |
| `src/SmartRouter.Cli/Program.fs` | No --routing-algorithm flag block; --retrain → configureWithoutMl | VERIFIED | grep `routing-algorithm` → 0 hits; line 36: `CompositionRoot.configureWithoutMl` |
| `src/SmartRouter.Cli/appsettings.json` | No Algorithm/ComplexityThreshold/Keywords in Routing section | VERIFIED | grep → 0 hits; Routing section: TimeoutSeconds/ML/TaskTable/ModelAliases |
| `tests/SmartRouter.Tests/StreamingTests.fs` | configureWithoutMl + testStubReg injection | VERIFIED | Lines 163/179-184 confirmed |
| `tests/SmartRouter.Tests/LoggingTests.fs` | configureWithoutMl + testStubReg + routing_algorithm="ml" assertion | VERIFIED | Lines 221/242 + assertion line 425 confirmed |
| `tests/SmartRouter.Tests/HealthFallbackTests.fs` | configureWithoutMl + manual HealthService/QueueDispatcher + testStubReg | VERIFIED | Lines 118/134-139 confirmed |
| `tests/SmartRouter.Tests/MLRoutingTests.fs` | No applyHeuristic/open Heuristic/Routing:Algorithm refs; 9 test cases | VERIFIED | grep `applyHeuristic\|Routing:Algorithm\|routing-algorithm` → 0 hits |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| Program.fs `--retrain` branch | configureWithoutMl | Direct call line 36 | WIRED | No intermediate heuristic override injection; ML init skipped |
| Program.fs HTTP path | configureRequestPipeline | configureServices alias line 124 | WIRED | alias = configureRequestPipeline; unconditional ML wiring |
| CompositionRoot configureRequestPipeline | ML registration | Unconditional block (no routingAlgoStr guard) | WIRED | Phase 9 Canary/ML block now unconditional within configureRequestPipeline |
| StreamingTests/LoggingTests/HealthFallbackTests | configureWithoutMl | Direct call in fixture setup | WIRED | All 3 fixtures confirmed; testStubReg injected after configureWithoutMl |
| DecisionLogger formatReason | RoutingReason | Exhaustive match (5 cases) | WIRED | No FS0025 incomplete match warning; TreatWarningsAsErrors=true build clean |

---

### Requirements Coverage (Locked Decisions Q1-Q7)

| Decision | Status | Verification |
|----------|--------|--------------|
| Q1=B: configureRequestPipeline + configureWithoutMl split | SATISFIED | CompositionRoot.fs lines 164/710; Program.fs --retrain calls configureWithoutMl |
| Q2=B: 3 test fixtures inject test-stub RoutingAlgorithmRegistration | SATISFIED | All 3 fixtures confirmed; LoggingTests routing_algorithm="ml" |
| Q3=완전 제거: Algorithm key + RoutingOptions.Algorithm + routingAlgoStr + heuristic arm + validation error all gone | SATISFIED | All grep checks → 0 hits; RoutingOptions has no Algorithm field |
| Q4=Yes: --routing-algorithm CLI flag deleted from Program.fs | SATISFIED | grep → 0 hits |
| Q5=전체 삭제: RoutingTests.fs gone; Tests.fsproj entry removed; RouterTests.rootTests entry removed | SATISFIED | File absent; fsproj confirmed; RouterTests.fs rootTests confirmed |
| Q6=Yes: scripts/check-routing-isolation.sh deleted | SATISFIED | File absent |
| Q7=Yes: archive/heuristic-baseline branch + v0.5-heuristic-baseline tag untouched | SATISFIED | git branch -a and git tag --list both confirm presence |

---

### Anti-Patterns Found

None blocking. One stale doc comment noted:

| File | Location | Pattern | Severity | Impact |
|------|----------|---------|----------|--------|
| `src/SmartRouter.Core/Routing.fs` | line 76 doc comment | "fall through to heuristic" (generic English, not code) | Info | None — comment only; no functional routing code |
| `src/SmartRouter.Core/Domain.fs` | line 12, 73 | "heuristic-routed" / "heuristic" in doc comments | Info | None — documentation of pipeline stage names |
| `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` | line 98 | "heuristic" in doc comment | Info | None — comment only |
| `src/SmartRouter.Cli/CompositionRoot.fs` | line 887 | configureServices alias has stale comment ("will be migrated in 12-05" — already done) | Info | Alias still needed by MLRoutingTests + CanaryTests + Program.fs HTTP path; functionally correct |

All info-level only. No blockers.

---

### Build and Test Sanity

```
dotnet build:
  0 warnings, 0 errors — Build succeeded (TreatWarningsAsErrors=true satisfied)

dotnet run --project tests/SmartRouter.Tests -- --sequenced:
  62 tests run — 62 passed, 16 ignored, 0 failed, 0 errored
```

**Phase 11 baseline:** 86 passed + 17 ignored
**Expected Phase 12 baseline (from CONTEXT.md):** ~61 passed
**Actual Phase 12 result:** 62 passed + 16 ignored + 0 failed + 0 errored

Delta from Phase 11:
- RoutingTests.fs deleted: -22 tests
- MLRoutingTests pruned: -3 tests
- ModelsTests.fs gap-closure (commit 748bb79): +3 fixed (previously erroring, now passing via configureWithoutMl migration)
- 1 ignored test resolved to passing: net -1 ignored
- Net: 86 → 62 passed (within CONTEXT.md estimate range of ~61)

---

### ARCH-01 Invariant

Core project (SmartRouter.Core.fsproj) has no new non-BCL dependencies after Phase 12. Heuristic.fs removal cannot add deps. Only package reference remains `FsToolkit.ErrorHandling` (pre-existing). ARCH-01 preserved.

---

## Gaps Summary

No gaps. All 7 observable truths verified. All locked decisions (Q1-Q7) confirmed in actual codebase. Build clean. Tests 62/0/0.

The three stale doc comments using "heuristic" generically in English (Routing.fs, Domain.fs, ChatCompletions.fs) are not routing-decision code and are explicitly deferred per 12-CONTEXT.md ("spec docs heuristic mentions cleanup: Phase 12 scope 외"). The stale configureServices alias comment is cosmetic; the alias itself is correctly wired and still required.

---

_Verified: 2026-05-09T08:24:31Z_
_Verifier: Claude (gsd-verifier)_
