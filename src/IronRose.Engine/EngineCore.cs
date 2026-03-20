// ------------------------------------------------------------
// @file    EngineCore.cs
// @brief   엔진 메인 오케스트레이터. Initialize/Update/Render/Shutdown 생명주기 관리.
//          입력, 그래픽, 물리, 에셋, 에디터, 스크립팅 서브시스템 초기화 및 매 프레임 업데이트.
// @deps    RoseEngine/Input, RoseEngine/Cursor, RoseEngine/Camera, RoseEngine/Time,
//          RoseEngine/SceneManager, RoseEngine/Debug, RoseEngine/Screen, RoseEngine/Application,
//          IronRose.Rendering/GraphicsManager, IronRose.Rendering/RenderSystem,
//          IronRose.AssetPipeline/AssetDatabase, IronRose.Engine.Editor/EditorPlayMode,
//          IronRose.Engine.Editor.ImGuiEditor/ImGuiOverlay, ShaderRegistry
// @exports
//   class EngineCore
//     Initialize(IWindow): void           — 엔진 초기화
//     Update(double): void                — 매 프레임 업데이트
//     Render(): void                      — 매 프레임 렌더링
//     Shutdown(): void                    — 엔진 종료
//     HeadlessEditor: bool                — Standalone 빌드 여부
//     ScreenCaptureEnabled: bool          — 디버깅 스크린캡처 on/off
//     ShowEditor(): void                  — 에디터 표시
//     RequestQuit(): void                 — 종료 요청
// @note    ProcessEngineKeys()에서 ESC 키로 커서 잠금 임시 해제 (에디터만).
//          UpdateImGuiInputState()에서 Game View 클릭으로 커서 잠금 재진입.
//          InitInput()에서 Cursor.Initialize() 호출.
// ------------------------------------------------------------
using IronRose.API;
using IronRose.AssetPipeline;
using IronRose.Engine.Automation;
using IronRose.Engine.Editor;
using IronRose.Engine.Editor.ImGuiEditor;
using IronRose.Engine.Editor.ImGuiEditor.Panels;
using IronRose.Rendering;
using RoseEngine;
using Silk.NET.Input;
using Silk.NET.Windowing;
using System;
using System.IO;

namespace IronRose.Engine
{
    public class EngineCore
    {
        private GraphicsManager? _graphicsManager;
        private RenderSystem? _renderSystem;
        private IWindow? _window;
        private IInputContext? _inputContext;
        private int _frameCount = 0;
        private AssetDatabase? _assetDatabase;
        private PhysicsManager? _physicsManager;
        private PostProcessManager? _postProcessManager;
        private ImGuiOverlay? _imguiOverlay;

        // 에디터 Game View 렌더 해상도 추적 (변경 시만 RenderSystem 리사이즈)
        private uint _editorRenderW;
        private uint _editorRenderH;
        private bool _wasEditorVisible;

        // Fixed timestep (물리)
        private const float FixedDeltaTime = RoseEngine.PhysicsConstants.DefaultFixedDeltaTime;
        private double _fixedAccumulator = 0;

        // 서브시스템 매니저 (Phase 15 — H-2)
        private LiveCodeManager? _liveCodeManager;
        private AssetWarmupManager? _warmupManager;
        private Editor.ThumbnailGenerator? _thumbnailGenerator;

        // Initialize() 이전에 설정된 콜백을 버퍼링하는 backing field
        private Action? _pendingOnAfterReload;
        private Action? _pendingOnWarmUpComplete;

        // GPU texture compressor (BC7/BC5 via Vulkan compute)
        private GpuTextureCompressor? _gpuCompressor;

        // 자동화 테스트 명령 실행기
        private TestCommandRunner? _testCommandRunner;

        // 디버깅 스크린캡처 (기본 off)
        public bool ScreenCaptureEnabled { get; set; } = false;
        public int FrameCount => _frameCount;

