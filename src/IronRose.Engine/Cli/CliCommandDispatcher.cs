// ------------------------------------------------------------
// @file    CliCommandDispatcher.cs
// @brief   CLI 요청 평문을 파싱하여 적절한 핸들러를 호출하고 JSON 응답을 반환한다.
//          메인 스레드 실행이 필요한 명령은 큐에 넣고 결과를 대기한다.
// @deps    System.Text.Json, RoseEngine/SceneManager, IronRose.Engine/ProjectContext,
//          IronRose.Engine.Editor/EditorPlayMode, IronRose.Engine.Editor/EditorSelection,
//          IronRose.Engine.Editor/SceneSerializer, IronRose.Engine.Editor/GameObjectSnapshot,
//          IronRose.Engine.Editor/UndoSystem, IronRose.Engine.Cli/CliLogBuffer
// @exports
//   class CliCommandDispatcher
//     Dispatch(string requestLine): string  -- 요청 처리 후 응답 JSON 반환
//     ProcessMainThreadQueue(): void        -- 메인 스레드에서 호출하여 대기 중 명령 실행
// @note    백그라운드 스레드(Pipe)에서 Dispatch()가 호출된다.
//          메인 스레드 접근이 필요한 명령은 _mainThreadQueue에 넣고
//          ManualResetEventSlim으로 완료를 대기한다 (타임아웃 5초).
//          지원 명령: ping, scene.info, scene.list, scene.save, scene.load,
//          go.get, go.find, go.set_active, go.set_field,
//          go.create, go.create_primitive, go.destroy, go.rename, go.duplicate,
//          transform.get, transform.set_position, transform.set_rotation,
//          transform.set_scale, transform.set_parent,
//          component.add, component.remove, component.list,
//          editor.undo, editor.redo,
//          select, play.enter, play.stop, play.pause, play.resume, play.state,
//          log.recent
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
using IronRose.Scripting;
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
            // Wave 1: 핵심 명령 세트 (go CRUD, transform, component, undo/redo)
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
                // LiveCode는 AssemblyLoadContext로 로드되며, 이름이 "LiveCode"이거나
                // 동적으로 생성된 이름을 가질 수 있다.
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
