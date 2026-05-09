---
phase: 13-service-logging
plan: 04
type: execute
wave: 3
depends_on: ["13-01"]
files_modified:
  - src/SmartRouter.Cli/Program.fs
autonomous: true

must_haves:
  truths:
    - "Program.fs has a parseLogLevel function that maps string → LogEventLevel; supports verbose|debug|information|warning|error|fatal AND short aliases (info, warn, dbg, vrb, err, ftl)"
    - "Program.fs parses --log-level CLI flag (both --log-level=DEBUG and --log-level DEBUG forms)"
    - "When --log-level is present, Logging.setLevel(parsedLevel) is called BEFORE app.Run() (to take effect for app lifetime)"
    - "When --trace flag is present (legacy), Program.fs fails with a clear migration error message: '--trace flag was removed in Phase 13; use --log-level=debug instead'"
    - "Invalid --log-level value fails with a clear error message listing valid values"
    - "When --log-level is absent, levelSwitch keeps its default (Information from Logging.fs initialization)"
    - "dotnet build clean; dotnet test green"
  artifacts:
    - path: "src/SmartRouter.Cli/Program.fs"
      provides: "main with --log-level CLI parsing replacing --trace"
---

<objective>
Replace the `--trace` boolean flag with `--log-level=enum` per Q8. Parser validates against Serilog `LogEventLevel` names + short aliases. Invalid values fail with clear error. `--trace` if encountered raises a migration error (no silent-fallback to default level).

`Logging.setLevel()` (added in 13-01) is the bridge — Program parses the flag, calls setLevel before host startup.
</objective>

<execution_context>
@./.planning/phases/13-service-logging/13-CONTEXT.md
</execution_context>

<context>
@.planning/phases/13-service-logging/13-CONTEXT.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Add parseLogLevel + arg parsing in Program.fs</name>
  <files>src/SmartRouter.Cli/Program.fs</files>
  <action>
Edit `src/SmartRouter.Cli/Program.fs`. Add the parser function near the top (or inside `main`), and the arg-parsing block after `Logging.configure(builder.Configuration)` but BEFORE `app.Run()`.

**Step 1.** Add `open Serilog.Events` at top of Program.fs if not already present.

**Step 2.** Add `parseLogLevel` private function. Place near the top of `main` (or as module-level `let private`):

```fsharp
let private parseLogLevel (raw: string) : Serilog.Events.LogEventLevel =
    match raw.ToLowerInvariant() with
    | "verbose" | "vrb" | "trace"       -> Serilog.Events.LogEventLevel.Verbose
    | "debug" | "dbg"                   -> Serilog.Events.LogEventLevel.Debug
    | "information" | "info" | "inf"    -> Serilog.Events.LogEventLevel.Information
    | "warning" | "warn" | "wrn"        -> Serilog.Events.LogEventLevel.Warning
    | "error" | "err"                   -> Serilog.Events.LogEventLevel.Error
    | "fatal" | "ftl"                   -> Serilog.Events.LogEventLevel.Fatal
    | other ->
        failwithf
            "--log-level=%s invalid; valid: verbose|debug|information|warning|error|fatal (or info/warn/dbg/vrb/err/ftl aliases)"
            other
```

**Step 3.** Add the argument-parsing block. Place AFTER `Logging.configure(builder.Configuration)` and BEFORE `app.Run()`:

```fsharp
// Phase 13: --log-level=enum CLI flag (replaces deprecated --trace).
// Migration error for legacy --trace usage.
if args |> Array.contains "--trace" then
    failwith "--trace flag was removed in Phase 13; use --log-level=debug instead"

let logLevelArg =
    args |> Array.tryFindIndex (fun a -> a = "--log-level" || a.StartsWith("--log-level="))
    |> Option.map (fun idx ->
        let raw =
            if args.[idx].StartsWith("--log-level=") then
                let v = args.[idx].["--log-level=".Length..]
                if v = "" then failwith "--log-level= requires a value"
                v
            elif idx + 1 < args.Length then
                let v = args.[idx + 1]
                if v.StartsWith("--") then
                    failwith "--log-level requires a value (e.g., debug)"
                v
            else failwith "--log-level requires a value"
        parseLogLevel raw)

match logLevelArg with
| Some level ->
    Logging.setLevel(level)
    Log.Information("Log level set to {Level} via --log-level CLI flag", level)
| None -> ()  // levelSwitch default (Information) — set in Logging.fs init
```

**Step 4 (optional convenience).** If the user already specified `--log-level` in BOTH the equals form AND the space form (rare), parser picks the first occurrence. This is consistent with most CLI conventions.

**Note:** The `--retrain` branch's `Host.CreateApplicationBuilder` flow ALSO needs to honor `--log-level`. Apply the same arg-parsing block in BOTH the `--retrain` branch and the main branch (after each builder is created). To DRY, factor into a helper:

```fsharp
let private applyLogLevelFromArgs (args: string array) : unit =
    if args |> Array.contains "--trace" then
        failwith "--trace flag was removed in Phase 13; use --log-level=debug instead"
    let logLevelArg = ...
    match logLevelArg with
    | Some level -> Logging.setLevel(level)
    | None -> ()

// In --retrain branch, after Logging.configure:
applyLogLevelFromArgs args

// In main branch, after Logging.configure:
applyLogLevelFromArgs args
```

The function lives at module-level above `main` so both branches can call it.
  </action>
  <verify>
```bash
grep -c "parseLogLevel\|applyLogLevelFromArgs\|--log-level\|setLevel" src/SmartRouter.Cli/Program.fs
# expected: >= 4
grep -c "trace flag was removed\|--log-level=" src/SmartRouter.Cli/Program.fs
# expected: >= 2
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# Build succeeded.

# Manual smoke (optional):
# dotnet run --project src/SmartRouter.Cli -- --log-level=debug ... (would start Kestrel; skip in CI)
# dotnet run --project src/SmartRouter.Cli -- --log-level=invalid ... (should fail with clear error)
```
  </verify>
</task>

</tasks>

<verification>
- [x] parseLogLevel function with 6 levels + short aliases
- [x] --log-level CLI flag parsed (= and space forms)
- [x] --trace migration error
- [x] Logging.setLevel called when --log-level present
- [x] Both --retrain and main branches honor --log-level
- [x] Build clean; tests green
</verification>
