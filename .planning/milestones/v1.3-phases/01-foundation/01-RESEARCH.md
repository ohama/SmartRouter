# Phase 1: Foundation — Research

**Researched:** 2026-05-07
**Domain:** F# .NET 10 project scaffold + Core domain types + non-streaming HTTP client
**Confidence:** HIGH

---

## Summary

Phase 1 is a three-plan scaffold-and-wire phase: stand up the solution structure, implement the pure Core domain, and get one real upstream HTTP call working end-to-end for the non-streaming path. Everything in this phase is foundational — decisions made here (Kestrel binding, heuristic shape, task table semantics, model alias set, Expecto `rootTests` pattern, `check-no-async.sh`) are expensive to retrofit later.

The research base is strong: blueCode provides nearly all implementation-ready code for the adapter layer, and the project research documents (ARCHITECTURE.md, PITFALLS.md, STACK.md) already contain concrete F# type signatures. The main gaps this document fills are (a) live-verified NuGet package versions for the 5 MEDIUM-confidence packages, (b) concrete answers to plan-level open questions (heuristic shape, task table semantics, model alias set, `appsettings.json` structure), and (c) the exact `dotnet new` commands and `.slnx` vs `.sln` decision.

**Primary recommendation:** Scaffold with `.slnx` (blueCode uses it), use `FsToolkit.ErrorHandling 5.2.0` (already in blueCode Core), and implement the heuristic as score-based with threshold=3. All five previously-MEDIUM-confidence packages are now pinned to verified live versions below.

---

## Standard Stack

### Core (all HIGH confidence — live verified or blueCode-locked)

| Library | Version | Purpose | Confidence |
|---------|---------|---------|------------|
| `FSharp.SystemTextJson` | 1.4.36 | F# DU/option/list round-trip via STJ | HIGH — blueCode `.fsproj` |
| `Serilog` | 4.3.1 | Structured logging core | HIGH — blueCode `.fsproj` |
| `Serilog.Sinks.Console` | 6.1.1 | stderr sink | HIGH — blueCode `.fsproj` |
| `Serilog.AspNetCore` | **10.0.0** | ASP.NET Core request logging integration | HIGH — live NuGet (was 9.0.0 MEDIUM) |
| `FsToolkit.ErrorHandling` | **5.2.0** | `result {}` / `taskResult {}` CE | HIGH — live NuGet + blueCode Core.fsproj |
| `Expecto` | 10.2.1 | Test framework | HIGH — blueCode Tests.fsproj |
| `Microsoft.Extensions.Http.Resilience` | **10.5.0** | Retry pipeline; replaces deprecated Polly direct | HIGH — live NuGet (was 9.4.0 MEDIUM) |
| `FSharp.Control.TaskSeq` | **1.1.1** | `IAsyncEnumerable` for SSE iteration (Phase 2) | HIGH — live NuGet (was 0.4.3 MEDIUM) |
| `Microsoft.AspNetCore.Mvc.Testing` | **10.0.7** | TestServer + integration tests | HIGH — live NuGet (was 10.0.0 MEDIUM) |

**Note:** `FsToolkit.ErrorHandling` 5.2.0 is already the version in blueCode's `BlueCode.Core.fsproj` — no version divergence. The `FSharp.Control.TaskSeq` version jumped from the 0.4.x series to 1.1.1 — this is a major version bump but the API is stable; the package was renamed/promoted.

### Version-Pinned Package References

**`SmartRouter.Core.fsproj`** — zero NuGet packages except FsToolkit:
```xml
<PackageReference Include="FsToolkit.ErrorHandling" Version="5.2.0" />
```

**`SmartRouter.Cli.fsproj`**:
```xml
<PackageReference Include="FSharp.SystemTextJson" Version="1.4.36" />
<PackageReference Include="Serilog" Version="4.3.1" />
<PackageReference Include="Serilog.Sinks.Console" Version="6.1.1" />
<PackageReference Include="Serilog.AspNetCore" Version="10.0.0" />
<PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="10.5.0" />
<PackageReference Include="FsToolkit.ErrorHandling" Version="5.2.0" />
<PackageReference Include="FSharp.Control.TaskSeq" Version="1.1.1" />
```

**`SmartRouter.Tests.fsproj`**:
```xml
<PackageReference Include="Expecto" Version="10.2.1" />
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.7" />
```

---

## Architecture Patterns

### Recommended Solution Layout

Use `.slnx` (not `.sln`) — blueCode uses `BlueCode.slnx` and the project constraint mirrors blueCode. `.slnx` is the modern XML solution format introduced in .NET 8+.

