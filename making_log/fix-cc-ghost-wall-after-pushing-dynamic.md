# CharacterController - Dynamic body 밀기 후 보이지 않는 벽 슬라이딩 수정

## 유저 보고 내용
- CC로 sphere rigidbody를 밀면 공은 정상적으로 밀려남
- 공이 떠난 후에도 CC가 마치 벽이 있는 것처럼 슬라이딩됨
- 이동을 멈추면 증상 해소, 다시 이동하면 정상

## 원인
`_prevContactNormal` 법선 보존 시스템이 dynamic body(Rigidbody)와의 충돌 법선도 보존하고 있었음.

1. CC가 sphere와 충돌 -> 충돌 법선이 `curContactNormal0`에 수집됨
2. 프레임 끝에서 `_prevContactNormal0`로 보존됨
3. 다음 프레임: sphere는 이미 밀려 떠남 -> 실제 충돌 없음 (`curContactCount == 0`)
4. 하지만 원래 motion이 여전히 같은 방향이므로 `dotOriginal < -0.001f` -> 법선 유지됨
5. Move() 시작 시 보존된 법선으로 motion의 해당 방향 성분이 계속 제거됨 -> "보이지 않는 벽" 발생

Static body(벽, 바닥 등)는 움직이지 않으므로 법선 보존이 유효하지만, dynamic body는 이동하므로 이전 프레임 법선이 다음 프레임에서 유효하지 않음.

## 수정 내용
접촉 법선 수집 시 `hit.Collidable.Mobility != CollidableMobility.Static`인 경우(dynamic body) 법선을 `curContact`에 수집하지 않도록 변경.

- `isDynamicBody` 플래그를 추가하여 dynamic body 여부 판별
- `curContactNormal0` 수집 조건에 `!isDynamicBody` 추가
- `curContactNormal1` 수집 조건에 `!isDynamicBody` 추가

이렇게 하면 dynamic body와 충돌할 때는:
- 슬라이딩 자체는 정상 동작 (해당 프레임에서는 법선 기반 슬라이딩 수행)
- 하지만 법선이 보존되지 않으므로 다음 프레임에서 "보이지 않는 벽"이 생기지 않음

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/CharacterController.cs` -- 접촉 법선 수집 시 dynamic body 제외

## 검증
- dotnet build 성공 (오류 0개)
- 유저 수동 테스트 필요: CC로 sphere를 밀고, sphere가 떠난 후 CC가 정상 이동하는지 확인
