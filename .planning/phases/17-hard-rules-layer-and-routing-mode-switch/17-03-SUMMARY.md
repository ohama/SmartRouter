---
phase: 17-hard-rules-layer-and-routing-mode-switch
plan: "03"
subsystem: routing-documentation
tags: [tests, readme, changelog, requirements, modeswitch, hardrules, cascade, di-integration]

dependency_graph:
  requires: [17-01, 17-02]
  provides:
    - ModeSwitchTests.fs (9 integration tests for Routing.Mode validation + cascade ordering)
    - README §5.0 Hard Rules subsection + §5.1 Four-stage diagram + §7 Routing.Mode row + §9.1 hard_rule/selfrouting values
    - CHANGELOG [Unreleased] documenting Phase 17 paradigm pivot
    - REQUIREMENTS.md HR-06 + MODE-03 wording corrections
    - ROADMAP.md SC-2 + 17-02 description corrections
  affects: [18-01, 19-04]

tech_stack:
  added: []
  patterns:
    - name: DI-integration-test-without-ML-bootstrap
      description: >
        Build in-memory IConfiguration omitting Routing:ML section so that
        configureRequestPipeline skips ensureEmbeddingFilesPresent (line 340 null guard).
        Selfrouting-mode DI tests run on all hosts regardless of ONNX file presence.
        ML-mode test skip-guarded with File.Exists(onnxEmbedPath) — W4 pattern from Phase 6.
    - name: README-prepend-subsection
      description: >
        Added §5.0 before §5.1 (instead of renumbering §5.5+). Preserves all existing
        anchor links (CLAUDE.md "prefer adding sub-section over renumbering" rule).

key_files:
  created:
    - tests/SmartRouter.Tests/ModeSwitchTests.fs
  modified:
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs
    - README.md
    - CHANGELOG.md
    - .planning/REQUIREMENTS.md
    - .planning/ROADMAP.md

decisions:
  - id: mode-switch-test-no-onnx
    description: >
      Tests that call configureRequestPipeline for selfrouting mode use a minimal
      in-memory config WITHOUT Routing:ML section so that mlOpts=null and the
      ensureEmbeddingFilesPresent bootstrap is skipped. ML-mode test uses W4 skip guard.
    rationale: >
      ONNX embedding files are not present in the dev/test environment. Without this
      approach, all 9 ModeSwitch tests would error with FileNotFoundException.
    alternatives_rejected:
      - "Use production appsettings.json (plan originally suggested this): mlOpts non-null → embedding file check throws"
      - "Mock ensureEmbeddingFilesPresent: requires production code change (violates ARCH-01)"

metrics:
  duration: ~15 minutes
  completed: "2026-05-11"
  test_delta: "+9 ModeSwitch tests (8 passing + 1 skip-guarded ml-mode test on host without ONNX files)"
  test_count_after: "137 passed, 17 ignored, 0 failed"
---

# Phase 17 Plan 03: Tests-and-Docs Summary

**One-liner:** ModeSwitchTests (9 tests: DI-integration via minimal config, no-ONNX guard) + README §5.0/§7/§9.1 Hard Rules + Routing.Mode + CHANGELOG paradigm pivot + REQUIREMENTS HR-06/MODE-03 wording fixes aligned with STATE.md decision 5.

## What Was Built

### Task 1: ModeSwitchTests.fs (9 integration tests)

Key design decision: the test fixture uses a minimal in-memory IConfiguration that deliberately omits the `Routing:ML` section. This causes `mlOpts=null` in `configureRequestPipeline` (CompositionRoot line 339), skipping the `ensureEmbeddingFilesPresent` bootstrap that would throw `FileNotFoundException` on hosts without ONNX files (the local dev environment).

Tests cover:
1. Invalid mode (`"totally-bogus-mode"`) throws `InvalidOperationException` at startup (MODE-01)
2. Invalid mode error message mentions "is not recognized", "selfrouting", "ml"
3. `"selfrouting"` → `Name="selfrouting"`, `ModelVersion="selfrouting-v1"` (MODE-02)
4. Default (no override) → selfrouting (appsettings.json default flows through)
5. Case-insensitive: `"SelfRouting"` normalizes to selfrouting
6. `"ml"` → `Name="ml"`, `ModelVersion` starts with "ml-" (skip-guarded if ONNX absent)
7. selfrouting + LLVM → Qwen122B / HardRule reason (end-to-end cascade)
8. selfrouting + non-keyword → Qwen35B / Default reason (stub algorithm)
9. selfrouting + model=35b + LLVM → Qwen122B / HardRule (Hard Rules beats override; HR-06 corrected)

### Task 2: README updates

