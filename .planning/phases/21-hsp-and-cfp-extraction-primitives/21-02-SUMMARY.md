---
phase: 21-hsp-and-cfp-extraction-primitives
plan: "02"
subsystem: session-extraction
tags: [fsharp, sha256, bcl, pure-function, hex-encoding, truncation, expecto]

# Dependency graph
requires:
  - phase: "21-01"
    provides: "Pure BCL adapter HermesSessionExtract in SmartRouter.Cli.Adapters; Phase-21 fsproj block established (HermesSessionExtract.fs insertion point)"
provides:
  - "Pure BCL adapter ContentFingerprint.compute : RouterRequest -> string (CFP-01, CFP-02, CFP-03)"
  - "16-char lowercase hex SHA-256 prefix of truncate(system) + '|||' + truncate(firstUser) (CFP-01)"
  - "7 unit tests covering CFP-04 cases (a)-(f): determinism, uniqueness x2, truncation, empty-message, Korean+English UTF-8, 16-char lowercase format"
  - "Phase 21 COMPLETE: all 8 requirements (HSP-01..04 + CFP-01..04) covered across plans 21-01 + 21-02"
affects:
  - "22-01"

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Per-call use sha = SHA256.Create() lifecycle — NOT module-level binding (SHA256 is not thread-safe; shared instance corrupts concurrent hash computations silently)"
    - "Lowercase hex via Array.map (sprintf \"%02x\") |> String.concat \"\" — explicitly NOT Convert.ToHexString (which produces UPPERCASE)"
    - "F# slice s.[..3999] for inclusive 4000-char truncation exactly matching CFP-01 spec (condition strict > 4000)"
    - "Test format+determinism assertions for hash-output edge cases — no hardcoded magic constants (avoids brittle rot if separator or encoding changes)"

key-files:
  created:
    - "src/SmartRouter.Cli/Adapters/ContentFingerprint.fs"
    - "tests/SmartRouter.Tests/ContentFingerprintTests.fs"
  modified:
    - "src/SmartRouter.Cli/SmartRouter.Cli.fsproj"
    - "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
    - "tests/SmartRouter.Tests/RouterTests.fs"

key-decisions:
  - "ARCH-01: ContentFingerprint placed in SmartRouter.Cli.Adapters, not SmartRouter.Core — pure transformation primitive consumed by Phase 22 middleware cascade"
  - "Empty-message case (Messages=[]) NOT special-cased: key = '|||', SHA-256 runs on separator, returns deterministic valid 16-hex (CFP-02)"
  - "No option wrapper on return type: compute always returns string, never string option (CFP-02)"
  - "Option.map not Option.bind for system/firstUser extraction (inner lambda returns string, not string option — opposite of HermesSessionExtract pattern)"
  - "Case (b) split into two testCases (system change + firstUser change) for clarity — yields 7 new tests, not 6; test count 188, not 187"

patterns-established:
  - "Per-call SHA256 lifecycle: use sha = SHA256.Create() inside function body (thread-safe; mirrors CorrelationMiddleware.fs:69 and SelfRouter.fs:168)"
  - "Lowercase hex convention: Array.map (sprintf \"%02x\") |> String.concat \"\" (NOT Convert.ToHexString) — project-wide pattern"
  - "CFP-style inclusive truncation: if s.Length > 4000 then s.[..3999] else s (spec-exact; F# slice is inclusive on both ends)"
  - "Hash-output test design: assert determinism + format assertions, not hardcoded precomputed values"

# Metrics
duration: 8min
completed: "2026-05-12"
---

# Phase 21 Plan 02: ContentFingerprint Adapter Summary

**BCL-only pure adapter `ContentFingerprint.compute` computing 16-char lowercase hex SHA-256 prefix of `truncate(system) + "|||" + truncate(firstUser)` using per-call `SHA256.Create()` lifecycle and `sprintf "%02x"` lowercase encoding**

## Performance

- **Duration:** 8 min
- **Started:** 2026-05-12T02:11:00Z
- **Completed:** 2026-05-12T02:19:11Z
- **Tasks:** 3
- **Files modified:** 5

## Accomplishments

