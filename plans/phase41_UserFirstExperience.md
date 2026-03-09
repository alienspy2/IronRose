# Phase 41: 유저가 실제 엔진 사용해보기

**작성일**: 2026-03-02
**목표**: 유저가 직접 IronRose 에디터를 사용하여 씬을 만들고, 스크립트를 작성하고, 플레이해본다

---

## 배경

Phase 0~40까지 엔진의 핵심 기능은 모두 구현되었다. 이제 **유저가 직접 앉아서 써보면서** 실제 사용감을 검증하고, 발견되는 문제를 기록하는 단계.

### 사전 조사 결과

모든 핵심 워크플로우가 구현되어 있으며, 유저 사용을 막는 기술적 블로커는 없음:

| 기능 | 상태 | 비고 |
|------|:----:|------|
| Hierarchy 우클릭 → 오브젝트 생성 | ✅ | 3D, Light, Camera, UI 모두 지원 |
| Add Component (LiveCode 포함) | ✅ | 리플렉션 기반 자동 탐색, 핫 리로드 시 캐시 갱신 |
| Play / Stop / Pause | ✅ | Ctrl+P, 씬 상태 저장/복원 |
| 씬 Save / Load | ✅ | Ctrl+S, Ctrl+O, Ctrl+N |
| LiveCode 핫 리로드 | ✅ | FileSystemWatcher + Roslyn 컴파일 |
| 물리 컴포넌트 (Rigidbody, Collider) | ✅ | Add Component에서 추가 가능 |
| 머티리얼 편집 (색상, Metallic, Roughness) | ✅ | Inspector에서 실시간 수정 |
| Standalone 빌드 | ✅ | ProjectSettings.StartScenePath 기반 |
| Console 로그 | ✅ | Debug.Log/Warn/Error → Console 패널 |
| 단축키 (Ctrl+D 복제, Delete 삭제 등) | ✅ | 전부 구현됨 |

---

## 테스트 워크플로우

유저가 아래 순서대로 직접 조작하며 테스트한다. 각 단계에서 발견되는 문제/불편함을 기록.

### WF-1: 첫 실행

```
에디터 실행 → 기본 씬 확인 → 씬 뷰 카메라 조작
```

```bash
dotnet run --project src/IronRose.RoseEditor
```

- [ ] 에디터가 크래시 없이 실행되는가
- [ ] 기본 씬(카메라 + 큐브 + 바닥 + 스팟라이트)이 보이는가
- [ ] PBR 렌더링이 정상인가 (검은 화면, 깨진 라이팅 없는지)
- [ ] 씬 뷰 카메라 조작이 자연스러운가
  - 우클릭 드래그: 회전
  - 중클릭 드래그: 팬
  - 마우스 휠: 줌
  - F: 선택 오브젝트 포커스

### WF-2: 씬 조작

```
오브젝트 선택 → Gizmo로 이동/회전 → Inspector 값 수정 → Undo/Redo
```

- [ ] 클릭으로 오브젝트 선택이 정확한가
- [ ] W/E/R 키로 도구 전환 (이동/회전/크기)
- [ ] Gizmo 드래그로 오브젝트 변환이 되는가
- [ ] Inspector에서 필드 편집이 즉시 반영되는가
- [ ] Ctrl+Z / Ctrl+Shift+Z 로 Undo/Redo가 동작하는가

### WF-3: 오브젝트 생성

```
Hierarchy 우클릭 → 3D Object → Cube → 컴포넌트 추가 → 씬 저장
```

- [ ] Hierarchy 우클릭 → 컨텍스트 메뉴 표시
- [ ] 3D Object → Cube 생성 → 씬 뷰에 즉시 보이는가
- [ ] Light, Camera 등 다른 오브젝트도 생성 가능한가
- [ ] Ctrl+D로 오브젝트 복제가 되는가
- [ ] Delete로 오브젝트 삭제가 되는가
- [ ] Ctrl+S로 씬 저장 → 에디터 재시작 후 로드 확인

