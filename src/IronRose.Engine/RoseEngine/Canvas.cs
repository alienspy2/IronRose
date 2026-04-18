using System;

namespace RoseEngine
{
    public enum CanvasRenderMode
    {
        ScreenSpaceOverlay,
        ScreenSpaceCamera,
        WorldSpace
    }

    public enum CanvasScaleMode
    {
        ConstantPixelSize,
        ScaleWithScreenSize
    }

    public class Canvas : Component
    {
        public CanvasRenderMode renderMode = CanvasRenderMode.ScreenSpaceOverlay;
        public int sortingOrder;
        public Vector2 referenceResolution = new(1920f, 1080f);
        public CanvasScaleMode scaleMode = CanvasScaleMode.ScaleWithScreenSize;
        public float matchWidthOrHeight = 0.5f;

        internal static readonly ComponentRegistry<Canvas> _allCanvases = new();

        internal override void OnAddedToGameObject()
        {
            ThreadGuard.DebugCheckMainThread("Canvas.Register");
            _allCanvases.Register(this);

            // Canvas가 있는 GO에는 RectTransform이 필수
            if (gameObject.GetComponent<RectTransform>() == null)
                gameObject.AddComponent<RectTransform>();
        }

        internal override void OnComponentDestroy()
        {
            ThreadGuard.DebugCheckMainThread("Canvas.Unregister");
            _allCanvases.Unregister(this);
        }

        internal static void ClearAll() => _allCanvases.Clear();

        /// <summary>Canvas Scaler: 기준 해상도 대비 스크린 크기 비율 계산.</summary>
        public float GetScaleFactor(float screenW, float screenH)
        {
            if (scaleMode == CanvasScaleMode.ConstantPixelSize)
                return 1f;

            if (referenceResolution.x <= 0 || referenceResolution.y <= 0)
                return 1f;

            float logW = MathF.Log2(screenW / referenceResolution.x);
            float logH = MathF.Log2(screenH / referenceResolution.y);
            return MathF.Pow(2f, logW + (logH - logW) * matchWidthOrHeight);
        }
    }
}
