---
phase: 15-quality-signal-enrichment
plan: 03
type: execute
wave: 3
depends_on: ["15-01", "15-02"]
files_modified:
  - tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
  - tests/SmartRouter.Tests/RouterTests.fs
  - README.md
  - CHANGELOG.md
  - .planning/docs/quality-check-improvement-options.md
autonomous: true

must_haves:
  truths:
    - "QualitySignalEnrichmentTests.fs covers each of the 5 detection dimensions in isolation (unit tests on pure functions) and at least one fake-Kestrel integration test that triggers fallback via finish_reason='length'"
    - "/stats endpoint integration test asserts that triggering each dimension increments the correct quality_check_hits_* counter and that the counters are exposed as int64 snake_case JSON keys"
    - "TraceRecord bad_reason field is asserted in trace-output integration test using JsonDocument.Parse — Some 'tag=value' on Bad, null on Good"
    - "Phase 14 QF-01/QF-02 tests pass UNCHANGED in the same `dotnet test` run as the new QSE-* tests (QSE-06 backward-compat hard gate)"
    - "README.md is updated in §5.5 (5-dimension trigger list), §7 (BadFinishReasons + EntropyThreshold rows + BadKeywords case-insensitive note + refusal-pattern operator-opt-in guidance), §9.3 (trace schema bad_reason field), §8 or §9 (/stats new quality_check_hits_* fields)"
    - "CHANGELOG.md [Unreleased] section has a `### Changed` entry per CONTEXT.md migration tone (silent enable behavior change documented)"
    - "Final test baseline >= 88 + N (N = new QSE testCases) passed; 16 ignored unchanged; 0 failed"
  artifacts:
    - path: "tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs"
      provides: "Phase 15 unit + integration test coverage"
      contains: "QSE-01"
      contains2: "QSE-06"
      contains3: "extractFinishReason"
      contains4: "charEntropy"
      contains5: "koreanRatio"
    - path: "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
      provides: "QualitySignalEnrichmentTests.fs in <Compile> list before RouterTests.fs"
      contains: "QualitySignalEnrichmentTests.fs"
    - path: "tests/SmartRouter.Tests/RouterTests.fs"
      provides: "QualitySignalEnrichmentTests.tests appended to rootTests"
      contains: "QualitySignalEnrichmentTests.tests"
    - path: "README.md"
      provides: "Phase 15 sync rule areas: §5.5, §7, §9.3, §9.6 or §8 (whichever documents /stats)"
      contains: "BadFinishReasons"
      contains2: "EntropyThreshold"
      contains3: "bad_reason"
      contains4: "case-insensitive"
    - path: "CHANGELOG.md"
      provides: "[Unreleased] ### Changed entry for Phase 15 silent-enable behavior"
      contains: "case-insensitive"
      contains2: "finish_reason"
  key_links:
    - from: "tests/SmartRouter.Tests/RouterTests.fs"
      to: "QualitySignalEnrichmentTests.tests"
      via: "rootTests list append (PITFALL-26 — explicit registration required)"
      pattern: "QualitySignalEnrichmentTests\\.tests"
    - from: "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
      to: "QualitySignalEnrichmentTests.fs"
      via: "<Compile> entry placed BEFORE RouterTests.fs (compile-order)"
      pattern: "QualitySignalEnrichmentTests\\.fs"
    - from: "README.md §7 Routing.QualityFallback table"
      to: "src/SmartRouter.Cli/appsettings.json:Routing.QualityFallback"
      via: "table rows for all 5 keys with current defaults"
      pattern: "BadFinishReasons.*length.*content_filter"
---

<objective>
Add Phase 15 test coverage, update README to satisfy CLAUDE.md sync rule for the 12 mandatory areas affected, and add a CHANGELOG entry documenting the silent-enable behavior change. This plan does NOT touch source code in `src/` — all changes are in tests, README, CHANGELOG, and planning docs. Verification is the deliverable: prove the wiring from Plan 15-02 actually catches the 5 dimensions and exposes them in trace + /stats.

Purpose: A phase is "done" only when its observable truths are demonstrated by tests and documented for operators. Plan 15-03 closes both: 6 testCases proving each QSE-01..06 + integration test for /stats counters and trace bad_reason; README sections updated so operators discover the new behavior without reading source.

