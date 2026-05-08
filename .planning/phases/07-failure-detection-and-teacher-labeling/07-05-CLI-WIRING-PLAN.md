---
phase: 07-failure-detection-and-teacher-labeling
plan: 05
type: execute
wave: 3
depends_on: ["07-02", "07-03", "07-04"]
files_modified:
  - src/SmartRouter.Cli/CompositionRoot.fs
  - src/SmartRouter.Cli/Program.fs
  - src/SmartRouter.Cli/appsettings.json
  - prompts/teacher-prompt.md
  - scripts/seed-hard-cases.fsx
  - .gitignore
autonomous: true

must_haves:
  truths:
    - "CompositionRoot binds TeacherLabelerOptions from 'TeacherLabeler' section and HardCaseDatasetOptions from 'HardCaseDataset' section in appsettings.json"
    - "A named HttpClient 'teacher' is registered with BaseAddress = TeacherLabeler:Endpoint, Timeout = TeacherLabeler:TimeoutSeconds, and AddResilienceHandler('teacher-pipeline') with explicit ShouldHandle predicate: 3 retry attempts on HttpRequestException/TaskCanceledException/5xx; explicitly does NOT retry on 4xx"
    - "FailureDetector, TeacherLabeler, and HardCaseDatasetWriter are each registered as DI singletons + interface aliases. HardCaseDatasetWriter additionally registered via AddHostedService<HardCaseDatasetWriter> with the same instance (triple-registration pattern from DecisionLogWriter)"
    - "Program.fs detects --retrain in args BEFORE WebApplication.CreateBuilder; on detection, runs the offline pipeline (resolve IFailureDetector + ITeacherLabeler + IHardCaseDatasetWriter from DI, extract hard cases, label each via teacher, append entries) and exits with code 0; does NOT start the Kestrel host"
    - "When --retrain runs and FailureDetector returns 0 hard cases, Program.fs prints a friendly summary ('Retrain: 0 hard cases found; run scripts/seed-hard-cases.fsx for synthetic data') and exits 0 — never crashes on empty input"
    - "appsettings.json has TeacherLabeler + HardCaseDataset sections; existing sections (Routing, Queue, DecisionLog, Serilog) untouched"
    - "prompts/teacher-prompt.md exists in the repo with the exact content from ~/projs/smart-router-distillation/prompts/teacher_prompt.md (or equivalent file in that repo)"
    - "scripts/seed-hard-cases.fsx exists, is executable via 'dotnet fsi scripts/seed-hard-cases.fsx', writes ~30 synthetic entries (Korean + English mix; technical/casual mix) to datasets/hard-cases.jsonl with source='synthetic'"
    - ".gitignore includes datasets/ entry (alongside existing logs/ and models/)"
    - "dotnet build succeeds with 0 warnings; existing 50 tests still pass + 10 ignored (DI ripples don't break tests because new services are unreferenced by existing test fixtures)"
  artifacts:
    - path: "src/SmartRouter.Cli/CompositionRoot.fs"
      provides: "DI registrations for TeacherLabelerOptions, HardCaseDatasetOptions, named HttpClient 'teacher' with retry policy, FailureDetector + TeacherLabeler + HardCaseDatasetWriter (triple-reg for the writer)"
      contains: "AddResilienceHandler"
    - path: "src/SmartRouter.Cli/Program.fs"
      provides: "--retrain argument handler that runs the offline pipeline and exits before host startup"
      contains: "--retrain"
    - path: "src/SmartRouter.Cli/appsettings.json"
      provides: "TeacherLabeler + HardCaseDataset sections"
      contains: "TeacherLabeler"
    - path: "prompts/teacher-prompt.md"
      provides: "Teacher prompt template — system message content for ROUTE_35B/ROUTE_122B classification"
      contains: "ROUTE_35B"
    - path: "scripts/seed-hard-cases.fsx"
      provides: "F# script that writes ~30 synthetic hard-case entries to datasets/hard-cases.jsonl"
      contains: "Qwen122B"
    - path: ".gitignore"
      provides: "datasets/ ignore entry"
      contains: "datasets/"
  key_links:
    - from: "CompositionRoot.fs HttpClient registration"
      to: "AddResilienceHandler('teacher-pipeline') with explicit ShouldHandle predicate"
      via: "Microsoft.Extensions.Http.Resilience"
      pattern: "AddResilienceHandler"
    - from: "Program.fs --retrain branch"
      to: "IFailureDetector + ITeacherLabeler + IHardCaseDatasetWriter"
      via: "host.Services.GetRequiredService<...>"
      pattern: "GetRequiredService<IFailureDetector>"
    - from: "CompositionRoot.fs HardCaseDatasetWriter registration"
      to: "AddHostedService<HardCaseDatasetWriter>"
      via: "triple-registration: AddSingleton<Concrete> + AddSingleton<Interface> + AddHostedService<Concrete>"
      pattern: "AddHostedService<HardCaseDatasetWriter>"
---

