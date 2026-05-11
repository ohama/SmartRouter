# Phase 12 — Heuristic Routing Removal: Research

**Drafted:** 2026-05-09
**Author session:** parallel session (write-target restricted to `.planning/preparing/`)
**Scope clarified by user:** the *routing decision* heuristic only — `applyHeuristic` 함수와 그것이 부르는 자료(`scoreComplexity`, `Keywords`, `ComplexityThreshold`)와 그 dispatch / config / test 까지. Log schema 의 `"heuristic:score=N"` 같은 문자열은 그 DU case 가 사라지면 자동으로 같이 사라지므로 separate decision 아님. `archive/heuristic-baseline` 브랜치 / `v0.5-heuristic-baseline` 태그 / git history / 메모리 파일 등은 untouched.

---

## 1. Surface area (file-by-file)

### 1.1 Core (Pure F#)

| File | Lines | What needs to change | Hits |
|---|---|---|---|
| `src/SmartRouter.Core/Heuristic.fs` | 45 | **DELETE** the whole file. `scoreComplexity` + `applyHeuristic`; nothing else lives here. | 4 |
| `src/SmartRouter.Core/Domain.fs` | — | **DELETE** `Heuristic of score: int` case from `RoutingReason` DU (line 34). Adjacent cases stay: `ExplicitModelOverride`, `ExplicitTask`, `Default`, `ML`, `FallbackTo35B`. | 5 |
| `src/SmartRouter.Core/Domain.fs` | — | **DELETE** `Keywords : string list` and `ComplexityThreshold : int` fields from `RoutingConfig` record (lines ~92–94). They are read ONLY by `scoreComplexity`. | (in same 5) |
| `src/SmartRouter.Core/Routing.fs` | — | **UPDATE** docstring on line 96 (`/// pluggable algorithm (Heuristic.applyHeuristic or ML.applyML).` → drop heuristic mention). **DELETE** `canonicalKeywords` constant (lines ~123). **UPDATE** `defaultRoutingConfig` to drop `Keywords` / `ComplexityThreshold` fields. | 2 |
| `src/SmartRouter.Core/SmartRouter.Core.fsproj` | — | **DELETE** `<Compile Include="Heuristic.fs" />` line (line 8). All other ordering preserved. | 1 |

### 1.2 Cli (adapters / wiring / endpoint)

| File | What changes | Hits |
|---|---|---|
| `src/SmartRouter.Cli/CompositionRoot.fs` | **DELETE** the entire `null \| "" \| "heuristic"` arm of `RoutingAlgorithmRegistration` selection (lines 348–358). The `"ml"` arm becomes the only valid path. **DELETE** the validation error case `"valid values: \"heuristic\", \"ml\""` (line 391) → only `"ml"` is valid. **UPDATE** `RoutingOptions.Algorithm` doc-comment (line 72: `// "heuristic" (default) \| "ml"; null when key absent`). **UPDATE** the `routingAlgoStr` defaulting block (lines 295–296: `if … then "heuristic"` → either remove the variable entirely or default to `"ml"`). **UPDATE** the `mlThreshold` defensive default comment (line 112: "heuristic-only deployments" wording). **UPDATE** comment on line 270 ("Heuristic mode gets a no-op pair"). The ML wiring guards (`routingAlgoStr = "ml"`, lines 299, 589) become unconditional once heuristic is gone. | 17 |
| `src/SmartRouter.Cli/Program.fs` | **DELETE** the `--retrain` heuristic-injection block (lines 33–41) — see §3 (Open Question 1) for the replacement. **DELETE** the `--routing-algorithm` CLI flag entirely (lines 117–153) OR shrink validation to accept only `"ml"` (the single-valid-value form is dead code; recommend full deletion of the flag). **UPDATE** comment on line 157 ("heuristic mode default"). | 8 |
| `src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs` | **UPDATE** doc comments (lines 11–14, 23): drop "Heuristic.applyHeuristic OR" wording; record itself stays — `Algorithm`/`Name`/`ModelVersion` triple is still useful. | 3 |
| `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` | **DELETE** `\| Heuristic score → sprintf "heuristic:score=%d" score` line in `formatReason` (line 34). The line goes away because the DU case is deleted; this is mechanical fallout, not a separate decision. | 2 |
| `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` | No code change required — it never calls `applyHeuristic` directly; it goes through `routeRequest config algorithm req` with the algorithm injected via DI. The algorithm pointer just always resolves to the ML closure. (1 grep hit is via `RoutingAlgorithmRegistration` import.) | 1 |
| `src/SmartRouter.Cli/Adapters/CanaryGate.fs`, `CanaryMetrics.fs`, `QwenUpstreamClient.fs`, `Core/CanaryPorts.fs` | Each has 1 grep hit but they're all comment-only mentions (e.g. "heuristic mode" historical notes). **UPDATE** comments only; no logic change. | 4 |
| `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` | No change. `RoutingAlgorithm.fs` stays (the registration record is still used by ML). | 0 |
| `src/SmartRouter.Cli/logs/decisions/2026-05-08.jsonl` | Stale dev artifact. Not part of build. **DELETE** (or leave; harmless). | 1 |

