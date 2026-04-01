# Future: Game View 전용 스크린샷 (Canvas UI 포함)

## 목표

`editor.screenshot_gameview` CLI 명령 추가. 에디터 UI 없이 게임뷰만 캡처하되, Canvas UI(게임 내 UI)를 포함하고, Game View의 resolution 설정(Native/1920x1080/1280x720)대로 캡처한다.

## 현재 상태

- `editor.screenshot` — 전체 에디터 창(스왑체인) 캡처. 에디터 UI 포함.
- 게임 3D 씬은 오프스크린 RT(ImGuiRenderTargetManager)에 렌더링됨. Game View resolution 설정에 따라 RT 크기 결정.
- Canvas UI는 `CanvasRenderer.RenderAll()`이 **ImGui DrawList**로 게임뷰 이미지 위에 오버레이 렌더링. GPU 프레임버퍼에는 직접 렌더링되지 않음.

## 문제

오프스크린 RT를 캡처하면 3D 씬만 포함되고 Canvas UI가 빠진다. Canvas UI는 ImGui의 2D 오버레이 시스템으로 그려지기 때문에 GPU 텍스처에 존재하지 않는다.

### 시도했으나 실패한 접근

1. **ImGui 미니 프레임**: 메인 ImGui 렌더 후 별도 `NewFrame/Render` 사이클로 Canvas UI를 오프스크린 RT에 렌더 시도.
   - draw data(vertex)는 생성되지만 실제 픽셀에 반영되지 않음.
   - VeldridImGuiRenderer의 파이프라인이 스왑체인 OutputDescription으로 생성되어 오프스크린 RT와 호환 문제 가능성.
   - 근본적으로 workaround이며 불안정.

2. **전체 창 캡처 후 크롭**: Game View resolution 설정대로 캡처해야 하므로 불가 (표시 크기 ≠ 렌더 해상도).

## 정석 해결 방향

### CanvasRenderer GPU 렌더 패스 추가

Canvas UI를 ImGui DrawList가 아닌 **Veldrid CommandList로 직접 프레임버퍼에 렌더**할 수 있도록 엔진 기능을 추가한다.

#### 필요한 작업

1. **SpriteBatch 시스템**: 2D 텍스처드 쿼드를 Veldrid 프레임버퍼에 배치 렌더링하는 시스템.
   - 알파 블렌딩 파이프라인
   - 2D 직교 투영 행렬
   - 동적 vertex/index 버퍼
   - 텍스처 바인딩 관리

2. **CanvasRenderer 이중 렌더 경로**:
   - 기존: `RenderAll(ImDrawListPtr, ...)` — 에디터 Game View 패널용 (ImGui DrawList)
   - 신규: `RenderAllToFramebuffer(CommandList, Framebuffer)` — GPU 직접 렌더용
   - UIText, UIImage, UIPanel 등 `IUIRenderable` 구현체에 GPU 렌더 경로 추가

3. **렌더 파이프라인 통합**: RenderSystem이 3D 씬을 오프스크린 RT에 렌더한 직후, Canvas UI GPU 렌더 패스를 실행.

4. **CLI 명령**: `editor.screenshot_gameview [path]` — 위 렌더 패스 실행 후 오프스크린 RT 캡처.

#### 텍스트 렌더링 고려사항

- UIText는 자체 Font atlas를 사용 (ImGui 폰트 아닌 엔진 폰트 시스템).
- SpriteBatch로 글리프를 텍스처드 쿼드로 렌더 가능 (현재 ImGui DrawList에 AddImage로 그리는 것과 동일한 원리).

#### 단계적 구현 순서

1. SpriteBatch 시스템 (IronRose.Rendering에 추가)
2. CanvasRenderer.RenderAllToFramebuffer() 구현
3. IUIRenderable에 GPU 렌더 인터페이스 추가 (또는 기존 OnRenderUI를 추상화)
4. 렌더 파이프라인 통합 (선택적 Canvas GPU 패스)
5. CLI editor.screenshot_gameview 명령 + 스킬 업데이트

## 관련 파일

- `src/IronRose.Engine/RoseEngine/CanvasRenderer.cs` — 현재 ImGui DrawList 기반 Canvas 렌더러
- `src/IronRose.Engine/RoseEngine/UI/IUIRenderable.cs` — UI 렌더 인터페이스
- `src/IronRose.Engine/RoseEngine/UI/UIText.cs`, `UIPanel.cs`, `UIImage.cs` — UI 컴포넌트들
- `src/IronRose.Engine/Editor/ImGui/ImGuiRenderTargetManager.cs` — 오프스크린 RT 관리
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiGameViewPanel.cs` — Game View 패널 (현재 Canvas 오버레이 호출 위치)
- `src/IronRose.Rendering/GraphicsManager.cs` — 스크린샷 캡처 로직
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` — CLI 명령 등록
- `src/IronRose.Engine/EngineCore.cs` — 렌더 파이프라인 오케스트레이션

## 부수 효과

SpriteBatch + Canvas GPU 렌더 패스가 추가되면:
- 스탠드얼론 빌드(에디터 없이)에서도 Canvas UI를 GPU로 직접 렌더할 수 있는 기반이 됨
- 향후 에디터 없는 런타임 빌드에서 ImGui 의존성 제거 가능
