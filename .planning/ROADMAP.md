# Roadmap: smart-router v2.1 — Hermes-less Session Tiering

## Overview

v2.1 replaces v2.0's network-level IP+UA fingerprint (`HMRS-02` / `FingerprintEnabled`) with a two-new-tier extraction: a system-prompt regex parse that reads Hermes' built-in `--pass-session-id` session_id line (Tier 2), and a SHA-256 content fingerprint of the conversation prefix (Tier 3). The `X-Session-Id` header path (Tier 1) is unchanged. All three tiers cascade in priority order inside `CorrelationMiddleware`. Both `Routing.Mode = "selfrouting"` and `"ml"` use the new tiers, and three new `/stats` counters (`session_extraction_source_*`) expose which tier resolved each request. Phase order is driven by a compile dependency: the new adapters (21) must exist before the middleware cascade can wire them (22), and documentation (23) closes the milestone after the code is verified.

## Milestones

- ✅ **v1.0–v1.3 ML Routing** — Phases 1-16 (shipped 2026-05-11; archived to `.planning/milestones/v1.3-ROADMAP.md`)
- ✅ **v2.0 Self-Routing + Session-Aware** — Phases 17-20 (shipped 2026-05-12; archived to `.planning/milestones/v2.0-ROADMAP.md`)
- 🚧 **v2.1 Hermes-less Session Tiering** — Phases 21-23 (in progress)

## Phases

**Phase Numbering:**
- Integer phases (21, 22, 23): Planned v2.1 milestone work
- Decimal phases (21.1, 21.2): Urgent insertions — none used yet in v2.1
- v2.1 continues numbering from v2.0's end at Phase 20

- [ ] **Phase 21: HSP + CFP Extraction Primitives** — New `HermesSessionExtract` adapter (system-prompt regex, Tier 2) and `ContentFingerprint` helper (SHA-256 prefix hash, Tier 3) with full unit test coverage. Both are BCL-only, live in `SmartRouter.Cli.Adapters`, and are independently testable without HTTP or DI. Ships as pure additions; nothing in `CorrelationMiddleware` changes in this phase.
- [ ] **Phase 22: Cascade Rewire + Migration + Observability** — `CorrelationMiddleware` session-key resolution rewired to the three-tier cascade; `session_extraction_source_*` counters wired; `HMRS-02` code (fingerprint logic, `FingerprintEnabled` config, `HermesFingerprintTests.fs`) deleted; `smoke-hermes-session.sh` updated; `CHANGELOG.md` `[2.1.0]` block written; `archive/v2.0-network-fingerprint` git tag created. Integration tests verify all three tier paths end-to-end, including sticky escalation continuity.
- [ ] **Phase 23: Documentation** — README §10 rewritten for v2.1 paradigm (3-tier cascade + `--pass-session-id` operator guide); §7 `FingerprintEnabled` row removed; §8 three new `/stats` counter rows added; §9.1 DecisionLog section confirmed current.

## Phase Details

### Phase 21: HSP + CFP Extraction Primitives

**Goal**: The codebase contains two new BCL-only extraction adapters — `HermesSessionExtract` and `ContentFingerprint` — that can be called by `CorrelationMiddleware` in Phase 22. Both are pure functions with no I/O, no HTTP, no DI wiring, and are fully covered by unit tests before Phase 22 touches them.

**Depends on**: Nothing (pure additions to `SmartRouter.Cli.Adapters`; no existing code modified)

**Requirements**: HSP-01, HSP-02, HSP-03, HSP-04, CFP-01, CFP-02, CFP-03, CFP-04

**Success Criteria** (what must be TRUE):

1. **System-prompt regex extracts Hermes session_id correctly**: Given a `RouterRequest` whose first `System` message contains a line matching `^Session ID:\s*(\S+)`, `HermesSessionExtract.extractFromSystemPrompt` returns `Some "20260512T1530_a1b2c3"` (the captured group); given a request with no system message, or a system message with no `Session ID:` line, the function returns `None`.
2. **Regex is pre-compiled and case-sensitive**: Accessing `HermesSessionExtract.extractFromSystemPrompt` under load does not allocate a new `Regex` per call; the pattern is a module-level `let private` binding compiled once at module init; the match is case-sensitive (`session id:` does not match).
3. **Content fingerprint is deterministic and always returns 16 lowercase hex chars**: Calling `ContentFingerprint.compute` twice with the same `RouterRequest` returns the same 16-character hex string; a request with empty system + empty first user returns a valid 16-hex hash of `"|||"`; inputs with >4000-char messages are handled without exception (truncated to first 4000 chars before hashing).
4. **Unit tests cover all edge cases**: The test suite passes: (a) HSP match-when-present, no-match-when-absent, no-match-when-no-system-message, multiline-still-matches, malformed-line-no-value; (b) CFP determinism, single-char-change-different-output, truncation safety, empty-message, Korean+English mixed UTF-8, exact-16-char-lowercase output.

