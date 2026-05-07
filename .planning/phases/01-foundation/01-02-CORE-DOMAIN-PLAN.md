---
phase: 01-foundation
plan: 02
type: execute
wave: 2
depends_on: ["01-01"]
files_modified:
  - src/SmartRouter.Core/Domain.fs
  - src/SmartRouter.Core/Routing.fs
  - src/SmartRouter.Core/Ports.fs
  - src/SmartRouter.Core/SmartRouter.Core.fsproj
  - src/SmartRouter.Cli/Adapters/Json.fs
  - src/SmartRouter.Cli/Adapters/Logging.fs
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - tests/SmartRouter.Tests/RoutingTests.fs
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
  - tests/SmartRouter.Tests/RouterTests.fs
autonomous: true

must_haves:
  truths:
    - "`Routing.routeRequest` has signature `RoutingConfig -> RouterRequest -> Result<RoutingDecision, RouterError>` — the config is a pure F# record passed in by the caller; Core has no `IOptions<T>` dependency (ARCH-01)"
    - "Sending `{\"task\": \"graph_indexing\"}` through `Routing.routeRequest config req` (where `config` mirrors the canonical `appsettings.json` task table) returns `Ok { Target = Qwen122B; Priority = High; Reason = ExplicitTask GraphIndexing; IsFallback = false }` (verified by Expecto unit test, no live upstream)"
    - "Sending `{\"task\": \"retrieval\"}` returns `Ok { Target = Qwen35B; ... }` (unit test)"
    - "Sending no task with a long complex prompt full of keywords routes to Qwen122B via heuristic; sending no task with a short hello-world prompt routes to Qwen35B (unit tests)"
    - "Sending `{\"model\": \"35b\"}` or `{\"model\": \"122b\"}` overrides task and heuristic routing — Reason = ExplicitModelOverride (unit test)"
    - "Sending `{\"task\": \"foobar\"}` returns `Error (UnsupportedTask \"foobar\")` (unit test)"
    - "Routing.fs runtime dispatch reads task→model from `RoutingConfig.TaskTable` (a `Map<string, ModelId * Priority>`) — NOT from a hardcoded F# match. Editing `appsettings.json` Routing.TaskTable changes runtime behavior without recompiling (CONTEXT.md locked decision)"
    - "Routing.fs `applyHeuristic` reads `ComplexityThreshold` and `Keywords` from `RoutingConfig` — no hardcoded literals in the runtime path"
    - "Routing.fs has no `| _ ->` catch-all anywhere (grep verified)"
    - "The exhaustive `taskToDecision` match remains in Core as a pure utility for startup validation and as a known-good baseline used by tests — but is NOT called by the runtime dispatch path"
    - "Core compiles with only FsToolkit.ErrorHandling — no Serilog, no HttpClient, no ASP.NET, no IOptions (build with those packages temporarily uninstalled to confirm)"
    - "`./scripts/check-no-async.sh` exits 0 — Core uses `task {}` exclusively, never `async {}`"
  artifacts:
    - path: "src/SmartRouter.Core/Domain.fs"
      provides: "All Core DUs: ModelId, Priority, TaskType, RoutingReason, RoutingDecision, RouterRequest, RouterError, MessageRole, Message, RoutingConfig"
      contains: "type RoutingConfig"
    - path: "src/SmartRouter.Core/Routing.fs"
      provides: "Pure three-stage pipeline parameterized by RoutingConfig: tryModelOverride, tryTaskTable (config-driven), applyHeuristic (config-driven), routeRequest, plus taskToDecision exhaustive baseline retained for validation/tests"
      contains: "RoutingConfig -> RouterRequest"
    - path: "src/SmartRouter.Core/Ports.fs"
      provides: "IUpstreamClient, IClock, IHealthProbe interfaces"
      contains: "IUpstreamClient"
    - path: "src/SmartRouter.Cli/Adapters/Json.fs"
      provides: "STJ jsonOptions binding (FSharp.SystemTextJson with WithUnionUnwrapFieldlessTags)"
      contains: "JsonFSharpConverter"
    - path: "src/SmartRouter.Cli/Adapters/Logging.fs"
      provides: "Serilog → stderr configuration (standardErrorFromLevel = Verbose)"
      contains: "standardErrorFromLevel"
    - path: "tests/SmartRouter.Tests/RoutingTests.fs"
      provides: "Expecto testList covering all three pipeline stages + override precedence + unknown task error"
      contains: "testList \"routing\""
  key_links:
    - from: "src/SmartRouter.Core/Routing.fs"
      to: "Domain.fs DUs"
      via: "open SmartRouter.Core.Domain"
      pattern: "open SmartRouter\\.Core\\.Domain"
    - from: "src/SmartRouter.Core/Ports.fs"
      to: "RouterRequest, ModelId, RouterError"
      via: "type signatures referencing Domain"
      pattern: "RouterRequest.*ModelId"
    - from: "tests/SmartRouter.Tests/RouterTests.fs"
      to: "RoutingTests.tests"
      via: "rootTests list entry"
      pattern: "RoutingTests\\.tests"
    - from: "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
      to: "RoutingTests.fs (compiled BEFORE RouterTests.fs)"
      via: "<Compile Include=...> ordering"
      pattern: "RoutingTests\\.fs"
---

<objective>
Implement the pure Core domain (`Domain.fs`, `Routing.fs`, `Ports.fs`) and the two verbatim-copy infrastructure adapters (`Json.fs`, `Logging.fs`) — and prove the routing pipeline correct with Expecto unit tests that run without any live upstream.

Phase goal contribution: Delivers Success Criterion #2 ("Sending `{\"task\": \"graph_indexing\"}` routes to 122B; ... — all four verified by Expecto unit tests that run without a live upstream") and Success Criterion #3 ("Sending `{\"model\": \"35b\"}` or `{\"model\": \"122b\"}` overrides task and heuristic routing — verified by unit test"). Also locks in ARCH-01 through ARCH-07 — the hexagonal Core boundary.

