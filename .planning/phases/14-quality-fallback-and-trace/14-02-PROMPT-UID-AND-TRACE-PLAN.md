---
phase: 14-quality-fallback-and-trace
plan: 02
type: execute
wave: 2
depends_on: ["14-01"]
files_modified:
  - src/SmartRouter.Cli/Adapters/TraceLogger.fs
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - src/SmartRouter.Cli/Program.fs
  - src/SmartRouter.Cli/CompositionRoot.fs
autonomous: true

must_haves:
  truths:
    - "src/SmartRouter.Cli/Adapters/TraceLogger.fs exists; defines `TraceRecord` 12-field record + `ITraceLogger` interface (Log: TraceRecord -> unit) + `TraceLogger : BackgroundService` implementation (Channel<TraceRecord> single-writer mirror of DecisionLogWriter)"
    - "TraceLogger writes to `logs/trace/YYYY-MM-DD.jsonl` (UTC date in filename); creates the directory if missing; FileShare.None per-line append"
    - "Program.fs parses `--trace-responses` flag and injects `Trace:Enabled=true` into IConfiguration AddInMemoryCollection BEFORE configureRequestPipeline runs"
    - "CompositionRoot.fs reads `Trace:Enabled` from IConfiguration; when true, registers ITraceLogger via AddSingleton + AddHostedService + IInterface alias (DecisionLogWriter triple-reg pattern); when false, ITraceLogger is NOT registered (consumers resolve via GetService and handle null)"
    - "TraceRecord schema matches CONTEXT spec exactly: schema_version (int=1), correlation_id (string), prompt_uid (string 12-hex), prompt_hash (string 64-hex), prompt_excerpt (string ≤200 chars), initial_target (string), initial_response_excerpt (string option), fallback_kind (string option: \"quality\"|\"availability\"|null), final_target (string), final_response_excerpt (string), total_latency_ms (float), timestamp (DateTimeOffset)"
    - "JSONL serialization uses snake_case property names + JsonFSharpConverter (mirrors DecisionLog convention)"
    - "Trace JSONL writer uses BoundedChannelFullMode.Wait (NOT DropWrite) — losing trace data is acceptable but defaulting to Wait keeps tooling-friendly behavior"
    - "dotnet build clean; dotnet test green; test count unchanged (this plan adds infrastructure only; tests in 14-05 actually exercise it)"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/TraceLogger.fs"
      provides: "TraceRecord type + ITraceLogger interface + TraceLogger BackgroundService implementation"
      min_lines: 90
---

<objective>
Add prompt-derived UID + trace logging infrastructure. Operator enables via `--trace-responses` CLI flag; when enabled, every chat-completion request appends a row to `logs/trace/YYYY-MM-DD.jsonl` containing prompt UID, initial 35B response excerpt (if fallback fires), final target, final response excerpt. UID = first 12 hex of existing prompt_hash (Phase 5).

This plan ships infrastructure only: TraceLogger writer + DI wiring. The endpoint integration (ChatCompletions.fs calling `traceLogger.Log(...)`) lands in plan 14-04.
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/14-quality-fallback-and-trace/14-CONTEXT.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Create Adapters/TraceLogger.fs</name>
  <files>
    - src/SmartRouter.Cli/Adapters/TraceLogger.fs (NEW)
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  </files>
  <action>
**Step 1.** Create `src/SmartRouter.Cli/Adapters/TraceLogger.fs`:

