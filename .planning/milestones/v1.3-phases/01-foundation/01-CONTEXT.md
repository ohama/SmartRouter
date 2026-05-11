# Phase 1: Foundation - Context

**Gathered:** 2026-05-07
**Status:** Ready for planning

<domain>
## Phase Boundary

Project scaffold (`SmartRouter.Core` / `SmartRouter.Cli` / `SmartRouter.Tests`), Core domain types and routing pipeline (testable in isolation, no live HTTP), non-streaming `POST /v1/chat/completions` path that reaches a real upstream Qwen and returns the response unchanged. SSE streaming, 122B concurrency gating, health/fallback, and observability ship in later phases.

</domain>

<decisions>
## Implementation Decisions

### Unknown `task` value behavior
- Reject with HTTP 400 when client sends a `task` field that does not match a known task string
- Rationale: client cannot typo their way to silent mis-routing; loud failure beats silent quality drift
- Match is case-insensitive

### Unknown `model` value behavior
- Fall through to the next routing stage (task table, then heuristic) when `model` is not a recognized alias
- Rationale: Hermes sends arbitrary model strings (whatever the OpenAI SDK forwards); the router must not block on this
- Recognized aliases short-circuit; everything else continues the pipeline silently

### Heuristic shape and threshold
- Score-based: sum of weighted signals (keyword hits +1 each, prompt-length tiers +1/+2/+4, message count +1/+2, code-block presence +1)
- Threshold = 3 → escalate to 122B; otherwise 35B
- Code-block detection: triple-backtick fences only (no indentation heuristic, no `<code>` tag)
- Tiebreaker: tie goes to 35B (latency-first per Core Value)
- Threshold and keyword list are configured in `appsettings.json` and tunable at runtime via env-var override

### Task→model table is CONFIG-DRIVEN from day 1
- **User explicitly overrode the recommended "hardcoded F# match" default**
- Mapping lives in `appsettings.json` under `Routing.TaskTable`
- Implementation reads the table at startup and validates that every value resolves to a known `ModelId`; startup fails fast on invalid config
- A separate `appsettings.json` "default task table" mirrors the canonical mapping (graph_indexing/compiler_debug/architecture_analysis/dependency_analysis/reasoning → 122B; retrieval/summary → 35B); operator can edit without recompiling
- Priority assignment for each task type stays in code (it's correctness, not policy) — the config only controls which model handles each task

### Solution layout
- `.slnx` solution file (`SmartRouter.slnx`), matching blueCode's pattern
- Three projects: `SmartRouter.Core` (classlib), `SmartRouter.Cli` (Microsoft.NET.Sdk.Web), `SmartRouter.Tests` (Exe with explicit Expecto `rootTests` list)

### Reuse from blueCode
- Copy verbatim: `Adapters/Json.fs`, `Adapters/Logging.fs`
- Copy and adapt for routing scope: `Adapters/QwenHttpClient.fs` (HF-id trap defense, 300s timeout, sampling defaults, error mapping)
- Mirror: `scripts/check-no-async.sh` scoped to `src/SmartRouter.Core/**`
- Mirror: explicit `rootTests` list pattern in test entrypoint

### Streaming requests in Phase 1
- If client sends `stream=true`, return HTTP 501 Not Implemented with a clear message stating SSE arrives in Phase 2
- This unblocks Hermes integration testing for non-streaming smoke; full streaming lands Phase 2

### Configuration source-of-truth
- `appsettings.json` is the single source of truth for tunables
- Env-var overrides via standard `IConfiguration` precedence (e.g., `Routing__Threshold=4`)
- No separate per-test config file; tests build configuration in-memory

### Claude's Discretion
- Exact F# module file names within Core (Domain.fs / Routing.fs / Ports.fs split is from research; minor adjustments OK)
- Heuristic keyword list contents (start with the union of brief + research keywords; tune later)
- Exact prompt-length tier boundaries (research suggested concrete numbers; planner may adjust)
- Logging field names (Serilog property names) — no user preference stated
- Test naming conventions within Expecto `testList`s

</decisions>

<specifics>
## Specific Ideas

- Mirror blueCode's commit protocol: per-task atomic commits `{feat,fix,test,refactor,perf,chore}({phase}-{plan}): {name}`; never `git add .`
- Mirror blueCode's stream separation: Serilog → stderr, application output → stdout
- Use `127.0.0.1` (not `0.0.0.0`, not `localhost`) when binding Kestrel
- Routing decisions must attach a `RoutingReason` DU value (`ExplicitModelOverride` / `ExplicitTask` / `Heuristic` / `Default`) — surfaces in `/stats` (later phase) and logs (later phase) but the type must exist now

</specifics>

<deferred>
## Deferred Ideas

- Tuning heuristic threshold and keyword list against real Hermes traffic — Phase 5 (when /stats and structured logs land) or post-v1
- Promoting heuristic to ML / learned routing — explicit v2 in PROJECT.md
- Dynamic reload of `appsettings.json` without restart — defer; launchd restart is fine for v1

</deferred>

---

*Phase: 01-foundation*
*Context gathered: 2026-05-07*
