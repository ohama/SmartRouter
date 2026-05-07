---
phase: 01-foundation
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - global.json
  - SmartRouter.slnx
  - src/SmartRouter.Core/SmartRouter.Core.fsproj
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - src/SmartRouter.Cli/appsettings.json
  - src/SmartRouter.Cli/Properties/launchSettings.json
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
  - tests/SmartRouter.Tests/RouterTests.fs
  - scripts/check-no-async.sh
  - .gitignore
autonomous: true

must_haves:
  truths:
    - "`dotnet build SmartRouter.slnx` succeeds with zero errors and zero warnings on a freshly cloned tree"
    - "`SmartRouter.Core.fsproj` declares only one NuGet package: FsToolkit.ErrorHandling 5.2.0 (no Serilog, no HttpClient, no ASP.NET)"
    - "`scripts/check-no-async.sh` exits 0 when `src/SmartRouter.Core` contains no `async {` literal and exits 1 if one appears"
    - "`appsettings.json` binds Kestrel to `http://127.0.0.1:4000` (literal IP, not `localhost`, not `0.0.0.0`)"
    - "Running the test project (`dotnet run --project tests/SmartRouter.Tests`) executes the explicit `rootTests` list and reports zero auto-discovery warnings (even though the list is initially empty)"
  artifacts:
    - path: "SmartRouter.slnx"
      provides: "Solution file in .slnx XML format with three project entries"
      contains: "src/SmartRouter.Core/SmartRouter.Core.fsproj"
    - path: "global.json"
      provides: "SDK pin matching blueCode (10.0.100, rollForward latestFeature)"
      contains: "10.0.100"
    - path: "src/SmartRouter.Core/SmartRouter.Core.fsproj"
      provides: "Class library targeting net10.0 with only FsToolkit.ErrorHandling"
      contains: "FsToolkit.ErrorHandling"
    - path: "src/SmartRouter.Cli/SmartRouter.Cli.fsproj"
      provides: "Microsoft.NET.Sdk.Web project with all 7 Cli NuGet pins, AssemblyName=SmartRouter"
      contains: "Microsoft.NET.Sdk.Web"
    - path: "src/SmartRouter.Cli/appsettings.json"
      provides: "Kestrel 127.0.0.1:4000 binding + Serilog stub config"
      contains: "127.0.0.1:4000"
    - path: "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
      provides: "Console exe project with Expecto + Mvc.Testing pins"
      contains: "Expecto"
    - path: "tests/SmartRouter.Tests/RouterTests.fs"
      provides: "[<EntryPoint>] with explicit empty rootTests list (modules added in later plans)"
      contains: "rootTests"
    - path: "scripts/check-no-async.sh"
      provides: "CI grep guard for `async {` in SmartRouter.Core"
      contains: "src/SmartRouter.Core"
  key_links:
    - from: "SmartRouter.slnx"
      to: "all three .fsproj files"
      via: "<Project Path=\"...\" /> entries"
      pattern: "Project Path"
    - from: "src/SmartRouter.Cli/appsettings.json"
      to: "Kestrel host binding"
      via: "Kestrel.Endpoints.Http.Url"
      pattern: "127\\.0\\.0\\.1:4000"
    - from: "tests/SmartRouter.Tests/RouterTests.fs"
      to: "Expecto runTestsWithCLIArgs"
      via: "[<EntryPoint>] main"
      pattern: "runTestsWithCLIArgs"
---

<objective>
Stand up the empty solution skeleton so all downstream plans (Core domain, adapters, endpoints) have a place to put files. Lock in the hardest-to-retrofit decisions: SDK pin, project layout, NuGet versions, Kestrel 127.0.0.1 binding, `async {}` ban in Core, and the Expecto explicit `rootTests` pattern.

Phase goal contribution: This plan delivers Success Criterion #4 ("`SmartRouter.Core` has zero compilation references to Serilog, HttpClient, or ASP.NET Core") and Success Criterion #5 ("`check-no-async.sh` passes; the Expecto test runner uses the explicit `rootTests` list"). It is the entry gate for plans 01-02 and 01-03 — neither can compile until the projects, NuGet pins, and project references exist on disk.

