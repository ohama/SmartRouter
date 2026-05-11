---
phase: 05-routing-decision-logging
plan: 02
type: execute
wave: 2
depends_on: [05-01]
files_modified:
  - src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - src/SmartRouter.Cli/CompositionRoot.fs
  - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
autonomous: true

must_haves:
  truths:
    - "Every routing decision (heuristic OR ml) emits exactly one JSONL DecisionLog at endpoint exit — success, routing-error 400, upstream-error 5xx, and cancellation are ALL covered (≥7 distinct call sites in ChatCompletions.fs)"
    - "routing_algorithm field is populated from RoutingAlgorithmRegistration.Name ('heuristic' | 'ml'), not inferred from the function reference"
    - "model_version is populated from RoutingAlgorithmRegistration.ModelVersion ('heuristic-v1' | 'ml-v0-placeholder')"
    - "RoutingAlgorithmRegistration record type lives in `src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs` (its own file) so both ChatCompletions.fs (compile pos 14) and CompositionRoot.fs (compile pos 16) can `open` it without F# compile-order violation"
    - "correlation_id in every DecisionLog comes from HttpContext.Items[\"CorrelationId\"], matching the value pushed onto Serilog LogContext"
    - "Every SSE error event body emitted by the streaming path includes a `correlation_id` field (LOG-04 / OBS-03) — e.g., `data: {\"error\":{\"message\":...,\"type\":\"upstream_error\",\"correlation_id\":\"<cid>\"}}\\n\\n`"
    - "latency_ms = (DateTimeOffset.UtcNow - started).TotalMilliseconds, captured at endpoint entry and computed at every exit point"
    - "prompt_hash is SHA-256 hex of concatenated message contents; prompt_korean_char_ratio is float 0..1; both computed via DecisionLogger helpers"
    - "task_type is Some when wire body had .task field, None otherwise (no string emptiness ambiguity)"
    - "fallback_used is RoutingDecision.IsFallback (always false in Phase 5 — Phase 10 sets it true)"
    - "Streaming path logs AFTER enumerator.DisposeAsync() in all three exit arms (normal, OperationCanceledException, unexpected ex) so latency_ms reflects time-to-last-byte, not time-to-first-byte"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs"
      provides: "RoutingAlgorithmRegistration record type definition (Algorithm + Name + ModelVersion). Lives in its own file so both ChatCompletions.fs (compile pos 14) and CompositionRoot.fs (compile pos 16) can `open` it without an F# compile-order violation."
      contains: "RoutingAlgorithmRegistration"
    - path: "src/SmartRouter.Cli/SmartRouter.Cli.fsproj"
      provides: "<Compile Include=\"Adapters/RoutingAlgorithm.fs\" /> entry inserted AFTER Adapters/CorrelationMiddleware.fs (added by Plan 05-01) and BEFORE Adapters/QwenUpstreamClient.fs"
      contains: "Adapters/RoutingAlgorithm.fs"
    - path: "src/SmartRouter.Cli/CompositionRoot.fs"
      provides: "DI registration of RoutingAlgorithmRegistration as singleton (consuming the record type defined in Adapters/RoutingAlgorithm.fs); replaces Phase 4's bare RoutingAlgorithm singleton"
      contains: "RoutingAlgorithmRegistration"
    - path: "src/SmartRouter.Cli/Endpoints/ChatCompletions.fs"
      provides: "DecisionLog enqueue at every exit path (UnsupportedTask 400, generic Error 400, Ok decision streaming/non-streaming success, Ok decision streaming cancellation, streaming upstream-error and non-streaming 502 — at least 7 call sites)"
      contains: "decisionLogger.Log"
  key_links:
    - from: "ChatCompletions.handler success path"
      to: "decisionLogger.Log buildDecisionLog ..."
      via: "after upstream response written or stream enumerator disposed"
      pattern: "decisionLogger\\.Log"
    - from: "ChatCompletions.handler error paths (400, 502)"
      to: "decisionLogger.Log buildDecisionLog ..."
      via: "before WriteAsJsonAsync of the error envelope"
      pattern: "decisionLogger\\.Log.*Error"
    - from: "CompositionRoot RoutingAlgorithmRegistration"
      to: "ChatCompletions.handler"
      via: "GetRequiredService<RoutingAlgorithmRegistration>() in mapEndpoints; handler reads .Algorithm + .Name + .ModelVersion"
      pattern: "RoutingAlgorithmRegistration"
    - from: "Adapters/RoutingAlgorithm.fs"
      to: "ChatCompletions.fs and CompositionRoot.fs"
      via: "Both files `open SmartRouter.Cli.Adapters.RoutingAlgorithm` to consume the shared record type; F# compile order: RoutingAlgorithm.fs precedes both"
      pattern: "open SmartRouter\\.Cli\\.Adapters\\.RoutingAlgorithm"
