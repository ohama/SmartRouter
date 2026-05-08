module SmartRouter.Tests.TeacherLabelerTests

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open SmartRouter.Cli.Adapters.TeacherLabeler
open SmartRouter.Core.RetrainingPorts

// ── Test infra ────────────────────────────────────────────────────────────────

let private mkTempDir () : string =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-tests-teacher-" + Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    dir

let private cleanupDir (dir: string) =
    try if Directory.Exists(dir) then Directory.Delete(dir, recursive = true)
    with _ -> ()

/// Spin up a fake teacher endpoint that returns the given canned response body.
/// Returns (app, port). Caller calls app.StopAsync/.DisposeAsync when done.
let private startFakeTeacher (responseBody: string) (statusCode: int) : Task<WebApplication * int> =
    task {
        let b = WebApplication.CreateBuilder()
        b.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        let app = b.Build()
        app.MapPost("/v1/chat/completions",
            Func<HttpContext, Task>(fun ctx ->
                task {
                    ctx.Response.StatusCode  <- statusCode
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync(responseBody)
                }))
        |> ignore
        do! app.StartAsync()
        let port =
            app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()
                .Addresses
            |> Seq.head
            |> fun a -> a.Split(':') |> Array.last |> int
        return app, port
    }

/// Build a TeacherLabeler instance pointed at the given fake-teacher base URL.
/// IHttpClientFactory is constructed via a minimal ServiceCollection.
/// NOTE: F# lambda `fun c -> ...` does not bind to the Action<HttpClient> overload of
/// AddHttpClient reliably — BaseAddress is not set on the created client. Use explicit
/// Action<HttpClient> wrapper so that BaseAddress is applied on CreateClient("teacher").
let private mkLabeler (baseUrl: string) (promptPath: string) (datasetsDir: string) (cap: int) : ITeacherLabeler =
    let services = ServiceCollection()
    services.AddHttpClient("teacher")
        .ConfigureHttpClient(fun c ->
            c.BaseAddress <- Uri(baseUrl)
            c.Timeout     <- TimeSpan.FromSeconds(5.0))
        |> ignore
    let sp = services.BuildServiceProvider()
    let factory = sp.GetRequiredService<IHttpClientFactory>()
    let opts = {
        Endpoint        = baseUrl
        PromptPath      = promptPath
        DailyCallCap    = cap
        TimeoutSeconds  = 5
        DatasetsDir     = datasetsDir }
    TeacherLabeler(factory, opts) :> ITeacherLabeler

/// Write a minimal teacher prompt file at the given path.
let private writePromptFile (path: string) =
    Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
    File.WriteAllText(path, "Test teacher prompt.\n\nRespond ONLY ROUTE_35B or ROUTE_122B.\n\nPrompt:\n{{PROMPT}}")

/// Canned successful chat-completions response for a given content string.
let private cannedResponse (content: string) : string =
    sprintf "{\"id\":\"x\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"%s\"}}]}" content

// ── Tests ─────────────────────────────────────────────────────────────────────