```fsharp
module SmartRouter.Cli.Adapters.TraceLogger

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open SmartRouter.Cli.Adapters.Json

/// Phase 14 — trace JSONL row for end-to-end request tracing.
/// Prompt UID = first 12 hex of prompt_hash (Phase 5 LOG-01).
[<CLIMutable>]
type TraceRecord = {
    [<JsonPropertyName("schema_version")>]
    schema_version              : int
    [<JsonPropertyName("correlation_id")>]
    correlation_id              : string
    [<JsonPropertyName("prompt_uid")>]
    prompt_uid                  : string
    [<JsonPropertyName("prompt_hash")>]
    prompt_hash                 : string
    [<JsonPropertyName("prompt_excerpt")>]
    prompt_excerpt              : string
    [<JsonPropertyName("initial_target")>]
    initial_target              : string
    [<JsonPropertyName("initial_response_excerpt")>]
    initial_response_excerpt    : string option
    [<JsonPropertyName("fallback_kind")>]
    fallback_kind               : string option
    [<JsonPropertyName("final_target")>]
    final_target                : string
    [<JsonPropertyName("final_response_excerpt")>]
    final_response_excerpt      : string
    [<JsonPropertyName("total_latency_ms")>]
    total_latency_ms            : float
    [<JsonPropertyName("timestamp")>]
    timestamp                   : DateTimeOffset
}

[<CLIMutable>]
type TraceLoggerOptions = {
    Directory       : string   // default "logs/trace"
    ChannelCapacity : int      // default 1000
}

type ITraceLogger =
    /// Append a trace record asynchronously via internal Channel. Returns immediately;
    /// background writer flushes to disk per JSONL line.
    abstract member Log : TraceRecord -> unit

/// Channel + BackgroundService single-writer mirror of DecisionLogWriter.
/// Daily file rotation by UTC date in filename; FileShare.None exclusive append.
type TraceLogger(opts: IOptions<TraceLoggerOptions>, logger: ILogger<TraceLogger>) =
    inherit BackgroundService()

    let options = opts.Value
    let directory =
        if String.IsNullOrWhiteSpace(options.Directory) then "logs/trace"
        else options.Directory
    let capacity =
        if options.ChannelCapacity <= 0 then 1000
        else options.ChannelCapacity

    let channel =
        let chanOpts = BoundedChannelOptions(capacity)
        chanOpts.FullMode <- BoundedChannelFullMode.Wait
        chanOpts.SingleReader <- true
        chanOpts.SingleWriter <- false
        Channel.CreateBounded<TraceRecord>(chanOpts)

    let jsonOpts =
        let o = JsonSerializerOptions(JsonSerializerDefaults.General)
        o.PropertyNamingPolicy <- JsonNamingPolicy.SnakeCaseLower
        o.Converters.Add(JsonFSharpConverter())
        o

    let pathForToday () =
        let date = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
        Path.Combine(directory, sprintf "%s.jsonl" date)

    let writeOne (record: TraceRecord) =
        Directory.CreateDirectory(directory) |> ignore
        let line = JsonSerializer.Serialize(record, jsonOpts)
        let path = pathForToday ()
        use stream =
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None)
        use writer = new StreamWriter(stream)
        writer.WriteLine(line)

    interface ITraceLogger with
        member _.Log(record: TraceRecord) =
            // Fire-and-forget; back-pressure via Channel.Writer.WriteAsync.
            let task = (channel.Writer.WriteAsync(record)).AsTask()
            task.GetAwaiter().GetResult()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                while not stoppingToken.IsCancellationRequested do
                    let! hasItem = channel.Reader.WaitToReadAsync(stoppingToken).AsTask()
                    if hasItem then
                        let mutable record = Unchecked.defaultof<TraceRecord>
                        while channel.Reader.TryRead(&record) do
                            try
                                writeOne record
                            with ex ->
                                logger.LogError(ex, "TraceLogger: write failed for correlation_id={Cid}", record.correlation_id)
            with
            | :? OperationCanceledException -> ()
            | :? ChannelClosedException -> ()
            | ex -> logger.LogError(ex, "TraceLogger: writer loop crashed")
        } :> Task

    override _.StopAsync(ct: CancellationToken) =
        task {
            // Drain pending records on graceful shutdown — same pattern as DecisionLogWriter.
            channel.Writer.TryComplete() |> ignore
            let mutable record = Unchecked.defaultof<TraceRecord>
            while channel.Reader.TryRead(&record) do
                try
                    writeOne record
                with ex ->
                    logger.LogWarning(ex, "TraceLogger: drain write failed")
            do! base.StopAsync(ct)
        } :> Task
```

**Step 2.** Add Compile entry to `src/SmartRouter.Cli/SmartRouter.Cli.fsproj`. Place after `DecisionLogWriter.fs` (uses similar Channel pattern):

```xml
<Compile Include="Adapters/DecisionLogWriter.fs" />
<Compile Include="Adapters/TraceLogger.fs" />     <!-- Phase 14 -->
<Compile Include="Adapters/CorrelationMiddleware.fs" />
...
```

(Exact position flexible; depends on what TraceLogger uses. It opens `Json` adapter — Json.fs must precede.)
  </action>
  <verify>
```bash
test -f src/SmartRouter.Cli/Adapters/TraceLogger.fs && echo OK
grep -c "type ITraceLogger\|type TraceLogger\|type TraceRecord" src/SmartRouter.Cli/Adapters/TraceLogger.fs
# expected: >= 3 (interface + class + record)
grep -c "BoundedChannelFullMode\.Wait" src/SmartRouter.Cli/Adapters/TraceLogger.fs
# expected: >= 1
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
```
  </verify>
