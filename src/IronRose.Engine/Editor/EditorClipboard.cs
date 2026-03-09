using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using IronRose.AssetPipeline;
using RoseEngine;
using Tomlyn.Model;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 에디터 전역 클립보드 — GameObject / Asset 복사·잘라내기·붙여넣기.
    /// </summary>
    internal static class EditorClipboard
    {
        public enum Kind { None, GameObjects, Assets }

        public static Kind ClipboardKind { get; private set; }
        public static bool IsCut { get; private set; }

        // ── GO 클립보드 데이터 ──
        // 각 루트 GO를 TomlTableArray(계층 직렬화)로 보관
        private static List<TomlTableArray>? _goEntries;
        private static List<int>? _cutGoIds; // cut 시 원본 GO ID (paste 때 삭제)

        // ── Asset 클립보드 데이터 ──
        private static List<string>? _assetPaths;

        // ================================================================
        // Copy / Cut — GameObjects
        // ================================================================

        public static void CopyGameObjects(bool cut)
        {
            var ids = EditorSelection.SelectedGameObjectIds;
            if (ids.Count == 0) return;

            _goEntries = new List<TomlTableArray>();
            foreach (var id in ids)
            {
                var go = SceneManager.AllGameObjects
                    .FirstOrDefault(g => !g._isDestroyed && g.GetInstanceID() == id);
                if (go == null) continue;
                _goEntries.Add(SceneSerializer.SerializeGameObjectHierarchy(go));
            }

            if (_goEntries.Count == 0) { _goEntries = null; return; }

            ClipboardKind = Kind.GameObjects;
            IsCut = cut;

            // cut: 원본 ID를 기억 (paste 시 삭제)
            if (cut)
            {
                _cutGoIds = new List<int>();
                foreach (var id in ids)
                {
                    var go = SceneManager.AllGameObjects
                        .FirstOrDefault(g => !g._isDestroyed && g.GetInstanceID() == id);
                    if (go != null) _cutGoIds.Add(id);
                }
            }
            else
            {
                _cutGoIds = null;
            }
        }

        // ================================================================
        // Paste — GameObjects
        // ================================================================

        public static void PasteGameObjects()
        {
            if (ClipboardKind != Kind.GameObjects || _goEntries == null || _goEntries.Count == 0)
                return;

            // 붙여넣기 대상: 현재 선택된 GO의 자식, 없으면 루트
            Transform? parentTransform = null;
            var selectedId = EditorSelection.SelectedGameObjectId;
            if (selectedId != null)
            {
                var parentGo = SceneManager.AllGameObjects
                    .FirstOrDefault(g => !g._isDestroyed && g.GetInstanceID() == selectedId.Value);
                parentTransform = parentGo?.transform;
            }

            var newIds = new List<int>();
            foreach (var goArray in _goEntries)
            {
                var created = SceneSerializer.DeserializeGameObjectHierarchy(goArray);
                if (created.Count == 0) continue;

                var root = created[0];

                // 고유 이름 생성
                root.name = GenerateUniqueName(root.name, parentTransform);

                if (parentTransform != null)
                    root.transform.SetParent(parentTransform, worldPositionStays: false);

                newIds.Add(root.GetInstanceID());
            }

            // 잘라내기 → 원본 삭제 (Undo 지원)
            if (IsCut && _cutGoIds != null)
            {
                var actions = new List<IUndoAction>();
                for (int i = _cutGoIds.Count - 1; i >= 0; i--)
                {
                    var go = SceneManager.AllGameObjects
                        .FirstOrDefault(g => !g._isDestroyed && g.GetInstanceID() == _cutGoIds[i]);
                    if (go == null) continue;
                    actions.Add(new DeleteGameObjectAction($"Cut {go.name}", go));
                    RoseEngine.Object.DestroyImmediate(go);
                }
                if (actions.Count == 1)
                    UndoSystem.Record(actions[0]);
                else if (actions.Count > 1)
                    UndoSystem.Record(new CompoundUndoAction($"Cut {actions.Count} objects", actions));
            }

            if (newIds.Count > 0)
            {
                EditorSelection.SetSelection(newIds);
                SceneManager.GetActiveScene().isDirty = true;
            }

            // 잘라내기 → 한 번만 붙여넣기
            if (IsCut)
                Clear();
        }

        // ================================================================
        // Copy / Cut — Assets
        // ================================================================

        public static void CopyAssets(IReadOnlyList<string> paths, bool cut)
        {
            if (paths.Count == 0) return;
            _assetPaths = paths.Where(p => File.Exists(p)).ToList();
            if (_assetPaths.Count == 0) { _assetPaths = null; return; }

            ClipboardKind = Kind.Assets;
            IsCut = cut;
        }

        // ================================================================
        // Paste — Assets (대상 디렉터리에 복사/이동)
        // ================================================================

        /// <summary>
        /// 클립보드 에셋을 targetDirectory에 붙여넣기.
        /// Copy+같은폴더 → duplicate (새 GUID), Copy+다른폴더 → 복사 (새 GUID),
        /// Cut+다른폴더 → 이동 (GUID 유지), Cut+같은폴더 → no-op.
        /// </summary>
        /// <returns>파일이 실제로 생성/이동되었으면 true.</returns>
        public static bool PasteAssets(string targetDirectory)
        {
            if (ClipboardKind != Kind.Assets || _assetPaths == null || _assetPaths.Count == 0)
                return false;

            if (!Directory.Exists(targetDirectory))
                return false;

            bool changed = false;

            foreach (var sourcePath in _assetPaths)
            {
                if (!File.Exists(sourcePath)) continue;

                var sourceDir = Path.GetFullPath(Path.GetDirectoryName(sourcePath)!);
                var targetDir = Path.GetFullPath(targetDirectory);
                bool sameFolder = sourceDir == targetDir;

                if (IsCut)
                {
                    // Cut + 같은 폴더 → no-op
                    if (sameFolder) continue;

                    // Cut + 다른 폴더 → 이동 (GUID 유지)
                    var fileName = Path.GetFileName(sourcePath);
                    var destPath = Path.Combine(targetDirectory, fileName);
                    if (File.Exists(destPath))
                        destPath = GenerateUniqueAssetPath(destPath);

                    File.Move(sourcePath, destPath);
                    var oldRose = sourcePath + ".rose";
                    var newRose = destPath + ".rose";
                    if (File.Exists(oldRose))
                        File.Move(oldRose, newRose);
                    changed = true;
                }
                else
                {
                    // Copy → 항상 새 파일 생성 (새 GUID)
                    var fileName = Path.GetFileName(sourcePath);
                    var destPath = Path.Combine(targetDirectory, fileName);

                    // 같은 폴더이거나 이미 존재 → 고유 이름
                    if (sameFolder || File.Exists(destPath))
                        destPath = GenerateUniqueAssetPath(destPath);

                    File.Copy(sourcePath, destPath);
                    // .rose 사이드카: 새 GUID로 복사
                    CopyRoseMetadata(sourcePath, destPath);
                    changed = true;
                }
            }

            // 잘라내기 → 한 번만 붙여넣기
            if (IsCut)
                Clear();

            return changed;
        }

        private static void CopyRoseMetadata(string sourcePath, string destPath)
        {
            var srcRose = sourcePath + ".rose";
            if (!File.Exists(srcRose)) return;

            var meta = RoseMetadata.LoadOrCreate(sourcePath);
            var newMeta = new RoseMetadata
            {
                version = meta.version,
                labels = meta.labels?.ToArray(),
            };
            var newImporter = new TomlTable();
            foreach (var kvp in meta.importer)
                newImporter[kvp.Key] = kvp.Value;
            newMeta.importer = newImporter;
            foreach (var sub in meta.subAssets)
            {
                newMeta.subAssets.Add(new SubAssetEntry
                {
                    name = sub.name,
                    type = sub.type,
                    index = sub.index,
                });
            }
            newMeta.Save(destPath + ".rose");
        }

        // ================================================================
        // Helpers
        // ================================================================

        public static void Clear()
        {
            ClipboardKind = Kind.None;
            IsCut = false;
            _goEntries = null;
            _cutGoIds = null;
            _assetPaths = null;
        }

        private static string GenerateUniqueName(string name, Transform? parent)
        {
            string nameBase = name;
            var numSuffix = Regex.Match(name, @"^(.+)_(\d+)$");
            if (numSuffix.Success)
                nameBase = numSuffix.Groups[1].Value;

            IEnumerable<GameObject> siblings;
            if (parent != null)
                siblings = Enumerable.Range(0, parent.childCount)
                    .Select(i => parent.GetChild(i).gameObject);
            else
                siblings = SceneManager.AllGameObjects
                    .Where(g => !g._isDestroyed && g.transform.parent == null);

            int maxNum = 0;
            var namePattern = new Regex($@"^{Regex.Escape(nameBase)}_(\d+)$");
            foreach (var sib in siblings)
            {
                var m = namePattern.Match(sib.name);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int num))
                    maxNum = System.Math.Max(maxNum, num);
            }

            return $"{nameBase}_{(maxNum + 1):D2}";
        }

        private static string GenerateUniqueAssetPath(string path)
        {
            var dir = Path.GetDirectoryName(path)!;
            var ext = Path.GetExtension(path);
            var baseName = Path.GetFileNameWithoutExtension(path);

            string nameBase = baseName;
            var numSuffix = Regex.Match(baseName, @"^(.+)_(\d+)$");
            if (numSuffix.Success)
                nameBase = numSuffix.Groups[1].Value;

            int maxNum = 0;
            var escapedExt = Regex.Escape(ext);
            var namePattern = new Regex($@"^{Regex.Escape(nameBase)}_(\d+){escapedExt}$");
            foreach (var file in Directory.GetFiles(dir))
            {
                var m = namePattern.Match(Path.GetFileName(file));
                if (m.Success && int.TryParse(m.Groups[1].Value, out int num))
                    maxNum = System.Math.Max(maxNum, num);
            }

            return Path.Combine(dir, $"{nameBase}_{(maxNum + 1):D2}{ext}");
        }
    }
}
