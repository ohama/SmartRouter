# Phase 20: Hermes Agent Integration + Documentation — Research

**Researched:** 2026-05-12
**Domain:** ASP.NET Core middleware, SHA-256 fingerprinting, smoke testing, documentation
**Confidence:** HIGH — all findings grounded in actual codebase reads; no speculation

---

## Summary

Phase 20 is the closing phase of the v2.0 milestone. The bulk of the session machinery (X-Session-Id header reading, SessionStore, sticky escalation, CorrelationMiddleware) shipped in Phases 18 and 19 and is fully operational. Phase 20 adds three things:

1. **Fingerprint fallback** (`Routing.Session.FingerprintEnabled=true`): when no `X-Session-Id` header is present, CorrelationMiddleware derives a session key from `SHA-256(RemoteIpAddress + "|" + User-Agent)[0..15]` (16 hex chars). This is purely additive in `CorrelationMiddleware.fs` — no changes needed downstream in `ChatCompletions.fs` or `SessionStore.fs`, because those already consume the `SessionId` string from `ctx.Items[SessionIdKey]`.

2. **Smoke test + integration tests**: `scripts/smoke-hermes-session.sh` (curl loop, no Hermes Agent dependency) and `tests/SmartRouter.Tests/HermesFingerprintTests.fs` (DI-integration, mirrors `StickyEscalationTests.fs` pattern exactly).

3. **Documentation close-out**: README §10 full rewrite for v2.0, README §7 new config row, CHANGELOG `[Unreleased]` Phase 20 entries closing out v2.0.0.

**Primary recommendation:** Implement fingerprint resolution inside `CorrelationMiddleware.correlationMiddleware`, immediately after the existing X-Session-Id header read block. This is the right location because middleware runs first in the pipeline, before any endpoint code, and the downstream code (ChatCompletions, SessionStore) is already correct for any non-empty SessionId string.

---

## Standard Stack

No new NuGet packages are required. All APIs used are BCL + existing ASP.NET Core.

### APIs in use

| API | Location | Purpose |
|-----|----------|---------|
| `System.Security.Cryptography.SHA256.Create()` | BCL | Fingerprint hash — same pattern as `DecisionLogger.computePromptHash` |
| `HttpContext.Connection.RemoteIpAddress` | ASP.NET Core | Gets client IP as `System.Net.IPAddress?` (nullable) |
| `ctx.Request.Headers.TryGetValue("User-Agent", ...)` | ASP.NET Core | Reads UA header — same `StringValues` pattern as existing `X-Session-Id` read |
| `System.Text.Encoding.UTF8.GetBytes(...)` | BCL | Input to SHA-256 |
| `Array.map (sprintf "%02x") \|> String.concat ""` | F# BCL | Lowercase hex conversion — canonical pattern from `DecisionLogger.fs:16` |
| `config.GetSection("Routing:Session").Get<SessionOptions>()` | Microsoft.Extensions.Configuration | SessionOptions binding — `FingerprintEnabled` adds one field |

**No new NuGet packages. No changes to `.fsproj` project references.**

---

## Architecture Patterns

### Q1: Where does fingerprint resolution slot in?

**Answer: inside `CorrelationMiddleware.correlationMiddleware`, immediately after the existing X-Session-Id read block.**

Current flow (CorrelationMiddleware.fs:39-62):
1. Generate `cid = Guid.NewGuid().ToString("N")` — line 41
2. Store `cid` in `ctx.Items[CorrelationIdKey]` — line 42
3. Read `X-Session-Id` header → `sessionId` (empty string if absent/whitespace) — lines 49-52
4. Store `sessionId` in `ctx.Items[SessionIdKey]` — line 53
5. Push Serilog property, register response header callback, call next — lines 55-61

Phase 20 inserts between steps 3-4 and step 4. After the header read produces an empty string, if `FingerprintEnabled=true`, compute the fingerprint and use it instead of empty string. The `ctx.Items[SessionIdKey]` write (step 4) then stores either the header value, the fingerprint, or still empty string — downstream code (`ChatCompletions.fs:219-222`) already handles all three correctly via its `String.IsNullOrEmpty` guard.

**This requires no changes to:**
- `ChatCompletions.fs` (reads `ctx.Items[SessionIdKey]` unchanged)
- `SessionStore.fs` (Update/TryGet are unchanged)
- `CompositionRoot.fs` (SessionOptions binding just gains one field)
- `Domain.fs` (RouterRequest.SessionId field unchanged)

### Q2: How is `RemoteIpAddress` accessed?

```fsharp
// ctx.Connection.RemoteIpAddress is System.Net.IPAddress? (nullable reference type)
let ip =
    if isNull ctx.Connection.RemoteIpAddress
    then "unknown"
    else ctx.Connection.RemoteIpAddress.ToString()
// Loopback addresses:
//   IPv4 loopback: "127.0.0.1"
//   IPv6 loopback: "::1"
// Both are valid — the fingerprint treats them as distinct strings,
// which is correct (they represent different network paths in theory,
// but in practice the operator's single-client scenario is loopback-only).
```

