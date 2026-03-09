# CharacterController 벽 접촉 시 jitter(떨림) 수정

## 유저 보고 내용
- 벽에 비빌 때 여전히 jitter가 있음 (다중 평면 제약 슬라이딩 적용 후에도)
- enableOverlapRecovery는 false 상태

## 원인
슬라이딩 계산 후 결과 모션이 충돌 평면 안쪽을 향하는 성분이 남아있었음:

1. **부동소수점 오차**: 단일 평면 슬라이딩 `leftoverMotion - hitNormal * Dot(leftoverMotion, hitNormal)`은 수학적으로 법선에 수직이어야 하지만, 부동소수점 연산 오차로 법선 방향 미세 성분이 남을 수 있음
2. **법선 미세 변동**: 바닥/벽의 법선이 프레임마다 미세하게 달라지면서 이전 프레임의 슬라이딩 결과가 현재 프레임에서는 벽 안쪽을 향하게 됨
3. **crease 투영의 평면 침범**: 2평면 crease 방향으로 투영한 결과가 첫 번째 평면의 안쪽을 향할 수 있음

이러한 미세 성분이 다음 sweep에서 즉시 재충돌을 유발하여 position이 매 프레임 벽 쪽/바깥쪽으로 진동하는 jitter 패턴을 만듦.

## 수정 내용
슬라이딩 계산 직후 결과 모션이 관련 충돌 평면 안쪽을 향하는지 dot product로 체크하고, 해당 성분을 제거:

- 현재 충돌한 평면(hitNormal): `Dot(remainingMotion, hitNormal) < 0`이면 해당 성분 제거
- 이전 평면(plane0Normal): planeCount >= 2일 때 `Dot(remainingMotion, plane0Normal) < 0`이면 해당 성분 제거

이는 근본 원인(슬라이딩 결과에 벽 안쪽 성분이 남는 것) 자체를 제거하는 수정이다.

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/CharacterController.cs` -- 슬라이딩 계산 후 충돌 평면 안쪽 향하는 잔여 모션 성분 제거 로직 추가

## 검증
- dotnet build 성공 (오류 0개)
- 유저에게 실행 테스트 확인 요청 필요 (벽 비비기 동작은 GUI 조작 필요)
