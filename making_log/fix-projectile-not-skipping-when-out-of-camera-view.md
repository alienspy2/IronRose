# Slingshot 포탄이 카메라 밖으로 나가도 즉시 다음 샷으로 넘어가지 않는 버그 수정

## 유저 보고 내용
- 포탄(projectile/slingshot)이 카메라 밖으로 나가면 기다리지 말고 즉시 다음 샷으로 넘어가야 하는데, 현재는 포탄이 화면 밖으로 나가도 일정 시간(8초 타임아웃) 대기하는 문제

## 원인
- `TrackCannonball()` 메서드에 카메라 뷰포트 밖 감지 로직이 없었음
- 포탄 상태 추적 조건이 두 가지뿐이었음:
  1. 타임아웃: 발사 후 8초 경과 (`CANNONBALL_TIMEOUT`)
  2. 낙하: `position.y < -10f`
- 포탄이 화면 좌/우/상 방향으로 날아가면 y < -10 조건에 걸리지 않아 8초 타임아웃까지 대기

## 수정 내용
- `TrackCannonball()`에 카메라 뷰포트 밖 감지 조건 추가
- `IsCannonballOutOfView()` 메서드 신규 작성:
  - perspective 카메라의 FOV와 aspect ratio로 XY 평면상의 가시 영역을 계산
  - 카메라에서 포탄까지의 Z 거리 기반으로 frustum의 halfWidth/halfHeight 산출
  - 50% 여유 마진(`OUT_OF_VIEW_MARGIN = 1.5f`)을 두어 화면 경계를 살짝 벗어난 직후가 아닌, 충분히 벗어났을 때만 판정
  - 포탄의 XY 위치가 마진 포함 가시 영역 밖이면 true 반환
- 뷰포트 밖 판정 시 포탄을 즉시 파괴하고 `HandleCannonballDone()` 호출하여 다음 샷으로 전환

## 변경된 파일
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/AngryClawdGame.cs` -- `TrackCannonball()`에 뷰포트 밖 감지 추가, `IsCannonballOutOfView()` 메서드 신규 추가

## 검증
- 정적 분석으로 원인 특정: 코드 흐름 추적으로 뷰포트 밖 감지 로직이 전혀 없음을 확인
- `UpdateCameraZoom()`에서 이미 사용하는 동일한 perspective frustum 계산 방식을 활용하여 일관성 유지
- dotnet build 성공 확인 (에러 0, 기존 warning만 존재)
