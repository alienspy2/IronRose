using IronRose.AssetPipeline;

namespace RoseEngine
{
    public static class Resources
    {
        private static AssetDatabase? _assetDatabase;

        internal static void SetAssetDatabase(AssetDatabase db)
        {
            _assetDatabase = db;
        }

        public static T? Load<T>(string path) where T : class
        {
            if (_assetDatabase == null)
            {
                Debug.LogError("[Resources] AssetDatabase not initialized");
                return null;
            }

            return _assetDatabase.Load<T>(path);
        }

        public static AssetDatabase? GetAssetDatabase() => _assetDatabase;
    }
}
