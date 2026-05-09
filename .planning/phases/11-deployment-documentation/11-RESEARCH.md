# Phase 11: Deployment + Documentation — Research

**Researched:** 2026-05-09
**Domain:** launchd plist authoring, OpenAI /v1/models aggregation, F# ASP.NET endpoint pattern, README structure
**Confidence:** HIGH

---

## Executive Summary

- **What to build:** Three deliverables — (1) `Endpoints/Models.fs` adding GET /v1/models as the 5th endpoint, (2) `com.ohama.smart-router.plist` wired for launchd auto-start + restart, (3) a complete `README.md`. No new Core domain logic; no new ports; no new DI services beyond one `IModelsAggregator` (or inlined handler).
- **Key APIs:** `IHttpClientFactory` (existing "health-probe" named client — reuse), `IHealthProbe.IsReachable` (existing, Phase 10), `System.Text.Json.JsonDocument` for passthrough merging, `app.MapGet("/v1/models", ...)` in Program.fs, `dotnet publish` framework-dependent, `launchctl load -w`.
- **Key risks:** (1) launchd PATH restriction — OPS-02 mandates absolute dotnet path `/opt/homebrew/bin/dotnet`; (2) Working-directory-relative paths in appsettings.json require launchd `WorkingDirectory` key set exactly to the deploy root; (3) ML.NET reflection breaks `PublishTrimmed=true` — must use framework-dependent OR single-file with trimming disabled.
- **What already exists from earlier phases:** `probeModelIdAsync` + `tryParseModelId` in `QwenUpstreamClient.fs` contain the full JSON parsing machinery for `/v1/models` responses; `HealthService.probeOne` already calls `baseUrl + "/v1/models"` on the "health-probe" named client (5s timeout, no BaseAddress); `startFakeUpstream` helper in `HealthFallbackTests.fs` provides the integration-test pattern for fake upstreams; all four endpoint files (`Health.fs`, `Canary.fs`, `Stats.fs`, `ChatCompletions.fs`) provide the exact F# shape to mirror.
- **Primary recommendation:** Inline the aggregation logic directly in `Endpoints/Models.fs` (no separate port/adapter) — the operation is read-only, stateless, 20 lines, and does not benefit from the port-adapter indirection that Phase 10 used for HealthService.

---

## Section 1: launchd Plist for .NET 10 Service

### Operator convention — observed from the three existing plists

All three operator plists (`com.ohama.qwen36-35b.plist`, `com.ohama.qwen122b.plist`, `com.ohama.hermes-for-web.plist`) share:

| Key | Convention |
|-----|------------|
| XML declaration | `<?xml version="1.0" encoding="UTF-8"?>` + Apple DTD header |
| Outer shape | `<plist version="1.0"><dict>...</dict></plist>` |
| `Label` | matches filename stem: `com.ohama.XXX` |
| `ProgramArguments` | `<array>` of `<string>` elements; **absolute paths only** |
| `RunAtLoad` | `<true/>` on all three |
| `KeepAlive` | `<true/>` on all three — plain bool, **not** a dictionary form |
| `ThrottleInterval` | `<integer>30</integer>` on both Qwen plists; `<integer>10</integer>` on hermes |
| `StandardOutPath` | qwen: `~/llm-system/services/logs/{name}.log`; hermes: `~/.local/state/launchd-logs/...` |
| `StandardErrorPath` | qwen: `~/llm-system/services/logs/{name}.err`; hermes: `~/.local/state/launchd-logs/...` |
| `WorkingDirectory` | qwen: `/Users/ohama/llm-system` (not a per-service subdir); hermes: `/Users/ohama/.hermes/hermes-agent` |
| `EnvironmentVariables` | always present; at minimum includes `PATH` with fully-qualified bin dirs |

**Key distinction between qwen and hermes:**
- qwen plists: 4-space indentation throughout; keys in natural (definition) order
- hermes plist: tab indentation; keys in **alphabetical order** (Xcode/PlistBuddy output)

