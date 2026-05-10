# Plan 16-03 Summary — Wiring

**Phase:** 16 (122B-as-Judge for Borderline Cases)
**Plan:** 16-03
**Wave:** 2
**Status:** Complete
**Date:** 2026-05-11

## Tasks Completed

| Task | Description | Commit |
|------|-------------|--------|
| 1 | Extend `TraceRecord` with 3 new fields (`judge_called`, `judge_verdict`, `judge_latency_ms`); schema_version=1 unchanged (additive) | `0a52557` |
| 2 | Add `Routing.Judge` block to `appsettings.json` (Enabled=false default; OPT-IN per autonomous decision A) | `a7cfefd` |
| 3 (DI) | Register `IJudgeClient` + `IJudgeStats` in CompositionRoot — clean two-branch if/else (B4 fix); real triple-reg in `then` branch when Enabled=true; NoOp `IJudgeStats` only in `else` branch; `configureWithoutMl` registers NoOp `IJudgeStats` only | `13128c8` |
| 3a + 3b + 3c | Wire borderline → judge → fallback in `ChatCompletions.fs` non-streaming branch + extend `Stats.fs` with 3 new judge counters (flat snake_case) | `c4685e2` |
| 3d | TraceRecord literal updates in test fixtures — **NO-OP** (no test file constructs `TraceRecord` literally; build passes clean) | n/a |

## Key Decisions Honored

| ID | Decision | Implementation |
|----|----------|----------------|
| **OQ3** | Hoist `computePromptHash` to top of Ok branch | `let promptHash = computePromptHash req.Messages` immediately after `Ok initialBody` match arm; shared by judge cache key + trace block |
| **OQ4** | `JudgeOptions.Endpoint` empty → derive from `Upstreams.Model122B` | Implementation in `JudgeClient.fs` (Plan 16-02); `appsettings.json` default empty `""` |
| **OQ5** | 2 retries at 200ms/400ms | `AddResilienceHandler` configured in 16-02 |
| **OQ6** + **B3** | `fallback_kind="quality"` only when SUBSTITUTION happened | Tuple element 6 (`judgeTriggeredFallback`) is `false` on judge-NO-but-retry-failed; trace records `judge_called=true`/`judge_verdict="no"` while `fallback_kind=null` |
| **A** | `Routing.Judge.Enabled = false` default | OPT-IN; appsettings.json default; mirrors Phase 14 `Trace:Enabled` config-read pattern |
| **B4** | Clean two-branch if/else for IJudgeStats DI | Mutually exclusive registration; NOT last-registration-wins |

## Files Modified

- `src/SmartRouter.Cli/Adapters/TraceLogger.fs` — TraceRecord 13 → 16 fields
- `src/SmartRouter.Cli/appsettings.json` — `Routing.Judge` block (Enabled, Endpoint, PromptPath, TimeoutSeconds, MaxCacheEntries, MaxRetries)
- `src/SmartRouter.Cli/CompositionRoot.fs` — IJudgeClient + IJudgeStats DI in both composition paths
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — borderline → judge → fallback wiring (+225 lines diff)
- `src/SmartRouter.Cli/Endpoints/Stats.fs` — 3 new judge counters in StatsWire + null-safe IJudgeStats resolve

## Test Baseline

**102 passed + 16 ignored + 0 failed** — backward-compat preserved across all of Phase 14 (QF-01..10), Phase 15 (QSE-01..06), and issue #13 (QF-03..08). Phase 16 introduces no new tests in 16-03 — those land in 16-04.

## Streaming Branch

INTENTIONALLY SKIPPED comment preserved (line in non-streaming branch begin). Judge logic exclusively in non-streaming `else` branch.

## Notable Implementation Patterns

1. **Fail-open on judge errors**: `JudgeFailed err` → forward 35B response, log warning, set `judge_called=true`/`judge_verdict=None`. Judge infrastructure failures must not suppress good 35B responses.

2. **Judge bypassed for non-Qwen35B initial decisions**: `if initialDecision.Target <> Qwen35B then return defaults` — judge has nothing to escalate to when 122B was already chosen.

3. **Sub-step 3d no-op**: Sub-step 3d ("update test fixture TraceRecord literals") was a precaution against compile failures from the 13 → 16 field expansion. In practice, no test file constructs `TraceRecord` literally; all production callers go through `traceLogger.Log({ ... })` which the compiler checks at the call site (commit `c4685e2`'s ChatCompletions update is the sole call site).

## Architectural Properties Preserved

- ARCH-01: Zero changes to `src/SmartRouter.Core/`
- ARCH-02: All new code uses `task {}` (no `async {}`)
- OBS-04: No stdout writes; all logs go to ILogger which routes to stderr via Serilog
- TraceRecord schema_version=1 unchanged (additive field expansion only)

## Commits Made

```
c4685e2  feat(16-03): wire borderline → judge → fallback in ChatCompletions; expose judge counters in /stats
13128c8  feat(16-03): register IJudgeClient + IJudgeStats DI in CompositionRoot (opt-in via Routing.Judge.Enabled)
a7cfefd  feat(16-03): add Routing.Judge config block to appsettings.json (Enabled=false default)
0a52557  feat(16-03): extend TraceRecord with judge_called/judge_verdict/judge_latency_ms (schema_version=1 unchanged)
```

## Ready for Wave 3

Plan 16-04 (tests + docs) can now proceed:
- `JudgeIntegrationTests.fs` exercises the wired call path (JDG-01..05 concrete assertions)
- README §5/§7/§8/§9.3 + CHANGELOG `[Unreleased] ### Added`
- REQUIREMENTS.md JDG-01 alignment update (B1 fix)