        /// <summary>true이면 에디터(ImGui) 초기화를 완전히 스킵합니다. Standalone 빌드용.</summary>
        public bool HeadlessEditor { get; set; }

        // 핫 리로드 후 씬 복원 콜백
        public Action? OnAfterReload
        {
            get => _liveCodeManager?.OnAfterReload ?? _pendingOnAfterReload;
            set
            {
                if (_liveCodeManager != null)
                    _liveCodeManager.OnAfterReload = value;
                else
                    _pendingOnAfterReload = value;
            }
        }

        // 에셋 캐시 워밍업 완료 후 콜백
        public Action? OnWarmUpComplete
        {
            get => _warmupManager?.OnWarmUpComplete ?? _pendingOnWarmUpComplete;
            set
            {
                if (_warmupManager != null)
                    _warmupManager.OnWarmUpComplete = value;
                else
                    _pendingOnWarmUpComplete = value;
            }
        }

        // LiveCode에서 발견된 MonoBehaviour 데모 타입 목록 (DemoLauncher에서 참조)
        public static Type[] LiveCodeDemoTypes
        {
            get => _staticLiveCodeManager?.LiveCodeDemoTypes ?? Array.Empty<Type>();
            private set { } // kept for API compat
        }
        private static LiveCodeManager? _staticLiveCodeManager;

        public void Initialize(IWindow window)
        {
            RoseEngine.Debug.LogSink = entry => EditorBridge.PushLog(entry);
            RoseEngine.Debug.Log("[Engine] EngineCore initializing...");

            ProjectContext.Initialize();

            RoseConfig.Load();
            ProjectSettings.Load();
            _window = window;
            SetWindowIcon(_window);

            InitApplication();
            InitInput();
            InitGraphics();
            InitShaderCache();
            ShaderRegistry.Initialize();
            InitRenderSystem();
            InitScreen();
            InitPluginApi();
            InitPhysics();
            InitAssets();
            InitLiveCode();
            InitGpuCompressor();
            if (!HeadlessEditor)
                InitEditor();

            EditorBridge.IsEditorConnected = !HeadlessEditor;

            // Play/Stop 시 _fixedAccumulator 리셋 콜백 등록
            Editor.EditorPlayMode.OnResetFixedAccumulator = () => _fixedAccumulator = 0;

            // 자동화 테스트 명령 파일 로드
            _testCommandRunner = TestCommandRunner.TryLoad();

            // 에셋 캐시 워밍업 시작
            _warmupManager!.Start();
        }

