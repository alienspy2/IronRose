using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using ImGuiNET;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using IronRose.Engine.Editor.ImGuiEditor;
using Image = SixLabors.ImageSharp.Image;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    public class ImGuiTextureToolPanel : IEditorPanel
    {
        private bool _isOpen;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }

        // ── Mode ──
        private enum ToolMode { ChannelRemix, Generation }
        private ToolMode _mode = ToolMode.ChannelRemix;
        private static readonly string[] ModeNames = { "Channel Remix", "Generation" };

        // ── Channel Remix ──
        private enum ChannelSourceMode { Color, Texture }
        private static readonly string[] ChannelSourceNames = { "Color", "Texture" };
        private static readonly string[] ChannelPickNames = { "R", "G", "B", "A" };
        private static readonly string[] ChannelLabels = { "Red (R)", "Green (G)", "Blue (B)", "Alpha (A)" };

        private struct ChannelSlot
        {
            public ChannelSourceMode SourceMode;
            public int Value; // 0–255, Color 모드에서 해당 채널 값
            public string TexturePath;
            public int SourceChannel; // 0=R 1=G 2=B 3=A
        }

        private ChannelSlot[] _channels = new ChannelSlot[4];
        private int _remixWidth = 1024;
        private int _remixHeight = 1024;

        // ── Generation ──
        private enum ProceduralType { Checker, Brick, Voronoi, Gradient, Noise }
        private static readonly string[] ProceduralNames = { "Checker", "Brick", "Voronoi", "Gradient", "Noise" };
        private ProceduralType _proceduralType = ProceduralType.Checker;

        // Checker
        private int _checkerTileCount = 8;
        private Vector4 _checkerColor1 = new(1, 1, 1, 1);
        private Vector4 _checkerColor2 = new(0, 0, 0, 1);

        // Brick
        private int _brickRows = 16;
        private int _brickCols = 8;
        private float _brickMortarSize = 0.05f;
        private Vector4 _brickColor = new(0.72f, 0.32f, 0.2f, 1);
        private Vector4 _brickMortarColor = new(0.85f, 0.82f, 0.78f, 1);

        // Voronoi
        private int _voronoiCellCount = 32;
        private int _voronoiSeed = 42;
        private bool _voronoiShowEdges;
        private float _voronoiEdgeWidth = 0.03f;

        // Gradient
        private enum GradientDirection { Horizontal, Vertical, Radial }
        private static readonly string[] GradientDirNames = { "Horizontal", "Vertical", "Radial" };
        private GradientDirection _gradientDir = GradientDirection.Vertical;
        private Vector4 _gradientStart = new(0, 0, 0, 1);
        private Vector4 _gradientEnd = new(1, 1, 1, 1);

        // Noise
        private int _noiseSeed;
        private float _noiseScale = 4.0f;
        private int _noiseOctaves = 4;
        private float _noisePersistence = 0.5f;

        private int _genWidth = 1024;
        private int _genHeight = 1024;

        // Voronoi cache
        private Vector2[]? _voronoiPoints;

        // Status
        private string _statusMessage = "";

        public ImGuiTextureToolPanel()
        {
            for (int i = 0; i < 4; i++)
            {
                _channels[i] = new ChannelSlot
                {
                    SourceMode = ChannelSourceMode.Color,
                    Value = i < 3 ? 0 : 255,
                    TexturePath = "",
                    SourceChannel = i,
                };
            }
        }

        // ────────────────────────────────────────────────────────────
        // Draw
        // ────────────────────────────────────────────────────────────

        public void Draw()
        {
            if (!IsOpen) return;

            var texToolVisible = ImGui.Begin("Texture Tool", ref _isOpen);
            PanelMaximizer.DrawTabContextMenu("Texture Tool");
            if (texToolVisible)
            {
                int modeInt = (int)_mode;
                if (ImGui.Combo("Mode", ref modeInt, ModeNames, ModeNames.Length))
                    _mode = (ToolMode)modeInt;

                ImGui.Separator();
                ImGui.Spacing();

                switch (_mode)
                {
                    case ToolMode.ChannelRemix: DrawChannelRemixMode(); break;
                    case ToolMode.Generation:   DrawGenerationMode();   break;
                }

                if (!string.IsNullOrEmpty(_statusMessage))
                {
                    ImGui.Separator();
                    ImGui.TextWrapped(_statusMessage);
                }
            }
            ImGui.End();
        }

        // ────────────────────────────────────────────────────────────
        // Channel Remix UI
        // ────────────────────────────────────────────────────────────

        private void DrawChannelRemixMode()
        {
            for (int i = 0; i < 4; i++)
            {
                if (ImGui.CollapsingHeader(ChannelLabels[i], ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.PushID(i);

                    int srcMode = (int)_channels[i].SourceMode;
                    if (ImGui.Combo("Source", ref srcMode, ChannelSourceNames, ChannelSourceNames.Length))
                        _channels[i].SourceMode = (ChannelSourceMode)srcMode;

                    if (_channels[i].SourceMode == ChannelSourceMode.Color)
                    {
                        var val = _channels[i].Value;
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 80);
                        if (ImGui.SliderInt("##slider", ref val, 0, 255))
                            _channels[i].Value = val;
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(70);
                        if (ImGui.InputInt("##input", ref val, 0, 0))
                            _channels[i].Value = Math.Clamp(val, 0, 255);
                    }
                    else
                    {
                        var path = _channels[i].TexturePath ?? "";
                        if (ImGui.InputText("Path", ref path, 512))
                            _channels[i].TexturePath = path;

                        // Drag-drop target: Project 패널에서 텍스쳐 에셋 드롭
                        if (ImGui.BeginDragDropTarget())
                        {
                            unsafe
                            {
                                var payload = ImGui.AcceptDragDropPayload("ASSET_PATH");
                                if (payload.NativePtr != null)
                                {
                                    var dropped = ImGuiProjectPanel._draggedAssetPath;
                                    if (!string.IsNullOrEmpty(dropped) && IsTextureFile(dropped))
                                        _channels[i].TexturePath = dropped;
                                }
                            }
                            ImGui.EndDragDropTarget();
                        }

                        ImGui.SameLine();
                        if (ImGui.Button("Browse"))
                        {
                            var selected = NativeFileDialog.OpenFileDialog(
                                title: "Select Source Texture",
                                filter: "*.png *.jpg *.jpeg *.tga *.bmp");
                            if (!string.IsNullOrEmpty(selected))
                                _channels[i].TexturePath = selected;
                        }

                        int ch = _channels[i].SourceChannel;
                        if (ImGui.Combo("Channel", ref ch, ChannelPickNames, ChannelPickNames.Length))
                            _channels[i].SourceChannel = ch;
                    }

                    ImGui.PopID();
                }
            }

            ImGui.Spacing();
            ImGui.Separator();

            ImGui.InputInt("Width", ref _remixWidth);
            ImGui.InputInt("Height", ref _remixHeight);
            _remixWidth  = Math.Clamp(_remixWidth,  1, 8192);
            _remixHeight = Math.Clamp(_remixHeight, 1, 8192);

            ImGui.Spacing();

            if (ImGui.Button("Compose", new Vector2(120, 0)))
                ComposeChannelRemix();
        }

        // ────────────────────────────────────────────────────────────
        // Channel Remix Logic
        // ────────────────────────────────────────────────────────────

        private void ComposeChannelRemix()
        {
            try
            {
                int w = _remixWidth;
                int h = _remixHeight;

                var loadedImages = new Dictionary<string, Image<Rgba32>>();
                for (int i = 0; i < 4; i++)
                {
                    if (_channels[i].SourceMode == ChannelSourceMode.Texture
                        && !string.IsNullOrWhiteSpace(_channels[i].TexturePath)
                        && !loadedImages.ContainsKey(_channels[i].TexturePath))
                    {
                        var img = Image.Load<Rgba32>(_channels[i].TexturePath);
                        if (img.Width != w || img.Height != h)
                            img.Mutate(ctx => ctx.Resize(w, h));
                        loadedImages[_channels[i].TexturePath] = img;
                    }
                }

                // Snapshot channel config for lambda capture
                var slots = (ChannelSlot[])_channels.Clone();

                using var output = new Image<Rgba32>(w, h);
                output.ProcessPixelRows(accessor =>
                {
                    var px = new byte[4];
                    for (int y = 0; y < h; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < w; x++)
                        {
                            px[0] = 0; px[1] = 0; px[2] = 0; px[3] = 255;
                            for (int c = 0; c < 4; c++)
                            {
                                ref var slot = ref slots[c];
                                if (slot.SourceMode == ChannelSourceMode.Color)
                                {
                                    px[c] = (byte)Math.Clamp(slot.Value, 0, 255);
                                }
                                else if (loadedImages.TryGetValue(slot.TexturePath, out var srcImg))
                                {
                                    var srcPixel = srcImg[x, y];
                                    px[c] = slot.SourceChannel switch
                                    {
                                        0 => srcPixel.R,
                                        1 => srcPixel.G,
                                        2 => srcPixel.B,
                                        _ => srcPixel.A,
                                    };
                                }
                            }
                            row[x] = new Rgba32(px[0], px[1], px[2], px[3]);
                        }
                    }
                });

                foreach (var img in loadedImages.Values) img.Dispose();

                var savePath = NativeFileDialog.SaveFileDialog(
                    title: "Save Composed Texture",
                    defaultName: "composed.png",
                    filter: "*.png",
                    initialDir: EditorPreferences.ResolveSaveInitialDir());

                if (string.IsNullOrEmpty(savePath)) { _statusMessage = "Save cancelled."; return; }
                if (!Path.HasExtension(savePath)) savePath += ".png";

                var dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                output.SaveAsPng(savePath);
                EditorPreferences.RememberSaveDir(savePath);
                _statusMessage = $"Saved: {savePath} ({w}x{h})";
                RoseEngine.EditorDebug.Log($"[TextureTool] Channel remix saved: {savePath} ({w}x{h})");
            }
            catch (Exception ex)
            {
                _statusMessage = $"Error: {ex.Message}";
                RoseEngine.EditorDebug.LogError($"[TextureTool] Compose failed: {ex}");
            }
        }

        // ────────────────────────────────────────────────────────────
        // Generation UI
        // ────────────────────────────────────────────────────────────

        private void DrawGenerationMode()
        {
            int typeInt = (int)_proceduralType;
            if (ImGui.Combo("Type", ref typeInt, ProceduralNames, ProceduralNames.Length))
                _proceduralType = (ProceduralType)typeInt;

            ImGui.Spacing();

            switch (_proceduralType)
            {
                case ProceduralType.Checker:
                    ImGui.InputInt("Tile Count", ref _checkerTileCount);
                    _checkerTileCount = Math.Max(1, _checkerTileCount);
                    EditorWidgets.ColorEdit4("Color 1", ref _checkerColor1);
                    EditorWidgets.ColorEdit4("Color 2", ref _checkerColor2);
                    break;

                case ProceduralType.Brick:
                    ImGui.InputInt("Rows", ref _brickRows);
                    ImGui.InputInt("Columns", ref _brickCols);
                    _brickRows = Math.Max(1, _brickRows);
                    _brickCols = Math.Max(1, _brickCols);
                    ImGui.SliderFloat("Mortar Size", ref _brickMortarSize, 0f, 0.2f);
                    EditorWidgets.ColorEdit4("Brick Color", ref _brickColor);
                    EditorWidgets.ColorEdit4("Mortar Color", ref _brickMortarColor);
                    break;

                case ProceduralType.Voronoi:
                    ImGui.InputInt("Cell Count", ref _voronoiCellCount);
                    ImGui.InputInt("Seed", ref _voronoiSeed);
                    _voronoiCellCount = Math.Max(1, _voronoiCellCount);
                    ImGui.Checkbox("Show Edges", ref _voronoiShowEdges);
                    if (_voronoiShowEdges)
                        ImGui.SliderFloat("Edge Width", ref _voronoiEdgeWidth, 0.001f, 0.1f);
                    break;

                case ProceduralType.Gradient:
                    int dirInt = (int)_gradientDir;
                    if (ImGui.Combo("Direction", ref dirInt, GradientDirNames, GradientDirNames.Length))
                        _gradientDir = (GradientDirection)dirInt;
                    EditorWidgets.ColorEdit4("Start Color", ref _gradientStart);
                    EditorWidgets.ColorEdit4("End Color", ref _gradientEnd);
                    break;

                case ProceduralType.Noise:
                    ImGui.InputInt("Seed", ref _noiseSeed);
                    ImGui.SliderFloat("Scale", ref _noiseScale, 0.1f, 32f);
                    ImGui.SliderInt("Octaves", ref _noiseOctaves, 1, 8);
                    ImGui.SliderFloat("Persistence", ref _noisePersistence, 0.1f, 1.0f);
                    break;
            }

            ImGui.Spacing();
            ImGui.Separator();

            ImGui.InputInt("Width", ref _genWidth);
            ImGui.InputInt("Height", ref _genHeight);
            _genWidth  = Math.Clamp(_genWidth,  1, 8192);
            _genHeight = Math.Clamp(_genHeight, 1, 8192);

            ImGui.Spacing();

            if (ImGui.Button("Generate", new Vector2(120, 0)))
                GenerateProcedural();
        }

        // ────────────────────────────────────────────────────────────
        // Generation Logic
        // ────────────────────────────────────────────────────────────

        private void GenerateProcedural()
        {
            try
            {
                int w = _genWidth;
                int h = _genHeight;

                if (_proceduralType == ProceduralType.Voronoi)
                    PrepareVoronoiPoints();

                using var output = new Image<Rgba32>(w, h);
                output.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < h; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < w; x++)
                        {
                            float u = (float)x / w;
                            float v = (float)y / h;

                            var color = _proceduralType switch
                            {
                                ProceduralType.Checker  => GenChecker(u, v),
                                ProceduralType.Brick    => GenBrick(u, v),
                                ProceduralType.Voronoi  => GenVoronoi(u, v),
                                ProceduralType.Gradient => GenGradient(u, v),
                                ProceduralType.Noise    => GenNoise(u, v),
                                _ => new Vector4(0, 0, 0, 1),
                            };

                            row[x] = new Rgba32(
                                (byte)Math.Clamp((int)(color.X * 255f + 0.5f), 0, 255),
                                (byte)Math.Clamp((int)(color.Y * 255f + 0.5f), 0, 255),
                                (byte)Math.Clamp((int)(color.Z * 255f + 0.5f), 0, 255),
                                (byte)Math.Clamp((int)(color.W * 255f + 0.5f), 0, 255));
                        }
                    }
                });

                var savePath = NativeFileDialog.SaveFileDialog(
                    title: "Save Generated Texture",
                    defaultName: $"{_proceduralType.ToString().ToLowerInvariant()}.png",
                    filter: "*.png",
                    initialDir: EditorPreferences.ResolveSaveInitialDir());

                if (string.IsNullOrEmpty(savePath)) { _statusMessage = "Save cancelled."; return; }
                if (!Path.HasExtension(savePath)) savePath += ".png";

                var dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                output.SaveAsPng(savePath);
                EditorPreferences.RememberSaveDir(savePath);
                _statusMessage = $"Saved: {savePath} ({w}x{h})";
                RoseEngine.EditorDebug.Log($"[TextureTool] Generated {_proceduralType}: {savePath} ({w}x{h})");
            }
            catch (Exception ex)
            {
                _statusMessage = $"Error: {ex.Message}";
                RoseEngine.EditorDebug.LogError($"[TextureTool] Generate failed: {ex}");
            }
        }

        // ────────────────────────────────────────────────────────────
        // Procedural Generators
        // ────────────────────────────────────────────────────────────

        private Vector4 GenChecker(float u, float v)
        {
            int cx = (int)(u * _checkerTileCount) % 2;
            int cy = (int)(v * _checkerTileCount) % 2;
            return (cx ^ cy) == 0 ? _checkerColor1 : _checkerColor2;
        }

        private Vector4 GenBrick(float u, float v)
        {
            float rowH = 1.0f / _brickRows;
            float colW = 1.0f / _brickCols;

            int row = (int)(v / rowH);
            float localV = (v - row * rowH) / rowH;

            float offsetU = u;
            if (row % 2 == 1)
                offsetU = (u + colW * 0.5f) % 1.0f;

            float localU = (offsetU - (int)(offsetU / colW) * colW) / colW;

            float halfMortar = _brickMortarSize * 0.5f;
            if (localU < halfMortar || localU > 1.0f - halfMortar ||
                localV < halfMortar || localV > 1.0f - halfMortar)
                return _brickMortarColor;

            return _brickColor;
        }

        private void PrepareVoronoiPoints()
        {
            var rng = new Random(_voronoiSeed);
            _voronoiPoints = new Vector2[_voronoiCellCount];
            for (int i = 0; i < _voronoiCellCount; i++)
                _voronoiPoints[i] = new Vector2((float)rng.NextDouble(), (float)rng.NextDouble());
        }

        private Vector4 GenVoronoi(float u, float v)
        {
            if (_voronoiPoints == null) return Vector4.Zero;

            float minDist = float.MaxValue;
            float secondMin = float.MaxValue;
            int closest = 0;

            for (int i = 0; i < _voronoiPoints.Length; i++)
            {
                float dx = u - _voronoiPoints[i].X;
                float dy = v - _voronoiPoints[i].Y;
                float dist = dx * dx + dy * dy;
                if (dist < minDist)
                {
                    secondMin = minDist;
                    minDist = dist;
                    closest = i;
                }
                else if (dist < secondMin)
                {
                    secondMin = dist;
                }
            }

            if (_voronoiShowEdges)
            {
                float edge = MathF.Sqrt(secondMin) - MathF.Sqrt(minDist);
                if (edge < _voronoiEdgeWidth)
                    return new Vector4(0, 0, 0, 1);
            }

            var cellRng = new Random(closest * 73856093);
            return new Vector4((float)cellRng.NextDouble(), (float)cellRng.NextDouble(), (float)cellRng.NextDouble(), 1);
        }

        private Vector4 GenGradient(float u, float v)
        {
            float t = _gradientDir switch
            {
                GradientDirection.Horizontal => u,
                GradientDirection.Vertical   => v,
                GradientDirection.Radial     => Math.Min(1f, MathF.Sqrt((u - 0.5f) * (u - 0.5f) + (v - 0.5f) * (v - 0.5f)) * 2f),
                _ => 0f,
            };
            return Vector4.Lerp(_gradientStart, _gradientEnd, t);
        }

        private Vector4 GenNoise(float u, float v)
        {
            float value = 0f;
            float amplitude = 1f;
            float frequency = _noiseScale;
            float maxValue = 0f;

            float seedOffX = _noiseSeed * 12.9898f;
            float seedOffY = _noiseSeed * 78.233f;

            for (int o = 0; o < _noiseOctaves; o++)
            {
                value += PerlinNoise2D((u + seedOffX) * frequency, (v + seedOffY) * frequency) * amplitude;
                maxValue += amplitude;
                amplitude *= _noisePersistence;
                frequency *= 2f;
            }

            float n = value / maxValue * 0.5f + 0.5f;
            n = Math.Clamp(n, 0f, 1f);
            return new Vector4(n, n, n, 1f);
        }

        // ────────────────────────────────────────────────────────────
        // Helpers
        // ────────────────────────────────────────────────────────────

        private static readonly HashSet<string> TextureExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".hdr", ".exr"
        };

        private static bool IsTextureFile(string path)
        {
            var ext = Path.GetExtension(path);
            return TextureExtensions.Contains(ext);
        }

        // ────────────────────────────────────────────────────────────
        // Perlin Noise
        // ────────────────────────────────────────────────────────────

        private static readonly int[] Perm = CreatePermutationTable();

        private static int[] CreatePermutationTable()
        {
            int[] p =
            {
                151,160,137,91,90,15,131,13,201,95,96,53,194,233,7,225,
                140,36,103,30,69,142,8,99,37,240,21,10,23,190,6,148,
                247,120,234,75,0,26,197,62,94,252,219,203,117,35,11,32,
                57,177,33,88,237,149,56,87,174,20,125,136,171,168,68,175,
                74,165,71,134,139,48,27,166,77,146,158,231,83,111,229,122,
                60,211,133,230,220,105,92,41,55,46,245,40,244,102,143,54,
                65,25,63,161,1,216,80,73,209,76,132,187,208,89,18,169,
                200,196,135,130,116,188,159,86,164,100,109,198,173,186,3,64,
                52,217,226,250,124,123,5,202,38,147,118,126,255,82,85,212,
                207,206,59,227,47,16,58,17,182,189,28,42,223,183,170,213,
                119,248,152,2,44,154,163,70,221,153,101,155,167,43,172,9,
                129,22,39,253,19,98,108,110,79,113,224,232,178,185,112,104,
                218,246,97,228,251,34,242,193,238,210,144,12,191,179,162,241,
                81,51,145,235,249,14,239,107,49,192,214,31,181,199,106,157,
                184,84,204,176,115,121,50,45,127,4,150,254,138,236,205,93,
                222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180,
            };
            var table = new int[512];
            for (int i = 0; i < 512; i++) table[i] = p[i & 255];
            return table;
        }

        private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);

        private static float Grad(int hash, float x, float y)
        {
            int h = hash & 3;
            float u = h < 2 ? x : y;
            float v = h < 2 ? y : x;
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        private static float PerlinNoise2D(float x, float y)
        {
            int xi = (int)MathF.Floor(x) & 255;
            int yi = (int)MathF.Floor(y) & 255;
            float xf = x - MathF.Floor(x);
            float yf = y - MathF.Floor(y);
            float u = Fade(xf);
            float v = Fade(yf);

            int aa = Perm[Perm[xi] + yi];
            int ab = Perm[Perm[xi] + yi + 1];
            int ba = Perm[Perm[xi + 1] + yi];
            int bb = Perm[Perm[xi + 1] + yi + 1];

            float x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
            float x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);
            return Lerp(x1, x2, v);
        }

        private static float Lerp(float a, float b, float t) => a + t * (b - a);
    }
}
