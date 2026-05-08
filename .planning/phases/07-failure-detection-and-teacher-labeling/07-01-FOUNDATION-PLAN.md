---
phase: 07-failure-detection-and-teacher-labeling
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/SmartRouter.Core/RetrainingPorts.fs
  - src/SmartRouter.Core/SmartRouter.Core.fsproj
  - src/SmartRouter.Cli/Adapters/FailureDetector.fs
  - src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
  - src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
autonomous: true

must_haves:
  truths:
    - "Core/RetrainingPorts.fs exists, BCL-only (no HttpClient/Serilog/ML/AspNetCore imports), and defines IFailureDetector, ITeacherLabeler, IHardCaseDatasetWriter interfaces plus RoutingLabel DU and LabelResult DU"
    - "Three Cli adapter STUB files exist (FailureDetector.fs, TeacherLabeler.fs, HardCaseDatasetWriter.fs) — each is a compilable module with NotImplementedException-throwing implementations of its interface; subsequent Wave 2 plans replace the stub bodies with real implementations"
    - "Core.fsproj <Compile> list includes RetrainingPorts.fs after Routing.fs and before Ports.fs (matches CONTEXT.md ordering — RetrainingPorts has no dependency on MLPorts/ML/Routing, so this is the cleanest placement)"
    - "Cli.fsproj <Compile> list includes all three new adapter files AFTER QueueDispatcher.fs and BEFORE Endpoints — the same compile zone as DecisionLogWriter.fs"
    - "dotnet build SmartRouter.slnx succeeds with 0 warnings (TreatWarningsAsErrors stays on); existing 50-pass+10-ignored test count is unchanged"
  artifacts:
    - path: "src/SmartRouter.Core/RetrainingPorts.fs"
      provides: "RoutingLabel DU (Route35B|Route122B), LabelResult DU (Labeled|Unparseable|Skipped|Failed), HardCaseEntry record, HardCase record, IFailureDetector, ITeacherLabeler, IHardCaseDatasetWriter interfaces"
      contains: "IFailureDetector"
    - path: "src/SmartRouter.Cli/Adapters/FailureDetector.fs"
      provides: "FailureDetector class skeleton implementing IFailureDetector — methods throw NotImplementedException; Wave 2 plan 07-02 fills in"
      contains: "IFailureDetector"
    - path: "src/SmartRouter.Cli/Adapters/TeacherLabeler.fs"
      provides: "TeacherLabeler class skeleton implementing ITeacherLabeler — methods throw NotImplementedException; Wave 2 plan 07-03 fills in"
      contains: "ITeacherLabeler"
    - path: "src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs"
      provides: "HardCaseDatasetWriter class skeleton implementing IHardCaseDatasetWriter — methods throw NotImplementedException; Wave 2 plan 07-04 fills in"
      contains: "IHardCaseDatasetWriter"
    - path: "src/SmartRouter.Core/SmartRouter.Core.fsproj"
      provides: "RetrainingPorts.fs registered as <Compile> entry after Routing.fs and before Ports.fs (per CONTEXT.md)"
      contains: "RetrainingPorts.fs"
    - path: "src/SmartRouter.Cli/SmartRouter.Cli.fsproj"
      provides: "Three new adapter <Compile> entries registered after QueueDispatcher.fs and before Endpoints/"
      contains: "FailureDetector.fs"
  key_links:
    - from: "src/SmartRouter.Cli/Adapters/FailureDetector.fs"
      to: "SmartRouter.Core.RetrainingPorts.IFailureDetector"
      via: "interface implementation"
      pattern: "interface IFailureDetector"
    - from: "src/SmartRouter.Cli/Adapters/TeacherLabeler.fs"
      to: "SmartRouter.Core.RetrainingPorts.ITeacherLabeler"
      via: "interface implementation"
      pattern: "interface ITeacherLabeler"
    - from: "src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs"
      to: "SmartRouter.Core.RetrainingPorts.IHardCaseDatasetWriter"
      via: "interface implementation"
      pattern: "interface IHardCaseDatasetWriter"
---