        public void Update(double deltaTime)
        {
            EditorBridge.ProcessCommands();
            Input.Update();
            RoseEngine.InputSystem.InputSystem.Update();

            // 자동화 테스트 명령 실행
            _testCommandRunner?.Update(deltaTime, _graphicsManager);

            // 에셋 캐시 워밍업 중
            if (_warmupManager is { IsWarmingUp: true })
            {
                _warmupManager.ProcessFrame();
                // 워밍업 진행 모달 표시 (에디터 패널 대신)
                if (_imguiOverlay != null)
                    _imguiOverlay.UpdateWarmup((float)deltaTime,
                        _warmupManager.CurrentIndex, _warmupManager.TotalCount,
                        _warmupManager.CurrentAssetName, _warmupManager.ElapsedSeconds);
                return;
            }

            // 썸네일 생성 요청 소비
            var thumbFolder = _imguiOverlay?.ConsumePendingThumbnailFolder();
            if (thumbFolder != null && _graphicsManager?.Device != null)
            {
                _thumbnailGenerator ??= new Editor.ThumbnailGenerator();
                _thumbnailGenerator.Start(_graphicsManager.Device, thumbFolder, recursive: true);
            }

            // 썸네일 생성 진행 중 (Load()가 reimport를 트리거할 수 있으므로 reimport보다 먼저 체크)
            if (_thumbnailGenerator is { IsGenerating: true })
            {
                // 썸네일 생성 중 발생한 reimport는 조용히 소화 (모달 표시 안 함)
                if (_assetDatabase is { IsReimporting: true })
                    _assetDatabase.ProcessReimport();

                _thumbnailGenerator.ProcessFrame();
                if (_imguiOverlay != null)
                    _imguiOverlay.UpdateWarmup((float)deltaTime,
                        _thumbnailGenerator.CurrentIndex, _thumbnailGenerator.TotalCount,
                        _thumbnailGenerator.CurrentAssetName, _thumbnailGenerator.ElapsedSeconds,
                        "Generating Thumbnails...");
                return;
            }

            // 비동기 재임포트 진행 중
            if (_assetDatabase is { IsReimporting: true })
            {
                if (_assetDatabase.ProcessReimport())
                {
                    // 완료 — 큐에 쌓인 pending reimport 처리
                    _assetDatabase.ProcessPendingReimports();
                }
                else
                {
                    // 진행 중 — 오버레이 표시
                    if (_imguiOverlay != null)
                        _imguiOverlay.UpdateWarmup((float)deltaTime,
                            0, 1, _assetDatabase.ReimportAssetName,
                            _assetDatabase.ReimportElapsed, "Reimporting...");
                    return;
                }
            }

            ProcessEngineKeys();

            // 에셋 파일 변경 감지 처리 (Play 상태와 무관)
            _assetDatabase?.ProcessFileChanges();

            // 핫 리로드 요청 처리 (Play 상태와 무관 — 에디터에서도 타입 등록 필요)
            _liveCodeManager?.ProcessReload();

            // 게임 로직은 Playing 상태에서만 실행
            bool shouldRunGameLogic = IronRose.Engine.Editor.EditorPlayMode.State == IronRose.Engine.Editor.PlayModeState.Playing;

            if (shouldRunGameLogic)
            {

                // Fixed timestep 물리 루프
                _fixedAccumulator += deltaTime;
                while (_fixedAccumulator >= FixedDeltaTime)
                {
                    Time.fixedDeltaTime = FixedDeltaTime;
                    Time.fixedTime += FixedDeltaTime;
                    _physicsManager?.FixedUpdate(FixedDeltaTime);
                    SceneManager.FixedUpdate(FixedDeltaTime);
                    _fixedAccumulator -= FixedDeltaTime;
                }

                _liveCodeManager?.UpdateScripts();
                SceneManager.Update((float)deltaTime);

                // PostProcess Volume 블렌딩 (카메라 위치 기반)
                var mainCam = Camera.main;
                _postProcessManager?.Update(mainCam?.transform.position ?? Vector3.zero);
            }

            // ImGui는 항상 업데이트 (Play 상태와 무관하게 UI 상호작용 가능)
            // NewFrame()이 먼저 호출되어야 UI 레이아웃에서 ImGui.GetFont() 사용 가능
            if (_imguiOverlay != null)
                _imguiOverlay.Update((float)deltaTime);

            UpdateImGuiInputState();
        }

        public void Render()
        {
            if (_graphicsManager == null) return;

            // Skip rendering when window is minimized or has zero size
            if (_window != null && (_window.WindowState == Silk.NET.Windowing.WindowState.Minimized
                || _window.Size.X <= 0 || _window.Size.Y <= 0))
                return;

            // Warmup / Reimport / Thumbnail 중: game/scene 렌더링 skip, ImGui만 렌더
            if (_warmupManager is { IsWarmingUp: true } || _assetDatabase is { IsReimporting: true }
                || _thumbnailGenerator is { IsGenerating: true })
            {
                _graphicsManager.BeginFrame();
                if (_imguiOverlay != null && _graphicsManager.CommandList != null)
                    _imguiOverlay.Render(_graphicsManager.CommandList);
                _graphicsManager.EndFrame();
                _imguiOverlay?.RenderSecondaryViewports();
                return;
            }

            _frameCount++;
            if (ScreenCaptureEnabled && (_frameCount == 1 || _frameCount == 60 || _frameCount % 300 == 0))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var filename = Path.Combine("logs", $"screenshot_frame{_frameCount}_{timestamp}.png");
                _graphicsManager.RequestScreenshot(filename);
            }

