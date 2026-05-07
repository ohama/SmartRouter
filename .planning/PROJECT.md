# Smart Router

## What This Is

An F# .NET 10 OpenAI-compatible LLM gateway that fronts two local Qwen models
on a Mac and auto-routes each request by prompt complexity. It serves
`http://localhost:4000/v1/chat/completions` and forwards to Qwen 35B
(`localhost:8000`) for simple work or Qwen 122B (`localhost:8001`) for complex
work. Built specifically so Hermes Agent (Nous Research) can target a single
endpoint and stop paying 122B latency on prompts the 35B can handle.

## Core Value

**Cut average response latency by routing simple requests to the fast 35B model
without losing quality on the requests that genuinely need 122B.**

When a tradeoff arises, latency wins over thoroughness: aggressively prefer 35B
unless the prompt strongly signals complexity. Misrouting a complex prompt to
35B produces a worse answer once; misrouting every simple prompt to 122B taxes
every interaction.

## Requirements

### Validated

<!-- Shipped and confirmed valuable. -->

(None yet — ship to validate)

### Active

<!-- Current scope. Building toward these. -->

- [ ] OpenAI-compatible `POST /v1/chat/completions` endpoint on `localhost:4000`
- [ ] Heuristic complexity classification (prompt length, keyword set, code-block detection, message count)
- [ ] Route simple → 35B (`localhost:8000`), complex → 122B (`localhost:8001`); aggressively prefer 35B
- [ ] Honor explicit `model` override in request body when value is `35b`/`122b` (or matching alias); otherwise route by complexity heuristic
- [ ] Pass-through SSE streaming when client sends `stream=true` (forward upstream chunks unchanged)
- [ ] Configurable routing rules via `appsettings.json` (threshold, keyword list, model URLs)
- [ ] Structured logging with per-request timing and routing-decision metadata (Serilog, stderr)
- [ ] Hexagonal architecture: pure Core (no HTTP / no logging / `task {}` only) + Cli adapters
- [ ] Extensibility seams for future providers (Claude / OpenAI / DeepSeek / Gemini) — interfaces in place, no implementations
- [ ] Stateless service (no static mutable state, DI throughout)
- [ ] Test pyramid via Expecto: unit (complexity scoring, keyword detection, routing decisions) + integration (fake upstream servers, end-to-end) + load (concurrent requests) + failure (timeout, malformed JSON, unavailable upstream)
- [ ] Mock OpenAI-compatible upstream for deterministic tests (controlled latency + failure simulation)
- [ ] launchd plist for daemonized operation (matches `com.ohama.qwen122b.plist` pattern)
- [ ] README with architecture, routing logic, threshold tuning, debugging, Hermes integration steps

### Out of Scope

<!-- Explicit boundaries. Includes reasoning to prevent re-adding. -->

- **Retry / circuit breaker** — defer to v2; add only if real failures observed in operation
- **Request queueing** — defer to v2; not needed at single-user / single-Hermes load profile
- **Rate limiting** — defer to v2; only one client (local Hermes Agent), no abuse vector
- **`/metrics` Prometheus endpoint** — defer to v2; Serilog timing logs cover v1 observability needs
- **`/health` probe endpoint** — defer to v2; launchd handles process supervision
- **ML / learned routing** — heuristics only for v1; revisit once we have logged routing data to train against
- **Concrete Claude / OpenAI / DeepSeek / Gemini provider implementations** — extension seams only, no live integrations
- **Windows support** — Mac-only, mirrors blueCode constraint (Unix path heuristic in `tryParseModelId`)
- **Auth / API keys on the router** — purely loopback (`localhost:4000`), single-user, no auth surface
- **Authoring a non-OpenAI client protocol** — strictly OpenAI chat-completions wire format on both sides
- **Persistence / session state** — router is stateless per request; conversation memory lives in Hermes

## Context

