module SmartRouter.Cli.Endpoints.Models

open System
open System.Collections.Generic
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports
open SmartRouter.Cli.Adapters.Json                  // jsonOptions
open SmartRouter.Cli.Adapters.QwenUpstreamClient    // UpstreamOptions

/// Fetch /v1/models from one upstream and return the parsed model entries.
/// Returns [] on any failure (HTTP non-success, network error, malformed JSON).
///
/// IMPORTANT — JsonElement.Clone() is mandatory:
/// `use doc = JsonDocument.Parse(json)` owns the underlying memory for every
/// JsonElement extracted from `doc.RootElement`. When `use doc` goes out of
/// scope (function returns), those elements become INVALID and any later
/// access produces undefined behavior (silent data corruption — not an
/// exception). Calling `.Clone()` copies each element into independently-
/// owned memory that survives doc disposal. Without this, the aggregation
/// loop below dereferences freed memory.
let private fetchModels
    (client: HttpClient)
    (baseUrl: string)
    (ct: CancellationToken)
    : Task<JsonElement list> =
    task {
        try
            let url = baseUrl.TrimEnd('/') + "/v1/models"
            use! resp = client.GetAsync(url, ct)
            if not resp.IsSuccessStatusCode then
                return []
            else
                let! json = resp.Content.ReadAsStringAsync(ct)
                use doc = JsonDocument.Parse(json)
                match doc.RootElement.TryGetProperty("data") with
                | true, data when data.ValueKind = JsonValueKind.Array ->
                    let acc = ResizeArray<JsonElement>()
                    for i in 0 .. data.GetArrayLength() - 1 do
                        let entry = data.[i]
                        match entry.TryGetProperty("id") with
                        | true, idEl when idEl.ValueKind = JsonValueKind.String ->
                            let id = idEl.GetString()
                            if not (String.IsNullOrEmpty id) then
                                // Clone() — see comment above. NON-NEGOTIABLE.
                                acc.Add(entry.Clone())
                        | _ -> ()
                    return List.ofSeq acc
                | _ -> return []
        with _ -> return []
    }

let mapEndpoints (app: WebApplication) =
    app.MapGet(
        "/v1/models",
        Func<HttpContext, Task>(fun ctx ->
            task {
                let probe   = ctx.RequestServices.GetRequiredService<IHealthProbe>()
                let opts    = ctx.RequestServices.GetRequiredService<IOptions<UpstreamOptions>>().Value
                let factory = ctx.RequestServices.GetRequiredService<IHttpClientFactory>()
                // L13 — reuse the existing "health-probe" named client (5s timeout, no retry,
                // no BaseAddress). DO NOT add a 12th named client.
                let client  = factory.CreateClient("health-probe")
                let ct      = ctx.RequestAborted

                // L11 + research §2 — IsReachable is a sync fast-path. If an upstream is
                // already known-down, skip its fetch entirely (don't burn the 5s timeout).
                let task35  =
                    if probe.IsReachable(Qwen35B)  then fetchModels client opts.Model35B  ct
                    else Task.FromResult []
                let task122 =
                    if probe.IsReachable(Qwen122B) then fetchModels client opts.Model122B ct
                    else Task.FromResult []

                let! both = Task.WhenAll([| task35; task122 |])
                let combined = (both.[0] @ both.[1])

                // L12 — dedupe by id; first-seen wins.
                let seen = HashSet<string>()
                let deduped =
                    [ for entry in combined do
                          match entry.TryGetProperty("id") with
                          | true, idEl when idEl.ValueKind = JsonValueKind.String ->
                              let id = idEl.GetString()
                              if not (String.IsNullOrEmpty id) && seen.Add(id) then
                                  yield entry
                          | _ -> () ]

                // L11 — both-down case: 200 + empty data array (NOT 503).
                let body = {| ``object`` = "list"; data = deduped |}
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsJsonAsync(body, jsonOptions, ct)
            } :> Task)) |> ignore
