using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using IronRose.AssetPipeline;
using IronRose.Engine.Editor.ImGuiEditor;
using IronRose.Engine.Editor.ImGuiEditor.Panels;
using IronRose.Engine.Editor.SceneView;
using RoseEngine;
using Tomlyn;
using Tomlyn.Model;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 씬을 TOML (.scene) 파일로 직렬화/역직렬화한다.
    /// GUID 기반 에셋 참조 시스템 사용.
    /// </summary>
    public static class SceneSerializer
    {
        // ================================================================
        // Save
        // ================================================================

        /// <summary>현재 씬을 TOML로 직렬화하여 파일에 저장.</summary>
        public static void Save(string filePath)
        {
            var tomlStr = BuildSceneToml();
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, tomlStr);

            SceneManager.GetActiveScene().isDirty = false;
            EditorDebug.Log($"[Scene] Saved: {filePath}");
        }

        /// <summary>현재 씬을 TOML 문자열로 직렬화 (디스크 I/O 없음).</summary>
        public static string SaveToString()
        {
            return BuildSceneToml();
        }

        private static string BuildSceneToml()
        {
            var scene = SceneManager.GetActiveScene();
            var root = new TomlTable
            {
                ["name"] = scene.name,
            };

            var goArray = new TomlTableArray();
            var allGOs = SceneManager.AllGameObjects;

            // 직렬화 대상 GO만 필터링하고 인스턴스ID → 직렬화 인덱스 매핑
            // 프리팹 인스턴스의 자식은 제외 (프리팹 원본에서 복원되므로)
            var prefabInstanceIds = new HashSet<int>();
            for (int i = 0; i < allGOs.Count; i++)
            {
                var go = allGOs[i];
                if (!go._isDestroyed && !go._isEditorInternal && go.GetComponent<PrefabInstance>() != null)
                    prefabInstanceIds.Add(go.GetInstanceID());
            }

            var serializableGOs = new List<GameObject>();
            var idToIndex = new Dictionary<int, int>();
            for (int i = 0; i < allGOs.Count; i++)
            {
                var go = allGOs[i];
                if (go._isDestroyed || go._isEditorInternal) continue;

                // 프리팹 인스턴스의 자식이면 건너뛰기 (중첩 PrefabInstance 포함)
                if (IsChildOfPrefabInstance(go, prefabInstanceIds))
                    continue;

                idToIndex[go.GetInstanceID()] = serializableGOs.Count;
                serializableGOs.Add(go);
            }

            for (int i = 0; i < serializableGOs.Count; i++)
            {
                var go = serializableGOs[i];

                var goTable = new TomlTable
                {
                    ["name"] = go.name,
                    ["guid"] = go.guid,
                    ["activeSelf"] = go.activeSelf,
                };

                // Transform
                var t = go.transform;
                int parentIndex = -1;
                if (t.parent != null && idToIndex.TryGetValue(t.parent.gameObject.GetInstanceID(), out var pi))
                    parentIndex = pi;

                var transformTable = new TomlTable
                {
                    ["localPosition"] = Vec3ToArray(t.localPosition),
                    ["localRotation"] = QuatToArray(t.localRotation),
                    ["localScale"] = Vec3ToArray(t.localScale),
                    ["parentIndex"] = (long)parentIndex,
                };
                goTable["transform"] = transformTable;

                // RectTransform 추가 데이터
                var rectTransform = go.GetComponent<RectTransform>();
                if (rectTransform != null)
                {
                    goTable["rectTransform"] = new TomlTable
                    {
                        ["anchorMin"] = Vec2ToArray(rectTransform.anchorMin),
                        ["anchorMax"] = Vec2ToArray(rectTransform.anchorMax),
                        ["anchoredPosition"] = Vec2ToArray(rectTransform.anchoredPosition),
                        ["sizeDelta"] = Vec2ToArray(rectTransform.sizeDelta),
                        ["pivot"] = Vec2ToArray(rectTransform.pivot),
                    };
                }

                // PrefabInstance — GUID + Transform만 저장, 컴포넌트 건너뛰기
                var prefabInst = go.GetComponent<PrefabInstance>();
                if (prefabInst != null)
                {
                    goTable["prefabInstance"] = new TomlTable
                    {
                        ["prefabGuid"] = prefabInst.prefabGuid ?? "",
                    };
                    // 프리팹 인스턴스는 컴포넌트를 저장하지 않음 (원본에서 복원)
                    goArray.Add(goTable);
                    continue;
                }

                // Components (Transform 제외)
                var compArray = new TomlTableArray();
                foreach (var comp in go.InternalComponents)
                {
                    if (comp is Transform || comp is RectTransform) continue;

                    var compTable = SerializeComponent(comp);
                    if (compTable != null)
                        compArray.Add(compTable);
                }

                if (compArray.Count > 0)
                    goTable["components"] = compArray;

                goArray.Add(goTable);
            }

            root["gameObjects"] = goArray;

            // Scene Environment (씬별 환경 설정 — Phase 38-B)
            var envTable = new TomlTable();
            if (!string.IsNullOrEmpty(RenderSettings.skyboxTextureGuid))
                envTable["skyboxTextureGuid"] = RenderSettings.skyboxTextureGuid;
            envTable["skyboxExposure"] = (double)RenderSettings.skyboxExposure;
            envTable["skyboxRotation"] = (double)RenderSettings.skyboxRotation;
            envTable["ambientIntensity"] = (double)RenderSettings.ambientIntensity;
            envTable["ambientLight"] = ColorToArray(RenderSettings.ambientLight);
            envTable["skyZenithIntensity"] = (double)RenderSettings.skyZenithIntensity;
            envTable["skyHorizonIntensity"] = (double)RenderSettings.skyHorizonIntensity;
            envTable["sunIntensity"] = (double)RenderSettings.sunIntensity;
            envTable["skyZenithColor"] = ColorToArray(RenderSettings.skyZenithColor);
            envTable["skyHorizonColor"] = ColorToArray(RenderSettings.skyHorizonColor);
            root["sceneEnvironment"] = envTable;

            // EditorState — hierarchy expand/collapse + Scene View 카메라
            var editorStateTable = new TomlTable();

            var panel = ImGuiHierarchyPanel.Instance;
            if (panel != null && panel.OpenNodeIds.Count > 0)
            {
                var expandedGuids = new TomlArray();
                foreach (var go in serializableGOs)
                    if (panel.OpenNodeIds.Contains(go.GetInstanceID()))
                        expandedGuids.Add(go.guid);
                if (expandedGuids.Count > 0)
                    editorStateTable["expandedNodes"] = expandedGuids;
            }

            // Scene View 카메라 상태 저장
            var edCam = EditorBridge.GetImGuiOverlay<IronRose.Engine.Editor.ImGuiEditor.ImGuiOverlay>()?.EditorCameraInstance;
            if (edCam != null)
            {
                editorStateTable["sceneViewCamera"] = new TomlTable
                {
                    ["position"] = Vec3ToArray(edCam.Position),
                    ["rotation"] = QuatToArray(edCam.Rotation),
                    ["pivot"] = Vec3ToArray(edCam.Pivot),
                };
            }

            if (editorStateTable.Count > 0)
                root["editorState"] = editorStateTable;

            return Toml.FromModel(root);
        }

        internal static TomlTable? SerializeComponent(Component comp)
        {
            // PrefabInstance는 씬 레벨에서 별도 처리 (컴포넌트로 직렬화하지 않음)
            if (comp is PrefabInstance) return null;

            if (comp is Camera cam)
            {
                return new TomlTable
                {
                    ["type"] = "Camera",
                    ["fields"] = new TomlTable
                    {
                        ["fieldOfView"] = (double)cam.fieldOfView,
                        ["nearClipPlane"] = (double)cam.nearClipPlane,
                        ["farClipPlane"] = (double)cam.farClipPlane,
                        ["clearFlags"] = cam.clearFlags.ToString(),
                        ["backgroundColor"] = ColorToArray(cam.backgroundColor),
                    },
                };
            }

            // Light — 범용 리플렉션 직렬화 사용 (하드코딩 제거)

            if (comp is MeshFilter mf)
            {
                var fields = new TomlTable();

                // 1) 프리미티브 메시 확인
                var primType = InferPrimitiveType(mf.mesh);
                if (primType != null)
                {
                    fields["primitiveType"] = primType;
                    EditorDebug.Log($"[Scene:Save] MeshFilter on '{comp.gameObject.name}': primitive={primType}");
                }
                else
                {
                    // 2) GUID 기반 에셋 참조
                    var db = Resources.GetAssetDatabase();
                    var meshGuid = mf.mesh != null ? db?.FindGuidForMesh(mf.mesh) : null;
                    if (meshGuid != null)
                    {
                        fields["assetGuid"] = meshGuid;
                        EditorDebug.Log($"[Scene:Save] MeshFilter on '{comp.gameObject.name}': meshGuid={meshGuid}, meshName={mf.mesh?.name}");
                    }
                    else
                    {
                        // GUID 해석 실패 — 빈 필드로 직렬화 (복구 가능하도록)
                        fields["assetGuid"] = "";
                        EditorDebug.LogWarning($"[Scene:Save] MeshFilter on '{comp.gameObject.name}': invalid ref — mesh={mf.mesh?.name ?? "null"}, no GUID found");
                    }
                }

                return new TomlTable { ["type"] = "MeshFilter", ["fields"] = fields };
            }

            if (comp is MipMeshFilter mmf)
            {
                var fields = new TomlTable
                {
                    ["mipBias"] = (double)mmf.mipBias,
                    ["lodScale"] = (double)mmf.lodScale,
                };

                // MipMesh는 MeshFilter와 같은 에셋의 Mesh GUID를 사용
                var db = Resources.GetAssetDatabase();
                var mfComp = comp.gameObject.GetComponent<MeshFilter>();
                var meshGuid = mfComp?.mesh != null ? db?.FindGuidForMesh(mfComp.mesh) : null;
                if (meshGuid != null)
                {
                    fields["meshGuid"] = meshGuid;
                    EditorDebug.Log($"[Scene:Save] MipMeshFilter on '{comp.gameObject.name}': meshGuid={meshGuid}");
                }
                else
                {
                    // GUID 해석 실패 — 빈 필드로 직렬화 (복구 가능하도록)
                    fields["meshGuid"] = "";
                    EditorDebug.LogWarning($"[Scene:Save] MipMeshFilter on '{comp.gameObject.name}': invalid ref — mfMesh={mfComp?.mesh?.name ?? "null"}, no GUID found");
                }

                return new TomlTable { ["type"] = "MipMeshFilter", ["fields"] = fields };
            }

            if (comp is MeshRenderer mr)
            {
                var fields = new TomlTable();

                if (!mr.enabled)
                    fields["enabled"] = false;

                // GUID 기반 머터리얼 참조
                var db = Resources.GetAssetDatabase();
                var matGuid = mr.material != null ? db?.FindGuidForMaterial(mr.material) : null;
                if (matGuid != null)
                {
                    // 임포트된 머티리얼은 GUID만 저장 — 값은 에셋에서 읽음
                    fields["materialGuid"] = matGuid;
                }
                else if (mr.material != null)
                {
                    // 인라인 머티리얼 (GUID 없음): 값 직접 저장
                    fields["color"] = ColorToArray(mr.material.color);
                    fields["metallic"] = (double)mr.material.metallic;
                    fields["roughness"] = (double)mr.material.roughness;
                    if (mr.material.textureScale.x != 1f || mr.material.textureScale.y != 1f)
                    {
                        fields["textureScaleX"] = (double)mr.material.textureScale.x;
                        fields["textureScaleY"] = (double)mr.material.textureScale.y;
                    }
                    if (mr.material.textureOffset.x != 0f || mr.material.textureOffset.y != 0f)
                    {
                        fields["textureOffsetX"] = (double)mr.material.textureOffset.x;
                        fields["textureOffsetY"] = (double)mr.material.textureOffset.y;
                    }
                }
                return new TomlTable
                {
                    ["type"] = "MeshRenderer",
                    ["fields"] = fields,
                };
            }

            if (comp is PostProcessVolume ppv)
            {
                var fields = new TomlTable
                {
                    ["blendDistance"] = (double)ppv.blendDistance,
                    ["weight"] = (double)ppv.weight,
                };
                if (!string.IsNullOrEmpty(ppv.profileGuid))
                    fields["profileGuid"] = ppv.profileGuid;
                return new TomlTable { ["type"] = "PostProcessVolume", ["fields"] = fields };
            }

            // 나머지 컴포넌트: 범용 리플렉션 직렬화
            return SerializeComponentGeneric(comp);
        }

        private static string? InferPrimitiveType(Mesh? mesh)
        {
            if (mesh == null) return null;
            var name = mesh.name.ToLowerInvariant();
            if (name.Contains("cube")) return "Cube";
            if (name.Contains("sphere")) return "Sphere";
            if (name.Contains("capsule")) return "Capsule";
            if (name.Contains("cylinder")) return "Cylinder";
            if (name.Contains("plane")) return "Plane";
            if (name.Contains("quad")) return "Quad";

            if (mesh.vertices.Length == 24 && mesh.indices.Length == 36) return "Cube";
            if (mesh.vertices.Length == 4 && mesh.indices.Length == 6) return "Quad";
            return null;
        }

        // ================================================================
        // Prefab Save / Load
        // ================================================================

        /// <summary>
        /// GameObject 계층을 .prefab TOML 파일로 저장.
        /// 루트의 localPosition은 (0,0,0)으로 리셋하여 저장.
        /// </summary>
        public static void SavePrefab(GameObject root, string path)
        {
            var tomlStr = BuildPrefabToml(root);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, tomlStr);
            EditorDebug.Log($"[Prefab] Saved: {path}");
        }

        /// <summary>Prefab TOML 문자열을 생성 (디스크 I/O 없음).</summary>
        public static string BuildPrefabToml(GameObject root)
        {
            var toml = new TomlTable();
            toml["prefab"] = new TomlTable
            {
                ["version"] = (long)1,
                ["rootName"] = root.name,
            };

            toml["gameObjects"] = SerializeGameObjectHierarchy(root);
            return Toml.FromModel(toml);
        }

        /// <summary>
        /// .prefab TOML 파일에서 GameObject 계층을 로드.
        /// 씬을 클리어하지 않으며 SceneManager에 등록된 독립 GO 목록을 반환.
        /// 반환된 GO는 _isEditorInternal = true (프리팹 템플릿).
        /// </summary>
        public static List<GameObject> LoadPrefabGameObjects(string filePath)
        {
            if (!File.Exists(filePath))
            {
                EditorDebug.LogError($"[Prefab] File not found: {filePath}");
                return new List<GameObject>();
            }

            var tomlStr = File.ReadAllText(filePath);
            return LoadPrefabGameObjectsFromString(tomlStr);
        }

        /// <summary>TOML 문자열에서 프리팹 GO 계층을 로드.</summary>
        public static List<GameObject> LoadPrefabGameObjectsFromString(string tomlStr)
        {
            TomlTable root;
            try { root = Toml.ToModel(tomlStr); }
            catch (Exception ex)
            {
                EditorDebug.LogError($"[Prefab] TOML parse error: {ex.Message}");
                return new List<GameObject>();
            }

            if (!root.TryGetValue("gameObjects", out var gosVal) || gosVal is not TomlTableArray goArray)
                return new List<GameObject>();

            var created = DeserializeGameObjectHierarchy(goArray);

            // 프리팹 템플릿으로 마킹 (씬에서 숨김)
            // 중첩 PrefabInstance에서 Instantiate로 생성된 자식 GO까지 재귀 마킹
            foreach (var go in created)
                SetEditorInternalRecursive(go, true);

            // short name 경고가 수집되었으면 하나의 Alert로 표시
            FlushShortNameWarnings();

            return created;
        }

        // ================================================================
        // Prefab Variant: save / load
        // ================================================================

        /// <summary>
        /// 프리팹 TOML 문자열에서 basePrefabGuid를 읽어 반환.
        /// Variant가 아니면 null.
        /// </summary>
        public static string? GetBasePrefabGuid(string tomlStr)
        {
            try
            {
                var root = Toml.ToModel(tomlStr);
                if (root.TryGetValue("prefab", out var pVal) && pVal is TomlTable prefabTable)
                {
                    if (prefabTable.TryGetValue("basePrefabGuid", out var bgVal) && bgVal is string bg && !string.IsNullOrEmpty(bg))
                        return bg;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Variant 프리팹을 저장. 현재 GO 계층과 부모(base) 프리팹을 비교하여 오버라이드만 기록.
        /// </summary>
        public static void SaveVariantPrefab(GameObject root, string basePrefabGuid, string variantPath)
        {
            var tomlStr = BuildVariantPrefabToml(root, basePrefabGuid);
            var dir = Path.GetDirectoryName(variantPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(variantPath, tomlStr);
            EditorDebug.Log($"[Prefab] Saved variant: {variantPath}");
        }

        /// <summary>
        /// Variant 프리팹 TOML을 생성. 현재 GO 계층과 base 프리팹을 비교하여 overrides를 추출.
        /// </summary>
        public static string BuildVariantPrefabToml(GameObject root, string basePrefabGuid)
        {
            var toml = new TomlTable();
            toml["prefab"] = new TomlTable
            {
                ["version"] = (long)1,
                ["rootName"] = root.name,
                ["basePrefabGuid"] = basePrefabGuid,
            };

            // Base 프리팹 로드하여 비교
            var db = Resources.GetAssetDatabase();
            var baseTemplate = db?.LoadByGuid<GameObject>(basePrefabGuid);

            if (baseTemplate != null)
            {
                var overrides = CalculateOverrides(root, baseTemplate);
                if (overrides.Count > 0)
                    toml["overrides"] = overrides;
            }
            else
            {
                // Base를 로드할 수 없으면 전체 저장 (폴백)
                toml["gameObjects"] = SerializeGameObjectHierarchy(root);
            }

            var raw = Toml.FromModel(toml);

            // Tomlyn 0.20은 [[overrides]] 배열 내 테이블 값을 [overrides.value] 형태로 출력하는데,
            // 후속 override 항목의 value 키와 충돌하여 TOML 파싱 에러를 발생시킴.
            // 인라인 테이블 {key = val} 형태로 후처리하여 유효한 TOML을 보장.
            return ConvertSectionTablesToInline(raw);
        }

        /// <summary>
        /// Tomlyn の FromModel() が [[array]] 内のテーブル値を [array.key] 形式で出力する問題を後処理。
        /// [overrides.value] 같은 섹션 테이블을 인라인 테이블 {key = val} 형식으로 변환하여 유효한 TOML을 생성.
        /// </summary>
        internal static string ConvertSectionTablesToInline(string toml)
        {
            var lines = toml.Split('\n');
            var sb = new System.Text.StringBuilder();
            int i = 0;
            while (i < lines.Length)
            {
                var trimmed = lines[i].TrimEnd('\r');

                // [overrides.value] 패턴 감지 (대소문자 무시하지 않음)
                if (trimmed.StartsWith("[") && !trimmed.StartsWith("[[") && trimmed.Contains(".value]"))
                {
                    // 하위 key = val 쌍 수집
                    var kvPairs = new List<string>();
                    i++;
                    while (i < lines.Length)
                    {
                        var nextTrimmed = lines[i].TrimEnd('\r');
                        if (string.IsNullOrWhiteSpace(nextTrimmed)
                            || nextTrimmed.StartsWith("[[")
                            || nextTrimmed.StartsWith("["))
                            break;
                        kvPairs.Add(nextTrimmed.Trim());
                        i++;
                    }
                    sb.Append("value = {");
                    sb.Append(string.Join(", ", kvPairs));
                    sb.AppendLine("}");
                }
                else
                {
                    sb.AppendLine(trimmed);
                    i++;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 현재 GO 계층과 base 템플릿을 필드 단위로 비교하여 다른 점만 [[overrides]]로 추출.
        /// path 형식: "goIndex/componentType/fieldName"
        /// </summary>
        internal static TomlTableArray CalculateOverrides(GameObject current, GameObject baseTemplate)
        {
            var overrides = new TomlTableArray();

            var currentGOs = new List<GameObject>();
            CollectHierarchy(current, currentGOs);

            var baseGOs = new List<GameObject>();
            CollectHierarchy(baseTemplate, baseGOs);

            for (int i = 0; i < currentGOs.Count && i < baseGOs.Count; i++)
            {
                var curGo = currentGOs[i];
                var baseGo = baseGOs[i];

                // 이름 변경
                if (curGo.name != baseGo.name)
                {
                    overrides.Add(new TomlTable
                    {
                        ["path"] = $"{i}/_name",
                        ["value"] = curGo.name,
                    });
                }

                // activeSelf 변경
                if (curGo.activeSelf != baseGo.activeSelf)
                {
                    overrides.Add(new TomlTable
                    {
                        ["path"] = $"{i}/_activeSelf",
                        ["value"] = curGo.activeSelf,
                    });
                }

                // Transform 변경 (localScale — position/rotation은 인스턴스에서 설정)
                if (i > 0) // 루트가 아닌 자식들의 Transform
                {
                    if (!VecApproxEqual(curGo.transform.localPosition, baseGo.transform.localPosition))
                    {
                        overrides.Add(new TomlTable
                        {
                            ["path"] = $"{i}/Transform/localPosition",
                            ["value"] = Vec3ToArray(curGo.transform.localPosition),
                        });
                    }
                    if (!QuatApproxEqual(curGo.transform.localRotation, baseGo.transform.localRotation))
                    {
                        overrides.Add(new TomlTable
                        {
                            ["path"] = $"{i}/Transform/localRotation",
                            ["value"] = QuatToArray(curGo.transform.localRotation),
                        });
                    }
                }
                if (!VecApproxEqual(curGo.transform.localScale, baseGo.transform.localScale))
                {
                    overrides.Add(new TomlTable
                    {
                        ["path"] = $"{i}/Transform/localScale",
                        ["value"] = Vec3ToArray(curGo.transform.localScale),
                    });
                }

                // 컴포넌트 비교
                CompareComponents(curGo, baseGo, i, overrides);
            }

            return overrides;
        }

        private static void CompareComponents(GameObject curGo, GameObject baseGo, int goIndex, TomlTableArray overrides)
        {
            var curComps = curGo.InternalComponents;
            var baseComps = baseGo.InternalComponents;

            // 타입별로 매핑 (같은 타입의 컴포넌트가 여러 개인 경우 순서로 매핑)
            var curByType = new Dictionary<string, List<Component>>();
            var baseByType = new Dictionary<string, List<Component>>();

            foreach (var c in curComps)
            {
                if (c is Transform || c is PrefabInstance) continue;
                var tn = c.GetType().Name;
                if (!curByType.ContainsKey(tn)) curByType[tn] = new List<Component>();
                curByType[tn].Add(c);
            }
            foreach (var c in baseComps)
            {
                if (c is Transform || c is PrefabInstance) continue;
                var tn = c.GetType().Name;
                if (!baseByType.ContainsKey(tn)) baseByType[tn] = new List<Component>();
                baseByType[tn].Add(c);
            }

            // 같은 타입의 컴포넌트끼리 필드 비교
            foreach (var kvp in curByType)
            {
                var typeName = kvp.Key;
                var curList = kvp.Value;

                if (!baseByType.TryGetValue(typeName, out var baseList))
                    continue; // 새로 추가된 컴포넌트 — 별도 처리 필요 (addedComponents)

                int matchCount = Math.Min(curList.Count, baseList.Count);
                for (int ci = 0; ci < matchCount; ci++)
                {
                    CompareComponentFields(curList[ci], baseList[ci], goIndex, typeName, overrides);
                }
            }
        }

        private static void CompareComponentFields(Component curComp, Component baseComp, int goIndex, string typeName, TomlTableArray overrides)
        {
            var type = curComp.GetType();

            // Fields
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!IsSerializableField(field)) continue;
                var curVal = field.GetValue(curComp);
                var baseVal = field.GetValue(baseComp);

                if (!ValuesEqual(curVal, baseVal, field.FieldType))
                {
                    var tomlVal = MemberToToml(curVal, field.FieldType);
                    if (tomlVal != null)
                    {
                        overrides.Add(new TomlTable
                        {
                            ["path"] = $"{goIndex}/{typeName}/{field.Name}",
                            ["value"] = tomlVal,
                        });
                    }
                }
            }

            // Properties
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!IsSerializableProperty(prop)) continue;

                object? curVal, baseVal;
                try
                {
                    curVal = prop.GetValue(curComp);
                    baseVal = prop.GetValue(baseComp);
                }
                catch { continue; }

                if (!ValuesEqual(curVal, baseVal, prop.PropertyType))
                {
                    var tomlVal = MemberToToml(curVal, prop.PropertyType);
                    if (tomlVal != null)
                    {
                        overrides.Add(new TomlTable
                        {
                            ["path"] = $"{goIndex}/{typeName}/{prop.Name}",
                            ["value"] = tomlVal,
                        });
                    }
                }
            }
        }

        /// <summary>값 타입 + 에셋 참조를 모두 처리하는 TOML 변환.</summary>
        private static object? MemberToToml(object? value, Type type)
        {
            if (value == null) return null;
            if (AssetReferenceTypes.Contains(type))
                return SerializeAssetRef(value, type);
            return ValueToToml(value, type);
        }

        /// <summary>Variant 오버라이드를 로드된 base GO 계층에 적용.</summary>
        internal static void ApplyOverrides(List<GameObject> gameObjects, TomlTableArray overridesArray)
        {
            foreach (TomlTable ov in overridesArray)
            {
                if (!ov.TryGetValue("path", out var pathVal) || pathVal is not string path)
                    continue;
                if (!ov.TryGetValue("value", out var value) || value == null)
                    continue;

                var parts = path.Split('/');
                if (parts.Length < 2) continue;

                if (!int.TryParse(parts[0], out int goIndex) || goIndex < 0 || goIndex >= gameObjects.Count)
                    continue;

                var go = gameObjects[goIndex];

                // 특수 경로: _name, _activeSelf
                if (parts[1] == "_name" && value is string nameVal)
                {
                    go.name = nameVal;
                    continue;
                }
                if (parts[1] == "_activeSelf" && value is bool activeVal)
                {
                    go.SetActive(activeVal);
                    continue;
                }

                // "goIndex/CompType/fieldName"
                if (parts.Length < 3) continue;
                var compType = parts[1];
                var fieldName = parts[2];

                if (compType == "Transform")
                {
                    ApplyTransformOverride(go.transform, fieldName, value);
                    continue;
                }

                // 일반 컴포넌트
                foreach (var comp in go.InternalComponents)
                {
                    var compFullName = comp.GetType().FullName ?? comp.GetType().Name;
                    if (compFullName != compType && comp.GetType().Name != compType) continue;

                    var field = comp.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null && IsSerializableField(field))
                    {
                        SetMemberValue(comp, field.FieldType, value, v => field.SetValue(comp, v));
                        break;
                    }

                    var prop = comp.GetType().GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null && IsSerializableProperty(prop))
                    {
                        SetMemberValue(comp, prop.PropertyType, value, v => prop.SetValue(comp, v));
                        break;
                    }
                }
            }
        }

        private static void ApplyTransformOverride(Transform t, string fieldName, object value)
        {
            switch (fieldName)
            {
                case "localPosition":
                    t.localPosition = ArrayToVec3Direct(value);
                    break;
                case "localRotation":
                    t.localRotation = ArrayToQuatDirect(value);
                    break;
                case "localScale":
                    t.localScale = ArrayToVec3Direct(value);
                    break;
            }
        }

        private static bool VecApproxEqual(Vector3 a, Vector3 b, float epsilon = 1e-5f)
        {
            return MathF.Abs(a.x - b.x) < epsilon
                && MathF.Abs(a.y - b.y) < epsilon
                && MathF.Abs(a.z - b.z) < epsilon;
        }

        private static bool QuatApproxEqual(Quaternion a, Quaternion b, float epsilon = 1e-5f)
        {
            return MathF.Abs(a.x - b.x) < epsilon
                && MathF.Abs(a.y - b.y) < epsilon
                && MathF.Abs(a.z - b.z) < epsilon
                && MathF.Abs(a.w - b.w) < epsilon;
        }

        private static bool ValuesEqual(object? a, object? b, Type type)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (type == typeof(float)) return MathF.Abs((float)a - (float)b) < 1e-5f;
            if (type == typeof(Vector3)) return VecApproxEqual((Vector3)a, (Vector3)b);
            if (type == typeof(Quaternion)) return QuatApproxEqual((Quaternion)a, (Quaternion)b);
            if (type == typeof(Color))
            {
                var ca = (Color)a; var cb = (Color)b;
                return MathF.Abs(ca.r - cb.r) < 1e-5f && MathF.Abs(ca.g - cb.g) < 1e-5f
                    && MathF.Abs(ca.b - cb.b) < 1e-5f && MathF.Abs(ca.a - cb.a) < 1e-5f;
            }
            return a.Equals(b);
        }

        // ================================================================
        // Shared: GO hierarchy serialize / deserialize
        // ================================================================

        /// <summary>루트 GO와 자식 계층 전체를 TomlTableArray로 직렬화.</summary>
        internal static TomlTableArray SerializeGameObjectHierarchy(GameObject root)
        {
            // 직렬화 대상 수집 (루트 + 자식 재귀, BFS)
            // 중첩 PrefabInstance의 자식은 제외
            var allGOs = new List<GameObject>();
            var nestedPrefabIds = new HashSet<int>();
            CollectHierarchyWithPrefabs(root, allGOs, nestedPrefabIds, isRoot: true);

            var idToIndex = new Dictionary<int, int>();
            for (int i = 0; i < allGOs.Count; i++)
                idToIndex[allGOs[i].GetInstanceID()] = i;

            var goArray = new TomlTableArray();
            for (int i = 0; i < allGOs.Count; i++)
            {
                var go = allGOs[i];
                var goTable = new TomlTable
                {
                    ["name"] = go.name,
                    ["guid"] = go.guid,
                    ["activeSelf"] = go.activeSelf,
                };

                var t = go.transform;
                int parentIndex = -1;
                if (t.parent != null && idToIndex.TryGetValue(t.parent.gameObject.GetInstanceID(), out var pi))
                    parentIndex = pi;

                // 루트(i==0)의 localPosition은 (0,0,0)으로 리셋
                var localPos = i == 0 ? Vector3.zero : t.localPosition;

                goTable["transform"] = new TomlTable
                {
                    ["localPosition"] = Vec3ToArray(localPos),
                    ["localRotation"] = QuatToArray(t.localRotation),
                    ["localScale"] = Vec3ToArray(t.localScale),
                    ["parentIndex"] = (long)parentIndex,
                };

                // 중첩 PrefabInstance — GUID + Transform만 저장, 자식/컴포넌트 건너뛰기
                var prefabInst = go.GetComponent<PrefabInstance>();
                if (prefabInst != null && i > 0) // i==0은 프리팹 루트 자체이므로 제외
                {
                    goTable["prefabInstance"] = new TomlTable
                    {
                        ["prefabGuid"] = prefabInst.prefabGuid ?? "",
                    };
                    goArray.Add(goTable);
                    continue;
                }

                var compArray = new TomlTableArray();
                foreach (var comp in go.InternalComponents)
                {
                    if (comp is Transform) continue;
                    if (comp is PrefabInstance) continue;
                    var compTable = SerializeComponent(comp);
                    if (compTable != null)
                        compArray.Add(compTable);
                }

                if (compArray.Count > 0)
                    goTable["components"] = compArray;

                goArray.Add(goTable);
            }

            return goArray;
        }

        /// <summary>TomlTableArray에서 GO 계층을 역직렬화. SceneManager에 등록됨.</summary>
        internal static List<GameObject> DeserializeGameObjectHierarchy(TomlTableArray goArray)
        {
            var createdGOs = new List<GameObject>();
            var parentIndices = new List<int>();

            foreach (TomlTable goTable in goArray)
            {
                var goName = goTable.TryGetValue("name", out var n) ? n?.ToString() ?? "GameObject" : "GameObject";
                var activeSelf = goTable.TryGetValue("activeSelf", out var a) && a is bool ab && ab;

                // 중첩 PrefabInstance 처리
                if (goTable.TryGetValue("prefabInstance", out var piObj) && piObj is TomlTable piTable)
                {
                    var prefabGuid = piTable.TryGetValue("prefabGuid", out var pgVal) ? pgVal?.ToString() ?? "" : "";
                    GameObject? prefabGo = null;

                    if (!string.IsNullOrEmpty(prefabGuid))
                    {
                        var db = Resources.GetAssetDatabase();
                        var template = db?.LoadByGuid<GameObject>(prefabGuid);
                        if (template != null)
                        {
                            prefabGo = RoseEngine.Object.Instantiate(template);
                            prefabGo.name = goName;
                            var inst = prefabGo.AddComponent<PrefabInstance>();
                            inst.prefabGuid = prefabGuid;
                        }
                    }

                    if (prefabGo == null)
                    {
                        prefabGo = new GameObject(goName);
                        EditorDebug.LogWarning($"[Prefab] Nested PrefabInstance '{goName}': fallback to empty GO (guid={goTable})");
                    }

                    if (goTable.TryGetValue("transform", out var tValP) && tValP is TomlTable tTableP)
                    {
                        prefabGo.transform.localPosition = ArrayToVec3(tTableP, "localPosition");
                        prefabGo.transform.localRotation = ArrayToQuat(tTableP, "localRotation");
                        prefabGo.transform.localScale = ArrayToVec3(tTableP, "localScale", Vector3.one);

                        int pIdx = -1;
                        if (tTableP.TryGetValue("parentIndex", out var piValP) && piValP is long piLongP)
                            pIdx = (int)piLongP;
                        parentIndices.Add(pIdx);
                    }
                    else
                    {
                        parentIndices.Add(-1);
                    }

                    // GO guid 복원
                    if (goTable.TryGetValue("guid", out var prefabGoGuid) && prefabGoGuid is string pgStr && pgStr.Length > 0)
                        prefabGo.guid = pgStr;

                    prefabGo.SetActive(activeSelf);
                    createdGOs.Add(prefabGo);
                    continue;
                }

                var go = new GameObject(goName);

                // GO guid 복원
                if (goTable.TryGetValue("guid", out var goGuidVal) && goGuidVal is string goGuidStr && goGuidStr.Length > 0)
                    go.guid = goGuidStr;

                if (goTable.TryGetValue("transform", out var tVal) && tVal is TomlTable tTable)
                {
                    go.transform.localPosition = ArrayToVec3(tTable, "localPosition");
                    go.transform.localRotation = ArrayToQuat(tTable, "localRotation");
                    go.transform.localScale = ArrayToVec3(tTable, "localScale", Vector3.one);

                    int pIdx = -1;
                    if (tTable.TryGetValue("parentIndex", out var piVal) && piVal is long piLong)
                        pIdx = (int)piLong;
                    parentIndices.Add(pIdx);
                }
                else
                {
                    parentIndices.Add(-1);
                }

                // RectTransform 복원
                if (goTable.TryGetValue("rectTransform", out var rtVal) && rtVal is TomlTable rtTable)
                {
                    var rt = go.AddComponent<RectTransform>();
                    if (rtTable.TryGetValue("anchorMin", out var v)) rt.anchorMin = ArrayToVec2(v);
                    if (rtTable.TryGetValue("anchorMax", out v)) rt.anchorMax = ArrayToVec2(v);
                    if (rtTable.TryGetValue("anchoredPosition", out v)) rt.anchoredPosition = ArrayToVec2(v);
                    if (rtTable.TryGetValue("sizeDelta", out v)) rt.sizeDelta = ArrayToVec2(v);
                    if (rtTable.TryGetValue("pivot", out v)) rt.pivot = ArrayToVec2(v);
                }

                if (goTable.TryGetValue("components", out var compsVal) && compsVal is TomlTableArray compsArray)
                {
                    foreach (TomlTable compTable in compsArray)
                        DeserializeComponent(go, compTable);
                }

                go.SetActive(activeSelf);
                createdGOs.Add(go);
            }

            // parent 설정
            for (int i = 0; i < createdGOs.Count; i++)
            {
                var pIdx = parentIndices[i];
                if (pIdx >= 0 && pIdx < createdGOs.Count)
                    createdGOs[i].transform.SetParent(createdGOs[pIdx].transform, false);
            }

            return createdGOs;
        }

        private static void CollectHierarchy(GameObject root, List<GameObject> result)
            => PrefabUtility.CollectHierarchy(root, result);

        /// <summary>GO와 모든 자식의 _isEditorInternal 플래그를 재귀적으로 설정.</summary>
        internal static void SetEditorInternalRecursive(GameObject go, bool value)
        {
            go._isEditorInternal = value;
            for (int i = 0; i < go.transform.childCount; i++)
                SetEditorInternalRecursive(go.transform.GetChild(i).gameObject, value);
        }

        /// <summary>
        /// 프리팹 직렬화용 — 중첩 PrefabInstance의 자식은 수집하지 않음.
        /// </summary>
        private static void CollectHierarchyWithPrefabs(GameObject go, List<GameObject> result,
            HashSet<int> nestedPrefabIds, bool isRoot)
        {
            if (go._isDestroyed) return;
            result.Add(go);

            // 중첩 PrefabInstance이면 자식 수집 건너뛰기 (루트 제외)
            if (!isRoot && go.GetComponent<PrefabInstance>() != null)
            {
                nestedPrefabIds.Add(go.GetInstanceID());
                return;
            }

            for (int i = 0; i < go.transform.childCount; i++)
                CollectHierarchyWithPrefabs(go.transform.GetChild(i).gameObject, result, nestedPrefabIds, false);
        }

        // ================================================================
        // Load
        // ================================================================

        /// <summary>.scene 파일에서 씬을 로드. SceneManager.Clear() 후 역직렬화.</summary>
        public static void Load(string filePath)
        {
            if (!File.Exists(filePath))
            {
                EditorDebug.LogError($"[Scene] File not found: {filePath}");
                return;
            }

            var tomlStr = File.ReadAllText(filePath);
            TomlTable root;
            try
            {
                root = Toml.ToModel(tomlStr);
            }
            catch (Exception ex)
            {
                EditorDebug.LogError($"[Scene] TOML parse error: {ex.Message}");
                return;
            }

            LoadFromTable(root, filePath);
            EditorDebug.Log($"[Scene] Loaded: {filePath}");
        }

        /// <summary>TOML 문자열에서 씬을 역직렬화 (디스크 I/O 없음).</summary>
        public static void LoadFromString(string tomlStr)
        {
            TomlTable root;
            try
            {
                root = Toml.ToModel(tomlStr);
            }
            catch (Exception ex)
            {
                EditorDebug.LogError($"[Scene] TOML parse error: {ex.Message}");
                return;
            }

            LoadFromTable(root, null);
        }

        private static void LoadFromTable(TomlTable root, string? filePath)
        {
            SceneManager.Clear();

            var sceneName = root.TryGetValue("name", out var nameVal) ? nameVal?.ToString() ?? "Untitled" : "Untitled";
            var scene = new Scene
            {
                path = filePath != null ? Path.GetFullPath(filePath) : null,
                name = sceneName,
                isDirty = false,
            };
            SceneManager.SetActiveScene(scene);

            if (!root.TryGetValue("gameObjects", out var gosVal) || gosVal is not TomlTableArray goArray)
                return;

            // GO 생성 + parent 설정을 공용 메서드에 위임 (코드 중복 제거)
            _pendingSceneRefs.Clear();
            DeserializeGameObjectHierarchy(goArray);
            ResolveSceneReferences();

            // Scene Environment (Phase 38-B) + 하위호환 (구 [renderSettings])
            ResetEnvironmentDefaults();
            if (root.TryGetValue("sceneEnvironment", out var envVal) && envVal is TomlTable envTable)
            {
                LoadSceneEnvironment(envTable);
            }
            else if (root.TryGetValue("renderSettings", out var rsVal) && rsVal is TomlTable rsTable)
            {
                // 구 씬 파일 하위호환: [renderSettings] → skybox만 저장돼있던 형식
                LoadSceneEnvironment(rsTable);
            }

            // EditorState — hierarchy expand/collapse + Scene View 카메라
            ImGuiHierarchyPanel.PendingExpandedGuids = null;
            EditorCamera.PendingState = null;

            if (root.TryGetValue("editorState", out var esVal) && esVal is TomlTable esTable)
            {
                if (esTable.TryGetValue("expandedNodes", out var enVal) && enVal is TomlArray enArray)
                {
                    var guids = new HashSet<string>();
                    foreach (var item in enArray)
                        if (item is string s && s.Length > 0) guids.Add(s);
                    ImGuiHierarchyPanel.PendingExpandedGuids = guids.Count > 0 ? guids : null;
                }

                // Scene View 카메라 복원
                if (esTable.TryGetValue("sceneViewCamera", out var camVal) && camVal is TomlTable camTable)
                {
                    var pos = camTable.TryGetValue("position", out var pv) ? ArrayToVec3Direct(pv) : new Vector3(0, 5, -10);
                    var rot = camTable.TryGetValue("rotation", out var rv) && rv is TomlArray ra && ra.Count >= 4
                        ? new Quaternion(ToFloat(ra[0]), ToFloat(ra[1]), ToFloat(ra[2]), ToFloat(ra[3]))
                        : Quaternion.identity;
                    var pivot = camTable.TryGetValue("pivot", out var pvt) ? ArrayToVec3Direct(pvt) : Vector3.zero;
                    EditorCamera.PendingState = (pos, rot, pivot);
                }
            }

            // short name 경고가 수집되었으면 하나의 Alert로 표시
            FlushShortNameWarnings();
        }

        internal static void DeserializeComponent(GameObject go, TomlTable compTable)
        {
            if (!compTable.TryGetValue("type", out var typeVal))
                return;
            var typeName = typeVal?.ToString() ?? "";

            compTable.TryGetValue("fields", out var fieldsVal);
            var fields = fieldsVal as TomlTable ?? new TomlTable();

            switch (typeName)
            {
                case "Camera":
                    var cam = go.AddComponent<Camera>();
                    if (fields.TryGetValue("fieldOfView", out var fovVal))
                        cam.fieldOfView = ToFloat(fovVal);
                    if (fields.TryGetValue("nearClipPlane", out var nearVal))
                        cam.nearClipPlane = ToFloat(nearVal);
                    if (fields.TryGetValue("farClipPlane", out var farVal))
                        cam.farClipPlane = ToFloat(farVal);
                    if (fields.TryGetValue("clearFlags", out var cfVal) && cfVal is string cfStr)
                        if (Enum.TryParse<CameraClearFlags>(cfStr, out var cf))
                            cam.clearFlags = cf;
                    if (fields.TryGetValue("backgroundColor", out var bgVal))
                        cam.backgroundColor = ArrayToColor(bgVal);
                    break;

                case "Light":
                    // 레거시 호환: 이전 포맷의 "lightType" → "type" 매핑
                    if (fields.ContainsKey("lightType") && !fields.ContainsKey("type"))
                        fields["type"] = fields["lightType"];
                    DeserializeComponentGeneric(go, typeName, fields);
                    break;

                case "MeshFilter":
                    if (fields.TryGetValue("primitiveType", out var ptVal) && ptVal is string ptStr)
                    {
                        if (Enum.TryParse<PrimitiveType>(ptStr, out var pt))
                        {
                            var mf = go.AddComponent<MeshFilter>();
                            mf.mesh = pt switch
                            {
                                PrimitiveType.Cube => PrimitiveGenerator.CreateCube(),
                                PrimitiveType.Sphere => PrimitiveGenerator.CreateSphere(),
                                PrimitiveType.Capsule => PrimitiveGenerator.CreateCapsule(),
                                PrimitiveType.Cylinder => PrimitiveGenerator.CreateCylinder(),
                                PrimitiveType.Plane => PrimitiveGenerator.CreatePlane(),
                                PrimitiveType.Quad => PrimitiveGenerator.CreateQuad(),
                                _ => PrimitiveGenerator.CreateCube(),
                            };
                            EditorDebug.Log($"[Scene:Load] MeshFilter on '{go.name}': primitive={ptStr}");
                        }
                    }
                    else if (fields.TryGetValue("assetGuid", out var guidVal) && guidVal is string meshGuid)
                    {
                        var db = Resources.GetAssetDatabase();
                        var guidPath = db?.GetPathFromGuid(meshGuid);
                        EditorDebug.Log($"[Scene:Load] MeshFilter on '{go.name}': assetGuid={meshGuid}, resolvedPath={guidPath ?? "NULL"}");
                        var mesh = db?.LoadByGuid<Mesh>(meshGuid);
                        if (mesh != null)
                        {
                            var mf = go.AddComponent<MeshFilter>();
                            mf.mesh = mesh;
                            EditorDebug.Log($"[Scene:Load] MeshFilter on '{go.name}': loaded mesh '{mesh.name}' ({mesh.vertices.Length} verts)");
                        }
                        else
                        {
                            EditorDebug.LogWarning($"[Scene:Load] MeshFilter on '{go.name}': FAILED to load mesh for guid={meshGuid}, path={guidPath ?? "NULL"}");
                        }
                    }
                    else
                    {
                        EditorDebug.LogWarning($"[Scene:Load] MeshFilter on '{go.name}': no primitiveType or assetGuid in fields");
                    }
                    break;

                case "MipMeshFilter":
                    if (fields.TryGetValue("meshGuid", out var mmGuidVal) && mmGuidVal is string mmMeshGuid)
                    {
                        var db = Resources.GetAssetDatabase();
                        var meshPath = db?.GetPathFromGuid(mmMeshGuid);
                        EditorDebug.Log($"[Scene:Load] MipMeshFilter on '{go.name}': meshGuid={mmMeshGuid}, resolvedPath={meshPath ?? "NULL"}");
                        if (meshPath != null && SubAssetPath.TryParse(meshPath, out var filePath, out _, out var idx) && idx >= 0)
                        {
                            var mipPath = SubAssetPath.Build(filePath, "MipMesh", idx);
                            var mipMesh = db?.Load<MipMesh>(mipPath);
                            if (mipMesh != null)
                            {
                                var mmf = go.AddComponent<MipMeshFilter>();
                                mmf.mipMesh = mipMesh;
                                if (fields.TryGetValue("mipBias", out var mbVal))
                                    mmf.mipBias = ToFloat(mbVal);
                                if (fields.TryGetValue("lodScale", out var lsVal))
                                    mmf.lodScale = ToFloat(lsVal);
                                EditorDebug.Log($"[Scene:Load] MipMeshFilter on '{go.name}': loaded MipMesh from '{mipPath}' ({mipMesh.LodCount} LODs)");
                            }
                            else
                            {
                                EditorDebug.LogWarning($"[Scene:Load] MipMeshFilter on '{go.name}': FAILED to load MipMesh from '{mipPath}'");
                            }
                        }
                        else
                        {
                            EditorDebug.LogWarning($"[Scene:Load] MipMeshFilter on '{go.name}': FAILED to resolve meshGuid '{mmMeshGuid}', meshPath={meshPath ?? "NULL"}");
                        }
                    }
                    else
                    {
                        EditorDebug.LogWarning($"[Scene:Load] MipMeshFilter on '{go.name}': no meshGuid field");
                    }
                    break;

                case "MeshRenderer":
                    var mr = go.AddComponent<MeshRenderer>();
                    if (fields.TryGetValue("enabled", out var mrEnabledVal) && mrEnabledVal is bool mrEnabled)
                        mr.enabled = mrEnabled;
                    Material? mat = null;

                    // GUID 기반 머터리얼 로드
                    if (fields.TryGetValue("materialGuid", out var matGuidVal) && matGuidVal is string materialGuid)
                    {
                        var db = Resources.GetAssetDatabase();
                        mat = db?.LoadByGuid<Material>(materialGuid);
                    }

                    if (mat == null)
                    {
                        // 인라인 머티리얼 (GUID 없거나 로드 실패): 씬 파일 값 사용
                        mat = new Material();
                        if (fields.TryGetValue("color", out var mcVal))
                            mat.color = ArrayToColor(mcVal);
                        if (fields.TryGetValue("metallic", out var mmVal))
                            mat.metallic = ToFloat(mmVal);
                        if (fields.TryGetValue("roughness", out var mrVal2))
                            mat.roughness = ToFloat(mrVal2);
                        float sx = fields.TryGetValue("textureScaleX", out var sxv) ? ToFloat(sxv) : 1f;
                        float sy = fields.TryGetValue("textureScaleY", out var syv) ? ToFloat(syv) : 1f;
                        mat.textureScale = new RoseEngine.Vector2(sx, sy);
                        float ox = fields.TryGetValue("textureOffsetX", out var oxv) ? ToFloat(oxv) : 0f;
                        float oy = fields.TryGetValue("textureOffsetY", out var oyv) ? ToFloat(oyv) : 0f;
                        mat.textureOffset = new RoseEngine.Vector2(ox, oy);
                    }
                    mr.material = mat;
                    break;

                case "PostProcessVolume":
                {
                    var ppv = go.AddComponent<PostProcessVolume>();
                    if (fields.TryGetValue("blendDistance", out var bdVal))
                        ppv.blendDistance = ToFloat(bdVal);
                    if (fields.TryGetValue("weight", out var wVal))
                        ppv.weight = ToFloat(wVal);
                    if (fields.TryGetValue("profileGuid", out var pgVal) && pgVal is string pgStr && !string.IsNullOrEmpty(pgStr))
                    {
                        ppv.profileGuid = pgStr;
                        var db = Resources.GetAssetDatabase();
                        var profile = db?.LoadByGuid<PostProcessProfile>(pgStr);
                        if (profile != null)
                            ppv.profile = profile;
                        else
                            EditorDebug.LogWarning($"[Scene:Load] PostProcessVolume on '{go.name}': profile GUID '{pgStr}' not found");
                    }
                    break;
                }

                default:
                    // 나머지 컴포넌트: 범용 리플렉션 역직렬화
                    DeserializeComponentGeneric(go, typeName, fields);
                    break;
            }
        }

        // ================================================================
        // Generic Reflection-based Serialization
        // ================================================================

        private static Dictionary<string, Type>? _componentTypeCache;
        private static Dictionary<string, string>? _shortNameToFullName;
        private static readonly List<(string shortName, string fullName)> _shortNameWarnings = new();

        /// <summary>핫 리로드 후 컴포넌트 타입 캐시 무효화.</summary>
        internal static void InvalidateComponentTypeCache()
        {
            _componentTypeCache = null;
            _shortNameToFullName = null;
        }

        /// <summary>
        /// 수집된 short name 경고를 하나의 Alert 모달로 표시하고 리스트를 비운다.
        /// </summary>
        private static void FlushShortNameWarnings()
        {
            if (_shortNameWarnings.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("The following component types were resolved by short name:");
            sb.AppendLine();
            foreach (var (shortName, fullName) in _shortNameWarnings)
            {
                sb.AppendLine($"  '{shortName}'  ->  {fullName}");
            }
            sb.AppendLine();
            sb.Append("The scene will be updated to use full names on next save.");

            EditorModal.EnqueueAlert(sb.ToString());
            _shortNameWarnings.Clear();
        }

        private static readonly HashSet<string> SkipMemberNames = new()
        {
            "gameObject", "transform", "name", "tag",
            "velocity", "angularVelocity", // 런타임 물리 상태
        };

        private static readonly HashSet<Type> AssetReferenceTypes = new()
        {
            typeof(Mesh), typeof(Material), typeof(Texture2D), typeof(Font), typeof(Sprite), typeof(AnimationClip),
        };

        // ── 필터링 ──

        private static bool IsSerializableField(FieldInfo field)
        {
            if (field.IsLiteral || field.IsInitOnly) return false;
            if (SkipMemberNames.Contains(field.Name)) return false;
            if (field.GetCustomAttribute<HideInInspectorAttribute>() != null) return false;
            if (Attribute.IsDefined(field, typeof(NonSerializedAttribute))) return false;

            bool isPublic = field.IsPublic;
            bool hasSerialize = field.GetCustomAttribute<SerializeFieldAttribute>() != null;

            if (!isPublic && !hasSerialize) return false;
            if (!hasSerialize && field.Name.StartsWith("_")) return false;

            var ft = field.FieldType;
            return IsSupportedValueType(ft) || AssetReferenceTypes.Contains(ft)
                || IsSupportedCollectionType(ft) || IsSceneObjectReferenceType(ft)
                || IsNestedSerializableType(ft);
        }

        private static bool IsSerializableProperty(PropertyInfo prop)
        {
            if (!prop.CanRead || !prop.CanWrite) return false;
            if (prop.GetMethod?.IsStatic == true) return false;
            if (SkipMemberNames.Contains(prop.Name)) return false;
            if (prop.GetCustomAttribute<HideInInspectorAttribute>() != null) return false;
            if (prop.DeclaringType == typeof(Component) || prop.DeclaringType == typeof(RoseEngine.Object)) return false;

            var pt = prop.PropertyType;
            if (pt == typeof(MipMesh)) return false;
            return IsSupportedValueType(pt) || AssetReferenceTypes.Contains(pt)
                || IsSupportedCollectionType(pt) || IsSceneObjectReferenceType(pt)
                || IsNestedSerializableType(pt);
        }

        private static bool IsSceneObjectReferenceType(Type t)
        {
            return t == typeof(GameObject) || typeof(Component).IsAssignableFrom(t);
        }

        private static bool IsSupportedCollectionType(Type t)
        {
            if (t.IsArray)
            {
                var elem = t.GetElementType()!;
                return IsSupportedValueType(elem) || AssetReferenceTypes.Contains(elem);
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                return IsSupportedValueType(t.GetGenericArguments()[0]);
            return false;
        }

        private static bool IsSupportedValueType(Type t)
        {
            return t == typeof(float) || t == typeof(int) || t == typeof(bool)
                || t == typeof(string) || t == typeof(Vector2) || t == typeof(Vector3)
                || t == typeof(Vector4) || t == typeof(Quaternion) || t == typeof(Color)
                || t.IsEnum
                || t == typeof(long) || t == typeof(double) || t == typeof(byte);
        }

        private static bool IsNestedSerializableType(Type t)
        {
            if (t.IsPrimitive || t == typeof(string) || t.IsEnum) return false;
            if (IsSupportedValueType(t)) return false;
            if (AssetReferenceTypes.Contains(t)) return false;
            if (IsSceneObjectReferenceType(t)) return false;
            return Attribute.IsDefined(t, typeof(SerializableAttribute))
                && (t.IsValueType || t.IsClass);
        }

        // ── 범용 Serialize ──

        private static TomlTable SerializeComponentGeneric(Component comp)
        {
            var type = comp.GetType();
            var fieldsTable = new TomlTable();

            // Fields
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!IsSerializableField(field)) continue;
                var value = field.GetValue(comp);
                SerializeMember(fieldsTable, field.Name, field.FieldType, value);
            }

            // Properties (DeclaredOnly 없이 — 상속 프로퍼티 포함)
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!IsSerializableProperty(prop)) continue;
                if (fieldsTable.ContainsKey(prop.Name)) continue; // 필드와 중복 방지

                object? value;
                try { value = prop.GetValue(comp); }
                catch { continue; }

                SerializeMember(fieldsTable, prop.Name, prop.PropertyType, value);
            }

            return new TomlTable
            {
                ["type"] = type.FullName ?? type.Name,
                ["fields"] = fieldsTable,
            };
        }

        private static void SerializeMember(TomlTable table, string name, Type memberType, object? value)
        {
            if (value == null) return;

            if (AssetReferenceTypes.Contains(memberType))
            {
                var assetTable = SerializeAssetRef(value, memberType);
                if (assetTable != null)
                    table[name] = assetTable;
                return;
            }

            // 에셋 참조 배열 (Sprite[], AnimationClip[] 등)
            if (memberType.IsArray)
            {
                var elemType = memberType.GetElementType()!;
                if (AssetReferenceTypes.Contains(elemType) && value is Array arr)
                {
                    var tomlArr = new TomlTableArray();
                    foreach (var elem in arr)
                    {
                        if (elem == null) continue;
                        var refTable = SerializeAssetRef(elem, elemType);
                        if (refTable != null)
                            tomlArr.Add(refTable);
                    }
                    if (tomlArr.Count > 0)
                        table[name] = tomlArr;
                    return;
                }

                // 값 타입 배열 (float[], Vector3[] 등)
                if (IsSupportedValueType(elemType) && value is Array valArr)
                {
                    var tomlArr = new TomlArray();
                    foreach (var elem in valArr)
                    {
                        var tv = ValueToToml(elem, elemType);
                        if (tv != null) tomlArr.Add(tv);
                    }
                    table[name] = tomlArr;
                    return;
                }
            }

            // 값 타입 List<T> (List<float>, List<Vector3> 등)
            if (memberType.IsGenericType && memberType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elemType = memberType.GetGenericArguments()[0];
                if (IsSupportedValueType(elemType) && value is System.Collections.IList valList)
                {
                    var tomlArr = new TomlArray();
                    foreach (var elem in valList)
                    {
                        var tv = ValueToToml(elem, elemType);
                        if (tv != null) tomlArr.Add(tv);
                    }
                    table[name] = tomlArr;
                    return;
                }
            }

            // 중첩 직렬화 구조체
            if (IsNestedSerializableType(memberType))
            {
                var nestedTable = new TomlTable();
                foreach (var f in memberType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.IsLiteral || f.IsInitOnly) continue;
                    bool fPublic = f.IsPublic;
                    bool fSerialize = f.GetCustomAttribute<SerializeFieldAttribute>() != null;
                    if (!fPublic && !fSerialize) continue;
                    if (f.GetCustomAttribute<HideInInspectorAttribute>() != null) continue;

                    var fVal = f.GetValue(value);
                    if (fVal != null)
                        SerializeMember(nestedTable, f.Name, f.FieldType, fVal);
                }
                if (nestedTable.Count > 0)
                    table[name] = nestedTable;
                return;
            }

            // 씬 오브젝트 참조 (GameObject, Component 서브타입)
            if (IsSceneObjectReferenceType(memberType))
            {
                if (memberType == typeof(GameObject) && value is GameObject go && !go._isDestroyed)
                {
                    table[name] = new TomlTable
                    {
                        ["_sceneRef"] = "GameObject",
                        ["_guid"] = go.guid,
                    };
                }
                else if (value is Component comp && comp.gameObject != null && !comp._isDestroyed)
                {
                    table[name] = new TomlTable
                    {
                        ["_sceneRef"] = memberType.Name,
                        ["_guid"] = comp.gameObject.guid,
                    };
                }
                return;
            }

            var tomlVal = ValueToToml(value, memberType);
            if (tomlVal != null)
                table[name] = tomlVal;
        }

        private static object? ValueToToml(object? value, Type type)
        {
            if (value == null) return null;
            if (type == typeof(float)) return (double)(float)value;
            if (type == typeof(int)) return (long)(int)value;
            if (type == typeof(bool)) return (bool)value;
            if (type == typeof(string)) return (string)value;
            if (type == typeof(Vector2)) return Vec2ToArray((Vector2)value);
            if (type == typeof(Vector3)) return Vec3ToArray((Vector3)value);
            if (type == typeof(Quaternion)) return QuatToArray((Quaternion)value);
            if (type == typeof(Color)) return ColorToArray((Color)value);
            if (type == typeof(Vector4)) return Vec4ToArray((Vector4)value);
            if (type == typeof(long)) return (long)value;
            if (type == typeof(double)) return (double)value;
            if (type == typeof(byte)) return (long)(byte)value;
            if (type.IsEnum) return value.ToString();
            return null;
        }

        private static TomlTable? SerializeAssetRef(object value, Type assetType)
        {
            var db = Resources.GetAssetDatabase();
            if (db == null) return null;

            string? guid = null;

            if (assetType == typeof(Mesh) && value is Mesh mesh)
                guid = db.FindGuidForMesh(mesh);
            else if (assetType == typeof(Material) && value is Material mat)
                guid = db.FindGuidForMaterial(mat);
            else if (assetType == typeof(Texture2D) && value is Texture2D tex)
                guid = FindGuidByName(db, tex.name);
            else if (assetType == typeof(Font) && value is Font font)
                guid = FindGuidByName(db, font.name);
            else if (assetType == typeof(AnimationClip) && value is AnimationClip animClip)
                guid = db.FindGuidForAnimationClip(animClip);
            else if (assetType == typeof(Sprite) && value is Sprite sprite)
            {
                // 서브 에셋 GUID 우선
                guid = db.FindGuidForSprite(sprite);
                if (guid != null)
                    return new TomlTable { ["_assetGuid"] = guid, ["_assetType"] = "Sprite" };

                // 레거시 폴백: 텍스처 GUID + rect/pivot
                if (sprite.texture != null)
                {
                    guid = FindGuidByName(db, sprite.texture.name);
                    if (guid != null)
                    {
                        return new TomlTable
                        {
                            ["_assetGuid"] = guid,
                            ["_assetType"] = "Sprite",
                            ["rectX"] = (double)sprite.rect.x,
                            ["rectY"] = (double)sprite.rect.y,
                            ["rectW"] = (double)sprite.rect.width,
                            ["rectH"] = (double)sprite.rect.height,
                            ["pivotX"] = (double)sprite.pivot.x,
                            ["pivotY"] = (double)sprite.pivot.y,
                        };
                    }
                }
                return null;
            }

            if (guid == null) return null;
            return new TomlTable
            {
                ["_assetGuid"] = guid,
                ["_assetType"] = assetType.Name,
            };
        }

        private static string? FindGuidByName(IAssetDatabase db, string? assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return null;
            var path = db.GetAllAssetPaths()
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == assetName);
            if (path == null) return null;
            return db.GetGuidFromPath(path);
        }

        // ── 범용 Deserialize ──

        private static void DeserializeComponentGeneric(GameObject go, string typeName, TomlTable fields)
        {
            var cache = GetComponentTypeCache();
            if (!cache.TryGetValue(typeName, out var compType))
            {
                // 하위호환: 단축이름으로 저장된 기존 씬 파일 지원
                if (_shortNameToFullName != null
                    && _shortNameToFullName.TryGetValue(typeName, out var fullName)
                    && cache.TryGetValue(fullName, out compType))
                {
                    _shortNameWarnings.Add((typeName, fullName));
                }
                else
                {
                    EditorDebug.LogWarning($"[Scene] Unknown component type: {typeName}");
                    return;
                }
            }

            Component comp;
            try { comp = go.AddComponent(compType); }
            catch (Exception ex)
            {
                EditorDebug.LogError($"[Scene] Failed to add component {typeName}: {ex.Message}");
                return;
            }

            // 레거시 호환: 이전 포맷의 에셋 GUID 키 처리
            HandleLegacyAssetKeys(comp, compType, fields);

            // 필드/프로퍼티 매칭
            var fieldInfos = compType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var propInfos = compType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var kvp in fields)
            {
                string memberName = kvp.Key;
                object tomlVal = kvp.Value;

                // 필드 먼저 시도
                FieldInfo? fi = null;
                foreach (var f in fieldInfos)
                {
                    if (f.Name == memberName && IsSerializableField(f)) { fi = f; break; }
                }

                if (fi != null)
                {
                    try { SetMemberValue(comp, fi.FieldType, tomlVal, v => fi.SetValue(comp, v)); }
                    catch { }
                    continue;
                }

                // 프로퍼티 시도
                PropertyInfo? pi = null;
                foreach (var p in propInfos)
                {
                    if (p.Name == memberName && IsSerializableProperty(p)) { pi = p; break; }
                }

                if (pi != null)
                {
                    try { SetMemberValue(comp, pi.PropertyType, tomlVal, v => pi.SetValue(comp, v)); }
                    catch { }
                }
            }
        }

        // ── 씬 오브젝트 참조 지연 해석 ──
        private record PendingSceneRef(Component Owner, Type MemberType, Action<object> Setter, string TargetGuid);
        private static readonly List<PendingSceneRef> _pendingSceneRefs = new();

        private static void DeserializeNestedFields(object instance, Type type, TomlTable tbl)
        {
            foreach (var kvp in tbl)
            {
                var field = type.GetField(kvp.Key,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null) continue;

                if (IsNestedSerializableType(field.FieldType) && kvp.Value is TomlTable innerTbl)
                {
                    var inner = Activator.CreateInstance(field.FieldType)!;
                    DeserializeNestedFields(inner, field.FieldType, innerTbl);
                    field.SetValue(instance, inner);
                }
                else
                {
                    var clrVal = TomlToValue(kvp.Value, field.FieldType);
                    if (clrVal != null) field.SetValue(instance, clrVal);
                }
            }
        }

        private static void ResolveSceneReferences()
        {
            foreach (var pending in _pendingSceneRefs)
            {
                var targetGo = SceneManager.AllGameObjects
                    .FirstOrDefault(g => g.guid == pending.TargetGuid);
                if (targetGo == null) continue;

                if (pending.MemberType == typeof(GameObject))
                {
                    pending.Setter(targetGo);
                }
                else if (typeof(Component).IsAssignableFrom(pending.MemberType))
                {
                    var comp = targetGo.GetComponent(pending.MemberType);
                    if (comp != null) pending.Setter(comp);
                }
            }
            _pendingSceneRefs.Clear();
        }

        private static void SetMemberValue(Component comp, Type memberType, object tomlVal, Action<object> setter)
        {
            // 중첩 직렬화 구조체 역직렬화
            if (IsNestedSerializableType(memberType) && tomlVal is TomlTable nestedTbl
                && !nestedTbl.ContainsKey("_sceneRef") && !nestedTbl.ContainsKey("_assetGuid"))
            {
                var instance = Activator.CreateInstance(memberType)!;
                foreach (var kvp in nestedTbl)
                {
                    var field = memberType.GetField(kvp.Key,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field == null) continue;

                    if (IsNestedSerializableType(field.FieldType) && kvp.Value is TomlTable innerTbl)
                    {
                        var inner = Activator.CreateInstance(field.FieldType)!;
                        DeserializeNestedFields(inner, field.FieldType, innerTbl);
                        field.SetValue(instance, inner);
                    }
                    else
                    {
                        var fieldClrVal = TomlToValue(kvp.Value, field.FieldType);
                        if (fieldClrVal != null) field.SetValue(instance, fieldClrVal);
                    }
                }
                setter(instance);
                return;
            }

            // 씬 오브젝트 참조 역직렬화 (지연 해석)
            if (IsSceneObjectReferenceType(memberType) && tomlVal is TomlTable refTbl
                && refTbl.ContainsKey("_sceneRef"))
            {
                if (refTbl.TryGetValue("_guid", out var guidVal) && guidVal is string guid)
                    _pendingSceneRefs.Add(new PendingSceneRef(comp, memberType, setter, guid));
                return;
            }

            if (AssetReferenceTypes.Contains(memberType) && tomlVal is TomlTable assetTbl)
            {
                var asset = DeserializeAssetRef(assetTbl, memberType);
                if (asset != null) setter(asset);
                return;
            }

            // 에셋 참조 배열 역직렬화 (Sprite[], AnimationClip[] 등)
            if (memberType.IsArray && tomlVal is TomlTableArray refArr)
            {
                var elemType = memberType.GetElementType()!;
                if (AssetReferenceTypes.Contains(elemType))
                {
                    var list = new System.Collections.ArrayList();
                    foreach (TomlTable assetRefTbl in refArr)
                    {
                        var asset = DeserializeAssetRef(assetRefTbl, elemType);
                        if (asset != null)
                            list.Add(asset);
                    }
                    var arr = Array.CreateInstance(elemType, list.Count);
                    for (int i = 0; i < list.Count; i++)
                        arr.SetValue(list[i], i);
                    setter(arr);
                    return;
                }
            }

            // 값 타입 배열 역직렬화 (float[], Vector3[] 등)
            if (memberType.IsArray && tomlVal is TomlArray valArr)
            {
                var elemType = memberType.GetElementType()!;
                if (IsSupportedValueType(elemType))
                {
                    var arr = Array.CreateInstance(elemType, valArr.Count);
                    for (int i = 0; i < valArr.Count; i++)
                    {
                        var clrElem = TomlToValue(valArr[i], elemType);
                        if (clrElem != null) arr.SetValue(clrElem, i);
                    }
                    setter(arr);
                    return;
                }
            }

            // 값 타입 List<T> 역직렬화 (List<float>, List<Vector3> 등)
            if (memberType.IsGenericType && memberType.GetGenericTypeDefinition() == typeof(List<>)
                && tomlVal is TomlArray listArr)
            {
                var elemType = memberType.GetGenericArguments()[0];
                if (IsSupportedValueType(elemType))
                {
                    var listType = typeof(List<>).MakeGenericType(elemType);
                    var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
                    foreach (var item in listArr)
                    {
                        var clrElem = TomlToValue(item, elemType);
                        if (clrElem != null) list.Add(clrElem);
                    }
                    setter(list);
                    return;
                }
            }

            var clrVal = TomlToValue(tomlVal, memberType);
            if (clrVal != null) setter(clrVal);
        }

        private static object? TomlToValue(object? tomlVal, Type targetType)
        {
            if (tomlVal == null) return null;
            if (targetType == typeof(float)) return ToFloat(tomlVal);
            if (targetType == typeof(int))
                return tomlVal is long l ? (int)l : (int)ToFloat(tomlVal);
            if (targetType == typeof(bool) && tomlVal is bool b) return b;
            if (targetType == typeof(string)) return tomlVal?.ToString() ?? "";
            if (targetType == typeof(Vector2)) return ArrayToVec2(tomlVal);
            if (targetType == typeof(Vector3)) return ArrayToVec3Direct(tomlVal);
            if (targetType == typeof(Quaternion) && tomlVal is TomlArray qa && qa.Count >= 4)
                return new Quaternion(ToFloat(qa[0]), ToFloat(qa[1]), ToFloat(qa[2]), ToFloat(qa[3]));
            if (targetType == typeof(Color)) return ArrayToColor(tomlVal);
            if (targetType == typeof(Vector4)) return ArrayToVec4(tomlVal);
            if (targetType == typeof(long)) return tomlVal is long l2 ? l2 : (long)ToFloat(tomlVal);
            if (targetType == typeof(double)) return (double)ToFloat(tomlVal);
            if (targetType == typeof(byte)) return (byte)Math.Clamp(tomlVal is long lb ? lb : (long)ToFloat(tomlVal), 0, 255);
            if (targetType.IsEnum && tomlVal is string s && Enum.TryParse(targetType, s, out var ev))
                return ev;
            return null;
        }

        private static object? DeserializeAssetRef(TomlTable assetTable, Type targetType)
        {
            if (!assetTable.TryGetValue("_assetGuid", out var guidVal) || guidVal is not string guid)
                return null;

            var db = Resources.GetAssetDatabase();
            if (db == null) return null;

            if (targetType == typeof(Mesh)) return db.LoadByGuid<Mesh>(guid);
            if (targetType == typeof(Material)) return db.LoadByGuid<Material>(guid);
            if (targetType == typeof(Texture2D)) return db.LoadByGuid<Texture2D>(guid);
            if (targetType == typeof(Font)) return db.LoadByGuid<Font>(guid);
            if (targetType == typeof(AnimationClip)) return db.LoadByGuid<AnimationClip>(guid);
            if (targetType == typeof(Sprite))
            {
                // 서브 에셋 GUID로 직접 로드 시도
                var sprite = db.LoadByGuid<Sprite>(guid);
                if (sprite != null)
                    return sprite;

                // 레거시 폴백: 텍스처 GUID + rect/pivot
                var tex = db.LoadByGuid<Texture2D>(guid);
                if (tex == null) return null;
                float rx = 0, ry = 0, rw = tex.width, rh = tex.height;
                float px = 0.5f, py = 0.5f;
                if (assetTable.TryGetValue("rectX", out var rxv)) rx = ToFloat(rxv);
                if (assetTable.TryGetValue("rectY", out var ryv)) ry = ToFloat(ryv);
                if (assetTable.TryGetValue("rectW", out var rwv)) rw = ToFloat(rwv);
                if (assetTable.TryGetValue("rectH", out var rhv)) rh = ToFloat(rhv);
                if (assetTable.TryGetValue("pivotX", out var pxv)) px = ToFloat(pxv);
                if (assetTable.TryGetValue("pivotY", out var pyv)) py = ToFloat(pyv);
                return Sprite.Create(tex, new Rect(rx, ry, rw, rh), new Vector2(px, py));
            }
            return null;
        }

        /// <summary>이전 포맷의 에셋 GUID 키 호환 처리 (spriteGuid, fontGuid 등).</summary>
        private static void HandleLegacyAssetKeys(Component comp, Type compType, TomlTable fields)
        {
            var db = Resources.GetAssetDatabase();
            if (db == null) return;

            // SpriteRenderer: spriteGuid → sprite 필드
            if (fields.TryGetValue("spriteGuid", out var sgVal) && sgVal is string spriteGuid)
            {
                var tex = db.LoadByGuid<Texture2D>(spriteGuid);
                if (tex != null)
                {
                    var fi = compType.GetField("sprite", BindingFlags.Public | BindingFlags.Instance);
                    fi?.SetValue(comp, Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f)));
                }
            }

            // TextRenderer: fontGuid → font 필드
            if (fields.TryGetValue("fontGuid", out var fgVal) && fgVal is string fontGuid)
            {
                var font = db.LoadByGuid<Font>(fontGuid);
                if (font != null)
                {
                    var fi = compType.GetField("font", BindingFlags.Public | BindingFlags.Instance);
                    fi?.SetValue(comp, font);
                }
            }
        }

        private static Dictionary<string, Type> GetComponentTypeCache()
        {
            if (_componentTypeCache != null) return _componentTypeCache;

            var cache = new Dictionary<string, Type>();
            var shortNameMap = new Dictionary<string, string>();
            var baseType = typeof(Component);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name;
                if (asmName == null) continue;
                if (asmName.StartsWith("System") || asmName.StartsWith("Microsoft")
                    || asmName.StartsWith("netstandard"))
                    continue;

                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.IsAbstract || t.IsInterface) continue;
                        if (!baseType.IsAssignableFrom(t)) continue;

                        var fullName = t.FullName ?? t.Name;

                        // Roslyn 핫 리로드 타입(Location 없음)은 빌드 시 타입(Location 있음)보다 우선
                        bool isDynamic = string.IsNullOrEmpty(asm.Location);
                        if (cache.TryGetValue(fullName, out var existingType))
                        {
                            bool existingIsDynamic = string.IsNullOrEmpty(existingType.Assembly.Location);
                            if (isDynamic && !existingIsDynamic)
                                cache[fullName] = t; // Roslyn 타입이 빌드 타입을 대체
                            // 그 외: 기존 유지 (첫 번째 우선)
                        }
                        else
                        {
                            cache[fullName] = t;
                        }

                        // 단축이름 → 풀네임 매핑 (하위호환 + 중복 감지)
                        if (shortNameMap.TryGetValue(t.Name, out var existingShort))
                        {
                            if (existingShort != fullName)
                                EditorDebug.LogError($"[Scripting] Duplicate component class name '{t.Name}' found: '{existingShort}' and '{fullName}'. Use different class names to avoid conflicts.");
                        }
                        else
                        {
                            shortNameMap[t.Name] = fullName;
                        }
                    }
                }
                catch { }
            }

            _componentTypeCache = cache;
            _shortNameToFullName = shortNameMap;
            return cache;
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static TomlArray Vec2ToArray(Vector2 v)
        {
            var arr = new TomlArray { (double)v.x, (double)v.y };
            return arr;
        }

        /// <summary>GO가 프리팹 인스턴스의 자식인지 확인 (부모 순회).</summary>
        private static bool IsChildOfPrefabInstance(GameObject go, HashSet<int> prefabInstanceIds)
        {
            var parent = go.transform.parent;
            while (parent != null)
            {
                if (prefabInstanceIds.Contains(parent.gameObject.GetInstanceID()))
                    return true;
                parent = parent.parent;
            }
            return false;
        }

        private static TomlArray Vec3ToArray(Vector3 v)
        {
            var arr = new TomlArray { (double)v.x, (double)v.y, (double)v.z };
            return arr;
        }

        private static TomlArray QuatToArray(Quaternion q)
        {
            var arr = new TomlArray { (double)q.x, (double)q.y, (double)q.z, (double)q.w };
            return arr;
        }

        private static TomlArray ColorToArray(Color c)
        {
            var arr = new TomlArray { (double)c.r, (double)c.g, (double)c.b, (double)c.a };
            return arr;
        }

        private static TomlArray Vec4ToArray(Vector4 v)
        {
            var arr = new TomlArray { (double)v.x, (double)v.y, (double)v.z, (double)v.w };
            return arr;
        }

        private static Vector4 ArrayToVec4(object? val)
        {
            if (val is not TomlArray arr || arr.Count < 4)
                return Vector4.zero;
            return new Vector4(ToFloat(arr[0]), ToFloat(arr[1]), ToFloat(arr[2]), ToFloat(arr[3]));
        }

        private static Vector3 ArrayToVec3(TomlTable table, string key, Vector3? defaultVal = null)
        {
            var def = defaultVal ?? Vector3.zero;
            if (!table.TryGetValue(key, out var val) || val is not TomlArray arr || arr.Count < 3)
                return def;
            return new Vector3(ToFloat(arr[0]), ToFloat(arr[1]), ToFloat(arr[2]));
        }

        private static Vector3 ArrayToVec3Direct(object? val)
        {
            if (val is not TomlArray arr || arr.Count < 3)
                return Vector3.zero;
            return new Vector3(ToFloat(arr[0]), ToFloat(arr[1]), ToFloat(arr[2]));
        }

        private static Vector2 ArrayToVec2(object? val)
        {
            if (val is not TomlArray arr || arr.Count < 2)
                return Vector2.zero;
            return new Vector2(ToFloat(arr[0]), ToFloat(arr[1]));
        }

        private static Quaternion ArrayToQuat(TomlTable table, string key)
        {
            if (!table.TryGetValue(key, out var val) || val is not TomlArray arr || arr.Count < 4)
                return Quaternion.identity;
            return new Quaternion(ToFloat(arr[0]), ToFloat(arr[1]), ToFloat(arr[2]), ToFloat(arr[3]));
        }

        private static Quaternion ArrayToQuatDirect(object? val)
        {
            if (val is not TomlArray arr || arr.Count < 4)
                return Quaternion.identity;
            return new Quaternion(ToFloat(arr[0]), ToFloat(arr[1]), ToFloat(arr[2]), ToFloat(arr[3]));
        }

        /// <summary>TOML 값을 지정된 C# 타입으로 역직렬화.</summary>
        private static object? DeserializeFieldValue(object? tomlVal, Type targetType)
        {
            if (tomlVal == null) return null;
            if (targetType == typeof(float)) return ToFloat(tomlVal);
            if (targetType == typeof(int)) return tomlVal is long l ? (int)l : 0;
            if (targetType == typeof(bool)) return tomlVal is bool b && b;
            if (targetType == typeof(string)) return tomlVal.ToString();
            if (targetType == typeof(Vector2)) return ArrayToVec2(tomlVal);
            if (targetType == typeof(Vector3)) return ArrayToVec3Direct(tomlVal);
            if (targetType == typeof(Quaternion)) return ArrayToQuatDirect(tomlVal);
            if (targetType == typeof(Color)) return ArrayToColor(tomlVal);
            if (targetType == typeof(Vector4)) return ArrayToVec4(tomlVal);
            if (targetType == typeof(long)) return tomlVal is long l2 ? l2 : 0L;
            if (targetType == typeof(double)) return (double)ToFloat(tomlVal);
            if (targetType == typeof(byte)) return (byte)Math.Clamp(tomlVal is long lb ? lb : 0L, 0, 255);
            if (targetType.IsEnum && tomlVal is string es)
            {
                try { return Enum.Parse(targetType, es); }
                catch { return null; }
            }
            return null;
        }

        private static Color ArrayToColor(object? val)
        {
            if (val is not TomlArray arr || arr.Count < 4)
                return Color.white;
            return new Color(ToFloat(arr[0]), ToFloat(arr[1]), ToFloat(arr[2]), ToFloat(arr[3]));
        }

        private static float ToFloat(object? val)
        {
            return val switch
            {
                double d => (float)d,
                long l => l,
                float f => f,
                int i => i,
                string s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0f,
                _ => 0f,
            };
        }

        // ─── Scene Environment 직렬화 헬퍼 (Phase 38-B) ──────────

        /// <summary>씬 로드 전 환경 프로퍼티를 기본값으로 리셋.</summary>
        private static void ResetEnvironmentDefaults()
        {
            RenderSettings.skyboxTextureGuid = null;
            RenderSettings.skybox = null;
            RenderSettings.skyboxExposure = 1.0f;
            RenderSettings.skyboxRotation = 0.0f;
            RenderSettings.ambientIntensity = 1.0f;
            RenderSettings.ambientLight = new Color(0.2f, 0.2f, 0.2f, 1f);
            RenderSettings.skyZenithIntensity = 0.8f;
            RenderSettings.skyHorizonIntensity = 1.0f;
            RenderSettings.sunIntensity = 20.0f;
            RenderSettings.skyZenithColor = new Color(0.15f, 0.3f, 0.65f);
            RenderSettings.skyHorizonColor = new Color(0.6f, 0.7f, 0.85f);
        }

        /// <summary>[sceneEnvironment] 또는 구 [renderSettings] 테이블에서 환경 값 로드.</summary>
        private static void LoadSceneEnvironment(TomlTable table)
        {
            // Skybox
            if (table.TryGetValue("skyboxTextureGuid", out var sgVal) && sgVal is string skyGuid)
            {
                RenderSettings.skyboxTextureGuid = skyGuid;
                if (table.TryGetValue("skyboxExposure", out var seVal))
                    RenderSettings.skyboxExposure = ToFloat(seVal);
                if (table.TryGetValue("skyboxRotation", out var srVal))
                    RenderSettings.skyboxRotation = ToFloat(srVal);
                RenderSettings.ApplySkyboxFromGuid();
            }

            // Ambient
            if (table.TryGetValue("ambientIntensity", out var aiVal))
                RenderSettings.ambientIntensity = ToFloat(aiVal);
            if (table.TryGetValue("ambientLight", out var alVal))
                RenderSettings.ambientLight = ArrayToColor(alVal);

            // Procedural Sky
            if (table.TryGetValue("skyZenithIntensity", out var sziVal))
                RenderSettings.skyZenithIntensity = ToFloat(sziVal);
            if (table.TryGetValue("skyHorizonIntensity", out var shiVal))
                RenderSettings.skyHorizonIntensity = ToFloat(shiVal);
            if (table.TryGetValue("sunIntensity", out var siVal))
                RenderSettings.sunIntensity = ToFloat(siVal);
            if (table.TryGetValue("skyZenithColor", out var szcVal))
                RenderSettings.skyZenithColor = ArrayToColor(szcVal);
            if (table.TryGetValue("skyHorizonColor", out var shcVal))
                RenderSettings.skyHorizonColor = ArrayToColor(shcVal);
        }
    }
}
