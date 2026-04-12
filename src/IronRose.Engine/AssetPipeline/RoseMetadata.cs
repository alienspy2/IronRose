// ------------------------------------------------------------
// @file    RoseMetadata.cs
// @brief   .rose 메타데이터 파일의 로드/저장을 담당한다. 에셋의 GUID, 버전,
//          라벨, 임포터 설정, 서브에셋 목록을 관리한다.
// @deps    IronRose.Engine/TomlConfig, IronRose.Engine/TomlConfigArray,
//          Tomlyn.Model (InferImporter가 TomlTable/TomlArray 직접 사용)
// @exports
//   class SubAssetEntry
//     name, type, index, guid                                   — 서브에셋 엔트리 정보
//   class RoseMetadata
//     guid, version, labels, importer (TomlTable), subAssets    — 메타데이터 필드
//     static OnSaved: event Action<string>                      — 저장 시 이벤트
//     static LoadOrCreate(string): RoseMetadata                 — .rose 파일 로드 또는 생성
//     Save(string): void                                        — .rose 파일 저장
//     GetOrCreateSubAsset(string, string, int): SubAssetEntry  — 서브에셋 찾기/생성
//     PruneSubAssets(HashSet<string>): void                     — 미사용 서브에셋 제거
// @note    importer 필드 타입은 TomlTable을 유지한다 (10+ 파일에서 직접 접근).
//          InferImporter()는 TomlTable을 직접 사용하므로 변경하지 않는다.
//          using Tomlyn.Model은 InferImporter 때문에 유지해야 한다.
// ------------------------------------------------------------
using Tomlyn.Model;
using IronRose.Engine;
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
                var config = TomlConfig.LoadFile(rosePath, "[RoseMetadata]");
                if (config != null)
                    return FromToml(config.GetRawTable());
            }

            var meta = new RoseMetadata();
            meta.importer = InferImporter(assetPath);
            meta.Save(rosePath);
            return meta;
        }

        public void Save(string rosePath)
        {
            var config = ToConfig();
            config.SaveToFile(rosePath);

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
                    ["quality"] = "High",
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
                ".txt" or ".json" or ".xml" or ".csv" => new TomlTable
                {
                    ["type"] = "TextAssetImporter",
                },
                _ => new TomlTable { ["type"] = "DefaultImporter" },
            };
        }

        private TomlConfig ToConfig()
        {
            var config = TomlConfig.CreateEmpty();
            config.SetValue("guid", guid);
            config.SetValue("version", (long)version);

            if (labels != null && labels.Length > 0)
            {
                var arr = new TomlArray();
                foreach (var label in labels)
                    arr.Add(label);
                config.SetValue("labels", arr);
            }

            if (importer.Count > 0)
                config.GetRawTable()["importer"] = importer; // TomlTable 직접 삽입

            if (subAssets.Count > 0)
            {
                var subArr = new TomlConfigArray();
                foreach (var sub in subAssets)
                {
                    var subConfig = TomlConfig.CreateEmpty();
                    subConfig.SetValue("name", sub.name);
                    subConfig.SetValue("type", sub.type);
                    subConfig.SetValue("index", (long)sub.index);
                    subConfig.SetValue("guid", sub.guid);
                    subArr.Add(subConfig);
                }
                config.SetArray("sub_assets", subArr);
            }

            return config;
        }

        private static RoseMetadata FromToml(TomlTable table)
        {
            var config = new TomlConfig(table); // internal 생성자 사용
            var meta = new RoseMetadata();

            meta.guid = config.GetString("guid", meta.guid);
            meta.version = config.GetInt("version", meta.version);

            var labelsValues = config.GetValues("labels");
            if (labelsValues != null)
            {
                meta.labels = labelsValues
                    .Where(x => x != null)
                    .Select(x => x!.ToString()!)
                    .ToArray();
            }

            // importer는 TomlTable 그대로 사용 (Phase 5까지 유지)
            if (table.TryGetValue("importer", out var impVal) && impVal is TomlTable impTable)
                meta.importer = impTable;

            var subArr = config.GetArray("sub_assets");
            if (subArr != null)
            {
                foreach (var subConfig in subArr)
                {
                    var entry = new SubAssetEntry();
                    entry.name = subConfig.GetString("name", "");
                    entry.type = subConfig.GetString("type", "");
                    entry.index = subConfig.GetInt("index", 0);
                    entry.guid = subConfig.GetString("guid", entry.guid);
                    meta.subAssets.Add(entry);
                }
            }

            return meta;
        }
    }
}