            bool editorVisible = _imguiOverlay is { IsVisible: true };
            _graphicsManager.BeginFrame();

            // Flush deferred resource disposals from the PREVIOUS frame.
            // Must happen after BeginFrame (previous commands submitted) and before any Render/Resize.
            _renderSystem?.BeginFrame();

            if (_renderSystem != null && _graphicsManager.CommandList != null)
            {
                if (_wasEditorVisible && !editorVisible)
                {
                    var swapW = _graphicsManager.Device!.SwapchainFramebuffer.Width;
                    var swapH = _graphicsManager.Device!.SwapchainFramebuffer.Height;
                    _renderSystem.Resize(swapW, swapH);
                    DebugOverlaySettings.wireframe = false;
                    _editorRenderW = 0;
                    _editorRenderH = 0;
                }
                _wasEditorVisible = editorVisible;

                float aspectRatio = _graphicsManager.AspectRatio;

                if (editorVisible)
                {
                    _imguiOverlay!.EnsureOffscreenRT();
                    var fb = _imguiOverlay.GameViewFramebuffer;

                    if (fb != null)
                    {
                        uint rtW = fb.Width;
                        uint rtH = fb.Height;

                        if (_editorRenderW != rtW || _editorRenderH != rtH)
                        {
                            _renderSystem.Resize(rtW, rtH);
                            _editorRenderW = rtW;
                            _editorRenderH = rtH;
                        }

                        var cl = _graphicsManager.CommandList!;
                        cl.SetFramebuffer(fb);
                        cl.ClearColorTarget(0, new Veldrid.RgbaFloat(0, 0, 0, 0));
                        cl.ClearDepthStencil(1f);

                        _renderSystem.OverrideOutputFramebuffer = fb;
                        aspectRatio = (float)rtW / rtH;
                    }

                    DebugOverlaySettings.wireframe = _imguiOverlay.IsWireframe;
                }

                // MipMesh LOD 선택 (렌더 직전에 수행)
                RoseEngine.MipMeshSystem.UpdateAllLods();

                _renderSystem.Render(
                    _graphicsManager.CommandList,
                    Camera.main,
                    aspectRatio,
                    _renderSystem.GameViewContext!);

                _renderSystem.OverrideOutputFramebuffer = null;

                // Scene View rendering
                if (editorVisible)
                {
                    // Scene View용 MipMesh LOD: 에디터 카메라 기준
                    var svProxy = _imguiOverlay!.CameraProxy;
                    var svEdCam = _imguiOverlay.EditorCameraInstance;
                    var svRenderer = _imguiOverlay.SceneViewRendererInstance;
                    if (svProxy != null && svEdCam != null)
                    {
                        svProxy.Sync(svEdCam);
                        float svScreenH = svRenderer?.Framebuffer?.Height ?? 720f;
                        RoseEngine.MipMeshSystem.UpdateLodsForSceneView(svProxy.Camera, svScreenH);
                    }

                    if (_imguiOverlay.SceneViewRenderMode == SceneViewRenderMode.Rendered)
                    {
                        // Rendered mode: use RenderSystem with separate Scene View context
                        var svFb = _imguiOverlay.SceneViewFramebuffer;
                        if (svFb != null && svProxy != null && svEdCam != null)
                        {
                            try
                            {
                                uint svW = svFb.Width;
                                uint svH = svFb.Height;

                                var sceneCtx = _renderSystem.CreateSceneViewContext(svW, svH);
                                _renderSystem.ResizeContext(sceneCtx, svW, svH);

                                var cl = _graphicsManager.CommandList!;
                                cl.SetFramebuffer(svFb);
                                cl.ClearColorTarget(0, new Veldrid.RgbaFloat(0, 0, 0, 0));
                                cl.ClearDepthStencil(1f);

                                // Material drag-hover override 동기화
                                if (svRenderer != null && svRenderer.MaterialOverrideObjectId != 0)
                                    _renderSystem.SetMaterialOverride(svRenderer.MaterialOverrideObjectId,
                                        svRenderer.MaterialOverrideRef);
                                else
                                    _renderSystem.ClearMaterialOverride();

                                // Scene View 카메라 위치 기준으로 PostProcess Volume 재평가 (Scene View 스택에 적용)
                                _postProcessManager?.Update(svEdCam.Position, sceneCtx.PostProcessStack);

                                _renderSystem.OverrideOutputFramebuffer = svFb;
                                _renderSystem.Render(cl, svProxy.Camera, (float)svW / svH, sceneCtx);
                                _renderSystem.OverrideOutputFramebuffer = null;
                                _renderSystem.ClearMaterialOverride();

                                // Game View 카메라 기준으로 PostProcess 상태 복원 (Game View 스택에 적용)
                                var mainCamRestore = Camera.main;
                                _postProcessManager?.Update(mainCamRestore?.transform.position ?? Vector3.zero, RoseEngine.RenderSettings.postProcessing);
                            }
                            catch (System.Exception ex)
                            {
                                RoseEngine.Debug.LogError($"[Rendered] EXCEPTION: {ex}");
                                _renderSystem.OverrideOutputFramebuffer = null;
                            }
                        }
                    }

                    _imguiOverlay.RenderSceneView(_graphicsManager.CommandList);

                    // Scene View 렌더 후 Game View LOD 복원 (다음 프레임 물리/로직용)
                    RoseEngine.MipMeshSystem.UpdateAllLods();

                    // Inspector: 활성 뷰 기준으로 currentLod 표시
                    bool sceneViewActive = _imguiOverlay.SceneViewPanel?.IsWindowFocused ?? false;
                    RoseEngine.MipMeshSystem.SetActiveViewForInspector(sceneViewActive);
                }
            }

