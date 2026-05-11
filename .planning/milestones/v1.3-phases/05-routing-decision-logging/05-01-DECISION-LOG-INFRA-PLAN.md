---
phase: 05-routing-decision-logging
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/SmartRouter.Cli/Adapters/DecisionLogger.fs
  - src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs
  - src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs
  - src/SmartRouter.Cli/CompositionRoot.fs
  - src/SmartRouter.Cli/Program.fs
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - src/SmartRouter.Cli/appsettings.json
  - .gitignore
autonomous: true

must_haves:
  truths:
    - "A bounded Channel<DecisionLog> exists and is owned by a BackgroundService that drains it; producers never block the request hot path"
    - "Correlation ID middleware runs FIRST in the ASP.NET pipeline, generates Guid.NewGuid().ToString(\"N\") per request, stores it in HttpContext.Items[\"CorrelationId\"], and pushes it onto Serilog LogContext so all stderr Serilog lines for the request carry correlation_id"
    - "The 12-field DecisionLog record exists with snake_case JSON serialization (PropertyNamingPolicy.SnakeCaseLower) and FSharp.SystemTextJson handles string option correctly"
    - "logs/decisions/ directory is created at startup; .gitignore includes logs/ so log files are never committed"
    - "Daily UTC rotation: writer reopens the file when DateTimeOffset.UtcNow.Date changes between entries"
    - "Graceful shutdown drains: BackgroundService.StopAsync calls channel.Writer.TryComplete then awaits drain; remaining items are flushed before file close"
    - "Channel-full path: BoundedChannelFullMode.DropWrite + Serilog warning to stderr ('decision log channel full; dropped 1 decision') — operator-visible, never silent"
    - "File.AppendAllText is forbidden anywhere in src/SmartRouter.Cli/ — single-writer Channel pattern is the only allowed write path"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/DecisionLogger.fs"
      provides: "DecisionLog record (12 fields), IDecisionLogger interface, computePromptHash (SHA-256), computeKoreanRatio ([가-힣] regex), formatReason (no F# DU %A reflection)"
      contains: "schema_version"
    - path: "src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs"
      provides: "BackgroundService Channel consumer with daily UTC rotation, DropWrite-with-warning, graceful shutdown drain"
      contains: "BackgroundService"
    - path: "src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs"
      provides: "ASP.NET middleware lambda that injects correlation_id per request"
      contains: "LogContext.PushProperty"
    - path: "src/SmartRouter.Cli/CompositionRoot.fs"
      provides: "DecisionLogWriter registered as concrete singleton + IDecisionLogger alias + IHostedService — same instance for all three"
      contains: "AddHostedService"
    - path: "src/SmartRouter.Cli/Program.fs"
      provides: "Correlation middleware registered FIRST in pipeline (before UseSerilogRequestLogging); logs/decisions/ directory ensured at startup"
      contains: "correlationMiddleware"
    - path: "src/SmartRouter.Cli/appsettings.json"
      provides: "DecisionLog section with Directory + ChannelCapacity defaults"
      contains: "DecisionLog"
    - path: ".gitignore"
      provides: "logs/ entry so JSONL files are never committed"
      contains: "logs/"
  key_links:
    - from: "Program.fs middleware pipeline"
      to: "CorrelationMiddleware.correlationMiddleware"
      via: "app.Use registered BEFORE app.UseSerilogRequestLogging"
      pattern: "app\\.Use.*correlationMiddleware|correlationMiddleware.*app\\.Use"
    - from: "DecisionLogWriter.ExecuteAsync"
      to: "channel.Reader.ReadAsync(stoppingToken)"
      via: "BackgroundService consumer loop with cancellation drain"
      pattern: "channel\\.Reader\\.ReadAsync"
    - from: "CompositionRoot.configureServices"
      to: "AddHostedService<DecisionLogWriter>"
      via: "same singleton instance also registered as IDecisionLogger"
      pattern: "AddHostedService"
---

