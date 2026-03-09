using ImGuiNET;

namespace RoseEngine
{
    /// <summary>
    /// Canvas UI 컴포넌트가 구현하는 렌더링 인터페이스.
    /// CanvasRenderer가 DFS 순회 중 호출.
    /// </summary>
    public interface IUIRenderable
    {
        /// <summary>ImGui DrawList에 UI를 그린다.</summary>
        /// <param name="drawList">ImGui DrawList 포인터.</param>
        /// <param name="screenRect">스크린 좌표 기준 최종 Rect (픽셀).</param>
        void OnRenderUI(ImDrawListPtr drawList, Rect screenRect);

        /// <summary>같은 GameObject 내 렌더 순서 (낮을수록 먼저).</summary>
        int renderOrder { get; }
    }
}