### 1.3 Tests

| File | What changes | Hits |
|---|---|---|
| `tests/SmartRouter.Tests/RoutingTests.fs` | **HEAVY**. 16 hits. The `route` helper passes `applyHeuristic` directly into `routeRequest` (line 30). Tests on lines 138/151 pattern-match `\| Heuristic s → ...` to assert score behavior. Most stage-3 routing tests EXIST SOLELY to test heuristic semantics (score < threshold, score ≥ threshold, keyword count escalation, edited threshold, edited keywords). **DECISION:** these tests die with the algorithm. We either (a) **delete** them and accept the loss of stage-3 coverage at the unit level (stage-1 + stage-2 coverage remains), or (b) **replace** them with a minimal test-only no-op algorithm `let testAlgo cfg req = { Target=Qwen35B; Priority=Low; Reason=ML; IsFallback=false; ModelVersion="" }` and keep the stage-pipeline tests (stage 3 falls through, returns 35B). Recommend (b) for stage-pipeline structural tests + (a) for the heuristic-internal tests (score thresholds, keyword counts). | 16 |
| `tests/SmartRouter.Tests/MLRoutingTests.fs` | Test on line 43 — `"Heuristic.applyHeuristic and ML.makeApplyML closure both satisfy RoutingAlgorithm"` — exists ONLY to prove the function-type alias accepts both algorithms. **DELETE** that test. Test on line 137 calls `routeRequest cfg applyHeuristic req`; it's the "heuristic vs ML same-input divergence" demonstration — **DELETE** if the only point is the comparison; **REWRITE** if the assertion is about ML behavior alone. Test on line 173 — `"CLI --routing-algorithm=ml overrides config Algorithm=heuristic"` — only meaningful while `--routing-algorithm` flag exists; **DELETE** with the flag. Tests on lines 156, 179, 204, 224 set `Routing:Algorithm = "ml"` explicitly; they survive (still ML, but redundant since "ml" becomes the only value — can drop the override). | 13 |
| `tests/SmartRouter.Tests/StreamingTests.fs` | Line 130: `KeyValuePair("Routing:Algorithm", "heuristic")` — config override **as a test fixture to skip ML model loading**. See §3 (Open Question 2) — needs replacement strategy. | 1 |
| `tests/SmartRouter.Tests/LoggingTests.fs` | Line 177: same fixture pattern as StreamingTests. Line 343: ASSERTS the JSONL field `routing_algorithm = "heuristic"`. The assertion currently relies on the config fixture. Replacement needed (§3 Q2). | 5 |
| `tests/SmartRouter.Tests/HealthFallbackTests.fs` | Line 80: same fixture pattern. Comment on line 79 explicitly says "heuristic avoids ML.zip dependency". Replacement needed (§3 Q2). | 2 |
| `tests/SmartRouter.Tests/RouterTests.fs` | (Expecto root list.) **UPDATE** if tests are deleted/added. | — |

### 1.4 Scripts / CI

| File | What changes |
|---|---|
| `scripts/check-routing-isolation.sh` | Enforces zero cross-imports between `Heuristic.fs` and `ML.fs` (ML-04). With `Heuristic.fs` gone the guard is meaningless. **DELETE** the script + remove any CI hook that calls it. (Quick check needed: any pre-commit / CI workflow file references it. Top-level repo: no `.github/`, no pre-commit hook detected; the script is operator-runnable only.) |

### 1.5 Top-level docs (informational only — no build impact)

`qwen35-122b-openai-compat-router.md`, `smart-router.md`, `graphify_smart_router_prompt.md` all mention heuristic for historical/spec context. **OUT OF PHASE 12 SCOPE per user clarification** — these are docs, not routing-decision code. Phase 11 (deployment+docs) or a follow-on can revise them. Note here, ignore for execution.

---

## 2. What stays (not in scope of "delete heuristic routing")

