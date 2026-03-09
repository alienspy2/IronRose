using System;

namespace RoseEngine
{
    /// <summary>
    /// RectTransform 축.
    /// </summary>
    public enum Axis { Horizontal = 0, Vertical = 1 }

    /// <summary>
    /// 부모 사각형의 가장자리.
    /// </summary>
    public enum Edge { Left = 0, Right = 1, Top = 2, Bottom = 3 }

    /// <summary>
    /// Unity 호환 RectTransform.
    /// 2D UI 레이아웃용 Transform 대체 컴포넌트.
    /// 좌표계: Y-down (ImGui 네이티브). (0,0) = 좌상단.
    ///
    /// 핵심 저장 필드: anchorMin, anchorMax, anchoredPosition, sizeDelta, pivot
    /// 파생 프로퍼티: offsetMin, offsetMax, rect
    /// </summary>
    public class RectTransform : Component
    {
        /// <summary>마지막 CanvasRenderer.RenderAll()에서 계산된 스크린 좌표 Rect.</summary>
        internal Rect lastScreenRect { get; set; }

        // ── 핵심 저장 필드 ──────────────────────────────────────

        public Vector2 anchorMin = Vector2.zero;
        public Vector2 anchorMax = new(1f, 1f);
        public Vector2 anchoredPosition = Vector2.zero;
        public Vector2 sizeDelta = Vector2.zero;
        public Vector2 pivot = new(0.5f, 0.5f);

        // ── 파생 프로퍼티: offsetMin / offsetMax ────────────────
        // offsetMin = anchorMin 기준점에서 사각형의 좌상단 꼭짓점까지의 오프셋
        // offsetMax = anchorMax 기준점에서 사각형의 우하단 꼭짓점까지의 오프셋

        /// <summary>
        /// anchorMin 기준점에서 사각형 좌상단 모서리까지의 오프셋.
        /// offsetMin = anchoredPosition - Scale(sizeDelta, pivot)
        /// </summary>
        public Vector2 offsetMin
        {
            get => anchoredPosition - Vector2.Scale(sizeDelta, pivot);
            set
            {
                Vector2 offset = value - (anchoredPosition - Vector2.Scale(sizeDelta, pivot));
                sizeDelta -= offset;
                anchoredPosition += Vector2.Scale(offset, Vector2.one - pivot);
            }
        }

        /// <summary>
        /// anchorMax 기준점에서 사각형 우하단 모서리까지의 오프셋.
        /// offsetMax = anchoredPosition + Scale(sizeDelta, one - pivot)
        /// </summary>
        public Vector2 offsetMax
        {
            get => anchoredPosition + Vector2.Scale(sizeDelta, Vector2.one - pivot);
            set
            {
                Vector2 offset = value - (anchoredPosition + Vector2.Scale(sizeDelta, Vector2.one - pivot));
                sizeDelta += offset;
                anchoredPosition += Vector2.Scale(offset, pivot);
            }
        }

        // ── rect 프로퍼티 ───────────────────────────────────────
        // 로컬 공간의 사각형 (피벗 기준). GetWorldRect() 호출 시 캐시됨.

        private Rect _cachedRect;

        /// <summary>
        /// 로컬 공간의 사각형. GetWorldRect() 호출 후 유효.
        /// 피벗을 원점으로 한 로컬 좌표.
        /// </summary>
        public Rect rect => _cachedRect;

        // ── 부모 크기 조회 헬퍼 ─────────────────────────────────

        /// <summary>
        /// 부모 RectTransform의 크기를 반환. 부모가 없거나 RectTransform이 없으면 zero.
        /// Canvas의 referenceResolution이나 화면 크기와는 무관 — 순수 논리적 크기.
        /// </summary>
        public Vector2 GetParentSize()
        {
            var parentGo = gameObject.transform.parent?.gameObject;
            if (parentGo == null) return Vector2.zero;

            var parentRt = parentGo.GetComponent<RectTransform>();
            if (parentRt != null)
                return parentRt._cachedRect.size;

            // Canvas 루트인 경우 캐시된 rect가 아직 없을 수 있음
            return Vector2.zero;
        }

