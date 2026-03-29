# 게임 시작 직후 Stage Clear 오판정 진단 로그 추가

## 유저 보고 내용
- 게임 시작하자마자 stage가 "clear"로 표시되는 경우가 간헐적으로 발생
- 수정이 아닌 진단 로그 추가 요청

## 의심되는 원인 (정적 분석)
- `AngryClawdGame.Start()` -> `SetupStage()` -> `PrefabUtility.InstantiatePrefab(pilePrefab)` 시점에서 PileScript가 `_pendingStart`에 등록됨
- SceneManager.Update()는 `_pendingStart` 리스트를 복사 후 비우고 순회하므로, AngryClawdGame.Start() 실행 도중 추가된 PileScript는 **다음 프레임**에야 Start() 호출됨
- PileScript.Start()에서 pig 큐브를 동적으로 생성하고 "Pig" 태그를 부여함
- 따라서 AngryClawdGame의 첫 Update() 시점에서 `CheckStageClear()` -> `FindGameObjectsWithTag("Pig")`가 호출되면 pig가 0마리로 잡혀 즉시 stage clear 판정 가능
- "간헐적"인 이유는 추가 분석 필요 (프레임 타이밍, 씬 로드 순서, 또는 다른 조건이 관여할 수 있음)

## 추가한 진단 로그
모든 로그는 `[AngryClawd][Diag]` 태그 사용, `Debug.Log` 사용.

### AngryClawdGame.cs
1. **Start() 진입/완료**: frame, time, Start 완료 시 pigCount
2. **SetupStage() 완료**: pile 수, shot 수, pigCount, frame
3. **CheckStageClear()**: 첫 10프레임 또는 pigCount==0일 때 상세 상태 (pigCount, frame, time, cannonballFired, stage, activePiles)
4. **STAGE CLEAR TRIGGERED**: clear 판정 시 stage, frame, time + 각 pile의 children 수
5. **Stage clear delay 완료**: frame, time
6. **NextStage() 호출**: 다음 stage 번호, frame, time

### PileScript.cs
1. **Start() 진입**: frame, time, gameObject name
2. **Start() 완료 (BuildPile 후)**: 전체 pigCount, frame

## 변경된 파일
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/AngryClawdGame.cs` -- 6곳에 진단 Debug.Log 추가
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/PileScript.cs` -- 2곳에 진단 Debug.Log 추가

## 검증
- dotnet build 성공 확인
- 실행 확인은 유저가 플레이하여 로그를 확인해야 함

## 로그 분석 가이드
재현 시 콘솔에서 `[AngryClawd][Diag]` 로그를 확인. 예상되는 정상 흐름:
```
[AngryClawd][Diag] Start() called at frame=N
[AngryClawd][Diag] SetupStage(1) done: ... pigCount=0  <-- Start 시점에는 pig 0 (PileScript.Start 미호출)
[AngryClawd][Diag] Start() finished: pigCount=0
[AngryClawd][Diag] PileScript.Start() called at frame=N+1  <-- 다음 프레임에 호출
[AngryClawd][Diag] PileScript.Start() done: totalPigCount=1
[AngryClawd][Diag] CheckStageClear: pigCount=1, frame=N+1  <-- pig 있음, 정상
```

비정상(버그 재현) 시:
```
[AngryClawd][Diag] Start() called at frame=N
[AngryClawd][Diag] SetupStage(1) done: ... pigCount=0
[AngryClawd][Diag] Start() finished: pigCount=0
[AngryClawd][Diag] CheckStageClear: pigCount=0, frame=N  <-- PileScript.Start 전에 CheckStageClear 호출
[AngryClawd][Diag] STAGE CLEAR TRIGGERED! stage=1, frame=N  <-- 오판정!
```

## 다음 작업
- 로그 확인 후 원인이 확정되면, CheckStageClear에 "첫 N프레임은 판정 건너뛰기" 또는 PileScript.Start() 호출을 보장하는 구조 수정 적용
- 진단 로그는 원인 확정 후 제거
