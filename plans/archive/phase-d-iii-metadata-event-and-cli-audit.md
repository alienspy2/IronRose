# Phase D-III: RoseMetadata.OnSaved thread-safe 이벤트 + CLI 감사

## 목표

마스터 계획 `plans/threading-safety-fix-master.md` §Phase D (C4)의 **세 번째/마지막 서브 phase**. 다음을 수행한다.

- `RoseMetadata.OnSaved` static 이벤트를 **explicit add/remove accessor + 전용 lock** 패턴으로 재작성. Invoke 시 로컬 변수 복사 후 호출하여 구독자 제거와 Invoke 경합 제거.
- `CliCommandDispatcher` 모든 핸들러를 감사하여, 씬/에셋 접근이 전부 `ExecuteOnMainThread` 람다 안에 있는지 확인하고 결과를 문서화한다.
- (선택) `ping` 처럼 씬을 건드리지 않는 핸들러는 예외로 유지하되 주석으로 "씬 접근 금지" 를 명시한다.

**이 Phase가 건드리지 않는 것**:
- `_all*` 리스트 및 외부 순회자(Phase D-I, D-II 에서 처리 완료).
- Phase A/B/C 범위.

## 선행 조건

- **Phase A 머지 완료**: ThreadGuard 사용 가능.
- **Phase B 머지 완료**: `OnRoseMetadataSaved` 핸들러가 이미 메인 큐잉(`_metadataSavedQueue`)으로 전환되어 있음. `RoseMetadata.OnSaved?.Invoke(...)` 는 FSW/백그라운드에서도 호출될 수 있으나, AssetDatabase 쪽 핸들러는 안전하게 enqueue 만 수행.
- **Phase C 머지 완료**: CLI dispatcher 안정화.
- **Phase D-I / D-II 머지 완료** 권장 (독립 가능하지만 일관성을 위해 순서 유지).

## Worktree 전략

- **단일 worktree**: `feat/phase-d-iii-metadata-event`.
- 수정 범위가 작으므로 한 번의 코더 호출로 완료.

---

## 배경: 현재 코드 구조 (aca-coder가 파일을 다시 열지 않아도 되도록)

### `RoseMetadata.cs` 현재 상태

파일 경로: `src/IronRose.Engine/AssetPipeline/RoseMetadata.cs`.

**관련 선언** (line 68):
```csharp
/// <summary>
/// .rose 파일이 저장될 때 발생. 인자는 에셋 경로 (.rose 제외).
/// AssetDatabase가 구독하여 자동 reimport 처리.
/// </summary>
public static event Action<string>? OnSaved;
```

**Invoke 지점** (line 101-102, `Save` 메서드 내부):
```csharp
if (rosePath.EndsWith(".rose", StringComparison.OrdinalIgnoreCase))
    OnSaved?.Invoke(rosePath[..^5]);
```

이 `Save()` 는 다음 경로에서 호출될 수 있다:
1. **메인 스레드**: 에디터 UI/Inspector 에서 `.rose` 저장 (예: `ImGuiSpriteEditorPanel.cs:572`).
2. **메인 스레드**: `AssetDatabase.RegisterSubAssets` / `RegisterSpriteSubAssets` 등 reimport 경로.
3. **백그라운드 Task**: `Texture2DImporter` 같은 임포터가 내부에서 `.rose` 를 수정 후 Save 할 수 있음 (Phase B 에서 임포트 경로의 일부는 백그라운드).

따라서 **OnSaved Invoke 가 동시에 여러 스레드에서 발생할 수 있으며**, 구독/해제도 `AssetDatabase.ScanAssets` (메인), `AssetDatabase.UnloadAll` (메인) 에서 이루어진다.

### 현재 코드의 위험

C# 의 field-like event (`public static event Action<string>? OnSaved;`) 는 컴파일러가 자동으로 lock 을 생성하지만, **Invoke 는 lock 을 쓰지 않는다**. `OnSaved?.Invoke(...)` 는 다음 두 줄로 컴파일된다:

```csharp
var tmp = OnSaved;   // race: 이 사이에 `OnSaved = null` 될 수 있음 (이론상)
tmp?.Invoke(...);
```

일반적으로 field-like event 의 add/remove 는 `Interlocked.CompareExchange` 로 스레드 안전하지만, **구독자가 실행 중일 때 다른 스레드가 해제하면** 이미 snapshot 된 delegate 가 불리는 "이미 해제된 구독자가 호출되는" 문제가 존재한다. IronRose 에서 `AssetDatabase.UnloadAll` 이 구독을 해제할 때 다른 스레드에서 Invoke 가 진행 중이면, 해제된 AssetDatabase 인스턴스의 `OnRoseMetadataSaved` 가 호출될 수 있다.

### 구독자 목록 (AssetDatabase 1개)

