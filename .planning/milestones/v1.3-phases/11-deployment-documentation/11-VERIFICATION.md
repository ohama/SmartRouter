---
phase: 11-deployment-documentation
verified: 2026-05-09T13:00:00Z
status: human_needed
score: 22/22 automated must-haves verified
date: 2026-05-09
re_verification: false
human_verification:
  - test: "launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist starts the router"
    expected: "curl http://127.0.0.1:4000/health returns 200 without dotnet run"
    why_human: "Requires actual host deployment — binary must be published first with ./scripts/deploy.sh, then ./scripts/install-launchd.sh, then the load command run on the operator's Mac. Verifier is in a sandbox that cannot run launchctl."
  - test: "kill -9 <smartrouter-pid> triggers auto-restart"
    expected: "After ~35 seconds (ThrottleInterval=30 + ~5s startup), curl http://127.0.0.1:4000/health returns 200 again"
    why_human: "Requires a running launchd-supervised process and a clock — cannot be verified structurally. Note: success criterion says '5 seconds' but ThrottleInterval=30 means real restart is ~35s; the plist is correct per the locked operator convention (research §1). Consider whether the SC should be re-worded."
  - test: "GET /v1/models against deployed router (both upstreams up) returns deduplicated entries from Qwen 35B and 122B"
    expected: "Response is 200, object=list, data contains entries from both mlx_lm servers"
    why_human: "Integration test with real mlx_lm.server instances — requires both to be running. Unit tests MODELS-01..03 cover this structurally."
---

# Phase 11: Deployment Documentation — Verification Report

**Phase Goal:** The router auto-starts under launchd supervision, the /v1/models endpoint proxies both upstream model lists, and the README gives the operator everything needed to tune, debug, and connect both clients.

**Verified:** 2026-05-09
**Status:** human_needed (all automated checks pass; 3 items require live host)
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths (from Plans 11-01 / 11-02 / 11-03)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | GET /v1/models (both up) → 200 + deduplicated merge | VERIFIED | MODELS-01 test passes; Models.fs lines 82-99 implement WhenAll + HashSet dedupe |
| 2 | GET /v1/models (one down) → 200 + reachable upstream's models only | VERIFIED | MODELS-02 test passes; IsReachable gates fetchModels before HTTP call |
| 3 | GET /v1/models (both down) → 200 + empty data array | VERIFIED | MODELS-03 test passes; both-down path returns `{object:list, data:[]}` |
| 4 | Models.fs reuses "health-probe" named HttpClient (no 12th client) | VERIFIED | Models.fs line 70: `factory.CreateClient("health-probe")` |
| 5 | IsReachable checked BEFORE HTTP GET (known-down skip) | VERIFIED | Models.fs lines 75-80: conditional `Task.FromResult []` short-circuit |
| 6 | JsonElement.Clone() called before `use doc` exits | VERIFIED | Models.fs line 53: `acc.Add(entry.Clone())` inside the `use doc` block |
| 7 | RouterTests.rootTests includes ModelsTests.tests | VERIFIED | RouterTests.fs line 30: `SmartRouter.Tests.ModelsTests.tests // Phase 11` |
| 8 | ModelsTests.fs compiled BEFORE RouterTests.fs | VERIFIED | SmartRouter.Tests.fsproj line 26 (ModelsTests) before line 27 (RouterTests) |
| 9 | Test count: 86 pass + 17 ignored + 0 failed | VERIFIED | `dotnet run -- --summary`: 86 tests run in 00:01:25, 86 passed, 17 ignored, 0 failed |
| 10 | Pure-Core invariant (ARCH-01): no new references in SmartRouter.Core | VERIFIED | grep over *.fs in Core: only comments reference invariant; no NuGet additions |
| 11 | plist passes plutil -lint | VERIFIED | `plutil -lint deploy/com.ohana.smart-router.plist`: OK |
| 12 | plist has KeepAlive=<true/>, ThrottleInterval=30, RunAtLoad=<true/> | VERIFIED | plist lines 14-17: `<key>KeepAlive</key><true/>`, `<key>ThrottleInterval</key><integer>30</integer>`, `<key>RunAtLoad</key><true/>` |
| 13 | plist uses absolute paths for ProgramArguments | VERIFIED | plist lines 9-10: `/opt/homebrew/bin/dotnet` and `/Users/ohama/llm-system/services/smart-router/SmartRouter.dll` |
| 14 | plist WorkingDirectory is /Users/ohama/llm-system/services/smart-router | VERIFIED | plist line 23 |
| 15 | plist StandardOut/Err paths correct | VERIFIED | plist lines 19-21: `.log` and `.err` under `/Users/ohama/llm-system/services/logs/` |
| 16 | deploy.sh: framework-dependent publish (no --self-contained, no trim) | VERIFIED | deploy.sh lines 45-47: `dotnet publish -c Release -o "$INSTALL_DIR"` — no self-contained, no trimming flags |
| 17 | deploy.sh: mkdir -p deploy structure before publish | VERIFIED | deploy.sh lines 26-31: `mkdir -p` for smart-router/{models/embed,datasets,prompts,logs/decisions} and logs dir |
| 18 | deploy.sh: set -euo pipefail + #!/usr/bin/env bash | VERIFIED | deploy.sh lines 1 and 12 |
| 19 | deploy.sh: bash -n passes | VERIFIED | `bash -n scripts/deploy.sh`: exit 0 |
| 20 | install-launchd.sh: does NOT auto-execute launchctl load (heredoc only) | VERIFIED | No bare `launchctl` call at column 0 in script body; the command appears only inside `cat <<'EOF'` heredoc at line 42 |
| 21 | install-launchd.sh: bash -n passes | VERIFIED | `bash -n scripts/install-launchd.sh`: exit 0 |
| 22 | README: 500-1500 lines, 13+ sections, all 8 endpoints, all 7 task types, Loop A+B, all 15 required terms | VERIFIED | 1020 lines; 14 sections; all 8 endpoint paths present; all 7 task types with model+priority table; Loop A section 6.1, Loop B section 6.2; all 15 terms confirmed present |