Output:
- New `tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs` with ~7-9 testCases
- `SmartRouter.Tests.fsproj` updated with new `<Compile>` entry
- `RouterTests.fs` updated with new entry in rootTests list
- README §5.5 (Quality fallback) extended with 5-dimension trigger list + case-insensitive note
- README §7 (Routing.QualityFallback config table) extended with `BadFinishReasons` row + `EntropyThreshold` row + updated `BadKeywords` description
- README §9.3 (TraceLog schema; or wherever the trace schema lives — verify section number) — `bad_reason` field documented with example jq workflow
- README §8 or §9.6 (/stats endpoint section) — 4 new `quality_check_hits_*` fields documented
- CHANGELOG.md `[Unreleased] ### Changed` entry per CONTEXT.md migration tone
- Optional: `.planning/docs/quality-check-improvement-options.md` updated with "Phase 15 implemented Tier 1+2" note for Phase 16/17 reference
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/STATE.md
@.planning/phases/15-quality-signal-enrichment/15-CONTEXT.md
@.planning/phases/15-quality-signal-enrichment/15-RESEARCH.md
@.planning/phases/15-quality-signal-enrichment/15-01-SUMMARY.md
@.planning/phases/15-quality-signal-enrichment/15-02-SUMMARY.md
@tests/SmartRouter.Tests/QualityFallbackTests.fs
@tests/SmartRouter.Tests/RouterTests.fs
@tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
@README.md
@CLAUDE.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Create QualitySignalEnrichmentTests.fs (unit + integration) and register in fsproj + rootTests</name>
  <files>
    tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs
    tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    tests/SmartRouter.Tests/RouterTests.fs
  </files>
  <action>
**Goal:** Demonstrate observable correctness of all 5 detection dimensions + trace `bad_reason` emission + /stats counter increments + Phase 14 backward-compat.

**File 1: Create `tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs`.**

Module:
```fsharp
module SmartRouter.Tests.QualitySignalEnrichmentTests
```

**Imports** — mirror QualityFallbackTests.fs pattern:
- `open Expecto`
- `open System`
- `open System.Text.Json`
- `open SmartRouter.Cli.Adapters.QualityCheck` (for analyzeResponse, isBadResponse, helpers, types)
- For integration tests, the same Kestrel + DI fixture imports as QualityFallbackTests.fs (line 1-30 there).

**Test cases — each addresses a specific QSE-* requirement:**

```fsharp
let private opts (badKeywords: string array) =
    { Enabled            = true
      MinResponseLength  = 30
      BadKeywords        = badKeywords
      BadFinishReasons   = [| "length"; "content_filter" |]
      EntropyThreshold   = 2.5 }

let private envelope (content: string) (finishReason: string) =
    sprintf """{"choices":[{"finish_reason":"%s","message":{"role":"assistant","content":%s}}]}"""
        finishReason
        (JsonSerializer.Serialize(content))
```

**Test 1 — QSE-01 (finish_reason match):**
```fsharp
testCase "QSE-01: finish_reason='length' triggers Bad regardless of content length" <| fun () ->
    let opts = opts [||]
    // Long, normal-entropy content that would otherwise pass all other stages
    let body = envelope "This is a normal length response with plenty of content and good entropy across many varied characters." "length"
    let verdict = analyzeResponse opts (Some "length") body
    match verdict with
    | Bad (FinishReasonMatch fr) -> Expect.equal fr "length" "FinishReasonMatch should carry the matched value"
    | other -> failtestf "Expected Bad (FinishReasonMatch \"length\") got %A" other
```

**Test 2 — QSE-01 alternate finish_reason ('stop' is GOOD):**
```fsharp
testCase "QSE-01: finish_reason='stop' does NOT trigger (only length/content_filter)" <| fun () ->
    let opts = opts [||]
    let body = envelope "This is a normal length response with plenty of content and good entropy across many varied characters." "stop"
    let verdict = analyzeResponse opts (Some "stop") body
    Expect.equal verdict Good "stop is the normal completion reason"
```

**Test 3 — QSE-02 (case-insensitive keyword):**
```fsharp
testCase "QSE-02: BadKeyword 'TODO' matches lowercase 'todo' in content (case-insensitive)" <| fun () ->
    let opts = opts [| "TODO" |]
    // Long content (passes length + entropy) but contains lowercase "todo"
    let body = envelope "This is a placeholder response that just says todo right here in the middle of the text body." "stop"
    let verdict = analyzeResponse opts None body
    match verdict with
    | Bad (KeywordMatch kw) -> Expect.equal kw "TODO" "KeywordMatch reports the configured keyword (not the matched casing)"
    | other -> failtestf "Expected Bad (KeywordMatch \"TODO\") got %A" other
```

