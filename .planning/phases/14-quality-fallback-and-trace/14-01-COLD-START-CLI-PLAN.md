---
phase: 14-quality-fallback-and-trace
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/SmartRouter.Cli/Adapters/ColdStart.fs
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - src/SmartRouter.Cli/Program.fs
autonomous: true

must_haves:
  truths:
    - "src/SmartRouter.Cli/Adapters/ColdStart.fs exists and exposes `runColdStartBackup (logger: ILogger) (rootDir: string) : unit`"
    - "runColdStartBackup looks at 4 candidate files (models/router.zip, models/router.zip.prev, datasets/hard-cases.jsonl, datasets/training-set.jsonl) and renames each existing one to `{path}.cold-start-backup-{yyyyMMdd-HHmmss}` with UTC timestamp"
    - "When no candidate files exist, runColdStartBackup logs an Information message stating ensureDummyModel will generate a fresh model on subsequent startup; does NOT throw"
    - "When at least one file is backed up, runColdStartBackup logs a single Information line with count + timestamp + comma-separated backup paths"
    - "Program.fs `main` checks for `--cold-start` arg BEFORE the `--retrain` branch; when present, calls runColdStartBackup with bootstrap-time logger AFTER Logging.configure but BEFORE configureRequestPipeline / configureWithoutMl runs"
    - "After backup, Program.fs continues normal startup (does NOT exit) — ensureDummyModel sees no router.zip and generates fresh dummy"
    - "Cli.fsproj has `<Compile Include=\"Adapters/ColdStart.fs\" />` placed BEFORE Program.fs (compile-order)"
    - "dotnet build clean with TreatWarningsAsErrors=true"
    - "dotnet test green; test count unchanged (no new tests in this plan)"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/ColdStart.fs"
      provides: "runColdStartBackup function — pure F# (BCL only); idempotent on missing files"
      min_lines: 30
---

<objective>
Implement `--cold-start` CLI flag: timestamp-suffixed backup of 4 model+dataset files, then proceed with normal startup so the existing `ensureDummyModel` path generates a fresh router.zip.

Per Q4 of CONTEXT: backup-then-fresh in a single process invocation. Operator runs once; recovery is `mv backup file` and restart.
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/14-quality-fallback-and-trace/14-CONTEXT.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Create Adapters/ColdStart.fs + add to .fsproj</name>
  <files>
    - src/SmartRouter.Cli/Adapters/ColdStart.fs (NEW)
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  </files>
  <action>
**Step 1.** Create `src/SmartRouter.Cli/Adapters/ColdStart.fs`:

```fsharp
module SmartRouter.Cli.Adapters.ColdStart

open System
open System.IO
open Microsoft.Extensions.Logging

/// Phase 14 — `--cold-start` CLI flag handler.
///
/// Behavior: rename existing model and training-data files with a timestamp suffix
/// so the next startup phase (ensureDummyModel) sees no router.zip and generates a
/// fresh dummy model. Idempotent — files that don't exist are skipped, NOT errored.
///
/// Recovery: operator manually `mv` the backup file back to its original name and
/// restart the router.
///
/// rootDir: typically `Environment.CurrentDirectory`. Resolves the four candidate
/// paths relative to it. Production launchd path is the install dir; dev paths are
/// the repo root or src/SmartRouter.Cli/ (depending on how `dotnet run` was invoked).
let runColdStartBackup (logger: ILogger) (rootDir: string) : unit =
    let timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")
    let candidates = [
        Path.Combine(rootDir, "models/router.zip")
        Path.Combine(rootDir, "models/router.zip.prev")
        Path.Combine(rootDir, "datasets/hard-cases.jsonl")
        Path.Combine(rootDir, "datasets/training-set.jsonl")
    ]
    let backedUp =
        candidates
        |> List.choose (fun src ->
            if File.Exists(src) then
                let dst = sprintf "%s.cold-start-backup-%s" src timestamp
                File.Move(src, dst)
                Some dst
            else
                None)
    if List.isEmpty backedUp then
        logger.LogInformation(
            "Cold-start: no existing model or dataset files to backup; ensureDummyModel will generate a fresh router.zip on this startup")
    else
        logger.LogInformation(
            "Cold-start: backed up {Count} file(s) with timestamp={Timestamp}; files=[{Files}]",
            backedUp.Length, timestamp, String.concat ", " backedUp)
```

**Step 2.** Add Compile entry to `src/SmartRouter.Cli/SmartRouter.Cli.fsproj`. Place AFTER `Logging.fs` (Logging.fs initializes Serilog; ColdStart.fs uses `ILogger` parameter so just needs MEL types — but compile-order in .fsproj should be safe). Suggest placement immediately after `Logging.fs`:

```xml
<Compile Include="Adapters/Logging.fs" />
<Compile Include="Adapters/ColdStart.fs" />     <!-- Phase 14 -->
<Compile Include="Adapters/DecisionLogger.fs" />
...
```