```
smart-router/
├── SmartRouter.slnx
├── global.json                   # SDK pin: 10.0.100
├── scripts/
│   └── check-no-async.sh
├── src/
│   ├── SmartRouter.Core/
│   │   ├── SmartRouter.Core.fsproj
│   │   ├── Domain.fs
│   │   ├── Routing.fs
│   │   └── Ports.fs
│   └── SmartRouter.Cli/
│       ├── SmartRouter.Cli.fsproj
│       ├── Program.fs
│       ├── CompositionRoot.fs
│       ├── Adapters/
│       │   ├── Json.fs            # copy verbatim from blueCode
│       │   ├── Logging.fs         # copy verbatim from blueCode
│       │   └── QwenUpstreamClient.fs  # adapted from blueCode QwenHttpClient.fs
│       └── Endpoints/
│           └── ChatCompletions.fs # Phase 1: non-streaming only
└── tests/
    └── SmartRouter.Tests/
        ├── SmartRouter.Tests.fsproj
        ├── RouterTests.fs         # [<EntryPoint>] + explicit rootTests list
        └── RoutingTests.fs        # pure routing pipeline tests
```

### Scaffold Commands

```bash
# From /Users/ohama/projs/smart-router/
dotnet new sln -n SmartRouter          # creates SmartRouter.sln initially
# Then rename to .slnx OR use:
dotnet new slnx -n SmartRouter         # .slnx directly (available in .NET 8+ SDK)

# Core project (class library, no web SDK)
dotnet new classlib --language F# -n SmartRouter.Core -o src/SmartRouter.Core

# Cli project (web SDK for ASP.NET Minimal API)
dotnet new web --language F# -n SmartRouter.Cli -o src/SmartRouter.Cli

# Tests project (console app — Expecto uses EntryPoint)
dotnet new console --language F# -n SmartRouter.Tests -o tests/SmartRouter.Tests

# Add to solution
dotnet sln SmartRouter.slnx add src/SmartRouter.Core/SmartRouter.Core.fsproj
dotnet sln SmartRouter.slnx add src/SmartRouter.Cli/SmartRouter.Cli.fsproj
dotnet sln SmartRouter.slnx add tests/SmartRouter.Tests/SmartRouter.Tests.fsproj

# Add project references
dotnet add src/SmartRouter.Cli reference src/SmartRouter.Core/SmartRouter.Core.fsproj
dotnet add tests/SmartRouter.Tests reference src/SmartRouter.Core/SmartRouter.Core.fsproj
dotnet add tests/SmartRouter.Tests reference src/SmartRouter.Cli/SmartRouter.Cli.fsproj
```

**`global.json`** — match blueCode exactly:
```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  }
}
```

**`SmartRouter.slnx`** shape (after creation + adds):
```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/SmartRouter.Core/SmartRouter.Core.fsproj" />
    <Project Path="src/SmartRouter.Cli/SmartRouter.Cli.fsproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/SmartRouter.Tests/SmartRouter.Tests.fsproj" />
  </Folder>
</Solution>
```

### `.fsproj` Compile Order (load-bearing)

F# compiles top-to-bottom. Each file may only reference types/modules defined in earlier files.

**`SmartRouter.Core.fsproj`**:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Domain.fs" />
    <Compile Include="Routing.fs" />
    <Compile Include="Ports.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="FsToolkit.ErrorHandling" Version="5.2.0" />
  </ItemGroup>
</Project>
```

**`SmartRouter.Cli.fsproj`**:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>SmartRouter</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <!-- Adapters (Infrastructure) — must precede Endpoints and Program -->
    <Compile Include="Adapters/Json.fs" />
    <Compile Include="Adapters/Logging.fs" />
    <Compile Include="Adapters/QwenUpstreamClient.fs" />
    <!-- Endpoints — depend on adapters -->
    <Compile Include="Endpoints/ChatCompletions.fs" />
    <!-- Composition + Entry Point — must be last -->
    <Compile Include="CompositionRoot.fs" />
    <Compile Include="Program.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="FSharp.SystemTextJson" Version="1.4.36" />
    <PackageReference Include="Serilog" Version="4.3.1" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.1.1" />
    <PackageReference Include="Serilog.AspNetCore" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="10.5.0" />
    <PackageReference Include="FsToolkit.ErrorHandling" Version="5.2.0" />
    <PackageReference Include="FSharp.Control.TaskSeq" Version="1.1.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\SmartRouter.Core\SmartRouter.Core.fsproj" />
  </ItemGroup>
</Project>
```

**`SmartRouter.Tests.fsproj`**:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- Test modules BEFORE entry point -->
    <Compile Include="RoutingTests.fs" />
    <!-- Entry point LAST -->
    <Compile Include="RouterTests.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Expecto" Version="10.2.1" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.7" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\SmartRouter.Core\SmartRouter.Core.fsproj" />
  </ItemGroup>
