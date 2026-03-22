// ------------------------------------------------------------
// @file    CliCommandDispatcher.cs
// @brief   CLI 요청 평문을 파싱하여 적절한 핸들러를 호출하고 JSON 응답을 반환한다.
//          메인 스레드 실행이 필요한 명령은 큐에 넣고 결과를 대기한다.
// @deps    System.Text.Json, RoseEngine/SceneManager, IronRose.Engine/ProjectContext,
//          IronRose.Engine.Editor/EditorPlayMode, IronRose.Engine.Editor/EditorSelection,
//          IronRose.Engine.Editor/SceneSerializer, IronRose.Engine.Editor/GameObjectSnapshot,
//          IronRose.Engine.Cli/CliLogBuffer,
//          RoseEngine/Material, RoseEngine/MeshRenderer, RoseEngine/Light,
//          RoseEngine/Camera, RoseEngine/RenderSettings, RoseEngine/Screen,
//          RoseEngine/PrefabUtility, RoseEngine/Resources,
//          IronRose.Engine.Editor/EditorClipboard, IronRose.Engine.Editor/UndoSystem,
//          IronRose.AssetPipeline/AssetDatabase
// @exports
//   class CliCommandDispatcher
//     Dispatch(string requestLine): string          -- 요청 처리 후 응답 JSON 반환
//     ProcessMainThreadQueue(): void                -- 메인 스레드에서 호출하여 대기 중 명령 실행
//     _pendingScreenshotPath: string? (internal)    -- CLI 스크린샷 요청 경로 (EngineCore에서 소비)
// @note    백그라운드 스레드(Pipe)에서 Dispatch()가 호출된다.
//          메인 스레드 접근이 필요한 명령은 _mainThreadQueue에 넣고
//          ManualResetEventSlim으로 완료를 대기한다 (타임아웃 5초).
//          지원 명령: ping, scene.info, scene.list, scene.save, scene.load,
//          go.get, go.find, go.set_active, go.set_field,
//          select, play.enter, play.stop, play.pause, play.resume, play.state,
//          log.recent,
//          go.create, go.create_primitive, go.destroy, go.rename, go.duplicate,
//          transform.get, transform.set_position, transform.set_rotation,
//          transform.set_scale, transform.set_parent,
//          component.add, component.remove, component.list,
//          editor.undo, editor.redo,
//          prefab.instantiate, prefab.save,
//          asset.list, asset.find, asset.guid, asset.path,
//          scene.tree, scene.new,
//          material.info, material.set_color, material.set_metallic, material.set_roughness,
//          light.info, light.set_color, light.set_intensity,
//          camera.info, camera.set_fov,
//          render.info, render.set_ambient,
//          transform.translate, transform.rotate, transform.look_at,
//          transform.get_children, transform.set_local_position,
//          prefab.create_variant, prefab.is_instance, prefab.unpack,
//          asset.import, asset.scan,
//          editor.screenshot, editor.copy, editor.paste, editor.select_all, editor.undo_history,
//          screen.info, scene.clear,
//          camera.set_clip, light.set_type, light.set_range, light.set_shadows,
//          render.set_skybox_exposure
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
using IronRose.AssetPipeline;
using IronRose.Engine.Editor;
using RoseEngine;

namespace IronRose.Engine.Cli
{
    public class CliCommandDispatcher
    {
        private readonly Dictionary<string, Func<string[], string>> _handlers = new();
        private readonly ConcurrentQueue<MainThreadTask> _mainThreadQueue = new();
        private readonly CliLogBuffer _logBuffer;

        /// <summary>CLI에서 요청한 스크린샷 경로. EngineCore.Update()에서 소비.</summary>
        internal static string? _pendingScreenshotPath;

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
            // select.get -- 현재 선택된 GameObject 조회 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["select.get"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    var ids = EditorSelection.SelectedGameObjectIds;
                    if (ids.Count == 0)
                        return JsonOk(new { count = 0, gameObjects = Array.Empty<object>() });

