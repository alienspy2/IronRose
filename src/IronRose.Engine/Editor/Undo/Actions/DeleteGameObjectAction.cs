using System.Collections.Generic;
using RoseEngine;
using Tomlyn.Model;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 선택된 GameObject(및 자식 계층)를 삭제하는 Undo 액션.
    /// Undo: 스냅샷에서 재생성, Redo: 다시 삭제.
    /// </summary>
    public sealed class DeleteGameObjectAction : IUndoAction
    {
        public string Description { get; }

        private readonly GOSnapshot[] _snapshots;
        private readonly int? _originalParentId;
        private int _rootId;

        public DeleteGameObjectAction(string description, GameObject root)
        {
            Description = description;
            _rootId = root.GetInstanceID();
            _originalParentId = root.transform.parent?.gameObject.GetInstanceID();
            _snapshots = CaptureSubtree(root);
        }

        public void Undo()
        {
            // 스냅샷에서 GO 계층 구조 재생성
            var created = new List<GameObject>(_snapshots.Length);
            for (int i = 0; i < _snapshots.Length; i++)
            {
                ref readonly var snap = ref _snapshots[i];
                var go = new GameObject(snap.Name);
                go.transform.localPosition = snap.LocalPosition;
                go.transform.localRotation = snap.LocalRotation;
                go.transform.localScale = snap.LocalScale;

                if (snap.Components != null)
                {
                    foreach (TomlTable ct in snap.Components)
                        SceneSerializer.DeserializeComponent(go, ct);
                }

                go.SetActive(snap.ActiveSelf);
                created.Add(go);
            }

            // 내부 parent-child 관계 복원
            for (int i = 0; i < created.Count; i++)
            {
                int pIdx = _snapshots[i].ParentIdx;
                if (pIdx >= 0 && pIdx < created.Count)
                    created[i].transform.SetParent(created[pIdx].transform, false);
            }

            // 원래 부모에 다시 연결
            if (_originalParentId != null)
            {
                var parent = UndoUtility.FindGameObjectById(_originalParentId.Value);
                if (parent != null)
                    created[0].transform.SetParent(parent.transform, false);
            }

            _rootId = created[0].GetInstanceID();
            EditorSelection.SelectGameObject(created[0]);
            SceneManager.GetActiveScene().isDirty = true;
        }

        public void Redo()
        {
            var go = UndoUtility.FindGameObjectById(_rootId);
            if (go != null)
                RoseEngine.Object.DestroyImmediate(go);

            EditorSelection.Clear();
            SceneManager.GetActiveScene().isDirty = true;
        }

        // ── Snapshot capture ──

        private struct GOSnapshot
        {
            public string Name;
            public bool ActiveSelf;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public Vector3 LocalScale;
            public int ParentIdx;           // _snapshots 배열 내 부모 인덱스, -1 = 루트
            public TomlTableArray? Components;
        }

        private static GOSnapshot[] CaptureSubtree(GameObject root)
        {
            var list = new List<GOSnapshot>();
            CaptureRecursive(root, -1, list);
            return list.ToArray();
        }

        private static void CaptureRecursive(GameObject go, int parentIdx, List<GOSnapshot> list)
        {
            int myIdx = list.Count;

            TomlTableArray? compArray = null;
            foreach (var comp in go.InternalComponents)
            {
                if (comp is Transform) continue;
                var ct = SceneSerializer.SerializeComponent(comp);
                if (ct != null)
                {
                    compArray ??= new TomlTableArray();
                    compArray.Add(ct);
                }
            }

            list.Add(new GOSnapshot
            {
                Name = go.name,
                ActiveSelf = go.activeSelf,
                LocalPosition = go.transform.localPosition,
                LocalRotation = go.transform.localRotation,
                LocalScale = go.transform.localScale,
                ParentIdx = parentIdx,
                Components = compArray,
            });

            for (int i = 0; i < go.transform.childCount; i++)
                CaptureRecursive(go.transform.GetChild(i).gameObject, myIdx, list);
        }
    }
}
