# Math Types 시스템

## 구조
- `src/IronRose.Engine/RoseEngine/Matrix4x4.cs` — 4x4 변환 행렬 (Unity 호환 왼손 좌표계)
- `src/IronRose.Engine/RoseEngine/Vector3.cs` — 3D 벡터
- `src/IronRose.Engine/RoseEngine/Quaternion.cs` — 회전 쿼터니언

## 핵심 동작
- 모든 수학 타입은 `System.Numerics` 대응 타입을 `inner` 필드로 래핑하여 SIMD 최적화 활용
- Unity API와 동일한 네이밍/시그니처를 유지 (예: `matrix.inverse`, `Vector3.Cross()`)
- 좌표계는 **왼손 좌표계** (Unity 호환). `LookAt`, `Perspective`는 직접 구현, 그 외는 System.Numerics 위임

## 주의사항
- System.Numerics는 **오른손 좌표계** 기반이므로, `LookAt`/`Perspective`는 System.Numerics의 메서드를 사용하지 않고 직접 구현
- Matrix4x4의 곱셈 순서: System.Numerics row-major 규칙에 따라 `S * R * T = TRS`
- `inverse` 프로퍼티는 singular matrix에서 예외 대신 `identity` 반환

## 사용하는 외부 라이브러리
- System.Numerics: SIMD 최적화된 수학 연산 백엔드
