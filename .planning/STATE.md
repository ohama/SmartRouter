# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-05-12 after v2.1 milestone shipped)
See: .planning/MILESTONES.md (v1.3 + v2.0 + v2.1 entries; reverse chronological)

**Core value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.

**Current focus:** v2.2 Operator Fail-Fast on Port Conflict — Phase 25 complete. Milestone ready to tag.

## Current Position

Milestone: v2.2 Operator Fail-Fast on Port Conflict — Phase 25 complete
Phase: Phase 25 — Port-conflict fail-fast at startup — COMPLETE
Plan: 25-01 complete (1/1 plans complete)
Status: All 5 PROBE requirements shipped. Test baseline 187 → 191. README §13 updated. v2.2 ready to tag.
Last activity: 2026-05-12 — Completed 25-01-PLAN.md (4 tasks: PortProbe adapter, tests, Program.fs wire, README recipe)

## Cumulative Project State

| Milestone | Phases | Plans | Tests | Tag | Shipped |
|-----------|--------|-------|-------|-----|---------|
| v1.0–v1.3 | 1-16 | 62 | 113 + 16 ignored | `v1.3.0` / `milestone-v1.3` | 2026-05-11 |
| v2.0 | 17-20 | 12 | 175 + 18 ignored | `milestone-v2.0` | 2026-05-12 |
| v2.1 | 21-24 | 7 (2+3+1+1) | 187 + 18 ignored | `v2.1.0` / `milestone-v2.1` | 2026-05-12 |
| v2.2 | 25 | 1/1 | 191 + 18 ignored | — | Ready to tag |

**Test baseline:** 191 passed + 18 ignored + 0 failed (as of Phase 25 / v2.2; +4 PROBE-04 tests).

## Architecture Invariants (preserved across all 24 phases)

- **ARCH-01:** `SmartRouter.Core` BCL-only (no Serilog / HttpClient / Microsoft.ML / ASP.NET Core)
- **ARCH-02:** `task {}` only (no `async {}`)
- **DecisionLog `schema_version=1`:** unchanged across v1.3 + v2.0 + v2.1 (only additive `routing_reason` enum values across v2.0: `hard_rule`, `sticky_to_122b`, `self_route`)
- **Per-task atomic commits** with `{type}({phase}-{plan}): {task-name}` format
- **Test framework:** Expecto with explicit `rootTests` list (no auto-discovery)

## Session-Key Cascade (as shipped in v2.1)

1. **Tier 1** — `X-Session-Id` HTTP header (explicit always wins)
2. **Tier 2** — System-prompt regex `^Session ID:[ \t]*(\S+)` against first System message (Hermes `--pass-session-id` opt-in)
3. **Tier 3** — SHA-256 content fingerprint: `truncate(system) + "|||" + truncate(firstUser)` → 16-char lowercase hex

`session_extraction_source_header / _sysprompt / _content` counters in `/stats` expose which tier resolved each request.

## Pending Todos

0 captured ideas. (`fail-fast-on-port-conflict-at-startup.md` promoted into v2.2 milestone scope as PROBE-01..05 → Phase 25; todo file retained at `.planning/todos/pending/` for source-tracing.)

## Open Carry-Over Tech Debt (deferred past v2.1)

- **TD-2:** `ModelsTests.fs` IEmbedder errors (MODELS-01..03 currently error or are suppressed). v1.3 carry-over. Non-blocking. Fix: register stub IEmbedder in DI fixture.
- **TD-3:** `configureServices` backwards-compat alias removal (`CompositionRoot.fs:1369`). Blocked on TD-2.
- **TD-4:** Operator live-rig smoke acceptance — `./scripts/smoke-hermes-session.sh` against live mlx_lm.server rig. Operator-manual, not a code gap.
- **TD-5:** `PITFALL-10` timing race in `QueueTests.fs:239-307`. Pre-existing flake — last touched in `fac58b2` (predates Phase 21). `Async.Sleep 30` barrier insufficient when racing `LatencyFake(30)` on occupy slot. ~60% isolation failure rate. Production logic correct (FairnessPicksHigh/Low counter assertions always pass; only FIFO completion order for `high4` is flaky). Fix: replace sleep with `Barrier` or `SemaphoreSlim` guaranteeing all 5 tasks called `EnqueueAsync` before occupy releases.

## Deferred Future Work (no formal REQ-IDs yet)

- **HMRS-FUTURE-01/02** — Hermes Agent custom provider PR for X-Session-Id propagation (operator chose Hermes-less path for v2.1; revisit if upstream opportunity opens)
- **MODE-FUTURE-01** — Hot-reload `Routing.Mode` without restart (FileSystemWatcher pattern)
- **SPEC-01..03** — Speculative routing (35B drafts while router evaluates complexity; mid-generation cancel + switch)
- **DRT-01** — Dedicated tiny router model (Qwen2.5-3B or smaller as separate inference server)
- **DB-01/02** — SQLite `~/.hermes/state.db` direct read (Approach C from `hermes-session-without-modification.md`); deferred due to same-machine coupling

## Session Continuity

Last session: 2026-05-12
Stopped at: Completed 25-01-PLAN.md. Phase 25 all plans done. v2.2 ready to tag.
Resume file: None. Next action: tag v2.2.0 and archive Phase 25 artifacts.
