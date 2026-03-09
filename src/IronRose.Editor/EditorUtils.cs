using RoseEngine;

namespace IronRose.Editor
{
    /// <summary>에디터/데모 공통 유틸리티 — 카메라 생성, 폰트 로딩 보일러플레이트 제거용.</summary>
    public static class EditorUtils
    {
        /// <summary>카메라 생성. lookAt이 null이면 LookAt 생략.</summary>
        public static (Camera cam, Transform transform) CreateCamera(
            Vector3 position, Vector3? lookAt = null,
            CameraClearFlags clearFlags = CameraClearFlags.Skybox,
            Color? backgroundColor = null)
        {
            var camObj = new GameObject("Main Camera");
            var cam = camObj.AddComponent<Camera>();
            cam.clearFlags = clearFlags;
            if (backgroundColor.HasValue)
                cam.backgroundColor = backgroundColor.Value;
            camObj.transform.position = position;
            if (lookAt.HasValue)
                camObj.transform.LookAt(lookAt.Value);
            return (cam, camObj.transform);
        }

        /// <summary>NotoSans 폰트 로드 (fallback: CreateDefault).</summary>
        public static Font LoadFont(int size = 32)
        {
            var fontPath = System.IO.Path.Combine(
                System.IO.Directory.GetCurrentDirectory(), "Assets", "Fonts", "NotoSans_eng.ttf");
            try { return Font.CreateFromFile(fontPath, size); }
            catch { return Font.CreateDefault(size); }
        }

        /// <summary>기본 빈 씬 카메라 생성 (에디터 기본 시작용).</summary>
        public static Camera CreateDefaultSceneCamera()
        {
            var (cam, _) = CreateCamera(
                new Vector3(0, 1, -5),
                lookAt: Vector3.zero,
                clearFlags: CameraClearFlags.Skybox);
            return cam;
        }

        /// <summary>
        /// 기본 씬 오브젝트 세트 생성:
        /// Main Camera, Cube, Plane, Spot Light.
        /// </summary>
        public static void CreateDefaultScene()
        {
            // 1. Main Camera
            CreateCamera(
                new Vector3(0, 3, -6),
                lookAt: Vector3.zero,
                clearFlags: CameraClearFlags.Skybox);

            // 2. Cube — 중앙, y=0.5 (바닥 위)
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Cube";
            cube.transform.position = new Vector3(0, 0.5f, 0);

            // 3. Plane — 바닥, 원점
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.name = "Plane";
            plane.transform.position = Vector3.zero;

            // 4. Spot Light — 위에서 아래로 비추는 스팟라이트
            var lightObj = new GameObject("Spot Light");
            var light = lightObj.AddComponent<Light>();
            light.type = LightType.Spot;
            light.color = Color.white;
            light.intensity = 2f;
            light.range = 15f;
            light.spotAngle = 30f;
            light.spotOuterAngle = 45f;
            light.shadows = true;
            lightObj.transform.position = new Vector3(0, 5, -2);
            lightObj.transform.LookAt(Vector3.zero);
        }
    }
}
