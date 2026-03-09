using System.Collections.Generic;

namespace RoseEngine
{
    /// <summary>
    /// Post Processing Volume 컴포넌트.
    /// BoxCollider와 함께 사용하여 카메라가 Volume 안에 있을 때 PP 적용.
    /// blendDistance로 경계에서 부드러운 페이드 인/아웃.
    /// </summary>
    public class PostProcessVolume : MonoBehaviour
    {
        internal static readonly List<PostProcessVolume> _allVolumes = new();

        /// <summary>블렌드 거리 (Volume 외벽에서 이 거리 안에서 페이드).</summary>
        public float blendDistance { get; set; } = 0f;

        /// <summary>Volume 가중치 (0~1).</summary>
        public float weight { get; set; } = 1f;

        /// <summary>연결된 PostProcessProfile.</summary>
        public PostProcessProfile? profile { get; set; }

        /// <summary>프로파일 에셋 GUID (직렬화용).</summary>
        [HideInInspector]
        public string? profileGuid { get; set; }

        internal override void OnAddedToGameObject()
        {
            _allVolumes.Add(this);
        }

        internal override void OnComponentDestroy()
        {
            _allVolumes.Remove(this);
        }

        internal static void ClearAll() => _allVolumes.Clear();

        /// <summary>BoxCollider의 inner bounds + blendDistance 확장된 outer bounds.</summary>
        public Bounds GetInflatedBounds()
        {
            var box = gameObject?.GetComponent<BoxCollider>();
            if (box == null) return default;

            var worldCenter = transform.TransformPoint(box.center);
            var scale = transform.lossyScale;
            var worldSize = new Vector3(
                box.size.x * Mathf.Abs(scale.x),
                box.size.y * Mathf.Abs(scale.y),
                box.size.z * Mathf.Abs(scale.z));

            var bounds = new Bounds(worldCenter, worldSize);
            bounds.Expand(blendDistance * 2f);
            return bounds;
        }

        /// <summary>BoxCollider의 월드 공간 inner bounds.</summary>
        public Bounds GetInnerBounds()
        {
            var box = gameObject?.GetComponent<BoxCollider>();
            if (box == null) return default;

            var worldCenter = transform.TransformPoint(box.center);
            var scale = transform.lossyScale;
            var worldSize = new Vector3(
                box.size.x * Mathf.Abs(scale.x),
                box.size.y * Mathf.Abs(scale.y),
                box.size.z * Mathf.Abs(scale.z));

            return new Bounds(worldCenter, worldSize);
        }
    }
}