Smart-router plist should follow the **qwen style** (4-space, natural order) since it lives in the same `llm-system` ecosystem.

### Smart-router plist — locked decisions

| Item | Decision | Source |
|------|----------|--------|
| Filename | `com.ohama.smart-router.plist` | ROADMAP success criterion #1 |
| dotnet path | `/opt/homebrew/bin/dotnet` | `which dotnet` on operator host |
| dotnet version | 10.0.203 | `dotnet --version` on operator host |
| Deploy binary | `~/llm-system/services/smart-router/SmartRouter.dll` | operator convention |
| WorkingDirectory | `/Users/ohama/llm-system/services/smart-router` | all relative paths in appsettings.json resolve from here |
| Log path (out) | `/Users/ohama/llm-system/services/logs/smart-router.log` | mirrors `36-35b.log` / `122b.log` naming |
| Log path (err) | `/Users/ohama/llm-system/services/logs/smart-router.err` | mirrors `36-35b.err` / `122b.err` naming |
| KeepAlive | `<true/>` plain bool | matches both qwen plists |
| ThrottleInterval | `<integer>30</integer>` | matches qwen plists (mlx_lm is also a server process) |
| RunAtLoad | `<true/>` | matches all three operator plists |

### EnvironmentVariables for .NET service

launchd does not inherit the user's shell PATH. Required env vars:

```
PATH = /opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin
ASPNETCORE_ENVIRONMENT = Production
HOME = /Users/ohama
```

