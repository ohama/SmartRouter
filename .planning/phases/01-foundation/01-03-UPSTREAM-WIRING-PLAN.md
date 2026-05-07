---
phase: 01-foundation
plan: 03
type: execute
wave: 3
depends_on: ["01-01", "01-02"]
files_modified:
  - src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
  - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
  - src/SmartRouter.Cli/CompositionRoot.fs
  - src/SmartRouter.Cli/Program.fs
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - src/SmartRouter.Cli/appsettings.json
autonomous: true

must_haves:
  truths:
    - "A non-streaming `POST /v1/chat/completions` request to `http://127.0.0.1:4000/v1/chat/completions` reaches QwenUpstreamClient, which forwards to the configured upstream Qwen port and returns the full upstream response body unchanged to the caller (Phase 1 Success Criterion #1)"
    - "Sending `{\"stream\": true}` returns HTTP 501 with body `{\"error\": {\"message\": \"streaming not yet implemented (Phase 2)\", \"type\": \"not_implemented\"}}` — Phase 1 streaming policy is enforced"
    - "Sending `{\"task\": \"foobar\"}` returns HTTP 400 with OpenAI-shaped error body containing `unknown task: foobar`"
    - "The HTTP client used to call upstream Qwen has `Timeout = TimeSpan.FromSeconds(300.0)` (CONC-07: 300s covers 122B cold start)"
    - "Unknown JSON fields in the request body are preserved and forwarded verbatim in the upstream POST body (API-04 / ROUT-07's UnknownFields)"
    - "QwenUpstreamClient resolves the upstream `model` field via `tryParseModelId` from `/v1/models` — preferring ids that start with `/` (HF-id trap defense — ROUT-07)"
    - "`appsettings.json` declares the routing tunables: `Routing.ComplexityThreshold`, `Routing.Keywords`, `Routing.TaskTable`, `Routing.ModelAliases`, `Upstreams.Model35B`, `Upstreams.Model122B`, `Routing.TimeoutSeconds` (ROUT-05 + OPS-05)"
    - "**`RoutingConfig` is built from `RoutingOptions` at composition time and registered as a DI singleton; the endpoint resolves it and passes it into `Routing.routeRequest`** — runtime dispatch reads operator-supplied JSON, not a hardcoded match (ROUT-05 wiring proof)"
    - "**Editing `appsettings.json` Routing.TaskTable to remap `retrieval` from `35b` to `122b` and restarting the router causes a `task=retrieval` request to route to Qwen122B at runtime (logged: `target=Qwen122B reason=ExplicitTask Retrieval`) — config-driven dispatch verified end-to-end without recompile**"
    - "Serilog is configured before WebApplication build; `app.UseSerilogRequestLogging()` is wired; logs go to stderr (OBS-04 verified by manual `2>/dev/null` smoke)"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs"
      provides: "IUpstreamClient implementation: HF-id probe, 300s timeout, sampling defaults, error mapping, non-streaming CompleteAsync (StreamAsync stubbed for Phase 2)"
      contains: "tryParseModelId"
    - path: "src/SmartRouter.Cli/Endpoints/ChatCompletions.fs"
      provides: "POST /v1/chat/completions handler: parse → route → upstream → respond; 501 on stream=true; 400 on UnsupportedTask; UnknownFields preserved"
      contains: "MapPost"
    - path: "src/SmartRouter.Cli/CompositionRoot.fs"
      provides: "DI wiring: named HttpClients (upstream35b, upstream122b) with 300s timeout; IUpstreamClient → QwenUpstreamClient as singleton; IOptions<RoutingOptions> binding; buildRoutingConfig translates RoutingOptions → Core RoutingConfig; RoutingConfig registered as DI singleton; validateConfig cross-checks JSON against canonical baseline at startup"
      contains: "AddSingleton<RoutingConfig>"
    - path: "src/SmartRouter.Cli/Program.fs"
      provides: "WebApplication entrypoint: Logging.configure(); UseSerilog; MapPost; app.Run()"
      contains: "WebApplication.CreateBuilder"
    - path: "src/SmartRouter.Cli/appsettings.json"
      provides: "Full Phase 1 config: Kestrel + Upstreams + Routing (threshold/keywords/task table/aliases) + Serilog"
      contains: "ComplexityThreshold"
  key_links:
    - from: "src/SmartRouter.Cli/Endpoints/ChatCompletions.fs"
      to: "SmartRouter.Core.Routing.routeRequest"
      via: "direct module call after parsing wire body, with RoutingConfig retrieved from DI"
      pattern: "Routing\\.routeRequest routingConfig"
    - from: "src/SmartRouter.Cli/CompositionRoot.fs"
      to: "SmartRouter.Core.Domain.RoutingConfig (DI singleton)"
      via: "services.AddSingleton<RoutingConfig>(fun sp -> buildRoutingConfig (sp.GetRequiredService<IOptions<RoutingOptions>>().Value))"
      pattern: "AddSingleton<RoutingConfig>"
    - from: "src/SmartRouter.Cli/Endpoints/ChatCompletions.fs"
      to: "IUpstreamClient.CompleteAsync"
      via: "DI-injected upstream client called with decision.Target"
      pattern: "CompleteAsync"
    - from: "src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs"
      to: "IHttpClientFactory.CreateClient(\"upstream35b\" | \"upstream122b\")"
      via: "named client lookup keyed off ModelId"
      pattern: "CreateClient\\(\"upstream"
    - from: "src/SmartRouter.Cli/CompositionRoot.fs"
      to: "appsettings.json Routing section"
      via: "services.Configure<RoutingOptions>(config.GetSection(\"Routing\"))"
      pattern: "GetSection\\(\"Routing\"\\)"
    - from: "src/SmartRouter.Cli/Program.fs"
      to: "Logging.configure() → Log.Logger sink"
      via: "called before WebApplication.CreateBuilder"
      pattern: "Logging\\.configure"
---

<objective>
Wire the live upstream HTTP path: implement `QwenUpstreamClient` (adapted from blueCode `QwenHttpClient.fs`), the `ChatCompletions` endpoint, the `CompositionRoot` DI wiring, the `Program.fs` host builder, and finalize `appsettings.json` with all Phase 1 routing tunables. After this plan ships, a real `POST /v1/chat/completions` request reaches a real Qwen upstream and the response is returned unchanged.

Phase goal contribution: Delivers Success Criterion #1 ("A non-streaming `POST /v1/chat/completions` request reaches the router and a response from the upstream Qwen model is returned to the caller with no field stripping"). Closes the open API + ROUT + OPS requirements left after plan 01-02.

Output: A `dotnet run --project src/SmartRouter.Cli` that listens on `127.0.0.1:4000`, accepts non-streaming chat-completion requests, routes them per the Core pipeline, and returns the upstream Qwen response intact.
</objective>

<execution_context>
@./.planning/phases/01-foundation/01-CONTEXT.md
@./.planning/phases/01-foundation/01-RESEARCH.md
@./.planning/research/PITFALLS.md
</execution_context>

