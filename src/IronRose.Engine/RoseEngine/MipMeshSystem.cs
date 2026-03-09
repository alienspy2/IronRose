using System;

namespace RoseEngine
{
    /// <summary>
    /// 매 프레임 모든 MipMeshFilter의 화면 픽셀 크기를 계산하여
    /// 적절한 LOD를 MeshFilter.mesh에 반영한다.
    /// </summary>
    public static class MipMeshSystem
    {
        /// <summary>Game View 용: Camera.main + Screen.height 기준 LOD 계산.</summary>
        public static void UpdateAllLods()
        {
            var camera = Camera.main;
            if (camera == null) return;

            ComputeLods(camera.transform.position, camera.fieldOfView, Screen.height, sceneView: false);
        }

        /// <summary>Scene View 용: 에디터 카메라 + Scene View 높이 기준 LOD 계산.</summary>
        public static void UpdateLodsForSceneView(Camera camera, float screenHeight)
        {
            ComputeLods(camera.transform.position, camera.fieldOfView, screenHeight, sceneView: true);
        }

        /// <summary>Inspector 표시 전환: 활성 뷰에 따라 currentLod 값을 설정.</summary>
        public static void SetActiveViewForInspector(bool sceneView)
        {
            foreach (var mipFilter in MipMeshFilter._allMipMeshFilters)
                mipFilter.currentLod = sceneView ? mipFilter._sceneViewLod : mipFilter._gameViewLod;
        }

        private static void ComputeLods(Vector3 cameraPos, float fov, float screenHeight, bool sceneView)
        {
            if (screenHeight <= 0) return;

            float fovRad = fov * (MathF.PI / 180f);
            float halfTanFov = MathF.Tan(fovRad * 0.5f);

            foreach (var mipFilter in MipMeshFilter._allMipMeshFilters)
            {
                if (mipFilter.mipMesh == null || mipFilter.mipMesh.LodCount <= 1)
                    continue;
                if (!mipFilter.gameObject.activeInHierarchy)
                    continue;

                var meshFilter = mipFilter.GetComponent<MeshFilter>();
                if (meshFilter == null) continue;

                // 바운딩 스피어 반지름 (월드 스케일 적용)
                var bounds = mipFilter.mipMesh.lodMeshes[0].bounds;
                var scale = mipFilter.transform.lossyScale;
                float maxScale = MathF.Max(MathF.Abs(scale.x),
                                 MathF.Max(MathF.Abs(scale.y), MathF.Abs(scale.z)));
                float worldRadius = bounds.extents.magnitude * maxScale;

                // 카메라까지의 거리
                var worldCenter = mipFilter.transform.TransformPoint(bounds.center);
                float distance = (worldCenter - cameraPos).magnitude;
                if (distance < 0.001f) distance = 0.001f;

                // 화면 픽셀 크기 계산
                float screenRatio = worldRadius / (distance * halfTanFov);
                float screenPixels = screenRatio * screenHeight;

                // LOD 선택: screenPixels 기반 log2
                float continuousLod = MathF.Log2(MathF.Max(screenHeight / MathF.Max(screenPixels, 1f), 1f))
                                      * mipFilter.lodScale
                                      + mipFilter.mipBias;

                int selectedLod = Math.Clamp(
                    (int)MathF.Floor(continuousLod),
                    0,
                    mipFilter.mipMesh.LodCount - 1);

                if (sceneView)
                    mipFilter._sceneViewLod = selectedLod;
                else
                    mipFilter._gameViewLod = selectedLod;

                mipFilter.currentLod = selectedLod;
                meshFilter.mesh = mipFilter.mipMesh.lodMeshes[selectedLod];
            }
        }
    }
}