**Test 4 — QSE-03 (refusal-keyword opt-in works when operator adds them):**
```fsharp
testCase "QSE-03: operator-added refusal pattern 'I cannot' is matched when configured (case-insensitive)" <| fun () ->
    let opts = opts [| "I cannot" |]
    let body = envelope "I cannot help with that request because it falls outside my supported scope here." "stop"
    let verdict = analyzeResponse opts None body
    match verdict with
    | Bad (KeywordMatch kw) -> Expect.equal kw "I cannot" "operator opt-in refusal pattern matches"
    | other -> failtestf "Expected Bad (KeywordMatch \"I cannot\") got %A" other
```

(This test demonstrates the policy: refusal patterns are NOT in default BadKeywords — operator must opt in. The test asserts the capability, not the default.)

**Test 5 — QSE-04 (Korean length correction):**
```fsharp
testCase "QSE-04: Korean response of 28 chars passes MinResponseLength=30 via effective length" <| fun () ->
    let opts = opts [||]
    let korean = "안녕하세요. 잘 지내고 있어요. 오늘은 좋은 하루입니다."  // ~28 chars
    let body = envelope korean "stop"
    let verdict = analyzeResponse opts None body
    Expect.equal verdict Good "Korean ratio inflates effective length above 30"

testCase "QSE-04: pure ASCII 28-char content does NOT pass (effective = length)" <| fun () ->
    let opts = opts [||]
    let body = envelope "Twenty eight chars exactly!!" "stop"  // 28 chars; verify with .Length
    let verdict = analyzeResponse opts None body
    match verdict with
    | Bad (LengthBelow n) -> Expect.isTrue (n < 30) "ASCII content has no Korean boost"
    | other -> failtestf "Expected Bad (LengthBelow _) got %A" other
```

**Test 6 — QSE-05 (entropy detection):**
```fsharp
testCase "QSE-05: low-entropy repetitive content triggers Bad (LowEntropy)" <| fun () ->
    let opts = opts [||]
    // 100+ chars, low entropy
    let repetitive = String.replicate 30 "the "  // "the the the..." 4 unique chars
    let body = envelope repetitive "stop"
    let verdict = analyzeResponse opts None body
    match verdict with
    | Bad (LowEntropy s) -> Expect.isLessThan s 2.5 "entropy below threshold"
    | other -> failtestf "Expected Bad (LowEntropy _) got %A" other

testCase "QSE-05: charEntropy of normal text is 4.0+" <| fun () ->
    let normal = "This is a perfectly normal English sentence with diverse character variety."
    let h = charEntropy normal
    Expect.isGreaterThan h 4.0 "normal text has high entropy"
```

**Test 7 — QSE-06 (Phase 14 backward-compat — re-run isBadResponse semantics):**
```fsharp
testCase "QSE-06: isBadResponse Phase 14 wrapper still returns true for 'TODO' content" <| fun () ->
    let opts = opts [| "TODO" |]
    let body = envelope "This is a long enough response. TODO: implement details later." "stop"
    Expect.isTrue (isBadResponse opts body) "wrapper preserves Phase 14 keyword behavior"

testCase "QSE-06: isBadResponse Phase 14 wrapper returns false for good response" <| fun () ->
    let opts = opts [| "TODO" |]
    let body = envelope "This is a perfectly good and complete response without any issues." "stop"
    Expect.isFalse (isBadResponse opts body) "wrapper preserves Phase 14 good-path behavior"
```

**Test 8 — Cheap-first cascade order (first-match wins):**
```fsharp
testCase "Cascade: finish_reason wins over content checks (early exit at stage 1)" <| fun () ->
    let opts = { opts [| "TODO" |] with MinResponseLength = 30 }
    // Content also fails length AND has TODO — but finish_reason fires first
    let body = envelope "Short. TODO" "length"
    let verdict = analyzeResponse opts (Some "length") body
    match verdict with
    | Bad (FinishReasonMatch _) -> ()  // pass — finish_reason won
    | other -> failtestf "Expected stage-1 win, got %A" other
```