**Plans**: 2 plans

Plans:
- [ ] 21-01-PLAN.md — `HermesSessionExtract` adapter (HSP-01..04)
- [ ] 21-02-PLAN.md — `ContentFingerprint` helper (CFP-01..04)

---

### Phase 22: Cascade Rewire + Migration + Observability

**Goal**: Every request through `POST /v1/chat/completions` resolves its session key via a clean three-tier cascade — `X-Session-Id` header wins, then system-prompt parse, then content fingerprint — visible in the new `/stats` extraction-source counters. The v2.0 network-fingerprint code (`SHA-256(RemoteIp+UA)`, `FingerprintEnabled` config, `HermesFingerprintTests.fs`) is entirely deleted. The smoke script verifies the new tier system. The CHANGELOG documents the breaking change.

**Depends on**: Phase 21 (both new adapters must exist before `CorrelationMiddleware` can call them; deletion of `HermesFingerprintTests.fs` requires the replacement tests ship first)

**Requirements**: TIER-01, TIER-02, TIER-03, TIER-04, TIER-05, OBS-01, MIG-01, MIG-02, MIG-03, MIG-04, MIG-05, MIG-06

**Success Criteria** (what must be TRUE):

1. **Header always wins (Tier 1)**: Sending `X-Session-Id: mysession` with a request that also has a `Session ID:` system-prompt line routes with session key `"mysession"` (not the parsed id); `/stats` `session_extraction_source_header` increments by 1.
2. **System-prompt parse activates as Tier 2**: A request with no `X-Session-Id` header but a system message containing `Session ID: 20260512T1530_abc` routes with session key `"20260512T1530_abc"`; `/stats` `session_extraction_source_sysprompt` increments; sending the same session a second time with a trivial "easy" prompt still routes to 122B (`routing_reason="sticky_to_122b"`), confirming sticky escalation continuity through the new tier.
3. **Content fingerprint activates as Tier 3**: A request with no header and no `Session ID:` line receives a deterministic 16-hex session key derived from its conversation prefix; `/stats` `session_extraction_source_content` increments; two identical requests receive the same session key.
4. **Network fingerprint code is gone**: The codebase contains no reference to `FingerprintEnabled`, no `SHA-256(RemoteIp` logic in middleware, and no `HermesFingerprintTests.fs` file; `dotnet build` and `dotnet test` pass with 0 failed tests.
5. **Both Routing.Mode values work**: Integration test passes with `Routing.Mode="ml"` config as well as `"selfrouting"` — session resolution uses the new tiers in both modes.

**Plans**: 3 plans

Plans:
- [ ] 22-01-PLAN.md — `CorrelationMiddleware` cascade rewire + `session_extraction_source_*` stats counters (TIER-01..05 + OBS-01)
- [ ] 22-02-PLAN.md — HMRS-02 deletion: `FingerprintEnabled` config + adapter logic + `HermesFingerprintTests.fs` + `MIG-06` git tag (MIG-01..03 + MIG-06)
- [ ] 22-03-PLAN.md — Smoke script update + CHANGELOG `[2.1.0]` block + integration tests (MIG-04 + MIG-05 + TIER-05 integration)

---

### Phase 23: Documentation

**Goal**: README is accurate for v2.1 — §10 guides operators through the three-tier cascade and the `--pass-session-id` opt-in; the stale `FingerprintEnabled` config row and `PROXY-01` reverse-proxy warning are gone; the three new `/stats` counters are documented; and §9.1 DecisionLog section is confirmed correct as written (no schema changes in v2.1).

**Depends on**: Phase 22 (documentation reflects shipped behavior; §8 counter descriptions must match what `StatsWire` actually emits)

**Requirements**: DOC-01, DOC-02, DOC-03, DOC-04

**Success Criteria** (what must be TRUE):

1. **README §10 describes the three-tier cascade accurately**: An operator reading §10 can follow the guide to enable `--pass-session-id` (via CLI arg, shell alias, env var, or wrapper script) and understand what happens when the option is absent (content fingerprint fallback). The "NOT SAFE BEHIND REVERSE PROXIES" / PROXY-01 callout is absent (moot once network fingerprint is deleted).
2. **README §7 no longer references `FingerprintEnabled`**: The `Routing.Session.FingerprintEnabled` row is removed; `Routing.Session.TtlMinutes` and `Routing.Session.MaxEntries` rows are present and unchanged.
3. **README §8 documents the three new `/stats` fields**: `session_extraction_source_header`, `session_extraction_source_sysprompt`, and `session_extraction_source_content` rows exist with descriptions matching their `IStatsProvider` semantics.
4. **README §9.1 DecisionLog section is confirmed correct**: No schema changes in v2.1 (session_id propagation uses the existing SES-04 channel; no new `routing_reason` values); the section is marked reviewed and unchanged.

