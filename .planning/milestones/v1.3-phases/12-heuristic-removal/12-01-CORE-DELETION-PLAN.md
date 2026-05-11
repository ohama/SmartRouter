---
phase: 12-heuristic-removal
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/SmartRouter.Core/Heuristic.fs
  - src/SmartRouter.Core/Domain.fs
  - src/SmartRouter.Core/Routing.fs
  - src/SmartRouter.Core/SmartRouter.Core.fsproj
  - src/SmartRouter.Cli/Adapters/DecisionLogger.fs
autonomous: true

must_haves:
  truths:
    - "src/SmartRouter.Core/Heuristic.fs file no longer exists (git rm)"
    - "Domain.fs RoutingReason DU has 5 cases: ExplicitModelOverride, ExplicitTask, Default, ML, FallbackTo35B (Heuristic case removed)"
    - "Domain.fs RoutingConfig record has 2 fields: TaskTable, MlThreshold (Keywords + ComplexityThreshold removed)"
    - "Routing.fs canonicalKeywords value is removed; defaultRoutingConfig builds with only TaskTable + MlThreshold"
    - "SmartRouter.Core.fsproj <Compile Include=\"Heuristic.fs\" /> line is removed; remaining order is Domain.fs → MLPorts.fs → CanaryPorts.fs → ML.fs → Routing.fs → RetrainingPorts.fs → Ports.fs"
    - "DecisionLogger.fs formatReason has no Heuristic arm; F# match exhaustiveness satisfied without warning"
    - "dotnet build src/SmartRouter.Core/SmartRouter.Core.fsproj succeeds with TreatWarningsAsErrors=true"
    - "dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj fails with expected errors only in CompositionRoot.fs / Program.fs (heuristic dispatch arms still reference deleted DU case) — these are addressed by 12-02; tests project build state is undefined at this plan's end"
  artifacts:
    - path: "src/SmartRouter.Core/Domain.fs"
      provides: "RoutingReason DU + RoutingConfig record after heuristic removal"
      contains: ["ExplicitModelOverride", "ExplicitTask", "FallbackTo35B", "MlThreshold", "TaskTable"]
    - path: "src/SmartRouter.Core/Routing.fs"
      provides: "Stage pipeline + defaultRoutingConfig without heuristic-only fields"
      contains: ["routeRequest", "tryModelOverride", "tryTaskTable", "canonicalTaskTable", "defaultRoutingConfig"]

---

<objective>
Delete `src/SmartRouter.Core/Heuristic.fs` and the `RoutingReason.Heuristic of score` DU case + `RoutingConfig.Keywords` + `RoutingConfig.ComplexityThreshold` fields. Update `DecisionLogger.fs` `formatReason` to drop the heuristic match arm (mechanical fallout). Cli compile remains broken at this plan's end (CompositionRoot still references heuristic dispatch); 12-02 finishes the wiring. Tests project compile is also undefined at this plan's end.

This plan is the atomic Core change. It MUST keep Core compilable on its own. Cli + Tests are explicitly allowed to be broken until Wave 2 (12-02) completes.
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md
@.planning/preparing/12-CONTEXT.md
@.planning/preparing/12-heuristic-removal-research.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Delete Heuristic.fs + remove from Core.fsproj</name>
  <files>
    - src/SmartRouter.Core/Heuristic.fs (DELETED)
    - src/SmartRouter.Core/SmartRouter.Core.fsproj
  </files>
  <action>
**Step 1.** Delete `src/SmartRouter.Core/Heuristic.fs`:

```bash
git rm src/SmartRouter.Core/Heuristic.fs
```

**Step 2.** Remove the `<Compile Include="Heuristic.fs" />` line from `src/SmartRouter.Core/SmartRouter.Core.fsproj` (currently line 8). Use Edit tool, NOT manual XML rewrite. Result must look like:

```xml
<ItemGroup>
    <Compile Include="Domain.fs" />
    <Compile Include="MLPorts.fs" />
    <Compile Include="CanaryPorts.fs" />
    <Compile Include="ML.fs" />
    <Compile Include="Routing.fs" />
    <Compile Include="RetrainingPorts.fs" />
    <Compile Include="Ports.fs" />
</ItemGroup>
```

(Comments after the Compile lines may be retained or trimmed per executor judgement; truth check is on the Compile entries themselves.)
  </action>
  <verify>
```bash
test ! -f src/SmartRouter.Core/Heuristic.fs && echo OK || echo MISSING
grep -c "Heuristic\.fs" src/SmartRouter.Core/SmartRouter.Core.fsproj
# expected: 0
```
  </verify>
</task>

<task type="auto">
  <name>Task 2: Domain.fs — remove RoutingReason.Heuristic case + RoutingConfig.Keywords/ComplexityThreshold fields</name>
  <files>
    - src/SmartRouter.Core/Domain.fs
  </files>
  <action>
Edit `src/SmartRouter.Core/Domain.fs` with two targeted Edit operations:

**Edit 1:** Remove the `Heuristic` DU case from `RoutingReason`. The case is currently:

```fsharp
type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string
    | ExplicitTask          of taskType: TaskType
    | Heuristic             of score: int
    | Default
    | ML
    | FallbackTo35B
```

Delete the `| Heuristic of score: int` line. Resulting:

