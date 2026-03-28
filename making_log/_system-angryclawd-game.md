# AngryClawd 게임 시스템

## 구조
- `AngryClawdGame.cs` — 메인 게임 컨트롤러. 스테이지 생성, slingshot 발사, cannonball 추적, 클리어 판정 담당. SimpleGameBase 상속.
- `PileScript.cs` — pile prefab에 부착. Start() 시 랜덤 크기의 큐브 더미를 동적 생성.
- `BlockScript.cs` — 일반 블록 큐브에 부착. 블록/폭탄 간 고속 충돌(8.0f 이상) 시 자신 파괴.
- `PigScript.cs` — pig 큐브에 부착. 모든 충돌에 대해 속도(2.0f 이상) 체크 후 사망 처리.
- `BombScript.cs` — 폭탄 큐브에 부착. 충돌(2.0f 이상) 또는 외부 Explode() 호출 시 반경 3m 내 오브젝트 삭제.
- `CannonballScript.cs` — cannonball에 부착. 태그 기반 판정: Pig 무조건 파괴, Block 속도 조건부 파괴, Bomb Explode() 위임.
- `SimpleGameBase.cs` — 공통 기반 클래스 (빈 클래스)

## 핵심 동작
1. **스테이지 생성**: AngryClawdGame.SetupStage() → PrefabUtility.InstantiatePrefab()으로 pile prefab 인스턴스화 → PileScript.Start()에서 BuildPile() 호출
2. **큐브 더미 생성**: PileScript.BuildPile()이 랜덤 width/height 격자에 블록/pig/bomb 큐브 생성. pig는 바닥층 아닌 곳에 1개, bomb은 pig 배치 이후 5% 확률로 생성.
3. **발사**: 마우스 드래그로 에이밍 → 드래그 반대 방향으로 Impulse 발사
4. **충돌 판정**: cannonball이 직접 충돌(CannonballScript) + 블록 간 연쇄 충돌(BlockScript/PigScript/BombScript)
5. **클리어 판정**: "Pig" 태그 오브젝트가 0개이면 스테이지 클리어

## 주의사항
- `ImplicitUsings`가 활성화된 LiveCode 프로젝트에서는 `Object`, `Random` 등 System 네임스페이스와 이름이 겹치는 RoseEngine 타입을 사용할 때 반드시 `RoseEngine.` 접두사를 붙여야 함.
- 충돌 판정 역할 분담: cannonball→대상 판정은 CannonballScript, 블록 간 연쇄 충돌은 각 스크립트(BlockScript/PigScript)에서 자체 처리.
- BombScript.Explode()는 외부 호출 가능 (CannonballScript에서 사용).
- PileScript의 pigPlaced 플래그로 인해 pig 배치 전 큐브에는 bomb이 생성되지 않음 (설계 의도).

## 사용하는 외부 라이브러리
- 없음 (IronRose.Engine 프로젝트 참조만 사용)

## 파일 경로
- 모든 스크립트: `/home/alienspy/git/MyGame/LiveCode/AngryClawd/`
- 머티리얼 에셋: `Assets/AngryClawdAssets/mat_block.mat`, `mat_pig.mat`, `mat_bomb.mat`
- 빌드 명령: `cd /home/alienspy/git/MyGame && dotnet build LiveCode/LiveCode.csproj`
