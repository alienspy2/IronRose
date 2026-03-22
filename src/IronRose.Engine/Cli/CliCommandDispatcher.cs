// ------------------------------------------------------------
// @file    CliCommandDispatcher.cs
// @brief   CLI 요청 평문을 파싱하여 적절한 핸들러를 호출하고 JSON 응답을 반환한다.
//          메인 스레드 실행이 필요한 명령은 큐에 넣고 결과를 대기한다.
// @deps    System.Text.Json, RoseEngine/SceneManager, RoseEngine/PrefabUtility,
//          IronRose.Engine/ProjectContext, IronRose.AssetPipeline/AssetDatabase,
//          IronRose.Engine.Editor/EditorPlayMode, IronRose.Engine.Editor/EditorSelection,
//          IronRose.Engine.Editor/SceneSerializer, IronRose.Engine.Editor/GameObjectSnapshot,
//          IronRose.Engine.Cli/CliLogBuffer
// @exports
//   class CliCommandDispatcher
//     Dispatch(string requestLine): string  -- 요청 처리 후 응답 JSON 반환
//     ProcessMainThreadQueue(): void        -- 메인 스레드에서 호출하여 대기 중 명령 실행
// @note    백그라운드 스레드(Pipe)에서 Dispatch()가 호출된다.
//          메인 스레드 접근이 필요한 명령은 _mainThreadQueue에 넣고
//          ManualResetEventSlim으로 완료를 대기한다 (타임아웃 5초).
//          지원 명령:
//          [Wave 1] ping, scene.info, scene.list, scene.save, scene.load,
//          go.get, go.find, go.set_active, go.set_field,
//          select, play.enter, play.stop, play.pause, play.resume, play.state,
//          log.recent
//          [Wave 2] prefab.instantiate, prefab.save,
//          asset.list, asset.find, asset.guid, asset.path,
//          scene.tree, scene.new
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
            // Wave 2 -- 에셋/프리팹/씬 확장 명령
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
