# Phase 0: 프로젝트 구조 및 환경 설정

## 목표
프로젝트의 기본 골격을 만들고 개발 환경을 구축합니다.

---

## 작업 항목

### 0.1 솔루션 구조 설계

> **플러그인 기반 핫 리로드 아키텍처**
>
> 엔진(IronRose.Engine)이 EXE 진입점이자 안정적 기반이고,
> 플러그인과 LiveCode를 핫 리로드합니다.

```
IronRose/
├── src/
│   ├── IronRose.Engine/            # 엔진 핵심 (EXE, 진입점 + 메인 루프)
│   │                                # - SDL/Veldrid 초기화
│   │                                # - GameObject, Component, Transform
│   │                                # - MonoBehaviour 시스템
│   │                                # - 플러그인 매니저
│   │
│   ├── IronRose.Contracts/         # 플러그인 API 계약
│   ├── IronRose.Scripting/         # Roslyn 컴파일러
│   ├── IronRose.AssetPipeline/     # Unity 에셋 임포터
│   ├── IronRose.Rendering/         # 렌더링
│   └── IronRose.Physics/           # 물리 엔진
│
├── samples/
│   ├── 01_HelloWorld/
│   ├── 02_RotatingCube/
│   └── 03_AIGeneratedScene/
├── tests/
└── docs/
```

**핵심 구조:**
- ✅ IronRose.Engine (EXE, 안정적 기반)
- ✅ IronRose.Contracts (플러그인 API 컨테이너)
- ✅ **플러그인/LiveCode만 핫 리로드 대상**

**작업 세부사항:**

#### 1. .NET 10 SDK 설치 확인

**설치 여부 확인:**
```bash
dotnet --version
dotnet --list-sdks
```

**.NET 10.0.x가 없는 경우 설치:**
- 공식 사이트: https://dotnet.microsoft.com/download/dotnet/10.0
- Windows: 설치 프로그램 다운로드 및 실행
- 설치 후 터미널 재시작하여 확인

**필수 버전:**
- .NET 10.0.101 이상

#### 2. VS Code 확장 설치
- **C# Dev Kit** (필수)
- **.NET Install Tool**
- **C# Extensions**

#### 3. 솔루션 및 프로젝트 생성 (커맨드라인)
```bash
# 솔루션 생성
dotnet new sln -n IronRose

# 각 프로젝트 생성
dotnet new classlib -n IronRose.Contracts -f net10.0 -o src/IronRose.Contracts
dotnet new classlib -n IronRose.Engine -f net10.0 -o src/IronRose.Engine
dotnet new classlib -n IronRose.Scripting -f net10.0 -o src/IronRose.Scripting
dotnet new classlib -n IronRose.AssetPipeline -f net10.0 -o src/IronRose.AssetPipeline
dotnet new classlib -n IronRose.Rendering -f net10.0 -o src/IronRose.Rendering
dotnet new classlib -n IronRose.Physics -f net10.0 -o src/IronRose.Physics

# 솔루션에 프로젝트 추가
dotnet sln add src/IronRose.Contracts/IronRose.Contracts.csproj
dotnet sln add src/IronRose.Engine/IronRose.Engine.csproj
dotnet sln add src/IronRose.Scripting/IronRose.Scripting.csproj
dotnet sln add src/IronRose.AssetPipeline/IronRose.AssetPipeline.csproj
dotnet sln add src/IronRose.Rendering/IronRose.Rendering.csproj
dotnet sln add src/IronRose.Physics/IronRose.Physics.csproj
```

#### 4. 프로젝트 간 참조 설정
```bash
# Engine → Contracts, Rendering 참조
dotnet add src/IronRose.Engine reference src/IronRose.Contracts
dotnet add src/IronRose.Engine reference src/IronRose.Rendering

# Scripting → Engine
dotnet add src/IronRose.Scripting reference src/IronRose.Engine

# Physics → Engine
dotnet add src/IronRose.Physics reference src/IronRose.Engine

# AssetPipeline → Engine
dotnet add src/IronRose.AssetPipeline reference src/IronRose.Engine
```