<objective>
Land the JSONL decision-logging infrastructure: 12-field DecisionLog record, IDecisionLogger interface, Channel-backed BackgroundService writer with daily UTC rotation, and correlation-ID middleware. This plan ships only the plumbing; Plan 05-02 wires the endpoint to actually emit logs and Plan 05-03 verifies behavior end-to-end.

Purpose: Phase 5 is Loop B's input. Every later phase (6 ML routing → 9 canary cohort comparison) depends on this JSONL stream existing and being correct on day one. Wave 1 is the foundation everything else attaches to.

Output:
- DecisionLogger.fs (record + interface + helpers)
- DecisionLogWriter.fs (BackgroundService consumer)
- CorrelationMiddleware.fs (lambda middleware)
- Updated CompositionRoot.fs (DI registration; KEEP existing RoutingAlgorithm registration as-is — Plan 05-02 refactors it into RoutingAlgorithmRegistration)
- Updated Program.fs (middleware FIRST in pipeline, logs dir at startup)
- Updated appsettings.json (DecisionLog section)
- Updated .gitignore (logs/)
- Updated SmartRouter.Cli.fsproj (3 new <Compile> entries in correct order)
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md
@.planning/phases/05-routing-decision-logging/05-CONTEXT.md
@.planning/phases/05-routing-decision-logging/05-RESEARCH.md
@src/SmartRouter.Cli/Adapters/Logging.fs
@src/SmartRouter.Cli/CompositionRoot.fs
@src/SmartRouter.Cli/Program.fs
@src/SmartRouter.Cli/SmartRouter.Cli.fsproj
@src/SmartRouter.Cli/appsettings.json
</context>

<tasks>

<task type="auto">
  <name>Task 1: DecisionLogger.fs (record + interface + helpers) and CorrelationMiddleware.fs</name>
  <files>
    src/SmartRouter.Cli/Adapters/DecisionLogger.fs
    src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs
  </files>
  <action>
Create `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` (module `SmartRouter.Cli.Adapters.DecisionLogger`):

1. Imports: `System`, `System.Security.Cryptography`, `System.Text`, `SmartRouter.Core.Domain`.

2. `let computePromptHash (messages: Message list) : string` — concat `m.Content` for every message (full conversation, matches what heuristic scores), UTF-8 encode, `use sha = SHA256.Create()`, `sha.ComputeHash(bytes)`, hex-encode lowercase. Return 64-char string. (Pitfall P8: `use sha` per call — SHA256 is not thread-safe.)

3. `let computeKoreanRatio (messages: Message list) : float` — concat all `m.Content`. If empty, return 0.0. Otherwise count chars where `c >= '가' && c <= '힣'` (Hangul Syllables block — does NOT include Jamo) divided by total char count. Use `Seq.filter` + `Seq.length`.

