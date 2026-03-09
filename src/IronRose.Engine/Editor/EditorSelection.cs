using System;
using System.Collections.Generic;
using System.Linq;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 에디터 전역 선택 상태. Hierarchy, Scene View 피킹, Inspector, Gizmo가 공유.
    /// 멀티셀렉션 지원 — 마지막 클릭된 오브젝트가 Primary.
    /// </summary>
    public static class EditorSelection
    {
        private static readonly List<int> _selectedIds = new();
        private static readonly HashSet<int> _selectedIdSet = new();

        public static long SelectionVersion { get; private set; }

        /// <summary>Primary (마지막 클릭) 오브젝트 ID. 하위 호환.</summary>
        public static int? SelectedGameObjectId =>
            _selectedIds.Count > 0 ? _selectedIds[^1] : null;

        /// <summary>Primary (마지막 클릭) GameObject. 하위 호환.</summary>
        public static GameObject? SelectedGameObject
        {
            get
            {
                if (_selectedIds.Count == 0) return null;
                int primaryId = _selectedIds[^1];
                return SceneManager.AllGameObjects
                    .FirstOrDefault(go => !go._isDestroyed && go.GetInstanceID() == primaryId);
            }
        }

        /// <summary>선택된 모든 ID (클릭 순서, 마지막이 Primary).</summary>
        public static IReadOnlyList<int> SelectedGameObjectIds => _selectedIds;

        /// <summary>선택된 오브젝트 수.</summary>
        public static int Count => _selectedIds.Count;

        /// <summary>O(1) 멤버십 테스트.</summary>
        public static bool IsSelected(int id) => _selectedIdSet.Contains(id);

        /// <summary>단일 선택 (기존 클릭 동작).</summary>
        public static void Select(int? id)
        {
            if (_selectedIds.Count == 1 && _selectedIds[0] == id)
            {
                // 같은 오브젝트를 다시 선택해도 버전을 올려서
                // Inspector 등이 모드 전환할 수 있도록 한다.
                SelectionVersion++;
                return;
            }
            _selectedIds.Clear();
            _selectedIdSet.Clear();
            if (id.HasValue)
            {
                _selectedIds.Add(id.Value);
                _selectedIdSet.Add(id.Value);
            }
            SelectionVersion++;
        }

        /// <summary>Ctrl+Click: 토글.</summary>
        public static void ToggleSelect(int id)
        {
            if (_selectedIdSet.Contains(id))
            {
                _selectedIds.Remove(id);
                _selectedIdSet.Remove(id);
            }
            else
            {
                _selectedIds.Add(id);
                _selectedIdSet.Add(id);
            }
            SelectionVersion++;
        }

        /// <summary>Shift+Click: Primary(anchor) ~ target 범위 선택.</summary>
        public static void RangeSelect(int targetId, IReadOnlyList<int> orderedIds)
        {
            int anchorId = SelectedGameObjectId ?? targetId;
            int anchorIdx = -1, targetIdx = -1;
            for (int i = 0; i < orderedIds.Count; i++)
            {
                if (orderedIds[i] == anchorId) anchorIdx = i;
                if (orderedIds[i] == targetId) targetIdx = i;
            }
            if (anchorIdx < 0 || targetIdx < 0) { Select(targetId); return; }

            int from = Math.Min(anchorIdx, targetIdx);
            int to = Math.Max(anchorIdx, targetIdx);

            _selectedIds.Clear();
            _selectedIdSet.Clear();
            for (int i = from; i <= to; i++)
            {
                _selectedIds.Add(orderedIds[i]);
                _selectedIdSet.Add(orderedIds[i]);
            }
            // target을 마지막(Primary)으로
            if (_selectedIds.Count > 0 && _selectedIds[^1] != targetId)
            {
                _selectedIds.Remove(targetId);
                _selectedIds.Add(targetId);
            }
            SelectionVersion++;
        }

        /// <summary>프로그래밍적 선택 교체 (Duplicate 후 등).</summary>
        public static void SetSelection(IEnumerable<int> ids)
        {
            _selectedIds.Clear();
            _selectedIdSet.Clear();
            foreach (var id in ids)
            {
                if (_selectedIdSet.Add(id))
                    _selectedIds.Add(id);
            }
            SelectionVersion++;
        }

        public static void SelectGameObject(GameObject? go)
        {
            Select(go != null ? go.GetInstanceID() : null);
        }

        public static void Clear()
        {
            _selectedIds.Clear();
            _selectedIdSet.Clear();
            SelectionVersion++;
        }
    }
}