            if (_imguiOverlay != null && _graphicsManager.CommandList != null)
                _imguiOverlay.Render(_graphicsManager.CommandList);

            // Debug overlay → Swapchain (ImGui 위에 직접 렌더)
            if (_renderSystem != null && _graphicsManager.CommandList != null)
                _renderSystem.RenderDebugOverlayToSwapchain(_graphicsManager.CommandList);

            _graphicsManager.EndFrame();

            // Multi-Viewport: 메인 CL submit 후 보조 뷰포트 렌더 (공유 버퍼 충돌 방지)
            _imguiOverlay?.RenderSecondaryViewports();
        }

        public void Shutdown()
        {
            RoseEngine.Debug.Log("[Engine] EngineCore shutting down...");
            Application.isPlaying = false;
            Application.QuitAction = null;
            SceneManager.Clear();
            _assetDatabase?.StopWatching();
            _assetDatabase?.UnloadAll();
            RoseCache.SetGpuCompressor(null);
            _gpuCompressor?.Dispose();
            _gpuCompressor = null;
            _postProcessManager?.Dispose();
            _physicsManager?.Dispose();
            _liveCodeManager?.Dispose();
            _thumbnailGenerator?.Dispose();
            _imguiOverlay?.Dispose();
            _renderSystem?.Dispose();
            _graphicsManager?.Dispose();
        }

        /// <summary>에디터 오버레이를 표시합니다. 이미 표시 중이면 무시합니다.</summary>
        public void ShowEditor()
        {
            if (_imguiOverlay != null && !_imguiOverlay.IsVisible)
                _imguiOverlay.Toggle();
        }

        /// <summary>윈도우 타이틀을 변경합니다.</summary>
        public void SetWindowTitle(string title)
        {
            if (_window != null)
                _window.Title = title;
        }

        /// <summary>종료 확인 다이얼로그를 통해 종료 요청.</summary>
        public void RequestQuit() => _imguiOverlay?.RequestQuit();