</Project>
```

---

## Focus Area Answers

### 1. Solution Layout — Concrete Answers

- **File format:** `.slnx` (blueCode uses `BlueCode.slnx`; this is the modern format)
- **`dotnet new slnx`** creates the file directly; `dotnet sln` commands work on both `.sln` and `.slnx`
- **Three projects:** Core (classlib), Cli (web), Tests (console/Exe)
- **Source layout:** `src/` for Core + Cli, `tests/` for Tests — mirrors blueCode

### 2. NuGet Package Versions — Live Verified

All 5 previously-MEDIUM packages are now HIGH confidence after live `dotnet package search`:

| Package | Old MEDIUM estimate | Live Verified Version |
|---------|--------------------|-----------------------|
| `Serilog.AspNetCore` | 9.0.0 | **10.0.0** |
| `Microsoft.Extensions.Http.Resilience` | 9.4.0 | **10.5.0** |
| `FsToolkit.ErrorHandling` | 4.19.0 | **5.2.0** (also confirmed in blueCode Core) |
| `FSharp.Control.TaskSeq` | 0.4.3 | **1.1.1** |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.0 | **10.0.7** |

### 3. Routing Pipeline Implementation

The concrete F# signatures are already specified in ARCHITECTURE.md. Here is the implementation-ready summary:

**`Domain.fs`** — All DUs (exhaustive, no `| _ ->`):
```fsharp
type ModelId = Qwen35B | Qwen122B

type Priority = High | Low

type TaskType =
    | GraphIndexing
    | CompilerDebug
    | ArchitectureAnalysis
    | DependencyAnalysis
    | Reasoning
    | Retrieval
    | Summary

type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string
    | ExplicitTask          of taskType: TaskType
    | Heuristic             of score: int
    | Default

type RoutingDecision =
    { Target     : ModelId
      Priority   : Priority
      Reason     : RoutingReason
      IsFallback : bool }

type RouterRequest =
    { Messages      : Message list
      ModelOverride  : string option
      Task           : string option
      Stream         : bool
      Temperature    : float option
      TopP           : float option
      MaxTokens      : int option
      UnknownFields  : Map<string, System.Text.Json.JsonElement> }

type RouterError =
    | InvalidRequest        of detail: string
    | UnsupportedTask       of raw: string
    | ModelUnavailable      of ModelId * detail: string
    | GraphIndexingMustFail

type MessageRole = System | User | Assistant
type Message = { Role: MessageRole; Content: string }
```

**`Routing.fs`** — Three-stage pipeline signatures:
```fsharp
// Stage 1
val tryParseModelAlias   : string -> ModelId option
val tryModelOverride     : RouterRequest -> RoutingDecision option

// Stage 2
val tryParseTaskType     : string -> TaskType option
val taskToDecision       : TaskType -> RoutingDecision      // exhaustive match over TaskType DU; NO | _ ->
val tryTaskTable         : RouterRequest -> Result<RoutingDecision option, RouterError>

// Stage 3
val scoreComplexity      : RouterRequest -> int             // pure; deterministic from input alone
val applyHeuristic       : RouterRequest -> RoutingDecision

// Pipeline entry point
val routeRequest         : RouterRequest -> Result<RoutingDecision, RouterError>
```

**`Ports.fs`** — Three interfaces:
```fsharp
type IUpstreamClient =
    abstract CompleteAsync : RouterRequest -> ModelId -> CancellationToken -> Task<Result<string, RouterError>>
    abstract StreamAsync   : RouterRequest -> ModelId -> CancellationToken -> IAsyncEnumerable<Result<string, RouterError>>

type IClock =
    abstract UtcNow : unit -> DateTimeOffset

type IHealthProbe =
    abstract IsReachableAsync : ModelId -> CancellationToken -> Task<bool>
```

### 4. Heuristic Algorithm — Concrete Shape

**Recommendation: score-based (sum weighted signals → threshold)**, not rule-based.

Rationale: score-based gives the planner a single configurable integer (`ComplexityThreshold`) rather than an ordered rule list. Threshold can be tuned via `/stats` data post-deploy.

**Signal weights:**
| Signal | Weight | Notes |
|--------|--------|-------|
| Each keyword hit | +1 per keyword | From configurable keyword list |
| Prompt > 8000 chars | +4 | Absolute complexity signal |
| Prompt > 4000 chars | +2 | Strong length signal |
| Prompt > 2000 chars | +1 | Moderate length signal |
| Message count > 6 | +2 | Deep multi-turn = complex context |
| Message count > 3 | +1 | Multi-turn signal |
| Code block present | +1 | Triple-backtick ` ``` ` only (no indentation, no `<code>` tags) |

