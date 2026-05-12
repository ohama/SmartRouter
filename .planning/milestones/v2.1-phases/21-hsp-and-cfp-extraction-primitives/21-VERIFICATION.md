---
phase: 21-hsp-and-cfp-extraction-primitives
verified: 2026-05-12T02:27:40Z
status: passed
score: 13/13 must-haves verified
---

# Phase 21: HSP + CFP Extraction Primitives — Verification Report

**Phase Goal:** The codebase contains two new BCL-only extraction adapters — `HermesSessionExtract` and `ContentFingerprint` — that can be called by `CorrelationMiddleware` in Phase 22. Both are pure functions with no I/O, no HTTP, no DI wiring, and are fully covered by unit tests before Phase 22 touches them.
**Verified:** 2026-05-12T02:27:40Z
**Status:** passed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | System-prompt regex extracts session_id correctly (Some/None paths) | VERIFIED | Lines 54-59 of HermesSessionExtract.fs: `List.tryFind` + `Option.bind`; `sessionIdRx.Match` returns `Some m'.Groups.[1].Value` on success, `None` on failure |
| 2 | Regex pre-compiled at module level and case-sensitive | VERIFIED | Line 35-36: `let private sessionIdRx = Regex(@"^Session ID:[ \t]*(\S+)", RegexOptions.Multiline)` — module-level `let private`, no `RegexOptions.IgnoreCase` |
| 3 | Content fingerprint deterministic + always returns 16 lowercase hex chars | VERIFIED | Lines 45-60 of ContentFingerprint.fs: SHA-256 via `Array.map (sprintf "%02x")` + `hex.Substring(0, 16)`; plain `string` return (not `option`); 7 test cases confirm all branches |
| 4 | Unit tests cover all edge cases (HSP-04 + CFP-04) | VERIFIED | 6 testCases in HermesSessionExtractTests.fs (cases a-e + case-sensitivity guard); 7 testCases in ContentFingerprintTests.fs (cases a-f, with (b) split into two testCases); `dotnet run -- --summary` reports 188 passed, 18 ignored, 0 failed, 0 errored |

**Score:** 4/4 success criteria verified

---

## Required Artifacts — Three-Level Verification

### HSP-01: `SmartRouter.Cli.Adapters.HermesSessionExtract` module

| Level | Check | Result |
|-------|-------|--------|
| Exists | `src/SmartRouter.Cli/Adapters/HermesSessionExtract.fs` | EXISTS |
| Substantive | 59 lines; no TODO/FIXME/placeholder; exports `extractFromSystemPrompt` | SUBSTANTIVE |
| Wired | Compile entry in `SmartRouter.Cli.fsproj` line 29; referenced in test file via `open SmartRouter.Cli.Adapters.HermesSessionExtract` | WIRED |

**Module declaration (line 1):** `module SmartRouter.Cli.Adapters.HermesSessionExtract` — in `Cli`, not `Core`. ARCH-01 preserved.

**Function signature (line 54):** `let extractFromSystemPrompt (req: RouterRequest) : string option` — exact match to HSP-01 spec.

**Implementation:** `req.Messages |> List.tryFind (fun m -> m.Role = System) |> Option.bind (...)` — first-System-message-only (HSP-02); `None` when absent (HSP-02). Uses `Option.bind` for safe None propagation, not `Option.map` (would produce `string option option`).

### HSP-02: First-System-message-only; None when absent

Verified directly in HermesSessionExtract.fs lines 54-59: `List.tryFind (fun m -> m.Role = System)` selects the first match; `Option.bind` short-circuits to `None` when `List.tryFind` returns `None`. Test case (c) in HermesSessionExtractTests.fs confirms no-system-message returns `None`.

### HSP-03: Module-level pre-compiled Regex; case-sensitive

**Line 35-36 of HermesSessionExtract.fs:**
```fsharp
let private sessionIdRx =
    Regex(@"^Session ID:[ \t]*(\S+)", RegexOptions.Multiline)
```
- `let private` at module scope: allocated once at module init, reused across calls.
- Pattern: `^Session ID:[ \t]*(\S+)` — capital `S`, capital `I`, capital `D`; no `RegexOptions.IgnoreCase`.
- `[ \t]*` (not `\s*`): horizontal whitespace only; prevents cross-line matching (executor-documented Wave 1 deviation from original `\s*`; legitimate per REQUIREMENTS intent).
- `RegexOptions.Multiline`: `^` matches line-start anywhere in content.