</task>

<task type="auto">
  <name>Task 2: Wire `--trace-responses` flag in Program.fs</name>
  <files>src/SmartRouter.Cli/Program.fs</files>
  <action>
Edit `src/SmartRouter.Cli/Program.fs`. Add `--trace-responses` flag detection.

**Pre-condition (post-14-01 state)**: After 14-01 ships, `Logging.configure` and `applyLogLevelFromArgs` are hoisted to BEFORE the if/else split, with the cold-start block sitting between them and the if/else. The `Program.fs` `main` body looks like:

```fsharp
[<EntryPoint>]
let main args =
    try
        try
            // Bootstrap config + Serilog (post-14-01)
            let bootstrapConfig = ...
            Logging.configure(bootstrapConfig)
            applyLogLevelFromArgs args

            // Phase 14 cold-start hook (added by 14-01)
            if args |> Array.contains "--cold-start" then
                Adapters.ColdStart.runColdStartBackup ...

            // ──── INSERT applyTraceFlagFromArgs HERE (after cold-start, before if/else) ────

            if args |> Array.contains "--retrain" then
                let retrainBuilder = Host.CreateApplicationBuilder(args)
                ...
                CompositionRoot.configureWithoutMl retrainBuilder.Services retrainBuilder.Configuration |> ignore
            else
                let builder = WebApplication.CreateBuilder(args)
                ...
                CompositionRoot.configureRequestPipeline builder.Services builder.Configuration |> ignore
```

**Edit 1.** Add `applyTraceFlagFromArgs` helper at module top (alongside `applyLogLevelFromArgs` and `parsePortFromArgs`):

```fsharp
/// Phase 14 — `--trace-responses` flag detection. When the flag is present,
/// add `Trace:Enabled=true` to the per-builder configuration BEFORE
/// configureRequestPipeline / configureWithoutMl runs. CompositionRoot reads
/// this key to conditionally register ITraceLogger (triple-registration
/// pattern: concrete + interface alias + AddHostedService).
let private applyTraceFlagFromArgs (configBuilder: IConfigurationBuilder) (args: string array) : unit =
    if args |> Array.contains "--trace-responses" then
        configBuilder.AddInMemoryCollection(dict [ "Trace:Enabled", "true" ]) |> ignore
        Log.Information("Trace logging enabled via --trace-responses CLI flag; output: logs/trace/{Date}.jsonl")
```

**Edit 2.** Call `applyTraceFlagFromArgs` once per BUILDER inside each if/else arm — NOT at the bootstrap layer. Reason: the `Trace:Enabled` injection must land on the SAME `IConfigurationBuilder` instance that the per-branch host (Host.CreateApplicationBuilder OR WebApplication.CreateBuilder) uses, because `configureRequestPipeline` reads from `builder.Configuration` not from `bootstrapConfig`.

In the `--retrain` branch, after `Logging.configure(retrainBuilder.Configuration)` is called per-branch (note: 14-01 may or may not keep the per-branch Logging.configure — see below):

```fsharp
if args |> Array.contains "--retrain" then
    let retrainBuilder = Host.CreateApplicationBuilder(args)
    retrainBuilder.Configuration ... AddJsonFile(...) |> ignore
    // Phase 14 trace flag — injects into the retrain branch's IConfigurationBuilder
    applyTraceFlagFromArgs (retrainBuilder.Configuration :> IConfigurationBuilder) args
    CompositionRoot.configureWithoutMl retrainBuilder.Services retrainBuilder.Configuration |> ignore
    ...
```

In the main branch:

```fsharp
else
    let builder = WebApplication.CreateBuilder(args)
    ...
    // Phase 14 trace flag — injects into the main branch's IConfigurationBuilder
    applyTraceFlagFromArgs (builder.Configuration :> IConfigurationBuilder) args
    CompositionRoot.configureRequestPipeline builder.Services builder.Configuration |> ignore
    ...
```

**Note on 14-01's hoisting**: 14-01's "Recommended (DRY)" approach hoists `Logging.configure(bootstrapConfig)` to the pre-branch position. The PER-BRANCH `Logging.configure` calls are removed by 14-01 — there's only ONE `Logging.configure` call site after 14-01 ships. `applyTraceFlagFromArgs` does NOT depend on `Logging.configure` order; it just needs to run before `configureRequestPipeline`. So insert it immediately before each `configureXxx` call inside the if/else arms.

If 14-01 chose to keep per-branch Logging.configure (alternative inline pattern), `applyTraceFlagFromArgs` still goes between `Logging.configure(branchConfig)` and `configureXxx(branchServices, branchConfig)`. Either way, pre-`configureXxx` is the correct anchor.

