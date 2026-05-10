---
phase: 16
subsystem: quality-verification
tags: [judge, borderline, lru-cache, entropy, effective-length, fake-kestrel, integration-tests]

dependency-graph:
  requires:
    - phase: 15
      provides: "QualityFallbackOptions (EntropyThreshold + MinResponseLength), charEntropy, analyzeResponse Verdict.Good|Bad, IQualityCheckStats, bad_reason trace field"
  provides:
    - "BorderlineClassifier.fs: classifyBorderline + BorderlineKind DU (UncertainEntropy | UncertainLength)"
    - "JudgeClient.fs: IJudgeClient + IJudgeStats + JudgeVerdict + LRU cache + named HttpClient consumer"
    - "prompts/judge-prompt.md: ROUTE_YES/ROUTE_NO operator-tunable template"
    - "CompositionRoot: Routing.Judge.Enabled opt-in DI gate"
    - "ChatCompletions.fs: borderline → judge → fallback non-streaming cascade"
    - "TraceRecord: 3 new fields (judge_called, judge_verdict, judge_latency_ms)"
    - "StatsWire: 3 new fields (judge_cache_hits, judge_cache_misses, judge_call_count)"
    - "JudgeIntegrationTests.fs: JDG-01..05 + combined (11 testCases, fake-Kestrel)"
  affects:
    - phase: 17
      note: "judge_verdict trace field available for QCLS-03 labeled dataset extraction"

tech-stack:
  added: []   # no new NuGet packages
  patterns:
    - "BorderlineKind as Good qualifier: separate DU from QualityCheck.Verdict — avoids 6 ChatCompletions.fs match-arm cascade"
    - "Hard-code band widths (OQ #1): entropy upper = EntropyThreshold+1.0, length upper = MinResponseLength*1.5 — expose config knobs only when operators demand them"
    - "Inline private helpers (OQ #2): koreanRatio + effectiveLength re-implemented in BorderlineClassifier.fs; QualityCheck.fs visibility unchanged"
    - "Keyword/finish_reason excluded from borderline: binary signals (present/absent), no natural partial zone"
    - "Fail-open judge: JudgeFailed/JudgeSkipped both forward 35B response — judge failures must not suppress good responses"
    - "LRU cache: ConcurrentDictionary<string * string, CacheEntry> keyed on (promptHash, responseHash); monotonic int64 AccessSeq; O(n) min-scan eviction"
    - "Named judge HttpClient: chain form only (.AddHttpClient().ConfigureHttpClient()...); 2-arg form silently drops BaseAddress in F# (ARCH pitfall)"
    - "AddResilienceHandler: 2 retries at 200ms/400ms (judge is hot path; faster than teacher's 3x1s/2s/4s)"
    - "OPT-IN via Routing.Judge.Enabled=false default: mirrors Phase 14 Trace:Enabled pattern"
    - "configureWithoutMl + full manual DI for integration tests: mirrors QualityFallbackTests pattern; configureRequestPipeline requires ML model files (IEmbedder)"
    - "ICanaryState stub required for /stats in judge tests: configureWithoutMl does not register it; GetRequiredService<ICanaryState>() would throw"
    - "CountingJudgeHandler: HttpMessageHandler subclass + Interlocked counter for fake judge HTTP tracking"

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs
    - src/SmartRouter.Cli/Adapters/JudgeClient.fs
    - prompts/judge-prompt.md
    - tests/SmartRouter.Tests/JudgeIntegrationTests.fs
  modified:
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/appsettings.json
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/Endpoints/Stats.fs
    - src/SmartRouter.Cli/Adapters/TraceLogger.fs
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs
    - README.md
    - CHANGELOG.md
    - .planning/docs/quality-check-improvement-options.md
    - .planning/REQUIREMENTS.md

