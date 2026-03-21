# Phase 43 Post-Task Bugfixes - 상세 구현 명세서

> 설계 문서: `plans/phase43_post_task_bugfixes.md` 항목 #1 ~ #15 구현.
> 항목 #16 ~ #20 (구조적 개선)은 리팩토링 범위가 크므로 본 명세서에서 제외.
> templates/default/Program.cs는 이미 삭제된 상태이므로 관련 수정 불필요.

---

## Phase A: 높음 우선순위 크래시/경로 불일치 수정 (#1, #2, #3)

### 목표
- ShaderRegistry 크래시 방지, ImGuiScriptsPanel CWD 탈피, .reimport_all sentinel 경로 통일

### 선행 조건
- 없음 (첫 번째 phase)

---

### A-1. ShaderRegistry 크래시 방지 (#1)

**파일**: `src/IronRose.Engine/EngineCore.cs`

**문제**: `EngineCore.Initialize()` 라인 144~146에서 `InitShaderCache()`와 `ShaderRegistry.Initialize()`가 `IsProjectLoaded` 가드 밖에서 항상 호출됨. `IsProjectLoaded = false`일 때 `ProjectContext.CachePath`가 CWD 기반이므로 `Shaders/` 디렉토리가 없으면 `DirectoryNotFoundException`으로 크래시.

**현재 코드** (EngineCore.cs 라인 143~155):
```csharp
            InitGraphics();
            InitShaderCache();
            ShaderRegistry.Initialize();
            InitRenderSystem();
            InitScreen();
            InitPluginApi();
            InitPhysics();
            if (ProjectContext.IsProjectLoaded)
            {
                InitAssets();
                InitLiveCode();
                InitGpuCompressor();
            }
```

**수정 후 코드**:
```csharp
            InitGraphics();
            if (ProjectContext.IsProjectLoaded)
            {
                InitShaderCache();
            }
            ShaderRegistry.Initialize();
            InitRenderSystem();
            InitScreen();
            InitPluginApi();
            InitPhysics();
            if (ProjectContext.IsProjectLoaded)
            {
                InitAssets();
                InitLiveCode();
                InitGpuCompressor();
            }
```

**파일**: `src/IronRose.Engine/ShaderRegistry.cs`

**문제**: `Shaders/` 디렉토리를 찾지 못하면 `DirectoryNotFoundException`을 throw함. `IsProjectLoaded = false`인 경우에도 안전하게 동작해야 함.

**현재 코드** (ShaderRegistry.cs 라인 34~70):
```csharp
        public static void Initialize()
        {
            // 1차: ProjectContext.EngineRoot 기준
            var candidate = Path.Combine(ProjectContext.EngineRoot, "Shaders");
            if (Directory.Exists(candidate))
            {
                ShaderRoot = Path.GetFullPath(candidate);
                Debug.Log($"[ShaderRegistry] Shader root: {ShaderRoot}");
                return;
            }

            // 2차: ProjectContext.ProjectRoot 기준 (엔진 레포 직접 실행 케이스)
            candidate = Path.Combine(ProjectContext.ProjectRoot, "Shaders");
            if (Directory.Exists(candidate))
            {
                ShaderRoot = Path.GetFullPath(candidate);
                Debug.Log($"[ShaderRegistry] Shader root (project): {ShaderRoot}");
                return;
            }

            // 3차: 기존 폴백 (CWD 기준 상위 탐색)
            string[] fallbacks = { "Shaders", "../Shaders", "../../Shaders" };
            foreach (var fb in fallbacks)
            {
                var fullPath = Path.GetFullPath(fb);
                if (Directory.Exists(fullPath))
                {
                    ShaderRoot = fullPath;
                    Debug.LogWarning($"[ShaderRegistry] Shader root (fallback): {ShaderRoot}");
                    return;
                }
            }

            throw new DirectoryNotFoundException(
                "[ShaderRegistry] Shaders directory not found. " +
                $"Searched: {ProjectContext.EngineRoot}/Shaders, {ProjectContext.ProjectRoot}/Shaders, CWD fallbacks");
        }
```

**수정 후 코드**:
```csharp
        public static void Initialize()
        {
            // 1차: ProjectContext.EngineRoot 기준
            var candidate = Path.Combine(ProjectContext.EngineRoot, "Shaders");
            if (Directory.Exists(candidate))
            {
                ShaderRoot = Path.GetFullPath(candidate);
                Debug.Log($"[ShaderRegistry] Shader root: {ShaderRoot}");
                return;
            }

            // 2차: ProjectContext.ProjectRoot 기준 (엔진 레포 직접 실행 케이스)
            candidate = Path.Combine(ProjectContext.ProjectRoot, "Shaders");
            if (Directory.Exists(candidate))
            {
                ShaderRoot = Path.GetFullPath(candidate);
                Debug.Log($"[ShaderRegistry] Shader root (project): {ShaderRoot}");
                return;
            }

            // 3차: 기존 폴백 (CWD 기준 상위 탐색)
            string[] fallbacks = { "Shaders", "../Shaders", "../../Shaders" };
            foreach (var fb in fallbacks)
            {
                var fullPath = Path.GetFullPath(fb);
                if (Directory.Exists(fullPath))
                {
                    ShaderRoot = fullPath;
                    Debug.LogWarning($"[ShaderRegistry] Shader root (fallback): {ShaderRoot}");
                    return;
                }
            }

            // IsProjectLoaded = false인 경우(Startup Panel 모드) 크래시 방지:
            // Shaders 디렉토리 없이도 엔진이 시작되도록 경고만 출력
            if (!ProjectContext.IsProjectLoaded)
            {
                Debug.LogWarning(
                    "[ShaderRegistry] Shaders directory not found, but no project loaded. " +
                    "Shader features will be unavailable until a project is opened.");
                return;
            }

            throw new DirectoryNotFoundException(
                "[ShaderRegistry] Shaders directory not found. " +
                $"Searched: {ProjectContext.EngineRoot}/Shaders, {ProjectContext.ProjectRoot}/Shaders, CWD fallbacks");
        }
```

