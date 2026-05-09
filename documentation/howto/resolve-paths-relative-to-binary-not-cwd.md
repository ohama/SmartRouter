---
created: 2026-05-10
description: dotnet run 의 CWD 는 project dir — repo root 의 models/ 를 안 봄. AppContext.BaseDirectory + walk-up 으로 CWD 무관하게 resolve
---

# Resolve Paths Relative to Binary, Not CWD

`appsettings.json` 의 상대경로 (`models/embed/...`) 를 CWD 가 아니라 **binary 위치 + walk-up** 으로 resolve 하면 `dotnet run --project ...` 와 launchd `WorkingDirectory` 가 다른 환경에서도 동일하게 동작한다.

## The Insight

.NET process 가 시작될 때 두 개의 디렉토리가 있다:

- **`Environment.CurrentDirectory`** (= CWD): process 를 시작한 shell 의 PWD. `dotnet run --project X` 는 X 의 디렉토리로 cd 한다.
- **`AppContext.BaseDirectory`**: 컴파일된 binary (.dll) 가 있는 디렉토리. `bin/Debug/net10.0/` 등.

`File.Open("models/embed/...")` 와 `IConfiguration["Routing:ML:ModelPath"]` 가 가리키는 경로는 **CWD 기준** 으로 resolve 된다. 하지만 모델 파일은 보통 repo root 의 `models/` 에 있고 CWD 는 `src/SomeProject/` 일 수 있다 — 미스매치.

해결: 상대경로를 **CWD 후보 + binary 후보 + walk-up 후보** 의 순서로 시도하고 첫 번째 존재하는 파일을 선택. 같은 코드가 dev (`dotnet run`) 와 production (launchd `WorkingDirectory`) 에서 모두 작동.

## Why This Matters

본 프로젝트 issue #9 사례:

- `appsettings.json`: `"Routing.ML.ModelPath": "models/embed/bge-m3-int8.onnx"`
- launchd plist: `WorkingDirectory = ~/llm-system/services/smart-router/` → models/ 가 거기 있음 → 정상
- 로컬: `dotnet run --project src/SmartRouter.Cli` → CWD = `src/SmartRouter.Cli/` → 거기 models/ 없음 → `[FTL] Required ML embedding files missing`
- 워크어라운드: 매 fresh clone 마다 `ln -s ../../models src/SmartRouter.Cli/models` 수동으로

**path resolution 만 binary-aware 하게 바꾸면 워크어라운드가 사라진다.** 그리고 launchd 환경의 정상 동작은 그대로.

## Recognition Pattern

- `dotnet run` 시 `[FTL] file missing` 류 startup 실패, 같은 파일이 분명히 repo 어딘가에 있음
- README 에 "make sure to cd to repo root before running" / "create a symlink" 같은 워크어라운드
- `WorkingDirectory` 차이가 dev vs prod 동작 차이의 원인
- `appsettings.json` 에 상대경로 를 쓰지만 process CWD 가 환경마다 다름

## The Approach

핵심 함수: 상대경로를 받아 후보 목록을 만들고 첫 번째 존재하는 것을 반환.

```fsharp
let resolveRelativePath (configPath: string) : string =
    if Path.IsPathRooted(configPath) then
        // 절대경로 → 그대로 사용 (operator override 도 같은 함수로)
        configPath
    else
        // 후보 1: CWD 기준 (back-compat — launchd WorkingDirectory + 명시적 cd 후 dotnet run)
        // 후보 2: binary 기준 (AppContext.BaseDirectory)
        // 후보 3+: binary 의 부모 N 단계 — bin/Debug/net10.0/ 같은 깊이를 우회
        let walkUp =
            let mutable dir = DirectoryInfo(AppContext.BaseDirectory)
            [ for _ in 1 .. 5 do
                if not (isNull dir) then
                    yield Path.Combine(dir.FullName, configPath)
                    dir <- dir.Parent ]
        let candidates =
            [ Path.GetFullPath(configPath)                              // CWD 기준
              Path.Combine(AppContext.BaseDirectory, configPath) ]      // binary 기준
            @ walkUp                                                     // ../, ../../, ... 5단계
        candidates
        |> List.tryFind File.Exists
        |> Option.defaultValue (Path.GetFullPath(configPath))           // none → 원래 경로 반환 (오류 메시지가 의미있게 출력되도록)
```

5 단계 walk-up 의 근거: `bin/Debug/net10.0/` 깊이 = 3, project dir 까지 +1 = 4, src/ 까지 +1 = 5. repo root 의 `models/` 를 잡기에 충분.

