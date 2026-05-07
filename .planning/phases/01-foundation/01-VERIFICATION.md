---
phase: 01-foundation
verified: 2026-05-07T16:02:00Z
status: passed
score: 9/10 automated must-haves verified; 1/1 human-verification items approved by user on automated evidence
human_verification:
  - test: "Send a non-streaming POST /v1/chat/completions with a valid message to http://127.0.0.1:4000 while Qwen 35B is running on 127.0.0.1:8000"
    expected: "HTTP 200 with an unmodified OpenAI-shaped response body forwarded verbatim from the upstream; no field stripping"
    why_human: "Live upstream (Qwen 35B on 127.0.0.1:8000) was not running during plan execution; the router's error-path (502 on upstream down) was verified but the success-path response passthrough requires a live service"
    resolution: "User approved on automated evidence 2026-05-07. Rationale: 502-on-down was already proven live during 01-03; full passthrough will be exercised during Phase 6 launchd deploy + first Hermes/Graphify smoke. Re-test deferred."
---

# Phase 1: Foundation — Verification Report

**Phase Goal:** The project compiles, the routing pipeline is testable in isolation, and the non-streaming HTTP path reaches a real upstream and returns a response.
**Verified:** 2026-05-07T16:02:00Z
**Status:** HUMAN_NEEDED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Non-streaming POST /v1/chat/completions reaches router and returns upstream response unchanged | ? HUMAN | Infrastructure wired; live-upstream success path not verified (Scenario B skipped — 35B not running during execution) |
| 2 | stream=true returns HTTP 501 with prescribed error body | ✓ VERIFIED | ChatCompletions.fs:112-117; Scenario A curl output in 01-03-SUMMARY.md |
| 3 | Unknown task returns HTTP 400 with OpenAI-shaped error body | ✓ VERIFIED | ChatCompletions.fs:123-128; Scenario A curl output in 01-03-SUMMARY.md; RoutingTests.fs:126-128 |
| 4 | `{"task":"graph_indexing"}` routes to 122B | ✓ VERIFIED | RoutingTests.fs:68-69; 22 tests pass (dotnet run output) |
| 5 | `{"task":"retrieval"}` routes to 35B | ✓ VERIFIED | RoutingTests.fs:108-109; 22 tests pass |
| 6 | Long complex prompt (no task) routes to 122B; short simple prompt routes to 35B | ✓ VERIFIED | RoutingTests.fs heuristic tests; scoreComplexity reads config.ComplexityThreshold and config.Keywords (Routing.fs:97-122) |
| 7 | `{"model":"35b"}` or `{"model":"122b"}` overrides task and heuristic routing | ✓ VERIFIED | RoutingTests.fs:34-40, 44, 194-200; tryModelOverride short-circuits stages 2+3 (Routing.fs:141-143) |
| 8 | SmartRouter.Core has zero compilation references to Serilog/HttpClient/ASP.NET | ✓ VERIFIED | SmartRouter.Core.fsproj has only FsToolkit.ErrorHandling 5.2.0; grep for forbidden opens returns empty |
| 9 | check-no-async.sh passes; exits 1 on canary async {} | ✓ VERIFIED | Script exits 0 on clean Core; exits 1 when async { } injected (canary test run live) |
| 10 | Expecto uses explicit rootTests list; zero test discovery warnings | ✓ VERIFIED | RouterTests.fs:14-18 explicit rootTests; 22 tests pass, 0 ignored, 0 failed, 0 errored |

