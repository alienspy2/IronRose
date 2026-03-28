# HideInConsoleStackTrace

콘솔 패널에서 로그를 더블클릭하면 해당 로그를 출력한 소스 코드 위치로 이동합니다.
`Debug.Log`를 래핑하는 커스텀 로거를 사용하는 경우, 더블클릭 시 래퍼 내부가 아닌 실제 호출 위치로 이동하려면 `[HideInConsoleStackTrace]` 어트리뷰트를 사용하세요.

## 클래스에 적용

클래스에 적용하면 해당 클래스의 모든 메서드가 스택트레이스에서 건너뜀 대상이 됩니다.

```csharp
using RoseEngine;

[HideInConsoleStackTrace]
public static class MyLogger
{
    public static void Info(string msg) => Debug.Log($"[MyGame] {msg}");
    public static void Warn(string msg) => Debug.LogWarning($"[MyGame] {msg}");
    public static void Error(string msg) => Debug.LogError($"[MyGame] {msg}");
}
```

## 메서드에 적용

특정 메서드만 건너뛰고 싶을 때 사용합니다.

```csharp
using RoseEngine;

public static class GameUtil
{
    [HideInConsoleStackTrace]
    public static void LogState(string state) => Debug.Log($"[State] {state}");

    // 이 메서드는 어트리뷰트가 없으므로 더블클릭 시 여기로 이동
    public static void DoSomething() => Debug.Log("doing something");
}
```
