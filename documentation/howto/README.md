# Howto Documents

| # | 문서 | 설명 | 작성일 |
|---|------|------|--------|
| 1 | [bypass-concurrency-gated-upstream-with-named-httpclient](bypass-concurrency-gated-upstream-with-named-httpclient.md) | SemaphoreSlim 게이트 우회용 named HttpClient 사이드채널 패턴 | 2026-05-08 |
| 2 | [handle-fsharp-try-with-semicolon-trap](handle-fsharp-try-with-semicolon-trap.md) | `try X with _ -> (); Y`는 Y가 예외 분기에만 실행됨 — 세미콜론 파싱 트랩 | 2026-05-08 |
| 3 | [wire-fsharp-namedhttpclient-with-configurehttpclient](wire-fsharp-namedhttpclient-with-configurehttpclient.md) | F# 람다는 `AddHttpClient(name, lambda)` 오버로드에 안정적으로 안 붙음 — `.ConfigureHttpClient(...)` 체인 사용 | 2026-05-08 |
| 4 | [build-priority-queue-on-semaphoreslim](build-priority-queue-on-semaphoreslim.md) | SemaphoreSlim 위에 priority queue 만들기 — sub-pattern A + 두 큐 + fairness counter | 2026-05-08 |
| 5 | [setup-aspnetcore-config-override-test](setup-aspnetcore-config-override-test.md) | in-process Kestrel 테스트의 IConfiguration 오버라이드 순서 | 2026-05-08 |
| 6 | [debug-kestrel-request-aborted](debug-kestrel-request-aborted.md) | ctx.RequestAborted는 token.Cancel이 아니라 TCP 소켓 close에 발화 | 2026-05-08 |
| 7 | [handle-fsharp-task-finally-disposal](handle-fsharp-task-finally-disposal.md) | F# task{} finally에서 동기/비동기 정리 분기 | 2026-05-08 |

---
총 7개 | 업데이트: 2026-05-08
