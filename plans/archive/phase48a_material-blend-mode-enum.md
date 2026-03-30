# Phase 48a: Material BlendMode enum 및 프로퍼티 추가

## 목표
- `BlendMode` enum(`Opaque`, `AlphaBlend`, `Additive`)을 정의한다.
- `Material` 클래스에 `blendMode` 프로퍼티를 추가한다.
- 기본값 `Opaque`로 기존 동작과 완전 호환을 유지한다.

## 선행 조건
- 없음 (첫 번째 phase)

## 수정할 파일

### `src/IronRose.Engine/RoseEngine/Material.cs`

- **변경 내용**: `BlendMode` enum 정의 + `Material.blendMode` 프로퍼티 추가
- **변경 위치**: 파일 최상단 namespace 블록 안, `Material` 클래스 선언 직전에 enum을 추가한다.

현재 코드 (라인 1~2):
```csharp
namespace RoseEngine
{
    public class Material
```

변경 후:
```csharp
namespace RoseEngine
{
    public enum BlendMode
    {
        Opaque = 0,
        AlphaBlend = 1,
        Additive = 2,
    }

    public class Material
```

그리고 `Material` 클래스 내부에 프로퍼티 추가. 위치: `normalMapStrength` 프로퍼티(현재 라인 16) 바로 다음, `MROMap` 프로퍼티 전.

현재 코드 (라인 16~17):
```csharp
        public float normalMapStrength { get; set; } = 1.0f;
        public Texture2D? MROMap { get; set; }
```

변경 후:
```csharp
        public float normalMapStrength { get; set; } = 1.0f;
        public Texture2D? MROMap { get; set; }

        // Blend mode for rendering pipeline selection
        public BlendMode blendMode { get; set; } = BlendMode.Opaque;
```

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `BlendMode.Opaque`, `BlendMode.AlphaBlend`, `BlendMode.Additive` 3개 값이 enum에 존재
- [ ] `Material` 인스턴스 생성 시 `blendMode` 기본값이 `BlendMode.Opaque`

## 참고
- `BlendMode` enum을 별도 파일로 분리하지 않고 `Material.cs` 파일 내에 정의한다 (Material과 밀접하게 관련된 enum이므로).
- 네이밍: camelCase 프로퍼티(`blendMode`), PascalCase enum/enum값(`BlendMode`, `Opaque` 등) -- 프로젝트 기존 컨벤션 준수.
- 이 phase는 데이터 구조만 추가하므로 런타임 동작에 영향 없음.
