---
phase: 17-hard-rules-layer-and-routing-mode-switch
verified: 2026-05-11T14:50:00Z
status: passed
score: 18/18 must-haves verified
re_verification: false
---

# Phase 17: Hard Rules Layer + Routing.Mode Switch — Verification Report

**Phase Goal:** Ship Stage 0 keyword pre-routing (LLVM/MLIR/compiler/segfault/optimization/concurrency → 122B) + Routing.Mode config switch (selfrouting/ml; default selfrouting) for ML dormancy. Foundation for v2.0 selfrouting cascade. Hard Rules applies to BOTH streaming and non-streaming. schema_version=1 unchanged (additive `routing_reason="hard_rule"` enum). ML adapters DI-registered in both modes (MODE-03); only `RoutingAlgorithmRegistration.Algorithm` branches.

**Verified:** 2026-05-11T14:50:00Z
**Status:** PASSED
**Re-verification:** No — initial verification
**Test suite:** 137 passed, 17 ignored, 0 failed

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|---------|
| 1 | `applyHardRules` returns Some Qwen122B+HardRule for any of 6 keywords (case-insensitive) | VERIFIED | `HardRules.fs:17-33` — keyword scan with OrdinalIgnoreCase; 16 HardRulesTests all pass |
| 2 | `applyHardRules` returns None when no keyword present | VERIFIED | `HardRules.fs:33` `else None`; test "no keyword returns None" + "empty content returns None" pass |
| 3 | `routeRequest` calls Hard Rules as Stage 0 BEFORE `tryModelOverride` | VERIFIED | `Routing.fs:110-116` — `match HardRules.applyHardRules req` precedes `match tryModelOverride req`; "Hard Rule beats model override (Stage 0 > Stage 1)" test passes |
| 4 | `formatReason` emits `"hard_rule"` for `HardRule` DU case; no catch-all arm | VERIFIED | `DecisionLogger.fs:38` `| HardRule -> "hard_rule"`; 0 `| _ ->` arms in `formatReason`; schema_version=1 unchanged |
| 5 | Hard Rules applies to BOTH streaming and non-streaming (HR-05) | VERIFIED | `ChatCompletions.fs:239` — single `routeRequest` call site covers both stream=true and stream=false branches; no separate Hard Rules call needed |
| 6 | `Routing.Mode="selfrouting"` (default) registers stub returning Qwen35B/Default/`Name="selfrouting"` | VERIFIED | `CompositionRoot.fs:462-477`; test "Routing.Mode=selfrouting registers selfrouting algorithm" + default test pass |
| 7 | `Routing.Mode="ml"` registers v1.x ML closure unchanged | VERIFIED | `CompositionRoot.fs:428-461`; ml-mode branch verbatim from v1.3; ml test skip-guarded (ONNX absent on this host) |
| 8 | Invalid `Routing.Mode` fails startup with `InvalidOperationException` | VERIFIED | `CompositionRoot.fs:407-413`; tests "throws InvalidOperationException" + "error message lists valid values" pass |
| 9 | ML adapters DI-registered unconditionally in BOTH modes (MODE-03) | VERIFIED | `CompositionRoot.fs:366-388` — `AddSingleton<IEmbedder>`, `AddKeyedSingleton<IClassifier>` × 2, `AddHostedService<RetrainingService>`, `AddHostedService<CanaryService>` all appear BEFORE `routingMode` block at line 402 |
| 10 | `HardRules.fs` BCL-only, no ARCH-01 violations | VERIFIED | `grep -E "Microsoft.ML|Serilog|HttpClient|AspNetCore" HardRules.fs` → 0 matches; opens only `SmartRouter.Core.Domain` |
| 11 | `RoutingReason` DU has exactly 7 cases including `HardRule` | VERIFIED | `Domain.fs:31-38` — 7 cases; `HardRule` is 7th with Phase 17 comment |
| 12 | Keyword list hardcoded in `HardRules.fs`, NOT exposed in appsettings.json | VERIFIED | `HardRules.fs:9-10` private keywords list; `grep "HardRules" appsettings.json` → 0 matches |
| 13 | `appsettings.json` has `"Mode": "selfrouting"` as first key in Routing block | VERIFIED | `appsettings.json:14` — `"Mode": "selfrouting"` first under `"Routing"` |
| 14 | README §5 documents 4-stage pipeline with §5.0 Hard Rules subsection | VERIFIED | `README.md:121-162` — §5.0 "Hard Rules pre-routing (Stage 0, Phase 17)" + §5.1 "Four-stage decision" code block; 12 hits for Hard Rules/Stage 0 |
| 15 | README §7 documents `Routing.Mode` key | VERIFIED | `README.md:310` — `Routing.Mode` row with values + default + Phase 17 note |
| 16 | README §9.1 lists `hard_rule` (routing_reason) + `selfrouting` (routing_algorithm); schema_version=1 unchanged | VERIFIED | `README.md:531-532`; §2 also updated (line 39: "4 stages, pure" + Stage 0 Hard Rules in diagram) |
| 17 | CHANGELOG `[Unreleased]` has Added + Changed + Notes for Phase 17 | VERIFIED | `CHANGELOG.md:8-30` — paradigm pivot, Hard Rules, Routing.Mode, schema_version=1 note; `[1.3.0]` section intact |
| 18 | REQUIREMENTS.md HR-06 + MODE-03 wording fixed; ROADMAP SC-2 + 17-02 description fixed | VERIFIED | HR-06 now says "Hard Rules wins"; wording fix footnote present; MODE-03 says "remain compiled and DI-registered in BOTH modes"; ROADMAP SC-2 says "routes to **122B** with routing_reason=\"hard_rule\""; 17-02 description says "routeRequest shared call site" |

