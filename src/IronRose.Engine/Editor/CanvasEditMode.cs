// ------------------------------------------------------------
// @file    CanvasEditMode.cs
// @brief   Canvas Edit Mode의 진입/퇴출 로직과 2D 뷰 상태(ViewOffset, ViewZoom) 관리.
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
//     ClampZoom(float): float                                — 줌 범위 제한
//     GetAspectSize(): (float, float)                        — aspect ratio에 따른 크기 반환
// @note    EditorCamera 상태 저장/복원의 실제 구현은 Phase D (ImGuiOverlay)에서 완성된다.
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

        public const float MinZoom = 0.1f;
        public const float MaxZoom = 10.0f;

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

            EditorState.SavedCanvasCameraPosition = null;
            EditorState.SavedCanvasCameraRotation = null;
            EditorState.SavedCanvasCameraPivot = null;

            EditorSelection.Clear();

            EditorDebug.Log("[CanvasEditMode] Exited");
        }

        /// <summary>뷰를 Canvas 전체에 맞게 초기화.</summary>
        public static void ResetView()
        {
            ViewOffset = System.Numerics.Vector2.Zero;
            ViewZoom = 1.0f;
        }

        /// <summary>줌 값을 허용 범위로 클램프.</summary>
        public static float ClampZoom(float zoom) =>
            System.Math.Clamp(zoom, MinZoom, MaxZoom);

        /// <summary>현재 aspect ratio 설정에 따른 캔버스 크기 반환.</summary>
        public static (float width, float height) GetAspectSize()
        {
            return EditorState.CanvasEditAspectRatio switch
            {
                "16:9"  => (1920f, 1080f),
                "16:10" => (1920f, 1200f),
                "4:3"   => (1600f, 1200f),
                "32:9"  => (3840f, 1080f),
                "Custom" => (EditorState.CanvasEditCustomWidth, EditorState.CanvasEditCustomHeight),
                _ => (1920f, 1080f),
            };
        }
    }
}
