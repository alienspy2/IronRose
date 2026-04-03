// ------------------------------------------------------------
// @file    CanvasEditMode.cs
// @brief   Canvas Edit Mode의 진입/퇴출 로직과 2D 뷰 상태(ViewOffset, ViewZoom) 관리.
//          Enter 시 EditorState에 상태 플래그를 설정하고, Exit 시 정리한다.
//          EditorCamera 저장/복원은 ImGuiOverlay에서 처리하며, 이 클래스는 상태만 관리한다.
// @deps    IronRose.Engine.Editor/EditorState, IronRose.Engine.Editor/EditorSelection,
//          RoseEngine/GameObject, RoseEngine/Canvas
// @exports
//   static class CanvasEditMode
//     IsActive: bool                                         — Canvas Edit Mode 활성 여부
//     EditingCanvasGoId: int?                                — 편집 중인 Canvas GO instance ID
//     ViewOffset: Vector2                                    — 패닝 오프셋 (screen 픽셀)
//     ViewZoom: float                                        — 확대/축소 배율
//     Enter(GameObject canvasGo): void                       — Canvas Edit Mode 진입
//     Exit(): void                                           — Canvas Edit Mode 퇴출
//     ResetView(): void                                      — 뷰를 Canvas 전체에 맞게 초기화
// @note    EditorCamera 상태 저장/복원의 실제 구현은 Phase D (ImGuiOverlay)에서 완성된다.
//          이 phase에서는 EditorState의 저장 필드만 준비하고, 카메라 조작은 하지 않는다.
// ------------------------------------------------------------
using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// Canvas Edit Mode — 2D Canvas 전용 편집 환경.
    /// 진입/퇴출 로직과 2D 뷰 상태(ViewOffset, ViewZoom)를 관리한다.
    /// </summary>
    public static class CanvasEditMode
    {
        /// <summary>Canvas Edit Mode 활성 여부.</summary>
        public static bool IsActive => EditorState.IsEditingCanvas;

        /// <summary>편집 중인 Canvas GameObject instance ID.</summary>
        public static int? EditingCanvasGoId => EditorState.EditingCanvasGoId;

        /// <summary>패닝 오프셋 (screen 픽셀).</summary>
        public static System.Numerics.Vector2 ViewOffset;

        /// <summary>확대/축소 배율.</summary>
        public static float ViewZoom = 1.0f;

        /// <summary>
        /// Canvas Edit Mode 진입.
        /// canvasGo에 Canvas 컴포넌트가 있어야 한다.
        /// </summary>
        public static void Enter(GameObject canvasGo)
        {
            if (canvasGo.GetComponent<Canvas>() == null)
            {
                EditorDebug.LogWarning("[CanvasEditMode] GameObject does not have a Canvas component.");
                return;
            }

            EditorState.IsEditingCanvas = true;
            EditorState.EditingCanvasGoId = canvasGo.GetInstanceID();

            ViewOffset = System.Numerics.Vector2.Zero;
            ViewZoom = 1.0f;

            EditorSelection.Clear();
            EditorSelection.Select(canvasGo.GetInstanceID());

            EditorDebug.Log($"[CanvasEditMode] Entered: {canvasGo.name}");
        }

        /// <summary>
        /// Canvas Edit Mode 퇴출. 상태를 정리하고 선택을 해제한다.
        /// EditorCamera 복원은 ImGuiOverlay에서 SavedCanvasCameraPosition/Rotation/Pivot 값으로 처리.
        /// </summary>
        public static void Exit()
        {
            if (!EditorState.IsEditingCanvas) return;

            EditorState.IsEditingCanvas = false;
            EditorState.EditingCanvasGoId = null;

            // EditorCamera 저장 상태 클리어 (복원은 ImGuiOverlay에서 이미 처리)
            EditorState.SavedCanvasCameraPosition = null;
            EditorState.SavedCanvasCameraRotation = null;
            EditorState.SavedCanvasCameraPivot = null;

            EditorSelection.Clear();

            EditorDebug.Log("[CanvasEditMode] Exited");
        }

        /// <summary>
        /// 뷰를 Canvas 전체에 맞게 초기화.
        /// </summary>
        public static void ResetView()
        {
            ViewOffset = System.Numerics.Vector2.Zero;
            ViewZoom = 1.0f;
        }
    }
}