**Score: 22/22 automated must-haves verified**

---

### Required Artifacts

| Artifact | Min Lines | Actual | Status | Notes |
|----------|-----------|--------|--------|-------|
| `src/SmartRouter.Cli/Endpoints/Models.fs` | 60 | 100 | VERIFIED | mapEndpoints, /v1/models, health-probe, IsReachable, .Clone(), HashSet all present |
| `src/SmartRouter.Cli/Program.fs` | existing | — | VERIFIED | Line 205: `SmartRouter.Cli.Endpoints.Models.mapEndpoints app` |
| `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` | existing | — | VERIFIED | Line 47: `<Compile Include="Endpoints/Models.fs" />` |
| `tests/SmartRouter.Tests/ModelsTests.fs` | 120 | 208 | VERIFIED | MODELS-01..03 tests; startFakeUpstream; StubHealthProbe; 3 test cases all pass |
| `tests/SmartRouter.Tests/RouterTests.fs` | existing | — | VERIFIED | ModelsTests.tests at line 30 of rootTests |
| `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` | existing | — | VERIFIED | ModelsTests.fs at line 26, RouterTests.fs at line 27 |
| `deploy/com.ohama.smart-router.plist` | 28 | 34 | VERIFIED | plutil -lint OK; all required keys present |
| `scripts/deploy.sh` | 25 | 65 | VERIFIED | executable (-rwxr-xr-x); bash -n clean |
| `scripts/install-launchd.sh` | 15 | 63 | VERIFIED | executable (-rwxr-xr-x); bash -n clean; launchctl load only in heredoc |
| `README.md` | 500 | 1020 | VERIFIED | Within 500-1500 range; all required content verified |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| Models.fs | IHealthProbe.IsReachable | ctx.RequestServices.GetRequiredService<IHealthProbe>() | WIRED | Lines 65, 76, 79: probe registered and called before HTTP |
| Models.fs | "health-probe" named HttpClient | factory.CreateClient("health-probe") | WIRED | Line 70 |
| Models.fs | UpstreamOptions | IOptions<UpstreamOptions>.Value | WIRED | Line 66 |
| Program.fs | Endpoints/Models.fs | Endpoints.Models.mapEndpoints app | WIRED | Line 205 |
| RouterTests.fs | ModelsTests.fs | rootTests list | WIRED | Line 30 |
| deploy.sh | SmartRouter.dll (via publish) | dotnet publish -c Release -o | WIRED | Lines 45-47 |
| install-launchd.sh | deploy/com.ohama.smart-router.plist | cp $PLIST_SRC $PLIST_DST | WIRED | Lines 19-20, 33 |
| README.md | deploy/com.ohama.smart-router.plist | explicit mention in Operations §12.1 | WIRED | 12 occurrences in README |
| README.md | scripts/deploy.sh + install-launchd.sh | explicit mention in Operations §12.1 | WIRED | 3 and 4 occurrences respectively |
| README.md | GET /v1/models as deduplicated from both upstreams | §8 Endpoints documentation | WIRED | Line 404 and 454 |