**Test 9 — Integration test (fake-Kestrel) — full end-to-end finish_reason → fallback fires + bad_reason in trace + counter increments:**

Mirror the fake-Kestrel pattern from QualityFallbackTests.fs (lines 30-300 area). The integration test:
1. Starts a fake 35B server returning `{"choices":[{"finish_reason":"length","message":{"content":"<long enough good content that passes all other stages>"}}]}`.
2. Starts a fake 122B server returning a normal good response with `finish_reason="stop"`.
3. POSTs to /v1/chat/completions; expects 200 + the 122B body.
4. Reads the trace JSONL file for the correlation_id; asserts `bad_reason == "finish_reason=length"` and `fallback_kind == "quality"`.
5. Calls /stats; asserts `quality_check_hits_finish_reason >= 1`.

**testSequenced wrap** the entire `testList "quality-signal-enrichment"` because the integration test uses Console.SetOut + temp dirs (PITFALL-27).

**Final structure:**
```fsharp
let tests =
    testSequenced <| testList "quality-signal-enrichment" [
        // Test 1-8 (unit)
        // Test 9 (integration)
    ]
```

**File 2: `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj`** — add `<Compile>` entry for the new file BEFORE `RouterTests.fs`:

After the existing `<Compile Include="QualityFallbackTests.fs" />` line:
```xml
<!-- Phase 15: quality signal enrichment tests -->
<Compile Include="QualitySignalEnrichmentTests.fs" />
```

**File 3: `tests/SmartRouter.Tests/RouterTests.fs`** — append to rootTests list. After the existing `SmartRouter.Tests.QualityFallbackTests.tests  // Phase 14` line:

```fsharp
        SmartRouter.Tests.QualitySignalEnrichmentTests.tests  // Phase 15
```

PITFALL-26: explicit registration is mandatory; auto-discovery is forbidden in this codebase.
  </action>
  <verify>
    1. `dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` exit 0, no warnings.
    2. `dotnet test --filter "FullyQualifiedName~QualitySignalEnrichmentTests"` — ALL new testCases pass.
    3. `dotnet test --filter "FullyQualifiedName~QualityFallbackTests"` — Phase 14 baseline (QF-01..QF-08, 8 cases) pass UNCHANGED.
    4. `dotnet test` (full): >= (88 + N) passed where N = number of new QSE testCases (8 unit + 1 integration = 9 typical) + 16 ignored + 0 failed. State the actual N+88 baseline in the SUMMARY.
    5. `grep -c "QualitySignalEnrichmentTests" tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` returns 1.
    6. `grep -c "QualitySignalEnrichmentTests.tests" tests/SmartRouter.Tests/RouterTests.fs` returns 1.
    7. `grep -c "testCase \"QSE-" tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs` returns >= 6 (one per QSE-01..06; can be more if you split QSE-04/05 into multiple cases).
    8. Integration test passes: `grep -c "bad_reason" tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs` returns >= 1; `grep -c "quality_check_hits_finish_reason" tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs` returns >= 1.
  </verify>
  <done>
QualitySignalEnrichmentTests.fs has 8-9 testCases covering all 5 detection dimensions + cheap-first cascade order + Phase 14 wrapper backward-compat + 1 fake-Kestrel integration test asserting trace bad_reason and /stats counter increments. Test file registered in fsproj + RouterTests.rootTests. All tests pass alongside Phase 14 baseline.

**Commit:** `test(15-03): add QualitySignalEnrichmentTests for 5 detection dimensions + integration test`
  </done>
</task>

<task type="auto">
  <name>Task 2: Update README — §5.5 (5-dimension trigger list), §7 (config table), §9.3 (trace bad_reason), /stats section (4 new fields)</name>
  <files>README.md</files>
  <action>