- `archive/heuristic-baseline` git branch + `v0.5-heuristic-baseline` tag — historical snapshot. UNTOUCHED.
- `RoutingAlgorithmRegistration` record (Cli `Adapters/RoutingAlgorithm.fs`) — still useful as the carrier of `(Algorithm, Name, ModelVersion)` triple. ML alone uses it.
- The `RoutingAlgorithm` function-type alias in `Domain.fs` (line 102) — still useful; ML's closure conforms to it. Keep the seam even if there's now only one implementer.
- `Routing.Algorithm` config key — RECOMMEND keep with only valid value `"ml"` (or default-and-implicit if removed; see §3 Q3). The `Routing.ML` subsection (`Threshold`) keeps its meaning.
- `RoutingReason` DU's `ExplicitModelOverride`, `ExplicitTask`, `Default`, `ML`, `FallbackTo35B` cases all stay.
- `defaultRoutingConfig` stays (with `Keywords` and `ComplexityThreshold` fields removed; `MlThreshold`, `TaskTable` remain).

---

## 3. Open questions (need user decision before planning)

### Q1. `--retrain` offline path — heuristic was a "skip ML init" trick

The current `--retrain` branch in `Program.fs` (lines 33–41) injects `Routing:Algorithm=heuristic` to make `CompositionRoot.configureServices` skip the ML registration block (which calls `ensureEmbeddingFilesPresent` and hard-fails when `models/embed/*` is absent). Once heuristic is removed:

- **Option A: separate config flag for "retrain mode"**
  Introduce a `Routing:SkipMLInit=true` (or `--retrain` injects this) that gates the ML registration block. ML algorithm wiring becomes conditional on `(routingAlgo = "ml") && not skipMLInit`. Retrain pipeline wires only the 3 ports it needs.

- **Option B: split `configureServices`**
  Extract the ML registration block into a separate function. `--retrain` calls only `configureServicesForOfflinePipeline`, which registers HTTP factory + retrain ports + decision-logger sinks but skips ML. Cleaner separation; biggest change to existing code.

- **Option C: keep ML registration unconditional but make `ensureEmbeddingFilesPresent` lazy / soft-fail in retrain mode**
  Defer model file presence check until first inference. Retrain pipeline never inferes, so files never load. Smallest delta to existing code, but introduces silent coupling.

Recommend **B** (cleanest) or **A** (smallest delta). User decides.

### Q2. Test fixtures (StreamingTests / LoggingTests / HealthFallbackTests) used heuristic to avoid ML model files

These three test modules don't care about routing decisions; they exercise streaming, logging, and fallback respectively. They picked `"heuristic"` as the algorithm because heuristic needs zero external files. Once heuristic is gone:

- **Option A: real ML with stub embedder + classifier in-test**
  Register a fake `IEmbedder` (returns fixed `float32[]`) and fake `IClassifier` (returns fixed score) via DI override. Tests still go through the ML algorithm closure but with deterministic, no-file fakes. **Mirror the pattern used by `MLRoutingTests` Tests 1–3** (which already do this).

- **Option B: register a hand-written test-only `RoutingAlgorithm` directly**
  Skip the ML closure entirely in these test fixtures. Inject a `RoutingAlgorithmRegistration` whose `Algorithm` field is `fun cfg req → { Target=Qwen35B; Priority=Low; Reason=ML; IsFallback=false; ModelVersion="" }`. Cleanest test isolation; smallest dependency surface.

- **Option C: move the ML model files into the test fixtures and run the real ML path**
  Highest fidelity, slowest test, requires `models/embed/*` files in the test environment. Already gated behind `mlTestCase` ptest-pattern in `MLRoutingTests`. Not appropriate for tests that aren't testing ML.

Recommend **B** for these three fixtures (smallest blast radius). LoggingTests' `routing_algorithm = "heuristic"` assertion (line 343) becomes `routing_algorithm = "ml"` (or whatever the registration's `Name` field carries).

### Q3. Do we keep `Routing.Algorithm` config key?

Three views:

- **Keep with sole valid value `"ml"`**: future-proof in case a second algorithm is added; minimal config-shape churn; CompositionRoot validates value-not-`"ml"` → error.
- **Drop entirely**: simpler config; if a second algorithm is added later, restore the key. Removes one dead string from `appsettings.json`.
- **Keep the key, treat any value as "ml" (validation removed)**: bad — silent misconfiguration is worse than a clear error.

Recommend **keep with sole valid value** — keeps the seam defensive against silent regressions and lets a future Phase reintroduce alternatives without a breaking config-shape change.