                    var list = new List<object>();
                    foreach (var id in ids)
                    {
                        var go = FindGameObjectById(id);
                        if (go != null)
                            list.Add(new { id = go.GetInstanceID(), name = go.name, active = go.activeSelf });
                    }
                    return JsonOk(new { count = list.Count, gameObjects = list });
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
            // Wave 1: 핵심 명령 (GO CRUD, Transform, Component, Undo/Redo)
            // ================================================================

            // ----------------------------------------------------------------
            // go.create -- 빈 GameObject 생성 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["go.create"] = args =>
            {
                var name = args.Length > 0 ? args[0] : "GameObject";
                return ExecuteOnMainThread(() =>
                {
                    var go = new GameObject(name);
                    return JsonOk(new { id = go.GetInstanceID(), name = go.name });
                });
            };

            // ----------------------------------------------------------------
            // go.create_primitive -- Primitive 생성 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["go.create_primitive"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: go.create_primitive <type> (Cube|Sphere|Capsule|Cylinder|Plane|Quad)");

                if (!Enum.TryParse<PrimitiveType>(args[0], ignoreCase: true, out var primitiveType))
                    return JsonError($"Unknown primitive type: {args[0]}. Valid: Cube, Sphere, Capsule, Cylinder, Plane, Quad");

                return ExecuteOnMainThread(() =>
                {
                    var go = GameObject.CreatePrimitive(primitiveType);
                    return JsonOk(new { id = go.GetInstanceID(), name = go.name });
                });
            };

