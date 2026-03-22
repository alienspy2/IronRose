// ------------------------------------------------------------
// @file    CliCommandDispatcher.cs
// @brief   CLI 요청 평문을 파싱하여 적절한 핸들러를 호출하고 JSON 응답을 반환한다.
//          메인 스레드 실행이 필요한 명령은 큐에 넣고 결과를 대기한다.
// @deps    System.Text.Json, RoseEngine/SceneManager, IronRose.Engine/ProjectContext,
//          IronRose.Engine.Editor/EditorPlayMode, IronRose.Engine.Editor/EditorSelection,
//          IronRose.Engine.Editor/SceneSerializer, IronRose.Engine.Editor/GameObjectSnapshot,
//          IronRose.Engine.Cli/CliLogBuffer,
//          RoseEngine/Material, RoseEngine/MeshRenderer, RoseEngine/Light,
//          RoseEngine/Camera, RoseEngine/RenderSettings
// @exports
//   class CliCommandDispatcher
//     Dispatch(string requestLine): string  -- 요청 처리 후 응답 JSON 반환
//     ProcessMainThreadQueue(): void        -- 메인 스레드에서 호출하여 대기 중 명령 실행
// @note    백그라운드 스레드(Pipe)에서 Dispatch()가 호출된다.
//          메인 스레드 접근이 필요한 명령은 _mainThreadQueue에 넣고
//          ManualResetEventSlim으로 완료를 대기한다 (타임아웃 5초).
//          지원 명령: ping, scene.info, scene.list, scene.save, scene.load,
//          go.get, go.find, go.set_active, go.set_field,
//          select, play.enter, play.stop, play.pause, play.resume, play.state,
//          log.recent,
//          material.info, material.set_color, material.set_metallic, material.set_roughness,
//          light.info, light.set_color, light.set_intensity,
//          camera.info, camera.set_fov,
//          render.info, render.set_ambient
// ------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using IronRose.Engine.Editor;
using RoseEngine;

namespace IronRose.Engine.Cli
{
    public class CliCommandDispatcher
    {
        private readonly Dictionary<string, Func<string[], string>> _handlers = new();
        private readonly ConcurrentQueue<MainThreadTask> _mainThreadQueue = new();
        private readonly CliLogBuffer _logBuffer;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private class MainThreadTask
        {
            public required Func<string> Execute { get; init; }
            public ManualResetEventSlim Done { get; } = new(false);
            public string? Result { get; set; }
        }

        public CliCommandDispatcher(CliLogBuffer logBuffer)
        {
            _logBuffer = logBuffer;
            RegisterHandlers();
        }

        public string Dispatch(string requestLine)
        {
            try
            {
                var tokens = ParseArgs(requestLine.Trim());
                if (tokens.Length == 0)
                    return JsonError("Empty command");

                var command = tokens[0].ToLowerInvariant();
                var args = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();

                if (_handlers.TryGetValue(command, out var handler))
                    return handler(args);

                return JsonError($"Unknown command: {command}");
            }
            catch (Exception ex)
            {
                return JsonError($"Dispatch error: {ex.Message}");
            }
        }

        public void ProcessMainThreadQueue()
        {
            while (_mainThreadQueue.TryDequeue(out var task))
            {
                try
                {
                    task.Result = task.Execute();
                }
                catch (Exception ex)
                {
                    task.Result = JsonError(ex.Message);
                }
                finally
                {
                    task.Done.Set();
                }
            }
        }