`HOME` is needed because .NET runtime may read `~/.nuget` or `~/.dotnet` for telemetry opt-out. The qwen plists do not include `HOME` (Python doesn't need it), but the hermes plist does — include it for .NET.

`DOTNET_ROOT` is not needed when using the absolute `/opt/homebrew/bin/dotnet` path — the runtime resolves from the executable's own directory.

### Complete plist template (locked)

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.ohama.smart-router</string>
    <key>ProgramArguments</key>
    <array>
        <string>/opt/homebrew/bin/dotnet</string>
        <string>/Users/ohama/llm-system/services/smart-router/SmartRouter.dll</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>ThrottleInterval</key>
    <integer>30</integer>
    <key>StandardOutPath</key>
    <string>/Users/ohama/llm-system/services/logs/smart-router.log</string>
    <key>StandardErrorPath</key>
    <string>/Users/ohama/llm-system/services/logs/smart-router.err</string>
    <key>WorkingDirectory</key>
    <string>/Users/ohama/llm-system/services/smart-router</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>ASPNETCORE_ENVIRONMENT</key>
        <string>Production</string>
        <key>HOME</key>
        <string>/Users/ohama</string>
        <key>PATH</key>
        <string>/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin</string>
    </dict>
</dict>
</plist>
```

### Pitfalls — launchd

1. **PATH not inherited.** `dotnet` will not resolve without an explicit absolute path. OPS-02 captures this; the plist above uses `/opt/homebrew/bin/dotnet`.
2. **ThrottleInterval prevents tight crash loops.** 30s matches the qwen plists. With `KeepAlive=true`, if smart-router exits within 30s of launch (e.g., missing dll), launchd waits 30s before restarting. This gives the operator time to diagnose.
3. **WorkingDirectory must exist before first load.** launchd will fail silently (exit code 78) if `WorkingDirectory` does not exist. The deploy setup script must `mkdir -p` the directory.
4. **StandardOutPath directory must exist.** `~/llm-system/services/logs/` must pre-exist — launchd will not create it. The operator's logs directory already exists (used by both qwen plists).
5. **Gatekeeper quarantine on downloaded binaries.** If `SmartRouter.dll` or any bundled `.so` arrives via web download rather than `dotnet publish` on-host, macOS may block execution. Fix: `xattr -dr com.apple.quarantine ~/llm-system/services/smart-router/`. Document in README troubleshooting.
6. **launchctl load vs launchctl bootstrap.** On macOS 14+ (Sonoma), `launchctl load` still works for user agents in `~/Library/LaunchAgents/` but Apple is deprecating it in favor of `launchctl bootstrap`. For v1, `launchctl load -w` matches what the operator already uses for the qwen plists — keep it consistent. README should note both forms.

---

## Section 2: /v1/models Endpoint Shape

### OpenAI /v1/models response format

```json
{
  "object": "list",
  "data": [
    {
      "id": "/Users/ohama/llm-system/models/qwen36-35b",
      "object": "model",
      "created": 1715000000,
      "owned_by": "local"
    }
  ]
}
```

The `id` field is what matters — it is the value clients send in the `model` field of POST /v1/chat/completions. For mlx_lm.server, the canonical id is the local filesystem path (the HF-id trap defense in `QwenUpstreamClient.tryParseModelId` prefers path-like ids exactly for this reason).

### Aggregation logic — locked decisions

| Decision | Locked Value | Rationale |
|----------|-------------|-----------|
| Fetch strategy | `Task.WhenAll` parallel | minimize latency; neither upstream blocks the other |
| Health pre-check | `IHealthProbe.IsReachable` before fetching | avoid burning HTTP timeout on known-down upstreams |
| Error on one-down | return models from the reachable upstream | graceful degradation; client still gets a usable model list |
| Error on both-down | return 200 + `{"object":"list","data":[]}` | router is healthy; it's the upstreams that are down; 503 would imply the router itself is broken |
| Dedupe key | `id` field only | simplest; matches how Hermes/Graphify select models; `(id, owned_by)` tuple is over-engineering for two local models |
| Caching | none for v1 | /v1/models is cold path; direct passthrough is correct; add caching in v2 if needed |
| Loopback-only | yes (inherits Kestrel binding `127.0.0.1:4000`) | matches /health, /stats, /canary convention |

### HTTP client selection

**Use the existing "health-probe" named client.** It already:
- has a 5-second timeout (appropriate for a metadata call to local upstreams)
- has no retry (correct — if /v1/models fails once, don't retry; skip that upstream)
- has no BaseAddress configured

Since "health-probe" has no BaseAddress, pass the full URL directly: `client.GetAsync(baseUrl + "/v1/models", ct)` — exactly what `HealthService.probeOne` already does. No need to add a 12th named client.

**Do NOT use "upstream35b" or "upstream35b-stream".** Those have 300s timeouts and retry policies — overkill for a metadata call and would tie up the retry infrastructure.

---

## Section 3: /v1/models Implementation in F#

### File: `src/SmartRouter.Cli/Endpoints/Models.fs` (NEW)

**Shape mirrors `Endpoints/Health.fs` exactly:**
- Module declaration
- `open` block (same set as Health.fs + `System.Text.Json`)
- `let mapEndpoints (app: WebApplication) =` function
- Single `app.MapGet("/v1/models", Func<HttpContext, Task>(fun ctx -> task { ... })) |> ignore`

### JSON merging approach

`QwenUpstreamClient.tryParseModelId` already demonstrates the `JsonDocument.Parse` → `doc.RootElement.TryGetProperty("data")` pattern. The Models endpoint needs a superset of that: iterate all entries in `data`, extract the full entry as a raw `JsonElement`, and emit them deduplicated.

**Approach:** use `System.Text.Json.JsonDocument` to parse each upstream response; iterate the `data` array; collect entries into a `Dictionary<string, JsonElement>` keyed by `id` (first-seen wins for dedup); serialize the combined dict's values back out.

Alternatively: collect into a list of anonymous record `{| id: string; object: string; created: int64; owned_by: string |}` for cleaner serialization. This is slightly more code but produces predictable output.

**Recommended approach:** collect typed records. Parse each entry's fields explicitly; if `id` is missing or empty, skip the entry. Use `HashSet<string>` for dedup tracking.

### IHealthProbe usage pattern (from HealthService.fs)

```fsharp
// Existing pattern in Health.fs:
let probe = ctx.RequestServices.GetRequiredService<IHealthProbe>()
let r35  = probe.IsReachable(Qwen35B)
let r122 = probe.IsReachable(Qwen122B)
```

In Models.fs, use `IsReachable` as a pre-condition before fetching:

```fsharp
let probe = ctx.RequestServices.GetRequiredService<IHealthProbe>()
let shouldFetch35  = probe.IsReachable(Qwen35B)
let shouldFetch122 = probe.IsReachable(Qwen122B)
```

### IUpstreamOptions access

Models.fs needs the upstream base URLs. Resolution options:
- Option A: resolve `IOptions<UpstreamOptions>` from DI (already registered in CompositionRoot).
- Option B: resolve `IConfiguration` and read keys directly.

Use Option A — `IOptions<UpstreamOptions>` is already registered; `UpstreamOptions` is already open in the Cli namespace.

### Task.WhenAll pattern in F#

```fsharp
// Source: existing pattern in QueueDispatcher.fs / HealthService.fs
let! results = Task.WhenAll([| task1; task2 |])
```

For Models.fs, since each fetch is conditional, use a helper:

```fsharp
let fetchModels (client: HttpClient) (baseUrl: string) (ct: CancellationToken) : Task<Result<JsonElement list, unit>> =
    task {
        try
            use! resp = client.GetAsync(baseUrl + "/v1/models", ct)
            if not resp.IsSuccessStatusCode then return Error ()
            else
                let! json = resp.Content.ReadAsStringAsync(ct)
                use doc = JsonDocument.Parse(json)
                // extract data array...
                return Ok entries
        with _ -> return Error ()
    }
```

### Program.fs integration

Add one line after `SmartRouter.Cli.Endpoints.Health.mapEndpoints app`:

```fsharp
SmartRouter.Cli.Endpoints.Models.mapEndpoints app
```

No new DI registrations needed — `IHealthProbe` and `IOptions<UpstreamOptions>` are already registered; "health-probe" named client is already registered.

### No IModelsAggregator port needed

The aggregation is 20-30 lines of pure fetch+merge. Creating a port (interface) + adapter would require:
- A new Core port definition
- A new Cli adapter
- DI registration changes

For a read-only, stateless, loopback-only, non-testable-by-unit-test operation, the overhead is not justified. Test it via integration test using fake upstreams (same pattern as HealthFallbackTests.fs).

---

## Section 4: dotnet publish Strategy

### Decision: framework-dependent (locked)

| Factor | Framework-dependent | Self-contained |
|--------|--------------------|--------------------|
| Binary size | ~5MB DLL | ~80MB+ executable |
| .NET runtime required | yes (10.0.x on host) | no |
| ML.NET trimming risk | none (no trimming) | `PublishTrimmed=true` breaks ML.NET reflection; must set `false` |
| Deploy command | `dotnet publish -c Release -o ...` | add `-r osx-arm64 --self-contained` |
| Operator already has .NET 10 | yes (confirmed `dotnet --version` = 10.0.203) | N/A |

**Decision: framework-dependent.** Operator has .NET 10. Self-contained adds complexity and trimming risk for no benefit.

### Publish command (locked)

```bash
dotnet publish src/SmartRouter.Cli/SmartRouter.Cli.fsproj \
    -c Release \
    -o ~/llm-system/services/smart-router/
```

### Deploy directory structure

```
~/llm-system/services/smart-router/
├── SmartRouter.dll                          # main entry point
├── SmartRouter.runtimeconfig.json           # .NET 10 runtime config (auto-generated)
├── appsettings.json                         # operator-editable config
├── models/
│   ├── router.zip                           # ML.NET trained model
│   ├── router-canary.zip                    # canary model (created on promote)
│   ├── embed/
│   │   ├── bge-m3-int8.onnx                 # embedding model
│   │   └── sentencepiece.bpe.model          # tokenizer
├── datasets/
│   ├── hard-cases.jsonl                     # auto-populated by failure detector
│   ├── training-set.jsonl                   # auto-populated by retraining service
│   └── .last-retrain.json                   # retrain state (auto-created)
├── prompts/
│   └── teacher-prompt.md                    # editable teacher labeler prompt
└── logs/                                    # auto-created by router at startup
    └── decisions/                           # YYYY-MM-DD.jsonl decision logs
```

The `logs/` directory is `Directory.CreateDirectory`-d by `Program.fs` at startup (`System.IO.Directory.CreateDirectory("logs/decisions")`). The rest must pre-exist before first `launchctl load`.

---

## Section 5: Working Directory and Relative Path Handling

### How the router resolves paths

`Program.fs` calls `Directory.GetCurrentDirectory()` implicitly (via `SetBasePath`) for `appsettings.json`. All relative paths in appsettings.json (`models/router.zip`, `datasets/hard-cases.jsonl`, etc.) resolve relative to CWD at process startup.

`launchd` sets the CWD to `WorkingDirectory` from the plist **before** the process starts. So:

```
WorkingDirectory = /Users/ohama/llm-system/services/smart-router
```

means `models/router.zip` resolves to `/Users/ohama/llm-system/services/smart-router/models/router.zip`. This matches the deploy structure above.

### Pre-flight setup steps (operator must run once)

```bash
mkdir -p ~/llm-system/services/smart-router/{models/embed,datasets,prompts,logs/decisions}
# publish binary
dotnet publish src/SmartRouter.Cli/SmartRouter.Cli.fsproj -c Release -o ~/llm-system/services/smart-router/
# copy appsettings.json if not already there
cp src/SmartRouter.Cli/appsettings.json ~/llm-system/services/smart-router/
# copy prompt
cp prompts/teacher-prompt.md ~/llm-system/services/smart-router/prompts/
# copy models
cp models/embed/* ~/llm-system/services/smart-router/models/embed/
# router.zip either copied from training or auto-generated as dummy on first ML start
```

---

## Section 6: Test Strategy

### Test file: `tests/SmartRouter.Tests/ModelsTests.fs` (NEW)

**Pattern:** matches `HealthFallbackTests.fs` exactly — `startFakeUpstream` helper already defined there. Models tests will use the same helper (or inline a simpler version).

**Three tests required:**

1. **Both upstreams up, one shared id:** fake35b returns `{"object":"list","data":[{"id":"model-shared"},{"id":"model-35b-only"}]}`; fake122b returns `{"object":"list","data":[{"id":"model-shared"},{"id":"model-122b-only"}]}`; assert response data has 3 unique entries (deduplicated `model-shared`).

2. **One upstream down:** fake35b is unreachable (no server started); fake122b returns its list; assert response 200 + contains 122b's models only.

3. **Both upstreams down:** both unreachable; assert 200 + `{"object":"list","data":[]}`.

**Test infrastructure needed:**
- Spin up the full router app using fake-Kestrel pattern (same as HealthFallbackTests startApp helper) with fake upstream URLs injected via `AddInMemoryCollection`.
- `IHealthProbe` in test: since HealthService is a BackgroundService that polls real upstreams, the test needs to either (a) disable the health polling or (b) inject a stub `IHealthProbe`. Given both upstreams are fake, the simplest approach: always return `IsReachable = true` in the test app config, or configure `ConsecutiveFailureThreshold = 999` so the health probe never marks fakes as down during the test window.

Actually, simpler: since `IsReachable` defaults to `true` until the first probe cycle fails enough times (see `HealthService.state` init: `(true, DateTimeOffset.MinValue)`), and tests run fast (< polling interval), the "unreachable" test cases need a different mechanism. The cleanest option: mock `IHealthProbe` in the test DI by replacing the HealthService registration with a stub. Use the same `TryAddSingleton` + override pattern visible in CompositionRoot.

**Current test count:** 75 `test "..."` lines in test files. Expected after Phase 11: 75 + 3 = 78 test cases (plus unchanged ~9 ignored/pending cases).

**RouterTests.fs update:** append `SmartRouter.Tests.ModelsTests.tests` to `rootTests`. Add `<Compile Include="ModelsTests.fs" />` to `.fsproj` before `RouterTests.fs`.

---

## Section 7: README Structure

### Target audience

"A new operator who has never seen smart-router" (ROADMAP success criterion #4). Assumes: knows how to use a Mac terminal, knows what an LLM is, has run `mlx_lm.server` before.

### Locked section list and order

1. **What This Is** — 3-sentence elevator pitch: F# .NET 10 gateway, two Qwen models, routes based on task complexity
2. **Architecture** — ASCII art: `Hermes/Graphify → smart-router :4000 → qwen36-35b :8000 / qwen122b :8001`; heuristic vs ML mode; Loop A (real-time feedback) vs Loop B (retraining); canary deployment lane
3. **Requirements** — .NET 10 SDK, two mlx_lm.server instances running, macOS arm64
4. **Quickstart** — 8 steps: clone → `dotnet restore` → `dotnet run` → curl health → curl chat/completions → see decision log → `launchctl load` → verify restart
5. **Routing Pipeline** — heuristic (keyword + complexity threshold), ML (bge-m3 + ML.NET linear classifier, threshold 0.5), task table (all 7 types), how to switch via `Routing.Algorithm`
6. **ML Feedback Loop** — Loop A (DecisionLog → FailureDetector → TeacherLabeler → HardCaseDataset), Loop B (RetrainingService timer + count trigger), canary workflow (promote/rollback/auto-rollback), `AutoRollbackEnabled` gate
7. **Configuration Reference** — every key in appsettings.json explained (35 keys across 8 sections)
8. **Endpoints** — table: method, path, description, example curl, example response for all 8 endpoints (chat/completions, models, health, stats, canary GET/promote/rollback/enable)
9. **Debugging** — DecisionLog schema (all fields), `/stats` field meanings, `/health` field meanings, Serilog log levels
10. **Hermes Integration** — `base_url = http://localhost:4000/v1`, no `task` field needed, what routing applies
11. **Graphify Integration** — `task` field for all 7 types, `graph_indexing` 122B-always semantics, concurrency cap
12. **Operations** — launchd setup (full plist, load/unload commands), dotnet publish command, flip algorithm, canary promote/rollback workflow, seed-and-retrain, tuning `ConsecutiveFailureThreshold`
13. **Troubleshooting** — model_unavailable (mlx_lm not running), HF-id trap (wrong model id), fallback flapping (ConsecutiveFailureThreshold too low), canary auto-rollback cascade (threshold too aggressive)

**Location:** repo root `README.md`. Optional deep-dive docs under `documentation/operations/` for launchd-setup, canary-workflow, retrain-workflow (not required for Phase 11 success criteria — README alone satisfies SC#4).

**Estimated length:** 900–1200 lines of markdown. The 7 task types + 35 config keys + 8 endpoints add substantial table rows.

---

## Section 8: Anti-Patterns to Avoid

| Anti-pattern | Why It's Bad | What to Do Instead |
|--------------|-------------|-------------------|
| Add 12th named HttpClient "models-probe" | Bloats DI registration; "health-probe" already has identical semantics (5s, no retry, no BaseAddress) | Reuse "health-probe" with absolute URL per-call |
| Create IModelsAggregator port + adapter | Over-engineering for 20 lines of fetch+merge that has no Core domain value | Inline in Endpoints/Models.fs |
| Use `PublishTrimmed=true` | ML.NET uses reflection for model loading; trimming silently removes needed types | Framework-dependent publish; no trimming flag needed |
| Use `KeepAlive` dictionary form | Operator's existing plists use plain `<true/>` | Match the convention: `<key>KeepAlive</key><true/>` |
| Return HTTP 503 when both upstreams are down | Implies the router itself is broken; clients may stop retrying | Return 200 + empty `data` array |
| Three separate `AddSingleton<ModelsHandler>` calls | Creates three instances (the same pattern warned against in CompositionRoot comments) | Not applicable — no DI registration needed for inline handler |
| Use relative path in ProgramArguments | launchd does not expand `~`; relative paths depend on CWD | Absolute path `/opt/homebrew/bin/dotnet` and `/Users/ohama/llm-system/...` |

---

## Section 9: Open Questions for Operator / Planner

Only the following require human confirmation — everything else is locked by observation or codebase evidence:

1. **README sub-docs:** Phase 11 ROADMAP says "README" but the success criterion is comprehensive enough to warrant optional `documentation/operations/` sub-docs (launchd-setup.md, canary-workflow.md). Decision: README-only satisfies SC#4; sub-docs are bonus. Recommend README-only for Phase 11, sub-docs deferred.

2. **ThrottleInterval value:** qwen plists use 30s; hermes uses 10s. Smart-router is closer to qwen (always-on server). Locked at 30s unless operator prefers faster restart response during development.

3. **appsettings.json TeacherLabeler section:** `DatasetsDir` key exists but `Endpoint` currently points to `http://127.0.0.1:8001` (qwen122b). After Phase 11 deploy, `dotnet publish` copies `appsettings.json` as-is. Operator should verify this file before first `launchctl load` — no code change needed, just a documentation callout.

---

## Code Examples

### /v1/models endpoint skeleton (F# — mirrors Health.fs)

```fsharp
// Source: mirrors src/SmartRouter.Cli/Endpoints/Health.fs structure
module SmartRouter.Cli.Endpoints.Models

open System
open System.Collections.Generic
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports
open SmartRouter.Cli.Adapters.Json
open SmartRouter.Cli.Adapters.QwenUpstreamClient

let private fetchModels (client: HttpClient) (baseUrl: string) (ct) =
    task {
        try
            use! resp = client.GetAsync(baseUrl + "/v1/models", ct)
            if not resp.IsSuccessStatusCode then return []
            else
                let! json = resp.Content.ReadAsStringAsync(ct)
                use doc = JsonDocument.Parse(json)
                match doc.RootElement.TryGetProperty("data") with
                | true, data when data.ValueKind = JsonValueKind.Array ->
                    return
                        [ for i in 0 .. data.GetArrayLength() - 1 do
                              let entry = data.[i]
                              match entry.TryGetProperty("id") with
                              | true, idEl when idEl.ValueKind = JsonValueKind.String ->
                                  let id = idEl.GetString()
                                  if not (String.IsNullOrEmpty id) then
                                      yield entry.Clone()   // Clone: doc is disposed after use
                              | _ -> () ]
                | _ -> return []
        with _ -> return []
    }

let mapEndpoints (app: WebApplication) =
    app.MapGet("/v1/models", Func<HttpContext, Task>(fun ctx ->
        task {
            let probe   = ctx.RequestServices.GetRequiredService<IHealthProbe>()
            let opts    = ctx.RequestServices.GetRequiredService<IOptions<UpstreamOptions>>().Value
            let factory = ctx.RequestServices.GetRequiredService<IHttpClientFactory>()
            let client  = factory.CreateClient("health-probe")
            let ct      = ctx.RequestAborted

            let! models35  = if probe.IsReachable(Qwen35B)  then fetchModels client opts.Model35B  ct else Task.FromResult []
            let! models122 = if probe.IsReachable(Qwen122B) then fetchModels client opts.Model122B ct else Task.FromResult []

            let seen = HashSet<string>()
            let deduped =
                [ for entry in (models35 @ models122) do
                      match entry.TryGetProperty("id") with
                      | true, idEl ->
                          let id = idEl.GetString()
                          if seen.Add(id) then yield entry
                      | _ -> () ]

            let result = {| ``object`` = "list"; data = deduped |}
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsJsonAsync(result, jsonOptions, ct)
        } :> Task)) |> ignore
```

**Note on `JsonElement.Clone()`:** when using `JsonDocument.Parse(json)`, the `JsonElement` values are owned by the document. When `use doc` goes out of scope, the elements are invalid. `Clone()` copies the element into independently-owned memory. This is required here.

### launchctl commands (for README Operations section)

```bash
# Install
cp deployment/com.ohama.smart-router.plist ~/Library/LaunchAgents/
launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist

# Verify running
curl http://127.0.0.1:4000/health

# Simulate crash and verify restart (within 30s + ThrottleInterval)
kill -9 $(pgrep -f SmartRouter.dll)
sleep 35
curl http://127.0.0.1:4000/health   # should respond

# Unload / stop
launchctl unload ~/Library/LaunchAgents/com.ohama.smart-router.plist

# View logs
tail -f ~/llm-system/services/logs/smart-router.log
tail -f ~/llm-system/services/logs/smart-router.err
```

---

## Sources

### Primary (HIGH confidence — read directly)

- `/Users/ohama/Library/LaunchAgents/com.ohama.qwen36-35b.plist` — operator's 35B plist, read verbatim
- `/Users/ohama/Library/LaunchAgents/com.ohama.qwen122b.plist` — operator's 122B plist, read verbatim
- `/Users/ohama/Library/LaunchAgents/com.ohama.hermes-for-web.plist` — operator's hermes plist, read verbatim
- `src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` — `tryParseModelId`, `probeModelIdAsync` — JSON parsing machinery
- `src/SmartRouter.Cli/Adapters/HealthService.fs` — `probeOne` pattern using "health-probe" named client
- `src/SmartRouter.Cli/Endpoints/Health.fs` — endpoint shape to mirror
- `src/SmartRouter.Cli/Endpoints/Canary.fs` — multi-route endpoint shape
- `src/SmartRouter.Cli/Endpoints/Stats.fs` — typed wire record pattern
- `src/SmartRouter.Cli/CompositionRoot.fs` — all 11 named HttpClient registrations; DI patterns
- `src/SmartRouter.Cli/Program.fs` — endpoint registration sequence; startup sequence
- `src/SmartRouter.Cli/appsettings.json` — all relative paths in config
- `tests/SmartRouter.Tests/HealthFallbackTests.fs` — `startFakeUpstream` helper pattern
- `tests/SmartRouter.Tests/RouterTests.fs` — rootTests registration pattern
- `which dotnet` + `dotnet --version` — confirms `/opt/homebrew/bin/dotnet`, version 10.0.203

### Secondary (MEDIUM confidence — domain knowledge)

- OpenAI /v1/models response shape: `{"object":"list","data":[{"id":...,"object":"model","created":...,"owned_by":...}]}` — well-established; mlx_lm.server returns this exact shape (confirmed indirectly via `tryParseModelId` implementation and comments in `QwenUpstreamClient.fs`)
- `JsonElement.Clone()` requirement: standard System.Text.Json behavior — JsonDocument-owned elements are invalid after disposal
- launchd `KeepAlive=true` semantics: plain bool means "always restart on exit"; dictionary form allows conditional restart — operator convention uses plain bool consistently
- .NET 10 framework-dependent publish: produces `.dll` entry point requiring `dotnet SmartRouter.dll`; verified by presence of `SmartRouter.runtimeconfig.json` in publish output

---

## Metadata

**Confidence breakdown:**
- launchd plist shape: HIGH — read all three operator plists verbatim; exact format locked
- dotnet path: HIGH — `which dotnet` confirmed `/opt/homebrew/bin/dotnet` on operator host
- /v1/models implementation: HIGH — codebase has existing parsing machinery; endpoint pattern established across 4 existing endpoints
- README structure: HIGH — all required topics enumerated; locked against ROADMAP SC#4 exact wording
- Test count: MEDIUM — counted `test "` occurrences (75); may differ from runner-reported count by ±3 due to pending/ignored variants

**Research date:** 2026-05-09
**Valid until:** 2026-06-09 (stable domain; launchd plist format changes require macOS major version)