`src/IronRose.Engine/AssetPipeline/AssetDatabase.cs`:
- line 200-201 (`ScanAssets` 내부): `RoseMetadata.OnSaved -= OnRoseMetadataSaved; RoseMetadata.OnSaved += OnRoseMetadataSaved;`
- line 1419 (`UnloadAll` 내부): `RoseMetadata.OnSaved -= OnRoseMetadataSaved;`

이외 구독자 없음 (grep 완료).

### CLI 감사 사전 작업 (요약)

`src/IronRose.Engine/Cli/CliCommandDispatcher.cs` 와 `CliCommandDispatcher.UI.cs` 의 핸들러 목록을 훑어 **씬/에셋 접근이 `ExecuteOnMainThread` 람다 내부인지** 확인:

- `CliCommandDispatcher.cs` 본체: 약 **145개 핸들러** 중 `ping` (line 172) 만 백그라운드에서 실행. 나머지는 모두 `ExecuteOnMainThread` 감싸짐.
- `CliCommandDispatcher.UI.cs`: **90+개 핸들러** 모두 `ExecuteOnMainThread` 래핑됨 (line 43, 64, 94, ..., 2825). 파일 헤더 주석(line 14)에 "모든 UI 조작은 ExecuteOnMainThread() 내부에서 수행" 명시.
- Private helper `FindGameObject`, `FindGameObjectById` (line 2822-2845) 는 `SceneManager.AllGameObjects` 를 순회하지만 호출자가 모두 `ExecuteOnMainThread` 람다 내부임을 확인.

**결론**: CLI 감사 결과 **모든 씬/에셋 접근이 안전**. Phase D-III 에서는 문서화만 한다.

---

## 생성할 파일

없음.

---

## 수정할 파일

### `src/IronRose.Engine/AssetPipeline/RoseMetadata.cs`

- **변경 내용**:
  1. 파일 상단 주석(line 15)의 export 섹션 갱신:
     - 기존: `static OnSaved: event Action<string>                      — 저장 시 이벤트`
     - 변경 후: `static OnSaved: event Action<string>                      — 저장 시 이벤트 (thread-safe accessor, lock 기반 add/remove)`

  2. `OnSaved` 이벤트 선언 교체 (line 64-68 영역):

  ```csharp
  // --- thread-safe event accessor (C4) ---
  // FSW/백그라운드에서 Invoke 가능하고, AssetDatabase.ScanAssets / UnloadAll 이
  // 메인에서 add/remove. Invoke 시 로컬 변수로 snapshot 후 호출하여
  // "구독 해제 중 invoke" 레이스를 제거한다.
  private static Action<string>? _onSaved;
  private static readonly object _onSavedLock = new();

  /// <summary>
  /// .rose 파일이 저장될 때 발생. 인자는 에셋 경로 (.rose 제외).
  /// AssetDatabase가 구독하여 자동 reimport 처리.
  /// 구독/해제는 어느 스레드에서든 안전. Invoke 는 구독 시점의 스냅샷을 사용.
  /// </summary>
  public static event Action<string>? OnSaved
  {
      add    { lock (_onSavedLock) { _onSaved += value; } }
      remove { lock (_onSavedLock) { _onSaved -= value; } }
  }
  ```

  3. `Save` 메서드의 Invoke 부분 (line 96-103) 교체:

  ```csharp
  public void Save(string rosePath)
  {
      var config = ToConfig();
      config.SaveToFile(rosePath);

      if (rosePath.EndsWith(".rose", StringComparison.OrdinalIgnoreCase))
      {
          // Invoke 시 snapshot 후 lock 밖 호출 (핸들러가 다시 Save 를 부를 수 있으므로
          // 락을 보유한 채 Invoke 하면 재진입은 허용되지만 경합 창을 넓힌다).
          Action<string>? snap;
          lock (_onSavedLock) { snap = _onSaved; }
          snap?.Invoke(rosePath[..^5]);
      }
  }
  ```

- **이유**: C4 (field-like event 의 구독/해제 시점 경합). explicit accessor 로 race 완전 제거.

- **구현 주의**:
  - `_onSaved` 는 multicast delegate 로, `+=` 는 내부적으로 새 delegate 를 생성하고 참조를 교체한다. lock 안에서 `_onSaved += value` 하면 안전.
  - Invoke 시 **snap 은 immutable** 이므로 lock 밖에서 호출해도 안전. 구독 해제가 이미 감지됐어도 이미 snap 된 delegate 는 호출된다 — 이는 문제 없음 (구독자가 자신의 `_isDestroyed` 등을 확인하면 됨).
  - 기존 field-like event 가 C# 컴파일러가 암시적으로 만들어주던 `+ remove` 메서드는 explicit accessor 를 선언하면 자동으로 사라진다. 필드 이름도 **변경됨** (`OnSaved` → `_onSaved` private field, `OnSaved` 는 event 로 남음). 따라서 **컴파일 시점에 외부에서 `RoseMetadata.OnSaved = null` 같은 직접 대입이 있었는지 확인** — grep 결과 없음(구독/해제만 사용).

---

## 추가: CLI 감사 주석 보강

