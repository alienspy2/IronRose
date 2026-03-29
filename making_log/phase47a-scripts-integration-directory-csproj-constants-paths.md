# Phase 47a: Scripts 통합 -- 디렉토리/csproj/상수/경로 변경

## 수행한 작업
- LiveCode + FrozenCode 이중 구조를 Scripts 단일 구조로 통합
- 상수, 경로 속성, 템플릿, csproj 참조, UI 패널, CLI 검색, 로그 메시지를 모두 Scripts로 변경
- MyGame 프로젝트의 디렉토리/파일도 Scripts로 마이그레이션
- BombScript.cs에 누락된 explosionVfxPrefab 필드 추가 (기존 빌드 에러 수정)

## 변경된 파일

### IronRose 엔진
- `src/IronRose.Engine/RoseEngine/EngineConstants.cs` -- LiveCodePath/FrozenCodePath 상수를 ScriptsPath로 통합
- `src/IronRose.Engine/ProjectContext.cs` -- LiveCodePath/FrozenCodePath 속성을 ScriptsPath로 통합
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiScriptsPanel.cs` -- 이중 트리(LiveCode/FrozenCode)를 Scripts 단일 트리로 변경
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` -- ResolveComponentType()의 FrozenCode/LiveCode 검색을 Scripts 단일 검색으로 변경. assembly.info 핸들러의 JSON key를 scriptDemoTypes/scriptDemoCount로 변경
- `src/IronRose.Engine/RoseEngine/SceneManager.cs` -- 주석의 "LiveCode"를 "Scripts"로 변경
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs` -- 주석의 "LiveCode"를 "Scripts"로 변경
- `src/IronRose.Engine/Editor/EditorPlayMode.cs` -- 주석의 "LiveCodeManager"를 "ScriptReloadManager"로 변경
- `src/IronRose.Contracts/Screen.cs` -- 주석의 "LiveCode"를 "Scripts"로 변경
- `src/IronRose.Standalone/IronRose.Standalone.csproj` -- FrozenCode 참조를 Scripts로 변경
- `src/IronRose.Engine/LiveCodeManager.cs` -- 빌드 통과를 위한 임시 수정 (ProjectContext.LiveCodePath -> ScriptsPath)
- `src/IronRose.Engine/EngineCore.cs` -- 빌드 통과를 위한 임시 수정 (ProjectContext.FrozenCodePath -> ScriptsPath)
- `doc/CodingGuide.md` -- 스크립트 위치 설명을 LiveCode/FrozenCode에서 Scripts로 변경

### 템플릿
- `templates/default/LiveCode/` -> `templates/default/Scripts/` (디렉토리 이름 변경, git mv)
- `templates/default/Scripts/Scripts.csproj` -- csproj 주석 갱신
- `templates/default/FrozenCode/` -- 삭제 (git rm -r)

### MyGame 프로젝트
- `MyGame/Scripts/` 디렉토리 생성 및 LiveCode/FrozenCode의 모든 .cs 파일 이동
- `MyGame/Scripts/Scripts.csproj` -- 새로 생성
- `MyGame/MyGame.sln` -- LiveCode를 Scripts로 변경, FrozenCode 프로젝트 엔트리 제거
- `MyGame/Scripts/AngryClawd/BombScript.cs` -- 누락된 explosionVfxPrefab 필드 추가 (기존 빌드 에러 수정)
- `MyGame/LiveCode/` -- 삭제
- `MyGame/FrozenCode/` -- 삭제

## 주요 결정 사항
- CliCommandDispatcher.cs의 `EngineCore.LiveCodeDemoTypes` 프로퍼티 참조는 Phase 47c에서 이름 변경되므로 이 Phase에서는 그대로 유지. JSON key만 변경 (liveCodeDemoTypes -> scriptDemoTypes)
- LiveCodeManager.cs와 EngineCore.cs는 Phase 47b/47c에서 전면 재작성되므로 최소 수정만 수행
- MyGame의 BombScript.cs에 explosionVfxPrefab 필드가 누락되어 빌드 실패 -- 기존 LiveCode에서도 동일한 에러가 있었으나 Roslyn 런타임 컴파일이라 빌드 시점에 노출되지 않았음. nullable GameObject? 타입으로 필드 추가하여 해결

## 다음 작업자 참고
- Phase 47b에서 LiveCodeManager.cs가 ScriptReloadManager.cs로 전면 재작성됨
- Phase 47c에서 EngineCore.cs의 LiveCodeDemoTypes 프로퍼티가 이름 변경됨
- IronRose.Standalone.csproj의 Scripts 참조는 IronRose 레포 루트에 Scripts/ 디렉토리가 없어서 MSB9008 경고 발생 -- Standalone 프로젝트는 게임 프로젝트에서만 사용하므로 정상 동작
- PileScript.cs의 explosionVfxPrefab 필드도 nullable로 변경하면 경고 제거 가능