            // ----------------------------------------------------------------
            // go.destroy -- GameObject 삭제 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["go.destroy"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: go.destroy <id>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    RoseEngine.Object.DestroyImmediate(go);
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // go.rename -- GameObject 이름 변경 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["go.rename"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: go.rename <id> <name>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                var newName = args[1];
                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    go.name = newName;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // go.duplicate -- GameObject 복제 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["go.duplicate"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: go.duplicate <id>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var clone = RoseEngine.Object.Instantiate(go);
                    clone.name = go.name + "_copy";
                    if (go.transform.parent != null)
                        clone.transform.SetParent(go.transform.parent);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { id = clone.GetInstanceID(), name = clone.name });
                });
            };

            // ----------------------------------------------------------------
            // transform.get -- position/rotation/scale 조회 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["transform.get"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: transform.get <id>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var t = go.transform;
                    return JsonOk(new
                    {
                        position = FormatVector3(t.position),
                        localPosition = FormatVector3(t.localPosition),
                        rotation = FormatVector3(t.eulerAngles),
                        localRotation = FormatVector3(t.localEulerAngles),
                        localScale = FormatVector3(t.localScale),
                        lossyScale = FormatVector3(t.lossyScale)
                    });
                });
            };

            // ----------------------------------------------------------------
            // transform.set_position -- 월드 위치 설정 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["transform.set_position"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: transform.set_position <id> <x,y,z>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    try
                    {
                        go.transform.position = ParseVector3(args[1]);
                        SceneManager.GetActiveScene().isDirty = true;
                        return JsonOk(new { ok = true });
                    }
                    catch (Exception ex)
                    {
                        return JsonError($"Failed to parse position: {ex.Message}");
                    }
                });
            };

            // ----------------------------------------------------------------
            // transform.set_rotation -- 오일러 회전 설정 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["transform.set_rotation"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: transform.set_rotation <id> <x,y,z>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    try
                    {
                        go.transform.eulerAngles = ParseVector3(args[1]);
                        SceneManager.GetActiveScene().isDirty = true;
                        return JsonOk(new { ok = true });
                    }
                    catch (Exception ex)
                    {
                        return JsonError($"Failed to parse rotation: {ex.Message}");
                    }
                });
            };

            // ----------------------------------------------------------------
            // transform.set_scale -- 로컬 스케일 설정 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["transform.set_scale"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: transform.set_scale <id> <x,y,z>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    try
                    {
                        go.transform.localScale = ParseVector3(args[1]);
                        SceneManager.GetActiveScene().isDirty = true;
                        return JsonOk(new { ok = true });
                    }
                    catch (Exception ex)
                    {
                        return JsonError($"Failed to parse scale: {ex.Message}");
                    }
                });
            };

            // ----------------------------------------------------------------
            // transform.set_parent -- 부모 변경 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["transform.set_parent"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: transform.set_parent <id> <parentId|none>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    if (args[1].Equals("none", StringComparison.OrdinalIgnoreCase) || args[1] == "0")
                    {
                        go.transform.SetParent(null);
                    }
                    else
                    {
                        if (!int.TryParse(args[1], out var parentId))
                            return JsonError($"Invalid parent ID: {args[1]}");

                        var parentGo = FindGameObjectById(parentId);
                        if (parentGo == null)
                            return JsonError($"Parent GameObject not found: {parentId}");

                        go.transform.SetParent(parentGo.transform);
                    }

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // component.add -- 컴포넌트 추가 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["component.add"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: component.add <goId> <typeName>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                var typeName = args[1];
                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var type = ResolveComponentType(typeName);
                    if (type == null)
                        return JsonError($"Component type not found: {typeName}");

                    if (!typeof(Component).IsAssignableFrom(type))
                        return JsonError($"{typeName} does not derive from Component");

                    var comp = go.AddComponent(type);
                    return JsonOk(new { ok = true, typeName = comp.GetType().Name });
                });
            };

            // ----------------------------------------------------------------
            // component.remove -- 컴포넌트 제거 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["component.remove"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: component.remove <goId> <typeName>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                var typeName = args[1];
                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var comp = go.InternalComponents
                        .FirstOrDefault(c => c.GetType().Name == typeName && c.GetType() != typeof(Transform));
                    if (comp == null)
                        return JsonError($"Component not found: {typeName}");

                    if (comp is Transform)
                        return JsonError("Cannot remove Transform component");

                    RoseEngine.Object.DestroyImmediate(comp);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // component.list -- GO의 모든 컴포넌트 목록 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["component.list"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: component.list <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var components = new List<object>();
                    foreach (var comp in go.InternalComponents)
                    {
                        if (comp._isDestroyed) continue;
                        var fields = new List<object>();
                        foreach (var field in comp.GetType().GetFields(
                            BindingFlags.Public | BindingFlags.Instance))
                        {
                            try
                            {
                                var val = field.GetValue(comp);
                                fields.Add(new
                                {
                                    name = field.Name,
                                    typeName = field.FieldType.Name,
                                    value = val?.ToString() ?? "null"
                                });
                            }
                            catch { /* skip unreadable fields */ }
                        }
                        foreach (var prop in comp.GetType().GetProperties(
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        {
                            if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                            if (prop.Name == "gameObject" || prop.Name == "transform") continue;
                            try
                            {
                                var val = prop.GetValue(comp);
                                fields.Add(new
                                {
                                    name = prop.Name,
                                    typeName = prop.PropertyType.Name,
                                    value = val?.ToString() ?? "null"
                                });
                            }
                            catch { /* skip unreadable properties */ }
                        }
                        components.Add(new
                        {
                            typeName = comp.GetType().Name,
                            fields
                        });
                    }
                    return JsonOk(new { components });
                });
            };

            // ----------------------------------------------------------------
            // editor.undo -- 실행취소 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["editor.undo"] = args => ExecuteOnMainThread(() =>
            {
                var desc = UndoSystem.PerformUndo();
                if (desc == null)
                    return JsonOk(new { ok = true, description = "Nothing to undo" });
                return JsonOk(new { ok = true, description = desc });
            });

            // ----------------------------------------------------------------
            // editor.redo -- 다시실행 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["editor.redo"] = args => ExecuteOnMainThread(() =>
            {
                var desc = UndoSystem.PerformRedo();
                if (desc == null)
                    return JsonOk(new { ok = true, description = "Nothing to redo" });
                return JsonOk(new { ok = true, description = desc });
            });

            // ================================================================
            // Wave 2: 에셋/프리팹 명령
            // ================================================================

            // ----------------------------------------------------------------
            // prefab.instantiate -- 프리팹 인스턴스 생성 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["prefab.instantiate"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: prefab.instantiate <guid> [x,y,z]");

                var guid = args[0];
                return ExecuteOnMainThread(() =>
                {
                    GameObject? go;
                    if (args.Length >= 2)
                    {
                        try
                        {
                            var pos = ParseVector3(args[1]);
                            go = PrefabUtility.InstantiatePrefab(guid, pos, Quaternion.identity);
                        }
                        catch (Exception ex)
                        {
                            return JsonError($"Failed to parse position: {ex.Message}");
                        }
                    }
                    else
                    {
                        go = PrefabUtility.InstantiatePrefab(guid);
                    }

                    if (go == null)
                        return JsonError($"Failed to instantiate prefab: guid={guid}");

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { id = go.GetInstanceID(), name = go.name });
                });
            };

            // ----------------------------------------------------------------
            // prefab.save -- GO를 프리팹으로 저장 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["prefab.save"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: prefab.save <goId> <path>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                var path = args[1];
                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    try
                    {
                        var guid = PrefabUtility.SaveAsPrefab(go, path);
                        return JsonOk(new { saved = true, path, guid });
                    }
                    catch (Exception ex)
                    {
                        return JsonError($"Failed to save prefab: {ex.Message}");
                    }
                });
            };

            // ----------------------------------------------------------------
            // asset.list -- 에셋 폴더 탐색 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["asset.list"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    var db = Resources.GetAssetDatabase();
                    if (db == null)
                        return JsonError("AssetDatabase not initialized");

                    var allPaths = db.GetAllAssetPaths();
                    string filterPath = args.Length > 0 ? args[0] : "";

                    var assets = new List<object>();
                    foreach (var assetPath in allPaths)
                    {
                        // filterPath가 지정되었으면 해당 경로로 시작하는 에셋만 반환
                        if (!string.IsNullOrEmpty(filterPath) &&
                            !assetPath.Contains(filterPath, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var guid = db.GetGuidFromPath(assetPath);
                        var ext = System.IO.Path.GetExtension(assetPath).TrimStart('.');
                        assets.Add(new
                        {
                            path = assetPath,
                            guid = guid ?? "",
                            type = ext
                        });
                    }
                    return JsonOk(new { assets });
                });
            };

            // ----------------------------------------------------------------
            // asset.find -- 이름으로 에셋 검색 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["asset.find"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: asset.find <name>");

                var searchName = args[0];
                return ExecuteOnMainThread(() =>
                {
                    var db = Resources.GetAssetDatabase();
                    if (db == null)
                        return JsonError("AssetDatabase not initialized");

                    var allPaths = db.GetAllAssetPaths();
                    var matches = new List<object>();
                    foreach (var assetPath in allPaths)
                    {
                        var fileName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
                        if (fileName.Contains(searchName, StringComparison.OrdinalIgnoreCase))
                        {
                            var guid = db.GetGuidFromPath(assetPath);
                            var ext = System.IO.Path.GetExtension(assetPath).TrimStart('.');
                            matches.Add(new
                            {
                                path = assetPath,
                                guid = guid ?? "",
                                type = ext
                            });
                        }
                    }
                    return JsonOk(new { assets = matches });
                });
            };

            // ----------------------------------------------------------------
            // asset.guid -- 경로에서 GUID 조회 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["asset.guid"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: asset.guid <path>");

                var path = args[0];
                return ExecuteOnMainThread(() =>
                {
                    var db = Resources.GetAssetDatabase();
                    if (db == null)
                        return JsonError("AssetDatabase not initialized");

                    var guid = db.GetGuidFromPath(path);
                    if (string.IsNullOrEmpty(guid))
                        return JsonError($"No GUID found for path: {path}");

                    return JsonOk(new { guid });
                });
            };

            // ----------------------------------------------------------------
            // asset.path -- GUID에서 경로 조회 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["asset.path"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: asset.path <guid>");

                var guid = args[0];
                return ExecuteOnMainThread(() =>
                {
                    var db = Resources.GetAssetDatabase();
                    if (db == null)
                        return JsonError("AssetDatabase not initialized");

                    var path = db.GetPathFromGuid(guid);
                    if (string.IsNullOrEmpty(path))
                        return JsonError($"No path found for GUID: {guid}");

                    return JsonOk(new { path });
                });
            };

            // ----------------------------------------------------------------
            // scene.tree -- 계층 트리 (부모-자식 구조, 메인 스레드)
            // ----------------------------------------------------------------
            _handlers["scene.tree"] = args => ExecuteOnMainThread(() =>
            {
                var roots = new List<object>();
                foreach (var go in SceneManager.AllGameObjects)
                {
                    if (go._isDestroyed) continue;
                    if (go.transform.parent != null) continue; // 루트만
                    roots.Add(BuildTreeNode(go));
                }
                return JsonOk(new { tree = roots });
            });

            // ----------------------------------------------------------------
            // scene.new -- 새 빈 씬 생성 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["scene.new"] = args => ExecuteOnMainThread(() =>
            {
                SceneManager.Clear();
                var newScene = new Scene();
                newScene.name = "New Scene";
                SceneManager.SetActiveScene(newScene);
                return JsonOk(new { ok = true });
            });

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
                        SaveMaterialToDisk(renderer.material);
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
                    SaveMaterialToDisk(renderer.material);
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
                    SaveMaterialToDisk(renderer.material);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // material.create -- 새 머티리얼 파일 생성 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["material.create"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: material.create <name> <dirPath> [r,g,b,a]");

                var matName = args[0];
                var dirPath = args[1];

                return ExecuteOnMainThread(() =>
                {
                    var filePath = Path.Combine(dirPath, matName + ".mat");

                    // 색상이 지정되면 해당 색으로, 아니면 기본 흰색으로 생성
                    if (args.Length >= 3)
                    {
                        var mat = new Material { name = matName, color = ParseColor(args[2]) };
                        MaterialImporter.WriteMaterial(filePath, mat);
                    }
                    else
                    {
                        MaterialImporter.WriteDefault(filePath);
                    }

                    // AssetDatabase에 등록하기 위해 리스캔
                    var db = Resources.GetAssetDatabase();
                    db?.ScanAssets(ProjectContext.ProjectRoot);

                    var guid = db?.GetGuidFromPath(filePath);
                    return JsonOk(new { created = true, path = filePath, guid = guid ?? "" });
                });
            };

            // ----------------------------------------------------------------
            // material.apply -- GO의 MeshRenderer에 머티리얼 적용 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["material.apply"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: material.apply <goId> <materialGuid|materialPath>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                var matRef = args[1];

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var renderer = go.GetComponent<MeshRenderer>();
                    if (renderer == null)
                        return JsonError($"No MeshRenderer on GameObject: {id}");

                    var db = Resources.GetAssetDatabase();
                    if (db == null)
                        return JsonError("AssetDatabase not available");

                    Material? mat = null;
                    // GUID로 시도
                    var path = db.GetPathFromGuid(matRef);
                    if (path != null)
                        mat = db.Load<Material>(path);
                    // 경로로 시도
                    if (mat == null && System.IO.File.Exists(matRef))
                        mat = db.Load<Material>(matRef);

                    if (mat == null)
                        return JsonError($"Material not found: {matRef}");

                    renderer.material = mat;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true, materialName = mat.name });
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

            // ================================================================
            // Wave 4: 편의 기능 명령
            // ================================================================

            // ----------------------------------------------------------------
            // transform.translate -- 상대 이동 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["transform.translate"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: transform.translate <id> <x,y,z>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    try
                    {
                        go.transform.Translate(ParseVector3(args[1]));
                        SceneManager.GetActiveScene().isDirty = true;
                        return JsonOk(new { ok = true });
                    }
                    catch (Exception ex)
                    {
                        return JsonError($"Failed to parse translation: {ex.Message}");
                    }
                });
            };

            // ----------------------------------------------------------------
            // transform.rotate -- 상대 회전 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["transform.rotate"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: transform.rotate <id> <x,y,z>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    try
                    {
                        go.transform.Rotate(ParseVector3(args[1]));
                        SceneManager.GetActiveScene().isDirty = true;
                        return JsonOk(new { ok = true });
                    }
                    catch (Exception ex)
                    {
                        return JsonError($"Failed to parse rotation: {ex.Message}");
                    }
                });
            };

            // ----------------------------------------------------------------
            // transform.look_at -- 타겟을 바라봄 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["transform.look_at"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: transform.look_at <id> <targetId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!int.TryParse(args[1], out var targetId))
                    return JsonError($"Invalid target GameObject ID: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var target = FindGameObjectById(targetId);
                    if (target == null)
                        return JsonError($"Target GameObject not found: {targetId}");

                    go.transform.LookAt(target.transform);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // transform.get_children -- 자식 목록 조회 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["transform.get_children"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: transform.get_children <id>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var children = new List<object>();
                    for (int i = 0; i < go.transform.childCount; i++)
                    {
                        var child = go.transform.GetChild(i).gameObject;
                        if (!child._isDestroyed)
                        {
                            children.Add(new
                            {
                                id = child.GetInstanceID(),
                                name = child.name,
                                active = child.activeSelf
                            });
                        }
                    }
                    return JsonOk(new { children });
                });
            };

            // ----------------------------------------------------------------
            // transform.set_local_position -- 로컬 위치 설정 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["transform.set_local_position"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: transform.set_local_position <id> <x,y,z>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    try
                    {
                        go.transform.localPosition = ParseVector3(args[1]);
                        SceneManager.GetActiveScene().isDirty = true;
                        return JsonOk(new { ok = true });
                    }
                    catch (Exception ex)
                    {
                        return JsonError($"Failed to parse local position: {ex.Message}");
                    }
                });
            };

            // ----------------------------------------------------------------
            // prefab.create_variant -- Variant 프리팹 생성 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["prefab.create_variant"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: prefab.create_variant <baseGuid> <path>");

                var baseGuid = args[0];
                var path = args[1];
                return ExecuteOnMainThread(() =>
                {
                    try
                    {
                        var variantGuid = PrefabUtility.CreateVariant(baseGuid, path);
                        if (string.IsNullOrEmpty(variantGuid))
                            return JsonError($"Failed to create variant for base: {baseGuid}");

                        return JsonOk(new { created = true, path, guid = variantGuid });
                    }
                    catch (Exception ex)
                    {
                        return JsonError($"Failed to create variant: {ex.Message}");
                    }
                });
            };

            // ----------------------------------------------------------------
            // prefab.is_instance -- 프리팹 인스턴스 여부 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["prefab.is_instance"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: prefab.is_instance <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var isPrefab = PrefabUtility.IsPrefabInstance(go);
                    var guid = PrefabUtility.GetPrefabGuid(go);
                    return JsonOk(new { isPrefab, guid = guid ?? "" });
                });
            };

            // ----------------------------------------------------------------
            // prefab.unpack -- 프리팹 인스턴스 언팩 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["prefab.unpack"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: prefab.unpack <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    if (!PrefabUtility.IsPrefabInstance(go))
                        return JsonError($"GameObject is not a prefab instance: {id}");

                    PrefabUtility.UnpackPrefabInstance(go);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // asset.import -- 에셋 임포트/리임포트 트리거 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["asset.import"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: asset.import <path>");

                var path = args[0];
                return ExecuteOnMainThread(() =>
                {
                    if (!System.IO.File.Exists(path))
                        return JsonError($"File not found: {path}");

                    var db = Resources.GetAssetDatabase();
                    if (db == null)
                        return JsonError("AssetDatabase not initialized");

                    var projectPath = ProjectContext.AssetsPath;
                    if (!string.IsNullOrEmpty(projectPath))
                    {
                        db.ScanAssets(projectPath);
                        return JsonOk(new { ok = true });
                    }

                    return JsonError("Project assets path not configured");
                });
            };

            // ----------------------------------------------------------------
            // asset.scan -- 에셋 스캔 실행 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["asset.scan"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    var db = Resources.GetAssetDatabase();
                    if (db == null)
                        return JsonError("AssetDatabase not initialized");

                    var projectPath = args.Length > 0 ? args[0] : ProjectContext.AssetsPath;
                    if (string.IsNullOrEmpty(projectPath))
                        return JsonError("No assets path specified");

                    db.ScanAssets(projectPath);
                    return JsonOk(new { count = db.AssetCount });
                });
            };

            // ----------------------------------------------------------------
            // editor.screenshot -- 현재 화면 캡처 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["editor.screenshot"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    string path;
                    if (args.Length > 0)
                    {
                        path = args[0];
                    }
                    else
                    {
                        var dir = System.IO.Path.Combine(ProjectContext.ProjectRoot, "Screenshots");
                        System.IO.Directory.CreateDirectory(dir);
                        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                        path = System.IO.Path.Combine(dir, $"cli_screenshot_{timestamp}.png");
                    }

                    _pendingScreenshotPath = path;
                    return JsonOk(new { saved = true, path });
                });
            };

            // ----------------------------------------------------------------
            // editor.copy -- GO 복사 (클립보드, 메인 스레드)
            // ----------------------------------------------------------------
            _handlers["editor.copy"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: editor.copy <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    EditorSelection.Select(id);
                    EditorClipboard.CopyGameObjects(cut: false);
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // editor.paste -- 클립보드에서 붙여넣기 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["editor.paste"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    if (EditorClipboard.ClipboardKind != EditorClipboard.Kind.GameObjects)
                        return JsonError("No GameObject in clipboard");

                    EditorClipboard.PasteGameObjects();

                    var selected = EditorSelection.SelectedGameObject;
                    if (selected != null)
                        return JsonOk(new { id = selected.GetInstanceID(), name = selected.name });

                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // editor.select_all -- 모든 GO 선택 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["editor.select_all"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    var ids = new List<int>();
                    foreach (var go in SceneManager.AllGameObjects)
                    {
                        if (!go._isDestroyed)
                            ids.Add(go.GetInstanceID());
                    }
                    EditorSelection.SetSelection(ids);
                    return JsonOk(new { count = ids.Count });
                });
            };

            // ----------------------------------------------------------------
            // editor.undo_history -- Undo/Redo 스택 설명 조회 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["editor.undo_history"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    return JsonOk(new
                    {
                        undo = UndoSystem.UndoDescription ?? "",
                        redo = UndoSystem.RedoDescription ?? ""
                    });
                });
            };

            // ----------------------------------------------------------------
            // screen.info -- 화면 정보 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["screen.info"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    return JsonOk(new
                    {
                        width = Screen.width,
                        height = Screen.height,
                        dpi = Screen.dpi
                    });
                });
            };

            // ----------------------------------------------------------------
            // scene.clear -- 씬 내 모든 GO 삭제 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["scene.clear"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    SceneManager.Clear();
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // camera.set_clip -- 클리핑 설정 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["camera.set_clip"] = args =>
            {
                if (args.Length < 3)
                    return JsonError("Usage: camera.set_clip <id> <near> <far>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var near))
                    return JsonError($"Invalid near value: {args[1]}");

                if (!float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var far))
                    return JsonError($"Invalid far value: {args[2]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var cam = go.GetComponent<Camera>();
                    if (cam == null)
                        return JsonError($"No Camera component on GameObject: {id}");

                    cam.nearClipPlane = near;
                    cam.farClipPlane = far;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // light.set_type -- 라이트 타입 변경 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["light.set_type"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: light.set_type <id> <Directional|Point|Spot>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!Enum.TryParse<LightType>(args[1], ignoreCase: true, out var lightType))
                    return JsonError($"Unknown light type: {args[1]}. Valid: Directional, Point, Spot");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var light = go.GetComponent<Light>();
                    if (light == null)
                        return JsonError($"No Light component on GameObject: {id}");

                    light.type = lightType;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // light.set_range -- 라이트 범위 변경 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["light.set_range"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: light.set_range <id> <value>");

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

                    light.range = value;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // light.set_shadows -- 그림자 on/off (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["light.set_shadows"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: light.set_shadows <id> <true|false>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!bool.TryParse(args[1], out var enabled))
                    return JsonError($"Invalid bool value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var light = go.GetComponent<Light>();
                    if (light == null)
                        return JsonError($"No Light component on GameObject: {id}");

                    light.shadows = enabled;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // render.set_skybox_exposure -- 스카이박스 노출 변경 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["render.set_skybox_exposure"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: render.set_skybox_exposure <value>");

                if (!float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    return JsonError($"Invalid float value: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    RenderSettings.skyboxExposure = value;
                    if (RenderSettings.skybox != null)
                        RenderSettings.skybox.exposure = value;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
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

        /// <summary>Vector3를 "x, y, z" 문자열로 포맷한다.</summary>
        private static string FormatVector3(Vector3 v)
        {
            return $"{v.x.ToString(CultureInfo.InvariantCulture)}, {v.y.ToString(CultureInfo.InvariantCulture)}, {v.z.ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>
        /// typeName 문자열로부터 Component Type을 찾는다.
        /// 검색 순서: 1) RoseEngine 네임스페이스 (엔진 내장), 2) FrozenCode 어셈블리, 3) LiveCode 어셈블리.
        /// </summary>
        private static Type? ResolveComponentType(string typeName)
        {
            // 1. 엔진 내장 타입 (RoseEngine 네임스페이스)
            var engineAssembly = typeof(Component).Assembly;
            foreach (var type in engineAssembly.GetTypes())
            {
                if (type.Name == typeName && typeof(Component).IsAssignableFrom(type))
                    return type;
            }

            // 2. FrozenCode 어셈블리
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "FrozenCode")
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == typeName && typeof(Component).IsAssignableFrom(type))
                            return type;
                    }
                }
            }

            // 3. LiveCode 어셈블리 (collectible ALC)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name;
                if (asmName != null && asmName.Contains("LiveCode", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        foreach (var type in asm.GetTypes())
                        {
                            if (type.Name == typeName && typeof(Component).IsAssignableFrom(type))
                                return type;
                        }
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        // collectible ALC 해제 후 접근 시 발생 가능 -- 무시
                    }
                }
            }

            return null;
        }

        /// <summary>GO를 트리 노드로 변환 (재귀). 메인 스레드에서 호출.</summary>
        private static object BuildTreeNode(GameObject go)
        {
            var children = new List<object>();
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i).gameObject;
                if (!child._isDestroyed)
                    children.Add(BuildTreeNode(child));
            }
            return new
            {
                id = go.GetInstanceID(),
                name = go.name,
                active = go.activeSelf,
                children
            };
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

        /// <summary>머티리얼 변경 후 .mat 파일에 저장한다. 메인 스레드에서 호출.</summary>
        private static void SaveMaterialToDisk(Material mat)
        {
            var db = Resources.GetAssetDatabase();
            if (db == null) return;
            var guid = db.FindGuidForMaterial(mat);
            if (guid == null) return;
            var path = db.GetPathFromGuid(guid);
            if (path == null) return;
            MaterialImporter.WriteMaterial(path, mat);
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