        // ── GetWorldRect ────────────────────────────────────────

        /// <summary>
        /// 부모 Rect 기준으로 최종 월드 Rect 계산.
        /// 좌표계: Y-down (ImGui 네이티브). (0,0) = 좌상단.
        /// </summary>
        public Rect GetWorldRect(Rect parentRect)
        {
            // Anchor region within parent
            float anchorX = parentRect.x + anchorMin.x * parentRect.width;
            float anchorY = parentRect.y + anchorMin.y * parentRect.height;
            float anchorW = (anchorMax.x - anchorMin.x) * parentRect.width;
            float anchorH = (anchorMax.y - anchorMin.y) * parentRect.height;

            // Final size = anchor region size + sizeDelta
            float finalW = anchorW + sizeDelta.x;
            float finalH = anchorH + sizeDelta.y;

            // Position = anchor center + offset - pivot * size
            float centerX = anchorX + anchorW * 0.5f;
            float centerY = anchorY + anchorH * 0.5f;

            float posX = centerX + anchoredPosition.x - pivot.x * finalW;
            float posY = centerY + anchoredPosition.y - pivot.y * finalH;

            // 로컬 rect 캐시 (피벗 기준)
            _cachedRect = new Rect(-pivot.x * finalW, -pivot.y * finalH, finalW, finalH);

            return new Rect(posX, posY, finalW, finalH);
        }

        // ── GetLocalCorners ─────────────────────────────────────

        /// <summary>
        /// 로컬 공간의 네 모서리 좌표. 피벗 기준.
        /// [0]=좌상, [1]=좌하, [2]=우하, [3]=우상 (Y-down)
        /// </summary>
        public void GetLocalCorners(Vector3[] fourCornersArray)
        {
            if (fourCornersArray == null || fourCornersArray.Length < 4)
                return;

            Rect r = _cachedRect;
            fourCornersArray[0] = new Vector3(r.x, r.y, 0f);              // 좌상
            fourCornersArray[1] = new Vector3(r.x, r.y + r.height, 0f);   // 좌하
            fourCornersArray[2] = new Vector3(r.x + r.width, r.y + r.height, 0f); // 우하
            fourCornersArray[3] = new Vector3(r.x + r.width, r.y, 0f);    // 우상
        }

        // ── SetSizeWithCurrentAnchors ───────────────────────────

        /// <summary>
        /// 현재 앵커를 유지하면서 지정 축의 크기를 설정.
        /// 부모 크기가 필요하므로 GetParentSize()를 사용.
        /// </summary>
        public void SetSizeWithCurrentAnchors(Axis axis, float size)
        {
            int i = (int)axis;
            Vector2 parentSize = GetParentSize();
            Vector2 sd = sizeDelta;
            sd[i] = size - parentSize[i] * (anchorMax[i] - anchorMin[i]);
            sizeDelta = sd;
        }

        /// <summary>
        /// 부모 크기를 직접 지정하여 크기 설정.
        /// </summary>
        public void SetSizeWithCurrentAnchors(Axis axis, float size, Vector2 parentSize)
        {
            int i = (int)axis;
            Vector2 sd = sizeDelta;
            sd[i] = size - parentSize[i] * (anchorMax[i] - anchorMin[i]);
            sizeDelta = sd;
        }

        // ── SetInsetAndSizeFromParentEdge ───────────────────────

        /// <summary>
        /// 부모의 지정 가장자리에서 inset 만큼 떨어진 곳에 size 크기로 배치.
        /// 앵커를 해당 가장자리에 고정시킴.
        /// Y-down 좌표계: Top=0, Bottom=1.
        /// </summary>
        public void SetInsetAndSizeFromParentEdge(Edge edge, float inset, float size)
        {
            int axis = (edge == Edge.Top || edge == Edge.Bottom) ? 1 : 0;
            // Y-down: Top/Left = 0, Bottom/Right = 1
            bool isEnd = (edge == Edge.Bottom || edge == Edge.Right);

            float anchorValue = isEnd ? 1f : 0f;

            Vector2 amin = anchorMin;
            amin[axis] = anchorValue;
            anchorMin = amin;

            Vector2 amax = anchorMax;
            amax[axis] = anchorValue;
            anchorMax = amax;

            Vector2 sd = sizeDelta;
            sd[axis] = size;
            sizeDelta = sd;

            Vector2 pos = anchoredPosition;
            if (isEnd)
                pos[axis] = -inset - size * (1f - pivot[axis]);
            else
                pos[axis] = inset + size * pivot[axis];
            anchoredPosition = pos;
        }