Edge cases:
- Null: occurs when Kestrel cannot determine the remote address (rare in practice; use sentinel "unknown")
- IPv6 loopback `::1`: included as-is; ToString() returns "::1"
- IPv4-mapped IPv6 (e.g., `::ffff:127.0.0.1`): ToString() returns "::ffff:127.0.0.1"; acceptable for fingerprint purposes

### Q3: How is `User-Agent` header accessed?

Use the same `TryGetValue` + StringValues pattern as the existing X-Session-Id read (CorrelationMiddleware.fs:50-52):

```fsharp
let ua =
    match ctx.Request.Headers.TryGetValue("User-Agent") with
    | true, sv when sv.Count > 0 && not (String.IsNullOrWhiteSpace(sv.[0])) -> sv.[0]
    | _ -> "unknown"
```

F# note: `StringValues` has an indexed accessor `sv.[0]` (not `sv[0]` without the dot in older F# — the dot form is explicit and safe). Missing or whitespace-only UA → sentinel "unknown".

### Q4: SHA-256 prefix derivation

**Exact pattern** (mirrors `DecisionLogger.computePromptHash`, line 13-16):

```fsharp
// In: CorrelationMiddleware.fs, after resolving ip + ua sentinels
let input = sprintf "%s|%s" ip ua
use sha = System.Security.Cryptography.SHA256.Create()
let bytes = System.Text.Encoding.UTF8.GetBytes(input)
let hash  = sha.ComputeHash(bytes)
let hex   = hash |> Array.map (sprintf "%02x") |> String.concat ""
let fingerprint = hex.Substring(0, 16)  // 16 hex chars = 8 bytes
```

Key facts:
- **Lowercase hex**: `sprintf "%02x"` produces lowercase. This matches `computePromptHash` (DecisionLogger.fs:16) and `computeContentHash` (ChatCompletions.fs:50-52 uses `b.ToString("x2")`). README §10 must document "16-character lowercase hex prefix".
- **`Convert.ToHexString`** (returns uppercase) — do NOT use; breaks consistency with existing codebase lowercase convention.
- **16 hex chars** = 8 bytes of SHA-256 output. `hex.Substring(0, 16)` is correct. The REQUIREMENTS.md spec says "[0..15]" meaning the first 16 characters (indices 0–15 inclusive), which is `Substring(0, 16)`.
- **Thread safety**: `SHA256.Create()` returns a new instance; `use` disposes it correctly. Do NOT share a single SHA256 instance across requests (Pitfall P8 from DecisionLogger.fs:10).

### Q5: Config wiring for `Routing.Session.FingerprintEnabled`

Current `SessionOptions` record (SessionStore.fs:34-37):
```fsharp
[<CLIMutable>]
type SessionOptions = {
    mutable TtlMinutes : int
    mutable MaxEntries : int
}
```

Add `FingerprintEnabled`:
```fsharp
[<CLIMutable>]
type SessionOptions = {
    mutable TtlMinutes         : int
    mutable MaxEntries         : int
    mutable FingerprintEnabled : bool  // default false — CLIMutable bool defaults to false when JSON key absent
}
```

The existing binding in `CompositionRoot.fs:429`:
```fsharp
services.Configure<SessionOptions>(config.GetSection("Routing:Session")) |> ignore
```
This already binds the full `Routing:Session` section. Adding `FingerprintEnabled` to `SessionOptions` is sufficient — no changes needed in `CompositionRoot.fs` binding code.