<objective>
Wire the three Phase 7 adapters into DI, register the named `"teacher"` HttpClient with retry policy + 30s timeout, add the `--retrain` CLI handler to Program.fs, ship the appsettings.json sections, copy the teacher prompt template into the repo, write the synthetic seed script, and update .gitignore for `datasets/`. After this plan, `dotnet run --project src/SmartRouter.Cli -- --retrain` exercises the full offline pipeline end-to-end (with synthetic seed data primed via `dotnet fsi scripts/seed-hard-cases.fsx`).

Purpose: integration. Wave 2 produced three independently-correct adapters; this plan composes them and exposes operator-driven entry points. Phase 8's BackgroundService will compose the same DI singletons later.

Output:
- Updated CompositionRoot.fs (4 new DI registrations + 1 named HttpClient + 1 hosted service)
- Updated Program.fs (--retrain branch — runs pipeline, exits before host startup)
- Updated appsettings.json (TeacherLabeler + HardCaseDataset sections)
- prompts/teacher-prompt.md (new file, committed to repo)
- scripts/seed-hard-cases.fsx (new file)
- Updated .gitignore (datasets/)
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/07-failure-detection-and-teacher-labeling/07-CONTEXT.md
@.planning/phases/07-failure-detection-and-teacher-labeling/07-RESEARCH.md
@src/SmartRouter.Core/RetrainingPorts.fs
@src/SmartRouter.Cli/Adapters/FailureDetector.fs
@src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
@src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
@src/SmartRouter.Cli/CompositionRoot.fs
@src/SmartRouter.Cli/Program.fs
@src/SmartRouter.Cli/appsettings.json
</context>

<tasks>

<task type="auto">
  <name>Task 1: CompositionRoot DI wiring + named 'teacher' HttpClient with resilience handler</name>
  <files>
    src/SmartRouter.Cli/CompositionRoot.fs
    src/SmartRouter.Cli/appsettings.json
    .gitignore
  </files>
  <action>
**Step 1 — `src/SmartRouter.Cli/appsettings.json`:** Add two new sections AFTER `"DecisionLog"` and BEFORE `"Serilog"`:

```json
"TeacherLabeler": {
  "Endpoint":       "http://127.0.0.1:8001",
  "PromptPath":     "prompts/teacher-prompt.md",
  "DailyCallCap":   1000,
  "TimeoutSeconds": 30,
  "DatasetsDir":    "datasets"
},
"HardCaseDataset": {
  "Path":            "datasets/hard-cases.jsonl",
  "ChannelCapacity": 1000
},
```

**Step 2 — `.gitignore`:** Append (placement after the existing `models/` block).

**CRITICAL: Add ONLY `datasets/` to .gitignore. DO NOT add `prompts/`.** `prompts/teacher-prompt.md` is a small text file that MUST be committed (operator-editable per CONTEXT.md decision; required by invariant 13). RESEARCH.md mentions gitignoring `prompts/` — that guidance is overridden by CONTEXT.md and is wrong; only `datasets/` belongs in .gitignore.

```
# Hard-case training datasets — operator-curated, may contain prompt text; do not commit
datasets/
```

**Step 3 — `src/SmartRouter.Cli/CompositionRoot.fs`:** Add three blocks of DI registration. Add to the existing imports at the top:

```fsharp
open Microsoft.Extensions.Http.Resilience
open Polly
open Polly.Retry
open SmartRouter.Core.RetrainingPorts
open SmartRouter.Cli.Adapters.FailureDetector
open SmartRouter.Cli.Adapters.TeacherLabeler
open SmartRouter.Cli.Adapters.HardCaseDatasetWriter
```