### Step 1: path resolution helper 를 정의

위 `resolveRelativePath` 를 path 가 사용되는 모듈에 둔다. 본 프로젝트는 `Adapters/ModelBootstrapper.fs`.

### Step 2: 모든 file-existence check 와 file open 을 helper 통해 호출

```fsharp
// BEFORE
if not (File.Exists onnxPath) then ...

// AFTER
let onnx = resolveRelativePath onnxPath
if not (File.Exists onnx) then ...
```

### Step 3: 운영 시나리오를 표로 검증

| 환경 | CWD | AppContext.BaseDirectory | 후보 매칭 |
|---|---|---|---|
| `dotnet run --project src/SmartRouter.Cli` (repo root 에서) | `src/SmartRouter.Cli/` | `src/SmartRouter.Cli/bin/Debug/net10.0/` | walk-up 4 단계: repo-root/models |
| `dotnet run --project src/SmartRouter.Cli` (`src/SmartRouter.Cli/` 에서) | `src/SmartRouter.Cli/` | `src/SmartRouter.Cli/bin/Debug/net10.0/` | walk-up 4 단계: repo-root/models |
| launchd plist (`WorkingDirectory = /llm-system/services/smart-router`) | 정상 | install 디렉토리/ | 후보 1 (CWD): `/llm-system/services/smart-router/models` |
| `cd /opt/myrouter && dotnet ./SmartRouter.dll` (수동 deploy) | `/opt/myrouter` | `/opt/myrouter` | 후보 1 (CWD) = 후보 2 (binary) — 같음 |

전 케이스에서 워크어라운드 (symlink, 명시적 cd) 없이 작동.

## Example

전체 구현: `src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs` 의 `resolveModelPath` (commit `a4195f5`).

```fsharp
let resolveModelPath (configPath: string) : string =
    if Path.IsPathRooted(configPath) then
        configPath
    else
        let walkUp =
            let mutable dir = DirectoryInfo(AppContext.BaseDirectory)
            [ for _ in 1 .. 5 do
                if not (isNull dir) then
                    yield Path.Combine(dir.FullName, configPath)
                    dir <- dir.Parent ]
        let candidates =
            [ Path.GetFullPath(configPath)
              Path.Combine(AppContext.BaseDirectory, configPath) ]
            @ walkUp
        candidates
        |> List.tryFind File.Exists
        |> Option.defaultValue (Path.GetFullPath(configPath))
```

호출 사이트:

```fsharp
let ensureEmbeddingFilesPresent logger onnxPath tokenizerPath =
    let onnx      = resolveModelPath onnxPath          // ← resolve, not just use
    let tokenizer = resolveModelPath tokenizerPath
    if not (File.Exists onnx) || not (File.Exists tokenizer) then
        // 다음 단계 가 resolved 경로 가 아니라 원래 configPath 를 출력하도록 주의 —
        // operator 가 보던 경로 그대로가 에러에 나와야 디버깅 친화적
        ...
```

## Note: F# list 안에서 `let mutable` + bare expression 트랩

`let candidates = [ exprA; exprB; let mutable dir = ...; for _ in ... do yield ... ]` 는 컴파일 에러 (FS0020 — bare expression 의 결과가 무시됨). 해결: walk-up 부분을 별도 list 로 빌드하고 `@` 으로 concatenate.

```fsharp
// BAD — F# 가 exprA, exprB 를 ignored 라고 판단 (let mutable 아래 yield 만 list 본문)
let candidates = [
    exprA
    exprB
    let mutable dir = ...
    for _ in 1 .. 5 do
        yield ...
]

// GOOD
let walkUp = [ let mutable dir = ... in for _ in 1 .. 5 do yield ... ]
let candidates = [ exprA; exprB ] @ walkUp
```

## 체크리스트

- [ ] 상대경로 가 CWD 기준 으로 resolve 되는 모든 사이트 식별 (`File.Exists`, `File.Open`, `new FileStream`, etc.)
- [ ] `resolveRelativePath` helper 추가 (CWD + AppContext.BaseDirectory + walk-up 후보)
- [ ] 호출 사이트를 helper 경유로 변경
- [ ] 절대경로 처리는 그대로 통과 (`Path.IsPathRooted` 분기)
- [ ] 에러 메시지에는 **원래 configPath** 를 출력 (resolve 된 경로 가 아니라) — operator 친화적
- [ ] 두 환경 (dev `dotnet run`, prod launchd) 둘 다 smoke test

## 관련 문서

- `setup-aspnetcore-config-override-test.md` — IConfiguration override 와 path 해석 충돌 회피