<objective>
Land the Phase 7 foundation: pure-Core port interfaces (`IFailureDetector`, `ITeacherLabeler`, `IHardCaseDatasetWriter` plus supporting DUs/records) and three Cli adapter STUB files that each implement their interface with `NotImplementedException` bodies. Both .fsproj files updated. Build clean. The three adapter stubs unblock Wave 2 parallel plans (07-02, 07-03, 07-04) — each takes exclusive ownership of one stub file and fills in the real implementation without touching .fsproj or the other two adapters. Pure-Core invariant preserved: RetrainingPorts.fs is BCL-only.

Purpose: This is the "scaffolding" plan that lets three independent adapter implementations land in parallel without .fsproj write conflicts. The stubs compile, the build is green, and tests pass unchanged because no DI registration consumes the new interfaces yet (Plan 07-05 wires them in).

Output:
- Core/RetrainingPorts.fs (interfaces + DUs + records)
- 3 stub adapter files in Cli/Adapters/ (each ~30-40 lines)
- Core.fsproj + Cli.fsproj updated with new <Compile> entries in correct order
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md
@.planning/phases/07-failure-detection-and-teacher-labeling/07-CONTEXT.md
@.planning/phases/07-failure-detection-and-teacher-labeling/07-RESEARCH.md
@src/SmartRouter.Core/MLPorts.fs
@src/SmartRouter.Core/SmartRouter.Core.fsproj
@src/SmartRouter.Cli/SmartRouter.Cli.fsproj
@src/SmartRouter.Cli/Adapters/DecisionLogger.fs
</context>

<tasks>

<task type="auto">
  <name>Task 1: Create Core/RetrainingPorts.fs (BCL-only port interfaces and types)</name>
  <files>
    src/SmartRouter.Core/RetrainingPorts.fs
    src/SmartRouter.Core/SmartRouter.Core.fsproj
  </files>
  <action>
Create `src/SmartRouter.Core/RetrainingPorts.fs` (module `SmartRouter.Core.RetrainingPorts`) with the following content. **BCL-only imports**: `System`, `System.Threading`, `System.Threading.Tasks`. NO references to Serilog, HttpClient, AspNetCore, ML.NET, or FSharp.SystemTextJson — pure-Core invariant (ARCH-01).

```fsharp
module SmartRouter.Core.RetrainingPorts

open System
open System.Threading
open System.Threading.Tasks

/// Teacher's binary label decision for a hard case.
/// Mirrors RoutingDecision.Target but lives in retraining domain — keep separate
/// from Domain.ModelId so the retraining vocabulary is independently evolvable.
type RoutingLabel =
    | Route35B
    | Route122B

/// Outcome of one teacher labeling call.
/// Labeled: parsed teacher response into a clean label.
/// Unparseable: teacher responded but response did not match ROUTE_35B / ROUTE_122B.
/// Skipped: cost cap hit (FAIL-03) or pre-flight check skipped this entry; no HTTP call made.
/// Failed: HTTP error after retries exhausted, or timeout.
type LabelResult =
    | Labeled    of label: RoutingLabel * teacherResponseExcerpt: string
    | Unparseable of rawResponse: string
    | Skipped    of reason: string
    | Failed     of error: string

/// One hard-case correlation_id surfaced by FailureDetector.
/// Carries enough context for downstream consumers (Phase 8 BackgroundService or
/// the --retrain CLI handler) to look up prompt text and feed TeacherLabeler.
/// prompt_text is None when extracted from logs (LOG-01 stores hash only); Some
/// when seeded synthetically OR when called inline by Phase 8's BackgroundService.
type HardCase =
    { CorrelationId          : string
      PromptHash             : string
      PromptKoreanCharRatio  : float
      RoutingAlgorithm       : string
      Target                 : string
      PromptText             : string option }

/// One entry written to datasets/hard-cases.jsonl.
/// Schema_version=1; Phase 8 reader branches on this field.
/// Cli's HardCaseDatasetWriter serializes this (with snake_case naming policy).
type HardCaseEntry =
    { SchemaVersion          : int
      CorrelationId          : string
      PromptHash             : string
      PromptText             : string
      Label                  : int    // 0 = Route35B, 1 = Route122B
      Source                 : string // "teacher" | "synthetic" | "operator"
      TeacherResponseExcerpt : string option
      LabeledAt              : DateTimeOffset
      PromptKoreanCharRatio  : float
      RoutingAlgorithm       : string
      Target                 : string }

/// Failure detector port — reads JSONL decision logs and returns hard cases.
/// FAIL-01: filter `fallback_used = true` records only.
/// Phase 10 will broaden the signal; FailureDetector code does not change.
/// Returns a list (not IAsyncEnumerable) — JSONL files are small enough.
type IFailureDetector =
    abstract member ExtractHardCases : ct: CancellationToken -> Task<HardCase list>

/// Teacher labeler port — calls 122B with prompt text + teacher prompt template,
/// parses ROUTE_35B / ROUTE_122B response. FAIL-02: 30s timeout, 3x retry on
/// transient HTTP failures. FAIL-03: enforces persistent daily cost cap before
/// each call; cap-hit returns Skipped without HTTP call.
type ITeacherLabeler =
    abstract member LabelAsync :
        promptText: string * correlationId: string * ct: CancellationToken
        -> Task<LabelResult>

/// Hard-case dataset writer port — appends entries to datasets/hard-cases.jsonl
/// via Channel + single-writer BackgroundService (mirrors DecisionLogWriter).
/// FAIL-04: dedupes on (correlation_id, prompt_hash); BoundedChannelFullMode.Wait
/// (back-pressure, never DropWrite — losing training data is unacceptable).
type IHardCaseDatasetWriter =
    abstract member AppendAsync :
        entry: HardCaseEntry * ct: CancellationToken
        -> Task<unit>
```

