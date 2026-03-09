# Phase 14: ImGui.NET Editor Overlay

## Context

Avalonia 에디터가 별도 스레드/윈도우에서 동작하면서 Dock 라이브러리 토글 버그, 레이아웃 깨짐, 스레드 동기화 복잡성 등 문제가 계속됨. 게임 엔진 에디터의 표준 방식인 ImGui 오버레이로 전면 교체.

**결과:** 같은 스레드에서 동작, FSR 이후 네이티브 해상도 렌더링, 패널 토글 한 줄, Avalonia 의존성 완전 제거.

---

## 아키텍처

```
현재 (Avalonia)                      목표 (ImGui)
──────────────                      ────────────
Main Thread     Avalonia Thread     Main Thread (전부 하나)
  Update()        10Hz polling        Update()
  → PushSnapshot  → ConsumeSnapshot   → ImGuiOverlay.Update() (패널 빌드)
  Render()        → EnqueueCommand    Render()
  → Scene                             → Scene → BlitToSwapchain
  → Present                           → ImGui.Render() (스왑체인에 직접)
                                      → Present
```

**핵심:** ImGui가 같은 스레드이므로 SceneManager, RenderSettings 등 엔진 데이터에 직접 접근 가능. EditorBridge 스냅샷 불필요.

---

## 렌더 파이프라인 삽입 지점

```
GraphicsManager.BeginFrame()
RenderSystem.Render()          ← GBuffer → Lighting → Forward → PostProcess → FSR → CAS → BlitToSwapchain
ImGuiOverlay.Render(cl)        ← NEW: 스왑체인에 ImGui 그리기 (네이티브 해상도, FSR 무관)
GraphicsManager.EndFrame()     ← Submit + SwapBuffers
```

---

## 구현 단계

### Phase 14a: Foundation

**Step 1 — NuGet + 프로젝트 정리**
- `src/IronRose.Engine/IronRose.Engine.csproj` — `ImGui.NET` 1.91+ 추가
- `src/IronRose.Demo/IronRose.Demo.csproj` — IronRose.Editor 참조 제거
- `src/IronRose.Demo/Program.cs` — Avalonia 스레드 시작 코드 제거 (lines 25-42)

**Step 2 — ImGui 셰이더**
- `Shaders/imgui.vert` — position(vec2) + texcoord(vec2) + color(vec4), orthographic projection
- `Shaders/imgui.frag` — texture sampling, alpha blending
- 기존 `ShaderCompiler.CompileGLSL()` + Veldrid.SPIRV로 컴파일

**Step 3 — VeldridImGuiRenderer (~300줄)**
- 새 파일: `src/IronRose.Engine/Editor/ImGui/VeldridImGuiRenderer.cs`
- Pipeline (alpha blend, no depth, scissor), ResourceLayout x2, font atlas Texture
- `Render(cl)`: ImDrawData 순회 → vertex/index 업로드 → DrawIndexed
- `GetOrCreateImGuiBinding(TextureView)`: 커스텀 텍스처 바인딩 (Game View용)

**Step 4 — ImGuiInputHandler**
- 새 파일: `src/IronRose.Engine/Editor/ImGui/ImGuiInputHandler.cs`
- Silk.NET IInputContext 콜백 → `ImGui.GetIO()` 전달 (키, 마우스, 스크롤, 텍스트입력)
- `WantCaptureMouse` / `WantCaptureKeyboard` 프로퍼티 노출
- 수정: `src/IronRose.Engine/RoseEngine/Input.cs` — ImGui가 입력을 원하면 게임 입력 차단 (F11은 항상 통과)

**Step 5 — ImGuiOverlay 컨트롤러**
- 새 파일: `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`
- `Initialize()`, `Toggle()`, `Update(dt)`, `Render(cl)`, `Resize()`, `Dispose()`
- ImGui 도킹 활성화: `ConfigFlags |= DockingEnable`
- 초기 레이아웃: `DockBuilderSplitNode`로 Left(Hierarchy) / Center(GameView) / Right(Inspector+RenderSettings) / Bottom(Console)

**Step 6 — EngineCore 통합**
- 수정: `src/IronRose.Engine/EngineCore.cs`
  - `Initialize()`: ImGuiOverlay 생성
  - `Update()`: `_imguiOverlay.Update(dt)` 호출
  - `Render()`: RenderSystem 후 `_imguiOverlay.Render(cl)` 호출
  - `ProcessEngineKeys()`: F11 → `_imguiOverlay.Toggle()` (EditorBridge.RequestToggleWindow 대체)
  - `Shutdown()`: `_imguiOverlay.Dispose()`
- 수정: `src/IronRose.Rendering/GraphicsManager.cs` — Window 프로퍼티 노출

**검증:** F11 → 빈 ImGui 도킹 레이아웃 표시, 입력 소비 동작 확인

### Phase 14b: 패널 포팅

**Step 7 — Hierarchy 패널**
- 새 파일: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiHierarchyPanel.cs`
- `SceneManager.AllGameObjects` 직접 접근 (스냅샷 불필요)
- `ImGui.TreeNodeEx()` + 선택 추적

**Step 8 — Inspector 패널**
- 새 파일: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs`
- 선택된 GO의 컴포넌트/필드를 리플렉션으로 표시
- `[Range]` → `ImGui.SliderFloat/Int`, `bool` → `ImGui.Checkbox`, 기타 → `ImGui.InputText`
- 변경 시 `EditorBridge.EnqueueCommand(new SetFieldCommand {...})`

