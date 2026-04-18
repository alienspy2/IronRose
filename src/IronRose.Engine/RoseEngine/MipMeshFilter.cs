namespace RoseEngine
{
    public class MipMeshFilter : Component
    {
        /// <summary>LOD 체인 데이터.</summary>
        public MipMesh? mipMesh { get; set; }

        /// <summary>
        /// LOD 선택 바이어스 (가산). 텍스처 mip bias와 동일 개념.
        /// 0 = 기본, 음수 = 높은 LOD(품질 우선), 양수 = 낮은 LOD(성능 우선)
        /// </summary>
        [Range(-30f, 30f)]
        [Tooltip("Additive LOD bias. Negative = higher quality, Positive = better performance")]
        public float mipBias = 0f;

        /// <summary>
        /// LOD 전환 거리 스케일 (승산). continuousLod에 곱해진다.
        /// 1.0 = 기본, &lt;1.0 = 멀리서도 높은 LOD 유지, &gt;1.0 = 가까이서도 낮은 LOD 사용
        /// </summary>
        [Range(0.1f, 20f)]
        [Tooltip("Multiplicative LOD scale. <1 = keep quality longer, >1 = drop LOD faster")]
        public float lodScale = 7f;

        /// <summary>현재 프레임에 선택된 LOD 레벨 (디버그/Inspector 표시용).</summary>
        [ReadOnlyInInspector]
        public int currentLod;

        // Per-view LOD (Inspector 표시 전환용)
        internal int _gameViewLod;
        internal int _sceneViewLod;

        // 전역 레지스트리 (매 프레임 LOD 업데이트용)
        internal static readonly ComponentRegistry<MipMeshFilter> _allMipMeshFilters = new();

        internal override void OnAddedToGameObject()
        {
            ThreadGuard.DebugCheckMainThread("MipMeshFilter.Register");
            _allMipMeshFilters.Register(this);
        }

        internal override void OnComponentDestroy()
        {
            ThreadGuard.DebugCheckMainThread("MipMeshFilter.Unregister");
            _allMipMeshFilters.Unregister(this);
        }

        internal static void ClearAll() => _allMipMeshFilters.Clear();
    }
}