### `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`

`ping` 핸들러 (line 170-172) 위에 다음 주석 추가:

**현재 (line 169-172)**:
```csharp
// ----------------------------------------------------------------
// ping -- 백그라운드 스레드에서 직접 실행
// ----------------------------------------------------------------
_handlers["ping"] = args => JsonOk(new { pong = true, project = ProjectContext.ProjectName });
```

**변경 후**:
```csharp
// ----------------------------------------------------------------
// ping -- 백그라운드 스레드에서 직접 실행 (ExecuteOnMainThread 래핑 없음)
// 이 핸들러에서는 **씬/에셋/컴포넌트 레지스트리에 절대 접근 금지**.
// 접근이 필요한 신규 기능은 ExecuteOnMainThread 람다 안에 넣어야 한다. (Phase D-III 감사)
// ----------------------------------------------------------------
_handlers["ping"] = args => JsonOk(new { pong = true, project = ProjectContext.ProjectName });
```

**이유**: 향후 기여자가 `ping` 을 확장할 때 실수로 씬을 건드리지 않도록 강제 가이드.

### `src/IronRose.Engine/Cli/CliCommandDispatcher.UI.cs`

파일 헤더(line 14)의 기존 주석 `// @note    모든 UI 조작은 ExecuteOnMainThread() 내부에서 수행.` 는 이미 존재. **변경 불필요**.

---

## 구현 체크리스트

- [ ] `RoseMetadata.OnSaved` 가 explicit add/remove accessor 로 교체됐는가?
- [ ] `_onSaved` private 필드 + `_onSavedLock` private 필드가 추가됐는가?
- [ ] `Save` 메서드에서 snapshot 후 lock 밖 Invoke 패턴이 적용됐는가?
- [ ] `AssetDatabase.cs:200-201, 1419` 의 구독/해제 코드는 그대로 동작하는가? (API 는 불변)
- [ ] CLI `ping` 핸들러 주석이 "씬 접근 금지" 를 명시하는가?
- [ ] `dotnet build` 성공?

---

## NuGet 패키지 (해당 시)

- 없음.

---

## 검증 기준

- [ ] `dotnet build` 성공.
- [ ] 에디터 기동 → AssetDatabase 가 `OnSaved` 구독 성공.
- [ ] Inspector 에서 Sprite 편집 후 `.rose` 저장 → Reimport 트리거 정상 (AssetDatabase.ProcessMetadataSavedQueue 경로).
- [ ] 에디터 종료 시 `UnloadAll` 이 구독 해제 → crash 없음.
- [ ] 텍스처 임포트 중 씬 전환 → .rose Save 와 구독 해제 경합 시나리오에서 NRE/예외 없음.

### 스모크 테스트

1. AI 이미지 생성 백그라운드 Task 가 `.rose` 저장 → 메인 스레드 ProcessMetadataSavedQueue 에서 dequeue → Reimport 정상.
2. 에디터 종료 → `UnloadAll` 호출 → 백그라운드 임포트가 `.rose` 저장 중이어도 crash 없음.
3. 빠른 scene.new/scene.load 반복 (CLI) → 구독 해제와 Invoke 경합 시 crash 없음.

---

## 참고

- **성능**: `OnSaved` Invoke 는 `.rose` 저장 시에만 발생(저빈도). lock 오버헤드 무시 가능.
- **대안 고려**: `ImmutableList<Action<string>>` 또는 `Interlocked.Exchange` 기반 lock-free 패턴도 가능하나, IronRose 의 저빈도 이벤트에서는 **lock 이 가장 단순하고 정확**.
- **미결 사항**: 향후 다른 static event (예: `SceneManager.OnSceneLoaded` 같은 것이 추가된다면) 도 동일한 explicit accessor 패턴을 사용해야 한다. 엔진 컨벤션으로 문서화 필요 (후속 PR 혹은 docs).
- **CLI 감사 결과 전체**:

| 범주 | 핸들러 수 | `ExecuteOnMainThread` 래핑 | 비고 |
|------|-----------|---------------------------|------|
| `CliCommandDispatcher.cs` 본체 | 약 145개 | 144개 | `ping` 만 예외 (씬 접근 없음). |
| `CliCommandDispatcher.UI.cs` | 약 90개 | 전부 | 파일 헤더 주석에 명시. |
| Private helpers (`FindGameObject*`, `BuildTreeNode` 등) | 내부용 | 호출자가 모두 메인 람다 내부 | 메인 스레드 보장. |

**감사 체크 지침 (본 Phase 에서 완료)**: 신규 핸들러 추가 시 반드시 `ExecuteOnMainThread` 로 감싸야 한다. 이를 강제하려면 후속 Phase 에서 `CliCommandDispatcher.DispatchSceneCommand(Func<string>)` 헬퍼 도입이 유용하나, 본 Phase 범위 밖. 마스터 계획 §Phase D-3 에서 "helper 도입 고려" 로 명시됨.
