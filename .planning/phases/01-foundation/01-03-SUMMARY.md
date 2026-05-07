---
phase: 01-foundation
plan: 03
subsystem: api
tags: [fsharp, aspnet, kestrel, serilog, httpclient, system-text-json, dependency-injection]

requires:
  - phase: 01-01
    provides: project scaffold, fsproj, solution file, Json.fs, Logging.fs stubs
  - phase: 01-02
    provides: Domain.fs, Routing.fs (routeRequest, canonicalTaskTable), Ports.fs (IUpstreamClient)

provides:
  - QwenUpstreamClient: IUpstreamClient implementation with HF-id probe, 300s timeout, sampling defaults, UnknownFields forwarding
  - ChatCompletions endpoint: POST /v1/chat/completions; 501 on stream=true; 400 on unknown task; OpenAI error envelope
  - CompositionRoot: named HttpClients, RoutingConfig DI singleton, IUpstreamClient DI singleton
  - Program.fs: Kestrel host on 127.0.0.1:4000; Serilog on stderr; validateConfig startup check
  - appsettings.json: full Phase 1 config (Upstreams, Routing.ComplexityThreshold/TimeoutSeconds/Keywords/TaskTable/ModelAliases)
  - Config-driven dispatch proven end-to-end (ROUT-05): JSON edit + restart changes routing without recompile

affects:
  - phase 02 (streaming): adds StreamAsync to ChatCompletions endpoint; swaps wireJsonOptions to handle stream=true
  - phase 03 (concurrency gate): swaps AddSingleton<IUpstreamClient> to QueueDispatcher(QwenUpstreamClient(...)) in CompositionRoot - one-line DI change
  - phase 04 (resilience): adds AddResilienceHandler to named HttpClients in CompositionRoot
  - phase 05 (verification): runs live Scenario B + Scenario C against real upstreams

tech-stack:
  added:
    - Microsoft.AspNetCore.Builder (WebApplication, MapPost)
    - Microsoft.Extensions.DependencyInjection (IServiceCollection, AddSingleton, AddHttpClient)
    - System.Text.Json JsonExtensionData for unknown-field preservation
    - Serilog.AspNetCore (UseSerilogRequestLogging)
    - FSharp.SystemTextJson wireJsonOptions (standard STJ without FSharp converter for wire body parsing)
  patterns:
    - Named HttpClient via IHttpClientFactory (not typed client, not cached instance) - PITFALL-15
    - Lazy<Task<Result<string, RouterError>>> per-upstream probe fired once at first CompleteAsync call
    - RoutingConfig as DI singleton built at composition time from IOptions<RoutingOptions>
    - Wire type [<CLIMutable>] with mutable fields + standard STJ wireJsonOptions (no FSharpConverter) for tolerant deserialization

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/CompositionRoot.fs
  modified:
    - src/SmartRouter.Cli/Program.fs
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/appsettings.json
    - src/SmartRouter.Cli/Adapters/Json.fs

key-decisions:
  - "Wire deserialization uses wireJsonOptions (standard STJ without FSharpConverter) to tolerate missing optional fields; FSharpConverter only used for upstream serialization"
  - "RouterRequestWire uses [<CLIMutable>] with mutable fields + Nullable<T> types; mapWireToRequest translates to Core RouterRequest"
  - "Lazy probe uses Lazy<Task<Result>> not Lazy<Task<ModelInfo>>; probe failure maps to ModelUnavailable error (no silent fallback to empty model id)"

patterns-established:
  - "RoutingConfig singleton: built at composition from IOptions<RoutingOptions> by buildRoutingConfig; endpoint resolves via GetRequiredService<RoutingConfig>() and passes to Routing.routeRequest"
  - "Error envelope: always {error:{message,type}} shaped — never ASP.NET Problem Details"
  - "Serilog configured before WebApplication.CreateBuilder; logs go to stderr only; stdout stays clean"

duration: 13min
completed: 2026-05-07
---

# Phase 1 Plan 03: Upstream Wiring Summary

**Live HTTP router on 127.0.0.1:4000 with config-driven routing, HF-id probe defense, 300s upstream timeout, and Scenario C proven: JSON edit + restart changes dispatch without recompile (ROUT-05)**

## Performance

- **Duration:** ~13 min
- **Started:** 2026-05-07T06:45:18Z
- **Completed:** 2026-05-07T06:58:00Z
- **Tasks:** 3
- **Files modified:** 7

## Accomplishments

- QwenUpstreamClient implements IUpstreamClient with lazy /v1/models probe (HF-id trap defense), 300s timeout via named HttpClients, sampling defaults (0.7/0.8/top_k=20/pp=0.0), and UnknownFields merge (API-04)
- ChatCompletions endpoint wired: 501 on stream=true, 400 OpenAI envelope on UnsupportedTask, GetRequiredService<RoutingConfig>() + Routing.routeRequest for config-driven dispatch
- Scenario C passed: editing appsettings.json retrieval.Model from "35b" to "122b" + restart (no rebuild) → log shows `target=Qwen122B reason=ExplicitTask Retrieval` — ROUT-05 proven end-to-end

