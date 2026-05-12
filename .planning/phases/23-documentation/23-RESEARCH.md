# Phase 23: Documentation — Research

**Researched:** 2026-05-12
**Domain:** README.md operator documentation — v2.1 behavior sync
**Confidence:** HIGH

---

## Summary

Phase 23 is a pure documentation phase. All implementation work shipped in Phase 22 (commits
`5e04118`, `fe5c3f0`, `814b46e`, `cfa5c91`, `c966d70`, `0af61b6`, `9806ebd`, `b8d1796`,
`1842491`, `938c8ac`). No code changes are needed.

The README has four areas of drift from v2.1 shipped behavior:

1. **§10 Hermes Integration (lines 781–875):** Describes v2.0 behavior only — the X-Session-Id
   header opt-in with sticky escalation, and the now-deleted IP+UA fingerprint fallback with its
   PROXY-01 warning. The entire "Fingerprint fallback" sub-section (lines 822–849) describes
   deleted code (`FingerprintEnabled`, `HMRS-FUTURE-01`, `SHA-256(RemoteIp+UA)`). §10 needs a
   rewrite that (a) updates the heading to v2.1, (b) describes the three-tier cascade, (c)
   documents the four `--pass-session-id` enablement options, and (d) removes the PROXY-01 block.

2. **§8 /stats (lines 567–620):** The three new `/stats` fields added in Phase 22 OBS-01
   (`session_extraction_source_header`, `session_extraction_source_sysprompt`,
   `session_extraction_source_content`) are not documented. The existing example JSON and field
   table end at `selfrouter_skipped`. New rows need to be added.

3. **§7 Configuration Reference — Routing.Session (lines 441–448):** DOC-02 was already
   completed in commit `938c8ac` (Plan 22-03). The `FingerprintEnabled` row is already removed.
   `TtlMinutes` and `MaxEntries` rows are present and correct. No action needed for §7.

4. **§9.1 DecisionLog schema (lines 636–668):** No schema changes in v2.1 (the resolved
   session_id passes through the existing `SES-04` channel). Section text is still accurate.
   Confirm only — no changes needed.

**Primary recommendation:** One plan (23-01) that rewrites §10 and adds three rows to §8. §7 is
already done; §9.1 only needs a read-and-confirm step in the plan checklist.

---

## Standard Stack

This phase has no code dependencies. The work is:
- Read current README sections
- Write replacement prose for §10
- Insert three table rows into §8
- Verify §7 and §9.1 are already correct

No libraries, no NuGet packages, no build steps.

---

## Current State of README Sections

### §10 Hermes / Graphify Integration (lines 781–875)

**Current sub-sections and their fate in v2.1:**

| Sub-section | Lines | Status |
|------------|-------|--------|
| `### Hermes Agent (v2.0 session-aware selfrouting)` | 783–789 | REPLACE — describes v2.0 cascade; needs v2.1 three-tier update |
| `#### Hermes config (unchanged from v1.x)` | 791–800 | KEEP — jsonc config block still accurate |
| `#### X-Session-Id header opt-in (session continuity)` | 801–820 | REWRITE — v2.0 framing "future work HMRS-FUTURE-01" is now wrong; Tier 1 is now the standard first-try, not opt-in header |
| `#### Fingerprint fallback (opt-in; loopback single-client only)` | 822–849 | DELETE ENTIRELY — describes deleted code (`FingerprintEnabled`, `SHA-256(RemoteIp+UA)`, `HMRS-FUTURE-01`); PROXY-01 warning must go |
| `### Graphify (concurrency-protected; sends task)` | 851–875 | KEEP AS-IS — unaffected by v2.1 |

**Key content that must be removed:**
- Line 813: "Hermes-side `X-Session-Id` propagation ... is **future work** tracked as HMRS-FUTURE-01"
- Line 824: "When `Routing.Session.FingerprintEnabled=true` ..."
- Lines 826–827: "`SHA-256(RemoteIpAddress + "|" + User-Agent)` ..."
- Lines 831–837: The `> **WARNING — NOT SAFE BEHIND REVERSE PROXIES.** ... PROXY-01 ...` block
- Lines 839–848: "Known nuances: A null `RemoteIpAddress` ..." + "Configuration: `Routing.Session.FingerprintEnabled`..."

**Section heading:** Change `### Hermes Agent (v2.0 session-aware selfrouting)` to
`### Hermes Agent (v2.1 — three-tier session cascade)`.

