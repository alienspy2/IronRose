namespace IronRose.AssetPipeline
{
    /// <summary>
    /// Sub-asset 경로 유틸리티.
    /// 형식: "Assets/Model.glb#Mesh:0", "Assets/Model.glb#Material:1"
    /// </summary>
    public static class SubAssetPath
    {
        public static bool TryParse(string fullPath, out string filePath, out string type, out int index)
        {
            int hashIdx = fullPath.IndexOf('#');
            if (hashIdx < 0)
            {
                filePath = fullPath;
                type = "";
                index = -1;
                return false;
            }

            filePath = fullPath[..hashIdx];
            var fragment = fullPath[(hashIdx + 1)..]; // "Mesh:0"
            int colonIdx = fragment.IndexOf(':');
            if (colonIdx < 0)
            {
                type = fragment;
                index = 0;
                return true;
            }

            type = fragment[..colonIdx];
            return int.TryParse(fragment[(colonIdx + 1)..], out index);
        }

        public static string Build(string filePath, string type, int index)
            => $"{filePath}#{type}:{index}";

        public static bool IsSubAssetPath(string path)
            => path.Contains('#');
    }
}
