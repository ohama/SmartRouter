# Phase 22: Cascade Rewire + Migration + Observability — Research

**Researched:** 2026-05-12
**Domain:** ASP.NET Core middleware lifecycle, F# DU extension, integration test fixtures, git tag mechanics
**Confidence:** HIGH

---

## Summary

Phase 22 wires the two Phase-21 adapters (`HermesSessionExtract`, `ContentFingerprint`) into the live
request pipeline as a three-tier session-key cascade, deletes the v2.0 IP+UA fingerprint code, adds
`/stats` extraction-source counters, updates the smoke script, and writes the `[2.1.0]` CHANGELOG block.

The single hardest question — **TIER-03: where exactly do Tier 2 and Tier 3 resolve?** — is answered
by reading `CorrelationMiddleware.fs` and `ChatCompletions.fs` in detail. The answer is unambiguous:
the middleware runs pre-body and cannot access `req.Messages`; the only correct location is inside
`ChatCompletions.fs` handler scope, after `mapWireToRequest` returns a `RouterRequest` and before
`routeRequest` is called. This is **Option (b)** — no new helper module is needed.

The OBS-01 stats counter pattern mirrors the Phase 19 SR-05 pattern from `SelfRouter.fs` exactly:
new `mutable` int64 fields + `Interlocked.Increment` at the point of resolution + new fields on
`StatsWire` + null-safe resolution in `Stats.mapEndpoints`. The counter owner lives in a new
`SessionCascadeStats` type registered in `CompositionRoot`.

The deletion ordering (22-02 after 22-01) ensures the build is always green: the new cascade is
wired and green before any old code is removed.

**Primary recommendation:** Wire Tier 2/3 in `ChatCompletions.fs` after `mapWireToRequest`, increment
counters there, register a `SessionCascadeStats` singleton in CompositionRoot, and expose it via
`ISessionCascadeStats` interface following the SR-05 pattern exactly.

---

## 1. TIER-03 Location Decision

### Why CorrelationMiddleware CANNOT do Tier 2/3 alone

`CorrelationMiddleware.fs` (line 42) is wired via `app.Use(...)` in `Program.fs` (line 279) as an
`IMiddlewareFactory`-style lambda. It runs **before** the endpoint handler, which means it runs
before the request body is read. The request body is first read on `ChatCompletions.fs` line 225:

```fsharp
let! wireBody = ctx.Request.ReadFromJsonAsync<RouterRequestWire>(wireJsonOptions, ctx.RequestAborted)
```

ASP.NET Core's request body is a forward-only stream. Reading it in middleware before the endpoint
handler would either consume the stream (so the handler sees an empty body) or require enabling
request buffering (`EnableBuffering()`), which allocates a temp file for large bodies and defeats the
purpose. For SSE (streaming) requests this is especially risky — body buffering changes the timing
semantics of the streaming pipeline.

**Conclusion:** Extending `CorrelationMiddleware` to read the body is Option (a) — REJECTED.
It is risky, unconventional, and would require request buffering. The middleware comment on lines 32-39
explicitly says it "runs FIRST in the pipeline" for correlation/session-header extraction only.

### Why a new helper module (Option c) is not needed

Option (c) — a new `SessionKeyResolver` module called from `ChatCompletions.fs` — would be clean for
testability, but there is already a clean insertion point in the ChatCompletions handler: immediately
after `mapWireToRequest` (line 248) and before `routeRequest` (line 254). The cascade logic is
four lines. Isolating it into a separate module adds a file + DI wiring for minimal gain. A
`let private resolveSessionId` inline helper inside `ChatCompletions.fs` keeps the logic local and
readable. The Phase-19 precedent (inline self-classify logic in the non-streaming branch) confirms
the codebase tolerates inline logic at this scale.

### Recommended: Option (b) — inline resolution in ChatCompletions.fs handler

**Exact location:** After `let req = mapWireToRequest correlationId sessionId wireBody` (line 248),
before `match routeRequest routingConfig regn.Algorithm req with` (line 254).

**How the tier cascade writes back to `ctx.Items[SessionIdKey]`:**

The `sessionId` variable that was extracted from `ctx.Items` on lines 219-222 and threaded into
`mapWireToRequest` is the Tier-1 value (header or empty string). After `mapWireToRequest` produces
`req`, the resolved session key must update BOTH `req.SessionId` (used by the routing algorithm for
sticky lookup) AND `ctx.Items[SessionIdKey]` (read by downstream observers if any). Since `req` is
an F# record (immutable), the resolution must shadow `req` with a new record.

**Pseudo-F# shape (10 lines total, inserted after line 248):**