Same question for `--routing-algorithm` CLI flag: if `Routing.Algorithm` survives with one value, the flag is dead; recommend **delete the flag** (deletes `Program.fs` lines 117–153).

### Q4. Backward compatibility for existing JSONL logs that contain `routing_algorithm = "heuristic"` and `model_version = "heuristic-v1"`

Phase 7 `FailureDetector` reads `logs/decisions/*.jsonl` and filters `fallback_used = true`. It does NOT filter on `routing_algorithm`. So legacy heuristic rows with `fallback_used = true` (rare) WOULD be passed downstream as hard cases.

Phase 8 `RetrainingService` consumes these via `IFailureDetector` → `ITeacherLabeler` → dataset. The teacher relabels them, so the `routing_algorithm` value is forgotten at the dataset boundary.

**Risk:** None functional. Logs are read-only by Phase 7+. Old rows pass through fine.

**Decision:** No code change for log compat. Note in PROJECT.md (when concurrent session permits) that legacy `"heuristic"`-labeled rows in JSONL are still parseable; they just route through the same FailureDetector → TeacherLabeler pipeline.

### Q5. CLS-03 cosine-similarity test (06-VERIFICATION) and any other dormant heuristic refs

Quick check: `grep -i heuristic` across .planning/ pulled 50+ files, but those are documentation of past decisions — they don't drive runtime. Only the items in §1 are runtime-relevant.