decisions:
  # Researcher open questions (OQ)
  - id: OQ-1
    choice: "Hard-code band widths (entropy upper = EntropyThreshold+1.0, length upper = MinResponseLength*1.5)"
    rationale: "Avoid over-configuration before empirical data; expose knobs only when operators demand them"
  - id: OQ-2
    choice: "Re-implement koreanRatio + effectiveLength as private inline helpers in BorderlineClassifier.fs"
    rationale: "koreanRatio/effectiveLength are private in QualityCheck.fs; charEntropy is public and reused via open import"
  - id: OQ-3
    choice: "Hoist computePromptHash to top of Ok branch in ChatCompletions.fs"
    rationale: "Shared by judge cache key (promptHash) + trace block; single computation, no duplicate SHA-256 work"
  - id: OQ-4
    choice: "JudgeOptions.Endpoint defaults to empty string; CompositionRoot resolves empty to Upstreams.Model122B at DI time"
    rationale: "Operator need not update two config keys when 122B port changes"
  - id: OQ-5
    choice: "2 retries at 200ms/400ms (AddResilienceHandler in CompositionRoot)"
    rationale: "Judge is hot path of borderline; teacher's 3x1s/2s/4s is too slow for a 1-token verification call"
  - id: OQ-6-B3
    choice: "fallback_kind='quality' only when substitution happened; judge-NO-but-retry-failed → fallback_kind=null"
    rationale: "B3 fix: fallback_kind tracks actual routing outcome, not judge signal"
  # Autonomous planner decisions (A, B)
  - id: A-routing-judge-opt-in
    choice: "Routing.Judge.Enabled=false default (OPT-IN)"
    rationale: "Mirrors Phase 14 Trace:Enabled pattern; operators must explicitly enable judge to avoid unexpected 122B traffic"
  - id: B4-clean-di-branch
    choice: "Clean two-branch if/else for IJudgeClient + IJudgeStats DI (not last-registration-wins)"
    rationale: "Mutually exclusive registration is clearer and safer; NoOp IJudgeStats only in else branch"
  # Plan 16-04 autonomous decisions (A-16-04)
  - id: A-16-04-1
    choice: "configureWithoutMl + full manual DI for judge integration tests (not configureRequestPipeline)"
    rationale: "configureRequestPipeline requires ML model files (IEmbedder) unavailable in test env; mirrors QualityFallbackTests"
  - id: A-16-04-2
    choice: "ICanaryState stub (5 members) required in judge integration test router setup"
    rationale: "configureWithoutMl does not register ICanaryState; Stats.mapEndpoints calls GetRequiredService<ICanaryState>"
  - id: A-16-04-3
    choice: "Named judge HttpClient uses chain form only (.AddHttpClient().ConfigureHttpClient()...)"
    rationale: "2-arg AddHttpClient(name, fun c -> ...) silently drops BaseAddress in F# (ARCH pitfall in CompositionRoot.fs)"
  - id: A-16-04-4
    choice: "JDG-01 entropy fixture: String.replicate 20 'abcdefgh' (entropy = log2(8) = 3.0)"
    rationale: "Original 'abcabc'×30 has entropy log2(3)=1.585 which is BELOW EntropyThreshold(2.5); classifyBorderline correctly returned None (code was right, fixture was wrong)"

metrics:
  plans: 4
  duration: "~60 min total"
  completed: "2026-05-11"

test-baseline:
  before: "102 passed, 16 ignored, 0 failed (end of Phase 15)"
  after: "113 passed, 16 ignored, 0 failed"
  new-tests: 11  # JDG-01..05 + combined (JudgeIntegrationTests.fs)
---

# Phase 16 Summary: 122B-as-Judge for Borderline Cases

**One-liner:** OPT-IN 122B judge cascade for borderline 35B responses (entropy/length bands) with LRU cache, fake-Kestrel integration tests (JDG-01..05), and full README/CHANGELOG/REQUIREMENTS.md sync.

## Plan Execution Summary