<context>
**blueCode source to adapt:**
- `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/QwenHttpClient.fs` — copy verbatim:
  - `tryParseModelId` (lines ~328–356): HF-id trap defense — prefer `data[n].id` starting with `/`
  - `tryParseMaxModelLen` (lines ~358–379): wire format helper
  - `probeModelInfoAsync` (lines ~398–439): one-shot lazy probe per port; remove blueCode-specific `validateModelPath` call
  - `postAsync` error-mapping pattern (lines ~93–127): adapt error type from `AgentError` to `RouterError`
  - `buildRequestBody` sampling defaults: `temperature=0.7, top_p=0.8, top_k=20, presence_penalty=0.0` only when client omits them

**Phase 1 streaming policy** (per CONTEXT.md):
- `stream=true` → return HTTP 501 with body `{"error": {"message": "streaming not yet implemented (Phase 2)", "type": "not_implemented"}}`
- Full SSE arrives in Phase 2

**OpenAI error envelope shape** (mandatory for client compat):
```
{ "error": { "message": "...", "type": "invalid_request_error" | "upstream_error" | "not_implemented" } }
```
Must NOT use ASP.NET problem details (which is `{"title": ..., "status": ...}`) — the OpenAI SDK clients parse the wrong shape and throw cryptic errors.

**Locked NuGet versions already pinned by 01-01:** Microsoft.Extensions.Http.Resilience 10.5.0 is referenced but the resilience pipeline is NOT wired in Phase 1 (Phase 4 adds retry policy via `AddResilienceHandler`). Plan 01-03 only sets `Timeout = 300s` on each named client.
</context>

<tasks>

