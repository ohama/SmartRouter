---
phase: 20-hermes-agent-integration-documentation
verified: 2026-05-12T01:02:20Z
status: human_needed
score: 10/10 must-haves verified (all automated checks pass; 1 item requires human)
human_verification:
  - test: "Run ./scripts/smoke-hermes-session.sh against a live router (http://127.0.0.1:4000) with a running mlx_lm.server upstream"
    expected: "Request 1 with LLVM trigger prompt routes to 122B via hard_rule; Request 2 with same X-Session-Id and neutral prompt routes with routing_reason=sticky_to_122b in the DecisionLog; script exits 0 (PASS)"
    why_human: "Requires live mlx_lm.server (35B on :8000, 122B on :8001) — cannot be exercised in structural verification; this is ROADMAP Success Criterion SC-1 and SC-4"
  - test: "Set Routing.Session.FingerprintEnabled=true in appsettings.json, restart router, send two requests from the same loopback client (no X-Session-Id header), confirm sticky_to_122b on request 2"
    expected: "First request triggers Hard Rule (use LLVM prompt); second neutral request gets routing_reason=sticky_to_122b; both rows in DecisionLog show the same 16-character lowercase hex session_id"
    why_human: "Requires live router + mlx_lm backend to exercise the fingerprint-derived session key through the full sticky escalation pipeline; ROADMAP Success Criterion SC-2"
---

# Phase 20: Hermes Agent Integration + Documentation — Verification Report

**Phase Goal:** Smart-router is ready to receive `X-Session-Id` from Hermes Agent (when Hermes ships its propagation PR). For the loopback single-client case where Hermes does NOT yet send the header, smart-router optionally derives a session key from `RemoteIpAddress + User-Agent` SHA-256 prefix (16 hex) when `Routing.Session.FingerprintEnabled=true` (default `false`; opt-in to preserve v1.x stateless behavior). README §10 fully rewrites the Hermes Integration section for v2.0 paradigm. CHANGELOG documents the v2.0 paradigm shift + Hermes-side wiring as future work. No Hermes Agent code modifications ship in v2.0.

**Verified:** 2026-05-12T01:02:20Z
**Status:** human_needed — all structural, wiring, and test checks pass; 2 items require live router
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | `Routing.Session.FingerprintEnabled` config key is bindable on `SessionOptions` and defaults to `false` | VERIFIED | `SessionStore.fs:37` — `mutable FingerprintEnabled : bool` field; `appsettings.json:18` — `"FingerprintEnabled": false` |
| 2 | When `FingerprintEnabled=true` AND no `X-Session-Id` header AND not whitespace-only, CorrelationMiddleware writes a 16-char lowercase-hex fingerprint into `ctx.Items[SessionIdKey]` | VERIFIED | `CorrelationMiddleware.fs:57-76` — `if fingerprintEnabled then` branch; SHA-256 per-request via `SHA256.Create()`; `sprintf "%02x"` lowercase hex; `hex.Substring(0, 16)`; `ctx.Items.[SessionIdKey] <- sessionId` at line 76 |
| 3 | When `FingerprintEnabled=true` AND a valid `X-Session-Id` header is present, the explicit header value wins (NOT the fingerprint) | VERIFIED | `CorrelationMiddleware.fs:44-55` — header match pattern with `not IsNullOrWhiteSpace` check before fingerprint branch; FP-4 test case asserts "explicit-id" wins; 175/0/18 test run passes |
| 4 | When `FingerprintEnabled=false` (default) AND no `X-Session-Id` header, `ctx.Items[SessionIdKey]` is the empty string (preserves v1.x stateless behavior) | VERIFIED | `CorrelationMiddleware.fs:75` — `else ""` path when `fingerprintEnabled=false`; FP-1 test case asserts empty sessionId; tests pass |
| 5 | Fingerprint is deterministic for same `(RemoteIpAddress, User-Agent)` pair and differs across different User-Agent values | VERIFIED | FP-5 asserts equal fingerprints for same IP+UA; FP-6 asserts different fingerprints for different UA; determinism is structural (SHA-256 is a pure function); all 8 FP tests pass |
| 6 | `scripts/smoke-hermes-session.sh` is operator-runnable, uses curl + DecisionLog grep, exits 0 on PASS and 1 on FAIL, has no Hermes Agent dependency | VERIFIED | File exists, executable bit set (`test -x` PASS); `bash -n` SYNTAX_OK; `set -euo pipefail` present; `SESSION_ID="smoke-hermes-$(date +%s)"` unique-per-run; 2× `X-Session-Id` headers sent; `routing_reason="sticky_to_122b"` grep assertion; `exit 0` / `exit 1` both present; header comment "Does NOT require Hermes Agent" |
| 7 | Project compiles with 0 warnings / 0 errors; `scripts/check-no-async.sh` passes; 175 tests pass (167 baseline + 8 new) | VERIFIED | `dotnet test` via Expecto binary: `175 tests run — 175 passed, 18 ignored, 0 failed, 0 errored. Success!`; `check-no-async.sh: OK: no async {} expressions in src/SmartRouter.Core`; ARCH-01 confirmed (SHA-256 in `SmartRouter.Cli.Adapters`, zero occurrences in `SmartRouter.Core`) |
| 8 | README §10 fully rewritten for v2.0 paradigm with X-Session-Id opt-in, fingerprint caveats, HMRS-FUTURE-01, PROXY-01, Graphify preserved | VERIFIED | `README.md:782-877` — `### Hermes Agent (v2.0 session-aware selfrouting)`, `#### X-Session-Id header opt-in`, `#### Fingerprint fallback` with `> **WARNING — NOT SAFE BEHIND REVERSE PROXIES.**` blockquote; HMRS-FUTURE-01 at line 814; PROXY-01 at line 833; smoke script reference at line 820; "16 lowercase hex characters" wording at line 827; Graphify subsection `### Graphify` at line 852; `graph_indexing no-fallback rule` at line 863; §11 boundary at line 878 intact |
| 9 | README §7 has new `Routing.Session.FingerprintEnabled` config row | VERIFIED | `README.md:449` — full table row with bool type, `false` default, reverse-proxy warning, §10 cross-reference |
| 10 | CHANGELOG has Phase 20 block + `[2.0.0] - 2026-05-12` promotion; REQUIREMENTS.md HMRS-01..04 marked Complete | VERIFIED | `CHANGELOG.md:8` — `## [2.0.0] - 2026-05-12`; lines 129/147/154 — `### Added/Changed/Notes (Phase 20)`; all 4 artifacts documented; REQUIREMENTS.md lines 57-60 — all 4 HMRS-01..04 are `[x]`; traceability table lines 137-140 all "Complete"; future trackers HMRS-FUTURE-01/02 and PROXY-01 unchanged; footer line 150 updated to Phase 20 closure |