        private void RegisterHandlers()
        {
            // ----------------------------------------------------------------
            // ping -- 백그라운드 스레드에서 직접 실행
            // ----------------------------------------------------------------
            _handlers["ping"] = args => JsonOk(new { pong = true, project = ProjectContext.ProjectName });

            // ----------------------------------------------------------------
            // scene.info -- 메인 스레드 필요
            // ----------------------------------------------------------------
            _handlers["scene.info"] = args => ExecuteOnMainThread(() =>
            {
                var scene = SceneManager.GetActiveScene();
                return JsonOk(new
                {
                    name = scene.name,
                    path = scene.path ?? "",
                    isDirty = scene.isDirty,
                    gameObjectCount = SceneManager.AllGameObjects.Count
                });
            });

            // ----------------------------------------------------------------
            // scene.list -- 메인 스레드 필요
            // ----------------------------------------------------------------
            _handlers["scene.list"] = args => ExecuteOnMainThread(() =>
            {
                var gos = SceneManager.AllGameObjects;
                var list = new List<object>();
                foreach (var go in gos)
                {
                    if (go._isDestroyed) continue;
                    list.Add(new
                    {
                        id = go.GetInstanceID(),
                        name = go.name,
                        active = go.activeSelf,
                        parentId = go.transform.parent?.gameObject.GetInstanceID()
                    });
                }
                return JsonOk(new { gameObjects = list });
            });

            // ----------------------------------------------------------------
            // scene.save -- 현재 씬 저장 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["scene.save"] = args => ExecuteOnMainThread(() =>
            {
                var scene = SceneManager.GetActiveScene();
                var savePath = args.Length > 0 ? args[0] : scene.path;

                if (string.IsNullOrEmpty(savePath))
                    return JsonError("No save path specified and scene has no existing path");

                SceneSerializer.Save(savePath);
                return JsonOk(new { saved = true, path = savePath });
            });

            // ----------------------------------------------------------------
            // scene.load -- 씬 파일 로드 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["scene.load"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: scene.load <path>");

                var loadPath = args[0];
                return ExecuteOnMainThread(() =>
                {
                    if (!System.IO.File.Exists(loadPath))
                        return JsonError($"File not found: {loadPath}");

                    SceneSerializer.Load(loadPath);
                    return JsonOk(new { loaded = true });
                });
            };

            // ----------------------------------------------------------------
            // go.get -- 특정 GameObject 상세 정보 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["go.get"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: go.get <id|name>");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObject(args[0]);
                    if (go == null)
                        return JsonError($"GameObject not found: {args[0]}");

