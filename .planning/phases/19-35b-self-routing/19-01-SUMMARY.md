---
phase: 19-35b-self-routing
plan: 01
subsystem: routing
tags: [selfrouting, adapter, DU, LRU-cache, safety-bias, prompt-template]
requires:
  - 18-03   # SessionStore + sticky escalation (RouterRequest.SessionId field must exist)
provides:
  - RoutingReason.SelfRoute DU case (9th case)
  - formatReason 9th arm returning "self_route"
  - ISelfRouter port (ClassifyAsync + PromptVersion)
  - ISelfRouterStats port (GetSelfRouterStats)
  - SelfRouter concrete adapter (LRU cache + safety-biased parser + task {} HTTP)
  - prompts/self-router-prompt.md operator-tunable template
affects:
  - 19-02   # DI registration for ISelfRouter + "selfrouter" named HttpClient
  - 19-03   # cascade integration: ClassifyAsync call in ChatCompletions.fs non-streaming branch
tech-stack:
  added: []        # no new NuGet packages; reuses existing FSharp.SystemTextJson (JsonFSharpConverter)
  patterns:
    - JudgeClient.fs line-by-line port pattern (substitute cache key + verdict names + max_tokens)
    - Safety-biased parser: check UNSAFE substring before SAFE (SAFE ⊂ UNSAFE — load-bearing order)
    - Prompt SHA-256 prefix as model_version (PromptVersion field for DecisionLog correlation)
    - Lock-on-first-read template caching (promptLock obj() double-check)
    - LRU eviction via Seq.minBy AccessSeq (mirrors JudgeClient.setCached)
key-files:
  created:
    - src/SmartRouter.Cli/Adapters/SelfRouter.fs   # 339 lines — full adapter
    - prompts/self-router-prompt.md                 # 25 lines — operator-tunable classify prompt
  modified:
    - src/SmartRouter.Core/Domain.fs               # +2 lines (SelfRoute DU case + comment)
    - src/SmartRouter.Cli/Adapters/DecisionLogger.fs  # +1 line (formatReason 9th arm)
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj   # +1 compile entry after DecisionLogger.fs
decisions:
  - key: "Safety-biased parser checks UNSAFE before SAFE"
    reason: "SAFE is a substring of UNSAFE; reversed order silently routes UNSAFE to 35B (SC-2 failure)"
  - key: "max_tokens=8 (not 4)"
    reason: "RESEARCH §9 PITFALL #5 — 8 tokens absorbs whitespace/punctuation drift from mlx_lm.server"
  - key: "Single-string cache key (promptHash) not tuple"
    reason: "Self-classify has no response to hash; JudgeClient uses (promptHash, responseHash) tuple because it classifies response quality"
  - key: "PromptVersion computed at construction time (File.ReadAllBytes)"
    reason: "Operator may edit prompt file at runtime; version must reflect what was loaded at startup, not current file state"
  - key: "RouteFailed and RouteSkipped never cached"
    reason: "Transient errors must not poison the LRU; operator may fix template or network without restart"
  - key: "factory.CreateClient(\"selfrouter\") not IUpstreamClient"
    reason: "IUpstreamClient routes through QueueDispatcher which holds 122B SemaphoreSlim(1) — using it would block inference traffic on the classifier"
metrics:
  duration: "~6m 32s"
  completed: "2026-05-12"
---

# Phase 19 Plan 01: DU + Adapter + Prompt Template Foundation Summary

**One-liner:** SelfRoute DU case + ISelfRouter/ISelfRouterStats adapter with SHA-256 LRU cache, UNSAFE-first safety-biased parser, and operator-tunable SAFE/UNSAFE prompt template.

## Deliverables

### Files Created

| File | Lines | Purpose |
|------|-------|---------|
| `src/SmartRouter.Cli/Adapters/SelfRouter.fs` | 339 | Full self-classify adapter — DU, options, port interfaces, concrete class |
| `prompts/self-router-prompt.md` | 25 | Operator-tunable SAFE/UNSAFE classify prompt; SHA-256: `84e243ae` |

### Files Modified

| File | Delta | Change |
|------|-------|--------|
| `src/SmartRouter.Core/Domain.fs` | +2 lines | `SelfRoute` 9th DU case + comment |
| `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` | +1 line | `\| SelfRoute -> "self_route"` 9th formatReason arm |
| `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` | +1 line | `<Compile Include="Adapters/SelfRouter.fs" />` after DecisionLogger.fs |

### Exported Types (SelfRouter.fs)

| Type | Kind | Purpose |
|------|------|---------|
| `SelfRouteVerdict` | DU (4 cases) | RouteSafe / RouteUnsafe / RouteSkipped of reason / RouteFailed of error |
| `SelfRouterOptions` | CLIMutable record | Endpoint, PromptPath, TimeoutSeconds, MaxCacheEntries — bound from `Routing:SelfRouter:*` |
| `ISelfRouter` | Interface | `ClassifyAsync(promptHash, promptText, ct) -> Task<SelfRouteVerdict>` + `PromptVersion: string` |
| `ISelfRouterStats` | Interface | `GetSelfRouterStats() -> struct (int64 * int64 * int64 * int64)` |
| `SelfRouter` | Concrete class | Implements ISelfRouter + ISelfRouterStats |

### RoutingReason DU Evolution