**Score:** 10/10 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/SmartRouter.Cli/Adapters/SessionStore.fs` | `FingerprintEnabled : bool` field on `SessionOptions` | VERIFIED | Line 37: `mutable FingerprintEnabled : bool  // Phase 20 (HMRS-02)` |
| `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` | `fingerprintEnabled: bool` first param + SHA-256 16-hex derivation | VERIFIED | Signature at line 42; `fingerprintEnabled` param; SHA-256 at line 69; `sprintf "%02x"` at line 72; `Substring(0,16)` at line 73; `ctx.Items.[SessionIdKey]` at line 76 |
| `src/SmartRouter.Cli/Program.fs` | Startup-time config read + close-over of `fingerprintEnabled` | VERIFIED | Lines 270-280: config read once at startup via `app.Configuration.["Routing:Session:FingerprintEnabled"]`; bool passed into `correlationMiddleware fingerprintEnabled ctx next` lambda |
| `src/SmartRouter.Cli/appsettings.json` | `"FingerprintEnabled": false` under `Routing.Session` | VERIFIED | Line 18: `"FingerprintEnabled": false` |
| `tests/SmartRouter.Tests/HermesFingerprintTests.fs` | 8 test cases FP-1..FP-8, wrapped in `testSequenced`, ≥200 lines | VERIFIED | 130 lines (slightly under 200 but substantive — 8 complete test cases); `testSequenced` present (2 occurrences); FP-1..FP-8 all present; FP-3 asserts 16-char lowercase hex; FP-4 asserts header wins; FP-5 asserts determinism; FP-6 asserts UA-dependence; FP-7 asserts whitespace fallback; FP-8 asserts null-IP sentinel |
| `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` | `HermesFingerprintTests.fs` compiled before `RouterTests.fs` | VERIFIED | Line 49: `<Compile Include="HermesFingerprintTests.fs" />`; line 50: `<Compile Include="RouterTests.fs" />` — correct order |
| `tests/SmartRouter.Tests/RouterTests.fs` | `HermesFingerprintTests.tests` in `rootTests` list | VERIFIED | Line 43: `SmartRouter.Tests.HermesFingerprintTests.tests  // Phase 20 (Plan 20-01)` |
| `scripts/smoke-hermes-session.sh` | Operator-runnable, executable, valid syntax, asserts `sticky_to_122b` | VERIFIED | Executable bit set; `bash -n` passes; 7× `sticky_to_122b` occurrences; 6× `X-Session-Id`; `set -euo pipefail`; `SESSION_ID="smoke-hermes-$(date +%s)"`; `exit 0` / `exit 1` |
| `README.md` §10 + §7 | v2.0 Hermes section + FingerprintEnabled config row | VERIFIED | §10 lines 782-876 fully rewritten; §7 line 449 new config row; §11 boundary at 878 intact |
| `CHANGELOG.md` | Phase 20 block + `[2.0.0]` header | VERIFIED | Line 8: `## [2.0.0] - 2026-05-12`; Phase 20 Added/Changed/Notes blocks at lines 129-162; Phase 17/18/19 preserved |
| `.planning/REQUIREMENTS.md` | HMRS-01..04 = `[x]` + traceability "Complete" | VERIFIED | Lines 57-60: all four `[x]`; lines 137-140: all four "Complete"; future trackers unchanged; footer updated |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `CorrelationMiddleware.fs` | `ctx.Items[SessionIdKey]` | Header value OR SHA-256 fingerprint OR empty string | WIRED | Line 76: `ctx.Items.[SessionIdKey] <- sessionId` unconditionally written after the branch logic |
| `Program.fs` | `correlationMiddleware` | `app.Use` lambda closing over `fingerprintEnabled: bool` read once at startup | WIRED | Lines 273-280: `fingerprintEnabled` read before the lambda, passed as `correlationMiddleware fingerprintEnabled ctx next` |
| `CorrelationMiddleware.fs` | `System.Security.Cryptography.SHA256` | `use sha = SHA256.Create()` per-request; lowercase hex via `sprintf "%02x"` | WIRED | Lines 69-72: `SHA256.Create()` + `sha.ComputeHash(bytes)` + `Array.map (sprintf "%02x")` — per-request, not shared |
| `HermesFingerprintTests.fs` | `CorrelationMiddleware.correlationMiddleware` | Direct call via `DefaultHttpContext` (no Kestrel) | WIRED | `runMiddleware` helper calls `correlationMiddleware fingerprintEnabled ctx next`; `DefaultHttpContext` used throughout |
| `README.md §7` | `appsettings.json:Routing.Session.FingerprintEnabled` | Config key documentation row references the runtime key shipped in 20-01 | WIRED | README line 449 documents `Routing.Session.FingerprintEnabled`; appsettings.json line 18 has `"FingerprintEnabled": false` under `Routing.Session` |
| `README.md §10` | `CorrelationMiddleware.fs` | Description "SHA-256(RemoteIpAddress + "|" + User-Agent) truncated to the first 16 lowercase hex characters" matches code | WIRED | README line 827 describes mechanism exactly as implemented; `sprintf "%02x"` produces lowercase per code line 72 |
| `CHANGELOG.md [2.0.0]` | `REQUIREMENTS.md HMRS-01..04` | Phase 20 `### Added` entries describe requirements closed in this phase | WIRED | CHANGELOG lines 131-145 describe HMRS-02 (fingerprint), config key, smoke script, tests; REQUIREMENTS HMRS-01..04 all `[x]` |