---

### A-2. ImGuiScriptsPanel CWD 기반 탐색 제거 (#2)

**파일**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiScriptsPanel.cs`

**문제**: `FindRootDirectories()` (라인 512~540)가 CWD 기준 `".", "..", "../.."` 휴리스틱으로 LiveCode/FrozenCode 디렉토리를 탐색함. `ProjectContext.LiveCodePath` / `ProjectContext.FrozenCodePath`를 사용해야 함.

**참고**: 이 패널의 `Draw()` 메서드 라인 70에 이미 `if (!ProjectContext.IsProjectLoaded) return;` 가드가 있음. 하지만 `FindRootDirectories()`는 생성자(라인 64)에서 호출되므로 `ProjectContext`가 아직 초기화되지 않았을 수 있음. 따라서 `FindRootDirectories()`를 lazy 초기화로 변경하거나, 첫 번째 Draw 호출 시 초기화하도록 수정.

**현재 코드** (라인 62~66):
```csharp
        public ImGuiScriptsPanel()
        {
            FindRootDirectories();
            SetupWatchers();
        }
```

**현재 코드** (라인 512~540):
```csharp
        private void FindRootDirectories()
        {
            // Search for LiveCode and FrozenCode directories the same way LiveCodeManager does
            string[] searchRoots = { ".", "..", "../.." };
            foreach (var root in searchRoots)
            {
                string liveCodeDir = Path.GetFullPath(Path.Combine(root, EngineDirectories.LiveCodePath));
                string frozenCodeDir = Path.GetFullPath(Path.Combine(root, EngineDirectories.FrozenCodePath));

                if (Directory.Exists(liveCodeDir))
                    _liveCodeRoot = liveCodeDir;
                if (Directory.Exists(frozenCodeDir))
                    _frozenCodeRoot = frozenCodeDir;

                if (_liveCodeRoot != null || _frozenCodeRoot != null)
                    break;
            }

            // Fallback: create LiveCode if not found
            if (_liveCodeRoot == null)
            {
                _liveCodeRoot = Path.GetFullPath(EngineDirectories.LiveCodePath);
                Directory.CreateDirectory(_liveCodeRoot);
            }

            Debug.Log($"[Scripts] LiveCode root: {_liveCodeRoot}");
            if (_frozenCodeRoot != null)
                Debug.Log($"[Scripts] FrozenCode root: {_frozenCodeRoot}");
        }
```

**수정 방안**: 생성자에서 `FindRootDirectories()`를 제거하고, 첫 번째 `Draw()` 호출 시 lazy 초기화하도록 변경. `FindRootDirectories()` 내부를 `ProjectContext` 기반으로 교체.

**수정 후 코드** - 생성자 (라인 62~66):
```csharp
        public ImGuiScriptsPanel()
        {
            // FindRootDirectories/SetupWatchers는 첫 Draw()에서 lazy 초기화
        }
```

**수정 후 코드** - `_initialized` 필드 추가 (기존 필드 영역, 라인 37 부근에 추가):
```csharp
        private bool _needsRebuild = true;
        private bool _initialized;
