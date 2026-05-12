---
phase: 22-cascade-rewire-migration-and-observability
verified: 2026-05-12T04:54:31Z
status: passed
score: 12/12 must-haves verified (TIER-05 both-modes: DI-only proxy; see note)
re_verification: false
human_verification:
  - test: "README §10 Fingerprint section still describes deleted v2.0 feature"
    expected: "Section '#### Fingerprint fallback (opt-in; loopback single-client only)' at README line 822 and the PROXY-01 warning at line 831 must be removed as part of Phase 23 DOC-01"
    why_human: "Phase 23 (DOC-01) owns the §10 rewrite; the content is factually stale but Phase 23 is the correct venue. The CHANGELOG [2.1.0] Removed section line 29 explicitly documents this as Phase 23 scope. No action needed now, but operator reading README §10 will encounter contradictory information until Phase 23 ships."
---

# Phase 22: Cascade Rewire + Migration + Observability — Verification Report

**Phase Goal:** Every request through `POST /v1/chat/completions` resolves its session key via a clean three-tier cascade — `X-Session-Id` header wins, then system-prompt parse, then content fingerprint — visible in the new `/stats` extraction-source counters. The v2.0 network-fingerprint code (`SHA-256(RemoteIp+UA)`, `FingerprintEnabled` config, `HermesFingerprintTests.fs`) is entirely deleted. The smoke script verifies the new tier system. The CHANGELOG documents the breaking change.

**Verified:** 2026-05-12T04:54:31Z
**Status:** PASSED (with one informational README §10 staleness item deferred to Phase 23)
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|---------|
| 1 | Header always wins (Tier 1) | VERIFIED | `resolveSessionCascade` at ChatCompletions.fs:201-207 returns `req.SessionId, "header"` when `req.SessionId` is non-empty; TC-1 asserts both header value and source="header" with sysprompt also present |
| 2 | System-prompt activates as Tier 2 | VERIFIED | `resolveSessionCascade` falls through to `extractFromSystemPrompt` when SessionId is empty; TC-2 asserts source="sysprompt" and resolvedId="20260512T1530_abc"; TC-5 then routes with that key and gets StickyEscalation |
| 3 | Content fingerprint activates as Tier 3 | VERIFIED | `resolveSessionCascade` falls through to `ContentFingerprint.compute` when both header and sysprompt absent; TC-3 asserts source="content", 16-char hex; TC-4 confirms determinism |
| 4 | Network fingerprint code is gone | VERIFIED | CorrelationMiddleware.fs: no SHA-256 block, no fingerprintEnabled parameter; appsettings.json: FingerprintEnabled key absent; HermesFingerprintTests.fs: file does not exist; dotnet build 0 errors 0 warnings |
| 5 | Both Routing.Mode values work | PARTIAL-VERIFIED | DI registration confirmed in both `configureRequestPipeline` (CompositionRoot.fs:452-455) AND `configureWithoutMl` (CompositionRoot.fs:1230-1233); no explicit ml-mode integration test exercises both modes in a single test (see TIER-05 note) |