                    var snapshot = GameObjectSnapshot.From(go);
                    return JsonOk(new
                    {
                        id = snapshot.InstanceId,
                        name = snapshot.Name,
                        active = snapshot.ActiveSelf,
                        parentId = snapshot.ParentId,
                        components = snapshot.Components.Select(c => new
                        {
                            typeName = c.TypeName,
                            fields = c.Fields.Select(f => new
                            {
                                name = f.Name,
                                typeName = f.TypeName,
                                value = f.Value
                            })
                        })
                    });
                });
            };

            // ----------------------------------------------------------------
            // go.find -- 이름으로 GameObject 검색 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["go.find"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: go.find <name>");

                var searchName = args[0];
                return ExecuteOnMainThread(() =>
                {
                    var matches = new List<object>();
                    foreach (var go in SceneManager.AllGameObjects)
                    {
                        if (go._isDestroyed) continue;
                        if (go.name == searchName)
                        {
                            matches.Add(new
                            {
                                id = go.GetInstanceID(),
                                name = go.name
                            });
                        }
                    }
                    return JsonOk(new { gameObjects = matches });
                });
            };

            // ----------------------------------------------------------------
            // go.set_active -- GameObject 활성/비활성 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["go.set_active"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: go.set_active <id> <true|false>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!bool.TryParse(args[1], out var active))
                    return JsonError($"Invalid bool value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    go.SetActive(active);
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // go.set_field -- 컴포넌트 필드 수정 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["go.set_field"] = args =>
            {
                if (args.Length < 4)
                    return JsonError("Usage: go.set_field <id> <component> <field> <value>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                var componentType = args[1];
                var fieldName = args[2];
                var newValue = args[3];

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var comp = go.InternalComponents
                        .FirstOrDefault(c => c.GetType().Name == componentType);
                    if (comp == null)
                        return JsonError($"Component not found: {componentType}");

                    var field = comp.GetType().GetField(fieldName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field == null)
                        return JsonError($"Field not found: {fieldName}");

                    var value = ParseFieldValue(field.FieldType, newValue);
                    if (value == null)
                        return JsonError($"Cannot parse value '{newValue}' as {field.FieldType.Name}");

                    field.SetValue(comp, value);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // select -- 에디터 선택 변경 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["select"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    if (args.Length == 0 || args[0].Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        EditorSelection.Clear();
                        return JsonOk(new { ok = true });
                    }

                    if (!int.TryParse(args[0], out var id))
                        return JsonError($"Invalid GameObject ID: {args[0]}");

                    EditorSelection.Select(id);
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // play.enter -- Play 모드 진입 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["play.enter"] = args => ExecuteOnMainThread(() =>
            {
                EditorPlayMode.EnterPlayMode();
                return JsonOk(new { state = EditorPlayMode.State.ToString() });
            });

            // ----------------------------------------------------------------
            // play.stop -- Play 모드 종료 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["play.stop"] = args => ExecuteOnMainThread(() =>
            {
                EditorPlayMode.StopPlayMode();
                return JsonOk(new { state = EditorPlayMode.State.ToString() });
            });

            // ----------------------------------------------------------------
            // play.pause -- 일시정지 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["play.pause"] = args => ExecuteOnMainThread(() =>
            {
                EditorPlayMode.PausePlayMode();
                return JsonOk(new { state = EditorPlayMode.State.ToString() });
            });

            // ----------------------------------------------------------------
            // play.resume -- 재개 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["play.resume"] = args => ExecuteOnMainThread(() =>
            {
                EditorPlayMode.ResumePlayMode();
                return JsonOk(new { state = EditorPlayMode.State.ToString() });
            });

            // ----------------------------------------------------------------
            // play.state -- 현재 Play 상태 조회 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["play.state"] = args => ExecuteOnMainThread(() =>
            {
                return JsonOk(new { state = EditorPlayMode.State.ToString() });
            });

            // ----------------------------------------------------------------
            // log.recent -- 최근 로그 조회 (스레드 안전, 직접 실행)
            // ----------------------------------------------------------------
            _handlers["log.recent"] = args =>
            {
                int count = 50;
                if (args.Length > 0 && int.TryParse(args[0], out var c))
                    count = c;

                var entries = _logBuffer.GetRecent(count);
                var logs = entries.Select(e => new
                {
                    level = e.Level.ToString(),
                    source = e.Source.ToString(),
                    message = e.Message,
                    timestamp = e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")
                });
                return JsonOk(new { logs });
            };

            // ================================================================
            // Wave 3: 렌더링/비주얼 명령
            // ================================================================

            // ----------------------------------------------------------------
            // material.info -- GO의 MeshRenderer 머티리얼 정보 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["material.info"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: material.info <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var renderer = go.GetComponent<MeshRenderer>();
                    if (renderer == null)
                        return JsonError($"No MeshRenderer on GameObject: {id}");

                    var mat = renderer.material;
                    if (mat == null)
                        return JsonError($"No material assigned to MeshRenderer on: {id}");

                    return JsonOk(new
                    {
                        name = mat.name,
                        color = FormatColor(mat.color),
                        metallic = mat.metallic,
                        roughness = mat.roughness,
                        occlusion = mat.occlusion,
                        emission = FormatColor(mat.emission),
                        hasMainTexture = mat.mainTexture != null,
                        hasNormalMap = mat.normalMap != null,
                        textureScale = $"{mat.textureScale.x.ToString(CultureInfo.InvariantCulture)}, {mat.textureScale.y.ToString(CultureInfo.InvariantCulture)}",
                        textureOffset = $"{mat.textureOffset.x.ToString(CultureInfo.InvariantCulture)}, {mat.textureOffset.y.ToString(CultureInfo.InvariantCulture)}"
                    });
                });
            };

            // ----------------------------------------------------------------
            // material.set_color -- 머티리얼 색상 변경 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["material.set_color"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: material.set_color <goId> <r,g,b,a>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var renderer = go.GetComponent<MeshRenderer>();
                    if (renderer?.material == null)
                        return JsonError($"No material on GameObject: {id}");

                    try
                    {
                        renderer.material.color = ParseColor(args[1]);
                        SceneManager.GetActiveScene().isDirty = true;
                        return JsonOk(new { ok = true });
                    }
                    catch (Exception ex)
                    {
                        return JsonError($"Failed to parse color: {ex.Message}");
                    }
                });
            };

            // ----------------------------------------------------------------
            // material.set_metallic -- metallic 변경 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["material.set_metallic"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: material.set_metallic <goId> <value>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    return JsonError($"Invalid float value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var renderer = go.GetComponent<MeshRenderer>();
                    if (renderer?.material == null)
                        return JsonError($"No material on GameObject: {id}");

                    renderer.material.metallic = value;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // material.set_roughness -- roughness 변경 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["material.set_roughness"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: material.set_roughness <goId> <value>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    return JsonError($"Invalid float value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var renderer = go.GetComponent<MeshRenderer>();
                    if (renderer?.material == null)
                        return JsonError($"No material on GameObject: {id}");

                    renderer.material.roughness = value;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // light.info -- 라이트 정보 조회 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["light.info"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: light.info <id>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var light = go.GetComponent<Light>();
                    if (light == null)
                        return JsonError($"No Light component on GameObject: {id}");

                    return JsonOk(new
                    {
                        type = light.type.ToString(),
                        color = FormatColor(light.color),
                        intensity = light.intensity,
                        range = light.range,
                        spotAngle = light.spotAngle,
                        spotOuterAngle = light.spotOuterAngle,
                        shadows = light.shadows,
                        shadowResolution = light.shadowResolution,
                        shadowBias = light.shadowBias,
                        enabled = light.enabled
                    });
                });
            };

            // ----------------------------------------------------------------
            // light.set_color -- 라이트 색상 변경 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["light.set_color"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: light.set_color <id> <r,g,b,a>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var light = go.GetComponent<Light>();
                    if (light == null)
                        return JsonError($"No Light component on GameObject: {id}");

                    try
                    {
                        light.color = ParseColor(args[1]);
                        SceneManager.GetActiveScene().isDirty = true;
                        return JsonOk(new { ok = true });
                    }
                    catch (Exception ex)
                    {
                        return JsonError($"Failed to parse color: {ex.Message}");
                    }
                });
            };

            // ----------------------------------------------------------------
            // light.set_intensity -- 라이트 강도 변경 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["light.set_intensity"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: light.set_intensity <id> <value>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    return JsonError($"Invalid float value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var light = go.GetComponent<Light>();
                    if (light == null)
                        return JsonError($"No Light component on GameObject: {id}");

                    light.intensity = value;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // camera.info -- 카메라 정보 조회 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["camera.info"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    Camera? cam;
                    if (args.Length >= 1 && int.TryParse(args[0], out var id))
                    {
                        var go = FindGameObjectById(id);
                        if (go == null)
                            return JsonError($"GameObject not found: {id}");
                        cam = go.GetComponent<Camera>();
                        if (cam == null)
                            return JsonError($"No Camera component on GameObject: {id}");
                    }
                    else
                    {
                        cam = Camera.main;
                        if (cam == null)
                            return JsonError("No main camera found");
                    }

                    return JsonOk(new
                    {
                        id = cam.gameObject.GetInstanceID(),
                        name = cam.gameObject.name,
                        fov = cam.fieldOfView,
                        near = cam.nearClipPlane,
                        far = cam.farClipPlane,
                        clearFlags = cam.clearFlags.ToString(),
                        backgroundColor = FormatColor(cam.backgroundColor)
                    });
                });
            };

            // ----------------------------------------------------------------
            // camera.set_fov -- FOV 설정 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["camera.set_fov"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: camera.set_fov <id> <fov>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var fov))
                    return JsonError($"Invalid float value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var cam = go.GetComponent<Camera>();
                    if (cam == null)
                        return JsonError($"No Camera component on GameObject: {id}");

                    cam.fieldOfView = fov;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // render.info -- 현재 렌더 설정 조회 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["render.info"] = args => ExecuteOnMainThread(() =>
            {
                return JsonOk(new
                {
                    ambientColor = FormatColor(RenderSettings.ambientLight),
                    ambientIntensity = RenderSettings.ambientIntensity,
                    skyboxExposure = RenderSettings.skyboxExposure,
                    skyboxRotation = RenderSettings.skyboxRotation,
                    hasSkybox = RenderSettings.skybox != null,
                    skyboxTextureGuid = RenderSettings.skyboxTextureGuid ?? "",
                    fsrEnabled = RenderSettings.fsrEnabled,
                    fsrScaleMode = RenderSettings.fsrScaleMode.ToString(),
                    ssilEnabled = RenderSettings.ssilEnabled
                });
            });

            // ----------------------------------------------------------------
            // render.set_ambient -- 앰비언트 색상 변경 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["render.set_ambient"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: render.set_ambient <r,g,b>");

                return ExecuteOnMainThread(() =>
                {
                    try
                    {
                        var parts = args[0].Trim('(', ')', ' ').Split(',');
                        float r = float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
                        float g = float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
                        float b = float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
                        float a = parts.Length > 3 ? float.Parse(parts[3].Trim(), CultureInfo.InvariantCulture) : 1f;
                        RenderSettings.ambientLight = new Color(r, g, b, a);
                        SceneManager.GetActiveScene().isDirty = true;
                        return JsonOk(new { ok = true });
                    }
                    catch (Exception ex)
                    {
                        return JsonError($"Failed to parse color: {ex.Message}");
                    }
                });
            };
        }

        private string ExecuteOnMainThread(Func<string> action)
        {
            var task = new MainThreadTask { Execute = action };
            _mainThreadQueue.Enqueue(task);
            if (!task.Done.Wait(TimeSpan.FromSeconds(5)))
                return JsonError("Main thread timeout (5s)");
            return task.Result!;
        }

        // ================================================================
        // 헬퍼 메서드
        // ================================================================

        /// <summary>ID 또는 이름으로 GameObject를 찾는다. 메인 스레드에서 호출해야 한다.</summary>
        private static GameObject? FindGameObject(string idOrName)
        {
            if (int.TryParse(idOrName, out var id))
                return FindGameObjectById(id);

            // 이름으로 검색 (첫 번째 매칭)
            foreach (var go in SceneManager.AllGameObjects)
            {
                if (!go._isDestroyed && go.name == idOrName)
                    return go;
            }
            return null;
        }

        /// <summary>InstanceID로 GameObject를 찾는다. 메인 스레드에서 호출해야 한다.</summary>
        private static GameObject? FindGameObjectById(int id)
        {
            foreach (var go in SceneManager.AllGameObjects)
            {
                if (!go._isDestroyed && go.GetInstanceID() == id)
                    return go;
            }
            return null;
        }

        /// <summary>문자열 값을 지정한 타입으로 파싱한다.</summary>
        private static object? ParseFieldValue(Type type, string raw)
        {
            try
            {
                if (type == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
                if (type == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
                if (type == typeof(bool)) return bool.Parse(raw);
                if (type == typeof(string)) return raw;
                if (type == typeof(Vector3)) return ParseVector3(raw);
                if (type == typeof(Color)) return ParseColor(raw);
                if (type.IsEnum) return Enum.Parse(type, raw);
            }
            catch { }
            return null;
        }

        private static Vector3 ParseVector3(string raw)
        {
            var cleaned = raw.Trim('(', ')', ' ');
            var parts = cleaned.Split(',');
            return new Vector3(
                float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture));
        }

        /// <summary>Color를 "r, g, b, a" 문자열로 포맷한다.</summary>
        private static string FormatColor(Color c)
        {
            return $"{c.r.ToString(CultureInfo.InvariantCulture)}, {c.g.ToString(CultureInfo.InvariantCulture)}, {c.b.ToString(CultureInfo.InvariantCulture)}, {c.a.ToString(CultureInfo.InvariantCulture)}";
        }

        private static Color ParseColor(string raw)
        {
            var cleaned = raw.Trim('(', ')', ' ');
            var parts = cleaned.Split(',');
            return new Color(
                float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture),
                parts.Length > 3 ? float.Parse(parts[3].Trim(), CultureInfo.InvariantCulture) : 1f);
        }

        // ================================================================
        // 파싱 / JSON
        // ================================================================

        private static string[] ParseArgs(string requestLine)
        {
            var args = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < requestLine.Length; i++)
            {
                char c = requestLine[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }
                if (c == ' ' && !inQuotes)
                {
                    if (current.Length > 0)
                    {
                        args.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }
                current.Append(c);
            }
            if (current.Length > 0)
                args.Add(current.ToString());

            return args.ToArray();
        }

        private static string JsonOk(object data)
        {
            return JsonSerializer.Serialize(new { ok = true, data }, _jsonOptions);
        }

        private static string JsonError(string message)
        {
            return JsonSerializer.Serialize(new { ok = false, error = message }, _jsonOptions);
        }
    }
}