**Current §10 accurate reference count:** Lines 791–800 (Hermes config jsonc) and 851–875
(Graphify) are correct and must be preserved unchanged.

### §7 Configuration Reference — Routing.Session (lines 441–448)

**Current state (ALREADY CORRECT — DOC-02 complete):**

```
### Routing.Session

Phase 18. Controls the in-memory session store used by sticky escalation (§5.6).

| Key | Type | Default | Description |
|---|---|---|---|
| `Routing.Session.TtlMinutes` | int | `30` | Session-entry sliding TTL ...
| `Routing.Session.MaxEntries` | int | `10000` | Bound for the in-memory session store...
```

`FingerprintEnabled` row is gone (deleted in commit `938c8ac`). `TtlMinutes` and `MaxEntries`
rows are present. **No changes needed.**

### §8 /stats (lines 567–620)

**Current state — the JSON example and Phase 19 counter block end at `selfrouter_skipped`.**

The existing stats example JSON (lines 570–580) does NOT include the three new Phase 22 fields.
The existing field description tables end at the Phase 19 block (lines 607–620).

**Fields that need to be added** (exact JSON field names from `StatsWire` record in
`src/SmartRouter.Cli/Endpoints/Stats.fs`):

| JSON field name | F# record field | Source |
|----------------|-----------------|--------|
| `session_extraction_source_header` | `session_extraction_source_header : int64` | `ISessionCascadeStats.RecordHeader()` |
| `session_extraction_source_sysprompt` | `session_extraction_source_sysprompt : int64` | `ISessionCascadeStats.RecordSysprompt()` |
| `session_extraction_source_content` | `session_extraction_source_content : int64` | `ISessionCascadeStats.RecordContent()` |

**Table format to match** (from existing Phase 19 block at lines 607–614):

```markdown
**Phase [N] — [name] counters** (all `int64`, process-lifetime, [zero condition]):

| Field | Description |
|---|---|
| `field_name` | Description |
```

**Existing Phase 19 block for reference (exact format to copy):**

```markdown
**Phase 19 — self-router counters** (all `int64`, process-lifetime, 0 when `Routing.Mode="ml"`):

| Field | Description |
|---|---|
| `selfrouter_cache_hits` | A non-streaming Default-reason request found its prompt hash in the LRU cache (no HTTP call). |
| `selfrouter_cache_misses` | A non-streaming Default-reason request's prompt hash was not in the cache; an HTTP classify call was initiated. |
| `selfrouter_call_count` | An HTTP classify call to the `"selfrouter"` named client was sent. Approximately equals `selfrouter_cache_misses` (minus any `selfrouter_skipped`). |
| `selfrouter_skipped` | Adapter returned `RouteSkipped` — typically because `prompts/self-router-prompt.md` is missing at the path specified by `Routing.SelfRouter.PromptPath`. Does **not** count streaming requests — streaming skip is structural and never reaches the adapter. |
```

**New block to add** (after the Phase 19 block, before the `curl` example):

```markdown
**Phase 22 — session cascade counters** (all `int64`, process-lifetime; exactly one counter
increments per request regardless of `Routing.Mode`):

| Field | Description |
|---|---|
| `session_extraction_source_header` | Requests where `X-Session-Id` HTTP header was present and non-empty (Tier 1). Operator-initiated via `hermes --pass-session-id` or direct header injection. |
| `session_extraction_source_sysprompt` | Requests where header was absent but a `Session ID: <id>` line was found in the first system message (Tier 2). Indicates Hermes `--pass-session-id` is active. |
| `session_extraction_source_content` | Requests that fell through to SHA-256 content fingerprint — `SHA-256(system + "|||" + firstUser)[..15]` (Tier 3). Indicates Hermes is running without `--pass-session-id`. |
```

**Also add to the stats JSON example** (lines 570–580): the three new fields should appear at the
end of the example JSON object so operators can see their shape.

**Also add a `jq` snippet** (following the Phase 19 curl snippet pattern) for monitoring cascade
tier distribution.

### §9.1 DecisionLog schema (lines 636–668)

**Current state — CONFIRMED CORRECT, no changes needed.**

- `schema_version: 1` — unchanged in v2.1
- `session_id` is not a field in the DecisionLog schema (it is not emitted; sticky decisions
  are recorded via `routing_reason="sticky_to_122b"` already present in the `routing_reason`
  enum list at line 662)