---

<objective>
Wire DecisionLog emission into every exit point of the ChatCompletions handler, and refactor Phase 4's bare `RoutingAlgorithm` singleton into a `RoutingAlgorithmRegistration = { Algorithm; Name; ModelVersion }` record so the endpoint has access to all three together. Plan 05-01 provided the infrastructure; this plan makes it produce data.

Purpose: LOG-01 / OBS-01 require a JSONL line per request with full schema. Without endpoint wiring, the BackgroundService writer is idle. This is the wave-2 plan that flips the system from "logging seam exists" to "logging actually happens".

Output:
- New file `src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs` defines `RoutingAlgorithmRegistration` record type (must precede both ChatCompletions.fs and CompositionRoot.fs in fsproj compile order so both modules can `open` it without F# compile-order violation)
- `SmartRouter.Cli.fsproj` updated with `<Compile Include="Adapters/RoutingAlgorithm.fs" />` inserted AFTER `Adapters/CorrelationMiddleware.fs` and BEFORE `Adapters/QwenUpstreamClient.fs`
- `RoutingAlgorithmRegistration` DI registration replaces bare `RoutingAlgorithm` singleton in CompositionRoot.fs (CompositionRoot.fs `open`s the new module)
- `ChatCompletions.fs` `open`s the new module, handler signature gains `regn: RoutingAlgorithmRegistration` and `decisionLogger: IDecisionLogger`; logs at every exit point (UnsupportedTask 400, generic Error 400, Ok streaming success, Ok streaming cancellation, Ok streaming error, Ok non-streaming success, Ok non-streaming 502 — at least 7 call sites)
- Streaming SSE error event body includes `correlation_id` (LOG-04 / OBS-03)
- A private helper `buildDecisionLog` in ChatCompletions.fs reduces duplication across the exit points
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
@.planning/phases/05-routing-decision-logging/05-01-DECISION-LOG-INFRA-PLAN.md
@src/SmartRouter.Cli/CompositionRoot.fs
@src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
@src/SmartRouter.Cli/SmartRouter.Cli.fsproj
@src/SmartRouter.Core/Domain.fs
</context>

<tasks>

<task type="auto">
  <name>Task 1: Extract RoutingAlgorithmRegistration into its own file + DI registration</name>
  <files>
    src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs
    src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    src/SmartRouter.Cli/CompositionRoot.fs
  </files>
  <action>
The current Phase 4 registration in CompositionRoot.fs uses `services.AddSingleton<RoutingAlgorithm>(Func<IServiceProvider, RoutingAlgorithm>(fun sp -> ...))` — a bare function singleton. The endpoint can dispatch but cannot tell *which* algorithm ran. Replace it with a record that pairs the function with its observable name and the model_version string.

**F# compile-order constraint (CRITICAL):** `ChatCompletions.fs` is at compile pos 14 in `SmartRouter.Cli.fsproj`; `CompositionRoot.fs` is at compile pos 16. F# requires the type's defining module to be compiled BEFORE any consumer. If we put `RoutingAlgorithmRegistration` inside `CompositionRoot.fs`, then `ChatCompletions.fs` (Task 2) cannot reference the type — the build fails. Therefore the record type lives in its OWN file in `Adapters/`, slotted into the .fsproj BEFORE both consumers.

**Step 1 — Create `src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs`** (module `SmartRouter.Cli.Adapters.RoutingAlgorithm`):

```fsharp
module SmartRouter.Cli.Adapters.RoutingAlgorithm

open SmartRouter.Core.Domain

// ── RoutingAlgorithmRegistration ────────────────────────────────────────────
//
// Phase 4 registered a bare RoutingAlgorithm function. Phase 5 needs to know
// which algorithm ran (for DecisionLog.routing_algorithm) and what model_version
// to log (for DecisionLog.model_version). This record carries all three together.
//
// Algorithm   : the chosen function — Heuristic.applyHeuristic OR ML.applyML
//               (RoutingAlgorithm is a function-type alias defined in
//               SmartRouter.Core.Domain — open above brings it into scope.)
// Name        : "heuristic" | "ml" — appears in JSONL routing_algorithm field
// ModelVersion: "heuristic-v1" | "ml-v0-placeholder" — appears in JSONL model_version field
// Phase 6 will redefine ModelVersion for ML to include router.zip's short hash.
//
// This type lives in its own file (rather than inside CompositionRoot.fs) so
// that ChatCompletions.fs (compile pos 14) can reference it: F# compile order
// requires the type's defining module to compile BEFORE every consumer, and
// CompositionRoot.fs sits at compile pos 16 — too late for ChatCompletions.fs.
type RoutingAlgorithmRegistration =
    { Algorithm    : RoutingAlgorithm
      Name         : string
      ModelVersion : string }
```

If `RoutingAlgorithm` is not defined in `SmartRouter.Core.Domain` (verify with `grep -n "type RoutingAlgorithm" src/SmartRouter.Core/Domain.fs`), adjust the `open` to wherever it is defined (likely also in `Domain.fs` near `RoutingDecision`).

**Step 2 — Update `src/SmartRouter.Cli/SmartRouter.Cli.fsproj`.** Insert ONE new `<Compile>` line. After Plan 05-01 the relevant region looks like:

```xml
<Compile Include="Adapters/Json.fs" />
<Compile Include="Adapters/Logging.fs" />
<Compile Include="Adapters/DecisionLogger.fs" />
<Compile Include="Adapters/DecisionLogWriter.fs" />
<Compile Include="Adapters/CorrelationMiddleware.fs" />
<Compile Include="Adapters/QwenUpstreamClient.fs" />
...
```

Insert `<Compile Include="Adapters/RoutingAlgorithm.fs" />` AFTER `Adapters/CorrelationMiddleware.fs` and BEFORE `Adapters/QwenUpstreamClient.fs`. Final region:

```xml
<Compile Include="Adapters/Json.fs" />
<Compile Include="Adapters/Logging.fs" />
<Compile Include="Adapters/DecisionLogger.fs" />
<Compile Include="Adapters/DecisionLogWriter.fs" />
<Compile Include="Adapters/CorrelationMiddleware.fs" />
<Compile Include="Adapters/RoutingAlgorithm.fs" />
<Compile Include="Adapters/QwenUpstreamClient.fs" />
<Compile Include="Adapters/QueueDispatcher.fs" />
<Compile Include="Endpoints/ChatCompletions.fs" />
<Compile Include="Endpoints/Stats.fs" />
<Compile Include="CompositionRoot.fs" />
<Compile Include="Program.fs" />
```

This places `RoutingAlgorithm.fs` BEFORE both `ChatCompletions.fs` (compile pos 14, Task 2 consumer) AND `CompositionRoot.fs` (compile pos 16, Step 3 below) — F# compile order satisfied for both.

**Step 3 — Update `src/SmartRouter.Cli/CompositionRoot.fs`.** Add `open SmartRouter.Cli.Adapters.RoutingAlgorithm` near the top alongside other `open SmartRouter.Cli.Adapters.*` lines. DO NOT redeclare the type here — it's defined in the new file.

In `configureServices`, REPLACE the existing `services.AddSingleton<RoutingAlgorithm>(...)` block (currently around the comment "RoutingAlgorithm as a DI singleton — dispatches to the correct algorithm function") with a `RoutingAlgorithmRegistration` registration:

```fsharp
// RoutingAlgorithmRegistration as a DI singleton — pairs the algorithm function
// with its name and model_version so the endpoint can populate DecisionLog.
// null | "" | "heuristic" -> applyHeuristic / "heuristic" / "heuristic-v1"
// "ml"                    -> applyML        / "ml"        / "ml-v0-placeholder"
// other                   -> InvalidOperationException at startup
services.AddSingleton<RoutingAlgorithmRegistration>(
    Func<IServiceProvider, RoutingAlgorithmRegistration>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<RoutingOptions>>().Value
        match opts.Algorithm with
        | null | "" | "heuristic" ->
            { Algorithm    = SmartRouter.Core.Heuristic.applyHeuristic
              Name         = "heuristic"
              ModelVersion = "heuristic-v1" }
        | "ml" ->
            { Algorithm    = SmartRouter.Core.ML.applyML
              Name         = "ml"
              ModelVersion = "ml-v0-placeholder" }
        | other ->
            let msg =
                sprintf
                    "appsettings.json Routing.Algorithm = \"%s\" is invalid; valid values: \"heuristic\", \"ml\""
                    other
            raise (System.InvalidOperationException(msg))))
|> ignore

// Backwards-compatible alias: register the bare RoutingAlgorithm function so
// any existing test or component that resolves RoutingAlgorithm directly still works.
// MLRoutingTests Tests 4+5 currently resolve `GetRequiredService<RoutingAlgorithm>()` —
// this preserves that resolution and lets those tests continue to pass without changes.
services.AddSingleton<RoutingAlgorithm>(
    Func<IServiceProvider, RoutingAlgorithm>(fun sp ->
        sp.GetRequiredService<RoutingAlgorithmRegistration>().Algorithm))
|> ignore
```

The alias registration is intentional: `MLRoutingTests` (Tests 4 and 5) call `sp.GetRequiredService<RoutingAlgorithm>()` directly and assert the algorithm runs ML. Re-exposing the bare function from the Registration keeps those tests green without modification.

The `Func<IServiceProvider, RoutingAlgorithmRegistration>` explicit cast is required for the same reason as the Phase 4 RoutingAlgorithm registration: F# function-type aliases need an explicit Func wrapper for DI overload resolution (per STATE.md decision 04-02).
  </action>
  <verify>
- New file exists: `test -f src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs && grep -n "type RoutingAlgorithmRegistration" src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs` returns 1 hit.
- fsproj insertion is in the correct compile-order slot: `grep -n 'Compile Include' src/SmartRouter.Cli/SmartRouter.Cli.fsproj` shows `Adapters/RoutingAlgorithm.fs` AFTER `Adapters/CorrelationMiddleware.fs` AND BEFORE `Adapters/QwenUpstreamClient.fs` AND BEFORE `Endpoints/ChatCompletions.fs` AND BEFORE `CompositionRoot.fs`.
- `dotnet build SmartRouter.slnx -nologo --tl:off` succeeds with 0 warnings — F# compile-order is satisfied (this is the load-bearing check; if RoutingAlgorithm.fs is in the wrong position, the build fails with FS0039 "type ... not defined").
- `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off --filter MLRoutingTests` passes all 5 tests (the bare RoutingAlgorithm alias keeps Tests 4+5 working).
- `grep -n "RoutingAlgorithmRegistration" src/SmartRouter.Cli/CompositionRoot.fs` returns ≥2 hits (AddSingleton + GetRequiredService inside alias). Type definition is NOT here — it's in Adapters/RoutingAlgorithm.fs.
- `grep -n "open SmartRouter.Cli.Adapters.RoutingAlgorithm" src/SmartRouter.Cli/CompositionRoot.fs` returns 1 hit.
- `grep -nE "AddSingleton<RoutingAlgorithm>(\(|\b)" src/SmartRouter.Cli/CompositionRoot.fs` returns 1 hit (the new alias) — the Phase-4 standalone registration is gone, replaced by the alias that delegates.
  </verify>
  <done>
RoutingAlgorithmRegistration record is defined in its own file (`Adapters/RoutingAlgorithm.fs`); fsproj has the new compile entry slotted before all consumers; CompositionRoot.fs `open`s the new module and registers the singleton; a backward-compat alias registers `RoutingAlgorithm` so MLRoutingTests Tests 4+5 still resolve directly. `dotnet build` clean; all 5 ML tests pass.
  </done>
</task>

<task type="auto">
  <name>Task 2: Wire DecisionLog at every ChatCompletions exit point</name>
  <files>
    src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
  </files>
  <action>
Modify `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` to enqueue a DecisionLog at all 4 exit paths.

**Step 1 — open the new modules** at the top of the file (after the existing `open SmartRouter.Cli.Adapters.Json` line):

```fsharp
open SmartRouter.Cli.Adapters.DecisionLogger
open SmartRouter.Cli.Adapters.CorrelationMiddleware
open SmartRouter.Cli.Adapters.RoutingAlgorithm
```

The third `open` is the new file from Task 1 — `RoutingAlgorithmRegistration` lives in `SmartRouter.Cli.Adapters.RoutingAlgorithm`, NOT in `SmartRouter.Cli.CompositionRoot` (F# compile-order requires the type to live earlier in the .fsproj than ChatCompletions.fs).

**Step 2 — change the handler signature.** Current signature:

```fsharp
let handler
    (routingConfig : RoutingConfig)
    (algorithm     : RoutingAlgorithm)
    (upstream      : IUpstreamClient)
    (ctx           : HttpContext) : Task =
```

New signature — replace `algorithm: RoutingAlgorithm` with `regn: RoutingAlgorithmRegistration` (unqualified — the `open` from Step 1 brings the type into scope) and add `decisionLogger: IDecisionLogger`:

```fsharp
let handler
    (routingConfig  : RoutingConfig)
    (regn           : RoutingAlgorithmRegistration)
    (decisionLogger : IDecisionLogger)
    (upstream       : IUpstreamClient)
    (ctx            : HttpContext) : Task =
```

In the `match routeRequest routingConfig algorithm req with` line, replace `algorithm` with `regn.Algorithm`.

**Step 3 — capture started timestamp + correlation_id at the very top** of the handler `task { ... }` body, BEFORE the wireBody parsing line:

```fsharp
let started = DateTimeOffset.UtcNow
let correlationId =
    match ctx.Items.TryGetValue(CorrelationMiddleware.CorrelationIdKey) with
    | true, (:? string as cid) when not (System.String.IsNullOrEmpty(cid)) -> cid
    | _ -> System.Guid.NewGuid().ToString("N")  // fallback if middleware not registered
```

The fallback is defensive — if Plan 05-01's middleware registration is somehow bypassed, the request still gets a unique id rather than a null/empty string.

**Step 4 — define a private helper `buildDecisionLog`** AT THE TOP OF THE FILE (after the `mapWireToRequest` helper, before `// ── Handler`):

```fsharp
/// Build a DecisionLog from request + decision (or error stand-in for error paths).
/// Used at every exit point in handler to avoid duplication.
let private buildDecisionLog
    (req            : RouterRequest)
    (regn           : RoutingAlgorithmRegistration)
    (correlationId  : string)
    (started        : DateTimeOffset)
    (target         : string)
    (reason         : string)
    (fallbackUsed   : bool)
    : DecisionLog =
    { schema_version           = 1
      correlation_id           = correlationId
      prompt_hash              = computePromptHash req.Messages
      prompt_korean_char_ratio = computeKoreanRatio req.Messages
      routing_algorithm        = regn.Name
      routing_reason           = reason
      target                   = target
      latency_ms               = (DateTimeOffset.UtcNow - started).TotalMilliseconds
      fallback_used            = fallbackUsed
      model_version            = regn.ModelVersion
      task_type                = req.Task
      timestamp                = DateTimeOffset.UtcNow }
```

If `req` is unavailable (e.g., wireBody was null and parsing failed before mapWireToRequest), the helper still works: the empty-message lists return empty hash and 0.0 ratio. So the body-null branch can also call it with `mapWireToRequest` of a synthetic empty wire.

**Step 5 — log at every exit path.**

The current handler has these exit points:

1. **`if isNull (wireBody :> obj)`** — body was null. Log it before the WriteAsJsonAsync of the error envelope. Construct a synthetic empty req (no messages, no task) and pass `target = "unknown"`, `reason = "error:null_body"`, `fallbackUsed = false`. (Alternatively, skip this case — it's a malformed request that never reached routing — but the cleanest behavior is to log every request that got past JSON parsing. Recommend: log it.)

2. **`Error (UnsupportedTask raw)`** — log before the WriteAsJsonAsync. `target = "unknown"`, `reason = sprintf "error:unsupported_task:%s" raw`, `fallbackUsed = false`.

3. **`Error e`** (catch-all routing error) — `target = "unknown"`, `reason = sprintf "error:%A" e`.

4. **`Ok decision`** — TWO sub-cases:
   - **Streaming path**: log AFTER `enumerator.DisposeAsync()` in EACH of the three try/with arms (normal, OperationCanceledException, unexpected ex). Pull `target = sprintf "%A" decision.Target`, `reason = formatReason decision.Reason`, `fallbackUsed = decision.IsFallback`. For OperationCanceledException case, append `";cancelled"` to the reason: `reason = formatReason decision.Reason + ";cancelled"`. For unexpected ex, append `";stream_error"`.
   - **Non-streaming path**: TWO sub-cases:
     - `Ok body` (success): log after `ctx.Response.WriteAsync(body, ...)`. Use `formatReason decision.Reason` and `decision.IsFallback`.
     - `Error e` (upstream error 502): log before the WriteAsJsonAsync of the error envelope. `target = sprintf "%A" decision.Target` (we know which target we tried), `reason = formatReason decision.Reason + ";upstream_error"`, `fallbackUsed = decision.IsFallback`.

**Each of the 4-to-7 exit points calls `decisionLogger.Log (buildDecisionLog req regn correlationId started target reason fallbackUsed)`. Fire-and-forget — never `do!`.**

**Step 6 — update `mapEndpoints`** to resolve and pass the new dependencies:

```fsharp
let mapEndpoints (app: WebApplication) =
    app.MapPost("/v1/chat/completions", Func<HttpContext, Task>(fun ctx ->
        let routingConfig   = ctx.RequestServices.GetRequiredService<RoutingConfig>()
        let regn            = ctx.RequestServices.GetRequiredService<RoutingAlgorithmRegistration>()
        let decisionLogger  = ctx.RequestServices.GetRequiredService<IDecisionLogger>()
        let upstream        = ctx.RequestServices.GetRequiredService<IUpstreamClient>()
        handler routingConfig regn decisionLogger upstream ctx)) |> ignore
```

**Note on the streaming finally semantics** (Pitfall P6 / P7): The current handler structure uses a try/with around the streaming enumerator loop. F# `task {}` does not allow `do!` in finally, so the existing code calls `enumerator.DisposeAsync()` in EACH arm of try/with. The DecisionLog enqueue must come AFTER the DisposeAsync in each arm so latency_ms is measured at end-of-stream, not start-of-stream. Position the `decisionLogger.Log ...` call as the LAST statement in each arm (after `do! enumerator.DisposeAsync()`).

The exact placement in the try arm:
```fsharp
do! enumerator.DisposeAsync()
decisionLogger.Log (buildDecisionLog req regn correlationId started
                        (sprintf "%A" decision.Target)
                        (formatReason decision.Reason)
                        decision.IsFallback)
```

In the OperationCanceledException arm:
```fsharp
do! enumerator.DisposeAsync()
decisionLogger.Log (buildDecisionLog req regn correlationId started
                        (sprintf "%A" decision.Target)
                        (formatReason decision.Reason + ";cancelled")
                        decision.IsFallback)
```

Same shape for the unexpected-ex arm.

**Step 7 — SSE error event body MUST include `correlation_id` (LOG-04 / OBS-03).**

REQUIREMENTS.md LOG-04 says: "correlation ID must be included in any SSE error event body — verified by a test." The current streaming-error arm in `ChatCompletions.fs` (line ~179) emits:

```fsharp
let errMsg = sprintf "data: {\"error\":{\"message\":\"%s\",\"type\":\"upstream_error\"}}\n\n" (string e)
```

Replace EVERY `data: {"error":...}` literal with a form that includes the correlation_id. To keep the JSON well-formed even when `e`'s string representation contains quotes, escape it:

```fsharp
// Reuse a small local helper (define just above the streaming branch, or inline):
let escapeJsonString (s: string) =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")

let errMsg =
    sprintf
        "data: {\"error\":{\"message\":\"%s\",\"type\":\"upstream_error\",\"correlation_id\":\"%s\"}}\n\n"
        (escapeJsonString (string e))
        correlationId
```

**Find every `data: {\"error\"` literal in `ChatCompletions.fs` and apply the same change.** Use `grep -n 'data:.*error' src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` to enumerate. As of the Phase 5 baseline there is one such literal (the streaming upstream-error arm at ~line 179); if any future paths emit SSE-error events (e.g., a 502 emitted in SSE shape), each MUST include `correlation_id`.

This change makes Plan 05-03's Test 5 (SSE error correlation propagation, added in Plan 05-03 below) pass and discharges LOG-04's "SSE error event body" clause within Phase 5.
  </action>
  <verify>
- `dotnet build SmartRouter.slnx -nologo --tl:off` succeeds with 0 warnings.
- `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off` passes all 44 existing tests.
- `grep -c "decisionLogger\.Log" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` returns ≥7 (null-body, UnsupportedTask, generic Error, Ok streaming-normal, Ok streaming-cancel, Ok streaming-error, Ok non-streaming success, Ok non-streaming Error 502 — count is 7-9 depending on whether null-body and streaming-error arms are merged).
- `grep -n "RoutingAlgorithmRegistration" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` returns ≥2 (handler signature + mapEndpoints resolution).
- `grep -nB1 "decisionLogger\.Log" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs | grep -E "(DisposeAsync|WriteAsync|StatusCode)"` shows DecisionLog.Log appears AFTER write/dispose in each branch (sanity check that latency captures end-state).
- **SSE error events include correlation_id** — every `data: {\"error\"` literal contains `\"correlation_id\":\"%s\"` and is paired with `correlationId` in its sprintf args:
  ```bash
  grep -nE 'data:.*error' src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
  ```
  Every match must include `correlation_id` in the JSON body (LOG-04). Count of matches must equal count of `correlation_id` substrings on the same lines.
- Smoke run: `dotnet run --project src/SmartRouter.Cli` in one shell, then `curl -X POST http://127.0.0.1:4000/v1/chat/completions -H 'Content-Type: application/json' -d '{"messages":[{"role":"user","content":"hi"}]}'` in another. Verify `logs/decisions/$(date -u +%Y-%m-%d).jsonl` contains exactly 1 line with all 12 fields. (502 is acceptable — Qwen upstream is not running; the LOG line still emits.)
  </verify>
  <done>
ChatCompletions handler emits a DecisionLog at every exit point. Streaming path logs AFTER enumerator disposal so latency_ms reflects time-to-last-byte. RoutingAlgorithmRegistration is wired into the handler. `dotnet build` clean; all 44 existing tests pass; manual smoke run produces a JSONL line with all 12 fields.
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

2. **All existing tests pass (44/44):**
   ```bash
   dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off
   ```
   Expected: 44 passed (RoutingTests 22 + StreamingTests 8 + QueueTests 9 + LoadTests 0 of 2 ignored + MLRoutingTests 5).

3. **All exit points emit a DecisionLog:**
   ```bash
   grep -c "decisionLogger\.Log" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
   ```
   Expected: ≥7. Reviewer must visually confirm each `match routeRequest ... with` branch + `match result with` branch has a `decisionLogger.Log` call.

3a. **SSE error events carry correlation_id:**
   ```bash
   grep -nE 'data:.*error' src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
   ```
   Every match line must include `correlation_id` literal (LOG-04 SSE clause).

4. **Smoke test — JSONL line has all 12 fields:**
   ```bash
   rm -rf src/SmartRouter.Cli/logs/
   ( cd src/SmartRouter.Cli && timeout 10 dotnet run & ) ; sleep 5
   curl -sS -X POST http://127.0.0.1:4000/v1/chat/completions \
        -H 'Content-Type: application/json' \
        -d '{"messages":[{"role":"user","content":"hi"}]}' > /dev/null || true
   sleep 2
   jq -r 'keys | sort | join(",")' src/SmartRouter.Cli/logs/decisions/*.jsonl | head -1
   pkill -f SmartRouter || true
   ```
   Expected output: `correlation_id,fallback_used,latency_ms,model_version,prompt_hash,prompt_korean_char_ratio,routing_algorithm,routing_reason,schema_version,target,task_type,timestamp` (12 fields, alphabetically sorted).

5. **routing_algorithm matches the configured algorithm:**
   With default config (heuristic), the JSONL line's `routing_algorithm` is `"heuristic"` and `model_version` is `"heuristic-v1"`. With `--routing-algorithm=ml`, both flip.

6. **MLRoutingTests Tests 4+5 still pass:** They resolve `RoutingAlgorithm` directly from DI; the alias registration in Task 1 makes this still work.
</verification>

<success_criteria>
- New file `src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs` defines `RoutingAlgorithmRegistration`; fsproj has it slotted before all consumers (Task 1)
- DI registration of RoutingAlgorithmRegistration lands in CompositionRoot.fs (Task 1)
- Bare RoutingAlgorithm singleton remains as a DI alias delegating to Registration.Algorithm — preserves MLRoutingTests Tests 4+5 (Task 1)
- ChatCompletions handler signature gains regn + decisionLogger; logs at every exit point — ≥7 call sites (Task 2)
- Streaming path logs AFTER enumerator.DisposeAsync() in all 3 try/with arms (Task 2)
- Every SSE error event body contains `correlation_id` (LOG-04) (Task 2 Step 7)
- All 44 existing tests pass (no regression)
- Smoke run: JSONL file contains 1 line with all 12 fields after a single curl
- 0 build warnings (TreatWarningsAsErrors=true)
</success_criteria>

<output>
After completion, create `.planning/phases/05-routing-decision-logging/05-02-SUMMARY.md` listing:
- The 2 files modified
- The number of decisionLogger.Log call sites in ChatCompletions.fs (expected: 4 routing exits + 3 streaming arms + 2 non-streaming arms = up to 9)
- A sample JSONL line emitted by the smoke run with all 12 fields
- Confirmation that 44/44 tests still pass
</output>

## Existing-test impact

| Test file | Tests | Needs change in 05-02? | Why / Why not |
|-----------|-------|------------------------|---------------|
| RoutingTests.fs | 22 | NO | Pure routing tests; do not exercise the endpoint. |
| MLRoutingTests.fs | 5 | NO | Tests 4+5 resolve `RoutingAlgorithm` directly. The Task 1 backward-compat alias keeps that resolution working — no test edits needed. |
| StreamingTests.fs | 8 | NO | startTestRouter resolves the endpoint via DI; the new `RoutingAlgorithmRegistration` and `IDecisionLogger` registrations from Plan 05-01 + this plan resolve transparently. DecisionLogs will be emitted to `logs/decisions/` in test bin but no test asserts no log files. |
| QueueTests.fs | 9 | NO | QueueDispatcher tests build their own DI without ChatCompletions endpoint; not affected by handler signature change. |
| LoadTests.fs | 2 (pending) | NO | Same as QueueTests; pending by default. |

## REQ-ID coverage in this plan

- **OBS-01** (per-request structured log line includes selected model, routing reason, latency, etc.): emitted via Serilog `Log.Information` (existing) PLUS the new JSONL via DecisionLog. The JSONL is the persistent canonical form; stderr remains for live tailing.
- **LOG-01** (12-field schema): every exit point produces a line with all 12 fields via `buildDecisionLog`.
- **LOG-04** (correlation ID propagated through Serilog stderr AND JSONL AND SSE error events): FULLY addressed. (a) JSONL: correlation_id is read from HttpContext.Items (set by Plan 05-01's middleware) and lands in DecisionLog.correlation_id. (b) Serilog stderr: carried via LogContext.PushProperty (Plan 05-01). (c) SSE error events: Step 7 of Task 2 modifies every `data: {\"error\":...}` literal in the streaming arm to include `\"correlation_id\":\"<cid>\"` in the JSON body. Plan 05-03 Test 5 verifies all three sources end-to-end.
- **OBS-03** (correlation id propagated through logs and SSE-error events): JSONL + Serilog stderr + SSE-error body all wired here. Plan 05-03 verifies via test.
