using System;
using System.Collections.Generic;
using System.IO;
using IronRose.Engine.Editor.ImGuiEditor;
using RoseEngine;
using Veldrid;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 에디터 전용 내장 에셋 — EditorAssets/Matcaps 디렉터리에서 MatCap 텍스처 로드.
    /// </summary>
    public static class EditorAssets
    {
        private static readonly List<Texture2D> _matCapTextures = new();
        private static readonly List<string> _matCapNames = new();
        private static readonly List<IntPtr> _matCapImGuiBindings = new();

        public static int MatCapCount => _matCapTextures.Count;

        public static void Initialize(GraphicsDevice device, VeldridImGuiRenderer imGuiRenderer)
        {
            var dir = Path.Combine(ProjectContext.EditorAssetsPath, "Matcaps");
            if (!Directory.Exists(dir))
            {
                Debug.LogWarning($"[EditorAssets] Matcap directory not found: {dir}");
                return;
            }

            var files = new List<string>();
            foreach (var ext in new[] { "*.jpg", "*.jpeg", "*.png" })
                files.AddRange(Directory.GetFiles(dir, ext));
            files.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                try
                {
                    var tex = Texture2D.LoadFromFile(file);
                    tex.UploadToGPU(device);

                    if (tex.TextureView == null)
                    {
                        Debug.LogWarning($"[EditorAssets] Failed to create TextureView: {file}");
                        tex.Dispose();
                        continue;
                    }

                    var binding = imGuiRenderer.GetOrCreateImGuiBinding(tex.TextureView);

                    _matCapTextures.Add(tex);
                    _matCapNames.Add(FormatName(Path.GetFileNameWithoutExtension(file)));
                    _matCapImGuiBindings.Add(binding);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[EditorAssets] Failed to load matcap: {file} — {ex.Message}");
                }
            }

            Debug.Log($"[EditorAssets] Loaded {_matCapTextures.Count} matcap textures from {dir}");
        }

        public static string GetMatCapName(int index)
        {
            if (index < 0 || index >= _matCapNames.Count) return "";
            return _matCapNames[index];
        }

        public static TextureView? GetMatCapTextureView(int index)
        {
            if (index < 0 || index >= _matCapTextures.Count) return null;
            return _matCapTextures[index]?.TextureView;
        }

        public static IntPtr GetMatCapImGuiBinding(int index)
        {
            if (index < 0 || index >= _matCapImGuiBindings.Count) return IntPtr.Zero;
            return _matCapImGuiBindings[index];
        }

        public static void Dispose()
        {
            foreach (var tex in _matCapTextures)
                tex.Dispose();
            _matCapTextures.Clear();
            _matCapNames.Clear();
            _matCapImGuiBindings.Clear();
        }

        private static string FormatName(string fileName)
        {
            // "matcap_basic_1" → "Basic 1", "matcap_ceramic_dark" → "Ceramic Dark"
            var name = fileName;
            if (name.StartsWith("matcap_", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(7);

            var parts = name.Split('_');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0)
                    parts[i] = char.ToUpper(parts[i][0]) + parts[i].Substring(1);
            }
            return string.Join(" ", parts);
        }
    }
}
