// ------------------------------------------------------------
// @file    Program.cs
// @brief   IronRose 에디터 애플리케이션 진입점. 윈도우 생성, 엔진 초기화,
//          씬 로드 체인(마지막 씬 → 기본 씬 → 새 씬 생성), 종료 처리를 담당한다.
// @deps    IronRose.Engine/EngineCore, IronRose.Engine/ProjectContext,
//          IronRose.Engine/RoseConfig, IronRose.Engine.Editor/EditorState,
//          IronRose.Engine.Editor/SceneSerializer, IronRose.Editor/EditorUtils,
//          RoseEngine/SceneManager, RoseEngine/Debug
// @exports
//   class Program (internal)
//     Main(string[]): void               — 에디터 메인 진입점
// @note    DefaultScenePath는 lazy 프로퍼티로 ProjectContext.AssetsPath를 사용.
//          ProjectContext가 초기화된 후에만 올바른 경로를 반환한다.
//          창 닫기 시 미저장 씬이 있으면 GLFW shouldClose를 해제하여 닫기를 취소한다.
// ------------------------------------------------------------
using System;
using System.IO;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using IronRose.Engine;
using IronRose.Engine.Editor;
using IronRose.Editor;
using RoseEngine;

namespace IronRose.RoseEditor
{
    class Program
    {
        private static EngineCore? _engine;
        private static IWindow? _window;

        private static int _frameCount = 0;
        private static double _fpsTimer = 0;

        /// <summary>기본 씬 경로 (Assets/Scenes/DefaultScene.scene).</summary>
        private static string DefaultScenePath =>
            Path.Combine(ProjectContext.AssetsPath, "Scenes", "DefaultScene.scene");

        private static bool _reimportAll;

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
            options.Size = new Vector2D<int>(1280, 720);
            options.Position = new Vector2D<int>(100, 100);
            options.Title = "IronRose Editor";
            options.UpdatesPerSecond = 60;
            options.FramesPerSecond = 60;
            options.API = GraphicsAPI.None;

            _window = Window.Create(options);
            _window.Load += OnLoad;
            _window.Update += OnUpdate;
            _window.Render += OnRender;
            _window.Closing += OnClosing;

            _window.Run();

            Debug.Log("[IronRose Editor] Stopped");
        }

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

            // EditorState는 EngineCore.Initialize() 내부에서 로드됨
            if (EditorState.WindowW.HasValue)
            {
                _window.Size = new Vector2D<int>(EditorState.WindowW.Value, EditorState.WindowH ?? 720);
                _window.Position = new Vector2D<int>(EditorState.WindowX ?? 100, EditorState.WindowY ?? 100);
                ValidateWindowPosition();
            }

