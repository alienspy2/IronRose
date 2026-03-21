---
description: MyGame/LiveCode에 새 스크립트를 추가합니다
argument-hint: <스크립트 설명>
---

사용자가 설명한 기능을 수행하는 LiveCode 스크립트를 작성합니다.

## LiveCode란

- **위치**: `/home/alienspy/git/MyGame/LiveCode/` 디렉토리
- **역할**: 런타임에 Roslyn으로 컴파일되며, FileSystemWatcher가 파일 변경을 감지하여 **핫 리로드** 지원
- **용도**: 빠른 프로토타이핑, 테스트, 실험적 기능 개발
- **승격**: 검증이 끝난 LiveCode는 `/digest` 명령으로 FrozenCode로 이동 가능

## 스크립트 작성 규칙

### 기본 구조
```csharp
using RoseEngine;

public class 클래스이름 : MonoBehaviour
{
    public override void Start() { }
    public override void Update() { }
}
```

### 필수 사항
- **인코딩**: UTF-8 with BOM (파일 첫 바이트에 `\xEF\xBB\xBF`)
- **네임스페이스**: `using RoseEngine;` 필수
- **상속**: `MonoBehaviour` 상속
- **라이프사이클 메서드**: `override` 키워드 필수 (Start, Update, Awake 등)
- **파일명**: 클래스 이름과 동일한 PascalCase `.cs` 파일

### MonoBehaviour 라이프사이클
- `Awake()` — 인스턴스 생성 시
- `OnEnable()` — 컴포넌트 활성화 시
- `Start()` — 첫 Update 전
- `Update()` — 매 프레임
- `FixedUpdate()` — 고정 타임스텝
- `LateUpdate()` — Update 후
- `OnDisable()` — 컴포넌트 비활성화 시
- `OnDestroy()` — GameObject 파괴 시

### 주요 API

**컴포넌트 접근**:
- `GetComponent<T>()` — 같은 GameObject의 컴포넌트
- `GetComponentInChildren<T>()` / `GetComponentInParent<T>()`

**렌더링**:
- `MeshRenderer` — `material` 프로퍼티로 Material 접근
- `Material` — `color`, `emission`, `metallic`, `roughness` 등 PBR 속성
- `SpriteRenderer` — `color` 프로퍼티 직접 접근

**색상**:
- `Color(r, g, b, a)` — 0~1 float 범위
- `Color.HSVToRGB(h, s, v)` / `Color.RGBToHSV(color, out h, out s, out v)`
- `Color.Lerp(a, b, t)` — 선형 보간
- 사전 정의: `Color.white`, `Color.black`, `Color.red`, `Color.green`, `Color.blue`

**시간**:
- `Time.deltaTime` — 프레임 간 경과 시간
- `Time.unscaledDeltaTime` — timeScale 무시 경과 시간
- `Time.time` — 게임 시작 이후 경과 시간

**입력/물리**:
- 3D 충돌: `OnCollisionEnter/Stay/Exit(Collision)`
- 3D 트리거: `OnTriggerEnter/Stay/Exit(Collider)`
- 2D 충돌: `OnCollisionEnter2D/Stay2D/Exit2D(Collision2D)`
- 2D 트리거: `OnTriggerEnter2D/Stay2D/Exit2D(Collider2D)`

**코루틴**:
- `StartCoroutine(IEnumerator)` / `StopCoroutine(Coroutine)`
- `StopAllCoroutines()`

**기타**:
- `Debug.Log(message)` — 콘솔 로그 (`using RoseEngine` 필요)
- `PlayerPrefs` — 데이터 저장/불러오기
- `Application.persistentDataPath` / `Application.dataPath`
- `Invoke(methodName, delay)` / `InvokeRepeating(methodName, delay, interval)`

### 네이밍 컨벤션
- 클래스/메서드: PascalCase
- 필드/변수: camelCase
- 상수: UPPER_CASE

## 절차

1. `doc/CodingGuide.md`를 읽습니다.
2. 사용자의 설명(`$ARGUMENTS`)을 분석하여 필요한 기능을 파악합니다.
3. 필요한 엔진 API가 있다면 `src/IronRose.Engine/RoseEngine/` 에서 해당 클래스를 확인합니다.
4. `/home/alienspy/git/MyGame/LiveCode/`에 UTF-8 BOM 인코딩으로 `.cs` 파일을 작성합니다.
5. `cd /home/alienspy/git/MyGame && dotnet build LiveCode/LiveCode.csproj`로 빌드를 검증합니다.
6. 빌드 실패 시 에러를 수정하고 재빌드합니다.
7. 결과를 보고합니다 (파일명, 용도, 사용법).

## 규칙

- Assets/ 폴더에 .cs 파일을 만들지 않습니다.
- `File.WriteAllText`, `File.AppendAllText` 등으로 로그 파일을 생성하지 않습니다. 로그는 `Debug.Log`만 사용합니다.
- `ImGui.DragFloat` 직접 사용 금지 — Inspector 필드는 `DragFloatClickable` 등 헬퍼 사용합니다.
- 빌드 에러는 반드시 수정합니다. 워닝도 가능하면 수정합니다.