<task type="auto">
  <name>Task 1: Implement QwenUpstreamClient.fs (HF-id probe, 300s timeout, sampling defaults, non-streaming CompleteAsync, StreamAsync Phase-2 stub)</name>
  <files>
    src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs,
    src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  </files>
  <action>
    1. Read `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/QwenHttpClient.fs` end-to-end to lift the load-bearing helpers.

    2. Create `src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs`. Module declaration: `module SmartRouter.Cli.Adapters.QwenUpstreamClient`. The implementation must:

       a. Define an `UpstreamOptions` record bound from `IOptions<UpstreamOptions>`:
          ```fsharp
          [<CLIMutable>]
          type UpstreamOptions =
              { Model35B  : string   // "http://127.0.0.1:8000"
                Model122B : string }
          ```
          (Routing-related options live in a separate `RoutingOptions` record bound in CompositionRoot; this adapter only needs the upstream URLs.)

       b. Hold a per-process lazy probe per upstream — `Lazy<Task<Result<string, RouterError>>>` keyed by `ModelId`. The probe sends `GET {upstreamUrl}/v1/models` and runs `tryParseModelId` on the JSON response to resolve the upstream-local-path id. Probe fires once per upstream per process (load-bearing per PITFALL-1: probing on every request would be wasteful and the response is stable for the process lifetime).

       c. Copy `tryParseModelId` from blueCode lines ~328–356 verbatim. The function takes the `/v1/models` JSON response and returns the id string to put in the POST body's `model` field. **The rule:** prefer entries with id starting with `/` (the loaded local path); fall back to `data[0].id`. Sending the HF repo id (e.g., `Qwen/Qwen2.5-Coder-32B`) instead of the local path overwrites the loaded Instruct tokenizer with a Base Coder one and responses become FIM-mode garbage (PITFALL-1 / ROUT-07).

       d. Implement `CompleteAsync` (non-streaming):
          ```fsharp
          // signature
          member _.CompleteAsync (req: RouterRequest) (target: ModelId) (ct: CancellationToken)
              : Task<Result<string, RouterError>> = task {
              // 1. Resolve upstream URL from UpstreamOptions based on target
              // 2. Force the lazy probe (if not yet completed) to get the model id
              // 3. Build request body:
              //    - messages mapped from req.Messages
              //    - model = resolved-local-path-id (NOT the alias the client sent)
              //    - stream = false  (Phase 1: never stream — endpoint already 501s on stream=true)
              //    - temperature, top_p, max_tokens passed through if Some; defaults if None:
              //         temperature defaults to 0.7
              //         top_p        defaults to 0.8
              //         max_tokens   passed as-is (None means upstream chooses)
              //    - top_k = 20, presence_penalty = 0.0 (added defensively even though OpenAI client
              //         doesn't send these — Qwen 3.5 misbehaves without them per CONTEXT.md)
              //    - **All req.UnknownFields keys must be merged into the body BEFORE serialization**
              //         so non-OpenAI fields like Hermes' stream_options.include_usage flow through (API-04)
              // 4. Serialize body using SmartRouter.Cli.Adapters.Json.jsonOptions
              // 5. POST to {upstreamUrl}/v1/chat/completions with the request body and Authorization-free
              //    (mlx_lm.server doesn't enforce auth; consumers connect via loopback)
              // 6. Map response:
              //    - 2xx → Ok (response body string)
              //    - non-2xx → Error (ModelUnavailable(target, $"HTTP {status}: {body}"))
              //    - TaskCanceledException with ct.IsCancellationRequested → Error (InvalidRequest "client cancelled")
              //    - timeout (TaskCanceledException without ct cancellation) → Error (ModelUnavailable(target, "upstream timeout"))
              //    - HttpRequestException → Error (ModelUnavailable(target, ex.Message))
              //    - JSON parse failure on probe → Error (ModelUnavailable(target, "invalid /v1/models response"))
          }
          ```
          The function uses `task {}` (CONC-07 still applies in adapters; though `async {}` is allowed in adapters it's discouraged — matches blueCode style).

          **Sampling-default merge order** (load-bearing): client values WIN. Defaults only fill `None` slots. The body builder must produce JSON like `{"messages": [...], "model": "/path/to/model", "stream": false, "temperature": 0.7, "top_p": 0.8, "top_k": 20, "presence_penalty": 0.0, "max_tokens": null OR <client-value>, ...UnknownFields...}`. Client-provided `temperature` overrides 0.7, etc.

       e. Stub `StreamAsync` for Phase 2:
          ```fsharp
          member _.StreamAsync (req: RouterRequest) (target: ModelId) (ct: CancellationToken)
              : IAsyncEnumerable<Result<string, RouterError>> =
              // Phase 2 implementation. For Phase 1, return an empty async enumerable
              // OR throw NotImplementedException. The endpoint never calls this in Phase 1
              // because it 501s on stream=true before reaching the upstream client.
              let empty = System.Linq.AsyncEnumerable.Empty<Result<string, RouterError>>()
              empty
          ```
          (If `System.Linq.AsyncEnumerable.Empty` isn't available, define a trivial private async enumerable that immediately completes. Either way, this code path is unreachable in Phase 1 — the endpoint short-circuits stream=true to 501.)

       f. Implement `IUpstreamClient`:
          ```fsharp
          type QwenUpstreamClient(httpFactory: IHttpClientFactory, opts: IOptions<UpstreamOptions>) =
              // ... lazy probes, helpers ...
              interface SmartRouter.Core.Ports.IUpstreamClient with
                  member this.CompleteAsync(req, target, ct) = this.CompleteAsync req target ct
                  member this.StreamAsync(req, target, ct)   = this.StreamAsync req target ct
          ```

       g. Use `httpFactory.CreateClient("upstream35b")` for `Qwen35B` and `httpFactory.CreateClient("upstream122b")` for `Qwen122B`. **Do NOT cache the HttpClient instance in a field** — `IHttpClientFactory` manages connection pooling; calling `CreateClient` per request is correct (PITFALL-15).

    3. Update `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` `<Compile>` ItemGroup to add `QwenUpstreamClient.fs` after `Logging.fs` and before any Endpoints or CompositionRoot:
       ```xml
       <Compile Include="Adapters/Json.fs" />
       <Compile Include="Adapters/Logging.fs" />
       <Compile Include="Adapters/QwenUpstreamClient.fs" />
       <!-- Endpoints + CompositionRoot + Program added in this same plan, tasks 2-3 -->
       ```

    **Anti-patterns to avoid:**
    - Do NOT use a typed `HttpClient` (`AddHttpClient<QwenUpstreamClient>(...)`) — that creates one client per DI scope and wastes the connection pool. Named clients via `IHttpClientFactory.CreateClient("upstream35b")` is the correct pattern (PITFALL-15).
    - Do NOT cache the named `HttpClient` instance in a singleton field — call `CreateClient` per request.
    - Do NOT skip `tryParseModelId` and just send the alias the client sent in `req.ModelOverride`. The mlx_lm.server expects the local path id; sending the HF id silently corrupts every response (PITFALL-1).
    - Do NOT set `httpClient.Timeout` to less than 300 seconds — 122B cold starts have been observed up to 240s and `httpClient.Timeout` defaults to 100s (PITFALL-12).
    - Do NOT eagerly probe upstreams at adapter construction time. Probe lazily on first request — startup must succeed even if upstreams are temporarily down (Phase 4 adds proper health probing).
    - Do NOT strip unknown JSON fields. The JSON request body merge order must be: `(client overrides) → (sampling defaults) → (UnknownFields merged in)` so all client-supplied fields, recognized or not, reach the upstream (API-04).
  </action>
  <verify>
    ```
    # 1. Cli compiles with the new adapter
    dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj    # exit 0

    # 2. tryParseModelId is present
    grep -F 'tryParseModelId' src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs   # 1+ matches

    # 3. 300s timeout is set somewhere (CompositionRoot or in QwenUpstreamClient — task 3 wires it via AddHttpClient)
    grep -nE '300|TimeSpan\.FromSeconds' src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs src/SmartRouter.Cli/CompositionRoot.fs 2>/dev/null
    # Expected: at least one match (likely in CompositionRoot once Task 3 runs)

    # 4. UnknownFields are referenced in body construction
    grep -F 'UnknownFields' src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs    # 1+ matches

    # 5. Whole solution still builds
    dotnet build SmartRouter.slnx                              # exit 0
    ```
  </verify>
  <done>
    `QwenUpstreamClient.fs` compiles. It implements `IUpstreamClient`. `tryParseModelId` is present and runs on the `/v1/models` probe. `UnknownFields` are merged into the upstream POST body. `StreamAsync` exists as a Phase-2 stub.
  </done>
</task>

<task type="auto">
  <name>Task 2: Write ChatCompletions endpoint + populate appsettings.json with all routing tunables</name>
  <files>
    src/SmartRouter.Cli/Endpoints/ChatCompletions.fs,
    src/SmartRouter.Cli/appsettings.json,
    src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  </files>
  <action>
    1. Create `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs`. Module declaration: `module SmartRouter.Cli.Endpoints.ChatCompletions`. The handler:

       a. Define the wire request type with `[<JsonExtensionData>]` for unknown fields:
          ```fsharp
          [<CLIMutable>]
          type WireMessage = { role: string; content: string }

          [<CLIMutable>]
          type RouterRequestWire =
              { messages    : WireMessage[]
                model       : string                           // nullable in JSON; STJ maps to ""
                stream      : Nullable<bool>
                temperature : Nullable<float>
                top_p       : Nullable<float>
                max_tokens  : Nullable<int>
                task        : string                           // null when absent
                [<JsonExtensionData>]
                extra       : Dictionary<string, JsonElement> }
          ```
          The `[<JsonExtensionData>]` attribute is the load-bearing piece for API-04: STJ deserializes any unrecognized JSON property into `extra`, and the handler merges `extra` into `RouterRequest.UnknownFields`.

       b. `mapWireToRequest` converts wire types to `RouterRequest`:
          - `messages` → `Message list` with case-insensitive role mapping (`"system"` → `System`, etc.). Unknown roles default to `User`.
          - `model` → `Some` if non-null/non-empty, else `None`
          - `task` → `Some` if non-null/non-empty (after trim), else `None`
          - `stream` → bool with `false` default if Nullable is null
          - `temperature`/`top_p`/`max_tokens` → option types
          - `extra` → `Map.ofSeq (extra |> Seq.map (fun kv -> kv.Key, kv.Value))` for `UnknownFields`

       c. Handler implementation (ASP.NET Minimal API style; **takes `RoutingConfig` as an explicit parameter** because Core's `routeRequest` is config-parameterized — the DI singleton built by `CompositionRoot.buildRoutingConfig` is what flows through here):
          ```fsharp
          let handler
              (routingConfig: RoutingConfig)
              (upstream: IUpstreamClient)
              (logger: ILogger)
              (ctx: HttpContext) : Task =
              task {
                  // 1. Parse wire body
                  let! wireBody = ctx.Request.ReadFromJsonAsync<RouterRequestWire>(Json.jsonOptions, ctx.RequestAborted)
                  let req = mapWireToRequest wireBody

                  // 2. Phase 1 streaming policy: 501 on stream=true
                  if req.Stream then
                      ctx.Response.StatusCode <- 501
                      do! ctx.Response.WriteAsJsonAsync(
                              {| error = {| message = "streaming not yet implemented (Phase 2)"
                                            ``type`` = "not_implemented" |} |},
                              Json.jsonOptions, ctx.RequestAborted)
                      return ()

                  // 3. Pure routing — config-driven. The RoutingConfig singleton was built from
                  //    appsettings.json at startup; editing JSON + restart changes this behavior
                  //    (ROUT-05 in action).
                  match Routing.routeRequest routingConfig req with
                  | Error (UnsupportedTask raw) ->
                      ctx.Response.StatusCode <- 400
                      do! ctx.Response.WriteAsJsonAsync(
                              {| error = {| message = $"unknown task: {raw}"
                                            ``type`` = "invalid_request_error" |} |},
                              Json.jsonOptions, ctx.RequestAborted)
                  | Error e ->
                      ctx.Response.StatusCode <- 400
                      do! ctx.Response.WriteAsJsonAsync(
                              {| error = {| message = string e
                                            ``type`` = "invalid_request_error" |} |},
                              Json.jsonOptions, ctx.RequestAborted)
                  | Ok decision ->
                      Log.Information("Routing target={Target} reason={Reason} priority={Priority}",
                                      decision.Target, decision.Reason, decision.Priority)
                      let! result = upstream.CompleteAsync(req, decision.Target, ctx.RequestAborted)
                      match result with
                      | Ok body ->
                          ctx.Response.ContentType <- "application/json"
                          do! ctx.Response.WriteAsync(body, ctx.RequestAborted)
                      | Error e ->
                          ctx.Response.StatusCode <- 502
                          do! ctx.Response.WriteAsJsonAsync(
                                  {| error = {| message = string e
                                                ``type`` = "upstream_error" |} |},
                                  Json.jsonOptions, ctx.RequestAborted)
              }
          ```

       d. Expose a registration helper. The handler **resolves the `RoutingConfig` singleton from DI** before calling into the handler — this is the explicit wiring that proves ROUT-05:
          ```fsharp
          let mapEndpoints (app: WebApplication) =
              app.MapPost("/v1/chat/completions", System.Func<HttpContext, Task>(fun ctx ->
                  let routingConfig = ctx.RequestServices.GetRequiredService<RoutingConfig>()
                  let upstream      = ctx.RequestServices.GetRequiredService<IUpstreamClient>()
                  handler routingConfig upstream Log.Logger ctx)) |> ignore
          ```
          Or use `app.MapPost("/v1/chat/completions", Func<HttpContext, RoutingConfig, IUpstreamClient, Task>(fun ctx cfg u -> handler cfg u Log.Logger ctx))` — whichever idiom the executor finds cleanest, but **the `RoutingConfig` resolution must be explicit**. The endpoint MUST be `/v1/chat/completions` exactly (API-01).

    2. Update `src/SmartRouter.Cli/appsettings.json` to the full Phase 1 shape (replace existing content):
       ```json
       {
         "Kestrel": {
           "Endpoints": {
             "Http": {
               "Url": "http://127.0.0.1:4000"
             }
           }
         },
         "Upstreams": {
           "Model35B": "http://127.0.0.1:8000",
           "Model122B": "http://127.0.0.1:8001"
         },
         "Routing": {
           "ComplexityThreshold": 3,
           "TimeoutSeconds": 300,
           "Keywords": [
             "recursive", "dependency", "lowering", "mlir", "llvm", "compiler",
             "architecture", "type inference", "graph relation", "closure conversion",
             "cross-file", "multi-file", "reasoning", "inference", "optimization",
             "refactor", "redesign", "abstract", "formal", "proof"
           ],
           "TaskTable": {
             "graph_indexing":        { "Model": "122b", "Priority": "high" },
             "compiler_debug":        { "Model": "122b", "Priority": "high" },
             "architecture_analysis": { "Model": "122b", "Priority": "high" },
             "dependency_analysis":   { "Model": "122b", "Priority": "low" },
             "reasoning":             { "Model": "122b", "Priority": "low" },
             "retrieval":             { "Model": "35b",  "Priority": "low" },
             "summary":               { "Model": "35b",  "Priority": "low" }
           },
           "ModelAliases": {
             "35b":      "Qwen35B",
             "qwen35b":  "Qwen35B",
             "qwen-35b": "Qwen35B",
             "122b":     "Qwen122B",
             "qwen122b": "Qwen122B",
             "qwen-122b":"Qwen122B"
           }
         },
         "Serilog": {
           "MinimumLevel": {
             "Default": "Information"
           }
         },
         "Logging": {
           "LogLevel": {
             "Default": "Information",
             "Microsoft.AspNetCore": "Warning"
           }
         }
       }
       ```

       **Note on TaskTable in JSON (CONTEXT.md-locked, config-driven from day 1):** Per CONTEXT.md the user explicitly overrode the recommended hardcoded F# match default. `appsettings.json` `Routing.TaskTable` is the **authoritative runtime source** for the task→model mapping. CompositionRoot's `buildRoutingConfig` reads `RoutingOptions.TaskTable: Dictionary<string, TaskTableEntry>` and constructs a Core `RoutingConfig` record. The endpoint resolves the `RoutingConfig` DI singleton and passes it into `Routing.routeRequest`. The runtime path reads the operator-supplied map — editing this JSON section + restarting the binary (no rebuild) changes runtime dispatch (Scenario C verifies this end-to-end). The F# `taskToDecision` exhaustive match survives in Core as: (a) the canonical baseline that `validateConfig` diffs the JSON against at startup (catches typos / missing tasks), and (b) a known-good fixture for tests. **Validation + baseline only, never the runtime dispatch.** Priority assignment per task stays in code (correctness, not policy) — but for shape uniformity the JSON's Priority field is plumbed through and validated against the canonical baseline.

    3. Update `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` `<Compile>` ItemGroup to add `Endpoints/ChatCompletions.fs` after the adapters and before CompositionRoot/Program (Task 3 adds the latter two):
       ```xml
       <Compile Include="Adapters/Json.fs" />
       <Compile Include="Adapters/Logging.fs" />
       <Compile Include="Adapters/QwenUpstreamClient.fs" />
       <Compile Include="Endpoints/ChatCompletions.fs" />
       <!-- CompositionRoot.fs + Program.fs added in Task 3 -->
       ```

    **Anti-patterns to avoid:**
    - Do NOT return ASP.NET Problem Details (`{"title": "...", "status": 400}`) on errors. The OpenAI SDK clients parse only the OpenAI envelope `{"error": {"message": ..., "type": ...}}`.
    - Do NOT skip the `if req.Stream` 501 short-circuit. Phase 1 must NOT call `upstream.StreamAsync` (it's a stub).
    - Do NOT serialize wire types directly to upstream — the body must be re-built from `RouterRequest` fields PLUS `UnknownFields` (and PLUS sampling defaults) so all of `(client values) ∪ (defaults) ∪ (unknowns)` reach the upstream verbatim (API-04).
    - Do NOT set `ctx.Response.ContentType` before deciding the response shape — the streaming/501 branch sets `application/json` via `WriteAsJsonAsync`; the success branch explicitly sets `application/json` before `WriteAsync(body)`.
    - **Do NOT call `Routing.routeRequest req` (config-less). The Core signature is `RoutingConfig -> RouterRequest -> Result<...>` — the handler MUST retrieve `RoutingConfig` from DI and pass it explicitly.** This is the wiring proof of ROUT-05 / CONTEXT.md's config-driven decision. A handler that references a hardcoded baseline `defaultRoutingConfig` instead of the DI-resolved singleton fails Scenario C and the plan is not done.
  </action>
  <verify>
    ```
    # 1. Build with new endpoint file
    dotnet build SmartRouter.slnx                                                  # exit 0

    # 2. Endpoint mapping uses the exact path
    grep -F '/v1/chat/completions' src/SmartRouter.Cli/Endpoints/ChatCompletions.fs # 1+ matches

    # 3. Streaming 501 branch present
    grep -F 'streaming not yet implemented' src/SmartRouter.Cli/Endpoints/ChatCompletions.fs

    # 4. UnknownFields preserved (JsonExtensionData)
    grep -F 'JsonExtensionData' src/SmartRouter.Cli/Endpoints/ChatCompletions.fs    # 1 match

    # 5. appsettings has all routing keys
    grep -F 'ComplexityThreshold' src/SmartRouter.Cli/appsettings.json    # 1 match
    grep -F 'TaskTable' src/SmartRouter.Cli/appsettings.json              # 1 match
    grep -F 'ModelAliases' src/SmartRouter.Cli/appsettings.json           # 1 match
    grep -F '127.0.0.1:8000' src/SmartRouter.Cli/appsettings.json         # 1 match
    grep -F '127.0.0.1:8001' src/SmartRouter.Cli/appsettings.json         # 1 match
    ```
  </verify>
  <done>
    Cli builds with the endpoint file in place. The endpoint registers `/v1/chat/completions`, returns 501 for `stream=true`, returns 400 with OpenAI envelope for `UnsupportedTask`, and uses `[<JsonExtensionData>]` for unknown-field preservation. `appsettings.json` has all 5 Routing keys (ComplexityThreshold, TimeoutSeconds, Keywords, TaskTable, ModelAliases) plus Upstreams.
  </done>
</task>

<task type="auto">
  <name>Task 3: Wire CompositionRoot.fs (DI: named HttpClients with 300s timeout, IUpstreamClient singleton, IOptions binding) and Program.fs (host builder); smoke-test the live HTTP path</name>
  <files>
    src/SmartRouter.Cli/CompositionRoot.fs,
    src/SmartRouter.Cli/Program.fs,
    src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  </files>
  <action>
    1. Create `src/SmartRouter.Cli/CompositionRoot.fs`. Module declaration: `module SmartRouter.Cli.CompositionRoot`. Responsibilities:

       a. Define `RoutingOptions` (the JSON-binding shape — Cli-only; Core uses the pure `RoutingConfig` record from `Domain.fs`):
          ```fsharp
          [<CLIMutable>]
          type TaskTableEntry = { Model: string; Priority: string }

          [<CLIMutable>]
          type RoutingOptions =
              { ComplexityThreshold : int
                TimeoutSeconds      : int
                Keywords            : string[]
                TaskTable           : Dictionary<string, TaskTableEntry>
                ModelAliases        : Dictionary<string, string> }
          ```

       b. **`buildRoutingConfig` — translates JSON-bound `RoutingOptions` into the Core `RoutingConfig` record.** This is the bridge between the Cli-side configuration (which knows about `IOptions<T>` and JSON shapes) and the Core (which is pure and only knows F# records). The endpoint handler uses this `RoutingConfig` directly when calling `Routing.routeRequest`. **This wiring is the proof of ROUT-05: editing `appsettings.json` rebuilds this `RoutingConfig` at startup, which changes runtime behavior.**

          ```fsharp
          /// Translate JSON-bound RoutingOptions → Core's pure RoutingConfig.
          /// Called once at composition time. Validates each TaskTable entry as it builds the map;
          /// startup fails fast on any malformed JSON.
          let buildRoutingConfig (opts: RoutingOptions) : RoutingConfig =
              let taskMap =
                  opts.TaskTable
                  |> Seq.map (fun (KeyValue(taskName, entry)) ->
                      let modelId =
                          match Routing.tryParseModelAlias entry.Model with
                          | Some m -> m
                          | None ->
                              raise (InvalidOperationException(
                                  $"appsettings.json Routing.TaskTable[\"{taskName}\"].Model = \"{entry.Model}\" is not a known model alias"))
                      let priority =
                          match entry.Priority.ToLowerInvariant() with
                          | "high" -> High
                          | "low"  -> Low
                          | other  ->
                              raise (InvalidOperationException(
                                  $"appsettings.json Routing.TaskTable[\"{taskName}\"].Priority = \"{other}\" must be \"high\" or \"low\""))
                      taskName.ToLowerInvariant(), (modelId, priority))
                  |> Map.ofSeq
              { ComplexityThreshold = opts.ComplexityThreshold
                Keywords            = List.ofArray opts.Keywords
                TaskTable           = taskMap }
          ```

       c. `configureServices` function — registers `IOptions<UpstreamOptions>`, `IOptions<RoutingOptions>`, named HttpClients, the upstream client, **and registers `RoutingConfig` itself as a DI singleton** so the endpoint can retrieve it via constructor injection / `RequestServices.GetRequiredService<RoutingConfig>()` and pass it into `Routing.routeRequest`:
          ```fsharp
          let configureServices (services: IServiceCollection) (config: IConfiguration) : IServiceCollection =
              services
                  .Configure<UpstreamOptions>(config.GetSection("Upstreams"))
                  .Configure<RoutingOptions>(config.GetSection("Routing"))
                  |> ignore

              // Named HttpClients per upstream — 300s timeout (CONC-07 / PITFALL-12)
              services.AddHttpClient("upstream35b", fun c ->
                  let opts = config.GetSection("Upstreams").Get<UpstreamOptions>()
                  c.BaseAddress <- Uri(opts.Model35B)
                  c.Timeout     <- TimeSpan.FromSeconds 300.0)
                  |> ignore

              services.AddHttpClient("upstream122b", fun c ->
                  let opts = config.GetSection("Upstreams").Get<UpstreamOptions>()
                  c.BaseAddress <- Uri(opts.Model122B)
                  c.Timeout     <- TimeSpan.FromSeconds 300.0)
                  |> ignore

              // RoutingConfig as a DI singleton — built once at composition time from RoutingOptions.
              // The endpoint retrieves this and passes it into Routing.routeRequest. THIS is the
              // wiring that satisfies ROUT-05: editing appsettings.json + restart rebuilds this
              // singleton, which changes runtime routing behavior without recompiling.
              services.AddSingleton<RoutingConfig>(fun sp ->
                  let opts = sp.GetRequiredService<IOptions<RoutingOptions>>().Value
                  buildRoutingConfig opts)
                  |> ignore

              // IUpstreamClient as singleton (ARCH-06: stateless service, DI singleton holds the lazy probes)
              services.AddSingleton<IUpstreamClient>(fun sp ->
                  QwenUpstreamClient(
                      sp.GetRequiredService<IHttpClientFactory>(),
                      sp.GetRequiredService<IOptions<UpstreamOptions>>())
                  :> IUpstreamClient)
                  |> ignore

              services
          ```

       d. `validateConfig` function — ROUT-05 startup validation, run AFTER `buildRoutingConfig` succeeds. Cross-checks the operator-supplied JSON against the canonical baseline (`Routing.canonicalTaskTable`) so typos or missing tasks fail loudly:
          - Confirms every `task` key in the JSON matches a known F# `TaskType` via `Routing.tryParseTaskType`. Unknown keys → log Warning (operators may add new tasks ahead of code; the runtime will return UnsupportedTask for them).
          - Confirms every known `TaskType` has at least one entry in the JSON (i.e., the JSON is complete with respect to the F# DU). Missing canonical task → throw at startup with a clear error.
          - `Model` and `Priority` validity is already enforced by `buildRoutingConfig` (it throws on parse failure), so this step is purely a coverage / drift check against the canonical baseline.
          ```fsharp
          let validateConfig (opts: RoutingOptions) : unit =
              // 1. Warn on unknown task keys (don't fail — config can lead the code).
              for KeyValue(taskName, _) in opts.TaskTable do
                  match Routing.tryParseTaskType taskName with
                  | None ->
                      Log.Warning("appsettings.json Routing.TaskTable contains unknown task {Task}; routing will return UnsupportedTask error for it", taskName)
                  | Some _ -> ()

              // 2. Fail-fast if the JSON is missing a canonical task (the F# DU has it but JSON does not).
              let canonical = Routing.canonicalTaskTable |> Map.toSeq |> Seq.map fst |> Set.ofSeq
              let provided  = opts.TaskTable.Keys |> Seq.map (fun k -> k.ToLowerInvariant()) |> Set.ofSeq
              let missing   = Set.difference canonical provided
              if not (Set.isEmpty missing) then
                  raise (InvalidOperationException(
                      $"appsettings.json Routing.TaskTable is missing canonical task(s): {String.concat \", \" missing}. " +
                      $"Add an entry for each (or remove the canonical task from Core if intentional)."))
          ```

    2. Create `src/SmartRouter.Cli/Program.fs`. Module declaration: `module SmartRouter.Cli.Program`. Body:
       ```fsharp
       module SmartRouter.Cli.Program

       open System
       open Microsoft.AspNetCore.Builder
       open Microsoft.Extensions.DependencyInjection
       open Microsoft.Extensions.Hosting
       open Microsoft.Extensions.Options
       open Serilog
       open SmartRouter.Cli.Adapters
       open SmartRouter.Cli.CompositionRoot
       open SmartRouter.Cli.Endpoints

       [<EntryPoint>]
       let main args =
           // Configure Serilog before WebApplication.CreateBuilder so even host startup logs go to stderr
           Logging.configure ()
           try
               try
                   let builder = WebApplication.CreateBuilder(args)

                   builder.Host.UseSerilog() |> ignore

                   CompositionRoot.configureServices builder.Services builder.Configuration
                   |> ignore

                   let app = builder.Build()

                   // Validate Routing config now that DI is built
                   let routing = app.Services.GetRequiredService<IOptions<RoutingOptions>>().Value
                   CompositionRoot.validateConfig routing

                   app.UseSerilogRequestLogging() |> ignore

                   ChatCompletions.mapEndpoints app

                   app.Run()
                   0
               with ex ->
                   Log.Fatal(ex, "Host terminated unexpectedly")
                   1
           finally
               Logging.shutdown ()
       ```
       The `Log.Fatal` path goes to stderr (OBS-04). The application output (none for an HTTP server, but if any test or admin tool needs stdout, it stays clean).

    3. Update `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` final `<Compile>` ItemGroup. Order (top-to-bottom F# constraint):
       ```xml
       <ItemGroup>
         <Compile Include="Adapters/Json.fs" />
         <Compile Include="Adapters/Logging.fs" />
         <Compile Include="Adapters/QwenUpstreamClient.fs" />
         <Compile Include="Endpoints/ChatCompletions.fs" />
         <Compile Include="CompositionRoot.fs" />
         <Compile Include="Program.fs" />
       </ItemGroup>
       ```

    4. Smoke test the live path. Two scenarios:

       **Scenario A — verify the router process starts and 501s on streaming (no upstream needed):**
       ```bash
       # In one terminal:
       dotnet run --project src/SmartRouter.Cli &
       SR_PID=$!
       sleep 3   # wait for Kestrel to bind

       # In a second terminal (or same after backgrounding):
       curl -i -X POST http://127.0.0.1:4000/v1/chat/completions \
            -H 'Content-Type: application/json' \
            -d '{"messages":[{"role":"user","content":"hi"}],"stream":true}'
       # Expected: HTTP/1.1 501 Not Implemented
       #           body: {"error":{"message":"streaming not yet implemented (Phase 2)","type":"not_implemented"}}

       curl -i -X POST http://127.0.0.1:4000/v1/chat/completions \
            -H 'Content-Type: application/json' \
            -d '{"messages":[{"role":"user","content":"hi"}],"task":"foobar"}'
       # Expected: HTTP/1.1 400 Bad Request
       #           body: {"error":{"message":"unknown task: foobar","type":"invalid_request_error"}}

       kill $SR_PID
       ```

       **Scenario B — live upstream end-to-end (requires Qwen 35B running on `127.0.0.1:8000`):**

       (Scenario C — live config-driven dispatch verification — is defined below after Scenario B.)

       ```bash
       # Verify Qwen 35B is reachable (operator may need to launchctl kickstart it)
       curl -s http://127.0.0.1:8000/v1/models | head -c 200
       # If empty / connection refused, skip Scenario B and document in SUMMARY.md as "live upstream
       # smoke pending — operator needs to start qwen 35b". Routing-only smoke (Scenario A) is
       # sufficient to claim Phase 1 Success Criterion #1 if the router process accepts requests
       # AND the upstream code path was built. Live e2e proves the wiring; see done criteria.

       dotnet run --project src/SmartRouter.Cli &
       SR_PID=$!
       sleep 3

       curl -i -X POST http://127.0.0.1:4000/v1/chat/completions \
            -H 'Content-Type: application/json' \
            -d '{"model":"35b","messages":[{"role":"user","content":"reply with the word OK"}],"max_tokens":5}'
       # Expected: HTTP/1.1 200 OK with an OpenAI-shape body containing choices[0].message.content
       # AND Serilog log line on stderr "Routing target=Qwen35B reason=ExplicitModelOverride ..."

       kill $SR_PID
       ```

       If Scenario B fails because no upstream is running, capture the exact error in `01-03-SUMMARY.md`. Phase 1 Success Criterion #1 requires the path to work against a *real* upstream — the Phase 5 verifier will rerun this. If the upstream is up and the test fails, that's a real bug that must be fixed before declaring the plan done.

       **Scenario C — config-driven dispatch verification (proves ROUT-05 end-to-end without recompile):**

       This is the load-bearing verification for the user's locked-in CONTEXT.md decision: "operator can edit without recompiling." The test edits `appsettings.json`, restarts the binary (no `dotnet build`), and confirms the routing decision changed at runtime.

       ```bash
       # 0. Baseline: confirm `retrieval` currently routes to Qwen35B
       dotnet run --project src/SmartRouter.Cli 2>/tmp/sr-baseline.log &
       SR_PID=$!
       sleep 3

       curl -s -X POST http://127.0.0.1:4000/v1/chat/completions \
            -H 'Content-Type: application/json' \
            -d '{"messages":[{"role":"user","content":"q"}],"task":"retrieval","stream":false}' \
            >/dev/null

       # Confirm baseline log line:
       grep -F 'target=Qwen35B' /tmp/sr-baseline.log
       # Expected: 1+ matches with `reason=ExplicitTask Retrieval`

       kill $SR_PID; wait

       # 1. Edit appsettings.json: remap `retrieval` from "35b" to "122b" — operator-style edit, no
       #    code change, no rebuild.
       cp src/SmartRouter.Cli/appsettings.json src/SmartRouter.Cli/appsettings.json.bak
       # Use jq if available (atomic edit); else sed for the model field.
       if command -v jq >/dev/null 2>&1; then
           jq '.Routing.TaskTable.retrieval.Model = "122b"' \
              src/SmartRouter.Cli/appsettings.json > /tmp/_appsettings.json && \
              mv /tmp/_appsettings.json src/SmartRouter.Cli/appsettings.json
       else
           # Fallback: replace the line `"retrieval": { "Model": "35b", ...}` →  `"Model": "122b"`
           sed -i.tmp 's/"retrieval":[[:space:]]*{[[:space:]]*"Model":[[:space:]]*"35b"/"retrieval": { "Model": "122b"/' \
               src/SmartRouter.Cli/appsettings.json
           rm -f src/SmartRouter.Cli/appsettings.json.tmp
       fi

       # Confirm the edit applied:
       grep -A1 '"retrieval"' src/SmartRouter.Cli/appsettings.json | grep -F '"122b"'   # 1 match

       # 2. Restart router (NO rebuild — re-uses the existing compiled binary):
       dotnet run --project src/SmartRouter.Cli --no-build 2>/tmp/sr-edited.log &
       SR_PID=$!
       sleep 3

       # 3. Re-issue the same retrieval request:
       curl -s -X POST http://127.0.0.1:4000/v1/chat/completions \
            -H 'Content-Type: application/json' \
            -d '{"messages":[{"role":"user","content":"q"}],"task":"retrieval","stream":false}' \
            >/dev/null

       kill $SR_PID; wait

       # 4. Confirm runtime behavior changed: log now shows target=Qwen122B
       grep -F 'target=Qwen122B' /tmp/sr-edited.log     # 1+ matches
       grep -F 'reason=ExplicitTask' /tmp/sr-edited.log  # 1+ matches
       # If both grep matches: ROUT-05 is proven — config edit changed runtime dispatch with NO
       # recompile. This is the verbatim verification command from the revision instructions.

       # 5. Restore baseline appsettings.json so subsequent runs / Phase 5 verifications start clean:
       mv src/SmartRouter.Cli/appsettings.json.bak src/SmartRouter.Cli/appsettings.json
       ```

       **What success looks like:** The baseline run logs `target=Qwen35B reason=ExplicitTask Retrieval`. After the JSON edit and restart (no rebuild), the same request logs `target=Qwen122B reason=ExplicitTask Retrieval`. The decision changed because `RoutingConfig.TaskTable` is rebuilt from the JSON at startup, the singleton holds the new value, the endpoint passes it into `Routing.routeRequest`, and the runtime dispatch reads it. If the second run still logs `Qwen35B`, the runtime path is reading a hardcoded source — that's a CONTEXT.md violation and the plan is not done.

       **If Scenario C fails** because the upstream call returns an error (e.g., 122B not running), that's fine — the routing decision is logged BEFORE the upstream call, so `target=Qwen122B reason=ExplicitTask Retrieval` will still appear in the log even if the subsequent upstream POST 502s. The verification is on the routing log line, not the HTTP response code.

    **Anti-patterns to avoid:**
    - Do NOT call `Logging.configure ()` AFTER `WebApplication.CreateBuilder(args)`. Pre-builder host startup logs (e.g., `Microsoft.Hosting.Lifetime` "Now listening on...") will go to the wrong sink (stdout instead of stderr) — breaks OBS-04 silently.
    - Do NOT wrap services as `Scoped` or `Transient`. The lazy probe cache and the QwenUpstreamClient itself must be `Singleton` for ARCH-06 (stateless service; one client per process).
    - Do NOT skip `Logging.shutdown ()` in `finally`. Serilog buffers events and may drop the last few seconds of logs on abrupt termination otherwise.
  </action>
  <verify>
    ```
    # 1. Build everything
    dotnet build SmartRouter.slnx                                              # exit 0

    # 2. All Cli files compile in correct order
    grep -nE '<Compile Include' src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    # Expected order: Json.fs, Logging.fs, QwenUpstreamClient.fs, ChatCompletions.fs,
    #                 CompositionRoot.fs, Program.fs (Program.fs LAST)

    # 3. Run unit tests still pass
    dotnet test SmartRouter.slnx                                               # 0 failures

    # 4. async-ban guard still passes
    ./scripts/check-no-async.sh                                                # exit 0

    # 5. Run Scenario A from <action> step 4. Save the exit codes and curl outputs into the
    #    SUMMARY artifact. Both 501 and 400 must reproduce.

    # 6. Run Scenario B if Qwen 35B is up. If not, document the skip in SUMMARY.md per the
    #    "execution context" — the verifier in Phase 5 will rerun against a live upstream.

    # 7. Run Scenario C (config-driven dispatch verification — load-bearing for ROUT-05):
    #    Edit appsettings.json (jq or sed) to remap `retrieval` from "35b" to "122b", restart
    #    the router with --no-build, re-issue a `task=retrieval` request, and confirm the log
    #    line shows `target=Qwen122B reason=ExplicitTask Retrieval`. Restore appsettings.json.
    #    See Scenario C above for the exact commands.

    # 8. Confirm RoutingConfig is registered as a DI singleton (the wiring is the proof of ROUT-05):
    grep -F 'AddSingleton<RoutingConfig>' src/SmartRouter.Cli/CompositionRoot.fs    # 1 match
    grep -F 'buildRoutingConfig' src/SmartRouter.Cli/CompositionRoot.fs              # 1+ matches
    grep -F 'GetRequiredService<RoutingConfig>' src/SmartRouter.Cli/Endpoints/ChatCompletions.fs  # 1 match
    grep -F 'Routing.routeRequest routingConfig' src/SmartRouter.Cli/Endpoints/ChatCompletions.fs  # 1 match

    # 9. Verify logs go to stderr (OBS-04 smoke):
    dotnet run --project src/SmartRouter.Cli >/tmp/sr-stdout.log 2>/tmp/sr-stderr.log &
    sleep 3 ; kill %1 ; wait
    test ! -s /tmp/sr-stdout.log && echo "OK: stdout empty"
    test -s   /tmp/sr-stderr.log && echo "OK: stderr has logs"
    grep -F 'Now listening on: http://127.0.0.1:4000' /tmp/sr-stderr.log     # 1 match expected
    ```
  </verify>
  <done>
    Solution builds. Unit tests pass. async-ban check passes. Scenario A reproduces (501 on stream=true; 400 on unknown task). Scenario B succeeds against a live Qwen 35B (or, if 35B is not running, the skip is documented in `01-03-SUMMARY.md` with the verbatim curl error and the operator-action note "start qwen 35b via launchctl"). **Scenario C succeeds: editing `appsettings.json` Routing.TaskTable.retrieval.Model from `"35b"` to `"122b"` and restarting (no rebuild) causes a `task=retrieval` request to log `target=Qwen122B reason=ExplicitTask Retrieval` — proving config-driven dispatch end-to-end (ROUT-05).** Stdout file is empty after a router run; stderr file contains the Kestrel startup log line confirming `127.0.0.1:4000` binding. The grep checks confirm `AddSingleton<RoutingConfig>` in CompositionRoot.fs and `Routing.routeRequest routingConfig` in ChatCompletions.fs — the wiring is explicit, not implied.
  </done>
</task>

</tasks>

<verification>
**End-to-end plan verification (closes Phase 1 success criteria):**

```bash
# 1. Solution builds clean
dotnet build SmartRouter.slnx                          # exit 0

# 2. Unit tests pass (RoutingTests from plan 01-02)
dotnet test SmartRouter.slnx                           # 0 failures

# 3. async-ban guard
./scripts/check-no-async.sh                            # exit 0

# 4. The router actually runs and binds 127.0.0.1:4000
dotnet run --project src/SmartRouter.Cli &
SR_PID=$! ; sleep 3
lsof -nP -i 'TCP@127.0.0.1:4000' | grep LISTEN         # 1+ lines
kill $SR_PID ; wait

# 5. Streaming returns 501 (Phase 1 policy)
dotnet run --project src/SmartRouter.Cli &
SR_PID=$! ; sleep 3
curl -i -X POST http://127.0.0.1:4000/v1/chat/completions \
     -H 'Content-Type: application/json' \
     -d '{"messages":[{"role":"user","content":"hi"}],"stream":true}' \
     | grep -F 'HTTP/1.1 501'                          # 1 match
kill $SR_PID ; wait

# 6. Unknown task returns 400 with OpenAI envelope
dotnet run --project src/SmartRouter.Cli &
SR_PID=$! ; sleep 3
curl -s -X POST http://127.0.0.1:4000/v1/chat/completions \
     -H 'Content-Type: application/json' \
     -d '{"messages":[{"role":"user","content":"hi"}],"task":"foobar"}' \
     | grep -F 'unknown task: foobar'                  # 1 match
kill $SR_PID ; wait

# 7. Live upstream test (only when Qwen 35B is up):
#    POST a real request, confirm 200, confirm response body has choices[0].message.content
#    AND that the body is forwarded byte-for-byte (no field stripping — API-04).

# 8. Config-driven dispatch verification (ROUT-05 end-to-end, no recompile):
#    Run Task 3 Scenario C — edit appsettings.json to remap retrieval → 122b, restart with
#    --no-build, re-issue a task=retrieval request, confirm log shows target=Qwen122B,
#    restore appsettings.json. Failure of this scenario means the config wiring is broken.
```
</verification>

<success_criteria>
- The router compiles, runs, and binds to `127.0.0.1:4000` (OPS-04 reaffirmed live).
- `POST /v1/chat/completions` with `stream=false` reaches QwenUpstreamClient and returns the upstream Qwen response unchanged. Phase 1 Success Criterion #1 met.
- `stream=true` returns 501 with `{"error": {"message": "streaming not yet implemented (Phase 2)", "type": "not_implemented"}}`.
- `task=<unknown>` returns 400 with `{"error": {"message": "unknown task: ...", "type": "invalid_request_error"}}`.
- Unknown JSON fields in the request body are forwarded to the upstream verbatim (API-04).
- Both named HttpClients have `Timeout = TimeSpan.FromSeconds 300.0`.
- `tryParseModelId` runs on first upstream call to defend the HF-id trap.
- `Serilog.UseSerilogRequestLogging()` is wired and logs go to stderr only; stdout stays empty during normal operation.
- **`buildRoutingConfig` translates `RoutingOptions` → `RoutingConfig` at composition time; `services.AddSingleton<RoutingConfig>` registers it; the endpoint resolves the singleton via `RequestServices.GetRequiredService<RoutingConfig>()` and passes it into `Routing.routeRequest`. The wiring is explicit, not implied.**
- **Scenario C passes: editing `appsettings.json` Routing.TaskTable.retrieval.Model from `"35b"` to `"122b"`, restarting with `--no-build`, and re-issuing a `task=retrieval` request causes the runtime log to show `target=Qwen122B reason=ExplicitTask Retrieval`. Config-driven dispatch is proven end-to-end without recompile (CONTEXT.md / ROUT-05).**

**Requirements satisfied by this plan:**
- ARCH-04 (QueueDispatcher seam exists: `IUpstreamClient` registered as DI singleton — Phase 3 will swap in `QueueDispatcher(QwenUpstreamClient(...))` without touching Endpoints. Phase 1 ships the seam; Phase 3 ships the wrapper. No `IdentityDispatcher` no-op needed — checker ruled this is valid satisfaction.)
- ARCH-06 (stateless service: all DI registrations are Singleton; no static mutable state)
- ROUT-05 (routing rules loaded from appsettings.json: ComplexityThreshold, Keywords, TaskTable, ModelAliases — translated by `buildRoutingConfig` into a Core `RoutingConfig` record, registered as a DI singleton, retrieved by the endpoint, and passed into `Routing.routeRequest` at runtime. **The wiring is explicit (DI singleton + endpoint resolution), not implied. Scenario C verifies end-to-end that editing the JSON + restarting (no rebuild) changes runtime dispatch — proving config-driven dispatch satisfies CONTEXT.md's locked decision.**)
- ROUT-07 (HF-id trap defense via `tryParseModelId` in QwenUpstreamClient probe)
- API-01 (POST /v1/chat/completions on 127.0.0.1:4000)
- API-02 (parses messages, model, stream, temperature, top_p, max_tokens via RouterRequestWire)
- API-03 (parses optional top-level `task` field)
- API-04 (UnknownFields preserved via [<JsonExtensionData>] and merged into upstream POST body)
- OPS-05 (appsettings.json captures all Phase 1 tunables: model URLs, threshold, keywords, task table, timeouts)
- CONC-07 (300s per-request timeout via named-client `c.Timeout <- TimeSpan.FromSeconds 300.0`)
- OBS-04 (Serilog stderr separation reaffirmed live; stdout stays empty)

**Note on ARCH-04 in Phase 1:**
ARCH-04 requires "queue + semaphore live in a `QueueDispatcher` adapter that wraps any `IUpstreamClient`". Phase 1 implements `IUpstreamClient` (the wrappable seam) and registers it as a DI singleton via `AddSingleton<IUpstreamClient>(QwenUpstreamClient ...)`. Phase 3 swaps this single line to register `QueueDispatcher(QwenUpstreamClient(...))` — the adapter wrapping is a one-line DI change because the seam is in place. The Phase 1 deliverable is the seam (interface + DI registration shape), not a queue.
</success_criteria>

<output>
After completion, create `.planning/phases/01-foundation/01-03-SUMMARY.md` with:
- The exact curl command + response for: 501 streaming, 400 unknown task, 200 live upstream (if available)
- The verbatim startup log line confirming `127.0.0.1:4000` binding (proof OPS-04 holds at runtime)
- A sample `Serilog` log line from a successful request showing `target=`, `reason=`, `priority=` fields
- If Scenario B (live upstream) was skipped: the exact reason and the operator action needed to enable it
- **Scenario C result: the verbatim before/after log lines proving config-driven dispatch (baseline `target=Qwen35B reason=ExplicitTask Retrieval` → after JSON edit + restart `target=Qwen122B reason=ExplicitTask Retrieval`). If the dispatch did NOT change after the edit, the plan is not done.**
- Confirmation that `lsof -nP -i 'TCP@127.0.0.1:4000' | grep LISTEN` showed exactly one entry (proves no IPv6 / 0.0.0.0 leak)
</output>
