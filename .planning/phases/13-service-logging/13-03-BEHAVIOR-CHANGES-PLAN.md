---
phase: 13-service-logging
plan: 03
type: execute
wave: 3
depends_on: ["13-02"]
files_modified:
  - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
  - src/SmartRouter.Cli/Adapters/HealthService.fs
  - src/SmartRouter.Cli/Endpoints/Health.fs
  - src/SmartRouter.Cli/Endpoints/Stats.fs
  - src/SmartRouter.Cli/Endpoints/Canary.fs
  - src/SmartRouter.Cli/Endpoints/Models.fs
autonomous: true

must_haves:
  truths:
    - "ChatCompletions.fs lines 282 and 367 (Routing target=... emissions) are at LogDebug level (was LogInformation; demoted to cut hot-path stderr volume)"
    - "HealthService probe loop only emits at INFO when probe transitions from up→down or down→up; steady-state probes emit at DEBUG"
    - "Endpoints/Health.fs emits LogDebug on each /health hit (\"endpoint=/health hit; status=200\")"
    - "Endpoints/Stats.fs emits LogDebug on each /stats hit"
    - "Endpoints/Canary.fs emits LogDebug on each /canary hit"
    - "Endpoints/Models.fs emits LogDebug on each /v1/models hit"
    - "Test count unchanged (no test logic affected); existing assertions about routing decisions untouched"
    - "JSONL DecisionLog format unchanged (this plan only touches operational log emissions)"
---

<objective>
Three behavior changes that reduce operational-log volume and add operator-visible endpoint hit signals:
1. **Hot-path demotion (Q-style):** ChatCompletions.fs lines 282 + 367 (per-request Information emissions) move to Debug. Same data is in JSONL DecisionLog already; operational log doesn't need duplicates at INFO.
2. **HealthService transition-only logging:** probe success/failure emissions move from per-tick INFO to transition-only INFO (state change = up↔down). Steady-state probes log at DEBUG.
3. **Endpoint-hit DEBUG logs:** /health, /stats, /canary, /v1/models endpoints each emit one DEBUG line per hit. Suppressed at default INFO level; visible at --log-level=debug.

No semantic behavior changes. JSONL DecisionLog unaffected. ML routing logic unchanged. Test count unchanged.
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/13-service-logging/13-CONTEXT.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: ChatCompletions hot-path demotion</name>
  <files>src/SmartRouter.Cli/Endpoints/ChatCompletions.fs</files>
  <action>
Edit `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs`. Two emissions to demote from `LogInformation` → `LogDebug`:

**Edit 1 — line 282 (streaming branch):**

```fsharp
// BEFORE
logger.LogInformation(
    "Routing target={Target} reason={Reason} priority={Priority} stream=true",
    decision.Target, decision.Reason, decision.Priority)

// AFTER
logger.LogDebug(
    "Routing target={Target} reason={Reason} priority={Priority} stream=true",
    decision.Target, decision.Reason, decision.Priority)
```

**Edit 2 — line 367 (non-streaming branch):**

```fsharp
// BEFORE
logger.LogInformation(
    "Routing target={Target} reason={Reason} priority={Priority}",
    decision.Target, decision.Reason, decision.Priority)

// AFTER
logger.LogDebug(
    "Routing target={Target} reason={Reason} priority={Priority}",
    decision.Target, decision.Reason, decision.Priority)
```

(Line numbers approximate — may have shifted after 13-02 ILogger migration.) Use grep to find the exact "Routing target=" emissions:

```bash
grep -n "Routing target=" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
```

Both should change from `LogInformation` to `LogDebug`.

**Step 3 (optional comment):** Add a comment explaining the choice:

```fsharp
// Hot-path: same routing decision is recorded in JSONL DecisionLog at INFO-equivalent.
// Operational log keeps this at DEBUG to avoid stderr duplication at default level.
```
  </action>
  <verify>
```bash
grep -n "Routing target=" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
# expected: 2 hits, both with LogDebug
grep -c "LogInformation.*Routing target" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
# expected: 0
grep -c "LogDebug.*Routing target" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
# expected: 2
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 2: HealthService transition-only logging</name>
  <files>src/SmartRouter.Cli/Adapters/HealthService.fs</files>
  <action>
Edit `src/SmartRouter.Cli/Adapters/HealthService.fs`. Restructure probe-success/failure emissions to log only on state transitions.

Current pattern (around lines 55-75):
```fsharp
// Per-probe emission (every PollingInterval × upstream count = 12/min):
logger.LogDebug("HealthService: probe for {Target} threw", target)   // failure path
logger.LogDebug("HealthService: {Target} reachable", target)         // success path
logger.LogInformation("HealthService: ...", ...)                      // state changes (already exist)
```

The current code's INFO transitions (line 67/74) need to be reviewed against the actual file content (post-13-02 form). Verify the existing transition logic is correct, then ensure:
- ☑ "{Target} reachable (transitioned from down)" → INFO
- ☑ "{Target} unreachable (transitioned from up)" → WARN
- ☑ Steady-state both directions → DEBUG only

Use a small in-memory state map: `let mutable probeState : Map<ModelId, bool> = Map.empty` (or use the existing ConcurrentDictionary that holds the IsReachable state — it should already be available).

Pseudocode for the probe iteration:
```fsharp
let prevReachable = state.[target]   // from existing state dict
let nowReachable = ... probe result ...

