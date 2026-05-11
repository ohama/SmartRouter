# Pitfalls Research

**Domain:** LLM routing gateway — v2.0 Self-Routing + Session-Aware
**Researched:** 2026-05-11
**Confidence:** HIGH (grounded in v1.x codebase + `35b-selfrouting.md` + Phase 16 post-mortem)

---

## Critical Pitfalls

### Pitfall 1: 35B Self-Classify Overconfidence on Subtle Complexity

**What goes wrong:**
35B receives a routing prompt ("Is this SAFE for a fast 35B model?") and replies `SAFE` for prompts that are deceptively simple-looking but contextually complex — e.g., "fix this" after 6 turns of debugging a concurrency bug. The Hard Rules layer only catches keyword-explicit signals (LLVM, MLIR, compiler, etc.). Subtle escalation signals ("retry", "keep going", "I still get the crash") bypass Hard Rules and 35B self-classifies them as SAFE because the prompt text is short and has no flagged keywords.

**Why it happens:**
The core design doc (`.planning/docs/35b-selfrouting.md` §7,10) names this explicitly: "35B may become overconfident — 'I can probably solve this' even when debugging is deep." Short prompts that imply continuation context look safe to a token-count-naive classifier. The model sees 5 tokens ("fix this please") not 6-turn debugging context.

**How to avoid:**
Two complementary defenses:
1. **Continuation keywords in UNSAFE examples** — The self-router prompt MUST include `retry/fix/continue workflows` as UNSAFE examples (per `.planning/docs/35b-selfrouting-prompt.md` §7). This is load-bearing; removing these lines silently degrades routing quality.
2. **Sticky escalation as the final safety net** — If ANY prior request in the session was routed to 122B, subsequent requests stay on 122B regardless of self-classify verdict. Sticky covers the case where session context makes a short prompt actually complex.

Both defenses must ship together. Neither alone is sufficient.

**Warning signs:**
- Log signal: `routing_reason="selfroute_safe"` on requests whose prompt contains "fix", "retry", "continue", "try again", "still failing", "same error" — flag these for spot-check.
- Operational symptom: Hermes user reports low-quality response on what felt like a complex debugging session that "reset" mid-conversation.

**Phase to address:**
Hard Rules phase (stage 0 pre-routing) + Self-Router phase (prompt template). Sticky escalation phase also mitigates this. These three phases form a layered defense.

---

### Pitfall 2: Self-Router Prompt Template Drift Changes Routing Behavior Silently

**What goes wrong:**
The operator edits `prompts/self-router-prompt.md` to adjust SAFE/UNSAFE examples. The router hot-reloads (if FileSystemWatcher is used, per Phase 9 pattern) or restarts. The new prompt changes which requests get SAFE verdicts. There is no audit trail. DecisionLog records `routing_reason` but not which prompt version was active. Six months later the operator can't explain why routing quality degraded.

