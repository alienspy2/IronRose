using System;
using RoseEngine;

namespace IronRose.Engine.Editor.SceneView
{
    public static class GizmoCallbackRunner
    {
        public static void DrawAllGizmos()
        {
            Gizmos.IsDrawing = true;
            try
            {
                foreach (var go in SceneManager.AllGameObjects)
                {
                    if (go._isDestroyed || !go.activeInHierarchy) continue;
                    if (go._isEditorInternal) continue;

                    bool isSelected = EditorSelection.IsSelected(go.GetInstanceID());
                    GizmoRenderer.CurrentOwnerInstanceId = (uint)go.GetInstanceID();

                    foreach (var comp in go.InternalComponents)
                    {
                        if (comp._isDestroyed) continue;

                        Gizmos.color = Color.white;
                        Gizmos.matrix = Matrix4x4.identity;

                        try { comp.OnDrawGizmos(); }
                        catch (Exception ex)
                        {
                            EditorDebug.LogError($"Exception in {comp.GetType().Name}.OnDrawGizmos(): {ex.Message}");
                        }

                        if (isSelected)
                        {
                            Gizmos.color = Color.white;
                            Gizmos.matrix = Matrix4x4.identity;

                            try { comp.OnDrawGizmosSelected(); }
                            catch (Exception ex)
                            {
                                EditorDebug.LogError($"Exception in {comp.GetType().Name}.OnDrawGizmosSelected(): {ex.Message}");
                            }
                        }
                    }
                }
            }
            finally
            {
                GizmoRenderer.CurrentOwnerInstanceId = 0;
                Gizmos.IsDrawing = false;
            }
        }
    }
}
