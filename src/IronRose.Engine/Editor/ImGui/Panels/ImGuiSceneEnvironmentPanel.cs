using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using IronRose.AssetPipeline;
using RoseEngine;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    /// <summary>
    /// Scene Environment panel — 씬별 Skybox/Ambient/Procedural Sky 설정.
    /// 값 변경 시 Scene.isDirty = true로 씬 파일에 저장됨.
    /// </summary>
    public class ImGuiSceneEnvironmentPanel : IEditorPanel
    {
        private bool _isOpen = true;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }

        private static bool _showSkyboxError;
        private static string _skyboxErrorMessage = "";

        // Skybox texture browser
        private bool _openSkyboxBrowser;
        private string _skyboxBrowserSearch = "";
        private List<(string guid, string path, string name)>? _skyboxBrowserList;

        public void Draw()
        {
            if (!ProjectContext.IsProjectLoaded) return;
            if (!IsOpen) return;

            if (ImGui.Begin("Scene Environment", ref _isOpen))
            {
                DrawSkyboxSection();
                DrawAmbientSection();
                DrawSkySection();
            }
            ImGui.End();
        }

        // ─── Skybox ──────────────────────────────────────────────

        private void DrawSkyboxSection()
        {
            if (!ImGui.CollapsingHeader("Skybox", ImGuiTreeNodeFlags.DefaultOpen)) return;

            var db = Resources.GetAssetDatabase();
            var skyboxMat = RenderSettings.skybox;
            string texName = skyboxMat?.mainTexture?.name ?? "(None)";

            // Resolve asset path from guid for ping/tooltip
            string? assetPath = null;
            if (!string.IsNullOrEmpty(RenderSettings.skyboxTextureGuid) && db != null)
                assetPath = db.GetPathFromGuid(RenderSettings.skyboxTextureGuid);

            ImGui.Text("Texture");
            ImGui.SameLine();

            float availW = ImGui.GetContentRegionAvail().X;
            float selectableW = availW - 24f; // browse button 공간

            // Object link — Inspector DrawPingableLabel 패턴
            if (assetPath != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.600f, 0.380f, 0.350f, 1f));
                if (ImGui.Selectable($"{texName}##SkyboxTexture", false, ImGuiSelectableFlags.None,
                        new Vector2(selectableW, 0)))
                    EditorBridge.PingAsset(assetPath);
                ImGui.PopStyleColor();

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(assetPath);
            }
            else
            {
                ImGui.Button($"{texName}##SkyboxTexture", new Vector2(selectableW, 0));
            }

            // Drag-drop target
            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload("ASSET_PATH");
                unsafe
                {
                    if (payload.NativePtr != null)
                    {
                        var path = ImGuiProjectPanel._draggedAssetPath;
                        if (!string.IsNullOrEmpty(path))
                        {
                            ApplySkyboxTexture(path);
                            MarkSceneDirty();
                        }
                    }
                }
                ImGui.EndDragDropTarget();
            }

            // Browse button (◎)
            ImGui.SameLine();
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 0));
            if (ImGui.Button("\u25ce##SkyboxTexture_browse"))
            {
                _openSkyboxBrowser = true;
                _skyboxBrowserSearch = "";
                _skyboxBrowserList = null;
            }
            ImGui.PopStyleVar();

            DrawSkyboxBrowserPopup();

            // Exposure / Rotation (only when skybox is set)
            if (RenderSettings.skybox != null)
            {
                float exposure = RenderSettings.skyboxExposure;
                if (EditorWidgets.SliderFloatWithInput("SE", "Exposure", ref exposure, 0f, 10f))
                {
                    RenderSettings.skyboxExposure = exposure;
                    if (RenderSettings.skybox != null)
                        RenderSettings.skybox.exposure = exposure;
                    MarkSceneDirty();
                }

                float rotation = RenderSettings.skyboxRotation;
                if (EditorWidgets.SliderFloatWithInput("SE", "Rotation", ref rotation, 0f, 360f))
                {
                    RenderSettings.skyboxRotation = rotation;
                    if (RenderSettings.skybox != null)
                        RenderSettings.skybox.rotation = rotation;
                    MarkSceneDirty();
                }
            }

            // Skybox error popup
            if (_showSkyboxError)
            {
                ImGui.OpenPopup("Skybox Texture Error");
                _showSkyboxError = false;
            }

            if (ImGui.BeginPopupModal("Skybox Texture Error", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
            {
                ImGui.TextWrapped(_skyboxErrorMessage);
                ImGui.Spacing();
                float buttonW = 120;
                ImGui.SetCursorPosX((ImGui.GetWindowWidth() - buttonW) * 0.5f);
                if (ImGui.Button("OK", new Vector2(buttonW, 0)))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        private void DrawSkyboxBrowserPopup()
        {
            if (_openSkyboxBrowser)
            {
                ImGui.OpenPopup("Select Skybox Texture##popup");
                _openSkyboxBrowser = false;
            }

            if (!ImGui.BeginPopup("Select Skybox Texture##popup")) return;

            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("##skyboxSearch", "Search...", ref _skyboxBrowserSearch, 256);
            ImGui.Separator();

            // Build list on first open
            if (_skyboxBrowserList == null)
            {
                _skyboxBrowserList = new List<(string guid, string path, string name)>();
                var db = Resources.GetAssetDatabase();
                if (db != null)
                {
                    foreach (var path in db.GetAllAssetPaths())
                    {
                        var ext = Path.GetExtension(path);
                        if (Array.IndexOf(_skyboxImageExtensions, ext.ToLowerInvariant()) < 0)
                            continue;
                        var guid = db.GetGuidFromPath(path);
                        if (guid != null)
                            _skyboxBrowserList.Add((guid, path, Path.GetFileName(path)));
                    }
                    _skyboxBrowserList.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
                }
            }

            // None option (clear)
            bool isNone = string.IsNullOrEmpty(RenderSettings.skyboxTextureGuid);
            if (ImGui.Selectable("(None)", isNone))
            {
                RenderSettings.skyboxTextureGuid = null;
                RenderSettings.skybox = null;
                MarkSceneDirty();
                ImGui.CloseCurrentPopup();
            }

            var search = _skyboxBrowserSearch;
            foreach (var (guid, path, name) in _skyboxBrowserList)
            {
                if (!string.IsNullOrEmpty(search) &&
                    name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                bool selected = guid == RenderSettings.skyboxTextureGuid;
                if (ImGui.Selectable(name, selected))
                {
                    ApplySkyboxTexture(path);
                    MarkSceneDirty();
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(path);
            }

            ImGui.EndPopup();
        }

        private static readonly string[] _skyboxImageExtensions = { ".hdr", ".exr", ".png", ".jpg", ".jpeg", ".tga", ".bmp" };
        private static readonly string[] _skyboxHdrExtensions = { ".hdr", ".exr" };

        private static void ApplySkyboxTexture(string assetPath)
        {
            var ext = Path.GetExtension(assetPath).ToLowerInvariant();

            // Reject non-image files
            if (Array.IndexOf(_skyboxImageExtensions, ext) < 0)
            {
                _skyboxErrorMessage = $"Skybox에 사용할 수 없는 파일 형식입니다: {ext}\n\n" +
                    "지원 형식: .hdr, .exr, .png, .jpg";
                _showSkyboxError = true;
                return;
            }

            var db = Resources.GetAssetDatabase();
            if (db == null) return;

            var tex = db.Load<Texture2D>(assetPath);
            if (tex == null)
            {
                EditorDebug.LogWarning($"[SceneEnvironment] Could not load texture: {assetPath}");
                return;
            }

            // Panoramic 타입은 import 시 이미 2:1 + HDR 강제됨 → 비율 검증 스킵
            var meta = RoseMetadata.LoadOrCreate(assetPath);
            string textureType = "";
            if (meta.importer.TryGetValue("texture_type", out var ttVal))
                textureType = ttVal?.ToString() ?? "";
            bool isPanoramic = textureType == "Panoramic";

            bool isHdr = Array.IndexOf(_skyboxHdrExtensions, ext) >= 0;
            if (!isHdr && !isPanoramic)
            {
                float aspect = tex.width / (float)tex.height;
                if (aspect < 1.5f || aspect > 2.5f)
                {
                    _skyboxErrorMessage =
                        $"Skybox 텍스쳐는 Equirectangular(2:1) 비율의 파노라마 이미지여야 합니다.\n\n" +
                        $"현재 텍스쳐: {tex.width} x {tex.height} (비율 {aspect:F2}:1)\n\n" +
                        "권장: .hdr 또는 .exr 형식의 HDRI 환경맵을 사용하세요.\n" +
                        "또는 texture_type을 Panoramic으로 설정하면 자동 변환됩니다.";
                    _showSkyboxError = true;
                    return;
                }
            }

            // face_size from metadata (Panoramic 전용)
            int faceSize = 512;
            if (isPanoramic && meta.importer.TryGetValue("face_size", out var fsVal))
                faceSize = System.Convert.ToInt32(fsVal);

            var guid = db.GetGuidFromPath(assetPath);
            RenderSettings.skyboxTextureGuid = guid;

            var mat = new Material(Shader.Find("Skybox/Panoramic")!);
            mat.mainTexture = tex;
            mat.exposure = RenderSettings.skyboxExposure;
            mat.rotation = RenderSettings.skyboxRotation;
            mat.cubemapFaceSize = faceSize;
            RenderSettings.skybox = mat;
        }

        // ─── Ambient ─────────────────────────────────────────────

        private static void DrawAmbientSection()
        {
            if (!ImGui.CollapsingHeader("Ambient", ImGuiTreeNodeFlags.DefaultOpen)) return;

            float intensity = RenderSettings.ambientIntensity;
            if (EditorWidgets.SliderFloatWithInput("SE", "Ambient Intensity", ref intensity, 0f, 5f))
            {
                RenderSettings.ambientIntensity = intensity;
                MarkSceneDirty();
            }

            var col = RenderSettings.ambientLight;
            if (EditorWidgets.ColorEdit4("Ambient Color", ref col))
            {
                RenderSettings.ambientLight = col;
                MarkSceneDirty();
            }
        }

        // ─── Procedural Sky ──────────────────────────────────────

        private static void DrawSkySection()
        {
            if (!ImGui.CollapsingHeader("Sky", ImGuiTreeNodeFlags.DefaultOpen)) return;

            float zi = RenderSettings.skyZenithIntensity;
            if (EditorWidgets.SliderFloatWithInput("SE", "Zenith Intensity", ref zi, 0f, 5f))
            {
                RenderSettings.skyZenithIntensity = zi;
                MarkSceneDirty();
            }

            float hi = RenderSettings.skyHorizonIntensity;
            if (EditorWidgets.SliderFloatWithInput("SE", "Horizon Intensity", ref hi, 0f, 5f))
            {
                RenderSettings.skyHorizonIntensity = hi;
                MarkSceneDirty();
            }

            float si = RenderSettings.sunIntensity;
            if (EditorWidgets.SliderFloatWithInput("SE", "Sun Intensity", ref si, 0f, 50f))
            {
                RenderSettings.sunIntensity = si;
                MarkSceneDirty();
            }

            var zc = RenderSettings.skyZenithColor;
            if (EditorWidgets.ColorEdit4("Zenith Color", ref zc))
            {
                RenderSettings.skyZenithColor = zc;
                MarkSceneDirty();
            }

            var hc = RenderSettings.skyHorizonColor;
            if (EditorWidgets.ColorEdit4("Horizon Color", ref hc))
            {
                RenderSettings.skyHorizonColor = hc;
                MarkSceneDirty();
            }
        }

        // ─── Helpers ─────────────────────────────────────────────

        private static void MarkSceneDirty()
        {
            SceneManager.GetActiveScene().isDirty = true;
        }
    }
}