        // ── 앵커 프리셋 ────────────────────────────────────────

        /// <summary>
        /// 자주 사용하는 앵커 프리셋.
        /// Y-down 좌표계 기준.
        /// </summary>
        public enum AnchorPreset
        {
            // 단일 앵커 (위치 고정)
            TopLeft,
            TopCenter,
            TopRight,
            MiddleLeft,
            MiddleCenter,
            MiddleRight,
            BottomLeft,
            BottomCenter,
            BottomRight,

            // 수평 스트레치
            TopStretch,
            MiddleStretch,
            BottomStretch,

            // 수직 스트레치
            StretchLeft,
            StretchCenter,
            StretchRight,

            // 양방향 스트레치
            StretchAll,
        }

        /// <summary>
        /// 앵커 프리셋을 적용하면서 시각적 위치/크기를 유지 (Unity 동작과 동일).
        /// parentSize: 부모의 최종 크기 (GetParentSize()로 획득).
        /// parentSize가 zero이면 위치 보정 없이 앵커만 변경.
        /// </summary>
        public void SetAnchorPresetKeepVisual(AnchorPreset preset, Vector2 parentSize, bool setPivot = true)
        {
            if (parentSize.x <= 0f && parentSize.y <= 0f)
            {
                SetAnchorPreset(preset, setPivot);
                return;
            }

            // 1. 변경 전: 부모 좌표계에서 사각형의 절대 위치/크기 계산
            float oldAnchorRegionX = anchorMin.x * parentSize.x;
            float oldAnchorRegionY = anchorMin.y * parentSize.y;
            float oldAnchorRegionW = (anchorMax.x - anchorMin.x) * parentSize.x;
            float oldAnchorRegionH = (anchorMax.y - anchorMin.y) * parentSize.y;

            float finalW = oldAnchorRegionW + sizeDelta.x;
            float finalH = oldAnchorRegionH + sizeDelta.y;

            float oldCenterX = oldAnchorRegionX + oldAnchorRegionW * 0.5f;
            float oldCenterY = oldAnchorRegionY + oldAnchorRegionH * 0.5f;

            // 사각형 좌상단 위치 (부모 로컬 좌표)
            float rectLeft = oldCenterX + anchoredPosition.x - pivot.x * finalW;
            float rectTop  = oldCenterY + anchoredPosition.y - pivot.y * finalH;

            // 2. 새 앵커/피벗 적용
            SetAnchorPreset(preset, setPivot);

            // 3. 새 앵커 기준으로 anchoredPosition, sizeDelta 역산
            float newAnchorRegionW = (anchorMax.x - anchorMin.x) * parentSize.x;
            float newAnchorRegionH = (anchorMax.y - anchorMin.y) * parentSize.y;

            float newCenterX = anchorMin.x * parentSize.x + newAnchorRegionW * 0.5f;
            float newCenterY = anchorMin.y * parentSize.y + newAnchorRegionH * 0.5f;

            sizeDelta = new Vector2(finalW - newAnchorRegionW, finalH - newAnchorRegionH);
            anchoredPosition = new Vector2(
                rectLeft - newCenterX + pivot.x * finalW,
                rectTop  - newCenterY + pivot.y * finalH
            );
        }