---

### Requirements Coverage

| Requirement | Status | Notes |
|-------------|--------|-------|
| API-06 (GET /v1/models) | SATISFIED | Endpoint implemented, tested (MODELS-01..03 pass), registered in Program.fs |
| OPS-01 (launchd auto-start) | SATISFIED (automated) + human_needed | plist is structurally correct (KeepAlive, RunAtLoad, plutil OK); live test requires host deployment |
| OPS-02 (auto-restart after kill -9) | SATISFIED (automated) + human_needed | KeepAlive=true + ThrottleInterval=30 verified in plist; live timing test requires running service |
| OPS-03 (README operator coverage) | SATISFIED | 1020-line README covers all 7 operator tasks, all 8 endpoints, both loops, all integrations |

---

### Anti-Patterns Found

None. No TODO/FIXME/placeholder/stub patterns found in any new file.

---

### Build Status

```
dotnet build SmartRouter.slnx -nologo --tl:off
  SmartRouter.Core -> SmartRouter.Core.dll
  SmartRouter.Cli  -> SmartRouter.dll
  SmartRouter.Tests -> SmartRouter.Tests.dll
Build succeeded.
  0 Warning(s)
  0 Error(s)
Elapsed: 00:00:05.11
```

### Test Status

```
dotnet run --project tests/SmartRouter.Tests -- --summary
[EXPECTO!] 86 tests run in 00:01:25.3681181 for all
  – 86 passed, 17 ignored, 0 failed, 0 errored. Success!
```

Phase 10 baseline (83 pass + 17 ignored) preserved. Phase 11 adds MODELS-01, MODELS-02, MODELS-03 (+3 pass). Totals match plan: 86 pass + 17 ignored.

---

### Human Verification Required

#### 1. launchd Auto-Start (ROADMAP SC#1)

**Test:** Run `./scripts/deploy.sh` then `./scripts/install-launchd.sh` then `launchctl load -w ~/Library/LaunchAgents/com.ohana.smart-router.plist`. Then `curl -fsS http://127.0.0.1:4000/health`.

**Expected:** HTTP 200 without `dotnet run`.

**Why human:** Requires actual macOS host with /opt/homebrew/bin/dotnet, the two qwen mlx_lm servers available (or at least the process starts even if upstreams are down), and the deploy directory ~/llm-system/services/smart-router/ writable. Cannot be run in a sandbox.

#### 2. Auto-Restart After kill -9 (ROADMAP SC#2)

**Test:** With router running under launchd, run `kill -9 $(pgrep -f SmartRouter.dll)`. Wait ~35 seconds (ThrottleInterval=30 + ~5s startup). Then `curl -fsS http://127.0.0.1:4000/health`.

**Expected:** HTTP 200 confirming restart.

**Why human:** Requires a live launchd-supervised process and wall-clock time. Note: success criterion says "within 5 seconds" but ThrottleInterval=30 means the actual restart takes ~35 seconds. The plist value is correct per the locked operator convention (research §1, matching qwen36-35b and qwen122b plists). The SC wording is aspirational; the real behavior is documented in README §12.1 ("ThrottleInterval: 30 — restart attempts are throttled to at most one every 30 seconds"). Operator should verify with awareness of the 30s throttle.

#### 3. Live GET /v1/models (ROADMAP SC#3 — live integration)

**Test:** With router under launchd and both mlx_lm servers running: `curl http://127.0.0.1:4000/v1/models | jq .`

**Expected:** 200 + `{"object":"list","data":[...]}` with model entries from both 35B (port 8000) and 122B (port 8001), deduplicated.

**Why human:** Requires both mlx_lm.server instances running. Unit tests (MODELS-01..03) fully cover the logic; this is end-to-end smoke test only.

---

### Gaps Summary

No gaps. All 22 automated must-haves verified. The 3 human verification items above are UAT steps that require a live macOS host with deployed services — structurally, all the pieces are correct and complete.

One note: ROADMAP SC#2 says "within 5 seconds" but ThrottleInterval=30 means ~35s actual. This is not a bug — the plist convention was locked in research §1 to match the operator's other plists. The README §12.1 correctly documents the 30s throttle. No remediation needed; operator should be aware.

---

*Verified: 2026-05-09*
*Verifier: Claude (gsd-verifier)*
