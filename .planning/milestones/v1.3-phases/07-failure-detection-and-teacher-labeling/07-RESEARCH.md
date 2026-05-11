# Phase 7: Failure Detection + Teacher Labeling — Research

**Researched:** 2026-05-08
**Domain:** Offline data pipeline — JSONL log reader, HTTP-based teacher labeler, append-only hard-case dataset writer
**Confidence:** HIGH

---

## Summary

Phase 7 ships the offline pipeline that converts production routing logs into labeled training samples for Phase 8's retraining loop. The pipeline has three components: `FailureDetector` (reads `logs/decisions/*.jsonl`, filters by failure signal), `TeacherLabeler` (calls 122B via HTTP with `teacher_prompt.md` system prompt, parses `ROUTE_35B`/`ROUTE_122B` response), and `HardCaseDatasetWriter` (appends to `datasets/hard-cases.jsonl` with file-lock safety). All three are Cli adapters. Phase 7 ships the components as injectable services; Phase 8 wires them into a `BackgroundService`.

**Critical pre-existing tension:** Phase 5's `LOG-01` schema hard-codes `fallback_used = false` (the actual fallback behavior ships in Phase 10, which is deferred). This means `FailureDetector`'s primary filter — `fallback_used = true` — never matches production logs. The phase ships code that will find zero hard cases until Phase 10 lands. This is not a bug to hide; it is a scaffolding phase. The plan must make this explicit and ship a secondary seeding mechanism to keep Phase 8 unblocked.