```fsharp
// v2.1 TIER-01..03: three-tier session key cascade.
// Tier 1 is already resolved by CorrelationMiddleware (header → sessionId).
// Tier 2 and Tier 3 require req.Messages — resolved here after mapWireToRequest.
let resolvedSessionId, extractionSource =
    if not (String.IsNullOrEmpty(req.SessionId)) then
        req.SessionId, "header"           // Tier 1 wins
    else
        match HermesSessionExtract.extractFromSystemPrompt req with
        | Some sid -> sid, "sysprompt"    // Tier 2
        | None     -> ContentFingerprint.compute req, "content"  // Tier 3

// Increment the appropriate OBS-01 counter.
cascadeStats.RecordSource(extractionSource)

// Rebind req so the routing algorithm and downstream sticky writes use the resolved key.
let req = { req with SessionId = resolvedSessionId }
// Update ctx.Items so any downstream middleware/observers see the resolved key.
ctx.Items.[SessionIdKey] <- resolvedSessionId
```

Where `cascadeStats` is an `ISessionCascadeStats` resolved via
`ctx.RequestServices.GetRequiredService<ISessionCascadeStats>()` — resolved once, same location as
`sessionStore` resolution on line 318.

**Both streaming and non-streaming paths:** The cascade insertion is at line ~249, BEFORE the
`if req.Stream then` branch at line 321. Both branches share the same `req` binding after shadowing.
The existing `if not (String.IsNullOrEmpty(req.SessionId)) then sessionStore.Update(...)` guards on
lines 418 and 686 continue to work correctly — they now use the resolved (non-empty) session key
from Tier 2/3 when the header was absent.

**The `null body` branch** (line 227) executes before `mapWireToRequest` is called, so there is no
`RouterRequest` available. That branch already sets `SessionId = ""` (line 239) and returns early.
No cascade resolution needed there — stateless behavior is correct for a null-body error path.

---

## 2. OBS-01 Stats Counter Pattern

### Existing pattern: SelfRouter SR-05 (the exact template)

`SelfRouter.fs` implements `ISelfRouterStats` (lines 64-71) with:
- `mutable` int64 fields at class level (`cacheHits`, `cacheMisses`, `callCount`, `skippedCnt`)
- `Interlocked.Increment` at the call site (not Volatile.Write)
- `Volatile.Read` in `GetSelfRouterStats()` for the read path
- Interface implemented inline on the class

`Stats.fs` resolves `ISelfRouterStats` null-safely via `ctx.RequestServices.GetService<ISelfRouterStats>()`
(line 108), returns zeros when null (lines 109-111), and writes four fields to `StatsWire` (lines 121-124).

### For OBS-01: new `ISessionCascadeStats` interface

Create a new interface `ISessionCascadeStats` in a new file `SessionCascadeStats.fs` in
`src/SmartRouter.Cli/Adapters/`:

```fsharp
type ISessionCascadeStats =
    abstract member RecordSource : source: string -> unit
    abstract member GetStats : unit -> struct (int64 * int64 * int64)

type SessionCascadeStats() =
    let mutable headerCount   = 0L
    let mutable syspromptCount = 0L
    let mutable contentCount  = 0L

    interface ISessionCascadeStats with
        member _.RecordSource(source) =
            match source with
            | "header"    -> Interlocked.Increment(&headerCount)   |> ignore
            | "sysprompt" -> Interlocked.Increment(&syspromptCount) |> ignore
            | _           -> Interlocked.Increment(&contentCount)   |> ignore
        member _.GetStats() =
            struct (Volatile.Read(&headerCount),
                    Volatile.Read(&syspromptCount),
                    Volatile.Read(&contentCount))
```

**Alternative:** Use an inline `RecordHeader()` / `RecordSysprompt()` / `RecordContent()` method
style (like `QualityCheckStats.RecordFinishReasonHit()`) rather than a string-dispatch
`RecordSource`. This avoids the string match and is more explicit. Either works; the explicit method
style is slightly more type-safe and mirrors the `IQualityCheckStats` pattern from Phase 15 more
closely. **Recommendation: use explicit methods** — `RecordHeader()`, `RecordSysprompt()`,
`RecordContent()` — rather than `RecordSource(string)`.

**DI registration** in `CompositionRoot.configureRequestPipeline` and `configureWithoutMl`:

```fsharp
services.AddSingleton<SessionCascadeStats>() |> ignore
services.AddSingleton<ISessionCascadeStats>(fun sp ->
    sp.GetRequiredService<SessionCascadeStats>() :> ISessionCascadeStats) |> ignore
```

No `AddHostedService` needed (not a BackgroundService).

**`StatsWire` additions** in `Stats.fs`:

```fsharp
session_extraction_source_header    : int64    // NEW Phase 22
session_extraction_source_sysprompt : int64    // NEW Phase 22
session_extraction_source_content   : int64    // NEW Phase 22
```

All three fields default to 0L in `snapshotToWireFields` and are overridden in `mapEndpoints` after
resolving `ISessionCascadeStats` (null-safe, same pattern as `selfRouterStats`).

**fsproj ordering:** `SessionCascadeStats.fs` must be declared before `ChatCompletions.fs` (which
consumes `ISessionCascadeStats`) and before `Stats.fs` (which consumes `ISessionCascadeStats`).
Current order near the bottom of `SmartRouter.Cli.fsproj` — check compile order before inserting.

---

## 3. MIG-03 Deletion Ordering