Output: A `dotnet build SmartRouter.slnx` that succeeds, a `dotnet run --project tests/SmartRouter.Tests` that reports zero failures from an empty rootTests list, and a `scripts/check-no-async.sh` that returns exit 0 against an empty Core directory.
</objective>

<execution_context>
@./.planning/PROJECT.md
@./.planning/STATE.md
@./.planning/ROADMAP.md
@./.planning/phases/01-foundation/01-CONTEXT.md
@./.planning/phases/01-foundation/01-RESEARCH.md
</execution_context>

<context>
**blueCode references to mirror:**
- `/Users/ohama/projs/blueCode/global.json` — SDK pin shape
- `/Users/ohama/projs/blueCode/BlueCode.slnx` — `.slnx` format
- `/Users/ohama/projs/blueCode/scripts/check-no-async.sh` — copy verbatim, change `CORE_DIR`

**Locked NuGet versions (HIGH confidence per RESEARCH.md):**
- FSharp.SystemTextJson 1.4.36
- Serilog 4.3.1
- Serilog.Sinks.Console 6.1.1
- Serilog.AspNetCore 10.0.0
- Microsoft.Extensions.Http.Resilience 10.5.0
- FsToolkit.ErrorHandling 5.2.0
- FSharp.Control.TaskSeq 1.1.1
- Expecto 10.2.1
- Microsoft.AspNetCore.Mvc.Testing 10.0.7
</context>

<tasks>

