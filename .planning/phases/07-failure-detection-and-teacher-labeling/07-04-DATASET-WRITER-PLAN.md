---
phase: 07-failure-detection-and-teacher-labeling
plan: 04
type: execute
wave: 2
depends_on: ["07-01"]
files_modified:
  - src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
autonomous: true

must_haves:
  truths:
    - "HardCaseDatasetWriter inherits BackgroundService and drains a single Channel<HardCaseEntry> — direct mirror of DecisionLogWriter pattern (Phase 5 SUMMARY)"
    - "Channel uses BoundedChannelFullMode.Wait (NOT DropWrite) — losing training data is unacceptable; back-pressure is fine because writes are infrequent (one per labeled hard case). Producers may block briefly when the channel is full"
    - "Dedupe by (CorrelationId, PromptHash) — an in-memory HashSet seeded from the existing JSONL file at first ExecuteAsync iteration; AppendAsync writes that miss dedupe are dropped silently with a Debug log line; HashSet updated after each successful append"
    - "AppendAsync awaits Channel.Writer.WriteAsync (back-pressure honored) then returns — does NOT wait for the consumer to actually flush; that's the BackgroundService's job"
    - "Graceful drain on StopAsync: Writer.TryComplete signals end-of-stream; the consumer loop drains remaining items then disposes the StreamWriter cleanly (mirrors DecisionLogWriter lines 100-117)"
    - "The output file datasets/hard-cases.jsonl is opened with FileShare.None (single-writer guarantee within the process); per-line FlushAsync after every WriteLine"
    - "JSON serialization uses PropertyNamingPolicy.SnakeCaseLower + JsonFSharpConverter — same shape as DecisionLogWriter; field names in JSONL output are snake_case (correlation_id, prompt_hash, etc.)"
    - "dotnet build succeeds with 0 warnings; existing 50 tests still pass + 10 ignored"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs"
      provides: "HardCaseDatasetWriter(options) — BackgroundService + Channel<HardCaseEntry> + dedupe HashSet + per-line FlushAsync; mirrors DecisionLogWriter exactly"
      contains: "BoundedChannelFullMode.Wait"
  key_links:
    - from: "HardCaseDatasetWriter.AppendAsync"
      to: "channel.Writer.WriteAsync"
      via: "Channel back-pressure honored"
      pattern: "channel\\.Writer\\.WriteAsync"
    - from: "HardCaseDatasetWriter.ExecuteAsync"
      to: "channel.Reader.ReadAsync"
      via: "single-consumer drain loop"
      pattern: "channel\\.Reader\\.ReadAsync"
    - from: "HardCaseDatasetWriter.StopAsync"
      to: "channel.Writer.TryComplete"
      via: "graceful end-of-stream signal"
      pattern: "channel\\.Writer\\.TryComplete"
---

<objective>
Replace the 07-01 stub bodies of `HardCaseDatasetWriter.AppendAsync` and `ExecuteAsync` with the real Channel + single-writer BackgroundService implementation. This is a direct structural mirror of `Adapters/DecisionLogWriter.fs` (Phase 5) — same Channel + drain pattern + per-line FlushAsync — with three differences:

1. Different file path (`datasets/hard-cases.jsonl` vs `logs/decisions/YYYY-MM-DD.jsonl`).
2. **`BoundedChannelFullMode.Wait`** instead of `DropWrite` — losing training data is unacceptable; producers tolerate back-pressure (writes are infrequent — one per labeled hard case).
3. Append-time dedupe by `(CorrelationId, PromptHash)` via in-memory `HashSet<string>` seeded from existing file at startup.

Constructor signature `(options: HardCaseDatasetOptions)` unchanged from Plan 07-01 stub. The `HardCaseDatasetOptions` record stays in this module (preserved from 07-01).

Purpose: FAIL-04. The writer ships the persistence destination for Phase 8's retraining loop and for the synthetic seed script (Plan 07-05).

Output:
- src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs (replaces 07-01 stub bodies)
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/07-failure-detection-and-teacher-labeling/07-CONTEXT.md
@.planning/phases/07-failure-detection-and-teacher-labeling/07-RESEARCH.md
@src/SmartRouter.Core/RetrainingPorts.fs
@src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs
</context>