**File ordering rules:**
- Pure F# / BCL only — no other module references except System namespaces.
- Records use PascalCase F# field names (the Cli serializer applies SnakeCaseLower at JSON write time, mirroring DecisionLogger pattern).
- DU cases use PascalCase (matches RoutingReason DU style in Domain.fs).
- The interface methods use F# named arg syntax (`promptText: string * ...`) so test mocks read naturally.

**Update `src/SmartRouter.Core/SmartRouter.Core.fsproj`:** Add `<Compile Include="RetrainingPorts.fs" />` AFTER `<Compile Include="Routing.fs" />` and BEFORE `<Compile Include="Ports.fs" />` — this matches the CONTEXT.md placement decision. RetrainingPorts has no dependency on MLPorts/ML/Routing, so placing it after Routing.fs is the cleanest position (types-first ordering is preserved by virtue of being placed before Ports.fs which is the composition surface). Final ordering:

```xml
<Compile Include="Domain.fs" />
<Compile Include="Heuristic.fs" />
<Compile Include="MLPorts.fs" />
<Compile Include="ML.fs" />
<Compile Include="Routing.fs" />
<Compile Include="RetrainingPorts.fs" />   <!-- NEW: Phase 7 — placed after Routing.fs per CONTEXT.md -->
<Compile Include="Ports.fs" />
```
  </action>
  <verify>
- `dotnet build src/SmartRouter.Core/SmartRouter.Core.fsproj -nologo --tl:off` succeeds with 0 warnings.
- `grep -RIn "Serilog\|HttpClient\|Microsoft\.ML\|Microsoft\.AspNetCore\|FSharp\.SystemTextJson" src/SmartRouter.Core/RetrainingPorts.fs` returns NOTHING (pure-Core invariant).
- `grep -n "IFailureDetector\|ITeacherLabeler\|IHardCaseDatasetWriter" src/SmartRouter.Core/RetrainingPorts.fs` returns 3 hits — one interface declaration each.
- `grep -n "RetrainingPorts\.fs" src/SmartRouter.Core/SmartRouter.Core.fsproj` returns 1 hit; verify line position is between Routing.fs and Ports.fs (per CONTEXT.md placement).
  </verify>
  <done>