`CorrelationMiddleware` cannot resolve DI-registered services directly (it's a lambda, not a class). **Resolution approach**: read the `FingerprintEnabled` value at middleware registration time via direct config read, close over a `bool` in the middleware lambda. Pattern:

```fsharp
// In Program.fs / wherever middleware is registered:
let fingerprintEnabled =
    let raw = config.["Routing:Session:FingerprintEnabled"]
    not (isNull raw) && raw.Trim().ToLowerInvariant() = "true"
// OR: config.GetSection("Routing:Session").Get<SessionOptions>() at startup

// Then pass to middleware factory:
app.Use(fun ctx (next: RequestDelegate) ->
    CorrelationMiddleware.correlationMiddleware fingerprintEnabled ctx next)
```

**Alternatively**, `correlationMiddleware` gains a `fingerprintEnabled: bool` parameter and the caller (Program.fs) passes it. This is the cleanest approach — no DI service resolution inside middleware, no IOptions<T> threading.

Read `Program.fs` to confirm current middleware registration shape before coding:

```
app.Use(fun (ctx: HttpContext) (next: RequestDelegate) -> correlationMiddleware ctx next)
```

If this is the current shape, change to:
```
app.Use(fun (ctx: HttpContext) (next: RequestDelegate) -> correlationMiddleware fingerprintEnabled ctx next)
```

**appsettings.json addition**:
```json
"Routing": {
  "Session": {
    "TtlMinutes": 30,
    "MaxEntries": 10000,
    "FingerprintEnabled": false
  }
```

### Q6: Integration test pattern (`HermesFingerprintTests.fs`)

The precedent is `StickyEscalationTests.fs`. Key structural facts:

1. **Location**: `tests/SmartRouter.Tests/HermesFingerprintTests.fs`
2. **Pattern**: `testSequenced <| testList "HermesFingerprintTests" [...]`
3. **DI setup**: `buildProvider()` from `StickyEscalationTests.fs` — copies `minimalConfigPairs` and adds `Routing:Session:FingerprintEnabled` key
4. **Registration in `RouterTests.fs`**: add `SmartRouter.Tests.HermesFingerprintTests.tests` to `rootTests` list and `SmartRouter.Tests.fsproj` `<Compile>` list (before `RouterTests.fs`)

The fingerprint tests operate at the `CorrelationMiddleware` function level, NOT at the HTTP/Kestrel level. This avoids full WebApplicationFactory complexity. The tests call `correlationMiddleware` directly with a fake `HttpContext`.

However, there is a challenge: `correlationMiddleware` operates on `HttpContext`, which requires `Microsoft.AspNetCore.Http`. The existing test suite (StickyEscalationTests) tests routing logic via `routeRequest` directly without an HTTP context. For fingerprint tests, we need to verify that `CorrelationMiddleware` correctly populates `ctx.Items[SessionIdKey]` with the fingerprint value.

**Two viable approaches:**

**Approach A (preferred — minimal)**: Use `DefaultHttpContext` from `Microsoft.AspNetCore.Http` (already transitively available via test project's reference to `SmartRouter.Cli`):

```fsharp
open Microsoft.AspNetCore.Http
open System.Net

let private makeHttpContext (remoteIp: string) (ua: string) (sessionHeader: string option) : DefaultHttpContext =
    let ctx = DefaultHttpContext()
    ctx.Connection.RemoteIpAddress <- if remoteIp = "" then null else IPAddress.Parse(remoteIp)
    match ua with
    | "" -> ()
    | s  -> ctx.Request.Headers["User-Agent"] <- Microsoft.Extensions.Primitives.StringValues(s)
    match sessionHeader with
    | None    -> ()
    | Some hv -> ctx.Request.Headers["X-Session-Id"] <- Microsoft.Extensions.Primitives.StringValues(hv)
    ctx
```

Then call `correlationMiddleware fingerprintEnabled ctx (fun _ -> Task.CompletedTask)` and assert `ctx.Items["SessionId"]`.

**Approach B**: Test via DI + routeRequest, injecting known session IDs. Less suitable here because fingerprint generation is a middleware concern, not a routing concern.

**Approach A is correct.** The test directly exercises the middleware function that will change.

### Concrete test cases for `HermesFingerprintTests.fs`

```
FP-1: fingerprint disabled (default) + no header → sessionId = ""
      ctx: FingerprintEnabled=false, no X-Session-Id, remote=127.0.0.1, ua="python/3.11"
      assert: ctx.Items["SessionId"] = ""

FP-2: fingerprint disabled + header present → sessionId = header value
      ctx: FingerprintEnabled=false, X-Session-Id="sess-abc", remote=127.0.0.1
      assert: ctx.Items["SessionId"] = "sess-abc"

FP-3: fingerprint enabled + no header → sessionId = fingerprint (non-empty, 16 chars)
      ctx: FingerprintEnabled=true, no X-Session-Id, remote="127.0.0.1", ua="hermes/2.0"
      assert: ctx.Items["SessionId"] is string, length = 16, all [0-9a-f]

FP-4: fingerprint enabled + header present → header wins (not fingerprint)
      ctx: FingerprintEnabled=true, X-Session-Id="explicit-id", remote="127.0.0.1"
      assert: ctx.Items["SessionId"] = "explicit-id"

FP-5: fingerprint enabled + same IP+UA → deterministic (two calls → same fingerprint)
      call middleware twice with same ctx params, compare SessionId values
      assert: both SessionId values equal

FP-6: fingerprint enabled + different UA → different fingerprint
      ctx-a: remote=127.0.0.1, ua="hermes/2.0"  → fingerprint-a
      ctx-b: remote=127.0.0.1, ua="curl/7.88"   → fingerprint-b
      assert: fingerprint-a ≠ fingerprint-b

FP-7: fingerprint enabled + whitespace-only header → falls back to fingerprint (not empty)
      ctx: FingerprintEnabled=true, X-Session-Id="   ", remote="127.0.0.1", ua="hermes/2.0"
      assert: ctx.Items["SessionId"] is non-empty fingerprint (16 chars)

FP-8: sticky integration — fingerprint-derived sessionId participates in sticky escalation
      Build DI (selfrouting config + FingerprintEnabled=true).
      fingerprint = computeFingerprint("127.0.0.1", "hermes/2.0")
      store.Update(fingerprint, Qwen122B)  // simulate Point B after Hard Rule
      req = mkReq fingerprint "what is 2+2"
      decision = routeRequest cfg alg req
      assert: decision.Target = Qwen122B, decision.Reason = StickyEscalation
```

**Target test count**: 8 tests added → ~175 passed total (167 + 8).

### Q7: Smoke test script (`scripts/smoke-hermes-session.sh`)

Precedent patterns from existing scripts:
- `scripts/check-no-async.sh`: `set -euo pipefail`, `#!/usr/bin/env bash`, PASS/FAIL via exit codes
- `scripts/deploy.sh`: absolute path via `REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"`, clear echoed progress

The smoke test must be self-contained (no Hermes Agent dependency) and re-runnable. Key design:

```bash
#!/usr/bin/env bash
# scripts/smoke-hermes-session.sh — verify X-Session-Id session propagation end-to-end.
#
# Assumes smart-router is ALREADY RUNNING on http://127.0.0.1:4000.
# Does NOT start or stop the router.
# Re-runnable: uses a timestamp-based session ID per run to avoid TTL interference.
#
# Exit 0 = PASS; exit 1 = FAIL.

set -euo pipefail

PORT=4000
BASE_URL="http://127.0.0.1:${PORT}"
DECISION_LOG="logs/decisions/$(date +%Y-%m-%d).jsonl"
SESSION_ID="smoke-hermes-$(date +%s)"  # unique per run; avoids TTL cross-contamination
TRIGGER_PROMPT="diagnose the LLVM compiler segfault in the optimizer"
FOLLOWUP_PROMPT="what was the last thing you said"

echo "[smoke] Session ID: ${SESSION_ID}"
echo "[smoke] Decision log: ${DECISION_LOG}"

# Request 1: Hard Rule keyword triggers 122B; Point B writes session → 122B.
CID1=$(curl -sf -X POST "${BASE_URL}/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "X-Session-Id: ${SESSION_ID}" \
  -d "{\"messages\":[{\"role\":\"user\",\"content\":\"${TRIGGER_PROMPT}\"}],\"stream\":false}" \
  -D - -o /dev/null 2>&1 | grep -i "x-correlation-id" | awk '{print $2}' | tr -d '\r')

echo "[smoke] Request 1 correlation_id: ${CID1}"

# Brief wait for DecisionLog async write to flush (channel-buffered; typically <100ms).
sleep 1

# Request 2: neutral prompt, same session → sticky_to_122b expected.
CID2=$(curl -sf -X POST "${BASE_URL}/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "X-Session-Id: ${SESSION_ID}" \
  -d "{\"messages\":[{\"role\":\"user\",\"content\":\"${FOLLOWUP_PROMPT}\"}],\"stream\":false}" \
  -D - -o /dev/null 2>&1 | grep -i "x-correlation-id" | awk '{print $2}' | tr -d '\r')

echo "[smoke] Request 2 correlation_id: ${CID2}"

sleep 1

# Assert: DecisionLog for CID2 shows routing_reason="sticky_to_122b".
if grep -q "\"correlation_id\":\"${CID2}\"" "${DECISION_LOG}" && \
   grep "\"correlation_id\":\"${CID2}\"" "${DECISION_LOG}" | grep -q '"routing_reason":"sticky_to_122b"'; then
  echo "[smoke] PASS: Request 2 routed sticky_to_122b (correlation_id=${CID2})"
  exit 0
else
  echo "[smoke] FAIL: Did not find routing_reason=sticky_to_122b for correlation_id=${CID2}" >&2
  echo "[smoke] DecisionLog tail:" >&2
  tail -5 "${DECISION_LOG}" >&2
  exit 1
fi
```

**Notes for the planner:**
- The script must run from the router's working directory (where `logs/decisions/` is relative). Document this in comments and usage line.
- Correlation ID extraction from response headers requires `-D -` flag to capture response headers. Alternative: grep the decision log for the session_id field (but correlation_id is more precise).
- `sleep 1` is conservative — the DecisionLog channel writer (`DecisionLogWriter.fs`) is async but typically flushes within milliseconds. Document this in the script.
- Add fingerprint variant section (optional, guarded by `FINGERPRINT_TEST=1` env var) for operators who want to test fingerprint mode without modifying appsettings.json manually.

### Q8: README §10 current state and diff scope

Current §10 content (README.md:781-813) is **7 lines of substantive content** describing the **v1.x ML routing paradigm** for both Hermes and Graphify. It references:
- "stage 3 (ML classifier)" — outdated; must become "v2.0 selfrouting cascade"
- Hermes config snippet (keep)
- Graphify task routing (keep — Graphify behavior is unchanged)
- graph_indexing no-fallback rule (keep — this is still accurate)

The `---` separator on line 812 and the `## 11. Operations` section start on line 814 are the boundaries. **Total diff scope: lines 781-812 (31 lines).** This is effectively a full rewrite of the Hermes subsection plus additions for session/fingerprint, while Graphify stays largely intact.

**README §10 rewrite outline (exact sections for planner use):**

```markdown
## 10. Hermes / Graphify Integration

### Hermes Agent (v2.0 session-aware selfrouting)

[one-paragraph: selfrouting paradigm replaced ML routing in v2.0; smart-router
now uses keyword Hard Rules + 35B self-classify + sticky session continuity
instead of ML classifier; Hermes sends requests as before — no Hermes config change
required for basic operation]

#### Hermes config (unchanged from v1.x)
[existing jsonc config block — no changes]

#### X-Session-Id header opt-in (session continuity)

[How Hermes can send X-Session-Id header → sticky escalation;
what routing_reason="sticky_to_122b" means;
that Hermes-side propagation is future work (HMRS-FUTURE-01);
v2.0 ships smart-router-side machinery only]

#### Fingerprint fallback (loopback single-client)

[opt-in via Routing.Session.FingerprintEnabled=true;
derives session key from RemoteIpAddress + User-Agent SHA-256 prefix;
WARNING: not safe behind reverse proxies (nginx/Caddy) — X-Forwarded-For not parsed;
tracked as PROXY-01 for v2.x;
suitable for: loopback single-client development where Hermes has not yet shipped X-Session-Id propagation]

### Graphify (concurrency-protected; sends `task`)

[existing content — unchanged; Graphify routing via task table is unaffected by v2.0 changes]
[graph_indexing no-fallback rule — unchanged]
```

**Cross-references that must stay consistent:**
- §7 gains one new row: `Routing.Session.FingerprintEnabled` (bool, default `false`)
- §5 routing pipeline description is accurate as-is (six stages documented in Phases 17-19)
- §11 Graphify content remains unchanged

### Q9: CHANGELOG current state

`[Unreleased]` section currently has three content blocks:
1. Phase 17 `### Added` + `### Changed` + `### Notes` (lines 15-31)
2. Phase 18 `### Added` + `### Notes` (lines 34-75)
3. Phase 19 `### Added` + `### Notes` (lines 78-126)

Each block is separated by `---`. Phase 20 appends a fourth block, then the `[Unreleased]` → `[2.0.0]` version header is promoted.

**Exact CHANGELOG lines for Phase 20 (planner can use directly):**

```markdown
---

### Added (Phase 20 — Hermes Integration + Documentation)

- **Fingerprint fallback session key (opt-in).** When `Routing.Session.FingerprintEnabled=true`
  (default `false`) and no `X-Session-Id` header is present, `CorrelationMiddleware` derives a
  session key from `SHA-256(RemoteIpAddress + "|" + User-Agent)` truncated to 16 hex characters.
  Enables sticky escalation continuity for the loopback single-client development scenario
  (Hermes Agent on the same host before it ships X-Session-Id propagation). See README §10.
  **Not safe behind reverse proxies** — `X-Forwarded-For` is not parsed; tracked as PROXY-01
  for v2.x work.
- **`Routing.Session.FingerprintEnabled` config key** (`appsettings.json`). Boolean, default
  `false`. Opt-in only — preserves v1.x stateless behavior unless explicitly enabled. See
  README §7.
- **`scripts/smoke-hermes-session.sh`.** Operator-runnable smoke test verifying X-Session-Id
  session propagation end-to-end with curl + DecisionLog grep assertion. No Hermes Agent
  dependency — curl drives both requests directly.
- **`HermesFingerprintTests.fs`** integration tests covering fingerprint-enabled and
  fingerprint-disabled paths (8 test cases).

### Changed (Phase 20)

- **README §10 "Hermes Integration" fully rewritten for v2.0.** Replaces the v1.x ML routing
  description (stage 3 ML classifier) with the v2.0 selfrouting paradigm (keyword Hard Rules +
  35B self-classify + sticky session). Documents X-Session-Id opt-in, fingerprint fallback
  caveats, and Hermes-side propagation as future v2.x work (HMRS-FUTURE-01).

### Notes (Phase 20)

- `schema_version=1` unchanged. No new DecisionLog fields — fingerprint-derived session IDs
  participate in the existing `routing_reason="sticky_to_122b"` flow.
- Hermes Agent code is NOT modified in v2.0. Smart-router ships the session-aware infrastructure;
  Hermes propagating `X-Session-Id` is tracked as HMRS-FUTURE-01 (post-v2.0).
- REQUIREMENTS.md HMRS-FUTURE-01, HMRS-FUTURE-02, PROXY-01 retained as v2.x trackers
  (no change to those entries).
```

After Phase 20 CHANGELOG is written, the `[Unreleased]` header becomes `[2.0.0] - 2026-05-12` (or the release date). The planner should include this rename as part of plan 20-02.

### Q10: REQUIREMENTS.md update scope

Current state (from `.planning/REQUIREMENTS.md`):
- HMRS-01..04: currently `[ ]` (Pending) — Phase 20 marks all four `[x]` (Complete)
- HMRS-FUTURE-01, HMRS-FUTURE-02: already present as future trackers — **no change needed**
- PROXY-01: already present as future tracker — **no change needed**
- The completion table at line 137-140 shows HMRS-01..04 Phase 20 / Pending — update to Complete

**Exact edits in REQUIREMENTS.md:**
- Lines 57-60: change `- [ ]` to `- [x]` for HMRS-01, HMRS-02, HMRS-03, HMRS-04
- Lines 137-140: change `Pending` to `Complete` for HMRS-01..04
- Line 143: update count from "32 total" to "32 total" (no change — requirements count does not change; completions change)

This is a small, mechanical edit. HMRS-FUTURE-01/02 and PROXY-01 at lines 72-83 are untouched.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| SHA-256 hex encoding | Custom byte-to-hex loop | `Array.map (sprintf "%02x") \|> String.concat ""` | Already canonical in codebase; SHA256.Create() per call avoids thread-safety issues |
| HTTP context in tests | Kestrel integration test host | `DefaultHttpContext()` from `Microsoft.AspNetCore.Http` | Sufficient for middleware unit tests; avoids full WebApplicationFactory complexity |
| Session ID uniqueness per smoke test run | Static session ID | `"smoke-hermes-$(date +%s)"` | Avoids TTL cross-contamination between runs |

---

## Common Pitfalls

### Pitfall 1: Sharing SHA256 instance across requests
**What goes wrong:** SHA256 is not thread-safe. A shared instance corrupts hashes under concurrent load.
**How to avoid:** `use sha = SHA256.Create()` inside the per-request middleware function. Already documented as Pitfall P8 in DecisionLogger.fs:10.

### Pitfall 2: Writing fingerprint to session store even when empty
**What goes wrong:** If `RemoteIpAddress` is null AND `User-Agent` is absent, the fingerprint is `SHA-256("unknown|unknown")[0..15]` — a fixed value that becomes a shared sticky bucket for all unknown clients.
**How to avoid:** Only apply fingerprint when at least one of IP or UA is non-unknown. Alternative: document and accept the "unknown|unknown" case as an operator concern (it only fires when running behind a broken proxy that strips all IP info). The `String.IsNullOrEmpty` guard in `SessionStore.Update` already prevents empty-string problems — this is a fingerprint-always-non-empty issue, not empty-string.
**Recommendation:** Document the "unknown|unknown" edge case in README §10 as a caveat of the loopback-only design.

### Pitfall 3: Fingerprint enabled behind reverse proxy silently breaks sessions
**What goes wrong:** Behind nginx/Caddy, `RemoteIpAddress` is the proxy's IP (same for all clients). All clients get the same fingerprint and share a sticky bucket — hard to debug.
**How to avoid:** README §10 must include a prominent warning box (not just a footnote). Document PROXY-01 tracking. Recommend operators only enable `FingerprintEnabled=true` in direct loopback deployments.

### Pitfall 4: `correlationMiddleware` signature change breaks production registration
**What goes wrong:** If `correlationMiddleware` gains a `fingerprintEnabled: bool` first parameter, the existing `app.Use(fun ctx next -> correlationMiddleware ctx next)` call in `Program.fs` breaks at compile time.
**How to avoid:** Read `Program.fs` before coding to find the exact registration line. The change is mechanical: the caller passes the bool, resolved from config at startup. Compile error at startup is the detection mechanism — not a runtime surprise.
**Detection:** `TreatWarningsAsErrors=true` means any unused parameter warning fails the build too.

### Pitfall 5: Test module not registered in `rootTests`
**What goes wrong:** `HermesFingerprintTests.fs` compiles but its tests never run. Expecto uses explicit `rootTests` list (not auto-discovery).
**How to avoid:** Add `SmartRouter.Tests.HermesFingerprintTests.tests` to `RouterTests.fs` `rootTests` list AND add `<Compile Include="HermesFingerprintTests.fs" />` to `SmartRouter.Tests.fsproj` before `RouterTests.fs`. Both are required.

### Pitfall 6: Smoke test session ID collision across runs
**What goes wrong:** Re-running the script within the SessionStore TTL window (30 min) causes Request 1 to see an existing sticky bucket from the prior run, making the Hard Rule assertion ambiguous.
**How to avoid:** Use `"smoke-hermes-$(date +%s)"` as session ID — unique per run. Document in script header.

### Pitfall 7: Smoke test DecisionLog timing
**What goes wrong:** `DecisionLogWriter` uses a channel-based async writer. The JSONL file may not be flushed by the time the grep assertion runs.
**How to avoid:** `sleep 1` after each request before grepping. The writer flushes in practice within milliseconds, but 1s is safe for scripting. Document in script header.

### Pitfall 8: `Program.fs` middleware registration — where to read `FingerprintEnabled`
**What goes wrong:** If config is read inside the middleware lambda (per-request), the value is read from config on every request rather than once at startup.
**How to avoid:** Read `FingerprintEnabled` once at app startup (outside the middleware lambda), close over the boolean. This mirrors how `routingMode` is read in `CompositionRoot.fs:405` — direct `config.["Routing:Mode"]` string read at composition time.

---

## Code Examples

### Fingerprint resolution in CorrelationMiddleware

```fsharp
// Proposed addition to correlationMiddleware (after existing X-Session-Id read block)
// fingerprintEnabled: bool is a new parameter, read from config at startup by caller

let sessionId =
    match ctx.Request.Headers.TryGetValue(SessionIdHeader) with
    | true, sv when sv.Count > 0 && not (String.IsNullOrWhiteSpace(sv.[0])) ->
        sv.[0]   // explicit header wins
    | _ ->
        // No valid X-Session-Id header
        if fingerprintEnabled then
            let ip =
                if isNull ctx.Connection.RemoteIpAddress then "unknown"
                else ctx.Connection.RemoteIpAddress.ToString()
            let ua =
                match ctx.Request.Headers.TryGetValue("User-Agent") with
                | true, sv when sv.Count > 0 && not (String.IsNullOrWhiteSpace(sv.[0])) -> sv.[0]
                | _ -> "unknown"
            let input = sprintf "%s|%s" ip ua
            use sha = System.Security.Cryptography.SHA256.Create()
            let bytes = System.Text.Encoding.UTF8.GetBytes(input)
            let hash  = sha.ComputeHash(bytes)
            let hex   = hash |> Array.map (sprintf "%02x") |> String.concat ""
            hex.Substring(0, 16)   // 16 hex chars = 8 bytes
        else
            ""   // stateless — no sticky bucket
ctx.Items.[SessionIdKey] <- sessionId
```

### SessionOptions with FingerprintEnabled

```fsharp
// SessionStore.fs — add one field to existing SessionOptions record
[<CLIMutable>]
type SessionOptions = {
    mutable TtlMinutes         : int
    mutable MaxEntries         : int
    mutable FingerprintEnabled : bool   // Phase 20: default false when JSON key absent
}
```

### DefaultHttpContext test setup (HermesFingerprintTests.fs skeleton)

```fsharp
module SmartRouter.Tests.HermesFingerprintTests

open System.Net
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open SmartRouter.Cli.Adapters.CorrelationMiddleware

let private makeCtx (remoteIp: string) (ua: string) (sessionHeader: string option) : DefaultHttpContext =
    let ctx = DefaultHttpContext()
    ctx.Connection.RemoteIpAddress <-
        if remoteIp = "" then null
        else IPAddress.Parse(remoteIp)
    if ua <> "" then
        ctx.Request.Headers["User-Agent"] <- Microsoft.Extensions.Primitives.StringValues(ua)
    match sessionHeader with
    | Some hv -> ctx.Request.Headers["X-Session-Id"] <- Microsoft.Extensions.Primitives.StringValues(hv)
    | None    -> ()
    ctx

let private runMiddleware (fingerprintEnabled: bool) (ctx: DefaultHttpContext) : unit =
    let next = RequestDelegate(fun _ -> Task.CompletedTask)
    correlationMiddleware fingerprintEnabled ctx next |> Async.AwaitTask |> Async.RunSynchronously

let tests : Test =
    testSequenced <| testList "HermesFingerprintTests" [
        // FP-1 through FP-8 here
    ]
```

---

## Files Modified by Plan

### Plan 20-01: Fingerprint fallback + smoke test

| File | Change type | Note |
|------|-------------|------|
| `src/SmartRouter.Cli/Adapters/SessionStore.fs` | Edit | Add `FingerprintEnabled : bool` field to `SessionOptions` |
| `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` | Edit | Add `fingerprintEnabled: bool` parameter + fingerprint logic |
| `src/SmartRouter.Cli/Program.fs` | Edit | Read `FingerprintEnabled` from config at startup; pass to middleware |
| `src/SmartRouter.Cli/appsettings.json` | Edit | Add `"FingerprintEnabled": false` to `Routing.Session` block |
| `tests/SmartRouter.Tests/HermesFingerprintTests.fs` | Create | 8 integration test cases |
| `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` | Edit | Add `<Compile Include="HermesFingerprintTests.fs" />` |
| `tests/SmartRouter.Tests/RouterTests.fs` | Edit | Add `HermesFingerprintTests.tests` to `rootTests` |
| `scripts/smoke-hermes-session.sh` | Create | curl loop + DecisionLog grep assertion |

### Plan 20-02: README §10 rewrite + CHANGELOG + REQUIREMENTS.md closure

| File | Change type | Note |
|------|-------------|------|
| `README.md` | Edit | §10 full rewrite (lines 781-812); §7 new `FingerprintEnabled` row |
| `CHANGELOG.md` | Edit | Append Phase 20 `### Added` + `### Changed` + `### Notes` block; rename `[Unreleased]` to `[2.0.0] - 2026-05-12` |
| `.planning/REQUIREMENTS.md` | Edit | HMRS-01..04 `[ ]` → `[x]`; completion table Pending → Complete |

**Dependency order**: Plan 20-01 must precede 20-02 (README §7 `FingerprintEnabled` documents the config key shipped in 20-01). These can be in separate commits but 20-01 commit must come first.

---

## State of the Art

| Old (v1.x) | New (v2.0) |
|------------|------------|
| README §10: "stage 3 (ML classifier) decides" | README §10: selfrouting cascade (Hard Rules + sticky + 35B self-classify) |
| No session continuity concept in §10 | §10: X-Session-Id opt-in + fingerprint fallback documented |
| SessionOptions: TtlMinutes, MaxEntries only | SessionOptions: adds FingerprintEnabled (bool, default false) |

---

## Open Questions

1. **`Program.fs` exact middleware registration line** — not read during research. Before coding plan 20-01, confirm the exact `app.Use(...)` call that registers `correlationMiddleware`. The change adds a `fingerprintEnabled: bool` first parameter. If the current call is already a curried lambda, the change is one-line. If it uses a different pattern, the approach may need adjustment.
   - What we know: `correlationMiddleware ctx next` is the current call signature (CorrelationMiddleware.fs:39). The caller in Program.fs wraps it in `app.Use(fun ctx next -> ...)`.
   - Recommendation: Read Program.fs first task of plan 20-01.

2. **`ipv6`-only loopback `::1` in smoke test** — on macOS, Kestrel with `http://127.0.0.1:4000` uses IPv4. If a client connects via `::1`, the fingerprint would differ from a `127.0.0.1` client even if logically the same machine. This only matters for the fingerprint feature, not the X-Session-Id header path. The smoke test uses explicit `X-Session-Id` header and is not affected. README §10 should mention IPv4 vs IPv6 loopback as a known nuance.

3. **Smoke test correlation ID extraction** — the script design above extracts `X-Correlation-Id` from response headers. An alternative is to grep the decision log for `"session_id":"smoke-hermes-..."` entries and get the correlation_id from there. The header-extraction approach is simpler for the operator reading the output. If the shell awk/grep for headers proves fragile, fall back to decision log correlation approach.

---

## Sources

### Primary (HIGH confidence — direct codebase reads)

- `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` — middleware structure, existing X-Session-Id read pattern, StringValues API usage
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — sessionId extraction from ctx.Items, mapWireToRequest signature, SHA-256 pattern
- `src/SmartRouter.Cli/Adapters/SessionStore.fs` — SessionOptions record, ISessionStore interface, 122B-wins merge
- `src/SmartRouter.Cli/CompositionRoot.fs` — SessionOptions binding, config read patterns, routingMode pattern
- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` — canonical lowercase hex SHA-256 pattern
- `src/SmartRouter.Cli/Adapters/SelfRouter.fs` — SHA-256 pattern with Substring(0, N)
- `tests/SmartRouter.Tests/StickyEscalationTests.fs` — test structure, minimalConfigPairs, testSequenced, DefaultHttpContext alternative
- `tests/SmartRouter.Tests/RouterTests.fs` — rootTests explicit list registration pattern
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — Compile order
- `src/SmartRouter.Cli/appsettings.json` — current Routing.Session block shape
- `README.md` lines 781-813 — current §10 content; lines 447-448 — §7 Session config rows
- `CHANGELOG.md` — Phase 17/18/19 entry format and `---` separator pattern
- `.planning/REQUIREMENTS.md` — HMRS-01..04, HMRS-FUTURE-01/02, PROXY-01 locations
- `.planning/STATE.md` — test count baseline (167 passed + 18 ignored + 0 failed)

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — BCL SHA-256, ASP.NET Core HttpContext.Connection, all verified in existing codebase
- Architecture: HIGH — fingerprint slot confirmed by reading CorrelationMiddleware.fs + ChatCompletions.fs
- Test patterns: HIGH — StickyEscalationTests.fs is the direct precedent; DefaultHttpContext verified as available
- Pitfalls: HIGH — all pitfalls are extrapolations of issues already documented in existing code comments
- README §10 scope: HIGH — read actual file, confirmed 31-line section with §11 boundary

**Research date:** 2026-05-12
**Valid until:** 2026-06-12 (stable APIs; no fast-moving ecosystem)

---

## RESEARCH COMPLETE

**Phase:** 20 — Hermes Agent Integration + Documentation
**Confidence:** HIGH

### Key Findings

- Fingerprint logic slots entirely inside `CorrelationMiddleware.fs` — no changes to ChatCompletions, SessionStore, or routing logic
- `correlationMiddleware` gains one new parameter `fingerprintEnabled: bool`; caller (Program.fs) reads it from config at startup
- SHA-256 pattern: `Array.map (sprintf "%02x") |> String.concat ""` → lowercase; Substring(0, 16) for 16 chars — consistent with `DecisionLogger.fs:16` and `SelfRouter.fs:172`
- Test pattern: `DefaultHttpContext()` from Microsoft.AspNetCore.Http enables middleware unit tests without Kestrel; 8 test cases identified
- README §10 is a full rewrite of the Hermes subsection (31 lines); Graphify subsection mostly unchanged
- REQUIREMENTS.md: only HMRS-01..04 `[ ]`→`[x]` changes; HMRS-FUTURE-01/02 and PROXY-01 untouched
- Test baseline: 167 passed + 18 ignored; Phase 20 adds 8 → target ~175 passed

### Files Created

`.planning/phases/20-hermes-agent-integration-documentation/20-RESEARCH.md`

### Ready for Planning

Research complete. Planner can now create PLAN.md files for 20-01 and 20-02.
