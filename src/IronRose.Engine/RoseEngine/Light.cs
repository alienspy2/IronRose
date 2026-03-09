using System;
using System.Collections.Generic;

namespace RoseEngine
{
    public enum LightType
    {
        Directional = 0,
        Point = 1,
        Spot = 2,
    }

    public enum ShadowCullMode
    {
        Front = 0,
        Back = 1,
        TwoFace = 2,
    }

    public class Light : Component
    {
        public Color color { get; set; } = Color.white;
        public float intensity { get; set; } = 1f;
        public float range { get; set; } = 10f;
        public float rangeNear { get; set; } = 0f;
        public LightType type { get; set; } = LightType.Directional;
        public bool enabled { get; set; } = true;

        // Spot Light — angles clamped to (0, 179] to prevent tan(90°)/perspective blow-up
        private float _spotAngle = 30f;
        private float _spotOuterAngle = 45f;

        public float spotAngle
        {
            get => _spotAngle;
            set => _spotAngle = Math.Clamp(value, 0.1f, 179f);
        }

        public float spotOuterAngle
        {
            get => _spotOuterAngle;
            set => _spotOuterAngle = Math.Clamp(value, 0.1f, 179f);
        }

        // Shadow
        public bool shadows { get; set; } = true;
        [IntDropdown(64, 128, 256, 512, 1024, 2048)]
        public int shadowResolution { get; set; } = 1024;
        public float shadowBias { get; set; } = 0.001f;
        public float shadowNormalBias { get; set; } = 0.001f;
        public float shadowNearPlane { get; set; } = 0.1f;
        public ShadowCullMode shadowCullMode { get; set; } = ShadowCullMode.TwoFace;
        public float shadowSoftness { get; set; } = 1.0f;   // PCSS light size (0=hard, higher=softer penumbra)

        internal static readonly List<Light> _allLights = new();

        internal override void OnAddedToGameObject() => _allLights.Add(this);
        internal override void OnComponentDestroy() => _allLights.Remove(this);
        internal static void ClearAll() => _allLights.Clear();

        // ── Gizmos ──

        public override void OnDrawGizmos()
        {
            if (!enabled) return;

            var pos = transform.position;
            var right = transform.rotation * Vector3.right;
            var up = transform.rotation * Vector3.up;
            var forward = transform.rotation * Vector3.forward;

            var lightColor = new Color(color.r, color.g, color.b, 1f);
            float luminance = lightColor.r * 0.299f + lightColor.g * 0.587f + lightColor.b * 0.114f;
            if (luminance < 0.3f)
                lightColor = new Color(
                    Math.Max(lightColor.r, 0.4f),
                    Math.Max(lightColor.g, 0.4f),
                    Math.Max(lightColor.b, 0.4f), 1f);

            Gizmos.color = lightColor;

            const float r = 0.3f;
            const float rayLen = 0.25f;
            const int rays = 8;

            // Circle facing forward
            Gizmos.DrawWireCircle(pos, right, up, r);

            // Rays radiating outward
            for (int i = 0; i < rays; i++)
            {
                float angle = (float)i / rays * MathF.PI * 2f;
                var dir = right * MathF.Cos(angle) + up * MathF.Sin(angle);
                var outer = pos + dir * r;
                Gizmos.DrawLine(outer, outer + dir * rayLen);
            }

            // // Short direction indicator
            Gizmos.DrawLine(pos, pos + forward * (r + rayLen));
        }

        public override void OnDrawGizmosSelected()
        {
            if (!enabled) return;

            var pos = transform.position;
            var forward = transform.rotation * Vector3.forward;
            var right = transform.rotation * Vector3.right;
            var up = transform.rotation * Vector3.up;

            var lightColor = new Color(color.r, color.g, color.b, 1f);
            float luminance = lightColor.r * 0.299f + lightColor.g * 0.587f + lightColor.b * 0.114f;
            if (luminance < 0.3f)
                lightColor = new Color(
                    Math.Max(lightColor.r, 0.4f),
                    Math.Max(lightColor.g, 0.4f),
                    Math.Max(lightColor.b, 0.4f), 1f);

            Gizmos.color = lightColor;
            Gizmos.matrix = Matrix4x4.identity;

            switch (type)
            {
                case LightType.Directional:
                    DrawDirectionalGizmo(pos, forward, right, up);
                    break;
                case LightType.Point:
                    Gizmos.DrawWireSphere(pos, range);
                    break;
                case LightType.Spot:
                    DrawSpotGizmo(pos, forward);
                    break;
            }
        }

        private void DrawDirectionalGizmo(Vector3 pos, Vector3 forward, Vector3 right, Vector3 up)
        {
            const float circleRadius = 0.5f;
            const float rayLength = 1.5f;
            const int rayCount = 8;

            Gizmos.DrawWireCircle(pos, right, up, circleRadius);

            for (int i = 0; i < rayCount; i++)
            {
                float angle = (float)i / rayCount * MathF.PI * 2f;
                var edgePoint = pos + (right * MathF.Cos(angle) + up * MathF.Sin(angle)) * circleRadius;
                Gizmos.DrawLine(edgePoint, edgePoint + forward * rayLength);
                Gizmos.DrawLine(edgePoint, edgePoint - forward * rayLength * 0.3f);
            }

            Gizmos.DrawLine(pos, pos + forward * (circleRadius + rayLength));
        }

        private void DrawSpotGizmo(Vector3 pos, Vector3 forward)
        {
            var perp1 = GetPerpendicular(forward);
            var perp2 = Vector3.Cross(forward, perp1).normalized;

            float outerHalfRad = spotOuterAngle * 0.5f * Mathf.Deg2Rad;

            // Outer cone at range
            float outerRadius = MathF.Tan(outerHalfRad) * range;
            var baseCenter = pos + forward * range;
            Gizmos.DrawWireCircle(baseCenter, perp1, perp2, outerRadius);

            // Near circle at rangeNear
            if (rangeNear > 0.001f)
            {
                float nearRadius = MathF.Tan(outerHalfRad) * rangeNear;
                var nearCenter = pos + forward * rangeNear;
                Gizmos.DrawWireCircle(nearCenter, perp1, perp2, nearRadius);
            }

            // 4 edge lines from apex to outer base circle
            for (int i = 0; i < 4; i++)
            {
                float a = i * MathF.PI * 0.5f;
                var basePoint = baseCenter + (perp1 * MathF.Cos(a) + perp2 * MathF.Sin(a)) * outerRadius;
                Gizmos.DrawLine(pos, basePoint);
            }
        }

        private static Vector3 GetPerpendicular(Vector3 dir)
        {
            var absDir = new Vector3(MathF.Abs(dir.x), MathF.Abs(dir.y), MathF.Abs(dir.z));
            Vector3 helper = absDir.x < 0.9f ? Vector3.right : Vector3.up;
            return Vector3.Cross(dir, helper).normalized;
        }
    }
}
