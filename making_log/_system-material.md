# Material 시스템

## 구조
- `src/IronRose.Engine/RoseEngine/Material.cs` — Material 클래스 및 BlendMode enum 정의

## 핵심 동작
- Material은 렌더링에 필요한 모든 속성을 담는 데이터 클래스
- PBR 속성: metallic, roughness, occlusion, normalMap, MROMap
- 블렌드 모드: `blendMode` 프로퍼티로 Opaque/AlphaBlend/Additive 선택
- 텍스처 변환: textureScale, textureOffset (Unity의 Tiling & Offset에 대응)
- 스카이박스: exposure, rotation, cubemapFaceSize (Skybox 셰이더 전용)
- RenderSystem이 `_cachedCubemap`/`_cachedCubemapSource` internal 필드를 사용하여 큐브맵 캐시 관리

## 주의사항
- BlendMode enum은 Material.cs 파일 내에 같은 namespace(RoseEngine)에 정의되어 있음 (별도 파일 아님)
- `_cachedCubemap` 등 internal 필드는 RenderSystem 전용이므로 다른 곳에서 접근하면 안 됨
- 생성자가 3개: 기본, Shader 지정, Color 지정

## 사용하는 외부 라이브러리
- 없음 (엔진 내부 타입만 사용)