(If the executor finds that `Polly` types come transitively via `Microsoft.Extensions.Http.Resilience` only, the `open Polly` and `open Polly.Retry` lines may not be needed — check the resolved type names of `RetryStrategyOptions`, `DelayBackoffType`, and `HttpRetryStrategyOptions` and import the correct namespace. The pattern follows `Microsoft.Extensions.Http.Resilience`'s public surface.)

Inside `configureServices`, AFTER the existing `AddHostedService<DecisionLogWriter>` block (the very last lines of the function before `services` return), insert these three registration groups in order:

**3a. Bind options + register named "teacher" HttpClient with resilience handler (FAIL-02 + FAIL-03):**

```fsharp
// ── Phase 7: Failure detection + teacher labeling ─────────────────────────
services.Configure<TeacherLabelerOptions>(config.GetSection("TeacherLabeler")) |> ignore
services.Configure<HardCaseDatasetOptions>(config.GetSection("HardCaseDataset")) |> ignore

// Named HttpClient "teacher" — independent of the QueueDispatcher-gated upstream clients.
// Routing teacher calls through QueueDispatcher would starve real inference traffic
// of the 122B SemaphoreSlim(1) slot (Pitfall 5 from RESEARCH.md).
//
// Resilience handler: 3 retry attempts, exponential 1s/2s/4s, transient errors only.
// HttpClient.Timeout = TeacherLabeler:TimeoutSeconds (default 30s) — applied per attempt.
services.AddHttpClient("teacher", fun c ->
    let opts = config.GetSection("TeacherLabeler").Get<TeacherLabelerOptions>()
    let endpoint = if String.IsNullOrWhiteSpace(opts.Endpoint) then "http://127.0.0.1:8001" else opts.Endpoint
    let timeoutSec = if opts.TimeoutSeconds <= 0 then 30 else opts.TimeoutSeconds
    c.BaseAddress <- Uri(endpoint)
    c.Timeout     <- TimeSpan.FromSeconds(float timeoutSec))
    .AddResilienceHandler("teacher-pipeline", fun builder ->
        // Use AddResilienceHandler (NOT AddStandardResilienceHandler) so we can
        // make the 4xx-skip explicit per FAIL-02 ("transient errors only").
        // RESEARCH.md Pattern 5 — explicit ShouldHandle predicate:
        //   - HttpRequestException → retry (transport errors)
        //   - TaskCanceledException → retry (timeout / per-attempt cancellation)
        //   - 5xx HTTP responses   → retry
        //   - 4xx HTTP responses   → DO NOT retry (logic errors; teacher-side rejection)
        //   - any other exception  → do not retry (fail fast)
        let opts = config.GetSection("TeacherLabeler").Get<TeacherLabelerOptions>()
        let timeoutSec = if opts.TimeoutSeconds <= 0 then 30 else opts.TimeoutSeconds
        let retryOpts =
            HttpRetryStrategyOptions(
                MaxRetryAttempts = 3,
                BackoffType      = DelayBackoffType.Exponential,
                Delay            = TimeSpan.FromSeconds(1.0),
                ShouldHandle     = fun args ->
                    ValueTask.FromResult(
                        match args.Outcome.Exception with
                        | :? HttpRequestException   -> true
                        | :? TaskCanceledException  -> true
                        | null ->
                            let status = int args.Outcome.Result.StatusCode
                            status >= 500
                        | _ -> false))
        builder.AddRetry(retryOpts) |> ignore
        builder.AddTimeout(TimeSpan.FromSeconds(float timeoutSec)) |> ignore)
    |> ignore
```

**Note:** The explicit `ShouldHandle` predicate is the load-bearing fix vs. `AddStandardResilienceHandler` defaults — it makes "skip 4xx" visible at the registration site and aligned with the locked FAIL-02 spec. If the executor finds the precise type `HttpRetryStrategyOptions` lives in a different namespace under the resolved Microsoft.Extensions.Http.Resilience version, adapt the import (likely `open Microsoft.Extensions.Http.Resilience` plus `open Polly` for the strategy types). The contract is: 3 retry attempts, exponential 1s/2s/4s backoff, retry only on transport exceptions or 5xx, NEVER on 4xx, with the per-attempt timeout = TeacherLabeler:TimeoutSeconds.

**3b. Register adapters as DI singletons + interface aliases:**

```fsharp
// FailureDetector — reads from the same logs/decisions/ directory as DecisionLogWriter writes to.
services.AddSingleton<FailureDetector>(fun sp ->
    let opts = sp.GetRequiredService<IOptions<DecisionLogOptions>>().Value
    let dir = if String.IsNullOrWhiteSpace(opts.Directory) then "logs/decisions" else opts.Directory
    FailureDetector(dir))
|> ignore

services.AddSingleton<IFailureDetector>(fun sp ->
    sp.GetRequiredService<FailureDetector>() :> IFailureDetector)
|> ignore

// TeacherLabeler — uses IHttpClientFactory + named "teacher" client (registered above).
services.AddSingleton<TeacherLabeler>(fun sp ->
    let opts = sp.GetRequiredService<IOptions<TeacherLabelerOptions>>().Value
    // Defensive defaults if config absent
    let normalized =
        { Endpoint        = if String.IsNullOrWhiteSpace(opts.Endpoint)   then "http://127.0.0.1:8001" else opts.Endpoint
          PromptPath      = if String.IsNullOrWhiteSpace(opts.PromptPath) then "prompts/teacher-prompt.md" else opts.PromptPath
          DailyCallCap    = if opts.DailyCallCap   <= 0 then 1000 else opts.DailyCallCap
          TimeoutSeconds  = if opts.TimeoutSeconds <= 0 then 30   else opts.TimeoutSeconds
          DatasetsDir     = if String.IsNullOrWhiteSpace(opts.DatasetsDir) then "datasets" else opts.DatasetsDir }
    TeacherLabeler(sp.GetRequiredService<IHttpClientFactory>(), normalized))
|> ignore

services.AddSingleton<ITeacherLabeler>(fun sp ->
    sp.GetRequiredService<TeacherLabeler>() :> ITeacherLabeler)
|> ignore
```

**3c. Triple-registration of HardCaseDatasetWriter (mirror DecisionLogWriter at lines 277-291):**

```fsharp
// HardCaseDatasetWriter — concrete singleton + IHardCaseDatasetWriter alias + AddHostedService.
// Same instance for all three roles (DO NOT use three separate AddSingleton<HardCaseDatasetWriter>
// — that creates three instances, each with its own Channel and BackgroundService loop).
services.AddSingleton<HardCaseDatasetWriter>(fun sp ->
    let opts = sp.GetRequiredService<IOptions<HardCaseDatasetOptions>>().Value
    let p   = if String.IsNullOrWhiteSpace(opts.Path) then "datasets/hard-cases.jsonl" else opts.Path
    let cap = if opts.ChannelCapacity <= 0 then 1000 else opts.ChannelCapacity
    new HardCaseDatasetWriter({ Path = p; ChannelCapacity = cap }))
|> ignore

services.AddSingleton<IHardCaseDatasetWriter>(fun sp ->
    sp.GetRequiredService<HardCaseDatasetWriter>() :> IHardCaseDatasetWriter)
|> ignore

services.AddHostedService<HardCaseDatasetWriter>(fun sp ->
    sp.GetRequiredService<HardCaseDatasetWriter>())
|> ignore
```

**Important:** all three registration groups go BEFORE the `services` return statement at the end of `configureServices`. DO NOT change any existing registration; only ADD.

The `IOptions<DecisionLogOptions>` resolution in 3b assumes Plan 05-01 already registered `DecisionLogOptions` — verified by reading the existing code (lines 271-291 in CompositionRoot.fs already do this).
  </action>
  <verify>
- `dotnet build SmartRouter.slnx -nologo --tl:off` succeeds with 0 warnings.
- `grep -n "TeacherLabelerOptions\|HardCaseDatasetOptions" src/SmartRouter.Cli/CompositionRoot.fs` returns at least 4 hits (Configure call + AddSingleton factory + IOptions resolution).
- `grep -n "AddHttpClient.*teacher" src/SmartRouter.Cli/CompositionRoot.fs` returns 1 hit.
- `grep -n "AddResilienceHandler" src/SmartRouter.Cli/CompositionRoot.fs` returns 1 hit (named "teacher-pipeline" with explicit ShouldHandle predicate).
- `grep -n "ShouldHandle" src/SmartRouter.Cli/CompositionRoot.fs` returns at least 1 hit (the predicate explicitly skips 4xx — FAIL-02 alignment).
- `grep -nE "AddSingleton<(IFailureDetector|ITeacherLabeler|IHardCaseDatasetWriter)>" src/SmartRouter.Cli/CompositionRoot.fs` returns 3 hits.
- `grep -n "AddHostedService<HardCaseDatasetWriter>" src/SmartRouter.Cli/CompositionRoot.fs` returns 1 hit.
- `grep -n "TeacherLabeler\|HardCaseDataset" src/SmartRouter.Cli/appsettings.json` returns at least 4 hits (2 section headers + at least 2 keys).
- `grep -n "datasets/" .gitignore` returns 1 hit.
  </verify>
  <done>
DI wiring complete: TeacherLabelerOptions + HardCaseDatasetOptions bound, named "teacher" HttpClient registered with resilience handler, three adapters registered as singletons + interface aliases, HardCaseDatasetWriter triple-registered (same instance for IHostedService). appsettings.json + .gitignore updated. Build clean. No test ripple (the new services are unreferenced by existing tests).
  </done>
</task>

<task type="auto">
  <name>Task 2: Program.fs --retrain handler (offline pipeline; exits before host startup)</name>
  <files>
    src/SmartRouter.Cli/Program.fs
  </files>
  <action>
Modify `src/SmartRouter.Cli/Program.fs` to detect `--retrain` in `args` and run the offline pipeline without starting the Kestrel host. The CLI command:
- Builds the same DI container as the host would (so adapters are wired identically)
- Resolves `IFailureDetector`, `ITeacherLabeler`, `IHardCaseDatasetWriter` from the container
- Calls `ExtractHardCases` -> for each result, calls `LabelAsync` with `hardCase.PromptText` (or skips if `None` since the offline path can't recover prompts), -> appends labeled results to the dataset
- Prints a friendly summary, exits 0
- Does NOT call `app.Run()`

**Diff to apply:**

After the existing `try try` block opens (around line 22), and BEFORE `let builder = WebApplication.CreateBuilder(args)`, add the --retrain branch:

```fsharp
// Phase 7: --retrain CLI command — runs the offline labeling pipeline and exits.
// Does NOT start the Kestrel host. Useful for operator-driven manual retraining
// and CI-friendly testing. Phase 8's BackgroundService composes the same DI
// singletons on a PeriodicTimer.
if args |> Array.contains "--retrain" then
    let builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args)
    builder.Configuration
           .SetBasePath(System.IO.Directory.GetCurrentDirectory())
           .AddJsonFile("appsettings.json", optional = false)
        |> ignore
    CompositionRoot.configureServices builder.Services builder.Configuration |> ignore
    use host = builder.Build()
    do host.StartAsync().GetAwaiter().GetResult()
    try
        let detector = host.Services.GetRequiredService<SmartRouter.Core.RetrainingPorts.IFailureDetector>()
        let labeler  = host.Services.GetRequiredService<SmartRouter.Core.RetrainingPorts.ITeacherLabeler>()
        let writer   = host.Services.GetRequiredService<SmartRouter.Core.RetrainingPorts.IHardCaseDatasetWriter>()
        let ct = System.Threading.CancellationToken.None

        let hardCases =
            detector.ExtractHardCases(ct).GetAwaiter().GetResult()

        Log.Information("Retrain: extracted {N} hard case(s)", List.length hardCases)

        let mutable labeled  = 0
        let mutable skipped  = 0
        let mutable failed   = 0
        for hc in hardCases do
            match hc.PromptText with
            | None ->
                Log.Information(
                    "Retrain: skipping correlation_id={Cid} — prompt text not in logs (LOG-01 schema; Phase 8 BackgroundService passes inline)",
                    hc.CorrelationId)
                skipped <- skipped + 1
            | Some pt ->
                let result = labeler.LabelAsync(pt, hc.CorrelationId, ct).GetAwaiter().GetResult()
                match result with
                | SmartRouter.Core.RetrainingPorts.Labeled (label, excerpt) ->
                    let labelInt =
                        match label with
                        | SmartRouter.Core.RetrainingPorts.Route35B  -> 0
                        | SmartRouter.Core.RetrainingPorts.Route122B -> 1
                    let target =
                        match label with
                        | SmartRouter.Core.RetrainingPorts.Route35B  -> "Qwen35B"
                        | SmartRouter.Core.RetrainingPorts.Route122B -> "Qwen122B"
                    let entry : SmartRouter.Core.RetrainingPorts.HardCaseEntry =
                        { SchemaVersion          = 1
                          CorrelationId          = hc.CorrelationId
                          PromptHash             = hc.PromptHash
                          PromptText             = pt
                          Label                  = labelInt
                          Source                 = "teacher"
                          TeacherResponseExcerpt = Some excerpt
                          LabeledAt              = System.DateTimeOffset.UtcNow
                          PromptKoreanCharRatio  = hc.PromptKoreanCharRatio
                          RoutingAlgorithm       = hc.RoutingAlgorithm
                          Target                 = target }
                    writer.AppendAsync(entry, ct).GetAwaiter().GetResult()
                    labeled <- labeled + 1
                | SmartRouter.Core.RetrainingPorts.Unparseable raw ->
                    Log.Warning("Retrain: unparseable response for {Cid}: {Raw}", hc.CorrelationId, raw)
                    failed <- failed + 1
                | SmartRouter.Core.RetrainingPorts.Skipped reason ->
                    Log.Information("Retrain: skipped {Cid} — {Reason}", hc.CorrelationId, reason)
                    skipped <- skipped + 1
                | SmartRouter.Core.RetrainingPorts.Failed err ->
                    Log.Warning("Retrain: failed {Cid} — {Err}", hc.CorrelationId, err)
                    failed <- failed + 1

        if List.isEmpty hardCases then
            printfn "Retrain: 0 hard cases found; run scripts/seed-hard-cases.fsx for synthetic data."
        else
            printfn "Retrain: %d labeled, %d skipped, %d failed of %d total"
                labeled skipped failed (List.length hardCases)
    finally
        host.StopAsync().GetAwaiter().GetResult()
    Logging.shutdown ()
    exit 0
```

**Imports needed** at the top of Program.fs (some may already be present):

```fsharp
open Microsoft.Extensions.DependencyInjection   // already present
open Microsoft.Extensions.Hosting               // ADD if missing — for Host.CreateApplicationBuilder
```

The check `Array.contains "--retrain"` is intentionally simple — `--retrain` is a flag, no value. If a future operator wants `--retrain --some-other-arg`, both are honored without parsing complexity.

**Why `Host.CreateApplicationBuilder` instead of `WebApplication.CreateBuilder`:** the offline path doesn't need Kestrel. `Host.CreateApplicationBuilder` produces a `HostApplicationBuilder` with the same DI/Configuration/Logging surface but no web pipeline — keeps the offline run lean and avoids accidentally binding to port 4000 if the host happens to start.

**`exit 0` is intentional** — the path uses `printfn` for the operator-readable summary and exits cleanly. The existing `try ... try ... finally Logging.shutdown ()` pattern is preserved by calling `Logging.shutdown ()` explicitly before `exit 0`. The existing `else` branch (regular host startup) is unchanged.

The `--retrain` path goes BEFORE `--routing-algorithm` parsing because `--retrain` runs to completion and exits — it never reaches the host startup logic that consumes `--routing-algorithm`.
  </action>
  <verify>
- `dotnet build SmartRouter.slnx -nologo --tl:off` succeeds with 0 warnings.
- `grep -n "\\-\\-retrain" src/SmartRouter.Cli/Program.fs` returns at least 1 hit (the args check).
- `grep -nE "GetRequiredService<(IFailureDetector|ITeacherLabeler|IHardCaseDatasetWriter)>" src/SmartRouter.Cli/Program.fs` returns 3 hits.
- `grep -n "ExtractHardCases\|LabelAsync\|AppendAsync" src/SmartRouter.Cli/Program.fs` returns at least 3 hits (one per pipeline step).
- **Runtime guard: --retrain MUST NOT bind Kestrel to port 4000.** The offline path uses `Host.CreateApplicationBuilder` which has no web pipeline; we assert the absence of the Kestrel "Now listening on" line for port 4000 to prove the host startup branch was skipped:
  ```bash
  timeout 30 dotnet run --project src/SmartRouter.Cli -- --retrain 2>&1 | tee /tmp/retrain.log
  grep -c "Now listening on.*4000" /tmp/retrain.log
  ```
  Expected: `0` (Kestrel not bound during --retrain). Exit code of the dotnet command should also be 0 (graceful exit).
- `dotnet run --project src/SmartRouter.Cli -- --retrain 2>&1 | head -30` runs to completion (exit code 0); output includes either "Retrain: 0 hard cases found" or per-entry log lines depending on whether seed data is present.
- Existing 50 tests still pass + 10 ignored (no test changes; the --retrain branch is a CLI path not exercised by tests).
  </verify>
  <done>
Program.fs has a --retrain branch that runs the full offline pipeline (detector → labeler → writer) and exits before host startup. The existing host startup path (without --retrain) is unchanged. Empty result is handled gracefully with a friendly message.
  </done>
</task>

<task type="auto">
  <name>Task 3: prompts/teacher-prompt.md + scripts/seed-hard-cases.fsx + .gitignore datasets/</name>
  <files>
    prompts/teacher-prompt.md
    scripts/seed-hard-cases.fsx
  </files>
  <action>
**Step 1 — `prompts/teacher-prompt.md`:** Create directory + file. Copy content verbatim from `~/projs/smart-router-distillation/prompts/teacher_prompt.md` (or `~/projs/smart-router-distillation/idea/prompts/teacher_prompt.md`, whichever exists). The exact content (verified in RESEARCH.md):

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

Note: `prompts/` is NOT gitignored (small text files are intentionally committed so a fresh checkout has the default prompt without running ops scripts). The prompt file IS the default; operators may swap it via the `TeacherLabeler:PromptPath` config key without recompiling.

**Step 2 — `scripts/seed-hard-cases.fsx`:** Create the synthetic seed script. Run command (documented at top of file): `dotnet fsi scripts/seed-hard-cases.fsx`. The script writes ~30 synthetic entries to `datasets/hard-cases.jsonl` (creating the directory if missing). Each entry uses `source = "synthetic"` and a synthetic correlation_id like `seed-NNN`. Mix Korean + English; mix technical (debug, refactor, compiler) + casual (chat, factual lookup); mix targets (~half each).

```fsharp
// scripts/seed-hard-cases.fsx
//
// Synthetic hard-case seed for Phase 8's retraining loop.
// Run after first router startup and download-models.sh.
//
// Usage:
//   dotnet fsi scripts/seed-hard-cases.fsx
//
// Writes ~30 entries to datasets/hard-cases.jsonl. Each entry has source="synthetic"
// and correlation_id="seed-NNN" so downstream consumers can filter or weight them.
//
// Idempotency: re-runs do not duplicate (the live HardCaseDatasetWriter dedupes by
// (correlation_id, prompt_hash); this script writes directly, so for now it will
// over-write on rerun — acceptable for v1 since the dataset is operator-curated.

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

let datasetsDir = "datasets"
let outPath     = Path.Combine(datasetsDir, "hard-cases.jsonl")

let computeHash (s: string) : string =
    use sha = SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(s)
    let hash  = sha.ComputeHash(bytes)
    hash |> Array.map (sprintf "%02x") |> String.concat ""

let koreanRatio (s: string) : float =
    if s.Length = 0 then 0.0
    else
        let n = s |> Seq.filter (fun c -> c >= '가' && c <= '힣') |> Seq.length
        float n / float s.Length

// (prompt, label_string) pairs — label is "Qwen35B" or "Qwen122B"
let hardCases : (string * string) list = [
    // English — easy / casual → 35B
    "what is 2+2",                                                                "Qwen35B"
    "summarize this paragraph in one sentence",                                   "Qwen35B"
    "list the days of the week",                                                  "Qwen35B"
    "what time zone is Seoul in",                                                 "Qwen35B"
    "translate 'hello' to Spanish",                                               "Qwen35B"
    "give me a one-line bash command to count lines in a file",                   "Qwen35B"

    // English — hard / technical → 122B
    "explain F# compiler error 'value restriction' with a minimal repro",         "Qwen122B"
    "design a hexagonal architecture for a multi-tenant SaaS billing service",    "Qwen122B"
    "debug a deadlock in a Go service using two channels and a SemaphoreSlim",    "Qwen122B"
    "refactor a 1500-line ASP.NET controller into clean vertical slices",         "Qwen122B"
    "trace an LLVM IR optimization pass that removes a side-effecting call",      "Qwen122B"
    "explain MLIR dialect lowering for a custom domain-specific operation",       "Qwen122B"

    // Korean — easy / casual → 35B
    "안녕하세요",                                                                  "Qwen35B"
    "오늘 날씨가 어때요",                                                          "Qwen35B"
    "한국어로 인사하는 법",                                                        "Qwen35B"
    "1 더하기 1은 무엇인가요",                                                     "Qwen35B"
    "서울에서 가장 유명한 음식 한 가지만 알려주세요",                              "Qwen35B"

    // Korean — hard / technical → 122B
    "F# 컴파일러 타입 추론 에러를 해결하기 위한 단계별 디버깅 전략을 설명해주세요", "Qwen122B"
    "이 코드의 메모리 누수 원인을 분석하고 패치 방법을 제안해주세요",              "Qwen122B"
    "트랜잭션 격리 수준을 설명하고 각 수준에서 발생하는 이상현상을 정리해주세요",  "Qwen122B"
    "헥사고날 아키텍처와 클린 아키텍처의 차이점을 비교 분석해주세요",              "Qwen122B"

    // Mixed Korean+English — varying difficulty
    "F# task {} 에러 메시지 'control flow not allowed' 해결 방법",                 "Qwen122B"
    "react useState hook 사용법 짧게 알려줘",                                      "Qwen35B"
    "Kubernetes pod CrashLoopBackOff 원인 진단 절차",                              "Qwen122B"
    "python list comprehension 예시 하나만",                                       "Qwen35B"

    // Code-heavy English → 122B
    "given this stack trace, identify the race condition: ...",                   "Qwen122B"
    "rewrite this O(n^2) algorithm in O(n log n)",                                "Qwen122B"

    // Short factual → 35B
    "capital of France",                                                          "Qwen35B"
    "current year",                                                               "Qwen35B"
    "who wrote 1984",                                                             "Qwen35B"
]

Directory.CreateDirectory(datasetsDir) |> ignore

let opts = JsonSerializerOptions(WriteIndented = false)

use sw = new StreamWriter(outPath, append = false, encoding = Encoding.UTF8)
let mutable idx = 1
for (prompt, label) in hardCases do
    let labelInt = if label = "Qwen122B" then 1 else 0
    let entry =
        {| schema_version            = 1
           correlation_id            = sprintf "seed-%03d" idx
           prompt_hash               = computeHash prompt
           prompt_text               = prompt
           label                     = labelInt
           source                    = "synthetic"
           teacher_response_excerpt  = Unchecked.defaultof<string>   // serialize as null
           labeled_at                = DateTimeOffset.UtcNow.ToString("o")
           prompt_korean_char_ratio  = koreanRatio prompt
           routing_algorithm         = "synthetic"
           target                    = label |}
    sw.WriteLine(JsonSerializer.Serialize(entry, opts))
    idx <- idx + 1

printfn "Wrote %d synthetic hard-case entries to %s" (idx - 1) outPath
```

**Notes on the seed script:**
- Uses `[<CLIMutable>]`-style anonymous records — F# scripts (.fsx) can use the lightweight `{| ... |}` form here for JSON serialization without a CLIMutable annotation.
- The `teacher_response_excerpt` field uses `Unchecked.defaultof<string>` (which serializes as JSON `null`) since synthetic entries have no teacher response. Phase 8 readers must handle this nullable field; the production HardCaseEntry record uses `string option` which serializes via FSharp.SystemTextJson, but the script bypasses FSharpConverter and uses the default STJ converter, so `null` is the correct wire shape for the absent excerpt.
- The 30-pair list is a balanced mix: ~50% target=Qwen35B, ~50% target=Qwen122B, with Korean content distributed across both labels (so the ML classifier sees Korean as both 35B and 122B examples — required for the multilingual semantics that bge-m3 captures).
- Append mode is `false` (overwrite) to keep reruns idempotent. Operators who want to preserve handcrafted entries should not edit `hard-cases.jsonl` directly — they should use the live HardCaseDatasetWriter via the --retrain CLI or Phase 8's BackgroundService.

The .gitignore entry `datasets/` (added in Task 1, Step 2) ensures the seed output is not committed.
  </action>
  <verify>
- `cat prompts/teacher-prompt.md | head -1` returns "You are an expert in LLM routing." (or equivalent first line of teacher prompt).
- `grep -n "ROUTE_35B\|ROUTE_122B" prompts/teacher-prompt.md` returns at least 2 hits.
- `dotnet fsi scripts/seed-hard-cases.fsx` runs without error and prints "Wrote N synthetic hard-case entries to datasets/hard-cases.jsonl" where N >= 30.
- `wc -l datasets/hard-cases.jsonl` returns >= 30.
- `head -1 datasets/hard-cases.jsonl | python3 -c "import sys,json; e=json.loads(sys.stdin.read()); assert e['source']=='synthetic'; assert e['schema_version']==1; print('OK')"` (or a `dotnet fsi` equivalent) prints OK — the first line parses as JSON with the expected fields.
- `git check-ignore datasets/hard-cases.jsonl` returns the path (exit 0) — confirms .gitignore catches it.
  </verify>
  <done>
prompts/teacher-prompt.md exists with the verbatim teacher prompt content. scripts/seed-hard-cases.fsx runs end-to-end and produces a >= 30-entry synthetic hard-case dataset with both labels and Korean+English coverage. .gitignore prevents the dataset from being committed.
  </done>
</task>

</tasks>

<verification>
**Plan-level verification (run all in order):**

1. **Build is clean:**
   ```bash
   dotnet build SmartRouter.slnx -nologo --tl:off
   ```
   Expected: 0 errors, 0 warnings.

2. **Existing tests still pass:**
   ```bash
   dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off
   ```
   Expected: 50 passed + 10 ignored (no regression).

3. **--retrain pipeline runs end-to-end on synthetic data:**
   ```bash
   dotnet fsi scripts/seed-hard-cases.fsx
   # The seed script writes prompts directly to hard-cases.jsonl, so --retrain has
   # nothing new to label — but it should still run cleanly and report 0 production
   # hard cases (because logs/decisions/ has no fallback_used=true entries until Phase 10).
   timeout 30 dotnet run --project src/SmartRouter.Cli -- --retrain
   ```
   Expected: exits 0; stdout contains "Retrain: 0 hard cases found" (or similar).

4. **DI registration sanity:**
   ```bash
   grep -nE "AddSingleton<(IFailureDetector|ITeacherLabeler|IHardCaseDatasetWriter)>" src/SmartRouter.Cli/CompositionRoot.fs
   ```
   Expected: 3 hits.

5. **Named HttpClient with retry (explicit ShouldHandle, NOT AddStandardResilienceHandler):**
   ```bash
   grep -nE "AddHttpClient.*teacher|AddResilienceHandler|ShouldHandle" src/SmartRouter.Cli/CompositionRoot.fs
   ```
   Expected: at least 3 hits (named client + AddResilienceHandler + ShouldHandle predicate).

6. **HardCaseDatasetWriter triple-registration:**
   ```bash
   grep -nE "AddSingleton<HardCaseDatasetWriter>|AddSingleton<IHardCaseDatasetWriter>|AddHostedService<HardCaseDatasetWriter>" src/SmartRouter.Cli/CompositionRoot.fs
   ```
   Expected: 3 hits.

7. **Datasets ignored:**
   ```bash
   git check-ignore datasets/hard-cases.jsonl
   ```
   Expected: exit 0, path echoed.

8. **Teacher prompt committed:**
   ```bash
   git check-ignore prompts/teacher-prompt.md ; echo "exit=$?"
   ```
   Expected: exit 1 (NOT ignored).
</verification>

<success_criteria>
- All three Phase 7 adapters wired into DI; HardCaseDatasetWriter triple-registered (Task 1)
- Named "teacher" HttpClient with retry handler + 30s timeout registered (Task 1)
- Program.fs --retrain branch runs full pipeline and exits 0 (Task 2)
- prompts/teacher-prompt.md committed; scripts/seed-hard-cases.fsx writes 30+ synthetic entries (Task 3)
- .gitignore catches datasets/ (Task 1)
- 0 build warnings (TreatWarningsAsErrors=true)
- All 50 existing tests still pass + 10 ignored
</success_criteria>

<output>
After completion, create `.planning/phases/07-failure-detection-and-teacher-labeling/07-05-SUMMARY.md` listing the 6 files modified, any deviations from the plan (especially around AddStandardResilienceHandler vs AddResilienceHandler), and the test count delta (expected: 50 → 50, no test changes).
</output>

## Existing-test impact

| Test file | Tests | Needs change in 07-05? | Why / Why not |
|-----------|-------|------------------------|---------------|
| RoutingTests.fs | 22 | NO | Pure routing tests; no DI surface area for Phase 7. |
| StreamingTests.fs | 8 | NO | Uses configureServices but resolves only IUpstreamClient + RoutingAlgorithmRegistration; new Phase 7 services unreferenced. |
| QueueTests.fs | 9 | NO | Builds its own DI mock; no Phase 7 surface area. |
| LoadTests.fs | 2 (pending) | NO | Pending; not run by default. |
| MLRoutingTests.fs | 5 | NO | Uses configureServices but does not resolve Phase 7 services. The new HardCaseDatasetWriter AddHostedService registration is inert in MLRoutingTests because they use a ServiceCollection without IHost (HostedServices register but never start). |
| MLEmbeddingTests.fs | 3 (gated) | NO | Independent of Phase 7. |
| MLClassifierTests.fs | 3 (gated) | NO | Independent of Phase 7. |
| LoggingTests.fs | 5 | NO | Uses configureServices; the new HardCaseDatasetWriter starts (StartAsync runs, ExecuteAsync seeds dedupe from datasets/hard-cases.jsonl). The seedDedupe try/catch swallows missing-file errors — non-breaking. The writer's BackgroundService loop blocks on ReadAsync waiting for entries that never arrive in LoggingTests; StopAsync drains an empty channel. Functionally non-breaking. If a flake materializes, Plan 07-06 may add an HardCaseDataset:Path override to AddInMemoryCollection in startTestRouter. |

## REQ-ID coverage in this plan

- **FAIL-01**: FailureDetector wired into DI as IFailureDetector via singleton registration; resolvable from --retrain handler.
- **FAIL-02**: Named "teacher" HttpClient with 30s timeout + AddResilienceHandler 3x retry transient-only; TeacherLabeler resolves it via IHttpClientFactory.CreateClient("teacher").
- **FAIL-03**: TeacherLabelerOptions.DailyCallCap (default 1000) bound from appsettings.json TeacherLabeler section; cost-cap enforcement is in TeacherLabeler.fs (Plan 07-03), wired here.
- **FAIL-04**: HardCaseDatasetWriter registered as IHardCaseDatasetWriter + IHostedService (triple-registration); HardCaseDatasetOptions.Path = "datasets/hard-cases.jsonl"; .gitignore catches output.
- **Plus**: --retrain CLI command implemented in Program.fs; prompts/teacher-prompt.md shipped; scripts/seed-hard-cases.fsx writes ~30 synthetic entries.