## Task Commits

1. **Task 1: QwenUpstreamClient adapter** - `5154ce7` (feat)
2. **Task 2: ChatCompletions endpoint + appsettings.json** - `d484499` (feat)
3. **Task 3: CompositionRoot, Program.fs, wire deserialization fix** - `b63ca33` (feat)

## Files Created/Modified

- `src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` - IUpstreamClient: tryParseModelId, lazy probe, CompleteAsync with sampling defaults, StreamAsync Phase-2 stub
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` - POST /v1/chat/completions: 501/400/502 paths, GetRequiredService<RoutingConfig>, [<JsonExtensionData>]
- `src/SmartRouter.Cli/CompositionRoot.fs` - buildRoutingConfig, validateConfig, configureServices (named HttpClients + AddSingleton<RoutingConfig> + AddSingleton<IUpstreamClient>)
- `src/SmartRouter.Cli/Program.fs` - Full host builder replacing stub; Logging.configure() pre-builder; validateConfig on startup
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` - Added 3 new Compile entries in correct F# order
- `src/SmartRouter.Cli/appsettings.json` - Full Phase 1 config (Upstreams, Routing section with 7 tunables)
- `src/SmartRouter.Cli/Adapters/Json.fs` - Added wireJsonOptions (standard STJ, no FSharp converter) for tolerant wire body deserialization

## Decisions Made

- `wireJsonOptions` uses standard System.Text.Json without `JsonFSharpConverter` for the incoming wire body — FSharp.SystemTextJson's record converter requires all fields to be present, but OpenAI clients omit most optional fields. Standard STJ with `[<CLIMutable>]` mutable fields handles this gracefully. `jsonOptions` (with FSharpConverter) is kept for upstream serialization where F# union types appear.
- Lazy probe returns `Result<string, RouterError>` rather than a ModelInfo record — probe failure maps directly to `ModelUnavailable` error with no silent fallback to empty model id (blueCode used empty id fallback which could cause silent 4xx failures).
- `buildRoutingConfig` uses `sprintf` instead of F# interpolated strings for exception messages that contain quotes — F# 9 does not allow escaped quotes inside `$"..."` interpolation expressions.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] IUpstreamClient interface implementation uses tupled vs curried syntax**
- **Found during:** Task 1 (QwenUpstreamClient.fs)
- **Issue:** `member this.CompleteAsync(req, target, ct)` is tupled; the Ports.fs interface uses curried `req -> target -> ct -> Task<...>`. Compiler error FS0856.
- **Fix:** Changed interface impl to `member this.CompleteAsync req target ct = this.CompleteAsync req target ct`
- **Files modified:** QwenUpstreamClient.fs
- **Verification:** Build passes, 0 warnings
- **Committed in:** 5154ce7 (Task 1)

**2. [Rule 1 - Bug] ChatCompletions.fs upstream call tupled vs curried**
- **Found during:** Task 2 (ChatCompletions.fs)
- **Issue:** `upstream.CompleteAsync(req, decision.Target, ctx.RequestAborted)` uses tupled call on curried interface. Compiler error FS0001.
- **Fix:** Changed to `upstream.CompleteAsync req decision.Target ctx.RequestAborted`
- **Files modified:** ChatCompletions.fs
- **Committed in:** d484499 (Task 2)

**3. [Rule 1 - Bug] CompositionRoot.fs missing `open System.Net.Http` for IHttpClientFactory**
- **Found during:** Task 3 (CompositionRoot.fs first build)
- **Issue:** `IHttpClientFactory` type not found. FS0039.
- **Fix:** Added `open System.Net.Http` to opens
- **Committed in:** b63ca33 (Task 3)

**4. [Rule 1 - Bug] F# interpolated strings reject escaped quotes in interpolation expressions**
- **Found during:** Task 3 (CompositionRoot.fs first build)
- **Issue:** `$"...[\"{taskName}\"]..."` — F# compiler error FS3373: literal strings cannot be used inside interpolated expressions.
- **Fix:** Changed all affected error messages in buildRoutingConfig and validateConfig to use `sprintf` instead of interpolated strings.
- **Committed in:** b63ca33 (Task 3)

**5. [Rule 1 - Bug] FSharp.SystemTextJson record converter requires all fields present**
- **Found during:** Task 3 (Scenario A smoke test)
- **Issue:** `ReadFromJsonAsync<RouterRequestWire>` throws "Missing field for record type: model" when client omits optional fields. OpenAI clients omit most optional fields.
- **Root cause:** `JsonFSharpConverter` treats all F# record fields as required by default. `WithAllowNullFields` only permits null values for present fields, not absent fields. `WithSkippableOptionFields` requires `string option` but `[<JsonExtensionData>]` dictionary field also fails as missing.
- **Fix:** Created `wireJsonOptions` (standard STJ without FSharpConverter) for incoming body deserialization. Wire type uses `[<CLIMutable>]` with `mutable` fields + `Nullable<T>` for value types (standard .NET null semantics). `jsonOptions` (FSharpConverter) is kept for upstream serialization.
- **Files modified:** Json.fs (added wireJsonOptions), ChatCompletions.fs (wireJsonOptions in ReadFromJsonAsync, mutable wire type)
- **Verification:** Scenario A: 501 on stream=true, 400 on task=foobar — both passing
- **Committed in:** b63ca33 (Task 3)