**No action needed** on .planning files (and they're owned by the canonical session anyway per the concurrent-session rule).

---

## 4. Risks

1. **Hidden test coupling.** RoutingTests' `route` helper is private and used by 16+ tests; replacing `applyHeuristic` with a no-op stub may pass the type checker but silently change semantics if any test asserts something the stub doesn't satisfy. Mitigation: read every `route` call site in RoutingTests before the stub is introduced.

2. **`canonicalKeywords` deletion.** Currently exported from `Routing.fs` for `defaultRoutingConfig`. No test references it directly (grep confirms). Safe to delete with `Keywords` field. Mitigation: a final grep across `tests/` and `src/` for `canonicalKeywords` before deletion.

3. **`mlThreshold` defensive default.** `CompositionRoot.fs` line 111-116 defaults `MlThreshold` to `0.5f` "for heuristic-only deployments that omit Routing.ML entirely". Once heuristic is gone, this defensive default is still useful (config can omit `ML` section). Keep the defaulting, just update the comment.

4. **`appsettings.json` Routing section shape.** Current shape has `Algorithm`, `ComplexityThreshold`, `Keywords`, `TaskTable`, `ModelAliases`, `ML`. After removal: drops `ComplexityThreshold` and `Keywords`. `ConfigurationManager` won't error on unknown keys, but operators with an old `appsettings.json` won't get a warning if they leave them. Mitigation: emit a startup-log warning if `Routing:ComplexityThreshold` or `Routing:Keywords:0` is present (unknown-key warning).

5. **`scripts/check-routing-isolation.sh` removal.** Script is meaningful only while both `Heuristic.fs` and `ML.fs` exist. After removal it's dead code. **Risk:** if any operator workflow / hook still calls it, removal breaks them. Mitigation: grep `.git/hooks/`, `.github/`, `Makefile`, `scripts/` for the script's name before deletion. Quick check now: not present in any of those.

6. **JSONL artifact in `src/SmartRouter.Cli/logs/decisions/2026-05-08.jsonl`.** This is a stray file that shouldn't be committed (logs are gitignored under repo root, but this is under `src/`). **Risk:** none functional. **Action:** delete it; verify `.gitignore` covers `src/SmartRouter.Cli/logs/`.

7. **`Domain.fs` `RoutingReason.Heuristic` deletion + exhaustive matches.** F# compiler with `TreatWarningsAsErrors=true` will fail to compile any `match … with | Heuristic _ → … | _ → …` site that no longer matches. The **only** site is `formatReason` in `DecisionLogger.fs`. Removal of the case is mechanical.

8. **`MLRoutingTests` line 137 — direct heuristic-vs-ML divergence test.** It asserts that `routeRequest cfg applyHeuristic` and `routeRequest cfg mlClosure` produce different `Target`/`Reason` for some prompt. Once heuristic is gone, this test cannot exist. Mitigation: delete the test outright.

9. **`scripts/seed-hard-cases.fsx`** (Phase 7). Hard-coded routing labels are `"Qwen35B"` / `"Qwen122B"` strings — independent of heuristic. **No change needed.**

---

## 5. Suggested phasing (waves) for the actual Phase 12 plan

This section is just a sketch for the eventual `gsd-planner`. Not authoritative.

**Wave 1: Core + dispatch (atomic, must ship together to keep build green)**
- Plan 12-01: Core deletion (Heuristic.fs + RoutingReason.Heuristic case + Keywords/ComplexityThreshold fields + `canonicalKeywords` + `defaultRoutingConfig` rebuild + Core.fsproj prune). Must compile alone.
- Plan 12-02: Cli rewire (CompositionRoot heuristic-arm deletion, formatReason heuristic-arm deletion, `--retrain` reroute per Q1, `--routing-algorithm` flag deletion per Q3, `RoutingOptions.Algorithm` doc-comment update). Depends on 12-01.

**Wave 2: Tests (parallel)**
- Plan 12-03: RoutingTests rewrite (remove heuristic-internal tests; replace stage-3 tests with no-op stub algorithm).
- Plan 12-04: MLRoutingTests prune (remove "both algorithms" test, "heuristic vs ML divergence", "--routing-algorithm override" tests).
- Plan 12-05: StreamingTests + LoggingTests + HealthFallbackTests fixture migration to test-stub algorithm (Q2 Option B). Includes LoggingTests' `routing_algorithm` assertion update (`"heuristic"` → `"ml"`).

**Wave 3: Cleanup**
- Plan 12-06: Delete `scripts/check-routing-isolation.sh`. Delete stray JSONL in `src/SmartRouter.Cli/logs/`. Add `.gitignore` rule for `src/**/logs/` if missing. Update inline comments in CanaryGate.fs / CanaryMetrics.fs / QwenUpstreamClient.fs / CanaryPorts.fs (4 historical mentions).

**Verification (Wave 4 / phase-level):**
- 0 grep hits for `applyHeuristic`, `Heuristic.applyHeuristic`, `scoreComplexity`, `canonicalKeywords` across `src/` and `tests/`.
- 0 grep hits for `\"heuristic\"` (string literal) across `src/` and `tests/` (excluding intentional historical comments if any survive — verify case by case).
- `dotnet build` clean with `TreatWarningsAsErrors=true`.
- `dotnet test` green (count drops by N — Wave 2 plans report exact deltas).
- `Routing:Algorithm` config validates only `"ml"` (Q3 confirmed decision).

---

## 6. Test count delta (estimate, not authoritative)

Current: 86 + 17 ignored / 93 + 10 with embeddings (Phase 11 estimate).

Removals (best estimate from grep):
- RoutingTests heuristic-internal tests: ~6–8 testCase blocks (score < threshold, score ≥ threshold, keyword counting, edited threshold, edited keywords, etc.)
- MLRoutingTests: ~3 tests (both-satisfy, divergence, --routing-algorithm-override)
- LoggingTests: 0 net change (the heuristic-fixture test stays, just asserts `"ml"` instead of `"heuristic"`)

Net delta: roughly **-9 to -11 tests**. The tests being removed are heuristic-semantics-specific; the non-routing tests using the heuristic fixture stay (just rewired).

---

## 7. Decisions needed from user before `/gsd:plan-phase 12`

1. **Q1 (retrain skip-ML-init mechanism):** A / B / C
2. **Q2 (test fixture migration):** A / B / C
3. **Q3 (Routing.Algorithm config key fate):** keep-with-sole-value-ml / drop-entirely / drop-validation
4. **Implicit confirm:** delete `--routing-algorithm` CLI flag entirely. (Yes/No)
5. **Implicit confirm:** delete RoutingTests heuristic-internal tests outright (vs port to a no-op stub for partial coverage). (Delete / Port)
6. **Implicit confirm:** delete `scripts/check-routing-isolation.sh`. (Yes/No)
7. **Implicit confirm:** retain `archive/heuristic-baseline` branch and tag untouched. (Yes/No)

---

## 8. Recommended path (author's view)

- Q1 → **B** (split `configureServices` into request-path and offline-pipeline variants)
- Q2 → **B** (test-only no-op routing algorithm; smallest blast radius for tests that don't care about routing decisions)
- Q3 → **keep with sole valid value `"ml"`**; delete `--routing-algorithm` CLI flag
- Delete RoutingTests heuristic-internal tests outright; preserve stage-1/stage-2 + stage-3-fallthrough coverage via the no-op stub from Q2
- Delete `scripts/check-routing-isolation.sh`
- Untouched: `archive/heuristic-baseline`, top-level `*.md` docs, `.planning/` docs

This minimizes ongoing maintenance, keeps the algorithm seam available for future re-introduction, and avoids retention of "soft-paused" code that the user has signaled they no longer want.