        /// <summary>사용자가 종료를 확인했는지 여부.</summary>
        public bool IsQuitConfirmed => _imguiOverlay?.IsQuitConfirmed ?? false;

        // ================================================================
        // Initialization helpers (Phase 15 — H-2)
        // ================================================================

        private void InitApplication()
        {
            Application.isPlaying = false;
            Application.isPaused = false;
            Application.QuitAction = () => _window!.Close();
            Application.PauseCallback = IronRose.Engine.Editor.EditorPlayMode.PausePlayMode;
            Application.ResumeCallback = IronRose.Engine.Editor.EditorPlayMode.ResumePlayMode;
        }

        private void InitInput()
        {
            _inputContext = _window!.CreateInput();
            Input.Initialize(_inputContext);

            // Cursor API 초기화: 첫 번째 마우스 디바이스 참조 전달
            if (_inputContext.Mice.Count > 0)
                RoseEngine.Cursor.Initialize(_inputContext.Mice[0]);
        }

        private void InitGraphics()
        {
            _graphicsManager = new GraphicsManager();
            RoseEngine.Debug.Log($"[Engine] Passing window to GraphicsManager: {_window!.GetType().Name}");
            _graphicsManager.Initialize(_window);
            RoseEngine.Debug.Log("[Engine] GraphicsManager initialized");
        }

        private void InitShaderCache()
        {
            if (!RoseConfig.DontUseCache)
            {
                var shaderCacheDir = Path.Combine(ProjectContext.CachePath, "shaders");
                ShaderCompiler.SetCacheDirectory(shaderCacheDir);

                if (RoseConfig.ForceClearCache)
                    ShaderCompiler.ClearCache();
            }
        }

        private void InitRenderSystem()
        {
            if (_graphicsManager!.Device == null) return;

            try
            {
                _renderSystem = new RenderSystem();
                _renderSystem.Initialize(_graphicsManager.Device);
                RoseEngine.Debug.Log("[Engine] RenderSystem initialized");
                RoseEngine.RenderSettings.postProcessing = _renderSystem.PostProcessing;

                _graphicsManager.Resized += (w, h) =>
                {
                    if (_imguiOverlay is { IsVisible: true }
                        && _imguiOverlay.SelectedResolution != GameViewResolution.Native)
                        return;
                    _renderSystem?.Resize(w, h);
                };
            }
            catch (Exception ex)
            {
                RoseEngine.Debug.LogWarning($"[Engine] RenderSystem init failed: {ex.Message}");
                RoseEngine.Debug.Log("[Engine] Falling back to clear-only rendering");
                _renderSystem = null;
            }
        }

        private void InitScreen()
        {
            RoseEngine.Screen.SetSize(_window!.Size.X, _window.Size.Y);
            _window.Resize += size =>
            {
                if (size.X > 0 && size.Y > 0)
                    RoseEngine.Screen.SetSize(size.X, size.Y);
            };
        }

        private void InitPluginApi()
        {
            IronRose.API.Screen.SetClearColorImpl = (r, g, b) => _graphicsManager!.SetClearColor(r, g, b);
        }

        private void InitPhysics()
        {
            _physicsManager = new PhysicsManager();
            _physicsManager.Initialize();

            _postProcessManager = new PostProcessManager();
            _postProcessManager.Initialize();
        }

        private void InitAssets()
        {
            _assetDatabase = new AssetDatabase();
            string assetsPath = ProjectContext.AssetsPath;
            if (Directory.Exists(assetsPath))
                _assetDatabase.ScanAssets(assetsPath);
            RoseEngine.Resources.SetAssetDatabase(_assetDatabase);

            _warmupManager = new AssetWarmupManager(_assetDatabase);
            if (_pendingOnWarmUpComplete != null)
            {
                _warmupManager.OnWarmUpComplete = _pendingOnWarmUpComplete;
                _pendingOnWarmUpComplete = null;
            }

            EnsureDefaultRendererProfile();
        }