**Primary recommendation:** Path A (build as-spec'd) plus Path F (synthetic seeding command) as secondary. Tests use fixture files with synthetic `fallback_used = true` entries. The components work correctly; they just have no real input until Phase 10.

---

## USER-FACING DECISIONS (ask before planning)

### Decision 1: Data-availability primary path

**Context:** `fallback_used` is always `false` until Phase 10 (deferred). FailureDetector finds 0 hard cases. Phase 8 retraining loop reads empty dataset.

**Options evaluated:**

| Option | Summary | Verdict |
|--------|---------|---------|
| **A. Build as-spec'd** | FailureDetector returns empty set. Phase 8 skips training. Phase 10 unblocks it. Tests use synthetic fixtures. | Honest, minimal scope. |
| B. Pull Phase 10 forward | Reorder: ship health/fallback before Phase 7. Phase 7 then has real data. | Re-prioritizes deferred phase; goes against SOFT-PAUSE decision. |
| C. Broaden failure signal | Use `latency_ms > p99` or algorithm inconsistencies as proxy hard cases. | Weak proxies; not true quality failures. Adds noise, not signal. |
| D. Add response-content to LOG-01 | Schema amendment to capture response excerpt; heuristic ("TODO", short response). | Scope explosion: schema change ripples through Phases 5-9 of already-locked schema. Ruled out. |
| E. Operator-curated CLI | `smart-router add-hard-case <correlation_id> <label>`. Manual but real. | Good secondary; does not unblock automated loop at all. |
| **F. Synthetic seeding** | Ship `scripts/seed-hard-cases.fsx` generating typed test prompts with known labels. | Cheap, unblocks Phase 8, clearly labeled "synthetic". Best secondary. |

**Recommendation: A (primary) + F (secondary)**

Rationale: Path A is the only honest approach given the locked LOG-01 schema. The FailureDetector is tested against fixture files; it works when `fallback_used` goes true. Path F (synthetic seeding script) generates a small known-good dataset so Phase 8's retraining loop can be exercised end-to-end before Phase 10 ships. The script is clearly marked synthetic; the `HardCaseEntry.source` field distinguishes `"synthetic"` from `"production"`. Phase 10 flips `fallback_used` to true and Phase 7's pipeline starts producing real data automatically — no Phase 7 code changes needed.

**Ask the user:** "Phase 7's FailureDetector finds zero hard cases until Phase 10 ships (fallback_used is always false). I recommend building as-spec'd (Path A) and adding a synthetic seeding script (Path F) so Phase 8's retraining loop can be exercised before Phase 10. Agree? Or do you want to pull Phase 10 forward instead?"

---

### Decision 2: Teacher prompt configurability

**Context:** `teacher_prompt.md` content is stable (7 criteria, two-token output). Should it be:
- (a) Embedded as a resource file (`SmartRouter.Cli/Resources/teacher-prompt.md`, `EmbeddedResource` in fsproj) — operator cannot change without rebuild.
- (b) Configurable path in `appsettings.json` (`TeacherLabeler:PromptPath`) — operator can swap in a different prompt without rebuild.

**Recommendation:** Configurable path (option b), defaulting to `prompts/teacher-prompt.md` in the working directory (gitignored, operator-managed). Rationale: the teacher prompt is a tunable artifact that operators may want to adjust as routing semantics evolve. Hardcoding it as an embedded resource makes experimentation harder. The `prompts/` directory is already a natural home (mirrors `smart-router-distillation/idea/prompts/`). The default path falls back gracefully if missing: log Fatal + throw at startup. If the operator wants zero-configuration, they commit the prompt file alongside the binary.

**Ask the user:** "Should the teacher prompt be a configurable file path (default `prompts/teacher-prompt.md`) or hardcoded as an embedded resource? Configurable is easier to tune; embedded is simpler to deploy."

---

### Decision 3: Trigger mechanism

**Context:** ROADMAP says "pure offline pipeline — no behavior change at request time." Phase 8 will run these components as a `PeriodicTimer` `BackgroundService`. Should Phase 7:
- (a) Ship components only (no trigger wiring) — Phase 8 composes them into a BackgroundService.
- (b) Ship a CLI subcommand (`dotnet run -- retrain --extract-hard-cases`) — operator-driven; useful for manual testing before Phase 8.
- (c) Ship both — components + CLI trigger.

**Recommendation:** Option (c) — ship both. The components are the main deliverable (Phase 8 composes them). The CLI trigger costs one `args` check in `Program.fs` and is invaluable for local testing of the pipeline without Phase 8. The CLI path also serves as the seeding mechanism (option F above).

**Ask the user:** "Should Phase 7 also ship a CLI trigger (`dotnet run -- --retrain`) for manually running the pipeline, or just the injectable components (Phase 8 wires them into a BackgroundService)? Recommend: both."

---

## Standard Stack

### Core (all already in SmartRouter.Cli.fsproj — no new NuGet required)

| Library | Already Present | Purpose in Phase 7 |
|---------|----------------|---------------------|
| `System.Text.Json` + `FSharp.SystemTextJson 1.4.36` | YES | Deserialize DecisionLog from JSONL; serialize HardCaseEntry to JSONL |
| `Microsoft.Extensions.Http` (IHttpClientFactory) | YES | Named HttpClient for TeacherLabeler → 122B |
| `Microsoft.Extensions.Http.Resilience 10.5.0` | YES | 30s timeout + 3x retry pipeline on teacher HttpClient |
| `Serilog 4.3.1` | YES | Structured logging: 0 hard cases, cost cap hit, malformed responses |
| `FsToolkit.ErrorHandling 5.2.0` | YES | `taskResult {}` for retry/cost-cap pipeline |
| `System.Threading.Channels` | BCL | Channel<HardCaseEntry> single-writer pattern (mirrors DecisionLogWriter) |

### No new NuGet packages required for Phase 7

All required infrastructure is already present. The teacher HTTP client reuses the existing `IHttpClientFactory` + `Microsoft.Extensions.Http.Resilience` pattern from `QwenUpstreamClient`. The `PredictionEnginePool` watchForChanges hot-reload from Phase 6 will pick up new `router.zip` files produced by Phase 8 with no Phase 7 involvement.

---

## Architecture Patterns

### Recommended component placement

```
src/SmartRouter.Cli/
├── Adapters/
│   ├── FailureDetector.fs          ← NEW Phase 7: reads logs/decisions/*.jsonl
│   ├── TeacherLabeler.fs           ← NEW Phase 7: calls 122B with teacher prompt
│   └── HardCaseDatasetWriter.fs    ← NEW Phase 7: appends datasets/hard-cases.jsonl
└── ...

prompts/
└── teacher-prompt.md               ← NEW Phase 7: copied from distillation repo; gitignored

datasets/
└── hard-cases.jsonl                ← NEW Phase 7 output; gitignored

scripts/
└── seed-hard-cases.fsx             ← NEW Phase 7: synthetic seeding (option F)
```

**.gitignore additions:**
```
datasets/
prompts/
```

**fsproj `<Compile>` order** (F# requires top-to-bottom dependency order):
```xml
<!-- Adapters (Infrastructure) -->
<Compile Include="Adapters/Json.fs" />
<Compile Include="Adapters/Logging.fs" />
<Compile Include="Adapters/DecisionLogger.fs" />
<Compile Include="Adapters/DecisionLogWriter.fs" />
<Compile Include="Adapters/CorrelationMiddleware.fs" />
<Compile Include="Adapters/RoutingAlgorithm.fs" />
<Compile Include="Adapters/MlNetClassifier.fs" />
<Compile Include="Adapters/BgeM3Embedder.fs" />
<Compile Include="Adapters/ModelBootstrapper.fs" />
<Compile Include="Adapters/QwenUpstreamClient.fs" />
<Compile Include="Adapters/QueueDispatcher.fs" />
<!-- Phase 7 — must follow DecisionLogger (uses DecisionLog record) -->
<Compile Include="Adapters/FailureDetector.fs" />
<Compile Include="Adapters/TeacherLabeler.fs" />
<Compile Include="Adapters/HardCaseDatasetWriter.fs" />
<!-- Endpoints -->
<Compile Include="Endpoints/ChatCompletions.fs" />
<Compile Include="Endpoints/Stats.fs" />
<Compile Include="CompositionRoot.fs" />
<Compile Include="Program.fs" />
```

**Why all three are Cli adapters (not Core ports):** FailureDetector does file I/O (reads JSONL), TeacherLabeler does HTTP I/O (calls 122B), HardCaseDatasetWriter does file I/O (writes JSONL). The Pure Core invariant prohibits I/O in Core. No Core types need to change for Phase 7. The `DecisionLog` record is already a Cli type in `Adapters/DecisionLogger.fs` — FailureDetector can reference it directly without a port.

**No separate `SmartRouter.Retraining` project** for Phase 7. The distillation doc §4.3 mentions a separate namespace as a future option, but for a single-developer local tool the added project overhead (new fsproj, new test project) is not justified. If Phase 8+ outgrows a single Cli project, extract then.

---

### Pattern 1: FailureDetector

**What:** Pure function reading `logs/decisions/*.jsonl` files, filtering by failure signal, returning a list of `HardCase` records. FAIL-01 specifies `fallback_used = true` as the sole current filter.

**Key design choices:**
- Returns `HardCase list` (not `IAsyncEnumerable`) — the JSONL files are small enough that sequential read is fine. Avoids streaming complexity.
- Reads ALL files in `logs/decisions/` that are newer than the last-processed timestamp. Uses a `last-processed.txt` watermark file so re-runs are idempotent and don't re-process old logs.
- Deduplication by `(prompt_hash, correlation_id)` — same entry must not be appended twice to `hard-cases.jsonl`.

```fsharp
// Adapters/FailureDetector.fs (F# module, not a class)
module SmartRouter.Cli.Adapters.FailureDetector

open System.IO
open System.Text.Json
open SmartRouter.Cli.Adapters.DecisionLogger

[<CLIMutable>]
type HardCase =
    { correlation_id : string
      prompt_hash    : string
      // Prompt content is NOT in LOG-01 (privacy). TeacherLabeler needs to re-read
      // from the log entry. Store the full DecisionLog so TeacherLabeler can inspect
      // all fields; do NOT store raw prompt text (it's not in the log anyway).
      log_entry      : DecisionLog }

/// Read all *.jsonl files in logsDir, filter fallback_used=true, return hard cases.
/// Phase 10 expands the signal; for now this is the sole filter per FAIL-01.
let extractHardCases (logsDir: string) : HardCase list =
    if not (Directory.Exists logsDir) then []
    else
        Directory.GetFiles(logsDir, "*.jsonl")
        |> Array.toList
        |> List.collect (fun path ->
            File.ReadAllLines(path)
            |> Array.toList
            |> List.choose (fun line ->
                if System.String.IsNullOrWhiteSpace(line) then None
                else
                    try
                        let entry = JsonSerializer.Deserialize<DecisionLog>(line, jsonOpts)
                        if entry.fallback_used then
                            Some { correlation_id = entry.correlation_id
                                   prompt_hash    = entry.prompt_hash
                                   log_entry      = entry }
                        else None
                    with _ -> None))
```

**IMPORTANT — Phase 10 forward-link:** When Phase 10 ships and `fallback_used` begins flipping to `true`, this function immediately starts producing real hard cases. No Phase 7 code changes needed. The function is correct today; it just has no matching input until Phase 10.

**What FailureDetector does NOT have access to:** The actual prompt text. LOG-01 schema only logs `prompt_hash` (SHA-256) — not the raw prompt content (privacy decision locked in Phase 5). This means `TeacherLabeler` cannot label from the log alone. **This is a design gap that must be surfaced in the plan.** Resolution: TeacherLabeler must either (a) require callers to pass the original prompt text alongside the hard case, or (b) operate on a different data source. For Phase 8's BackgroundService the caller will be the request handler which CAN pass the prompt. For Phase 7's offline-only mode, the test fixtures must include prompt text. The `HardCase` record should carry a `prompt_text: string option` field that is `None` when extracted from logs and `Some` when provided by the BackgroundService.

---

### Pattern 2: TeacherLabeler

**What:** HTTP client that calls 122B (at `localhost:8001`) with the teacher prompt as system message and the hard case's prompt as user message. Parses `ROUTE_35B` or `ROUTE_122B` from response. 30s timeout + 3x retry + daily cost cap.

**HTTP client choice:** Use `IHttpClientFactory` + named client `"teacher"` with a custom `AddResilienceHandler` (already available via `Microsoft.Extensions.Http.Resilience`). Do NOT reuse the existing `upstream122b` named client — that client has a 300s timeout set for inference. TeacherLabeler needs a tighter 30s timeout + retry policy configured independently.

**Teacher prompt file:** Load from configurable path (`TeacherLabeler:PromptPath`, default `prompts/teacher-prompt.md`). Cache in memory after first read (file is small, read once at construction). The content is the system message in the chat request.

**Request body shape:**
```json
{
  "model": "<resolved-from-/v1/models>",
  "messages": [
    { "role": "system", "content": "<teacher_prompt.md content>" },
    { "role": "user",   "content": "<prompt_text from hard case>" }
  ],
  "max_tokens": 10,
  "temperature": 0.0,
  "stream": false
}
```

`max_tokens: 10` is critical — the teacher prompt instructs the model to respond with exactly `ROUTE_35B` or `ROUTE_122B` (5-7 tokens). Setting `max_tokens: 10` prevents chatty prose and keeps cost predictable.

**Response parsing:**
```fsharp
type TeacherLabel =
    | Route35B
    | Route122B
    | Unparseable of rawResponse: string

let parseLabel (responseBody: string) : TeacherLabel =
    // Extract choices[0].message.content from OpenAI response shape
    use doc = JsonDocument.Parse(responseBody)
    let content =
        doc.RootElement
           .GetProperty("choices").[0]
           .GetProperty("message")
           .GetProperty("content")
           .GetString()
           .Trim()
    if content.Contains("ROUTE_35B")  then Route35B
    elif content.Contains("ROUTE_122B") then Route122B
    else Unparseable content
```

Retry policy: retry once on `Unparseable` (model may have hallucinated once). Skip with log warning on second `Unparseable`. Never retry on `Route35B`/`Route122B` — already successful.

**Concurrency:** Serial calls (one-at-a-time teacher loop). Rationale: 122B has a `SemaphoreSlim(1)` gate from Phase 3. Parallel teacher calls would queue up and hold the slot. Serial is safer and the offline pipeline is not latency-sensitive.

---

### Pattern 3: Cost Cap (FAIL-03)

**Mechanism:** Persistent counter file at `datasets/teacher-cap-YYYY-MM-DD.json` (UTC date). Shape: `{ "date": "2026-05-08", "count": 42 }`. On each teacher call:
1. Read counter file. If date != today UTC → reset to 0.
2. If count >= configured max → log warning + skip this entry.
3. On successful teacher call → increment + write counter file.

**Why persistent (not in-memory):** The operator may restart the router. In-memory counter loses state. If the daily cap is 1000 calls and the operator restarts at call 999, an in-memory counter would allow 1000 more calls on restart. Persistent file survives restarts and resets correctly at UTC midnight.

**Configuration in `appsettings.json`:**
```json
"TeacherLabeler": {
  "Endpoint":    "http://127.0.0.1:8001",
  "PromptPath":  "prompts/teacher-prompt.md",
  "DailyCapCalls": 1000,
  "TimeoutSeconds": 30,
  "MaxRetries": 3
}
```

**UTC consistency:** Phase 5 log rotation uses UTC (`DateTime.UtcNow.Date`). The cost cap uses the same UTC date for the counter file name. If the operator is in a non-UTC timezone, the reset happens at midnight UTC — consistent with log file rotation boundary.

**appsettings binding type (Cli-only):**
```fsharp
[<CLIMutable>]
type TeacherLabelerOptions =
    { Endpoint      : string   // default "http://127.0.0.1:8001"
      PromptPath    : string   // default "prompts/teacher-prompt.md"
      DailyCapCalls : int      // default 1000
      TimeoutSeconds : int     // default 30
      MaxRetries    : int      // default 3 }
```

---

### Pattern 4: HardCaseDatasetWriter (FAIL-04)

**Mirror of DecisionLogWriter** — same Channel + single-writer BackgroundService pattern, same `FileShare.None` open, same per-line flush, same DropWrite overflow policy.

```
HardCaseEntry shape (hard-cases.jsonl schema):
{
  "prompt_hash"              : "sha256hex",
  "correlation_id"           : "from log OR 'synthetic-{guid}'",
  "label"                    : 0 | 1,         // 0=35B, 1=122B
  "source"                   : "production" | "teacher-labeled" | "synthetic",
  "labeled_at"               : "ISO8601 UTC",
  "teacher_response_excerpt" : "ROUTE_35B" | "ROUTE_122B" | null,
  "schema_version"           : 1
}
```

**`label` field:** 0 = Route35B, 1 = Route122B. Matches Phase 8's ML.NET `RouteInput.Label: bool` (false = 35B, true = 122B). The `label` int is the JSON wire format; Phase 8 maps 0→false, 1→true.

**Deduplication:** Before appending, check if `(prompt_hash, correlation_id)` already exists in `hard-cases.jsonl`. For a small dataset (hundreds of entries) a full linear scan on each write is acceptable. Phase 8 can upgrade to a bloom filter if dataset grows. The check is done by the single-writer consumer (no race condition since single-writer pattern guarantees sequential writes).

**Why NOT a separate `BackgroundService` in Phase 7:** Phase 7 ships `HardCaseDatasetWriter` as a Channel + a method `AppendAsync(entry)`. Phase 8's `RetrainingService` (BackgroundService) calls `AppendAsync` in its loop. Phase 7 can register `HardCaseDatasetWriter` as a `BackgroundService` too (like `DecisionLogWriter`) so the Channel consumer runs. This is the simplest path.

**File lock:** Use `FileStream` with `FileAccess.Write + FileShare.None` for the duration of each write. If a concurrent process tries to open the file it will get `IOException` — acceptable since concurrent processes are not expected in single-user setup. The single-writer Channel pattern means within the process, only one thread ever holds the handle.

---

### Pattern 5: Resilience Handler for TeacherLabeler

**Use `Microsoft.Extensions.Http.Resilience`** (already pinned at 10.5.0). Configure a named client `"teacher"` with a custom resilience handler:

```fsharp
services.AddHttpClient("teacher", fun c ->
    let opts = config.GetSection("TeacherLabeler").Get<TeacherLabelerOptions>()
    c.BaseAddress <- Uri(opts.Endpoint)
    c.Timeout     <- TimeSpan.FromSeconds(float opts.TimeoutSeconds))
    .AddResilienceHandler("teacher-retry", fun builder ->
        builder.AddRetry(RetryStrategyOptions(
            MaxRetryAttempts = 3,
            Delay            = TimeSpan.FromSeconds(1.0),
            BackoffType      = DelayBackoffType.Constant,
            ShouldHandle     = fun args ->
                ValueTask.FromResult(
                    match args.Outcome.Exception with
                    | :? HttpRequestException   -> true
                    | :? TaskCanceledException  -> true   // timeout
                    | null ->
                        let status = int args.Outcome.Result.StatusCode
                        status >= 500   // 5xx only; do NOT retry 4xx
                    | _ -> false)))
    |> ignore)
```

**Important:** Do NOT retry on 4xx. A 400 from the teacher endpoint is a malformed request (logic bug) — retrying won't help. Retry only on `HttpRequestException`, `TaskCanceledException` (timeout), and 5xx.

**Circuit-breaker-lite:** If the teacher endpoint fails K times in a row (e.g., 5), skip teacher labels for the next N minutes (e.g., 10). Prevents time amplification (K=500 entries × 3 retries × 30s = 45 minutes blocked). Implementation: a mutable `consecutiveFailures` counter and a `circuitOpenUntil: DateTimeOffset` field on `TeacherLabeler`. Reset on success.

---

### Pattern 6: Phase 8 forward-link (interface shapes Phase 8 will compose)

Phase 8's `RetrainingService` will call:
1. `failureDetector.ExtractHardCases(logsDir)` → `HardCase list`
2. For each hard case: `teacherLabeler.LabelAsync(hardCase, ct)` → `TeacherLabel`
3. `datasetWriter.AppendAsync(entry)` → `unit Task`

**Interface shapes (F# function-typed or explicit interfaces — both work):**

Option A — F# functions (no interface DU):
```fsharp
// In CompositionRoot, register as lambdas:
type ExtractHardCasesFn = string -> HardCase list
type LabelAsync = HardCase -> CancellationToken -> Task<TeacherLabel>
type AppendHardCaseFn = HardCaseEntry -> Task<unit>
```

Option B — Explicit interfaces (better for testing with mocks):
```fsharp
type IFailureDetector =
    abstract member ExtractHardCases : logsDir:string -> HardCase list

type ITeacherLabeler =
    abstract member LabelAsync : hardCase:HardCase * ct:CancellationToken -> Task<TeacherLabel>

type IHardCaseDatasetWriter =
    abstract member AppendAsync : entry:HardCaseEntry -> Task<unit>
```

**Recommendation:** Explicit interfaces (Option B). Phase 8 can inject fakes in tests (analogous to `IUpstreamClient` fake in `StreamingTests.fs`). The interfaces are simple enough that the overhead is negligible.

**Registration in CompositionRoot:**
```fsharp
// Phase 7 registrations (add to configureServices):
services.Configure<TeacherLabelerOptions>(config.GetSection("TeacherLabeler")) |> ignore

services.AddSingleton<IFailureDetector>(fun sp ->
    let opts = sp.GetRequiredService<IOptions<DecisionLogOptions>>().Value
    FailureDetector(opts.Directory) :> IFailureDetector)
|> ignore

services.AddSingleton<TeacherLabeler>(...) |> ignore
services.AddSingleton<ITeacherLabeler>(fun sp ->
    sp.GetRequiredService<TeacherLabeler>() :> ITeacherLabeler) |> ignore

services.AddSingleton<HardCaseDatasetWriter>(...) |> ignore
services.AddSingleton<IHardCaseDatasetWriter>(fun sp ->
    sp.GetRequiredService<HardCaseDatasetWriter>() :> IHardCaseDatasetWriter) |> ignore
services.AddHostedService<HardCaseDatasetWriter>(fun sp ->
    sp.GetRequiredService<HardCaseDatasetWriter>()) |> ignore
```

---

## Teacher Prompt Content

The file at `/Users/ohama/projs/smart-router-distillation/idea/prompts/teacher_prompt.md` (verified by read):

```
You are an expert in LLM routing.

Your job is to decide whether a prompt requires a large model.

Criteria for ROUTE_122B:
- multi-step reasoning
- debugging
- architecture design
- ambiguous problem
- requires deep explanation

Otherwise → ROUTE_35B

Respond ONLY:

ROUTE_35B
or
ROUTE_122B

Prompt:
{{PROMPT}}
```

**Key observations for Phase 7:**
1. The response format is binary and deterministic: exactly two possible output tokens (`ROUTE_35B` or `ROUTE_122B`). Parser is simple.
2. `{{PROMPT}}` placeholder — the Phase 7 implementation must replace this with the actual prompt text before sending. Two approaches:
   - (a) Simple string replace: `systemPrompt.Replace("{{PROMPT}}", promptText)` and pass as single user message. **Recommended** — simpler.
   - (b) Pass system prompt as `role: system` and prompt as `role: user` — the teacher prompt already has "Prompt:" at the end, which makes (b) slightly redundant. Use (a): replace `{{PROMPT}}` inline and send as a single user message, OR send system prompt without the "Prompt:\n{{PROMPT}}" footer and the prompt as the user message. Either works — consistency with the template matters more than cleverness.
3. No `reason` field in the current prompt. The distillation research notes a "강화 버전 (이유까지 분석)" variant with reasoning — NOT in current template. Phase 7 uses the current template as-is. The `teacher_response_excerpt` field in `HardCaseEntry` captures the raw `ROUTE_35B`/`ROUTE_122B` string.
4. `max_tokens: 10` is safe. Even if the model adds a trailing period or newline, `contains("ROUTE_35B")` parsing handles it.

**Embed strategy:** Copy `teacher_prompt.md` to `prompts/teacher-prompt.md` in the working directory. Add `prompts/` to `.gitignore`. Load at TeacherLabeler construction. Fail-fast with `FileNotFoundException` if missing (log Fatal + throw, same pattern as `ensureEmbeddingFilesPresent`).

---

## Hard-Case Dataset Schema

```fsharp
/// One entry in datasets/hard-cases.jsonl
/// schema_version = 1; Phase 8 reader branches on this field.
[<CLIMutable>]
type HardCaseEntry =
    { schema_version           : int             // 1
      prompt_hash              : string          // SHA-256 hex (from DecisionLog)
      correlation_id           : string          // from DecisionLog, OR "synthetic-{guid}"
      label                    : int             // 0 = Route35B, 1 = Route122B
      source                   : string          // "teacher-labeled" | "synthetic"
      labeled_at               : DateTimeOffset  // UTC
      teacher_response_excerpt : string option   // "ROUTE_35B" | "ROUTE_122B" | null
      prompt_korean_char_ratio : float           // from DecisionLog (Phase 9 cohort use)
      routing_algorithm        : string          // from DecisionLog (what was decided)
      target                   : string }        // from DecisionLog (what model was called)
```

**What Phase 8 needs:** `prompt_hash` (dedup key), `label` (training target), `source` (to filter synthetic vs production). Phase 9 canary comparison may use `prompt_korean_char_ratio`. Keep all fields.

**Note on `prompt_text` absence:** The raw prompt text is NOT in `hard-cases.jsonl` — it was never in LOG-01 (privacy decision). Phase 8's trainer uses `prompt_hash` to look up embeddings from a separate cache (TBD in Phase 8). For Phase 7, the teacher labeling step needs the prompt text, which must come from the BackgroundService caller's in-memory request context. The offline pipeline (FailureDetector reading logs) cannot recover prompt text from hashes — this is a known limitation. The synthetic seeding script provides prompt text directly.

---

## Test Seam

Three separate test files to match the three components (mirroring Phase 6's split into MLEmbeddingTests, MLClassifierTests, MLRoutingTests):

### `FailureDetectorTests.fs`

- `"extractHardCases returns empty list for empty logs directory"` — no directory
- `"extractHardCases returns empty list when all log entries have fallback_used=false"` — fixture JSONL with 10 entries, all `fallback_used: false`
- `"extractHardCases returns matching entries when fallback_used=true"` — fixture JSONL with 3 entries where 1 has `fallback_used: true`; assert exactly 1 HardCase returned
- `"extractHardCases handles malformed JSON lines gracefully"` — fixture file with 1 valid line + 1 malformed line; assert 0 errors, 1 valid result
- `"extractHardCases reads multiple daily log files"` — two fixture files (`2026-05-06.jsonl`, `2026-05-07.jsonl`); assert all matching entries across both

All tests use fixture files in a temp directory, not the production `logs/decisions/` path. Tests are pure (no HTTP, no file lock).

### `TeacherLabelerTests.fs`

Mirror the `StreamingTests.fs` / `LoggingTests.fs` pattern: spin up a fake Kestrel server returning canned responses.

- `"labelAsync returns Route35B when teacher responds ROUTE_35B"` — fake upstream returns `{"choices":[{"message":{"content":"ROUTE_35B"}}]}`
- `"labelAsync returns Route122B when teacher responds ROUTE_122B"`
- `"labelAsync retries on 503 and succeeds on third attempt"` — fake upstream fails twice then succeeds; assert result is correct and 3 HTTP calls were made
- `"labelAsync returns Unparseable on malformed response"` — fake returns chatty prose; assert `Unparseable` not crash
- `"labelAsync respects 30s timeout"` — fake delays indefinitely; assert `TaskCanceledException` within 31s (test has 35s timeout)
- `"daily cost cap: calls beyond cap are skipped"` — create cap file with count = maxCalls; assert labelAsync returns immediately without HTTP call + logs warning
- `"daily cost cap resets when date changes"` — create cap file with yesterday's date and count = maxCalls; assert labelAsync proceeds

All tests use a temp directory for the cost cap file. Tests are wrapped in `testSequenced` (cap file has shared state even with temp dirs; sequential is safer).

### `HardCaseDatasetTests.fs`

- `"appendAsync writes JSONL entry with all fields"` — one entry; parse and assert all fields present + correct types
- `"appendAsync is idempotent — same correlation_id not appended twice"` — append same entry twice; assert file has 1 line
- `"50 concurrent appendAsync calls produce 50 valid non-interleaved JSONL lines"` — parallel append test mirroring LoggingTests Test 2
- `"graceful shutdown drains channel"` — append 10 entries, call StopAsync, assert all 10 present

All tests use a temp `datasets/` directory.

**fsproj order for test project:**
```xml
<Compile Include="RoutingTests.fs" />
<Compile Include="StreamingTests.fs" />
<Compile Include="QueueTests.fs" />
<Compile Include="LoadTests.fs" />
<Compile Include="MLRoutingTests.fs" />
<Compile Include="MLEmbeddingTests.fs" />
<Compile Include="MLClassifierTests.fs" />
<Compile Include="LoggingTests.fs" />
<!-- Phase 7 — after LoggingTests (uses DecisionLog fixture helpers) -->
<Compile Include="FailureDetectorTests.fs" />
<Compile Include="TeacherLabelerTests.fs" />
<Compile Include="HardCaseDatasetTests.fs" />
<Compile Include="RouterTests.fs" />    <!-- rootTests last — always -->
```

**RouterTests.fs rootTests update:** Add the three new testList entries to the explicit `rootTests` list (Expecto auto-discovery pitfall — PITFALL-26 from SUMMARY.md).

---

## Common Pitfalls

### Pitfall 1: FailureDetector finds 0 hard cases and silently does nothing
**What goes wrong:** FailureDetector correctly finds 0 entries. Phase 8 retraining triggers. Dataset is empty. Trainer gets empty input. Silent no-op. Phase 8 never improves the model. Operator doesn't notice.
**How to avoid:** Log an explicit informational message: `"FailureDetector: 0 hard cases found in {logsDir}; fallback_used is always false until Phase 10 ships. Run scripts/seed-hard-cases.fsx to seed synthetic data."` Never crash or throw on empty input — it's expected behavior in current state.
**Warning sign:** Phase 8 BackgroundService runs without ever updating `router.zip`.

### Pitfall 2: Cost cap in-memory loses state on restart
**What goes wrong:** Operator restarts router at call 999/1000. In-memory counter resets to 0. Next run fires 1000 more calls. True daily count = 1999.
**How to avoid:** Persistent counter file at `datasets/teacher-cap-YYYY-MM-DD.json`. Read + validate on every call. UTC date check for reset.

### Pitfall 3: File lock starvation on crash mid-write
**What goes wrong:** `HardCaseDatasetWriter` opens `FileShare.None`, crashes (unhandled exception) before `Dispose`. File handle may linger on some OS configurations.
**How to avoid:** Use `use` binding for `FileStream` — F# `use` guarantees `Dispose` even on exception. The Channel + single-writer pattern means only one `use` binding is active at any time. `try/with` around the write loop with `Log.Error` on failure (mirrors DecisionLogWriter pattern).

### Pitfall 4: Teacher response hallucination
**What goes wrong:** 122B returns `"I would route this to ROUTE_122B because..."` — contains the keyword but also prose. Parser incorrectly extracts `Route122B`. OR: 122B returns `"ROUTE122B"` (missing underscore).
**How to avoid:** Use `contains` not exact match for parsing. Trim whitespace. Log `teacher_response_excerpt` field in `HardCaseEntry` so operator can audit. On Unparseable after retry, skip with warning (not crash).

### Pitfall 5: Teacher calls during 122B inference (capacity conflict)
**What goes wrong:** TeacherLabeler fires 5 teacher calls while a graph_indexing request is waiting in the QueueDispatcher. The teacher calls hold the `SemaphoreSlim(1)` for 5 × 30s = 2.5 minutes. Graph indexing request times out.
**How to avoid:** TeacherLabeler should NOT use the `QueueDispatcher`-gated `IUpstreamClient`. It must use its own named `HttpClient("teacher")` that bypasses the queue. The `IHttpClientFactory` pattern allows this cleanly — register `"teacher"` independently with its own endpoint config.
**Warning sign:** `graph_indexing` requests timing out during teacher labeling passes.

### Pitfall 6: Prompt text unavailability in offline pipeline
**What goes wrong:** FailureDetector extracts `HardCase` from JSONL. JSONL only has `prompt_hash` — not `prompt_text`. TeacherLabeler cannot call 122B without the prompt text.
**How to avoid:** `HardCase.log_entry` has all LOG-01 fields but not `prompt_text` (never was in LOG-01). Phase 7's offline pipeline cannot label historical logs without prompt text. This is a known limitation documented in research. Mitigation: synthetic seeding (option F) provides prompt text directly. Phase 8's BackgroundService will call TeacherLabeler with prompts from the in-process request context (not from logs). The `HardCase` type has a `prompt_text: string option` field.

### Pitfall 7: Daily cost cap timezone mismatch
**What goes wrong:** `DateTime.Now.Date` resets at local midnight. `DateTime.UtcNow.Date` resets at UTC midnight. Log files rotate at UTC midnight (Phase 5). If cost cap uses local time, logs and caps reset at different times → off-by-one day issues.
**How to avoid:** Always use `DateTime.UtcNow.Date` for the cost cap counter file name. Consistent with Phase 5 log rotation.

### Pitfall 8: Expecto auto-discovery failure (PITFALL-26 from SUMMARY.md)
**What goes wrong:** Three new test modules added to `.fsproj` `<Compile>` list but NOT added to `rootTests` in `RouterTests.fs`. Tests compile but never run. Zero test failures = false confidence.
**How to avoid:** After writing each test file, immediately add its `testList` to `rootTests` in `RouterTests.fs`. The plan's verification step must assert all test module names appear in `rootTests`.

---

## State of the Art

| Approach | Phase 7 Choice | Why |
|----------|---------------|-----|
| Teacher labeling via full re-inference | Call 122B with original prompt | Standard distillation practice (narrow-label-wide-train.md) |
| Response-quality heuristics (length < 30, "TODO") | NOT used in Phase 7 | Not in LOG-01 schema; would require schema change |
| Fallback detection | `fallback_used = true` only (FAIL-01) | Locked by LOG-01; Phase 10 expands |
| Dataset deduplication | prompt_hash + correlation_id | Standard; prevents duplicate training samples |
| Cost cap | Persistent file, UTC reset | Survives restarts; consistent with log rotation UTC |
| File lock | Channel + single-writer | Mirrors DecisionLogWriter; proven in Phase 5 |

---

## Open Questions

1. **Prompt text in offline pipeline**
   - What we know: LOG-01 does not store prompt text (privacy decision, Phase 5 locked)
   - What's unclear: How does Phase 8's BackgroundService obtain prompt text to pass to TeacherLabeler? It runs offline — the original request is gone.
   - Recommendation: Phase 7 ships TeacherLabeler with `prompt_text: string` parameter. Phase 8 plan must address this gap. For now, the synthetic seeding script is the only source of labeled data with prompt text available.

2. **Whether synthetic seeding output should be mixed into Phase 8 training**
   - The `source` field in `HardCaseEntry` distinguishes `"synthetic"` from `"teacher-labeled"`. Phase 8's DatasetMerger can filter or weight by source.
   - Recommendation: Phase 8 plan decides. Phase 7 just marks the source field accurately.

3. **Circuit-breaker threshold tuning**
   - K=5 consecutive failures → open circuit for N=10 minutes is a guess.
   - Recommendation: Make both configurable (`TeacherLabeler:CircuitBreakerThreshold`, `TeacherLabeler:CircuitBreakerCooldownMinutes`) with sensible defaults. Phase 8 tunes.

---

## Sources

### Primary (HIGH confidence — verified from codebase)
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/DecisionLogger.fs` — `DecisionLog` record (12 fields, `fallback_used: bool`)
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs` — Channel + BackgroundService pattern (authoritative template for HardCaseDatasetWriter)
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` — IHttpClientFactory + named client + resilience handler pattern
- `/Users/ohama/projs/smart-router/.planning/phases/05-routing-decision-logging/05-CONTEXT.md` — LOG-01 schema lock, `fallback_used` always false in Phase 5 SC-1
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — existing fsproj `<Compile>` order (Phase 7 adapters slot after QueueDispatcher)
- `/Users/ohama/projs/smart-router/tests/SmartRouter.Tests/LoggingTests.fs` — test patterns (fake Kestrel, temp dir isolation, Channel drain, testSequenced)
- `/Users/ohama/projs/smart-router/.planning/research/SUMMARY.md` — PITFALL-26 (Expecto rootTests), architecture invariants

### Secondary (HIGH confidence — direct reads)
- `/Users/ohama/projs/smart-router-distillation/idea/prompts/teacher_prompt.md` — exact teacher prompt content verified
- `/Users/ohama/projs/smart-router-distillation/documentation/auto-retraining-research.md` — §3.2 #9 (fallback signal gaps), component list, §3.1 feasibility matrix
- `/Users/ohama/projs/smart-router-distillation/documentation/howto/narrow-label-wide-train.md` — failure-only labeling rationale, `isFailure` pattern
- `/Users/ohama/projs/smart-router-distillation/idea/auto-retraining-code.md` — §5 FailureDetector sketch, §6 TeacherLabeler sketch

### Tertiary (supporting context)
- `Microsoft.Extensions.Http.Resilience` docs — `AddResilienceHandler`, `RetryStrategyOptions`, `DelayBackoffType`

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new NuGet packages; all patterns are direct mirrors of existing Phase 5/6 adapters
- Architecture: HIGH — component placement, fsproj order, and interface shapes are all derivable from existing codebase
- Pitfalls: HIGH — Pitfalls 1, 5, 6, 7, 8 are grounded in existing code constraints; Pitfalls 2, 3, 4 are standard file I/O / HTTP patterns
- Data-availability problem: HIGH — LOG-01 schema lock and Phase 10 deferral are documented facts

**Research date:** 2026-05-08
**Valid until:** Invalidated if Phase 10 ships before Phase 8 (data-availability recommendation changes) or if LOG-01 schema changes