**Code block detection:** Triple-backtick fences (` ``` `) only. Not indentation-based (too ambiguous for prose mixed with code). Not `<code>` tags (not present in Hermes/Graphify traffic). Detection: `allText.Contains("```")` is sufficient — exact fence boundaries are not needed since we only want a boolean signal.

**Default threshold:** `3` — at or above → 122B; below → 35B. Matches the ARCHITECTURE.md sketch and the "aggressive 35B bias" constraint.

**Keyword list (default in `appsettings.json`):**
```json
["recursive","dependency","lowering","mlir","llvm","compiler",
 "architecture","type inference","graph relation","closure conversion",
 "cross-file","multi-file","reasoning","inference","optimization",
 "refactor","redesign","abstract","formal","proof"]
```

**Score examples:**
- Short "hello world" prompt (100 chars, 1 message, no keywords): score=0 → 35B
- "build dependency graph" (100 chars, but keyword "dependency"): score=1 → 35B (below threshold)
- 5000-char refactor request with code blocks + "architecture": score=2+1+1=4 → 122B
- 10,000-char multi-file prompt with 4 keywords: score=4+4=8 → 122B

**OPEN QUESTION (flag for user):** The keyword "dependency" scores 1 by itself, which means a short Graphify-style "build dependency graph" without a `task` field would score 1 → 35B default. This is correct for the Hermes path (latency bias), but worth confirming the threshold is right for the expected Hermes traffic distribution. Recommend keeping at 3 and adjusting post-deploy via `/stats`.

### 5. Task Table Semantics

**Decision: fall through on unknown task; case-insensitive; log at Warning.**

When `task=foobar` (unknown string):
- **Behavior:** `tryParseTaskType` returns `None` → `tryTaskTable` returns `Error(UnsupportedTask "foobar")`
- **Endpoint handling:** Returns HTTP 400 with body `{"error": {"message": "unknown task: foobar", "type": "invalid_request_error"}}`
- **Rationale:** Unknown tasks should NOT silently fall through to the heuristic. If Graphify sends a task field, it is an intentional declaration. A typo or unknown value silently routing to heuristic would be harder to diagnose than a loud 400.

**OPEN QUESTION (flag for user):** The current ARCHITECTURE.md has `UnsupportedTask raw` as a `RouterError` case that routes to 400. Confirm this is correct for Phase 1. If the user prefers silent fall-through for unknown tasks (easier for Graphify callers to add new task strings), change `tryTaskTable` to return `Ok None` on unknown rather than `Error(UnsupportedTask raw)`.

**Recommendation:** Keep 400 on unknown task for Phase 1. The task string set is small and known; a 400 surfaces integration bugs immediately.

### 6. Model Alias Set

**Decision: case-insensitive match for `35b`, `122b`, `qwen35b`, `qwen-35b`, `qwen122b`, `qwen-122b`. Unknown values fall through to task/heuristic — do NOT reject.**

```fsharp
let tryParseModelAlias (s: string) : ModelId option =
    match s.ToLowerInvariant() with
    | "35b" | "qwen35b" | "qwen-35b" -> Some Qwen35B
    | "122b" | "qwen122b" | "qwen-122b" -> Some Qwen122B
    | _ -> None
```

**Do NOT include HF repo ids** (`"Qwen/Qwen2.5-Coder-32B"`) in the alias set. Those come from the upstream `/v1/models` response but should never be sent as a routing alias — they are the HF-id-trap risk vector. The `model` field in the POST body is what the *client* sends; the upstream model id is what `QwenUpstreamClient` resolves via `probeModelInfoAsync`.

**Unknown model value:** `tryParseModelAlias` returns `None` → `tryModelOverride` returns `None` → pipeline continues to stage 2 (task table). This is correct: if a client sends `model=gpt-4o`, the router treats it as if no model was specified and routes by task/heuristic. No rejection, no error.

**OPEN QUESTION (flag for user):** Should an unrecognized `model` field value log a warning? Recommendation: yes, log at `Debug` level so it's visible under `--trace` but doesn't pollute production logs. Confirm this preference.

### 7. `appsettings.json` Shape

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://127.0.0.1:4000"
      }
    }
  },
  "Upstreams": {
    "Model35B": "http://127.0.0.1:8000",
    "Model122B": "http://127.0.0.1:8001"
  },
  "Routing": {
    "ComplexityThreshold": 3,
    "TimeoutSeconds": 300,
    "Keywords": [
      "recursive", "dependency", "lowering", "mlir", "llvm", "compiler",
      "architecture", "type inference", "graph relation", "closure conversion",
      "cross-file", "multi-file", "reasoning", "inference", "optimization",
      "refactor", "redesign", "abstract", "formal", "proof"
    ],
    "TaskTable": {
      "graph_indexing":        { "Model": "122b", "Priority": "high" },
      "compiler_debug":        { "Model": "122b", "Priority": "high" },
      "architecture_analysis": { "Model": "122b", "Priority": "high" },
      "dependency_analysis":   { "Model": "122b", "Priority": "low" },
      "reasoning":             { "Model": "122b", "Priority": "low" },
      "retrieval":             { "Model": "35b",  "Priority": "low" },
      "summary":               { "Model": "35b",  "Priority": "low" }
    },
    "ModelAliases": {
      "35b":      "Qwen35B",
      "qwen35b":  "Qwen35B",
      "qwen-35b": "Qwen35B",
      "122b":     "Qwen122B",
      "qwen122b": "Qwen122B",
      "qwen-122b":"Qwen122B"
    }
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information"
    }
  }
}
```

**Section notes:**
- `Kestrel.Endpoints.Http.Url` — explicit `127.0.0.1` binding (not `localhost`, not `0.0.0.0`). See PITFALL-24.
- `Upstreams` — F# `IOptions<UpstreamOptions>` with `Model35B: string` and `Model122B: string`
- `Routing` — F# `IOptions<RoutingOptions>` bound in `CompositionRoot`
- `Serilog` section — read by `Serilog.AspNetCore` / `UseSerilog()`. Minimum level controlled here; override to Debug via environment variable (see below)
- `TaskTable` is informational in `appsettings.json` but the authoritative routing is the exhaustive F# `taskToDecision` function. The `TaskTable` section can be read to validate config at startup, but the pipeline does not dynamically dispatch from JSON config — the DU match is the source of truth.

**Env-var override convention** (ASP.NET Core standard):
```bash
# Override minimum log level to Debug at runtime (for --trace equivalent):
DOTNET_Serilog__MinimumLevel__Default=Debug ./SmartRouter

# Override upstream URLs:
DOTNET_Upstreams__Model122B=http://127.0.0.1:9000 ./SmartRouter
```

Double-underscore (`__`) separates nesting levels in env var names. This is the ASP.NET Core standard — no extra package needed.

### 8. `check-no-async.sh`

Adapt blueCode's script verbatim, changing only the path:

```bash
#!/usr/bin/env bash
# scripts/check-no-async.sh
# Enforces no `async {}` in SmartRouter.Core — use task {} CE only.
# Exit 0 if clean; exit 1 on any match; exit 2 if Core dir missing.

set -euo pipefail

CORE_DIR="src/SmartRouter.Core"

if [ ! -d "$CORE_DIR" ]; then
    echo "ERROR: $CORE_DIR does not exist (run from repository root)" >&2
    exit 2
fi

if grep -rn --include='*.fs' 'async {' "$CORE_DIR" ; then
    echo "" >&2
    echo "ERROR: async {} found in $CORE_DIR — use task {} CE instead." >&2
    exit 1
fi

echo "OK: no async {} expressions in $CORE_DIR"
exit 0
```

**Scope:** greps `src/SmartRouter.Core/**` only (not Cli, not Tests). The ban does not apply to Cli adapters (which may use `Async.AwaitTask` interop bridges) or Tests.

**When to run:** In CI (later phase) and manually as a pre-commit guard. Add to `.claude/settings.json` hooks if desired.

### 9. OBS-04: Stderr Separation

Copy `Adapters/Logging.fs` verbatim from blueCode. The key line is `standardErrorFromLevel = System.Nullable<LogEventLevel>(LogEventLevel.Verbose)` — this routes ALL Serilog events (even Verbose) to stderr:

```fsharp
// src/SmartRouter.Cli/Adapters/Logging.fs
module SmartRouter.Cli.Adapters.Logging

open Serilog
open Serilog.Core
open Serilog.Events

let levelSwitch: LoggingLevelSwitch = LoggingLevelSwitch(LogEventLevel.Information)

let configure () : unit =
    Log.Logger <-
        LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .WriteTo.Console(
                standardErrorFromLevel = System.Nullable<LogEventLevel>(LogEventLevel.Verbose),
                outputTemplate = "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger()

let shutdown () : unit = Log.CloseAndFlush()
```

In `Program.fs`, call `Logging.configure()` before building the `WebApplication`, then wire Serilog into ASP.NET Core:

```fsharp
Logging.configure()
let builder = WebApplication.CreateBuilder(args)
builder.Host.UseSerilog() |> ignore
// ...
app.UseSerilogRequestLogging() |> ignore
```

**Source:** `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/Logging.fs` (copy verbatim, change module name).

### 10. Non-Streaming POST Endpoint Shape

**Phase 1 streaming policy: return 501 Not Implemented when `stream=true`.**

The endpoint detects streaming via the `stream` field in the parsed request body. In Phase 1, if `stream=true`, return 501 with a message that Phase 2 will wire it. This prevents silent incorrect behavior and gives callers a clear error to act on.

```fsharp
// Endpoints/ChatCompletions.fs (Phase 1 stub — non-streaming only)
let handler (upstream: IUpstreamClient) (ctx: HttpContext) : Task =
    task {
        let! wireBody = ctx.Request.ReadFromJsonAsync<RouterRequestWire>(...)
        let req = mapWireToRequest wireBody

        // Phase 1: reject streaming requests with 501
        if req.Stream then
            ctx.Response.StatusCode <- 501
            do! ctx.Response.WriteAsJsonAsync(
                {| error = {| message = "streaming not yet implemented (Phase 2)"
                               ``type`` = "not_implemented" |} |})
            return ()

        match SmartRouter.Core.Routing.routeRequest req with
        | Error (UnsupportedTask raw) ->
            ctx.Response.StatusCode <- 400
            do! ctx.Response.WriteAsJsonAsync(
                {| error = {| message = $"unknown task: {raw}"
                               ``type`` = "invalid_request_error" |} |})
        | Error e ->
            ctx.Response.StatusCode <- 400
            do! ctx.Response.WriteAsJsonAsync(
                {| error = {| message = string e; ``type`` = "invalid_request_error" |} |})
        | Ok decision ->
            Log.Information("Routing {Target} reason={Reason}", decision.Target, decision.Reason)
            match! upstream.CompleteAsync(req, decision.Target, ctx.RequestAborted) with
            | Ok body ->
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsync(body)
            | Error e ->
                ctx.Response.StatusCode <- 502
                do! ctx.Response.WriteAsJsonAsync(
                    {| error = {| message = string e; ``type`` = "upstream_error" |} |})
    }
```

**Request parsing wire type** (in `ChatCompletions.fs` or a shared `WireTypes.fs`):
```fsharp
[<CLIMutable>]
type RouterRequestWire =
    { messages    : WireMessage[]
      model       : string
      stream      : bool
      temperature : float Nullable
      top_p       : float Nullable
      max_tokens  : int Nullable
      task        : string        // nullable in JSON; STJ maps missing field to null
      [<JsonExtensionData>]
      extra       : Dictionary<string, JsonElement> }
```

`[<JsonExtensionData>]` captures unknown fields (satisfies API-04 / ROUT-07 UnknownFields preservation).

**Route registration in `Program.fs`**:
```fsharp
app.MapPost("/v1/chat/completions", ChatCompletions.handler upstreamClient)
|> ignore
```

### 11. Test Seam for Phase 1

**Phase 1 tests are pure unit tests only — no TestServer, no live HTTP.**

`RoutingTests.fs` tests the pure `routeRequest` pipeline. No `WebApplicationFactory`, no `Microsoft.AspNetCore.Mvc.Testing` needed in Phase 1. That package is added to the project now (so it compiles), but not used until Phase 6 integration tests.

**`RouterTests.fs`** (entry point with explicit `rootTests`):
```fsharp
module SmartRouter.Tests.RouterTests

open Expecto

let rootTests =
    [ SmartRouter.Tests.RoutingTests.tests
      // <- ADD NEW MODULES HERE before adding to .fsproj
    ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv (testList "all" rootTests)
```

**`RoutingTests.fs`** (pure routing tests):
```fsharp
module SmartRouter.Tests.RoutingTests

open Expecto
open SmartRouter.Core.Domain
open SmartRouter.Core.Routing

let private emptyRequest task model =
    { Messages      = [{ Role = User; Content = "test" }]
      ModelOverride  = model
      Task           = task
      Stream         = false
      Temperature    = None; TopP = None; MaxTokens = None
      UnknownFields  = Map.empty }

let tests = testList "routing" [
    testCase "model override 35b routes to Qwen35B" <| fun () ->
        let req = emptyRequest None (Some "35b")
        Expect.equal (routeRequest req) (Ok { Target=Qwen35B; Priority=Low; Reason=ExplicitModelOverride "35b"; IsFallback=false }) ""

    testCase "model override 122b routes to Qwen122B" <| fun () ->
        let req = emptyRequest None (Some "122b")
        match routeRequest req with
        | Ok d -> Expect.equal d.Target Qwen122B ""
        | Error e -> failwithf "expected Ok, got %A" e

    testCase "task graph_indexing routes to Qwen122B high priority" <| fun () ->
        let req = emptyRequest (Some "graph_indexing") None
        match routeRequest req with
        | Ok d ->
            Expect.equal d.Target Qwen122B ""
            Expect.equal d.Priority High ""
        | Error e -> failwithf "expected Ok, got %A" e

    testCase "task retrieval routes to Qwen35B" <| fun () ->
        let req = emptyRequest (Some "retrieval") None
        match routeRequest req with
        | Ok d -> Expect.equal d.Target Qwen35B ""
        | Error e -> failwithf "expected Ok, got %A" e

    testCase "unknown task returns UnsupportedTask error" <| fun () ->
        let req = emptyRequest (Some "foobar") None
        Expect.equal (routeRequest req) (Error(UnsupportedTask "foobar")) ""

    testCase "short simple prompt routes to Qwen35B via heuristic" <| fun () ->
        let req = { emptyRequest None None with Messages = [{ Role=User; Content="hello" }] }
        match routeRequest req with
        | Ok d -> Expect.equal d.Target Qwen35B ""
        | Error e -> failwithf "expected Ok, got %A" e

    testCase "long complex prompt with keywords routes to Qwen122B via heuristic" <| fun () ->
        let longText = System.String.replicate 500 "recursive compiler architecture dependency "
        let req = { emptyRequest None None with Messages = [{ Role=User; Content=longText }] }
        match routeRequest req with
        | Ok d -> Expect.equal d.Target Qwen122B ""
        | Error e -> failwithf "expected Ok, got %A" e

    testCase "model override takes precedence over task" <| fun () ->
        let req = emptyRequest (Some "graph_indexing") (Some "35b")
        match routeRequest req with
        | Ok d -> Expect.equal d.Target Qwen35B "model override should beat task"
        | Error e -> failwithf "expected Ok, got %A" e
]
```

**`testSequenced` rule:** Phase 1 routing tests are pure (no `Console.SetOut`), so `testSequenced` is not needed. Add it when integration tests arrive.

### 12. Phase 1 Pitfalls (Phase-Specific Only)

Only pitfalls that materially affect Phase 1 plans:

**PITFALL-1 (HF-id trap) — affects plan 01-03 `QwenUpstreamClient`**
- Copy `tryParseModelId` verbatim from blueCode `QwenHttpClient.fs` lines 328–356
- Prefer ids starting with `/`; fall back to `data[0].id`
- Lazy probe: `Lazy<Task<ModelInfo>>` per port — probe fires on first call, cached forever
- Source: `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/QwenHttpClient.fs`

**PITFALL-12 (100s timeout default) — affects plan 01-03**
- Set `httpClient.Timeout <- TimeSpan.FromSeconds(300.0)` at named client creation
- 122B cold-start observed up to 240s; default 100s fails every cold start
- Source: blueCode `QwenHttpClient.fs` line 35

**PITFALL-13 (`async {}` ban) — affects all plans, enforced by check-no-async.sh**
- Mirror blueCode `scripts/check-no-async.sh` with Core path `src/SmartRouter.Core`
- Run during scaffold (plan 01-01) before any F# code is written
- Source: `/Users/ohama/projs/blueCode/scripts/check-no-async.sh` (copy verbatim, adapt path)

**PITFALL-15 (named vs typed HttpClient) — affects plan 01-03**
- Register named clients: `services.AddHttpClient("upstream35b", ...)` and `services.AddHttpClient("upstream122b", ...)`
- Factory creates clients per-request via `factory.CreateClient("upstream35b")` — correct pooling
- Do NOT use typed client wrapping — creates instance-per-DI-scope

**PITFALL-24 (`127.0.0.1` binding) — affects plan 01-01**
- In `appsettings.json`: `"Kestrel": { "Endpoints": { "Http": { "Url": "http://127.0.0.1:4000" } } }`
- Not `localhost` (may resolve to IPv6 `::1`); not `0.0.0.0` (triggers macOS firewall)

**PITFALL-25 (`enable_thinking=false`) — affects plan 01-03**
- Add startup smoke test: POST a trivial request to each upstream, assert `choices[0].message.content` does not start with `<think>`
- The mlx_lm.server plists already include `--chat-template-args '{"enable_thinking": false}'`; this is defensive validation

**PITFALL-26 (Expecto rootTests) — affects plan 01-01 / 01-03**
- Establish explicit `rootTests` list in `RouterTests.fs` at scaffold time (plan 01-01)
- Every new test module: (1) add to `.fsproj` `<Compile>` list BEFORE `RouterTests.fs`, (2) add to `rootTests` list
- Do NOT rely on `[<Tests>]` auto-discovery — burned 4 executors in blueCode

**PITFALL-27 (Expecto Console.SetOut races) — affects test scaffold**
- Wrap any `testList` touching `Console.SetOut`/`Console.SetError` with `testSequenced`
- Phase 1 routing tests are pure — not an issue yet; document the pattern in `RouterTests.fs` comments

---

## Code Examples

### `check-no-async.sh` (adapted from blueCode)

See Section 8 above. File path: `/Users/ohama/projs/smart-router/scripts/check-no-async.sh`.

The only change from blueCode: `CORE_DIR="src/SmartRouter.Core"` (was `src/BlueCode.Core`).

### `Json.fs` — copy verbatim

Source: `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/Json.fs`

**Important:** The blueCode `Json.fs` imports `BlueCode.Core.Domain` and `BlueCode.Cli.Adapters.LlmWire`. The smart-router copy must:
1. Change module declaration to `module SmartRouter.Cli.Adapters.Json`
2. Remove the blueCode-specific domain imports (`open BlueCode.Core.Domain`, `open BlueCode.Cli.Adapters.LlmWire`)
3. Keep only the `jsonOptions` binding and the `JsonFSharpConverter` setup
4. The extraction pipeline (`parseLlmResponse`, `extractLlmStep`, `llmStepSchema`) is blueCode-specific — omit entirely

**Minimal `Json.fs` for smart-router:**
```fsharp
module SmartRouter.Cli.Adapters.Json

open System.Text.Json
open System.Text.Json.Serialization

/// Shared STJ options for all JSON round-trips.
/// JsonFSharpConverter with WithUnionUnwrapFieldlessTags serializes fieldless DU
/// cases as bare strings ("System", "User") not {"Case":"System"}.
let jsonOptions: JsonSerializerOptions =
    let opts = JsonSerializerOptions()
    opts.Converters.Add(JsonFSharpConverter(JsonFSharpOptions.Default().WithUnionUnwrapFieldlessTags(true)))
    opts
```

### `Logging.fs` — copy verbatim (module name change only)

Source: `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/Logging.fs`

Only change: `module SmartRouter.Cli.Adapters.Logging`

### `QwenUpstreamClient.fs` — key sections to copy from blueCode

Source: `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/QwenHttpClient.fs`

**Copy verbatim:**
- `tryParseModelId` (lines 328–356) — HF-id trap defense
- `tryParseMaxModelLen` (lines 358–379)
- `probeModelInfoAsync` (lines 398–439) — but remove blueCode-specific `validateModelPath` call
- `postAsync` error mapping pattern (lines 93–127) — adapt to `RouterError` DU instead of `AgentError`
- `buildRequestBody` sampling-param defaults (temperature=0.7, top_p=0.8, top_k=20, presence_penalty=0.0)

**Rewrite:**
- `httpClient` → named `IHttpClientFactory` clients (not module-level singleton)
- Error type: `AgentError` → `RouterError`
- Remove blueCode-specific types (`ModelInfo`, `AppComponents` references)
- Remove `withSpinner` (blueCode UI concern — no spinner in a gateway)
- Implement `IUpstreamClient` interface (not `ILlmClient`)
- `CompleteAsync` method returns `Task<Result<string, RouterError>>` (full response body, not extracted content)

---

## Open Questions

1. **Unknown task string behavior (confirmed: return 400)**
   - What we know: `UnsupportedTask raw` maps to `Error` in `tryTaskTable`; endpoint returns 400
   - Recommendation: keep 400 for Phase 1 (loud failure aids debugging)
   - **Flag for user:** If Graphify callers should be able to send new task strings without router updates, change to silent fall-through (`Ok None`). Confirm before plan-writing.

2. **Unknown model alias behavior (confirmed: fall-through)**
   - What we know: unrecognized `model` → `tryModelOverride` returns `None` → continues to task/heuristic
   - Recommendation: log at Debug, do not reject
   - **Flag for user:** Confirm this behavior is correct for the Hermes path (Hermes may send `model="gpt-4o"` or other strings from earlier tests).

3. **Heuristic threshold (confirmed: 3)**
   - Recommendation: ship with threshold=3; adjust post-deploy via `/stats` data
   - **Flag for user:** If Hermes sends complex prompts that score 3-4 and the user prefers them to go to 35B, raise threshold to 4.

4. **`TaskTable` in `appsettings.json` — config-driven vs code-driven**
   - Current design: `taskToDecision` is an exhaustive F# match (code-driven); `appsettings.json` has a `TaskTable` section as documentation / validation cross-reference
   - **Flag for user:** If tasks should be dynamically configurable without recompile, `taskToDecision` must read from `IOptions<RoutingOptions>`. This adds complexity but allows adding new tasks without a code change. Recommendation for Phase 1: keep code-driven (simpler, exhaustive match enforces completeness). Mark as a potential Phase 2 enhancement.

---

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|------------------|--------|
| `Serilog.AspNetCore` 9.x | 10.0.0 | Version aligned with .NET 10 |
| `Microsoft.Extensions.Http.Resilience` 9.x | 10.5.0 | Version aligned with .NET 10 |
| `FsToolkit.ErrorHandling` 4.x | 5.2.0 | Already in blueCode Core; no divergence |
| `FSharp.Control.TaskSeq` 0.4.x | 1.1.1 | Major version bump; stable API |
| `Microsoft.AspNetCore.Mvc.Testing` 10.0.0 | 10.0.7 | Patch release; no breaking changes |

---

## Sources

### Primary (HIGH confidence — direct file analysis)
- `/Users/ohama/projs/blueCode/src/BlueCode.Core/BlueCode.Core.fsproj` — FsToolkit.ErrorHandling 5.2.0 confirmed
- `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/QwenHttpClient.fs` — tryParseModelId, probeModelInfoAsync, postAsync patterns
- `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/Json.fs` — jsonOptions pattern
- `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/Logging.fs` — Serilog stderr pattern
- `/Users/ohama/projs/blueCode/scripts/check-no-async.sh` — exact script to adapt
- `/Users/ohama/projs/blueCode/BlueCode.slnx` — .slnx format confirmed
- `/Users/ohama/projs/blueCode/global.json` — SDK pin 10.0.100 confirmed
- `/Users/ohama/projs/smart-router/.planning/research/ARCHITECTURE.md` — F# type signatures (implementation-ready)
- `/Users/ohama/projs/smart-router/.planning/research/PITFALLS.md` — pitfall inventory

### Secondary (HIGH confidence — live NuGet verification)
- `dotnet package search Serilog.AspNetCore` → 10.0.0
- `dotnet package search Microsoft.Extensions.Http.Resilience` → 10.5.0
- `dotnet package search FsToolkit.ErrorHandling` → 5.2.0
- `dotnet package search FSharp.Control.TaskSeq` → 1.1.1
- `dotnet package search Microsoft.AspNetCore.Mvc.Testing` → 10.0.7

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all 9 packages now live-verified or blueCode-confirmed
- Architecture: HIGH — F# signatures from ARCHITECTURE.md are implementation-ready
- Pitfalls: HIGH — all Phase 1 pitfalls grounded in blueCode operational history

**Research date:** 2026-05-07
**Valid until:** 2026-06-07 (NuGet versions stable; framework stable)