        private void EnsureDefaultRendererProfile()
        {
            var settingsDir = Path.Combine(ProjectContext.AssetsPath, "Settings");
            var defaultPath = Path.Combine(settingsDir, "Default.renderer");
            if (!File.Exists(defaultPath))
            {
                Directory.CreateDirectory(settingsDir);
                RendererProfileImporter.WriteDefault(defaultPath);
                RoseMetadata.LoadOrCreate(defaultPath);
                Debug.Log("[Engine] Created default renderer profile: Assets/Settings/Default.renderer");
                // 새로 생성한 파일을 AssetDatabase에 등록
                _assetDatabase?.ScanAssets(ProjectContext.AssetsPath);
            }

            // ProjectSettings에 저장된 활성 프로파일 GUID 로드, 없으면 Default
            RendererProfile? profile = null;
            var savedGuid = ProjectSettings.ActiveRendererProfileGuid;
            if (!string.IsNullOrEmpty(savedGuid))
                profile = _assetDatabase?.LoadByGuid<RendererProfile>(savedGuid);

            if (profile == null)
                profile = _assetDatabase?.Load<RendererProfile>(
                    Path.Combine(ProjectContext.AssetsPath, "Settings", "Default.renderer"));

            if (profile != null)
            {
                // 실제 로드된 프로파일의 GUID를 사용 (savedGuid 또는 Default 경로에서 조회)
                var activeGuid = savedGuid;
                if (string.IsNullOrEmpty(activeGuid))
                    activeGuid = _assetDatabase?.GetGuidFromPath(Path.Combine(ProjectContext.AssetsPath, "Settings", "Default.renderer"));
                RenderSettings.activeRendererProfile = profile;
                RenderSettings.activeRendererProfileGuid = activeGuid;
                profile.ApplyToRenderSettings();
                Debug.Log($"[Engine] Loaded renderer profile: {profile.name}");
            }
        }

        private void InitLiveCode()
        {
            _liveCodeManager = new LiveCodeManager();
            _liveCodeManager.Initialize();
            _staticLiveCodeManager = _liveCodeManager;
            if (_pendingOnAfterReload != null)
            {
                _liveCodeManager.OnAfterReload = _pendingOnAfterReload;
                _pendingOnAfterReload = null;
            }
        }

        private void InitGpuCompressor()
        {
            if (_graphicsManager!.Device == null) return;

            try
            {
                _gpuCompressor = new GpuTextureCompressor(_graphicsManager.Device);
                _gpuCompressor.Initialize(ShaderRegistry.ShaderRoot);
                RoseCache.SetGpuCompressor(_gpuCompressor);
            }
            catch (Exception ex)
            {
                RoseEngine.Debug.LogError($"[Engine] GPU compressor init failed, using CPU fallback: {ex.Message}");
                _gpuCompressor = null;
            }
        }

        private void InitEditor()
        {
            if (_graphicsManager!.Device == null || _inputContext == null) return;

            try
            {
                _imguiOverlay = new ImGuiOverlay();
                _imguiOverlay.Initialize(
                    _graphicsManager.Device,
                    _window!,
                    _inputContext,
                    ShaderRegistry.ShaderRoot);
                _graphicsManager.Resized += (w, h) => _imguiOverlay?.Resize(w, h);
                RoseEngine.Debug.Log("[Engine] ImGui overlay initialized");
            }
            catch (Exception ex)
            {
                RoseEngine.Debug.LogError($"[Engine] ImGui overlay init failed: {ex.Message}");
                _imguiOverlay = null;
            }
        }

        // ================================================================
        // Engine keys & ImGui state
        // ================================================================