**14-02 executor responsibility**: read the actual post-14-01 Program.fs state via Read tool BEFORE making the edit. The `applyTraceFlagFromArgs` insertion point is "immediately before `CompositionRoot.configureRequestPipeline` call" and "immediately before `CompositionRoot.configureWithoutMl` call" — those anchor strings are stable post-14-01.

If 14-01's hoisting inadvertently broke `Logging.configure`'s positioning, 14-02 may need to add a pre-builder Log instance for the trace-flag log line. In that case adapt `applyTraceFlagFromArgs` to use Serilog's static `Log` (which is initialized by `Logging.configure(bootstrapConfig)` from 14-01).
  </action>
  <verify>
```bash
grep -c "\\-\\-trace-responses\\|applyTraceFlagFromArgs\\|Trace:Enabled" src/SmartRouter.Cli/Program.fs
# expected: >= 3
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
```
  </verify>
</task>

<task type="auto">
  <name>Task 3: CompositionRoot — conditional ITraceLogger registration</name>
  <files>src/SmartRouter.Cli/CompositionRoot.fs</files>
  <action>
Edit `src/SmartRouter.Cli/CompositionRoot.fs` `configureRequestPipeline`. Add registration block — only when `Trace:Enabled = true`. Triple-reg pattern (DecisionLogWriter mirror): concrete + IInterface alias + AddHostedService.

Insert after DecisionLogWriter registration block (~line where DecisionLogger triple-reg lives). Pattern:

```fsharp
// Phase 14 — TraceLogger (optional; --trace-responses CLI flag enables).
let traceEnabled =
    let raw = config.["Trace:Enabled"]
    not (isNull raw) && raw.Equals("true", StringComparison.OrdinalIgnoreCase)
if traceEnabled then
    services.Configure<SmartRouter.Cli.Adapters.TraceLogger.TraceLoggerOptions>(fun (opts: SmartRouter.Cli.Adapters.TraceLogger.TraceLoggerOptions) ->
        opts.Directory       <- "logs/trace"
        opts.ChannelCapacity <- 1000) |> ignore
    services.AddSingleton<SmartRouter.Cli.Adapters.TraceLogger.TraceLogger>() |> ignore
    services.AddSingleton<SmartRouter.Cli.Adapters.TraceLogger.ITraceLogger>(
        fun sp -> sp.GetRequiredService<SmartRouter.Cli.Adapters.TraceLogger.TraceLogger>() :> SmartRouter.Cli.Adapters.TraceLogger.ITraceLogger)
        |> ignore
    services.AddHostedService<SmartRouter.Cli.Adapters.TraceLogger.TraceLogger>(
        fun sp -> sp.GetRequiredService<SmartRouter.Cli.Adapters.TraceLogger.TraceLogger>())
        |> ignore
```

Note: when `traceEnabled = false`, `ITraceLogger` is NOT registered; consumers must use `IServiceProvider.GetService<ITraceLogger>()` (returns null) instead of `GetRequiredService` (throws). 14-04 implements that consumer-side defensive check.

Add to `configureWithoutMl` IF the `--retrain` branch should also support tracing — but typical use case is request-path only, so leave `configureWithoutMl` unchanged. Document that `--trace-responses` has no effect when combined with `--retrain` (offline pipeline doesn't go through ChatCompletions).
  </action>
  <verify>
```bash
grep -c "TraceLogger\|ITraceLogger" src/SmartRouter.Cli/CompositionRoot.fs
# expected: >= 3 (concrete + interface + AddHostedService)
grep -c "Trace:Enabled\|traceEnabled" src/SmartRouter.Cli/CompositionRoot.fs
# expected: >= 1
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-restore 2>&1 | grep "EXPECTO!" | tail -1
# expected: 80 passed (no test changes)
```
  </verify>
</task>

</tasks>

<verification>
- [x] `Adapters/TraceLogger.fs` 존재; ITraceLogger + TraceLogger + TraceRecord + TraceLoggerOptions
- [x] Channel + BackgroundService 단일 writer (BoundedChannelFullMode.Wait)
- [x] Program.fs `--trace-responses` flag 인지 + IConfiguration 에 Trace:Enabled=true 주입
- [x] CompositionRoot `Trace:Enabled` 조건부 ITraceLogger 등록 (triple-reg)
- [x] 빌드 clean; 기존 80 테스트 통과
- [x] consumer-side (ChatCompletions) 통합은 14-04 이 처리
</verification>
