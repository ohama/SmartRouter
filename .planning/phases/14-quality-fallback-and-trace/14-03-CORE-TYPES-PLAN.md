---
phase: 14-quality-fallback-and-trace
plan: 03
type: execute
wave: 3
depends_on: ["14-02"]
files_modified:
  - src/SmartRouter.Core/Domain.fs
  - src/SmartRouter.Cli/Adapters/DecisionLogger.fs
  - src/SmartRouter.Cli/Adapters/QualityCheck.fs
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - src/SmartRouter.Cli/CompositionRoot.fs
  - src/SmartRouter.Cli/appsettings.json
autonomous: true

must_haves:
  truths:
    - "Domain.fs RoutingReason DU has 6 cases including new `FallbackTo122B` (Phase 14)"
    - "DecisionLogger.fs formatReason matches all 6 cases exhaustively; new arm: `| FallbackTo122B -> \"fallback_to_122b\"`"
    - "src/SmartRouter.Cli/Adapters/QualityCheck.fs exists; defines `QualityFallbackOptions` [<CLIMutable>] record + `isBadResponse` pure function"
    - "isBadResponse signature: `QualityFallbackOptions -> string -> bool`; returns false when `Enabled = false`; otherwise checks `response.Length < MinResponseLength` OR any of `BadKeywords` is contained (case-sensitive)"
    - "appsettings.json `Routing` section has new `QualityFallback` subsection with `Enabled: true`, `MinResponseLength: 30`, `BadKeywords: [\"TODO\", \"I think\"]` defaults"
    - "RoutingOptions record (CompositionRoot.fs) gains `QualityFallback: QualityFallbackOptions` field; bound from config via existing Configure<RoutingOptions>"
    - "ARCH-01 preserved: Domain.fs DU change is BCL-only; QualityCheck.fs is Cli adapter (not in Core)"
    - "TreatWarningsAsErrors=true holds — all RoutingReason match sites are exhaustive (catch-all `| r ->` arms or explicit case enumeration); confirmed via grep"
    - "dotnet build clean; dotnet test green; test count unchanged"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/QualityCheck.fs"
      provides: "isBadResponse pure function + QualityFallbackOptions record"
      min_lines: 25
---

<objective>
Add core types for Phase 14 quality fallback: `RoutingReason.FallbackTo122B` DU case, `formatReason` arm, `isBadResponse` heuristic + options record. No behavior change yet — these types are consumed by 14-04 ChatCompletions handler.
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
  <name>Task 1: Domain.fs — add RoutingReason.FallbackTo122B</name>
  <files>src/SmartRouter.Core/Domain.fs</files>
  <action>
Edit `src/SmartRouter.Core/Domain.fs` `RoutingReason` DU. Add `FallbackTo122B` case after `FallbackTo35B`:

```fsharp
type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string
    | ExplicitTask          of taskType: TaskType
    | Default
    | ML
    | FallbackTo35B
    | FallbackTo122B   // NEW Phase 14: 35B response failed quality check, retried on 122B
```

(Existing 5 cases unchanged. New case added at end.)
  </action>
  <verify>
```bash
grep -c "FallbackTo122B" src/SmartRouter.Core/Domain.fs
# expected: 1
grep -c "| FallbackTo35B\|| FallbackTo122B" src/SmartRouter.Core/Domain.fs
# expected: 2
dotnet build src/SmartRouter.Core/SmartRouter.Core.fsproj 2>&1 | tail -3
# expected: Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 2: DecisionLogger.fs — formatReason new arm</name>
  <files>src/SmartRouter.Cli/Adapters/DecisionLogger.fs</files>
  <action>
Edit `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` `formatReason`. Add `FallbackTo122B` arm:

```fsharp
let formatReason (reason: RoutingReason) : string =
    match reason with
    | ExplicitModelOverride alias -> sprintf "explicit_model:%s" alias
    | ExplicitTask taskType       -> sprintf "explicit_task:%A" taskType
    | Default                     -> "default"
    | ML                          -> "ml"
    | FallbackTo35B               -> "fallback_to_35b"
    | FallbackTo122B              -> "fallback_to_122b"   // NEW Phase 14
```

Match must be exhaustive over all 6 cases. F# compiler enforces under TreatWarningsAsErrors=true.

**Verify no other RoutingReason match site needs update.** Grep across src/ + tests/ for `match.*Reason\|RoutingReason` and inspect each:

```bash
grep -rn "match.*reason\b\|: RoutingReason ->" src/ tests/
```

Most sites use a catch-all `| r -> failtestf` or `| _ -> ...` — adding a new case is safe. Only `formatReason` requires explicit handling.
  </action>
  <verify>
```bash
grep -c "FallbackTo122B" src/SmartRouter.Cli/Adapters/DecisionLogger.fs
# expected: 1
grep -c "fallback_to_122b" src/SmartRouter.Cli/Adapters/DecisionLogger.fs
# expected: 1
dotnet build 2>&1 | tail -3
# expected: Build succeeded (full solution; check for FS0025 incomplete-match)
```
  </verify>
</task>

<task type="auto">
  <name>Task 3: Create QualityCheck.fs + appsettings.json + RoutingOptions binding</name>
  <files>
    - src/SmartRouter.Cli/Adapters/QualityCheck.fs (NEW)
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/appsettings.json
    - src/SmartRouter.Cli/CompositionRoot.fs
  </files>
  <action>
**Step 1.** Create `src/SmartRouter.Cli/Adapters/QualityCheck.fs`:

```fsharp
module SmartRouter.Cli.Adapters.QualityCheck