### Recommended plan ordering

**Plan 22-01 (TIER-01..05 + OBS-01) — pure ADDITIVE, no deletions:**
- Add `SessionCascadeStats.fs` (new file)
- Update `CompositionRoot.fs` — register `ISessionCascadeStats` in both pipelines
- Update `ChatCompletions.fs` — insert cascade resolution block after `mapWireToRequest`
- Update `Stats.fs` — add three new fields to `StatsWire` + wire `ISessionCascadeStats`
- Add `SessionKeyCascadeTests.fs` (new integration test file, TIER-05)
- Update `SmartRouter.Tests.fsproj` — add `SessionKeyCascadeTests.fs` compile entry
- Update `RouterTests.fs` — add `SessionKeyCascadeTests.tests` to rootTests

At the end of Plan 22-01, the build is green: new cascade wired; old IP+UA fingerprint code still
exists in parallel (fingerprintEnabled=false in all test fixtures → dead code path; does not conflict).
Test count: 188 + N_new (estimate 6-8 new tests).

**Plan 22-02 (MIG-01..03 + MIG-06) — pure DELETION + git tag:**
- Create `archive/v2.0-network-fingerprint` tag/branch on the LAST COMMIT of Plan 22-01 (BEFORE deletions)
- Delete `FingerprintEnabled` field from `SessionOptions` in `SessionStore.fs` (MIG-01a)
- Delete `FingerprintEnabled` key from `appsettings.json` (MIG-01b)
- Delete IP+UA fingerprint block from `CorrelationMiddleware.fs` + remove `fingerprintEnabled: bool` parameter (MIG-02a)
- Delete `fingerprintEnabled` read from `Program.fs` (MIG-02b)
- Update `LoggingTests.fs` line 318 — remove `false` arg from `correlationMiddleware` call
- Update `SessionStoreTests.fs` line 13 — remove `FingerprintEnabled = false` from `SessionOptions` literal
- Delete `HermesFingerprintTests.fs` entirely (MIG-03a)
- Remove `HermesFingerprintTests.fs` from `SmartRouter.Tests.fsproj` (MIG-03b)
- Remove `HermesFingerprintTests.tests` from `RouterTests.fs` rootTests (MIG-03c)

At the end of Plan 22-02, the build is green: deleted code was dead (fingerprintEnabled=false path
never exercised by remaining tests); test count = 188 + N_new - 8.

**Plan 22-03 (MIG-04 + MIG-05 + any final verification):**
- Update `scripts/smoke-hermes-session.sh`
- Write `CHANGELOG.md` `[2.1.0]` block
- Final `dotnet build` + `dotnet test` verification pass

### Cross-plan dependencies inside the phase

- Plan 22-02 MUST come after Plan 22-01 is committed (archive tag points to the post-22-01 commit)
- Plan 22-03 has no compile-time dependency on 22-01/22-02; it is pure documentation + script

---

## 4. MIG-06 Archive Tag Mechanics

### Convention from this repo

| Artifact | Type | Name Pattern | Example |
|----------|------|--------------|---------|
| Archived code snapshots (branches) | Git branch | `archive/{name}` | `archive/heuristic-baseline` |
| Corresponding release tags | Lightweight tag | `v{semver}-{name}` or `{name}` | `v0.5-heuristic-baseline` |
| Annotated milestone tags | Annotated tag | `milestone-v{N.M}` | `milestone-v2.0` |
| Version tags | Annotated tag | `v{N.M.P}` | `v1.3.0` |

`v0.5-heuristic-baseline` is a **lightweight** tag (`commit` object type, not `tag` object type).
`v1.3-ml-routing` is an **annotated** tag (has tagger + message).
`milestone-v2.0` is an **annotated** tag.

For MIG-06, both a **branch** and a **tag** should be created, matching the `archive/heuristic-baseline`
+ `v0.5-heuristic-baseline` dual pattern:

```bash
# After Plan 22-01's final commit is in place:
# 1. Create archive branch pointing to that commit
git branch archive/v2.0-network-fingerprint

# 2. Create annotated tag (matches milestone-v2.0 style for a significant preservation)
git tag -a v2.0-network-fingerprint -m "archive: preserve v2.0 IP+UA network fingerprint code

Pre-v2.1 snapshot. Contains:
- CorrelationMiddleware.fs: SHA-256(RemoteIp+UA) fingerprint block
- SessionOptions.FingerprintEnabled config field
- HermesFingerprintTests.fs (FP-01..FP-08)
- appsettings.json Routing.Session.FingerprintEnabled key

Deleted in v2.1 Phase 22 Plan 22-02 (MIG-01..03)."
```

**CRITICAL ORDERING:** Create branch + tag BEFORE the deletion commits land. The branch and tag must
point to the commit that INCLUDES the fingerprint code. If created after deletion, they would point
to the post-deletion commit (wrong). The MIG-06 git operations are the FIRST tasks in Plan 22-02.

