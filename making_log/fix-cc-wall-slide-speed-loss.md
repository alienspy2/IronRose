# CharacterController 벽 비비기 후 이동속도 저하 수정

## 유저 보고 내용
- CharacterController로 벽에 비비고 나면 이동속도가 매우 느려지는 문제

## 원인
`Move()` 메서드의 slide iteration 루프에서 `leftoverMotion` 계산 시 **실제 position 이동량(safeDistance)**이 아닌 **충돌 지점까지의 거리(usedDist = hit.T * remainingDist)**를 사용하고 있었음.

두 값의 차이는 `skinWidth`(기본값 0.08f)이며, 이 차이만큼 매 iteration마다 모션이 소실됨.

- `safeDistance = max(0, hit.T * remainingDist - skinWidth)` -- 실제 position 이동량
- `usedDist = hit.T * remainingDist` -- 충돌 지점까지의 거리 (skinWidth 미적용)

벽에 비빌 때 매 프레임 최대 3회 iteration이 발생하면 `skinWidth * 3 = 0.24`만큼의 모션이 소실됨.
`moveSpeed(5) * deltaTime(0.016) = 0.08`이면 전체 모션보다 소실량이 더 크므로 사실상 이동 불가 상태가 됨.

또한 이 소실 때문에 position이 벽에 점점 더 가까워지고, 벽을 벗어나려 해도 sweep이 즉시 충돌을 감지하여 연쇄적으로 모션이 소실되는 문제가 발생함.

## 수정 내용
- `leftoverMotion` 계산에서 `usedDist` 대신 `safeDistance`를 사용하도록 변경
- `TryStepUp` 호출 시 남은 거리도 `safeDistance` 기준으로 변경

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/CharacterController.cs`
  - line 177-179: leftoverMotion 계산에서 safeDistance 사용
  - line 190: TryStepUp 전방 남은 거리에서 safeDistance 사용

## 검증
- 빌드 성공 확인 (`dotnet build`)
- 유저 실행 테스트 필요 (벽에 비비고 나서 속도가 정상으로 유지되는지 확인)