**Consumer.** The router fronts [Hermes Agent](https://github.com/NousResearch/hermes-agent),
configured through its `custom` provider plugin
(`plugins/model-providers/custom`). Hermes will set `base_url=http://localhost:4000/v1`
and call `chat.completions.create(stream=True, ...)` — pass-through SSE is
therefore a v1 must, not a nice-to-have.

**Why now.** Qwen 122B (`Qwen/Qwen3.5-122B-A10B-4bit` MoE) is the canonical
production model in the user's local LLM rig but it's slow — 240s cold-start,
RSS ~45 GB, generation latency dominates interactive sessions. Qwen 35B is
already running on `localhost:8000` as the standby/rollback service. Both
launchd plists already exist
(`~/Library/LaunchAgents/com.ohama.qwen{35b,122b}.plist`). The router unlocks
"keep both loaded, route between them" without forcing a per-call manual
choice.

**Companion project: blueCode** (`/Users/ohama/projs/blueCode`). Existing F#
.NET 10 hexagonal codebase that already speaks to both Qwen ports. We will
**copy adapters** from blueCode (decision logged below): `QwenHttpClient.fs`
(plus its HF-id parsing trap defenses), `Adapters/Json.fs`,
`Adapters/Logging.fs`. Core will be rewritten cleanly — blueCode's Core is an
agent loop, not a router, so its domain doesn't transfer. blueCode's hard-won
operational knowledge is also load-bearing context:

- mlx_lm.server HF-fallback trap: send the local path (not the HF id) in the
  `model` field of POST bodies, otherwise the server overwrites the loaded
  Instruct tokenizer with a Base Coder one and responses become FIM-mode
  garbage. `tryParseModelId` in blueCode's `QwenHttpClient.fs` resolves this by
  preferring `data[0]` entries that start with `/`.
- Both servers are launched with `--chat-template-args '{"enable_thinking": false}'`
  to suppress `<think>...</think>` tokens; the router doesn't need to do
  anything special, but should not assume thinking traces are absent if a
  future server change reverts this.
- HttpClient timeout: 300s (covers 122B cold-start up to 240s after
  `launchctl kickstart`).
- Sampling parameters Qwen 3.5 expects: `temperature=0.7, top_p=0.8, top_k=20,
  presence_penalty=0.0` (non-thinking coding defaults). The router passes
  client-supplied parameters through; only fills these as defaults when client
  omits them.

**Code-style invariants from blueCode (load-bearing).**
- Pure Core: no Serilog / Spectre / HTTP client references in `*.Core/**`
- `task {}` not `async {}` in Core (CI grep enforces in blueCode; we mirror
  the convention here)
- Serilog → stderr, application output → stdout
- Per-task atomic commits; `git add <file>` not `git add -A`
- Test discovery: explicit `rootTests` list pattern in the test entrypoint —
  Expecto auto-discovery is unreliable and burned multiple executors

## Constraints

- **Tech stack**: F# / .NET 10 / ASP.NET Core Minimal API / HttpClientFactory / System.Text.Json — fixed by the brief; `dotnet --version` must support net10.0
- **No Python** — explicit in brief
- **Test stack**: Expecto + ASP.NET Core TestServer — overrides brief's xUnit/FsUnit suggestion, matches blueCode for consistency and operator familiarity
- **Logging**: Serilog (stderr) — match blueCode; stream separation matters for tools that capture stdout
- **Stateless**: no static mutable state, DI throughout
- **Mac-only**: launchd plist deployment, Unix path conventions
- **Loopback-only**: binds to `localhost`, no public exposure, no auth required
- **Aggressive 35B preference**: when in doubt about complexity, route to 35B — false-cheap is worse than false-slow per Core Value

## Key Decisions

<!-- Decisions that constrain future work. -->

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Test framework: **Expecto** (override brief's xUnit/FsUnit) | Matches blueCode; user knows its quirks (`testSequenced`, explicit `rootTests` list, Console.SetOut races) | — Pending |
| Code reuse: **copy adapters** from blueCode (`QwenHttpClient.fs`, `Json.fs`, `Logging.fs`); rewrite Core cleanly | blueCode's Core is an agent loop — domain doesn't transfer. Adapters carry hard-won mlx_lm gotchas worth lifting verbatim. Avoids a cross-project shared library refactor. | — Pending |
| Streaming: **pass-through SSE from v1** | Hermes Agent calls `chat.completions.create(stream=True)` by default; rejecting or buffering breaks Hermes UX. Forwarding upstream chunks unchanged is the simplest correct option. | — Pending |
| `model` field handling: **honor explicit override (`35b`/`122b` aliases) + log decision** | Lets the operator force a target for debugging while keeping default behavior heuristic. Logging captures the data we'd need later to tune thresholds or train a learned router. | — Pending |
| Deployment: **launchd plist** (mirror `com.ohama.qwen122b.plist`) | Auto-start + supervision in the same shape as the upstream services; one operational pattern instead of two. | — Pending |
| v1 scope: **none of the "extra features"** (retry, circuit breaker, queueing, rate limit, /metrics, /health) | Single-user loopback; defer until real failure modes observed. Each adds surface area that has to be tested and maintained for hypothetical needs. | — Pending |
| Architecture: **hexagonal mirror of blueCode** (pure Core + Cli adapters, `task {}` only in Core) | Operator already maintains blueCode under these invariants; matching them keeps both projects on the same mental model and same CI patterns. | — Pending |
| Mac-only / loopback-only | Matches blueCode constraint and the actual deployment target; cuts auth, TLS, and cross-platform path handling out of v1 scope. | — Pending |

---
*Last updated: 2026-05-07 after initialization*