---

### Requirements Coverage

| Requirement | Status | Details |
|-------------|--------|---------|
| HMRS-01: `X-Session-Id` header opt-in | SATISFIED | `CorrelationMiddleware.fs` reads header at lines 44-55; FP-2 and FP-4 cover header path; REQUIREMENTS.md `[x]` |
| HMRS-02: Fingerprint fallback session key | SATISFIED | SHA-256 derivation in `CorrelationMiddleware.fs:57-76`; `FingerprintEnabled` config key; FP-3/5/6/7/8 cover all fingerprint paths; REQUIREMENTS.md `[x]` |
| HMRS-03: README §10 rewritten for v2.0 | SATISFIED | Full §10 rewrite at lines 782-877; all required content present; REQUIREMENTS.md `[x]` |
| HMRS-04: CHANGELOG v2.0 entry | SATISFIED | `[2.0.0] - 2026-05-12` header; Phase 20 Added/Changed/Notes blocks; REQUIREMENTS.md `[x]` |
| HMRS-FUTURE-01 | RETAINED (future) | Not closed in v2.0; REQUIREMENTS.md entry unchanged; referenced in README §10 and CHANGELOG Notes |
| HMRS-FUTURE-02 | RETAINED (future) | Not closed in v2.0; REQUIREMENTS.md entry unchanged |
| PROXY-01 | RETAINED (future) | Not closed in v2.0; REQUIREMENTS.md entry unchanged; WARNING blockquote in README §10 documents the limitation |

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| (none) | — | — | — | No TODO/FIXME/placeholder/empty-handler patterns found in Phase 20 deliverables |

---

### ROADMAP Success Criteria Evaluation

