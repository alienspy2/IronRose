using System.Collections.Generic;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    internal static class UndoUtility
    {
        /// <summary>씬 복원 후 old instanceId → new instanceId 매핑.</summary>
        private static Dictionary<int, int>? _idRemap;

        public static GameObject? FindGameObjectById(int instanceId)
        {
            // 리맵이 있으면 새 ID로 변환
            int resolvedId = instanceId;
            if (_idRemap != null && _idRemap.TryGetValue(instanceId, out var mapped))
                resolvedId = mapped;

            foreach (var go in SceneManager.AllGameObjects)
            {
                if (!go._isDestroyed && go.GetInstanceID() == resolvedId)
                    return go;
            }
            return null;
        }

        public static Component? FindComponent(int gameObjectId, string componentTypeName)
        {
            var go = FindGameObjectById(gameObjectId);
            if (go == null) return null;

            foreach (var comp in go.InternalComponents)
            {
                if (!comp._isDestroyed && comp.GetType().Name == componentTypeName)
                    return comp;
            }
            return null;
        }

        /// <summary>ID 리맵 테이블 설정 (Prefab Edit Mode 복귀 시).</summary>
        public static void SetIdRemap(Dictionary<int, int>? remap)
        {
            _idRemap = remap;
        }

        /// <summary>
        /// 현재 씬의 모든 GO에 대해 계층 경로 → instanceId 맵을 생성.
        /// Prefab Edit Mode 진입 전에 호출하여 나중에 리맵 빌드에 사용.
        /// </summary>
        public static Dictionary<string, int> CaptureIdMap()
        {
            var map = new Dictionary<string, int>();
            foreach (var go in SceneManager.AllGameObjects)
            {
                if (go._isDestroyed || go._isEditorInternal) continue;
                var path = GetHierarchyPath(go);
                map.TryAdd(path, go.GetInstanceID());
            }
            return map;
        }

        /// <summary>
        /// 씬 복원 후 호출. 저장된 맵과 비교하여 old→new ID 리맵을 빌드.
        /// </summary>
        public static Dictionary<int, int> BuildRemap(Dictionary<string, int> oldMap)
        {
            var remap = new Dictionary<int, int>();
            foreach (var go in SceneManager.AllGameObjects)
            {
                if (go._isDestroyed || go._isEditorInternal) continue;
                var path = GetHierarchyPath(go);
                if (oldMap.TryGetValue(path, out var oldId))
                {
                    var newId = go.GetInstanceID();
                    if (oldId != newId)
                        remap[oldId] = newId;
                }
            }
            return remap;
        }

        private static string GetHierarchyPath(GameObject go)
        {
            var parts = new List<string>();
            var current = go.transform;
            while (current != null)
            {
                parts.Add(current.gameObject.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
