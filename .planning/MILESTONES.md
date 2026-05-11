# Project Milestones: smart-router

## v1.3 Quality-Aware ML Routing (Shipped: 2026-05-11)

**Delivered:** F# .NET 10 OpenAI-compatible router for local Qwen 35B / 122B with ML-driven 3-stage routing, quality fallback (35B → 122B retry on bad responses), opt-in 122B-as-judge for borderline cases, self-retraining classifier, and canary deployment — all behind a single localhost:4000 gateway.

**Phases completed:** 1-16 (Phase 17 ML QualityClassifier deferred per operator pivot to v2.0 selfrouting)

**Releases:** v1.0.0 → v1.1.0 → v1.1.1 (fix #13) → v1.2.0 → v1.3.0

**Key accomplishments:**

- **Hexagonal F# core** — `SmartRouter.Core` BCL-only (zero Microsoft.ML / HttpClient / Serilog / ASP.NET Core); adapters confined to `SmartRouter.Cli`; ARCH-01 invariant preserved across 16 phases and 62 plans
- **Live ML routing** — bge-m3 int8 ONNX (1024-dim multilingual) + ML.NET LbfgsLogisticRegression; first-run bootstrap auto-generates dummy classifier; PredictionEnginePool hot-swap; ~50ms p95 inference on Mac M-series CPU
- **Closed-loop retraining** — failure detector + 122B teacher labeler (daily cost cap) + dataset writer (Channel + BackgroundService dedupe) + retraining service (PeriodicTimer + SemaphoreSlim skip-if-busy + held-out validation gate)
- **Canary deployment** — `Microsoft.FeatureManagement.AspNetCore` + `ContextualTargetingFilter` (sticky 10/90 split by correlation_id) + rolling-60s fallback rate watchdog with auto-rollback
- **Production hardening** — HealthService (10s upstream probes; ConsecutiveFailureThreshold) + graph_indexing no-fallback rule (503 instead of silent 35B reroute); launchd plist + deploy scripts; rolling Serilog file sink (50MB cap, 30-day retention)
- **Distillation arc** — quality fallback (Phase 14: 35B response fails heuristic → 122B retry); 5-dimension signal enrichment (Phase 15: finish_reason + case-insensitive + Korean length + Shannon entropy + cheap-first cascade); OPT-IN 122B-as-judge for borderline cases (Phase 16: 1-token verification + LRU cache by (prompt_hash, response_hash))
- **Observability** — structured DecisionLog JSONL (12-field schema; one row per request) + opt-in TraceLog JSONL (16 fields with bad_reason + judge_*); /stats endpoint with 21+ flat snake_case fields; correlation_id propagation across all log streams
- **Heuristic retirement** — Phase 12 removed Routing.Algorithm config + --routing-algorithm CLI flag; archive/heuristic-baseline branch + v0.5-heuristic-baseline tag preserved as historical reference

**Stats:**

- 16 phases shipped, 62 plans total
- ~15,000 LOC F# (src + tests)
- 113 tests passed + 16 ignored + 0 failed (final baseline)
- 329 commits across 4 days (2026-05-07 → 2026-05-11)
- 5 NuGet versioned releases on origin/master
- Test coverage: routing pipeline, SSE streaming, concurrency gate, ML classifier, retraining loop, canary, health/fallback, deployment, quality fallback (Phase 14-16), trace logging, /stats wire

**Git range:** `feat(01-01-SCAFFOLD)` → `feat(16-04-TESTS-AND-DOCS)`

**Tags:** `v1.0.0`, `v1.1.0`, `v1.2.0`, `v1.3.0`, `v0.5-heuristic-baseline`, `v1.3-ml-routing` (archive)

**Decimal phases:** None — all phases sequential.

**Key decisions:**

- Hexagonal architecture with Core BCL-only (ARCH-01) — enforced via CI grep
- `task {}` exclusively (no `async {}`) — `scripts/check-no-async.sh` enforces
- Heuristic routing retired in Phase 12 — ML is sole stage-3 algorithm post-Phase 12
- bge-m3 int8 over bge-small — multilingual Korean+English mixed traffic requires it; int8 quantization saves ~580MB; <50ms p95 latency
- Quality fallback as separate post-routing layer (Phase 14) — not folded into routing classifier
- Phase 16 judge OPT-IN (`Routing.Judge.Enabled=false` default) — adds 122B network call on every borderline case; operator opts in after evaluating Phase 15 `quality_check_hits_*` counters
- Issue #13 (`isBadResponse` on raw envelope, not content) — v1.1.1 patch added `extractAssistantText` helper; degrades safely on malformed JSON

**Issues deferred:**

- Phase 17 ML QualityClassifier (distillation endgame) — replaced by v2.0 selfrouting paradigm
- ROADMAP SC#1/SC#2/SC#3 (host launchd UAT) — deferred to operator manual verification (require live macOS host)
- Streaming branch quality fallback — INTENTIONALLY SKIPPED (chunks already shipped; cannot retract)

**What's next:** v2.0 milestone "Self-Routing + Session-Aware" — Hard Rules + 35B self-classify + sticky escalation + Hermes Agent integration per `.planning/docs/35b-selfrouting.md`. ML code retained but dormant (mirroring Phase 12 heuristic retirement pattern).

---
