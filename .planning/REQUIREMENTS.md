# Requirements: Smart Router — v2.1 Hermes-less Session Tiering

**Defined:** 2026-05-12
**Core Value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.
**Milestone:** v2.1 (replace v2.0 IP+UA network fingerprint with a multi-tier session extraction: explicit X-Session-Id header → Hermes `--pass-session-id` system-prompt parse → content fingerprint of conversation prefix)

## v2.1 Requirements

24 requirements across 6 categories. Each maps to a roadmap phase.

### Tier 2 — Hermes System-Prompt Parse (HSP-*)

- [x] **HSP-01**: `SmartRouter.Cli.Adapters.HermesSessionExtract` module with `extractFromSystemPrompt: RouterRequest -> string option`; BCL-only `System.Text.RegularExpressions.Regex` with `RegexOptions.Multiline`; pattern `^Session ID:\s*(\S+)`; returns the captured group when matched, `None` otherwise
- [x] **HSP-02**: Match applies only to the first message with `Role = System` in `req.Messages`; returns `None` when no system message exists OR no `Session ID:` line is present (graceful when operator hasn't enabled `--pass-session-id`)
- [x] **HSP-03**: Regex is module-level pre-compiled (one allocation at module init; no per-request compilation overhead); pattern is case-sensitive (matches Hermes' exact emission `Session ID: `)
- [x] **HSP-04**: Unit tests cover: (a) match-when-line-present (timestamp line + `Session ID: 20260512T1530_a1b2c3` + Model line; extract `20260512T1530_a1b2c3`); (b) no-match-when-line-absent; (c) no-match-when-no-system-message; (d) multi-line system content with `Session ID:` not on first line still matches (Multiline flag); (e) malformed `Session ID:` line with no value returns None

### Tier 3 — Content Fingerprint (CFP-*)

- [x] **CFP-01**: `SmartRouter.Cli.Adapters.ContentFingerprint` module with `compute: RouterRequest -> string`; BCL-only `System.Security.Cryptography.SHA256`; output is 16-character lowercase hex prefix of the SHA-256 digest of `key`, where `key = truncate(system) + "|||" + truncate(firstUser)` and `truncate(s) = if s.Length > 4000 then s.[..3999] else s`
- [x] **CFP-02**: `system` resolves to the content of the first message with `Role = System`, or empty string when absent; `firstUser` resolves to the content of the first message with `Role = User`, or empty string when absent; the function always returns a valid 16-hex string (no `option` wrapper)
- [x] **CFP-03**: Same `(system, firstUser)` input always produces same output (determinism); different inputs produce different outputs with overwhelming probability (cryptographic hash collision resistance); function is pure (no I/O, no allocation beyond the hash buffer)
- [x] **CFP-04**: Unit tests cover: (a) determinism (same input twice → identical output); (b) uniqueness (single character change in either system or firstUser → different output); (c) truncation (>4000-char inputs handled without exception; hash uses only first 4000 chars); (d) empty-message handling (empty system + empty firstUser → valid hash of `"|||"`); (e) Korean+English mixed content produces valid 16-hex (UTF-8 byte encoding correct); (f) 16-hex output is exactly 16 chars, lowercase

### Multi-tier Cascade in CorrelationMiddleware (TIER-*)

- [ ] **TIER-01**: `CorrelationMiddleware` resolves session key in priority order: (1) explicit `X-Session-Id` HTTP header (Tier 1); (2) `HermesSessionExtract.extractFromSystemPrompt` (Tier 2); (3) `ContentFingerprint.compute` (Tier 3). First non-empty value wins; `ctx.Items[SessionIdKey]` stores the resolved key
- [ ] **TIER-02**: `X-Session-Id: ` (empty value) or `X-Session-Id:    ` (whitespace-only value) is treated as absent header → cascade falls through to Tier 2; matches v2.0 SES-04 null-safe behavior
- [ ] **TIER-03**: Tier 2 and Tier 3 are evaluated AFTER `mapWireToRequest` has constructed the `RouterRequest` (need access to `req.Messages`); the resolution happens in `ChatCompletions.fs` request-handler scope OR in a new helper invoked between message parsing and routing — exact location decided during planning (existing `CorrelationMiddleware` runs before body is parsed, so it cannot do Tier 2/3 by itself)
- [ ] **TIER-04**: Cascade applies in BOTH `Routing.Mode = "selfrouting"` AND `Routing.Mode = "ml"` (matches v2.0 Hard Rules + sticky cascade pattern); ML mode also benefits from improved session continuity
- [ ] **TIER-05**: Integration tests verify: (a) header-wins (header + matching system-prompt line → header value used); (b) sysprompt-fallback (no header + Session ID line present → parsed value used); (c) content-fingerprint-fallback (no header + no Session ID line → SHA-256 hash used); (d) sticky escalation (Phase 18 SES-05) continues working across all three tier paths; (e) determinism (same conversation request twice → same resolved session key)

### Observability (OBS-*)

- [ ] **OBS-01**: `/stats` JSON exposes three new flat snake_case Int64 fields: `session_extraction_source_header`, `session_extraction_source_sysprompt`, `session_extraction_source_content`. Each `Interlocked.Increment` once per request based on which tier resolved the session key. Fields exposed via `IStatsProvider` extension and `StatsWire` (mirrors Phase 19 SR-05 `selfrouter_*` pattern)

### v2.0 HMRS-02 Migration (MIG-*)

- [ ] **MIG-01**: `Routing.Session.FingerprintEnabled` config key DELETED from `appsettings.json` and from `SessionOptions` record in `SessionStore.fs`; CLIMutable field removed; no fallback default — this is a breaking change for any operator who had `FingerprintEnabled=true` (likely zero users in production)
- [ ] **MIG-02**: SHA-256(RemoteIp + "|" + User-Agent) block in `CorrelationMiddleware.fs` DELETED; the `fingerprintEnabled: bool` first parameter REMOVED from middleware signature; `Program.fs` startup-time config read for FingerprintEnabled DELETED
- [ ] **MIG-03**: `tests/SmartRouter.Tests/HermesFingerprintTests.fs` (FP-01..FP-08; 8 tests; 130 lines) DELETED entirely; replaced by new HSP-04 + CFP-04 + TIER-05 tests; `SmartRouter.Tests.fsproj` Compile entry removed; `RouterTests.fs` rootTests entry removed
- [ ] **MIG-04**: `scripts/smoke-hermes-session.sh` updated: drop any references to `FingerprintEnabled` config flag; verify cascade test cases (X-Session-Id explicit + sysprompt parse + content fingerprint) instead of header-vs-fingerprint comparison; remains operator-runnable, exits 0/1, no Hermes Agent dependency
- [ ] **MIG-05**: `CHANGELOG.md` `[2.1.0]` block has: `### Removed` (FingerprintEnabled config, IP+UA fingerprint, PROXY-01 warning); `### Added` (sysprompt parse adapter HermesSessionExtract, content fingerprint helper ContentFingerprint, multi-tier cascade in CorrelationMiddleware, `session_extraction_source_*` /stats counters); `### Changed` (CorrelationMiddleware signature; session resolution moved from header-only to 3-tier cascade); `### Notes` (breaking change for FingerprintEnabled users)
- [ ] **MIG-06**: `archive/v2.0-network-fingerprint` git tag (or branch) preserves the pre-v2.1 commit so the network fingerprint code remains reachable for archaeological reference — matches the project pattern of `archive/heuristic-baseline` tag from v1.0

### Documentation (DOC-*)

- [ ] **DOC-01**: README §10 "Hermes / Graphify Integration" rewritten for v2.1 — describes the new 3-tier cascade (header → sysprompt → content); explains `Session ID:` line emission from Hermes `--pass-session-id`; operator opt-in guide covering all four enablement options from the source doc (CLI arg, shell alias `hermes-router='hermes --pass-session-id ...'`, env var `HERMES_TUI_PASS_SESSION_ID=1`, wrapper script). NOT SAFE BEHIND REVERSE PROXIES warning + PROXY-01 callout REMOVED (no longer applies)
- [ ] **DOC-02**: README §7 Configuration Reference — `Routing.Session.FingerprintEnabled` row REMOVED; existing `Routing.Session.TtlMinutes` and `Routing.Session.MaxEntries` rows preserved unchanged
- [ ] **DOC-03**: README §8 `/stats` field reference — `session_extraction_source_header`, `_sysprompt`, `_content` three rows added with semantic explanations matching `IStatsProvider` field meanings
- [ ] **DOC-04**: README §9 DecisionLog reference — no schema changes for v2.1 (resolved session_id is propagated via existing SES-04 channel; no new routing_reason values); section confirmed still correct as written

## Future Requirements

Tracked but not in v2.1 roadmap.

### Tier 2c — SQLite state.db direct read (Approach C from source doc)

- **DB-01**: `SmartRouter.Cli.Adapters.HermesStateDb` adapter reads `~/.hermes/state.db` SQLite (read-only mode) to look up active session by recency + model match; produces session_id AND parent_session_id for compression rotation tracking. Deferred from v2.1 due to same-machine deployment coupling concerns.
- **DB-02**: Race condition handling — Hermes writes session row asynchronously; first request may arrive before DB row exists; fall through to Tier 3 (content fingerprint) when state.db lookup fails.

### Hermes-side X-Session-Id propagation (operator-deferred v2.x)

- **HMRS-FUTURE-01**: Hermes Agent custom provider plugin extended to send `X-Session-Id` header (Hermes session.id propagated downward). Operator decision 2026-05-12: deferred indefinitely; v2.1 ships the no-Hermes-PR alternative path instead.
- **HMRS-FUTURE-02**: Smoke test against live Hermes Agent verifies session_id round-trip with Hermes-side propagation.

### Routing.Mode runtime switch (currently restart-required)

- **MODE-FUTURE-01**: Hot-reload `Routing.Mode` config without restart (FileSystemWatcher pattern).

### Speculative routing (selfrouting doc §17)

- **SPEC-01**: 35B starts draft generation while router evaluates complexity in parallel
- **SPEC-02**: Mid-generation cancel + switch to 122B when complexity detected
- **SPEC-03**: Streaming-compatible speculative path

### Dedicated tiny router model

- **DRT-01**: Optional Qwen2.5-3B (or smaller) router model as separate inference server

## Out of Scope

Explicitly excluded for v2.1.

| Feature | Reason |
|---------|--------|
| State.db direct read (Approach C) | Same-machine + same-FS coupling; race conditions on first request; multi-Hermes-instance matching ambiguity. Deferred (tracked as DB-01/02). |
| mitmproxy injection (Approach D from source doc) | Over-engineered for current scale; adds proxy layer + SSL CA setup; rated ★★ in source doc analysis |
| Per-Hermes router instance (Approach E from source doc) | Resource cost (one router per Hermes); rated ★★ in source doc analysis; not viable for multi-user |
| `X-Forwarded-For` parsing (PROXY-01) | Moot — network fingerprint deleted in v2.1; no reverse-proxy concern remains |
| Hermes Agent code modifications | v2.1 ships smart-router-side only; the entire point is "no Hermes change" per the source doc |
| Collision detection / mitigation for content fingerprint | Source doc §3.7 method 3 — defer to operational data; revisit if hit_rate metrics show genuine collision (very low probability for distinct conversations) |
| Time-windowed fingerprint expiry (source doc §3.5 variant a) | TTL is already enforced by Phase 18 `SessionStore` (default 30 min); no need for fingerprint-side time window |
| Multi-prefix layered hashing (source doc §3.9 variant b) | Single-prefix hash is sufficient for v2.1; defer until collision data justifies complexity |
| Token-based key (source doc §3.9 variant c) | Tokenizer dependency would violate Core BCL-only invariant; defer indefinitely |
| Pre-existing v2.0 features (X-Session-Id header, SessionStore TTL, sticky escalation, Hard Rules, SelfRouter, Routing.Mode) | Unchanged from v2.0; v2.1 only modifies the session-extraction tiering, not the downstream sticky logic |

## Traceability

Assigned by roadmapper 2026-05-12.

| Requirement | Phase | Status |
|-------------|-------|--------|
| HSP-01 | Phase 21 | Complete |
| HSP-02 | Phase 21 | Complete |
| HSP-03 | Phase 21 | Complete |
| HSP-04 | Phase 21 | Complete |
| CFP-01 | Phase 21 | Complete |
| CFP-02 | Phase 21 | Complete |
| CFP-03 | Phase 21 | Complete |
| CFP-04 | Phase 21 | Complete |
| TIER-01 | Phase 22 | Pending |
| TIER-02 | Phase 22 | Pending |
| TIER-03 | Phase 22 | Pending |
| TIER-04 | Phase 22 | Pending |
| TIER-05 | Phase 22 | Pending |
| OBS-01 | Phase 22 | Pending |
| MIG-01 | Phase 22 | Pending |
| MIG-02 | Phase 22 | Pending |
| MIG-03 | Phase 22 | Pending |
| MIG-04 | Phase 22 | Pending |
| MIG-05 | Phase 22 | Pending |
| MIG-06 | Phase 22 | Pending |
| DOC-01 | Phase 23 | Pending |
| DOC-02 | Phase 23 | Pending |
| DOC-03 | Phase 23 | Pending |
| DOC-04 | Phase 23 | Pending |

**Coverage:**
- v2.1 requirements: 24 total (4 HSP + 4 CFP + 5 TIER + 1 OBS + 6 MIG + 4 DOC)
- Mapped to phases: 24/24 (Phase 21: 8, Phase 22: 12, Phase 23: 4)
- Unmapped: 0
- Future: 8 (2 DB + 2 HMRS-FUTURE + 1 MODE-FUTURE + 3 SPEC + 1 DRT)

---
*Requirements defined: 2026-05-12.*
*Source doc: `~/projs/smart-router-distillation/idea/hermes-session-without-modification.md`.*