Case-sensitivity guard: test case (f) in HermesSessionExtractTests.fs verifies `"session id: abc123"` returns `None`.

### HSP-04: 6 testCase entries in HermesSessionExtractTests.fs

| Case | Test Name | Spec Case |
|------|-----------|-----------|
| (a) | "extracts session id when Hermes Session ID line is present" | match-when-present |
| (b) | "returns None when system message has no Session ID line" | no-match-when-absent |
| (c) | "returns None when no system message exists" | no-system-message |
| (d) | "matches Session ID even when on line 3 of system content" | multiline verification |
| (e) | "returns None when Session ID line has no value after colon" | malformed-line-no-value |
| (f) | "case-sensitive: lowercase 'session id:' does not match" | HSP-03 case-sensitivity guard |

`grep -c "testCase"` = 6. All 6 confirmed passing in test run output (visible in `dotnet run -- --summary` stdout for `HermesSessionExtractTests` section).

### CFP-01: `SmartRouter.Cli.Adapters.ContentFingerprint` module

| Level | Check | Result |
|-------|-------|--------|
| Exists | `src/SmartRouter.Cli/Adapters/ContentFingerprint.fs` | EXISTS |
| Substantive | 61 lines; no TODO/FIXME/placeholder; exports `compute` | SUBSTANTIVE |
| Wired | Compile entry in `SmartRouter.Cli.fsproj` line 30; referenced in test file via `open SmartRouter.Cli.Adapters.ContentFingerprint` | WIRED |

**Module declaration (line 1):** `module SmartRouter.Cli.Adapters.ContentFingerprint` — in `Cli`, not `Core`. ARCH-01 preserved.

**Function signature (line 45):** `let compute (req: RouterRequest) : string` — plain `string`, not `string option` (CFP-02).

**Key implementation details verified:**
- `use sha = SHA256.Create()` at line 57: per-call instance, not module-level (thread-safe; CFP-03).
- `Array.map (sprintf "%02x") |> String.concat ""` at line 59: lowercase hex (not `Convert.ToHexString`).
- `hex.Substring(0, 16)` at line 60: exactly 16 chars.
- `truncate` helper (line 18-19): `if s.Length > 4000 then s.[..3999] else s` — exact spec wording.
- Key separator: `truncate system + "|||" + truncate firstUser` at line 56 — exactly three pipes.
- Empty-string defaults: `Option.defaultValue ""` for both system (line 50) and firstUser (line 55) — empty Messages → key = `"|||"` → valid 16-hex (CFP-02).

### CFP-02: Empty message → empty string default; always valid 16-hex (no option)

ContentFingerprint.fs lines 47-55: `Option.defaultValue ""` for both messages. Return type is `string`. Test case (d) in ContentFingerprintTests.fs explicitly validates: `mkReqEmpty()` (empty Messages list) → deterministic, valid 16-hex.

### CFP-03: Deterministic, collision-resistant, pure

- Pure: no I/O, no mutation, no DI. Confirmed by reading the entire file.
- Deterministic: SHA-256 is deterministic; same key → same hash. Test case (a) calls `compute` twice on same input, asserts equality.
- Collision-resistant: test case (b) verifies a single-char change in system or firstUser produces a different fingerprint.
- Thread-safety: `use sha = SHA256.Create()` is per-call; no shared mutable state.

### CFP-04: 7 testCase entries in ContentFingerprintTests.fs

| Case | Test Name | Spec Case |
|------|-----------|-----------|
| (a) | "(a) determinism: identical input produces identical output" | determinism |
| (b-1) | "(b) uniqueness: single-char change in system produces different output" | uniqueness/system |
| (b-2) | "(b) uniqueness: single-char change in firstUser produces different output" | uniqueness/user |
| (c) | "(c) truncation: 4001-char system handled without exception..." | truncation |
| (d) | "(d) empty-message: no messages produces deterministic valid 16-hex" | empty-message |
| (e) | "(e) Korean+English mixed UTF-8 produces valid 16-hex" | multi-byte UTF-8 |
| (f) | "(f) output is exactly 16 chars of lowercase hex" | format assertion |

