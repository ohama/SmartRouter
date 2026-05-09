---
phase: 11-deployment-documentation
type: context
locked: true
created: 2026-05-09
source: 11-RESEARCH.md
---

# Phase 11 — Locked Architectural Decisions

These decisions are LOCKED before plan authoring. Plans cite this file when an
implementation choice references one of the items below. Do not revisit during
execution.

## Scope

Phase 11 is the FINAL pre-v1.0 phase. It is purely additive:

- One new endpoint file (`Endpoints/Models.fs`)
- One new test file (`tests/SmartRouter.Tests/ModelsTests.fs`)
- One new launchd plist (`deploy/com.ohama.smart-router.plist`)
- Two small bash deploy scripts (`scripts/deploy.sh`, `scripts/install-launchd.sh`)
- One root README (`README.md`)
- Two file-list edits (`SmartRouter.Cli.fsproj`, `SmartRouter.Tests.fsproj`)
- Three Program.fs lines (one `Endpoints.Models.mapEndpoints app` insertion)
- One RouterTests.fs line (append `ModelsTests.tests` to `rootTests`)

NO Core changes. NO new domain types. NO new ports. NO new DI registrations
(reuses existing `health-probe` named HttpClient + `IHealthProbe` +
`IOptions<UpstreamOptions>`). Pure-Core invariant (ARCH-01) is preserved.

## Locked Decisions