<tasks>

<task type="auto">
  <name>Task 1: Implement HardCaseDatasetWriter (Channel + BackgroundService + dedupe + per-line append) — direct mirror of DecisionLogWriter</name>
  <files>
    src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
  </files>
  <action>
Replace the entire contents of `src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs` (preserving the module name, the `HardCaseDatasetOptions` record, and the constructor signature from Plan 07-01) with the real implementation.

**Source-to-mirror reference:** `src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs` lines 26-122. The structural pattern is identical — Channel + BackgroundService + per-line write + graceful drain. Diffs called out inline as comments.

```fsharp
module SmartRouter.Cli.Adapters.HardCaseDatasetWriter

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Serilog
open SmartRouter.Core.RetrainingPorts

/// Cli-only options bound from appsettings.json "HardCaseDataset" section.
[<CLIMutable>]
type HardCaseDatasetOptions =
    { Path            : string   // default "datasets/hard-cases.jsonl"
      ChannelCapacity : int }    // default 1000

/// BackgroundService that drains Channel<HardCaseEntry> and appends to
/// datasets/hard-cases.jsonl. Single consumer — no locking on file handle.
///
/// Mirrors DecisionLogWriter (Phase 5) pattern with three diffs:
///   1. Single fixed-path file (no daily rotation — training datasets accumulate forever).
///   2. BoundedChannelFullMode.Wait (NOT DropWrite) — losing training data is unacceptable;
///      back-pressure is fine because writes are infrequent (one per labeled hard case).
///   3. Append-time dedupe by (CorrelationId, PromptHash) via in-memory HashSet seeded
///      from existing file at first iteration. Prevents double-labeling on rerun.
type HardCaseDatasetWriter(options: HardCaseDatasetOptions) =
    inherit BackgroundService()

    // Defensive defaults — CompositionRoot in Plan 07-05 also applies these,
    // but defending here lets ad-hoc construction (e.g., scripts) work without options.
    let path =
        if String.IsNullOrWhiteSpace(options.Path) then "datasets/hard-cases.jsonl"
        else options.Path

    let capacity =
        if options.ChannelCapacity <= 0 then 1000
        else options.ChannelCapacity

    // Bounded channel — Wait on overflow (per CONTEXT.md FAIL-04 design).
    // SingleWriter = false: many producers may call AppendAsync concurrently.
    // SingleReader = true: only this BackgroundService consumes.
    let channel =
        Channel.CreateBounded<HardCaseEntry>(
            BoundedChannelOptions(
                capacity,
                FullMode    = BoundedChannelFullMode.Wait,   // diff #2 from DecisionLogWriter
                SingleWriter = false,
                SingleReader = true))

    // JSON options for JSONL serialization. Same shape as DecisionLogWriter:
    //   - SnakeCaseLower: PascalCase F# fields → snake_case JSON keys.
    //   - JsonFSharpConverter: handles `string option` (TeacherResponseExcerpt) → null.
    let jsonOpts =
        let o = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
        o.Converters.Add(JsonFSharpConverter())
        o

    /// Build the dedupe key from an entry — kept opaque (string) so the HashSet
    /// is BCL-only and uses default string hashing.
    let dedupeKey (entry: HardCaseEntry) : string =
        sprintf "%s|%s" entry.CorrelationId entry.PromptHash

    /// Seed the dedupe HashSet from an existing hard-cases.jsonl file (if present).
    /// Called once at start of ExecuteAsync. Malformed lines are logged-and-skipped.
    let seedDedupe (set: HashSet<string>) : unit =
        if File.Exists(path) then
            try
                File.ReadAllLines(path)
                |> Array.iter (fun line ->
                    if not (String.IsNullOrWhiteSpace(line)) then
                        try
                            let entry = JsonSerializer.Deserialize<HardCaseEntry>(line, jsonOpts)
                            set.Add(dedupeKey entry) |> ignore
                        with ex ->
                            Log.Warning(ex, "HardCaseDatasetWriter: skipping malformed line during seed"))
            with ex ->
                Log.Warning(ex, "HardCaseDatasetWriter: failed to seed dedupe from {Path}", path)

    /// AppendAsync — fire-and-forget from the producer's perspective. Returns
    /// after the entry is written into the channel (back-pressure honored on Wait).
    /// The BackgroundService consumer flushes to disk on its own loop.
    interface IHardCaseDatasetWriter with
        member _.AppendAsync(entry: HardCaseEntry, ct: CancellationToken) : Task<unit> =
            task {
                do! channel.Writer.WriteAsync(entry, ct).AsTask()
            }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            // Ensure parent directory exists before any open. Idempotent.
            let parent = Path.GetDirectoryName(path)
            if not (String.IsNullOrEmpty(parent)) then
                Directory.CreateDirectory(parent) |> ignore

            // Seed dedupe set from existing file (rerun-safety per FAIL-04).
            let dedupe = HashSet<string>()
            seedDedupe dedupe
            Log.Information(
                "HardCaseDatasetWriter: seeded dedupe set with {N} existing entries from {Path}",
                dedupe.Count, path)

            let mutable writer : StreamWriter option = None
            // Open the writer with FileShare.None for single-writer guarantee.
            let openWriter () =
                writer |> Option.iter (fun w -> try w.Flush(); w.Dispose() with _ -> ())
                let stream =
                    new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.None)
                let sw = new StreamWriter(stream, Encoding.UTF8)
                sw.AutoFlush <- false   // explicit per-line flush for atomicity
                writer <- Some sw
                sw

            // Lazy: open on first write so an empty consumer never touches the file.
            let getWriter () =
                match writer with
                | Some w -> w
                | None   -> openWriter ()

            try
                while not stoppingToken.IsCancellationRequested do
                    let! entry = channel.Reader.ReadAsync(stoppingToken)
                    let key = dedupeKey entry
                    if dedupe.Contains(key) then
                        Log.Debug(
                            "HardCaseDatasetWriter: dedupe hit; skipping correlation_id={Cid} prompt_hash={Hash}",
                            entry.CorrelationId, entry.PromptHash)
                    else
                        let sw = getWriter ()
                        try
                            let line = JsonSerializer.Serialize(entry, jsonOpts)
                            sw.WriteLine(line)
                            sw.Flush()   // per-line OS write; atomic for short lines under PIPE_BUF
                            dedupe.Add(key) |> ignore
                        with ex ->
                            Log.Error(ex, "HardCaseDatasetWriter: write failed for correlation_id={Cid}", entry.CorrelationId)
            with
            | :? OperationCanceledException -> ()   // graceful shutdown via stoppingToken
            | :? ChannelClosedException     -> ()   // graceful shutdown via TryComplete()
            | ex -> Log.Error(ex, "HardCaseDatasetWriter: writer loop crashed")

            // Drain remaining items after cancellation (Pitfall P2 mirror).
            let mutable more = true
            while more do
                match channel.Reader.TryRead() with
                | true, entry ->
                    let key = dedupeKey entry
                    if dedupe.Contains(key) then ()
                    else
                        try
                            let sw = getWriter ()
                            sw.WriteLine(JsonSerializer.Serialize(entry, jsonOpts))
                            sw.Flush()
                            dedupe.Add(key) |> ignore
                        with ex ->
                            Log.Warning(ex, "HardCaseDatasetWriter: drain write failed")
                | false, _ -> more <- false

            // Dispose the StreamWriter cleanly (Pitfall P3 — no handle leak).
            writer |> Option.iter (fun w -> try w.Flush() with _ -> (); w.Dispose())
        }

    /// Signal end-of-stream so ExecuteAsync's drain phase runs.
    override _.StopAsync(cancellationToken: CancellationToken) =
        channel.Writer.TryComplete() |> ignore
        base.StopAsync(cancellationToken)
```

