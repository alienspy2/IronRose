# EngineCore 시스템

## 구조
- `src/IronRose.Engine/EngineCore.cs` — 엔진 메인 오케스트레이터
  - Initialize/Update/Render/Shutdown 생명주기 관리
  - 모든 서브시스템(그래픽, 물리, 에셋, 에디터, 스크립팅 등)의 초기화 및 매 프레임 업데이트 조율

## 핵심 동작

### Initialize() 흐름
1. `Debug.LogSink` 설정 (EditorBridge로 로그 전달)
2. `ProjectContext.Initialize()` — 프로젝트 경로 탐색
3. 프로젝트 로드 성공 시 `Debug.SetLogDirectory(ProjectRoot/Logs/)` — 로그를 프로젝트 폴더로 전환
4. `RoseConfig.Load()`, `ProjectSettings.Load()`, `EditorState.Load()`
5. 서브시스템 초기화: Application(경로 초기화 + PlayerPrefs 로드 포함), Input, Graphics, ShaderCache, RenderSystem, Screen, PluginApi, Physics
6. 프로젝트 로드 시: Assets, LiveCode, GpuCompressor 초기화
7. `HeadlessEditor`가 false이면 에디터(ImGuiOverlay) 초기화
8. 에셋 캐시 워밍업 시작

### Update() 흐름
- EditorBridge 명령 처리
- Input 업데이트
- 자동화 테스트 명령 실행
- 워밍업/썸네일/리임포트 진행 중이면 해당 진행 표시 후 return
- 에셋 파일 변경 감지, 핫 리로드
- 게임 로직(물리, 스크립트, PostProcess) — Playing 상태에서만
- ImGui 업데이트
- ImGui 입력 상태 동기화

### 프로젝트 바인딩 모델
- **프로세스 = 프로젝트**: 하나의 프로세스는 하나의 프로젝트에 바인딩됨
- mid-session 프로젝트 전환은 지원하지 않음 (코드 제거 완료)
- 프로젝트 전환은 프로세스 재시작을 통해서만 가능

### Shutdown() 흐름
1. `PlayerPrefs.Shutdown()` — 더티 상태면 자동 Save
2. `Application.isPlaying = false`, QuitAction 해제
3. SceneManager, AssetDatabase, 각 서브시스템 정리/Dispose

## 주의사항
- `HeadlessEditor = true`일 때 ImGuiOverlay 초기화를 완전히 스킵 (Standalone 빌드용)
- EngineCore는 ImGuiOverlay의 null 여부로 에디터 모드를 판단
- 워밍업/리임포트/썸네일 생성 중에는 게임 로직 Update를 스킵하고 진행 모달만 표시
- **`EditorState.Load()`는 반드시 `InitEditor()` 이전에 호출되어야 함**. ImGuiOverlay.Initialize()에서 EditorState의 ImGuiLayoutData, EditorFont, UiScale을 참조하기 때문. 이 순서가 역전되면 레이아웃/폰트/스케일이 복원되지 않음.

## 사용하는 외부 라이브러리
- Silk.NET: 윈도우, 입력 관리 (IWindow, IInputContext)
- Veldrid: 그래픽 백엔드 (CommandList, Framebuffer)
- SixLabors.ImageSharp: 윈도우 아이콘 로딩