- §2 Architecture: "3 stages" → "4 stages"; routing algorithm description updated for mode-dependency
- §5.0 Hard Rules pre-routing (Stage 0, Phase 17): new subsection with keywords, rationale, hardcoded-not-configurable rationale, streaming applicability
- §5.1 renamed "Four-stage decision"; Stage 0 prepended in code block; Stage 3 noted as mode-dependent
- §5.5 Quality fallback + §5.5.5 Borderline judge: unchanged (no renumbering)
- §7 Routing table: `Routing.Mode` row added (first row; default selfrouting; ml=v1.x)
- §9.1 routing_reason: `hard_rule` enum value added; schema_version=1 unchanged note
- §9.1 routing_algorithm: `selfrouting` added alongside `ml` and `ml-canary`

### Task 3: CHANGELOG.md [Unreleased]

Added Added + Changed + Notes blocks covering the Phase 17 paradigm pivot. Key items:
- Hard Rules (keywords, hardcoded, streaming-applies, schema_version=1 unchanged)
- Routing.Mode config key (selfrouting default, ml rollback, fail-fast invalid)
- routing_algorithm=selfrouting DecisionLog value
- Default paradigm flip ML → selfrouting (ML adapters DI-registered in both modes)
- 3→4 stage pipeline change

### Task 4: REQUIREMENTS.md + ROADMAP.md alignment

REQUIREMENTS.md:
- HR-06: corrected "BYPASS" wording to "Hard Rules wins over model override" (STATE.md decision 5)
- HR-06 footnote: explains the wording change with date attribution
- MODE-03: corrected "not registered in routing path" to "remain compiled and DI-registered in BOTH modes; Algorithm closure branches"

ROADMAP.md:
- SC-2 (B1): LLVM+model=35b example corrected to route to **122B** with routing_reason=hard_rule (not 35B)
- 17-02 description (B2): ChatCompletions.fs removed as modification target; HR-05 explained via routeRequest shared call site

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 — Bug] Test fixture strategy: AddJsonFile → minimal in-memory config**

- **Found during:** Task 1 test execution
- **Issue:** Plan suggested loading production `appsettings.json` via `AddJsonFile`. The production appsettings.json includes a `Routing:ML` section, causing `mlOpts` to be non-null, which triggers `ensureEmbeddingFilesPresent` → `FileNotFoundException` (ONNX files absent in dev environment). All 9 tests errored.
- **Fix:** Built minimal in-memory config dictionary omitting `Routing:ML` section entirely. `mlOpts=null` → ML bootstrap skipped. Added W4 skip guard for ml-mode test (checks `File.Exists(onnxEmbedPath)`).
- **Files modified:** `tests/SmartRouter.Tests/ModeSwitchTests.fs`
- **Impact:** Tests pass on all hosts; behavior verified identical (configureRequestPipeline selfrouting path uses no ML services)

**2. [Rule 2 — Missing] §2 Architecture updated**

- **Found during:** Task 2 README review
- **Issue:** §2 Architecture diagram explicitly said "Routing (3 stages, pure) → 1. model override → 2. task table → 3. ML" and paragraph said "Stage 3 always runs ML" — both stale after Phase 17.
- **Fix:** Updated diagram to "4 stages" with Stage 0 Hard Rules and mode-dependent Stage 3; updated paragraph to describe both modes.
- **Files modified:** `README.md`

## Open Questions Resolved

- **Q1 (HR-03 vs HR-06 wording conflict):** Fully resolved in Plans 17-01 (cascade ordering implementation in code) + 17-03 Task 4 (REQUIREMENTS.md wording correction + footnote).
- **Q2 (RoutingOptions.Mode vs direct read):** Resolved in Plan 17-02 (direct read chosen).
- **Q3 (Startup banner):** No banner change needed; `regn.Name` carries mode implicitly.
- **Q4 (HardRulesTests in rootTests):** Done in Plan 17-01.

## Phase 17 Closeout

All 10 requirements covered:

| Req | Plan | Status |
|-----|------|--------|
| MODE-01 | 17-02 (fail-fast validation) + 17-03 (MODE switch test) | Done |
| MODE-02 | 17-02 (branch registration) + 17-03 (selfrouting/ml tests) | Done |
| MODE-03 | 17-02 (implementation) + 17-03 (wording fix) | Done |
| MODE-04 | 17-03 (README §7 + CHANGELOG) | Done |
| HR-01 | 17-01 (HardRules.fs) | Done |
| HR-02 | 17-01 (hardcoded keyword list) | Done |
| HR-03 | 17-01 (Stage 0 in routeRequest) | Done |
| HR-04 | 17-01 (RoutingReason.HardRule DU + formatReason) | Done |
| HR-05 | 17-01 (routeRequest shared call site; both branches) | Done |
| HR-06 | 17-01 (tests) + 17-03 (wording fix) | Done |

## Next Phase Readiness

Phase 18 (Session Store + Sticky Escalation) can begin:
- README §5.0 anchor established for Phase 18 SUMMARY cross-reference
- Phase 17 cascade order locked (Stage 0 Hard Rules → Stage 1 model override → Stage 2 task table → Stage 3 algorithm); Phase 18 inserts Stage 3 sticky escalation between algorithm and the default
- No blockers identified