**Step 9 — RenderSettings 패널**
- 새 파일: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiRenderSettingsPanel.cs`
- `RenderSettings.*` 프로퍼티 직접 읽기/쓰기 (같은 스레드)
- 섹션: Ambient, Sky, FSR, SSIL, PostProcess 이펙트

**Step 10 — Console 패널**
- 새 파일: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiConsolePanel.cs`
- `EditorBridge.DrainLogs()` → 500개 순환 버퍼
- 색상: Info=하늘, Warning=노랑, Error=빨강
- 필터 + Clear + Auto-scroll

**Step 11 — 테마**
- 새 파일: `src/IronRose.Engine/Editor/ImGui/ImGuiTheme.cs`
- 기존 Avalonia 패널의 Catppuccin Mocha 색상 매칭

**검증:** 4개 패널 모두 동작, Avalonia와 기능 동등

### Phase 14c: 정리

**Step 12 — EditorBridge 간소화**
- 수정: `src/IronRose.Engine/Editor/EditorBridge.cs`
  - 제거: `_snapshots` 큐, `PushSnapshot()`, `ConsumeSnapshot()`, `IsEditorWindowVisible`, `RequestToggleWindow()`, `ConsumeToggleRequest()`
  - 유지: `_commands` 큐 (SetFieldCommand 등), `_logs` 큐 (Console), `IsEditorConnected`
- 수정: `src/IronRose.Engine/EngineCore.cs` — `EditorBridge.PushSnapshot()` 호출 제거

**Step 13 — Avalonia 에디터 제거 (선택)**
- `src/IronRose.Editor/` 프로젝트 전체 제거 또는 보관

**검증:** 빌드 성공, 기존 기능 정상

### Phase 14d: Game View (후속)

**Step 14 — 오프스크린 RT Game View**
- `ImGuiOverlay`에서 스왑체인과 동일 포맷의 오프스크린 Framebuffer 생성
- 에디터 ON: RenderSystem이 오프스크린 RT에 렌더 → `ImGui.Image()`로 Game View 패널에 표시
- 에디터 OFF: 기존처럼 스왑체인 직접 렌더
- `RenderSystem.Render()`의 `BlitToSwapchain` 타겟을 교체 가능하게 수정

---

## 파일 요약

### 새 파일 (10개)
| 파일 | 설명 |
|------|------|
| `Shaders/imgui.vert` | ImGui 버텍스 셰이더 |
| `Shaders/imgui.frag` | ImGui 프래그먼트 셰이더 |
| `src/IronRose.Engine/Editor/ImGui/VeldridImGuiRenderer.cs` | Veldrid ImGui 렌더러 |
| `src/IronRose.Engine/Editor/ImGui/ImGuiInputHandler.cs` | Silk.NET → ImGui IO 브리지 |
| `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs` | 오버레이 컨트롤러 |
| `src/IronRose.Engine/Editor/ImGui/ImGuiTheme.cs` | Catppuccin Mocha 테마 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiHierarchyPanel.cs` | Hierarchy 패널 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs` | Inspector 패널 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiRenderSettingsPanel.cs` | RenderSettings 패널 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiConsolePanel.cs` | Console 패널 |

### 수정 파일 (5개)
| 파일 | 변경 |
|------|------|
| `src/IronRose.Engine/IronRose.Engine.csproj` | ImGui.NET 패키지 추가 |
| `src/IronRose.Engine/EngineCore.cs` | ImGuiOverlay 통합, F11, PushSnapshot 제거 |
| `src/IronRose.Engine/RoseEngine/Input.cs` | ImGui 입력 소비 체크 |
| `src/IronRose.Engine/Editor/EditorBridge.cs` | 스냅샷 큐/토글 제거 |
| `src/IronRose.Demo/Program.cs` | Avalonia 스레드 제거 |

### 제거 파일
| 파일 | 이유 |
|------|------|
| `src/IronRose.Demo/IronRose.Demo.csproj` | Editor 프로젝트 참조 제거 |
| `src/IronRose.Editor/` (전체) | Avalonia 에디터 → ImGui로 대체 |

---

## 검증 체크리스트

1. F11 → ImGui 도킹 레이아웃 표시/숨김
2. Hierarchy: 모든 GO 트리 표시, 클릭 선택
3. Inspector: 선택 GO 컴포넌트/필드, 슬라이더/체크박스 변경 반영
4. RenderSettings: FSR/SSIL 토글 + 파라미터 조정
5. Console: Debug.Log 색상별 표시, Clear, Auto-scroll
6. 입력 격리: ImGui 위에서 마우스 클릭 → 게임 카메라 안 움직임
7. 입력 통과: ImGui 밖에서 → 게임 입력 정상
8. FSR 호환: ImGui가 네이티브 해상도로 선명하게 표시
9. 윈도우 리사이즈: ImGui 레이아웃 정상 유지
10. 성능: ImGui 오버헤드 < 0.5ms
