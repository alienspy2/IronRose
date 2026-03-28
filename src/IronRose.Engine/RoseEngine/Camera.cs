// ------------------------------------------------------------
// @file    Camera.cs
// @brief   Unity 호환 Camera 컴포넌트. FOV/클립 플레인 설정, 뷰/프로젝션 매트릭스 생성,
//          ScreenPointToRay를 통한 화면좌표-월드레이 변환, 기즈모 렌더링을 담당한다.
// @deps    Component, Transform, Vector3, Matrix4x4, Mathf, Screen, Ray, Color, Gizmos
// @exports
//   enum CameraClearFlags { Skybox, SolidColor }
//   class Camera : Component
//     fieldOfView: float                                    — 수직 시야각 (도)
//     nearClipPlane: float                                  — 근거리 클립 평면
//     farClipPlane: float                                   — 원거리 클립 평면
//     clearFlags: CameraClearFlags                          — 배경 클리어 모드
//     backgroundColor: Color                                — SolidColor 모드 배경색
//     static main: Camera?                                  — 메인 카메라 (자동 등록)
//     GetViewMatrix(): Matrix4x4                            — 뷰 매트릭스 반환
//     GetProjectionMatrix(float aspectRatio): Matrix4x4     — 프로젝션 매트릭스 반환
//     ScreenPointToRay(Vector3 screenPoint): Ray            — 화면 좌표(좌하단 원점)를 월드 레이로 변환
//     internal static ClearMain(): void                     — main 참조 초기화
// @note    main은 에디터 내부 오브젝트가 아닌 첫 번째 Camera가 자동 등록된다.
//          ScreenPointToRay는 Screen.width/height를 사용하므로 Screen이 초기화된 후 호출해야 한다.
// ------------------------------------------------------------
using System;

namespace RoseEngine
{
    public enum CameraClearFlags
    {
        Skybox = 1,
        SolidColor = 2,
    }

    public class Camera : Component
    {
        public float fieldOfView = 60f;
        public float nearClipPlane = 0.1f;
        public float farClipPlane = 1000f;

        public CameraClearFlags clearFlags = CameraClearFlags.Skybox;
        public Color backgroundColor = new Color(0.192f, 0.302f, 0.475f, 1f);

        public static Camera? main { get; internal set; }

        internal override void OnAddedToGameObject()
        {
            if (main == null && !gameObject._isEditorInternal)
                main = this;
        }

        internal override void OnComponentDestroy()
        {
            if (main == this)
                main = null;
        }

        public Matrix4x4 GetViewMatrix()
        {
            return Matrix4x4.LookAt(
                transform.position,
                transform.position + transform.forward,
                transform.up);
        }

        public Matrix4x4 GetProjectionMatrix(float aspectRatio)
        {
            return Matrix4x4.Perspective(fieldOfView, aspectRatio, nearClipPlane, farClipPlane);
        }

        internal static void ClearMain()
        {
            main = null;
        }

        /// <summary>
        /// 화면 좌표를 월드 레이로 변환. Unity 호환 API.
        /// screenPoint: 화면 좌표 (픽셀, 좌하단 원점 — Unity 컨벤션)
        /// </summary>
        public Ray ScreenPointToRay(Vector3 screenPoint)
        {
            float screenW = Screen.width;
            float screenH = Screen.height;
            float aspect = screenW / screenH;

            // 화면 좌표 (좌하단 원점) → NDC (-1 ~ 1)
            float ndcX = (screenPoint.x / screenW) * 2f - 1f;
            float ndcY = (screenPoint.y / screenH) * 2f - 1f;

            // NDC → 뷰 공간 방향
            float tanHalfFov = MathF.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
            float viewX = ndcX * aspect * tanHalfFov;
            float viewY = ndcY * tanHalfFov;

            // 뷰 공간 → 월드 공간
            var forward = transform.forward;
            var right = transform.right;
            var up = transform.up;

            var dir = (right * viewX + up * viewY + forward).normalized;
            return new Ray(transform.position, dir);
        }

        // ── Gizmos ──

