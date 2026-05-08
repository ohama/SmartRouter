module SmartRouter.Cli.Adapters.TeacherLabeler

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open SmartRouter.Core.RetrainingPorts

/// Cli-only options bound from appsettings.json "TeacherLabeler" section.
/// Defaults applied at registration time in CompositionRoot (Plan 07-05).
[<CLIMutable>]
type TeacherLabelerOptions =
    { Endpoint        : string   // default "http://127.0.0.1:8001"
      PromptPath      : string   // default "prompts/teacher-prompt.md"
      DailyCallCap    : int      // default 1000
      TimeoutSeconds  : int      // default 30
      DatasetsDir     : string } // default "datasets" (cost-cap counter file lives here)

/// Calls 122B (or any teacher endpoint) with the teacher prompt + a hard case's
/// prompt text, parses ROUTE_35B / ROUTE_122B from the response. FAIL-02 + FAIL-03.
///
/// Stub at Wave 1 — Plan 07-03 replaces LabelAsync body with the real HTTP client +
/// prompt template loader + cost-cap enforcement. Constructor signature is final:
/// (httpFactory: IHttpClientFactory, options: TeacherLabelerOptions).
type TeacherLabeler(httpFactory: IHttpClientFactory, options: TeacherLabelerOptions) =

    interface ITeacherLabeler with
        member _.LabelAsync(_promptText: string, _correlationId: string, _ct: CancellationToken) : Task<LabelResult> =
            raise (NotImplementedException "TeacherLabeler.LabelAsync is a Wave 1 stub; Plan 07-03 fills this in")
