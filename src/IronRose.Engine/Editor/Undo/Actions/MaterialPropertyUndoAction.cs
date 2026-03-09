using System.IO;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// .mat 파일의 TOML 스냅샷 기반 Undo/Redo 액션.
    /// 변경 전/후 전체 TOML 문자열을 저장하여 파일 복원 + 재임포트를 수행한다.
    /// </summary>
    public sealed class MaterialPropertyUndoAction : IUndoAction
    {
        public string Description { get; }

        private readonly string _matPath;
        private readonly string _oldToml;
        private readonly string _newToml;

        /// <summary>Undo/Redo 후 Inspector가 파일을 다시 읽도록 트리거하는 전역 버전.</summary>
        public static long GlobalVersion { get; private set; }

        public MaterialPropertyUndoAction(string description, string matPath, string oldToml, string newToml)
        {
            Description = description;
            _matPath = matPath;
            _oldToml = oldToml;
            _newToml = newToml;
        }

        public void Undo() => Apply(_oldToml);
        public void Redo() => Apply(_newToml);

        private void Apply(string toml)
        {
            File.WriteAllText(_matPath, toml);
            GlobalVersion++;

            var db = Resources.GetAssetDatabase();
            db?.ReimportAsync(_matPath);
        }
    }
}
