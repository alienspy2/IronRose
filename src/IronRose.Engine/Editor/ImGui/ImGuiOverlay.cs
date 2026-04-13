// ------------------------------------------------------------
// @file    ImGuiOverlay.cs
// @brief   ImGui 오버레이 최상위 컨트롤러. 패널 라이프사이클, 컨텍스트/입력/렌더링 담당.
//          RT 관리는 ImGuiRenderTargetManager, 레이아웃은 ImGuiLayoutManager에 위임.
// @deps    IronRose.Engine/EngineCore, IronRose.Engine/ProjectContext,
//          IronRose.Rendering/GraphicsManager, IronRose.Rendering/VeldridImGuiRenderer,
//          IronRose.Engine.Editor.ImGuiEditor.Panels/*, IronRose.Engine.Editor.SceneView/*
// @exports
//   class ImGuiOverlay : IDisposable
//     Initialize(IWindow, GraphicsDevice, ...): void — ImGui 컨텍스트 및 렌더러 초기화
//     Update(double, IWindow): void                  — 입력 상태 업데이트
//     Render(CommandList): void                      — ImGui 프레임 렌더
//     Toggle(): void                                 — 오버레이 표시/숨김 토글
//     IsVisible: bool                                — 오버레이 표시 상태
//     ImGuiRenderer: VeldridImGuiRenderer?            — 내부 렌더러 접근
// @note    ProbeGlyphRanges()는 폰트 글리프 범위를 디스크 캐시(FontGlyphCache/)에 저장하여
//          두 번째 실행부터 CPU-집약적 프로브를 건너뜀.
//          Initialize 폰트 루프에서 EngineCore.PumpWindowEvents()를 호출하여 OS "응답 없음" 방지.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ImGuiNET;
using SixLabors.Fonts;
using IronRose.Engine;
using IronRose.Engine.Editor.ImGuiEditor.Panels;
using IronRose.Engine.Editor.SceneView;
using IronRose.Rendering;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Veldrid;
using Debug = RoseEngine.EditorDebug;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// Top-level ImGui overlay controller.
    /// 패널 라이프사이클, 컨텍스트/입력/렌더링만 담당.
    /// RT 관리는 ImGuiRenderTargetManager, 레이아웃은 ImGuiLayoutManager에 위임.
    /// </summary>
    public sealed class ImGuiOverlay : IDisposable
    {
        private IntPtr _context;
        private VeldridImGuiRenderer? _renderer;
        public VeldridImGuiRenderer? ImGuiRenderer => _renderer;
        private ImGuiInputHandler? _inputHandler;
        private ImGuiPlatformBackend? _platformBackend;
        private ImGuiRendererBackend? _rendererBackend;

        private IWindow _window = null!;
        private GraphicsDevice _device = null!;

        // Panels
        private ImGuiHierarchyPanel? _hierarchy;
        private ImGuiInspectorPanel? _inspector;
        private ImGuiProjectSettingsPanel? _projectSettings;
        private ImGuiSceneEnvironmentPanel? _sceneEnvironment;
        private ImGuiConsolePanel? _console;
        private ImGuiGameViewPanel? _gameView;
        private ImGuiProjectPanel? _project;
        private ImGuiSceneViewPanel? _sceneView;
        private ImGuiTextureToolPanel? _textureTool;
        private ImGuiSpriteEditorPanel? _spriteEditor;
        private ImGuiAnimationEditorPanel? _animEditor;
        private ImGuiScriptsPanel? _scripts;
        private ImGuiFeedbackPanel? _feedback;
        private ImGuiStartupPanel? _startupPanel;
        private ImGuiPreferencesPanel? _preferencesPanel;

        // Property windows (고정 Inspector)
        private readonly List<ImGuiPropertyWindow> _propertyWindows = new();

        // Sub-managers (Phase 15 — M-4)
        private ImGuiRenderTargetManager? _rtManager;
        private SceneViewRenderTargetManager? _sceneRtManager;
        private readonly ImGuiLayoutManager _layoutManager = new();

        // Scene View
        private EditorCamera? _editorCamera;
        private SceneViewRenderer? _sceneRenderer;
        private SceneViewCameraProxy? _camProxy;
        private TransformGizmo? _gizmo;
        private ColliderGizmoEditor? _colliderEditor;
        private RectGizmoEditor? _rectGizmoEditor;
        private UITransformGizmo2D? _uiTransformGizmo;
        private RectSelectionTool? _rectSelection;
        private GizmoRenderer? _gizmoRenderer;

        // MMB click-to-focus tracking
        private Vector2 _mmbDownPos;
        private bool _mmbTracking;
        private bool _mmbIsDragging;

        // Camera-active lock: once camera movement starts, hold until button released
        // Prevents gizmo hover from stealing input and allows movement outside panel bounds
        private bool _cameraActive;

        // Material drag-hover preview
        private int _materialHoverObjectId;
        private RoseEngine.Material? _materialHoverPreview;
        private string? _lastMaterialHoverPath;

        // About popup
        private IntPtr _aboutTextureId;
        private Texture? _aboutTexture;
        private TextureView? _aboutTextureView;
        private uint _aboutImageW;
        private uint _aboutImageH;
        private bool _showAbout;

        // Quit confirmation
        private bool _showQuitConfirm;
        public bool IsQuitConfirmed { get; private set; }

        // Reimport All confirmation
        private bool _showReimportConfirm;
        private bool _showReimportNotice;

        // Open Scene from Project panel (double-click)
        private bool _showOpenSceneConfirm;
        private string? _pendingOpenScenePath;

        // Prefab Edit Mode — Back 확인
        private bool _showPrefabBackConfirm;
        private int _pendingPrefabBackCount;

        private bool _titleNeedsUpdate = true;
        private bool _lastDirtyState;

        // System clipboard bridge (delegates kept alive to prevent GC)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private unsafe delegate byte* ImGuiGetClipboardFn(void* userData);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private unsafe delegate void ImGuiSetClipboardFn(void* userData, byte* text);
        private static ImGuiGetClipboardFn? _getClipboardDelegate;
        private static ImGuiSetClipboardFn? _setClipboardDelegate;
        private static nint _clipboardReturnBuffer;

        // UI Scale
        private float _uiScale = 1.0f;
        private const float MinUiScale = 0.5f;
        private const float MaxUiScale = 3.0f;
        private const float UiScaleStep = 0.1f;

        // Font selection
        private const float DefaultFontSize = 15.0f;
        private static readonly string[] FontNames = { "Roboto", "ArchivoBlack", "NotoSans", "NotoSansKR" };
        private static readonly string[] FontFiles = { "Roboto.ttf", "ArchivoBlack.ttf", "NotoSans.ttf", "NotoSansKR.ttf" };

        // Fallback 체인 (순서대로 시도, EditorAssets/Fonts/ 내 파일명 + 사이즈 스케일)
        private static readonly (string file, float sizeScale)[] FallbackFontFiles = {
            ("NotoSans.ttf",         1.0f),
            ("NotoSansSymbols.ttf",  1.3f),
            ("NotoSansSymbols2.ttf", 1.3f),
            ("NotoSansKR.ttf",       1.0f),
        };

        /// <summary>폰트 글리프 범위: Basic Latin + General Punctuation (em dash 등).</summary>
        private static readonly ushort[] _fontGlyphRanges = {
            0x0020, 0x00FF,   // Basic Latin + Latin Supplement
            0x2010, 0x2027,   // General Punctuation (en dash, em dash, quotes, bullet, ellipsis)
            0,                // terminator
        };
        private static GCHandle _fontGlyphRangesHandle;

        /// <summary>프로브 대상 유니코드 블록 (시작, 끝, 대표 코드포인트).</summary>
        private static readonly (ushort start, ushort end, char probe)[] _unicodeBlocks = {
            (0x0020, 0x00FF, 'A'),      // Basic Latin + Latin Supplement
            (0x0100, 0x024F, '\u0100'), // Latin Extended-A/B
            (0x2010, 0x2027, '\u2014'), // General Punctuation (em dash)
            (0x2190, 0x21FF, '\u2192'), // Arrows (→)
            (0x2300, 0x23FF, '\u2302'), // Misc Technical
            (0x2500, 0x257F, '\u2500'), // Box Drawing (─)
            (0x25A0, 0x25FF, '\u25C9'), // Geometric Shapes (◉)
            (0x2600, 0x26FF, '\u2602'), // Misc Symbols
            (0x2700, 0x27BF, '\u2714'), // Dingbats
            (0x2B00, 0x2BFF, '\u2B1C'), // Misc Symbols & Arrows (⬜)
            (0x3000, 0x303F, '\u3001'), // CJK Symbols & Punctuation
            (0x3130, 0x318F, '\u3131'), // Hangul Compatibility Jamo
            (0xAC00, 0xD7AF, '\uAC00'), // Hangul Syllables
        };

        // 동적으로 생성된 fallback 글리프 범위 (GCHandle로 pinned)
        private readonly List<GCHandle> _fallbackGlyphRangeHandles = new();

        private string _currentFont = "Roboto";
        private ImFontPtr[] _loadedFonts = Array.Empty<ImFontPtr>();

        public bool IsVisible { get; private set; }

        /// <summary>
        /// The offscreen framebuffer that the scene should render to when the editor is visible.
        /// null when the editor is hidden (render to swapchain normally).
        /// </summary>
        public Framebuffer? GameViewFramebuffer => IsVisible ? _rtManager?.Framebuffer : null;

        /// <summary>Game View의 선택 해상도.</summary>
        public GameViewResolution SelectedResolution => _gameView?.SelectedResolution ?? GameViewResolution.Native;

        /// <summary>Game View의 와이어프레임 상태.</summary>
        public bool IsWireframe => _gameView?.IsWireframe ?? false;

        /// <summary>Project 패널에서 요청된 썸네일 생성 대상 폴더 소비. EngineCore에서 호출.</summary>
        public string? ConsumePendingThumbnailFolder() => _project?.ConsumePendingThumbnailFolder();

        /// <summary>True if ImGui wants to consume mouse input this frame (Game View 이미지 위일 때 제외).</summary>
        public bool WantCaptureMouse => IsVisible && (_inputHandler?.WantCaptureMouse ?? false) && !IsGameViewImageHovered;
        /// <summary>True if ImGui wants to consume keyboard input this frame (Game View 포커스 시 제외).</summary>
        public bool WantCaptureKeyboard => IsVisible && (_inputHandler?.WantCaptureKeyboard ?? false) && !(_gameView?.IsWindowFocused ?? false);

        /// <summary>Game View 이미지 위에 마우스가 있는지.</summary>
        public bool IsGameViewImageHovered => _gameView?.IsImageHovered ?? false;

        /// <summary>Game View 이미지 영역의 스크린 좌표 (좌상단).</summary>
        public Vector2 GameViewImageScreenMin => _gameView?.ImageScreenMin ?? Vector2.Zero;

        /// <summary>Game View 이미지 영역의 스크린 좌표 (우하단).</summary>
        public Vector2 GameViewImageScreenMax => _gameView?.ImageScreenMax ?? Vector2.Zero;

        /// <summary>Scene View 렌더 모드.</summary>
        public SceneViewRenderMode SceneViewRenderMode => _sceneView?.SelectedRenderMode ?? SceneViewRenderMode.MatCap;

        /// <summary>Scene View 렌더러.</summary>
        public SceneViewRenderer? SceneViewRendererInstance => _sceneRenderer;

        /// <summary>Scene View 에디터 카메라.</summary>
        public EditorCamera? EditorCameraInstance => _editorCamera;

        /// <summary>Scene View 카메라 프록시 (WYSIWYG).</summary>
        public SceneViewCameraProxy? CameraProxy => _camProxy;

        /// <summary>Scene View 패널.</summary>
        public ImGuiSceneViewPanel? SceneViewPanel => _sceneView;

        /// <summary>Sprite Editor 패널.</summary>
        public ImGuiSpriteEditorPanel? SpriteEditorPanel => _spriteEditor;

        /// <summary>Animation Editor 패널.</summary>
        public ImGuiAnimationEditorPanel? AnimationEditorPanel => _animEditor;

        /// <summary>Scene View RT 프레임버퍼.</summary>
        public Framebuffer? SceneViewFramebuffer => IsVisible ? _sceneRenderer?.Framebuffer : null;

        public void Initialize(GraphicsDevice device, IWindow window, IInputContext inputContext, string shaderDirectory)
        {
            _device = device;
            _window = window;

            // Create ImGui context
            _context = ImGui.CreateContext();
            ImGui.SetCurrentContext(_context);

            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
            io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
            io.ConfigDockingWithShift = false;
            io.ConfigViewportsNoAutoMerge = true; // 플로팅 창을 항상 별도 OS 윈도우로 분리

            // Multi-Viewport 백엔드 지원 플래그 — 이것이 없으면 ImGui가 보조 뷰포트를 생성하지 않음
            io.BackendFlags |= ImGuiBackendFlags.PlatformHasViewports;
            io.BackendFlags |= ImGuiBackendFlags.RendererHasViewports;

            // 뷰포트 모드 스타일 조정
            if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
            {
                var style = ImGui.GetStyle();
                style.WindowRounding = 0.0f;
                style.Colors[(int)ImGuiCol.WindowBg].W = 1.0f;
            }

            // Disable ImGui built-in ini auto-save (we manage it manually)
            unsafe { io.NativePtr->IniFilename = null; }

            // System clipboard integration (GLFW ↔ ImGui)
            SetupSystemClipboard(window, io);

            // Font setup — load all available fonts with auto-detected fallback chain
            var fontsDir = Path.Combine(ProjectContext.EditorAssetsPath, "Fonts");
            _fontGlyphRangesHandle = GCHandle.Alloc(_fontGlyphRanges, GCHandleType.Pinned);
            var glyphRangesPtr = _fontGlyphRangesHandle.AddrOfPinnedObject();

            // Fallback 폰트: TTF를 프로브하여 실제 지원 범위를 자동 감지
            var fallbackConfigs = new List<(string file, IntPtr ranges, float sizeScale)>();
            foreach (var (fbFile, sizeScale) in FallbackFontFiles)
            {
                var fbPath = Path.Combine(fontsDir, fbFile);
                if (!File.Exists(fbPath)) continue;
                var ranges = ProbeGlyphRanges(fbPath);
                EngineCore.PumpWindowEvents();
                if (ranges == null) continue;
                var handle = GCHandle.Alloc(ranges, GCHandleType.Pinned);
                _fallbackGlyphRangeHandles.Add(handle);
                fallbackConfigs.Add((fbFile, handle.AddrOfPinnedObject(), sizeScale));
            }

            _loadedFonts = new ImFontPtr[FontFiles.Length];
            for (int fi = 0; fi < FontFiles.Length; fi++)
            {
                var fontPath = Path.Combine(fontsDir, FontFiles[fi]);
                _loadedFonts[fi] = File.Exists(fontPath)
                    ? io.Fonts.AddFontFromFileTTF(fontPath, DefaultFontSize, default, glyphRangesPtr)
                    : io.Fonts.AddFontDefault();

                // Fallback 폰트 merge (자기 자신은 건너뜀)
                foreach (var (fbFile, fbRanges, fbScale) in fallbackConfigs)
                {
                    if (FontFiles[fi] == fbFile) continue;
                    var fbPath = Path.Combine(fontsDir, fbFile);
                    unsafe
                    {
                        var mergeCfg = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
                        mergeCfg.MergeMode = true;
                        mergeCfg.GlyphOffset = new System.Numerics.Vector2(0, DefaultFontSize * (1f - fbScale) * 0.5f);
                        io.Fonts.AddFontFromFileTTF(fbPath, DefaultFontSize * fbScale, mergeCfg, fbRanges);
                    }
                }
            }

            // Restore saved font selection
            _currentFont = EditorPreferences.EditorFont;

            // Apply theme
            ImGuiTheme.Apply(EditorPreferences.ColorTheme);

            // Restore saved UI scale
            _uiScale = EditorPreferences.UiScale;
            io.FontGlobalScale = _uiScale;

            // Load saved layout if exists
            _layoutManager.TryLoadSaved();

            // Create Veldrid renderer
            _renderer = new VeldridImGuiRenderer(device, shaderDirectory, device.SwapchainFramebuffer.OutputDescription);

            // Canvas UI 텍스처 바인딩 콜백 설정
            RoseEngine.CanvasRenderer.ResolveTextureBinding = tex =>
            {
                if (tex.TextureView == null)
                    tex.UploadToGPU(device);
                if (tex.TextureView == null) return System.IntPtr.Zero;
                return _renderer.GetOrCreateImGuiBinding(tex.TextureView);
            };

            // Create input handler
            _inputHandler = new ImGuiInputHandler();
            _inputHandler.Initialize(inputContext, window);

            // Multi-Viewport backends
            _platformBackend = new ImGuiPlatformBackend(_window, device);
            _platformBackend.Initialize(_inputHandler);
            _rendererBackend = new ImGuiRendererBackend(device, _renderer, _platformBackend);
            _rendererBackend.Initialize();
            Debug.Log("[ImGui] Multi-Viewport backends initialized");

            // Create panels
            _hierarchy = new ImGuiHierarchyPanel();
            _inspector = new ImGuiInspectorPanel(device, _renderer);
            _projectSettings = new ImGuiProjectSettingsPanel();
            _sceneEnvironment = new ImGuiSceneEnvironmentPanel();
            _console = new ImGuiConsolePanel();
            _gameView = new ImGuiGameViewPanel();
            _project = new ImGuiProjectPanel();
            _textureTool = new ImGuiTextureToolPanel();
            _spriteEditor = new ImGuiSpriteEditorPanel(device, _renderer);
            _animEditor = new ImGuiAnimationEditorPanel();
            _scripts = new ImGuiScriptsPanel();
            _feedback = new ImGuiFeedbackPanel();
            _startupPanel = new ImGuiStartupPanel();
            _preferencesPanel = new ImGuiPreferencesPanel();
            _inspector.AnimEditor = _animEditor;

            // EditorBridge에 overlay 참조 등록
            EditorBridge.SetImGuiOverlay(this);

            // Variant tree 초기 빌드
            PrefabVariantTree.Instance.Rebuild();

            // Create render target manager & initial offscreen RT
            _rtManager = new ImGuiRenderTargetManager(device, _renderer, _gameView);
            _rtManager.CreateInitial();

            // Scene View
            _sceneView = new ImGuiSceneViewPanel();
            _editorCamera = new EditorCamera();
            _sceneRenderer = new SceneViewRenderer();
            _sceneRenderer.Initialize(device);
            _camProxy = new SceneViewCameraProxy();
            _gizmo = new TransformGizmo();
            _gizmo.Initialize(device);
            _gizmo.AnimEditor = _animEditor;
            _colliderEditor = new ColliderGizmoEditor();
            _rectGizmoEditor = new RectGizmoEditor();
            _uiTransformGizmo = new UITransformGizmo2D();
            _rectSelection = new RectSelectionTool();

            // Register 2D gizmo overlay callback (drawn within SceneView panel window context)
            _sceneView.DrawGizmoOverlay = DrawSceneView2DOverlays;
            // Prefab Edit Mode 오버레이를 SceneView 윈도우 내에서 그려 Z-order 문제 방지
            _sceneView.DrawPrefabOverlay = DrawPrefabOverlaysInSceneView;
            _gizmoRenderer = new GizmoRenderer();
            _gizmoRenderer.Initialize(device);
            RoseEngine.Gizmos.Backend = new GizmoRendererBackend(_gizmoRenderer);
            EditorAssets.Initialize(device, _renderer!);

            _sceneRtManager = new SceneViewRenderTargetManager(device, _renderer, _sceneView, _sceneRenderer);
            _sceneRtManager.CreateInitial();

            // Load About image
            LoadAboutImage();

            // Register panels with PanelMaximizer for tab context menu
            PanelMaximizer.Register("Hierarchy", _hierarchy);
            PanelMaximizer.Register("Inspector", _inspector);
            PanelMaximizer.Register("Project Settings", _projectSettings);
            PanelMaximizer.Register("Scene Environment", _sceneEnvironment);
            PanelMaximizer.Register("Console", _console);
            PanelMaximizer.Register("Game View", _gameView);
            PanelMaximizer.Register("Scene View", _sceneView);
            PanelMaximizer.Register("Project", _project);
            PanelMaximizer.Register("Texture Tool", _textureTool);
            PanelMaximizer.Register("Sprite Editor", _spriteEditor);
            PanelMaximizer.Register("Animation Editor", _animEditor);
            PanelMaximizer.Register("Scripts", _scripts);
            PanelMaximizer.Register("Feedback", _feedback);
            PanelMaximizer.Register("Preferences", _preferencesPanel);

            // Restore saved panel visibility (only if layout was loaded, not first-time default)
            if (!_layoutManager.NeedsLayout)
                RestorePanelStates();

            Debug.Log("[ImGui] Overlay initialized");
        }

        public void Toggle()
        {
            IsVisible = !IsVisible;

            if (!IsVisible)
            {
                _layoutManager.Save();
            }
            else
            {
                _gameView?.ResetLayoutStabilization();
                _sceneView?.ResetLayoutStabilization();
            }
        }

        public void Update(float deltaTime)
        {
            if (!IsVisible) return;

            ImGui.SetCurrentContext(_context);

            // ── Preferences 역동기화 ──
            // Preferences 패널에서 변경된 UiScale/EditorFont 값을 Overlay 로컬 상태와 맞춘다.
            // (Color Theme은 Preferences 패널이 직접 ImGuiTheme.Apply를 호출하므로 여기선 다루지 않음.)
            if (Math.Abs(_uiScale - EditorPreferences.UiScale) > 0.0001f)
            {
                _uiScale = EditorPreferences.UiScale;
                ImGui.GetIO().FontGlobalScale = _uiScale;
            }
            if (_currentFont != EditorPreferences.EditorFont)
            {
                _currentFont = EditorPreferences.EditorFont;
            }

            _inputHandler?.Update(deltaTime, _window.Size.X, _window.Size.Y);
            ImGui.NewFrame();
            PushCurrentFont();

            // ── 프로젝트 미로드 시: startup panel만 표시 ──
            if (!ProjectContext.IsProjectLoaded)
            {
                _startupPanel?.Draw();
                PopCurrentFont();
                return;
            }

            // ── Dockspace ──
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.WorkPos);
            ImGui.SetNextWindowSize(viewport.WorkSize);
            ImGui.SetNextWindowViewport(viewport.ID);

            var dockFlags = ImGuiWindowFlags.NoDocking
                          | ImGuiWindowFlags.NoTitleBar
                          | ImGuiWindowFlags.NoCollapse
                          | ImGuiWindowFlags.NoResize
                          | ImGuiWindowFlags.NoMove
                          | ImGuiWindowFlags.NoBringToFrontOnFocus
                          | ImGuiWindowFlags.NoNavFocus
                          | ImGuiWindowFlags.NoBackground
                          | ImGuiWindowFlags.MenuBar;

            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

            ImGui.Begin("DockSpace", dockFlags);
            ImGui.PopStyleVar(3);

            var dockspaceId = ImGui.GetID("EditorDockSpace");
            ImGui.DockSpace(dockspaceId, Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);

            // ── 첫 프레임 또는 리셋 → 기본 레이아웃 적용 ──
            _layoutManager.ApplyDefaultIfNeeded(dockspaceId, _window,
                _hierarchy!, _inspector!, _sceneEnvironment!,
                _console!, _gameView!, _project!, _sceneView, _scripts);

            // ── Window title ──
            var currentDirty = RoseEngine.SceneManager.GetActiveScene().isDirty;
            if (_titleNeedsUpdate || currentDirty != _lastDirtyState)
            {
                _titleNeedsUpdate = false;
                _lastDirtyState = currentDirty;
                UpdateWindowTitle();
            }

            // ── Keyboard shortcuts ──
            ProcessSceneShortcuts();

            // ── Menu bar ──
            if (ImGui.BeginMenuBar())
            {
                // File menu
                if (ImGui.BeginMenu("File"))
                {
                    bool canEditScene = !EditorPlayMode.IsInPlaySession;

                    if (ImGui.MenuItem("New Project..."))
                        _startupPanel?.ShowNewProjectDialog();

                    if (ImGui.MenuItem("Open Project..."))
                        _startupPanel?.OpenExistingProject();

                    ImGui.Separator();

                    if (ImGui.MenuItem("New Scene", "Ctrl+N", false, canEditScene))
                        NewScene();

                    if (ImGui.MenuItem("Open Scene", "Ctrl+O", false, canEditScene))
                        OpenScene();

                    ImGui.Separator();

                    if (ImGui.MenuItem("Save Scene", "Ctrl+S", false, canEditScene))
                        SaveScene();

                    if (ImGui.MenuItem("Save Scene As...", "Ctrl+Shift+S", false, canEditScene))
                        SaveSceneAs();

                    ImGui.Separator();

                    if (ImGui.MenuItem("Reimport All", null, false, canEditScene))
                        RequestReimportAll();

                    ImGui.Separator();

                    if (ImGui.MenuItem("Exit"))
                        RequestQuit();

                    ImGui.EndMenu();
                }

                // Edit menu
                if (ImGui.BeginMenu("Edit"))
                {
                    var uDesc = UndoSystem.UndoDescription;
                    var rDesc = UndoSystem.RedoDescription;
                    string undoLabel = uDesc != null ? $"Undo {uDesc}" : "Undo";
                    string redoLabel = rDesc != null ? $"Redo {rDesc}" : "Redo";

                    if (ImGui.MenuItem(undoLabel, "Ctrl+Z", false, uDesc != null))
                        UndoSystem.PerformUndo();
                    if (ImGui.MenuItem(redoLabel, "Ctrl+Shift+Z", false, rDesc != null))
                        UndoSystem.PerformRedo();

                    ImGui.Separator();

                    if (ImGui.MenuItem("Preferences..."))
                        _preferencesPanel!.IsOpen = true;

                    ImGui.EndMenu();
                }

                // View menu
                if (ImGui.BeginMenu("View"))
                {
                    // Windows submenu
                    if (ImGui.BeginMenu("Windows"))
                    {
                        // General
                        if (ImGui.BeginMenu("General"))
                        {
                            bool h = _hierarchy!.IsOpen;
                            if (ImGui.MenuItem("Hierarchy", null, ref h))
                                _hierarchy.IsOpen = h;

                            bool i = _inspector!.IsOpen;
                            if (ImGui.MenuItem("Inspector", null, ref i))
                                _inspector.IsOpen = i;

                            bool c = _console!.IsOpen;
                            if (ImGui.MenuItem("Console", null, ref c))
                                _console.IsOpen = c;

                            bool p = _project!.IsOpen;
                            if (ImGui.MenuItem("Project", null, ref p))
                                _project.IsOpen = p;

                            bool ps = _projectSettings!.IsOpen;
                            if (ImGui.MenuItem("Project Settings", null, ref ps))
                                _projectSettings.IsOpen = ps;

                            bool sc = _scripts!.IsOpen;
                            if (ImGui.MenuItem("Scripts", null, ref sc))
                                _scripts.IsOpen = sc;

                            bool fb2 = _feedback!.IsOpen;
                            if (ImGui.MenuItem("Feedback", null, ref fb2))
                                _feedback.IsOpen = fb2;

                            ImGui.EndMenu();
                        }

                        // Scene
                        if (ImGui.BeginMenu("Scene"))
                        {
                            bool sv = _sceneView!.IsOpen;
                            if (ImGui.MenuItem("Scene View", null, ref sv))
                                _sceneView.IsOpen = sv;

                            bool gv = _gameView!.IsOpen;
                            if (ImGui.MenuItem("Game View", null, ref gv))
                                _gameView.IsOpen = gv;

                            bool se = _sceneEnvironment!.IsOpen;
                            if (ImGui.MenuItem("Scene Environment", null, ref se))
                                _sceneEnvironment.IsOpen = se;

                            ImGui.EndMenu();
                        }

                        // Asset
                        if (ImGui.BeginMenu("Asset"))
                        {
                            bool se2 = _spriteEditor!.IsOpen;
                            if (ImGui.MenuItem("Sprite Editor", null, ref se2))
                                _spriteEditor.IsOpen = se2;

                            bool ae2 = _animEditor!.IsOpen;
                            if (ImGui.MenuItem("Animation Editor", null, ref ae2))
                                _animEditor.IsOpen = ae2;

                            ImGui.EndMenu();
                        }

                        ImGui.EndMenu();
                    }

                    ImGui.Separator();

                    // UI submenu (Font + Scale)
                    if (ImGui.BeginMenu("UI"))
                    {
                        // Set Font submenu
                        if (ImGui.BeginMenu("Set Font"))
                        {
                            for (int fi = 0; fi < FontNames.Length; fi++)
                            {
                                bool isCurrent = _currentFont == FontNames[fi];
                                if (ImGui.MenuItem(FontNames[fi], null, isCurrent, true))
                                    SetFont(FontNames[fi]);
                            }
                            ImGui.EndMenu();
                        }

                        // UI Scale submenu
                        if (ImGui.BeginMenu("UI Scale"))
                        {
                            if (ImGui.MenuItem("Zoom In", "Ctrl+=", false, _uiScale < MaxUiScale))
                                SetUiScale(_uiScale + UiScaleStep);

                            if (ImGui.MenuItem("Zoom Out", "Ctrl+-", false, _uiScale > MinUiScale))
                                SetUiScale(_uiScale - UiScaleStep);

                            ImGui.Separator();

                            if (ImGui.MenuItem("Reset (100%)", "Ctrl+0"))
                                SetUiScale(1.0f);

                            ImGui.Separator();

                            string[] presets = { "50%", "75%", "100%", "125%", "150%", "200%" };
                            float[] values = { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f };
                            for (int idx = 0; idx < presets.Length; idx++)
                            {
                                bool selected = Math.Abs(_uiScale - values[idx]) < 0.01f;
                                if (ImGui.MenuItem(presets[idx], null, selected, true))
                                    SetUiScale(values[idx]);
                            }

                            ImGui.EndMenu();
                        }

                        ImGui.EndMenu();
                    }

                    ImGui.Separator();

                    // Debug submenu
                    if (ImGui.BeginMenu("Debug"))
                    {
                        var overlay = RoseEngine.DebugOverlaySettings.overlay;

                        bool gbuf = overlay == RoseEngine.DebugOverlay.GBuffer;
                        if (ImGui.MenuItem("Show G-Buffer", null, ref gbuf))
                            RoseEngine.DebugOverlaySettings.overlay = gbuf ? RoseEngine.DebugOverlay.GBuffer : RoseEngine.DebugOverlay.None;

                        bool shadow = overlay == RoseEngine.DebugOverlay.ShadowMap;
                        if (ImGui.MenuItem("Show Shadow Map", null, ref shadow))
                            RoseEngine.DebugOverlaySettings.overlay = shadow ? RoseEngine.DebugOverlay.ShadowMap : RoseEngine.DebugOverlay.None;

                        ImGui.EndMenu();
                    }

                    ImGui.EndMenu();
                }

                // Layout menu
                if (ImGui.BeginMenu("Layout"))
                {
                    if (ImGui.MenuItem("Save Layout"))
                        _layoutManager.Save();

                    if (ImGui.MenuItem("Reset to Default"))
                        _layoutManager.RequestReset();

                    ImGui.EndMenu();
                }

                // Tools menu
                if (ImGui.BeginMenu("Tools"))
                {
                    bool tt = _textureTool!.IsOpen;
                    if (ImGui.MenuItem("Texture Tool", null, ref tt))
                        _textureTool.IsOpen = tt;

                    ImGui.EndMenu();
                }

                // About
                if (ImGui.MenuItem("About"))
                    _showAbout = true;

                // ── Play / Pause / Stop toolbar (centered) ──
                DrawPlayModeToolbar();

                ImGui.EndMenuBar();
            }

            ImGui.End(); // DockSpace

            // ── Startup panel (프로젝트 미로드 시) ──
            _startupPanel?.Draw();

            // ── Draw panels ──
            _hierarchy?.Draw();
            _inspector?.Draw(_hierarchy?.SelectedGameObjectId, _hierarchy?.SelectionVersion ?? 0, _project?.SelectedAssetPath, _project?.AssetSelectionVersion ?? 0, _project?.SelectedAssetPaths);
            _projectSettings?.Draw();
            _sceneEnvironment?.Draw();
            _console?.Draw();
            _gameView?.Draw();
            _sceneView?.Draw();
            _project?.Draw();

            // ── Prefab Edit Mode overlays ──
            // Scene View 윈도우 내부에서 DrawPrefabOverlay 콜백으로 렌더링됨 (Z-order 문제 방지)
            _textureTool?.Draw();
            _spriteEditor?.Draw();
            _animEditor?.Draw();
            _scripts?.Draw();
            _feedback?.Draw();
            _preferencesPanel?.Draw();

            // ── Property windows (고정 Inspector) ──
            DrawPropertyWindows();

            // ── Project panel: double-click .scene → open scene ──
            HandleProjectSceneOpen();

            // ── Scene View input ──
            UpdateSceneViewInput(deltaTime);

            // ── Material drag-hover preview ──
            HandleMaterialDragHover();

            // ── Asset drag-drop onto Scene View ──
            HandleSceneViewAssetDrop();

            // ── About popup ──
            DrawAboutPopup();

            // ── Quit confirmation popup ──
            DrawQuitConfirmPopup();

            // ── Reimport All confirmation popup ──
            DrawReimportConfirmPopup();
            DrawReimportNoticePopup();

            // ── Open Scene confirmation popup ──
            DrawOpenSceneConfirmPopup();

            // ── Prefab Back confirmation popup ──
            DrawPrefabBackConfirmPopup();

            // ── Alert popups (component migration notices, etc.) ──
            EditorModal.DrawAlertPopups();

            // ── Script build progress modal ──
            DrawBuildProgressModal();

            // ── Maximize auto-restore (최대화된 패널이 닫히면 자동 복원) ──
            PanelMaximizer.CheckAutoRestore();

            // ── Auto-save (최대화 중에는 패널 상태를 저장하지 않음) ──
            if (!PanelMaximizer.IsMaximized)
                SyncPanelStatesToEditorState();
            _layoutManager.UpdateAutoSave(deltaTime);

            PopCurrentFont();
        }

        /// <summary>
        /// Ensure the offscreen render target matches the selected resolution.
        /// Call before RenderSystem.Render() each frame when the editor is visible.
        /// </summary>
        public void EnsureOffscreenRT()
        {
            if (!IsVisible) return;
            _rtManager?.EnsureMatchesResolution();
            _sceneRtManager?.EnsureMatchesResolution();
        }

        public void Render(CommandList cl)
        {
            if (!IsVisible) return;

            ImGui.SetCurrentContext(_context);

            ImGui.Render();

            // 메인 뷰포트 렌더
            var drawData = ImGui.GetDrawData();
            if (drawData.CmdListsCount > 0)
            {
                cl.SetFramebuffer(_device.SwapchainFramebuffer);
                cl.ClearColorTarget(0, new RgbaFloat(0.902f, 0.863f, 0.824f, 1f)); // ironRoseColor
                cl.ClearDepthStencil(1f);
                cl.SetFullViewports();
                _renderer?.Render(cl, drawData);
            }
        }

        /// <summary>
        /// 보조 뷰포트 업데이트 + 렌더. 메인 CL이 submit된 후에 호출해야 함.
        /// (공유 vertex/index 버퍼 충돌 방지)
        /// </summary>
        public void RenderSecondaryViewports()
        {
            if (!IsVisible) return;

            ImGui.SetCurrentContext(_context);
            var io = ImGui.GetIO();
            if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
            {
                ImGui.UpdatePlatformWindows();
                ImGui.RenderPlatformWindowsDefault();
            }
        }

        /// <summary>
        /// 워밍업 중 전용 업데이트. 에디터 패널 대신 진행 모달만 표시하고 모든 입력 차단.
        /// </summary>
        public void UpdateWarmup(float deltaTime, int current, int total, string? currentAsset, double elapsed, string title = "Caching Assets...")
        {
            if (!IsVisible) return;

            ImGui.SetCurrentContext(_context);
            // 입력 상태만 갱신 (NewFrame에 필요)
            _inputHandler?.Update(deltaTime, _window.Size.X, _window.Size.Y);
            ImGui.NewFrame();
            PushCurrentFont();

            // 전체 화면 어둡게
            var viewport = ImGui.GetMainViewport();
            var drawList = ImGui.GetBackgroundDrawList();
            drawList.AddRectFilled(viewport.Pos, viewport.Pos + viewport.Size,
                ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.12f, 1f)));

            // 모달 창 크기/위치
            float modalW = 480;
            float modalH = 140;
            var center = viewport.GetCenter();
            ImGui.SetNextWindowPos(center, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(modalW, modalH));

            var flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                      | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNav
                      | ImGuiWindowFlags.NoTitleBar;

            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(24, 20));
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.16f, 0.16f, 0.21f, 1f));

            if (ImGui.Begin("##WarmupModal", flags))
            {
                // 제목
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.5f, 1f));
                ImGui.Text(title);
                ImGui.PopStyleColor();

                ImGui.Spacing();

                // 프로그레스 바
                float progress = total > 0 ? (float)current / total : 0f;
                ImGui.ProgressBar(progress, new Vector2(-1, 20), $"{current} / {total}");

                ImGui.Spacing();

                // 현재 파일명 + 경과 시간
                string assetDisplay = currentAsset ?? "";
                if (assetDisplay.Length > 50)
                    assetDisplay = "..." + assetDisplay[^47..];

                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));
                ImGui.Text(assetDisplay);
                ImGui.SameLine(ImGui.GetWindowWidth() - 80);
                ImGui.Text($"{elapsed:F1}s");
                ImGui.PopStyleColor();

                ImGui.End();
            }

            ImGui.PopStyleColor();
            ImGui.PopStyleVar(2);

            PopCurrentFont();
        }

        public void Resize(uint width, uint height)
        {
            // Offscreen RT resize is handled in EnsureOffscreenRT()
        }

        // ── About Image ──

        private void LoadAboutImage()
        {
            var path = Path.Combine("iron_rose.png");
            if (!File.Exists(path))
            {
                Debug.LogWarning("[ImGui] About image not found: " + path);
                return;
            }

            try
            {
                using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(path);
                _aboutImageW = (uint)image.Width;
                _aboutImageH = (uint)image.Height;

                var pixels = new byte[_aboutImageW * _aboutImageH * 4];
                image.CopyPixelDataTo(pixels);

                var factory = _device.ResourceFactory;
                _aboutTexture = factory.CreateTexture(TextureDescription.Texture2D(
                    _aboutImageW, _aboutImageH, 1, 1,
                    PixelFormat.R8_G8_B8_A8_UNorm,
                    TextureUsage.Sampled));
                _device.UpdateTexture(_aboutTexture, pixels, 0, 0, 0, _aboutImageW, _aboutImageH, 1, 0, 0);

                _aboutTextureView = factory.CreateTextureView(_aboutTexture);
                _aboutTextureId = _renderer!.GetOrCreateImGuiBinding(_aboutTextureView);

                Debug.Log($"[ImGui] About image loaded: {_aboutImageW}x{_aboutImageH}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] About image load failed: {ex.Message}");
            }
        }

        private void DrawBuildProgressModal()
        {
            if (!EditorBridge.ShouldShowBuildModal) return;

            var viewport = ImGui.GetMainViewport();
            float modalW = 360;
            float modalH = 80;
            var center = viewport.GetCenter();
            ImGui.SetNextWindowPos(center, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(modalW, modalH));

            var flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                      | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNav
                      | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar;

            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(24, 16));
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.16f, 0.16f, 0.21f, 1f));

            if (ImGui.Begin("##BuildProgressModal", flags))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.5f, 1f));
                ImGui.Text("Compiling Scripts...");
                ImGui.PopStyleColor();

                ImGui.Spacing();

                float elapsed = EditorBridge.BuildElapsed;
                // indeterminate spinner: animated progress bar
                float t = (elapsed * 2f) % 1f;
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new Vector4(0.4f, 0.65f, 1f, 1f));
                ImGui.ProgressBar(t, new Vector2(-1, 16), $"{elapsed:F1}s");
                ImGui.PopStyleColor();

                ImGui.End();
            }

            ImGui.PopStyleColor();
            ImGui.PopStyleVar(2);
        }

        private void DrawAboutPopup()
        {
            if (_showAbout)
            {
                ImGui.OpenPopup("About IronRose");
                _showAbout = false;
            }

            float maxW = 480;
            float scale = _aboutImageW > 0 ? Math.Min(maxW / _aboutImageW, 1f) : 1f;
            float imgW = _aboutImageW * scale;
            float imgH = _aboutImageH * scale;

            ImGui.SetNextWindowSize(new Vector2(imgW + 32, imgH + 140), ImGuiCond.Always);
            if (ImGui.BeginPopupModal("About IronRose", ImGuiWindowFlags.NoResize))
            {
                if (_aboutTextureId != IntPtr.Zero)
                {
                    float avail = ImGui.GetContentRegionAvail().X;
                    float offset = (avail - imgW) * 0.5f;
                    if (offset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
                    ImGui.Image(_aboutTextureId, new Vector2(imgW, imgH));
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                string text = "IronRose Engine";
                float textW = ImGui.CalcTextSize(text).X;
                ImGui.SetCursorPosX((ImGui.GetWindowWidth() - textW) * 0.5f);
                ImGui.Text(text);

                string versionText = $"Build: {RoseEngine.BuildVersion.BuildEnv} | {RoseEngine.BuildVersion.BuildTime}";
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1f));
                float versionW = ImGui.CalcTextSize(versionText).X;
                ImGui.SetCursorPosX((ImGui.GetWindowWidth() - versionW) * 0.5f);
                ImGui.Text(versionText);
                ImGui.PopStyleColor();

                ImGui.Spacing();
                float btnW = 80;
                ImGui.SetCursorPosX((ImGui.GetWindowWidth() - btnW) * 0.5f);
                if (ImGui.Button("Close", new Vector2(btnW, 0)))
                    ImGui.CloseCurrentPopup();

                ImGui.EndPopup();
            }
        }

        // ── Quit confirmation ──

        public void RequestQuit()
        {
            var scene = RoseEngine.SceneManager.GetActiveScene();
            if (scene.isDirty)
            {
                _showQuitConfirm = true;
            }
            else
            {
                IsQuitConfirmed = true;
                _window.Close();
            }
        }

        private void DrawQuitConfirmPopup()
        {
            if (_showQuitConfirm)
            {
                ImGui.OpenPopup("Unsaved Changes##QuitConfirm");
                _showQuitConfirm = false;
            }

            if (ImGui.BeginPopupModal("Unsaved Changes##QuitConfirm", ImGuiWindowFlags.AlwaysAutoResize))
            {
                var scene = RoseEngine.SceneManager.GetActiveScene();
                ImGui.Text($"Scene \"{scene.name}\" has unsaved changes.");
                ImGui.Text("Do you want to save before quitting?");
                ImGui.Spacing();

                if (ImGui.Button("Save and Quit"))
                {
                    SaveScene();
                    IsQuitConfirmed = true;
                    ImGui.CloseCurrentPopup();
                    _window.Close();
                }
                ImGui.SameLine();
                if (ImGui.Button("Quit without Saving"))
                {
                    IsQuitConfirmed = true;
                    ImGui.CloseCurrentPopup();
                    _window.Close();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }

        // ── Reimport All ──

        private void RequestReimportAll()
        {
            if (EditorPlayMode.IsInPlaySession) return;

            var scene = RoseEngine.SceneManager.GetActiveScene();
            if (scene.isDirty)
            {
                _showReimportConfirm = true;
            }
            else
            {
                PerformReimportAll();
            }
        }

        private void DrawReimportConfirmPopup()
        {
            if (_showReimportConfirm)
            {
                ImGui.OpenPopup("Unsaved Changes##ReimportConfirm");
                _showReimportConfirm = false;
            }

            if (ImGui.BeginPopupModal("Unsaved Changes##ReimportConfirm", ImGuiWindowFlags.AlwaysAutoResize))
            {
                var scene = RoseEngine.SceneManager.GetActiveScene();
                ImGui.Text($"Scene \"{scene.name}\" has unsaved changes.");
                ImGui.Text("Do you want to save before reimporting all assets?");
                ImGui.Spacing();

                if (ImGui.Button("Save and Reimport"))
                {
                    SaveScene();
                    ImGui.CloseCurrentPopup();
                    PerformReimportAll();
                }
                ImGui.SameLine();
                if (ImGui.Button("Reimport without Saving"))
                {
                    ImGui.CloseCurrentPopup();
                    PerformReimportAll();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }

        private void PerformReimportAll()
        {
            _showReimportNotice = true;
        }

        private void DrawReimportNoticePopup()
        {
            if (_showReimportNotice)
            {
                ImGui.OpenPopup("Reimport All##Notice");
                _showReimportNotice = false;
            }

            if (ImGui.BeginPopupModal("Reimport All##Notice", ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.Text("All asset caches will be cleared.");
                ImGui.Text("The editor will close. Please restart manually.");
                ImGui.Spacing();

                if (ImGui.Button("OK", new Vector2(120, 0)))
                {
                    // Write sentinel file — next launch will clear cache
                    var sentinelPath = Path.Combine(ProjectContext.ProjectRoot, ".reimport_all");
                    File.WriteAllText(sentinelPath, "");

                    ImGui.CloseCurrentPopup();
                    IsQuitConfirmed = true;
                    _window.Close();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel", new Vector2(120, 0)))
                {
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }

        // ── Open Scene from Project panel ──

        // ================================================================
        // Prefab Edit Mode — Scene View 내 오버레이 (child window)
        // ================================================================

        /// <summary>
        /// Scene View 윈도우 컨텍스트 내에서 호출되는 프리팹 오버레이.
        /// 별도 ImGui 윈도우가 아닌 child window로 그려 Z-order 문제를 근본적으로 해결.
        /// </summary>
        private void DrawPrefabOverlaysInSceneView()
        {
            if (_sceneView == null) return;

            // Canvas Edit Mode breadcrumb
            if (EditorState.IsEditingCanvas)
                DrawCanvasBreadcrumbChild();

            if (!EditorState.IsEditingPrefab) return;

            DrawPrefabBreadcrumbChild();
            DrawPrefabVariantTreeChild();
        }

        /// <summary>Canvas Edit Mode Breadcrumb: Scene View 이미지 좌상단에 child window로 배치.</summary>
        private void DrawCanvasBreadcrumbChild()
        {
            var imgMin = _sceneView!.ImageScreenMin;
            var imgMax = _sceneView.ImageScreenMax;
            if (imgMax.X - imgMin.X < 1 || imgMax.Y - imgMin.Y < 1) return;

            string sceneName = "Scene";
            var scene = RoseEngine.SceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(scene.name))
                sceneName = scene.name;

            string canvasName = "Canvas";
            if (EditorState.EditingCanvasGoId.HasValue)
            {
                var go = UndoUtility.FindGameObjectById(EditorState.EditingCanvasGoId.Value);
                if (go != null) canvasName = go.name;
            }

            const float pad = 6f;
            const float childPadX = 8f;
            const float childPadY = 4f;
            var bcScreenPos = new Vector2(imgMin.X + pad, imgMin.Y + pad);

            ImGui.SetCursorScreenPos(bcScreenPos);

            float maxWidth = imgMax.X - imgMin.X - pad * 2;
            ImGui.PushStyleColor(ImGuiCol.ChildBg,
                new Vector4(0.18f, 0.25f, 0.12f, 0.92f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(childPadX, childPadY));
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);

            if (ImGui.BeginChild("##CanvasBreadcrumb", new Vector2(maxWidth, 0),
                    ImGuiChildFlags.AutoResizeX | ImGuiChildFlags.AutoResizeY
                    | ImGuiChildFlags.AlwaysAutoResize,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.TextDisabled(sceneName);
                ImGui.SameLine();
                ImGui.TextDisabled(">");
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.4f, 1.0f), canvasName);

                ImGui.SameLine(0, 20);
                if (ImGui.SmallButton("Back##canvas_bc"))
                    CanvasEditMode.Exit();
            }
            ImGui.EndChild();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();
        }

        /// <summary>Breadcrumb: Scene View 이미지 좌상단에 child window로 배치.</summary>
        private float _lastBreadcrumbHeight;

        private void DrawPrefabBreadcrumbChild()
        {
            var imgMin = _sceneView!.ImageScreenMin;
            var imgMax = _sceneView.ImageScreenMax;
            if (imgMax.X - imgMin.X < 1 || imgMax.Y - imgMin.Y < 1) return;

            var breadcrumbs = PrefabEditMode.Breadcrumbs;

            const float pad = 6f;
            const float childPadX = 8f;
            const float childPadY = 4f;
            var bcScreenPos = new Vector2(imgMin.X + pad, imgMin.Y + pad);

            // 배경 사각형을 DrawList에 직접 그림
            // 먼저 child window를 그려서 크기를 알아내야 하므로, 커서 위치를 설정하고 child를 시작
            ImGui.SetCursorScreenPos(bcScreenPos);

            // child window: 자동 크기 조절을 위해 큰 영역으로 시작 (실제 내용에 맞게 잘림)
            float maxWidth = imgMax.X - imgMin.X - pad * 2;
            ImGui.PushStyleColor(ImGuiCol.ChildBg,
                new Vector4(0.12f, 0.18f, 0.30f, 0.92f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(childPadX, childPadY));
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);

            if (ImGui.BeginChild("##PrefabBreadcrumb", new Vector2(maxWidth, 0),
                    ImGuiChildFlags.AutoResizeX | ImGuiChildFlags.AutoResizeY
                    | ImGuiChildFlags.AlwaysAutoResize,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                // Breadcrumb 경로
                for (int i = 0; i < breadcrumbs.Count; i++)
                {
                    if (i > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled(">");
                        ImGui.SameLine();
                    }

                    bool isCurrent = i == breadcrumbs.Count - 1;

                    if (isCurrent)
                    {
                        ImGui.TextColored(
                            new Vector4(0.4f, 0.7f, 1.0f, 1.0f),
                            breadcrumbs[i]);
                    }
                    else
                    {
                        if (ImGui.SmallButton(breadcrumbs[i]))
                        {
                            int backsNeeded = breadcrumbs.Count - 1 - i;
                            RequestPrefabBack(backsNeeded);
                        }
                    }
                }

                // Save / Back 버튼
                ImGui.SameLine(0, 20);
                if (ImGui.SmallButton("Save"))
                    PrefabEditMode.Save();
                ImGui.SameLine();
                if (ImGui.SmallButton("Back"))
                    RequestPrefabBack(1);

                _lastBreadcrumbHeight = ImGui.GetWindowSize().Y;
            }
            ImGui.EndChild();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();
        }

        /// <summary>Variant Tree: Breadcrumb 아래에 child window로 배치.</summary>
        private void DrawPrefabVariantTreeChild()
        {
            var editingGuid = EditorState.EditingPrefabGuid;
            if (string.IsNullOrEmpty(editingGuid)) return;

            var imgMin = _sceneView!.ImageScreenMin;
            var imgMax = _sceneView.ImageScreenMax;
            if (imgMax.X - imgMin.X < 1 || imgMax.Y - imgMin.Y < 1) return;

            var tree = PrefabVariantTree.Instance;
            var rootNode = tree.BuildTree(editingGuid);
            if (rootNode == null) return;

            // Breadcrumb 아래에 배치
            const float pad = 6f;
            const float gap = 4f;
            float topY = imgMin.Y + pad + _lastBreadcrumbHeight + gap;
            var vtScreenPos = new Vector2(imgMin.X + pad, topY);

            ImGui.SetCursorScreenPos(vtScreenPos);

            // 최대 높이 제한 (Scene View 높이의 60%)
            float maxH = (imgMax.Y - topY - 10f) * 0.6f;
            if (maxH < 30f) return;

            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.85f, 0.85f, 0.85f, 0.95f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 4));
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);

            if (ImGui.BeginChild("##PrefabVariantTree", new Vector2(300, maxH),
                    ImGuiChildFlags.AutoResizeX | ImGuiChildFlags.AutoResizeY
                    | ImGuiChildFlags.AlwaysAutoResize,
                    ImGuiWindowFlags.NoScrollbar))
            {
                ImGui.TextDisabled("Variant Tree");
                ImGui.Separator();
                DrawVariantTreeNode(rootNode, editingGuid);
            }
            ImGui.EndChild();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();
        }

        private void DrawVariantTreeNode(PrefabVariantTree.TreeNode node, string editingGuid)
        {
            bool isCurrent = node.Guid == editingGuid;
            bool hasChildren = node.Children.Count > 0;

            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen;
            if (!hasChildren)
                flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
            if (isCurrent)
                flags |= ImGuiTreeNodeFlags.Selected;

            // 현재 편집 중인 노드는 강조 배경색
            if (isCurrent)
            {
                ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.2f, 0.45f, 0.8f, 1f));
                ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.25f, 0.5f, 0.85f, 1f));
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0f, 0f, 0f, 1f));
            }

            string label = node.IsVariant ? $"{node.DisplayName}  (V)" : node.DisplayName;
            bool opened = ImGui.TreeNodeEx($"{label}##{node.Guid}", flags);

            ImGui.PopStyleColor(isCurrent ? 3 : 1);

            // 클릭 시 해당 프리팹 편집 모드로 진입
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !isCurrent && node.Path != null)
            {
                // 현재 편집 중인 것을 저장 후 대상 프리팹으로 전환
                PrefabEditMode.Save();
                PrefabEditMode.Exit();
                PrefabEditMode.Enter(node.Path);
            }

            if (hasChildren && opened)
            {
                foreach (var child in node.Children)
                    DrawVariantTreeNode(child, editingGuid);
                ImGui.TreePop();
            }
        }

        private void HandleProjectSceneOpen()
        {
            if (EditorPlayMode.IsInPlaySession) return;

            // .renderer double-click → activate profile
            var rendererPath = _project?.ConsumePendingActivateRendererPath();
            if (rendererPath != null && _projectSettings != null)
            {
                var db = RoseEngine.Resources.GetAssetDatabase();
                var guid = db?.GetGuidFromPath(rendererPath);
                if (guid != null)
                {
                    _projectSettings.ActivateProfile(guid, rendererPath);
                }
            }

            // Thumbnail generation request → EngineCore에서 소비

            // .anim double-click → Animation Editor
            var animPath = _project?.ConsumePendingOpenAnimPath();
            if (animPath != null && _animEditor != null)
            {
                var db = RoseEngine.Resources.GetAssetDatabase();
                var clip = db?.Load<RoseEngine.AnimationClip>(animPath);
                if (clip != null)
                    _animEditor.Open(animPath, clip);
            }

            // Prefab double-click → Prefab Edit Mode
            var prefabEditPath = _project?.ConsumePendingOpenPrefabPath();
            if (prefabEditPath != null)
            {
                PrefabEditMode.Enter(prefabEditPath);
                return;
            }

            var scenePath = _project?.ConsumePendingOpenScenePath();
            if (scenePath == null) return;

            var scene = RoseEngine.SceneManager.GetActiveScene();
            if (scene.isDirty)
            {
                _pendingOpenScenePath = scenePath;
                _showOpenSceneConfirm = true;
            }
            else
            {
                LoadSceneFromPath(scenePath);
            }
        }

        private void DrawOpenSceneConfirmPopup()
        {
            if (_showOpenSceneConfirm)
            {
                ImGui.OpenPopup("Unsaved Changes##OpenSceneConfirm");
                _showOpenSceneConfirm = false;
            }

            if (ImGui.BeginPopupModal("Unsaved Changes##OpenSceneConfirm", ImGuiWindowFlags.AlwaysAutoResize))
            {
                var scene = RoseEngine.SceneManager.GetActiveScene();
                ImGui.Text($"Scene \"{scene.name}\" has unsaved changes.");
                ImGui.Text("Do you want to save before opening another scene?");
                ImGui.Spacing();

                if (ImGui.Button("Save and Open"))
                {
                    SaveScene();
                    ImGui.CloseCurrentPopup();
                    if (_pendingOpenScenePath != null)
                        LoadSceneFromPath(_pendingOpenScenePath);
                    _pendingOpenScenePath = null;
                }
                ImGui.SameLine();
                if (ImGui.Button("Open without Saving"))
                {
                    ImGui.CloseCurrentPopup();
                    if (_pendingOpenScenePath != null)
                        LoadSceneFromPath(_pendingOpenScenePath);
                    _pendingOpenScenePath = null;
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    _pendingOpenScenePath = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }

        private void RequestPrefabBack(int count)
        {
            var scene = RoseEngine.SceneManager.GetActiveScene();
            if (scene.isDirty)
            {
                _pendingPrefabBackCount = count;
                _showPrefabBackConfirm = true;
            }
            else
            {
                for (int i = 0; i < count; i++)
                    PrefabEditMode.Exit();
            }
        }

        private void DrawPrefabBackConfirmPopup()
        {
            if (_showPrefabBackConfirm)
            {
                ImGui.OpenPopup("Unsaved Changes##PrefabBackConfirm");
                _showPrefabBackConfirm = false;
            }

            if (ImGui.BeginPopupModal("Unsaved Changes##PrefabBackConfirm", ImGuiWindowFlags.AlwaysAutoResize))
            {
                var prefabName = !string.IsNullOrEmpty(EditorState.EditingPrefabPath)
                    ? System.IO.Path.GetFileNameWithoutExtension(EditorState.EditingPrefabPath)
                    : "Prefab";
                ImGui.Text($"Prefab \"{prefabName}\" has unsaved changes.");
                ImGui.Text("Do you want to save before going back?");
                ImGui.Spacing();

                if (ImGui.Button("Save and Back"))
                {
                    PrefabEditMode.Save();
                    for (int i = 0; i < _pendingPrefabBackCount; i++)
                        PrefabEditMode.Exit();
                    _pendingPrefabBackCount = 0;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Don't Save"))
                {
                    for (int i = 0; i < _pendingPrefabBackCount; i++)
                        PrefabEditMode.Exit();
                    _pendingPrefabBackCount = 0;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    _pendingPrefabBackCount = 0;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }

        private void LoadSceneFromPath(string path)
        {
            SceneSerializer.Load(path);
            UndoSystem.Clear();
            EditorState.UpdateLastScene(Path.GetFullPath(path));
            UpdateWindowTitle();
        }

        // ── Scene management ──

        private void ProcessSceneShortcuts()
        {
            var io = ImGui.GetIO();
            if (io.WantTextInput) return; // 텍스트 입력 중에는 단축키 무시

            bool ctrl = io.KeyCtrl;
            bool shift = io.KeyShift;

            // Play/Stop 토글 (Ctrl+P)
            if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.P))
            {
                if (EditorPlayMode.State == PlayModeState.Edit)
                    EditorPlayMode.EnterPlayMode();
                else
                    EditorPlayMode.StopPlayMode();
                UpdateWindowTitle();
            }

            // Pause/Resume 토글 (Ctrl+Shift+P)
            if (ctrl && shift && ImGui.IsKeyPressed(ImGuiKey.P))
            {
                EditorPlayMode.TogglePause();
                UpdateWindowTitle();
            }

            // Animation Editor 포커스 시 자체 Undo/Redo 처리하므로 전역 스킵 (이중 실행 방지)
            bool animFocused = _animEditor != null && _animEditor.IsWindowFocused;
            if (!animFocused && ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.Z)) UndoSystem.PerformUndo();
            if (!animFocused && ctrl && shift && ImGui.IsKeyPressed(ImGuiKey.Z)) UndoSystem.PerformRedo();
            if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.N)) NewScene();
            if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.O)) OpenScene();
            if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.S)) SaveScene();
            if (ctrl && shift && ImGui.IsKeyPressed(ImGuiKey.S)) SaveSceneAs();

            // Scene/Hierarchy 포커스 여부 — GO 조작 단축키는 이 패널에서만 동작
            bool sceneOrHierarchyFocused =
                (_sceneView != null && _sceneView.IsWindowFocused) ||
                (_hierarchy != null && _hierarchy.IsWindowFocused);

            // Duplicate (Ctrl+D)
            if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.D))
                DuplicateSelected();

            // Delete selected object (Scene View / Hierarchy 포커스 시에만)
            if (!ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.Delete) && sceneOrHierarchyFocused)
                DeleteSelectedGameObject();

            // Copy / Cut / Paste — GameObjects (Hierarchy / Scene View 포커스 시에만)
            if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.C) && sceneOrHierarchyFocused)
                EditorClipboard.CopyGameObjects(cut: false);
            if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.X) && sceneOrHierarchyFocused)
                EditorClipboard.CopyGameObjects(cut: true);
            if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.V) && sceneOrHierarchyFocused)
                EditorClipboard.PasteGameObjects();

            // UI Scale shortcuts
            if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.Equal))       // Ctrl+=  (Zoom In)
                SetUiScale(_uiScale + UiScaleStep);
            if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.Minus))       // Ctrl+-  (Zoom Out)
                SetUiScale(_uiScale - UiScaleStep);
            if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey._0))          // Ctrl+0  (Reset)
                SetUiScale(1.0f);
        }

        private void PushCurrentFont()
        {
            int idx = Array.IndexOf(FontNames, _currentFont);
            if (idx >= 0 && idx < _loadedFonts.Length)
                ImGui.PushFont(_loadedFonts[idx]);
        }

        private void PopCurrentFont()
        {
            int idx = Array.IndexOf(FontNames, _currentFont);
            if (idx >= 0 && idx < _loadedFonts.Length)
                ImGui.PopFont();
        }

        private void SetFont(string fontName)
        {
            int idx = Array.IndexOf(FontNames, fontName);
            if (idx < 0 || idx >= _loadedFonts.Length) return;

            _currentFont = fontName;

            // Persist to editor preferences
            EditorPreferences.EditorFont = fontName;
            EditorPreferences.Save();
        }

        private void RestorePanelStates()
        {
            _hierarchy!.IsOpen = EditorState.PanelHierarchy;
            _inspector!.IsOpen = EditorState.PanelInspector;
            _projectSettings!.IsOpen = EditorState.PanelProjectSettings;
            _sceneEnvironment!.IsOpen = EditorState.PanelSceneEnvironment;
            _console!.IsOpen = EditorState.PanelConsole;
            _gameView!.IsOpen = EditorState.PanelGameView;
            _sceneView!.IsOpen = EditorState.PanelSceneView;
            _project!.IsOpen = EditorState.PanelProject;
            _textureTool!.IsOpen = EditorState.PanelTextureTool;
            _feedback!.IsOpen = EditorState.PanelFeedback;
        }

        private void SyncPanelStatesToEditorState()
        {
            EditorState.PanelHierarchy = _hierarchy?.IsOpen ?? true;
            EditorState.PanelInspector = _inspector?.IsOpen ?? true;
            EditorState.PanelProjectSettings = _projectSettings?.IsOpen ?? false;
            EditorState.PanelSceneEnvironment = _sceneEnvironment?.IsOpen ?? true;
            EditorState.PanelConsole = _console?.IsOpen ?? true;
            EditorState.PanelGameView = _gameView?.IsOpen ?? true;
            EditorState.PanelSceneView = _sceneView?.IsOpen ?? true;
            EditorState.PanelProject = _project?.IsOpen ?? true;
            EditorState.PanelTextureTool = _textureTool?.IsOpen ?? false;
            EditorState.PanelFeedback = _feedback?.IsOpen ?? false;
        }

        private void SetUiScale(float scale)
        {
            _uiScale = Math.Clamp(scale, MinUiScale, MaxUiScale);
            // Round to nearest step to avoid floating-point drift
            _uiScale = MathF.Round(_uiScale / UiScaleStep) * UiScaleStep;
            var io = ImGui.GetIO();
            io.FontGlobalScale = _uiScale;

            // Persist to editor preferences
            EditorPreferences.UiScale = _uiScale;
            EditorPreferences.Save();
        }

        private void DuplicateSelected()
        {
            // Project panel: duplicate asset (only when project panel is focused)
            if (_project != null && _project.IsWindowFocused && _project.HasSelectedAsset)
            {
                _project.DuplicateSelectedAsset();
                return;
            }

            // Scene / Hierarchy: duplicate selected GameObjects (포커스 시에만)
            bool sceneOrHierarchy =
                (_sceneView != null && _sceneView.IsWindowFocused) ||
                (_hierarchy != null && _hierarchy.IsWindowFocused);
            if (!sceneOrHierarchy) return;

            var ids = EditorSelection.SelectedGameObjectIds;
            if (ids.Count == 0) return;

            var newIds = new System.Collections.Generic.List<int>();
            foreach (var id in ids)
            {
                var go = RoseEngine.SceneManager.AllGameObjects
                    .FirstOrDefault(g => !g._isDestroyed && g.GetInstanceID() == id);
                if (go == null) continue;

                var clone = RoseEngine.Object.Instantiate(go);

                // Generate numbered copy name: _01, _02, _03, ...
                string nameBase = go.name;
                var numSuffix = Regex.Match(go.name, @"^(.+)_(\d+)$");
                if (numSuffix.Success)
                    nameBase = numSuffix.Groups[1].Value;

                IEnumerable<RoseEngine.GameObject> siblings;
                if (go.transform.parent != null)
                    siblings = Enumerable.Range(0, go.transform.parent.childCount)
                        .Select(i => go.transform.parent.GetChild(i).gameObject);
                else
                    siblings = RoseEngine.SceneManager.AllGameObjects
                        .Where(g => !g._isDestroyed && g.transform.parent == null);

                int maxNum = 0;
                var namePattern = new Regex(
                    $@"^{Regex.Escape(nameBase)}_(\d+)$");
                foreach (var sib in siblings)
                {
                    var m = namePattern.Match(sib.name);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int num))
                        maxNum = Math.Max(maxNum, num);
                }
                clone.name = $"{nameBase}_{(maxNum + 1):D2}";

                if (go.transform.parent != null)
                    clone.transform.SetParent(go.transform.parent, worldPositionStays: false);

                newIds.Add(clone.GetInstanceID());
            }

            EditorSelection.SetSelection(newIds);
            RoseEngine.SceneManager.GetActiveScene().isDirty = true;
        }

        private static void DeleteSelectedGameObject()
        {
            var ids = EditorSelection.SelectedGameObjectIds;
            if (ids.Count == 0) return;

            var actions = new System.Collections.Generic.List<IUndoAction>();
            for (int i = ids.Count - 1; i >= 0; i--)
            {
                var go = RoseEngine.SceneManager.AllGameObjects
                    .FirstOrDefault(g => !g._isDestroyed && g.GetInstanceID() == ids[i]);
                if (go == null) continue;
                // 프리팹 인스턴스의 자식은 삭제 불가
                if (IsPrefabChildLocked(go)) continue;
                actions.Add(new DeleteGameObjectAction($"Delete {go.name}", go));
                RoseEngine.Object.DestroyImmediate(go);
            }

            if (actions.Count == 1)
                UndoSystem.Record(actions[0]);
            else if (actions.Count > 1)
                UndoSystem.Record(new CompoundUndoAction($"Delete {actions.Count} objects", actions));

            EditorSelection.Clear();
            RoseEngine.SceneManager.GetActiveScene().isDirty = true;
        }

        /// <summary>조상 중 PrefabInstance가 있으면 잠금. 프리팹 루트 자체는 이동 가능, 그 아래는 전부 잠금.</summary>
        private static bool IsPrefabChildLocked(RoseEngine.GameObject go)
            => RoseEngine.PrefabUtility.HasPrefabInstanceAncestor(go);

        private void NewScene()
        {
            if (EditorPlayMode.IsInPlaySession) return;

            var path = NativeFileDialog.SaveFileDialog(
                title: "New Scene",
                defaultName: "NewScene.scene",
                filter: "*.scene");

            if (string.IsNullOrEmpty(path)) return;

            RoseEngine.SceneManager.Clear();
            UndoSystem.Clear();

            var sceneName = Path.GetFileNameWithoutExtension(path);
            var scene = new RoseEngine.Scene
            {
                path = Path.GetFullPath(path),
                name = sceneName,
            };
            RoseEngine.SceneManager.SetActiveScene(scene);

            IronRose.API.EditorScene.CreateDefaultScene();

            SceneSerializer.Save(path);
            EditorState.UpdateLastScene(scene.path);
            UpdateWindowTitle();
            Debug.Log($"[Scene] New scene created: {sceneName}");
        }

        private void OpenScene()
        {
            if (EditorPlayMode.IsInPlaySession) return;

            var path = NativeFileDialog.OpenFileDialog(
                title: "Open Scene",
                filter: "*.scene");

            if (string.IsNullOrEmpty(path)) return;

            LoadSceneFromPath(path);
        }

        private void SaveScene()
        {
            if (EditorPlayMode.IsInPlaySession) return;

            // Prefab Edit Mode에서는 프리팹 저장
            if (EditorState.IsEditingPrefab)
            {
                PrefabEditMode.Save();
                UpdateWindowTitle();
                return;
            }

            var scene = RoseEngine.SceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(scene.path))
            {
                SceneSerializer.Save(scene.path);
                UpdateWindowTitle();
            }
            else
            {
                SaveSceneAs();
            }
        }

        private void SaveSceneAs()
        {
            if (EditorPlayMode.IsInPlaySession) return;

            var scene = RoseEngine.SceneManager.GetActiveScene();
            var defaultName = (scene.name ?? "Untitled") + ".scene";

            var path = NativeFileDialog.SaveFileDialog(
                title: "Save Scene As",
                defaultName: defaultName,
                filter: "*.scene");

            if (string.IsNullOrEmpty(path)) return;

            scene.path = Path.GetFullPath(path);
            scene.name = Path.GetFileNameWithoutExtension(path);

            SceneSerializer.Save(path);
            EditorState.UpdateLastScene(scene.path);
            UpdateWindowTitle();
        }

        // ── Asset drag-drop onto Scene View ──

        private void HandleSceneViewAssetDrop()
        {
            if (_sceneView == null || _editorCamera == null) return;

            var assetPath = _sceneView.ConsumePendingDropAssetPath(out var screenPos);
            if (assetPath == null) return;

            // Material drop → 별도 처리
            if (ImGuiSceneViewPanel.IsMaterialAsset(assetPath))
            {
                HandleMaterialDrop(assetPath);
                return;
            }

            // 스크린 좌표 → Scene View 패널 로컬 좌표
            var min = _sceneView.ImageScreenMin;
            var max = _sceneView.ImageScreenMax;
            float localX = screenPos.X - min.X;
            float localY = screenPos.Y - min.Y;
            float panelW = max.X - min.X;
            float panelH = max.Y - min.Y;

            if (panelW <= 0 || panelH <= 0) return;

            // 레이-그라운드 교차로 월드 좌표 계산
            var worldPos = ScreenToWorldOnGroundPlane(localX, localY, panelW, panelH);

            // 에셋에서 GameObject 생성
            var go = AssetSpawner.SpawnFromAsset(assetPath, worldPos);
            if (go == null) return;

            // Prefab Edit Mode이면 root GO 자식으로 배치
            if (EditorState.IsEditingPrefab)
            {
                RoseEngine.GameObject? prefabRoot = null;
                foreach (var g in RoseEngine.SceneManager.AllGameObjects)
                {
                    if (!g._isDestroyed && !g._isEditorInternal && g.transform.parent == null)
                    { prefabRoot = g; break; }
                }
                if (prefabRoot is not null && !ReferenceEquals(go, prefabRoot))
                {
                    var localPos = go.transform.localPosition;
                    go.transform.SetParent(prefabRoot.transform, false);
                    go.transform.localPosition = localPos;
                }
            }

            // Undo 기록
            UndoSystem.Record(new SpawnGameObjectAction(
                $"Spawn {go.name}", assetPath, worldPos, go.GetInstanceID()));

            // 생성된 오브젝트 선택
            EditorSelection.SelectGameObject(go);

            // 씬 dirty 표시
            RoseEngine.SceneManager.GetActiveScene().isDirty = true;

            Debug.Log($"[DragDrop] Spawned '{go.name}' at ({worldPos.x:F1}, {worldPos.y:F1}, {worldPos.z:F1})");
        }

        // ── Material drag-hover preview ──

        private void HandleMaterialDragHover()
        {
            if (_sceneView == null || _sceneRenderer == null) return;

            if (!_sceneView.IsMaterialDragHovering)
            {
                // 호버 종료 → 정리
                if (_materialHoverObjectId != 0)
                    ClearMaterialHoverState();
                return;
            }

            var path = _sceneView.HoveringMaterialPath;
            if (path == null) return;

            // Material 경로 변경 시 로드
            if (path != _lastMaterialHoverPath)
            {
                _lastMaterialHoverPath = path;
                var db = RoseEngine.Resources.GetAssetDatabase();
                _materialHoverPreview = db?.Load<RoseEngine.Material>(path);
            }

            // 화면좌표 → pick 좌표 변환
            var min = _sceneView.ImageScreenMin;
            var max = _sceneView.ImageScreenMax;
            float panelW = max.X - min.X;
            float panelH = max.Y - min.Y;
            if (panelW <= 0 || panelH <= 0) return;

            var screenPos = _sceneView.HoveringScreenPos;
            float relX = (screenPos.X - min.X) / panelW;
            float relY = (screenPos.Y - min.Y) / panelH;
            if (relX < 0 || relX > 1 || relY < 0 || relY > 1) return;

            var fb = _sceneRenderer.Framebuffer;
            if (fb == null) return;

            uint px = (uint)(relX * fb.Width);
            uint py = (uint)(relY * fb.Height);

            _sceneRenderer.RequestPick(px, py, hitId =>
            {
                if (hitId == 0)
                {
                    _materialHoverObjectId = 0;
                    _sceneRenderer.ClearMaterialOverride();
                    return;
                }

                // MeshRenderer가 있는 오브젝트인지 확인
                var go = RoseEngine.SceneManager.AllGameObjects
                    .FirstOrDefault(g => !g._isDestroyed && g.GetInstanceID() == (int)hitId);
                if (go == null || go.GetComponent<RoseEngine.MeshRenderer>() == null)
                {
                    _materialHoverObjectId = 0;
                    _sceneRenderer.ClearMaterialOverride();
                    return;
                }

                _materialHoverObjectId = (int)hitId;
                _sceneRenderer.SetMaterialOverride(_materialHoverObjectId, _materialHoverPreview);
            });
        }

        private void HandleMaterialDrop(string assetPath)
        {
            if (_sceneRenderer == null) return;

            // hover 중이던 오브젝트에 Material 할당
            if (_materialHoverObjectId == 0) return;

            var go = RoseEngine.SceneManager.AllGameObjects
                .FirstOrDefault(g => !g._isDestroyed && g.GetInstanceID() == _materialHoverObjectId);
            var meshRenderer = go?.GetComponent<RoseEngine.MeshRenderer>();
            if (meshRenderer == null)
            {
                ClearMaterialHoverState();
                return;
            }

            // Material 로드
            var db = RoseEngine.Resources.GetAssetDatabase();
            var newMat = db?.Load<RoseEngine.Material>(assetPath);
            if (newMat == null)
            {
                ClearMaterialHoverState();
                return;
            }

            // Undo 기록
            var oldMat = meshRenderer.material;
            meshRenderer.material = newMat;

            UndoSystem.Record(new SetPropertyAction(
                "Change Material", _materialHoverObjectId,
                typeof(RoseEngine.MeshRenderer).Name, "material",
                oldMat, newMat));

            // 씬 dirty 표시
            RoseEngine.SceneManager.GetActiveScene().isDirty = true;

            ClearMaterialHoverState();
        }

        private void ClearMaterialHoverState()
        {
            _materialHoverObjectId = 0;
            _materialHoverPreview = null;
            _lastMaterialHoverPath = null;
            _sceneRenderer?.ClearMaterialOverride();
        }

        /// <summary>
        /// MMB 클릭 시 GPU pick → 히트한 오브젝트 방향으로 카메라를 이동.
        /// </summary>
        private void HandleMiddleClickFocus(Framebuffer fb)
        {
            var min = _sceneView!.ImageScreenMin;
            var max = _sceneView.ImageScreenMax;
            float panelW = max.X - min.X;
            float panelH = max.Y - min.Y;
            if (panelW <= 0 || panelH <= 0) return;

            float relX = (_mmbDownPos.X - min.X) / panelW;
            float relY = (_mmbDownPos.Y - min.Y) / panelH;
            if (relX < 0 || relX > 1 || relY < 0 || relY > 1) return;

            uint px = (uint)(relX * fb.Width);
            uint py = (uint)(relY * fb.Height);
            float localX = _mmbDownPos.X - min.X;
            float localY = _mmbDownPos.Y - min.Y;

            _sceneRenderer!.RequestPick(px, py, pickedId =>
            {
                if (pickedId == 0) return;

                RoseEngine.GameObject? go = null;
                foreach (var g in RoseEngine.SceneManager.AllGameObjects)
                {
                    if (!g._isDestroyed && g.GetInstanceID() == (int)pickedId)
                    { go = g; break; }
                }
                if (go == null) return;

                var worldPoint = ScreenToWorldOnObjectPlane(
                    localX, localY, panelW, panelH, go.transform.position);
                _editorCamera!.FocusOnPoint(worldPoint);
            });
        }

        /// <summary>
        /// 스크린 좌표에서 레이를 쏘아 오브젝트 중심을 지나는 카메라 수직 평면과의 교차점을 구합니다.
        /// </summary>
        private RoseEngine.Vector3 ScreenToWorldOnObjectPlane(
            float localX, float localY, float panelW, float panelH,
            RoseEngine.Vector3 objectCenter)
        {
            var fb = _sceneRenderer?.Framebuffer;
            float aspect = (fb != null && fb.Height > 0)
                ? (float)fb.Width / fb.Height
                : panelW / panelH;

            float ndcX = (localX / panelW) * 2f - 1f;
            float ndcY = 1f - (localY / panelH) * 2f;

            float tanHalfFov = MathF.Tan(_editorCamera!.FieldOfView * 0.5f * RoseEngine.Mathf.Deg2Rad);
            float viewX = ndcX * aspect * tanHalfFov;
            float viewY = ndcY * tanHalfFov;

            var forward = _editorCamera.Forward;
            var right = _editorCamera.Right;
            var up = _editorCamera.Up;

            var rayDir = (right * viewX + up * viewY + forward).normalized;
            var rayOrigin = _editorCamera.Position;

            // Intersect ray with plane through objectCenter, perpendicular to camera forward
            var toCenter = objectCenter - rayOrigin;
            float denom = RoseEngine.Vector3.Dot(rayDir, forward);
            if (MathF.Abs(denom) > 1e-6f)
            {
                float t = RoseEngine.Vector3.Dot(toCenter, forward) / denom;
                if (t > 0f)
                    return rayOrigin + rayDir * t;
            }

            return objectCenter;
        }

        /// <summary>
        /// 패널 로컬 좌표에서 레이를 쏘아 Y=0 평면과의 교차점을 구합니다.
        /// 교차하지 않으면 카메라 전방 10 유닛 + Y=0 위치로 폴백합니다.
        /// </summary>
        private RoseEngine.Vector3 ScreenToWorldOnGroundPlane(
            float localX, float localY, float panelW, float panelH)
        {
            var fb = _sceneRenderer?.Framebuffer;
            float aspect = (fb != null && fb.Height > 0)
                ? (float)fb.Width / fb.Height
                : panelW / panelH;

            // Screen → NDC (TransformGizmo.ScreenToRay와 동일한 수학)
            float ndcX = (localX / panelW) * 2f - 1f;
            float ndcY = 1f - (localY / panelH) * 2f;

            float tanHalfFov = MathF.Tan(_editorCamera!.FieldOfView * 0.5f * RoseEngine.Mathf.Deg2Rad);
            float viewX = ndcX * aspect * tanHalfFov;
            float viewY = ndcY * tanHalfFov;

            var forward = _editorCamera.Forward;
            var right = _editorCamera.Right;
            var up = _editorCamera.Up;

            var rayDir = (right * viewX + up * viewY + forward).normalized;
            var rayOrigin = _editorCamera.Position;

            // Y=0 평면과 교차: rayOrigin.y + t * rayDir.y = 0
            if (MathF.Abs(rayDir.y) > 1e-6f)
            {
                float t = -rayOrigin.y / rayDir.y;
                if (t > 0f)
                {
                    t = MathF.Min(t, 1000f);
                    return rayOrigin + rayDir * t;
                }
            }

            // 폴백: 카메라 전방 10 유닛, Y=0
            var fallback = rayOrigin + forward * 10f;
            return new RoseEngine.Vector3(fallback.x, 0f, fallback.z);
        }

        // ── Scene View input & rendering ──

        /// <summary>Canvas Edit Mode 전용 입력 처리.</summary>
        private void UpdateCanvasEditInput(float deltaTime)
        {
            if (_sceneView == null) return;

            // 기즈모 업데이트 (비활성 GO는 기즈모 스킵)
            var selectedGo = EditorSelection.SelectedGameObject;
            bool isActiveGo = selectedGo != null && selectedGo.activeInHierarchy;
            bool hasRectTransform = isActiveGo && selectedGo!.GetComponent<RoseEngine.RectTransform>() != null;
            bool isRectTool = _sceneView.SelectedTool == TransformTool.Rect;
            bool useUI2DGizmo = hasRectTransform
                && (_sceneView.SelectedTool == TransformTool.Translate
                    || _sceneView.SelectedTool == TransformTool.Rotate
                    || _sceneView.SelectedTool == TransformTool.Scale);

            if (isRectTool && hasRectTransform && _rectGizmoEditor != null)
                _rectGizmoEditor.Update(_sceneView);
            else if (useUI2DGizmo && _uiTransformGizmo != null)
                _uiTransformGizmo.Update(_sceneView);

            if (!_sceneView.IsImageHovered) return;

            var io = ImGui.GetIO();

            // ── 마우스 휠: 줌 ──
            if (MathF.Abs(io.MouseWheel) > 0.001f)
            {
                float oldZoom = CanvasEditMode.ViewZoom;
                float zoomFactor = io.MouseWheel > 0 ? 1.15f : 1f / 1.15f;
                float newZoom = CanvasEditMode.ClampZoom(oldZoom * zoomFactor);

                // 포인트 줌: 마우스 위치 기준
                var viewCenter = new Vector2(
                    (_sceneView.ImageScreenMin.X + _sceneView.ImageScreenMax.X) * 0.5f,
                    (_sceneView.ImageScreenMin.Y + _sceneView.ImageScreenMax.Y) * 0.5f);
                var mouseOffset = io.MousePos - viewCenter - CanvasEditMode.ViewOffset;
                CanvasEditMode.ViewOffset -= mouseOffset * (newZoom / oldZoom - 1f);

                CanvasEditMode.ViewZoom = newZoom;
            }

            // ── MMB 드래그: 패닝 ──
            if (io.MouseDown[2])
            {
                CanvasEditMode.ViewOffset += io.MouseDelta;
            }

            // ── F 키: 포커스 / 전체 뷰 리셋 ──
            bool fKeyPressed = ImGui.IsKeyPressed(ImGuiKey.F)
                && !ImGui.IsKeyDown(ImGuiKey.ModCtrl)
                && !ImGui.IsKeyDown(ImGuiKey.ModShift)
                && !ImGui.IsKeyDown(ImGuiKey.ModAlt);

            if (fKeyPressed)
                CanvasEditMode.ResetView();

            // ── LMB 클릭: UI 요소 선택 ──
            bool gizmoInteracting = (_rectGizmoEditor?.IsDragging ?? false)
                || (_uiTransformGizmo?.IsDragging ?? false);

            if (io.MouseClicked[0] && !io.KeyAlt && !io.MouseDown[1] && !io.MouseDown[2] && !gizmoInteracting)
            {
                var imgSize = _sceneView.ImageScreenMax - _sceneView.ImageScreenMin;
                var (canvasMin, canvasMax) = _sceneView.CalculateCanvasRect(imgSize.X, imgSize.Y);
                float canvasW = canvasMax.X - canvasMin.X;
                float canvasH = canvasMax.Y - canvasMin.Y;

                var uiHit = RoseEngine.CanvasRenderer.HitTest(
                    io.MousePos.X, io.MousePos.Y, canvasMin.X, canvasMin.Y, canvasW, canvasH);
                if (uiHit != null)
                {
                    int id = uiHit.GetInstanceID();
                    if (io.KeyCtrl)
                        EditorSelection.ToggleSelect(id);
                    else
                        EditorSelection.Select(id);
                }
                else if (!io.KeyCtrl)
                {
                    EditorSelection.Clear();
                }
            }
        }

        private void UpdateSceneViewInput(float deltaTime)
        {
            if (_sceneView == null || _editorCamera == null) return;

            _sceneView.ProcessShortcuts();

            // ── Alt+Shift+A → Active 토글 (Scene View 공통, 멀티셀렉트 지원) ──
            if (ImGui.GetIO().KeyAlt && ImGui.GetIO().KeyShift && ImGui.IsKeyPressed(ImGuiKey.A))
                {
                    var selectedIds = EditorSelection.SelectedGameObjectIds;
                    if (selectedIds.Count > 0)
                    {
                        var firstGo = UndoUtility.FindGameObjectById(selectedIds.First());
                        if (firstGo != null)
                        {
                            bool newActive = !firstGo.activeSelf;
                            var actions = new List<IUndoAction>();

                            foreach (var id in selectedIds)
                            {
                                var go = UndoUtility.FindGameObjectById(id);
                                if (go == null) continue;
                                bool oldActive = go.activeSelf;
                                go.SetActive(newActive);
                                actions.Add(new SetActiveAction(
                                    $"Toggle Active {go.name}", id, oldActive, newActive));
                            }

                            if (actions.Count == 1)
                                UndoSystem.Record(actions[0]);
                            else if (actions.Count > 1)
                                UndoSystem.Record(new CompoundUndoAction("Toggle Active", actions));

                            RoseEngine.SceneManager.GetActiveScene().isDirty = true;
                        }
                    }
                }

            // Canvas Edit Mode 카메라 상태 관리
            if (EditorState.IsEditingCanvas)
            {
                // 최초 진입 시 카메라 상태 저장
                if (EditorState.SavedCanvasCameraPosition == null)
                {
                    EditorState.SavedCanvasCameraPosition = _editorCamera.Position;
                    EditorState.SavedCanvasCameraRotation = _editorCamera.Rotation;
                    EditorState.SavedCanvasCameraPivot = _editorCamera.Pivot;
                }
                UpdateCanvasEditInput(deltaTime);
                return;
            }

            // Canvas Edit Mode 퇴출 후 카메라 복원
            if (EditorState.SavedCanvasCameraPosition.HasValue)
            {
                _editorCamera.Position = EditorState.SavedCanvasCameraPosition.Value;
                _editorCamera.Rotation = EditorState.SavedCanvasCameraRotation!.Value;
                _editorCamera.Pivot = EditorState.SavedCanvasCameraPivot!.Value;
                EditorState.SavedCanvasCameraPosition = null;
                EditorState.SavedCanvasCameraRotation = null;
                EditorState.SavedCanvasCameraPivot = null;
            }

            // Auto-disable Edit Collider mode when selection is cleared or has no collider
            if (EditorState.IsEditingCollider)
            {
                var selGo = EditorSelection.SelectedGameObject;
                if (selGo == null || selGo.GetComponent<RoseEngine.Collider>() == null)
                    EditorState.IsEditingCollider = false;
            }

            // Determine if selected object is a UI element (has RectTransform)
            var selectedGo = EditorSelection.SelectedGameObject;
            bool isActiveGo = selectedGo != null && selectedGo.activeInHierarchy;
            bool hasRectTransform = isActiveGo
                && selectedGo!.GetComponent<RoseEngine.RectTransform>() != null;
            bool isRectTool = _sceneView.SelectedTool == TransformTool.Rect;
            bool useUI2DGizmo = hasRectTransform
                && (_sceneView.SelectedTool == TransformTool.Translate
                    || _sceneView.SelectedTool == TransformTool.Rotate
                    || _sceneView.SelectedTool == TransformTool.Scale);

            // Update gizmo (needs to run even when dragging outside the panel)
            var fb = _sceneRenderer?.Framebuffer;
            if (fb != null)
            {
                if (isRectTool && hasRectTransform && _rectGizmoEditor != null)
                    _rectGizmoEditor.Update(_sceneView);
                else if (useUI2DGizmo && _uiTransformGizmo != null)
                    _uiTransformGizmo.Update(_sceneView);
                else if (EditorState.IsEditingCollider && _colliderEditor != null)
                    _colliderEditor.Update(_editorCamera, _sceneView, fb.Width, fb.Height);
                else if (_gizmo != null)
                    _gizmo.Update(_editorCamera, _sceneView, fb.Width, fb.Height);
            }

            // Ctrl+Shift+F: Align with View (set selected object's transform to camera)
            if (ImGui.IsKeyPressed(ImGuiKey.F)
                && ImGui.IsKeyDown(ImGuiKey.ModCtrl)
                && ImGui.IsKeyDown(ImGuiKey.ModShift)
                && !ImGui.IsKeyDown(ImGuiKey.ModAlt))
            {
                var go = EditorSelection.SelectedGameObject;
                if (go != null)
                {
                    var oldLocalPos = go.transform.localPosition;
                    var oldLocalRot = go.transform.localRotation;
                    var oldScale = go.transform.localScale;

                    go.transform.position = _editorCamera.Position;
                    go.transform.rotation = _editorCamera.Rotation;

                    UndoSystem.Record(new SetTransformAction(
                        $"Align with View: {go.name}", go.GetInstanceID(),
                        oldLocalPos, oldLocalRot, oldScale,
                        go.transform.localPosition, go.transform.localRotation, oldScale));
                }
            }

            // F key (no modifiers): Focus on selected object
            bool fKeyPressed = ImGui.IsKeyPressed(ImGuiKey.F)
                && !ImGui.IsKeyDown(ImGuiKey.ModCtrl)
                && !ImGui.IsKeyDown(ImGuiKey.ModShift)
                && !ImGui.IsKeyDown(ImGuiKey.ModAlt);
            bool focusRequested = (_sceneView.IsWindowFocused || _sceneView.IsImageHovered) && fKeyPressed;

            var io = ImGui.GetIO();

            // ── MMB click-to-focus tracking (runs even when mouse leaves scene view during hold) ──
            if (io.MouseClicked[2] && _sceneView.IsImageHovered)
            {
                _mmbTracking = true;
                _mmbDownPos = io.MousePos;
                _mmbIsDragging = false;
            }
            if (_mmbTracking && io.MouseDown[2])
            {
                var delta = io.MousePos - _mmbDownPos;
                if (MathF.Abs(delta.X) > 2f || MathF.Abs(delta.Y) > 2f)
                    _mmbIsDragging = true;
            }
            if (_mmbTracking && io.MouseReleased[2])
            {
                if (!_mmbIsDragging && fb != null)
                    HandleMiddleClickFocus(fb);
                _mmbTracking = false;
            }

            // ── Camera-active lock ──
            // Start camera tracking when a camera button is first pressed inside scene view
            if (!_cameraActive && _sceneView.IsImageHovered)
            {
                bool gizmoInteracting = _gizmo?.IsInteracting ?? false;
                if (!gizmoInteracting && (io.MouseClicked[1] || (io.MouseClicked[0] && io.KeyAlt)))
                    _cameraActive = true;
                if (_mmbTracking && _mmbIsDragging && !gizmoInteracting)
                    _cameraActive = true;
            }
            // Release camera lock when the driving button is released
            if (_cameraActive)
            {
                bool anyHeld = io.MouseDown[1] || (io.KeyAlt && io.MouseDown[0]) || (io.MouseDown[2] && _mmbIsDragging);
                if (!anyHeld)
                    _cameraActive = false;
            }

            // Process camera input when hovered OR when camera is already active (dragging outside panel)
            if (!_sceneView.IsImageHovered && !_cameraActive)
            {
                if (focusRequested)
                {
                    RoseEngine.EditorDebug.Log("[SceneView:F] Dispatching focus (not hovered path)");
                    _editorCamera.Update(0, new SceneViewInputState { FocusRequested = true });
                }
                return;
            }

            // Don't process camera input while gizmo is active (but camera lock overrides gizmo hover)
            bool gizmoActive = !_cameraActive && (_gizmo?.IsInteracting ?? false);

            var input = new SceneViewInputState
            {
                IsFlyMode = !gizmoActive && io.MouseDown[1], // RMB
                IsOrbitMode = !gizmoActive && io.KeyAlt && io.MouseDown[0], // Alt + LMB
                IsPanMode = !gizmoActive && io.MouseDown[2] && _mmbIsDragging, // MMB drag

                MoveForward = ImGui.IsKeyDown(ImGuiKey.W),
                MoveBackward = ImGui.IsKeyDown(ImGuiKey.S),
                MoveLeft = ImGui.IsKeyDown(ImGuiKey.A),
                MoveRight = ImGui.IsKeyDown(ImGuiKey.D),
                MoveUp = ImGui.IsKeyDown(ImGuiKey.E),
                MoveDown = ImGui.IsKeyDown(ImGuiKey.Q),
                IsSprintHeld = io.KeyShift,

                MouseDelta = new RoseEngine.Vector2(io.MouseDelta.X, io.MouseDelta.Y),
                ScrollDelta = io.MouseWheel,

                FocusRequested = focusRequested,
            };

            _editorCamera.Update(deltaTime, input);

            // ── Mouse cursor wrapping at screen edges (Unity-style) ──
            if (_cameraActive && _inputHandler != null)
            {
                var mouseLocal = _inputHandler.GetMouseWindowPosition();
                float winW = _window.Size.X;
                float winH = _window.Size.Y;
                const float edgeMargin = 1f;
                const float warpOffset = 2f;

                float newX = mouseLocal.X;
                float newY = mouseLocal.Y;
                bool needsWarp = false;

                if (mouseLocal.X <= edgeMargin)
                {
                    newX = winW - warpOffset - edgeMargin;
                    needsWarp = true;
                }
                else if (mouseLocal.X >= winW - edgeMargin - 1f)
                {
                    newX = edgeMargin + warpOffset;
                    needsWarp = true;
                }

                if (mouseLocal.Y <= edgeMargin)
                {
                    newY = winH - warpOffset - edgeMargin;
                    needsWarp = true;
                }
                else if (mouseLocal.Y >= winH - edgeMargin - 1f)
                {
                    newY = edgeMargin + warpOffset;
                    needsWarp = true;
                }

                if (needsWarp)
                {
                    var newLocalPos = new System.Numerics.Vector2(newX, newY);
                    _inputHandler.WarpMouse(newLocalPos);

                    // Update ImGui's MousePos so next frame's delta is correct
                    // (GLFW's SetCursorPos does NOT fire the callback)
                    if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
                        io.MousePos = new System.Numerics.Vector2(
                            _window.Position.X + newX, _window.Position.Y + newY);
                    else
                        io.MousePos = newLocalPos;
                }
            }

            // ── Rectangle selection / click-to-select ──
            bool colliderEditing = EditorState.IsEditingCollider;

            bool rectEditing = (isRectTool && hasRectTransform && (_rectGizmoEditor?.IsDragging ?? false))
                || (useUI2DGizmo && (_uiTransformGizmo?.IsDragging ?? false));

            // LMB click: start tracking for potential rect drag
            if (io.MouseClicked[0] && !io.KeyAlt && !io.MouseDown[1] && !io.MouseDown[2]
                && !gizmoActive && !colliderEditing && !rectEditing && _sceneView.IsImageHovered)
            {
                _rectSelection!.BeginTracking(io.MousePos);
            }

            // Update drag while LMB held
            if (_rectSelection!.IsTracking && io.MouseDown[0])
            {
                _rectSelection.UpdateDrag(io.MousePos);
            }

            // Cancel if Alt pressed mid-tracking (entering orbit mode)
            if (_rectSelection.IsTracking && io.KeyAlt)
            {
                _rectSelection.Cancel();
            }

            // LMB released: rect-select or fall back to click-select
            if (_rectSelection.IsTracking && io.MouseReleased[0])
            {
                if (_rectSelection.IsActive)
                {
                    _rectSelection.EndTracking(
                        _editorCamera!, _sceneView!, fb?.Width ?? 1, fb?.Height ?? 1,
                        io.KeyCtrl, io.KeyShift);
                }
                else
                {
                    // Click (no drag): UI hit-test first, then GPU pick
                    _rectSelection.Cancel();

                    var min = _sceneView.ImageScreenMin;
                    var max = _sceneView.ImageScreenMax;
                    var mouse = io.MousePos;
                    if (mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y)
                    {
                        float imgW = max.X - min.X;
                        float imgH = max.Y - min.Y;
                        bool ctrlHeld = io.KeyCtrl;

                        // UI overlay hit-test (rendered on top of 3D scene)
                        var uiHit = _sceneView.ShowUI
                            ? RoseEngine.CanvasRenderer.HitTest(mouse.X, mouse.Y, min.X, min.Y, imgW, imgH)
                            : null;

                        if (uiHit != null)
                        {
                            int id = uiHit.GetInstanceID();
                            if (ctrlHeld)
                                EditorSelection.ToggleSelect(id);
                            else
                                EditorSelection.Select(id);
                        }
                        else if (fb != null)
                        {
                            float relX = (mouse.X - min.X) / imgW;
                            float relY = (mouse.Y - min.Y) / imgH;
                            uint px = (uint)(relX * fb.Width);
                            uint py = (uint)(relY * fb.Height);
                            _sceneRenderer!.RequestPick(px, py, pickedId =>
                            {
                                if (pickedId == 0)
                                {
                                    if (!ctrlHeld) EditorSelection.Clear();
                                }
                                else
                                {
                                    if (ctrlHeld)
                                        EditorSelection.ToggleSelect((int)pickedId);
                                    else
                                        EditorSelection.Select((int)pickedId);
                                }
                            });
                        }
                    }
                }
            }

            // Draw selection rectangle overlay
            _rectSelection.DrawOverlay();
        }

        /// <summary>
        /// SceneViewPanel.Draw() 내부에서 호출되는 2D 기즈모 오버레이 콜백.
        /// Window DrawList 컨텍스트에서 실행되므로 Multi-Viewport에서도 정상 동작.
        /// </summary>
        private void DrawSceneView2DOverlays()
        {
            if (_sceneView == null) return;

            var selGo = EditorSelection.SelectedGameObject;
            if (selGo == null || !selGo.activeInHierarchy) return;

            bool hasRT = selGo.GetComponent<RoseEngine.RectTransform>() != null;
            var tool = _sceneView.SelectedTool;

            if (tool == TransformTool.Rect && hasRT && _rectGizmoEditor != null)
                _rectGizmoEditor.DrawOverlay(_sceneView);
            else if ((tool == TransformTool.Translate || tool == TransformTool.Rotate || tool == TransformTool.Scale) && hasRT && _uiTransformGizmo != null)
                _uiTransformGizmo.DrawOverlay(_sceneView);
        }

        /// <summary>
        /// Scene View 렌더링. EngineCore에서 매 프레임 호출.
        /// </summary>
        public void RenderSceneView(CommandList cl)
        {
            if (!IsVisible || _sceneRenderer == null || _editorCamera == null || _sceneView == null)
                return;

            // Canvas Edit Mode: 3D 씬 렌더링 불필요 (ImGui DrawList 기반 2D 렌더링)
            if (EditorState.IsEditingCanvas)
                return;

            var mode = _sceneView.SelectedRenderMode;

            if (mode == SceneViewRenderMode.Rendered)
            {
                // WYSIWYG는 EngineCore에서 RenderSystem으로 별도 처리
                // 여기서는 오버레이만 (그리드 + 아웃라인)
                var fb = _sceneRenderer.Framebuffer;
                if (fb != null)
                {
                    var selIds = EditorSelection.Count > 0 ? EditorSelection.SelectedGameObjectIds : null;
                    _sceneRenderer.RenderOverlays(cl, fb, _editorCamera, selIds);
                }
            }
            else
            {
                // Wireframe / MatCap / DiffuseOnly
                TextureView? matcapTex = null;
                if (mode == SceneViewRenderMode.MatCap)
                    matcapTex = EditorAssets.GetMatCapTextureView(_sceneView.SelectedMatCapIndex);

                var selIds2 = EditorSelection.Count > 0 ? EditorSelection.SelectedGameObjectIds : null;
                _sceneRenderer.Render(cl, _editorCamera, mode, matcapTex, selIds2);
            }

            // Hide 3D gizmo when 2D UI gizmo is active (overlay drawn in UpdateSceneViewInput)
            var renderSelGo = EditorSelection.SelectedGameObject;
            bool renderHasRT = renderSelGo?.GetComponent<RoseEngine.RectTransform>() != null;
            bool uiGizmoActive = renderHasRT
                && (_sceneView.SelectedTool == TransformTool.Rect
                    || _sceneView.SelectedTool == TransformTool.Translate
                    || _sceneView.SelectedTool == TransformTool.Rotate
                    || _sceneView.SelectedTool == TransformTool.Scale);

            // Gizmo (always on top, after all other scene rendering)
            if (_sceneView.ShowGizmos)
            {
                if (!EditorState.IsEditingCollider && !uiGizmoActive
                    && _gizmo != null && _sceneRenderer.Framebuffer != null)
                {
                    _gizmo.Render(cl, _editorCamera, _sceneRenderer,
                        _sceneView.SelectedTool, _sceneView.SelectedSpace,
                        _sceneRenderer.Framebuffer.Width, _sceneRenderer.Framebuffer.Height);
                }

                // Component gizmo callbacks (wireframe, always on top)
                if (_gizmoRenderer != null && _sceneRenderer.Framebuffer != null)
                {
                    _gizmoRenderer.BeginFrame();
                    GizmoCallbackRunner.DrawAllGizmos();

                    // Draw collider edit handles (within Gizmos drawing context)
                    if (EditorState.IsEditingCollider && _colliderEditor != null && _editorCamera != null)
                    {
                        RoseEngine.Gizmos.IsDrawing = true;
                        _colliderEditor.DrawHandles(_editorCamera,
                            _sceneRenderer.Framebuffer.Width, _sceneRenderer.Framebuffer.Height);
                        RoseEngine.Gizmos.IsDrawing = false;
                    }

                    _gizmoRenderer.Render(cl, _editorCamera, _sceneRenderer,
                        _sceneRenderer.Framebuffer.Width, _sceneRenderer.Framebuffer.Height);
                }
            }

            // Execute pending pick after gizmo geometry is collected (gizmo lines render on top)
            if (_sceneRenderer.Framebuffer != null)
            {
                float aspect = (float)_sceneRenderer.Framebuffer.Width / _sceneRenderer.Framebuffer.Height;
                _sceneRenderer.ExecutePendingPick(_editorCamera, aspect, _gizmoRenderer);
            }
        }

        // ================================================================
        // Play Mode Toolbar
        // ================================================================

        private void DrawPlayModeToolbar()
        {
            var state = EditorPlayMode.State;
            bool isInPlaySession = EditorPlayMode.IsInPlaySession;

            float buttonW = 28f;
            float buttonH = 20f;
            float spacing = 4f;
            float totalWidth = buttonW * 2 + spacing;

            // 메뉴바 중앙에 버튼 배치
            float menuBarWidth = ImGui.GetWindowWidth();
            float currentX = ImGui.GetCursorPosX();
            float centerX = (menuBarWidth - totalWidth) * 0.5f;
            if (centerX > currentX)
                ImGui.SetCursorPosX(centerX);

            // ── Play 버튼 ──
            if (isInPlaySession)
                PushActiveButtonStyle();

            if (ImGui.Button(">", new Vector2(buttonW, buttonH)))
            {
                if (state == PlayModeState.Edit)
                    EditorPlayMode.EnterPlayMode();
                else
                    EditorPlayMode.StopPlayMode();
                UpdateWindowTitle();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(isInPlaySession ? "Stop (Ctrl+P)" : "Play (Ctrl+P)");

            if (isInPlaySession)
                ImGui.PopStyleColor(3);

            ImGui.SameLine(0, spacing);

            // ── Pause 버튼 ──
            bool isPaused = state == PlayModeState.Paused;
            bool canPause = isInPlaySession;

            if (isPaused)
                PushActiveButtonStyle();

            if (!canPause)
                ImGui.BeginDisabled();

            if (ImGui.Button("||", new Vector2(buttonW, buttonH)))
            {
                EditorPlayMode.TogglePause();
                UpdateWindowTitle();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Pause (Ctrl+Shift+P)");

            if (!canPause)
                ImGui.EndDisabled();

            if (isPaused)
                ImGui.PopStyleColor(3);

        }

        private static void PushActiveButtonStyle()
        {
            var accent   = new Vector4(0.600f, 0.380f, 0.350f, 1f);
            var accentLt = new Vector4(0.700f, 0.480f, 0.450f, 1f);
            var accentDk = new Vector4(0.480f, 0.280f, 0.260f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Button, accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, accentLt);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, accentDk);
        }

        private void UpdateWindowTitle()
        {
            var scene = RoseEngine.SceneManager.GetActiveScene();
            var dirty = scene.isDirty ? " *" : "";
            var playState = EditorPlayMode.State switch
            {
                PlayModeState.Playing => " [PLAYING]",
                PlayModeState.Paused => " [PAUSED]",
                _ => ""
            };
            var projectName = !string.IsNullOrEmpty(ProjectContext.ProjectName)
                ? $" [{ProjectContext.ProjectName}]"
                : "";
            _window.Title = $"IronRose Editor{projectName} \u2014 {scene.name}{dirty}{playState}";
        }

        private void DrawPropertyWindows()
        {
            // 대기 중인 요청 소비 → 새 창 생성
            var newWindows = ImGuiPropertyWindow.ConsumePendingRequests(_device, _renderer!);
            if (newWindows.Count > 0)
                _propertyWindows.AddRange(newWindows);

            // 그리기 + 닫힌 창 정리
            for (int i = _propertyWindows.Count - 1; i >= 0; i--)
            {
                _propertyWindows[i].Draw();
                if (!_propertyWindows[i].IsOpen)
                {
                    _propertyWindows[i].Dispose();
                    _propertyWindows.RemoveAt(i);
                }
            }
        }

        // ── Font Glyph Probing ──

        /// <summary>TTF 파일을 읽어 _unicodeBlocks 중 실제 글리프가 있는 블록만 ushort[] 범위로 반환. 결과를 디스크 캐시.</summary>
        private static ushort[]? ProbeGlyphRanges(string fontPath)
        {
            try
            {
                // 캐시 확인
                var cached = LoadGlyphRangeCache(fontPath);
                if (cached != null) return cached;

                var collection = new FontCollection();
                var family = collection.Add(fontPath);
                var slFont = family.CreateFont(16, SixLabors.Fonts.FontStyle.Regular);
                var options = new TextOptions(slFont);

                var ranges = new List<ushort>();
                foreach (var (start, end, probe) in _unicodeBlocks)
                {
                    var bounds = TextMeasurer.MeasureBounds(probe.ToString(), options);
                    if (bounds.Width > 0 && bounds.Height > 0)
                    {
                        ranges.Add(start);
                        ranges.Add(end);
                    }
                }

                if (ranges.Count == 0) return null;
                ranges.Add(0); // terminator
                var result = ranges.ToArray();

                // 캐시 저장
                SaveGlyphRangeCache(fontPath, result);

                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>폰트 글리프 범위 캐시 파일 경로를 반환합니다.</summary>
        private static string GetGlyphCachePath(string fontPath)
        {
            var cacheDir = Path.Combine(ProjectContext.CachePath, "FontGlyphCache");
            var fontName = Path.GetFileNameWithoutExtension(fontPath);
            return Path.Combine(cacheDir, fontName + ".bin");
        }

        /// <summary>캐시에서 글리프 범위를 로드합니다. 폰트 파일이 변경되었으면 null을 반환합니다.</summary>
        private static ushort[]? LoadGlyphRangeCache(string fontPath)
        {
            try
            {
                if (!ProjectContext.IsProjectLoaded) return null;

                var cachePath = GetGlyphCachePath(fontPath);
                if (!File.Exists(cachePath)) return null;

                var fontInfo = new FileInfo(fontPath);
                using var reader = new BinaryReader(File.OpenRead(cachePath));

                // 헤더: 폰트 파일 크기(long) + LastWriteTimeUtc ticks(long)
                var cachedSize = reader.ReadInt64();
                var cachedTicks = reader.ReadInt64();

                if (cachedSize != fontInfo.Length || cachedTicks != fontInfo.LastWriteTimeUtc.Ticks)
                    return null;

                var count = reader.ReadInt32();
                if (count <= 0 || count > 1000) return null; // 안전 검사

                var ranges = new ushort[count];
                for (int i = 0; i < count; i++)
                    ranges[i] = reader.ReadUInt16();

                return ranges;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>글리프 범위를 디스크 캐시에 저장합니다.</summary>
        private static void SaveGlyphRangeCache(string fontPath, ushort[] ranges)
        {
            try
            {
                if (!ProjectContext.IsProjectLoaded) return;

                var cachePath = GetGlyphCachePath(fontPath);
                var cacheDir = Path.GetDirectoryName(cachePath)!;
                if (!Directory.Exists(cacheDir))
                    Directory.CreateDirectory(cacheDir);

                var fontInfo = new FileInfo(fontPath);
                using var writer = new BinaryWriter(File.Create(cachePath));

                // 헤더
                writer.Write(fontInfo.Length);
                writer.Write(fontInfo.LastWriteTimeUtc.Ticks);

                // 데이터
                writer.Write(ranges.Length);
                foreach (var r in ranges)
                    writer.Write(r);
            }
            catch
            {
                // 캐시 저장 실패는 무시 — 다음 실행 시 다시 프로브
            }
        }

        // ── System Clipboard Bridge ──

        private unsafe void SetupSystemClipboard(IWindow window, ImGuiIOPtr io)
        {
            var glfwHandle = window.Native?.Glfw;
            if (!glfwHandle.HasValue) return;

            // Runtime SystemClipboard 초기화
            RoseEngine.SystemClipboard.Initialize(glfwHandle.Value);

            // ImGui 클립보드 콜백을 GLFW로 연결
            _getClipboardDelegate = GetSystemClipboardText;
            _setClipboardDelegate = SetSystemClipboardText;

            io.NativePtr->GetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(_getClipboardDelegate);
            io.NativePtr->SetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(_setClipboardDelegate);
            io.NativePtr->ClipboardUserData = (void*)glfwHandle.Value;
        }

        private static unsafe byte* GetSystemClipboardText(void* userData)
        {
            try
            {
                var text = RoseEngine.SystemClipboard.GetText();
                if (_clipboardReturnBuffer != 0)
                    Marshal.FreeHGlobal(_clipboardReturnBuffer);
                var utf8 = Encoding.UTF8.GetBytes(text);
                _clipboardReturnBuffer = Marshal.AllocHGlobal(utf8.Length + 1);
                Marshal.Copy(utf8, 0, _clipboardReturnBuffer, utf8.Length);
                ((byte*)_clipboardReturnBuffer)[utf8.Length] = 0;
                return (byte*)_clipboardReturnBuffer;
            }
            catch { return null; }
        }

        private static unsafe void SetSystemClipboardText(void* userData, byte* text)
        {
            try
            {
                var str = Marshal.PtrToStringUTF8((nint)text) ?? "";
                RoseEngine.SystemClipboard.SetText(str);
            }
            catch { /* clipboard access failed */ }
        }

        public void Dispose()
        {
            // Clipboard buffer cleanup
            if (_clipboardReturnBuffer != 0)
            {
                Marshal.FreeHGlobal(_clipboardReturnBuffer);
                _clipboardReturnBuffer = 0;
            }

            if (_context != IntPtr.Zero && IsVisible)
                _layoutManager.Save();

            // Property windows 정리
            foreach (var pw in _propertyWindows)
                pw.Dispose();
            _propertyWindows.Clear();

            _gizmo?.Dispose();
            RoseEngine.Gizmos.Backend = null;
            _gizmoRenderer?.Dispose();
            _sceneRtManager?.Dispose();
            _sceneRenderer?.Dispose();
            _camProxy?.Dispose();
            EditorAssets.Dispose();

            _aboutTextureView?.Dispose();
            _aboutTexture?.Dispose();
            _rtManager?.Dispose();
            _rendererBackend?.Dispose();
            _platformBackend?.Dispose();
            _renderer?.Dispose();
            _inputHandler?.Dispose();

            if (_context != IntPtr.Zero)
            {
                ImGui.DestroyContext(_context);
                _context = IntPtr.Zero;
            }

            Debug.Log("[ImGui] Overlay disposed");
        }
    }
}