```

**수정 후 코드** - Draw() 시작 부분 (라인 68~77):
```csharp
        public void Draw()
        {
            if (!ProjectContext.IsProjectLoaded) return;

            if (!IsOpen) return;

            if (!_initialized)
            {
                _initialized = true;
                FindRootDirectories();
                SetupWatchers();
                _needsRebuild = true;
            }

            if (_needsRebuild)
            {
                RebuildTree();
                _needsRebuild = false;
            }
```

**수정 후 코드** - `FindRootDirectories()` (라인 512~540):
```csharp
        private void FindRootDirectories()
        {
            // ProjectContext 기반으로 LiveCode/FrozenCode 디렉토리 설정
            var liveCodeDir = ProjectContext.LiveCodePath;
            var frozenCodeDir = ProjectContext.FrozenCodePath;

            if (Directory.Exists(liveCodeDir))
                _liveCodeRoot = liveCodeDir;

            if (Directory.Exists(frozenCodeDir))
                _frozenCodeRoot = frozenCodeDir;

            // Fallback: LiveCode 디렉토리가 없으면 생성
            if (_liveCodeRoot == null)
            {
                _liveCodeRoot = liveCodeDir;
                Directory.CreateDirectory(_liveCodeRoot);
            }

            Debug.Log($"[Scripts] LiveCode root: {_liveCodeRoot}");
            if (_frozenCodeRoot != null)
                Debug.Log($"[Scripts] FrozenCode root: {_frozenCodeRoot}");
        }
```

---

### A-3. .reimport_all sentinel 경로 통일 (#3)

**파일**: `src/IronRose.RoseEditor/Program.cs`

**문제**: 라인 46에서 `Directory.GetCurrentDirectory()`를 사용하여 sentinel을 감지하지만, `ImGuiOverlay.cs`는 `ProjectContext.ProjectRoot`를 사용하여 생성함.

**참고**: `ProjectContext.Initialize()`는 `Main()` 이후 `OnLoad()`의 `_engine.Initialize(_window)` 내부에서 호출됨. 따라서 `Main()` 시점에서는 `ProjectContext.ProjectRoot`가 아직 초기화되지 않았음. sentinel 체크를 `OnLoad()` 내부의 `_engine.Initialize()` 이후로 이동해야 함.

**현재 코드** (Program.cs 라인 41~52):
```csharp
        static void Main(string[] _)
        {
            Debug.Log("[IronRose Editor] Starting...");

            // Reimport All sentinel 확인 (이전 실행에서 요청)
            var sentinelPath = Path.Combine(Directory.GetCurrentDirectory(), ".reimport_all");
            if (File.Exists(sentinelPath))
            {
                _reimportAll = true;
                File.Delete(sentinelPath);
                Debug.Log("[IronRose Editor] Reimport All requested — will clear cache on startup");
            }

            var options = WindowOptions.DefaultVulkan;
```

**현재 코드** (Program.cs 라인 73~107):
```csharp
        static void OnLoad()
        {
            Debug.Log($"[IronRose Editor] Window created: {_window!.Size.X}x{_window.Size.Y}");

            // 화면 밖이면 기본 위치로 리셋
            ValidateWindowPosition();

            // Reimport All: 설정 로드 후 ForceClearCache 강제 활성화
            if (_reimportAll)
            {
                RoseConfig.Load();
                RoseConfig.EnableForceClearCache();
            }

            _engine = new EngineCore();

            // 워밍업 완료 후 씬 로드 체인
            _engine.OnWarmUpComplete = LoadSceneChain;

            // New Scene 콜백 등록 (Contracts API)
            IronRose.API.EditorScene.CreateDefaultSceneImpl = EditorUtils.CreateDefaultScene;

            _engine.Initialize(_window);
```

**수정 후 코드** - `Main()` 에서 sentinel 체크 제거 (라인 41~56):
```csharp
        static void Main(string[] _)
        {
            Debug.Log("[IronRose Editor] Starting...");

            var options = WindowOptions.DefaultVulkan;
```

**수정 후 코드** - `OnLoad()` 에서 `_engine.Initialize()` 이후로 sentinel 체크 이동 (라인 73~):
```csharp
        static void OnLoad()
        {
            Debug.Log($"[IronRose Editor] Window created: {_window!.Size.X}x{_window.Size.Y}");

            // 화면 밖이면 기본 위치로 리셋
            ValidateWindowPosition();

            _engine = new EngineCore();

            // 워밍업 완료 후 씬 로드 체인
            _engine.OnWarmUpComplete = LoadSceneChain;

            // New Scene 콜백 등록 (Contracts API)
            IronRose.API.EditorScene.CreateDefaultSceneImpl = EditorUtils.CreateDefaultScene;

            _engine.Initialize(_window);

            // Reimport All sentinel 확인 (ProjectContext 초기화 이후)
            var sentinelPath = Path.Combine(ProjectContext.ProjectRoot, ".reimport_all");
            if (File.Exists(sentinelPath))
            {
                File.Delete(sentinelPath);
                Debug.Log("[IronRose Editor] Reimport All requested — clearing cache");
                RoseConfig.EnableForceClearCache();
            }

            // EditorState는 EngineCore.Initialize() 내부에서 로드됨
```

**최종 수정 방안**: `RoseEditor`는 `IronRose.Rendering`을 직접 참조하지 않으므로 `ShaderCompiler.ClearCache()`를 호출할 수 없음. 가장 깔끔한 해결책은 sentinel 체크를 `EngineCore.Initialize()` 내부로 이동하여, `ProjectContext.Initialize()` 직후 + `RoseConfig.Load()` / `InitShaderCache()` 직전에 수행하는 것:

1. `RoseEditor/Program.cs`의 `Main()`에서 sentinel 체크 코드 + `_reimportAll` 필드 제거
2. `RoseEditor/Program.cs`의 `OnLoad()`에서 `if (_reimportAll)` 분기 제거
3. `EngineCore.Initialize()` 내부에서 `ProjectContext.Initialize()` 직후, `RoseConfig.Load()` 직전에 sentinel 체크 수행
4. sentinel이 발견되면 `ProjectSettings.ForceClearCache = true` 설정 → 이후 `InitShaderCache()`에서 `ShaderCompiler.ClearCache()` 자동 호출

**수정 파일 추가**: `src/IronRose.Engine/EngineCore.cs`

**현재 코드** (EngineCore.cs 라인 125~136):
```csharp
            ProjectContext.Initialize();

            // 프로젝트 로드 성공 시 로그 경로를 프로젝트 폴더로 전환
            if (ProjectContext.IsProjectLoaded)
            {
                var projectLogDir = Path.Combine(ProjectContext.ProjectRoot, "Logs");
                RoseEngine.Debug.SetLogDirectory(projectLogDir);
                RoseEngine.Debug.Log($"[Engine] Log directory switched to: {projectLogDir}");
            }

            RoseConfig.Load();
```

**수정 후 코드**:
```csharp
            ProjectContext.Initialize();

            // 프로젝트 로드 성공 시 로그 경로를 프로젝트 폴더로 전환
            if (ProjectContext.IsProjectLoaded)
            {
                var projectLogDir = Path.Combine(ProjectContext.ProjectRoot, "Logs");
                RoseEngine.Debug.SetLogDirectory(projectLogDir);
                RoseEngine.Debug.Log($"[Engine] Log directory switched to: {projectLogDir}");

                // Reimport All sentinel 확인 (ProjectContext 초기화 이후, 경로 통일)
                var sentinelPath = Path.Combine(ProjectContext.ProjectRoot, ".reimport_all");
                if (File.Exists(sentinelPath))
                {
                    File.Delete(sentinelPath);
                    RoseEngine.Debug.Log("[Engine] Reimport All requested — will clear cache on startup");
                    // RoseConfig.Load() 전에 ForceClearCache 플래그 설정
                    // 이후 InitShaderCache()에서 ShaderCompiler.ClearCache()가 호출됨
                    ProjectSettings.ForceClearCache = true;
                }
            }

            RoseConfig.Load();
```

**수정 후 RoseEditor/Program.cs OnLoad()** (reimport 분기 제거):
```csharp
        static void OnLoad()
        {
            Debug.Log($"[IronRose Editor] Window created: {_window!.Size.X}x{_window.Size.Y}");

            // 화면 밖이면 기본 위치로 리셋
            ValidateWindowPosition();

            _engine = new EngineCore();

            // 워밍업 완료 후 씬 로드 체인
            _engine.OnWarmUpComplete = LoadSceneChain;

            // New Scene 콜백 등록 (Contracts API)
            IronRose.API.EditorScene.CreateDefaultSceneImpl = EditorUtils.CreateDefaultScene;

            _engine.Initialize(_window);

            // EditorState는 EngineCore.Initialize() 내부에서 로드됨
```

**참고**: `ProjectSettings.ForceClearCache`를 직접 설정하면, 이후 `RoseConfig.ForceClearCache` (= `ProjectSettings.ForceClearCache` 위임)가 `true`를 반환하여 `InitShaderCache()`에서 `ShaderCompiler.ClearCache()`가 실행됨. 기존 동작과 동일한 효과이면서 경로 불일치가 해결됨.

---

### 검증 기준 (Phase A)
- [ ] `dotnet build` 성공
- [ ] `IsProjectLoaded = false` 상태에서 `ShaderRegistry.Initialize()`가 예외 없이 완료됨
- [ ] `ImGuiScriptsPanel`이 `ProjectContext.LiveCodePath` / `FrozenCodePath`를 사용함
- [ ] `.reimport_all` sentinel이 `ProjectContext.ProjectRoot` 기반으로 생성/감지됨

---

## Phase B: 중간 우선순위 CWD 경로 수정 (#4, #5, #6, #7)

### 목표
- CWD 의존 상대 경로들을 ProjectContext 기반으로 변환
- project.toml dead config 수정
- SaveLastProjectPath 전체 덮어쓰기 문제 수정

### 선행 조건
- Phase A 완료

---

### B-1. ImGuiProjectSettingsPanel 상대 경로 수정 (#4)

**파일**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectSettingsPanel.cs`

**문제**: `RefreshSceneListIfNeeded()` 라인 185에서 `Path.Combine("Assets", "Scenes")`로 CWD 기준 상대 경로 사용. 라인 190에서도 `Path.GetRelativePath(".", file)` 사용.

**현재 코드** (라인 183~195):
```csharp
            _sceneList.Clear();
            var scenesDir = Path.Combine("Assets", "Scenes");
            if (!Directory.Exists(scenesDir)) return;

            foreach (var file in Directory.GetFiles(scenesDir, "*.scene", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(".", file).Replace('\\', '/');
                var name = Path.GetFileNameWithoutExtension(file);
                _sceneList.Add((relPath, name));
            }
```

**수정 후 코드**:
```csharp
            _sceneList.Clear();
            var scenesDir = Path.Combine(ProjectContext.AssetsPath, "Scenes");
            if (!Directory.Exists(scenesDir)) return;

            foreach (var file in Directory.GetFiles(scenesDir, "*.scene", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(ProjectContext.ProjectRoot, file).Replace('\\', '/');
                var name = Path.GetFileNameWithoutExtension(file);
                _sceneList.Add((relPath, name));
            }
```

---

### B-2. Standalone Program.cs 경로 수정 (#5)

**파일**: `src/IronRose.Standalone/Program.cs`

**문제**: 라인 56에서 `Path.Combine("Assets", "Scenes", "DefaultScene.scene")`로 CWD 기준 상대 경로 사용.

**현재 코드** (라인 48~57):
```csharp
        static void LoadStartScene()
        {
            // ProjectSettings에서 시작 씬 경로 읽기
            var scenePath = ProjectSettings.StartScenePath;

            if (string.IsNullOrEmpty(scenePath) || !File.Exists(scenePath))
            {
                // 폴백: Assets/Scenes/DefaultScene.scene
                scenePath = Path.GetFullPath(Path.Combine("Assets", "Scenes", "DefaultScene.scene"));
            }
```

**수정 후 코드**:
```csharp
        static void LoadStartScene()
        {
            // ProjectSettings에서 시작 씬 경로 읽기
            var scenePath = ProjectSettings.StartScenePath;

            if (string.IsNullOrEmpty(scenePath) || !File.Exists(scenePath))
            {
                // 폴백: Assets/Scenes/DefaultScene.scene
                scenePath = Path.GetFullPath(Path.Combine(ProjectContext.AssetsPath, "Scenes", "DefaultScene.scene"));
            }
```

**참고**: `IronRose.Engine` 네임스페이스의 `ProjectContext`는 이미 `using IronRose.Engine;`으로 참조되어 있음 (라인 5).

---

### B-3. project.toml dead config 수정 (#6)

**파일**: `templates/default/project.toml`

**문제**: `[build] start_scene = "Assets/Scenes/DefaultScene.scene"` 인데 실제 존재하는 파일은 `Assets/Scenes/Sample.scene` (`rose_projectSettings.toml`에 올바르게 설정됨).

**현재 코드**:
```toml
[project]
name = "{{ProjectName}}"
version = "0.1.0"

[engine]
# 엔진 소스 경로 (상대 경로)
path = "../IronRose"

[editor]
last_scene = ""

[build]
start_scene = "Assets/Scenes/DefaultScene.scene"
```

**수정 후 코드**:
```toml
[project]
name = "{{ProjectName}}"
version = "0.1.0"

[engine]
# 엔진 소스 경로 (상대 경로)
path = "../IronRose"

[editor]
last_scene = ""

[build]
start_scene = "Assets/Scenes/Sample.scene"
```

---

### B-4. SaveLastProjectPath 전체 덮어쓰기 수정 (#7)

**파일**: `src/IronRose.Engine/ProjectContext.cs`

**문제**: `SaveLastProjectPath()` (라인 197~210)에서 `~/.ironrose/settings.toml` 전체를 `[editor]` 섹션만으로 덮어씀. 향후 다른 섹션 추가 시 기존 설정 유실.

**현재 코드** (라인 197~210):
```csharp
        public static void SaveLastProjectPath(string projectPath)
        {
            try
            {
                Directory.CreateDirectory(GlobalSettingsDir);
                var content = $"[editor]\nlast_project = \"{Path.GetFullPath(projectPath).Replace("\\", "/")}\"\n";
                File.WriteAllText(GlobalSettingsPath, content);
                Debug.Log($"[ProjectContext] Saved last project to settings: {projectPath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ProjectContext] Failed to save settings: {ex.Message}");
            }
        }
```

**수정 후 코드** (read-modify-write 패턴):
```csharp
        public static void SaveLastProjectPath(string projectPath)
        {
            try
            {
                Directory.CreateDirectory(GlobalSettingsDir);
                var normalizedPath = Path.GetFullPath(projectPath).Replace("\\", "/");

                // 기존 settings.toml이 있으면 읽어서 수정, 없으면 새로 생성
                TomlTable table;
                if (File.Exists(GlobalSettingsPath))
                {
                    try
                    {
                        table = Toml.ToModel(File.ReadAllText(GlobalSettingsPath));
                    }
                    catch
                    {
                        // 파싱 실패 시 새로 생성
                        table = new TomlTable();
                    }
                }
                else
                {
                    table = new TomlTable();
                }

                // [editor] 섹션 업데이트
                if (!table.TryGetValue("editor", out var editorVal) || editorVal is not TomlTable editorTable)
                {
                    editorTable = new TomlTable();
                    table["editor"] = editorTable;
                }
                editorTable["last_project"] = normalizedPath;

                File.WriteAllText(GlobalSettingsPath, Toml.FromModel(table));
                Debug.Log($"[ProjectContext] Saved last project to settings: {projectPath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ProjectContext] Failed to save settings: {ex.Message}");
            }
        }
```

**참고**: `Tomlyn.Toml.FromModel()` / `Toml.ToModel()` 은 이미 `ProjectContext.cs`에서 `using Tomlyn;` / `using Tomlyn.Model;` 으로 참조됨.

---

### 검증 기준 (Phase B)
- [ ] `dotnet build` 성공
- [ ] `ImGuiProjectSettingsPanel`에서 씬 목록이 `ProjectContext.AssetsPath` 기반으로 탐색됨
- [ ] `Standalone/Program.cs`에서 폴백 씬 경로가 `ProjectContext.AssetsPath` 기반
- [ ] `templates/default/project.toml`의 start_scene이 Sample.scene으로 변경됨
- [ ] `SaveLastProjectPath()`가 기존 settings.toml 내용을 보존함

---

## Phase C: 중간 우선순위 안전성 수정 (#8, #9)

### 목표
- NativeFileDialog 타임아웃 후 좀비 프로세스 방지
- PrefabImporter / ScriptCompiler TOCTOU 수정

### 선행 조건
- Phase A 완료

---

### C-1. NativeFileDialog 타임아웃 후 좀비 프로세스 수정 (#8)

**파일**: `src/IronRose.Engine/Editor/ImGui/NativeFileDialog.cs`

**문제**: `RunProcess()` 라인 224에서 `process.WaitForExit(30000)` 타임아웃 이후 프로세스를 Kill하지 않음. `finally` 블록에서 `_runningProcess = null`로 참조만 정리되므로 `KillRunning()`도 작동하지 않게 됨.

**현재 코드** (라인 201~241):
```csharp
        private static string? RunProcess(string command, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null) return null;

                lock (_processLock)
                    _runningProcess = process;

                try
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(30000);

                    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                        return null;

                    return output;
                }
                finally
                {
                    lock (_processLock)
                        _runningProcess = null;
                }
            }
            catch
            {
                return null;
            }
        }
```

**수정 후 코드**:
```csharp
        private static string? RunProcess(string command, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null) return null;

                lock (_processLock)
                    _runningProcess = process;

                try
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    bool exited = process.WaitForExit(30000);

                    if (!exited)
                    {
                        // 타임아웃: 좀비 프로세스 방지를 위해 강제 종료
                        try { process.Kill(); } catch { }
                        return null;
                    }

                    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                        return null;

                    return output;
                }
                finally
                {
                    lock (_processLock)
                        _runningProcess = null;
                }
            }
            catch
            {
                return null;
            }
        }
```

---

### C-2. PrefabImporter TOCTOU 수정 (#9)

**파일**: `src/IronRose.Engine/AssetPipeline/PrefabImporter.cs`

**문제 1**: `IsVariantPrefab()` (라인 133~136) — `File.Exists()` 후 `File.ReadAllText()` 를 catch 없이 호출.

**현재 코드** (라인 132~137):
```csharp
        public static bool IsVariantPrefab(string prefabPath)
        {
            if (!File.Exists(prefabPath)) return false;
            var tomlStr = File.ReadAllText(prefabPath);
            return SceneSerializer.GetBasePrefabGuid(tomlStr) != null;
        }
```

**수정 후 코드**:
```csharp
        public static bool IsVariantPrefab(string prefabPath)
        {
            try
            {
                if (!File.Exists(prefabPath)) return false;
                var tomlStr = File.ReadAllText(prefabPath);
                return SceneSerializer.GetBasePrefabGuid(tomlStr) != null;
            }
            catch (IOException)
            {
                return false;
            }
        }
```

**문제 2**: `GetBasePrefabGuidFromFile()` (라인 142~147) — 동일 패턴.

**현재 코드** (라인 142~147):
```csharp
        public static string? GetBasePrefabGuidFromFile(string prefabPath)
        {
            if (!File.Exists(prefabPath)) return null;
            var tomlStr = File.ReadAllText(prefabPath);
            return SceneSerializer.GetBasePrefabGuid(tomlStr);
        }
```

**수정 후 코드**:
```csharp
        public static string? GetBasePrefabGuidFromFile(string prefabPath)
        {
            try
            {
                if (!File.Exists(prefabPath)) return null;
                var tomlStr = File.ReadAllText(prefabPath);
                return SceneSerializer.GetBasePrefabGuid(tomlStr);
            }
            catch (IOException)
            {
                return null;
            }
        }
```

---

### C-3. ScriptCompiler TOCTOU 수정 (#9)

**파일**: `src/IronRose.Scripting/ScriptCompiler.cs`

**문제**: `CompileFromFiles()` 라인 58~60에서 `Where(File.Exists).Select(File.ReadAllText)` 패턴은 `Where` 통과 후 `Select` 실행 전에 파일이 삭제될 수 있음.

**현재 코드** (라인 54~64):
```csharp
        public CompilationResult CompileFromFiles(string[] filePaths, string assemblyName = "DynamicScript")
        {
            Debug.Log($"[Scripting] Compiling {filePaths.Length} files: {assemblyName}");

            var syntaxTrees = filePaths
                .Where(f => File.Exists(f))
                .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f))
                .ToArray();

            return CompileFromSyntaxTrees(syntaxTrees, assemblyName);
        }
```

**수정 후 코드**:
```csharp
        public CompilationResult CompileFromFiles(string[] filePaths, string assemblyName = "DynamicScript")
        {
            Debug.Log($"[Scripting] Compiling {filePaths.Length} files: {assemblyName}");

            var syntaxTrees = new List<SyntaxTree>();
            foreach (var f in filePaths)
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    var source = File.ReadAllText(f);
                    syntaxTrees.Add(CSharpSyntaxTree.ParseText(source, path: f));
                }
                catch (IOException ex)
                {
                    Debug.LogWarning($"[Scripting] Skipping file {f}: {ex.Message}");
                }
            }

            return CompileFromSyntaxTrees(syntaxTrees.ToArray(), assemblyName);
        }
```

**참고**: `using System.Collections.Generic;`은 이미 ScriptCompiler.cs 라인 4에 있음. `SyntaxTree`는 `Microsoft.CodeAnalysis` 네임스페이스로 이미 라인 1에서 using됨.

---

### 검증 기준 (Phase C)
- [ ] `dotnet build` 성공
- [ ] NativeFileDialog 타임아웃 시 프로세스가 Kill됨
- [ ] PrefabImporter의 `IsVariantPrefab`/`GetBasePrefabGuidFromFile`이 IOException을 graceful하게 처리
- [ ] ScriptCompiler의 `CompileFromFiles`가 파일 삭제/접근 불가 시 예외 없이 나머지 파일을 컴파일

---

## Phase D: 낮음 우선순위 수정 (#10, #11, #12, #13, #14, #15)

### 목표
- CWD 의존 잔존 경로 제거, Debug IOException 방어, 폰트 경로 수정, 패널 가드 추가

### 선행 조건
- Phase A 완료

---

### D-1. RoseConfig.Load() CWD 의존 수정 (#10)

**파일**: `src/IronRose.Engine/RoseConfig.cs`

**문제**: 라인 65에서 `{ "rose_config.toml", "../rose_config.toml", "../../rose_config.toml" }`로 CWD 기준 탐색. `ProjectContext.ProjectRoot` 기반으로 변경해야 함.

**현재 코드** (라인 58~92):
```csharp
        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            // 레거시 rose_config.toml에서 [editor] 섹션만 읽기 (EnableEditor)
            // [cache] 섹션은 ProjectSettings.Load()에서 읽으므로 여기서는 처리하지 않는다.
            string[] searchPaths = { "rose_config.toml", "../rose_config.toml", "../../rose_config.toml" };

            foreach (var rel in searchPaths)
            {
                var path = Path.GetFullPath(rel);
                if (!File.Exists(path)) continue;

                try
                {
                    var table = Toml.ToModel(File.ReadAllText(path));

                    if (table.TryGetValue("editor", out var editorVal) && editorVal is TomlTable editor)
                    {
                        if (editor.TryGetValue("enable_editor", out var v4) && v4 is bool b4)
                            EnableEditor = b4;
                    }

                    Debug.Log($"[RoseConfig] Loaded: {path} (EnableEditor={EnableEditor})");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[RoseConfig] Failed to parse {path}: {ex.Message}");
                }
            }

            Debug.Log("[RoseConfig] No config file found, using defaults");
        }
```

**수정 후 코드**:
```csharp
        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            // 레거시 rose_config.toml에서 [editor] 섹션만 읽기 (EnableEditor)
            // [cache] 섹션은 ProjectSettings.Load()에서 읽으므로 여기서는 처리하지 않는다.

            // ProjectContext.ProjectRoot 기반 탐색 (우선) + CWD 폴백
            string[] searchPaths;
            if (!string.IsNullOrEmpty(ProjectContext.ProjectRoot))
            {
                searchPaths = new[]
                {
                    Path.Combine(ProjectContext.ProjectRoot, "rose_config.toml"),
                    "rose_config.toml",
                    Path.Combine("..", "rose_config.toml"),
                    Path.Combine("..", "..", "rose_config.toml"),
                };
            }
            else
            {
                searchPaths = new[] { "rose_config.toml", Path.Combine("..", "rose_config.toml"), Path.Combine("..", "..", "rose_config.toml") };
            }

            foreach (var rel in searchPaths)
            {
                var path = Path.GetFullPath(rel);
                if (!File.Exists(path)) continue;

                try
                {
                    var table = Toml.ToModel(File.ReadAllText(path));

                    if (table.TryGetValue("editor", out var editorVal) && editorVal is TomlTable editor)
                    {
                        if (editor.TryGetValue("enable_editor", out var v4) && v4 is bool b4)
                            EnableEditor = b4;
                    }

                    Debug.Log($"[RoseConfig] Loaded: {path} (EnableEditor={EnableEditor})");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[RoseConfig] Failed to parse {path}: {ex.Message}");
                }
            }

            Debug.Log("[RoseConfig] No config file found, using defaults");
        }
```

---

### D-2. Debug.cs IOException 미처리 수정 (#11)

**파일**: `src/IronRose.Contracts/Debug.cs`

**문제**: `Write()` 라인 80에서 `File.AppendAllText()`가 IOException을 throw할 수 있음. 로깅 메서드 내 예외는 호출자를 크래시시킴.

**현재 코드** (라인 71~90):
```csharp
        private static void Write(string level, object message)
        {
            if (!Enabled) return;

            var line = $"[{level}] {message}";
            Console.WriteLine(line);

            lock (_lock)
            {
                File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }

            var logLevel = level switch
            {
                "WARNING" => LogLevel.Warning,
                "ERROR" => LogLevel.Error,
                _ => LogLevel.Info,
            };
            LogSink?.Invoke(new LogEntry(logLevel, message?.ToString() ?? "null", DateTime.Now));
        }
```

**수정 후 코드**:
```csharp
        private static void Write(string level, object message)
        {
            if (!Enabled) return;

            var line = $"[{level}] {message}";
            Console.WriteLine(line);

            lock (_lock)
            {
                try
                {
                    File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
                }
                catch (IOException)
                {
                    // 디스크 풀, 권한 문제 등 — 콘솔 출력은 이미 완료되었으므로 무시
                }
            }

            var logLevel = level switch
            {
                "WARNING" => LogLevel.Warning,
                "ERROR" => LogLevel.Error,
                _ => LogLevel.Info,
            };
            LogSink?.Invoke(new LogEntry(logLevel, message?.ToString() ?? "null", DateTime.Now));
        }
```

---

### D-3. Debug static 생성자 실패 방어 (#12)

**파일**: `src/IronRose.Contracts/Debug.cs`

**문제**: 라인 35~39에서 `Directory.CreateDirectory("Logs")`가 `UnauthorizedAccessException`을 throw하면 `TypeInitializationException`이 발생하고 이후 `Debug` 클래스 사용이 모두 실패.

**현재 코드** (라인 35~40):
```csharp
        static Debug()
        {
            _logFileName = $"ironrose_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            Directory.CreateDirectory("Logs");
            _logPath = Path.Combine("Logs", _logFileName);
        }
```

**수정 후 코드**:
```csharp
        static Debug()
        {
            _logFileName = $"ironrose_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            try
            {
                Directory.CreateDirectory("Logs");
                _logPath = Path.Combine("Logs", _logFileName);
            }
            catch
            {
                // CWD에 Logs/ 생성 실패 시 임시 디렉토리에 로그 작성
                var tempDir = Path.Combine(Path.GetTempPath(), "IronRose", "Logs");
                try { Directory.CreateDirectory(tempDir); } catch { }
                _logPath = Path.Combine(tempDir, _logFileName);
            }
        }
```

---

### D-4. EditorUtils.cs 폰트 경로 수정 (#13)

**파일**: `src/IronRose.Editor/EditorUtils.cs`

**문제**: 라인 28~29에서 `Assets/Fonts/NotoSans_eng.ttf` (프로젝트 에셋)을 CWD 기반으로 참조. EditorUtils는 에디터 유틸리티이므로 `EditorAssets/Fonts/`의 폰트를 사용해야 함. `EditorAssets/Fonts/`에 있는 폰트: `NotoSans.ttf`, `NotoSansKR.ttf`, `Roboto.ttf` 등.

**참고**: `ProjectContext.EditorAssetsPath`는 `internal`이므로 `IronRose.Editor` 프로젝트에서 접근 가능한지 확인 필요. `IronRose.Editor`가 `IronRose.Engine`을 참조하고 있고 `EditorAssetsPath`가 `internal`이면, InternalsVisibleTo 설정이 없는 한 접근 불가.

**현재 코드** (라인 26~31):
```csharp
        public static Font LoadFont(int size = 32)
        {
            var fontPath = System.IO.Path.Combine(
                System.IO.Directory.GetCurrentDirectory(), "Assets", "Fonts", "NotoSans_eng.ttf");
            try { return Font.CreateFromFile(fontPath, size); }
            catch { return Font.CreateDefault(size); }
        }
```

**수정 후 코드** - `ProjectContext.EngineRoot` 기반 (public):
```csharp
        public static Font LoadFont(int size = 32)
        {
            var fontPath = System.IO.Path.Combine(
                IronRose.Engine.ProjectContext.EngineRoot, "EditorAssets", "Fonts", "NotoSans.ttf");
            try { return Font.CreateFromFile(fontPath, size); }
            catch { return Font.CreateDefault(size); }
        }
```

**참고**: `ProjectContext.EngineRoot`는 `public static` 이므로 접근 가능. `NotoSans.ttf`는 `EditorAssets/Fonts/`에 존재 확인됨. `NotoSans_eng.ttf`는 `templates/default/Assets/Fonts/`에만 있었고, 프로젝트 에셋이므로 엔진 코드에서 참조하면 안 됨.

---

### D-5. NativeFileDialog initialDir 기본값 수정 (#14)

**파일**: `src/IronRose.Engine/Editor/ImGui/NativeFileDialog.cs`

**문제**: 라인 157, 174, 188에서 `initialDir ?? Directory.GetCurrentDirectory()` 사용. `ProjectContext.ProjectRoot` 또는 `AssetsPath`가 더 적절한 기본값.

**현재 코드** (라인 155~158):
```csharp
        private static string? LinuxSaveDialog(string title, string defaultName, string filter, string? initialDir)
        {
            var dir = initialDir ?? Directory.GetCurrentDirectory();
```

**현재 코드** (라인 172~175):
```csharp
        private static string? LinuxOpenDialog(string title, string filter, string? initialDir)
        {
            var dir = initialDir ?? Directory.GetCurrentDirectory();
```

**현재 코드** (라인 186~188):
```csharp
        private static string? LinuxPickFolder(string title, string? initialDir)
        {
            var dir = initialDir ?? Directory.GetCurrentDirectory();
```

**수정 후 코드** (세 곳 모두 동일 패턴):
```csharp
            var dir = initialDir
                ?? (ProjectContext.IsProjectLoaded ? ProjectContext.ProjectRoot : Directory.GetCurrentDirectory());
```

**참고**: `ProjectContext`는 `IronRose.Engine` 네임스페이스에 있고, `NativeFileDialog`도 `IronRose.Engine.Editor.ImGuiEditor` 네임스페이스이므로 동일 프로젝트 내 접근 가능.

---

### D-6. 패널별 IsProjectLoaded 가드 추가 (#15)

아래 4개 패널의 `Draw()` 메서드 시작 부분에 `if (!ProjectContext.IsProjectLoaded) return;` 가드 추가.

**파일 1**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectSettingsPanel.cs`

**현재 코드** (라인 34~36):
```csharp
        public void Draw()
        {
            if (!IsOpen) return;
```

**수정 후 코드**:
```csharp
        public void Draw()
        {
            if (!ProjectContext.IsProjectLoaded) return;
            if (!IsOpen) return;
```

**파일 2**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiGameViewPanel.cs`

**현재 코드** (라인 100~104):
```csharp
        public void Draw()
        {
            if (!IsOpen)
            {
                _isImageHovered = false;
                _isWindowFocused = false;
```

**수정 후 코드**:
```csharp
        public void Draw()
        {
            if (!ProjectContext.IsProjectLoaded) return;
            if (!IsOpen)
            {
                _isImageHovered = false;
                _isWindowFocused = false;
```

**파일 3**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiSceneEnvironmentPanel.cs`

**현재 코드** (라인 30~32):
```csharp
        public void Draw()
        {
            if (!IsOpen) return;
```

**수정 후 코드**:
```csharp
        public void Draw()
        {
            if (!ProjectContext.IsProjectLoaded) return;
            if (!IsOpen) return;
```

**파일 4**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiVariantTreePanel.cs`

**현재 코드** (라인 23~25):
```csharp
        public void Draw()
        {
            if (!_isOpen) return;
```

**수정 후 코드**:
```csharp
        public void Draw()
        {
            if (!ProjectContext.IsProjectLoaded) return;
            if (!_isOpen) return;
```

**참고**: `ImGuiConsolePanel`은 프로젝트와 무관하게 로그를 표시하므로 가드 추가하지 않음 (설계 문서 명시).

---

### 검증 기준 (Phase D)
- [ ] `dotnet build` 성공
- [ ] `RoseConfig.Load()`가 `ProjectContext.ProjectRoot` 기반으로 탐색
- [ ] `Debug.Write()`에서 `IOException` 발생 시 호출자에게 예외가 전파되지 않음
- [ ] `Debug` static 생성자 실패 시 `TypeInitializationException` 미발생
- [ ] `EditorUtils.LoadFont()`가 `EditorAssets/Fonts/NotoSans.ttf` 경로 사용
- [ ] NativeFileDialog의 기본 initialDir이 `ProjectContext.ProjectRoot`
- [ ] 4개 패널에 `IsProjectLoaded` 가드 추가됨

---

## Phase 실행 순서 요약

| Phase | 항목 | 우선순위 | 의존 | 수정 파일 수 |
|-------|------|----------|------|-------------|
| A | #1, #2, #3 | 높음 | 없음 | 4 파일 |
| B | #4, #5, #6, #7 | 중간 | Phase A | 4 파일 |
| C | #8, #9 | 중간 | Phase A | 3 파일 |
| D | #10, #11, #12, #13, #14, #15 | 낮음 | Phase A | 8 파일 |

**Phase B, C, D는 서로 독립적**이므로 Phase A 완료 후 병렬 진행 가능.

---

## 제외된 항목 (#16 ~ #20)

| # | 항목 | 제외 이유 |
|---|------|-----------|
| 16 | 설계 문서 미갱신 | 코드 변경 불필요, 문서만 수정 |
| 17 | ProjectContext.Initialize() 재귀 구조 | 현재 안전, 리팩토링 범위 큼 |
| 18 | AssetDatabase 필드 초기화자 | 현재 안전 (IsProjectLoaded 가드 보호), 리팩토링 필요 |
| 19 | ProjectContext CWD 폴백 제거 | 근본적 아키텍처 변경, 별도 Phase 필요 |
| 20 | TomlConfig 래퍼 | 전체 TOML 사용처 리팩토링, 별도 Phase 필요 |

---

## 구현 시 공통 주의사항

1. **UTF-8 BOM**: 모든 C# 파일은 UTF-8 with BOM 인코딩 사용
2. **Path.Combine**: 경로 조합 시 반드시 `Path.Combine()` 사용, 문자열 결합 금지
3. **디버깅 로그**: `Debug.Log` (RoseEngine 네임스페이스) 사용, `File.WriteAllText` 등으로 별도 로그 파일 생성 금지
4. **빌드 확인**: 각 Phase 완료 후 반드시 `dotnet build` 실행하여 빌드 성공 확인
5. **ProjectContext 접근 시점**: `ProjectContext.Initialize()` 이전에는 `ProjectRoot` / `EngineRoot`가 빈 문자열이므로, 접근 시점에 주의