**Plans**: 1 plan

Plans:
- [ ] 23-01-PLAN.md — README §10 rewrite + §7 row removal + §8 counter rows + §9.1 review (DOC-01..04)

---

## Progress

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 21. HSP + CFP Primitives | v2.1 | 0/2 | In Progress | — |
| 22. Cascade Rewire + Migration + OBS | v2.1 | 0/3 | Not Started | — |
| 23. Documentation | v2.1 | 0/1 | Not Started | — |

## Coverage

v2.1 requirement coverage: **24 / 24 mapped** (no orphans, no duplicates).

| Phase | Requirements |
|-------|--------------|
| Phase 21 | HSP-01, HSP-02, HSP-03, HSP-04, CFP-01, CFP-02, CFP-03, CFP-04 (8 reqs) |
| Phase 22 | TIER-01, TIER-02, TIER-03, TIER-04, TIER-05, OBS-01, MIG-01, MIG-02, MIG-03, MIG-04, MIG-05, MIG-06 (12 reqs) |
| Phase 23 | DOC-01, DOC-02, DOC-03, DOC-04 (4 reqs) |

## Architectural Invariants (carried into v2.1)

- **ARCH-01** (Core BCL-only): Both new files (`HermesSessionExtract.fs`, `ContentFingerprint.fs`) live in `SmartRouter.Cli.Adapters` — NOT in `SmartRouter.Core`. Core receives no new files in v2.1. Pattern mirrors v2.0 (`SessionStore.fs`, `SelfRouter.fs` in Cli; only `HardRules.fs` was Core-BCL). `System.Text.RegularExpressions` and `System.Security.Cryptography` are BCL but the adapter placement is the invariant.
- **ARCH-02** (`task {}` only): No new async code in v2.1 (both new adapters are synchronous pure functions). `scripts/check-no-async.sh` continues to pass.
- **DecisionLog `schema_version=1` unchanged**: Session key resolution happens upstream of the routing decision; no new `routing_reason` values are needed. The resolved `session_id` flows through the existing `SES-04` channel into the existing DecisionLog `session_id` field. §9.1 confirmed in DOC-04.
- **Both `Routing.Mode` values use the new tiers**: TIER-04 requirement; verified by integration test in Phase 22.
- **Breaking change documented**: `Routing.Session.FingerprintEnabled` config key deleted in MIG-01; documented in MIG-05 CHANGELOG `### Removed` block; README §7 row removed in DOC-02.

---

## Milestone Summary

**Phase count:** 3 (Phases 21, 22, 23)
**Total plans:** 6 (2 + 3 + 1)
**Requirement coverage:** 24/24

**Cascade order after v2.1:**
- Stage 0: Hard Rules (Phase 17 — unchanged)
- Stage 1: Explicit model override (unchanged)
- Stage 2: Explicit task table (unchanged)
- Stage 3: Sticky session — session key now resolved from 3-tier cascade: `X-Session-Id` header → system-prompt `Session ID:` parse → content fingerprint (TIER-01..05)
- Stage 4: 35B self-classify (non-streaming only; Phase 19 — unchanged)
- Stage 5: Default 35B (unchanged)

**Key decisions locked for v2.1:**

1. **Adapter placement**: `HermesSessionExtract` and `ContentFingerprint` go in `SmartRouter.Cli.Adapters`, not Core. BCL regex and BCL SHA-256 are used, but the hexagonal invariant is about file placement, not BCL usage.
2. **TIER-03 placement**: Tier 2 and Tier 3 resolution happen after `mapWireToRequest` constructs the `RouterRequest` (need `req.Messages`). The existing `CorrelationMiddleware` runs pre-body-parse and handles only Tier 1 (header); Tier 2/3 resolve in `ChatCompletions.fs` scope or a helper invoked there. Exact wire-up location decided during 22-01 planning.
3. **MIG-03 delete order**: `HermesFingerprintTests.fs` (FP-01..FP-08) deleted in Phase 22 after replacement tests (HSP-04 + CFP-04 + TIER-05) are in the same or prior commit. Net test count change: -8 (deleted) + new HSP + CFP + TIER tests (estimated +15..20).
4. **`archive/v2.0-network-fingerprint` git tag**: Created in MIG-06 before the deletion commits, so the pre-deletion commit is permanently reachable — matches `archive/heuristic-baseline` and `v0.5-heuristic-baseline` project pattern.
5. **`schema_version=1` unchanged**: Confirmed in DOC-04. No routing_reason additions needed for v2.1.

---

*Roadmap created: 2026-05-12 via `/gsd:new-project` → roadmapper.*