Output: Pure Core that compiles and is independently testable; verbatim Json.fs/Logging.fs adapters ready to be consumed by the QwenUpstreamClient in plan 01-03; `RoutingTests.fs` Expecto suite that all passes.
</objective>

<execution_context>
@./.planning/phases/01-foundation/01-CONTEXT.md
@./.planning/phases/01-foundation/01-RESEARCH.md
@./.planning/research/ARCHITECTURE.md
</execution_context>

<context>
**blueCode files to copy verbatim** (only the module name changes):
- `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/Logging.fs` — Serilog stderr configuration
- `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/Json.fs` — STJ options binding (strip blueCode-specific extraction pipeline; keep only `jsonOptions`)

**Concrete F# signatures** (from RESEARCH.md sections 3 and 11; ARCHITECTURE.md "Core Domain Types" and "Core Routing Types and Pipeline"):
- `Domain.fs`: 10 types (ModelId, Priority, TaskType, RoutingReason, RoutingDecision, RouterRequest, RouterError, MessageRole, Message, RoutingConfig)
- `Routing.fs`: tryParseModelAlias, tryModelOverride, tryParseTaskType, taskToDecision (utility/validation only — NOT runtime dispatch), tryTaskTable (config-parameterized), scoreComplexity (config-parameterized), applyHeuristic (config-parameterized), routeRequest (signature: `RoutingConfig -> RouterRequest -> Result<RoutingDecision, RouterError>`)
- `Ports.fs`: IUpstreamClient, IClock, IHealthProbe

**Locked decisions from CONTEXT.md:**
- Unknown task → `Error (UnsupportedTask raw)` (case-insensitive match in `tryParseTaskType`)
- Unknown model → fall through to next stage (no error)
- Heuristic threshold = 3, score-based, code-block detection = triple-backtick only, tie → 35B (latency-first)
- **Task→model table is CONFIG-DRIVEN from day 1** (CONTEXT.md, user explicitly overrode the recommended hardcoded default): runtime dispatch reads the mapping from `RoutingConfig.TaskTable: Map<string, ModelId * Priority>`. The Cli composition root constructs `RoutingConfig` from `appsettings.json` `Routing.TaskTable` (plus `ComplexityThreshold` + `Keywords`) and passes it into `Routing.routeRequest`. Operator can edit JSON and change runtime behavior without recompiling. Priority assignment per task stays in code (correctness, not policy) — only the model assignment is operator-tunable, but for shape uniformity the config carries `(ModelId, Priority)` tuples and the priority is validated against the canonical `taskToDecision` baseline at startup.
- The exhaustive `taskToDecision` match remains in Core as a pure utility used for (a) startup validation that the JSON covers all known tasks with no typos, and (b) a known-good baseline available to tests. **It is never called by the runtime dispatch path.**
</context>

<tasks>

