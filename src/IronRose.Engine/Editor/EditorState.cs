// ------------------------------------------------------------
// @file    EditorState.cs
// @brief   에디터 상태 영속화 (.rose_editor_state.toml).
//          마지막 씬 경로, 창 위치/크기, UI 스케일, 스냅 설정,
//          패널 가시성, ImGui 레이아웃, 프리팹 편집 모드 상태를 저장/복원한다.
// @deps    IronRose.Engine/ProjectContext, IronRose.Engine/TomlConfig, RoseEngine/Debug
// @exports
//   static class EditorState
//     LastScenePath: string?                     — 마지막으로 열었던 씬 절대 경로
//     WindowX/Y/W/H: int?                       — 창 위치/크기
//     UiScale: float                            — UI 스케일 (0.5~3.0)
//     EditorFont: string                        — UI 폰트 이름
//     SceneViewRenderStyle: string              — Scene View 렌더 스타일
//     SnapTranslate/Rotate/Scale/Grid2D: float  — 스냅 설정
//     ImGuiLayoutData: string?                  — ImGui 독 레이아웃 INI 데이터
//     PanelFeedback: bool                       — Feedback 패널 가시성
//     IsEditingPrefab: bool                     — 프리팹 편집 모드 여부
//     Load(): void                              — 상태 파일 로드
//     Save(): void                              — 상태 파일 저장
//     UpdateLastScene(string?): void            — 씬 경로 업데이트 + 저장
//     CleanupPrefabEditMode(): void             — 종료 시 프리팹 편집 상태 정리
//   class PrefabEditContext (internal)           — 프리팹 편집 스택 한 레벨 컨텍스트
// @note    경로 저장 시 ProjectRoot 기준 상대 경로로 변환하여 저장.
//          FindOrCreatePath()는 ProjectContext.ProjectRoot를 기반으로 파일 경로를 결정한다.
//          TOML 읽기에 TomlConfig API를 사용한다. Save()는 문자열 직접 조합을 유지한다.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using IronRose.Engine;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 에디터 상태 영속화 (.rose_editor_state.toml).
    /// 마지막으로 열었던 씬 경로, 창 위치/크기 등을 저장/복원.
    /// </summary>
    public static class EditorState
    {
        private const string FileName = ".rose_editor_state.toml";

        /// <summary>마지막으로 열었던 씬의 절대 경로.</summary>
        public static string? LastScenePath { get; set; }

        // 창 위치/크기 (null = 저장된 값 없음)
        public static int? WindowX { get; set; }
        public static int? WindowY { get; set; }
        public static int? WindowW { get; set; }
        public static int? WindowH { get; set; }

        /// <summary>에디터 UI 스케일 (0.5 ~ 3.0, 기본 1.0).</summary>
        public static float UiScale { get; set; } = 1.0f;

        /// <summary>에디터 UI 폰트 이름 ("Roboto" 또는 "ArchivoBlack").</summary>
        public static string EditorFont { get; set; } = "Roboto";

        /// <summary>Scene View 렌더 스타일 ("wireframe", "matcap", "diffuse_only", "rendered").</summary>
        public static string SceneViewRenderStyle { get; set; } = "matcap";

        // Snap settings
        /// <summary>이동 스냅 단위 (월드 유닛).</summary>
        public static float SnapTranslate { get; set; } = 1.0f;
        /// <summary>회전 스냅 단위 (도).</summary>
        public static float SnapRotate { get; set; } = 15.0f;
        /// <summary>스케일 스냅 단위.</summary>
        public static float SnapScale { get; set; } = 0.5f;
        /// <summary>2D 그리드 스냅 단위 (UI 캔버스 픽셀).</summary>
        public static float SnapGrid2D { get; set; } = 10.0f;

        /// <summary>ImGui 독 레이아웃 INI 데이터 (메모리 기반).</summary>
        public static string? ImGuiLayoutData { get; set; }

        /// <summary>Edit Collider 모드 활성화 여부.</summary>
        public static bool IsEditingCollider { get; set; } = false;

        // ── Prefab Edit Mode ──

        /// <summary>프리팹 편집 모드 활성화 여부.</summary>
        public static bool IsEditingPrefab { get; set; } = false;

        /// <summary>현재 편집 중인 프리팹 에셋 경로.</summary>
        public static string? EditingPrefabPath { get; set; }

        /// <summary>현재 편집 중인 프리팹 에셋 GUID.</summary>
        public static string? EditingPrefabGuid { get; set; }

        /// <summary>프리팹 편집 스택 (중첩 진입 지원).</summary>
        internal static readonly Stack<PrefabEditContext> PrefabEditStack = new();

        /// <summary>씬 복원용 스냅샷 (최초 진입 시 저장).</summary>
        internal static string? SavedSceneSnapshot;
        internal static string? SavedScenePath;
        internal static bool SavedSceneIsDirty;
        internal static List<IUndoAction>? SavedUndoStack;
        internal static List<IUndoAction>? SavedRedoStack;
        internal static Dictionary<string, int>? SavedGoIdMap;

        // Panel visibility
        public static bool PanelHierarchy { get; set; } = true;
        public static bool PanelInspector { get; set; } = true;
        public static bool PanelSceneEnvironment { get; set; } = true;
        public static bool PanelConsole { get; set; } = true;
        public static bool PanelGameView { get; set; } = true;
        public static bool PanelSceneView { get; set; } = true;
        public static bool PanelProject { get; set; } = true;
        public static bool PanelTextureTool { get; set; } = false;
        public static bool PanelFeedback { get; set; } = false;
        public static bool PanelProjectSettings { get; set; } = false;

        // Legacy: ActiveRendererProfileGuid — 마이그레이션용 (ProjectSettings로 이전됨)
        public static string? ActiveRendererProfileGuid { get; set; }

        private static string BoolStr(bool v) => v ? "true" : "false";

        private static string FindOrCreatePath() =>
            Path.Combine(ProjectContext.ProjectRoot, FileName);

        /// <summary>프로젝트 루트 (ProjectContext 기반).</summary>
        private static string ProjectRoot => ProjectContext.ProjectRoot;

        private static string ToAbsolute(string relative) =>
            Path.GetFullPath(Path.Combine(ProjectRoot, relative));

        private static string ToRelative(string absolute)
        {
            var rel = Path.GetRelativePath(ProjectRoot, absolute);
            return rel.Replace('\\', '/');
        }

        public static void Load()
        {
            var path = FindOrCreatePath();
            if (!File.Exists(path)) return;

            var config = TomlConfig.LoadFile(path, "[EditorState]");
            if (config == null) return;

            var editor = config.GetSection("editor");
            if (editor != null)
            {
                var s = editor.GetString("last_scene", "");
                if (!string.IsNullOrEmpty(s))
                    LastScenePath = ToAbsolute(s);
                UiScale = Math.Clamp(editor.GetFloat("ui_scale", UiScale), 0.5f, 3.0f);
                var sf = editor.GetString("editor_font", "");
                if (!string.IsNullOrEmpty(sf))
                    EditorFont = sf;
                var sr = editor.GetString("scene_view_render_style", "");
                if (!string.IsNullOrEmpty(sr))
                    SceneViewRenderStyle = sr;
                var sarp = editor.GetString("active_renderer_profile_guid", "");
                if (!string.IsNullOrEmpty(sarp))
                    ActiveRendererProfileGuid = sarp;
            }

            var snap = config.GetSection("snap");
            if (snap != null)
            {
                SnapTranslate = Math.Max(snap.GetFloat("translate", SnapTranslate), 0.001f);
                SnapRotate = Math.Max(snap.GetFloat("rotate", SnapRotate), 0.001f);
                SnapScale = Math.Max(snap.GetFloat("scale", SnapScale), 0.001f);
                SnapGrid2D = Math.Max(snap.GetFloat("grid_2d", SnapGrid2D), 0.001f);
            }

            var window = config.GetSection("window");
            if (window != null)
            {
                if (window.HasKey("x")) WindowX = window.GetInt("x");
                if (window.HasKey("y")) WindowY = window.GetInt("y");
                if (window.HasKey("w")) WindowW = window.GetInt("w");
                if (window.HasKey("h")) WindowH = window.GetInt("h");
            }

            var panels = config.GetSection("panels");
            if (panels != null)
            {
                PanelHierarchy = panels.GetBool("hierarchy", PanelHierarchy);
                PanelInspector = panels.GetBool("inspector", PanelInspector);
                PanelSceneEnvironment = panels.GetBool("scene_environment", PanelSceneEnvironment);
                PanelConsole = panels.GetBool("console", PanelConsole);
                PanelGameView = panels.GetBool("game_view", PanelGameView);
                PanelSceneView = panels.GetBool("scene_view", PanelSceneView);
                PanelProject = panels.GetBool("project", PanelProject);
                PanelTextureTool = panels.GetBool("texture_tool", PanelTextureTool);
                PanelFeedback = panels.GetBool("feedback", PanelFeedback);
                PanelProjectSettings = panels.GetBool("project_settings", PanelProjectSettings);
            }

            var layout = config.GetSection("imgui_layout");
            if (layout != null)
            {
                var sd = layout.GetString("data", "");
                if (!string.IsNullOrEmpty(sd))
                    ImGuiLayoutData = sd;
            }

            var winInfo = WindowX.HasValue ? $"{WindowX},{WindowY} {WindowW}x{WindowH}" : "default";
            EditorDebug.Log($"[EditorState] Loaded: last_scene={LastScenePath ?? "(none)"}, window={winInfo}");
        }

        public static void Save()
        {
            var path = FindOrCreatePath();
            try
            {
                var scenePath = string.IsNullOrEmpty(LastScenePath) ? "" : ToRelative(LastScenePath);
                var toml = "[editor]\n";
                toml += $"last_scene = \"{scenePath}\"\n";
                toml += $"ui_scale = {UiScale:F1}\n";
                toml += $"editor_font = \"{EditorFont}\"\n";
                toml += $"scene_view_render_style = \"{SceneViewRenderStyle}\"\n";
                toml += "\n[snap]\n";
                toml += $"translate = {SnapTranslate:F3}\n";
                toml += $"rotate = {SnapRotate:F1}\n";
                toml += $"scale = {SnapScale:F3}\n";
                toml += $"grid_2d = {SnapGrid2D:F1}\n";
                toml += "\n[window]\n";
                if (WindowX.HasValue) toml += $"x = {WindowX.Value}\n";
                if (WindowY.HasValue) toml += $"y = {WindowY.Value}\n";
                if (WindowW.HasValue) toml += $"w = {WindowW.Value}\n";
                if (WindowH.HasValue) toml += $"h = {WindowH.Value}\n";

                toml += "\n[panels]\n";
                toml += $"hierarchy = {BoolStr(PanelHierarchy)}\n";
                toml += $"inspector = {BoolStr(PanelInspector)}\n";
                toml += $"scene_environment = {BoolStr(PanelSceneEnvironment)}\n";
                toml += $"console = {BoolStr(PanelConsole)}\n";
                toml += $"game_view = {BoolStr(PanelGameView)}\n";
                toml += $"scene_view = {BoolStr(PanelSceneView)}\n";
                toml += $"project = {BoolStr(PanelProject)}\n";
                toml += $"texture_tool = {BoolStr(PanelTextureTool)}\n";
                toml += $"feedback = {BoolStr(PanelFeedback)}\n";
                toml += $"project_settings = {BoolStr(PanelProjectSettings)}\n";

                if (!string.IsNullOrEmpty(ImGuiLayoutData))
                {
                    toml += "\n[imgui_layout]\n";
                    toml += "data = '''\n";
                    toml += ImGuiLayoutData;
                    if (!ImGuiLayoutData.EndsWith("\n")) toml += "\n";
                    toml += "'''\n";
                }

                File.WriteAllText(path, toml);
            }
            catch (Exception ex)
            {
                EditorDebug.LogWarning($"[EditorState] Failed to save {path}: {ex.Message}");
            }
        }

        /// <summary>현재 활성 씬 경로로 last_scene 업데이트 + 저장.</summary>
        public static void UpdateLastScene(string? scenePath)
        {
            if (string.IsNullOrEmpty(scenePath)) return;
            LastScenePath = scenePath;
            Save();
        }

        /// <summary>
        /// 앱 종료 시 Prefab Edit Mode 상태 정리.
        /// SavedScenePath를 LastScenePath로 복원하고 편집 상태 초기화.
        /// </summary>
        public static void CleanupPrefabEditMode()
        {
            if (!IsEditingPrefab) return;
            LastScenePath = SavedScenePath;
            IsEditingPrefab = false;
            EditingPrefabPath = null;
            EditingPrefabGuid = null;
            SavedSceneSnapshot = null;
            SavedScenePath = null;
            SavedUndoStack = null;
            SavedRedoStack = null;
            SavedGoIdMap = null;
            PrefabEditStack.Clear();
        }
    }

    /// <summary>프리팹 편집 스택의 한 레벨 컨텍스트.</summary>
    internal class PrefabEditContext
    {
        public string PrefabPath = "";
        public string PrefabGuid = "";
        public string SceneSnapshot = ""; // 이전 레벨의 씬 TOML 문자열
        public bool IsDirty;
        public List<IUndoAction> UndoStack = new();
        public List<IUndoAction> RedoStack = new();
        public Dictionary<string, int> GoIdMap = new();
    }
}

