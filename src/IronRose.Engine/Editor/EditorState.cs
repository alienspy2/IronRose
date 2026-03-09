using System;
using System.Collections.Generic;
using System.IO;
using Tomlyn;
using Tomlyn.Model;
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
        public static bool PanelProjectSettings { get; set; } = false;

        // Legacy: ActiveRendererProfileGuid — 마이그레이션용 (ProjectSettings로 이전됨)
        public static string? ActiveRendererProfileGuid { get; set; }

        private static string BoolStr(bool v) => v ? "true" : "false";

        private static string FindOrCreatePath()
        {
            string[] searchPaths = { ".", "..", "../.." };
            foreach (var dir in searchPaths)
            {
                var full = Path.GetFullPath(dir);
                if (File.Exists(Path.Combine(full, "rose_config.toml")))
                    return Path.Combine(full, FileName);
            }
            return Path.GetFullPath(FileName);
        }

        /// <summary>프로젝트 루트 (.rose_editor_state.toml 기준 디렉토리).</summary>
        private static string ProjectRoot => Path.GetDirectoryName(FindOrCreatePath())!;

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

            try
            {
                var table = Toml.ToModel(File.ReadAllText(path));
                if (table.TryGetValue("editor", out var editorVal) && editorVal is TomlTable editor)
                {
                    if (editor.TryGetValue("last_scene", out var v) && v is string s && !string.IsNullOrEmpty(s))
                        LastScenePath = ToAbsolute(s);
                    if (editor.TryGetValue("ui_scale", out var vs) && vs is double ds)
                        UiScale = Math.Clamp((float)ds, 0.5f, 3.0f);
                    if (editor.TryGetValue("editor_font", out var vf) && vf is string sf)
                        EditorFont = sf;
                    if (editor.TryGetValue("scene_view_render_style", out var vr) && vr is string sr)
                        SceneViewRenderStyle = sr;
                    if (editor.TryGetValue("active_renderer_profile_guid", out var varp) && varp is string sarp && !string.IsNullOrEmpty(sarp))
                        ActiveRendererProfileGuid = sarp;
                }

                if (table.TryGetValue("snap", out var snapVal) && snapVal is TomlTable snap)
                {
                    if (snap.TryGetValue("translate", out var vst) && vst is double dst)
                        SnapTranslate = Math.Max((float)dst, 0.001f);
                    if (snap.TryGetValue("rotate", out var vsr) && vsr is double dsr)
                        SnapRotate = Math.Max((float)dsr, 0.001f);
                    if (snap.TryGetValue("scale", out var vss) && vss is double dss)
                        SnapScale = Math.Max((float)dss, 0.001f);
                    if (snap.TryGetValue("grid_2d", out var vsg) && vsg is double dsg)
                        SnapGrid2D = Math.Max((float)dsg, 0.001f);
                }

                if (table.TryGetValue("window", out var windowVal) && windowVal is TomlTable window)
                {
                    if (window.TryGetValue("x", out var vx) && vx is long lx) WindowX = (int)lx;
                    if (window.TryGetValue("y", out var vy) && vy is long ly) WindowY = (int)ly;
                    if (window.TryGetValue("w", out var vw) && vw is long lw) WindowW = (int)lw;
                    if (window.TryGetValue("h", out var vh) && vh is long lh) WindowH = (int)lh;
                }

                if (table.TryGetValue("panels", out var panelsVal) && panelsVal is TomlTable panels)
                {
                    if (panels.TryGetValue("hierarchy", out var ph) && ph is bool bh) PanelHierarchy = bh;
                    if (panels.TryGetValue("inspector", out var pi) && pi is bool bi) PanelInspector = bi;
                    if (panels.TryGetValue("scene_environment", out var pse) && pse is bool bse) PanelSceneEnvironment = bse;
                    if (panels.TryGetValue("console", out var pc) && pc is bool bc) PanelConsole = bc;
                    if (panels.TryGetValue("game_view", out var pg) && pg is bool bg) PanelGameView = bg;
                    if (panels.TryGetValue("scene_view", out var psv) && psv is bool bsv) PanelSceneView = bsv;
                    if (panels.TryGetValue("project", out var pp) && pp is bool bp) PanelProject = bp;
                    if (panels.TryGetValue("texture_tool", out var pt) && pt is bool bt) PanelTextureTool = bt;
                    if (panels.TryGetValue("project_settings", out var pps) && pps is bool bps) PanelProjectSettings = bps;
                }

                if (table.TryGetValue("imgui_layout", out var layoutVal) && layoutVal is TomlTable layout)
                {
                    if (layout.TryGetValue("data", out var vd) && vd is string sd && !string.IsNullOrEmpty(sd))
                        ImGuiLayoutData = sd;
                }

                var winInfo = WindowX.HasValue ? $"{WindowX},{WindowY} {WindowW}x{WindowH}" : "default";
                Debug.Log($"[EditorState] Loaded: last_scene={LastScenePath ?? "(none)"}, window={winInfo}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EditorState] Failed to load {path}: {ex.Message}");
            }
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
                Debug.LogWarning($"[EditorState] Failed to save {path}: {ex.Message}");
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