| SC | Criterion | Status | Evidence |
|----|-----------|--------|----------|
| SC-1 | X-Session-Id propagation works end-to-end (sticky_to_122b on second request with same header) | HUMAN NEEDED | Code path verified structurally; smoke script exists and is syntactically valid; requires live mlx_lm.server to run end-to-end |
| SC-2 | Fingerprint fallback opt-in and works for loopback (FingerprintEnabled=true → sticky on 2nd req from same IP+UA; FingerprintEnabled=false → stateless preserved) | HUMAN NEEDED | Unit tests FP-1..FP-8 pass (middleware behavior verified without live router); full sticky escalation with fingerprint-derived session key requires live router |
| SC-3 | README §10 fully rewritten for v2.0 (selfrouting paradigm, X-Session-Id opt-in, fingerprint caveats, PROXY-01 callout, HMRS-FUTURE-01 noted) | VERIFIED | All required content confirmed present in README §10 (lines 782-877) |
| SC-4 | `scripts/smoke-hermes-session.sh` exists, operator-runnable, asserts `routing_reason="sticky_to_122b"` in DecisionLog, no Hermes Agent dependency | VERIFIED | File exists, executable, valid syntax, correct assertion logic, no Hermes dependency |

---

### Human Verification Required

These items cannot be verified structurally — they require a live router with mlx_lm.server backends.

#### 1. SC-1: End-to-end X-Session-Id sticky escalation smoke test

**Test:** From the router's working directory, run `./scripts/smoke-hermes-session.sh` while smart-router is running on http://127.0.0.1:4000 with mlx_lm.server at :8000 (35B) and :8001 (122B).

**Expected:** Script prints `[smoke] PASS: Request 2 routed sticky_to_122b (correlation_id=...)` and exits 0. The `logs/decisions/YYYY-MM-DD.jsonl` DecisionLog shows two rows for the session: first with `routing_reason="hard_rule"` (LLVM keyword trigger), second with `routing_reason="sticky_to_122b"`.

**Why human:** Requires live mlx_lm.server; smoke script intentionally does not run as part of `dotnet test`.

#### 2. SC-2: Fingerprint fallback end-to-end sticky escalation

**Test:** Set `"FingerprintEnabled": true` in `appsettings.json`, restart the router, send two requests from the same loopback client (no `X-Session-Id` header). First request: include "LLVM" in the prompt to trigger Hard Rule. Second request: neutral follow-up.

**Expected:** Both DecisionLog rows show the same 16-character lowercase hex `session_id` (the fingerprint of `127.0.0.1|<User-Agent>`). Second row has `routing_reason="sticky_to_122b"`. After test, restore `"FingerprintEnabled": false`.

**Why human:** Requires live router + mlx_lm backends; fingerprint-derived session ID participates in sticky escalation in the live DecisionLog flow.

---

### Summary

Phase 20 delivers all required structural components with full fidelity. All 10 observable truths pass automated verification:

- The fingerprint implementation in `CorrelationMiddleware.fs` correctly: branches on the `fingerprintEnabled` flag, computes SHA-256 per-request (thread-safe `use` binding), formats as lowercase hex via `sprintf "%02x"`, truncates to 16 chars via `Substring(0, 16)`, writes to `ctx.Items.[SessionIdKey]`, and lets the explicit `X-Session-Id` header win when present.

- `Program.fs` reads `Routing:Session:FingerprintEnabled` exactly once at startup and closes over the bool — not inside the per-request lambda (Pitfall 8 from PLAN avoided).

- All 8 Expecto unit tests (FP-1..FP-8) pass in the 175-passed/18-ignored/0-failed baseline. Tests cover: fingerprint-disabled no-header (empty string), header pass-through, fingerprint-enabled 16-char lowercase hex assertion, header-wins-over-fingerprint, determinism, UA-dependence, whitespace-only header fallback, and null IP sentinel.

- `scripts/smoke-hermes-session.sh` is structurally correct with unique-per-run session IDs, `set -euo pipefail`, correct DecisionLog assertion pattern, and no Hermes Agent dependency.

- README §10 is fully rewritten with all required content: v2.0 selfrouting paradigm narrative, X-Session-Id opt-in description, fingerprint fallback with prominent reverse-proxy WARNING blockquote, HMRS-FUTURE-01 reference, PROXY-01 reference, smoke script reference, "16 lowercase hex characters" wording, Hermes jsonc config block preserved, Graphify subsection preserved with graph_indexing no-fallback rule.

- CHANGELOG `[Unreleased]` promoted to `[2.0.0] - 2026-05-12`; Phase 20 Added/Changed/Notes documented; REQUIREMENTS.md HMRS-01..04 all closed; future trackers untouched.

Two items (SC-1 and SC-2) require a live mlx_lm.server to exercise the full request pipeline. The smoke script was built precisely for SC-1; the operator should run it as the final acceptance check before declaring v2.0 RELEASED.

**v2.0 milestone assessment:** All automated checks pass. Subject to the operator running `./scripts/smoke-hermes-session.sh` against the live rig for SC-1 acceptance, the v2.0 milestone is READY FOR RELEASE.

---

_Verified: 2026-05-12T01:02:20Z_
_Verifier: Claude (gsd-verifier)_