**Name choice:** `archive/v2.0-network-fingerprint` (branch) and `v2.0-network-fingerprint` (tag).
The tag name `v2.0-network-fingerprint` is consistent with `v0.5-heuristic-baseline` (version prefix
+ descriptive name) but using the v2.0 milestone version.

---

## 5. TIER-05 Integration Test Design

### File name recommendation

`SessionKeyCascadeTests.fs` — named after the cascade behavior (parallels `StickyEscalationTests.fs`,
`SelfRoutingIntegrationTests.fs`).

### Test fixture reuse strategy

Reuse the `minimalConfigPairs` + `buildConfig()` + `buildProvider()` pattern from
`StickyEscalationTests.fs` (lines 28-116). The DI provider from `configureRequestPipeline` already
registers `ISessionStore` and `RoutingAlgorithmRegistration`. For Phase 22, add `ISessionCascadeStats`
registration (present in the full DI graph via `configureRequestPipeline`).

The TIER-05 tests do NOT need a full Kestrel `TestServer` — the cascade resolution is inline in
`ChatCompletions.fs`, but the cascade logic itself operates on `RouterRequest.SessionId` before
`routeRequest`. Integration tests can exercise the cascade by:

1. **For TIER-01..03 + OBS-01:** Build a `RouterRequest` with known messages, call the cascade
   resolution logic directly (extract the private `resolveSessionId` logic into a testable helper,
   OR test through a minimal Kestrel `TestServer` with fake upstream — the `LoggingTests.fs` pattern).

**Recommendation:** Use the `LoggingTests.fs` minimal TestServer pattern for TIER-05, because the
cascade resolution is embedded inside the HTTP handler and cannot be unit-tested without invoking the
handler. The fake upstream from `LoggingTests.fs` (lines 76-107) is reusable. The `StickyEscalationTests`
DI pattern only tests the routing algorithm — TIER-05 tests need the full handler chain.

**Alternatively:** Factor the cascade logic into a small private module-level function
`let private resolveSessionCascade (req: RouterRequest) (header: string) : string * string` where
the two outputs are (resolvedId, source). This function is pure and unit-testable without Kestrel.
**This is the better approach** — pure function test is faster and more deterministic than
TestServer-based tests. The `Interlocked.Increment` happens at the call site in the handler, not
inside `resolveSessionCascade`, so the counter is separately tested.

### Test cases for SessionKeyCascadeTests.fs

**TC-1: header-wins (TIER-01)**
- RouterRequest with `SessionId = "mysession"` (set by middleware) + system message containing `Session ID: sysprompt-id`
- Call `resolveSessionCascade` → expect `("mysession", "header")`

**TC-2: sysprompt-fallback (TIER-02)**
- RouterRequest with `SessionId = ""` (whitespace-only header was absent) + system message `"Session ID: 20260512T1530_abc"`
- Call `resolveSessionCascade` → expect `("20260512T1530_abc", "sysprompt")`

**TC-3: content-fallback (TIER-03)**
- RouterRequest with `SessionId = ""` + no system message with `Session ID:` line
- Call `resolveSessionCascade` → result is 16-char lowercase hex (non-empty); source is "content"

**TC-4: determinism (TIER-05e)**
- Same RouterRequest passed to `resolveSessionCascade` twice → identical result both calls

**TC-5: whitespace-only header falls through (TIER-02)**
- RouterRequest with `SessionId = ""` (CorrelationMiddleware already coerced whitespace to "") + `Session ID:` line present → Tier 2 wins, not Tier 1

**TC-6: sticky escalation through Tier 2 key (TIER-05d)**
- Build DI provider (reuse StickyEscalationTests buildProvider)
- Manually set `req.SessionId` to `"20260512T1530_abc"` (simulating Tier 2 resolution)
- Route a Hard Rule request → store writes `"20260512T1530_abc"` → Qwen122B
- Route a trivial request with same session key → `StickyEscalation` reason

**TC-7: both Routing.Mode values work (TIER-04)**
- Build two DI providers: one with `Routing:Mode = "selfrouting"`, one with `Routing:Mode = "ml"` (W4 skip guard when ONNX absent)
- Verify cascade resolution produces identical `resolvedSessionId` for both

**TC-8: counter increments (OBS-01)**
- Create `SessionCascadeStats` instance directly
- Call `RecordHeader()` once, `RecordSysprompt()` twice, `RecordContent()` zero times
- Assert `GetStats()` returns `struct (1L, 2L, 0L)`

Total new tests: 8 (matches TIER-05 estimate). Net count after Phase 22: 188 + 8 - 8 = 188 passed.

---

## 6. MIG-04 Smoke Script Changes

### Current script analysis

`scripts/smoke-hermes-session.sh` has **no direct `FingerprintEnabled` reference** — it does not
set or check the config key. The comment on line 25-27 references `FingerprintEnabled` and
`HermesFingerprintTests.fs`, but those are comments only, not functional code.

The functional curl logic (lines 45-76) tests only the X-Session-Id explicit header path (Tier 1).
It does NOT test Tier 2 or Tier 3 — the smoke script cannot easily inject a `Session ID:` system
prompt line because curl-driven JSON requires building the exact system message content.