<task type="auto">
  <name>Task 1: Create solution + three .fsproj files + project references</name>
  <files>
    SmartRouter.slnx,
    global.json,
    src/SmartRouter.Core/SmartRouter.Core.fsproj,
    src/SmartRouter.Cli/SmartRouter.Cli.fsproj,
    src/SmartRouter.Cli/Properties/launchSettings.json,
    tests/SmartRouter.Tests/SmartRouter.Tests.fsproj,
    .gitignore
  </files>
  <action>
    1. Working directory: `/Users/ohama/projs/smart-router`. If files already exist from a prior partial run, delete them and start clean.

    2. Write `global.json` (verbatim mirror of blueCode):
       ```json
       {
         "sdk": {
           "version": "10.0.100",
           "rollForward": "latestFeature"
         }
       }
       ```

    3. Create `.gitignore` (standard .NET ignores):
       ```
       bin/
       obj/
       .vs/
       *.user
       .DS_Store
       ```

    4. Run `dotnet new slnx -n SmartRouter` (creates `SmartRouter.slnx`). If `dotnet new slnx` is unavailable (older SDK), run `dotnet new sln -n SmartRouter` and rename the generated `.sln` to `.slnx` after manually writing the XML body shown below — but expect `slnx` to work on .NET 10.

    5. Run scaffold commands sequentially:
       ```
       dotnet new classlib --language F# -n SmartRouter.Core -o src/SmartRouter.Core
       dotnet new web      --language F# -n SmartRouter.Cli  -o src/SmartRouter.Cli
       dotnet new console  --language F# -n SmartRouter.Tests -o tests/SmartRouter.Tests
       ```

    6. Delete the auto-generated `Program.fs` / `Library.fs` / `Class1.fs` files inside each project (we replace contents in later plans). Leave the `.fsproj` files for now — we rewrite them in step 7.

    7. Overwrite each `.fsproj` with the exact contents below (copy from RESEARCH.md Section "`.fsproj` Compile Order"):

       **`src/SmartRouter.Core/SmartRouter.Core.fsproj`:**
       ```xml
       <Project Sdk="Microsoft.NET.Sdk">
         <PropertyGroup>
           <TargetFramework>net10.0</TargetFramework>
           <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
         </PropertyGroup>
         <ItemGroup>
           <!-- Compile items added by plan 01-02 -->
         </ItemGroup>
         <ItemGroup>
           <PackageReference Include="FsToolkit.ErrorHandling" Version="5.2.0" />
         </ItemGroup>
       </Project>
       ```

       **`src/SmartRouter.Cli/SmartRouter.Cli.fsproj`:**
       ```xml
       <Project Sdk="Microsoft.NET.Sdk.Web">
         <PropertyGroup>
           <OutputType>Exe</OutputType>
           <TargetFramework>net10.0</TargetFramework>
           <AssemblyName>SmartRouter</AssemblyName>
           <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
         </PropertyGroup>
         <ItemGroup>
           <!-- Compile items added by plans 01-02 and 01-03 -->
         </ItemGroup>
         <ItemGroup>
           <PackageReference Include="FSharp.SystemTextJson" Version="1.4.36" />
           <PackageReference Include="Serilog" Version="4.3.1" />
           <PackageReference Include="Serilog.Sinks.Console" Version="6.1.1" />
           <PackageReference Include="Serilog.AspNetCore" Version="10.0.0" />
           <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="10.5.0" />
           <PackageReference Include="FsToolkit.ErrorHandling" Version="5.2.0" />
           <PackageReference Include="FSharp.Control.TaskSeq" Version="1.1.1" />
         </ItemGroup>
         <ItemGroup>
           <ProjectReference Include="..\SmartRouter.Core\SmartRouter.Core.fsproj" />
         </ItemGroup>
       </Project>
       ```

       **`tests/SmartRouter.Tests/SmartRouter.Tests.fsproj`:**
       ```xml
       <Project Sdk="Microsoft.NET.Sdk">
         <PropertyGroup>
           <OutputType>Exe</OutputType>
           <TargetFramework>net10.0</TargetFramework>
         </PropertyGroup>
         <ItemGroup>
           <!-- Test modules added by plans 01-02 and 01-03 BEFORE RouterTests.fs -->
           <Compile Include="RouterTests.fs" />
         </ItemGroup>
         <ItemGroup>
           <PackageReference Include="Expecto" Version="10.2.1" />
           <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.7" />
         </ItemGroup>
         <ItemGroup>
           <ProjectReference Include="..\..\src\SmartRouter.Core\SmartRouter.Core.fsproj" />
         </ItemGroup>
       </Project>
       ```

    8. Add all three projects to the solution:
       ```
       dotnet sln SmartRouter.slnx add src/SmartRouter.Core/SmartRouter.Core.fsproj
       dotnet sln SmartRouter.slnx add src/SmartRouter.Cli/SmartRouter.Cli.fsproj
       dotnet sln SmartRouter.slnx add tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
       ```

    9. Create `src/SmartRouter.Cli/Properties/launchSettings.json` with a single Kestrel profile that does NOT override the `applicationUrl` (so `appsettings.json` is the single source of truth):
       ```json
       {
         "profiles": {
           "SmartRouter": {
             "commandName": "Project",
             "dotnetRunMessages": true,
             "environmentVariables": {
               "ASPNETCORE_ENVIRONMENT": "Development"
             }
           }
         }
       }
       ```

    **Anti-patterns to avoid:**
    - Do NOT add Serilog or HttpClient or ASP.NET packages to `SmartRouter.Core.fsproj` — Core stays pure (ARCH-01).
    - Do NOT use `applicationUrl` in `launchSettings.json` — that overrides `appsettings.json` and would silently break the 127.0.0.1 invariant (OPS-04). Single source of truth = `appsettings.json` (Task 2).
    - Do NOT auto-discover tests — the `RouterTests.fs` entry point uses an explicit `rootTests` list (TEST-07). Auto-discovery burned 4 executors in blueCode.
  </action>
  <verify>
    Run from `/Users/ohama/projs/smart-router`:
    ```
    dotnet restore SmartRouter.slnx
    dotnet build SmartRouter.slnx --no-restore
    ```
    Expected: build succeeds with zero errors. Warnings are unacceptable because of `TreatWarningsAsErrors=true` in Core and Cli — fix any that appear.

    Then verify Core has no forbidden references:
    ```
    grep -E '(Serilog|HttpClient|Microsoft\.AspNetCore)' src/SmartRouter.Core/SmartRouter.Core.fsproj
    ```
    Expected: empty output (exit code 1).
  </verify>
  <done>
    `dotnet build SmartRouter.slnx` returns exit 0 with no errors and no warnings; `SmartRouter.Core.fsproj` contains only the `FsToolkit.ErrorHandling` PackageReference; the solution opens cleanly in any tooling that reads `.slnx`.
  </done>
</task>

