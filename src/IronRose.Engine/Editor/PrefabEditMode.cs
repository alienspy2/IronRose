using System.Collections.Generic;
using System.IO;
using IronRose.AssetPipeline;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 프리팹 편집 모드 — 격리된 환경에서 프리팹을 편집하고 저장.
    /// 프로퍼티 변경이 가능한 유일한 장소 (씬에서는 Transform만 편집 가능).
    /// </summary>
    public static class PrefabEditMode
    {
        /// <summary>현재 프리팹 편집 모드에서의 Breadcrumb 경로 (표시용).</summary>
        public static IReadOnlyList<string> Breadcrumbs
        {
            get
            {
                var list = new List<string>();

                // 저장된 씬 이름
                if (!string.IsNullOrEmpty(EditorState.SavedScenePath))
                    list.Add(Path.GetFileNameWithoutExtension(EditorState.SavedScenePath));
                else
                    list.Add("Scene");

                // 스택에 있는 프리팹들
                foreach (var ctx in EditorState.PrefabEditStack)
                    list.Add(Path.GetFileNameWithoutExtension(ctx.PrefabPath));

                // 현재 편집 중인 프리팹
                if (EditorState.IsEditingPrefab && !string.IsNullOrEmpty(EditorState.EditingPrefabPath))
                    list.Add(Path.GetFileNameWithoutExtension(EditorState.EditingPrefabPath));

                return list;
            }
        }

        /// <summary>
        /// 프리팹 편집 모드 진입.
        /// 현재 씬을 메모리에 스냅샷 저장 → 씬 클리어 → 프리팹 로드 → 편집 가능 상태.
        /// </summary>
        public static void Enter(string prefabPath)
        {
            var db = Resources.GetAssetDatabase();
            if (db == null) return;

            var guid = db.GetGuidFromPath(prefabPath);
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogWarning($"[PrefabEditMode] No GUID for path: {prefabPath}");
                return;
            }

            var scene = SceneManager.GetActiveScene();

            // GO ID 맵 캡처 (복귀 시 undo 리맵용)
            var goIdMap = UndoUtility.CaptureIdMap();

            // 이미 편집 중이면 스택에 push (중첩 진입)
            if (EditorState.IsEditingPrefab)
            {
                var currentSnapshot = SceneSerializer.SaveToString();
                var (undo, redo) = UndoSystem.SaveAndClear();
                EditorState.PrefabEditStack.Push(new PrefabEditContext
                {
                    PrefabPath = EditorState.EditingPrefabPath!,
                    PrefabGuid = EditorState.EditingPrefabGuid!,
                    SceneSnapshot = currentSnapshot,
                    IsDirty = scene.isDirty,
                    UndoStack = undo,
                    RedoStack = redo,
                    GoIdMap = goIdMap,
                });
            }
            else
            {
                // 최초 진입 — 씬 스냅샷 + isDirty + Undo 스택 저장
                EditorState.SavedSceneSnapshot = SceneSerializer.SaveToString();
                EditorState.SavedScenePath = scene.path;
                EditorState.SavedSceneIsDirty = scene.isDirty;
                var (undo, redo) = UndoSystem.SaveAndClear();
                EditorState.SavedUndoStack = undo;
                EditorState.SavedRedoStack = redo;
                EditorState.SavedGoIdMap = goIdMap;
            }

            // 씬 클리어 후 프리팹 로드 (PrefabImporter로 Variant 체인도 처리)
            SceneManager.Clear();

            var importer = new PrefabImporter(db);
            var root = importer.LoadPrefab(prefabPath);
            if (root != null)
                SceneSerializer.SetEditorInternalRecursive(root, false); // 편집 가능하도록 표시 해제

            EditorState.IsEditingPrefab = true;
            EditorState.EditingPrefabPath = prefabPath;
            EditorState.EditingPrefabGuid = guid;

            // 프리팹 편집은 깨끗한 상태로 시작
            SceneManager.GetActiveScene().isDirty = false;

            EditorSelection.Clear();
            Debug.Log($"[PrefabEditMode] Entered: {prefabPath}");
        }

        /// <summary>
        /// 현재 편집 중인 프리팹을 저장.
        /// Base 프리팹이면 전체 저장, Variant이면 오버라이드 자동 계산 후 저장.
        /// </summary>
        public static void Save()
        {
            if (!EditorState.IsEditingPrefab || string.IsNullOrEmpty(EditorState.EditingPrefabPath))
                return;

            var allGOs = SceneManager.AllGameObjects;
            // 루트 GO 찾기 (parent == null, _isEditorInternal이 아닌)
            GameObject? root = null;
            foreach (var go in allGOs)
            {
                if (!go._isEditorInternal && !go._isDestroyed && go.transform.parent == null)
                {
                    root = go;
                    break;
                }
            }

            if (root == null)
            {
                Debug.LogWarning("[PrefabEditMode] No root GameObject found to save");
                return;
            }

            var prefabPath = EditorState.EditingPrefabPath!;

            // Variant 여부 확인
            var baseGuid = PrefabImporter.GetBasePrefabGuidFromFile(prefabPath);
            if (baseGuid != null)
            {
                // Variant 저장: 부모와 비교하여 오버라이드만 저장
                SceneSerializer.SaveVariantPrefab(root, baseGuid, prefabPath);
            }
            else
            {
                // Base 프리팹 전체 저장
                SceneSerializer.SavePrefab(root, prefabPath);
            }

            // AssetDatabase 캐시 무효화 (Exit 시 최신 템플릿 사용하도록)
            // Dependency graph 기반으로 수정된 프리팹과 이를 참조하는 부모들만 캐스케이드 무효화
            var db = Resources.GetAssetDatabase();
            if (db != null && !string.IsNullOrEmpty(EditorState.EditingPrefabGuid))
                db.InvalidatePrefabAndDependents(EditorState.EditingPrefabGuid);

            // Variant tree 갱신
            PrefabVariantTree.Instance.Rebuild();

            SceneManager.GetActiveScene().isDirty = false;
            Debug.Log($"[PrefabEditMode] Saved: {prefabPath}");
        }

        /// <summary>
        /// 프리팹 편집 모드 종료.
        /// 스택에 이전 레벨이 있으면 그쪽으로 복귀, 없으면 원래 씬 복원.
        /// </summary>
        public static void Exit()
        {
            if (!EditorState.IsEditingPrefab) return;

            SceneManager.Clear();
            UndoSystem.Clear(); // 프리팹 편집 undo 스택 정리

            if (EditorState.PrefabEditStack.Count > 0)
            {
                // 스택에서 이전 레벨 복귀
                var ctx = EditorState.PrefabEditStack.Pop();
                SceneSerializer.LoadFromString(ctx.SceneSnapshot);
                EditorState.EditingPrefabPath = ctx.PrefabPath;
                EditorState.EditingPrefabGuid = ctx.PrefabGuid;
                SceneManager.GetActiveScene().isDirty = ctx.IsDirty;
                UndoSystem.Restore(ctx.UndoStack, ctx.RedoStack);

                // Undo action의 GO ID 리맵 빌드
                var remap = UndoUtility.BuildRemap(ctx.GoIdMap);
                UndoUtility.SetIdRemap(remap.Count > 0 ? remap : null);

                Debug.Log($"[PrefabEditMode] Returned to: {ctx.PrefabPath}");
            }
            else
            {
                // 원래 씬 복원
                if (!string.IsNullOrEmpty(EditorState.SavedSceneSnapshot))
                {
                    SceneSerializer.LoadFromString(EditorState.SavedSceneSnapshot);
                }

                // 씬 path + isDirty + Undo 복원
                var scene = SceneManager.GetActiveScene();
                if (!string.IsNullOrEmpty(EditorState.SavedScenePath))
                    scene.path = EditorState.SavedScenePath;
                scene.isDirty = EditorState.SavedSceneIsDirty;

                if (EditorState.SavedUndoStack != null && EditorState.SavedRedoStack != null)
                    UndoSystem.Restore(EditorState.SavedUndoStack, EditorState.SavedRedoStack);

                // Undo action의 GO ID 리맵 빌드
                if (EditorState.SavedGoIdMap != null)
                {
                    var remap = UndoUtility.BuildRemap(EditorState.SavedGoIdMap);
                    UndoUtility.SetIdRemap(remap.Count > 0 ? remap : null);
                }

                EditorState.IsEditingPrefab = false;
                EditorState.EditingPrefabPath = null;
                EditorState.EditingPrefabGuid = null;
                EditorState.SavedSceneSnapshot = null;
                EditorState.SavedScenePath = null;
                EditorState.SavedUndoStack = null;
                EditorState.SavedRedoStack = null;
                EditorState.SavedGoIdMap = null;

                Debug.Log("[PrefabEditMode] Exited to scene");
            }

            EditorSelection.Clear();
        }

        /// <summary>
        /// 프리팹 편집 모드에서 "Back".
        /// 저장 확인 다이얼로그는 ImGuiOverlay.RequestPrefabBack()에서 처리.
        /// </summary>
        public static void Back()
        {
            Exit();
        }
    }
}