RetrainingPorts.fs ships with 3 interfaces + 4 supporting types (RoutingLabel, LabelResult, HardCase, HardCaseEntry). Core builds clean. No NuGet additions to Core. Existing tests unaffected.
  </done>
</task>

<task type="auto">
  <name>Task 2: Create three Cli adapter STUB files implementing the new interfaces with NotImplementedException</name>
  <files>
    src/SmartRouter.Cli/Adapters/FailureDetector.fs
    src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
    src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
    src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  </files>
  <action>
Create three STUB adapter files. Each file is a compilable module that implements its interface with `raise (NotImplementedException ...)` bodies. Wave 2 plans (07-02, 07-03, 07-04) replace the stub bodies with real implementations — each plan owns ONE stub file exclusively, eliminating .fsproj write conflicts.

**Step 1 — `src/SmartRouter.Cli/Adapters/FailureDetector.fs`** (module `SmartRouter.Cli.Adapters.FailureDetector`):

```fsharp
module SmartRouter.Cli.Adapters.FailureDetector

open System
open System.Threading
open System.Threading.Tasks
open SmartRouter.Core.RetrainingPorts

/// Reads logs/decisions/*.jsonl files, filters fallback_used=true records,
/// and returns hard-case entries. FAIL-01.
///
/// Stub at Wave 1 — Plan 07-02 replaces ExtractHardCasesAsync body with the
/// real JSONL reader. Constructor signature is final: (logsDirectory: string).
type FailureDetector(logsDirectory: string) =

    interface IFailureDetector with
        member _.ExtractHardCases(_ct: CancellationToken) : Task<HardCase list> =
            raise (NotImplementedException "FailureDetector.ExtractHardCases is a Wave 1 stub; Plan 07-02 fills this in")
```

**Step 2 — `src/SmartRouter.Cli/Adapters/TeacherLabeler.fs`** (module `SmartRouter.Cli.Adapters.TeacherLabeler`):

```fsharp
module SmartRouter.Cli.Adapters.TeacherLabeler

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open SmartRouter.Core.RetrainingPorts

/// Cli-only options bound from appsettings.json "TeacherLabeler" section.
/// Defaults applied at registration time in CompositionRoot (Plan 07-05).
[<CLIMutable>]
type TeacherLabelerOptions =
    { Endpoint        : string   // default "http://127.0.0.1:8001"
      PromptPath      : string   // default "prompts/teacher-prompt.md"
      DailyCallCap    : int      // default 1000
      TimeoutSeconds  : int      // default 30
      DatasetsDir     : string } // default "datasets" (cost-cap counter file lives here)

/// Calls 122B (or any teacher endpoint) with the teacher prompt + a hard case's
/// prompt text, parses ROUTE_35B / ROUTE_122B from the response. FAIL-02 + FAIL-03.
///
/// Stub at Wave 1 — Plan 07-03 replaces LabelAsync body with the real HTTP client +
/// prompt template loader + cost-cap enforcement. Constructor signature is final:
/// (httpFactory: IHttpClientFactory, options: TeacherLabelerOptions).
type TeacherLabeler(httpFactory: IHttpClientFactory, options: TeacherLabelerOptions) =

    interface ITeacherLabeler with
        member _.LabelAsync(_promptText: string, _correlationId: string, _ct: CancellationToken) : Task<LabelResult> =
            raise (NotImplementedException "TeacherLabeler.LabelAsync is a Wave 1 stub; Plan 07-03 fills this in")
```

**Step 3 — `src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs`** (module `SmartRouter.Cli.Adapters.HardCaseDatasetWriter`):

