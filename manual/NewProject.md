# 새 프로젝트 만들기

## 1. 에디터에서 프로젝트 생성

1. 에디터 실행: `dotnet run --project src/IronRose.RoseEditor`
2. 시작 화면에서 **New Project** 클릭
3. 프로젝트 이름과 위치 지정 → **Create**

### 생성되는 폴더 구조

```
MyGame/
├── Assets/
│   ├── Fonts/
│   ├── Scenes/
│   └── Settings/
├── FrozenCode/
│   └── FrozenCode.csproj
├── LiveCode/
│   └── LiveCode.csproj
├── RoseCache/
├── MyGame.code-workspace ← 엔진+게임 멀티루트 워크스페이스 (자동 생성, gitignore)
├── MyGame.sln            ← 엔진+게임 통합 솔루션 (자동 생성, gitignore)
├── Directory.Build.props
├── project.toml
├── rose_projectSettings.toml
└── .gitignore
```

## 2. VS Code로 열기

프로젝트 생성 후 재시작 안내 다이얼로그에 표시되는 워크스페이스 파일로 엽니다:

```bash
code MyGame/MyGame.code-workspace
```

이 워크스페이스는 **게임 프로젝트 폴더 안에** 생성되며, 엔진 폴더와 게임 폴더를 멀티루트로 묶고, `dotnet.defaultSolution`을 `MyGame.sln`로 설정하여 엔진 + 게임 전체에 IntelliSense가 동작합니다.

> **주의**: 게임 폴더만 단독으로 열면 엔진 코드의 IntelliSense가 제한됩니다. 반드시 워크스페이스 파일로 여세요.

## 3. .sln 파일 설명

| 파일 | 위치 | 내용 | git |
|------|------|------|-----|
| `IronRose.sln` | 엔진 repo | 엔진 프로젝트만 | 커밋 |
| `MyGame.code-workspace` | 게임 폴더 | 엔진+게임 멀티루트 워크스페이스 | gitignore |
| `MyGame.sln` | 게임 폴더 | 엔진+게임 통합 솔루션 (IntelliSense용) | gitignore |

- `MyGame.sln`은 프로젝트 생성/열기 시 자동 생성됩니다.
- 수동으로 재생성하려면:
  ```bash
  cd MyGame
  dotnet new sln -n "MyGame" --output . --format sln --force
  dotnet sln MyGame.sln add LiveCode/LiveCode.csproj FrozenCode/FrozenCode.csproj ../IronRose/src/**/*.csproj
  ```

## 4. 기존 프로젝트 열기

1. 에디터 시작 화면에서 **Open Project** 클릭
2. `project.toml`이 있는 폴더 선택
3. 워크스페이스(`.code-workspace`)와 통합 솔루션(`.sln`)이 재생성됩니다

## 5. 프로젝트 디렉토리 규칙

| 폴더 | 용도 |
|------|------|
| `Assets/` | 텍스처, 모델, 씬, 프리팹 등 비코드 에셋 전용. **C# 파일 금지** |
| `LiveCode/` | 런타임 핫 리로드 스크립트. Roslyn이 자동 컴파일 |
| `FrozenCode/` | 안정화된 스크립트. 빌드 시 컴파일 |
| `RoseCache/` | 에셋 캐시 (자동 생성, gitignore) |

## 6. 스크립트 작성 시작

`LiveCode/` 폴더에 `.cs` 파일을 만들면 됩니다:

```csharp
using RoseEngine;

public class MyScript : MonoBehaviour
{
    public override void Start()
    {
        Debug.Log("Hello IronRose!");
    }

    public override void Update()
    {
        // 매 프레임 실행
    }
}
```

자세한 내용은 [QuickStart.md](QuickStart.md)를 참조하세요.