4. `let formatReason (reason: RoutingReason) : string` — pattern match on `RoutingReason` DU (cases: `ExplicitModelOverride`, `ExplicitTask`, `Heuristic`, `Default`, `ML`). Hand-rolled, NOT `sprintf "%A"` (avoid F# reflection strings like `"Heuristic 3"`):
   - `ExplicitModelOverride alias -> sprintf "explicit_model:%s" alias`
   - `ExplicitTask taskType -> sprintf "explicit_task:%A" taskType` (TaskType DU printing is acceptable here — only RoutingReason's outer cases matter)
   - `Heuristic score -> sprintf "heuristic:score=%d" score`
   - `Default -> "default"`
   - `ML -> "ml"`

   Confirm exact RoutingReason DU shape from `src/SmartRouter.Core/Domain.fs` line 31 onward before writing the match — if the DU has different field shapes, mirror them exactly. (RouterTests.fs already pattern matches on these cases; reuse the shape.)

5. `[<CLIMutable>] type DecisionLog` record — EXACTLY 12 fields in this order with these snake_case-friendly F# names (the JsonNamingPolicy.SnakeCaseLower in DecisionLogWriter handles PascalCase→snake_case conversion at serialize time, but we use lowercase F# names here matching CONTEXT.md so the wire output is unambiguous):
   ```fsharp
   [<CLIMutable>]
   type DecisionLog =
       { schema_version           : int
         correlation_id           : string
         prompt_hash              : string
         prompt_korean_char_ratio : float
         routing_algorithm        : string
         routing_reason           : string
         target                   : string
         latency_ms               : float
         fallback_used            : bool
         model_version            : string
         task_type                : string option
         timestamp                : DateTimeOffset }
   ```

6. `type IDecisionLogger` interface with single abstract member: `Log : DecisionLog -> unit`. Fire-and-forget; producers never block.

Create `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` (module `SmartRouter.Cli.Adapters.CorrelationMiddleware`):

1. Imports: `System`, `System.Threading.Tasks`, `Microsoft.AspNetCore.Http`, `Serilog.Context`.

2. `[<Literal>] let CorrelationIdKey = "CorrelationId"` — string key for `HttpContext.Items`.

3. Async middleware function suitable for `app.Use`. **Function name must be `correlationMiddleware`** so the must_haves grep in Task 3 (and Plan-level verification) passes:
   ```fsharp
   let correlationMiddleware (ctx: HttpContext) (next: RequestDelegate) : Task =
       task {
           let cid = Guid.NewGuid().ToString("N")           // 32-char hex, no dashes
           ctx.Items.[CorrelationIdKey] <- cid
           use _ = LogContext.PushProperty("correlation_id", cid)  // (Pitfall P5: use _ for disposal scope)
           do! next.Invoke(ctx)
       }
   ```
   (The exact F# call site for `app.Use` in Program.fs is shown in Task 3, which calls `CorrelationMiddleware.correlationMiddleware`.)

DO NOT use `IMiddleware` — the lambda form is simpler and avoids DI scope/lifetime concerns for stateless middleware.
  </action>
  <verify>
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj -nologo` compiles after Task 2 wires the .fsproj — Task 1 alone will not compile until .fsproj is updated; that ordering is intentional.
- After full plan: `dotnet build SmartRouter.slnx -nologo --tl:off` succeeds with 0 warnings (TreatWarningsAsErrors=true).
- `grep -RIn "FSharp.SystemTextJson\|JsonNamingPolicy" src/SmartRouter.Cli/Adapters/DecisionLogger.fs` returns NOTHING (record file is BCL-only; serialization options live in the writer).
- `grep -RIn "computePromptHash\|computeKoreanRatio\|formatReason" src/SmartRouter.Cli/Adapters/DecisionLogger.fs` returns 3 hits — one per function.
  </verify>
  <done>
DecisionLogger.fs and CorrelationMiddleware.fs exist with the 12-field record, IDecisionLogger interface, three pure helpers, and stateless ASP.NET middleware lambda. The 12 fields exactly match Phase 5 CONTEXT.md schema (schema_version + prompt_korean_char_ratio included from day one).
  </done>
</task>

<task type="auto">
  <name>Task 2: DecisionLogWriter.fs (BackgroundService) + .fsproj wiring</name>
  <files>
    src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs
    src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  </files>
  <action>
Create `src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs` (module `SmartRouter.Cli.Adapters.DecisionLogWriter`).

Imports:
```
System
System.IO
System.Text
System.Text.Json
System.Threading
System.Threading.Channels
Microsoft.Extensions.Hosting
Serilog
SmartRouter.Cli.Adapters.DecisionLogger
```

Define:

1. `[<CLIMutable>] type DecisionLogOptions = { Directory: string; ChannelCapacity: int }` — bound from appsettings.json `"DecisionLog"` section in Task 3. Defaults applied at registration time: Directory="logs/decisions", ChannelCapacity=10000.

2. `type DecisionLogWriter(options: DecisionLogOptions)` inheriting `BackgroundService`:

   - Construct a bounded channel:
     ```fsharp
     let channel =
         Channel.CreateBounded<DecisionLog>(
             BoundedChannelOptions(options.ChannelCapacity,
                 FullMode = BoundedChannelFullMode.DropWrite,
                 SingleWriter = false,   // many endpoint tasks produce
                 SingleReader = true))   // only this BackgroundService consumes
     ```
     **DropWrite (per CONTEXT.md), not DropOldest** — operator-visible warning fires when TryWrite fails (newest dropped is more meaningful than oldest).

   - Build per-instance `JsonSerializerOptions`:
     ```fsharp
     let jsonOpts =
         let o = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
         o.Converters.Add(FSharp.SystemTextJson.JsonFSharpConverter())
         o
     ```
     The FSharp.SystemTextJson converter is required for `string option` → `null` serialization (Pitfall P10).

   - Public method `member _.Enqueue(entry: DecisionLog) : unit`:
     ```fsharp
     if not (channel.Writer.TryWrite(entry)) then
         Log.Warning("decision log channel full; dropped 1 decision; correlation_id={CorrelationId}",
                     entry.correlation_id)
     ```
     Operator-visible signal on overflow; no exception.

   - `interface IDecisionLogger with member this.Log(entry) = this.Enqueue(entry)` — same instance fronts as IDecisionLogger.

   - `override this.ExecuteAsync(stoppingToken: CancellationToken) : Task = task { ... }`:
     - `Directory.CreateDirectory(options.Directory) |> ignore` — idempotent.
     - Mutable state: `let mutable currentDate = DateTime.MinValue` and `let mutable writer : StreamWriter option = None`.
     - Helper `let openWriter (date: DateTime) =` closes prior writer (if any), opens new `StreamWriter(path, append=true, encoding=Encoding.UTF8)`, sets `AutoFlush <- false`, returns it. Path is `Path.Combine(options.Directory, date.ToString("yyyy-MM-dd") + ".jsonl")`.
     - Main loop:
       ```fsharp
       try
           while not stoppingToken.IsCancellationRequested do
               let! entry = channel.Reader.ReadAsync(stoppingToken)
               let today = DateTime.UtcNow.Date
               let sw =
                   if today <> currentDate then openWriter today
                   else
                       match writer with
                       | Some w -> w
                       | None -> openWriter today
               try
                   let line = JsonSerializer.Serialize(entry, jsonOpts)
                   sw.WriteLine(line)
                   sw.Flush()  // per-line OS write; atomic for short lines under PIPE_BUF
               with ex ->
                   Log.Error(ex, "DecisionLogWriter: write failed for correlation_id={Cid}", entry.correlation_id)
       with
       | :? OperationCanceledException -> ()  // graceful shutdown signal
       | ex -> Log.Error(ex, "DecisionLogWriter: writer loop crashed")
       ```
     - **Drain remaining items after cancellation** (Pitfall P2) — read with `TryRead` until empty:
       ```fsharp
       let mutable more = true
       while more do
           match channel.Reader.TryRead() with
           | true, entry ->
               try
                   let today = DateTime.UtcNow.Date
                   let sw =
                       if today <> currentDate then openWriter today
                       else match writer with Some w -> w | None -> openWriter today
                   sw.WriteLine(JsonSerializer.Serialize(entry, jsonOpts))
                   sw.Flush()
               with ex -> Log.Warning(ex, "DecisionLogWriter: drain write failed")
           | false, _ -> more <- false
       writer |> Option.iter (fun w -> try w.Flush() with _ -> (); w.Dispose())
       ```

   - `override _.StopAsync(cancellationToken: CancellationToken) : Task =`:
     ```fsharp
     channel.Writer.TryComplete() |> ignore  // unblocks ReadAsync after drain
     base.StopAsync(cancellationToken)
     ```

3. **CRITICAL**: `File.AppendAllText` MUST NOT appear anywhere in src/SmartRouter.Cli/. Verify with grep below.

Update `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` `<ItemGroup>` Compile section. **Compile order matters in F#** — DecisionLogger.fs must precede DecisionLogWriter.fs which must precede ChatCompletions.fs. Insert these three lines AFTER `Adapters/Logging.fs` and BEFORE `Adapters/QwenUpstreamClient.fs`:

```xml
<Compile Include="Adapters/DecisionLogger.fs" />
<Compile Include="Adapters/DecisionLogWriter.fs" />
<Compile Include="Adapters/CorrelationMiddleware.fs" />
```

CorrelationMiddleware has no dependency on DecisionLogger/Writer; either order works for it but keeping it after Writer is consistent with the research file structure.
  </action>
  <verify>
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj -nologo --tl:off` succeeds with 0 warnings.
- `grep -RIn "File\.AppendAllText" src/` returns NOTHING (forbidden writer pattern). If it appears anywhere, the build is wrong.
- `grep -n "Compile Include" src/SmartRouter.Cli/SmartRouter.Cli.fsproj` shows DecisionLogger.fs BEFORE DecisionLogWriter.fs BEFORE ChatCompletions.fs.
- `grep -n "BoundedChannelFullMode\.DropWrite" src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs` returns 1 hit (DropWrite, not DropOldest).
- `grep -n "channel\.Writer\.TryComplete" src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs` returns 1 hit (graceful shutdown signal).
- `grep -n "channel\.Reader\.TryRead" src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs` returns at least 1 hit (drain after cancel).
  </verify>
  <done>
DecisionLogWriter.fs implements a single-writer Channel-consuming BackgroundService with daily UTC rotation, DropWrite back-pressure with operator-visible Serilog warning, graceful shutdown drain, and per-line explicit Flush. .fsproj has the 3 new files in correct compile order. `dotnet build` succeeds with 0 warnings; `File.AppendAllText` does not appear anywhere in src/.
  </done>
</task>

<task type="auto">
  <name>Task 3: DI registration + middleware + appsettings + .gitignore</name>
  <files>
    src/SmartRouter.Cli/CompositionRoot.fs
    src/SmartRouter.Cli/Program.fs
    src/SmartRouter.Cli/appsettings.json
    .gitignore
  </files>
  <action>
**Step 1 — `src/SmartRouter.Cli/appsettings.json`:** Add a `"DecisionLog"` section as a new top-level key (place it after `"Queue"` and before `"Serilog"` for readability):

```json
"DecisionLog": {
  "Directory": "logs/decisions",
  "ChannelCapacity": 10000
},
```

**Step 2 — `.gitignore`:** Append:

```
# Decision logs — contain hashed prompt data; do not commit
logs/
```

**Step 3 — `src/SmartRouter.Cli/CompositionRoot.fs`:** Add registrations. Open the new module at the top: `open SmartRouter.Cli.Adapters.DecisionLogger` and `open SmartRouter.Cli.Adapters.DecisionLogWriter`.

Inside `configureServices`, AFTER the existing `RoutingConfig` registration block and BEFORE the `Queue` section binding (or AFTER `IStatsProvider` — anywhere in the function body works since DI registration order is irrelevant for singletons), add:

```fsharp
// Bind DecisionLog options
services.Configure<DecisionLogOptions>(config.GetSection("DecisionLog")) |> ignore

// DecisionLogWriter — concrete singleton; same instance exposed as IDecisionLogger AND IHostedService
services.AddSingleton<DecisionLogWriter>(fun sp ->
    let opts = sp.GetRequiredService<IOptions<DecisionLogOptions>>().Value
    // Defensive defaults if config keys absent / blank
    let dir = if String.IsNullOrWhiteSpace(opts.Directory) then "logs/decisions" else opts.Directory
    let cap = if opts.ChannelCapacity <= 0 then 10000 else opts.ChannelCapacity
    DecisionLogWriter({ Directory = dir; ChannelCapacity = cap }))
|> ignore

services.AddSingleton<IDecisionLogger>(fun sp ->
    sp.GetRequiredService<DecisionLogWriter>() :> IDecisionLogger)
|> ignore

services.AddHostedService<DecisionLogWriter>(fun sp ->
    sp.GetRequiredService<DecisionLogWriter>())
|> ignore
```

The three-line registration is load-bearing: AddSingleton creates the instance, the IDecisionLogger registration aliases the same instance for endpoint injection, and AddHostedService aliases the same instance to the host's IHostedService collection so StartAsync/StopAsync run on it. **DO NOT** use three separate `AddSingleton<DecisionLogWriter>` calls — that creates three instances.

**DO NOT change the existing `RoutingAlgorithm` registration in this plan** — Plan 05-02 will refactor that into `RoutingAlgorithmRegistration` with paired Name + ModelVersion. Keeping that change scoped to 05-02 keeps this plan's blast radius minimal.

**Step 4 — `src/SmartRouter.Cli/Program.fs`:**

Add `open SmartRouter.Cli.Adapters.CorrelationMiddleware` at the top (alongside existing `open SmartRouter.Cli.Adapters`).

After `let app = builder.Build()` and BEFORE `app.UseSerilogRequestLogging() |> ignore` (so correlation_id is in scope when Serilog request-logging fires), add:

```fsharp
// Ensure logs directory exists at startup so DecisionLogWriter never races on first write
System.IO.Directory.CreateDirectory("logs/decisions") |> ignore

// Correlation ID middleware — runs FIRST in the pipeline so every downstream
// log line and the JSONL DecisionLog entry carry the same correlation_id.
app.Use(fun (ctx: HttpContext) (next: RequestDelegate) ->
    CorrelationMiddleware.correlationMiddleware ctx next) |> ignore
```

You'll need `open Microsoft.AspNetCore.Http` if it's not already imported. The exact lambda shape `Func<HttpContext, RequestDelegate, Task>` may need an explicit cast — if the F# compiler complains, wrap with `Func<HttpContext, RequestDelegate, Task>`:

```fsharp
app.Use(System.Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
    CorrelationMiddleware.correlationMiddleware ctx next)) |> ignore
```

The middleware MUST be registered before `app.UseSerilogRequestLogging()` so Serilog's per-request log carries `correlation_id` from the LogContext property.
  </action>
  <verify>
- `dotnet build SmartRouter.slnx -nologo --tl:off` succeeds with 0 warnings.
- `dotnet run --project src/SmartRouter.Cli -- --help 2>&1` (or just startup-then-Ctrl-C) does not crash; logs/decisions/ directory is created.
- `grep -n "logs/" .gitignore` returns 1 hit.
- `git check-ignore logs/decisions/test.jsonl` returns logs/decisions/test.jsonl (exit 0) — confirms .gitignore actually ignores the path.
- `grep -n "AddHostedService<DecisionLogWriter>" src/SmartRouter.Cli/CompositionRoot.fs` returns 1 hit.
- `grep -nB1 "UseSerilogRequestLogging" src/SmartRouter.Cli/Program.fs` shows the correlation middleware Use call appearing on a line BEFORE UseSerilogRequestLogging.
- `grep -n "DecisionLog" src/SmartRouter.Cli/appsettings.json` returns at least 3 hits (section header + Directory + ChannelCapacity).
  </verify>
  <done>
DI wires DecisionLogWriter as one instance fronting IDecisionLogger and IHostedService. Correlation middleware is FIRST in the pipeline. logs/decisions/ exists at startup. .gitignore prevents log files from being committed. appsettings.json has the DecisionLog section. Build is clean (0 warnings, TreatWarningsAsErrors=true).
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

2. **Existing tests still pass (no logging-side ripple yet — endpoint not wired):**
   ```bash
   dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off
   ```
   Expected: 44 passed (same as Phase 4 baseline). The new DI registrations are inert from existing tests' perspective because no test resolves IDecisionLogger yet.

   **MLRoutingTests Tests 4+5 use `configureServices` directly with `AddInMemoryCollection` over the bin-copied appsettings.json. They will resolve a real DecisionLogWriter** (since CompositionRoot now registers it). The DecisionLogWriter's StartAsync only fires when AddHostedService is connected to a real host — these tests build a ServiceCollection directly without IHost, so `AddHostedService` registration is registered but never started. The constructor takes only DecisionLogOptions, runs no I/O. Verified non-breaking.

   **StreamingTests `startTestRouter` uses `WebApplication.CreateBuilder`** which DOES start hosted services on `app.StartAsync()`. DecisionLogWriter will start; it will create `logs/decisions/` in the test's CWD (typically project bin). This creates real log files during streaming tests but does not break them — `Enqueue` is fire-and-forget and no test asserts no log files exist. Plan 05-03 will isolate test directories properly. For now: streaming tests pass.

3. **Forbidden patterns check:**
   ```bash
   grep -RIn "File\.AppendAllText" src/ tests/
   ```
   Expected: NO output.

4. **Correlation middleware is registered FIRST:**
   ```bash
   grep -nE "(app\.Use|UseSerilogRequestLogging|mapEndpoints)" src/SmartRouter.Cli/Program.fs
   ```
   Expected: `app.Use(... correlationMiddleware ...)` line number is LESS THAN `UseSerilogRequestLogging` line number.

5. **DI wiring uses the same instance for all three roles:**
   ```bash
   grep -nE "(AddSingleton<DecisionLogWriter>|AddSingleton<IDecisionLogger>|AddHostedService<DecisionLogWriter>)" src/SmartRouter.Cli/CompositionRoot.fs
   ```
   Expected: 3 distinct lines; the latter two use `sp.GetRequiredService<DecisionLogWriter>()`.

6. **Smoke run — startup creates the log directory:**
   ```bash
   rm -rf src/SmartRouter.Cli/logs/
   timeout 3 dotnet run --project src/SmartRouter.Cli || true
   ls src/SmartRouter.Cli/logs/decisions/
   ```
   Expected: `logs/decisions/` exists (may be empty since no requests served).
</verification>

<success_criteria>
- DecisionLog record + IDecisionLogger interface compile (Task 1)
- DecisionLogWriter compiles and is wired into the .fsproj before ChatCompletions.fs (Task 2)
- File.AppendAllText does not exist anywhere in src/ (Task 2)
- Correlation middleware registered FIRST in pipeline (Task 3)
- logs/ in .gitignore (Task 3)
- All 44 existing tests still pass (no regression)
- 0 build warnings (TreatWarningsAsErrors=true)
</success_criteria>

<output>
After completion, create `.planning/phases/05-routing-decision-logging/05-01-SUMMARY.md` listing the 7 files modified, any deviations from the plan, and the test count delta (expected: 44 → 44, no test changes in this plan).
</output>

## Existing-test impact

| Test file | Tests | Needs change in 05-01? | Why / Why not |
|-----------|-------|------------------------|---------------|
| RoutingTests.fs | 22 | NO | Pure routing tests; no DI; no app startup. |
| MLRoutingTests.fs | 5 | NO | Tests 4+5 use `configureServices` against ServiceCollection (no IHost) — DecisionLogWriter ctor runs no I/O so registration is inert. |
| StreamingTests.fs | 8 | NO (in 05-01) | Uses WebApplication so DecisionLogWriter STARTS in-process and writes to logs/ in test bin. Functionally non-breaking; Plan 05-03 will isolate test dirs. |
| QueueTests.fs | 9 | NO | Tests build their own DI mock without configureServices — see QueueDispatcher tests; do not depend on DecisionLogWriter. |
| LoadTests.fs | 2 (pending) | NO | Same as QueueTests; pending by default. |

## REQ-ID coverage in this plan

- **OBS-03** (correlation ID through logs): correlation middleware + Serilog LogContext.PushProperty + HttpContext.Items — infrastructure landed, propagation to JSONL happens in Plan 05-02.
- **LOG-02** (thread-safe single-writer Channel; File.AppendAllText forbidden): DecisionLogWriter Channel + BackgroundService; verify-grep blocks regression.
- **LOG-03** (daily rotation + graceful shutdown flush): UTC date check per entry + StopAsync drain.
- **OBS-01, LOG-01, LOG-04** are infrastructure-supported here but verified by Plan 05-02 (endpoint wiring) and Plan 05-03 (tests).
