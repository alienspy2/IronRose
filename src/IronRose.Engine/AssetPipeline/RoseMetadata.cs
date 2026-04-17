// ------------------------------------------------------------
// @file    RoseMetadata.cs
// @brief   .rose 메타데이터 파일의 로드/저장을 담당한다. 에셋의 GUID, 버전,
//          라벨, 임포터 설정, 서브에셋 목록을 관리한다.
// @deps    IronRose.Engine/TomlConfig, IronRose.Engine/TomlConfigArray,
//          Tomlyn.Model (InferImporter가 TomlTable/TomlArray 직접 사용),
//          RoseEngine.EditorDebug (DetectDefaultTextureType 경고 로그),
//          IronRose.AssetPipeline/TextureMetadataMigration
// @exports
//   class SubAssetEntry
//     name, type, index, guid                                   — 서브에셋 엔트리 정보
//   class RoseMetadata
//     guid, version, labels, importer (TomlTable), subAssets    — 메타데이터 필드
//     internal _migrated: bool                                  — FromToml에서 마이그레이션 수행 플래그 (직렬화 X)
//     static OnSaved: event Action<string>                      — 저장 시 이벤트
//     static LoadOrCreate(string): RoseMetadata                 — .rose 파일 로드 또는 생성. 마이그레이션 시 자동 재저장
//     Save(string): void                                        — .rose 파일 저장
//     GetOrCreateSubAsset(string, string, int): SubAssetEntry  — 서브에셋 찾기/생성
//     PruneSubAssets(HashSet<string>): void                     — 미사용 서브에셋 제거
// @note    importer 필드 타입은 TomlTable을 유지한다 (10+ 파일에서 직접 접근).
//          InferImporter()는 TomlTable을 직접 사용하므로 변경하지 않는다.
//          using Tomlyn.Model은 InferImporter 때문에 유지해야 한다.
//          DetectDefaultTextureType()은 신규 PNG/TGA 임포트 시 헤더 스캔으로
//          알파 채널 유무를 판정한다. 기존 .rose는 FromToml 경로로 로드되어
//          영향받지 않는다.
//          Phase 1 변경: TextureImporter InferImporter에서 compression 키 제거,
//          .hdr/.exr는 quality="High" 추가. FromToml에서 TextureMetadataMigration.Apply
//          호출하여 구버전 compression 키 정리. _migrated=true이면 LoadOrCreate가 자동 저장.
// ------------------------------------------------------------
using Tomlyn.Model;
using IronRose.Engine;
using RoseEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Debug = RoseEngine.EditorDebug;

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
        /// FromToml에서 메타데이터 마이그레이션(구버전 compression 키 정리 등)이
        /// 수행되었는지 여부. TOML에는 직렬화되지 않는다. LoadOrCreate가 true일 때
        /// 파일을 자동 저장하여 마이그레이션 결과를 디스크에 반영한다.
        /// </summary>
        internal bool _migrated { get; set; }

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
                {
                    var loaded = FromToml(config.GetRawTable());
                    if (loaded._migrated)
                    {
                        loaded.Save(rosePath);
                        loaded._migrated = false;
                        Debug.Log($"[RoseMetadata] Migrated legacy importer keys: {rosePath}");
                    }
                    return loaded;
                }
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
                    ["texture_type"] = "HDR",
                    ["quality"] = "High",
                    ["srgb"] = false,
                    ["filter_mode"] = "Bilinear",
                    ["wrap_mode"] = "Repeat",
                    ["generate_mipmaps"] = true,
                },
                ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" => new TomlTable
                {
                    ["type"] = "TextureImporter",
                    ["max_size"] = (long)2048,
                    ["quality"] = "Low",
                    ["texture_type"] = DetectDefaultTextureType(assetPath, ext),
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

        /// <summary>
        /// 신규 텍스처 임포트 시, 파일 헤더를 읽어 알파 채널 유무에 따라
        /// 기본 texture_type을 "Color" 또는 "ColorWithAlpha"로 결정한다.
        /// 실패 시 안전하게 "Color"를 반환한다.
        /// </summary>
        private static string DetectDefaultTextureType(string assetPath, string extLower)
        {
            // JPEG/BMP는 알파 없음(표준). 32비트 BMP는 드물어 무시한다.
            if (extLower == ".jpg" || extLower == ".jpeg" || extLower == ".bmp")
                return "Color";

            if (extLower != ".png" && extLower != ".tga")
                return "Color";

            try
            {
                using var fs = new FileStream(assetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                Span<byte> header = stackalloc byte[32];
                int read = fs.Read(header);

                if (extLower == ".png")
                {
                    // PNG magic(8) + IHDR length(4) + IHDR type(4) + width(4) + height(4) + bitDepth(1) = colorType at byte 25.
                    if (read < 26) return "Color";
                    if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
                        return "Color";
                    byte colorType = header[25];
                    // 2=RGB, 6=RGBA, 4=Gray+Alpha, 3=Palette(+tRNS 가능). palette는 보수적으로 ColorWithAlpha 취급.
                    bool hasAlpha = colorType == 4 || colorType == 6 || colorType == 3;
                    return hasAlpha ? "ColorWithAlpha" : "Color";
                }

                if (extLower == ".tga")
                {
                    // TGA header: byte 16 = pixel depth.
                    if (read < 18) return "Color";
                    byte pixelDepth = header[16];
                    return pixelDepth == 32 ? "ColorWithAlpha" : "Color";
                }
            }
            catch (Exception ex)
            {
                EditorDebug.LogWarning($"[RoseMetadata] DetectDefaultTextureType failed for {assetPath}: {ex.Message}. Falling back to Color.");
            }

            return "Color";
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
            {
                meta.importer = impTable;
                // 구버전 compression 키 등 TextureImporter 섹션 마이그레이션.
                // 변경이 있었으면 LoadOrCreate가 Save 호출.
                if (TextureMetadataMigration.Apply(meta.importer))
                    meta._migrated = true;
            }

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
