using System;
using System.IO;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using IronRose.Engine;
using IronRose.Engine.Editor;
using RoseEngine;

namespace IronRose.Standalone
{
    class Program
    {
        private static EngineCore? _engine;
        private static IWindow? _window;

        static void Main(string[] args)
        {
            Debug.Log("[IronRose Standalone] Starting...");

            var options = WindowOptions.DefaultVulkan;
            options.Size = new Vector2D<int>(1280, 720);
            options.Title = "IronRose";
            options.UpdatesPerSecond = 60;
            options.FramesPerSecond = 60;
            options.API = GraphicsAPI.None;

            _window = Window.Create(options);
            _window.Load += OnLoad;
            _window.Update += OnUpdate;
            _window.Render += OnRender;
            _window.Closing += OnClosing;

            _window.Run();

            Debug.Log("[IronRose Standalone] Stopped");
        }

        static void OnLoad()
        {
            Debug.Log($"[IronRose Standalone] Window created: {_window!.Size.X}x{_window.Size.Y}");

            _engine = new EngineCore();
            _engine.HeadlessEditor = true;
            _engine.OnWarmUpComplete = LoadStartScene;
            _engine.Initialize(_window);
        }

        static void LoadStartScene()
        {
            // ProjectSettings에서 시작 씬 경로 읽기
            var scenePath = ProjectSettings.StartScenePath;

            if (string.IsNullOrEmpty(scenePath) || !File.Exists(scenePath))
            {
                // 폴백: Assets/Scenes/DefaultScene.scene
                scenePath = Path.GetFullPath(Path.Combine("Assets", "Scenes", "DefaultScene.scene"));
            }

            if (File.Exists(scenePath))
            {
                Debug.Log($"[IronRose Standalone] Loading scene: {scenePath}");
                SceneSerializer.Load(scenePath);
            }
            else
            {
                Debug.LogError("[IronRose Standalone] No start scene found. Set it in Editor > Project Settings > Build.");
                return;
            }

            // 게임 로직 즉시 시작
            EditorPlayMode.EnterPlayMode();
            Debug.Log("[IronRose Standalone] Play mode started");
        }

        static void OnUpdate(double deltaTime)
        {
            try { _engine!.Update(deltaTime); }
            catch (Exception ex) { Debug.LogError($"[IronRose Standalone] Update ERROR: {ex.Message}\n{ex.StackTrace}"); }
        }

        static void OnRender(double deltaTime)
        {
            try { _engine!.Render(); }
            catch (Exception ex) { Debug.LogError($"[IronRose Standalone] Render ERROR: {ex.Message}\n{ex.StackTrace}"); }
        }

        static void OnClosing()
        {
            Debug.Log("[IronRose Standalone] Shutting down...");
            _engine?.Shutdown();
        }
    }
}