- **Before (Phase 18 baseline):** 8 cases, formatReason 8-arm exhaustive match
- **After (Phase 19-01):** 9 cases — `SelfRoute` added; formatReason 9-arm match, `"self_route"` string

### Prompt File (prompts/self-router-prompt.md)

SHA-256 prefix: `84e243ae` → at runtime `SelfRouter.promptVersion = "selfrouting-84e243ae"`.

This value threads into `DecisionLog.model_version` via Plan 19-03, enabling operators to correlate routing decisions with specific prompt template versions.

## Decisions Made

1. **Safety-biased parser: `hasUnsafe` checked before `hasSafe`** — load-bearing order because `"SAFE"` is a substring of `"UNSAFE"`. Reversed order causes `"UNSAFE"` to match SAFE and route to 35B silently (SC-2 failure). Pattern: `| true, _ -> RouteUnsafe` precedes `| false, true -> RouteSafe`.

2. **max_tokens=8** (not the plan's initial suggestion of 4) — RESEARCH §9 PITFALL #5: 8 tokens absorbs whitespace/punctuation drift from mlx_lm.server while still being fast.

3. **Single-string cache key** (`promptHash: string`) vs JudgeClient's `(promptHash, responseHash)` tuple — self-classify has no response to include in the key; this is the correct substitution per the plan.

4. **PromptVersion computed at construction time** — `File.ReadAllBytes(promptPath)` in the class initializer, not in `ClassifyAsync`. Reflects the prompt state when the service started, not current disk state.

5. **`factory.CreateClient("selfrouter")` exclusively** — `IUpstreamClient` routes through `QueueDispatcher` which holds the 122B `SemaphoreSlim(1)` concurrency gate. Using it would make classifier calls compete with real inference traffic.

6. **No DI registration in 19-01** — adapter is feature-complete but inert. DI registration (`AddHttpClient("selfrouter")` + `services.AddSingleton<ISelfRouter, SelfRouter>()`) is Plan 19-02's responsibility.

## Deviations from Plan

None — plan executed exactly as written.

The plan's code skeleton used `open SmartRouter.Cli.Adapters.DecisionLogger // for computePromptHash` as a guide comment, but `computePromptHash` is called by the _caller_ (ChatCompletions.fs in 19-03), not inside SelfRouter itself. SelfRouter receives `promptHash` as a parameter. No `open DecisionLogger` is needed in the implementation; the plan's comment was indicating architectural intent (shared hash function), not a required import. This matches the actual JudgeClient.fs template which also receives pre-computed hashes.

## Authentication Gates

None.

## Must-Haves Verification

| Truth | Status |
|-------|--------|
| `RoutingReason` has 9th case `SelfRoute` | `grep -c "SelfRoute" Domain.fs` → 1 |
| `formatReason` returns `"self_route"` for SelfRoute | `grep -c "self_route" DecisionLogger.fs` → 1 |
| `ISelfRouter.ClassifyAsync + PromptVersion` exposed | Defined at lines 52–58 in SelfRouter.fs |
| Parser: UNSAFE check before SAFE check | `hasUnsafe` line 90, match arm `\| true, _ -> RouteUnsafe` line 93 |
| `prompts/self-router-prompt.md` with `{{PROMPT}}` | `grep -c "{{PROMPT}}" prompts/self-router-prompt.md` → 1 |
| Project compiles cleanly | `dotnet build`: 0 warnings, 0 errors |

## Test Baseline Preserved

```
150 passed, 17 ignored, 0 failed — identical to Phase 18 baseline
```

No new tests in 19-01 (tests come in 19-03 per plan).

## Commits

| Hash | Type | Description |
|------|------|-------------|
| `0f8448a` | feat | add RoutingReason.SelfRoute DU case + formatReason arm |
| `b46f94a` | feat | add SelfRouter.fs adapter with LRU cache and safety-biased parser |
| `09a38a5` | feat | add prompts/self-router-prompt.md operator-tunable template |

## Next Plan Handoff Notes (19-02)

Plan 19-02 needs to:

1. **Register named `"selfrouter"` HttpClient** in `CompositionRoot.fs`:
   - `AddHttpClient("selfrouter")` with `BaseAddress = Uri(Upstreams.Model35B)` (35B endpoint, not 122B)
   - `AddResilienceHandler` (2 retries, exponential backoff, mirrors judge pattern)
   - `HttpClient.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds)`

2. **Register `SelfRouter` in DI** as `ISelfRouter + ISelfRouterStats`:
   - `services.AddSingleton<SelfRouter>()` — concrete registration needed for double-interface resolution
   - `services.AddSingleton<ISelfRouter>(sp -> sp.GetRequiredService<SelfRouter>())`
   - `services.AddSingleton<ISelfRouterStats>(sp -> sp.GetRequiredService<SelfRouter>())`

3. **Bind `SelfRouterOptions`** from `Routing:SelfRouter` config section

4. **Register in both** `configureRequestPipeline` AND `configureWithoutMl` paths (mirrors SessionStore triple-reg pattern from 18-02)

5. **Expose stats** on `/stats` endpoint (Stats.fs) — `ISelfRouterStats.GetSelfRouterStats()` → cacheHits, cacheMisses, callCount, skipped

The `SelfRouterOptions.Endpoint` field defaults to `""` — CompositionRoot should resolve `""` to `Upstreams.Model35B` at registration time (same pattern as `JudgeOptions.Endpoint` resolves to `Upstreams.Model122B`).