        public override void OnDrawGizmos()
        {
            // Small camera icon (always visible for picking)
            Gizmos.color = Color.white;
            Gizmos.matrix = Matrix4x4.identity;

            var pos = transform.position;
            var right = transform.rotation * Vector3.right;
            var up = transform.rotation * Vector3.up;
            var forward = transform.rotation * Vector3.forward;

            // Draw a small wireframe box representing the camera body
            const float s = 0.3f;
            var c = pos - forward * s * 0.5f;
            var rx = right * s * 0.5f;
            var ry = up * s * 0.4f;
            var rz = forward * s * 0.5f;

            // Front face
            Gizmos.DrawLine(c - rx + ry + rz, c + rx + ry + rz);
            Gizmos.DrawLine(c + rx + ry + rz, c + rx - ry + rz);
            Gizmos.DrawLine(c + rx - ry + rz, c - rx - ry + rz);
            Gizmos.DrawLine(c - rx - ry + rz, c - rx + ry + rz);
            // Back face
            Gizmos.DrawLine(c - rx + ry - rz, c + rx + ry - rz);
            Gizmos.DrawLine(c + rx + ry - rz, c + rx - ry - rz);
            Gizmos.DrawLine(c + rx - ry - rz, c - rx - ry - rz);
            Gizmos.DrawLine(c - rx - ry - rz, c - rx + ry - rz);
            // Connecting edges
            Gizmos.DrawLine(c - rx + ry - rz, c - rx + ry + rz);
            Gizmos.DrawLine(c + rx + ry - rz, c + rx + ry + rz);
            Gizmos.DrawLine(c + rx - ry - rz, c + rx - ry + rz);
            Gizmos.DrawLine(c - rx - ry - rz, c - rx - ry + rz);

            // "Up" triangle
            float triW = s * 0.25f;
            float triH = s * 0.2f;
            var triBase = c + ry;
            Gizmos.DrawLine(triBase - right * triW, triBase + right * triW);
            Gizmos.DrawLine(triBase - right * triW, triBase + up * triH);
            Gizmos.DrawLine(triBase + right * triW, triBase + up * triH);
        }

        public override void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.white;
            Gizmos.matrix = Matrix4x4.identity;

            var pos = transform.position;
            var right = transform.rotation * Vector3.right;
            var up = transform.rotation * Vector3.up;
            var forward = transform.rotation * Vector3.forward;

            const float aspect = 16f / 9f;
            const float maxVizDist = 50f;

            float clampedFov = MathF.Min(fieldOfView, 170f);
            float tanHalfFov = MathF.Tan(clampedFov * 0.5f * Mathf.Deg2Rad);

            float vizNear = nearClipPlane;
            float vizFar = MathF.Min(farClipPlane, maxVizDist);
            if (vizNear >= vizFar) return;

            float nearH = vizNear * tanHalfFov;
            float nearW = nearH * aspect;
            float farH = vizFar * tanHalfFov;
            float farW = farH * aspect;

            // Near plane corners
            var nc = pos + forward * vizNear;
            var ntl = nc + up * nearH - right * nearW;
            var ntr = nc + up * nearH + right * nearW;
            var nbl = nc - up * nearH - right * nearW;
            var nbr = nc - up * nearH + right * nearW;

            // Far plane corners
            var fc = pos + forward * vizFar;
            var ftl = fc + up * farH - right * farW;
            var ftr = fc + up * farH + right * farW;
            var fbl = fc - up * farH - right * farW;
            var fbr = fc - up * farH + right * farW;

            // Near plane rectangle
            Gizmos.DrawLine(ntl, ntr);
            Gizmos.DrawLine(ntr, nbr);
            Gizmos.DrawLine(nbr, nbl);
            Gizmos.DrawLine(nbl, ntl);

            // Far plane rectangle
            Gizmos.DrawLine(ftl, ftr);
            Gizmos.DrawLine(ftr, fbr);
            Gizmos.DrawLine(fbr, fbl);
            Gizmos.DrawLine(fbl, ftl);

            // Connecting edges
            Gizmos.DrawLine(ntl, ftl);
            Gizmos.DrawLine(ntr, ftr);
            Gizmos.DrawLine(nbl, fbl);
            Gizmos.DrawLine(nbr, fbr);
        }
    }
}
