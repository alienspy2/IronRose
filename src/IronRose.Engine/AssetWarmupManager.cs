// ------------------------------------------------------------
// @file    AssetWarmupManager.cs
// @brief   에디터 기동 시 캐시가 없는 에셋을 프레임당 1개씩 디스크에 캐싱한다.
//          Phase 2: 텍스처도 백그라운드 Task.Run 경로로 전환하여 메인 UI freeze 를 제거한다.
//          메시는 기존과 동일하게 Task.Run(EnsureDiskCached). 텍스처는 Task.Run(PrepareTextureWarmupBackground)
//          으로 CLI/CPU 압축까지 백그라운드에서 수행하고, 완료 프레임에 FinalizeTextureWarmupOnMain 에서
//          GPU 마무리 + 디스크 저장을 메인 스레드에서 처리한다.
// @deps    IronRose.AssetPipeline (AssetDatabase, WarmupHandoff), RoseEngine (EditorDebug, ThreadGuard)
// @exports
//   internal class AssetWarmupManager
//     AssetWarmupManager(AssetDatabase)                      — 생성자
//     Start(): void                                           — 워밍업 큐 초기화
//     ProcessFrame(): void                                    — 매 프레임 호출 (메인 전용)
//     IsWarmingUp: bool                                       — 진행 여부
//     CurrentIndex / TotalCount / CurrentAssetName / ElapsedSeconds — 프로그레스 UI용
//     OnWarmUpComplete: Action?                               — 완료 콜백
// @note    한 프레임에 _meshBackgroundTask 또는 _textureBackgroundTask 중 하나만 active 하다 (단일 레인).
//          프레임당 하나의 에셋만 처리되는 기존 UX 유지 → 프로그레스 바 로직 변화 없음.
//          백그라운드 Task 예외는 Task.IsFaulted 분기에서 잡고, WarmupHandoff.Error 는
//          FinalizeTextureWarmupOnMain 내부에서 로깅한다. 어떤 실패도 crash 를 유발하지 않고
//          다음 에셋으로 진행.
// ------------------------------------------------------------
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
    /// 메시/텍스처 모두 백그라운드 Task 로 수행하고, 텍스처는 다음 프레임 메인에서 GPU 마무리.
    /// </summary>
    internal class AssetWarmupManager
    {
        private readonly AssetDatabase _assetDatabase;

        private string[]? _warmUpQueue;
        private int _warmUpNext;
        private bool _isWarmingUp;
        private Stopwatch? _warmUpTimer;

        // 메시 워밍업: Task 는 void, 내부에서 _assetDatabase.EnsureDiskCached 를 동기 호출.
        private Task? _meshBackgroundTask;

        // 텍스처 워밍업: Task<WarmupHandoff> 로 받아 다음 프레임 메인에서 Finalize.
        private Task<WarmupHandoff>? _textureBackgroundTask;

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
            // ProcessFrame 은 EngineCore.Update 에서만 호출되지만, 방어적으로 메인 검증.
            // 위반 시 즉시 return 하여 _warmUpNext 가 엉키지 않도록 한다.
            if (!ThreadGuard.CheckMainThread("AssetWarmupManager.ProcessFrame"))
                return;

            if (_warmUpQueue == null) { Finish(); return; }

            // 1. 진행 중 Task 확인 (메시 또는 텍스처 중 하나).
            if (_meshBackgroundTask != null)
            {
                if (!_meshBackgroundTask.IsCompleted)
                    return;

                if (_meshBackgroundTask.IsFaulted)
                {
                    var ex = _meshBackgroundTask.Exception?.InnerException;
                    RoseEngine.EditorDebug.LogError($"[Engine] Warm-up (mesh) failed for {CurrentAssetName}: {ex?.Message}");
                }
                _meshBackgroundTask = null;
                _warmUpNext++;
            }
            else if (_textureBackgroundTask != null)
            {
                if (!_textureBackgroundTask.IsCompleted)
                    return;

                if (_textureBackgroundTask.IsFaulted)
                {
                    var ex = _textureBackgroundTask.Exception?.InnerException;
                    RoseEngine.EditorDebug.LogError($"[Engine] Warm-up (texture, bg) failed for {CurrentAssetName}: {ex?.Message}");
                    _textureBackgroundTask = null;
                    _warmUpNext++;
                }
                else
                {
                    var handoff = _textureBackgroundTask.Result;
                    _textureBackgroundTask = null;

                    try
                    {
                        _assetDatabase.FinalizeTextureWarmupOnMain(handoff);
                    }
                    catch (Exception ex)
                    {
                        // FinalizeTextureWarmupOnMain 내부에서 대부분의 예외를 이미 잡아 로그 처리하지만,
                        // 방어적으로 한 번 더 catch 하여 warmup 진행이 멈추지 않도록 한다.
                        RoseEngine.EditorDebug.LogError($"[Engine] Warm-up (texture, finalize) failed for {CurrentAssetName}: {ex.Message}");
                    }
                    _warmUpNext++;
                }
            }

            // 2. 다음 에셋 큐잉.
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
                _meshBackgroundTask = Task.Run(() => _assetDatabase.EnsureDiskCached(path));
            }
            else
            {
                // 텍스처(TextureImporter 외 기타 에셋은 PrepareTextureWarmupBackground 내부에서
                // importerType 체크 후 Skip-handoff 반환 → 다음 프레임 즉시 _warmUpNext++).
                _textureBackgroundTask = Task.Run(() => _assetDatabase.PrepareTextureWarmupBackground(path));
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
            _meshBackgroundTask = null;
            _textureBackgroundTask = null;

            OnWarmUpComplete?.Invoke();
        }
    }
}