(Exact position flexible; ColdStart.fs has no internal dependencies on other adapters.)
  </action>
  <verify>
```bash
test -f src/SmartRouter.Cli/Adapters/ColdStart.fs && echo OK
grep -c "runColdStartBackup" src/SmartRouter.Cli/Adapters/ColdStart.fs
# expected: >= 2 (definition + at least one usage in same file or 0 in same file but referenced from Program.fs)
grep -c "Adapters/ColdStart\.fs" src/SmartRouter.Cli/SmartRouter.Cli.fsproj
# expected: 1
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# expected: Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 2: Wire `--cold-start` flag into Program.fs</name>
  <files>src/SmartRouter.Cli/Program.fs</files>
  <action>
Edit `src/SmartRouter.Cli/Program.fs`. Add `--cold-start` handling near the start of `main`, AFTER `Logging.configure` runs (so the function has a working logger to use) and BEFORE the `--retrain` branch (so cold-start can be combined with `--retrain` if operator wants to reset then run an offline retrain on freshly-seeded data).

Current Program.fs shape (post-Phase 13):
```fsharp
[<EntryPoint>]
let main args =
    try
        try
            // Logging.configure was previously called here BEFORE the if/else split,
            // but now it's called per-branch. The cold-start handler runs in BOTH
            // branches before configureServices because it must precede ensureDummyModel.
            ...
            if args |> Array.contains "--retrain" then
                let retrainBuilder = Host.CreateApplicationBuilder(args)
                ...
                Logging.configure(retrainBuilder.Configuration)
                applyLogLevelFromArgs args
                CompositionRoot.configureWithoutMl ...
                ...
            else
                let builder = WebApplication.CreateBuilder(args)
                ...
                Logging.configure(builder.Configuration)
                applyLogLevelFromArgs args
                ...
                CompositionRoot.configureServices ...
```

Insert cold-start handling. Suggested placement: near the top of `main`, BEFORE the if/else split. Build a temporary IConfiguration just to call Logging.configure once, OR call cold-start AFTER Logging.configure in EACH branch.

**Recommended (DRY)**: hoist Logging.configure + applyLogLevelFromArgs + cold-start to BEFORE the if/else split:

```fsharp
[<EntryPoint>]
let main args =
    try
        try
            // Build a minimal config just to drive Logging.configure (this resolves
            // appsettings.json once; the per-branch builders use the same file).
            let bootstrapConfig =
                let builder = ConfigurationBuilder()
                builder.SetBasePath(Directory.GetCurrentDirectory()) |> ignore
                builder.AddJsonFile("appsettings.json", optional = true) |> ignore
                builder.Build() :> IConfiguration
            Logging.configure(bootstrapConfig)
            applyLogLevelFromArgs args

            // Phase 14: --cold-start MUST run BEFORE configureServices (ensureDummyModel
            // sees the post-backup state).
            if args |> Array.contains "--cold-start" then
                let coldStartLogger =
                    let factory = LoggerFactory.Create(fun b ->
                        b.AddSerilog(Log.Logger, dispose = false) |> ignore)
                    factory.CreateLogger("ColdStart")
                Adapters.ColdStart.runColdStartBackup coldStartLogger Environment.CurrentDirectory

            if args |> Array.contains "--retrain" then
                ...
            else
                ...
```

If hoisting is too invasive (touches many lines and existing per-branch Logging.configure calls work), alternative: inline cold-start in each branch right after that branch's Logging.configure. Either pattern acceptable; document the choice in the SUMMARY.

**Important**: `Environment.CurrentDirectory` is the CWD at process-start time. Under launchd, this is the plist's `WorkingDirectory`. In dev (`dotnet run --project src/SmartRouter.Cli` from repo root), this is `src/SmartRouter.Cli/`. Cold-start backup applies to whichever directory contains the candidate files. Operator can `cd` to repo-root before running for repo-wide reset.
  </action>
  <verify>
```bash
grep -c "\\-\\-cold-start\\|runColdStartBackup" src/SmartRouter.Cli/Program.fs
# expected: >= 2 (flag check + function call)
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# expected: Build succeeded.
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-restore 2>&1 | grep "EXPECTO!" | tail -1
# expected: 80 passed (existing baseline preserved)
```
  </verify>
</task>

</tasks>

<verification>
- [x] `Adapters/ColdStart.fs` 존재 + .fsproj 등록
- [x] `runColdStartBackup` 4 candidate 파일 timestamp backup
- [x] Program.fs `--cold-start` flag 인지 + Logging.configure 후 / configureServices 전 호출
- [x] 빌드 clean; 기존 80 테스트 그대로 pass
- [x] 새 테스트 없음 (테스트는 14-05 plan 에서 cold-start CLI 활용)
</verification>
