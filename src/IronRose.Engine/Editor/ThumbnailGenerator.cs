using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using RoseEngine;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Veldrid;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 에셋 폴더를 재귀 순회하며 폴더당 1장의 .thumbnails.png 합본 이미지를 생성.
    /// 프레임당 1개 에셋을 처리하여 진행 상황을 모달로 표시.
    /// </summary>
    internal class ThumbnailGenerator : IDisposable
    {
        private const int ThumbSize = 256;
        private const int LabelHeight = 20;
        private const int CellSize = ThumbSize + LabelHeight; // 276

        private static readonly HashSet<string> TextureExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".tga", ".bmp" };

        private static readonly HashSet<string> HdrExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".hdr", ".exr" };

        private static readonly HashSet<string> MeshExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".glb", ".gltf", ".fbx", ".obj" };

        // ── Work queue ──────────────────────────────────────────────
        private string[]? _queue;
        private int _nextIndex;
        private Stopwatch? _timer;
        private string? _outputFolder;

        private GraphicsDevice? _device;
        private MeshPreviewRenderer? _renderer;
        private Mesh? _sphere;

        // 전체 썸네일 셀 누적 (루트 폴더에 한 장으로 합본)
        private readonly List<(string name, Image<Rgba32> thumb)> _cells = new();

        // ── Progress state (ImGui 오버레이용) ────────────────────────
        public bool IsGenerating { get; private set; }
        public int CurrentIndex => _nextIndex;
        public int TotalCount => _queue?.Length ?? 0;
        public string? CurrentAssetName { get; private set; }
        public double ElapsedSeconds => _timer?.Elapsed.TotalSeconds ?? 0;

        public void Start(GraphicsDevice device, string folderPath, bool recursive)
        {
            _device = device;
            var absFolder = Path.GetFullPath(folderPath);
            if (!Directory.Exists(absFolder))
            {
                Debug.LogWarning($"[Thumbnail] Folder not found: {absFolder}");
                return;
            }

            _outputFolder = absFolder;

            // 대상 폴더 수집
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = new List<string>();
            foreach (var file in Directory.EnumerateFiles(absFolder, "*", searchOption))
            {
                if (IsEligible(file))
                    files.Add(file);
            }

            if (files.Count == 0)
            {
                Debug.Log($"[Thumbnail] No eligible assets in {folderPath}");
                return;
            }

            _queue = files.ToArray();
            _nextIndex = 0;
            _cells.Clear();
            _timer = Stopwatch.StartNew();
            IsGenerating = true;

            // Load() 중 OnRoseMetadataSaved → ReimportAsync 억제
            var db = Resources.GetAssetDatabase();
            (db as IronRose.AssetPipeline.AssetDatabase)?.PushImportGuard();

            Debug.Log($"[Thumbnail] Starting: {_queue.Length} assets");
        }

        /// <summary>프레임당 1개 에셋 처리. EngineCore.Update()에서 호출.</summary>
        public void ProcessFrame()
        {
            if (_queue == null || _device == null) { Finish(); return; }
            if (_nextIndex >= _queue.Length) { Finish(); return; }

            var filePath = _queue[_nextIndex];
            CurrentAssetName = Path.GetFileName(filePath);

            try
            {
                var thumb = RenderThumbnailForFile(filePath);
                if (thumb != null)
                    _cells.Add((CurrentAssetName, thumb));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Thumbnail] Failed: {filePath} — {ex.Message}");
            }

            _nextIndex++;
        }

        private void Finish()
        {
            string? savedPath = null;
            try
            {
                if (_cells.Count > 0 && _outputFolder != null)
                {
                    ComposeAndSave(_outputFolder, _cells);
                    savedPath = Path.Combine(_outputFolder, ".thumbnails.png");
                }
            }
            finally
            {
                foreach (var (_, thumb) in _cells)
                    thumb.Dispose();
                _cells.Clear();
            }

            // Import guard 해제
            var db = Resources.GetAssetDatabase();
            (db as IronRose.AssetPipeline.AssetDatabase)?.PopImportGuard();

            _timer?.Stop();
            Debug.Log($"[Thumbnail] Complete: {_queue?.Length ?? 0} assets, {_cells.Count} thumbnails ({_timer?.Elapsed.TotalSeconds:F1}s)");

            IsGenerating = false;
            CurrentAssetName = null;
            _queue = null;
            _outputFolder = null;
            _timer = null;

            // 생성된 썸네일을 OS 기본 뷰어로 열기
            if (savedPath != null)
                OpenWithOS(savedPath);
        }

        // ── OS 기본 뷰어로 파일 열기 ─────────────────────────────────

        internal static void OpenWithOS(string filePath)
        {
            if (!File.Exists(filePath)) return;

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true,
                    });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        Arguments = $"\"{filePath}\"",
                        UseShellExecute = false,
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Thumbnail] Failed to open file: {ex.Message}");
            }
        }

        // ── 파일 적격 판단 ──────────────────────────────────────────

        private static bool IsEligible(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            if (fileName.StartsWith('.')) return false;

            var ext = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(ext)) return false;
            if (ext.Equals(".rose", StringComparison.OrdinalIgnoreCase)) return false;

            return TextureExtensions.Contains(ext)
                || HdrExtensions.Contains(ext)
                || MeshExtensions.Contains(ext)
                || ext.Equals(".mat", StringComparison.OrdinalIgnoreCase);
        }

        // ── 에셋별 썸네일 렌더 ──────────────────────────────────────

        private Image<Rgba32>? RenderThumbnailForFile(string filePath)
        {
            var ext = Path.GetExtension(filePath);

            if (TextureExtensions.Contains(ext) || HdrExtensions.Contains(ext))
                return RenderTextureThumbnail(filePath, HdrExtensions.Contains(ext));

            if (MeshExtensions.Contains(ext))
            {
                _renderer ??= new MeshPreviewRenderer(_device!);
                return RenderMeshThumbnail(_device!, _renderer, filePath);
            }

            if (ext.Equals(".mat", StringComparison.OrdinalIgnoreCase))
            {
                _renderer ??= new MeshPreviewRenderer(_device!);
                _sphere ??= PrimitiveGenerator.CreateSphere();
                return RenderMaterialThumbnail(_device!, _renderer, _sphere, filePath);
            }

            return null;
        }

        // ── 시트 합본 ───────────────────────────────────────────────

        private static SixLabors.Fonts.Font? _labelFont;

        private static SixLabors.Fonts.Font GetLabelFont()
        {
            if (_labelFont != null) return _labelFont;

            var fontPath = Path.Combine(AppContext.BaseDirectory, "EditorAssets", "Fonts", "Roboto.ttf");
            if (!File.Exists(fontPath))
            {
                // fallback: 실행 위치 기준
                fontPath = Path.Combine("EditorAssets", "Fonts", "Roboto.ttf");
            }

            if (File.Exists(fontPath))
            {
                var collection = new FontCollection();
                var family = collection.Add(fontPath);
                _labelFont = family.CreateFont(12, FontStyle.Regular);
            }
            else
            {
                _labelFont = SystemFonts.CreateFont("Arial", 12, FontStyle.Regular);
            }
            return _labelFont;
        }

        private static void ComposeAndSave(string folderPath, List<(string name, Image<Rgba32> thumb)> cells)
        {
            int cols = (int)MathF.Ceiling(MathF.Sqrt(cells.Count));
            int rows = (int)MathF.Ceiling((float)cells.Count / cols);
            int sheetW = cols * CellSize;
            int sheetH = rows * CellSize;

            var font = GetLabelFont();
            var textColor = SixLabors.ImageSharp.Color.FromRgba(200, 200, 200, 255);
            var textOptions = new RichTextOptions(font)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                WrappingLength = ThumbSize,
            };

            using var sheet = new Image<Rgba32>(sheetW, sheetH, new Rgba32(30, 30, 30, 255));

            for (int i = 0; i < cells.Count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                int x = col * CellSize;
                int y = row * CellSize;

                var thumb = cells[i].thumb;
                var name = cells[i].name;

                // 프리뷰 이미지: 셀 상단 ThumbSize 영역 중앙 배치
                int ox = x + (ThumbSize - thumb.Width) / 2;
                int oy = y + (ThumbSize - thumb.Height) / 2;
                sheet.Mutate(ctx => ctx.DrawImage(thumb, new Point(ox, oy), 1f));

                // 파일명 라벨: ThumbSize 아래
                textOptions.Origin = new System.Numerics.Vector2(x + ThumbSize / 2f, y + ThumbSize + 2);
                sheet.Mutate(ctx => ctx.DrawText(textOptions, name, textColor));
            }

            var outputPath = Path.Combine(folderPath, ".thumbnails.png");
            sheet.SaveAsPng(outputPath);
            Debug.Log($"[Thumbnail] Sheet: {outputPath} ({cells.Count} assets, {cols}x{rows})");
        }

        // ── Texture (CPU only) ──────────────────────────────────────

        private static Image<Rgba32>? RenderTextureThumbnail(string srcPath, bool isHdr)
        {
            if (isHdr)
                return RenderHdrThumbnail(srcPath);

            var image = Image.Load<Rgba32>(srcPath);
            ResizeToFit(image, ThumbSize);
            return image;
        }

        private static Image<Rgba32> RenderHdrThumbnail(string srcPath)
        {
            using var hdrImage = Image.Load<RgbaVector>(srcPath);
            var ldrImage = new Image<Rgba32>(hdrImage.Width, hdrImage.Height);

            hdrImage.ProcessPixelRows(ldrImage, (hdrAccessor, ldrAccessor) =>
            {
                for (int y = 0; y < hdrAccessor.Height; y++)
                {
                    var hdrRow = hdrAccessor.GetRowSpan(y);
                    var ldrRow = ldrAccessor.GetRowSpan(y);
                    for (int x = 0; x < hdrAccessor.Width; x++)
                    {
                        var hdr = hdrRow[x];
                        float r = MathF.Pow(Math.Clamp(hdr.R / (1f + hdr.R), 0f, 1f), 1f / 2.2f);
                        float g = MathF.Pow(Math.Clamp(hdr.G / (1f + hdr.G), 0f, 1f), 1f / 2.2f);
                        float b = MathF.Pow(Math.Clamp(hdr.B / (1f + hdr.B), 0f, 1f), 1f / 2.2f);
                        ldrRow[x] = new Rgba32((byte)(r * 255f), (byte)(g * 255f), (byte)(b * 255f), 255);
                    }
                }
            });

            ResizeToFit(ldrImage, ThumbSize);
            return ldrImage;
        }

        private static void ResizeToFit(Image image, int maxSize)
        {
            if (image.Width <= maxSize && image.Height <= maxSize) return;
            float scale = (float)maxSize / Math.Max(image.Width, image.Height);
            int newW = Math.Max(1, (int)(image.Width * scale));
            int newH = Math.Max(1, (int)(image.Height * scale));
            image.Mutate(ctx => ctx.Resize(newW, newH));
        }

        // ── Mesh (GPU) ─────────────────────────────────────────────

        private static Image<Rgba32>? RenderMeshThumbnail(
            GraphicsDevice device, MeshPreviewRenderer renderer, string assetPath)
        {
            var db = Resources.GetAssetDatabase();
            if (db == null) return null;

            var mesh = db.Load<Mesh>(assetPath);
            if (mesh == null) return null;

            renderer.ClearMaterialOverride();
            renderer.SetMesh(mesh);
            mesh.UploadToGPU(device);
            renderer.RenderIfDirty();

            return ReadbackToImage(device, renderer);
        }

        // ── Material (GPU) ──────────────────────────────────────────

        private static Image<Rgba32>? RenderMaterialThumbnail(
            GraphicsDevice device, MeshPreviewRenderer renderer,
            Mesh sphere, string assetPath)
        {
            var db = Resources.GetAssetDatabase();
            if (db == null) return null;

            var mat = db.Load<Material>(assetPath);
            if (mat == null) return null;

            renderer.SetMesh(sphere);
            sphere.UploadToGPU(device);

            var c = mat.color;
            TextureView? tv = null;
            if (mat.mainTexture != null)
            {
                mat.mainTexture.UploadToGPU(device);
                tv = mat.mainTexture.TextureView;
            }
            renderer.SetMaterialOverride(
                new System.Numerics.Vector4(c.r, c.g, c.b, c.a),
                mat.metallic, mat.roughness, tv);
            renderer.RenderIfDirty();

            return ReadbackToImage(device, renderer);
        }

        // ── GPU readback → in-memory image ──────────────────────────

        private static Image<Rgba32>? ReadbackToImage(GraphicsDevice device, MeshPreviewRenderer renderer)
        {
            var colorView = renderer.ColorTextureView;
            if (colorView == null) return null;

            var colorTex = colorView.Target;
            uint w = colorTex.Width;
            uint h = colorTex.Height;

            var staging = device.ResourceFactory.CreateTexture(new TextureDescription(
                w, h, 1, 1, 1, colorTex.Format, TextureUsage.Staging, colorTex.Type));

            var cl = device.ResourceFactory.CreateCommandList();
            cl.Begin();
            cl.CopyTexture(colorTex, staging);
            cl.End();
            device.SubmitCommands(cl);
            device.WaitForIdle();

            var map = device.Map(staging, MapMode.Read);
            int rowPitch = (int)map.RowPitch;
            int width = (int)w;
            int height = (int)h;

            var image = new Image<Rgba32>(width, height);
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    unsafe
                    {
                        var srcPtr = (byte*)map.Data.ToPointer() + (y * rowPitch);
                        for (int x = 0; x < width; x++)
                        {
                            // BGRA → RGBA
                            byte b = srcPtr[x * 4 + 0];
                            byte g = srcPtr[x * 4 + 1];
                            byte r = srcPtr[x * 4 + 2];
                            byte a = srcPtr[x * 4 + 3];
                            row[x] = new Rgba32(r, g, b, a);
                        }
                    }
                }
            });

            device.Unmap(staging);
            staging.Dispose();
            cl.Dispose();

            if (image.Width != ThumbSize || image.Height != ThumbSize)
                image.Mutate(ctx => ctx.Resize(ThumbSize, ThumbSize));

            return image;
        }

        public void Dispose()
        {
            _renderer?.Dispose();
            _renderer = null;
            _sphere = null;

            foreach (var (_, thumb) in _cells)
                thumb.Dispose();
            _cells.Clear();
        }
    }
}
