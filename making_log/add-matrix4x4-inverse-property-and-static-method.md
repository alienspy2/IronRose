# Matrix4x4.inverse 프로퍼티 및 Inverse 정적 메서드 추가

## 수행한 작업
- `Matrix4x4` struct에 `inverse` 인스턴스 프로퍼티 추가 (Unity API 호환)
- `Matrix4x4.Inverse(Matrix4x4 m)` 정적 메서드 추가
- 내부적으로 `System.Numerics.Matrix4x4.Invert()`를 사용하여 SIMD 최적화된 역행렬 계산 위임
- singular matrix(역행렬 불가)인 경우 `identity` 반환

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/Matrix4x4.cs` — `inverse` 프로퍼티, `Inverse` static 메서드 추가, frontmatter 추가

## 주요 결정 사항
- `SN.Matrix4x4.Invert()` 실패 시 예외 대신 `identity` 반환: Unity의 동작과 일치하며, 런타임 안정성 우선
- 인스턴스 프로퍼티(`matrix.inverse`)와 정적 메서드(`Matrix4x4.Inverse(m)`) 양쪽 모두 제공: Unity API 호환
- `AggressiveInlining` 적용: 기존 코드 패턴과 동일하게 성능 최적화 힌트 부여

## 다음 작업자 참고
- 현재 `Matrix4x4`에는 개별 원소 접근자(M11, M12 등)가 없음. 필요 시 추가 가능.
- `determinant` 프로퍼티도 Unity API에 있으므로 필요 시 추가 고려.
