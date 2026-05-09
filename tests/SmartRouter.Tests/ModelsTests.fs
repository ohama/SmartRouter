module SmartRouter.Tests.ModelsTests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Expecto
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports

// ── Fake upstream helper (mirrors HealthFallbackTests.startFakeUpstream) ──
let private startFakeUpstream (respond: HttpContext -> Task<unit>) : int * IDisposable =
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
    let app = builder.Build()
    app.Run(RequestDelegate(fun ctx -> task { do! respond ctx } :> Task)) |> ignore
    app.StartAsync().GetAwaiter().GetResult()
    let port =
        app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>().Addresses
        |> Seq.head
        |> fun a -> a.Split(':') |> Array.last |> int
    let dispose =
        { new IDisposable with
            member _.Dispose() = app.StopAsync().GetAwaiter().GetResult() }
    port, dispose

/// Acquire a port that is guaranteed unused (TcpListener bind+release pattern from HLTH-03).
let private acquireDeadPort () : int =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

/// Models.fs reads IsReachable BEFORE issuing the HTTP GET. To make tests
/// deterministic without waiting for the real HealthService probe cycle to
/// converge, we replace IHealthProbe with a stub keyed on a per-target dict.
type private StubHealthProbe(reachable35: bool, reachable122: bool) =
    interface IHealthProbe with
        member _.IsReachableAsync target _ct =
            match target with
            | Qwen35B  -> Task.FromResult reachable35
            | Qwen122B -> Task.FromResult reachable122
        member _.IsReachable(target) =
            match target with
            | Qwen35B  -> reachable35
            | Qwen122B -> reachable122
        member _.LastProbedAt(_target) = DateTimeOffset.UtcNow

