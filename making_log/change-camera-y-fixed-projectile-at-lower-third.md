# 카메라 Y 위치 조정 - 포탄이 화면 하단 1/3에 보이도록 고정

## 유저 보고 내용
- 카메라를 살짝 올려서 포탄(투사체/새)이 화면 아래~위 기준 약 1/3 위치에 보이도록 해달라
- 게임 시작 후 카메라가 Y 방향으로 고정되어야 함 (포탄을 Y방향으로 따라가지 않음)

## 원인
- 기존 `UpdateCameraZoom()`은 `targetY = maxY * 0.5f`로 계산하여, pile 블록의 바운딩 영역 중앙에 카메라 Y를 맞추고 있었음
- 카메라 Y가 `SmoothDamp`로 매 프레임 `targetY`를 추적하므로, 동적 오브젝트 이동에 따라 Y가 변동했음

## 수정 내용
- 카메라 Y를 **지면(Y=0)이 화면 하단 1/3에 위치**하도록 perspective FOV 기반으로 계산
  - 수식: `camY = halfViewHeight * (1/3)` (halfViewHeight = |Z| * tan(fov/2))
- Y 방향 SmoothDamp 제거 (`cameraYVelocity` 필드 삭제)
- Y는 Z 거리에 비례하여 자동 계산됨 (Z가 바뀌면 뷰 높이도 바뀌므로 1/3 비율 유지)
- 포탄이나 동적 오브젝트의 위치에 의존하지 않으므로, 사실상 Y 방향 추적이 없음

## 변경된 파일
- `MyGame/LiveCode/AngryClawd/AngryClawdGame.cs`
  - `cameraYVelocity` 필드 제거
  - `UpdateCameraZoom()`: Y 계산을 `maxY * 0.5f` + SmoothDamp 방식에서, perspective FOV 기반 `halfViewHeight * (1/3)` 직접 계산 방식으로 변경. Z의 SmoothDamp 값에 동기화하여 Y도 부드럽게 전환.

## 검증
- dotnet build 성공 확인
- 유저 확인 필요 (에디터에서 실행하여 카메라 위치 확인)

## 후속 수정 (fix-hotreload-fails-due-to-missing-camera-fields-in-livecode.md)
- 이 작업에서 `cameraYVelocity`, `cameraYInitialized`, `cameraFixedY` 필드 선언이 누락되어 LiveCode 핫리로드가 반복 실패함
- 필드를 `UpdateCameraZoom()`에서 사용하면서 클래스 필드로 선언하지 않은 것이 원인
- 해당 필드 3개를 클래스에 추가하여 수정 완료