#### 5. VS Code 설정 파일
**.vscode/launch.json:**
```json
{
    "version": "0.2.0",
    "configurations": [
        {
            "name": "IronRose Runtime",
            "type": "coreclr",
            "request": "launch",
            "preLaunchTask": "build",
            "program": "${workspaceFolder}/src/IronRose.Engine/bin/Debug/net10.0/IronRose.Engine.dll",
            "args": [],
            "cwd": "${workspaceFolder}",
            "console": "internalConsole",
            "stopAtEntry": false
        }
    ]
}
```

**.vscode/tasks.json:**
```json
{
    "version": "2.0.0",
    "tasks": [
        {
            "label": "build",
            "command": "dotnet",
            "type": "process",
            "args": [
                "build",
                "${workspaceFolder}/IronRose.sln",
                "/property:GenerateFullPaths=true",
                "/consoleloggerparameters:NoSummary"
            ],
            "problemMatcher": "$msCompile"
        }
    ]
}
```

---

### 0.2 NuGet 패키지 설치

**커맨드라인으로 패키지 설치:**

#### IronRose.Rendering
```bash
cd src/IronRose.Rendering
dotnet add package Veldrid
dotnet add package Veldrid.SPIRV
dotnet add package Veldrid.ImageSharp
dotnet add package Silk.NET.SDL
cd ../..
```

#### IronRose.Scripting
```bash
cd src/IronRose.Scripting
dotnet add package Microsoft.CodeAnalysis.CSharp
cd ../..
```

#### IronRose.AssetPipeline
```bash
cd src/IronRose.AssetPipeline
dotnet add package YamlDotNet
dotnet add package AssimpNet
dotnet add package StbImageSharp
cd ../..
```

#### IronRose.Engine
```bash
cd src/IronRose.Engine
dotnet add package Tomlyn
cd ../..
```

#### IronRose.Physics
```bash
cd src/IronRose.Physics
dotnet add package BepuPhysics
dotnet add package Aether.Physics2D
cd ../..
```

**또는 한 번에 설치:**
```bash
# install-packages.bat 또는 install-packages.sh
dotnet add src/IronRose.Rendering package Veldrid
dotnet add src/IronRose.Rendering package Veldrid.SPIRV
dotnet add src/IronRose.Rendering package Veldrid.ImageSharp
dotnet add src/IronRose.Rendering package Silk.NET.SDL
dotnet add src/IronRose.Scripting package Microsoft.CodeAnalysis.CSharp
dotnet add src/IronRose.AssetPipeline package YamlDotNet
dotnet add src/IronRose.AssetPipeline package AssimpNet
dotnet add src/IronRose.AssetPipeline package SixLabors.ImageSharp
dotnet add src/IronRose.Engine package Tomlyn
dotnet add src/IronRose.Physics package BepuPhysics
dotnet add src/IronRose.Physics package Aether.Physics2D
```

---

### 0.3 Git 저장소 초기화

```bash
git init
git add .
git commit -m "Initial commit: IronRose project structure"
```

**.gitignore 추가:**
```gitignore
# Build outputs
bin/
obj/

# VS Code
.vscode/
!.vscode/launch.json
!.vscode/tasks.json

# User-specific files
*.user
*.suo

# IDE folders (Visual Studio, Rider 등)
.vs/
.idea/
*.DotSettings.user
```

**.editorconfig 추가:**
```ini
root = true

[*.cs]
charset = utf-8-bom
indent_style = space
indent_size = 4
end_of_line = crlf

[*.{bat,ps1}]
charset = utf-8
end_of_line = crlf
```

---

## 검증 기준

✅ 솔루션이 빌드 오류 없이 컴파일됨:
```bash
dotnet build
```

✅ VS Code에서 C# IntelliSense 동작 확인

✅ F5 (디버깅) 실행 가능 확인

✅ 모든 프로젝트가 .NET 10을 타겟으로 설정됨:
```bash
dotnet list package
```

✅ 필요한 NuGet 패키지가 모두 설치됨

✅ Git 저장소 초기화 완료:
```bash
git status
```

---

## 예상 소요 시간
**1-2일**

---

## 다음 단계
→ [Phase 1: 최소 실행 가능 엔진](Phase1_MinimalEngine.md)
