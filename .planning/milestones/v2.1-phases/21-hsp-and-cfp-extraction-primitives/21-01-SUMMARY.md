---
phase: 21-hsp-and-cfp-extraction-primitives
plan: "01"
subsystem: session-extraction
tags: [fsharp, regex, bcl, pure-function, hermes, session-id, expecto]

# Dependency graph
requires: []
provides:
  - "Pure BCL adapter HermesSessionExtract.extractFromSystemPrompt : RouterRequest -> string option (HSP-01)"
  - "Module-level pre-compiled Regex with RegexOptions.Multiline + case-sensitive matching (HSP-03)"
  - "6 unit tests covering HSP-04 (a)-(e) + HSP-03 case-sensitivity guard"
affects:
  - "22-01"

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Module-level let private pre-compiled Regex for one-shot pattern compilation across all calls (HSP-03)"
    - "Pure-function Cli adapter (no DI, no logging, no port interface) — appropriate for transformation primitives consumed by a later middleware cascade"
    - "[ \t]* instead of \\s* for horizontal-whitespace-only between regex colon and captured group (prevents cross-line match bug)"

key-files:
  created:
    - "src/SmartRouter.Cli/Adapters/HermesSessionExtract.fs"
    - "tests/SmartRouter.Tests/HermesSessionExtractTests.fs"
  modified:
    - "src/SmartRouter.Cli/SmartRouter.Cli.fsproj"
    - "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
    - "tests/SmartRouter.Tests/RouterTests.fs"

key-decisions:
  - "ARCH-01: adapter placed in SmartRouter.Cli.Adapters, not SmartRouter.Core (corrects design doc's incorrect module header SmartRouter.Core.Routing.HermesSessionExtract)"
  - "Regex uses [ \t]* not \\s* to prevent cross-line match — \\s includes \\n, causing malformed-line case (e) to erroneously match next line's content"
  - "No RegexOptions.Compiled (JIT-emit cost not justified; module-level let private already amortizes allocation)"
  - "No RegexOptions.IgnoreCase (HSP-03 case-sensitive; Hermes always emits Session ID: with exact capitalization)"
  - "Option.bind not Option.map (inner lambda returns string option; map would produce string option option)"

patterns-established:
  - "Module-level let private pre-compiled Regex: F# idiom for one-time setup, reused across all calls"
  - "Pure Cli adapter pattern: pure synchronous function, no DI, no port interface, no logging — Plan 21-02 (ContentFingerprint) follows same pattern"
  - "Use [ \t]* not \\s* when horizontal-whitespace-only is intended and cross-line matching must be prevented"

# Metrics
duration: 8min
completed: "2026-05-12"
---

# Phase 21 Plan 01: HermesSessionExtract Adapter Summary

**BCL-only pure adapter `HermesSessionExtract.extractFromSystemPrompt` using module-level pre-compiled Regex with `RegexOptions.Multiline` to extract Hermes `--pass-session-id` session_id from RouterRequest System messages**

## Performance

- **Duration:** 8 min
- **Started:** 2026-05-12T11:02:07Z
- **Completed:** 2026-05-12T11:10:07Z
- **Tasks:** 3
- **Files modified:** 5

## Accomplishments

- Shipped `HermesSessionExtract.fs` in `SmartRouter.Cli.Adapters` — pure BCL adapter with module-level pre-compiled Regex covering HSP-01, HSP-02, HSP-03, HSP-04
- Added 6 Expecto unit tests (HSP-04 cases a-e + HSP-03 case-sensitivity guard): 175 → 181 passing
- Fixed regex pattern from `\s*` to `[ \t]*` discovered during test case (e) — prevents cross-line match bug where `Session ID:   \n` would erroneously capture the next line's content

## Task Commits

Each task was committed atomically:

1. **Task 1: Add HermesSessionExtract.fs Cli adapter + register Compile entry** - `6a7eb37` (feat)
2. **Task 2: Add HermesSessionExtractTests.fs + register in fsproj and rootTests** - `2ad565c` (test)
3. **Task 3: Write 21-01-SUMMARY.md plan summary** - (docs — this commit)

**Plan metadata:** staged with SUMMARY.md in `docs(21-01): complete plan SUMMARY`

## Files Created/Modified