```fsharp
type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string
    | ExplicitTask          of taskType: TaskType
    | Default
    | ML
    | FallbackTo35B
```

**Edit 2:** Remove `Keywords` and `ComplexityThreshold` from `RoutingConfig`. Currently:

```fsharp
type RoutingConfig =
    { ComplexityThreshold : int
      Keywords            : string list
      TaskTable           : Map<string, ModelId * Priority>
      MlThreshold         : float32 }
```

Resulting:

```fsharp
type RoutingConfig =
    { TaskTable   : Map<string, ModelId * Priority>
      MlThreshold : float32 }
```

(Doc comments mentioning heuristic should also be cleaned; they're informational. The "Heuristic algorithm ignores this field; only ML reads it" comment near MlThreshold becomes "ML reads this; routes to 122B when P >= MlThreshold".)

**Edit 3 (doc comment cleanup, optional):** Update line 100 doc comment "Both Heuristic.applyHeuristic and ML.applyML conform to this shape" to just "ML.applyML conforms to this shape".
  </action>
  <verify>
```bash
grep -c "| Heuristic " src/SmartRouter.Core/Domain.fs
# expected: 0
grep -c "ComplexityThreshold\|Keywords" src/SmartRouter.Core/Domain.fs
# expected: 0
grep -c "MlThreshold\|TaskTable" src/SmartRouter.Core/Domain.fs
# expected: >= 2 (declaration + comment OK)
```
  </verify>
</task>

<task type="auto">
  <name>Task 3: Routing.fs — remove canonicalKeywords + rebuild defaultRoutingConfig</name>
  <files>
    - src/SmartRouter.Core/Routing.fs
  </files>
  <action>
**Edit 1:** Delete the entire `canonicalKeywords` value definition (currently around lines 123–127):

```fsharp
let canonicalKeywords : string list =
    [ "recursive"; "dependency"; "lowering"; "mlir"; "llvm"; "compiler"
      "architecture"; "type inference"; "graph relation"; "closure conversion"
      "cross-file"; "multi-file"; "reasoning"; "inference"; "optimization"
      "refactor"; "redesign"; "abstract"; "formal"; "proof" ]
```

Delete the let binding entirely.

**Edit 2:** Rebuild `defaultRoutingConfig` (currently lines 130–134):

```fsharp
let defaultRoutingConfig : RoutingConfig =
    { ComplexityThreshold = 3
      Keywords            = canonicalKeywords
      TaskTable           = canonicalTaskTable
      MlThreshold         = 0.5f }
```

Become:

```fsharp
let defaultRoutingConfig : RoutingConfig =
    { TaskTable   = canonicalTaskTable
      MlThreshold = 0.5f }
```

**Edit 3 (doc comment cleanup):** Line 96 `/// pluggable algorithm (Heuristic.applyHeuristic or ML.applyML).` → `/// pluggable algorithm (ML.applyML; future implementations conform to RoutingAlgorithm shape).`
  </action>
  <verify>
```bash
grep -c "canonicalKeywords\|ComplexityThreshold\|Keywords " src/SmartRouter.Core/Routing.fs
# expected: 0
grep -c "Heuristic\.applyHeuristic\|applyHeuristic" src/SmartRouter.Core/Routing.fs
# expected: 0
dotnet build src/SmartRouter.Core/SmartRouter.Core.fsproj 2>&1 | tail -5
# expected: Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 4: DecisionLogger.fs — remove Heuristic arm from formatReason</name>
  <files>
    - src/SmartRouter.Cli/Adapters/DecisionLogger.fs
  </files>
  <action>
Edit `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` `formatReason` function (currently lines 30–37). Remove the `| Heuristic score → sprintf "heuristic:score=%d" score` line:

```fsharp
let formatReason (reason: RoutingReason) : string =
    match reason with
    | ExplicitModelOverride alias -> sprintf "explicit_model:%s" alias
    | ExplicitTask taskType       -> sprintf "explicit_task:%A" taskType
    | Default                     -> "default"
    | ML                          -> "ml"
    | FallbackTo35B               -> "fallback_to_35b"
```

(Heuristic arm gone. F# match must remain exhaustive over 5 cases — verified at compile time.)

Doc comment line 28 "/// Hand-rolled to produce stable, operator-readable strings in JSONL output." stays. Line 29 referencing routing reason stays.
  </action>
  <verify>
```bash
grep -c "| Heuristic " src/SmartRouter.Cli/Adapters/DecisionLogger.fs
# expected: 0
grep -c "heuristic:score" src/SmartRouter.Cli/Adapters/DecisionLogger.fs
# expected: 0
# Build Core only — Cli still broken (12-02 finishes the wiring):
dotnet build src/SmartRouter.Core/SmartRouter.Core.fsproj 2>&1 | tail -3
# expected: Build succeeded.
```
  </verify>
</task>

</tasks>

<verification>
- [x] Heuristic.fs deleted; Core.fsproj prune
- [x] Domain.fs: RoutingReason has 5 cases; RoutingConfig has 2 fields
- [x] Routing.fs: canonicalKeywords gone; defaultRoutingConfig rebuilds
- [x] DecisionLogger.fs: formatReason match exhaustive over 5 cases
- [x] Core builds clean
- [x] Cli + Tests projects: build state undefined at this plan's end (intentional; 12-02 finishes wiring)
</verification>
