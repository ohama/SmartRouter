---
created: 2026-05-12T15:30
title: Fail fast on port conflict at startup
area: general
files:
  - src/SmartRouter.Cli/Program.fs
  - src/SmartRouter.Cli/CompositionRoot.fs
  - tests/SmartRouter.Tests/ (new test file TBD)
  - README.md (§13 Troubleshooting)
  - CLAUDE.md (§13 trigger area)
---

## Problem

ASP.NET Core Kestrel이 알아서 바인딩을 시도하지만, 포트 충돌 시 발생하는 `SocketException`(EADDRINUSE / AddressAlreadyInUse) 스택트레이스가 운영자에게 직관적이지 않다. 로컬 LLM 운영 환경에서는 같은 포트(`:4000`)를 잡는 다른 smart-router 인스턴스(이전 launchd 잔재, 수동 실행, `dotnet run`)와 충돌하는 일이 잦다.

**Why this matters:**
- launchd가 KeepAlive로 retry하다가 영원히 실패 루프에 빠지는 경우 운영자가 원인을 찾기 어렵다.
- Hermes Agent가 `:4000`으로 요청을 보내는데 다른 프로세스가 거기 바인딩되어 있으면 silent misrouting (잘못된 응답 형식)이 발생할 수 있다.
- 현재 troubleshooting recipe가 README §13에 없어서 운영자가 처음 만나면 시간을 쓴다.

## Solution

**Desired behavior (startup probe):**

1. `Program.fs` (또는 `CompositionRoot`의 Kestrel 설정 직전)에서 `appsettings.json`의 listen URL(`http://localhost:4000`) 또는 CLI 오버라이드로 결정된 포트를 추출한다.
2. `TcpListener`로 해당 포트에 바인딩을 한 번 시도해 본 뒤 즉시 닫는다 (probe). 또는 `IPGlobalProperties.GetActiveTcpListeners()`로 already-bound 여부 검사.
3. 이미 사용 중이면 stderr로 다음을 출력하고 `Environment.Exit(1)`로 멈춘다 (스택트레이스 없이):

   ```
   ERROR: Port 4000 is already in use. Smart Router cannot start.
   Likely culprit: another smart-router instance, or a different process bound to :4000.
   To investigate: `lsof -iTCP:4000 -sTCP:LISTEN -n -P`
   To stop a stuck launchd instance: `launchctl unload ~/Library/LaunchAgents/com.ohama.smartrouter.plist`
   ```

4. Loopback-only invariant (`127.0.0.1:4000`)이 적용된 포트만 검사 (다른 인터페이스는 검사 안 함).

**Scope:**
- 포트 충돌 검사 로직 — `SmartRouter.Cli.Adapters` 또는 `Program.fs`에 직접 (ARCH-01: Core에는 넣지 않음 — `System.Net.Sockets`는 Core BCL-only 침범).
- 단위 테스트: `TcpListener.Start()`로 임시 점거 후 검사 호출 → 에러 종료 검증. `Environment.Exit` 자체는 테스트하기 어려우므로 probe 함수를 `Result<unit, PortConflictError>` 같은 형태로 분리해서 검사 부분만 테스트.
- README §13 Troubleshooting에 새 recipe 추가 (CLAUDE.md README-sync rule 트리거: §13).

**Not in scope:**
- 포트 충돌 시 다른 포트로 자동 fallback — 명시적 실패가 운영자에게 더 좋은 신호 (smart-router는 launchd 패턴이라 무작위 포트가 의미 없음).
- 시작 시점 외의 포트 health 모니터링 — `/health` 엔드포인트가 이미 커버.

**Suggested placement:**
- 다음 milestone (v2.2?) 또는 standalone hotfix 작업으로 처리 가능.
- 영향 영역: `src/SmartRouter.Cli/Program.fs` (또는 `CompositionRoot.fs`의 Kestrel pre-bind hook), `tests/SmartRouter.Tests/` (새 테스트), README §13.

**Estimated effort:** 1 plan, ~3 tasks (probe util + Program.fs wire + tests + README §13 추가).