- `src/SmartRouter.Cli/Adapters/HermesSessionExtract.fs` — Pure adapter: module-level `let private sessionIdRx`, `extractFromSystemPrompt : RouterRequest -> string option`
- `tests/SmartRouter.Tests/HermesSessionExtractTests.fs` — 6 Expecto test cases (HSP-04 a-e + HSP-03 guard)
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — Compile entry added after SessionStore.fs (Phase 21 block)
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — Compile entry added before RouterTests.fs
- `tests/SmartRouter.Tests/RouterTests.fs` — rootTests list entry appended (PITFALL-26)

## Decisions Made

- **ARCH-01 placement**: `SmartRouter.Cli.Adapters`, not `SmartRouter.Core`. The design doc (`hermes-session-without-modification.md §2.2`) incorrectly used `SmartRouter.Core.Routing.HermesSessionExtract` as the module header. Corrected to `module SmartRouter.Cli.Adapters.HermesSessionExtract` per ROADMAP.md §Architectural Invariants and plan guidance.
- **`[ \t]*` not `\s*`**: Discovered during Task 2 test execution that `\s*` includes `\n`, allowing the regex to cross line boundaries and match the next line's content when a malformed `Session ID:   \n` line is present. Fixed to `[ \t]*` (horizontal whitespace only).
- **No `RegexOptions.Compiled`**: JIT-emit cost not justified at our request rate; module-level `let private` binding already amortizes the one allocation.
- **No `testSequenced` wrapper**: HermesSessionExtractTests are pure functions (no Console.SetOut, no temp dirs, no DefaultHttpContext). Mirrors HardRulesTests.fs precedent.
- **`Option.bind` not `Option.map`**: `List.tryFind` returns `Message option`; the inner lambda returns `string option`; `Option.map` would produce `string option option` — rejected by the `RouterRequest -> string option` signature.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Regex `\s*` cross-line match causes malformed-line test (e) to fail**

- **Found during:** Task 2 (HermesSessionExtractTests.fs execution)
- **Issue:** Plan spec and research doc used `\s*` between the colon and `(\S+)` in the pattern `^Session ID:\s*(\S+)`. Since `\s` includes `\n` (newline), a malformed line like `Session ID:   \nModel: qwen-35b` causes `\s*` to consume the spaces AND the newline, then `\S+` matches `Model:` on the next line — erroneously returning `Some "Model:"` instead of `None`.
- **Fix:** Changed pattern from `^Session ID:\s*(\S+)` to `^Session ID:[ \t]*(\S+)`. `[ \t]*` matches only spaces and tabs (horizontal whitespace), preventing cross-line matching. The malformed-line case (e) now correctly returns `None` because `Session ID:   ` followed by `\n` has no non-whitespace on the same line after the colon.
- **Files modified:** `src/SmartRouter.Cli/Adapters/HermesSessionExtract.fs`
- **Verification:** All 6 test cases pass after fix. 181 passed + 18 ignored + 0 failed.
- **Committed in:** `2ad565c` (Task 2 commit — fix was discovered during test authoring and staged with test files)

---

**Total deviations:** 1 auto-fixed (Rule 1 — bug in regex pattern from spec)
**Impact on plan:** Fix essential for HSP-04 case (e) correctness. The `[ \t]*` pattern is strictly more correct than `\s*` for the Hermes emission format (always one space after colon on the same line). No scope creep.

## Issues Encountered

- `dotnet test` command appeared to hang without output in the shell environment. Worked around by using `dotnet run --project ... -- --summary` to run the test executable directly. All tests ran correctly.

## Next Phase Readiness

- `extractFromSystemPrompt : RouterRequest -> string option` is available in `SmartRouter.Cli.Adapters.HermesSessionExtract`; Phase 22 Plan 22-01 can wire it directly into `CorrelationMiddleware` as Tier 2 of the cascade (no DI registration needed — pure module function)
- Pattern established for Plan 21-02 (`ContentFingerprint`): pure-function Cli adapter with module-level pre-compiled helper, registered in same Phase-21 block in `SmartRouter.Cli.fsproj` after `HermesSessionExtract.fs`
- Test count baseline for Phase 21 Plan 21-02: **181 passed + 18 ignored + 0 failed**

---
*Phase: 21-hsp-and-cfp-extraction-primitives*
*Completed: 2026-05-12*