**Why it happens:**
Operator-tunable prompt files are a feature (they're in `appsettings.json` via `PromptPath`). The same pattern used in Phase 16 `judge-prompt.md` and Phase 7 `teacher-prompt.md`. The risk is unique to the self-router because the prompt IS the routing algorithm — a word change in UNSAFE examples is a behavioral code change with no compiler enforcement.

**How to avoid:**
1. **Log the prompt hash on startup** — At startup (or on hot-reload), compute SHA-256 of the loaded self-router prompt and log it at `Information` level. Include it in `/stats` response as `self_router_prompt_hash`. Operator can correlate routing behavior change with prompt change.
2. **Pin `self-router-prompt.md` to git** — Never gitignore it. Diff is the audit trail.
3. **Default prompt is load-bearing** — The default prompt ships with `retry/fix/continue` in UNSAFE. Document that removing these degrades routing quality.

**Warning signs:**
- `/stats` `self_router_prompt_hash` changes across restarts.
- Sudden shift in `routing_reason="selfroute_safe"` vs `"selfroute_unsafe"` ratio in DecisionLog (observable via `jq '.routing_reason' logs/decisions/*.jsonl | sort | uniq -c`).

**Phase to address:**
Self-Router phase. Add prompt hash logging as part of the Self-Router wiring plan.

---

### Pitfall 3: Self-Classify Response Format Drift (Non-SAFE/UNSAFE Token)

**What goes wrong:**
The 35B self-classify call is supposed to return a single token: `SAFE` or `UNSAFE` (or with the JSON format from `.planning/docs/35b-selfrouting-prompt.md` §3: `{"route": "35b"|"122b", ...}`). Under load, at non-zero temperature, or after a model update, 35B sometimes responds with `"The request is SAFE..."` or a JSON blob with a typo (`"35B"` instead of `"35b"`) or multi-token preamble before the token. The parser sees neither `SAFE` nor `UNSAFE` and must have a defined fallback.

**Why it happens:**
mlx_lm.server does not enforce grammar-constrained decoding by default. `max_tokens=4-8` reduces the risk but does not eliminate it. Even at `temperature=0`, floating-point determinism on different hardware/model-load states isn't guaranteed to be perfectly stable. This is the same class of failure as the Phase 16 JudgeClient `JudgeFailed` path (unparseable response).

**How to avoid:**
1. **Fail-to-UNSAFE** — When the self-classify response is unparseable (neither SAFE nor UNSAFE found), default to `UNSAFE` (route to 122B). This is the conservative bias: prefer a slower-but-correct route over a confident-but-wrong fast route. Analogous to Phase 16's `ROUTE_NO` bias on collision.
2. **Substring search, not equality** — Check `content.Contains("SAFE")` + `content.Contains("UNSAFE")`. If both present, `UNSAFE` wins. If neither, `UNSAFE` wins. Never `content = "SAFE"`.
3. **Log the raw response on parse failure** — `LogWarning("SelfRouter: unparseable response '{Raw}'; defaulting to UNSAFE; cid={Cid}", raw, cid)`. This catches prompt format drift early.

**Warning signs:**
- Operational log: `SelfRouter: unparseable response` warning lines appearing.
- `/stats` `self_router_parse_fail_count` counter rising (add this counter to the Self-Router phase; mirror Phase 16 `judge_call_count` pattern).

**Phase to address:**
Self-Router phase. The parser is the first thing to unit-test.

---

### Pitfall 4: Session Store Race Condition — Concurrent Requests on Same Session

**What goes wrong:**
Hermes sends two requests concurrently with the same `X-Session-Id` header (rare but possible under retry or UI double-send). Both requests read the session store, see `LastModel = None` (fresh session), run the self-classify pipeline, and both get routed to 35B. Request A finishes, escalates (35B gives UNSAFE), and writes `LastModel = 122B` to the store. Request B meanwhile already read the old state, ran on 35B, and its response is already in-flight. The session store write from A and B's concurrent read form a TOCTOU race. Result: one session turn is lost from the sticky escalation history.

**Why it happens:**
v1.x is fully stateless. v2.0 introduces the session store as the first piece of shared mutable state accessed across concurrent request handlers. The session store needs at minimum `ConcurrentDictionary<sessionId, SessionState>` (same pattern as Phase 16 LRU cache). But the read-modify-write cycle (read current model → decide → write new model) is not atomic.

**How to avoid:**
Use `ConcurrentDictionary.AddOrUpdate` for the write path so the update is atomic with respect to other writers on the same key. The read side can be non-atomic (`TryGetValue`) — reading a slightly stale value means at worst one extra 35B call, which is acceptable. The critical invariant is that a `122B` write never gets lost due to two concurrent writers both overwriting with their results. Solution: `AddOrUpdate` with a merge function that keeps `122B` if either old or new value is `122B`:

```fsharp
store.AddOrUpdate(
    sessionId,
    addValue = newState,
    updateValueFactory = fun _ oldState ->
        // Escalation is sticky: once 122B appears, it stays
        if oldState.LastModel = Some Qwen122B || newState.LastModel = Some Qwen122B
        then { newState with LastModel = Some Qwen122B }
        else newState)
```

**Warning signs:**
- Log: two requests with the same correlation-id prefix but different session assignments both showing `routing_reason="selfroute_safe"` when the session should have been escalated.
- Operational symptom: sticky escalation appears to "forget" that the session was on 122B after a rapid re-query.

**Phase to address:**
Sticky Session / Session Store phase. The `AddOrUpdate` merge strategy must be in the implementation plan.

---

### Pitfall 5: Session Store Lost on Restart — Stateless Mental Model Broken

**What goes wrong:**
v1.x is stateless. v2.0 adds an in-memory session store (likely `ConcurrentDictionary`). When smart-router restarts (OS reboot, launchd restart, `--retrain` path), all sessions are wiped. A Hermes user mid-debugging-session who was on 122B will get routed to 35B on their next request after restart. The user experiences this as a quality regression with no visible cause.

**Why it happens:**
In-memory stores don't survive process death. This is the expected trade-off for simplicity, but it needs an explicit design decision and operator documentation so it isn't discovered at 2am.

**How to avoid:**
1. **Document the behavior explicitly** — "Session store is in-memory. Router restart clears all sessions. Sticky escalation resets to fresh-session routing after restart." README §5 (Routing Pipeline) must state this.
2. **Accept the trade-off for v2.0** — A file-backed session store adds persistence but complicates startup, file locking, and format migration. For a Mac single-user setup with ~1 active session at a time, in-memory is correct. The LRU eviction (see Pitfall 6) is sufficient.
3. **If persistence is later needed** — Mirror Phase 16 LRU cache: `ConcurrentDictionary` + a JSON serialization step on graceful shutdown (`IHostApplicationLifetime.ApplicationStopping`). Not v2.0 scope; name-drop in REQUIREMENTS.md as future work.

**Warning signs:**
- User reports "it forgot I was debugging" after launchd restarted the router.
- `/stats` `session_store_entry_count` drops to 0 on router restart (add this counter to `/stats`).

**Phase to address:**
Sticky Session / Session Store phase. Document in-memory semantics explicitly in the phase plan and README sync checklist.

---

### Pitfall 6: Session Store Unbounded Growth — Memory Leak Over Days

**What goes wrong:**
Each unique `X-Session-Id` adds an entry to the session store. Hermes generates new session IDs on each agent run. Over days of continuous operation, the session store grows indefinitely. At 1 entry per session × 100 sessions/day × 30 days = 3000 entries, each a small F# record. This is not a memory catastrophe but it signals the need for TTL-based eviction.

**Why it happens:**
Same class of issue as Phase 16 LRU cache growth — any unbounded in-memory collection needs an eviction policy. Phase 16 used `MaxCacheEntries` config + O(n) min-scan eviction. Session store needs a TTL: sessions older than N minutes without activity are evicted.

**How to avoid:**
Add TTL eviction via a `BackgroundService` with `PeriodicTimer` (mirror Phase 8/16 pattern). Session entries store a `LastSeenAt : DateTimeOffset`. The eviction loop runs every 5 minutes and removes entries where `DateTimeOffset.UtcNow - LastSeenAt > SessionTtl`. Default TTL: 30 minutes. Operator-tunable via `Routing.Session.TtlMinutes`.

```fsharp
// SessionEntry (stored per session_id)
type SessionEntry = {
    LastModel   : ModelId option  // None = no 122B routing yet in this session
    LastSeenAt  : DateTimeOffset  // Updated on every request; used for TTL eviction
}
```

Do NOT use the Phase 16 LRU pattern (evict-on-insert by count) for sessions — session age matters, not session count. A session from 2 hours ago should be evicted before a session from 5 minutes ago regardless of insertion order.

**Warning signs:**
- `/stats` `session_store_entry_count` growing monotonically across days.
- Process RSS growing slowly but steadily after days of operation.

**Phase to address:**
Sticky Session / Session Store phase. TTL eviction must be in the same phase as session creation.

---

### Pitfall 7: Hermes Backward-Compat — v1.x Clients Without X-Session-Id Silently Get Wrong Behavior

**What goes wrong:**
v1.x Hermes clients don't send `X-Session-Id`. In v2.0, if the session store lookup uses `X-Session-Id` as key and falls back to `""` (empty string) for missing headers, all v1.x clients share a single session entry keyed by `""`. The first time any v1.x client triggers a 122B route, ALL subsequent v1.x requests get sticky-to-122B — even unrelated Graphify `retrieval` tasks that should go to 35B.

**Why it happens:**
Null/empty header treated as a valid session key. One shared bucket for all sessions-without-id.

**How to avoid:**
When `X-Session-Id` is absent or empty, do not create a session store entry. Treat the request as fresh-session / sessionless: run Hard Rules → self-classify → route normally, but skip the sticky check and skip the session write. The request behaves as v1.x did: stateless. Sticky escalation is opt-in via the presence of a session ID header.

```fsharp
// In ChatCompletions.fs handler
let sessionId = ctx.Request.Headers.TryGetValue("X-Session-Id") |> ...
let stickyTarget =
    match sessionId with
    | None | Some "" -> None   // no session → no sticky
    | Some sid ->
        sessionStore.TryGet(sid)
        |> Option.bind (fun entry -> entry.LastModel)
```

**Warning signs:**
- `routing_reason="sticky_122b"` appearing on requests that have no `X-Session-Id` header in the DecisionLog.
- Graphify `task=retrieval` requests (always → 35B) suddenly routing to 122B after any Hermes debug session.

**Phase to address:**
Hermes Integration phase. The null-session guard is the first thing the Hermes integration plan must specify.

---

### Pitfall 8: Cascade Ordering Bug — Sticky Wins Before Hard Rules

**What goes wrong:**
If the cascade in `ChatCompletions.fs` checks sticky escalation BEFORE Hard Rules, a session that is sticky-to-35B can bypass a Hard Rule that should force 122B. Example: session was sticky on 35B (multiple safe requests), operator sends "debug my LLVM pass" — Hard Rule should trigger 122B, but sticky says 35B. If sticky check runs first, Hard Rule is never reached.

Also the reverse: if Hard Rules run before task-table check, a `task=graph_indexing` request from Graphify that happens to contain the word "compiler" is intercepted by Hard Rules before reaching the explicit task table — but since `graph_indexing` already routes to 122B, the result is correct anyway. The dangerous ordering bugs are only where a less-specific stage could give a wrong result.

**How to avoid:**
The canonical cascade ordering (highest precedence first):

```
1. Explicit model override (model="35b" | "122b")       — always wins
2. Explicit task table (task=graph_indexing, etc.)       — always wins over routing heuristics
3. Hard Rules (keyword pre-routing; LLVM/MLIR/compiler) — wins over self-classify
4. Sticky escalation check                               — wins over self-classify result
5. 35B self-classify                                     — runs only if none of above fired
6. Default: 35B                                          — never reached if self-classify ran
```

Sticky check at position 4 means: if a session is sticky-to-122B, we go to 122B even without a Hard Rule hit. But Hard Rules (position 3) always override sticky — a Hard Rule forces 122B, full stop, even if the session had established a 35B baseline (though in practice Hard Rules only ever force 122B, never 35B, so this is benign).

**Warning signs:**
- A request with `task=graph_indexing` routing to 35B (explicit task table bypassed).
- A request with "LLVM" in the prompt routing to 35B (Hard Rule bypassed).
- A sticky-122B session getting routed to 35B (sticky bypassed by an unexpected reset).

**Phase to address:**
Hard Rules phase must define the cascade ordering. Self-Router phase must wire into position 5. Sticky Session phase must wire into position 4. All three phases must agree on the ordering by referencing a single cascade spec.

---

### Pitfall 9: Sticky Never Resets — Session Permanently on 122B

**What goes wrong:**
The design document (`.planning/docs/35b-selfrouting.md` §16) says: `if session.current_model == "122b": return "122b"`. There is no explicit reset path. A short Hermes session that triggers one compiler debug call is now permanently 122B for that session ID. If Hermes reuses session IDs across topically-distinct conversations (or the session TTL is too long), the user's "write me a simple function" request routes to 122B unnecessarily for hours.

**Why it happens:**
Sticky escalation optimizes for debugging continuity (never lose 122B mid-debug). It does not optimize for returning to 35B after the debug is done. That's the correct design per `.planning/docs/35b-selfrouting.md` §16, but it requires the TTL to be the reset mechanism rather than an explicit "session done" signal.

**How to avoid:**
1. **TTL is the reset** — Sessions expire after `SessionTtlMinutes` (recommended default: 30 minutes) of inactivity. A fresh agent run (new session ID or TTL-expired old session) starts clean. This is the correct mechanism.
2. **Document that reset = session expiry** — Operators must understand that "start a new agent run" resets sticky. Add to README.
3. **Do NOT add a "downgrade to 35B" in the cascade** — Once a session is sticky-to-122B, downgrading within the session would break debugging continuity. The TTL is the only reset path.

**Warning signs:**
- `/stats` `session_store_entry_count` stays high even when no active Hermes sessions are open (TTL not evicting properly).
- User reports "simple requests are slow" across multiple distinct topics (session was never evicted between topics).

**Phase to address:**
Sticky Session / Session Store phase. TTL default must be set in the phase plan.

---

### Pitfall 10: 35B Self-Classify Blocking the 35B KV Cache / Slot

**What goes wrong:**
The self-classify call hits the same mlx_lm.server as the actual 35B response. The classify call uses `max_tokens=4-8` and is fast (~10ms), but it occupies a server slot while running. If mlx_lm.server serializes requests (it typically does for a single model process), a heavy in-flight 35B generation (user A's long code response) blocks the routing classify call for user B. User B's routing classify latency balloons from 10ms to 10ms + user-A-generation-time (potentially 2-3s). This adds latency to the routing decision itself.

**Why it happens:**
mlx_lm.server is a single-process server. If it serializes requests at the framework level (common for local servers), the routing classify call queues behind ongoing generations.

**How to avoid:**
1. **Quantify before optimizing** — Measure actual classify latency in production. If classify latency is consistently <100ms including the occasional wait, it's fine. The impact is: classify adds latency before the actual 35B response starts, but the user only perceives total latency. If total = classify_wait + generation = 2s + 3s, that's the same as if classify were instant (just generation 3s + 2s classify serial).
2. **Fire-and-continue optimization (speculative)** — The design doc §17 names "Speculative Routing": start 35B generation immediately, cancel and reroute to 122B if complexity detected mid-stream. This avoids classify blocking entirely but adds implementation complexity. Mark as v2.1 candidate.
3. **SemaphoreSlim on 35B is NOT the issue** — v1.x has `SemaphoreSlim(1)` on 122B only. 35B bypasses the queue. So the classify call does not consume the 122B semaphore slot. The concurrency concern is only about mlx_lm.server internal serialization, not the router gate.

**Warning signs:**
- DecisionLog: `self_router_classify_latency_ms` values >500ms (add this field to TraceRecord for the self-router call).
- Operational pattern: classify latency spikes correlate with long 35B generation times for other requests.

**Phase to address:**
Self-Router phase. Add `self_router_classify_latency_ms` to TraceRecord in the wiring plan. Document the concurrency model (no semaphore on 35B) explicitly.

---

### Pitfall 11: ML Code Dormant Drift — Routing.Mode Switch Breaks After v2.0

**What goes wrong:**
v2.0 makes ML dormant in the routing path (mirrors Phase 12 heuristic retirement). `MlNetClassifier.fs`, `BgeM3Embedder.fs`, `RetrainingService.fs`, etc., stay compiled but are not exercised at runtime. Over v2.x phases, if any of these files are touched for unrelated reasons (housekeeping, NuGet bumps, type changes in `Domain.fs`), the dormant ML code can drift out of sync with the active code. When an operator later tries to re-activate via `Routing.Mode = "ml"`, the switch breaks because the dormant code was never tested against v2.x types.

**Why it happens:**
This is the exact pattern that happened with heuristic code before Phase 12 retired it formally. Dormant code doesn't get the benefit of the compiler's exhaustive-match checks on new DU cases, integration test coverage, or router-start validation. The F# compiler only checks dormant code when it compiles — not when it runs.

**How to avoid:**
1. **Add one integration test that exercises the ML path via `Routing.Mode = "ml"`** — This test would be identical to a v1.3 routing test but run under a "ml mode" config. It catches `Domain.fs` DU additions (new `RoutingReason` cases) that would break `formatReason` exhaustive match in the dormant path.
2. **Run the dormant test in CI (even if ignored in prod)** — Mark the test with `[<Ignore "dormant-ml-path">]` and run it with `--include-tag dormant` in a weekly CI job or pre-release gate.
3. **Document the dormancy contract in code** — `RoutingAlgorithm.fs` or `CompositionRoot.fs` should have a comment: "ML path is dormant as of v2.0. Re-activate via Routing.Mode = 'ml'. Dormant path test in MlDormantTests.fs."

**Warning signs:**
- `Domain.fs` `RoutingReason` DU gains a new case without a corresponding update to `formatReason` exhaustive match — compiler catches this, but only if the ML path compiled. Since the F# compiler always compiles all files, this is actually caught automatically. The real risk is runtime behavior: a new case added to `RoutingConfig` or `RouterRequest` that the ML adapter handles incorrectly without an integration test to catch it.
- Months go by without the `Routing.Mode = "ml"` code path being tested in any CI or manual run.

**Phase to address:**
This is a cross-cutting concern. The roadmap should include one plan in a later phase explicitly labeled "Dormant ML path test" that adds `MlDormantTests.fs` (or similar).

---

## Technical Debt Patterns

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|----------|-------------------|----------------|-----------------|
| In-memory session store only (no persistence) | Simpler implementation; no file locking | Sessions lost on restart; sticky resets after launchd restart | Acceptable for v2.0 single-user local setup |
| Single self-router prompt template, not versioned | Simpler operator UX | Behavior change on prompt edit with no audit trail | Never — always log prompt hash at startup |
| Sticky escalation never downgrades within session | Debugging continuity guaranteed | 122B overuse if session not expired | Acceptable; TTL is the counterbalance |
| Hard Rules keyword list in appsettings.json only | Easy to tune | Drift from UNSAFE examples in self-router prompt | Acceptable; document that both lists must be kept in sync |
| Self-classify via same 35B instance (not dedicated) | No extra server | Classify blocks on in-flight 35B generation | Acceptable for v2.0; speculative routing is v2.1 |

---

## Integration Gotchas

| Integration | Common Mistake | Correct Approach |
|-------------|----------------|------------------|
| Hermes `X-Session-Id` header | Treating absent header as `""` session key; all sessionless requests share one sticky bucket | Treat absent/empty `X-Session-Id` as "no session" — skip sticky read/write entirely |
| mlx_lm.server self-classify call | Routing classify call through `IUpstreamClient` / `QueueDispatcher` — consumes SemaphoreSlim(1) gate | Separate named HttpClient "self-router" (same pattern as "judge" in Phase 16) — bypass queue entirely |
| mlx_lm.server model field in classify body | Sending HF model ID instead of local path — triggers HF-id trap from Phase 1 | Reuse `tryParseModelId` from `QwenUpstreamClient.fs` for the self-classify request body (same fix as v1.x upstream calls) |
| Self-classify response parsing | `content = "SAFE"` equality check breaks on multi-token or prefixed responses | `content.Contains("SAFE")` + `content.Contains("UNSAFE")` + `UNSAFE` wins on ambiguity |
| Session ID from Hermes header | Forwarding session_id to 35B/122B upstream in the classify request body | Session ID is router-internal; never forward in the upstream POST body; it's only used for sticky store lookup |

---

## Performance Traps

| Trap | Symptoms | Prevention | When It Breaks |
|------|----------|------------|----------------|
| Self-classify caching by prompt_hash only | Same prompt in two contexts (SAFE in one, UNSAFE in other) hits cache; wrong routing on second | Cache by `(session_id, prompt_hash)` if using per-session classify cache. Or: don't cache — classify latency (~10ms) is cheap enough | Immediately if continuation prompts repeat across sessions |
| Session store O(n) eviction scan at TTL check | Eviction loop takes >1s when store has 100k entries | Eviction loop walks all entries — O(n) acceptable at n<10,000; add `MaxEntries` cap if store grows large | Not an issue at expected scale (<1000 active sessions) |
| Classify call on every streaming request | Streaming requests shouldn't incur classify round-trip since quality fallback doesn't apply to streaming | In streaming branch, still run Hard Rules + sticky check. Self-classify for streaming is debatable: it adds latency before the first chunk. Consider skipping self-classify for stream=true and using only Hard Rules + sticky | Noticeable when classify call takes >200ms |
| TraceRecord growing with 3+ new self-router fields | TraceLog JSONL rows get larger; `logs/trace/` fills faster | Fields are small (string option + float option); negligible disk impact at expected volume | Not an issue at operator scale |

---

## "Looks Done But Isn't" Checklist

- [ ] **Hard Rules keyword list**: Verify keywords match between `appsettings.json` (HardRules section) and `prompts/self-router-prompt.md` UNSAFE examples — these two lists must be in sync.
- [ ] **Self-classify parser**: Verify `UNSAFE` wins when both `SAFE` and `UNSAFE` appear in the response (parse-failure safety bias).
- [ ] **Session store null-header guard**: Verify absent `X-Session-Id` does NOT create a session entry — test with a request that has no session header and confirm `/stats session_store_entry_count` does not increment.
- [ ] **Sticky cascade ordering**: Verify Hard Rules fire before sticky check — confirm a "LLVM" prompt routes to 122B even when session is sticky-to-35B.
- [ ] **Hermes backward compat**: Verify v1.x client (no `X-Session-Id`) works identically to v1.3 routing — no regression in `routing_reason` distribution.
- [ ] **Session TTL eviction**: Confirm session store entries are evicted after TTL; verify `/stats session_store_entry_count` decreases after idle period.
- [ ] **ML dormant path compiles**: `dotnet build` succeeds with all ML code present even when `Routing.Mode = "selfrouting"` — ensure no compilation-level dormant drift.
- [ ] **Self-router prompt hash logged**: Startup log contains `SelfRouter: loaded prompt hash={Hash}` — verify operator can detect prompt changes.
- [ ] **SemaphoreSlim NOT consumed**: Confirm self-classify HTTP call uses a named `"self-router"` HttpClient (not IUpstreamClient / QueueDispatcher) — grep `QueueDispatcher` in the self-router wiring; it must NOT appear.
- [ ] **HF-id trap**: The self-classify request body `model` field must use local path format — verify via `"self_router_classify_model"` log field or trace field.

---

## Recovery Strategies

| Pitfall | Recovery Cost | Recovery Steps |
|---------|---------------|----------------|
| 35B overconfidence causing quality regression | LOW | Set `HardRules.Enabled=true` + expand keyword list in appsettings.json; no deploy needed; restart router |
| Prompt template drift causing wrong routing | LOW | `git diff prompts/self-router-prompt.md`; revert or fix the prompt; restart router; compare prompt hash in startup log |
| Session store stuck on wrong model | LOW | Router restart clears all sessions (in-memory store); brief quality disruption; sessions re-establish from scratch |
| Cascade ordering bug routing wrong model | MEDIUM | Fix cascade ordering in `ChatCompletions.fs`; full deploy needed; test with known Hard Rule trigger prompt |
| ML dormant code broken after DU change | MEDIUM | Reactivate ML path via `Routing.Mode = "ml"` fails with runtime error; fix Domain.fs DU handling in ML adapter; add dormant integration test |

---

## Pitfall-to-Phase Mapping

| Pitfall | Prevention Phase | Verification |
|---------|------------------|--------------|
| 35B overconfidence on continuation prompts (#1) | Hard Rules + Self-Router prompt | Smoke test: "fix this" prompt routes to UNSAFE when session sticky |
| Prompt template drift (#2) | Self-Router wiring | Startup log shows prompt hash; `/stats` includes `self_router_prompt_hash` |
| Format drift / unparseable classify response (#3) | Self-Router parser + unit tests | Unit test: `SAFE` + `UNSAFE` in same response → `UNSAFE`; neither → `UNSAFE` |
| Session store race condition (#4) | Sticky Session / Session Store | Integration test: concurrent requests on same session_id; 122B write not lost |
| Session lost on restart (#5) | Sticky Session (documentation) | README §5 states in-memory semantics explicitly |
| Session store unbounded growth (#6) | Sticky Session TTL eviction | `/stats session_store_entry_count` stabilizes; eviction loop tested with fake PeriodicTimer |
| Hermes backward-compat / null session (#7) | Hermes Integration | Integration test: request with no X-Session-Id → no session entry created |
| Cascade ordering bug (#8) | Hard Rules phase (cascade spec) | Tests assert explicit ordering: Hard Rule > sticky > self-classify |
| Sticky never resets (#9) | Sticky Session TTL | TTL eviction test; README documents reset = session expiry |
| Self-classify blocking KV cache slot (#10) | Self-Router wiring | TraceRecord `self_router_classify_latency_ms` field; monitor for latency spikes |
| ML dormant code drift (#11) | Post-v2.0 dormant test plan | `MlDormantTests.fs` runs in CI; no new DU case passes without updating dormant path |

---

## Sources

- `.planning/docs/35b-selfrouting.md` §7,10,16 — "router thinking too deeply" failure mode; overconfidence; sticky escalation design
- `.planning/docs/35b-selfrouting-prompt.md` §6,7,13 — UNSAFE keywords critical; continuation-aware routing; overconfidence prevention; SAFE-not-SIMPLE framing
- `.planning/milestones/v1.3-phases/16-122b-as-judge-for-borderline-cases/16-RESEARCH.md` — Named HttpClient pitfalls; LRU cache TOCTOU; judge timeout too long (same class as self-classify timeout); fail-open bias
- `.planning/milestones/v1.3-phases/16-122b-as-judge-for-borderline-cases/16-SUMMARY.md` — `AddHttpClient` 2-arg form silently drops BaseAddress in F#; Expecto rootTests explicit list
- `.planning/milestones/v1.3-phases/14-quality-fallback-and-trace/14-CONTEXT.md` — Streaming branch intentionally skipped; quality fallback non-streaming only (same applies to self-classify for streaming)
- `src/SmartRouter.Cli/Adapters/JudgeClient.fs` — Working implementation of named HttpClient pattern, fail-open verdicts, parse-failure handling (template for self-router client)
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — Current cascade structure (explicit model override → task → ML → quality fallback → judge); v2.0 extends this ordering
- `src/SmartRouter.Core/Domain.fs` — `RoutingReason` DU exhaustive match constraint; `task {}` invariant; ML dormant risk surface

---

*Pitfalls research for: smart-router v2.0 Self-Routing + Session-Aware*
*Researched: 2026-05-11*
