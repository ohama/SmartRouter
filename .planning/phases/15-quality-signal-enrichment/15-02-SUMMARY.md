---
phase: 15
plan: 02
subsystem: quality-signal-enrichment
tags: [quality-fallback, analyzeResponse, bad_reason, IQualityCheckStats, stats, trace, DI]
requires: ["15-01"]
provides: ["analyzeResponse-cascade", "bad_reason-trace-field", "IQualityCheckStats-counters", "quality_check_hits-stats-fields"]
affects: ["15-03"]
tech-stack:
  added: []
  patterns: ["cheap-first-cascade", "struct-tuple-allocation-free", "volatile-read-counters", "interface-alias-DI"]
key-files:
  created: []
  modified:
    - src/SmartRouter.Cli/Adapters/QualityCheck.fs
    - src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
    - src/SmartRouter.Cli/Adapters/TraceLogger.fs
    - src/SmartRouter.Cli/Endpoints/Stats.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/CompositionRoot.fs
decisions:
  - "IQualityCheckStats as separate interface on QueueDispatcher: cleaner separation; struct tuple in GetHits is allocation-free on /stats hot path"
  - "bad_reason wire format: 'tag=value' with '=' separator (operator jq: .bad_reason | split(\"=\")[0])"
  - "NoOp IQualityCheckStats in configureWithoutMl: object expression inline (no standalone class needed)"
  - "open SmartRouter.Cli.Adapters.QueueDispatcher added to ChatCompletions.fs for IQualityCheckStats access"
metrics:
  duration: "~14 min"
  completed: "2026-05-10"
---

# Phase 15 Plan 02: analyzeResponse and Wiring Summary

**One-liner:** Wired 4-stage cheap-first cascade (analyzeResponse) into ChatCompletions with finish_reason extraction, structured Verdict propagation, bad_reason trace serialization, and 4 IQualityCheckStats counters exposed via /stats.

## Tasks Completed

| Task | Name | Commit | Key Files |
|------|------|--------|-----------|
| 1 | Add analyzeResponse cascade + isBadResponse compat wrapper | 9a17270 | QualityCheck.fs |
| 2 | Add IQualityCheckStats + 4 counters; extend StatsWire | d27dffa | QueueDispatcher.fs, Stats.fs |
| 3 | Add bad_reason to TraceRecord; wire ChatCompletions; register DI | 7b89bc6 | TraceLogger.fs, ChatCompletions.fs, CompositionRoot.fs |

## What Was Built

### Task 1: analyzeResponse cascade (QualityCheck.fs)

Implemented `analyzeResponse opts finishReason responseBody -> Verdict` with cheap-first cascade:
1. Stage 1 — finish_reason match (OrdinalIgnoreCase vs BadFinishReasons array)
2. Stage 2 — effectiveLength (Korean-aware; ASCII backward-compat)
3. Stage 3 — charEntropy (Shannon; skipped when EntropyThreshold <= 0)
4. Stage 4 — matchKeyword (case-insensitive OrdinalIgnoreCase)

`isBadResponse` rewritten as 1-call wrapper: `match analyzeResponse opts None body with Bad _ -> true | Good -> false`. Phase 14 QF-03..QF-08 tests pass unchanged (finishReason=None means Stage 1 always skips; cascade behaves identically from Stage 2 onwards).

### Task 2: IQualityCheckStats + StatsSnapshot + StatsWire (QueueDispatcher.fs, Stats.fs)

- `QualityCheckHits` nested record: FinishReason/Length/Entropy/Keyword (int64)
- `StatsSnapshot.QualityCheckHits` populated via Volatile.Read in GetSnapshot
- `IQualityCheckStats` interface with 4 Record* methods + GetHits (struct tuple, allocation-free)
- `QueueDispatcher` implements IQualityCheckStats via Interlocked.Increment on 4 private mutable fields
- `StatsWire` gains 4 snake_case fields: quality_check_hits_finish_reason/_length/_entropy/_keyword

### Task 3: TraceRecord + ChatCompletions wiring + CompositionRoot DI

**TraceLogger.fs:** `bad_reason: string option` added as 13th field (schema_version stays 1 — additive change). JsonFSharpConverter already registered on TraceLogger's jsonOpts handles string option → null/value correctly.

**ChatCompletions.fs non-streaming branch changes:**
1. Added `open SmartRouter.Cli.Adapters.QueueDispatcher` for IQualityCheckStats
2. Resolve `qualityCheckStats` via GetRequiredService<IQualityCheckStats>()
3. Extract finish_reason: `let initialFinishReason = extractFinishReason initialBody`
4. Run cascade: `analyzeResponse qualityFallbackOpts initialFinishReason initialBody` (when 35B initial; Good for 122B initial)
5. Derive `qualityFallbackTriggered` from Verdict pattern match
6. Match Verdict → call correct Record* method on IQualityCheckStats
7. Compute `badReasonStr` with "tag=value" format
8. Pass `bad_reason = badReasonStr` to TraceRecord construction