### WF-4: 스크립팅 (LiveCode)

```
LiveCode/에 .cs 파일 생성 → MonoBehaviour 작성 → 핫 리로드 → Add Component → Play
```

예시 스크립트 (`LiveCode/RotateTest.cs`):
```csharp
using RoseEngine;

public class RotateTest : MonoBehaviour
{
    public float speed = 90f;

    void Update()
    {
        transform.Rotate(0, speed * Time.deltaTime, 0);
        Debug.Log($"[RotateTest] angle: {transform.eulerAngles.y:F1}");
    }
}
```

- [ ] 파일 저장 후 Console에 Roslyn 컴파일 로그가 뜨는가
- [ ] 컴파일 에러 시 Console에 에러 메시지가 표시되는가
- [ ] Add Component 목록에 `RotateTest`가 나타나는가
- [ ] 오브젝트에 추가 후 Inspector에서 `speed` 필드가 보이는가
- [ ] Play 모드에서 오브젝트가 회전하는가
- [ ] Console에 Debug.Log 출력이 보이는가
- [ ] Play 중 LiveCode 수정 → 즉시 반영 (핫 리로드)

### WF-5: 물리 시뮬레이션

```
큐브에 Rigidbody + BoxCollider 추가 → 바닥에 BoxCollider 추가 → Play → 낙하 확인
```

- [ ] Add Component → Rigidbody가 목록에 있는가
- [ ] Add Component → BoxCollider가 목록에 있는가
- [ ] Play → 큐브가 중력으로 떨어지는가
- [ ] 바닥 Collider와 충돌하여 멈추는가
- [ ] Stop → 씬 상태가 Play 이전으로 복원되는가

### WF-6: 머티리얼 & 렌더링

```
오브젝트 선택 → 머티리얼 속성 수정 → 실시간 반영 확인
```

- [ ] MeshRenderer의 머티리얼 속성이 Inspector에 보이는가
- [ ] 색상 변경이 실시간으로 렌더링에 반영되는가
- [ ] Metallic / Roughness 조절이 반영되는가
- [ ] Skybox / IBL이 정상 표시되는가
- [ ] Post-Processing (Bloom, Tonemap) 효과가 보이는가

### WF-7: Standalone 빌드

```
Project Settings → Start Scene 설정 → Standalone 실행
```

- [ ] Project Settings 패널에서 Start Scene을 지정할 수 있는가
- [ ] 씬에서 사용한 스크립트를 FrozenCode로 이동 (`/digest`)
- [ ] `dotnet run --project src/IronRose.Standalone` 실행 시 정상 동작하는가
- [ ] FrozenCode 스크립트가 Standalone에서 실행되는가

---

## 발견 사항 기록

테스트 중 발견되는 문제를 여기에 기록:

### 버그 (기능이 동작하지 않는 것)

| # | 워크플로우 | 증상 | 심각도 | 해결 여부 |
|---|-----------|------|--------|----------|
| | | | | |

### UX 문제 (동작은 하지만 불편한 것)

| # | 워크플로우 | 설명 | 개선안 |
|---|-----------|------|--------|
| | | | |

### 개선 아이디어 (있으면 좋겠는 것)

| # | 설명 | 우선순위 |
|---|------|---------|
| | | |

---

## 참고 문서

| 문서 | 내용 |
|------|------|
| [`docs/manual/QuickStart.md`](../manual/QuickStart.md) | 에디터 사용법 튜토리얼 |
| [`docs/manual/ShortcutReference.md`](../manual/ShortcutReference.md) | 전체 단축키 레퍼런스 |

---

## 완료 기준

- [ ] WF-1 ~ WF-7 전체 수행 완료
- [ ] 발견된 버그/UX 문제 기록 완료
- [ ] 치명적 버그(크래시, 데이터 손실) 수정 완료