```fsharp
module SmartRouter.Cli.Adapters.HardCaseDatasetWriter

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open SmartRouter.Core.RetrainingPorts

/// Cli-only options bound from appsettings.json "HardCaseDataset" section.
[<CLIMutable>]
type HardCaseDatasetOptions =
    { Path            : string   // default "datasets/hard-cases.jsonl"
      ChannelCapacity : int }    // default 1000

/// Channel-backed BackgroundService that drains HardCaseEntry items and appends
/// them to datasets/hard-cases.jsonl. Mirrors DecisionLogWriter pattern.
///
/// Stub at Wave 1 — Plan 07-04 replaces AppendAsync + ExecuteAsync + StopAsync
/// bodies with the real Channel + single-writer logic. Constructor signature is
/// final: (options: HardCaseDatasetOptions). Inherits BackgroundService so the DI
/// registration AddHostedService<HardCaseDatasetWriter> in Plan 07-05 works.
type HardCaseDatasetWriter(options: HardCaseDatasetOptions) =
    inherit BackgroundService()

    interface IHardCaseDatasetWriter with
        member _.AppendAsync(_entry: HardCaseEntry, _ct: CancellationToken) : Task<unit> =
            raise (NotImplementedException "HardCaseDatasetWriter.AppendAsync is a Wave 1 stub; Plan 07-04 fills this in")

    override _.ExecuteAsync(_stoppingToken: CancellationToken) : Task =
        // Stub returns immediately — no Channel, no consumer loop. Plan 07-04 replaces.
        Task.CompletedTask
```

**Step 4 — Update `src/SmartRouter.Cli/SmartRouter.Cli.fsproj`:**

Add the three new `<Compile>` entries to the existing `<ItemGroup>`. Insert AFTER `Adapters/QueueDispatcher.fs` and BEFORE `Endpoints/ChatCompletions.fs`:

```xml
<Compile Include="Adapters/QwenUpstreamClient.fs" />
<Compile Include="Adapters/QueueDispatcher.fs" />
<!-- Phase 7 — Failure detection + teacher labeling -->
<Compile Include="Adapters/FailureDetector.fs" />
<Compile Include="Adapters/TeacherLabeler.fs" />
<Compile Include="Adapters/HardCaseDatasetWriter.fs" />
<Compile Include="Endpoints/ChatCompletions.fs" />
```