| Plan | Name | Wave | Key Commits | Outcome |
|------|------|------|-------------|---------|
| 16-01 | Borderline Classifier | 1 | 014bf5b | `BorderlineClassifier.fs` (77 lines): `classifyBorderline` + `BorderlineKind` DU |
| 16-02 | Judge Adapter | 1 | 510fa22, 94291c6, 014bf5b | `JudgeClient.fs` (328 lines): LRU cache + named HttpClient + `IJudgeClient`/`IJudgeStats` + `prompts/judge-prompt.md` |
| 16-03 | Wiring | 2 | 0a52557, a7cfefd, 13128c8, c4685e2 | CompositionRoot DI gate + ChatCompletions cascade + TraceRecord 16 fields + Stats judge counters |
| 16-04 | Tests + Docs | 3 | a0c1c8c, dae2604, afba9f2, 6264825 | JudgeIntegrationTests.fs (11 testCases) + README §5.5.5/§7/§8/§9.3 + CHANGELOG + REQUIREMENTS.md |

## Architecture

### Cascade (non-streaming branch only)

```
analyzeResponse(opts, req, body) → Good
  └─ classifyBorderline(opts, body) → Some borderlineKind
       └─ judgeClient.VerdictAsync(promptHash, responseHash, promptText, body, ct) → verdict
            ├─ RouteYes  → forward 35B response; judge_verdict="yes"
            ├─ RouteNo   → retry to 122B (quality fallback); judge_verdict="no"
            ├─ JudgeSkipped → fail-open; judge_called=true, judge_verdict=None
            └─ JudgeFailed  → fail-open; judge_called=true, judge_verdict=None
```

### Judge Bypass Conditions

- `Routing.Judge.Enabled = false` (config kill switch, default)
- Initial decision target != Qwen35B (122B already chosen; nothing to escalate)
- `analyzeResponse` → `Bad` (quality fallback fires first; judge not reached)
- `analyzeResponse` → `Good` AND `classifyBorderline` → `None` (clearly good; fast path)
- Streaming requests (`INTENTIONALLY SKIPPED` comment preserved)

### LRU Cache

- `ConcurrentDictionary<string * string, CacheEntry>` keyed on `(promptHash, responseHash)` — content hashes
- Monotonic `int64 globalSeq` via `Interlocked.Increment`; `AccessSeq` updated on cache hit
- O(n) min-scan eviction when `count >= MaxCacheEntries` (bounded; operator-configurable)
- Only `RouteYes`/`RouteNo` cached — `JudgeFailed`/`JudgeSkipped` not cached (may be transient/recoverable)

## Configuration

```json
{
  "Routing": {
    "Judge": {
      "Enabled": false,
      "Endpoint": "",
      "PromptPath": "prompts/judge-prompt.md",
      "TimeoutSeconds": 5,
      "MaxCacheEntries": 10000
    }
  }
}
```

`Endpoint: ""` → CompositionRoot resolves to `Upstreams.Model122B` at DI time (operator need not duplicate 122B port).

## New Trace Fields (schema_version=1 unchanged)

| Field | Type | Notes |
|-------|------|-------|
| `judge_called` | bool | false when judge disabled/bypassed |
| `judge_verdict` | string \| null | "yes"/"no"/null (null when JudgeSkipped/JudgeFailed or judge bypassed) |
| `judge_latency_ms` | float \| null | null when judge not called |

## New /stats Fields

| Field | Type | Notes |
|-------|------|-------|
| `judge_cache_hits` | int | LRU cache hits |
| `judge_cache_misses` | int | LRU cache misses (actual HTTP calls) |
| `judge_call_count` | int | Total judge HTTP calls made |

## Test Coverage (11 new tests)

### JDG-01: BorderlineClassifier 3-way classification (3 testCases)
- `None` for clearly good (high entropy + long length)
- `Some UncertainLength` for length in [30, 45)
- `Some UncertainEntropy` for entropy in [2.5, 3.5)

