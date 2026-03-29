# CLI material 블렌드 모드 명령 추가 (Phase 48f)

## 수행한 작업
- `material.info` CLI 응답에 `blendMode` 필드 추가
- `material.set_blend_mode` CLI 핸들러 신규 추가 (Opaque/AlphaBlend/Additive)
- `material.create`에 blendMode 옵션 파라미터 (4번째 인자) 추가
- 선행 조건으로 Material.cs에 BlendMode enum 및 blendMode 프로퍼티 추가 (Phase 48a)
- 선행 조건으로 MaterialImporter에 blendMode 직렬화/역직렬화 추가 (Phase 48b)

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/Material.cs` — BlendMode enum 추가, Material.blendMode 프로퍼티 추가, frontmatter 추가
- `src/IronRose.Engine/AssetPipeline/MaterialImporter.cs` — Import()에서 blendMode 역직렬화, BuildConfig/WriteMaterial에 blendMode 직렬화 추가
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` — material.info에 blendMode 필드, material.set_blend_mode 핸들러, material.create에 blendMode 옵션

## 주요 결정 사항
- worktree가 Phase 48a/48b 이전에 분기되어 BlendMode enum과 직렬화가 없었으므로, 선행 조건도 함께 구현
- MaterialImporter에서 blendMode가 Opaque(기본값)일 때는 TOML에 기록하지 않음 (기존 파일 호환성 유지)
- material.create에서 색상 없이 blendMode만 지정하는 경우도 WriteMaterial 사용으로 통일

## 다음 작업자 참고
- Phase 48c: 렌더 파이프라인에서 blendMode를 읽어 실제 블렌딩 처리 구현 필요
- Phase 48e: 에디터 Inspector UI에 블렌드 모드 선택 드롭다운 추가 필요
- 메인 브랜치 머지 시 Material.cs, MaterialImporter.cs가 이미 48a/48b 변경이 적용되어 있을 수 있으므로 충돌 주의
