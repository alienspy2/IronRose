# CharacterController 공중 시작 시 입력 없이 낙하하지 않는 버그

## 유저 보고 내용
- CharacterController를 사용하는 오브젝트가 공중에서 시작하면, 사용자가 입력을 줘서 움직여야만 낙하가 시작됨
- 시작하자마자 바로 자유낙하해야 함

## 분석 중

### 의심 원인
1. `CharacterController.Move()`의 `minMoveDistance` 체크 (0.001f) — 계산상 첫 프레임부터 통과해야 하지만 확인 필요
2. `PhysicsManager.Instance`가 null이어서 조기 리턴 — 첫 몇 프레임에 물리 미초기화 가능성
3. `_kinematicHandle` 미등록 상태에서의 동작 문제

### 진단 로그 삽입 위치
- `CharacterController.Move()` 시작부 — motion, magnitude, minMoveDistance, handle, mgr 존재 여부
- `TestCC.Update()` — dt, verticalVelocity, move, finalMotion, magnitude, position, isGrounded

### 테스트 절차
1. 에디터 실행: `dotnet run --project src/IronRose.RoseEditor`
2. CharacterController가 있는 씬을 로드 (TestCC가 붙은 오브젝트가 공중에 위치해야 함)
3. Play 모드 진입
4. **키보드/마우스 입력을 하지 않고** 3~5초 대기
5. 프로젝트 루트의 `_diag.log` 파일 내용 확인

## 상태: 진단 로그 삽입 완료, 유저 테스트 대기