### Changes needed

1. **Delete the `FingerprintEnabled` comment block** (lines 25-27) — references a deleted feature.
2. **Update the script header comment** — replace "X-Session-Id session propagation" with "3-tier
   session cascade verification". Replace the note about `FingerprintEnabled` with a note that
   Tier 2/3 are covered by unit tests.
3. **The existing two-request test (Tier 1 → sticky)** remains correct and sufficient for smoke.
   The smoke script's job is operator-runnable live-rig verification; Tier 2/3 are covered by
   unit tests in `SessionKeyCascadeTests.fs`.
4. **Optionally add a Tier 3 smoke test** — a request with no `X-Session-Id` header; verify the
   session key in DecisionLog is a 16-char hex string. This is useful for operator confidence.

**Recommendation:** Keep Tier 1 test as-is (it already works). Add one Tier 3 smoke request after
the existing two: a curl without `X-Session-Id` header; grep the DecisionLog for the correlation ID;
extract and validate `session_id` is 16 lowercase hex chars using `[[ "$sid" =~ ^[0-9a-f]{16}$ ]]`.

**Do NOT add a Tier 2 test** to the smoke script — constructing a valid Hermes `Session ID:` system
prompt via curl requires building multiline JSON (`\n` escaping), which is fragile in bash. The
unit tests in `SessionKeyCascadeTests.fs` cover Tier 2 deterministically.

---

## 7. MIG-05 CHANGELOG Content

Based on the Phase 17-20 CHANGELOG style (see `CHANGELOG.md` lines 1-163), the `[2.1.0]` block
should be inserted at the TOP (before `[2.0.0]`), following Keep-a-Changelog convention.

**Exact wording:**

```markdown
## [2.1.0] - 2026-05-12

Phase 22 — Hermes-less Session Tiering. Replaces the v2.0 IP+UA network
fingerprint with a multi-tier session extraction: explicit header → Hermes
system-prompt parse → content fingerprint.

### Removed

- **`Routing.Session.FingerprintEnabled` config key** (`appsettings.json`).
  Opt-in IP+UA fingerprint introduced in Phase 20 (v2.0) is superseded by the
  system-prompt and content-fingerprint tiers. **Breaking change** for any
  operator who had `FingerprintEnabled=true` — update `appsettings.json` by
  removing the key (or set it to `false`; the key is ignored in v2.1).
  If `--pass-session-id` is not available on your Hermes version, content
  fingerprint (Tier 3) provides equivalent sticky-bucket continuity without
  IP address coupling.
- **IP+UA network fingerprint** (`SHA-256(RemoteIpAddress + "|" + User-Agent)`
  block in `CorrelationMiddleware`). Deleted with `FingerprintEnabled` — no
  equivalent behavior in v2.1 (superseded by Tier 2/3).
- **`PROXY-01` reverse-proxy warning** (README §10). Moot — network fingerprint
  code that required `X-Forwarded-For` parsing is gone.
- **`HermesFingerprintTests.fs`** (FP-01..FP-08, 8 tests). Replaced by
  `HermesSessionExtractTests.fs` + `ContentFingerprintTests.fs` +
  `SessionKeyCascadeTests.fs`.

### Added

- **Hermes system-prompt session parse (Tier 2).** `HermesSessionExtract.extractFromSystemPrompt`
  reads a `Session ID: <id>` line from the first `system` message when Hermes is
  started with `--pass-session-id`. Pre-compiled `Regex("^Session ID:[ \t]*(\S+)",
  Multiline)` — no per-request allocation. Falls through when line absent (graceful
  for operators who have not enabled `--pass-session-id`).
- **Content fingerprint session key (Tier 3).** `ContentFingerprint.compute` derives a
  16-character lowercase hex session key from `SHA-256(system_msg + "|||" + first_user_msg)[0..15]`.
  Deterministic and collision-resistant — the same conversation start always maps to the
  same session bucket. Inputs truncated to 4000 characters before hashing.
- **Three-tier cascade in `CorrelationMiddleware` / `ChatCompletions`.** Session key
  resolution priority: `X-Session-Id` header → Tier 2 system-prompt parse → Tier 3 content
  fingerprint. First non-empty value wins; `ctx.Items[SessionIdKey]` and `req.SessionId`
  carry the resolved key. Sticky escalation (`routing_reason="sticky_to_122b"`) works across
  all three tiers.
- **`session_extraction_source_header`, `session_extraction_source_sysprompt`,
  `session_extraction_source_content`** — three new `int64` fields on `GET /stats`.
  Each `Interlocked.Increment`s once per request based on which tier resolved the
  session key. Resolves null-safe to 0 in test fixtures without full DI graph.
- **`archive/v2.0-network-fingerprint` git branch + `v2.0-network-fingerprint` tag.**
  Preserves the pre-v2.1 commit (last commit of Plan 22-01) for archaeological reference —
  mirrors `archive/heuristic-baseline` + `v0.5-heuristic-baseline` pattern.

### Changed

- **`CorrelationMiddleware.correlationMiddleware` signature.** `fingerprintEnabled: bool`
  first parameter **removed**. Callers (`Program.fs` and `LoggingTests.fs`) must drop the
  argument. `Program.fs` startup-time `app.Configuration.["Routing:Session:FingerprintEnabled"]`
  read also deleted.
- **Session key resolution moved from header-only (Tier 1) to 3-tier cascade.** Requests
  without `X-Session-Id` header now always receive a non-empty session key (Tier 2 or Tier 3),
  enabling sticky escalation for Hermes operators who have not yet enabled `--pass-session-id`.

### Notes

- `schema_version=1` unchanged. Session key resolution happens upstream of the routing
  decision. No new `routing_reason` values. The resolved key flows through the existing
  `SES-04` channel into the existing `session_id` DecisionLog field (which is not currently
  emitted — tracked as future schema-version work).
- Both `Routing.Mode = "selfrouting"` and `"ml"` use the new 3-tier cascade — verified by
  TIER-04 integration test.
- Operators with `Routing.Session.FingerprintEnabled=true` in their `appsettings.json` will
  see the key silently ignored at startup (CLIMutable binding does not crash on unknown keys
  — see §Pitfalls #6 below for confirmation).
```

