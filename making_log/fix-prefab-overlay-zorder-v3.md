# Prefab Variant Tree 오버레이 Z-order 문제 수정 (3차 - 근본 해결)

## 유저 보고 내용
- Prefab Edit Mode에서 Hierarchy 패널을 클릭하면 Scene View 위의 Prefab Variant Tree 오버레이와 Breadcrumb 바가 사라지는 현상
- 1차 수정 (Begin 직후 BringCurrentWindowToDisplayFront) 실패
- 2차 수정 (EndFrame 이후 FlushPendingFront로 지연 적용) 실패
- 진단 로그 분석: 오버레이 윈도우의 Begin=True, 위치/크기 정상, FlushPendingFront 정상 호출되지만 draw data의 clipRect에 오버레이 영역이 없음

## 원인
ImGui docking 환경에서 별도 floating 윈도우(ImGui.Begin/End)로 그린 오버레이가 dock host window의 클리핑 영역에 의해 잘려나가는 문제.

igBringWindowToDisplayFront P/Invoke로 display order를 조작하는 접근(1차, 2차)은 근본적으로 한계가 있었음:
- EndFrame()의 focus 처리가 display order를 매 프레임 덮어씀 (1차 실패 원인)
- EndFrame() 이후 FlushPendingFront()로 다시 올려도, draw data 생성 시점에서 오버레이의 draw command가 dock host의 clipRect에 포함되지 않음 (2차 실패 원인)
- 별도 윈도우의 draw data가 독립 cmdList로 분리되지 않고, dock host 윈도우에 병합되어 잘리거나 무시됨

## 수정 내용 (3차 - 접근 전환)
별도 ImGui 윈도우 방식을 완전히 포기하고, **Scene View 윈도우 내부에서 child window(ImGui.BeginChild/EndChild)로 오버레이를 렌더링**하는 방식으로 전환.

### 핵심 변경
1. `ImGuiSceneViewPanel`에 `DrawPrefabOverlay` 콜백 추가 (기존 `DrawGizmoOverlay`와 동일 패턴)
2. Scene View의 `Draw()` 내에서 이미지 렌더 후 `DrawPrefabOverlay?.Invoke()` 호출
3. `ImGuiOverlay`에서 `_sceneView.DrawPrefabOverlay = DrawPrefabOverlaysInSceneView` 등록
4. `DrawPrefabBreadcrumb()` -> `DrawPrefabBreadcrumbChild()`: ImGui.Begin/End -> ImGui.BeginChild/EndChild
5. `DrawPrefabVariantTreeOverlay()` -> `DrawPrefabVariantTreeChild()`: 동일 전환
6. `SetCursorScreenPos`로 Scene View 이미지 좌상단에 위치 지정
7. `ImGuiChildFlags.AutoResizeX | AutoResizeY | AlwaysAutoResize`로 내용에 맞게 크기 자동 조절
8. `ImGuiCol.ChildBg`로 배경색 설정 (기존 WindowBg 대체)
9. `ChildRounding`으로 모서리 라운딩 적용

### 제거된 코드
- `ImGuiOverlay.Draw()`에서의 별도 `DrawPrefabBreadcrumb()` / `DrawPrefabVariantTreeOverlay()` 호출
- `EnqueueCurrentWindowForDisplayFront()` 호출 (더 이상 불필요)
- `Render()` 내 EndFrame() -> FlushPendingFront() -> Render() 3단계 패턴 -> 단순 `ImGui.Render()`로 복원
- 모든 진단 로그 코드 (DIAG START/END 블록 전체)
- `_diagFrameCount`, `_diagLastSelectionVersion`, `_diagFramesSinceSelChange`, `DiagShouldLog()` 메서드

## 변경된 파일
- `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs` -- 오버레이를 child window 방식으로 전환, 진단 로그 전체 제거, Render 메서드 단순화
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiSceneViewPanel.cs` -- DrawPrefabOverlay 콜백 추가 및 Draw()에서 호출
- `src/IronRose.Engine/Editor/ImGui/ImGuiWindowNative.cs` -- 진단 로그 제거 (메서드 자체는 유지)

## 검증
- dotnet build 성공 (오류 0개)
- 유저 확인 필요: 앱 실행 후 Prefab Edit Mode에서 Hierarchy 클릭 시 Breadcrumb과 Variant Tree가 사라지지 않는지 확인

## 기술 노트
- ImGui docking에서 floating overlay의 Z-order를 보장하는 것은 매우 어려움 (EndFrame의 focus 처리, dock host의 clipRect 등 다중 요인)
- child window 방식은 부모 윈도우의 draw list에 포함되므로 Z-order 문제가 원천적으로 발생하지 않음
- Scene View 이미지 위에 `SetCursorScreenPos`로 원하는 위치에 child window를 배치하면 기존과 동일한 시각적 결과를 얻을 수 있음
- `ImGuiWindowNative`의 P/Invoke 바인딩(BringCurrentWindowToDisplayFront, EnqueueCurrentWindowForDisplayFront, FlushPendingFront)은 향후 필요할 수 있으므로 코드를 유지

## 주의사항 (향후 참고)
- ImGui docking 환경에서 특정 패널 위에 오버레이를 표시해야 할 때, **별도 윈도우(Begin/End)가 아닌 해당 패널 내부의 child window로 구현**해야 Z-order 문제가 발생하지 않음
- 이 패턴은 `DrawGizmoOverlay` 콜백과 동일: Scene View 윈도우 컨텍스트 내에서 콜백을 호출하고, 콜백 내에서 BeginChild로 오버레이를 그림
