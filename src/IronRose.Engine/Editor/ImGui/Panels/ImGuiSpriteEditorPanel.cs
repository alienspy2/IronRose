using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using IronRose.AssetPipeline;
using RoseEngine;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    /// <summary>
    /// Sprite Slice 에디터 — 텍스처를 슬라이스하고, 9-slice border를 설정하고,
    /// 각 슬라이스에 이름/피벗/GUID를 지정.
    /// </summary>
    public class ImGuiSpriteEditorPanel : IEditorPanel
    {
        private bool _isOpen;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }

        private readonly Veldrid.GraphicsDevice _device;
        private readonly VeldridImGuiRenderer _renderer;

        // Current texture
        private Texture2D? _texture;
        private string _texturePath = "";
        private IntPtr _textureId;
        private RoseMetadata? _metadata;

        // View state
        private float _zoom = 1f;
        private Vector2 _panOffset;

        // Slices
        private readonly List<SpriteSliceEntry> _slices = new();
        private int _selectedSlice = -1;
        private bool _isDirty;

        // Drag state for creating/resizing slices
        private bool _isDragging;
        private Vector2 _dragStart;
        private int _dragBorderEdge = -1; // -1=none, 0=left, 1=bottom, 2=right, 3=top
        private bool _isCreatingSlice;
        private Vector2 _createStart;

        // Sprite mode
        private int _spriteModeIdx; // 0=Single, 1=Multiple
        private float _pixelsPerUnit = 100f;

        // Single-sprite border/pivot
        private Vector2 _singlePivot = new(0.5f, 0.5f);
        private Vector4 _singleBorder;

        public ImGuiSpriteEditorPanel(Veldrid.GraphicsDevice device, VeldridImGuiRenderer renderer)
        {
            _device = device;
            _renderer = renderer;
        }

        /// <summary>외부에서 텍스처를 열 때 호출.</summary>
        public void Open(string assetPath, Texture2D texture, RoseMetadata metadata)
        {
            _texturePath = assetPath;
            _texture = texture;
            _metadata = metadata;
            _isOpen = true;
            _selectedSlice = -1;
            _isDirty = false;
            _zoom = 1f;
            _panOffset = Vector2.Zero;

            // Ensure GPU upload
            if (texture.TextureView == null)
                texture.UploadToGPU(_device);
            if (texture.TextureView != null)
                _textureId = _renderer.GetOrCreateImGuiBinding(texture.TextureView);

            // Load sprite mode from metadata
            LoadFromMetadata();
        }

        private void LoadFromMetadata()
        {
            _slices.Clear();
            if (_metadata == null) return;

            var imp = _metadata.importer;
            var spriteMode = imp.TryGetValue("sprite_mode", out var sm) ? sm?.ToString() : "Single";
            _spriteModeIdx = spriteMode == "Multiple" ? 1 : 0;

            if (imp.TryGetValue("pixels_per_unit", out var ppuVal))
                _pixelsPerUnit = ppuVal is double d ? (float)d : (ppuVal is long l ? l : 100f);

            // Load single-sprite border/pivot
            _singlePivot = new Vector2(0.5f, 0.5f);
            _singleBorder = Vector4.Zero;
            if (imp.TryGetValue("pivot", out var pivotVal) && pivotVal is Tomlyn.Model.TomlArray pa && pa.Count >= 2)
                _singlePivot = new Vector2(ToFloat(pa[0]), ToFloat(pa[1]));
            if (imp.TryGetValue("border", out var borderVal) && borderVal is Tomlyn.Model.TomlArray ba && ba.Count >= 4)
                _singleBorder = new Vector4(ToFloat(ba[0]), ToFloat(ba[1]), ToFloat(ba[2]), ToFloat(ba[3]));

            // Load slices from sub-assets
            foreach (var sub in _metadata.subAssets)
            {
                if (sub.type != "Sprite") continue;

                var entry = new SpriteSliceEntry
                {
                    name = sub.name,
                    guid = sub.guid,
                };

                // Read rect/pivot/border from importer table
                var key = $"sprite_{sub.name}";
                if (imp.TryGetValue(key, out var sliceVal) && sliceVal is Tomlyn.Model.TomlTable st)
                {
                    entry.rect = ReadRect(st);
                    entry.pivot = ReadVec2(st, "pivot", new Vector2(0.5f, 0.5f));
                    entry.border = ReadVec4(st, "border");
                }

                _slices.Add(entry);
            }
        }

        public void Draw()
        {
            if (!_isOpen || _texture == null) return;

            ImGui.SetNextWindowSize(new Vector2(900, 600), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Sprite Editor", ref _isOpen, ImGuiWindowFlags.MenuBar))
            {
                DrawMenuBar();
                DrawContent();
            }
            ImGui.End();
        }

        private void DrawMenuBar()
        {
            if (ImGui.BeginMenuBar())
            {
                // Sprite Mode
                ImGui.Text("Mode:");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);
                if (ImGui.Combo("##SpriteMode", ref _spriteModeIdx, "Single\0Multiple\0"))
                    _isDirty = true;

                ImGui.SameLine();
                ImGui.Text("PPU:");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(60);
                if (FloatField("##PPU", ref _pixelsPerUnit, "%.0f", 1f, 1000f))
                    _isDirty = true;

                ImGui.SameLine();
                ImGui.Separator();
                ImGui.SameLine();

                bool hasChanges = _isDirty;
                if (!hasChanges) ImGui.BeginDisabled();
                if (ImGui.Button("Apply"))
                    Apply();
                ImGui.SameLine();
                if (ImGui.Button("Revert"))
                    LoadFromMetadata();
                if (!hasChanges) ImGui.EndDisabled();

                ImGui.EndMenuBar();
            }
        }

        private void DrawContent()
        {
            // Left: texture preview | Right: slice info
            float rightPanelW = 220f;
            var contentSize = ImGui.GetContentRegionAvail();

            // Left panel: texture view
            ImGui.BeginChild("##SpriteTexView", new Vector2(contentSize.X - rightPanelW, 0), ImGuiChildFlags.Border);
            DrawTextureView();
            ImGui.EndChild();

            ImGui.SameLine();

            // Right panel: slice inspector
            ImGui.BeginChild("##SpriteSliceInfo", new Vector2(rightPanelW, 0), ImGuiChildFlags.Border);
            DrawSliceInspector();
            ImGui.EndChild();
        }

        private void DrawTextureView()
        {
            if (_textureId == IntPtr.Zero) return;

            var contentSize = ImGui.GetContentRegionAvail();
            var contentMin = ImGui.GetCursorScreenPos();

            // Invisible button for interaction (zoom/pan/click)
            ImGui.InvisibleButton("##texCanvas", contentSize);
            bool hovered = ImGui.IsItemHovered();
            var io = ImGui.GetIO();

            // Zoom with scroll wheel
            if (hovered)
            {
                float scrollDelta = io.MouseWheel;
                if (scrollDelta != 0)
                {
                    float oldZoom = _zoom;
                    _zoom *= (1f + scrollDelta * 0.1f);
                    _zoom = Math.Clamp(_zoom, 0.1f, 20f);

                    // Zoom toward mouse position
                    var mouseRel = io.MousePos - contentMin - _panOffset;
                    _panOffset -= mouseRel * (_zoom / oldZoom - 1f);
                }
            }

            // Pan with middle mouse
            if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
                _panOffset += io.MouseDelta;

            // Draw texture
            float texW = _texture!.width * _zoom;
            float texH = _texture.height * _zoom;
            var texMin = new Vector2(contentMin.X + _panOffset.X, contentMin.Y + _panOffset.Y);
            var texMax = new Vector2(texMin.X + texW, texMin.Y + texH);

            var drawList = ImGui.GetWindowDrawList();

            // Checkerboard background (alpha indication)
            drawList.AddRectFilled(texMin, texMax, ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.3f, 1f)));

            // Texture image
            drawList.AddImage(_textureId, texMin, texMax);

            // Single mode — draw 9-slice border lines over the full texture
            if (_spriteModeIdx == 0)
            {
                var b = _singleBorder;
                if (b.X > 0 || b.Y > 0 || b.Z > 0 || b.W > 0)
                {
                    uint borderCol = ImGui.GetColorU32(new Vector4(0f, 1f, 0f, 0.7f));
                    if (b.X > 0) // Left
                    {
                        var lx = texMin.X + b.X * _zoom;
                        drawList.AddLine(new Vector2(lx, texMin.Y), new Vector2(lx, texMax.Y), borderCol);
                    }
                    if (b.Z > 0) // Right
                    {
                        var rx = texMax.X - b.Z * _zoom;
                        drawList.AddLine(new Vector2(rx, texMin.Y), new Vector2(rx, texMax.Y), borderCol);
                    }
                    if (b.W > 0) // Top
                    {
                        var ty = texMin.Y + b.W * _zoom;
                        drawList.AddLine(new Vector2(texMin.X, ty), new Vector2(texMax.X, ty), borderCol);
                    }
                    if (b.Y > 0) // Bottom
                    {
                        var by = texMax.Y - b.Y * _zoom;
                        drawList.AddLine(new Vector2(texMin.X, by), new Vector2(texMax.X, by), borderCol);
                    }
                }
            }

            // Draw slice rects
            if (_spriteModeIdx == 1) // Multiple mode
            {
                for (int i = 0; i < _slices.Count; i++)
                {
                    var s = _slices[i];
                    var rMin = TexToScreen(new Vector2(s.rect.x, s.rect.y), texMin);
                    var rMax = TexToScreen(new Vector2(s.rect.x + s.rect.width, s.rect.y + s.rect.height), texMin);

                    uint rectColor = i == _selectedSlice
                        ? ImGui.GetColorU32(new Vector4(0.2f, 0.6f, 1f, 1f))
                        : ImGui.GetColorU32(new Vector4(0.2f, 0.6f, 1f, 0.5f));
                    drawList.AddRect(rMin, rMax, rectColor, 0f, ImDrawFlags.None, i == _selectedSlice ? 2f : 1f);

                    // 9-slice border lines (green)
                    if (i == _selectedSlice && (s.border.X > 0 || s.border.Y > 0 || s.border.Z > 0 || s.border.W > 0))
                    {
                        uint borderCol = ImGui.GetColorU32(new Vector4(0f, 1f, 0f, 0.7f));
                        // Left
                        if (s.border.X > 0)
                        {
                            var lx = rMin.X + s.border.X * _zoom;
                            drawList.AddLine(new Vector2(lx, rMin.Y), new Vector2(lx, rMax.Y), borderCol);
                        }
                        // Right
                        if (s.border.Z > 0)
                        {
                            var rx = rMax.X - s.border.Z * _zoom;
                            drawList.AddLine(new Vector2(rx, rMin.Y), new Vector2(rx, rMax.Y), borderCol);
                        }
                        // Top
                        if (s.border.W > 0)
                        {
                            var ty = rMin.Y + s.border.W * _zoom;
                            drawList.AddLine(new Vector2(rMin.X, ty), new Vector2(rMax.X, ty), borderCol);
                        }
                        // Bottom
                        if (s.border.Y > 0)
                        {
                            var by = rMax.Y - s.border.Y * _zoom;
                            drawList.AddLine(new Vector2(rMin.X, by), new Vector2(rMax.X, by), borderCol);
                        }
                    }
                }
            }

            // Click to select slice
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && _spriteModeIdx == 1)
            {
                var mouseTexPos = ScreenToTex(io.MousePos, texMin);
                _selectedSlice = -1;
                for (int i = 0; i < _slices.Count; i++)
                {
                    var r = _slices[i].rect;
                    if (mouseTexPos.X >= r.x && mouseTexPos.X <= r.x + r.width &&
                        mouseTexPos.Y >= r.y && mouseTexPos.Y <= r.y + r.height)
                    {
                        _selectedSlice = i;
                        break;
                    }
                }
            }
        }

        private void DrawSliceInspector()
        {
            ImGui.Text("Slice Info");
            ImGui.Separator();

            if (_spriteModeIdx == 0) // Single mode
            {
                ImGui.Text($"Texture: {_texture!.width} x {_texture.height}");
                ImGui.Text($"PPU: {_pixelsPerUnit}");

                ImGui.Separator();

                // Pivot
                ImGui.Text("Pivot:");
                float spx = _singlePivot.X, spy = _singlePivot.Y;
                ImGui.SetNextItemWidth(100);
                if (FloatField("X##spivot", ref spx, "%.2f", 0f, 1f))
                {
                    _singlePivot = new Vector2(spx, spy);
                    _isDirty = true;
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);
                if (FloatField("Y##spivot", ref spy, "%.2f", 0f, 1f))
                {
                    _singlePivot = new Vector2(spx, spy);
                    _isDirty = true;
                }

                // 9-Slice Border
                ImGui.Text("Border (9-slice):");
                float sbL = _singleBorder.X, sbB = _singleBorder.Y, sbR = _singleBorder.Z, sbT = _singleBorder.W;
                bool sbc = false;
                ImGui.SetNextItemWidth(60);
                sbc |= FloatField("L##sborder", ref sbL, "%.0f", 0f);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(60);
                sbc |= FloatField("B##sborder", ref sbB, "%.0f", 0f);
                ImGui.SetNextItemWidth(60);
                sbc |= FloatField("R##sborder", ref sbR, "%.0f", 0f);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(60);
                sbc |= FloatField("T##sborder", ref sbT, "%.0f", 0f);
                if (sbc)
                {
                    _singleBorder = new Vector4(MathF.Max(0, sbL), MathF.Max(0, sbB), MathF.Max(0, sbR), MathF.Max(0, sbT));
                    _isDirty = true;
                }

                return;
            }

            // Multiple mode
            ImGui.Text($"Slices: {_slices.Count}");

            if (ImGui.Button("+ Add Slice"))
            {
                var entry = new SpriteSliceEntry
                {
                    name = $"Slice_{_slices.Count}",
                    guid = Guid.NewGuid().ToString(),
                    rect = new RoseEngine.Rect(0, 0, MathF.Min(64, _texture!.width), MathF.Min(64, _texture.height)),
                };
                _slices.Add(entry);
                _selectedSlice = _slices.Count - 1;
                _isDirty = true;
            }

            ImGui.Separator();

            if (_selectedSlice >= 0 && _selectedSlice < _slices.Count)
            {
                var s = _slices[_selectedSlice];

                // Name
                string name = s.name;
                if (ImGui.InputText("Name", ref name, 64))
                {
                    s.name = name;
                    _isDirty = true;
                }

                // GUID (readonly)
                ImGui.TextDisabled($"GUID: {s.guid[..Math.Min(8, s.guid.Length)]}...");

                // Rect
                ImGui.Text("Rect:");
                float rx = s.rect.x, ry = s.rect.y, rw = s.rect.width, rh = s.rect.height;
                bool changed = false;
                ImGui.SetNextItemWidth(100);
                changed |= FloatField("X##rect", ref rx, "%.0f");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);
                changed |= FloatField("Y##rect", ref ry, "%.0f");
                ImGui.SetNextItemWidth(100);
                changed |= FloatField("W##rect", ref rw, "%.0f", 1f);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);
                changed |= FloatField("H##rect", ref rh, "%.0f", 1f);
                if (changed)
                {
                    s.rect = new RoseEngine.Rect(rx, ry, MathF.Max(1, rw), MathF.Max(1, rh));
                    _isDirty = true;
                }

                // Pivot
                ImGui.Text("Pivot:");
                float px = s.pivot.X, py = s.pivot.Y;
                ImGui.SetNextItemWidth(100);
                if (FloatField("X##pivot", ref px, "%.2f", 0f, 1f))
                {
                    s.pivot = new Vector2(px, py);
                    _isDirty = true;
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);
                if (FloatField("Y##pivot", ref py, "%.2f", 0f, 1f))
                {
                    s.pivot = new Vector2(px, py);
                    _isDirty = true;
                }

                // 9-Slice Border
                ImGui.Text("Border (9-slice):");
                float bL = s.border.X, bB = s.border.Y, bR = s.border.Z, bT = s.border.W;
                bool bc = false;
                ImGui.SetNextItemWidth(60);
                bc |= FloatField("L##border", ref bL, "%.0f", 0f);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(60);
                bc |= FloatField("B##border", ref bB, "%.0f", 0f);
                ImGui.SetNextItemWidth(60);
                bc |= FloatField("R##border", ref bR, "%.0f", 0f);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(60);
                bc |= FloatField("T##border", ref bT, "%.0f", 0f);
                if (bc)
                {
                    s.border = new Vector4(MathF.Max(0, bL), MathF.Max(0, bB), MathF.Max(0, bR), MathF.Max(0, bT));
                    _isDirty = true;
                }

                ImGui.Separator();
                if (ImGui.Button("Delete Slice"))
                {
                    _slices.RemoveAt(_selectedSlice);
                    _selectedSlice = -1;
                    _isDirty = true;
                }
            }
            else
            {
                ImGui.TextDisabled("No slice selected");
            }
        }

        private void Apply()
        {
            if (_metadata == null) return;

            // Update importer settings
            _metadata.importer["sprite_mode"] = _spriteModeIdx == 1 ? "Multiple" : "Single";
            _metadata.importer["pixels_per_unit"] = (double)_pixelsPerUnit;

            // Save existing single sprite sub-asset GUID before clearing
            var existingSingleGuid = _metadata.subAssets
                .FirstOrDefault(s => s.type == "Sprite" && s.index == 0)?.guid;

            // Clear old sprite sub-assets and slice data
            _metadata.subAssets.RemoveAll(s => s.type == "Sprite");
            var keysToRemove = new List<string>();
            foreach (var kvp in _metadata.importer)
            {
                if (kvp.Key.StartsWith("sprite_"))
                    keysToRemove.Add(kvp.Key);
            }
            foreach (var key in keysToRemove)
                _metadata.importer.Remove(key);

            // Write single-sprite pivot/border + sub-asset entry
            if (_spriteModeIdx == 0)
            {
                _metadata.importer["pivot"] = new Tomlyn.Model.TomlArray
                {
                    (double)_singlePivot.X, (double)_singlePivot.Y
                };
                _metadata.importer["border"] = new Tomlyn.Model.TomlArray
                {
                    (double)_singleBorder.X, (double)_singleBorder.Y,
                    (double)_singleBorder.Z, (double)_singleBorder.W
                };

                // Preserve the sprite sub-asset entry so the GUID survives Apply
                var texName = System.IO.Path.GetFileNameWithoutExtension(_texturePath);
                _metadata.subAssets.Add(new SubAssetEntry
                {
                    name = texName,
                    type = "Sprite",
                    index = 0,
                    guid = existingSingleGuid ?? Guid.NewGuid().ToString(),
                });
            }

            // Write slices
            if (_spriteModeIdx == 1)
            {
                for (int i = 0; i < _slices.Count; i++)
                {
                    var s = _slices[i];

                    // Sub-asset entry for GUID management
                    var subEntry = new SubAssetEntry
                    {
                        name = s.name,
                        type = "Sprite",
                        index = i,
                        guid = s.guid,
                    };
                    _metadata.subAssets.Add(subEntry);

                    // Slice visual data
                    var sliceTable = new Tomlyn.Model.TomlTable
                    {
                        ["rect"] = new Tomlyn.Model.TomlArray
                        {
                            (double)s.rect.x, (double)s.rect.y, (double)s.rect.width, (double)s.rect.height
                        },
                        ["pivot"] = new Tomlyn.Model.TomlArray { (double)s.pivot.X, (double)s.pivot.Y },
                        ["border"] = new Tomlyn.Model.TomlArray
                        {
                            (double)s.border.X, (double)s.border.Y, (double)s.border.Z, (double)s.border.W
                        },
                    };
                    _metadata.importer[$"sprite_{s.name}"] = sliceTable;
                }
            }

            // Save metadata — triggers RoseMetadata.OnSaved → AssetDatabase auto-reimport
            _metadata.Save(_texturePath + ".rose");
            _isDirty = false;

            RoseEngine.EditorDebug.Log($"[SpriteEditor] Applied: {_slices.Count} slices saved");
        }

        // ── InputFloat wrapper (single-click edit) ──

        private static bool FloatField(string label, ref float value, string format = "%.3f",
            float min = float.MinValue, float max = float.MaxValue)
        {
            bool changed = ImGui.InputFloat(label, ref value, 0, 0, format);
            if (changed)
                value = Math.Clamp(value, min, max);
            return changed;
        }

        // ── Coordinate helpers ──

        private Vector2 TexToScreen(Vector2 texPos, Vector2 texMin)
        {
            return new Vector2(texMin.X + texPos.X * _zoom, texMin.Y + texPos.Y * _zoom);
        }

        private Vector2 ScreenToTex(Vector2 screenPos, Vector2 texMin)
        {
            return new Vector2(
                (screenPos.X - texMin.X) / _zoom,
                (screenPos.Y - texMin.Y) / _zoom);
        }

        // ── Metadata read helpers ──

        private static RoseEngine.Rect ReadRect(Tomlyn.Model.TomlTable t)
        {
            if (t.TryGetValue("rect", out var rv) && rv is Tomlyn.Model.TomlArray ra && ra.Count >= 4)
                return new RoseEngine.Rect(ToFloat(ra[0]), ToFloat(ra[1]), ToFloat(ra[2]), ToFloat(ra[3]));
            return new RoseEngine.Rect(0, 0, 64, 64);
        }

        private static Vector2 ReadVec2(Tomlyn.Model.TomlTable t, string key, Vector2 def)
        {
            if (t.TryGetValue(key, out var v) && v is Tomlyn.Model.TomlArray a && a.Count >= 2)
                return new Vector2(ToFloat(a[0]), ToFloat(a[1]));
            return def;
        }

        private static Vector4 ReadVec4(Tomlyn.Model.TomlTable t, string key)
        {
            if (t.TryGetValue(key, out var v) && v is Tomlyn.Model.TomlArray a && a.Count >= 4)
                return new Vector4(ToFloat(a[0]), ToFloat(a[1]), ToFloat(a[2]), ToFloat(a[3]));
            return Vector4.Zero;
        }

        private static float ToFloat(object? v)
        {
            return v switch
            {
                double d => (float)d,
                long l => l,
                float f => f,
                _ => 0f,
            };
        }

        // ── Slice entry ──

        private class SpriteSliceEntry
        {
            public string name = "";
            public string guid = "";
            public RoseEngine.Rect rect;
            public Vector2 pivot = new(0.5f, 0.5f);
            public Vector4 border;
        }
    }
}