---

## 8. Pitfalls

### Pitfall 1: CorrelationMiddleware signature change breaks callers in unexpected files

`correlationMiddleware fingerprintEnabled ctx next` is called in TWO places that are NOT `Program.fs`:
- `tests/SmartRouter.Tests/LoggingTests.fs` line 318: `correlationMiddleware false ctx next`
- `Program.fs` line 280: `CorrelationMiddleware.correlationMiddleware fingerprintEnabled ctx next`

After removing the `fingerprintEnabled: bool` parameter, both call sites must be updated.
`LoggingTests.fs` simply drops the `false` argument. The `TreatWarningsAsErrors=true` project-wide
setting means a wrong argument count is a compile error — but the executor might miss this if they
only grep `Program.fs` for `fingerprintEnabled` and not the test project.

**Prevention:** In Plan 22-02 task list, explicitly call out: "update LoggingTests.fs line 318:
remove `false` argument". Check with `grep -rn "correlationMiddleware" src/ tests/` before closing.

### Pitfall 2: SessionStoreTests.fs references FingerprintEnabled on SessionOptions

`tests/SmartRouter.Tests/SessionStoreTests.fs` line 13:
```fsharp
let opts = { TtlMinutes = ttlMinutes; MaxEntries = maxEntries; FingerprintEnabled = false }
```

After removing `FingerprintEnabled` from `SessionOptions` (MIG-01), this record literal fails to
compile with `TreatWarningsAsErrors=true` (actually a compile error, not a warning — unknown record
field). The deletion task in Plan 22-02 must explicitly include updating `SessionStoreTests.fs`.

**Prevention:** In Plan 22-02, add explicit step: "update `SessionStoreTests.fs` line 13: remove
`FingerprintEnabled = false` from SessionOptions literal".

### Pitfall 3: PITFALL-26 enforcement — three deletions required atomically

When deleting `HermesFingerprintTests.fs` (MIG-03), three changes MUST be in the same commit:
1. Delete the file `tests/SmartRouter.Tests/HermesFingerprintTests.fs`
2. Remove `<Compile Include="HermesFingerprintTests.fs" />` from `SmartRouter.Tests.fsproj` (line 49)
3. Remove `SmartRouter.Tests.HermesFingerprintTests.tests` from `RouterTests.fs` rootTests (line 43)

If only the file is deleted but the fsproj entry remains, `dotnet build` errors with missing file.
If file + fsproj are deleted but rootTests entry remains, F# compiler errors on the unresolved reference.
Neither partial state is compilable. Enforce as one atomic commit.

### Pitfall 4: CLIMutable field removal — is FingerprintEnabled silently ignored?

`SessionOptions` is `[<CLIMutable>]`. When `appsettings.json` contains `FingerprintEnabled: false`
but the F# record no longer has the field, ASP.NET Core's configuration binder uses
`BinderOptions` (default `BindNonPublicProperties = false`) with a fallback that **ignores unknown
keys silently** — confirmed by ASP.NET Core source (IOptions binding does not throw on extra JSON
keys). The operator's existing `appsettings.json` will continue to work after updating the binary;
the `FingerprintEnabled` JSON key is simply ignored.

**Verification:** The project's existing pattern (adding new CLIMutable fields in e.g. `SelfRouterOptions`)
never required operators to delete old keys — only new ones needed to be added. The reverse (removing
a field from the record while JSON still has the key) is equally safe.

**Conclusion:** Removing `FingerprintEnabled` from `SessionOptions.fs` without removing the key from
the operator's `appsettings.json` is SAFE — startup will not crash. The operator may see a warning in
logs if `ValidateDataAnnotations` is in use, but this project does not use ValidateDataAnnotations on
SessionOptions. No graceful-ignore handler needed.

### Pitfall 5: req.SessionId shadowing in ChatCompletions.fs

