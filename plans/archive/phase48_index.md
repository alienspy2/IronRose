# Phase 48 Index: Material별 Alpha Blending 모드 선택 기능

## 개요
Material에 `Opaque`, `AlphaBlend`, `Additive` 3가지 블렌드 모드를 선택 가능하게 하고, 렌더 파이프라인에서 Opaque/Transparent를 분리하여 반투명 메시를 Forward 패스로 렌더링한다.

## Phase 목록

| Phase | 제목 | 파일 | 선행 | 상태 |
|-------|------|------|------|------|
| 48a | Material BlendMode enum 추가 | phase48a_material-blend-mode-enum.md | - | 미완료 |
| 48b | 직렬화 (RoseCache + MaterialImporter) | phase48b_serialization.md | 48a | 미완료 |
| 48c | RenderSystem 파이프라인 생성 | phase48c_render-pipelines.md | 48a | 미완료 |
| 48d | 렌더 루프 분리 (Opaque/Transparent) | phase48d_render-loop-split.md | 48c | 미완료 |
| 48e | 에디터 UI (BlendMode 콤보박스) | phase48e_editor-ui.md | 48a, 48b | 미완료 |
| 48f | rose-cli 연동 | phase48f_rose-cli.md | 48a, 48b | 미완료 |

## 의존 관계

```
Phase 48a (enum/프로퍼티)
  ├── Phase 48b (직렬화) ──┬── Phase 48e (에디터 UI)
  │                        └── Phase 48f (rose-cli)
  └── Phase 48c (파이프라인) ── Phase 48d (렌더 루프)
```

권장 실행 순서: `48a -> 48b -> 48c -> 48d -> 48e -> 48f`

48b와 48c는 서로 독립적이므로 병렬 실행 가능하나, 순차 실행을 권장한다 (빌드 확인 용이).

## 수정 대상 파일 총괄

| 파일 | Phase | 변경 유형 |
|------|-------|-----------|
| `src/IronRose.Engine/RoseEngine/Material.cs` | 48a | enum 추가, 프로퍼티 추가 |
| `src/IronRose.Engine/AssetPipeline/RoseCache.cs` | 48b | FormatVersion 증가, 직렬화 수정 |
| `src/IronRose.Engine/AssetPipeline/MaterialImporter.cs` | 48b | TOML 읽기/쓰기에 blendMode 추가 |
| `src/IronRose.Engine/RenderSystem.cs` | 48c, 48d | 파이프라인 2개 추가, 렌더 루프 수정, Dispose 수정 |
| `src/IronRose.Engine/RenderSystem.Draw.cs` | 48d | DrawOpaqueRenderers 필터링, DrawTransparentRenderers 추가 |
| `src/IronRose.Engine/RenderSystem.Shadow.cs` | 48d | Shadow Pass에서 반투명 메시 제외 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs` | 48e | Material 에디터 UI 수정 |
| `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` | 48f | material.info 수정, material.set_blend_mode 추가, material.create 수정 |

## 기존 기능 영향

- **Opaque 메시**: 변경 없음. `blendMode` 기본값이 `Opaque`이므로 기존 동작 유지.
- **Sprite/Text**: 변경 없음. 기존 `_spritePipeline` 그대로 사용.
- **RoseCache**: 버전 8 -> 9 변경. 기존 캐시 자동 무효화 (첫 실행 시 재생성).
- **기존 .mat 파일**: blendMode 키 없으면 `Opaque` 기본 동작. 하위 호환성 유지.

## 미결 사항
- 반투명 메시의 그림자: Phase 48에서는 비활성화(Shadow Pass에서 제외). 향후 필요시 별도 Phase로 추가.
- Alpha Cutout(AlphaTest): `discard` 기반 모드는 현재 범위에 포함하지 않음.
- 오브젝트 중심점 기준 정렬의 한계: 교차하는 반투명 메시에서 시각적 아티팩트 발생 가능.