- No new `routing_reason` values were added in v2.1 (Phase 22 is purely a session-key source
  change; the routing decision and `routing_reason` values are unchanged)
- The `schema_version=1` note at line 659 says "All v2.0 additions (Phase 17–19) are additive
  enum values" — should be updated to say "Phase 17–22" or "v2.0–v2.1" but this is a cosmetic
  update, not a schema correction. The Phase 23 plan should decide whether to update this wording.

---

## Three-Tier Cascade Authoritative Description

This is the canonical behavior to describe in §10, derived directly from
`src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` lines 183–207 and
`src/SmartRouter.Cli/Adapters/HermesSessionExtract.fs` and
`src/SmartRouter.Cli/Adapters/ContentFingerprint.fs`:

```
Tier 1 — X-Session-Id HTTP header
  - CorrelationMiddleware extracts the header value before body parse.
  - Non-empty value short-circuits the cascade immediately.
  - Set by: hermes --pass-session-id (which emits the id as a header when
    the Hermes build supports it) OR direct client injection.
  - Counter: session_extraction_source_header

Tier 2 — System-prompt Session ID line
  - When header is absent/empty, the first System message is scanned for
    a line matching: ^Session ID:[ \t]*(\S+)  (case-sensitive, multiline regex)
  - This line is emitted by Hermes when `--pass-session-id` is active:
      run_agent.py:5764-5766 appends "\nSession ID: {self.session_id}" to
      the timestamp_line in the system prompt.
  - Counter: session_extraction_source_sysprompt

Tier 3 — Content fingerprint (always-on fallback)
  - When both Tier 1 and Tier 2 miss, a 16-char lowercase hex fingerprint
    is computed: SHA-256(truncate(system, 4000) + "|||" + truncate(firstUser, 4000))
    first 16 hex chars.
  - Always produces a value (never None); provides sticky escalation
    continuity even with no Hermes opt-in.
  - Counter: session_extraction_source_content
```

**Important behavioral note for §10:** ALL three tiers are always active — there is no config
flag to disable Tier 2 or Tier 3. The cascade runs on every request. The operator can observe
which tier is winning via `/stats session_extraction_source_*` counters.

**Counter semantics:** Exactly ONE counter increments per request (mutual exclusion via the
Tier 1 → 2 → 3 fallback chain in `resolveSessionCascade`). The three counters are
process-lifetime `int64`, never reset, and are not affected by `Routing.Mode`.

---

## Four `--pass-session-id` Enablement Options

Source: `~/projs/smart-router-distillation/idea/hermes-session-without-modification.md`,
Section 2.3 "운영 가이드" and Section 2.7 "Stage 0 — 사전 준비" (the four a1–a4 options).

These are the EXACT four enablement options to document in §10:

**Option 1 — Direct CLI argument:**
```bash
hermes --pass-session-id --base-url http://localhost:4000/v1
```
Simple but requires typing the flag every session.

**Option 2 — Shell alias (recommended):**
```bash
# ~/.zshrc or ~/.bashrc
alias hermes-router='hermes --pass-session-id --base-url http://localhost:4000/v1'
```
Run `hermes-router` instead of `hermes`. Never forget the flag.

**Option 3 — Environment variable:**
```bash
export HERMES_TUI_PASS_SESSION_ID=1
hermes-tui
```
`hermes_cli/main.py:1276` reads `HERMES_TUI_PASS_SESSION_ID` and auto-enables the option.
Set in `~/.zshrc` for persistent enablement.

**Option 4 — Wrapper script (most robust):**
```bash
#!/bin/bash
# /usr/local/bin/hermes-with-router
exec hermes --pass-session-id --base-url http://localhost:4000/v1 "$@"
```
Works for multiple users; cannot be forgotten.

**Key operator note for §10:** Without `--pass-session-id`, Tier 2 never fires and requests fall
through to Tier 3 (content fingerprint). Tier 3 is less precise than Tier 2 for context
compression scenarios (when Hermes rebuilds the message list, the fingerprint changes). Tier 2
uses Hermes's internal `session_id` directly — exact and stable across context compression.

---

## `IStatsProvider` — Exact Field Names and Semantics

Source: `src/SmartRouter.Cli/Endpoints/Stats.fs` (`StatsWire` record, lines 51–53) and
`src/SmartRouter.Cli/Adapters/SessionCascadeStats.fs` (`ISessionCascadeStats` interface).

