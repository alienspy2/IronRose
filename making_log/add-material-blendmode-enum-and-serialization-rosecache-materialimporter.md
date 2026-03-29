# Material BlendMode enum 추가 및 RoseCache/MaterialImporter 직렬화

## 수행한 작업
- `BlendMode` enum (Opaque=0, AlphaBlend=1, Additive=2)을 Material.cs에 추가
- `Material` 클래스에 `blendMode` 프로퍼티 추가 (기본값 Opaque)
- RoseCache FormatVersion 8 -> 9로 증가
- RoseCache의 WriteMaterial/ReadMaterial에 blendMode byte 직렬화 추가
- MaterialImporter Import()에서 TOML의 blendMode 문자열을 파싱하여 Material에 설정
- MaterialImporter BuildConfig()에 blendMode 파라미터 추가, TOML에 문자열로 저장
- WriteDefault(), WriteMaterial() 호출부 수정

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/Material.cs` — BlendMode enum 정의, Material.blendMode 프로퍼티 추가, frontmatter 추가
- `src/IronRose.Engine/AssetPipeline/RoseCache.cs` — FormatVersion 9, WriteMaterial/ReadMaterial에 blendMode 직렬화, frontmatter 추가
- `src/IronRose.Engine/AssetPipeline/MaterialImporter.cs` — Import()에 blendMode 읽기, BuildConfig()에 blendMode 파라미터/쓰기 추가, frontmatter 갱신

## 주요 결정 사항
- Phase 48a (enum 정의)와 Phase 48b (직렬화)를 함께 구현 — worktree에 Phase 48a가 아직 적용되지 않은 상태였으므로
- RoseCache에서 blendMode는 Material의 첫 번째 필드로 직렬화 (color 앞)하여 명세서 순서 준수
- TOML에는 blendMode를 항상 명시적으로 저장하여 가독성 확보 (Opaque가 기본이더라도)
- Enum.TryParse에 ignoreCase=true로 대소문자 무관 파싱

## 다음 작업자 참고
- FormatVersion 9로 변경되어 기존 캐시는 첫 실행 시 자동 재생성됨
- Phase 48c (렌더 파이프라인)에서 blendMode에 따라 실제 블렌딩 동작을 분기해야 함
- Phase 48e (에디터 UI)에서 Inspector에 blendMode 드롭다운 추가 필요