**Key design notes:**
- The `AppendAsync` body uses the canonical `do! channel.Writer.WriteAsync(entry, ct).AsTask()` form: `WriteAsync` returns `ValueTask`, `.AsTask()` lifts it to `Task`, and `do!` awaits inside the F# `task {}` CE. This preserves back-pressure: the producer awaits while the bounded channel is full (BoundedChannelFullMode.Wait). DO NOT use `|> Async.AwaitTask |> Async.StartAsTask` — the round-trip through `Async` breaks back-pressure semantics and adds a thread-pool hop for no reason.
- `HashSet<string>` (not `HashSet<struct (string * string)>`) — string concat as the dedupe key is simpler and lets the seed reader reuse the same key shape. The `|` separator is safe because correlation_ids are GUIDs (32 hex chars) and prompt_hashes are SHA-256 hex (64 chars) — neither contains `|`.
- `BoundedChannelFullMode.Wait` is the load-bearing diff from DecisionLogWriter. Producers will block on `WriteAsync` when the channel is full; this is acceptable because hard-case appends are infrequent (typically one per labeled call, after a 30s teacher round-trip).
- `FileShare.None` ensures another process cannot open the same file for writing — the in-process Channel already guarantees single-writer; this defends against accidental dual-process invocations during testing.
- The `getWriter ()` lazy-open pattern means an idle BackgroundService that receives no messages never opens the file — important for tests that start/stop without producing entries.
- The dedupe HashSet is seeded ONCE on first ExecuteAsync iteration. Subsequent additions during the run keep it in sync. A fresh restart re-seeds from the on-disk file.