**JSON field names (exact, as emitted by System.Text.Json with `StatsWire`):**

```json
"session_extraction_source_header":    <int64>,
"session_extraction_source_sysprompt": <int64>,
"session_extraction_source_content":   <int64>
```

Note: `session_extraction_source_sysprompt` has a double-p in `sysprompt` — this matches the F#
record field name exactly. The README must use this spelling.

**Semantics (from `ISessionCascadeStats` doc comments):**

- `session_extraction_source_header`: Tier 1 hit — `X-Session-Id` HTTP header was present and
  non-empty. Incremented by `RecordHeader()`.
- `session_extraction_source_sysprompt`: Tier 2 hit — header was absent; `extractFromSystemPrompt`
  returned `Some`. Incremented by `RecordSysprompt()`.
- `session_extraction_source_content`: Tier 3 hit — header absent AND sysprompt absent; fell
  through to `ContentFingerprint.compute`. Incremented by `RecordContent()`.

**Zero conditions:** All three are `int64`, process-lifetime, never reset. They apply in BOTH
`Routing.Mode="selfrouting"` and `Routing.Mode="ml"` (unconditional DI registration — both
`configureRequestPipeline` and `configureWithoutMl` register `ISessionCascadeStats`).

---

## Cross-Cutting README Drift Inventory

Searched for all stale references related to Phase 22 changes:

| Pattern searched | Hits | Action required |
|----------------|------|----------------|
| `FingerprintEnabled` | Line 824, 836, 848 | Remove entire "Fingerprint fallback" sub-section (§10 only) |
| `PROXY-01` | Line 832 | Remove with the WARNING block |
| `SHA-256(RemoteIp` | Line 826 | Remove with the Fingerprint fallback sub-section |
| `HMRS-FUTURE-01` | Lines 813, 828 | Remove — v2.1 ships the sysprompt alternative; the HMRS-FUTURE-01 future-work framing is no longer accurate |
| `session_extraction_source_*` | 0 hits in README | Add — new §8 rows needed |
| `v2.0 ships smart-router-side only` | Line 814 | Remove/rewrite — v2.1 ships Tier 2 sysprompt parse |

**Other potential drift (confirmed NOT drift):**

- Line 659 `schema_version` description: "All v2.0 additions (Phase 17–19)" — technically could
  say "Phase 17–22" but v2.1 has no schema changes. This is imprecise but not wrong. The
  planner should decide if a cosmetic update is in scope for Phase 23.
- §5.6 Stage 3 (sticky session escalation, lines 280–310): describes how `X-Session-Id` feeds
  sticky. This is still accurate in v2.1 — the cascade resolves the session key and then
  `routeRequest` uses it for sticky. No change needed.
- `HMRS-FUTURE-01` in `.planning/REQUIREMENTS.md` — this is a planning artifact, not README. Not
  in scope for Phase 23.

**Line-level audit of §10 stale references:**
- 813: "is **future work** tracked as HMRS-FUTURE-01" → remove entire sentence
- 814: "v2.0 ships smart-router-side machinery only" → remove / replace with v2.1 description
- 822: `#### Fingerprint fallback (opt-in; loopback single-client only)` → DELETE entire sub-section (lines 822–849)

---

## v2.1 Documentation Voice and Patterns

The README uses a consistent style that §10 rewrite must match:

**Pattern: Phase-tagged operator opt-in guide** (from §5.7 `Routing.Mode="selfrouting"`, lines 312–357):
- Lead with what the feature does and when it activates
- Show the config key or flag
- Provide a brief "operator note" for the common workflow
- Cross-reference other sections with §N notation

**Pattern: Multi-option enablement guide** (from §11.1 launchd setup):
- Numbered or labeled options in code blocks
- Each option labeled with its trade-off

**Pattern: Stats counter documentation** (from Phase 15 and Phase 19 blocks):
```markdown
**Phase N — name counters** (all `int64`, process-lifetime, 0 when [condition]):

| Field | Description |
|---|---|
| `field_name` | Description. |
```

**Tone:** Operator-facing, imperative. Short sentences. No jargon. Cross-reference via §N. Warn
about edge cases inline.

---

## Architecture Patterns

No code architecture applies to this phase. The "architecture" is the README section structure
itself, documented above.

### README Section Dependencies (for the rewrite)

