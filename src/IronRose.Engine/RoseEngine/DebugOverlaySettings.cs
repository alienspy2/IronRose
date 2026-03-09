namespace RoseEngine
{
    public enum DebugOverlay
    {
        None,
        GBuffer,
        ShadowMap,
    }

    public static class DebugOverlaySettings
    {
        /// <summary>와이어프레임 오버레이 표시 여부 (기본 false)</summary>
        public static bool wireframe { get; set; } = false;

        /// <summary>와이어프레임 색상 (기본 검정)</summary>
        public static Color wireframeColor { get; set; } = Color.black;

        /// <summary>디버그 오버레이 모드 (기본 None)</summary>
        public static DebugOverlay overlay { get; set; } = DebugOverlay.None;
    }
}