`grep -c "testCase"` = 7. Executor correctly noted case (b) was split into two testCases. All 7 confirmed passing.

---

## Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `SmartRouter.Cli.fsproj` | `HermesSessionExtract.fs` | `<Compile>` line 29 | WIRED | After `SessionStore.fs` (line 27), before `ContentFingerprint.fs` (line 30) |
| `SmartRouter.Cli.fsproj` | `ContentFingerprint.fs` | `<Compile>` line 30 | WIRED | Directly after `HermesSessionExtract.fs` |
| `SmartRouter.Tests.fsproj` | `HermesSessionExtractTests.fs` | `<Compile>` line 51 | WIRED | Before `RouterTests.fs` (line 53) |
| `SmartRouter.Tests.fsproj` | `ContentFingerprintTests.fs` | `<Compile>` line 52 | WIRED | Before `RouterTests.fs` (line 53) |
| `RouterTests.fs` rootTests | `HermesSessionExtractTests.tests` | line 44 | WIRED | Phase 21 (Plan 21-01) comment |
| `RouterTests.fs` rootTests | `ContentFingerprintTests.tests` | line 45 | WIRED | Phase 21 (Plan 21-02) comment |

---

## ARCH-01 Verification

`src/SmartRouter.Core/` directory listing: `CanaryPorts.fs`, `Domain.fs`, `HardRules.fs`, `ML.fs`, `MLPorts.fs`, `Ports.fs`, `RetrainingPorts.fs`, `Routing.fs`, `SmartRouter.Core.fsproj` — **no new files added**. Both new adapters are in `SmartRouter.Cli/Adapters/` only. ARCH-01 satisfied.

## ARCH-02 Verification (check-no-async.sh)

`bash scripts/check-no-async.sh` exits 0 with output: `OK: no async {} expressions in src/SmartRouter.Core`. New files contain no `async {}` literals (both are pure synchronous functions using BCL only).

---

## Test Run Results

```
dotnet run --project tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -- --summary

188 tests run in 00:01:27 for all
  188 passed
   18 ignored
    0 failed
    0 errored
SUCCESS
```

Expected: 175 baseline + 6 HSP + 7 CFP = 188 passed. Actual: 188 passed. Matches exactly.

---

## Anti-Patterns Scan

Scanned both new adapter files and both new test files for: TODO/FIXME/XXX/HACK, placeholder/lorem ipsum, `return null`/`return {}`/`return []`, empty handlers.

**HermesSessionExtract.fs:** No stubs. Comments document design rationale (Pitfall references, HSP-04 cases). All exports are real implementations.

**ContentFingerprint.fs:** No stubs. Comments document design rationale (Pitfall references, CFP-04 cases). All exports are real implementations.

**HermesSessionExtractTests.fs:** No stubs. All 6 testCases have real assertions (`Expect.isSome`, `Expect.isNone`, `Expect.equal`).

**ContentFingerprintTests.fs:** No stubs. All 7 testCases have real assertions; `assertHexFormat` is a real helper with length and char-set checks.

**Result:** No blocker anti-patterns. No warnings. No placeholders.

---

## Human Verification Required

None. Phase 21 is foundation-only (pure extraction primitives, no operator-facing behavior, no HTTP endpoints, no UI). All verification is fully automated.

---

## Overall Assessment

Phase 21 goal is achieved. Both `HermesSessionExtract` and `ContentFingerprint` adapters:

1. Exist as substantive, non-stub implementations in `SmartRouter.Cli.Adapters`.
2. Are correctly ordered in `SmartRouter.Cli.fsproj` (after `SessionStore.fs`, before `DecisionLogger.fs`).
3. Are pure functions with no I/O, no HTTP, no DI wiring — ready for Phase 22 `CorrelationMiddleware` to call them directly.
4. Are fully covered by 13 unit tests (6 HSP + 7 CFP) all passing.
5. Introduce no ARCH-01 violations (no new Core files).
6. Pass the `check-no-async.sh` constraint.
7. Bring the test suite from 175 to 188 passed with 0 failed, 0 errored.

Phase 22 (`CorrelationMiddleware` wiring) is unblocked.

---

_Verified: 2026-05-12T02:27:40Z_
_Verifier: Claude (gsd-verifier)_