### JDG-02: JudgeClient parser + template loading (2 testCases)
- ROUTE_NO wins on collision (safety bias)
- JudgeSkipped when prompt template file missing

### JDG-03: LRU cache hit/miss + counters (1 testCase)
- Same `(promptHash, responseHash)` → cache hit on 2nd call
- `IJudgeStats`: hits=1, misses=1, callCount=1

### JDG-04: Fake-Kestrel judge HTTP counter (2 testCases)
- Clearly-good response → judge HTTP counter = 0
- Borderline response → judge counter = 1; 2nd identical → cache hit (counter still 1)

### JDG-05: Trace JSONL + /stats (2 testCases)
- Judge disabled: `judge_called=false`, `judge_verdict=null`, `judge_latency_ms=null`; schema_version=1
- After miss+hit: `/stats` counters judge_cache_hits=1, judge_cache_misses=1, judge_call_count=1

### Combined (1 testCase)
- Borderline → judge fires → trace `judge_called=true` / `verdict="yes"` / `latency_ms >= 0.0`
- `/stats`: judge_cache_hits=0, judge_cache_misses=1, judge_call_count=1

## Deviations from Plan (Phase-Level)

### Plan 16-01
None — executed exactly as written.

### Plan 16-02
1. **[Rule 1 - Bug] F# FS0010 abstract member multi-line tuple** — Collapsed `IJudgeClient.VerdictAsync` all 5 params onto single line.
2. **[Note] Wave-1 fsproj bundling** — Both 16-01 (BorderlineClassifier.fs) and 16-02 (JudgeClient.fs) entries landed in commit 014bf5b; correct compile positions confirmed.

### Plan 16-03
1. **[Rule 1 - Bug] Sub-step 3d no-op** — Plan warned to update `TraceRecord` literals in test fixtures; in practice no test constructs `TraceRecord` literally; no test edits needed.
2. **[B3 fix] fallback_kind semantics** — `judgeTriggeredFallback` tracks actual substitution; judge-NO-but-retry-failed → `fallback_kind=null`, not `"quality"`.

### Plan 16-04
1. **[Rule 1 - Bug] QualityFallbackTests.startTestRouter is private** — Inlined full `configureWithoutMl` + manual DI setup for JDG-05 disabled test.
2. **[Rule 1 - Bug] configureRequestPipeline requires ML model files (IEmbedder)** — Switched all integration test router builds to `configureWithoutMl` + manual DI registrations.
3. **[Rule 1 - Bug] ICanaryState not registered by configureWithoutMl** — Added full 5-member `ICanaryState` stub to both `startTestRouterWithJudge` and JDG-05 inline setup.
4. **[Rule 1 - Bug] 2-arg AddHttpClient silently drops BaseAddress in F#** — Changed to chain form `.AddHttpClient("judge").ConfigureHttpClient(...).ConfigurePrimaryHttpMessageHandler(...)`.
5. **[Rule 1 - Bug] JDG-01 entropy fixture wrong entropy** — `"abcabc"×30` → entropy=1.585 (below threshold); changed to `"abcdefgh"×20` → entropy=3.0 ∈ [2.5, 3.5).

## Architectural Properties Preserved

- **ARCH-01**: Zero `src/SmartRouter.Core/` changes across all 4 plans
- **ARCH-02**: All new code uses `task {}` (no `async {}`)
- **OBS-04**: No stdout writes; all logs via ILogger → Serilog → stderr
- **TraceRecord schema_version=1**: Unchanged (additive field expansion only)
- **PITFALL-26 (Expecto)**: `JudgeIntegrationTests.tests` added to explicit `rootTests` list in RouterTests.fs

## Next Phase Readiness

Phase 17 (QualityClassifier distillation endgame) can begin. Prerequisites:
- Phase 16 `judge_verdict` trace field available for QCLS-03 labeled dataset extraction
- Phase 14/15 `bad_reason` + `fallback_kind` fields available for training signal
- 113 passed, 16 ignored, 0 failed baseline stable
