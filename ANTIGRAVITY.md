# IronRose 프로젝트 개발 가이드라인

## 디자인 가이드라인

### 메인 테마 색상

**IronRose Theme Color**: 금속의 백장미 (Metallic White Rose)

```csharp
// RGB: (230, 220, 210) - 은은한 베이지 톤
// 정규화: (0.902, 0.863, 0.824)
// Hex: #E6DCD2

// Veldrid 사용 예시
var ironRoseColor = new RgbaFloat(0.902f, 0.863f, 0.824f, 1.0f);

// Unity 스타일 Color32
var ironRoseColor32 = new Color32(230, 220, 210, 255);
```

**색상 설명**:
- 백장미의 우아하고 은은한 흰색
- 금속의 차가운 광택감
- RGB로 표현 시 따뜻한 베이지 톤
- 배경, UI 기본 색상, 엔진 로고 등에 사용

**보조 색상** (추후 정의):
- 어둡게: 회색 톤 (금속 그림자)
- 밝게: 순백색 (하이라이트)
- 강조: 장미의 붉은색 (액센트)

---

## Antigravity 서브 에이전트 스타일 워크플로우

Antigravity는 단일 에이전트이지만, **Skills**와 **Workflows**를 결합하여 Claude의 서브 에이전트(역할 분할) 방식을 재현합니다. 다음 슬래시 커맨드를 통해 특정 역할의 전문 지침을 로드할 수 있습니다.

| 명령어 | 역할 (페르소나) | 지침 경로 (Skill) | 용도 |
|--------|----------------|-------------------|------|
| `/plan` | **Architect** | `.agents/skills/roles/architect/` | 아키텍처 설계, Phase 계획 수립 |
| `/fix` | **Fixer** | `.agents/skills/roles/fixer/` | 디버깅, 버그 수정, 진단 로그 기록 |

**사용 방법**:
- 대화 창에 `/plan` 또는 `/fix`를 입력하면 Antigravity가 해당 역할의 전용 지침(SKILL.md)을 즉시 읽고 작업을 시작합니다.
- 각 역할은 작업 결과를 아티팩트(`implementation_plan.md`, `task.md`) 또는 로그 파일(`making_log/`, `plans/`)로 남겨 작업의 연속성을 보장합니다.

---

## 코딩 스타일

### 크로스 플랫폼
- **파일 경로**: 항상 `Path.Combine()`을 사용. `"foo/bar"` 또는 `"foo\\bar"` 금지.
- **줄 끝**: LF 기본 (`.editorconfig`, `.gitattributes` 참조)

### 인코딩
- **C# 소스 파일(.cs)**: UTF-8 with BOM 사용 (사용자 전역 설정 준수)
- **.editorconfig**에 명시되어 자동 적용됨

### 네이밍 컨벤션
- Unity와 유사한 C# 표준 컨벤션 사용
- 클래스/메서드: PascalCase
- 필드/변수: camelCase
- 상수: UPPER_CASE

---

## Unity와의 차이점

### 스크립트 파일 위치 제한
- **IronRose에서는 `Assets/` 폴더에 `.cs` 파일을 추가할 수 없음**
- 모든 C# 스크립트는 반드시 **LiveCode** 또는 **FrozenCode** 프로젝트에만 추가해야 함

### Prefab Override 미지원
- **IronRose에서는 Prefab 인스턴스의 값 override를 지원하지 않음**
- 값을 변경하려면 반드시 Prefab Variant를 생성하여 Variant에서 원하는 값을 수정해야 함

---

## 프로젝트 디렉토리 구조

```
IronRose/
├── .agents/                         # Antigravity 워크플로우 및 커스텀 에이전트 커맨드
│   └── workflows/                   # 내장 워크플로우 (plan.md, fix.md, worktree.md 등)
├── .antigravity/                    # 자동화 테스트 명령 및 출력
├── Assets/                          # 게임 에셋
├── Shaders/                         # GLSL 셰이더
├── src/
│   ├── IronRose.Engine/             # 엔진 코어
│   ├── IronRose.RoseEditor/         # 에디터 프로젝트
│   └── IronRose.Standalone/        # Standalone 빌드
├── making_log/                      # 작업/수정 로그 기록
└── docs/                            # 프로젝트 문서 파일들
```

---

## Git Worktree 병렬 작업 가이드

여러 Gemini Agent가 동시에 독립된 작업 디렉토리에서 작업할 수 있도록 Git Worktree를 동적으로 활용합니다.

### 워크플로우 (Dynamic Process)

1. **공간 생성**: 작업 시작 전 `./worktree-create.sh [작업명]` 실행
2. **독립 작업**: 생성된 `../IronRose-worktrees/[작업명]` 디렉토리로 이동하여 코드 수정 및 테스트
3. **병합/정리**: 작업 완료(Push/Merge) 후 `./worktree-delete.sh [작업명]`으로 공간 삭제

### 관리 스크립트

| 스크립트 | 역할 |
|----------|------|
| `worktree-create.sh` | 새 작업 브랜치(`wt-[이름]`)와 독립 디렉토리 생성 |
| `worktree-sync.sh` | 현재 열려있는 모든 작업 공간을 main 최신으로 동기화 |
| `worktree-delete.sh` | 특정 작업 공간과 브랜치 삭제 |

또는 `/worktree` 슬래시 커맨드를 사용하여 대화형으로 관리할 수 있습니다.

### 권장 수칙

- **작업 명명**: `fix-ui`, `feature-vulkan` 등 식별 가능한 이름을 권장합니다.
- **주기적 동기화**: `worktree-sync.sh`를 자주 실행하여 충돌을 미리 방지하세요.
- **수동 정리**: 작업이 끝난 공간은 리소스 확보를 위해 즉시 삭제하는 것이 좋습니다.
