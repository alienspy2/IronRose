# Material 직렬화 시스템

## 구조
- `Material.cs` (`RoseEngine` namespace) — Material 데이터 클래스 및 BlendMode enum 정의
- `RoseCache.cs` (`IronRose.AssetPipeline`) — 바이너리 캐시 직렬화 (WriteMaterial/ReadMaterial)
- `MaterialImporter.cs` (`IronRose.AssetPipeline`) — TOML .mat 파일 임포트/내보내기

## 핵심 동작

### RoseCache 바이너리 직렬화
- FormatVersion으로 캐시 호환성 관리 (현재 v9)
- WriteMaterial 순서: blendMode(byte) -> color -> emission -> metallic -> roughness -> occlusion -> normalMapStrength -> textures
- ReadMaterial은 동일 순서로 역직렬화
- FormatVersion 변경 시 기존 캐시는 자동 무효화

### MaterialImporter TOML 직렬화
- Import(): TOML에서 각 프로퍼티를 읽어 Material 인스턴스 생성
- BuildConfig(): Material 프로퍼티를 TomlConfig에 설정
- blendMode는 문자열로 저장 ("Opaque", "AlphaBlend", "Additive")
- 기존 .mat 파일에 blendMode 키가 없으면 기본값 Opaque

## 주의사항
- RoseCache WriteMaterial/ReadMaterial의 필드 순서는 정확히 일치해야 함 (바이너리 포맷)
- FormatVersion 변경 시 모든 캐시가 재생성되므로 첫 실행 시 로딩 시간 증가
- BuildConfig의 파라미터 순서와 호출부(WriteDefault, WriteMaterial)를 항상 동기화해야 함

## 사용하는 외부 라이브러리
- Tommy (TOML 파서) — TomlConfig 래퍼를 통해 사용