        private void ProcessEngineKeys()
        {
            // Play/Pause/Stop 단축키는 ImGuiOverlay.ProcessSceneShortcuts()에서 처리

            // ESC: 커서 잠금 임시 해제 (에디터 Play 모드에서 Locked 상태일 때만)
            // Standalone 빌드에서는 ESC로 커서 해제하지 않음 — 게임 스크립트가 직접 제어
            if (!HeadlessEditor && Input.GetKeyDownRaw(KeyCode.Escape) && RoseEngine.Cursor.IsEffectivelyLocked)
            {
                RoseEngine.Cursor.EscapeRelease();
                Input.SkipNextDelta();
            }

            if (Input.GetKeyDownRaw(KeyCode.F11))
                _imguiOverlay?.Toggle();

            if (Input.GetKeyDownRaw(KeyCode.F12) && _graphicsManager != null)
            {
                var dir = Path.Combine("Screenshots");
                Directory.CreateDirectory(dir);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var filename = Path.Combine(dir, $"screenshot_{timestamp}.png");
                _graphicsManager.RequestScreenshot(filename);
            }
        }

        private void UpdateImGuiInputState()
        {
            if (_imguiOverlay != null)
            {
                Input.ImGuiWantsKeyboard = _imguiOverlay.WantCaptureKeyboard;
                Input.ImGuiWantsMouse = _imguiOverlay.WantCaptureMouse;

                if (_imguiOverlay.IsVisible && _imguiOverlay.IsGameViewImageHovered)
                {
                    var min = _imguiOverlay.GameViewImageScreenMin;
                    var max = _imguiOverlay.GameViewImageScreenMax;
                    var fb = _imguiOverlay.GameViewFramebuffer;
                    Input.GameViewActive = true;
                    Input.GameViewMinX = min.X;
                    Input.GameViewMinY = min.Y;
                    Input.GameViewMaxX = max.X;
                    Input.GameViewMaxY = max.Y;
                    Input.GameViewRenderW = fb?.Width ?? 0;
                    Input.GameViewRenderH = fb?.Height ?? 0;
                }
                else
                {
                    Input.GameViewActive = false;
                }
            }
            else
            {
                Input.GameViewActive = false;
            }

            // Game View 클릭으로 커서 잠금 재진입
            // Raw 사용: ESC override 후 커서가 Normal이 되면 ImGui가 마우스를 캡처하여
            // GetMouseButtonDown이 false를 반환하므로, ImGui 차단을 무시해야 함
            if (RoseEngine.Cursor.isEscapeOverridden
                && Editor.EditorPlayMode.State == Editor.PlayModeState.Playing
                && Input.GetMouseButtonDownRaw(0))
            {
                bool clickedGameView = false;

                if (_imguiOverlay != null && _imguiOverlay.IsVisible)
                {
                    // 에디터: Game View 이미지 영역 클릭 시
                    clickedGameView = _imguiOverlay.IsGameViewImageHovered;
                }
                else
                {
                    // Standalone 또는 에디터 숨김: 윈도우 어디든 클릭 시
                    clickedGameView = true;
                }

                if (clickedGameView)
                {
                    RoseEngine.Cursor.ReacquireLock();
                    Input.SkipNextDelta();
                }
            }
        }

        // ================================================================
        // Utilities
        // ================================================================

        private static void SetWindowIcon(IWindow window)
        {
            try
            {
                string[] searchPaths = { "iron_rose.png", "../iron_rose.png", "../../iron_rose.png" };
                string? iconPath = null;
                foreach (var p in searchPaths)
                {
                    var full = Path.GetFullPath(p);
                    if (File.Exists(full)) { iconPath = full; break; }
                }
                if (iconPath == null) return;

                using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(iconPath);
                var pixels = new byte[image.Width * image.Height * 4];
                image.CopyPixelDataTo(pixels);

                var rawImage = new Silk.NET.Core.RawImage(image.Width, image.Height, pixels);
                window.SetWindowIcon(ref rawImage);
                RoseEngine.Debug.Log($"[Engine] Window icon set: {iconPath}");
            }
            catch (Exception ex)
            {
                RoseEngine.Debug.LogError($"[Engine] Window icon failed: {ex.Message}");
            }
        }

    }
}
