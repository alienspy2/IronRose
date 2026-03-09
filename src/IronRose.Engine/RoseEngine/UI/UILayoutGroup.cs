using System;

namespace RoseEngine
{
    public enum LayoutDirection
    {
        Horizontal,
        Vertical
    }

    public enum LayoutChildAlignment
    {
        UpperLeft,
        UpperCenter,
        UpperRight,
        MiddleLeft,
        MiddleCenter,
        MiddleRight,
        LowerLeft,
        LowerCenter,
        LowerRight
    }

    public class UILayoutGroup : Component
    {
        public LayoutDirection direction = LayoutDirection.Vertical;
        public float spacing = 5f;
        public Vector4 padding; // left, bottom, right, top (px)
        public LayoutChildAlignment childAlignment = LayoutChildAlignment.UpperLeft;
        public bool childForceExpandWidth;
        public bool childForceExpandHeight;

        /// <summary>
        /// 자식 RectTransform들의 anchoredPosition과 sizeDelta를 자동 배치.
        /// CanvasRenderer.RenderNode()에서 호출됨.
        /// </summary>
        public void LayoutChildren()
        {
            var t = gameObject.transform;
            var rt = gameObject.GetComponent<RectTransform>();
            if (rt == null) return;

            int childCount = t.childCount;
            if (childCount == 0) return;

            // 자식 RectTransform 수집
            var children = new RectTransform[childCount];
            int validCount = 0;
            for (int i = 0; i < childCount; i++)
            {
                var child = t.GetChild(i);
                var childRT = child.gameObject.GetComponent<RectTransform>();
                if (childRT != null)
                    children[validCount++] = childRT;
            }
            if (validCount == 0) return;

            // 부모 영역 (패딩 적용)
            float startX = padding.x;   // left
            float startY = padding.w;   // top
            float areaW = -(padding.x + padding.z); // will be added to parent width via sizeDelta
            float areaH = -(padding.w + padding.y); // will be added to parent height

            float cursorX = startX;
            float cursorY = startY;

            for (int i = 0; i < validCount; i++)
            {
                var child = children[i];

                // Set child anchors to top-left for layout control
                child.anchorMin = Vector2.zero;
                child.anchorMax = Vector2.zero;
                child.pivot = new Vector2(0f, 0f);

                float childW = child.sizeDelta.x;
                float childH = child.sizeDelta.y;

                child.anchoredPosition = new Vector2(cursorX, cursorY);

                if (direction == LayoutDirection.Horizontal)
                    cursorX += childW + spacing;
                else
                    cursorY += childH + spacing;
            }
        }
    }
}