**Score:** 18/18 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/SmartRouter.Core/HardRules.fs` | Pure BCL module with `applyHardRules` | VERIFIED | 33 lines; no ARCH-01 violations; module-level keyword list; `applyHardRules : RouterRequest -> RoutingDecision option` |
| `src/SmartRouter.Core/Domain.fs` | `RoutingReason` with 7th case `HardRule` | VERIFIED | Line 38: `| HardRule // Phase 17: ...` |
| `src/SmartRouter.Core/Routing.fs` | `routeRequest` with Stage 0 Hard Rules | VERIFIED | Lines 105-121; Stage 0 comment + `HardRules.applyHardRules req` BEFORE `tryModelOverride` |
| `src/SmartRouter.Core/SmartRouter.Core.fsproj` | `HardRules.fs` between `RetrainingPorts.fs` and `ML.fs` | VERIFIED | Line 11: `<Compile Include="HardRules.fs" />` in correct position |
| `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` | 7-arm exhaustive `formatReason` including `HardRule` | VERIFIED | Lines 30-38; `| HardRule -> "hard_rule"`; no `| _ ->` arm |
| `src/SmartRouter.Cli/CompositionRoot.fs` | `Routing.Mode` read + validation + branched factory | VERIFIED | Lines 402-477; fail-fast `InvalidOperationException` on invalid value; `match routingMode with | "ml" -> ... | _ -> selfrouting stub` |
| `src/SmartRouter.Cli/appsettings.json` | `"Mode": "selfrouting"` default | VERIFIED | Line 14 under `"Routing"` block |
| `tests/SmartRouter.Tests/HardRulesTests.fs` | 16 tests (6 keywords + case + cascade + formatReason) | VERIFIED | 124 lines; 16 test cases listed by `--list-tests`; all pass |
| `tests/SmartRouter.Tests/ModeSwitchTests.fs` | 9 tests (invalid mode + selfrouting + ml + cascade) | VERIFIED | 284 lines; 9 test cases; ml-mode test skip-guarded when ONNX absent (1 skipped → counted in 17 ignored total) |
| `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` | HardRulesTests + ModeSwitchTests before RouterTests | VERIFIED | Lines 38-41: `HardRulesTests.fs` → `ModeSwitchTests.fs` → `RouterTests.fs` |
| `tests/SmartRouter.Tests/RouterTests.fs` | `rootTests` includes both new modules | VERIFIED | Lines 36-37: `HardRulesTests.tests` + `ModeSwitchTests.tests` |
| `README.md` | §5.0 + §5.1 4-stage + §7 Routing.Mode + §9.1 hard_rule + §2 4-stage | VERIFIED | All 5 updates confirmed |
| `CHANGELOG.md` | `[Unreleased]` Added + Changed + Notes | VERIFIED | Lines 8-30 |
| `.planning/REQUIREMENTS.md` | HR-06 + MODE-03 wording corrected | VERIFIED | HR-06 says "Hard Rules wins"; MODE-03 says "remain compiled and DI-registered in BOTH modes" |
| `.planning/ROADMAP.md` | SC-2 + 17-02 description corrected | VERIFIED | SC-2: 122B + hard_rule; 17-02: routeRequest shared call site |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `Routing.fs` | `HardRules.fs` | `HardRules.applyHardRules req` at Stage 0 | WIRED | `Routing.fs:110`; BEFORE `tryModelOverride` at line 114 |
| `DecisionLogger.fs` | `Domain.fs` | Exhaustive match on `RoutingReason.HardRule` | WIRED | `DecisionLogger.fs:38`; no catch-all arm; compile-time enforced |
| `RouterTests.fs` | `HardRulesTests.fs` | `HardRulesTests.tests` in rootTests | WIRED | `RouterTests.fs:36` |
| `RouterTests.fs` | `ModeSwitchTests.fs` | `ModeSwitchTests.tests` in rootTests | WIRED | `RouterTests.fs:37` |
| `CompositionRoot.fs` | `appsettings.json` | `config.["Routing:Mode"]` direct read | WIRED | `CompositionRoot.fs:403`; mirrors Phase 16 `Routing:Judge:Enabled` pattern |
| `RoutingAlgorithmRegistration` factory | Mode-branched closure | `match routingMode with | "ml" -> ... | _ -> selfrouting stub` | WIRED | `CompositionRoot.fs:427-477` |
| `ChatCompletions.fs` | `Routing.routeRequest` | Single shared call site line 239 | WIRED | Both streaming and non-streaming paths use same `routeRequest`; HR-05 satisfied transparently |