### L1 — launchd plist filename
`com.ohama.smart-router.plist` (per ROADMAP success criterion #1 verbatim).

### L2 — launchd plist install path
`~/Library/LaunchAgents/com.ohama.smart-router.plist` (LaunchAgent, runs as
the operator user — not a system daemon).

### L3 — dotnet absolute path
`/opt/homebrew/bin/dotnet` (operator host; confirmed by `which dotnet`).

### L4 — dotnet publish strategy
Framework-dependent: `dotnet publish -c Release -o ~/llm-system/services/smart-router/`.
NOT self-contained. NOT `PublishTrimmed=true` (ML.NET reflection breaks under
trimming).

### L5 — Binary install path
`~/llm-system/services/smart-router/` (matches operator's qwen36-35b /
qwen122b service convention).

### L6 — launchd WorkingDirectory
`/Users/ohama/llm-system/services/smart-router` (set in plist's
`WorkingDirectory` key; relative paths in `appsettings.json` resolve from here).

### L7 — launchd log paths
- StandardOutPath: `/Users/ohama/llm-system/services/logs/smart-router.log`
- StandardErrorPath: `/Users/ohama/llm-system/services/logs/smart-router.err`

(Mirrors qwen36-35b.log / 122b.log naming convention.)

### L8 — launchd KeepAlive
`<true/>` plain bool. Mirrors both qwen plists. NOT the dictionary form.

### L9 — launchd ThrottleInterval
`<integer>30</integer>`. Mirrors both qwen plists.

### L10 — launchd RunAtLoad
`<true/>`. Mirrors all three operator plists.

### L11 — /v1/models response when both upstreams down
HTTP 200 + `{"object":"list","data":[]}`. Graceful degradation. 503 is
RESERVED for actual router-side server errors.

### L12 — /v1/models dedupe key
`id` field only. First-seen wins. NOT `(id, owned_by)` tuple — that is
unnecessary complexity for v1.

### L13 — /v1/models HttpClient
Reuse existing `health-probe` named client (5s timeout, no retry, no
BaseAddress). Pass full URL per request: `client.GetAsync(baseUrl + "/v1/models", ct)`.
NO new named client.

### L14 — JsonElement.Clone() requirement (CRITICAL)
Every `JsonElement` returned from the /v1/models aggregation MUST be cloned
before its owning `JsonDocument`'s `use doc` scope exits. Without `.Clone()`,
the JsonElements become invalid memory references after disposal — silent
data-corruption bug. Plan 11-01 calls this out explicitly in the task action
and the verify step greps for `entry.Clone()` in the source.

### L15 — README location
Repo root: `/Users/ohama/projs/smart-router/README.md`.

### L16 — README sub-docs deferred
`documentation/operations/launchd-setup.md`, `canary-workflow.md`, etc. are
DEFERRED to v2. v1 ships README-only per ROADMAP SC#4 (one operator-facing doc).

### L17 — No new domain types or ports
Phase 11 is purely additive. NO `Domain.fs` / `RoutingPorts.fs` /
`RetrainingPorts.fs` / `CanaryPorts.fs` / `MLPorts.fs` changes. Pure-Core
invariant is preserved.

### L18 — Wave structure
- Wave 1: 11-01 (API + tests for /v1/models) — `depends_on: []`
- Wave 2: 11-02 (Ops plist + scripts) AND 11-03 (README) — `depends_on: [11-01]`

11-02 and 11-03 are FILE-DISJOINT (deploy/* + scripts/* vs README.md), do not
import each other's outputs, and can run in parallel within Wave 2. Both
depend on 11-01 because both REFERENCE the /v1/models endpoint shipped by
11-01 (plist health probe semantics + README endpoint table).

### L19 — No new tests in 11-02 / 11-03
Plan 11-01 adds 3 new tests (one per scenario: both-up + dedupe; one-down +
graceful; both-down + empty). Plans 11-02 and 11-03 add ZERO tests — they are
pure operations + documentation work.

### L20 — Test count baseline
Pre-Phase 11: 83 pass + 17 ignored without embeddings; 90 + 10 with
embeddings. Post-Plan 11-01 target: 86 pass + 17 ignored / 93 + 10 with
embeddings.

### L21 — Manual UAT for SC#1 + SC#2
ROADMAP success criteria #1 and #2 (`launchctl load -w` and kill -9 restart)
require the actual operator host. They are NOT executed by Plan 11-02 — that
plan SHIPS the plist + scripts and DOCUMENTS the manual UAT steps. The
operator runs them after Phase 11 completes.

## Goal-Backward Anchors

Phase goal (from ROADMAP):
> The router auto-starts under launchd supervision, the /v1/models endpoint
> proxies both upstream model lists, and the README gives the operator
> everything needed to tune, debug, and connect both clients.

Observable truths (operator perspective):
1. `curl http://127.0.0.1:4000/v1/models` returns deduplicated entries from
   both upstreams when both are up.
2. `curl http://127.0.0.1:4000/v1/models` returns a usable list (one
   upstream's models) when one upstream is down.
3. `curl http://127.0.0.1:4000/v1/models` returns 200 + empty data array
   when both upstreams are down.
4. `cp deploy/com.ohama.smart-router.plist ~/Library/LaunchAgents/ &&
   launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist`
   starts the router; `curl http://127.0.0.1:4000/health` succeeds without
   running `dotnet run`.
5. `kill -9 $(pgrep -f SmartRouter.dll)` followed by ~30s wait shows the
   router back up at `/health`.
6. A new operator reading README.md alone can: switch routing algorithm,
   tune the heuristic threshold/keywords, interpret DecisionLog, connect
   Hermes, connect Graphify, run the canary workflow, and follow the launchd
   restart procedure — without asking for clarification.

## File Ownership Map (no overlap → safe parallelism in Wave 2)

| Plan  | Files (created/edited) |
|-------|------------------------|
| 11-01 | `src/SmartRouter.Cli/Endpoints/Models.fs` (NEW), `src/SmartRouter.Cli/Program.fs` (1-line edit), `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` (1-line edit), `tests/SmartRouter.Tests/ModelsTests.fs` (NEW), `tests/SmartRouter.Tests/RouterTests.fs` (1-line edit), `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` (1-line edit) |
| 11-02 | `deploy/com.ohama.smart-router.plist` (NEW), `scripts/deploy.sh` (NEW), `scripts/install-launchd.sh` (NEW) |
| 11-03 | `README.md` (NEW) |

11-02 and 11-03 share NO files. Safe to run parallel in Wave 2.

## Pitfalls Already Locked (do not re-research)

- **PATH not inherited by launchd**: must use absolute `/opt/homebrew/bin/dotnet`.
- **WorkingDirectory must pre-exist**: `scripts/deploy.sh` `mkdir -p`s it.
- **StandardOutPath directory must pre-exist**: `~/llm-system/services/logs/`
  already exists on operator host (used by qwen plists).
- **Gatekeeper quarantine**: README troubleshooting section documents
  `xattr -dr com.apple.quarantine ~/llm-system/services/smart-router/`.
- **launchctl load vs bootstrap**: README documents both forms; v1 uses
  `launchctl load -w` (matches operator's existing qwen practice).

## Sources

- `.planning/phases/11-deployment-documentation/11-RESEARCH.md` (full research, HIGH confidence)
- Operator's `~/Library/LaunchAgents/com.ohama.qwen36-35b.plist` (read verbatim)
- Operator's `~/Library/LaunchAgents/com.ohama.qwen122b.plist` (read verbatim)
- `src/SmartRouter.Cli/Endpoints/Health.fs` (endpoint shape)
- `src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` (`tryParseModelId`,
  `UpstreamOptions`)
- `src/SmartRouter.Cli/Adapters/HealthService.fs` (`probeOne` HTTP client
  usage)
- `tests/SmartRouter.Tests/HealthFallbackTests.fs` (`startFakeUpstream`
  helper, `startApp` pattern)
