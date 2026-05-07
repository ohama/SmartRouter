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
