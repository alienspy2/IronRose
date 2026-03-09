# Phase 37 검토: ImGui 패널을 메인 윈도우 밖으로 분리 (Multi-Viewport)

> **목적**: ImGui 패널을 메인 윈도우 밖으로 꺼내서 별도 OS 윈도우로 띄울 수 있는지 기술적 가능성 검토

---

## 현재 상태

| 요소 | 현황 | 파일 |
|------|------|------|
| ImGui.NET | v1.91.0.1 — multi-viewport API 포함 | IronRose.Engine.csproj |
| Docking | `DockingEnable` ✅ 활성화 됨 | ImGuiOverlay.cs:219 |
| ViewportsEnable | ❌ **비활성화** | ImGuiOverlay.cs:219 |
| 윈도우 백엔드 | Silk.NET 2.23.0 — 다중 윈도우 생성 가능 | Program.cs |
| 렌더러 | Veldrid 커스텀 구현 — **단일 뷰포트만 지원** | VeldridImGuiRenderer.cs |
| Platform IO 콜백 | ❌ **미구현** | — |

현재 모든 패널은 메인 윈도우 내 단일 DockSpace에서만 동작한다.

---

## Multi-Viewport 활성화에 필요한 작업

### 1단계: 플래그 활성화 (소규모)
- `ImGuiConfigFlags.ViewportsEnable` 추가
- `ImGui.UpdatePlatformWindows()` + `ImGui.RenderPlatformWindowsDefault()` 호출 추가

### 2단계: Platform IO 백엔드 구현 (대규모)
ImGui가 요구하는 플랫폼 콜백 구현 필요:
- `CreateWindow` — Silk.NET으로 새 OS 윈도우 생성
- `DestroyWindow` — 윈도우 파괴
- `ShowWindow`, `SetWindowPos`, `GetWindowPos`, `SetWindowSize`, `GetWindowSize`
- `SetWindowFocus`, `GetWindowFocus`, `SetWindowTitle`
- 각 윈도우에 대한 **입력 이벤트 라우팅** (키보드, 마우스)

### 3단계: 렌더러 확장 (대규모)
- 뷰포트별 Veldrid `Swapchain` + `Framebuffer` 생성/관리
- `ImGui.GetDrawData()` 대신 각 뷰포트의 DrawData를 순회하며 렌더링
- 뷰포트 생성/파괴 시 GPU 리소스 라이프사이클 관리

### 4단계: P/Invoke 바인딩 보강 (중규모)
- 현재 DockBuilder 바인딩은 기본적인 것만 있음
- Viewport 관련 추가 네이티브 함수 바인딩 필요

---

## 난이도 평가

| 항목 | 난이도 | 비고 |
|------|--------|------|
| 플래그 켜기 | 낮음 | 2줄 수정, 하지만 백엔드 없이는 크래시 |
| Platform IO | **높음** | Silk.NET 다중 윈도우 + 이벤트 분배 구현 |
| 렌더러 확장 | **높음** | Veldrid 멀티 스왑체인 관리 |
| 안정성 확보 | **높음** | 윈도우 포커스, DPI, 크로스 플랫폼 이슈 |

**총 예상 작업량**: 대규모 (Platform IO + Renderer 양쪽 모두 상당한 구현 필요)

---

## 대안

| 방안 | 설명 | 난이도 |
|------|------|--------|
| **A. 내부 도킹만 유지** | 현재 방식. 패널을 메인 윈도우 내에서만 자유 배치 | 이미 완료 |
| **B. Multi-Viewport 전체 구현** | 위 1~4단계 모두 구현 | 높음 |
| **C. 참조 구현 활용** | imgui 공식 Vulkan/SDL2 백엔드를 참고해 Silk.NET+Veldrid용으로 포팅 | 중~높음 |

---

## 결론

**기술적으로 가능하지만 작업량이 상당하다.**

- ImGui.NET 1.91은 multi-viewport API를 제공하고, Silk.NET/Veldrid도 다중 윈도우를 지원하므로 **근본적인 차단 요소는 없다**.
- 그러나 Platform IO 콜백 백엔드와 멀티 뷰포트 렌더러를 **새로 구현**해야 한다.
- imgui 공식 예제(backends/imgui_impl_vulkan + imgui_impl_sdl2)를 참조하면 구현 방향은 명확하나, Silk.NET + Veldrid 조합에 맞게 적응시키는 작업이 필요하다.
- 현재 단계에서는 **엔진 핵심 기능(애니메이션 등) 우선 개발** 후 나중에 도입하는 것이 합리적으로 보인다.