**CLAUDE.md sync rule areas affected by Phase 15 (per orchestrator's "Key constraints" callout):**
- Area 5 — Routing pipeline behavior (§5.5)
- Area 7 — Configuration keys (§7 Routing.QualityFallback)
- Area 9 — DecisionLog/TraceLog JSONL schema (§9.3 trace schema)
- Area 11 — Operator workflows (jq workflow on bad_reason)
- /stats endpoint behavior (§8 or wherever endpoints are documented)

**Step 1 — Update §5.5 (Quality fallback subsection).**

Use Read to find the current §5.5 content. Locate the "Trigger conditions" or equivalent bullets list. Replace/extend with:

> **Trigger conditions** (since Phase 15; cheap-first cascade — first match wins):
>
> 1. **finish_reason match** — upstream `choices[0].finish_reason` matches any value in `Routing.QualityFallback.BadFinishReasons` (default `["length", "content_filter"]`). Catches max-tokens-truncated and content-filtered responses regardless of length or keywords.
> 2. **Effective length below threshold** — `effectiveLength = int(length × (1 + koreanRatio × 0.8))` is below `MinResponseLength` (default 30). Korean responses are inflated by their Hangul-syllable ratio so a 28-char Korean answer (effective ~50) passes while a 28-char ASCII answer fails.
> 3. **Low Shannon entropy** — `charEntropy(content) < EntropyThreshold` (default 2.5). Catches token-loop responses (`"the the the..."`) that pass length and keyword checks. Normal text scores 4.0-5.0+; pathological loops score 1.0-2.0.
> 4. **Bad keyword present** — content contains any value in `Routing.QualityFallback.BadKeywords` (case-insensitive since Phase 15; defaults `["TODO", "I think"]`). Operators may add refusal patterns like `"I cannot"`, `"As an AI"`, `"I'm unable"` if appropriate for their traffic — these are NOT in the default to avoid false positives in Q&A about AI itself.
>
> When any check fires, the request is retried on Qwen 122B (graceful degradation: if 122B is unreachable, the original 35B response is forwarded as-is). The TraceLog (when enabled with `--trace-responses`) records which check fired in the new `bad_reason` field.

**Step 2 — Update §7 Routing.QualityFallback config table.**

Locate the existing config table (it currently lists `Enabled`, `MinResponseLength`, `BadKeywords` rows). Add 2 new rows + update BadKeywords description:

| Key                       | Type     | Default                          | Description                                                                                                                                                                |
|---------------------------|----------|----------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Enabled`                 | bool     | `true`                           | Master kill switch. When `false`, no quality checks run regardless of other settings.                                                                                      |
| `MinResponseLength`       | int      | `30`                             | Minimum effective length (Korean-aware) below which the response is considered bad. `<= 0` resets to default.                                                              |
| `BadKeywords`             | string[] | `["TODO", "I think"]`            | Case-insensitive substring matches against assistant content (since Phase 15). Operator may add refusal patterns like `"I cannot"`, `"As an AI"` if appropriate for traffic. |
| `BadFinishReasons`        | string[] | `["length", "content_filter"]`   | **NEW Phase 15.** Match upstream `choices[0].finish_reason` (case-insensitive). `"length"` catches max-tokens-truncated; `"content_filter"` catches policy refusals. Empty array disables this check. |
| `EntropyThreshold`        | float    | `2.5`                            | **NEW Phase 15.** Shannon character-entropy threshold. Below this, the response is considered repetitive/looped. Set to `0` (or omit) to use default; set to a tiny positive value (e.g. `0.01`) to effectively disable. |

Add operator override example below the table:
```jsonc
"Routing": {
  "QualityFallback": {
    "BadKeywords": ["TODO", "I think", "I cannot", "As an AI"],
    "BadFinishReasons": ["length"],
    "EntropyThreshold": 2.0
  }
}
```

**Step 3 — Update §9.3 (TraceLog schema) — add `bad_reason` field row.**

Locate the existing 12-field TraceRecord table (Phase 14 baseline). Add row 13:

| Field        | Type            | Description                                                                                                          |
|--------------|-----------------|----------------------------------------------------------------------------------------------------------------------|
| `bad_reason` | string \| null  | **NEW Phase 15.** Format `"tag=value"` when quality fallback fired; `null` when response was judged good or fallback was availability-driven. Tags: `length`, `keyword`, `finish_reason`, `entropy`. |

`schema_version` stays at `1` — additive field addition is backward-compatible with existing JSONL consumers.

Add operator jq workflow example:
```bash
# Which check is most often firing? (count by tag prefix)
jq -r 'select(.bad_reason != null) | .bad_reason | split("=")[0]' \
  logs/trace/$(date -u +%F).jsonl | sort | uniq -c | sort -rn

# Show prompts where entropy detection fired
jq 'select(.bad_reason | startswith("entropy=")) | {prompt_excerpt, initial_response_excerpt, bad_reason}' \
  logs/trace/$(date -u +%F).jsonl
```

**Step 4 — Update /stats endpoint section (likely §8 or §9.x).**

Locate the existing /stats wire schema documentation. Append 4 new fields:

| Field                              | Type   | Description                                                                                                          |
|------------------------------------|--------|----------------------------------------------------------------------------------------------------------------------|
| `quality_check_hits_finish_reason` | int64  | **NEW Phase 15.** Process-lifetime count of quality fallbacks triggered by `finish_reason` match.                    |
| `quality_check_hits_length`        | int64  | **NEW Phase 15.** Process-lifetime count of quality fallbacks triggered by effective-length-below-threshold.         |
| `quality_check_hits_entropy`       | int64  | **NEW Phase 15.** Process-lifetime count of quality fallbacks triggered by Shannon entropy below threshold.          |
| `quality_check_hits_keyword`       | int64  | **NEW Phase 15.** Process-lifetime count of quality fallbacks triggered by case-insensitive `BadKeywords` match.     |

Add operator monitoring example:
```bash
# Spot which check is fallback-trigger-dominant in production
curl -s http://127.0.0.1:4000/stats | \
  jq '{quality_check_hits_finish_reason, quality_check_hits_length, quality_check_hits_entropy, quality_check_hits_keyword}'
```

**Step 5 — Verify table of contents reflects any new subsections.** If you added a new sub-section number (e.g., "§5.5.1 Trigger details"), update the ToC to keep anchor links stable.

**Important — DO NOT renumber existing sections.** CLAUDE.md says "Section numbering is meaningful — prefer adding a sub-section over renumbering". Insert sub-bullets and table rows in place; if a totally new sub-section is needed, append (e.g., §5.5.1) rather than renumbering §5.6+.
  </action>
  <verify>
    1. `grep -c "BadFinishReasons" README.md` returns >= 1.
    2. `grep -c "EntropyThreshold" README.md` returns >= 1.
    3. `grep -c "bad_reason" README.md` returns >= 1.
    4. `grep -c "quality_check_hits" README.md` returns >= 4.
    5. `grep -c "case-insensitive" README.md` returns >= 1 (per Phase 15 keyword behavior).
    6. `grep -c "koreanRatio\|effective length\|Korean" README.md` returns >= 1 (Korean-aware length doc present).
    7. `grep -c "charEntropy\|Shannon" README.md` returns >= 1 (entropy doc present).
    8. Manual visual check: open README.md and verify §5.5, §7, §9.3, /stats sections render cleanly with the new rows; no broken markdown tables.
  </verify>
  <done>
README.md is updated in 4 sections (§5.5 trigger list, §7 config table, §9.3 trace schema, /stats endpoint section) with all Phase 15 changes documented. Operator workflow examples (jq + curl) included. Table of contents stable.

**Commit:** `docs(15-03): update README §5.5/§7/§9.3 + /stats with Phase 15 quality signal enrichment`
  </done>
</task>

<task type="auto">
  <name>Task 3: Add CHANGELOG [Unreleased] ### Changed entry + update planning docs</name>
  <files>
    CHANGELOG.md
    .planning/docs/quality-check-improvement-options.md
  </files>
  <action>
**Step 1 — `CHANGELOG.md` `[Unreleased] ### Changed` entry per CONTEXT.md migration tone.**

If `CHANGELOG.md` does not exist at repo root, create it with Keep-a-Changelog skeleton:
```markdown
# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed

- Quality fallback now triggers on additional signals (Phase 15): `finish_reason='length'` or `'content_filter'`, case-insensitive keyword match (was case-sensitive in Phase 14), low Shannon entropy responses (default threshold 2.5), and Korean-aware length threshold (Hangul-syllable ratio inflates effective length). Existing `Routing.QualityFallback` config keys (`Enabled`, `MinResponseLength`, `BadKeywords`) are unchanged. New keys `BadFinishReasons` and `EntropyThreshold` have defaults that activate the new checks silently — operators who relied on Phase 14's narrower trigger surface may see slightly more 35B→122B retries. Set `BadFinishReasons: []` and `EntropyThreshold: 0.01` (or any small positive) to restore Phase 14 trigger behavior.

### Added

- TraceLog field `bad_reason` (string | null) records which quality check fired (`length=12`, `keyword=TODO`, `finish_reason=length`, `entropy=1.85`). Format `"tag=value"` with `=` separator; `jq -r '.bad_reason | split("=")[0]'` for tag-only aggregation.
- `/stats` endpoint exposes 4 new fields: `quality_check_hits_finish_reason`, `quality_check_hits_length`, `quality_check_hits_entropy`, `quality_check_hits_keyword` (all int64, process-lifetime counters).
```

If `CHANGELOG.md` exists, locate the `[Unreleased]` section (or create it at the top under the existing structure) and add the `### Changed` and `### Added` entries above. Do NOT modify any released-version sections.

**Tone:** matches CONTEXT.md migration policy — silent enable acknowledged ("activate the new checks silently"), restore-Phase-14 instructions provided, no breaking-change marker (operator behavior is not forced; defaults are stricter only).

**Step 2 — Update `.planning/docs/quality-check-improvement-options.md`.**

This document was the design source for Phases 15-17. Locate the Tier 1+2 section header (likely "Tier 1: Free Wins" or similar). Add a status note at the top:

```markdown
> **Status (2026-05-10):** Tier 1 (sub-tiers 1-A, 1-B, 1-D) and Tier 2 (sub-tier 2-A) implemented in Phase 15 — see `.planning/phases/15-quality-signal-enrichment/`. Tier 1-C (refusal-pattern default expansion) was DEFERRED — operator opt-in via `BadKeywords` is the chosen path (see `.planning/phases/15-quality-signal-enrichment/15-CONTEXT.md` § "Refusal pattern default 정책"). Tier 2-B (prompt-relative length) and Tier 2-C (logprob threshold) remain candidates for Phase 16+.
```

This keeps the planning corpus internally consistent for the Phase 16/17 planners and verifiers.

**Step 3 — Final test sweep.**

Run the full test suite one more time to confirm the green baseline:
```bash
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
```

Capture and record exact pass/fail/ignore counts in the SUMMARY (e.g., "97 passed + 16 ignored + 0 failed").
  </action>
  <verify>
    1. `grep -c "Phase 15\|finish_reason\|EntropyThreshold" CHANGELOG.md` returns >= 3.
    2. `grep -c "bad_reason" CHANGELOG.md` returns >= 1.
    3. `grep -c "quality_check_hits" CHANGELOG.md` returns >= 1.
    4. `grep -c "Status.*2026-05-10\|Phase 15" .planning/docs/quality-check-improvement-options.md` returns >= 1.
    5. `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` returns: passed >= 88+N (record exact N in SUMMARY), ignored = 16, failed = 0.
    6. ARCH-01 final grep:
       ```bash
       grep -E "(Microsoft\\.ML|Serilog|HttpClient|System\\.Net\\.Http)" src/SmartRouter.Cli/Adapters/QualityCheck.fs
       ```
       returns no matches.
    7. CLAUDE.md sync rule satisfied — verify §5.5, §7, §9.3, /stats section all reference Phase 15 changes.
  </verify>
  <done>
CHANGELOG.md has [Unreleased] ### Changed and ### Added entries documenting Phase 15 silent-enable behavior and new fields. Planning doc reference (.planning/docs/quality-check-improvement-options.md) marks Tier 1+2 implemented status. Final test baseline confirmed green.

**Commit:** `docs(15-03): add CHANGELOG [Unreleased] entry + Phase 15 status note in quality-check-improvement-options.md`
  </done>
</task>

</tasks>

<verification>
**Plan-level + Phase-level verification (run after all 3 tasks):**

1. `dotnet build` (root): exit 0, no warnings.
2. `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj`: passed >= 88+N (where N = new QSE testCases ~8-9), ignored = 16, failed = 0. CRITICAL: QF-01..QF-08 (Phase 14 baseline) all pass unchanged.
3. ARCH-01:
   ```bash
   grep -E "(Microsoft\\.ML|Serilog|HttpClient|System\\.Net\\.Http)" src/SmartRouter.Cli/Adapters/QualityCheck.fs
   ```
   no matches.
4. ARCH-02:
   ```bash
   bash scripts/check-no-async.sh
   ```
   exit 0.
5. CLAUDE.md sync rule areas covered:
   - Area 5 (routing pipeline) → README §5.5 ✓
   - Area 7 (config keys) → README §7 ✓
   - Area 8 (operational log behavior) → README /stats ✓ (counters new)
   - Area 9.1 (decision log schema) → unchanged (no DecisionLog change in Phase 15)
   - Trace schema (extension of Phase 14 §9.10) → README §9.3 (or wherever trace docs live) ✓
6. Test registration: `grep -c "QualitySignalEnrichmentTests.tests" tests/SmartRouter.Tests/RouterTests.fs` = 1 (PITFALL-26).
7. Phase 14 baseline preserved: `dotnet test --filter "FullyQualifiedName~QualityFallback"` returns 8 pass.
8. /stats wire schema test: integration test in QualitySignalEnrichmentTests asserts 4 new fields present + correct counter incremented after each Bad verdict.
9. Trace schema test: integration test asserts `bad_reason` field present with correct "tag=value" format.

**Phase 15 acceptance gate (orchestrator quality_gate):**
- [x] PLAN.md files exist for 15-01, 15-02, 15-03
- [x] All have valid frontmatter (wave, depends_on, files_modified, autonomous, must_haves)
- [x] Tasks specific (file path + line range or precise change)
- [x] Dependencies: 15-02 depends_on [15-01]; 15-03 depends_on [15-01, 15-02]
- [x] Wave structure: 1 → 2 → 3 (sequential, single-thread)
- [x] must_haves derived goal-backward from QSE-01..06 + CONTEXT.md decisions
- [x] README sync tasks explicit (specific section + change)
- [x] CHANGELOG entry task included (15-03 Task 3)
- [x] Backward-compat verification: Phase 14 QF-01..QF-08 baseline asserted in 15-02 + 15-03 verify steps
- [x] /stats wire test for 4 new counters (15-03 Task 1 integration test)
- [x] Researcher's Q1 (rename) resolved with rationale: keep `isBadResponse` as wrapper + add `analyzeResponse` as primary
- [x] Researcher's Q2 (stats interface) resolved with rationale: separate `IQualityCheckStats` (cleaner separation; struct tuple alloc-free)
</verification>

<success_criteria>
- [ ] QualitySignalEnrichmentTests.fs covers 5 detection dimensions + cheap-first cascade + Phase 14 wrapper backward-compat + 1 fake-Kestrel integration test for trace bad_reason + /stats counters
- [ ] Test file registered in fsproj (before RouterTests.fs) and rootTests list (PITFALL-26)
- [ ] Final dotnet test result: >= 88 + N passed, 16 ignored, 0 failed
- [ ] README.md updated in §5.5 (5-dimension trigger list with cheap-first ordering), §7 (BadFinishReasons + EntropyThreshold rows + case-insensitive note + refusal-pattern operator-opt-in note), §9.3 (bad_reason field doc + jq workflow), /stats section (4 quality_check_hits_* fields + curl example)
- [ ] CHANGELOG.md [Unreleased] ### Changed + ### Added entries per CONTEXT.md migration tone (silent enable + restore-Phase-14 instruction)
- [ ] .planning/docs/quality-check-improvement-options.md status note for Tier 1+2 implemented
- [ ] ARCH-01 + ARCH-02 invariants preserved
- [ ] CLAUDE.md sync rule satisfied for affected areas (5, 7, 8, trace schema)
- [ ] Three commits: `test(15-03): ...`, `docs(15-03): update README ...`, `docs(15-03): add CHANGELOG ...`
</success_criteria>

<output>
After completion, create `.planning/phases/15-quality-signal-enrichment/15-03-SUMMARY.md` documenting:
- Three commit hashes
- Final test count (e.g., "97 passed + 16 ignored + 0 failed; net +9 from Phase 14 baseline")
- Test breakdown: how many unit tests vs integration tests for QualitySignalEnrichment
- README sections updated (with section numbers — verify §9.3 is correct or substitute the actual trace schema section number found in the README)
- CHANGELOG entry verbatim (so verifier can confirm tone)
- Open items for Phase 16 planner: `bad_reason` semantics already defined; `IQualityCheckStats` interface available for Phase 16 borderline classifier to extend if needed; entropy band edge thresholds (2.5..3.5) referenced from CONTEXT.md but not implemented (Phase 16 owns the borderline-band thresholds)

Then create the phase-level SUMMARY at `.planning/phases/15-quality-signal-enrichment/15-SUMMARY.md` aggregating all three plan SUMMARYs (per templates/summary.md convention) — this is what `/gsd:verify-phase 15` and the Phase 16 planner consume.
</output>
</content>
</invoke>