**CompositionRoot.fs:**
- `configureRequestPipeline`: `AddSingleton<IQualityCheckStats>` alias resolving from `GetRequiredService<QueueDispatcher>()` (after existing concrete + IUpstreamClient + IStatsProvider registrations)
- `configureWithoutMl`: inline NoOp object expression — no QueueDispatcher in offline path; all Record* methods are unit no-ops; GetHits returns struct (0L, 0L, 0L, 0L)

## Decisions Made

| Decision | Rationale |
|----------|-----------|
| IQualityCheckStats as separate interface (not extending IStatsProvider) | Cleaner separation; Stats endpoint needs only GetSnapshot, not record-hit API |
| struct tuple in IQualityCheckStats.GetHits | Avoids tiny heap allocation on /stats hot path; consistent with existing Interlocked/Volatile patterns |
| GetRequiredService (not GetService) for IQualityCheckStats in ChatCompletions | Interface registered unconditionally in both paths — null-guard pattern not needed |
| Inline NoOp object expression in configureWithoutMl | No new class/module needed; object expression is idiomatic F# for one-off interface impls |
| open QueueDispatcher added to ChatCompletions.fs | ChatCompletions.fs was using QueueDispatcher types indirectly via IUpstreamClient; now explicit |
| bad_reason only on initial verdict (not retry verdict) | Represents why fallback was triggered, not the quality of the retry response |

## Compile Order Surprises

None unexpected. The planned order was followed:
1. QualityCheck.fs (types + cascade) — Task 1
2. QueueDispatcher.fs (IQualityCheckStats interface + counters) + Stats.fs — Task 2
3. TraceLogger.fs (bad_reason field) + ChatCompletions.fs (caller) + CompositionRoot.fs (DI) — Task 3

The plan noted `open SmartRouter.Cli.Adapters.QueueDispatcher` might be needed in ChatCompletions.fs — confirmed: it was missing and was added in Task 3.

## Backward-Compat Verification (QSE-06)

All 8 QF-* tests pass unchanged:
- QF-03 (short content): effectiveLength on ASCII = length; Stage 2 still fires as LengthBelow
- QF-04 (keyword "TODO"): uppercase fixture → case-insensitive OrdinalIgnoreCase still matches uppercase "TODO"
- QF-05 (envelope keyword): extractAssistantText still isolates content; envelope keyword doesn't appear in content
- QF-06 (malformed JSON): extractAssistantText returns ""; effectiveLength=0 < 30 → LengthBelow; true
- QF-07 (Enabled=false): short-circuits to Good → false
- QF-08 (defensive text extraction): same extraction logic

## Deviations from Plan

None — plan executed exactly as written.

Pre-existing flaky test: First run of the full suite showed 1 failure (streaming cancellation TaskCanceledException). This is the documented pre-existing Phase 7/8 flakiness. Second and third runs: 88 passed + 16 ignored + 0 failed. Plan 15-02's changes do not affect the streaming branch.

## Authentication Gates

None required.

## Counter Behavior

- Process-lifetime, never reset
- Incremented via Interlocked.Increment (thread-safe; no lock contention)
- Read via Volatile.Read in GetSnapshot() (consistent snapshot without lock)
- Initial value at startup: all 4 counters = 0
- Semantics: only the WINNING (first-match) reason is counted per request

## /stats Wire Shape Change

4 new flat int64 fields added to the StatsWire JSON body:
```json
{
  "quality_check_hits_finish_reason": 0,
  "quality_check_hits_length": 0,
  "quality_check_hits_entropy": 0,
  "quality_check_hits_keyword": 0
}
```

Snake_case matches existing field naming convention. All initialize to 0 — no traffic = no hits.

## Test Baseline Confirmation

After all 3 tasks: 88 passed + 16 ignored + 0 failed (unchanged from Phase 14 baseline).

## Open Questions for Plan 15-03

- Any new bad_reason values expected in QF-01/QF-02 trace files? None expected — those integration tests use "stop" finish_reason (not in BadFinishReasons default ["length","content_filter"]) and content that either passes all 4 stages or fails at keyword stage with "TODO".
- Plan 15-03 will add QSE-* unit tests exercising each of the 4 cascade stages and the bad_reason serialization format.
- Plan 15-03 should verify /stats returns non-zero quality_check_hits_* after triggering each dimension.