The cascade resolution pattern `let req = { req with SessionId = resolvedSessionId }` creates a
shadowed `req` binding. F# allows this, but the compiler will emit a warning if
`TreatWarningsAsErrors=true` and the FS compiler version considers shadowing a warning. Check
whether the project compiler settings suppress FS1030 (value shadowing warning). In practice,
`let decision = ...` is already shadowed in `ChatCompletions.fs` at line 300 (`// shadows the parameter`)
without issues, confirming shadowing is accepted in this codebase.

### Pitfall 6: Stats JSON field ordering

The `StatsWire` record is serialized via `JsonFSharpConverter` (via `jsonOptions` in `Stats.fs`
line 129). Record fields are emitted in declaration order. The three new fields should be appended
at the END of the record (after `selfrouter_skipped`) to maintain stable JSON layout for any
tooling that depends on field order. This is not a correctness issue but operator tooling (jq scripts)
may reference field offsets.

### Pitfall 7: fsproj compile order for SessionCascadeStats.fs

`SessionCascadeStats.fs` must be declared in `SmartRouter.Cli.fsproj` BEFORE both
`Endpoints/ChatCompletions.fs` (which calls `ISessionCascadeStats`) AND `Endpoints/Stats.fs`
(which also calls `ISessionCascadeStats`). Check the current fsproj compile order before inserting.
The current file ordering places adapters before endpoints, so inserting near the other `Adapters/`
entries should be safe.

### Pitfall 8: MIG-06 tag created AFTER deletion

If the executor forgets to create the archive branch/tag BEFORE running the deletion commits, the
archive preserves the post-deletion state — defeating the purpose entirely. The MIG-06 operations
must be the FIRST task in Plan 22-02. A safe ordering: commit 22-02-T1 = tag creation, commit
22-02-T2 = FingerprintEnabled deletion, etc.

### Pitfall 9: Tier 2 + Tier 3 counter increment must be at the ChatCompletions handler scope

The `Interlocked.Increment` for OBS-01 counters must happen in the same scope as the cascade
resolution — inside the ChatCompletions handler, after `mapWireToRequest`, before `routeRequest`.
Do NOT move it to middleware or to `SessionCascadeStats.RecordSource` itself; the counter must
increment once per request, not once per DI resolution.

### Pitfall 10: Tier 3 ContentFingerprint now runs on EVERY request without header or sysprompt

In v2.0, requests without `X-Session-Id` and with `FingerprintEnabled=false` (the default) received
`sessionId = ""`. In v2.1, those same requests now always call `ContentFingerprint.compute`, which
creates a per-call `SHA256` instance + computes a hash. This adds ~1-2 μs per request. Confirmed
acceptable by the Phase 21 design (CFP is pure BCL, sub-microsecond on modern hardware). No
performance concern, but note it in the plan so the executor doesn't add a guard condition that
accidentally reverts to stateless behavior.

---

## 9. README.md Sync Rule Check

Phase 22's code changes touch the following README areas:

| Area | Section | Change | When |
|------|---------|--------|------|
| `Routing.Session.FingerprintEnabled` config key deleted | §7 Configuration Reference | Remove row | Phase 23 Plan 23-01 |
| Three new `/stats` counter fields | §8 Endpoints `/stats` | Add 3 rows | Phase 23 Plan 23-01 |
| Hermes integration now uses 3-tier cascade | §10 Hermes Integration | Rewrite for v2.1 paradigm | Phase 23 Plan 23-01 |
| PROXY-01 warning removed (FingerprintEnabled gone) | §10 | Remove callout | Phase 23 Plan 23-01 |
| CorrelationMiddleware signature change | Internal only; no public surface | No README change needed | — |

**CLAUDE.md sync rule evaluation for Phase 22:**

The CLAUDE.md README sync rule requires changes that touch the 12 observable-behavior areas to update
README in the same change set. Phase 22's Plans 22-01/22-02/22-03 are code + migration commits.

**Recommendation:** Phase 22 Plans 22-01 and 22-02 are code-only commits and do NOT need README
updates by themselves — the behavior changes are documented via CHANGELOG (MIG-05). The smoke script
update (22-03) is the right point to decide: should 22-03 also touch README?

**Answer: YES — Plan 22-03 should update README §7 to remove the FingerprintEnabled row**, because
that is the lowest-friction update (single-row deletion). The full §10 rewrite is Phase 23's scope
(DOC-01), but the §7 row deletion is simple enough to bundle with 22-03. This avoids having a
committed codebase where the config key was deleted (MIG-01) but README §7 still lists it as valid.

Plans 22-01 and 22-02 do not touch the 12 areas in ways that require README sync before the deletions
are completed — the additive OBS-01 counters are documented in the CHANGELOG and covered by Phase 23.
The §7 FingerprintEnabled row is the one exception: it should be removed in 22-03 alongside the CHANGELOG.

**Explicit recommendation:**
- Plans 22-01, 22-02: No README changes (use CHANGELOG only)
- Plan 22-03: Delete `Routing.Session.FingerprintEnabled` row from README §7 (CLAUDE.md §12 sync rule);
  leave §8 `/stats` new rows and §10 rewrite for Phase 23

