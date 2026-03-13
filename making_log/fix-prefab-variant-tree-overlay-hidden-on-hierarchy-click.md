# Prefab Variant Tree 오버레이 Z-order 문제 수정 (2차)

## 유저 보고 내용
- Prefab Edit Mode에서 Hierarchy 패널을 클릭하여 게임오브젝트를 선택하면, Scene View 위에 표시되던 Prefab Variant Tree 오버레이와 Breadcrumb 바가 사라지는 현상
- 1차 수정(Begin() 직후 BringCurrentWindowToDisplayFront 호출)으로 해결되지 않음
- 진단 로그 분석 결과: 오버레이 자체는 매 프레임 정상 호출/렌더링(Begin=True, wPos/wSize 정상)되지만 화면에 보이지 않음

## 원인 (1차 수정이 실패한 이유)
- **ImGui EndFrame()의 focus 처리가 display order를 덮어씀**: `EndFrame()` (또는 `Render()` 내부에서 자동 호출되는 `EndFrame()`)에서 focus가 있는 window의 root (dock host window)를 `BringWindowToDisplayFront()`로 display front에 올림
- 오버레이의 `BringCurrentWindowToDisplayFront()`는 `Begin()` 직후에 호출되므로, 이후 `EndFrame()`에서 dock host가 다시 앞으로 올라와 오버레이를 가림
- 결과: 오버레이의 display front 설정이 매 프레임 dock host에 의해 무효화됨

## 수정 내용 (2차)
- **`ImGuiWindowNative.EnqueueCurrentWindowForDisplayFront()`**: Begin() 직후에 window pointer를 큐에 저장 (즉시 front로 올리지 않음)
- **`ImGuiWindowNative.FlushPendingFront()`**: 큐에 저장된 모든 window를 일괄적으로 display front로 올림
- **`ImGui.EndFrame()` 이후, `ImGui.Render()` 이전에 `FlushPendingFront()` 호출**: EndFrame()의 focus 처리가 완료된 후에 overlay를 display front로 올리므로, dock host에 의한 덮어쓰기 문제가 해결됨
- `Render()`는 EndFrame()이 이미 호출된 경우 중복 호출하지 않으므로 안전

### 호출 순서 (수정 후)
```
1. 모든 패널 Draw() (Begin/End) + overlay EnqueueCurrentWindowForDisplayFront()
2. ImGui.EndFrame() → dock host가 focus 처리로 display front에 올라감
3. FlushPendingFront() → overlay 윈도우들이 dock host 위로 다시 올라감
4. ImGui.Render() → EndFrame() skip, g.Windows 배열의 현재 순서로 draw data 생성
```

## 변경된 파일
- `src/IronRose.Engine/Editor/ImGui/ImGuiWindowNative.cs` -- EnqueueCurrentWindowForDisplayFront/FlushPendingFront 메서드 추가
- `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs` -- Render()에서 EndFrame() 분리 호출 + FlushPendingFront() 삽입; overlay에서 BringCurrentWindowToDisplayFront → EnqueueCurrentWindowForDisplayFront로 변경

## 검증
- dotnet build 성공 (오류 0개)
- 유저 확인 필요: 앱 실행 후 Prefab Edit Mode에서 Hierarchy 클릭 시 Variant Tree 오버레이와 Breadcrumb가 계속 표시되는지 확인

## 기술 노트
- ImGui.NET (1.91.0.1)은 `BringWindowToDisplayFront` 같은 internal API를 노출하지 않음
- cimgui 네이티브 라이브러리에서 `igBringWindowToDisplayFront`, `igGetCurrentWindow`가 export됨을 확인하고 직접 P/Invoke로 바인딩
- `ImGui.Render()`는 내부적으로 `if (g.FrameCountEnded != g.FrameCount) EndFrame();`로 guard되어 있어, 외부에서 `EndFrame()`을 먼저 호출해도 안전
- `EndFrame()` 이후의 `g.Windows` 배열 조작은 `Render()`의 draw data 생성에 반영됨 (Render는 g.Windows를 직접 순회)

## 주의사항 (향후 참고)
- ImGui docking에서 floating overlay의 Z-order를 유지하려면, Begin() 직후가 아닌 **EndFrame() 이후, Render() 이전**에 BringWindowToDisplayFront를 호출해야 함
- EndFrame()의 focus 처리 코드가 dock host를 display front로 올리는 부분이 핵심 원인이었음
- 이 패턴은 ImGui docking을 사용하는 모든 floating overlay에 적용 가능

## 후속: 3차 수정으로 대체됨
- 2차 수정(EndFrame 후 FlushPendingFront)도 실패함. draw data의 clipRect에 오버레이 영역이 포함되지 않는 문제 확인
- **3차 수정에서 별도 윈도우 방식을 폐기하고, Scene View 윈도우 내 child window 방식으로 전환하여 근본 해결**
- 상세: `fix-prefab-overlay-zorder-v3.md` 참조