/// Build the full router app pointed at the given upstream URLs, with the
/// stub IHealthProbe overriding HealthService.
let private startApp
    (model35bUrl: string)
    (model122bUrl: string)
    (reachable35: bool)
    (reachable122: bool)
    : HttpClient * IDisposable * string =

    let tempBase = Path.Combine(Path.GetTempPath(), "smart-router-models-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    Directory.CreateDirectory(tempBase) |> ignore
    let logsDir = Path.Combine(tempBase, "logs", "decisions")
    Directory.CreateDirectory(logsDir) |> ignore

    let testBuilder = WebApplication.CreateBuilder()
    testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore

    (testBuilder.Configuration :> IConfigurationBuilder)
        .AddInMemoryCollection([
            KeyValuePair("Upstreams:Model35B",  model35bUrl)
            KeyValuePair("Upstreams:Model122B", model122bUrl)
            KeyValuePair("Routing:Algorithm",            "heuristic")
            KeyValuePair("Routing:ComplexityThreshold", "3")
            KeyValuePair("Routing:TimeoutSeconds",       "300")
            KeyValuePair("Routing:Keywords:0",           "recursive")
            KeyValuePair("Routing:TaskTable:graph_indexing:Model",           "122b")
            KeyValuePair("Routing:TaskTable:graph_indexing:Priority",        "high")
            KeyValuePair("Routing:TaskTable:compiler_debug:Model",           "122b")
            KeyValuePair("Routing:TaskTable:compiler_debug:Priority",        "high")
            KeyValuePair("Routing:TaskTable:architecture_analysis:Model",    "122b")
            KeyValuePair("Routing:TaskTable:architecture_analysis:Priority", "high")
            KeyValuePair("Routing:TaskTable:dependency_analysis:Model",      "122b")
            KeyValuePair("Routing:TaskTable:dependency_analysis:Priority",   "low")
            KeyValuePair("Routing:TaskTable:reasoning:Model",                "122b")
            KeyValuePair("Routing:TaskTable:reasoning:Priority",             "low")
            KeyValuePair("Routing:TaskTable:retrieval:Model",                "35b")
            KeyValuePair("Routing:TaskTable:retrieval:Priority",             "low")
            KeyValuePair("Routing:TaskTable:summary:Model",                  "35b")
            KeyValuePair("Routing:TaskTable:summary:Priority",               "low")
            KeyValuePair("Routing:ModelAliases:35b",       "Qwen35B")
            KeyValuePair("Routing:ModelAliases:122b",      "Qwen122B")
            KeyValuePair("Queue:FairnessK",                "10")
            KeyValuePair("Queue:MaxConcurrent122B",        "1")
            KeyValuePair("Queue:PerRequestTimeoutSeconds", "60")
            KeyValuePair("DecisionLog:Directory",       logsDir)
            KeyValuePair("DecisionLog:ChannelCapacity", "1000")
            KeyValuePair("Routing:Health:PollingIntervalSeconds",      "60")  // slow — stub overrides anyway
            KeyValuePair("Routing:Health:ConsecutiveFailureThreshold", "1")
        ])
    |> ignore

    SmartRouter.Cli.CompositionRoot.configureServices
        testBuilder.Services
        testBuilder.Configuration
    |> ignore

    // Override IHealthProbe with stub AFTER configureServices (last-registration-wins).
    testBuilder.Services.AddSingleton<IHealthProbe>(StubHealthProbe(reachable35, reachable122) :> IHealthProbe) |> ignore

    let app = testBuilder.Build()

    SmartRouter.Cli.Endpoints.Models.mapEndpoints app

    app.StartAsync().GetAwaiter().GetResult()

    let routerPort =
        app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>().Addresses
        |> Seq.head
        |> fun a -> a.Split(':') |> Array.last |> int

    let httpClient = new HttpClient()
    let dispose =
        { new IDisposable with
            member _.Dispose() =
                httpClient.Dispose()
                app.StopAsync().GetAwaiter().GetResult()
                try Directory.Delete(tempBase, true) with _ -> () }
    httpClient, dispose, sprintf "http://127.0.0.1:%d" routerPort

let private modelsHandler (json: string) : HttpContext -> Task<unit> =
    fun ctx -> task {
        if ctx.Request.Path.Value.EndsWith("/v1/models") then
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsync(json)
        else
            ctx.Response.StatusCode <- 404
    }

let tests =
    testList "models endpoint" [

        testCase "MODELS-01: both upstreams up — deduplicated merge" <| fun () ->
            let json35  = """{"object":"list","data":[{"id":"model-shared","object":"model","created":1715000000,"owned_by":"local"},{"id":"model-35b-only","object":"model","created":1715000000,"owned_by":"local"}]}"""
            let json122 = """{"object":"list","data":[{"id":"model-shared","object":"model","created":1715000000,"owned_by":"local"},{"id":"model-122b-only","object":"model","created":1715000000,"owned_by":"local"}]}"""
            let port35,  d35  = startFakeUpstream (modelsHandler json35)
            let port122, d122 = startFakeUpstream (modelsHandler json122)
            try
                let client, da, baseUrl = startApp (sprintf "http://127.0.0.1:%d" port35) (sprintf "http://127.0.0.1:%d" port122) true true
                try
                    let resp = client.GetAsync(baseUrl + "/v1/models").GetAwaiter().GetResult()
                    Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK"
                    let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    use doc = JsonDocument.Parse(body)
                    Expect.equal (doc.RootElement.GetProperty("object").GetString()) "list" "object=list"
                    let data = doc.RootElement.GetProperty("data")
                    Expect.equal (data.GetArrayLength()) 3 "3 unique entries (model-shared deduped)"
                    let ids = [ for i in 0 .. data.GetArrayLength() - 1 -> data.[i].GetProperty("id").GetString() ]
                    Expect.contains ids "model-shared"      "shared id present"
                    Expect.contains ids "model-35b-only"    "35b-only id present"
                    Expect.contains ids "model-122b-only"   "122b-only id present"
                finally da.Dispose()
            finally d35.Dispose() ; d122.Dispose()

        testCase "MODELS-02: one upstream down — returns reachable upstream's models" <| fun () ->
            let json122 = """{"object":"list","data":[{"id":"model-122b","object":"model","created":1715000000,"owned_by":"local"}]}"""
            let deadPort35 = acquireDeadPort()
            let port122, d122 = startFakeUpstream (modelsHandler json122)
            try
                // 35B is unreachable per stub — Models.fs short-circuits and never connects to deadPort35.
                let client, da, baseUrl = startApp (sprintf "http://127.0.0.1:%d" deadPort35) (sprintf "http://127.0.0.1:%d" port122) false true
                try
                    let resp = client.GetAsync(baseUrl + "/v1/models").GetAwaiter().GetResult()
                    Expect.equal resp.StatusCode HttpStatusCode.OK "200 OK"
                    let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    use doc = JsonDocument.Parse(body)
                    let data = doc.RootElement.GetProperty("data")
                    Expect.equal (data.GetArrayLength()) 1 "only 122b's model returned"
                    Expect.equal (data.[0].GetProperty("id").GetString()) "model-122b" "id matches"
                finally da.Dispose()
            finally d122.Dispose()

        testCase "MODELS-03: both upstreams down — 200 + empty data array" <| fun () ->
            let dead35  = acquireDeadPort()
            let dead122 = acquireDeadPort()
            // Both unreachable per stub.
            let client, da, baseUrl = startApp (sprintf "http://127.0.0.1:%d" dead35) (sprintf "http://127.0.0.1:%d" dead122) false false
            try
                let resp = client.GetAsync(baseUrl + "/v1/models").GetAwaiter().GetResult()
                Expect.equal resp.StatusCode HttpStatusCode.OK "200 (NOT 503) — graceful degradation"
                let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                use doc = JsonDocument.Parse(body)
                Expect.equal (doc.RootElement.GetProperty("object").GetString()) "list" "object=list"
                let data = doc.RootElement.GetProperty("data")
                Expect.equal (data.GetArrayLength()) 0 "empty data array"
            finally da.Dispose()
    ]