open System

/// Phase 14 — quality-fallback heuristic.
///
/// distillation 디자인 (auto-retraining-code.md §3 isBadResponse) 의 패턴을
/// appsettings.json 으로 tunable 하게 외부화한 형태. operator 가 재빌드 없이
/// MinResponseLength / BadKeywords 를 조정 가능.
[<CLIMutable>]
type QualityFallbackOptions = {
    Enabled            : bool
    MinResponseLength  : int
    BadKeywords        : string array
}

/// Pure F# (BCL only). Returns true when the response is "bad" by the configured
/// heuristic. Returns false when QualityFallback is disabled regardless of content.
///
/// Defaults applied at the option-binding layer (CompositionRoot):
///   - Enabled: true
///   - MinResponseLength: 30
///   - BadKeywords: ["TODO", "I think"]
///
/// Case-sensitive keyword match. Operator can add lowercase variants
/// ("todo", "i think") to the array for broader coverage.
let isBadResponse (opts: QualityFallbackOptions) (response: string) : bool =
    if not opts.Enabled then
        false
    elif response.Length < opts.MinResponseLength then
        true
    elif obj.ReferenceEquals(opts.BadKeywords, null) then
        false
    else
        opts.BadKeywords
        |> Array.exists (fun kw ->
            not (String.IsNullOrEmpty(kw)) && response.Contains(kw))
```

**Step 2.** Add Compile entry to `src/SmartRouter.Cli/SmartRouter.Cli.fsproj`. Place after `Logging.fs` (no internal deps; can sit near other small adapters):

```xml
<Compile Include="Adapters/QualityCheck.fs" />
```

(Suggest placing near `ColdStart.fs` from 14-01 — both are small standalone helpers.)

**Step 3.** Edit `src/SmartRouter.Cli/appsettings.json` — add `QualityFallback` subsection under `Routing`:

```jsonc
"Routing": {
  "TimeoutSeconds": 300,
  "ML": { ... },
  "Health": { ... },
  "TaskTable": { ... },
  "ModelAliases": { ... },
  "QualityFallback": {
    "Enabled": true,
    "MinResponseLength": 30,
    "BadKeywords": [ "TODO", "I think" ]
  }
}
```

**Step 4.** Edit `src/SmartRouter.Cli/CompositionRoot.fs` `RoutingOptions` record — add new field:

```fsharp
type RoutingOptions =
    { TimeoutSeconds   : int
      ML               : MlOptions
      TaskTable        : Dictionary<string, TaskTableEntry>
      ModelAliases     : Dictionary<string, string>
      QualityFallback  : QualityCheck.QualityFallbackOptions   // NEW Phase 14
    }
```

The existing `services.Configure<RoutingOptions>(config.GetSection("Routing"))` binds the new field automatically.

Apply defensive defaults in `buildRoutingConfig` (or wherever options are read) — when the QualityFallback section is absent OR fields are zero/null:

```fsharp
let qualityFallback =
    if obj.ReferenceEquals(opts.QualityFallback, null) then
        { Enabled = false; MinResponseLength = 30; BadKeywords = [||] }
    else
        let qf = opts.QualityFallback
        { Enabled            = qf.Enabled
          MinResponseLength  = (if qf.MinResponseLength <= 0 then 30 else qf.MinResponseLength)
          BadKeywords        = (if obj.ReferenceEquals(qf.BadKeywords, null) then [||] else qf.BadKeywords) }
```

(14-04 reads this through the IOptions binding when it consumes from ChatCompletions; defensive defaults here handle missing-config cases.)
  </action>
  <verify>
```bash
test -f src/SmartRouter.Cli/Adapters/QualityCheck.fs && echo OK
grep -c "isBadResponse\|QualityFallbackOptions" src/SmartRouter.Cli/Adapters/QualityCheck.fs
# expected: >= 2
grep -c "QualityFallback" src/SmartRouter.Cli/appsettings.json
# expected: 1
grep -c "QualityFallback" src/SmartRouter.Cli/CompositionRoot.fs
# expected: >= 1
dotnet build 2>&1 | tail -3
# expected: Build succeeded.
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-restore 2>&1 | grep "EXPECTO!" | tail -1
# expected: 80 passed (existing baseline preserved)
```
  </verify>
</task>

</tasks>

<verification>
- [x] Domain.fs RoutingReason 6-case (FallbackTo122B 추가)
- [x] DecisionLogger.formatReason exhaustive 매치 + new arm `fallback_to_122b`
- [x] QualityCheck.fs (BCL-only Cli adapter); isBadResponse + QualityFallbackOptions
- [x] appsettings.json Routing.QualityFallback 섹션
- [x] RoutingOptions record + 기본값 fallback 처리
- [x] ARCH-01 preserved (Core 변경은 DU case 만; QualityCheck 는 Cli)
- [x] 빌드 clean; 80 테스트 통과
</verification>
