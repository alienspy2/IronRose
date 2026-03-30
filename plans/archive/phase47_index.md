# Phase Index: Roslyn 제거 및 Scripts 통합

설계 문서: `plans/phase47_remove-roslyn-scripts-integration.md`

## Phase 목록

| Phase | 제목 | 파일 | 선행 | 상태 |
|-------|------|------|------|------|
| 47a | Scripts 통합 (상수/경로/템플릿/UI/CLI/MyGame) | phase47a_scripts-integration.md | - | 미완료 |
| 47b | ScriptReloadManager (dotnet build 방식 전환 + EngineCore 기본 변경) | phase47b_script-reload-manager.md | 47a | 미완료 |
| 47c | Play mode 콜백 연결 및 최종 정리 | phase47c_engine-core-integration.md | 47b | 미완료 |
| 47d | Roslyn 제거 및 문서 갱신 | phase47d_cleanup-roslyn-removal.md | 47c | 미완료 |

## 의존 관계

```
Phase 47a (Scripts 통합)
  |
  v
Phase 47b (ScriptReloadManager + EngineCore 기본 변경)
  |
  v
Phase 47c (Play mode 콜백 연결)
  |
  v
Phase 47d (정리: Roslyn 제거 + 문서)
```

순차 의존. 각 Phase가 이전 Phase에 의존한다.

## Phase별 주요 변경 요약

### Phase 47a: Scripts 통합
- EngineConstants: `LiveCodePath`/`FrozenCodePath` --> `ScriptsPath`
- ProjectContext: `LiveCodePath`/`FrozenCodePath` --> `ScriptsPath`
- 템플릿 디렉토리: `LiveCode/` --> `Scripts/`, `FrozenCode/` 삭제
- ImGuiScriptsPanel: 이중 트리 --> 단일 Scripts 트리
- CliCommandDispatcher: FrozenCode/LiveCode 검색 --> Scripts 검색
- Standalone csproj: FrozenCode --> Scripts 참조
- 주석 정리: SceneManager, InspectorPanel, EditorPlayMode, Screen.cs
- MyGame 디렉토리 마이그레이션
- 빌드 통과를 위한 LiveCodeManager.cs, EngineCore.cs 최소 수정

### Phase 47b: ScriptReloadManager + EngineCore 기본 변경
- `LiveCodeManager.cs` 삭제 --> `ScriptReloadManager.cs` 생성
- Roslyn 인메모리 컴파일 --> `dotnet build` + DLL 읽기
- Play mode: FileSystemWatcher 중단/재활성화 + HasSourceChangedSinceBuild
- EngineCore: `InitFrozenCode()` 삭제, `InitLiveCode()` --> `InitScripts()`
- EngineCore: 모든 필드/프로퍼티 LiveCodeManager --> ScriptReloadManager
- CliCommandDispatcher: `LiveCodeDemoTypes` --> `ScriptDemoTypes`

### Phase 47c: Play mode 콜백 연결
- EditorPlayMode: `OnBeforeEnterPlayMode` 콜백 추가
- EngineCore의 `InitScripts()`에서 콜백 등록
- 최종 grep 검증

### Phase 47d: 정리
- `ScriptCompiler.cs` 삭제
- `Microsoft.CodeAnalysis.CSharp` 패키지 제거
- 문서 재작성: `ScriptHotReloading.md`, `ProjectStructure.md`, `CLAUDE.md`
- `.claude/commands/digest.md` 삭제

## 수정 파일 총 목록

| 파일 | Phase | 변경 유형 |
|------|-------|-----------|
| `src/IronRose.Engine/RoseEngine/EngineConstants.cs` | 00 | 상수 변경 |
| `src/IronRose.Engine/ProjectContext.cs` | 00 | 속성 변경 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiScriptsPanel.cs` | 00 | 전면 수정 |
| `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` | 00, 01 | 검색 로직 + 응답 변경 |
| `src/IronRose.Engine/RoseEngine/SceneManager.cs` | 00 | 주석 변경 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs` | 00 | 주석 변경 |
| `src/IronRose.Engine/Editor/EditorPlayMode.cs` | 00, 02 | 주석 + 콜백 추가 |
| `src/IronRose.Contracts/Screen.cs` | 00 | 주석 변경 |
| `src/IronRose.Standalone/IronRose.Standalone.csproj` | 00 | 참조 변경 |
| `templates/default/LiveCode/` --> `Scripts/` | 00 | 이름 변경 + csproj 수정 |
| `templates/default/FrozenCode/` | 00 | 삭제 |
| `MyGame/` 디렉토리 마이그레이션 | 00 | 파일 이동 |
| `src/IronRose.Engine/LiveCodeManager.cs` | 00(임시), 01(삭제) | 임시 수정 후 삭제 |
| `src/IronRose.Engine/ScriptReloadManager.cs` | 01 | 신규 생성 |
| `src/IronRose.Engine/EngineCore.cs` | 00(임시), 01(주 변경), 02(콜백) | 단계적 변경 |
| `src/IronRose.Scripting/ScriptCompiler.cs` | 03 | 삭제 |
| `src/IronRose.Scripting/IronRose.Scripting.csproj` | 03 | 패키지 제거 |
| `doc/ScriptHotReloading.md` | 03 | 전면 재작성 |
| `doc/ProjectStructure.md` | 03 | 용어 변경 |
| `CLAUDE.md` | 03 | 용어 변경 |
| `.claude/commands/digest.md` | 03 | 삭제 |
