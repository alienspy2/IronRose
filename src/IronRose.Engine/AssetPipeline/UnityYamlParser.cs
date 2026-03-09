using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace IronRose.AssetPipeline
{
    public class UnityYamlParser
    {
        private readonly IDeserializer _deserializer;

        public UnityYamlParser()
        {
            _deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
        }

        public T Parse<T>(string yamlContent)
        {
            return _deserializer.Deserialize<T>(yamlContent);
        }

        public object? Parse(string yamlContent)
        {
            return _deserializer.Deserialize(yamlContent);
        }
    }

    public class UnityAsset
    {
        public string guid { get; set; } = string.Empty;
        public long fileID { get; set; }
        public int type { get; set; }
    }

    public class UnityPrefab
    {
        public Dictionary<string, object> GameObject { get; set; } = new();
        public Dictionary<string, object> Transform { get; set; } = new();
        public Dictionary<string, object>[] MonoBehaviour { get; set; } = [];
    }
}