**Score:** 5/5 truths verified (must-have 5 is DI-proxy verified; no explicit ml+selfrouting integration test)

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` | Contains `resolveSessionCascade` called between `mapWireToRequest` and `routeRequest` | VERIFIED | Lines 201-207: module-level pure function; called at line 291; shadowed `req` at line 300; ctx.Items updated at line 302 |
| `src/SmartRouter.Cli/Adapters/SessionCascadeStats.fs` | Interface + impl with Interlocked.Increment | VERIFIED | 52 lines; `ISessionCascadeStats` with RecordHeader/RecordSysprompt/RecordContent; Interlocked.Increment + Volatile.Read pattern; exported |
| `src/SmartRouter.Cli/Endpoints/Stats.fs` | 3 new flat snake_case Int64 fields in StatsWire | VERIFIED | Lines 51-53: `session_extraction_source_header`, `session_extraction_source_sysprompt`, `session_extraction_source_content` all Int64; null-safe resolve at lines 123-126; wired at lines 140-142 |
| `src/SmartRouter.Cli/CompositionRoot.fs` | DI registration in BOTH `configureRequestPipeline` AND `configureWithoutMl` | VERIFIED | Lines 447-455 (configureRequestPipeline); lines 1226-1233 (configureWithoutMl); AddSingleton concrete + interface mapping in both branches |
| `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` | IP+UA fingerprint block DELETED | VERIFIED | 71-line file; no SHA-256 block, no `fingerprintEnabled` parameter, no RemoteIpAddress reference; signature is `correlationMiddleware (ctx: HttpContext) (next: RequestDelegate) : Task` |
| `src/SmartRouter.Cli/appsettings.json` | `FingerprintEnabled` key DELETED | VERIFIED | `Routing.Session` section has only `TtlMinutes: 30` and `MaxEntries: 10000`; no FingerprintEnabled key |
| `tests/SmartRouter.Tests/HermesFingerprintTests.fs` | File does NOT exist | VERIFIED | `ls` confirms file absent; not in SmartRouter.Tests.fsproj; not in RouterTests.fs rootTests |
| `tests/SmartRouter.Tests/SessionKeyCascadeTests.fs` | File exists with 6 testCase entries (TC-1..TC-6) | VERIFIED | 249 lines; 6 `testCase` entries; wrapped in `testSequenced`; TC-1 through TC-6 cover all specified must-haves |
| `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` | `SessionKeyCascadeTests.fs` registered BEFORE `RouterTests.fs` | VERIFIED | Line 52: `<Compile Include="SessionKeyCascadeTests.fs" />`; line 53: `<Compile Include="RouterTests.fs" />` |
| `tests/SmartRouter.Tests/RouterTests.fs` | `SessionKeyCascadeTests.tests` in rootTests | VERIFIED | Line 45: `SmartRouter.Tests.SessionKeyCascadeTests.tests` in the rootTests list |
| `scripts/smoke-hermes-session.sh` | Header/sysprompt/content tier coverage references; `bash -n` clean | VERIFIED | Lines 24-29 reference all three tiers; Tier 2/3 note at line 26; `bash -n` exits 0; existing Tier 1 curl test retained |
| `CHANGELOG.md` | `[2.1.0]` block with Added/Removed/Changed covering cascade + FingerprintEnabled breaking change | VERIFIED | Lines 8-108; Removed: FingerprintEnabled + IP+UA block + PROXY-01 + HermesFingerprintTests; Added: Tier 2/3 adapters + cascade + stats counters + archive tag; Changed: signature + session resolution; Notes section |
| `README.md §7` | `FingerprintEnabled` row deleted; `TtlMinutes`/`MaxEntries` present | VERIFIED | Lines 447-448: table has only TtlMinutes and MaxEntries rows; no FingerprintEnabled row in §7 |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `CorrelationMiddleware.fs` | `ctx.Items["SessionId"]` | Header read at line 56-60 | WIRED | Coerces absent/whitespace to ""; Tier 1 only |
| `ChatCompletions.fs:283` | `mapWireToRequest` | `let req = mapWireToRequest correlationId sessionId wireBody` | WIRED | sessionId from ctx.Items at line 254-257 |
| `ChatCompletions.fs:291` | `resolveSessionCascade req` | `let resolvedSessionId, extractionSource = resolveSessionCascade req` | WIRED | After mapWireToRequest, before routeRequest |
| `ChatCompletions.fs:293-297` | `cascadeStats.RecordX()` | Match on extractionSource string | WIRED | Exactly one of RecordHeader/RecordSysprompt/RecordContent called per request |
| `ChatCompletions.fs:300` | `req` rebind | `let req = { req with SessionId = resolvedSessionId }` | WIRED | Shadowed req carries resolved key to routeRequest and sticky writes |
| `ChatCompletions.fs:308` | `routeRequest` | `match routeRequest routingConfig regn.Algorithm req with` | WIRED | Receives shadowed req with resolved SessionId |
| `Stats.fs:123-126` | `ISessionCascadeStats.GetStats()` | GetService null-safe resolve | WIRED | Returns 0L when null (defense-in-depth); wires to StatsWire fields at lines 140-142 |
| `CompositionRoot.fs:452-455` | `ISessionCascadeStats` | AddSingleton in configureRequestPipeline | WIRED | Concrete + interface registration present |
| `CompositionRoot.fs:1230-1233` | `ISessionCascadeStats` | AddSingleton in configureWithoutMl | WIRED | Same pattern, offline path also registers |
| `archive/v2.0-network-fingerprint` tag | commit `d4797e7` | Annotated tag | WIRED | Tag + branch both point to d4797e7 (last Plan 22-01 commit, pre-deletion) |

---

### Requirements Coverage

| Requirement | Status | Evidence |
|-------------|--------|----------|
| TIER-01: X-Session-Id header wins when present | SATISFIED | `resolveSessionCascade` line 202: `if not (String.IsNullOrEmpty(req.SessionId)) then req.SessionId, "header"`; TC-1 verifies |
| TIER-02: Sysprompt parse activates when header absent | SATISFIED | `resolveSessionCascade` line 205-206: `HermesSessionExtract.extractFromSystemPrompt` fallback; TC-2 verifies; TC-5 confirms sticky continuity through Tier 2 key |
| TIER-03: Content fingerprint activates as final fallback | SATISFIED | `resolveSessionCascade` line 207: `ContentFingerprint.compute req, "content"`; TC-3 verifies 16-char hex; TC-4 verifies determinism |
| TIER-04: Cascade applies in both Routing.Mode values | SATISFIED (DI-proxy) | ISessionCascadeStats registered in BOTH `configureRequestPipeline` (line 452) AND `configureWithoutMl` (line 1230); cascade code in ChatCompletions.fs is mode-agnostic (no mode check); TC-5 uses `configureRequestPipeline` with Mode=selfrouting only — no explicit ml-mode integration test (flagged below) |
| TIER-05: Integration tests verify all tier paths | SATISFIED | TC-1 (header wins), TC-2 (sysprompt), TC-3 (content), TC-4 (determinism), TC-5 (sticky through Tier 2), TC-6 (counter increments); 6 testCase entries; all pass (186 passed total) |
| OBS-01: /stats exposes 3 new Int64 fields | SATISFIED | StatsWire lines 51-53; Stats.mapEndpoints lines 140-142; null-safe GetService pattern at lines 123-126 |
| MIG-01: FingerprintEnabled deleted from config + SessionOptions | SATISFIED | appsettings.json: key absent; SessionStore.fs: SessionOptions record has only TtlMinutes/MaxEntries; grep finds 0 hits in src/ and tests/ (only bin/ artifacts contain old value) |
| MIG-02: IP+UA fingerprint block deleted from CorrelationMiddleware | SATISFIED | CorrelationMiddleware.fs: 71-line file, no SHA-256 block, no fingerprintEnabled parameter, no RemoteIpAddress logic |
| MIG-03: HermesFingerprintTests.fs deleted | SATISFIED | File absent; not in fsproj; not in rootTests |
| MIG-04: smoke-hermes-session.sh updated | SATISFIED | FingerprintEnabled comment removed; tier-system header added at lines 24-29; Tier 1 curl test intact; bash -n exits 0 |
| MIG-05: CHANGELOG [2.1.0] block written | SATISFIED | Lines 8-108; Removed/Added/Changed/Notes sections; breaking change documented |
| MIG-06: Archive tag/branch preserves pre-deletion commit | SATISFIED | `v2.0-network-fingerprint` annotated tag + `archive/v2.0-network-fingerprint` branch both point to d4797e7 (last Plan 22-01 commit, before any deletions) |

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `README.md` | 822-849 | Stale §10 fingerprint section describes deleted v2.0 feature | Info | Phase 23 DOC-01 owns §10 rewrite; CHANGELOG [2.1.0] Notes line 103-105 explicitly defers this; no code impact |

No blocker anti-patterns. No stub patterns in any Phase 22 deliverable. No empty implementations.

---

### Build / Test / Script Command Outputs

**dotnet build** (from repo root):
```
SmartRouter.Core -> SmartRouter.Core.dll
SmartRouter.Cli  -> SmartRouter.dll
SmartRouter.Tests -> SmartRouter.Tests.dll
빌드했습니다.
    경고 0개
    오류 0개