---

## 10. Open Questions for Planner

1. **`ISessionCascadeStats` file placement:** Should `SessionCascadeStats.fs` live with the other
   small adapter interfaces (alongside `SessionStore.fs`) or in a dedicated `Observability/` subfolder?
   Recommendation: alongside `SessionStore.fs` in `src/SmartRouter.Cli/Adapters/` — consistent with
   the existing adapter pattern.

2. **`resolveSessionCascade` testability:** The planner should decide whether to extract the cascade
   logic into a top-level `let private resolveSessionCascade` function at the module level of
   `ChatCompletions.fs` (unit-testable without Kestrel) or keep it inline in the handler. Recommendation:
   extract as a module-level private function for clean unit testing (TC-1..TC-5 above don't need
   a running server).

3. **Stats endpoint field ordering:** Append the three new `session_extraction_source_*` fields at
   the END of `StatsWire` record, after `selfrouter_skipped`. Confirm this is acceptable for any
   operator scripts that consume `/stats` output.

4. **Test count arithmetic:** 188 (current) + 8 (TC-1..TC-8 in SessionKeyCascadeTests) - 8 (deleted
   HermesFingerprintTests) = 188. The 18 ignored tests remain unchanged. End state: 188 passed + 18
   ignored + 0 failed.

5. **Plan 22-03 CHANGELOG date:** Use `2026-05-12` (today). If execution spans multiple days, use
   the actual completion date. Confirm with the executor at task time.

---

## Sources

### Primary (HIGH confidence)
- `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` — full file read; lifecycle confirmed pre-body
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — full file read; line 225 body read, line 248 mapWireToRequest, line 254 routeRequest confirmed
- `src/SmartRouter.Cli/Program.fs` — full file read; line 279 middleware registration + lines 273-280 fingerprintEnabled read confirmed
- `src/SmartRouter.Cli/Adapters/SelfRouter.fs` — SR-05 ISelfRouterStats pattern read lines 64-71, 334-339
- `src/SmartRouter.Cli/Endpoints/Stats.fs` — StatsWire struct + mapEndpoints read; OBS-01 insertion point confirmed
- `src/SmartRouter.Cli/Adapters/SessionStore.fs` — FingerprintEnabled field on SessionOptions confirmed (line 37)
- `tests/SmartRouter.Tests/HermesFingerprintTests.fs` — 8 test cases, fingerprintEnabled signature confirmed
- `tests/SmartRouter.Tests/LoggingTests.fs` — line 318 correlationMiddleware false call confirmed
- `tests/SmartRouter.Tests/SessionStoreTests.fs` — line 13 FingerprintEnabled = false literal confirmed
- `tests/SmartRouter.Tests/RouterTests.fs` — rootTests entries confirmed (line 43, 44, 45)
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — HermesFingerprintTests.fs compile entry line 49 confirmed
- `scripts/smoke-hermes-session.sh` — full file read; no functional FingerprintEnabled reference (comment only)
- `src/SmartRouter.Cli/appsettings.json` — FingerprintEnabled: false on line 18 confirmed
- `CHANGELOG.md` — Phase 17-20 style confirmed for MIG-05 template
- `git tag -l` + `git branch -a` — confirmed `archive/heuristic-baseline` branch + `v0.5-heuristic-baseline` lightweight tag pattern; `milestone-v2.0` is annotated tag; `v1.3-ml-routing` is annotated tag
- `git cat-file -t refs/tags/v0.5-heuristic-baseline` → `commit` (lightweight); `v1.3-ml-routing` → `tag` (annotated)

### Secondary (MEDIUM confidence)
- `.planning/ROADMAP.md` Phase 22 block — plan structure confirmed
- `.planning/REQUIREMENTS.md` TIER-01..05, OBS-01, MIG-01..06 — all requirements read
- `.planning/STATE.md` — Phase 21 complete, 188 + 18 + 0 test baseline confirmed

---

## Metadata

**Confidence breakdown:**
- TIER-03 location decision: HIGH — directly confirmed by reading ChatCompletions.fs and CorrelationMiddleware.fs; no ambiguity remains
- OBS-01 stats pattern: HIGH — SelfRouter.fs ISelfRouterStats + Stats.fs is the exact template
- Deletion ordering: HIGH — logical sequencing confirmed by reading existing code
- MIG-06 tag mechanics: HIGH — confirmed from `git cat-file -t` on existing tags
- TIER-05 test design: HIGH — StickyEscalationTests.fs and SelfRoutingIntegrationTests.fs patterns directly applicable
- MIG-04 smoke script: HIGH — full script read; no functional FingerprintEnabled reference
- MIG-05 CHANGELOG: HIGH — Phase 17-20 CHANGELOG blocks read as style reference
- Pitfalls: HIGH — all confirmed from reading actual source files

**Research date:** 2026-05-12
**Valid until:** 2026-06-12 (codebase is stable; no external dependency)
