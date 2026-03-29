using System.IO;
using RoseEngine;

namespace IronRose.AssetPipeline
{
    public class TextAssetImporter
    {
        public TextAsset? Import(string path, RoseMetadata meta)
        {
            if (!File.Exists(path)) return null;

            var asset = new TextAsset
            {
                name = Path.GetFileNameWithoutExtension(path),
                text = File.ReadAllText(path),
                bytes = File.ReadAllBytes(path),
            };
            return asset;
        }
    }
}