            // 에디터 UI 즉시 표시
            _engine.ShowEditor();
        }

        /// <summary>
        /// 씬 로드 체인 (웜업 완료 후 호출):
        /// 1. 마지막으로 열었던 씬이 있으면 열기
        /// 2. 없으면 Assets/Scenes/DefaultScene.scene 열기
        /// 3. 그것도 없으면 기본 씬을 생성하고 DefaultScene.scene로 저장
        /// </summary>
        static void LoadSceneChain()
        {
            Debug.Log("[IronRose Editor] Loading scene...");

            // 1. 마지막으로 열었던 씬
            if (!string.IsNullOrEmpty(EditorState.LastScenePath) && File.Exists(EditorState.LastScenePath))
            {
                Debug.Log($"[IronRose Editor] Restoring last scene: {EditorState.LastScenePath}");
                SceneSerializer.Load(EditorState.LastScenePath);
                return;
            }

            // 2. 기본 씬 파일
            var defaultPath = DefaultScenePath;
            if (File.Exists(defaultPath))
            {
                Debug.Log($"[IronRose Editor] Opening default scene: {defaultPath}");
                SceneSerializer.Load(defaultPath);
                EditorState.UpdateLastScene(defaultPath);
                return;
            }

            // 3. 기본 씬 생성 + 저장
            Debug.Log("[IronRose Editor] Creating default scene...");

            var scenesDir = Path.GetDirectoryName(defaultPath)!;
            if (!Directory.Exists(scenesDir))
                Directory.CreateDirectory(scenesDir);

            var scene = new Scene
            {
                path = defaultPath,
                name = "DefaultScene",
            };
            SceneManager.SetActiveScene(scene);
            EditorUtils.CreateDefaultScene();
            SceneSerializer.Save(defaultPath);
            EditorState.UpdateLastScene(defaultPath);
        }

        /// <summary>
        /// 저장된 창 위치가 화면 밖이면 기본 위치로 리셋.
        /// 타이틀바(최소 50px)가 보이는지 검사.
        /// </summary>
        static void ValidateWindowPosition()
        {
            if (!EditorState.WindowX.HasValue) return;

            var monitor = _window!.Monitor;
            // 모니터 정보를 가져올 수 없으면 검증 생략
            if (monitor == null) return;

            var bounds = monitor.Bounds;
            int monX = bounds.Origin.X;
            int monY = bounds.Origin.Y;
            int monW = bounds.Size.X;
            int monH = bounds.Size.Y;

            int wx = _window.Position.X;
            int wy = _window.Position.Y;
            int ww = _window.Size.X;
            int wh = _window.Size.Y;

            // 창의 최소 50px이 모니터 안에 있어야 함
            const int minVisible = 50;
            bool outOfBounds = wx + ww < monX + minVisible
                            || wx > monX + monW - minVisible
                            || wy < monY
                            || wy > monY + monH - minVisible;

            if (outOfBounds)
            {
                Debug.Log($"[EditorState] Window position ({wx},{wy}) out of bounds, resetting to default");
                _window.Position = new Vector2D<int>(100, 100);
                _window.Size = new Vector2D<int>(1280, 720);
            }
        }

        static void OnUpdate(double deltaTime)
        {
            _frameCount++;
            _fpsTimer += deltaTime;
            if (_fpsTimer >= 1.0)
            {
                _frameCount = 0;
                _fpsTimer = 0;
            }

            try { _engine!.Update(deltaTime); }
            catch (Exception ex)
            {
                Debug.LogError($"[IronRose Editor] Update ERROR: {ex.Message}\n{ex.StackTrace}");
            }
        }

        static void OnRender(double deltaTime)
        {
            try { _engine!.Render(); }
            catch (Exception ex) { Debug.LogError($"[IronRose Editor] Render ERROR: {ex.Message}\n{ex.StackTrace}"); }
        }

        static void OnClosing()
        {
            // 저장하지 않은 변경이 있고, 사용자가 아직 확인하지 않은 경우 → 닫기 취소
            var scene = SceneManager.GetActiveScene();
            if (scene.isDirty && _engine != null && !_engine.IsQuitConfirmed)
            {
                // GLFW의 shouldClose 플래그를 해제하여 창 닫기를 취소
                PreventWindowClose();
                _engine.RequestQuit();
                return;
            }

            Debug.Log("[IronRose Editor] Shutting down...");

            // Prefab Edit Mode 상태 정리 + 씬 경로 저장
            EditorState.CleanupPrefabEditMode();
            if (!string.IsNullOrEmpty(scene.path))
                EditorState.LastScenePath = scene.path;
            EditorState.WindowX = _window!.Position.X;
            EditorState.WindowY = _window.Position.Y;
            EditorState.WindowW = _window.Size.X;
            EditorState.WindowH = _window.Size.Y;
            EditorState.Save();

            _engine?.Shutdown();
        }

        static void PreventWindowClose()
        {
            try
            {
                var glfwHandle = _window?.Native?.Glfw;
                if (glfwHandle.HasValue)
                {
                    unsafe
                    {
                        Silk.NET.GLFW.GlfwProvider.GLFW.Value.SetWindowShouldClose(
                            (Silk.NET.GLFW.WindowHandle*)glfwHandle.Value, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IronRose Editor] Failed to prevent close: {ex.Message}");
            }
        }
    }
}