§10 cross-references these sections that must stay consistent:
- §5.6 (sticky session escalation) — unchanged; the cascade resolves the key that feeds §5.6
- §7 Routing.Session — `TtlMinutes`, `MaxEntries` rows are correct; no cross-reference changes needed
- §8 /stats — the three new fields are additions; no existing rows change

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Describing the cascade | Custom narrative from memory | Derive from `resolveSessionCascade` source code (ChatCompletions.fs lines 183–207) | The function comment IS the authoritative spec |
| Counter semantics | Infer from interface name | Read `ISessionCascadeStats` doc comments in `SessionCascadeStats.fs` | Exact wording for operator-facing docs |
| Field name spelling | Guess `sysprompt` vs `sys_prompt` | Read `StatsWire` record in `Stats.fs` lines 51–53 | `session_extraction_source_sysprompt` is the exact JSON name |
| Enablement options | Paraphrase from memory | Quote from `hermes-session-without-modification.md` §2.7 options a1–a4 | Shell alias and env var syntax must be exact |

---

## Common Pitfalls

### Pitfall 1: `HMRS-FUTURE-01` framing must be completely removed
**What goes wrong:** Leaving any mention of HMRS-FUTURE-01 or "future work" for the session
header in §10 gives operators the wrong impression that v2.1 is still incomplete.
**Root cause:** The v2.0 text framed the X-Session-Id header as "smart-router side only; Hermes
side is future work." In v2.1, Tier 2 (sysprompt parse) is the operator-facing solution — no
Hermes code change needed.
**How to avoid:** Delete lines 812–816 entirely. The new §10 should say Tier 2 is the primary
Hermes → smart-router session continuity mechanism, activated via `--pass-session-id`.

### Pitfall 2: `session_extraction_source_sysprompt` spelling
**What goes wrong:** Writing `_sys_prompt` or `_system_prompt` in the README.
**Root cause:** The F# field is `session_extraction_source_sysprompt` (no underscore between
`sys` and `prompt`). System.Text.Json serializes the record field as-is (camelCase off, snake_case
by JsonSerializerOptions in `Stats.fs`).
**How to avoid:** Copy the exact spelling from `StatsWire` record in `Stats.fs` lines 51–53.

### Pitfall 3: The stats JSON example in §8 must also be updated
**What goes wrong:** Adding the field description table but forgetting to add the three fields
to the example JSON block (lines 570–580).
**Root cause:** The example JSON and the description table are two separate locations. Both must
show the new fields.
**How to avoid:** Plan 23-01 should explicitly list both locations as separate tasks.

### Pitfall 4: Tier 1 semantics in v2.1 vs v2.0
**What goes wrong:** Writing that Tier 1 requires "custom Hermes builds" or "clients that already
send the header."
**Root cause:** The v2.0 text (line 814) said "custom Hermes builds, direct curl clients,
downstream tooling get sticky escalation today." In v2.1, `--pass-session-id` is the standard
Hermes opt-in, not requiring any custom build.
**How to avoid:** The rewrite should describe Tier 1 as the output of Hermes's `--pass-session-id`
flag (when Hermes sends the session id as an HTTP header — verify whether current Hermes supports
this or whether the sysprompt is always the mechanism). See research note below.

### Pitfall 5: Tier 1 vs Tier 2 — Hermes's actual emission channel
**What goes wrong:** Misrepresenting which tier Hermes `--pass-session-id` fires.
**Root cause:** The source doc (`hermes-session-without-modification.md` §2.1) shows that
`--pass-session-id` emits `Session ID: <id>` in the **system prompt** (not as an HTTP header).
The X-Session-Id header (Tier 1) is used by "custom Hermes builds, direct curl, downstream
tooling" — it is NOT what stock `hermes --pass-session-id` sets.

**Conclusion:** In the current Hermes, `--pass-session-id` → Tier 2 (sysprompt), not Tier 1.
Tier 1 (X-Session-Id header) is for other clients that inject the header directly.

This distinction MUST be correct in §10:
- Tier 1 fires for: clients that inject `X-Session-Id` header (Graphify, curl, custom clients)
- Tier 2 fires for: Hermes with `--pass-session-id` (standard Hermes emit path)
- Tier 3 fires for: Hermes without `--pass-session-id`, any client not setting header or sysprompt

---

## Recommended Scope for Plan 23-01

Plan 23-01 is the single plan for Phase 23. Recommended tasks:

### Task 1: Verify §7 and §9.1 (read-only confirm, no edit needed)
- Read README.md lines 441–448 (Routing.Session) — confirm `FingerprintEnabled` absent, `TtlMinutes` and `MaxEntries` present.
- Read README.md lines 636–668 (§9.1 DecisionLog) — confirm `schema_version=1`, no `session_id` field added, `routing_reason` enum still accurate.
- Document verification result in SUMMARY.md. If §9.1 line 659 needs "Phase 17–22" update, do it here.

### Task 2: Rewrite README §10 Hermes Integration sub-sections
**What changes:**
1. Rename top-level sub-heading: `### Hermes Agent (v2.0 ...)` → `### Hermes Agent (v2.1 — three-tier session cascade)`
2. Rewrite the intro paragraph (lines 783–789): describe the three-tier cascade, not just v2.0 framing.
3. Keep `#### Hermes config (unchanged from v1.x)` block as-is (lines 791–800).
4. Rewrite `#### X-Session-Id header opt-in` → rename to `#### Session continuity — three-tier cascade` (or similar); describe all three tiers; document the four `--pass-session-id` opt-in options.
5. DELETE `#### Fingerprint fallback (opt-in; loopback single-client only)` (lines 822–849) entirely. This describes deleted code.
6. Keep `### Graphify` block as-is (lines 851–875).

**What to say in the new cascade description:**
- Tier 1: `X-Session-Id` HTTP header. Present when set by custom clients (curl, direct tooling).
- Tier 2: System-prompt parse — `Session ID: <id>` line emitted when Hermes runs with `--pass-session-id`. Operator opt-in (four options).
- Tier 3: Content fingerprint — SHA-256 of `(system + "|||" + firstUser)` first 16 hex chars. Zero-config fallback; always fires when Tiers 1 and 2 miss.
- All three tiers feed sticky escalation (§5.6). Tier order is fixed; no config to skip tiers.
- Monitor which tier is active via `/stats` `session_extraction_source_*` counters.

### Task 3: Add Phase 22 counter rows to README §8 /stats
**What changes:**
1. Add three new fields to the stats example JSON (after `selfrouter_skipped`).
2. Add a new "Phase 22 — session cascade counters" table after the Phase 19 block.
3. Add a `jq` snippet for monitoring tier distribution.

**Where to insert:** After line 620 (end of Phase 19 block / curl snippet), before `### GET /canary` (line 622).

### Task 4: Write SUMMARY.md and update STATE.md
Standard phase close-out.

---

## Open Questions

### Q1: Does §9.1 "Phase 17–19" wording need updating to "Phase 17–22"?
- **What we know:** Line 659 says "All v2.0 additions (Phase 17–19) are additive enum values." v2.1 adds no new schema fields.
- **What's unclear:** Whether cosmetic accuracy (updating to Phase 17–22) is desired or whether the current wording is acceptable since v2.1 truly adds nothing to the schema.
- **Recommendation:** Update to "All v2.0–v2.1 additions (Phase 17–22)" for completeness. Low-cost fix.

### Q2: Tier 1 vs Tier 2 — which does standard Hermes `--pass-session-id` use?
- **What we know:** From `hermes-session-without-modification.md` §2.1, `--pass-session-id` emits `Session ID: <id>` in the system prompt (Tier 2). The X-Session-Id header (Tier 1) is for custom clients.
- **Confirmed:** The `resolveSessionCascade` function checks header first (`req.SessionId` from CorrelationMiddleware), THEN sysprompt. Stock Hermes with `--pass-session-id` uses Tier 2. Tier 1 is for direct header injection.
- **Action:** §10 rewrite must clearly document this — `--pass-session-id` → Tier 2, not Tier 1.

### Q3: Should the smoke test reference in §10 be updated?
- **What we know:** Line 818–820 references `./scripts/smoke-hermes-session.sh`. This script was updated in Phase 22 commit `1842491` to reflect v2.1 cascade scope (header updated, functional curl logic unchanged).
- **Recommendation:** Keep the reference. The script path and description are still accurate.

---

## Sources

