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
