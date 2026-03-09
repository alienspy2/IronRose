using Tomlyn;
using Tomlyn.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IronRose.AssetPipeline
{
    public class SubAssetEntry
    {
        public string name { get; set; } = "";
        public string type { get; set; } = "";       // "Mesh", "Material", "Texture2D"
        public int index { get; set; }
        public string guid { get; set; } = Guid.NewGuid().ToString();
    }

    public class RoseMetadata
    {
        public string guid { get; set; } = Guid.NewGuid().ToString();
        public int version { get; set; } = 1;
        public string[]? labels { get; set; }
        public TomlTable importer { get; set; } = new();
        public List<SubAssetEntry> subAssets { get; set; } = new();

        /// <summary>
        /// .rose 파일이 저장될 때 발생. 인자는 에셋 경로 (.rose 제외).
        /// AssetDatabase가 구독하여 자동 reimport 처리.
        /// </summary>
        public static event Action<string>? OnSaved;

        public static RoseMetadata LoadOrCreate(string assetPath)
        {
            var rosePath = assetPath + ".rose";

            if (File.Exists(rosePath))
            {
                var toml = Toml.ToModel(File.ReadAllText(rosePath));
                return FromToml(toml);
            }

            var meta = new RoseMetadata();
            meta.importer = InferImporter(assetPath);
            meta.Save(rosePath);
            return meta;
        }

        public void Save(string rosePath)
        {
            var toml = ToToml();
            File.WriteAllText(rosePath, Toml.FromModel(toml));

            if (rosePath.EndsWith(".rose", StringComparison.OrdinalIgnoreCase))
                OnSaved?.Invoke(rosePath[..^5]);
        }

        /// <summary>
        /// 이름+타입으로 기존 sub-asset을 찾거나, 없으면 새 GUID로 생성.
        /// Reimport 시 GUID 안정성을 보장한다.
        /// </summary>
        public SubAssetEntry GetOrCreateSubAsset(string name, string type, int index)
        {
            // 1. 이름+타입으로 기존 찾기 (GUID 안정성)
            var existing = subAssets.FirstOrDefault(s => s.name == name && s.type == type);
            if (existing != null)
            {
                existing.index = index;
                return existing;
            }

            // 2. 타입+인덱스로 폴백 (임포터 변경으로 이름이 바뀐 경우 GUID 보존)
            var byIndex = subAssets.FirstOrDefault(s => s.type == type && s.index == index);
            if (byIndex != null)
            {
                byIndex.name = name;
                return byIndex;
            }

            var entry = new SubAssetEntry { name = name, type = type, index = index };
            subAssets.Add(entry);
            return entry;
        }

        /// <summary>
        /// 임포트 결과에 없는 stale sub-asset 엔트리를 제거한다.
        /// </summary>
        public void PruneSubAssets(HashSet<string> activeKeys)
        {
            subAssets.RemoveAll(s => !activeKeys.Contains($"{s.type}:{s.name}"));
        }

        private static TomlTable InferImporter(string assetPath)
        {
            var ext = Path.GetExtension(assetPath).ToLowerInvariant();
            return ext switch
            {
                ".glb" or ".gltf" or ".fbx" or ".obj" => new TomlTable
                {
                    ["type"] = "MeshImporter",
                    ["scale"] = 1.0,
                    ["generate_normals"] = true,
                    ["flip_uvs"] = true,
                    ["triangulate"] = true,
                    ["generate_mipmesh"] = false,
                    ["mipmesh_min_triangles"] = (long)500,
                    ["mipmesh_target_error"] = 0.02,
                    ["mipmesh_reduction"] = 0.1,
                },
                ".hdr" or ".exr" => new TomlTable
                {
                    ["type"] = "TextureImporter",
                    ["max_size"] = (long)4096,
                    ["compression"] = "BC6H",
                    ["texture_type"] = "HDR",
                    ["srgb"] = false,
                    ["filter_mode"] = "Bilinear",
                    ["wrap_mode"] = "Repeat",
                    ["generate_mipmaps"] = true,
                },
                ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" => new TomlTable
                {
                    ["type"] = "TextureImporter",
                    ["max_size"] = (long)2048,
                    ["compression"] = "BC7",
                    ["texture_type"] = "Color",
                    ["srgb"] = true,
                    ["filter_mode"] = "Bilinear",
                    ["wrap_mode"] = "Repeat",
                    ["generate_mipmaps"] = true,
                },
                ".ttf" or ".otf" => new TomlTable
                {
                    ["type"] = "FontImporter",
                    ["font_size"] = (long)32,
                },
                ".prefab" => new TomlTable
                {
                    ["type"] = "PrefabImporter",
                },
                ".mat" => new TomlTable
                {
                    ["type"] = "MaterialImporter",
                },
                ".anim" => new TomlTable
                {
                    ["type"] = "AnimationClipImporter",
                    ["frame_rate"] = (long)60,
                    ["wrap_mode"] = "Once",
                },
                ".renderer" => new TomlTable
                {
                    ["type"] = "RendererProfileImporter",
                },
                ".ppprofile" => new TomlTable
                {
                    ["type"] = "PostProcessProfileImporter",
                },
                _ => new TomlTable { ["type"] = "DefaultImporter" },
            };
        }

        private TomlTable ToToml()
        {
            var table = new TomlTable
            {
                ["guid"] = guid,
                ["version"] = (long)version,
            };

            if (labels != null && labels.Length > 0)
            {
                var arr = new TomlArray();
                foreach (var label in labels)
                    arr.Add(label);
                table["labels"] = arr;
            }

            if (importer.Count > 0)
            {
                table["importer"] = importer;
            }

            if (subAssets.Count > 0)
            {
                var subArr = new TomlTableArray();
                foreach (var sub in subAssets)
                {
                    subArr.Add(new TomlTable
                    {
                        ["name"] = sub.name,
                        ["type"] = sub.type,
                        ["index"] = (long)sub.index,
                        ["guid"] = sub.guid,
                    });
                }
                table["sub_assets"] = subArr;
            }

            return table;
        }

        private static RoseMetadata FromToml(TomlTable table)
        {
            var meta = new RoseMetadata();

            if (table.TryGetValue("guid", out var guidVal))
                meta.guid = guidVal?.ToString() ?? meta.guid;

            if (table.TryGetValue("version", out var verVal) && verVal is long v)
                meta.version = (int)v;

            if (table.TryGetValue("labels", out var labelsVal) && labelsVal is TomlArray labelsArr)
            {
                meta.labels = labelsArr
                    .Where(x => x != null)
                    .Select(x => x!.ToString()!)
                    .ToArray();
            }

            if (table.TryGetValue("importer", out var impVal) && impVal is TomlTable impTable)
                meta.importer = impTable;

            if (table.TryGetValue("sub_assets", out var subVal) && subVal is TomlTableArray subArr)
            {
                foreach (TomlTable subTable in subArr)
                {
                    var entry = new SubAssetEntry();
                    if (subTable.TryGetValue("name", out var nameVal))
                        entry.name = nameVal?.ToString() ?? "";
                    if (subTable.TryGetValue("type", out var typeVal))
                        entry.type = typeVal?.ToString() ?? "";
                    if (subTable.TryGetValue("index", out var idxVal) && idxVal is long idx)
                        entry.index = (int)idx;
                    if (subTable.TryGetValue("guid", out var gVal))
                        entry.guid = gVal?.ToString() ?? entry.guid;
                    meta.subAssets.Add(entry);
                }
            }

            return meta;
        }
    }
}
