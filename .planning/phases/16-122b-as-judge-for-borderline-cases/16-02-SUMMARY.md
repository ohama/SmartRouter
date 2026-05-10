---
phase: 16-122b-as-judge-for-borderline-cases
plan: 02
subsystem: judge-adapter
tags: [judge, lru-cache, http-client, interfaces, ports, quality-verification]

dependency-graph:
  requires:
    - 15-01  # QualityCheck.fs + QualityFallbackOptions (IJudgeClient shares namespace)
    - 16-01  # BorderlineClassifier.fs (fsproj ordering dependency)
  provides:
    - IJudgeClient port (VerdictAsync tupled 5-param signature)
    - IJudgeStats interface (GetJudgeStats() -> struct(int64 * int64 * int64))
    - JudgeVerdict DU (RouteYes | RouteNo | JudgeSkipped | JudgeFailed)
    - JudgeOptions [CLIMutable] record (4 fields)
    - JudgeClient singleton class (LRU cache + named HttpClient consumer)
    - prompts/judge-prompt.md operator-tunable template
  affects:
    - 16-03  # CompositionRoot wiring: registers "judge" named HttpClient + IJudgeClient/IJudgeStats
    - 16-04  # ChatCompletions.fs cascade: consumes IJudgeClient.VerdictAsync
    - 16-05  # Stats.fs + TraceLogger.fs: consumes IJudgeStats.GetJudgeStats()

tech-stack:
  added: []   # no new NuGet packages; all BCL + existing Cli packages
  patterns:
    - named-httpclient-consumer  # mirrors Phase 7 TeacherLabeler "teacher" pattern
    - lru-cache-concurrentdictionary  # ConcurrentDictionary + monotonic int64 AccessSeq
    - fail-open-judge  # JudgeFailed/JudgeSkipped both treated as RouteYes by caller
    - lock-on-first-read-prompt  # promptTemplate mutable + promptLock for template caching

key-files:
  created:
    - prompts/judge-prompt.md           # 15 lines; {{QUESTION}}/{{RESPONSE}} placeholders
    - src/SmartRouter.Cli/Adapters/JudgeClient.fs  # 328 lines; all 4 types + JudgeClient class
  modified:
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj   # +1 Compile entry at line 25

decisions:
  - id: OQ4-endpoint-default
    description: JudgeOptions.Endpoint defaults to empty string; CompositionRoot (16-03) resolves "" to Upstreams.Model122B
    rationale: Avoids operator updating two config keys when 122B port changes
  - id: OQ5-retry-policy
    description: 2 retries at 200ms/400ms (documented in module comment; AddResilienceHandler lives in 16-03)
    rationale: Judge is hot path of borderline; teacher's 3x1s/2s/4s too slow for 1-token call
  - id: decision-B-prompt
    description: prompts/judge-prompt.md uses {{QUESTION}}/{{RESPONSE}} + ROUTE_YES/ROUTE_NO
    rationale: Mirrors teacher-prompt.md style; ROUTE_NO wins on collision (safety bias)
  - id: interface-decl-syntax
    description: IJudgeClient abstract member VerdictAsync declared with all tuple params on single line (not multi-line with *)
    rationale: F# FS0010 — cannot break tuple parameter list across lines in abstract member signatures (discovered at compile time)
  - id: no-cache-judge-failed
    description: JudgeFailed/JudgeSkipped verdicts are NOT cached; only RouteYes/RouteNo are cached
    rationale: Failures may be transient (network blip); Skipped may resolve if operator adds template at runtime
  - id: wave1-fsproj-bundled
    description: fsproj JudgeClient.fs entry was bundled into commit 014bf5b (16-01) due to concurrent wave-1 execution
    rationale: Wave-1 plans run in parallel; Edit tool applied before 16-01 committed the fsproj; both entries present and correct

metrics:
  duration: ~5 minutes
  completed: 2026-05-11
---

# Phase 16 Plan 02: Judge Adapter Summary

**One-liner:** `IJudgeClient` port + `IJudgeStats` + `JudgeClient` with ConcurrentDictionary LRU cache and named "judge" HttpClient consumer; mirrors Phase 7 TeacherLabeler pattern exactly.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | Create prompts/judge-prompt.md | 510fa22 | prompts/judge-prompt.md (15 lines) |
| 2 | Create JudgeClient.fs | 94291c6 | src/SmartRouter.Cli/Adapters/JudgeClient.fs (328 lines) |
| 3 | Register JudgeClient.fs in fsproj | 014bf5b (16-01 wave-1 bundle) | SmartRouter.Cli.fsproj line 25 |

## Artifacts Created

### prompts/judge-prompt.md
- 15 lines, UTF-8, no BOM
- `{{QUESTION}}` placeholder for original prompt text
- `{{RESPONSE}}` placeholder for 35B response content
- `ROUTE_YES` / `ROUTE_NO` sentinels with "Answer ONLY:" instruction
- Operator-tunable via `Routing.Judge.PromptPath` (default path)