---

**Total deviations:** 5 auto-fixed (all Rule 1 - bugs)
**Impact on plan:** All fixes necessary for correctness. The FSharp.SystemTextJson wire-parsing bug (#5) required the most significant design change (separate wireJsonOptions), but the design is clean and the separation is principled (strict serialization for upstream bodies, tolerant deserialization for client input).

## Issues Encountered

- `dotnet run` prepends a Korean-language "Using startup settings from launchSettings.json" message to stdout from the tool itself (not from the application). This is a `dotnet run` launcher artifact, not an OBS-04 violation. The actual application binary writes zero bytes to stdout — verified by running the binary directly: `dotnet SmartRouter.dll >/tmp/stdout.log 2>/dev/null && wc -c /tmp/stdout.log` shows 0 bytes.

## Scenario Results

### Scenario A: Router smoke (no upstream required)

```
$ curl -i -X POST http://127.0.0.1:4000/v1/chat/completions \
       -H 'Content-Type: application/json' \
       -d '{"messages":[{"role":"user","content":"hi"}],"stream":true}'
HTTP/1.1 501 Not Implemented
{"error":{"message":"streaming not yet implemented (Phase 2)","type":"not_implemented"}}

$ curl -i -X POST http://127.0.0.1:4000/v1/chat/completions \
       -H 'Content-Type: application/json' \
       -d '{"messages":[{"role":"user","content":"hi"}],"task":"foobar"}'
HTTP/1.1 400 Bad Request
{"error":{"message":"unknown task: foobar","type":"invalid_request_error"}}
```

### Scenario B: Live upstream (Qwen 35B)

Qwen 35B was not running on 127.0.0.1:8000 during plan execution.

**Operator action needed:** `launchctl kickstart -k user/$(id -u)/com.ohama.qwen35b` or equivalent to start the 35B service, then re-run the curl test. Phase 5 verifier will execute the live test.

**Upstream-unreachable path verified:** With upstream down, the router returns HTTP 502 (not a crash):
```
HTTP/1.1 502 Bad Gateway
{"error":{"message":"ModelUnavailable (Qwen35B, \"probe failed: Connection refused (127.0.0.1:8000)\")","type":"upstream_error"}}
```

### Scenario C: Config-driven dispatch (ROUT-05 proof)

**Baseline** (appsettings.json retrieval.Model = "35b"):
```
[INF] Routing target=Qwen35B reason=ExplicitTask Retrieval priority=Low
```

**After JSON edit** (jq set retrieval.Model = "122b", restart with --no-build, NO recompile):
```
[INF] Routing target=Qwen122B reason=ExplicitTask Retrieval priority=Low
```

ROUT-05 proven: editing appsettings.json + restart (no rebuild) changes runtime dispatch. The RoutingConfig singleton was rebuilt from the new JSON by buildRoutingConfig, the endpoint retrieved the updated singleton via GetRequiredService<RoutingConfig>(), and Routing.routeRequest read the new TaskTable.

### Startup log (confirms 127.0.0.1:4000 binding, OPS-04 / OBS-04):
```
[INF] Now listening on: http://127.0.0.1:4000
[INF] Application started. Press Ctrl+C to shut down.
[INF] Hosting environment: Development
[INF] Content root path: /Users/ohama/projs/smart-router/src/SmartRouter.Cli
```

lsof binding verification:
```
SmartRout  PID  ohama  216u  IPv4  ... TCP 127.0.0.1:4000 (LISTEN)
```
Exactly one entry, IPv4 only, 127.0.0.1 (no 0.0.0.0 or IPv6 leak).

## User Setup Required

None — no external service configuration required for the router infrastructure itself. Live upstream smoke (Scenario B) requires the Qwen 35B service to be started.

## Next Phase Readiness

- Phase 1 is complete. Core routing (01-02) + upstream wiring (01-03) = full non-streaming pipeline.
- Phase 2 (streaming) can add StreamAsync to QwenUpstreamClient and update ChatCompletions to handle stream=true.
- Phase 3 (concurrency gate) swaps `AddSingleton<IUpstreamClient>(QwenUpstreamClient...)` to `AddSingleton<IUpstreamClient>(QueueDispatcher(QwenUpstreamClient(...)))` — one-line DI change because the seam (IUpstreamClient) is in place.
- Phase 4 (resilience) adds `AddResilienceHandler` to the named HttpClients in CompositionRoot.

---
*Phase: 01-foundation*
*Completed: 2026-05-07*
