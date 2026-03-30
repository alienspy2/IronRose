# Phase 46 Index: IronRose Editor CLI 브릿지

## 설계 문서
- `plans/phase46_cli-bridge.md` (전체 설계)
- `plans/phase46d_extended-commands.md` (확장 명령 설계)

## Phase 목록

| Phase | 제목 | 파일 | 선행 | 상태 |
|-------|------|------|------|------|
| 46a | Named Pipe 서버 + 기본 명령 + EngineCore 통합 | phase46a_pipe-server.md | - | 완료 |
| 46b | Python CLI 래퍼 | phase46b_python-wrapper.md | 46a | 완료 |
| 46c | 추가 명령 세트 | phase46c_extra-commands.md | 46b | 완료 |
| 46d-w1 | 핵심 명령 (go CRUD, transform, component, undo/redo) | phase46d-w1_core-commands.md | 46c | 미완료 |
| 46d-w2 | 에셋/프리팹 (prefab.*, asset.*, scene.tree/new) | phase46d-w2_asset-prefab-commands.md | 46d-w1 | 미완료 |
| 46d-w3 | 렌더링/비주얼 (material.*, light.*, camera.*, render.*) | phase46d-w3_visual-commands.md | 46d-w2 | 미완료 |
| 46d-w4 | 편의 기능 (나머지 전부) | phase46d-w4_convenience-commands.md | 46d-w3 | 미완료 |

## 의존 관계
```
Phase 46a (Named Pipe 서버 + 기본 명령 + EngineCore 통합)
    |
    v
Phase 46b (Python CLI 래퍼)
    |
    v
Phase 46c (추가 명령 세트)
    |
    v
Phase 46d-w1 (핵심: go CRUD, transform, component, undo/redo)
    |
    v
Phase 46d-w2 (에셋/프리팹: prefab.*, asset.*, scene.tree/new)
    |
    v
Phase 46d-w3 (렌더링/비주얼: material.*, light.*, camera.*, render.*)
    |
    v
Phase 46d-w4 (편의 기능: 나머지 전부)
```

## Phase 분할 근거
- **46a**: 엔진 측 핵심 인프라. Named Pipe 서버, 명령 디스패처, 로그 버퍼, EngineCore 통합. 파이프 수동 연결로 동작 확인 가능. C# 파일 3개 신규 + EngineCore 수정.
- **46b**: Python 래퍼. 46a의 파이프 서버에 연결하는 클라이언트. 엔진 코드 수정 없음. Python 파일 1개 신규.
- **46c**: 실질적인 명령 핸들러 추가. go.get, go.set_field, play.*, scene.save/load, log.recent 등. CliCommandDispatcher에 핸들러만 추가.
- **46d-w1**: 씬 구성 필수 명령. go CRUD, transform 기본, component 관리, editor undo/redo. 핸들러 15개 + 헬퍼 2개.
- **46d-w2**: 에셋/프리팹 워크플로우. prefab instantiate/save, asset list/find/guid/path, scene tree/new. 핸들러 8개 + 헬퍼 1개.
- **46d-w3**: 시각적 조작. material info/set_color/set_metallic/set_roughness, light info/set_color/set_intensity, camera info/set_fov, render info/set_ambient. 핸들러 11개 + 헬퍼 1개.
- **46d-w4**: 나머지 편의 기능. transform 추가, prefab 추가, asset 추가, editor 추가, screen, scene clear, camera/light/render 추가. 핸들러 22개 + EngineCore 스크린샷 처리.

## 영향 범위 요약

| 파일 | Phase | 변경 유형 |
|------|-------|-----------|
| `src/IronRose.Engine/Cli/CliPipeServer.cs` | 46a | 신규 - Named Pipe 서버 |
| `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` | 46a, 46c, 46d-w1~w4 | 신규 - 명령 디스패처 + 핸들러 추가 |
| `src/IronRose.Engine/Cli/CliLogBuffer.cs` | 46a | 신규 - 로그 링 버퍼 |
| `src/IronRose.Engine/EngineCore.cs` | 46a, 46d-w4 | 수정 - CLI 서버 초기화/업데이트/종료, LogSink 연결, 스크린샷 처리 |
| `tools/ironrose-cli/ironrose_cli.py` | 46b | 신규 - Python CLI 래퍼 |

## 46d Wave별 명령 수 요약

| Wave | 명령 수 | 주요 카테고리 |
|------|---------|---------------|
| w1 | 15 | go.create/destroy/rename/duplicate, transform.get/set_*, component.add/remove/list, editor.undo/redo |
| w2 | 8 | prefab.instantiate/save, asset.list/find/guid/path, scene.tree/new |
| w3 | 11 | material.info/set_*, light.info/set_*, camera.info/set_fov, render.info/set_ambient |
| w4 | 22 | transform.translate/rotate/look_at/get_children/set_local_position, prefab.create_variant/is_instance/unpack, asset.import/scan, editor.screenshot/copy/paste/select_all/undo_history, screen.info, scene.clear, camera.set_clip, light.set_type/set_range/set_shadows, render.set_skybox_exposure |
| **합계** | **56** | |