### src/SmartRouter.Cli/Adapters/JudgeClient.fs (328 lines)

**Types declared:**
- `JudgeVerdict` DU: `RouteYes | RouteNo | JudgeSkipped of reason | JudgeFailed of err`
- `JudgeOptions` `[<CLIMutable>]` record: `Endpoint, PromptPath, TimeoutSeconds, MaxCacheEntries`
- `IJudgeClient` interface: `VerdictAsync : string * string * string * string * CancellationToken -> Task<JudgeVerdict>`
- `IJudgeStats` interface: `GetJudgeStats : unit -> struct (int64 * int64 * int64)`
- `CacheEntry` (private): `{ Verdict: JudgeVerdict; mutable AccessSeq: int64 }`
- `JudgeClient` class implementing both `IJudgeClient` and `IJudgeStats`

**LRU cache:**
- `ConcurrentDictionary<string * string, CacheEntry>` keyed on `(promptHash, responseHash)`
- Monotonic `int64 globalSeq` counter via `Interlocked.Increment`
- O(n) min-scan eviction when `cache.Count >= maxCacheEntries`
- TOCTOU race on eviction accepted per researcher Pitfall 5 (cache temporarily n+1, not a correctness issue)

**Parser (safety bias):**
- `ROUTE_NO` wins over `ROUTE_YES` on substring collision
- Mirrors Phase 7 `ROUTE_122B`-wins pattern

**Request body:**
- `max_tokens=1, temperature=0.0, stream=false` — 1-token deterministic verification
- `JsonFSharpConverter` required for F# anonymous record `{| role; content |}` in messages array

**ARCH-01 preserved:** Zero `open SmartRouter.Core` imports

### fsproj compile order
Line 23: `QualityCheck.fs`
Line 24: `BorderlineClassifier.fs` (16-01)
Line 25: `JudgeClient.fs` (16-02) ← added
...
Line 60: `ChatCompletions.fs`

## Verification Results

- `prompts/judge-prompt.md`: 4 sentinels/placeholders confirmed; 15 lines; UTF-8
- `JudgeClient.fs`: 41 sentinel/type occurrences; 11 concurrency primitives; 328 lines
- `max_tokens=1` in buildBody confirmed
- `CreateClient("judge")` confirmed (1 occurrence)
- `open SmartRouter.Core` = 0 occurrences (ARCH-01 preserved)
- `JsonFSharpConverter` present in buildBody
- `dotnet build SmartRouter.Cli.fsproj` → 0 warnings, 0 errors (TreatWarningsAsErrors=true)
- `dotnet run -- --sequenced` → 102 passed, 16 ignored, 0 failed (baseline preserved)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] F# abstract member multi-line tuple declaration (FS0010)**

- **Found during:** Task 2 compilation (Task 3 build step)
- **Issue:** Plan sample showed `IJudgeClient.VerdictAsync` with each tuple parameter on its own line separated by `*`. F# FS0010 rejects `->` on the next line after multi-line tuple params in abstract member signatures.
- **Fix:** Collapsed all 5 tuple parameters onto a single line: `promptHash: string * responseHash: string * promptText: string * responseText: string * ct: CancellationToken`
- **Files modified:** `src/SmartRouter.Cli/Adapters/JudgeClient.fs` (line 54-55)
- **Impact:** None — calling convention is unchanged; the declared signature is identical; only the source formatting changed

**2. [Note] Wave-1 fsproj bundling**

- **Found during:** Task 3 commit attempt
- **Situation:** Plan 16-01 ran concurrently (wave-1) and committed the fsproj at 014bf5b. At that moment, the Edit tool had already applied the JudgeClient.fs line to the fsproj working tree, so it was included in 16-01's commit. This is the expected concurrent-edit behavior described in the plan's PARALLEL-EXECUTION CAVEAT.
- **Resolution:** No separate commit needed — both entries present and in correct order; build verified clean.

## Open Questions Resolved

| OQ | Resolution |
|----|-----------|
| OQ #4 (Endpoint default) | Empty string → CompositionRoot resolves to `Upstreams.Model122B` at DI time |
| OQ #5 (Retry count) | 2 retries at 200ms/400ms — documented in `JudgeClient.fs` module comment; `AddResilienceHandler` wiring deferred to 16-03 |
| Decision B (Prompt content) | `prompts/judge-prompt.md` with `{{QUESTION}}`/`{{RESPONSE}}` + `ROUTE_YES`/`ROUTE_NO` |

## Next Phase Readiness

Plan 16-03 (wave 2) can proceed immediately. It needs to:
1. Register `"judge"` named HttpClient with `ConfigureHttpClient` chain + `AddResilienceHandler(2 retries, 200ms/400ms, 5xx + transient)`
2. Register `JudgeClient` as concrete singleton + `IJudgeClient` alias + `IJudgeStats` alias
3. Wire `Routing.Judge.Enabled` config gate (DI conditional)
4. Add NoOp registrations to `configureWithoutMl`
5. Add `Routing.Judge` section to `appsettings.json`
