# Future: RenderTexture API + Canvas GPU 렌더 패스

## 개요

Camera.targetTexture(RenderTexture) API와 Canvas UI GPU 렌더 패스를 추가해야 한다.

## 필요 이유

1. **Game View 스크린샷**: 현재 Canvas UI가 ImGui DrawList로만 렌더되어 오프스크린 RT 캡처 시 게임 UI가 빠짐. GPU 렌더 패스가 있어야 해상도 설정대로 UI 포함 캡처 가능.
2. **RenderTexture**: 카메라가 임의 프레임버퍼에 렌더하는 API가 없어 미니맵, 보안카메라 등 게임 기능 구현 불가.
3. **런타임 빌드**: 에디터 없는 스탠드얼론 빌드에서 Canvas UI를 ImGui 없이 렌더하려면 GPU 렌더 경로 필수.

## 구현 항목

### A. RenderTexture API
- `RenderTexture` 클래스 (Veldrid Texture + Framebuffer 래핑)
- `Camera.targetTexture` 필드
- RenderSystem이 Camera.targetTexture 설정 시 해당 프레임버퍼에 렌더

### B. SpriteBatch
- 2D 텍스처드 쿼드 배치 렌더링 시스템 (Veldrid CommandList 기반)
- 알파 블렌딩 파이프라인, 직교 투영, 동적 버퍼

### C. Canvas GPU 렌더 패스
- `CanvasRenderer.RenderToFramebuffer(CommandList, Framebuffer)` 추가
- UIText, UIPanel, UIImage 등 IUIRenderable에 GPU 렌더 경로 추가
- 3D 씬 렌더 직후 오프스크린 RT에 Canvas UI GPU 렌더

### D. CLI 명령
- `editor.screenshot_gameview [path]` — 게임뷰(3D + Canvas UI) 캡처

## 상세 분석

[doc/future_gameview_screenshot.md](../doc/future_gameview_screenshot.md) 참조
