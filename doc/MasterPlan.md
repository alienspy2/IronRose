# **IronRose 게임 엔진 마스터 플랜**

> **"Iron for Strength, Rose for Beauty"**
> AI-Native .NET 10 Game Engine - From Prompt to Play

---

## **프로젝트 개요**

**IronRose**는 AI(LLM)와의 협업을 최우선으로 설계된 .NET 10 기반 게임 엔진입니다.
Unity API 호환성을 유지하면서도 런타임 코드 생성 및 핫 리로딩에 특화되어 있으며,
ImGui 기반 에디터를 중심으로 AI가 개발을 보조하는 워크플로우를 제공합니다.

**핵심 가치:**
- **단순함이 최우선** — 복잡한 아키텍처보다 이해하기 쉬운 코드
- **실용주의** — 이론보다 실제로 동작하는 것
- **AI 친화적** — Unity 스타일 코드를 그대로 실행

---

## **아키텍처 개요**

```
IronRose/
├── src/
│   ├── IronRose.Engine/         # 엔진 코어 (EXE 진입점 + RoseEngine API + 에디터)
│   ├── IronRose.Rendering/      # Forward/Deferred PBR 렌더링 파이프라인
│   ├── IronRose.Physics/        # BepuPhysics 3D + Aether.Physics2D
│   ├── IronRose.Scripting/      # Roslyn 런타임 컴파일 + 핫 리로드
│   ├── IronRose.Contracts/      # 플러그인 API 계약
│   ├── IronRose.RoseEditor/     # 에디터 실행 파일
│   └── IronRose.Standalone/     # Standalone 빌드
├── Shaders/                      # GLSL 셰이더 (47파일)
├── Assets/                       # 게임 에셋
└── docs/
```

**핫 리로드 구조:**
- **FrozenCode** — `dotnet build` 시 컴파일, 안정적 기반
- **LiveCode** — Roslyn + FileSystemWatcher 런타임 컴파일, 빠른 반복

---

## **기술 스택**

| 분류 | 기술 |
|------|------|
| 런타임 | .NET 10.0 |
| GPU | Veldrid (Vulkan) |
| 윈도우/입력 | Silk.NET (GLFW) |
| 에디터 | ImGui.NET |
| 스크립팅 | Roslyn |
| 3D 모델 | AssimpNet + SharpGLTF |
| 이미지/폰트 | SixLabors.ImageSharp + Fonts |
| 물리 | BepuPhysics v2 + Aether.Physics2D |
| 직렬화 | Tomlyn (TOML) |
| 인게임 UI | AngleSharp (HTML/CSS) |
| 최적화 | BCnEncoder.Net, Meshoptimizer.NET |

---

## **프로젝트 철학**

> **"Simplicity is the ultimate sophistication."** — Leonardo da Vinci
> **"Make it work, make it right, make it fast — in that order."**

1. **단순성** — ECS 변환, Shim 레이어 없이 Unity 아키텍처 직접 구현
2. **실용주의** — 과도한 엔지니어링 금지, 병목이 생기면 그때 최적화
3. **AI 친화성** — AI가 에디터 워크플로우를 보조하여 개발 생산성 향상
4. **극한의 유연성** — FrozenCode/LiveCode 이중 구조, `/digest`로 검증 후 편입

**IronRose — Simple, AI-Native, .NET-Powered**