**Order rationale:**
- All three Phase 7 adapters depend only on `Core.RetrainingPorts` (compile-resolved via Core project ref) plus a few BCL/Microsoft.Extensions namespaces — no cross-deps among themselves.
- Order between the three is irrelevant; alphabetical is fine.
- They MUST come before Endpoints (Plan 07-05 may have ChatCompletions reference one of them via DI; even though it doesn't today, keeping the layering clean future-proofs).
- They come after QueueDispatcher to match the "infrastructure adapters first, then endpoints" convention.

**No NuGet changes** — all required packages (Microsoft.Extensions.Http.Resilience, Microsoft.Extensions.Hosting, FSharp.SystemTextJson, Serilog) are already pinned in Cli.fsproj.

**No appsettings.json changes in this plan** — Plan 07-05 adds the TeacherLabeler + HardCaseDataset sections.
  </action>
  <verify>
- `dotnet build SmartRouter.slnx -nologo --tl:off` succeeds with 0 warnings; TreatWarningsAsErrors stays on for both Core and Cli.
- `grep -n "FailureDetector\.fs\|TeacherLabeler\.fs\|HardCaseDatasetWriter\.fs" src/SmartRouter.Cli/SmartRouter.Cli.fsproj` returns 3 hits, all between QueueDispatcher.fs and ChatCompletions.fs lines.
- **Compile-order guard for DecisionLogger.fs (existing module that may be referenced transitively):** verify the three Phase 7 entries come AFTER `DecisionLogger.fs` to prevent 'undefined module SmartRouter.Cli.Adapters.DecisionLogger' errors:
  ```bash
  awk '/DecisionLogger\.fs/{a=NR} /FailureDetector\.fs/{b=NR} END{print (a<b)?"OK":"FAIL"}' src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  ```
  Expected: `OK` (DecisionLogger.fs line number < FailureDetector.fs line number).
- `grep -n "interface IFailureDetector\|interface ITeacherLabeler\|interface IHardCaseDatasetWriter" src/SmartRouter.Cli/Adapters/` returns one hit per stub file.
- `grep -n "NotImplementedException" src/SmartRouter.Cli/Adapters/FailureDetector.fs src/SmartRouter.Cli/Adapters/TeacherLabeler.fs src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs` returns at least 3 hits (one per stub interface method).
- `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off --filter 'TestCategory!=requires-models' 2>&1 | tail -20` shows existing tests still pass — the new stubs are unreferenced (no DI binding yet), so test count is unchanged from Phase 6 baseline (50 pass + 10 ignored).
  </verify>
  <done>
Three stub adapter files exist, each implementing its interface with NotImplementedException. Cli.fsproj has the 3 new <Compile> entries in correct compile order. Build is clean. Test count unchanged. Wave 2 plans (07-02 / 07-03 / 07-04) can now run in parallel — each modifies one stub file's body without touching .fsproj or the other two adapters.
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

2. **Pure-Core invariant:**
   ```bash
   grep -RIn "Serilog\|HttpClient\|Microsoft\.ML\|Microsoft\.AspNetCore\|FSharp\.SystemTextJson" src/SmartRouter.Core/RetrainingPorts.fs
   ```
   Expected: NO output.

3. **Existing tests still pass:**
   ```bash
   dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off
   ```
   Expected: 50 passed + 10 ignored (Phase 6 baseline).

4. **Stub files implement their interfaces:**
   ```bash
   grep -nE "interface (IFailureDetector|ITeacherLabeler|IHardCaseDatasetWriter)" src/SmartRouter.Cli/Adapters/FailureDetector.fs src/SmartRouter.Cli/Adapters/TeacherLabeler.fs src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
   ```
   Expected: 3 hits, one per file.

5. **fsproj compile order:**
   ```bash
   grep -nE "(QueueDispatcher\.fs|FailureDetector\.fs|TeacherLabeler\.fs|HardCaseDatasetWriter\.fs|ChatCompletions\.fs)" src/SmartRouter.Cli/SmartRouter.Cli.fsproj
   ```
   Expected: line numbers strictly increasing in the order listed.

6. **Core.fsproj compile order:**
   ```bash
   grep -nE "(Routing\.fs|RetrainingPorts\.fs|Ports\.fs)" src/SmartRouter.Core/SmartRouter.Core.fsproj
   ```
   Expected: Routing < RetrainingPorts < Ports (per CONTEXT.md).
</verification>

<success_criteria>
- Core/RetrainingPorts.fs compiles, BCL-only, exports 3 interfaces + 4 types (Task 1)
- Three Cli adapter stub files compile and implement their interfaces with NotImplementedException (Task 2)
- Both .fsproj files updated with new <Compile> entries in correct order (Tasks 1+2)
- All 50 existing tests still pass + 10 ignored (no regression)
- 0 build warnings (TreatWarningsAsErrors=true)
- Pure-Core invariant preserved (verified by grep)
</success_criteria>

<output>
After completion, create `.planning/phases/07-failure-detection-and-teacher-labeling/07-01-SUMMARY.md` listing the 6 files modified, any stub-signature deviations from this plan, and the test count delta (expected: 50 → 50, no test changes).
</output>

## Existing-test impact

| Test file | Tests | Needs change in 07-01? | Why / Why not |
|-----------|-------|------------------------|---------------|
| RoutingTests.fs | 22 | NO | Pure routing tests; no DI; no app startup; no Phase 7 references. |
| StreamingTests.fs | 8 | NO | Uses configureServices but no DI registration in Phase 7-01 changes. |
| QueueTests.fs | 9 | NO | Builds its own DI mock; no Phase 7 surface area touched. |
| LoadTests.fs | 2 (pending) | NO | Pending; not run by default. |
| MLRoutingTests.fs | 5 | NO | Uses configureServices but Phase 7-01 adds no service registrations. |
| MLEmbeddingTests.fs | 3 (gated) | NO | Independent of Phase 7. |
| MLClassifierTests.fs | 3 (gated) | NO | Independent of Phase 7. |
| LoggingTests.fs | 5 | NO | Uses configureServices; Phase 7-01 adds no service registrations. |

## REQ-ID coverage in this plan

- **FAIL-01..FAIL-04**: Foundation only — interfaces exist, stubs throw. Real implementations land in Wave 2 (Plans 07-02, 07-03, 07-04). Plan 07-05 wires DI.