**Score:** 9/10 truths verified (1 human-needed)

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/SmartRouter.Core/SmartRouter.Core.fsproj` | Only FsToolkit.ErrorHandling 5.2.0 | ✓ VERIFIED | Contains exactly one PackageReference: FsToolkit.ErrorHandling 5.2.0 |
| `src/SmartRouter.Core/Routing.fs` | routeRequest : RoutingConfig -> RouterRequest -> Result<RoutingDecision, RouterError> | ✓ VERIFIED | Routing.fs:141 exact signature confirmed |
| `src/SmartRouter.Core/Routing.fs` | tryTaskTable reads config.TaskTable NOT taskToDecision | ✓ VERIFIED | tryTaskTable body (Routing.fs:79-90) has zero calls to taskToDecision; taskToDecision declared as test/validation utility only |
| `src/SmartRouter.Core/Routing.fs` | applyHeuristic/scoreComplexity read config.ComplexityThreshold/config.Keywords | ✓ VERIFIED | scoreComplexity uses config.Keywords (Routing.fs:109); applyHeuristic uses config.ComplexityThreshold (Routing.fs:129) |
| `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` | routeRequest routingConfig invoked from endpoint | ✓ VERIFIED | ChatCompletions.fs:122 — `match routeRequest routingConfig req with` |
| `src/SmartRouter.Cli/CompositionRoot.fs` | AddSingleton<RoutingConfig> registered | ✓ VERIFIED | CompositionRoot.fs — `services.AddSingleton<RoutingConfig>(fun sp -> ...)` |
| `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` | GetRequiredService<RoutingConfig>() at endpoint | ✓ VERIFIED | ChatCompletions.fs:168 |
| `src/SmartRouter.Cli/appsettings.json` | Kestrel bound to 127.0.0.1:4000 | ✓ VERIFIED | appsettings.json: `"Url": "http://127.0.0.1:4000"` |
| `src/SmartRouter.Cli/CompositionRoot.fs` | HttpClient timeout 300s (CONC-07) | ✓ VERIFIED | CompositionRoot.fs:121,127 — `c.Timeout <- TimeSpan.FromSeconds(300.0)` for both upstream35b and upstream122b |
| `scripts/check-no-async.sh` | Exists, executable, exits 0 on clean Core, exits 1 on async {} | ✓ VERIFIED | Script runs clean exit 0; canary injection test exits 1 |
| `tests/SmartRouter.Tests/RouterTests.fs` | Explicit rootTests list + runTestsWithCLIArgs | ✓ VERIFIED | RouterTests.fs:14-22; `let rootTests : Test list` with explicit module reference |
| `tests/SmartRouter.Tests/RoutingTests.fs` | 22 Expecto tests pass, 3 ROUT-05 config-driven | ✓ VERIFIED | `22 tests run — 22 passed, 0 ignored, 0 failed, 0 errored`; ROUT-05 tests at RoutingTests.fs:161-188 |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `ChatCompletions.mapEndpoints` | `RoutingConfig` singleton | `GetRequiredService<RoutingConfig>()` | ✓ WIRED | ChatCompletions.fs:168 |
| `ChatCompletions.handler` | `Routing.routeRequest` | `routeRequest routingConfig req` | ✓ WIRED | ChatCompletions.fs:122 |
| `CompositionRoot.configureServices` | `RoutingConfig` | `AddSingleton<RoutingConfig>(buildRoutingConfig opts)` | ✓ WIRED | CompositionRoot.fs, `services.AddSingleton<RoutingConfig>` |
| `CompositionRoot.configureServices` | Named HttpClients | `AddHttpClient("upstream35b"/""upstream122b"")` with 300s timeout | ✓ WIRED | CompositionRoot.fs:116-128 |
| `ChatCompletions.handler` | `IUpstreamClient.CompleteAsync` | `upstream.CompleteAsync req decision.Target ct` | ✓ WIRED | ChatCompletions.fs:143 |
| `Routing.routeRequest` | `config.TaskTable` (not taskToDecision) | `tryTaskTable config req` → `Map.tryFind ... config.TaskTable` | ✓ WIRED | Routing.fs:145,86 |
| `Routing.applyHeuristic` | `config.ComplexityThreshold` | `if score >= config.ComplexityThreshold` | ✓ WIRED | Routing.fs:129 |

---

### Requirements Coverage

| Requirement | Status | Notes |
|-------------|--------|-------|
| Non-streaming path end-to-end (SC-1) | ? HUMAN | Infrastructure and error-paths verified; live success path needs human test |
| Routing unit tests all 4 dispatch cases (SC-2) | ✓ SATISFIED | RoutingTests.fs covers graph_indexing→122B, retrieval→35B, heuristic→122B, heuristic→35B |
| Model override unit tests (SC-3) | ✓ SATISFIED | RoutingTests.fs:34-40, 44, 194-200 |
| Core purity — zero forbidden deps (SC-4) | ✓ SATISFIED | Single package ref confirmed; build 0 warnings 0 errors |
| check-no-async.sh passes + rootTests explicit (SC-5) | ✓ SATISFIED | Script verified both directions; rootTests list explicit in RouterTests.fs |

---

### Anti-Patterns Found

None detected. No TODO/FIXME/placeholder in verified files. No empty handlers. Routing pipeline is fully implemented.

---

### Human Verification Required

#### 1. Live upstream success path (Scenario B)

**Test:** Start Qwen 35B on 127.0.0.1:8000, then send:
```
curl -X POST http://127.0.0.1:4000/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{"messages":[{"role":"user","content":"hello"}]}'
```
**Expected:** HTTP 200 with an OpenAI-shaped response body forwarded verbatim from the upstream — all fields present, no stripping. Logs should show `target=Qwen35B reason=Heuristic`.

**Why human:** Qwen 35B was not running on 127.0.0.1:8000 during plan execution. The 502-on-downstream-error path was verified live (SUMMARY documents the probe-failure 502 response). The success path (HTTP 200 with body passthrough) requires a live upstream and cannot be verified structurally.

---

### Gaps Summary

No gaps. All must-haves have verifiable implementations in the codebase. The single human-needed item (Scenario B live upstream) is correctly classified per the verification request instructions — it is infrastructure-complete and held only by the live service dependency.

---

_Verified: 2026-05-07T16:02:00Z_
_Verifier: Claude (gsd-verifier)_