**No additional changes outside this file** — Cli.fsproj already has HardCaseDatasetWriter.fs registered (Plan 07-01). DI registration (AddSingleton + AddHostedService + IHardCaseDatasetWriter alias, mirroring DecisionLogWriter triple-registration pattern) lands in Plan 07-05's CompositionRoot. No tests (Plan 07-06).
  </action>
  <verify>
- `dotnet build SmartRouter.slnx -nologo --tl:off` succeeds with 0 warnings.
- `grep -n "NotImplementedException" src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs` returns NO hits (stub fully replaced).
- `grep -n "BoundedChannelFullMode\.Wait" src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs` returns 1 hit (the load-bearing diff from DecisionLogWriter).
- `grep -n "BoundedChannelFullMode\.DropWrite" src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs` returns NO hits (must be Wait, not DropWrite — Pitfall: losing training data is unacceptable).
- `grep -n "channel\.Writer\.WriteAsync\|channel\.Reader\.ReadAsync\|channel\.Writer\.TryComplete\|channel\.Reader\.TryRead" src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs` returns at least 4 hits (Channel API surface mirrors DecisionLogWriter).
- `grep -n "HashSet<string>\|dedupe" src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs` returns at least 2 hits (dedupe set + dedupe references).
- Existing 50 tests still pass + 10 ignored.
  </verify>
  <done>
HardCaseDatasetWriter implementation ships: Channel + BackgroundService consumer + dedupe HashSet + per-line FlushAsync + graceful drain. Constructor signature unchanged from 07-01 stub. `BoundedChannelFullMode.Wait` enforced. File opened with FileShare.None. JSONL output uses snake_case naming policy.
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
   grep -n "NotImplementedException" src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
   ```
   Expected: NO output.

3. **Channel back-pressure mode is Wait, not DropWrite:**
   ```bash
   grep -nE "BoundedChannelFullMode\.(Wait|DropWrite|DropOldest)" src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
   ```
   Expected: 1 hit, must be `Wait`.

4. **Channel API surface present (mirrors DecisionLogWriter):**
   ```bash
   grep -nE "channel\.(Writer|Reader)\.(WriteAsync|ReadAsync|TryComplete|TryRead)" src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
   ```
   Expected: at least 4 hits.

5. **Dedupe seeded from existing file:**
   ```bash
   grep -nE "(HashSet|dedupe|seedDedupe)" src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
   ```
   Expected: at least 3 hits.

6. **Existing tests still pass:**
   ```bash
   dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off
   ```
   Expected: 50 passed + 10 ignored.
</verification>

<success_criteria>
- HardCaseDatasetWriter real implementation ships; constructor signature unchanged
- BoundedChannelFullMode.Wait used (NOT DropWrite — losing training data unacceptable)
- Channel + BackgroundService + per-line FlushAsync + graceful drain pattern matches DecisionLogWriter
- Dedupe HashSet seeded from existing file at startup
- 0 build warnings (TreatWarningsAsErrors=true)
- All 50 existing tests still pass + 10 ignored
</success_criteria>

<output>
After completion, create `.planning/phases/07-failure-detection-and-teacher-labeling/07-04-SUMMARY.md` listing the file modified, any deviations from this plan, and the test count delta (expected: 50 → 50).
</output>
