module SmartRouter.Cli.Adapters.Json

open System.Text.Json
open System.Text.Json.Serialization

/// Shared System.Text.Json options for all JSON round-trips.
/// JsonFSharpConverter with WithUnionUnwrapFieldlessTags serializes
/// fieldless DU cases as bare strings ("System", "User") instead of
/// {"Case": "System"}. This matches the blueCode wire-format convention
/// and the OpenAI message-role JSON shape.
///
/// JsonFSharpConverter registered with WithUnionUnwrapFieldlessTags(true)
/// configures F#-idiomatic behavior:
///   - option types: None -> null, Some v -> v
///   - F# list -> JSON array
///   - F# Map -> JSON object
///   - Fieldless DU cases serialize as bare strings: System -> "System"
///     (NOT the default {"Case":"System"} adjacent-tag form)
///
/// WithUnionUnwrapFieldlessTags(true) is required for LLM-04: MessageRole
/// (System | User | Assistant) must round-trip as "System", "User",
/// "Assistant" — the bare-string form. Without this flag the default
/// adjacent-tag form {"Case":"System"} is produced, which is not
/// the F#-idiomatic form the QwenUpstreamClient wire protocol expects.
///
/// PUBLIC: used by QwenUpstreamClient (Plan 01-03) for buildRequestBody
/// serialization and reused for DU round-trip guarantees (LLM-04).
let jsonOptions: JsonSerializerOptions =
    let opts = JsonSerializerOptions()
    opts.Converters.Add(JsonFSharpConverter(JsonFSharpOptions.Default().WithUnionUnwrapFieldlessTags(true)))
    opts

/// Wire deserialization options for incoming HTTP request bodies.
///
/// Extends jsonOptions with WithAllowNullFields(true) so that optional fields
/// (like `model`, `task`, `stream`) can be absent from the client's JSON without
/// the FSharp.SystemTextJson converter throwing "Missing field for record type".
/// Using a separate instance keeps upstream serialization strict while incoming
/// requests are tolerant of missing fields (OpenAI clients omit most optional fields).
/// Wire deserialization options for incoming HTTP request bodies.
///
/// Uses standard System.Text.Json WITHOUT the FSharp converter — the wire type
/// RouterRequestWire uses [<CLIMutable>] with standard .NET/option types so STJ's
/// own converter handles null/missing fields gracefully. The FSharpConverter is only
/// needed when serializing F# union types (for upstream bodies); it is not used here.
let wireJsonOptions: JsonSerializerOptions =
    let opts = JsonSerializerOptions()
    opts.PropertyNameCaseInsensitive <- true
    opts.DefaultIgnoreCondition <- System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    opts
