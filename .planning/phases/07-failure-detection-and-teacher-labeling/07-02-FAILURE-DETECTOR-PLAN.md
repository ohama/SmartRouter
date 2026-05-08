---
phase: 07-failure-detection-and-teacher-labeling
plan: 02
type: execute
wave: 2
depends_on: ["07-01"]
files_modified:
  - src/SmartRouter.Cli/Adapters/FailureDetector.fs
autonomous: true

must_haves:
  truths:
    - "FailureDetector.ExtractHardCases reads logs/decisions/*.jsonl, filters records where fallback_used=true, and returns one HardCase per matching record"
    - "Malformed JSON lines and missing directories are handled gracefully (logged as warning) — never throw"
    - "Each returned HardCase carries CorrelationId, PromptHash, PromptKoreanCharRatio, RoutingAlgorithm, Target from the source DecisionLog; PromptText = None (LOG-01 stores hash only — privacy decision; Phase 8's BackgroundService passes prompt text inline; the offline CLI path operates on synthetic seed data)"
    - "FailureDetector logs an explicit informational message when 0 hard cases found ('FailureDetector: 0 hard cases found in {dir}; fallback_used is always false until Phase 10 ships') so Phase 8's empty-loop is visible to operators"
    - "dotnet build succeeds with 0 warnings; existing 50 tests still pass + 10 ignored (no test changes in this plan; tests land in Plan 07-06)"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/FailureDetector.fs"
      provides: "FailureDetector(logsDirectory: string) implements IFailureDetector via JSONL reader + fallback_used filter; uses jsonOptions matching DecisionLogger snake_case"
      contains: "fallback_used"
  key_links:
    - from: "FailureDetector.ExtractHardCases"
      to: "DecisionLog.fallback_used"
      via: "JSON deserialization + predicate filter"
      pattern: "fallback_used"
    - from: "FailureDetector module"
      to: "SmartRouter.Cli.Adapters.DecisionLogger.DecisionLog"
      via: "open SmartRouter.Cli.Adapters.DecisionLogger"
      pattern: "open SmartRouter\\.Cli\\.Adapters\\.DecisionLogger"
---

<objective>
Replace the 07-01 stub body of `FailureDetector.ExtractHardCases` with the real implementation: read all `*.jsonl` files in `logsDirectory`, deserialize each non-blank line into a `DecisionLog`, filter `fallback_used = true`, and return one `HardCase` per match. Malformed lines logged-and-skipped (do not throw). Empty result logs an informational message explaining `fallback_used` is dormant until Phase 10.

Purpose: FAIL-01. The detector ships correct today; it just has no matching input until Phase 10 flips `fallback_used = true`. Synthetic seed data (Plan 07-05) primes the dataset for Phase 8 testing in the meantime.

Output:
- src/SmartRouter.Cli/Adapters/FailureDetector.fs (replaces 07-01 stub body; constructor signature unchanged)
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/07-failure-detection-and-teacher-labeling/07-CONTEXT.md
@.planning/phases/07-failure-detection-and-teacher-labeling/07-RESEARCH.md
@src/SmartRouter.Core/RetrainingPorts.fs
@src/SmartRouter.Cli/Adapters/DecisionLogger.fs
@src/SmartRouter.Cli/Adapters/Json.fs
</context>

<tasks>

<task type="auto">
  <name>Task 1: Implement FailureDetector.ExtractHardCases (JSONL reader + fallback_used filter)</name>
  <files>
    src/SmartRouter.Cli/Adapters/FailureDetector.fs
  </files>
  <action>
Replace the entire contents of `src/SmartRouter.Cli/Adapters/FailureDetector.fs` (preserving the module name and constructor signature established in Plan 07-01) with the real implementation:

```fsharp
module SmartRouter.Cli.Adapters.FailureDetector

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Serilog
open SmartRouter.Core.RetrainingPorts
open SmartRouter.Cli.Adapters.DecisionLogger

/// Reads logs/decisions/*.jsonl files, filters fallback_used=true records,
/// returns hard-case correlation_ids and surrounding metadata. FAIL-01.
///
/// Pure-ish — file I/O is the only side effect. Returns Task<HardCase list>
/// (not IAsyncEnumerable) — JSONL files are small (one line per request,
/// hundreds per day at v1 scale).
///
/// Phase 10 forward-link: when fallback_used flips to true, this code immediately
/// produces real hard cases — no Phase 7 changes needed. Until then, the empty
/// result is logged at Information level so Phase 8's empty-loop is visible.
type FailureDetector(logsDirectory: string) =

    // JSON options must match DecisionLogWriter's serializer:
    //   - PropertyNamingPolicy.SnakeCaseLower → matches snake_case JSONL field names
    //   - JsonFSharpConverter → handles `string option` (task_type field) round-trip
    let jsonOpts =
        let o = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
        o.Converters.Add(JsonFSharpConverter())
        o

    let toHardCase (entry: DecisionLog) : HardCase =
        { CorrelationId         = entry.correlation_id
          PromptHash            = entry.prompt_hash
          PromptKoreanCharRatio = entry.prompt_korean_char_ratio
          RoutingAlgorithm      = entry.routing_algorithm
          Target                = entry.target
          // PromptText: LOG-01 stores prompt_hash only (privacy decision). The CLI
          // --retrain offline path cannot recover prompt text from logs; Phase 8's
          // in-process BackgroundService will set this from RouterRequest.Messages,
          // and the synthetic seed script writes Some directly to hard-cases.jsonl.
          // The detector itself returns None — downstream callers handle the gap.
          PromptText            = None }

    // Parse one JSONL line. Returns None for blank lines, malformed JSON, or
    // non-fallback entries. Errors are logged at Warning (not Error) — a single
    // bad line should not derail the entire scan.
    let tryParseLine (line: string) : HardCase option =
        if String.IsNullOrWhiteSpace(line) then None
        else
            try
                let entry = JsonSerializer.Deserialize<DecisionLog>(line, jsonOpts)
                if entry.fallback_used then Some (toHardCase entry)
                else None
            with ex ->
                Log.Warning(ex, "FailureDetector: skipping malformed JSONL line in {Dir}", logsDirectory)
                None

    interface IFailureDetector with
        member _.ExtractHardCases(_ct: CancellationToken) : Task<HardCase list> =
            task {
                if not (Directory.Exists logsDirectory) then
                    Log.Information(
                        "FailureDetector: directory {Dir} does not exist; returning 0 hard cases",
                        logsDirectory)
                    return []
                else
                    let files = Directory.GetFiles(logsDirectory, "*.jsonl")
                    let hardCases =
                        files
                        |> Array.toList
                        |> List.collect (fun path ->
                            try
                                File.ReadAllLines(path)
                                |> Array.toList
                                |> List.choose tryParseLine
                            with ex ->
                                Log.Warning(ex, "FailureDetector: failed to read {Path}", path)
                                [])

                    if List.isEmpty hardCases then
                        Log.Information(
                            "FailureDetector: 0 hard cases found in {Dir} across {N} JSONL file(s); fallback_used is always false until Phase 10 ships. Run scripts/seed-hard-cases.fsx to seed synthetic data.",
                            logsDirectory, files.Length)
                    else
                        Log.Information(
                            "FailureDetector: extracted {Count} hard case(s) from {N} JSONL file(s) in {Dir}",
                            List.length hardCases, files.Length, logsDirectory)

                    return hardCases
            }
```

**Key design notes:**
- Constructor signature `(logsDirectory: string)` MUST match the Plan 07-01 stub — Plan 07-05 DI registration depends on this shape.
- `JsonOpts` mirrors DecisionLogWriter exactly — the same SnakeCaseLower naming policy and JsonFSharpConverter so JSONL roundtrips correctly.
- The CancellationToken parameter is accepted but not propagated to `File.ReadAllLines` — JSONL reads are synchronous and complete in milliseconds at v1 scale; honoring the ct via async file streaming is unjustified complexity. Wave 2 verification: `_ct` underscore prefix prevents F# unused-binding warning.
- The `try` around `File.ReadAllLines` per-file isolation: a single permission denied or transient I/O error on one file does not abort the entire scan.
- Empty-result Information message explicitly references Phase 10 + the seed script — Pitfall 1 from RESEARCH.md.