---

### Requirements Coverage

| Requirement | Status | Evidence |
|-------------|--------|---------|
| HR-01 (BCL-only pure `applyHardRules`) | SATISFIED | HardRules.fs BCL-only; ARCH-01 clean |
| HR-02 (6 keywords hardcoded, not configurable) | SATISFIED | `keywords` list private in HardRules.fs; 0 matches in appsettings.json |
| HR-03 (Stage 0 before model override) | SATISFIED | Routing.fs cascade ordering verified by code and test |
| HR-04 (RoutingReason.HardRule + formatReason "hard_rule") | SATISFIED | Domain.fs + DecisionLogger.fs; schema_version=1 |
| HR-05 (streaming + non-streaming) | SATISFIED | Single routeRequest call site in ChatCompletions.fs:239 |
| HR-06 (cascade ordering tests + wording fix) | SATISFIED | 4 cascade tests in HardRulesTests + 1 in ModeSwitchTests; REQUIREMENTS wording corrected |
| MODE-01 (config key + fail-fast) | SATISFIED | appsettings.json:14; CompositionRoot fail-fast validation |
| MODE-02 (branched factory) | SATISFIED | selfrouting stub + ml closure |
| MODE-03 (ML adapters DI-registered in both modes) | SATISFIED | `AddSingleton<IEmbedder>` etc. above routingMode block; unconditional |
| MODE-04 (README + CHANGELOG) | SATISFIED | README §5/§7/§9.1/§2 + CHANGELOG [Unreleased] |

---

### Anti-Patterns Found

None. No blockers, no warnings.

- No `| _ ->` catch-all arms in `formatReason` — compile-time DU exhaustiveness preserved.
- No TODOs/FIXMEs in HardRules.fs, Routing.fs, or DecisionLogger.fs Phase 17 additions.
- `ModelVersion = ""` in `applyHardRules` return value is the established convention (ChatCompletions falls back to `IModelVersionProvider.CurrentVersion`) — not a stub anti-pattern.
- Selfrouting stub returning Qwen35B/Default is intentional (Phase 19 replaces with real self-classify), documented in code comments + CHANGELOG.

---

### Human Verification Required

None required for structural verification. All automated checks pass.

Optional smoke tests an operator could run after deploying:

1. **Test:** `POST /v1/chat/completions` with `{"messages":[{"role":"user","content":"debug LLVM pass"}]}`
   **Expected:** Response from Qwen 122B; DecisionLog `routing_reason="hard_rule"`, `routing_algorithm="selfrouting"`, `model_version="selfrouting-v1"`

2. **Test:** Set `Routing.Mode="invalid"` in appsettings.json and restart.
   **Expected:** Process exits immediately with `InvalidOperationException` message containing "is not recognized; valid values are 'selfrouting' or 'ml'"`

---

## Gaps Summary

No gaps. All 18 must-haves verified.

---

_Verified: 2026-05-11T14:50:00Z_
_Verifier: Claude (gsd-verifier, claude-sonnet-4-6)_
