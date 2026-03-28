# AngryClawd 카메라 자동 줌 추가 (모든 pile이 화면 안에 보이도록)

## 유저 보고 내용
- AngryClawdGame에서 스테이지가 올라가면 pile이 많아지면서 오른쪽 pile이 화면 밖에 스폰됨
- 카메라가 고정 위치(-12, 8, -32)에 있어서 pile 수 증가에 대응하지 못함
- 카메라 Z 좌표를 유동적으로 조절하여 모든 블록이 화면 안에 들어오도록 해야 함
- 카메라 이동은 부드럽게(smooth) 해야 함

## 원인
- 카메라가 씬에 고정 위치로 배치되어 있고, 스테이지 진행에 따른 동적 조절 로직이 없었음
- pile은 PILE_START_X=0부터 PILE_SPACING=8씩 오른쪽으로 배치되어 최대 x=32까지 갈 수 있음
- shooter는 x=-24.5에 위치하므로 전체 X 범위는 약 -24.5 ~ 32
- FOV 60도, 카메라 Z=-32로는 이 범위를 모두 담을 수 없음

## 수정 내용
`AngryClawdGame.cs`에 `UpdateCameraZoom()` 메서드를 추가하여 매 프레임 카메라 위치를 자동 조절:

1. **바운딩 영역 계산**: shooter 위치와 모든 활성 pile의 자식 블록 위치를 순회하여 X/Y 바운딩 박스 계산
2. **필요 Z 거리 역산**: perspective 카메라의 FOV와 화면 aspect ratio를 기반으로, 바운딩 영역이 뷰에 들어오는 최소 Z 거리를 계산
   - `requiredZ = max(halfHeight / tan(fov/2), halfWidth / (tan(fov/2) * aspect))`
3. **부드러운 이동**: `Mathf.SmoothDamp`로 X, Y, Z 좌표 모두 부드럽게 이동 (smoothTime=0.6초)
4. **카메라 X/Y**: 바운딩 박스 중심으로 이동하여 모든 오브젝트가 화면 중앙에 오도록 함
5. **최소 거리 제한**: CAMERA_MIN_Z=-20으로 카메라가 너무 가까이 오지 않도록 제한
6. **stageClearing 중에도 동작**: 스테이지 클리어 대기 중에도 카메라가 계속 업데이트됨

### 추가된 상수
- `CAMERA_SMOOTH_TIME = 0.6f` -- SmoothDamp 평활 시간
- `CAMERA_PADDING = 4.0f` -- 바운딩 영역 X축 여백
- `CAMERA_MIN_Z = -20.0f` -- 최소 후퇴 거리
- `CAMERA_Y_PADDING = 3.0f` -- Y축 상단 여백

## 변경된 파일
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/AngryClawdGame.cs` -- UpdateCameraZoom() 메서드 추가, Update()에서 호출, 카메라 SmoothDamp 필드 추가

## 검증
- 빌드 성공 확인 (dotnet build)
- 유저 실행 확인 필요: 에디터에서 AngryClawd 씬을 플레이하여 스테이지 진행 시 카메라가 부드럽게 줌아웃되는지 확인