**Imports required and reasons:**
- `System` — DateTimeOffset (carried in HardCase via DecisionLog), basic types
- `System.IO` — Directory.Exists, Directory.GetFiles, File.ReadAllLines
- `System.Text.Json` — JsonSerializer.Deserialize, JsonSerializerOptions
- `System.Text.Json.Serialization` — JsonNamingPolicy, JsonFSharpConverter (the F# DU/Option converter; same import as DecisionLogWriter line 7)
- `System.Threading` / `System.Threading.Tasks` — CancellationToken, Task
- `Serilog` — Log.Information, Log.Warning
- `SmartRouter.Core.RetrainingPorts` — IFailureDetector, HardCase
- `SmartRouter.Cli.Adapters.DecisionLogger` — DecisionLog record (Cli-side type; Pure-Core invariant unchanged)

**No additional changes outside this file** — Cli.fsproj already has FailureDetector.fs registered (Plan 07-01). No DI changes (Plan 07-05). No tests (Plan 07-06).
  </action>
  <verify>
- `dotnet build SmartRouter.slnx -nologo --tl:off` succeeds with 0 warnings.
- `grep -n "fallback_used" src/SmartRouter.Cli/Adapters/FailureDetector.fs` returns at least 1 hit (the predicate filter).
- `grep -n "NotImplementedException" src/SmartRouter.Cli/Adapters/FailureDetector.fs` returns NO hits (stub body fully replaced).
- `grep -n "Phase 10" src/SmartRouter.Cli/Adapters/FailureDetector.fs` returns at least 1 hit (operator-visible empty-result message references Phase 10).
- `grep -n "JsonNamingPolicy\.SnakeCaseLower" src/SmartRouter.Cli/Adapters/FailureDetector.fs` returns 1 hit (matches DecisionLogWriter serializer).
- Existing 50 tests still pass + 10 ignored (no DI registration consumes the new behavior yet).
  </verify>
  <done>
FailureDetector reads JSONL files, filters fallback_used=true, returns HardCase list. Malformed lines and missing directories handled gracefully. Operator-visible Information log on empty result. Build is clean. No test or DI ripple.
  </done>
</task>

</tasks>

<verification>
**Plan-level verification:**

1. **Build is clean:**
   ```bash
   dotnet build SmartRouter.slnx -nologo --tl:off
   ```
   Expected: 0 errors, 0 warnings.

2. **Stub fully replaced:**
   ```bash
   grep -n "NotImplementedException" src/SmartRouter.Cli/Adapters/FailureDetector.fs
   ```
   Expected: NO output.

3. **JSONL filter logic present:**
   ```bash
   grep -nE "fallback_used|HardCase" src/SmartRouter.Cli/Adapters/FailureDetector.fs
   ```
   Expected: at least 2 hits.

4. **Existing tests still pass:**
   ```bash
   dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off
   ```
   Expected: 50 passed + 10 ignored.

5. **Pure-Core invariant unchanged:**
   ```bash
   grep -RIn "Serilog\|HttpClient\|System\.IO\|System\.Net" src/SmartRouter.Core/
   ```
   Expected: no Serilog/HttpClient/System.IO hits in src/SmartRouter.Core/ (unchanged from Plan 07-01).
</verification>

<success_criteria>
- FailureDetector real implementation ships; constructor signature unchanged from 07-01 stub
- 0 build warnings (TreatWarningsAsErrors=true)
- All 50 existing tests still pass + 10 ignored
- Operator-visible empty-result message references Phase 10 + seed script
</success_criteria>

<output>
After completion, create `.planning/phases/07-failure-detection-and-teacher-labeling/07-02-SUMMARY.md` listing the file modified, any deviations, and the test count delta (expected: 50 → 50).
</output>
