# PileScript 블록 겹침으로 인한 물리 밀어내기 현상 수정

## 유저 보고 내용
- 스테이지 시작 시 블록들이 서로 밀쳐내는 현상 발생
- 블록끼리 겹쳐서 배치되어 물리 엔진이 밀어내는 것으로 보임

## 원인
- `PileScript.BuildPile()`에서 블록 배치 시 X, Y 간격을 `CUBE_SIZE`(0.8)로 사용
- 큐브의 `localScale`도 `CUBE_SIZE`(0.8)이므로, 블록 간 간격이 0으로 정확히 맞닿음
- 부동소수점 오차로 인해 물리 엔진(Bepu)이 미세한 겹침(penetration)으로 판단하여 블록들을 밀어냄

## 수정 내용
- `CUBE_SPACING = CUBE_SIZE + 0.02f` (0.82f) 상수를 추가하여 블록 간 배치 간격에 0.02 단위의 미세 간격 확보
- 큐브의 시각적 크기(`localScale`)는 `CUBE_SIZE`(0.8) 그대로 유지
- 위치 계산(offsetX, offsetY)에서 간격 계산에 사용되는 값을 `CUBE_SIZE` -> `CUBE_SPACING`으로 변경
- offsetY의 바닥 오프셋(`CUBE_SIZE / 2f`)은 큐브 중심이 지면 위에 놓이도록 하는 것이므로 `CUBE_SIZE` 유지

## 변경된 파일
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/PileScript.cs` -- CUBE_SPACING 상수 추가, 블록 배치 간격 계산을 CUBE_SPACING 기반으로 변경

## 검증
- dotnet build 성공 확인
- 유저에게 플레이모드 실행하여 블록 밀쳐내기 현상이 해소되었는지 확인 요청