        /// <summary>
        /// 앵커 프리셋을 적용. anchorMin, anchorMax, pivot 을 설정.
        /// anchoredPosition, sizeDelta는 변경하지 않음.
        /// </summary>
        public void SetAnchorPreset(AnchorPreset preset, bool setPivot = true)
        {
            switch (preset)
            {
                // 단일 앵커 (고정 크기)
                case AnchorPreset.TopLeft:
                    anchorMin = new Vector2(0f, 0f); anchorMax = new Vector2(0f, 0f);
                    if (setPivot) pivot = new Vector2(0f, 0f);
                    break;
                case AnchorPreset.TopCenter:
                    anchorMin = new Vector2(0.5f, 0f); anchorMax = new Vector2(0.5f, 0f);
                    if (setPivot) pivot = new Vector2(0.5f, 0f);
                    break;
                case AnchorPreset.TopRight:
                    anchorMin = new Vector2(1f, 0f); anchorMax = new Vector2(1f, 0f);
                    if (setPivot) pivot = new Vector2(1f, 0f);
                    break;
                case AnchorPreset.MiddleLeft:
                    anchorMin = new Vector2(0f, 0.5f); anchorMax = new Vector2(0f, 0.5f);
                    if (setPivot) pivot = new Vector2(0f, 0.5f);
                    break;
                case AnchorPreset.MiddleCenter:
                    anchorMin = new Vector2(0.5f, 0.5f); anchorMax = new Vector2(0.5f, 0.5f);
                    if (setPivot) pivot = new Vector2(0.5f, 0.5f);
                    break;
                case AnchorPreset.MiddleRight:
                    anchorMin = new Vector2(1f, 0.5f); anchorMax = new Vector2(1f, 0.5f);
                    if (setPivot) pivot = new Vector2(1f, 0.5f);
                    break;
                case AnchorPreset.BottomLeft:
                    anchorMin = new Vector2(0f, 1f); anchorMax = new Vector2(0f, 1f);
                    if (setPivot) pivot = new Vector2(0f, 1f);
                    break;
                case AnchorPreset.BottomCenter:
                    anchorMin = new Vector2(0.5f, 1f); anchorMax = new Vector2(0.5f, 1f);
                    if (setPivot) pivot = new Vector2(0.5f, 1f);
                    break;
                case AnchorPreset.BottomRight:
                    anchorMin = new Vector2(1f, 1f); anchorMax = new Vector2(1f, 1f);
                    if (setPivot) pivot = new Vector2(1f, 1f);
                    break;

                // 수평 스트레치 (좌우 늘림, 높이 고정)
                case AnchorPreset.TopStretch:
                    anchorMin = new Vector2(0f, 0f); anchorMax = new Vector2(1f, 0f);
                    if (setPivot) pivot = new Vector2(0.5f, 0f);
                    break;
                case AnchorPreset.MiddleStretch:
                    anchorMin = new Vector2(0f, 0.5f); anchorMax = new Vector2(1f, 0.5f);
                    if (setPivot) pivot = new Vector2(0.5f, 0.5f);
                    break;
                case AnchorPreset.BottomStretch:
                    anchorMin = new Vector2(0f, 1f); anchorMax = new Vector2(1f, 1f);
                    if (setPivot) pivot = new Vector2(0.5f, 1f);
                    break;

                // 수직 스트레치 (상하 늘림, 너비 고정)
                case AnchorPreset.StretchLeft:
                    anchorMin = new Vector2(0f, 0f); anchorMax = new Vector2(0f, 1f);
                    if (setPivot) pivot = new Vector2(0f, 0.5f);
                    break;
                case AnchorPreset.StretchCenter:
                    anchorMin = new Vector2(0.5f, 0f); anchorMax = new Vector2(0.5f, 1f);
                    if (setPivot) pivot = new Vector2(0.5f, 0.5f);
                    break;
                case AnchorPreset.StretchRight:
                    anchorMin = new Vector2(1f, 0f); anchorMax = new Vector2(1f, 1f);
                    if (setPivot) pivot = new Vector2(1f, 0.5f);
                    break;

                // 양방향 스트레치 (부모 가득 채움)
                case AnchorPreset.StretchAll:
                    anchorMin = new Vector2(0f, 0f); anchorMax = new Vector2(1f, 1f);
                    if (setPivot) pivot = new Vector2(0.5f, 0.5f);
                    break;
            }
        }
    }
}