- Shipped `ContentFingerprint.fs` in `SmartRouter.Cli.Adapters` — pure BCL adapter covering CFP-01 (truncate + SHA-256 + 16-hex), CFP-02 (system/firstUser resolution + no option wrapper), CFP-03 (determinism + purity)
- Added 7 Expecto unit tests (CFP-04 cases a-f plus split of (b) into two testCases): 181 → 188 passing
- Phase 21 complete: all 8 requirements (HSP-01..04 + CFP-01..04) covered; combined test delta +13 (175 → 188 passing)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add ContentFingerprint.fs Cli adapter + register Compile entry** - `2454a20` (feat)
2. **Task 2: Add ContentFingerprintTests.fs + register in fsproj and rootTests** - `ec16f5e` (test)
3. **Task 3: Write 21-02-SUMMARY.md plan summary** - (docs — this commit)

**Plan metadata:** staged with SUMMARY.md in `docs(21-02): complete content-fingerprint plan`

## Files Created/Modified

- `src/SmartRouter.Cli/Adapters/ContentFingerprint.fs` — Pure adapter: private `truncate` helper + `compute : RouterRequest -> string`
- `tests/SmartRouter.Tests/ContentFingerprintTests.fs` — 7 Expecto test cases (CFP-04 a-f; case (b) split into two testCases)
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — Compile entry added after HermesSessionExtract.fs, before DecisionLogger.fs
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — Compile entry added after HermesSessionExtractTests.fs, before RouterTests.fs
- `tests/SmartRouter.Tests/RouterTests.fs` — rootTests list entry appended (PITFALL-26)

## Decisions Made

- **ARCH-01 placement**: `SmartRouter.Cli.Adapters`, not `SmartRouter.Core`. Both new Phase-21 adapters are transformation primitives consumed by Phase 22 middleware; they belong in Cli per ARCH-01.
- **Empty-message not special-cased**: When `Messages = []`, both `system` and `firstUser` resolve to `""`, making `key = "|||"`. SHA-256 runs on this literal separator and returns a stable, deterministic 16-hex value. No `if key = "|||" then ...` short-circuit was added (would defeat determinism and violate CFP-02).
- **No `option` return type**: `compute` always returns a `string`. Empty/absent messages yield a valid fingerprint of `"|||"`, not `None`. CFP-02 is explicit: "always returns a valid 16-hex string."
- **Test count 188 not 187**: Plan predicted +6 tests (cases a-f) but case (b) was split into two `testCase` entries for clarity (one for system-change uniqueness, one for firstUser-change uniqueness). This yields 7 new tests (188 total), preserving full coverage with better failure messages.
- **`Option.map` not `Option.bind`**: The inner lambda for extracting `m.Content` returns `string`, not `string option`. This is the opposite of Plan 21-01's `extractFromSystemPrompt` which used `Option.bind` because its lambda returned `string option`. The difference is documented in the source file.

## Deviations from Plan

None — plan executed exactly as written. Test count was 188 (not 187) due to case (b) being split into two named `testCase` entries for clarity; this is an improvement in test specificity, not a deviation from the CFP-04 requirement.

## Issues Encountered

- `dotnet test` command again appeared to produce no output (same issue as Plan 21-01). Worked around by using `dotnet run --project ... -- --summary` to run the test executable directly, which shows full Expecto output including pass/fail counts.

## Next Phase Readiness

- `compute : RouterRequest -> string` is available in `SmartRouter.Cli.Adapters.ContentFingerprint`; Phase 22 Plan 22-01 can wire it directly into `CorrelationMiddleware` as Tier 3 of the cascade (no DI registration needed — pure module function)
- Both Phase-21 adapters (`HermesSessionExtract.extractFromSystemPrompt` and `ContentFingerprint.compute`) are in tree and tested; Phase 22 can proceed immediately
- Test count baseline for Phase 22: **188 passed + 18 ignored + 0 failed**
- Phase 21 complete: 8/8 requirements (HSP-01..04 + CFP-01..04) shipped

---
*Phase: 21-hsp-and-cfp-extraction-primitives*
*Completed: 2026-05-12*
