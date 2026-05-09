# Howto Documents

| # | 문서 | 설명 | 작성일 |
|---|------|------|--------|
| 1 | [avoid-closure-capture-of-mutable-state](avoid-closure-capture-of-mutable-state.md) | Closure 가 가변 값을 capture 하면 source 가 바뀌어도 stale — 값 대신 provider 인터페이스를 받아서 per-call live read | 2026-05-10 |
| 2 | [handle-singleton-of-scoped-via-scope-factory](handle-singleton-of-scoped-via-scope-factory.md) | Singleton 이 Scoped 서비스를 root provider 에서 resolve 하면 ValidateScopes 가 거부 — IServiceScopeFactory 받아서 per-call scope 안에서 resolve | 2026-05-10 |
| 3 | [resolve-paths-relative-to-binary-not-cwd](resolve-paths-relative-to-binary-not-cwd.md) | dotnet run 의 CWD 는 project dir — repo root 의 models/ 를 안 봄. AppContext.BaseDirectory + walk-up 으로 CWD 무관하게 resolve | 2026-05-10 |
| 4 | [test-di-scopes-with-validatescopes](test-di-scopes-with-validatescopes.md) | BuildServiceProvider(ServiceProviderOptions(ValidateScopes=true)) 를 unit test 에서 사용해 prod-only DI 수명 위반을 잡아낸다 | 2026-05-10 |
| 5 | [force-task-yield-in-fake-async-doubles](force-task-yield-in-fake-async-doubles.md) | Fake async double에 `Task.Yield()`를 강제해야 동시성 race가 관찰됨 (`Task.FromResult`는 sync 완료) | 2026-05-09 |
| 6 | [order-mlnet-traintest-split-before-fit](order-mlnet-traintest-split-before-fit.md) | ML.NET 검증 파이프라인은 `TrainTestSplit` → `Fit` 순서가 필수 — held-out 오염과 baseline 비교 불공정 방지 | 2026-05-09 |
| 7 | [propagate-cancellation-through-fsharp-task-trywith](propagate-cancellation-through-fsharp-task-trywith.md) | F# `task{}` 안 try/with에서 `reraise()`는 FS0413 — `ExceptionDispatchInfo.Capture(ex).Throw()`로 우회 | 2026-05-09 |
| 8 | [use-semaphoreslim-not-mutex-for-async-idempotency](use-semaphoreslim-not-mutex-for-async-idempotency.md) | 비동기 idempotency 게이트는 `SemaphoreSlim(1,1).Wait(0)` — `Mutex`는 thread affinity로 task{} await 깨짐 | 2026-05-09 |
| 9 | [bypass-concurrency-gated-upstream-with-named-httpclient](bypass-concurrency-gated-upstream-with-named-httpclient.md) | SemaphoreSlim 게이트 우회용 named HttpClient 사이드채널 패턴 | 2026-05-08 |
| 10 | [handle-fsharp-try-with-semicolon-trap](handle-fsharp-try-with-semicolon-trap.md) | `try X with _ -> (); Y`는 Y가 예외 분기에만 실행됨 — 세미콜론 파싱 트랩 | 2026-05-08 |
| 11 | [wire-fsharp-namedhttpclient-with-configurehttpclient](wire-fsharp-namedhttpclient-with-configurehttpclient.md) | F# 람다는 `AddHttpClient(name, lambda)` 오버로드에 안정적으로 안 붙음 — `.ConfigureHttpClient(...)` 체인 사용 | 2026-05-08 |
| 12 | [build-priority-queue-on-semaphoreslim](build-priority-queue-on-semaphoreslim.md) | SemaphoreSlim 위에 priority queue 만들기 — sub-pattern A + 두 큐 + fairness counter | 2026-05-08 |
| 13 | [setup-aspnetcore-config-override-test](setup-aspnetcore-config-override-test.md) | in-process Kestrel 테스트의 IConfiguration 오버라이드 순서 | 2026-05-08 |
| 14 | [debug-kestrel-request-aborted](debug-kestrel-request-aborted.md) | ctx.RequestAborted는 token.Cancel이 아니라 TCP 소켓 close에 발화 | 2026-05-08 |
| 15 | [handle-fsharp-task-finally-disposal](handle-fsharp-task-finally-disposal.md) | F# task{} finally에서 동기/비동기 정리 분기 | 2026-05-08 |

---
총 15개 | 업데이트: 2026-05-10
