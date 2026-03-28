# BombScript 폭발 시 Block 이외 오브젝트(Cannonball, Floor)까지 파괴하는 버그 수정

## 유저 보고 내용
- 폭탄블럭(BombScript)이 폭발할 때, 포탄(Cannonball)과 바닥(Floor/Ground)까지 파괴됨
- 폭탄블럭은 Block만 파괴해야 하며, 포탄이나 바닥은 절대 파괴되면 안 됨

## 원인
`BombScript.Explode()` 메서드에서 `Physics.OverlapSphere`로 폭발 반경 내의 모든 콜라이더를 수집한 후, 자기 자신(`gameObject`)만 제외하고 **태그 필터링 없이 무조건 모든 오브젝트를 파괴**하고 있었음:

```csharp
// 수정 전 (문제 코드)
if (col.gameObject != gameObject)
{
    RoseEngine.Object.Destroy(col.gameObject);
}
```

이로 인해 폭발 반경 내의 Cannonball, Floor/Ground, Pig 등 모든 종류의 오브젝트가 구분 없이 파괴됨.

## 수정 내용
`Explode()` 내부의 forEach 루프에서 태그 기반 필터링을 추가:
- `"Block"` 태그인 경우에만 `Object.Destroy()`로 파괴
- `"Bomb"` 태그인 경우 해당 BombScript의 `Explode()`를 호출하여 연쇄 폭발 지원
- 그 외 태그(Cannonball, Pig, Floor/Ground, Untagged 등)는 무시

```csharp
// 수정 후
if (col.gameObject.CompareTag("Block"))
{
    RoseEngine.Object.Destroy(col.gameObject);
}
else if (col.gameObject.CompareTag("Bomb"))
{
    var otherBomb = col.gameObject.GetComponent<BombScript>();
    if (otherBomb != null)
    {
        otherBomb.Explode();
    }
}
```

## 변경된 파일
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/BombScript.cs` -- Explode() 메서드에서 태그 기반 파괴 필터링 추가. Block만 파괴, Bomb은 연쇄 폭발, 나머지는 무시.

## 검증
- 정적 분석으로 원인 특정 (코드 로직이 명확)
- `dotnet build` 성공 확인
- 유저 실행 테스트 필요 (GUI 게임이므로 직접 실행 불가)
