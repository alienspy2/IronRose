# 플레이모드 종료 시 프리팹 Rigidbody 위치 복원 실패 수정

## 유저 보고 내용
- 플레이모드 종료 시 Rigidbody가 원래 위치로 돌아가지 않는 버그
- 이전 수정(PhysicsManager.Reset + _fixedAccumulator 리셋)으로 해결되지 않음
- TOML 직렬화/역직렬화 자체는 정상이었으나 실제 구체 낙하 시나리오에서 복원 안 됨

## 원인
프리팹 템플릿 GO의 Rigidbody가 물리 시뮬레이션에 참여하여 프리팹 원본의 Transform이 오염되는 것이 근본 원인이었다.

### 상세 경로

1. **프리팹 로드 시 Rigidbody가 전역 리스트에 등록됨**:
   - `PrefabImporter.LoadPrefab()` -> `SceneSerializer.LoadPrefabGameObjectsFromString()` -> `DeserializeGameObjectHierarchy()`
   - GO 생성 시 Rigidbody 컴포넌트가 추가되면 `OnAddedToGameObject()` -> `_rigidbodies.Add(this)` 실행
   - 이후 `SetEditorInternalRecursive(go, true)`로 `_isEditorInternal` 플래그 설정하지만, 이미 `_rigidbodies` 리스트에는 등록된 상태

2. **물리 시뮬레이션이 프리팹 템플릿의 Transform을 변경**:
   - `PhysicsManager.FixedUpdate()` -> `PullPhysicsToTransforms()` -> `IsActiveBody()` 체크
   - `IsActiveBody`가 `_isDestroyed`와 `activeInHierarchy`만 확인하고, `_isEditorInternal`은 체크하지 않았음
   - 프리팹 템플릿 GO도 `activeSelf = true`이므로 물리 시뮬레이션에 참여하여 Transform이 변경됨

3. **플레이모드 종료 시 오염된 템플릿에서 인스턴스 복원**:
   - `StopPlayMode()` -> `SceneSerializer.LoadFromString()` -> `SceneManager.Clear()` -> 씬 재구축
   - 프리팹 인스턴스 복원 시 `AssetDatabase.LoadByGuid<GameObject>()` -> 캐시된 프리팹 템플릿 반환
   - `Object.Instantiate(template)` -> `CloneGameObject()` -> 오염된 localPosition/localRotation을 복사
   - 결과: 물리 시뮬레이션 중 변경된 위치로 복원됨 (원래 위치 아님)

### 진단 로그로 확인된 증거
- 수정 전: `rigidbodies=18` (프리팹 템플릿 10개 + 실제 인스턴스 8개)
- `GetBodyVelocity: body handle X does not exist` 경고 대량 발생
- StopPlayMode:AFTER에서 localPosition이 Play 중 물리가 설정한 값과 동일

## 수정 내용
`PhysicsManager.IsActiveBody()`와 `EnsureStaticColliders()`에서 `_isEditorInternal` GO를 제외하도록 변경.

```csharp
// 수정 전
private static bool IsActiveBody(Component body)
    => !body._isDestroyed && body.gameObject.activeInHierarchy;

// 수정 후
private static bool IsActiveBody(Component body)
    => !body._isDestroyed && !body.gameObject._isEditorInternal && body.gameObject.activeInHierarchy;
```

`EnsureStaticColliders()`에서도 동일하게 `_isEditorInternal` 체크 추가.

## 변경된 파일
- `src/IronRose.Engine/Physics/PhysicsManager.cs` -- `IsActiveBody()`에 `_isEditorInternal` 체크 추가, `EnsureStaticColliders()`에 동일 체크 추가

## 검증
- `dotnet build` 빌드 성공 (경고 0, 기존 경고만 존재)
- 자동화 테스트 (physics_test.scene): bodies=8 (프리팹 템플릿 제외), GetBodyVelocity 경고 0건
- 자동화 테스트 (aa.scene): 비-프리팹 씬에서도 정상 동작 확인

## 주의사항
- 프리팹 템플릿 GO는 `_isEditorInternal = true`로 마킹되지만, `Rigidbody._rigidbodies` 등 전역 컴포넌트 리스트에는 여전히 등록됨
- 향후 프리팹 템플릿의 컴포넌트가 다른 전역 리스트를 통해 예기치 않게 동작하는 유사 문제가 발생할 수 있음
- `LoadPrefabGameObjectsFromString()`에서 `SetEditorInternalRecursive()` 전에 `DeserializeGameObjectHierarchy()`가 실행되므로, 컴포넌트의 `OnAddedToGameObject()`에서 `_isEditorInternal` 플래그를 체크할 수 없음
- 근본적 해결을 위해서는 프리팹 템플릿 로드 시 `_isEditorInternal` 플래그를 먼저 설정하거나, 전역 리스트 등록을 지연시키는 구조 변경이 필요
