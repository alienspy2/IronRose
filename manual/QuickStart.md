# IronRose Quick Start Guide

## 1. 에디터 실행

```bash
dotnet run --project src/IronRose.RoseEditor
```

## 2. 에디터 화면 구성

| 패널 | 설명 |
|------|------|
| **Hierarchy** | 씬의 오브젝트 목록. 우클릭으로 새 오브젝트 생성 |
| **Inspector** | 선택된 오브젝트의 컴포넌트와 속성 편집 |
| **Scene View** | 3D 뷰포트. 오브젝트를 시각적으로 배치/조작 |
| **Game View** | 실제 게임 카메라 시점의 렌더링 결과 |
| **Project** | 에셋 브라우저. 모델, 텍스처, 씬 파일 탐색 |
| **Console** | 로그 출력 (Info/Warning/Error) |

## 3. 첫 번째 씬 만들기

1. **새 씬 생성**: `Ctrl+N` 또는 File → New Scene
2. **바닥 만들기**: Hierarchy 우클릭 → 3D Object → Plane
3. **큐브 올리기**: Hierarchy 우클릭 → 3D Object → Cube
   - Inspector에서 Position Y를 `0.5`로 수정 (바닥 위에 놓기)
4. **라이트 추가**: Hierarchy 우클릭 → Light → Directional Light
5. **카메라 확인**: 기본 카메라가 없으면 Hierarchy 우클릭 → Camera
   - Scene View에서 원하는 앵글을 잡은 후 카메라 위치 조정
6. **씬 저장**: `Ctrl+S` → 파일명 지정

## 4. 첫 번째 스크립트 작성

`LiveCode/` 디렉토리에 `.cs` 파일을 생성하면 Roslyn이 자동으로 컴파일합니다.

### MonoBehaviour 기본 구조

```csharp
using RoseEngine;

public class MyFirstScript : MonoBehaviour
{
    public float speed = 3.0f;

    void Start()
    {
        Debug.Log("[MyScript] Start!");
    }

    void Update()
    {
        // 매 프레임 Y축으로 회전
        transform.Rotate(0, speed * Time.deltaTime * 60f, 0);
    }
}
```

### 스크립트를 오브젝트에 붙이기

1. Hierarchy에서 오브젝트 선택
2. Inspector 하단의 **Add Component** 클릭
3. 목록에서 `MyFirstScript` 선택
4. Inspector에서 `speed` 값을 수정할 수 있음

### 실행 확인

- **Play** 버튼 클릭 (또는 `Ctrl+P`)
- Game View에서 큐브가 회전하는지 확인
- **Stop** 버튼으로 정지 → 씬 상태가 Play 이전으로 복원됨

## 5. 물리 추가하기

1. 큐브를 선택하고 **Add Component** → `Rigidbody`
2. 바닥(Plane)을 선택하고 **Add Component** → `BoxCollider` (없으면 추가)
3. Play → 큐브가 중력으로 떨어져 바닥에 착지

### 힘 가하기 (스크립트)

```csharp
using RoseEngine;

public class JumpOnSpace : MonoBehaviour
{
    public float jumpForce = 5.0f;
    private Rigidbody rb;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            rb.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
        }
    }
}
```

## 6. 핫 리로드 활용하기

IronRose의 핵심 기능 중 하나는 **LiveCode 핫 리로드**입니다.

### 워크플로우

```
LiveCode/*.cs 수정 → 자동 감지 → Roslyn 컴파일 → 즉시 반영
```

- Play 모드 중에도 스크립트 수정이 실시간 반영됩니다
- 컴파일 에러가 있으면 Console 패널에 표시됩니다

### FrozenCode로 편입

LiveCode에서 충분히 테스트한 스크립트는 FrozenCode로 옮겨 안정화합니다:

```
/digest
```

이 커맨드는 LiveCode의 `.cs` 파일을 `FrozenCode/` 프로젝트로 이동시킵니다.

### Standalone 빌드

```bash
dotnet run --project src/IronRose.Standalone
```

- Project Settings 패널에서 Start Scene을 지정
- FrozenCode에 편입된 스크립트가 Standalone에서 동작합니다

## 7. 주요 단축키

[전체 단축키 목록](ShortcutReference.md) 참조.