### Primary (HIGH confidence)
- `src/SmartRouter.Cli/Endpoints/Stats.fs` — exact `StatsWire` field names and `mapEndpoints` cascade stats resolution
- `src/SmartRouter.Cli/Adapters/SessionCascadeStats.fs` — `ISessionCascadeStats` interface doc comments
- `src/SmartRouter.Cli/Adapters/ContentFingerprint.fs` — Tier 3 fingerprint algorithm
- `src/SmartRouter.Cli/Adapters/HermesSessionExtract.fs` — Tier 2 regex and extraction logic
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` lines 183–207 — `resolveSessionCascade` tier order
- `README.md` lines 441–875 — current state of §7, §8, §9.1, §10

### Secondary (HIGH confidence)
- `.planning/phases/22-cascade-rewire-migration-and-observability/22-01-SUMMARY.md` — Phase 22 Plan 01 accomplishments and commit list
- `.planning/phases/22-cascade-rewire-migration-and-observability/22-02-SUMMARY.md` — Phase 22 Plan 02 deletion scope
- `.planning/phases/22-cascade-rewire-migration-and-observability/22-03-SUMMARY.md` — Phase 22 Plan 03 completion: DOC-02 done in commit `938c8ac`

### Tertiary (HIGH confidence — primary source doc)
- `~/projs/smart-router-distillation/idea/hermes-session-without-modification.md` — four Hermes opt-in options (§2.3, §2.7 options a1–a4); exact shell alias and env var syntax

---

## Metadata

**Confidence breakdown:**
- §10 rewrite scope: HIGH — current content fully read; stale lines identified by number; new content derived from shipped source code
- §8 new rows: HIGH — exact field names verified in `StatsWire` and `ISessionCascadeStats`; table format derived from existing Phase 19 block
- §7 status: HIGH — commit `938c8ac` confirmed to have removed `FingerprintEnabled` row; `TtlMinutes`/`MaxEntries` present at lines 447–448
- §9.1 status: HIGH — no schema changes in Phase 22; DecisionLog source code not modified; existing rows remain accurate

**Research date:** 2026-05-12
**Valid until:** Stable — this is documentation of already-shipped code; no expiry.

---

## RESEARCH COMPLETE

**Phase:** 23 — Documentation
**Confidence:** HIGH

### Key Findings

1. **§10 needs a substantive rewrite:** The "Fingerprint fallback" sub-section (lines 822–849) describes entirely deleted code and must be removed. PROXY-01 warning, `FingerprintEnabled`, `HMRS-FUTURE-01` references must all go. The section heading, intro paragraph, and X-Session-Id sub-section all need updating for v2.1.

2. **Three-tier cascade for §10:** Tier 1 = X-Session-Id header (custom clients); Tier 2 = sysprompt `Session ID:` line from Hermes `--pass-session-id` (standard Hermes opt-in); Tier 3 = SHA-256 content fingerprint (zero-config fallback). ALL three are always active.

3. **`--pass-session-id` → Tier 2, not Tier 1:** Stock Hermes emits the session id in the system prompt, not as an HTTP header. The README must clearly distinguish these.

4. **§8 needs three new rows:** Exact field names: `session_extraction_source_header`, `session_extraction_source_sysprompt`, `session_extraction_source_content`. Table format matches existing Phase 15/19 pattern. Stats JSON example also needs updating.

5. **§7 already done (DOC-02 complete):** Commit `938c8ac` removed `FingerprintEnabled` row. `TtlMinutes` and `MaxEntries` rows correct. No action needed.

6. **§9.1 correct as written:** No schema changes in v2.1. Optional cosmetic update: change "Phase 17–19" to "Phase 17–22".

### File Created

`/Users/ohama/projs/smart-router/.planning/phases/23-documentation/23-RESEARCH.md`

### Confidence Assessment

| Area | Level | Reason |
|------|-------|--------|
| §10 stale content identification | HIGH | Every stale line identified by number; confirmed by reading current README and Phase 22 summaries |
| §10 new content | HIGH | Derived from shipped source: `resolveSessionCascade`, `ISessionCascadeStats`, source doc §2.7 |
| §8 new rows | HIGH | Exact field names from `StatsWire` record; format from existing Phase 19 block |
| §7 status | HIGH | Confirmed by commit `938c8ac` in 22-03-SUMMARY.md |
| §9.1 status | HIGH | No DecisionLog source changes in Phase 22 |

### Open Questions

1. Whether §9.1 line 659 "Phase 17–19" cosmetic update is in scope (recommend yes — low cost)
2. Whether the smoke test reference (line 818–820) needs updating (recommend no — script updated in 22-03, reference still accurate)

### Ready for Planning

Research complete. Planner can now create `23-01-PLAN.md`.