match prevReachable, nowReachable with
| false, true ->
    logger.LogInformation("HealthService: {Target} reachable (transitioned from down)", target)
| true, false ->
    logger.LogWarning("HealthService: {Target} unreachable (transitioned from up)", target)
| false, false ->
    logger.LogDebug("HealthService: {Target} still unreachable", target)
| true, true ->
    logger.LogDebug("HealthService: {Target} still reachable", target)
```

If a `bool` reachable status doesn't yet exist in HealthService (only ConsecutiveFailureCount), implement reachability lookup:
```fsharp
let nowReachable = consecutiveFailures < threshold
```

Apply this pattern to wherever the probe iteration emits the success/failure log.

The initial probe pass (HealthService.fs:103-116) emits one INFO line on completion — keep that as INFO (operator wants startup confirmation).

State-change transitions are operator-relevant (potential incident); steady-state probes are noise. This is the right tradeoff.
  </action>
  <verify>
```bash
grep -c "transitioned from\|still reachable\|still unreachable" src/SmartRouter.Cli/Adapters/HealthService.fs
# expected: >= 4 (the 4 transition messages)
grep -c "LogInformation.*reachable\|LogWarning.*unreachable" src/SmartRouter.Cli/Adapters/HealthService.fs
# expected: >= 2 (the up-transitioning and down-transitioning lines)
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# Build succeeded.
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --filter "FullyQualifiedName~Health" --no-restore 2>&1 | tail -5
# expected: passes (HLTH-04..08 should still work; transition logic doesn't break their assertions)
```
  </verify>
</task>

<task type="auto">
  <name>Task 3: Endpoint-hit DEBUG logs in 4 endpoint files</name>
  <files>
    - src/SmartRouter.Cli/Endpoints/Health.fs
    - src/SmartRouter.Cli/Endpoints/Stats.fs
    - src/SmartRouter.Cli/Endpoints/Canary.fs
    - src/SmartRouter.Cli/Endpoints/Models.fs
  </files>
  <action>
For each of the 4 endpoint files, add one `logger.LogDebug` emission per endpoint hit. The endpoints are typically lambda handlers like:

```fsharp
let handle (ctx: HttpContext) : Task = task { ... }
```

Add the emission near the top of each handler, after correlation_id is established (correlationMiddleware runs first; correlation_id is in `ctx.Items` already).

**Health.fs example:**
```fsharp
let handle (ctx: HttpContext) : Task = task {
    let logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Health")
    logger.LogDebug("/health hit; method={Method}", ctx.Request.Method)
    ...
}
```

**Stats.fs:**
```fsharp
logger.LogDebug("/stats hit; queue_depth_high={H} active_122b={A}", h, a)
```
(After the stats are computed; can be later in the handler. The structured fields are useful when reviewing operational log for stats-endpoint usage patterns.)

**Canary.fs:** has multiple sub-endpoints (GET status, POST promote, POST rollback, POST enable). One log per:
```fsharp
logger.LogDebug("/canary {Method} hit; result={Status}", method, status)
```

**Models.fs:**
```fsharp
logger.LogDebug("/v1/models hit; upstreams_reachable={N}", upstreamsReachable)
```

All emissions at DEBUG level — suppressed at default Information; visible at --log-level=debug.

Add `open Microsoft.Extensions.Logging` if not already present.
  </action>
  <verify>
```bash
for f in src/SmartRouter.Cli/Endpoints/Health.fs src/SmartRouter.Cli/Endpoints/Stats.fs src/SmartRouter.Cli/Endpoints/Canary.fs src/SmartRouter.Cli/Endpoints/Models.fs; do
  echo "=== $f ==="
  grep -c "logger\.LogDebug\|LogDebug" "$f"
done
# expected: each file >= 1
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# Build succeeded.
```
  </verify>
</task>

</tasks>

<verification>
- [x] ChatCompletions hot-path emissions at LogDebug (was LogInformation)
- [x] HealthService transition-only INFO/WARN; steady-state DEBUG
- [x] 4 endpoint files emit LogDebug per hit
- [x] Build clean; tests green
</verification>