경과 시간: 00:00:06.02
```
Result: 0 warnings, 0 errors. PASS.

**dotnet test** (binary direct execution):
```
EXPECTO! 186 tests run in 00:01:27 for all — 186 passed, 18 ignored, 0 failed, 0 errored. Success!
```
Result: 186 passed + 18 ignored + 0 failed. PASS. (Baseline was 188 passed; Phase 22 net: +6 TC cascade tests, -8 FP tests = 186 correct.)

**scripts/check-no-async.sh** (ARCH-02):
```
OK: no async {} expressions in src/SmartRouter.Core
EXIT: 0
```
Result: PASS.

**grep FingerprintEnabled in src/ tests/** (source files only):
```
(no output)
```
Result: 0 hits in `*.fs`, `*.fsproj`, `*.json` source files. PASS. (Only `bin/` artifacts contain old value — build outputs are not source.)

**grep SHA-256.*RemoteIp in CorrelationMiddleware.fs**:
```
(no output)
```
Result: 0 hits. PASS.

**git tag/branch archive verification**:
```
v2.0-network-fingerprint    → d4797e7 (tag)
archive/v2.0-network-fingerprint → d4797e7 (branch)
```
Result: Both point to the correct pre-deletion commit. PASS.

**bash -n smoke script**:
```
BASH SYNTAX OK
```
Result: PASS.

---

### ARCH-01 / ARCH-02 Invariant Status

| Invariant | Status | Evidence |
|-----------|--------|---------|
| ARCH-01: SmartRouter.Core BCL-only | CONFIRMED | No Phase 22 commits touch `src/SmartRouter.Core/`; git log for that directory shows last change was Phase 19; no Serilog/HttpClient/AspNetCore/Microsoft.ML imports in Core |
| ARCH-02: task {} only, no async {} | CONFIRMED | `scripts/check-no-async.sh` exits 0; all Phase 22 F# files use `task {}` |

---

### TIER-05 Both-Modes Assessment (Must-Have #5)

**Finding:** There is no explicit integration test that exercises `Routing.Mode="ml"` separately from `Routing.Mode="selfrouting"`. TC-5 builds a DI provider via `configureRequestPipeline` with `Routing:Mode=selfrouting` only.

**Why this is acceptable at this stage:** The cascade resolution (`resolveSessionCascade`) is a pure function (no DI, no mode awareness) that executes identically regardless of `Routing.Mode`. The `ISessionCascadeStats` DI registration is present in BOTH `configureRequestPipeline` (line 452) AND `configureWithoutMl` (line 1230). The cascade is registered _before_ the `Routing.Mode` branch in `configureRequestPipeline`, so it cannot be mode-gated. This is structurally equivalent to proving both modes work.

**Recommendation for Phase 23:** Add a TC-7 that builds two DI providers (`configureRequestPipeline` with `Mode=selfrouting` and `Mode=ml`) and confirms `ISessionCascadeStats` resolves non-null in both — or adds a `ProductionDiTests.fs` entry asserting `ISessionCascadeStats` resolvable. This closes the formal TIER-04 gap with an explicit assertion rather than structural inference.

---

### Known Deviation: README §7 Row Deleted in Phase 22 (Not Phase 23)

**ROADMAP Phase 23** states it owns "§7 `FingerprintEnabled` row removed." However, commit `938c8ac` deleted the §7 row in Phase 22 (Plan 22-03). This deviation is:

1. **Consistent with CLAUDE.md:** The mandatory README sync rule (area #9: Configuration keys in appsettings.json) requires updating §7 in the same change set as the config key deletion (MIG-01). Deleting the key without updating §7 would create the prohibited README drift.
2. **Explicitly rationalized in 22-RESEARCH.md §9:** The research recommended §7 deletion in Plan 22-03 alongside CHANGELOG.
3. **Net effect:** Phase 23 (DOC-01) still owns §7 per ROADMAP, but the row is already gone. Phase 23 DOC-01 scope can be narrowed to "verify §7 has no FingerprintEnabled reference" (trivially satisfied) rather than deleting it.

**Verdict:** Deviation is correct and beneficial; CLAUDE.md compliance takes precedence over ROADMAP Phase 23 attribution.

---

### Human Verification Required

**Item 1: README §10 Fingerprint section**

- **Test:** Open README.md §10 (Hermes Integration), find the "#### Fingerprint fallback (opt-in; loopback single-client only)" heading at line 822. Confirm operator understands this section describes a **deleted** v2.0 feature.
- **Expected:** Phase 23 DOC-01 will remove this entire subsection and the PROXY-01 warning (lines 822-849). No operator action needed now — the feature is gone from the code. The CHANGELOG [2.1.0] Removed section documents the deletion. An operator reading only §10 without the CHANGELOG would be misled.
- **Why human:** Phase 23 is the correct venue for §10 rewrite. The staleness is documented in CHANGELOG Notes (line 103-105). This is an informational gap, not a code gap. Automated checks confirm §7 is clean; §10 is out of scope for Phase 22.

---

### Net Deviations vs SUMMARY Claims

| Claim in SUMMARYs | Actual Code State | Verdict |
|-------------------|-------------------|---------|
| 22-01-SUMMARY: "resolveSessionCascade pure helper after mapWireToRequest" | ChatCompletions.fs lines 201-207 (module-level function), called at line 291 | MATCHES |
| 22-01-SUMMARY: "ISessionCascadeStats in both DI paths" | CompositionRoot.fs lines 452-455 + 1230-1233 | MATCHES |
| 22-01-SUMMARY: "3 new Int64 fields in StatsWire" | Stats.fs lines 51-53 | MATCHES |
| 22-02-SUMMARY: "FingerprintEnabled deleted from appsettings.json and SessionOptions" | appsettings.json: absent; grep of src/tests: 0 hits | MATCHES |
| 22-02-SUMMARY: "CorrelationMiddleware IP+UA block deleted" | CorrelationMiddleware.fs: 71 lines, no SHA-256, no fingerprintEnabled param | MATCHES |
| 22-02-SUMMARY: "HermesFingerprintTests.fs deleted" | File absent; fsproj line removed; rootTests entry removed | MATCHES |
| 22-02-SUMMARY: "archive/v2.0-network-fingerprint tag + branch at d4797e7" | Both point to d4797e7 | MATCHES |
| 22-03-SUMMARY: "SessionKeyCascadeTests.fs with 6 testCase entries" | 6 testCase entries confirmed | MATCHES — NOTE: RESEARCH estimated 8 tests (TC-7 both-modes, TC-8 counter separate); executor shipped TC-1..TC-6 with TC-6 covering counters (OBS-01) and TC-5 covering sticky (TIER-05d). 6 tests, not 8. Both modes not separately tested. |
| 22-03-SUMMARY: "CHANGELOG [2.1.0] block written" | Lines 8-108 verified | MATCHES |
| 22-03-SUMMARY: "README §7 FingerprintEnabled row removed" | Lines 447-448: only TtlMinutes/MaxEntries | MATCHES |
| 22-03-SUMMARY: "smoke script updated" | Lines 24-29: tier system documented; bash -n clean | MATCHES |

One notable delta: RESEARCH planned 8 test cases (TC-1..TC-8 including a dedicated both-modes TC-7 and a standalone OBS-01 TC-8). The executor shipped 6 tests (TC-1..TC-6), merging OBS-01 into TC-6 and omitting the explicit both-modes TC-7. This results in the TIER-04 DI-proxy-only verification described above.

---

### Gaps Summary

No blocking gaps. All 12 requirements satisfied. Build and tests pass. The two informational items are:

1. **TIER-04 explicit ml-mode integration test absent** — structural DI verification is sufficient for Phase 22; Phase 23 should add TC-7 for completeness.
2. **README §10 fingerprint section stale** — correctly deferred to Phase 23 DOC-01; documented in CHANGELOG Notes.

---

## Recommended Next Action

**Continue to Phase 23 (Documentation).** Phase 22 goal is achieved:

- Three-tier cascade is live in the request path with verified call order
- All three tiers resolve correctly (confirmed by 6 passing test cases)
- Network fingerprint code is entirely deleted (0 grep hits, build clean)
- /stats counters wired and counting
- Archive tag preserves pre-deletion snapshot
- CHANGELOG [2.1.0] documents the breaking change
- 186 passed + 18 ignored + 0 failed

Phase 23 (DOC-01..04) scope should include:
- README §10 rewrite for v2.1 paradigm (remove fingerprint section + PROXY-01 warning, lines 822-849)
- README §8 three new `/stats` counter rows
- README §9.1 DecisionLog section review
- Optional: TC-7 explicit both-modes cascade test (TIER-04 formalization)

---

*Verified: 2026-05-12T04:54:31Z*
*Verifier: Claude (gsd-verifier)*