<task type="auto">
  <name>Task 2: Add appsettings.json with Kestrel 127.0.0.1:4000 binding + check-no-async.sh + .gitignore polish</name>
  <files>
    src/SmartRouter.Cli/appsettings.json,
    scripts/check-no-async.sh
  </files>
  <action>
    1. Create `src/SmartRouter.Cli/appsettings.json` with **only** the Kestrel + Serilog scaffolding sections. The `Routing` and `Upstreams` sections are added in plan 01-03 (they are not load-bearing for plan 01-01's "scaffold compiles" goal). Content:
       ```json
       {
         "Kestrel": {
           "Endpoints": {
             "Http": {
               "Url": "http://127.0.0.1:4000"
             }
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
       The literal `127.0.0.1` (not `localhost`, not `0.0.0.0`) is load-bearing per OPS-04 / PITFALL-24. `localhost` resolves to IPv6 `::1` in some macOS setups; `0.0.0.0` triggers the macOS firewall prompt.

       Add `<None Update="appsettings.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>` to `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` so the file is published next to the binary. Insert this as a new `<ItemGroup>` after the `ProjectReference` group.

    2. Create `scripts/check-no-async.sh` (mirror blueCode's exact script body, change only the path):
       ```bash
       #!/usr/bin/env bash
       # scripts/check-no-async.sh
       # Enforces no `async {}` in SmartRouter.Core — use task {} CE only.
       # Mirrors /Users/ohama/projs/blueCode/scripts/check-no-async.sh.
       # Exit 0 if clean; exit 1 on any match; exit 2 if Core dir missing.

       set -euo pipefail

       CORE_DIR="src/SmartRouter.Core"

       if [ ! -d "$CORE_DIR" ]; then
           echo "ERROR: $CORE_DIR does not exist (run from repository root)" >&2
           exit 2
       fi

       if grep -rn --include='*.fs' 'async {' "$CORE_DIR" ; then
           echo "" >&2
           echo "ERROR: async {} found in $CORE_DIR — use task {} CE instead." >&2
           exit 1
       fi

       echo "OK: no async {} expressions in $CORE_DIR"
       exit 0
       ```

    3. Make the script executable: `chmod +x scripts/check-no-async.sh`.

    **Anti-patterns to avoid:**
    - Do NOT bind to `localhost` — it can resolve to `::1` and break Hermes which connects to the v4 address explicitly. Use the literal `127.0.0.1`.
    - Do NOT use `0.0.0.0` — triggers the macOS firewall prompt the first time Kestrel starts and exposes the router on the LAN. This router is loopback-only by design.
    - Do NOT rename the script. Phase 5 / CI hooks may grep for the exact path `scripts/check-no-async.sh`.
  </action>
  <verify>
    From repo root:
    ```
    ./scripts/check-no-async.sh
    ```
    Expected: prints `OK: no async {} expressions in src/SmartRouter.Core` and exits 0.

    Then verify the Kestrel binding string is the literal IP:
    ```
    grep -F '127.0.0.1:4000' src/SmartRouter.Cli/appsettings.json
    ```
    Expected: one matching line.

    Negative-case smoke test: temporarily create `src/SmartRouter.Core/_async_canary.fs` containing the line `let _ = async { return 1 }`, re-run `./scripts/check-no-async.sh`, confirm exit 1, then delete the canary file.
  </verify>
  <done>
    `./scripts/check-no-async.sh` exits 0 against the (empty) Core directory; `appsettings.json` contains the literal string `http://127.0.0.1:4000`; the canary smoke test confirms the script correctly rejects an `async {` literal.
  </done>
</task>

<task type="auto">
  <name>Task 3: Write RouterTests.fs entrypoint with explicit (initially empty) rootTests list</name>
  <files>tests/SmartRouter.Tests/RouterTests.fs</files>
  <action>
    Write `tests/SmartRouter.Tests/RouterTests.fs`:
    ```fsharp
    module SmartRouter.Tests.RouterTests

    open Expecto

    /// Explicit list of test modules. Auto-discovery via [<Tests>] is forbidden —
    /// it is unreliable in Expecto and burned 4 executors in blueCode. Every new
    /// test module must (1) be added to SmartRouter.Tests.fsproj <Compile> list
    /// BEFORE this file, and (2) be appended to rootTests below.
    ///
    /// testSequenced rule (PITFALL-27): if a test module touches Console.SetOut
    /// or Console.SetError, wrap its testList with `testSequenced`. Phase 1
    /// routing tests are pure and do not need it; integration tests in later
    /// phases may.
    let rootTests : Test list =
        [
            // SmartRouter.Tests.RoutingTests.tests        // <- added in plan 01-02
            // SmartRouter.Tests.IntegrationTests.tests    // <- added in later phases
        ]

    [<EntryPoint>]
    let main argv =
        runTestsWithCLIArgs [] argv (testList "all" rootTests)
    ```

    The list is intentionally empty in plan 01-01. Plan 01-02 uncomments the `RoutingTests.tests` line and adds `RoutingTests.fs` to the `.fsproj` `<Compile>` list above `RouterTests.fs`.

    **Anti-patterns to avoid:**
    - Do NOT use `[<Tests>]` attribute or `runTestsInAssembly`/`runTestsInAssemblyWithCLIArgs` — they auto-discover and have skipped tests silently in blueCode.
    - Do NOT use `Tests.runTests` (no CLI args wiring) — `runTestsWithCLIArgs [] argv` is required so `--filter`, `--summary`, etc. work.
  </action>
  <verify>
    ```
    dotnet run --project tests/SmartRouter.Tests
    ```
    Expected output includes `0 tests run` (or equivalent — Expecto reports an empty testList run cleanly with exit code 0). No "no tests found" warning, no `[<Tests>]` discovery errors.

    Confirm exit code:
    ```
    dotnet run --project tests/SmartRouter.Tests; echo "exit=$?"
    ```
    Expected: `exit=0`.
  </verify>
  <done>
    The test runner executes the explicit (empty) list, reports zero test failures, exits 0, and emits no Expecto auto-discovery warnings.
  </done>
</task>

</tasks>

<verification>
**End-to-end plan verification:**

```bash
# 1. Solution builds clean
dotnet restore SmartRouter.slnx
dotnet build SmartRouter.slnx --no-restore        # exit 0, no errors, no warnings

# 2. Tests run (empty list)
dotnet run --project tests/SmartRouter.Tests       # exit 0

# 3. async-ban guard passes
./scripts/check-no-async.sh                        # exit 0, "OK: no async {}"

# 4. Core has no forbidden NuGet refs
grep -E '(Serilog|HttpClient|AspNetCore)' src/SmartRouter.Core/SmartRouter.Core.fsproj
# Expected: empty (exit 1 from grep)

# 5. Kestrel binding is literal IP
grep -F '127.0.0.1:4000' src/SmartRouter.Cli/appsettings.json    # 1 match

# 6. Test entrypoint uses explicit list (not [<Tests>])
grep -F 'runTestsWithCLIArgs' tests/SmartRouter.Tests/RouterTests.fs    # 1 match
grep -F '[<Tests>]' tests/SmartRouter.Tests/RouterTests.fs              # 0 matches
```
</verification>

<success_criteria>
- `dotnet build SmartRouter.slnx` exits 0 with zero errors and zero warnings (Core + Cli have `TreatWarningsAsErrors=true`).
- `dotnet run --project tests/SmartRouter.Tests` exits 0 and reports zero tests run from an explicit empty `rootTests` list (no auto-discovery output).
- `./scripts/check-no-async.sh` exits 0 against an empty Core; exits 1 against a planted canary `async {`.
- `SmartRouter.Core.fsproj` declares only one NuGet package (`FsToolkit.ErrorHandling 5.2.0`) — no Serilog, no HttpClient, no ASP.NET.
- `appsettings.json` binds Kestrel to the literal string `http://127.0.0.1:4000`.

**Requirements satisfied by this plan:**
- ARCH-02 (`async {}` ban guard in place — script + Core has zero `.fs` files yet so trivially passes)
- OPS-04 (127.0.0.1 binding in appsettings.json)
- OPS-05 (appsettings.json file exists; full content populated in 01-03)
- TEST-07 (explicit `rootTests` pattern established at scaffold time per PITFALL-26)
</success_criteria>

<output>
After completion, create `.planning/phases/01-foundation/01-01-SUMMARY.md` with:
- Files written and their absolute paths
- The exact NuGet versions resolved by `dotnet restore` (run `dotnet list src/SmartRouter.Cli package` and capture)
- Any deviations from the plan (e.g., if `dotnet new slnx` was unavailable and you fell back to `.sln` rename)
- Confirmation that all three verification commands passed
</output>