let tests =
    testSequenced (
        testList "TeacherLabeler" [

            testCase "ROUTE_35B response → Labeled Route35B" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let promptPath = Path.Combine(dir, "prompt.md")
                    writePromptFile promptPath
                    let serverTask = startFakeTeacher (cannedResponse "ROUTE_35B") 200
                    let app, port = serverTask.GetAwaiter().GetResult()
                    try
                        let baseUrl = sprintf "http://127.0.0.1:%d" port
                        let labeler = mkLabeler baseUrl promptPath dir 1000
                        let result = labeler.LabelAsync("test prompt", "cid-1", CancellationToken.None).GetAwaiter().GetResult()
                        match result with
                        | Labeled (Route35B, _excerpt) -> ()
                        | other -> failtestf "expected Labeled Route35B; got %A" other
                    finally
                        app.StopAsync().GetAwaiter().GetResult()
                        (app :> IDisposable).Dispose()
                finally cleanupDir dir

            testCase "ROUTE_122B response → Labeled Route122B" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let promptPath = Path.Combine(dir, "prompt.md")
                    writePromptFile promptPath
                    let serverTask = startFakeTeacher (cannedResponse "ROUTE_122B") 200
                    let app, port = serverTask.GetAwaiter().GetResult()
                    try
                        let baseUrl = sprintf "http://127.0.0.1:%d" port
                        let labeler = mkLabeler baseUrl promptPath dir 1000
                        let result = labeler.LabelAsync("complex debug case", "cid-1", CancellationToken.None).GetAwaiter().GetResult()
                        match result with
                        | Labeled (Route122B, _) -> ()
                        | other -> failtestf "expected Labeled Route122B; got %A" other
                    finally
                        app.StopAsync().GetAwaiter().GetResult()
                        (app :> IDisposable).Dispose()
                finally cleanupDir dir

            testCase "chatty response with no sentinel → Unparseable" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let promptPath = Path.Combine(dir, "prompt.md")
                    writePromptFile promptPath
                    let chatty = "I think we should consider all the options carefully here."
                    let serverTask = startFakeTeacher (cannedResponse chatty) 200
                    let app, port = serverTask.GetAwaiter().GetResult()
                    try
                        let baseUrl = sprintf "http://127.0.0.1:%d" port
                        let labeler = mkLabeler baseUrl promptPath dir 1000
                        let result = labeler.LabelAsync("ambiguous", "cid-1", CancellationToken.None).GetAwaiter().GetResult()
                        match result with
                        | Unparseable _ -> ()
                        | other -> failtestf "expected Unparseable; got %A" other
                    finally
                        app.StopAsync().GetAwaiter().GetResult()
                        (app :> IDisposable).Dispose()
                finally cleanupDir dir

            testCase "malformed HTTP body → Unparseable" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let promptPath = Path.Combine(dir, "prompt.md")
                    writePromptFile promptPath
                    // Body is not valid OpenAI shape (no choices array)
                    let serverTask = startFakeTeacher """{"this": "is not OpenAI"}""" 200
                    let app, port = serverTask.GetAwaiter().GetResult()
                    try
                        let baseUrl = sprintf "http://127.0.0.1:%d" port
                        let labeler = mkLabeler baseUrl promptPath dir 1000
                        let result = labeler.LabelAsync("anything", "cid-1", CancellationToken.None).GetAwaiter().GetResult()
                        match result with
                        | Unparseable _ -> ()
                        | other -> failtestf "expected Unparseable; got %A" other
                    finally
                        app.StopAsync().GetAwaiter().GetResult()
                        (app :> IDisposable).Dispose()
                finally cleanupDir dir

            testCase "missing prompt template → Skipped" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let promptPath = Path.Combine(dir, "no-such-file.md")  // intentionally missing
                    let serverTask = startFakeTeacher (cannedResponse "ROUTE_35B") 200
                    let app, port = serverTask.GetAwaiter().GetResult()
                    try
                        let baseUrl = sprintf "http://127.0.0.1:%d" port
                        let labeler = mkLabeler baseUrl promptPath dir 1000
                        let result = labeler.LabelAsync("test", "cid-1", CancellationToken.None).GetAwaiter().GetResult()
                        match result with
                        | Skipped reason ->
                            Expect.stringContains reason "prompt template missing" "reason mentions missing template"
                        | other -> failtestf "expected Skipped; got %A" other
                    finally
                        app.StopAsync().GetAwaiter().GetResult()
                        (app :> IDisposable).Dispose()
                finally cleanupDir dir

            testCase "daily cost cap pre-set to max → Skipped without HTTP call" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let promptPath = Path.Combine(dir, "prompt.md")
                    writePromptFile promptPath
                    // Pre-write a cap counter at the configured max
                    let todayUtc = DateTime.UtcNow.ToString("yyyy-MM-dd")
                    let capPath = Path.Combine(dir, sprintf "teacher-cap-%s.json" todayUtc)
                    File.WriteAllText(capPath, sprintf "{\"date\":\"%s\",\"count\":3,\"max\":3}" todayUtc)
                    // Use a server that would FAIL if hit (no route registered for /v1/chat/completions)
                    // — proving the labeler returned Skipped without making the HTTP call.
                    let baseUrl = "http://127.0.0.1:1"  // unreachable address; if the labeler tries to call, we'd see Failed instead of Skipped
                    let labeler = mkLabeler baseUrl promptPath dir 3   // cap == count == 3, so cap-hit
                    let result = labeler.LabelAsync("test", "cid-1", CancellationToken.None).GetAwaiter().GetResult()
                    match result with
                    | Skipped reason ->
                        Expect.stringContains reason "cap" "reason mentions cap hit"
                    | other -> failtestf "expected Skipped (cap hit); got %A" other
                finally cleanupDir dir
        ])
