using IronRose.AssetPipeline;
using RoseEngine;
using System;
using System.IO;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace IronRose.Engine
{
    /// <summary>
    /// Asset cache warm-up: 프레임당 1개씩 에셋 캐싱.
    /// 메시 에셋은 백그라운드 스레드, 텍스처(GPU 압축)는 메인 스레드에서 처리.
    /// </summary>
    internal class AssetWarmupManager
    {
        private readonly AssetDatabase _assetDatabase;

        private string[]? _warmUpQueue;
        private int _warmUpNext;
        private bool _isWarmingUp;
        private Stopwatch? _warmUpTimer;
        private Task? _backgroundTask;

        public bool IsWarmingUp => _isWarmingUp;

        // 진행 상태 (ImGui 오버레이용)
        public int CurrentIndex => _warmUpNext;
        public int TotalCount => _warmUpQueue?.Length ?? 0;
        public string? CurrentAssetName { get; private set; }
        public double ElapsedSeconds => _warmUpTimer?.Elapsed.TotalSeconds ?? 0;

        /// <summary>에셋 캐시 워밍업 완료 후 콜백.</summary>
        public Action? OnWarmUpComplete { get; set; }

        public AssetWarmupManager(AssetDatabase assetDatabase)
        {
            _assetDatabase = assetDatabase;
        }

        public void Start()
        {
            var uncached = _assetDatabase.GetUncachedAssetPaths();
            if (uncached.Length == 0)
            {
                RoseEngine.EditorDebug.Log("[Engine] All assets already cached, skipping warm-up");
                OnWarmUpComplete?.Invoke();
                return;
            }

            RoseEngine.EditorDebug.Log($"[Engine] Warm-up: {uncached.Length} assets to cache");
            _warmUpQueue = uncached;
            _warmUpNext = 0;
            _isWarmingUp = true;
            _warmUpTimer = Stopwatch.StartNew();
        }

        /// <summary>프레임마다 호출. 백그라운드 태스크 완료 대기 후 다음 에셋 처리.</summary>
        public void ProcessFrame()
        {
            if (_warmUpQueue == null) { Finish(); return; }

            // 백그라운드 태스크가 아직 실행 중이면 다음 프레임에서 재확인
            if (_backgroundTask != null)
            {
                if (!_backgroundTask.IsCompleted)
                    return;

                // 완료됨 — 에러 처리
                if (_backgroundTask.IsFaulted)
                {
                    var ex = _backgroundTask.Exception?.InnerException;
                    RoseEngine.EditorDebug.LogError($"[Engine] Warm-up failed for {CurrentAssetName}: {ex?.Message}");
                }
                _backgroundTask = null;
                _warmUpNext++;
            }

            if (_warmUpNext >= _warmUpQueue.Length)
            {
                Finish();
                return;
            }

            var path = _warmUpQueue[_warmUpNext];
            CurrentAssetName = Path.GetFileName(path);

            if (IsMeshAsset(path))
            {
                // 메시: 백그라운드 스레드 (SharpGLTF/Assimp + meshoptimizer = CPU만 사용)
                _backgroundTask = Task.Run(() => _assetDatabase.EnsureDiskCached(path));
            }
            else
            {
                // 텍스처 등: 메인 스레드 (GPU 텍스처 압축 필요)
                try
                {
                    _assetDatabase.EnsureDiskCached(path);
                }
                catch (Exception ex)
                {
                    RoseEngine.EditorDebug.LogError($"[Engine] Warm-up failed for {path}: {ex.Message}");
                }
                _warmUpNext++;
            }
        }

        private static bool IsMeshAsset(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".glb" or ".gltf" or ".obj" or ".fbx" or ".dae" or ".3ds" or ".blend";
        }

        private void Finish()
        {
            _warmUpTimer?.Stop();
            RoseEngine.EditorDebug.Log($"[Engine] Warm-up complete: {_warmUpQueue?.Length ?? 0} assets cached ({_warmUpTimer?.Elapsed.TotalSeconds:F1}s)");
            _isWarmingUp = false;
            CurrentAssetName = null;
            _warmUpQueue = null;
            _warmUpTimer = null;
            _backgroundTask = null;

            OnWarmUpComplete?.Invoke();
        }
    }
}