<task type="auto">
  <name>Task 1: Implement Core (Domain.fs, Routing.fs, Ports.fs) and wire into .fsproj</name>
  <files>
    src/SmartRouter.Core/Domain.fs,
    src/SmartRouter.Core/Routing.fs,
    src/SmartRouter.Core/Ports.fs,
    src/SmartRouter.Core/SmartRouter.Core.fsproj
  </files>
  <action>
    1. Create `src/SmartRouter.Core/Domain.fs` from the F# signatures in ARCHITECTURE.md "Core Domain Types" section, plus the new `RoutingConfig` record (Core-pure; no `IOptions<T>`). Note: ARCHITECTURE.md sample has `RoutingReason` referencing `TaskType` before `TaskType` is declared — F# requires top-to-bottom order, so emit them in this order: `ModelId`, `Priority`, `TaskType`, `RoutingReason`, `MessageRole`, `Message`, `RouterRequest`, `RouterError`, `RoutingDecision`, `RoutingConfig`. Module declaration line: `module SmartRouter.Core.Domain`.

       Required public types (all DU cases verbatim from ARCHITECTURE.md):
       - `type ModelId = Qwen35B | Qwen122B`
       - `type Priority = High | Low`
       - `type TaskType = GraphIndexing | CompilerDebug | ArchitectureAnalysis | DependencyAnalysis | Reasoning | Retrieval | Summary`
       - `type RoutingReason = ExplicitModelOverride of requestedAlias: string | ExplicitTask of taskType: TaskType | Heuristic of score: int | Default`
       - `type MessageRole = System | User | Assistant`
       - `type Message = { Role: MessageRole; Content: string }`
       - `type RouterRequest = { Messages: Message list; ModelOverride: string option; Task: string option; Stream: bool; Temperature: float option; TopP: float option; MaxTokens: int option; UnknownFields: Map<string, System.Text.Json.JsonElement> }`
       - `type RouterError = InvalidRequest of detail: string | UnsupportedTask of raw: string | ModelUnavailable of ModelId * detail: string | GraphIndexingMustFail`
       - `type RoutingDecision = { Target: ModelId; Priority: Priority; Reason: RoutingReason; IsFallback: bool }`
       - **`type RoutingConfig = { ComplexityThreshold: int; Keywords: string list; TaskTable: Map<string, ModelId * Priority> }`** — plain F# record, no `IOptions<T>`, no ASP.NET, no JSON binding. The Cli layer (plan 01-03) constructs this from `appsettings.json` and injects it. ARCH-01 preserved.

       **Note on `JsonElement`:** `System.Text.Json.JsonElement` is in the BCL — Core can reference it without a NuGet package (it lives in `System.Text.Json` which ships with the .NET 10 runtime). This does NOT violate ARCH-01 (the ban is on Serilog / HttpClient / ASP.NET Core, not the BCL).

    2. Create `src/SmartRouter.Core/Routing.fs`. Module declaration: `module SmartRouter.Core.Routing`. The runtime dispatch path is **config-driven** per CONTEXT.md (locked decision). Functions:

       - `tryParseModelAlias` — accepts `35b`, `122b`, `qwen35b`, `qwen-35b`, `qwen122b`, `qwen-122b` (case-insensitive); returns `Some ModelId` or `None`. Pure.
       - `tryModelOverride : RouterRequest -> RoutingDecision option` — Stage 1. Pure, no config needed (model alias parsing is structural correctness, not policy).
       - `tryParseTaskType : string -> TaskType option` — case-insensitive match over the 7 task strings. Pure.
       - **`taskToDecision : TaskType -> RoutingDecision`** — exhaustive `function` over `TaskType` DU; **MUST NOT contain `| _ ->`**. **NOT called by `routeRequest` at runtime.** Retained as: (a) the canonical baseline used by startup validation (proves the JSON covers all known tasks with the expected models/priorities), and (b) a known-good fixture for tests. Compile-time exhaustiveness is the contract; if a new `TaskType` DU case is added in a future phase, the F# compiler forces an update here, which then forces a corresponding entry in `appsettings.json` via the startup validator.
       - **`tryTaskTable : RoutingConfig -> RouterRequest -> Result<RoutingDecision option, RouterError>`** — Stage 2 runtime dispatch. Reads `config.TaskTable` (a `Map<string, ModelId * Priority>`) keyed by the lowercased raw task string. If `req.Task = Some raw`:
           - First call `tryParseTaskType raw`. `None` → `Error (UnsupportedTask raw)` (CONTEXT.md: loud failure on unknown task names).
           - Then look up `Map.tryFind (raw.ToLowerInvariant()) config.TaskTable`. If found, return `Ok (Some { Target = model; Priority = prio; Reason = ExplicitTask t; IsFallback = false })`.
           - If `tryParseTaskType` says the task is known but the config map has no entry for it (operator removed it from JSON), return `Error (InvalidRequest $"task '{raw}' has no config entry")`. This shouldn't happen because startup validation rejects an incomplete JSON, but defends against runtime config-mutation in future phases.
           - If `req.Task = None`, return `Ok None` (fall through to heuristic).
       - **`scoreComplexity : RoutingConfig -> RouterRequest -> int`** — reads `config.Keywords` (no hardcoded list); pure score per CONTEXT.md spec: keyword hit = +1 each (case-insensitive substring match, configurable list), length tiers +1/+2/+4 at 2000/4000/8000 chars across all message contents combined, message count +1/+2 at 4/7, code-block = +1 if any message content contains `"```"` (triple backtick).
       - **`applyHeuristic : RoutingConfig -> RouterRequest -> RoutingDecision`** — reads `config.ComplexityThreshold` (no hardcoded `3`). Score `>= threshold` → `Qwen122B Low`; else `Qwen35B Low`. Tie (score `< threshold`) goes to 35B (latency-first per CONTEXT.md). Reason = `Heuristic score`.
       - **`routeRequest : RoutingConfig -> RouterRequest -> Result<RoutingDecision, RouterError>`** — three-stage pipeline:
           1. `tryModelOverride req` → if `Some d`, return `Ok d`.
           2. `tryTaskTable config req` → if `Ok (Some d)`, return `Ok d`. If `Error e`, propagate. If `Ok None`, fall through.
           3. `Ok (applyHeuristic config req)`.

       **Critical correctness rules:**
       - `taskToDecision`, `tryParseTaskType`, and `tryParseModelAlias` are exhaustive matches with NO `| _ ->` catch-all on DUs. Verify with `grep -n '| _ ->' src/SmartRouter.Core/Routing.fs` — must return zero hits.
       - `routeRequest` MUST take `RoutingConfig` as its first parameter. The signature `RouterRequest -> ...` (config-less) is forbidden — that would be the hardcoded path the user explicitly rejected.
       - `taskToDecision` MUST NOT be called by `routeRequest`, `tryTaskTable`, or any function in the runtime path. It is referenced only by the startup validator (in plan 01-03's Cli) and tests. A grep for `taskToDecision` inside `routeRequest`'s body or `tryTaskTable`'s body should return zero hits.

       **Use `task {}` not `async {}`:** No CE is needed for these pure functions, but if any helper accidentally uses `async { ... }`, `check-no-async.sh` will reject it (ARCH-02).

       **Helper for tests and validation:** also expose a pure utility
       ```fsharp
       /// Canonical task table derived from the exhaustive taskToDecision match.
       /// Used by (a) tests as a known-good RoutingConfig, (b) plan 01-03's startup
       /// validator as the baseline to diff against the JSON-loaded TaskTable.
       let canonicalTaskTable : Map<string, ModelId * Priority> =
           [ "graph_indexing"        , taskToDecision GraphIndexing        |> fun d -> d.Target, d.Priority
             "compiler_debug"        , taskToDecision CompilerDebug        |> fun d -> d.Target, d.Priority
             "architecture_analysis" , taskToDecision ArchitectureAnalysis |> fun d -> d.Target, d.Priority
             "dependency_analysis"   , taskToDecision DependencyAnalysis   |> fun d -> d.Target, d.Priority
             "reasoning"             , taskToDecision Reasoning            |> fun d -> d.Target, d.Priority
             "retrieval"             , taskToDecision Retrieval            |> fun d -> d.Target, d.Priority
             "summary"               , taskToDecision Summary              |> fun d -> d.Target, d.Priority ]
           |> Map.ofList

       let canonicalKeywords : string list =
           [ "recursive"; "dependency"; "lowering"; "mlir"; "llvm"; "compiler"
             "architecture"; "type inference"; "graph relation"; "closure conversion"
             "cross-file"; "multi-file"; "reasoning"; "inference"; "optimization"
             "refactor"; "redesign"; "abstract"; "formal"; "proof" ]

       /// Default RoutingConfig (used by tests + as the baseline the JSON validator diffs against).
       let defaultRoutingConfig : RoutingConfig =
           { ComplexityThreshold = 3
             Keywords            = canonicalKeywords
             TaskTable           = canonicalTaskTable }
       ```
       This is the bridge that lets the exhaustive match remain a compile-time correctness anchor without forcing the runtime path through it.

    3. Create `src/SmartRouter.Core/Ports.fs` per ARCHITECTURE.md "Core Ports" section. Module declaration: `module SmartRouter.Core.Ports`. Required interfaces:
       ```fsharp
       module SmartRouter.Core.Ports

       open System
       open System.Collections.Generic
       open System.Threading
       open System.Threading.Tasks
       open SmartRouter.Core.Domain

       type IUpstreamClient =
           abstract member CompleteAsync:
               req: RouterRequest -> target: ModelId -> ct: CancellationToken
               -> Task<Result<string, RouterError>>
           abstract member StreamAsync:
               req: RouterRequest -> target: ModelId -> ct: CancellationToken
               -> IAsyncEnumerable<Result<string, RouterError>>

       type IClock =
           abstract member UtcNow: unit -> DateTimeOffset

       type IHealthProbe =
           abstract member IsReachableAsync:
               target: ModelId -> ct: CancellationToken
               -> Task<bool>
       ```
       `IAsyncEnumerable<T>` lives in `System.Collections.Generic` (BCL). This satisfies ARCH-05 (SSE crosses port boundary as `IAsyncEnumerable<Result<string, RouterError>>` — no `HttpResponseMessage` leaks). Phase 2 will implement `StreamAsync`; Phase 1 only requires the signature compiles.

    4. Update `src/SmartRouter.Core/SmartRouter.Core.fsproj` `<Compile>` order (top-to-bottom F# constraint). Replace the placeholder `<ItemGroup>` with:
       ```xml
       <ItemGroup>
         <Compile Include="Domain.fs" />
         <Compile Include="Routing.fs" />
         <Compile Include="Ports.fs" />
       </ItemGroup>
       ```
       Order matters: `Routing.fs` references `Domain` types; `Ports.fs` references `Domain` types.

    **Anti-patterns to avoid:**
    - Do NOT add `| _ -> ...` catch-alls in `taskToDecision`, `tryParseTaskType`, or `tryParseModelAlias`. The exhaustiveness check is the safety net for future task additions (ROUT-02 + REQUIREMENTS.md ARCH-03).
    - Do NOT make `routeRequest`'s runtime dispatch call `taskToDecision`. The hardcoded F# match was the recommended-but-rejected default; CONTEXT.md explicitly locks the config-driven path. `taskToDecision` survives only as a validation/test utility.
    - Do NOT add `IOptions<T>`, `Microsoft.Extensions.Options`, or any ASP.NET configuration-binding type to Core. `RoutingConfig` is a plain record — the Cli layer (plan 01-03) does the JSON binding and constructs the record at composition time (ARCH-01).
    - Do NOT hardcode the heuristic threshold (`3`) or keyword list inside `applyHeuristic` / `scoreComplexity`. They must read from the `RoutingConfig` argument so operators editing `appsettings.json` change runtime behavior (ROUT-05).
    - Do NOT introduce `async {}` anywhere in Core. Use `task {}` if a CE is needed (none should be required in plan 01-02 — all functions are pure synchronous).
    - Do NOT call `IHealthProbe` from `routeRequest` — fallback policy lives in adapters per ARCHITECTURE.md anti-pattern #6.
    - Do NOT log inside Core (no `Log.Information` calls). The endpoint reads `RoutingDecision.Reason` and logs it.
  </action>
  <verify>
    From repo root:
    ```
    # 1. Core compiles
    dotnet build src/SmartRouter.Core/SmartRouter.Core.fsproj    # exit 0

    # 2. async-ban guard still passes
    ./scripts/check-no-async.sh                                  # exit 0

    # 3. No catch-all matches in Routing.fs (DU exhaustiveness)
    grep -n '| _ ->' src/SmartRouter.Core/Routing.fs            # exit 1 (no matches)

    # 4. Core has no forbidden imports (Serilog/AspNetCore/HttpClient/IOptions)
    grep -E '^open (Serilog|Microsoft\.AspNetCore|System\.Net\.Http|Microsoft\.Extensions\.Options)' src/SmartRouter.Core/*.fs
    # Expected: empty (exit 1)

    # 5. routeRequest signature is config-parameterized (CONTEXT.md compliance)
    grep -E 'let routeRequest .*RoutingConfig.*RouterRequest' src/SmartRouter.Core/Routing.fs   # 1 match
    # OR equivalent F# signature shape — verify `routeRequest` takes RoutingConfig as first arg.

    # 6. taskToDecision is NOT called by the runtime path (it's only a validation/test utility)
    #    Inspect routeRequest and tryTaskTable bodies — they must NOT contain `taskToDecision`.
    awk '/^let routeRequest/,/^let [^ ]/' src/SmartRouter.Core/Routing.fs | grep -F 'taskToDecision'   # exit 1 (no matches)
    awk '/^let tryTaskTable/,/^let [^ ]/' src/SmartRouter.Core/Routing.fs | grep -F 'taskToDecision'  # exit 1 (no matches)
    # The only callers of taskToDecision should be `canonicalTaskTable` (and tests / Cli validator added later).

    # 7. RoutingConfig is defined in Domain.fs as a plain record
    grep -F 'type RoutingConfig' src/SmartRouter.Core/Domain.fs    # 1 match

    # 8. The whole solution still builds
    dotnet build SmartRouter.slnx                                # exit 0
    ```

    Smoke-test the negative ARCH-01 case to prove Core doesn't actually depend on Serilog/HttpClient/IOptions: temporarily remove all NuGet packages from Cli (`Serilog*`, `Microsoft.Extensions.Http*`, `FSharp.Control.TaskSeq`) — `dotnet build src/SmartRouter.Core` must still succeed. Restore the Cli packages afterward.
  </verify>
  <done>
    `dotnet build src/SmartRouter.Core` exits 0. `routeRequest` signature is `RoutingConfig -> RouterRequest -> Result<RoutingDecision, RouterError>`. `taskToDecision` is not invoked by `routeRequest` or `tryTaskTable`. `RoutingConfig` is a plain F# record in `Domain.fs` (no `IOptions<T>`). The exhaustiveness grep returns no `| _ ->` matches. `check-no-async.sh` exits 0. Core compiles even with all Cli NuGet packages temporarily uninstalled.
  </done>
</task>

<task type="auto">
  <name>Task 2: Copy Json.fs + Logging.fs adapters from blueCode (verbatim, module rename only) and wire into Cli .fsproj</name>
  <files>
    src/SmartRouter.Cli/Adapters/Json.fs,
    src/SmartRouter.Cli/Adapters/Logging.fs,
    src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  </files>
  <action>
    1. Read `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/Logging.fs`. Copy it to `src/SmartRouter.Cli/Adapters/Logging.fs` with **only one change**: rename the module declaration line from `module BlueCode.Cli.Adapters.Logging` to `module SmartRouter.Cli.Adapters.Logging`. Everything else — including the `levelSwitch`, the `configure ()` body with `standardErrorFromLevel = System.Nullable<LogEventLevel>(LogEventLevel.Verbose)`, and `shutdown ()` — is verbatim. The `standardErrorFromLevel = LogEventLevel.Verbose` line is load-bearing for OBS-04 (all log levels go to stderr).

    2. Read `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/Json.fs`. The blueCode file mixes pure STJ options with blueCode-specific extraction pipelines (`parseLlmResponse`, `extractLlmStep`, etc.) that depend on `BlueCode.Core.Domain` types. SmartRouter only needs the `jsonOptions` binding. Per RESEARCH.md Section "Code Examples → Json.fs", create a minimal `src/SmartRouter.Cli/Adapters/Json.fs`:
       ```fsharp
       module SmartRouter.Cli.Adapters.Json

       open System.Text.Json
       open System.Text.Json.Serialization

       /// Shared System.Text.Json options for all JSON round-trips.
       /// JsonFSharpConverter with WithUnionUnwrapFieldlessTags serializes
       /// fieldless DU cases as bare strings ("System", "User") instead of
       /// {"Case": "System"}. This matches the blueCode wire-format convention
       /// and the OpenAI message-role JSON shape.
       let jsonOptions: JsonSerializerOptions =
           let opts = JsonSerializerOptions()
           opts.Converters.Add(JsonFSharpConverter(JsonFSharpOptions.Default().WithUnionUnwrapFieldlessTags(true)))
           opts
       ```
       Do NOT copy the blueCode-specific `parseLlmResponse` / `extractLlmStep` / `llmStepSchema` — they belong to blueCode's agent-loop domain and are out of scope for the router.

    3. Update `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` `<Compile>` items. Replace the placeholder ItemGroup with:
       ```xml
       <ItemGroup>
         <!-- Adapters (Infrastructure) — must precede Endpoints and Program -->
         <Compile Include="Adapters/Json.fs" />
         <Compile Include="Adapters/Logging.fs" />
         <!-- Adapters/QwenUpstreamClient.fs added by plan 01-03 -->
         <!-- Endpoints/ChatCompletions.fs added by plan 01-03 -->
         <!-- CompositionRoot.fs + Program.fs added by plan 01-03 -->
       </ItemGroup>
       <ItemGroup>
         <None Update="appsettings.json">
           <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
         </None>
       </ItemGroup>
       ```
       (Plan 01-01 may have already added the `<None Update>` block; if so, leave it.)

    **Anti-patterns to avoid:**
    - Do NOT add `WriteTo.File(...)` or any non-Console sink in `Logging.fs`. Stderr-only is OBS-04. File logging is a Phase 5 concern (if at all).
    - Do NOT change `standardErrorFromLevel` to `Information` or anything other than `Verbose`. The blueCode invariant is that ALL Serilog levels (including Verbose / Debug) go to stderr; stdout is reserved for application output.
    - Do NOT import `BlueCode.Core.Domain` or any other `BlueCode.*` namespace in the copied files — those won't resolve and indicate a copy mistake.
  </action>
  <verify>
    ```
    # 1. Cli compiles with the two new adapter files
    dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj    # exit 0

    # 2. Logging.fs has the stderr-routing line (OBS-04 invariant)
    grep -F 'standardErrorFromLevel' src/SmartRouter.Cli/Adapters/Logging.fs    # 1 match
    grep -F 'LogEventLevel.Verbose' src/SmartRouter.Cli/Adapters/Logging.fs     # at least 1 match

    # 3. Json.fs uses WithUnionUnwrapFieldlessTags (matches blueCode wire convention)
    grep -F 'WithUnionUnwrapFieldlessTags' src/SmartRouter.Cli/Adapters/Json.fs # 1 match

    # 4. No stale BlueCode references leaked
    grep -rn 'BlueCode' src/SmartRouter.Cli/                   # exit 1 (no matches)

    # 5. Whole solution still builds
    dotnet build SmartRouter.slnx                              # exit 0
    ```
  </verify>
  <done>
    Cli builds with `Adapters/Json.fs` and `Adapters/Logging.fs` present; `grep` confirms `standardErrorFromLevel = ... Verbose` is in `Logging.fs`; no `BlueCode` symbols remain.
  </done>
</task>

<task type="auto">
  <name>Task 3: Write RoutingTests.fs Expecto suite covering all routing pipeline branches; wire into Tests.fsproj and uncomment in rootTests</name>
  <files>
    tests/SmartRouter.Tests/RoutingTests.fs,
    tests/SmartRouter.Tests/SmartRouter.Tests.fsproj,
    tests/SmartRouter.Tests/RouterTests.fs
  </files>
  <action>
    1. Create `tests/SmartRouter.Tests/RoutingTests.fs`. The helper builds both a `RouterRequest` and a default `RoutingConfig` so every test calls the config-parameterized `routeRequest` signature. `defaultConfig` mirrors the canonical baseline (`Routing.defaultRoutingConfig` from Core), which equals what `appsettings.json` Routing.TaskTable / Routing.Keywords / Routing.ComplexityThreshold will produce after plan 01-03's binding. Keeping this default in the helper means tests assert the **same** routing decisions as before — only the call shape changes from `routeRequest req` to `routeRequest config req`.

       ```fsharp
       module SmartRouter.Tests.RoutingTests

       open Expecto
       open SmartRouter.Core.Domain
       open SmartRouter.Core.Routing

       /// Default RoutingConfig for tests — mirrors the canonical task table + threshold=3 +
       /// canonical keyword list (the same values appsettings.json ships with). Tests that need
       /// a non-default config (e.g., to verify operator-edited TaskTable changes runtime
       /// behavior) build their own RoutingConfig inline.
       let private defaultConfig : RoutingConfig = Routing.defaultRoutingConfig

       let private mkReq (task: string option) (model: string option) (content: string) (msgs: int) : RouterRequest =
           let messages =
               [ for i in 1 .. msgs ->
                   { Role = User; Content = content } ]
           { Messages      = messages
             ModelOverride = model
             Task          = task
             Stream        = false
             Temperature   = None
             TopP          = None
             MaxTokens     = None
             UnknownFields = Map.empty }

       /// Convenience: route with the default config. Every test in this suite uses this
       /// unless it's specifically testing config-driven behavior.
       let private route req = routeRequest defaultConfig req

       let tests =
           testList "routing" [

               // ── ROUT-01: explicit model override short-circuits ──────────
               testCase "model override 35b routes to Qwen35B with ExplicitModelOverride reason" <| fun () ->
                   let req = mkReq None (Some "35b") "anything" 1
                   match route req with
                   | Ok d ->
                       Expect.equal d.Target Qwen35B "Target"
                       match d.Reason with
                       | ExplicitModelOverride alias -> Expect.equal alias "35b" "alias preserved"
                       | r -> failtestf "wrong reason: %A" r
                   | Error e -> failtestf "expected Ok, got Error %A" e

               testCase "model override 122b routes to Qwen122B" <| fun () ->
                   let req = mkReq None (Some "122b") "x" 1
                   match route req with
                   | Ok d -> Expect.equal d.Target Qwen122B ""
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "model override aliases are case-insensitive" <| fun () ->
                   let req = mkReq None (Some "QWEN-122B") "x" 1
                   match route req with
                   | Ok d -> Expect.equal d.Target Qwen122B ""
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "unknown model alias falls through to next stage (no error)" <| fun () ->
                   // model=gpt-4o + task=retrieval → task table wins
                   let req = mkReq (Some "retrieval") (Some "gpt-4o") "x" 1
                   match route req with
                   | Ok d ->
                       Expect.equal d.Target Qwen35B ""
                       match d.Reason with
                       | ExplicitTask Retrieval -> ()
                       | r -> failtestf "expected ExplicitTask Retrieval, got %A" r
                   | Error e -> failtestf "expected Ok, got %A" e

               // ── ROUT-02: explicit task table ──────────────────────────────
               testCase "task graph_indexing → Qwen122B High priority" <| fun () ->
                   let req = mkReq (Some "graph_indexing") None "x" 1
                   match route req with
                   | Ok d ->
                       Expect.equal d.Target Qwen122B "Target"
                       Expect.equal d.Priority High "Priority"
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "task compiler_debug → Qwen122B High" <| fun () ->
                   let req = mkReq (Some "compiler_debug") None "x" 1
                   match route req with
                   | Ok d ->
                       Expect.equal d.Target Qwen122B ""
                       Expect.equal d.Priority High ""
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "task architecture_analysis → Qwen122B High" <| fun () ->
                   let req = mkReq (Some "architecture_analysis") None "x" 1
                   match route req with
                   | Ok d ->
                       Expect.equal d.Target Qwen122B ""
                       Expect.equal d.Priority High ""
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "task dependency_analysis → Qwen122B Low" <| fun () ->
                   let req = mkReq (Some "dependency_analysis") None "x" 1
                   match route req with
                   | Ok d ->
                       Expect.equal d.Target Qwen122B ""
                       Expect.equal d.Priority Low ""
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "task reasoning → Qwen122B Low" <| fun () ->
                   let req = mkReq (Some "reasoning") None "x" 1
                   match route req with
                   | Ok d ->
                       Expect.equal d.Target Qwen122B ""
                       Expect.equal d.Priority Low ""
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "task retrieval → Qwen35B" <| fun () ->
                   let req = mkReq (Some "retrieval") None "x" 1
                   match route req with
                   | Ok d -> Expect.equal d.Target Qwen35B ""
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "task summary → Qwen35B" <| fun () ->
                   let req = mkReq (Some "summary") None "x" 1
                   match route req with
                   | Ok d -> Expect.equal d.Target Qwen35B ""
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "task name is case-insensitive" <| fun () ->
                   let req = mkReq (Some "GRAPH_INDEXING") None "x" 1
                   match route req with
                   | Ok d -> Expect.equal d.Target Qwen122B ""
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "unknown task returns UnsupportedTask error" <| fun () ->
                   let req = mkReq (Some "foobar") None "x" 1
                   Expect.equal (route req) (Error (UnsupportedTask "foobar")) ""

               // ── ROUT-03 + ROUT-04: heuristic fallback (35B-biased) ────────
               testCase "short hello-world → Qwen35B (heuristic, score below threshold)" <| fun () ->
                   let req = mkReq None None "hello" 1
                   match route req with
                   | Ok d ->
                       Expect.equal d.Target Qwen35B ""
                       match d.Reason with
                       | Heuristic s -> Expect.isLessThan s 3 "score < threshold"
                       | r -> failtestf "expected Heuristic, got %A" r
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "long complex prompt with keywords → Qwen122B (heuristic, score >= 3)" <| fun () ->
                   // 500 * 44 chars ≈ 22000 chars → length tier +4; 4 keywords → +4; total 8 ≥ 3
                   let longText = System.String.replicate 500 "recursive compiler architecture dependency "
                   let req = mkReq None None longText 1
                   match route req with
                   | Ok d ->
                       Expect.equal d.Target Qwen122B ""
                       match d.Reason with
                       | Heuristic s -> Expect.isGreaterThanOrEqual s 3 "score >= threshold"
                       | r -> failtestf "expected Heuristic, got %A" r
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "code block (triple backtick) contributes +1 to heuristic score" <| fun () ->
                   let withBackticks = "```\nfoo\n```"
                   let withoutBackticks = "foo"
                   let s1 = scoreComplexity defaultConfig (mkReq None None withBackticks 1)
                   let s2 = scoreComplexity defaultConfig (mkReq None None withoutBackticks 1)
                   Expect.equal (s1 - s2) 1 "code-block contributes exactly +1"

               // ── ROUT-05: config-driven dispatch (proves runtime path reads config) ──
               testCase "config-driven TaskTable: moving 'retrieval' to Qwen122B reroutes to 122B at runtime" <| fun () ->
                   // Operator-edit simulation: take the default config and remap retrieval to 122B.
                   // This is the unit-test counterpart to the Phase 1-03 live verification command
                   // (edit appsettings.json, restart, observe new routing). Proves runtime dispatch
                   // reads the config map, not a hardcoded F# match.
                   let edited =
                       { defaultConfig with
                           TaskTable = defaultConfig.TaskTable |> Map.add "retrieval" (Qwen122B, Low) }
                   let req = mkReq (Some "retrieval") None "x" 1
                   match routeRequest edited req with
                   | Ok d ->
                       Expect.equal d.Target Qwen122B "retrieval should now route to 122B per edited config"
                       match d.Reason with
                       | ExplicitTask Retrieval -> ()
                       | r -> failtestf "expected ExplicitTask Retrieval, got %A" r
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "config-driven threshold: lowering threshold to 1 promotes a single keyword to 122B" <| fun () ->
                   let edited = { defaultConfig with ComplexityThreshold = 1 }
                   let req = mkReq None None "recursive" 1   // 1 keyword → score 1
                   match routeRequest edited req with
                   | Ok d -> Expect.equal d.Target Qwen122B "score >= threshold(1) should escalate"
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "config-driven keywords: empty keyword list neutralizes heuristic" <| fun () ->
                   let edited = { defaultConfig with Keywords = [] }
                   let req = mkReq None None "recursive compiler architecture dependency" 1
                   match routeRequest edited req with
                   | Ok d -> Expect.equal d.Target Qwen35B "no keyword hits → score 0 → 35B"
                   | Error e -> failtestf "expected Ok, got %A" e

               // ── Override precedence (ROUT-01 beats ROUT-02 beats ROUT-03) ─
               testCase "model override beats task field" <| fun () ->
                   let req = mkReq (Some "graph_indexing") (Some "35b") "x" 1
                   match route req with
                   | Ok d ->
                       Expect.equal d.Target Qwen35B "model override should win"
                       match d.Reason with
                       | ExplicitModelOverride _ -> ()
                       | r -> failtestf "expected ExplicitModelOverride, got %A" r
                   | Error e -> failtestf "expected Ok, got %A" e

               testCase "task beats heuristic (long prompt + retrieval task → 35B not 122B)" <| fun () ->
                   let longText = System.String.replicate 500 "recursive compiler architecture dependency "
                   let req = mkReq (Some "retrieval") None longText 1
                   match route req with
                   | Ok d ->
                       Expect.equal d.Target Qwen35B "task table should beat heuristic"
                       match d.Reason with
                       | ExplicitTask Retrieval -> ()
                       | r -> failtestf "expected ExplicitTask Retrieval, got %A" r
                   | Error e -> failtestf "expected Ok, got %A" e

               // ── Tie-break (latency-first) ─────────────────────────────────
               testCase "score exactly at threshold-minus-one routes to 35B (latency-first bias)" <| fun () ->
                   // tie-break: scores below 3 → 35B; verifies the < not <= boundary
                   let twoKeywords = "recursive dependency"  // 2 keywords, short → score 2
                   let req = mkReq None None twoKeywords 1
                   match route req with
                   | Ok d -> Expect.equal d.Target Qwen35B ""
                   | Error e -> failtestf "expected Ok, got %A" e
           ]
       ```

    2. Update `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` `<ItemGroup>` to compile `RoutingTests.fs` BEFORE `RouterTests.fs` (entry point must be last):
       ```xml
       <ItemGroup>
         <Compile Include="RoutingTests.fs" />
         <Compile Include="RouterTests.fs" />
       </ItemGroup>
       ```

    3. Update `tests/SmartRouter.Tests/RouterTests.fs` to uncomment the `RoutingTests.tests` entry in `rootTests`:
       ```fsharp
       let rootTests : Test list =
           [
               SmartRouter.Tests.RoutingTests.tests
               // SmartRouter.Tests.IntegrationTests.tests   // <- added in later phases
           ]
       ```

    **Anti-patterns to avoid:**
    - Do NOT use `[<Tests>]` attribute on `RoutingTests.tests` (auto-discovery is banned per PITFALL-26 / TEST-07).
    - Do NOT mix routing tests and integration tests in the same testList. Integration tests live in their own module added in Phase 5.
    - Do NOT depend on `Console.SetOut` in routing tests (no `testSequenced` needed).
    - Do NOT call `routeRequest` without a `RoutingConfig` argument — that signature does not exist. The helper `route req = routeRequest defaultConfig req` exists precisely so routine tests stay terse while still going through the config-driven path.
    - Do NOT hand-build a `RoutingConfig` literal in every test. Use `defaultConfig` (= `Routing.defaultRoutingConfig`) for the canonical baseline; only construct an inline config when specifically asserting that an operator-edited config changes runtime behavior (the three new ROUT-05 tests above).
  </action>
  <verify>
    ```
    # All tests pass
    dotnet test SmartRouter.slnx        # exit 0; 21+ tests run, 0 failures

    # Or run the project directly:
    dotnet run --project tests/SmartRouter.Tests --no-build

    # Confirm the test entry uncommented in rootTests
    grep -F 'RoutingTests.tests' tests/SmartRouter.Tests/RouterTests.fs    # 1 match (uncommented)

    # Confirm RoutingTests.fs is BEFORE RouterTests.fs in the .fsproj
    grep -nE '<Compile Include="(RoutingTests|RouterTests)\.fs"' tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    # Expected: RoutingTests.fs line < RouterTests.fs line

    # Confirm test helper threads RoutingConfig through every call (ROUT-05 / config-driven dispatch)
    grep -F 'defaultConfig' tests/SmartRouter.Tests/RoutingTests.fs        # 1+ match
    grep -F 'routeRequest defaultConfig' tests/SmartRouter.Tests/RoutingTests.fs  # 1+ match (via the `route` helper)
    grep -F 'routeRequest edited' tests/SmartRouter.Tests/RoutingTests.fs  # 1+ match (config-edit tests)
    ```
  </verify>
  <done>
    `dotnet test SmartRouter.slnx` runs the full Expecto suite and reports 0 failures across at least 21 test cases covering: (a) all 7 task table entries, (b) model override + case-insensitivity + fall-through on unknown alias, (c) heuristic short→35B and long→122B, (d) override precedence chain, (e) tie-break behavior, (f) **three ROUT-05 config-driven tests proving that editing the `RoutingConfig` (TaskTable remap, threshold change, empty keyword list) changes runtime routing behavior — the unit-test counterpart to plan 01-03's live `appsettings.json` edit verification.** All `routeRequest` calls go through the `RoutingConfig`-parameterized signature. Phase Success Criteria #2 and #3 are now demonstrably satisfied.
  </done>
</task>

</tasks>

<verification>
**End-to-end plan verification:**

```bash
# 1. Solution builds clean with new Core + adapters
dotnet build SmartRouter.slnx                                # exit 0

# 2. Async-ban guard still passes (Core uses task only)
./scripts/check-no-async.sh                                  # exit 0

# 3. No DU catch-alls in Routing.fs (exhaustiveness contract)
grep -n '| _ ->' src/SmartRouter.Core/Routing.fs            # exit 1, no matches

# 4. Core has no forbidden references
grep -rE '^open (Serilog|Microsoft\.AspNetCore|System\.Net\.Http)' src/SmartRouter.Core/   # exit 1

# 5. ARCH-01 demonstration: temporarily uninstall Cli's Serilog/HttpClient/AspNetCore
#    packages and confirm Core still builds. Then restore.
#    (Optional defensive verification — script for the executor:)
#    dotnet remove src/SmartRouter.Cli package Serilog
#    dotnet remove src/SmartRouter.Cli package Serilog.Sinks.Console
#    dotnet remove src/SmartRouter.Cli package Serilog.AspNetCore
#    dotnet remove src/SmartRouter.Cli package Microsoft.Extensions.Http.Resilience
#    dotnet build src/SmartRouter.Core   # MUST still exit 0
#    Then restore each package.

# 6. Full test suite passes
dotnet test SmartRouter.slnx                                 # 0 failures, all RoutingTests pass
```
</verification>

<success_criteria>
- All RoutingTests pass: model override (3 cases incl. case-insensitivity + alias fallthrough), all 7 task table entries, unknown task error, heuristic short→35B, heuristic long→122B, code-block scoring, override precedence (model > task, task > heuristic), tie-break (35B latency-first), **plus 3 config-driven dispatch tests proving runtime reads `RoutingConfig.TaskTable` / `Keywords` / `ComplexityThreshold` (ROUT-05)**.
- `Routing.routeRequest` signature is `RoutingConfig -> RouterRequest -> Result<RoutingDecision, RouterError>` — config-parameterized, not hardcoded.
- `taskToDecision` survives in Core as an exhaustive utility (compile-time DU coverage anchor) but is NOT called by the runtime path; it's referenced only by `canonicalTaskTable` / `defaultRoutingConfig` (which tests use) and by plan 01-03's startup validator.
- `Routing.fs` contains no `| _ ->` over DU patterns (`grep -n '| _ ->'` returns no hits).
- `Core/*.fs` files have no `open Serilog`, `open Microsoft.AspNetCore`, `open System.Net.Http`, or `open Microsoft.Extensions.Options` lines (`RoutingConfig` is a plain F# record — ARCH-01 preserved).
- `Logging.fs` retains `standardErrorFromLevel = ... LogEventLevel.Verbose` from blueCode (OBS-04 invariant).
- `Json.fs` is the minimal version (jsonOptions only) — no blueCode-specific extraction pipelines.
- `RouterTests.fs` `rootTests` list contains the uncommented `RoutingTests.tests` entry.

**Requirements satisfied by this plan:**
- ARCH-01 (Core has only FsToolkit.ErrorHandling NuGet ref; `RoutingConfig` is a plain record, no `IOptions<T>` in Core)
- ARCH-02 (Core uses `task {}` exclusively — script enforces, plan adds no `async {}`)
- ARCH-03 (composable three-stage pipeline as pure functions, parameterized by `RoutingConfig`)
- ARCH-05 (`IUpstreamClient.StreamAsync` returns `IAsyncEnumerable<Result<string, RouterError>>` — no HttpResponseMessage in port)
- ARCH-07 (`IUpstreamClient` provider seam exists; only Qwen impl arrives in 01-03)
- ROUT-01 (model override short-circuit — `tryModelOverride`)
- ROUT-02 (task→model dispatch — config-driven runtime path; exhaustive `taskToDecision` retained as validation/baseline utility)
- ROUT-03 (heuristic fallback — `applyHeuristic`, config-driven)
- ROUT-04 (35B-biased heuristic — config-driven threshold; default 3, tie→35B)
- ROUT-05 (routing rules driven by `RoutingConfig` record; the Core half — Cli wiring + JSON binding lands in 01-03)
- ROUT-06 (`RoutingReason` DU with all 4 cases)
- OBS-04 (Logging.fs verbatim → stderr)
- TEST-07 (RoutingTests.tests added to explicit `rootTests` list)
</success_criteria>

<output>
After completion, create `.planning/phases/01-foundation/01-02-SUMMARY.md` with:
- Number of tests in `RoutingTests.tests` and pass/fail status
- The exact command output of `./scripts/check-no-async.sh` and `grep -n '| _ ->' src/SmartRouter.Core/Routing.fs`
- Confirmation Logging.fs is byte-for-byte (or near-byte-for-byte) identical to blueCode's, with only the module name changed
- Any deviations from the plan
</output>
