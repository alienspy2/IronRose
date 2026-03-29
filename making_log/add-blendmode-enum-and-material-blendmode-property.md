# Material에 BlendMode enum 및 blendMode 프로퍼티 추가

## 수행한 작업
- `BlendMode` enum을 `Material.cs` 파일 내에 정의 (Opaque=0, AlphaBlend=1, Additive=2)
- `Material` 클래스에 `blendMode` 프로퍼티 추가 (기본값 `BlendMode.Opaque`)
- 파일 frontmatter 추가

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/Material.cs` — BlendMode enum 추가, Material.blendMode 프로퍼티 추가, frontmatter 추가

## 주요 결정 사항
- BlendMode enum을 별도 파일로 분리하지 않고 Material.cs 내에 정의 (명세서 지시에 따름)
- enum 값에 명시적 정수값 할당 (직렬화 안정성 고려)

## 다음 작업자 참고
- Phase 48b에서 직렬화(Serialization) 지원 추가 필요
- Phase 48c에서 렌더 파이프라인이 blendMode를 읽어 실제 블렌딩 처리를 해야 함
- Phase 48e에서 에디터 UI에 블렌드 모드 선택 드롭다운 추가 필요
