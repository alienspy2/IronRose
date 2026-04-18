// ------------------------------------------------------------
// @file    CliCommandDispatcher.UI.cs
// @brief   CLI UI 명령 핸들러. Canvas, RectTransform, UIText, UIImage, UIPanel, UIButton,
//          UIToggle, UISlider, UIInputField, UILayoutGroup, UIScrollView 관련 85개 명령을 등록한다.
//          편의 생성, 트리/조회, RectTransform 조작, 컴포넌트별 info/set, 디버깅, 정렬/분배,
//          테마 일괄 적용, 프리팹화 기능을 제공한다.
// @deps    CliCommandDispatcher (partial class), RoseEngine/Canvas, RoseEngine/RectTransform,
//          RoseEngine/UI/UIText, RoseEngine/UI/UIImage, RoseEngine/UI/UIPanel,
//          RoseEngine/UI/UIButton, RoseEngine/UI/UIToggle, RoseEngine/UI/UISlider,
//          RoseEngine/UI/UIInputField, RoseEngine/UI/UILayoutGroup, RoseEngine/UI/UIScrollView,
//          RoseEngine/CanvasRenderer, RoseEngine/Screen, RoseEngine/PrefabUtility,
//          RoseEngine/Resources, IronRose.AssetPipeline/AssetDatabase
// @exports (partial class -- no new public exports; all handlers registered via RegisterUIHandlers())
// @note    모든 UI 조작은 ExecuteOnMainThread() 내부에서 수행.
//          Vector2 형식: "x,y" (공백 없이). Color 형식: "r,g,b,a" (0~1 범위).
//          생성 명령 반환: { id, name }. 수정 명령 반환: { ok: true } 또는 관련 정보.
//          씬 변경 시 SceneManager.GetActiveScene().isDirty = true 설정.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using IronRose.AssetPipeline;
using RoseEngine;

namespace IronRose.Engine.Cli
{
    public partial class CliCommandDispatcher
    {
        private void RegisterUIHandlers()
        {
            // ================================================================
            // 편의 생성 명령 (10개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.create_canvas [name]
            // ----------------------------------------------------------------
            _handlers["ui.create_canvas"] = args =>
            {
                var name = args.Length > 0 ? args[0] : "Canvas";
                return ExecuteOnMainThread(() =>
                {
                    var go = new GameObject(name);
                    go.AddComponent<Canvas>();
                    // Canvas.OnAddedToGameObject()에서 RectTransform 자동 추가됨
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { id = go.GetInstanceID(), name = go.name });
                });
            };

            // ----------------------------------------------------------------
            // ui.create_text <parentId> [text] [fontSize]
            // ----------------------------------------------------------------
            _handlers["ui.create_text"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.create_text <parentId> [text] [fontSize]");

                if (!int.TryParse(args[0], out var parentId))
                    return JsonError($"Invalid parent ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var parent = FindGameObjectById(parentId);
                    if (parent == null)
                        return JsonError($"Parent not found: {parentId}");

                    var go = new GameObject("Text");
                    go.transform.SetParent(parent.transform);
                    go.AddComponent<RectTransform>();
                    var text = go.AddComponent<UIText>();
                    if (args.Length > 1) text.text = args[1];
                    if (args.Length > 2 && float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var fs))
                        text.fontSize = fs;

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { id = go.GetInstanceID(), name = go.name });
                });
            };

            // ----------------------------------------------------------------
            // ui.create_image <parentId>
            // ----------------------------------------------------------------
            _handlers["ui.create_image"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.create_image <parentId>");

                if (!int.TryParse(args[0], out var parentId))
                    return JsonError($"Invalid parent ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var parent = FindGameObjectById(parentId);
                    if (parent == null)
                        return JsonError($"Parent not found: {parentId}");

                    var go = new GameObject("Image");
                    go.transform.SetParent(parent.transform);
                    go.AddComponent<RectTransform>();
                    go.AddComponent<UIImage>();

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { id = go.GetInstanceID(), name = go.name });
                });
            };

            // ----------------------------------------------------------------
            // ui.create_panel <parentId> [r,g,b,a]
            // ----------------------------------------------------------------
            _handlers["ui.create_panel"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.create_panel <parentId> [r,g,b,a]");

                if (!int.TryParse(args[0], out var parentId))
                    return JsonError($"Invalid parent ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var parent = FindGameObjectById(parentId);
                    if (parent == null)
                        return JsonError($"Parent not found: {parentId}");

                    var go = new GameObject("Panel");
                    go.transform.SetParent(parent.transform);
                    go.AddComponent<RectTransform>();
                    var panel = go.AddComponent<UIPanel>();
                    if (args.Length > 1)
                        panel.color = ParseColor(args[1]);

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { id = go.GetInstanceID(), name = go.name });
                });
            };

            // ----------------------------------------------------------------
            // ui.create_button <parentId> [label]
            // ----------------------------------------------------------------
            _handlers["ui.create_button"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.create_button <parentId> [label]");

                if (!int.TryParse(args[0], out var parentId))
                    return JsonError($"Invalid parent ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var parent = FindGameObjectById(parentId);
                    if (parent == null)
                        return JsonError($"Parent not found: {parentId}");

                    // Button GO: UIButton + UIImage + RectTransform
                    var go = new GameObject("Button");
                    go.transform.SetParent(parent.transform);
                    go.AddComponent<RectTransform>();
                    go.AddComponent<UIImage>();
                    go.AddComponent<UIButton>();

                    // Label 자식: UIText + RectTransform (StretchAll)
                    var labelGo = new GameObject("Label");
                    labelGo.transform.SetParent(go.transform);
                    var labelRt = labelGo.AddComponent<RectTransform>();
                    labelRt.SetAnchorPreset(RectTransform.AnchorPreset.StretchAll);
                    labelRt.sizeDelta = Vector2.zero;
                    labelRt.anchoredPosition = Vector2.zero;
                    var labelText = labelGo.AddComponent<UIText>();
                    labelText.text = args.Length > 1 ? args[1] : "Button";
                    labelText.alignment = TextAnchor.MiddleCenter;

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { id = go.GetInstanceID(), name = go.name, labelId = labelGo.GetInstanceID() });
                });
            };

            // ----------------------------------------------------------------
            // ui.create_toggle <parentId>
            // ----------------------------------------------------------------
            _handlers["ui.create_toggle"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.create_toggle <parentId>");

                if (!int.TryParse(args[0], out var parentId))
                    return JsonError($"Invalid parent ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var parent = FindGameObjectById(parentId);
                    if (parent == null)
                        return JsonError($"Parent not found: {parentId}");

                    var go = new GameObject("Toggle");
                    go.transform.SetParent(parent.transform);
                    var rt = go.AddComponent<RectTransform>();
                    rt.sizeDelta = new Vector2(20f, 20f);
                    go.AddComponent<UIToggle>();

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { id = go.GetInstanceID(), name = go.name });
                });
            };

            // ----------------------------------------------------------------
            // ui.create_slider <parentId>
            // ----------------------------------------------------------------
            _handlers["ui.create_slider"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.create_slider <parentId>");

                if (!int.TryParse(args[0], out var parentId))
                    return JsonError($"Invalid parent ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var parent = FindGameObjectById(parentId);
                    if (parent == null)
                        return JsonError($"Parent not found: {parentId}");

                    var go = new GameObject("Slider");
                    go.transform.SetParent(parent.transform);
                    var rt = go.AddComponent<RectTransform>();
                    rt.sizeDelta = new Vector2(160f, 20f);
                    go.AddComponent<UISlider>();

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { id = go.GetInstanceID(), name = go.name });
                });
            };

            // ----------------------------------------------------------------
            // ui.create_input <parentId> [placeholder]
            // ----------------------------------------------------------------
            _handlers["ui.create_input"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.create_input <parentId> [placeholder]");

                if (!int.TryParse(args[0], out var parentId))
                    return JsonError($"Invalid parent ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var parent = FindGameObjectById(parentId);
                    if (parent == null)
                        return JsonError($"Parent not found: {parentId}");

                    var go = new GameObject("InputField");
                    go.transform.SetParent(parent.transform);
                    var rt = go.AddComponent<RectTransform>();
                    rt.sizeDelta = new Vector2(160f, 30f);
                    var input = go.AddComponent<UIInputField>();
                    if (args.Length > 1)
                        input.placeholder = args[1];

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { id = go.GetInstanceID(), name = go.name });
                });
            };

            // ----------------------------------------------------------------
            // ui.create_layout <parentId> [Horizontal|Vertical]
            // ----------------------------------------------------------------
            _handlers["ui.create_layout"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.create_layout <parentId> [Horizontal|Vertical]");

                if (!int.TryParse(args[0], out var parentId))
                    return JsonError($"Invalid parent ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var parent = FindGameObjectById(parentId);
                    if (parent == null)
                        return JsonError($"Parent not found: {parentId}");

                    var go = new GameObject("LayoutGroup");
                    go.transform.SetParent(parent.transform);
                    go.AddComponent<RectTransform>();
                    var layout = go.AddComponent<UILayoutGroup>();
                    if (args.Length > 1 && Enum.TryParse<LayoutDirection>(args[1], true, out var dir))
                        layout.direction = dir;

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { id = go.GetInstanceID(), name = go.name });
                });
            };

            // ----------------------------------------------------------------
            // ui.create_scroll <parentId>
            // ----------------------------------------------------------------
            _handlers["ui.create_scroll"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.create_scroll <parentId>");

                if (!int.TryParse(args[0], out var parentId))
                    return JsonError($"Invalid parent ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var parent = FindGameObjectById(parentId);
                    if (parent == null)
                        return JsonError($"Parent not found: {parentId}");

                    var go = new GameObject("ScrollView");
                    go.transform.SetParent(parent.transform);
                    go.AddComponent<RectTransform>();
                    go.AddComponent<UIScrollView>();

                    // Content 자식 GO
                    var contentGo = new GameObject("Content");
                    contentGo.transform.SetParent(go.transform);
                    var contentRt = contentGo.AddComponent<RectTransform>();
                    contentRt.SetAnchorPreset(RectTransform.AnchorPreset.TopStretch);
                    contentRt.sizeDelta = new Vector2(0f, 600f);
                    contentRt.anchoredPosition = Vector2.zero;

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { id = go.GetInstanceID(), name = go.name, contentId = contentGo.GetInstanceID() });
                });
            };

            // ================================================================
            // 트리/조회 명령 (4개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.tree [canvasId]
            // ----------------------------------------------------------------
            _handlers["ui.tree"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    var canvases = new List<object>();

                    if (args.Length > 0 && int.TryParse(args[0], out var canvasId))
                    {
                        var go = FindGameObjectById(canvasId);
                        if (go == null)
                            return JsonError($"GameObject not found: {canvasId}");
                        var canvas = go.GetComponent<Canvas>();
                        if (canvas == null)
                            return JsonError($"No Canvas component on GO: {canvasId}");

                        canvases.Add(new
                        {
                            id = go.GetInstanceID(),
                            name = go.name,
                            renderMode = canvas.renderMode.ToString(),
                            sortingOrder = canvas.sortingOrder,
                            tree = BuildUITreeNode(go)
                        });
                    }
                    else
                    {
                        var uiTreeSnap = Canvas._allCanvases.Snapshot();
                        foreach (var canvas in uiTreeSnap)
                        {
                            if (canvas._isDestroyed || !canvas.gameObject.activeInHierarchy) continue;
                            var go = canvas.gameObject;
                            canvases.Add(new
                            {
                                id = go.GetInstanceID(),
                                name = go.name,
                                renderMode = canvas.renderMode.ToString(),
                                sortingOrder = canvas.sortingOrder,
                                tree = BuildUITreeNode(go)
                            });
                        }
                    }

                    return JsonOk(new { canvases });
                });
            };

            // ----------------------------------------------------------------
            // ui.list [canvasId]
            // ----------------------------------------------------------------
            _handlers["ui.list"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    var elements = new List<object>();

                    if (args.Length > 0 && int.TryParse(args[0], out var canvasId))
                    {
                        var go = FindGameObjectById(canvasId);
                        if (go == null)
                            return JsonError($"GameObject not found: {canvasId}");
                        var canvas = go.GetComponent<Canvas>();
                        if (canvas == null)
                            return JsonError($"No Canvas component on GO: {canvasId}");

                        CollectUIElements(go, elements);
                    }
                    else
                    {
                        var uiListSnap = Canvas._allCanvases.Snapshot();
                        foreach (var canvas in uiListSnap)
                        {
                            if (canvas._isDestroyed || !canvas.gameObject.activeInHierarchy) continue;
                            CollectUIElements(canvas.gameObject, elements);
                        }
                    }

                    return JsonOk(new { elements, count = elements.Count });
                });
            };

            // ----------------------------------------------------------------
            // ui.find <name>
            // ----------------------------------------------------------------
            _handlers["ui.find"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.find <name>");

                var searchName = args[0];
                return ExecuteOnMainThread(() =>
                {
                    var matches = new List<object>();
                    foreach (var go in SceneManager.AllGameObjects)
                    {
                        if (go._isDestroyed) continue;
                        if (go.name != searchName) continue;

                        // UI 컴포넌트가 있는 GO만 반환
                        bool hasUI = false;
                        foreach (var comp in go.InternalComponents)
                        {
                            if (comp is IUIRenderable && !comp._isDestroyed)
                            {
                                hasUI = true;
                                break;
                            }
                        }
                        if (go.GetComponent<Canvas>() != null) hasUI = true;
                        if (go.GetComponent<RectTransform>() != null) hasUI = true;

                        if (hasUI)
                        {
                            matches.Add(new
                            {
                                id = go.GetInstanceID(),
                                name = go.name,
                                active = go.activeSelf
                            });
                        }
                    }
                    return JsonOk(new { gameObjects = matches, count = matches.Count });
                });
            };

            // ----------------------------------------------------------------
            // ui.canvas.list
            // ----------------------------------------------------------------
            _handlers["ui.canvas.list"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    var list = new List<object>();
                    var canvasListSnap = Canvas._allCanvases.Snapshot();
                    foreach (var canvas in canvasListSnap)
                    {
                        if (canvas._isDestroyed) continue;
                        var go = canvas.gameObject;
                        list.Add(new
                        {
                            id = go.GetInstanceID(),
                            name = go.name,
                            renderMode = canvas.renderMode.ToString(),
                            sortingOrder = canvas.sortingOrder
                        });
                    }
                    return JsonOk(new { canvases = list, count = list.Count });
                });
            };

            // ================================================================
            // RectTransform 명령 (8개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.rect.get <goId>
            // ----------------------------------------------------------------
            _handlers["ui.rect.get"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.rect.get <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var rt = go.GetComponent<RectTransform>();
                    if (rt == null)
                        return JsonError($"No RectTransform on GO: {id}");

                    var r = rt.rect;
                    var sr = rt.lastScreenRect;
                    return JsonOk(new
                    {
                        anchorMin = FormatVector2(rt.anchorMin),
                        anchorMax = FormatVector2(rt.anchorMax),
                        anchoredPosition = FormatVector2(rt.anchoredPosition),
                        sizeDelta = FormatVector2(rt.sizeDelta),
                        pivot = FormatVector2(rt.pivot),
                        offsetMin = FormatVector2(rt.offsetMin),
                        offsetMax = FormatVector2(rt.offsetMax),
                        rect = new { x = r.x, y = r.y, width = r.width, height = r.height },
                        lastScreenRect = new { x = sr.x, y = sr.y, width = sr.width, height = sr.height }
                    });
                });
            };

            // ----------------------------------------------------------------
            // ui.rect.set_anchors <goId> <minX,minY> <maxX,maxY>
            // ----------------------------------------------------------------
            _handlers["ui.rect.set_anchors"] = args =>
            {
                if (args.Length < 3)
                    return JsonError("Usage: ui.rect.set_anchors <goId> <minX,minY> <maxX,maxY>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var rt = go.GetComponent<RectTransform>();
                    if (rt == null)
                        return JsonError($"No RectTransform on GO: {id}");

                    rt.anchorMin = ParseVector2(args[1]);
                    rt.anchorMax = ParseVector2(args[2]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.rect.set_position <goId> <x,y>
            // ----------------------------------------------------------------
            _handlers["ui.rect.set_position"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.rect.set_position <goId> <x,y>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var rt = go.GetComponent<RectTransform>();
                    if (rt == null)
                        return JsonError($"No RectTransform on GO: {id}");

                    rt.anchoredPosition = ParseVector2(args[1]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.rect.set_size <goId> <w,h>
            // ----------------------------------------------------------------
            _handlers["ui.rect.set_size"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.rect.set_size <goId> <w,h>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var rt = go.GetComponent<RectTransform>();
                    if (rt == null)
                        return JsonError($"No RectTransform on GO: {id}");

                    rt.sizeDelta = ParseVector2(args[1]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.rect.set_pivot <goId> <x,y>
            // ----------------------------------------------------------------
            _handlers["ui.rect.set_pivot"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.rect.set_pivot <goId> <x,y>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var rt = go.GetComponent<RectTransform>();
                    if (rt == null)
                        return JsonError($"No RectTransform on GO: {id}");

                    rt.pivot = ParseVector2(args[1]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.rect.set_offsets <goId> <minX,minY> <maxX,maxY>
            // ----------------------------------------------------------------
            _handlers["ui.rect.set_offsets"] = args =>
            {
                if (args.Length < 3)
                    return JsonError("Usage: ui.rect.set_offsets <goId> <minX,minY> <maxX,maxY>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var rt = go.GetComponent<RectTransform>();
                    if (rt == null)
                        return JsonError($"No RectTransform on GO: {id}");

                    rt.offsetMin = ParseVector2(args[1]);
                    rt.offsetMax = ParseVector2(args[2]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.rect.set_preset <goId> <preset> [keepVisual]
            // ----------------------------------------------------------------
            _handlers["ui.rect.set_preset"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.rect.set_preset <goId> <preset> [keepVisual]");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!Enum.TryParse<RectTransform.AnchorPreset>(args[1], true, out var preset))
                    return JsonError($"Invalid AnchorPreset: {args[1]}. Valid: {string.Join(", ", Enum.GetNames<RectTransform.AnchorPreset>())}");

                bool keepVisual = args.Length > 2 && bool.TryParse(args[2], out var kv) && kv;

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var rt = go.GetComponent<RectTransform>();
                    if (rt == null)
                        return JsonError($"No RectTransform on GO: {id}");

                    if (keepVisual)
                        rt.SetAnchorPresetKeepVisual(preset, rt.GetParentSize());
                    else
                        rt.SetAnchorPreset(preset);

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.rect.get_world_rect <goId>
            // ----------------------------------------------------------------
            _handlers["ui.rect.get_world_rect"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.rect.get_world_rect <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var rt = go.GetComponent<RectTransform>();
                    if (rt == null)
                        return JsonError($"No RectTransform on GO: {id}");

                    var sr = rt.lastScreenRect;
                    return JsonOk(new { x = sr.x, y = sr.y, width = sr.width, height = sr.height });
                });
            };

            // ================================================================
            // Canvas 명령 (6개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.canvas.info <goId>
            // ----------------------------------------------------------------
            _handlers["ui.canvas.info"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.canvas.info <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var canvas = go.GetComponent<Canvas>();
                    if (canvas == null)
                        return JsonError($"No Canvas component on GO: {id}");

                    return JsonOk(new
                    {
                        renderMode = canvas.renderMode.ToString(),
                        sortingOrder = canvas.sortingOrder,
                        referenceResolution = FormatVector2(canvas.referenceResolution),
                        scaleMode = canvas.scaleMode.ToString(),
                        matchWidthOrHeight = canvas.matchWidthOrHeight
                    });
                });
            };

            // ----------------------------------------------------------------
            // ui.canvas.set_render_mode <goId> <mode>
            // ----------------------------------------------------------------
            _handlers["ui.canvas.set_render_mode"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.canvas.set_render_mode <goId> <mode>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!Enum.TryParse<CanvasRenderMode>(args[1], true, out var mode))
                    return JsonError($"Invalid CanvasRenderMode: {args[1]}. Valid: {string.Join(", ", Enum.GetNames<CanvasRenderMode>())}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var canvas = go.GetComponent<Canvas>();
                    if (canvas == null)
                        return JsonError($"No Canvas component on GO: {id}");

                    canvas.renderMode = mode;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.canvas.set_sorting_order <goId> <order>
            // ----------------------------------------------------------------
            _handlers["ui.canvas.set_sorting_order"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.canvas.set_sorting_order <goId> <order>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!int.TryParse(args[1], out var order))
                    return JsonError($"Invalid sorting order: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var canvas = go.GetComponent<Canvas>();
                    if (canvas == null)
                        return JsonError($"No Canvas component on GO: {id}");

                    canvas.sortingOrder = order;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.canvas.set_reference_resolution <goId> <w,h>
            // ----------------------------------------------------------------
            _handlers["ui.canvas.set_reference_resolution"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.canvas.set_reference_resolution <goId> <w,h>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var canvas = go.GetComponent<Canvas>();
                    if (canvas == null)
                        return JsonError($"No Canvas component on GO: {id}");

                    canvas.referenceResolution = ParseVector2(args[1]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.canvas.set_scale_mode <goId> <mode>
            // ----------------------------------------------------------------
            _handlers["ui.canvas.set_scale_mode"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.canvas.set_scale_mode <goId> <mode>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!Enum.TryParse<CanvasScaleMode>(args[1], true, out var mode))
                    return JsonError($"Invalid CanvasScaleMode: {args[1]}. Valid: {string.Join(", ", Enum.GetNames<CanvasScaleMode>())}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var canvas = go.GetComponent<Canvas>();
                    if (canvas == null)
                        return JsonError($"No Canvas component on GO: {id}");

                    canvas.scaleMode = mode;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.canvas.set_match <goId> <value>
            // ----------------------------------------------------------------
            _handlers["ui.canvas.set_match"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.canvas.set_match <goId> <value>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    return JsonError($"Invalid float value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var canvas = go.GetComponent<Canvas>();
                    if (canvas == null)
                        return JsonError($"No Canvas component on GO: {id}");

                    canvas.matchWidthOrHeight = value;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ================================================================
            // UIText 명령 (6개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.text.info <goId>
            // ----------------------------------------------------------------
            _handlers["ui.text.info"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.text.info <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var text = go.GetComponent<UIText>();
                    if (text == null)
                        return JsonError($"No UIText component on GO: {id}");

                    return JsonOk(new
                    {
                        text = text.text,
                        fontSize = text.fontSize,
                        color = FormatColor(text.color),
                        alignment = text.alignment.ToString(),
                        overflow = text.overflow.ToString(),
                        hasFont = text.font != null
                    });
                });
            };

            // ----------------------------------------------------------------
            // ui.text.set_text <goId> <text>
            // ----------------------------------------------------------------
            _handlers["ui.text.set_text"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.text.set_text <goId> <text>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var text = go.GetComponent<UIText>();
                    if (text == null)
                        return JsonError($"No UIText component on GO: {id}");

                    text.text = args[1];
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.text.set_font <goId> <fontGuid|fontPath>
            // ----------------------------------------------------------------
            _handlers["ui.text.set_font"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.text.set_font <goId> <fontGuid|fontPath>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                var assetRef = args[1];
                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var text = go.GetComponent<UIText>();
                    if (text == null)
                        return JsonError($"No UIText component on GO: {id}");

                    var font = ResolveFont(assetRef);
                    if (font == null)
                        return JsonError($"Font not found: {assetRef}");

                    text.font = font;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.text.set_font_size <goId> <size>
            // ----------------------------------------------------------------
            _handlers["ui.text.set_font_size"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.text.set_font_size <goId> <size>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
                    return JsonError($"Invalid font size: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var text = go.GetComponent<UIText>();
                    if (text == null)
                        return JsonError($"No UIText component on GO: {id}");

                    text.fontSize = size;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.text.set_color <goId> <r,g,b,a>
            // ----------------------------------------------------------------
            _handlers["ui.text.set_color"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.text.set_color <goId> <r,g,b,a>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var text = go.GetComponent<UIText>();
                    if (text == null)
                        return JsonError($"No UIText component on GO: {id}");

                    text.color = ParseColor(args[1]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.text.set_alignment <goId> <alignment>
            // ----------------------------------------------------------------
            _handlers["ui.text.set_alignment"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.text.set_alignment <goId> <alignment>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!Enum.TryParse<TextAnchor>(args[1], true, out var alignment))
                    return JsonError($"Invalid TextAnchor: {args[1]}. Valid: {string.Join(", ", Enum.GetNames<TextAnchor>())}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var text = go.GetComponent<UIText>();
                    if (text == null)
                        return JsonError($"No UIText component on GO: {id}");

                    text.alignment = alignment;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.text.set_overflow <goId> <overflow>
            // ----------------------------------------------------------------
            _handlers["ui.text.set_overflow"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.text.set_overflow <goId> <overflow>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!Enum.TryParse<TextOverflow>(args[1], true, out var overflow))
                    return JsonError($"Invalid TextOverflow: {args[1]}. Valid: {string.Join(", ", Enum.GetNames<TextOverflow>())}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var text = go.GetComponent<UIText>();
                    if (text == null)
                        return JsonError($"No UIText component on GO: {id}");

                    text.overflow = overflow;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ================================================================
            // UIImage 명령 (5개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.image.info <goId>
            // ----------------------------------------------------------------
            _handlers["ui.image.info"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.image.info <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var img = go.GetComponent<UIImage>();
                    if (img == null)
                        return JsonError($"No UIImage component on GO: {id}");

                    return JsonOk(new
                    {
                        color = FormatColor(img.color),
                        imageType = img.imageType.ToString(),
                        preserveAspect = img.preserveAspect,
                        hasSprite = img.sprite != null
                    });
                });
            };

            // ----------------------------------------------------------------
            // ui.image.set_color <goId> <r,g,b,a>
            // ----------------------------------------------------------------
            _handlers["ui.image.set_color"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.image.set_color <goId> <r,g,b,a>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var img = go.GetComponent<UIImage>();
                    if (img == null)
                        return JsonError($"No UIImage component on GO: {id}");

                    img.color = ParseColor(args[1]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.image.set_type <goId> <type>
            // ----------------------------------------------------------------
            _handlers["ui.image.set_type"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.image.set_type <goId> <type>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!Enum.TryParse<ImageType>(args[1], true, out var imgType))
                    return JsonError($"Invalid ImageType: {args[1]}. Valid: {string.Join(", ", Enum.GetNames<ImageType>())}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var img = go.GetComponent<UIImage>();
                    if (img == null)
                        return JsonError($"No UIImage component on GO: {id}");

                    img.imageType = imgType;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.image.set_sprite <goId> <spriteGuid|spritePath>
            // ----------------------------------------------------------------
            _handlers["ui.image.set_sprite"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.image.set_sprite <goId> <spriteGuid|spritePath>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                var assetRef = args[1];
                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var img = go.GetComponent<UIImage>();
                    if (img == null)
                        return JsonError($"No UIImage component on GO: {id}");

                    var sprite = ResolveSprite(assetRef);
                    if (sprite == null)
                        return JsonError($"Sprite not found: {assetRef}");

                    img.sprite = sprite;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.image.set_preserve_aspect <goId> <true|false>
            // ----------------------------------------------------------------
            _handlers["ui.image.set_preserve_aspect"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.image.set_preserve_aspect <goId> <true|false>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!bool.TryParse(args[1], out var preserve))
                    return JsonError($"Invalid bool value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var img = go.GetComponent<UIImage>();
                    if (img == null)
                        return JsonError($"No UIImage component on GO: {id}");

                    img.preserveAspect = preserve;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ================================================================
            // UIPanel 명령 (4개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.panel.info <goId>
            // ----------------------------------------------------------------
            _handlers["ui.panel.info"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.panel.info <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var panel = go.GetComponent<UIPanel>();
                    if (panel == null)
                        return JsonError($"No UIPanel component on GO: {id}");

                    return JsonOk(new
                    {
                        color = FormatColor(panel.color),
                        imageType = panel.imageType.ToString(),
                        hasSprite = panel.sprite != null
                    });
                });
            };

            // ----------------------------------------------------------------
            // ui.panel.set_color <goId> <r,g,b,a>
            // ----------------------------------------------------------------
            _handlers["ui.panel.set_color"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.panel.set_color <goId> <r,g,b,a>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var panel = go.GetComponent<UIPanel>();
                    if (panel == null)
                        return JsonError($"No UIPanel component on GO: {id}");

                    panel.color = ParseColor(args[1]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.panel.set_sprite <goId> <spriteGuid|spritePath>
            // ----------------------------------------------------------------
            _handlers["ui.panel.set_sprite"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.panel.set_sprite <goId> <spriteGuid|spritePath>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                var assetRef = args[1];
                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var panel = go.GetComponent<UIPanel>();
                    if (panel == null)
                        return JsonError($"No UIPanel component on GO: {id}");

                    var sprite = ResolveSprite(assetRef);
                    if (sprite == null)
                        return JsonError($"Sprite not found: {assetRef}");

                    panel.sprite = sprite;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.panel.set_type <goId> <type>
            // ----------------------------------------------------------------
            _handlers["ui.panel.set_type"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.panel.set_type <goId> <type>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!Enum.TryParse<ImageType>(args[1], true, out var imgType))
                    return JsonError($"Invalid ImageType: {args[1]}. Valid: {string.Join(", ", Enum.GetNames<ImageType>())}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var panel = go.GetComponent<UIPanel>();
                    if (panel == null)
                        return JsonError($"No UIPanel component on GO: {id}");

                    panel.imageType = imgType;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ================================================================
            // UIButton 명령 (4개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.button.info <goId>
            // ----------------------------------------------------------------
            _handlers["ui.button.info"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.button.info <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var btn = go.GetComponent<UIButton>();
                    if (btn == null)
                        return JsonError($"No UIButton component on GO: {id}");

                    return JsonOk(new
                    {
                        interactable = btn.interactable,
                        normalColor = FormatColor(btn.normalColor),
                        hoverColor = FormatColor(btn.hoverColor),
                        pressedColor = FormatColor(btn.pressedColor),
                        disabledColor = FormatColor(btn.disabledColor),
                        transition = btn.transition.ToString()
                    });
                });
            };

            // ----------------------------------------------------------------
            // ui.button.set_interactable <goId> <true|false>
            // ----------------------------------------------------------------
            _handlers["ui.button.set_interactable"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.button.set_interactable <goId> <true|false>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!bool.TryParse(args[1], out var interactable))
                    return JsonError($"Invalid bool value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var btn = go.GetComponent<UIButton>();
                    if (btn == null)
                        return JsonError($"No UIButton component on GO: {id}");

                    btn.interactable = interactable;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.button.set_colors <goId> <normal> <hover> <pressed> <disabled>
            // ----------------------------------------------------------------
            _handlers["ui.button.set_colors"] = args =>
            {
                if (args.Length < 5)
                    return JsonError("Usage: ui.button.set_colors <goId> <normal> <hover> <pressed> <disabled>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var btn = go.GetComponent<UIButton>();
                    if (btn == null)
                        return JsonError($"No UIButton component on GO: {id}");

                    btn.normalColor = ParseColor(args[1]);
                    btn.hoverColor = ParseColor(args[2]);
                    btn.pressedColor = ParseColor(args[3]);
                    btn.disabledColor = ParseColor(args[4]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.button.set_transition <goId> <transition>
            // ----------------------------------------------------------------
            _handlers["ui.button.set_transition"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.button.set_transition <goId> <transition>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!Enum.TryParse<ButtonTransition>(args[1], true, out var transition))
                    return JsonError($"Invalid ButtonTransition: {args[1]}. Valid: {string.Join(", ", Enum.GetNames<ButtonTransition>())}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var btn = go.GetComponent<UIButton>();
                    if (btn == null)
                        return JsonError($"No UIButton component on GO: {id}");

                    btn.transition = transition;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ================================================================
            // UIToggle 명령 (4개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.toggle.info <goId>
            // ----------------------------------------------------------------
            _handlers["ui.toggle.info"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.toggle.info <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var toggle = go.GetComponent<UIToggle>();
                    if (toggle == null)
                        return JsonError($"No UIToggle component on GO: {id}");

                    return JsonOk(new
                    {
                        isOn = toggle.isOn,
                        interactable = toggle.interactable,
                        backgroundColor = FormatColor(toggle.backgroundColor),
                        checkmarkColor = FormatColor(toggle.checkmarkColor)
                    });
                });
            };

            // ----------------------------------------------------------------
            // ui.toggle.set_on <goId> <true|false>
            // ----------------------------------------------------------------
            _handlers["ui.toggle.set_on"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.toggle.set_on <goId> <true|false>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!bool.TryParse(args[1], out var isOn))
                    return JsonError($"Invalid bool value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var toggle = go.GetComponent<UIToggle>();
                    if (toggle == null)
                        return JsonError($"No UIToggle component on GO: {id}");

                    toggle.isOn = isOn;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.toggle.set_interactable <goId> <true|false>
            // ----------------------------------------------------------------
            _handlers["ui.toggle.set_interactable"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.toggle.set_interactable <goId> <true|false>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!bool.TryParse(args[1], out var interactable))
                    return JsonError($"Invalid bool value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var toggle = go.GetComponent<UIToggle>();
                    if (toggle == null)
                        return JsonError($"No UIToggle component on GO: {id}");

                    toggle.interactable = interactable;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.toggle.set_colors <goId> <bgColor> <checkColor>
            // ----------------------------------------------------------------
            _handlers["ui.toggle.set_colors"] = args =>
            {
                if (args.Length < 3)
                    return JsonError("Usage: ui.toggle.set_colors <goId> <bgColor> <checkColor>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var toggle = go.GetComponent<UIToggle>();
                    if (toggle == null)
                        return JsonError($"No UIToggle component on GO: {id}");

                    toggle.backgroundColor = ParseColor(args[1]);
                    toggle.checkmarkColor = ParseColor(args[2]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ================================================================
            // UISlider 명령 (7개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.slider.info <goId>
            // ----------------------------------------------------------------
            _handlers["ui.slider.info"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.slider.info <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var slider = go.GetComponent<UISlider>();
                    if (slider == null)
                        return JsonError($"No UISlider component on GO: {id}");

                    return JsonOk(new
                    {
                        value = slider.value,
                        minValue = slider.minValue,
                        maxValue = slider.maxValue,
                        wholeNumbers = slider.wholeNumbers,
                        direction = slider.direction.ToString(),
                        interactable = slider.interactable,
                        backgroundColor = FormatColor(slider.backgroundColor),
                        fillColor = FormatColor(slider.fillColor),
                        handleColor = FormatColor(slider.handleColor)
                    });
                });
            };

            // ----------------------------------------------------------------
            // ui.slider.set_value <goId> <value>
            // ----------------------------------------------------------------
            _handlers["ui.slider.set_value"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.slider.set_value <goId> <value>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    return JsonError($"Invalid float value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var slider = go.GetComponent<UISlider>();
                    if (slider == null)
                        return JsonError($"No UISlider component on GO: {id}");

                    slider.value = value;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.slider.set_range <goId> <min> <max>
            // ----------------------------------------------------------------
            _handlers["ui.slider.set_range"] = args =>
            {
                if (args.Length < 3)
                    return JsonError("Usage: ui.slider.set_range <goId> <min> <max>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var min))
                    return JsonError($"Invalid min value: {args[1]}");

                if (!float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var max))
                    return JsonError($"Invalid max value: {args[2]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var slider = go.GetComponent<UISlider>();
                    if (slider == null)
                        return JsonError($"No UISlider component on GO: {id}");

                    slider.minValue = min;
                    slider.maxValue = max;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.slider.set_direction <goId> <direction>
            // ----------------------------------------------------------------
            _handlers["ui.slider.set_direction"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.slider.set_direction <goId> <direction>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!Enum.TryParse<SliderDirection>(args[1], true, out var dir))
                    return JsonError($"Invalid SliderDirection: {args[1]}. Valid: {string.Join(", ", Enum.GetNames<SliderDirection>())}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var slider = go.GetComponent<UISlider>();
                    if (slider == null)
                        return JsonError($"No UISlider component on GO: {id}");

                    slider.direction = dir;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.slider.set_whole_numbers <goId> <true|false>
            // ----------------------------------------------------------------
            _handlers["ui.slider.set_whole_numbers"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.slider.set_whole_numbers <goId> <true|false>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!bool.TryParse(args[1], out var wholeNumbers))
                    return JsonError($"Invalid bool value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var slider = go.GetComponent<UISlider>();
                    if (slider == null)
                        return JsonError($"No UISlider component on GO: {id}");

                    slider.wholeNumbers = wholeNumbers;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.slider.set_interactable <goId> <true|false>
            // ----------------------------------------------------------------
            _handlers["ui.slider.set_interactable"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.slider.set_interactable <goId> <true|false>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!bool.TryParse(args[1], out var interactable))
                    return JsonError($"Invalid bool value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var slider = go.GetComponent<UISlider>();
                    if (slider == null)
                        return JsonError($"No UISlider component on GO: {id}");

                    slider.interactable = interactable;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.slider.set_colors <goId> <bgColor> <fillColor> <handleColor>
            // ----------------------------------------------------------------
            _handlers["ui.slider.set_colors"] = args =>
            {
                if (args.Length < 4)
                    return JsonError("Usage: ui.slider.set_colors <goId> <bgColor> <fillColor> <handleColor>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var slider = go.GetComponent<UISlider>();
                    if (slider == null)
                        return JsonError($"No UISlider component on GO: {id}");

                    slider.backgroundColor = ParseColor(args[1]);
                    slider.fillColor = ParseColor(args[2]);
                    slider.handleColor = ParseColor(args[3]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ================================================================
            // UIInputField 명령 (8개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.input.info <goId>
            // ----------------------------------------------------------------
            _handlers["ui.input.info"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.input.info <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var input = go.GetComponent<UIInputField>();
                    if (input == null)
                        return JsonError($"No UIInputField component on GO: {id}");

                    return JsonOk(new
                    {
                        text = input.text,
                        placeholder = input.placeholder,
                        fontSize = input.fontSize,
                        maxLength = input.maxLength,
                        contentType = input.contentType.ToString(),
                        interactable = input.interactable,
                        readOnly = input.readOnly,
                        textColor = FormatColor(input.textColor),
                        placeholderColor = FormatColor(input.placeholderColor),
                        backgroundColor = FormatColor(input.backgroundColor)
                    });
                });
            };

            // ----------------------------------------------------------------
            // ui.input.set_text <goId> <text>
            // ----------------------------------------------------------------
            _handlers["ui.input.set_text"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.input.set_text <goId> <text>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var input = go.GetComponent<UIInputField>();
                    if (input == null)
                        return JsonError($"No UIInputField component on GO: {id}");

                    input.text = args[1];
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.input.set_placeholder <goId> <text>
            // ----------------------------------------------------------------
            _handlers["ui.input.set_placeholder"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.input.set_placeholder <goId> <text>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var input = go.GetComponent<UIInputField>();
                    if (input == null)
                        return JsonError($"No UIInputField component on GO: {id}");

                    input.placeholder = args[1];
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.input.set_font_size <goId> <size>
            // ----------------------------------------------------------------
            _handlers["ui.input.set_font_size"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.input.set_font_size <goId> <size>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
                    return JsonError($"Invalid font size: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var input = go.GetComponent<UIInputField>();
                    if (input == null)
                        return JsonError($"No UIInputField component on GO: {id}");

                    input.fontSize = size;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.input.set_max_length <goId> <length>
            // ----------------------------------------------------------------
            _handlers["ui.input.set_max_length"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.input.set_max_length <goId> <length>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!int.TryParse(args[1], out var maxLength))
                    return JsonError($"Invalid max length: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var input = go.GetComponent<UIInputField>();
                    if (input == null)
                        return JsonError($"No UIInputField component on GO: {id}");

                    input.maxLength = maxLength;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.input.set_content_type <goId> <type>
            // ----------------------------------------------------------------
            _handlers["ui.input.set_content_type"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.input.set_content_type <goId> <type>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!Enum.TryParse<InputFieldContentType>(args[1], true, out var ct))
                    return JsonError($"Invalid InputFieldContentType: {args[1]}. Valid: {string.Join(", ", Enum.GetNames<InputFieldContentType>())}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var input = go.GetComponent<UIInputField>();
                    if (input == null)
                        return JsonError($"No UIInputField component on GO: {id}");

                    input.contentType = ct;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.input.set_interactable <goId> <true|false>
            // ----------------------------------------------------------------
            _handlers["ui.input.set_interactable"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.input.set_interactable <goId> <true|false>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!bool.TryParse(args[1], out var interactable))
                    return JsonError($"Invalid bool value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var input = go.GetComponent<UIInputField>();
                    if (input == null)
                        return JsonError($"No UIInputField component on GO: {id}");

                    input.interactable = interactable;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.input.set_read_only <goId> <true|false>
            // ----------------------------------------------------------------
            _handlers["ui.input.set_read_only"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.input.set_read_only <goId> <true|false>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!bool.TryParse(args[1], out var readOnly))
                    return JsonError($"Invalid bool value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var input = go.GetComponent<UIInputField>();
                    if (input == null)
                        return JsonError($"No UIInputField component on GO: {id}");

                    input.readOnly = readOnly;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ================================================================
            // UILayoutGroup 명령 (6개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.layout.info <goId>
            // ----------------------------------------------------------------
            _handlers["ui.layout.info"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.layout.info <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var layout = go.GetComponent<UILayoutGroup>();
                    if (layout == null)
                        return JsonError($"No UILayoutGroup component on GO: {id}");

                    return JsonOk(new
                    {
                        direction = layout.direction.ToString(),
                        spacing = layout.spacing,
                        padding = FormatVector4(layout.padding),
                        childAlignment = layout.childAlignment.ToString(),
                        childForceExpandWidth = layout.childForceExpandWidth,
                        childForceExpandHeight = layout.childForceExpandHeight
                    });
                });
            };

            // ----------------------------------------------------------------
            // ui.layout.set_direction <goId> <Horizontal|Vertical>
            // ----------------------------------------------------------------
            _handlers["ui.layout.set_direction"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.layout.set_direction <goId> <Horizontal|Vertical>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!Enum.TryParse<LayoutDirection>(args[1], true, out var dir))
                    return JsonError($"Invalid LayoutDirection: {args[1]}. Valid: {string.Join(", ", Enum.GetNames<LayoutDirection>())}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var layout = go.GetComponent<UILayoutGroup>();
                    if (layout == null)
                        return JsonError($"No UILayoutGroup component on GO: {id}");

                    layout.direction = dir;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.layout.set_spacing <goId> <value>
            // ----------------------------------------------------------------
            _handlers["ui.layout.set_spacing"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.layout.set_spacing <goId> <value>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var spacing))
                    return JsonError($"Invalid spacing value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var layout = go.GetComponent<UILayoutGroup>();
                    if (layout == null)
                        return JsonError($"No UILayoutGroup component on GO: {id}");

                    layout.spacing = spacing;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.layout.set_padding <goId> <left,bottom,right,top>
            // ----------------------------------------------------------------
            _handlers["ui.layout.set_padding"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.layout.set_padding <goId> <left,bottom,right,top>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var layout = go.GetComponent<UILayoutGroup>();
                    if (layout == null)
                        return JsonError($"No UILayoutGroup component on GO: {id}");

                    layout.padding = ParseVector4(args[1]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.layout.set_child_alignment <goId> <alignment>
            // ----------------------------------------------------------------
            _handlers["ui.layout.set_child_alignment"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.layout.set_child_alignment <goId> <alignment>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!Enum.TryParse<LayoutChildAlignment>(args[1], true, out var alignment))
                    return JsonError($"Invalid LayoutChildAlignment: {args[1]}. Valid: {string.Join(", ", Enum.GetNames<LayoutChildAlignment>())}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var layout = go.GetComponent<UILayoutGroup>();
                    if (layout == null)
                        return JsonError($"No UILayoutGroup component on GO: {id}");

                    layout.childAlignment = alignment;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.layout.set_force_expand <goId> <width:true|false> <height:true|false>
            // ----------------------------------------------------------------
            _handlers["ui.layout.set_force_expand"] = args =>
            {
                if (args.Length < 3)
                    return JsonError("Usage: ui.layout.set_force_expand <goId> <width:true|false> <height:true|false>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!bool.TryParse(args[1], out var expandWidth))
                    return JsonError($"Invalid bool value for width: {args[1]}");

                if (!bool.TryParse(args[2], out var expandHeight))
                    return JsonError($"Invalid bool value for height: {args[2]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var layout = go.GetComponent<UILayoutGroup>();
                    if (layout == null)
                        return JsonError($"No UILayoutGroup component on GO: {id}");

                    layout.childForceExpandWidth = expandWidth;
                    layout.childForceExpandHeight = expandHeight;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ================================================================
            // UIScrollView 명령 (5개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.scroll.info <goId>
            // ----------------------------------------------------------------
            _handlers["ui.scroll.info"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.scroll.info <goId>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var scroll = go.GetComponent<UIScrollView>();
                    if (scroll == null)
                        return JsonError($"No UIScrollView component on GO: {id}");

                    return JsonOk(new
                    {
                        horizontal = scroll.horizontal,
                        vertical = scroll.vertical,
                        scrollPosition = FormatVector2(scroll.scrollPosition),
                        contentSize = FormatVector2(scroll.contentSize),
                        scrollSensitivity = scroll.scrollSensitivity,
                        scrollbarColor = FormatColor(scroll.scrollbarColor),
                        scrollbarHoverColor = FormatColor(scroll.scrollbarHoverColor),
                        scrollbarWidth = scroll.scrollbarWidth
                    });
                });
            };

            // ----------------------------------------------------------------
            // ui.scroll.set_scroll_position <goId> <x,y>
            // ----------------------------------------------------------------
            _handlers["ui.scroll.set_scroll_position"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.scroll.set_scroll_position <goId> <x,y>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var scroll = go.GetComponent<UIScrollView>();
                    if (scroll == null)
                        return JsonError($"No UIScrollView component on GO: {id}");

                    scroll.scrollPosition = ParseVector2(args[1]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.scroll.set_content_size <goId> <w,h>
            // ----------------------------------------------------------------
            _handlers["ui.scroll.set_content_size"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.scroll.set_content_size <goId> <w,h>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var scroll = go.GetComponent<UIScrollView>();
                    if (scroll == null)
                        return JsonError($"No UIScrollView component on GO: {id}");

                    scroll.contentSize = ParseVector2(args[1]);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.scroll.set_direction <goId> <horizontal:true|false> <vertical:true|false>
            // ----------------------------------------------------------------
            _handlers["ui.scroll.set_direction"] = args =>
            {
                if (args.Length < 3)
                    return JsonError("Usage: ui.scroll.set_direction <goId> <horizontal:true|false> <vertical:true|false>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!bool.TryParse(args[1], out var h))
                    return JsonError($"Invalid bool value for horizontal: {args[1]}");

                if (!bool.TryParse(args[2], out var v))
                    return JsonError($"Invalid bool value for vertical: {args[2]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var scroll = go.GetComponent<UIScrollView>();
                    if (scroll == null)
                        return JsonError($"No UIScrollView component on GO: {id}");

                    scroll.horizontal = h;
                    scroll.vertical = v;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ----------------------------------------------------------------
            // ui.scroll.set_sensitivity <goId> <value>
            // ----------------------------------------------------------------
            _handlers["ui.scroll.set_sensitivity"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.scroll.set_sensitivity <goId> <value>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var sensitivity))
                    return JsonError($"Invalid sensitivity value: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var scroll = go.GetComponent<UIScrollView>();
                    if (scroll == null)
                        return JsonError($"No UIScrollView component on GO: {id}");

                    scroll.scrollSensitivity = sensitivity;
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true });
                });
            };

            // ================================================================
            // 디버깅 명령 (3개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.debug.rects <true|false>
            // ----------------------------------------------------------------
            _handlers["ui.debug.rects"] = args =>
            {
                if (args.Length < 1)
                    return JsonError("Usage: ui.debug.rects <true|false>");

                if (!bool.TryParse(args[0], out var enabled))
                    return JsonError($"Invalid bool value: {args[0]}");

                return ExecuteOnMainThread(() =>
                {
                    CanvasRenderer.DebugDrawRects = enabled;
                    return JsonOk(new { debugDrawRects = enabled });
                });
            };

            // ----------------------------------------------------------------
            // ui.debug.overlap [canvasId]
            // ----------------------------------------------------------------
            _handlers["ui.debug.overlap"] = args =>
            {
                return ExecuteOnMainThread(() =>
                {
                    var rects = new List<(int id, string name, Rect sr)>();

                    if (args.Length > 0 && int.TryParse(args[0], out var canvasId))
                    {
                        var go = FindGameObjectById(canvasId);
                        if (go == null)
                            return JsonError($"GameObject not found: {canvasId}");
                        var canvas = go.GetComponent<Canvas>();
                        if (canvas == null)
                            return JsonError($"No Canvas component on GO: {canvasId}");

                        CollectRectsForOverlap(go, rects);
                    }
                    else
                    {
                        var overlapSnap = Canvas._allCanvases.Snapshot();
                        foreach (var canvas in overlapSnap)
                        {
                            if (canvas._isDestroyed || !canvas.gameObject.activeInHierarchy) continue;
                            CollectRectsForOverlap(canvas.gameObject, rects);
                        }
                    }

                    var overlaps = new List<object>();
                    for (int i = 0; i < rects.Count; i++)
                    {
                        for (int j = i + 1; j < rects.Count; j++)
                        {
                            var a = rects[i];
                            var b = rects[j];
                            if (RectsOverlap(a.sr, b.sr, out var intersection))
                            {
                                overlaps.Add(new
                                {
                                    a = new { id = a.id, name = a.name },
                                    b = new { id = b.id, name = b.name },
                                    intersection = new
                                    {
                                        x = intersection.x,
                                        y = intersection.y,
                                        width = intersection.width,
                                        height = intersection.height
                                    }
                                });
                            }
                        }
                    }

                    return JsonOk(new { overlaps, count = overlaps.Count });
                });
            };

            // ----------------------------------------------------------------
            // ui.debug.hit_test <screenX> <screenY>
            // ----------------------------------------------------------------
            _handlers["ui.debug.hit_test"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.debug.hit_test <screenX> <screenY>");

                if (!float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var screenX))
                    return JsonError($"Invalid screenX: {args[0]}");

                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var screenY))
                    return JsonError($"Invalid screenY: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var hit = CanvasRenderer.HitTest(screenX, screenY, 0, 0, Screen.width, Screen.height);
                    if (hit == null)
                        return JsonOk(new { hit = (object?)null });

                    return JsonOk(new
                    {
                        hit = new
                        {
                            id = hit.GetInstanceID(),
                            name = hit.name
                        }
                    });
                });
            };

            // ================================================================
            // 정렬/분배 명령 (2개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.align <edge> <goId1> <goId2> [goId3...]
            // ----------------------------------------------------------------
            _handlers["ui.align"] = args =>
            {
                if (args.Length < 3)
                    return JsonError("Usage: ui.align <edge> <goId1> <goId2> [goId3...] (edge: left, right, top, bottom, center_h, center_v)");

                var edge = args[0].ToLowerInvariant();
                if (edge != "left" && edge != "right" && edge != "top" && edge != "bottom" &&
                    edge != "center_h" && edge != "center_v")
                    return JsonError($"Invalid edge: {args[0]}. Valid: left, right, top, bottom, center_h, center_v");

                return ExecuteOnMainThread(() =>
                {
                    var rts = new List<RectTransform>();
                    for (int i = 1; i < args.Length; i++)
                    {
                        if (!int.TryParse(args[i], out var goId))
                            return JsonError($"Invalid GameObject ID: {args[i]}");

                        var go = FindGameObjectById(goId);
                        if (go == null)
                            return JsonError($"GameObject not found: {goId}");

                        var rt = go.GetComponent<RectTransform>();
                        if (rt == null)
                            return JsonError($"No RectTransform on GO: {goId}");

                        rts.Add(rt);
                    }

                    if (rts.Count < 2)
                        return JsonError("At least 2 GameObjects required");

                    // 기준: 첫 번째 GO
                    var refRt = rts[0];
                    var refPos = refRt.anchoredPosition;
                    var refSize = refRt.sizeDelta;

                    for (int i = 1; i < rts.Count; i++)
                    {
                        var rt = rts[i];
                        var pos = rt.anchoredPosition;

                        switch (edge)
                        {
                            case "left":
                                pos.x = refPos.x - refSize.x * refRt.pivot.x + rt.sizeDelta.x * rt.pivot.x;
                                break;
                            case "right":
                                pos.x = refPos.x + refSize.x * (1f - refRt.pivot.x) - rt.sizeDelta.x * (1f - rt.pivot.x);
                                break;
                            case "top":
                                pos.y = refPos.y - refSize.y * refRt.pivot.y + rt.sizeDelta.y * rt.pivot.y;
                                break;
                            case "bottom":
                                pos.y = refPos.y + refSize.y * (1f - refRt.pivot.y) - rt.sizeDelta.y * (1f - rt.pivot.y);
                                break;
                            case "center_h":
                                pos.x = refPos.x;
                                break;
                            case "center_v":
                                pos.y = refPos.y;
                                break;
                        }

                        rt.anchoredPosition = pos;
                    }

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true, aligned = rts.Count });
                });
            };

            // ----------------------------------------------------------------
            // ui.distribute <axis> <goId1> <goId2> [goId3...]
            // ----------------------------------------------------------------
            _handlers["ui.distribute"] = args =>
            {
                if (args.Length < 4)
                    return JsonError("Usage: ui.distribute <axis> <goId1> <goId2> <goId3> [goId4...] (axis: horizontal, vertical)");

                var axis = args[0].ToLowerInvariant();
                if (axis != "horizontal" && axis != "vertical")
                    return JsonError($"Invalid axis: {args[0]}. Valid: horizontal, vertical");

                return ExecuteOnMainThread(() =>
                {
                    var rts = new List<RectTransform>();
                    for (int i = 1; i < args.Length; i++)
                    {
                        if (!int.TryParse(args[i], out var goId))
                            return JsonError($"Invalid GameObject ID: {args[i]}");

                        var go = FindGameObjectById(goId);
                        if (go == null)
                            return JsonError($"GameObject not found: {goId}");

                        var rt = go.GetComponent<RectTransform>();
                        if (rt == null)
                            return JsonError($"No RectTransform on GO: {goId}");

                        rts.Add(rt);
                    }

                    if (rts.Count < 3)
                        return JsonError("At least 3 GameObjects required for distribute");

                    bool horizontal = axis == "horizontal";

                    // 첫 번째와 마지막 사이를 균등 분배
                    float startVal = horizontal ? rts[0].anchoredPosition.x : rts[0].anchoredPosition.y;
                    float endVal = horizontal ? rts[rts.Count - 1].anchoredPosition.x : rts[rts.Count - 1].anchoredPosition.y;

                    for (int i = 1; i < rts.Count - 1; i++)
                    {
                        float t = (float)i / (rts.Count - 1);
                        float val = startVal + (endVal - startVal) * t;
                        var pos = rts[i].anchoredPosition;

                        if (horizontal)
                            pos.x = val;
                        else
                            pos.y = val;

                        rts[i].anchoredPosition = pos;
                    }

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true, distributed = rts.Count });
                });
            };

            // ================================================================
            // 테마/일괄 적용 명령 (2개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.theme.apply_color <canvasId> <componentType> <field> <r,g,b,a>
            // ----------------------------------------------------------------
            _handlers["ui.theme.apply_color"] = args =>
            {
                if (args.Length < 4)
                    return JsonError("Usage: ui.theme.apply_color <canvasId> <componentType> <field> <r,g,b,a>");

                if (!int.TryParse(args[0], out var canvasId))
                    return JsonError($"Invalid canvas ID: {args[0]}");

                var componentType = args[1];
                var fieldName = args[2];

                return ExecuteOnMainThread(() =>
                {
                    var canvasGo = FindGameObjectById(canvasId);
                    if (canvasGo == null)
                        return JsonError($"GameObject not found: {canvasId}");

                    var canvas = canvasGo.GetComponent<Canvas>();
                    if (canvas == null)
                        return JsonError($"No Canvas component on GO: {canvasId}");

                    var color = ParseColor(args[3]);
                    int affected = 0;

                    ApplyThemeColorRecursive(canvasGo, componentType, fieldName, color, ref affected);

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true, affected });
                });
            };

            // ----------------------------------------------------------------
            // ui.theme.apply_font_size <canvasId> <size>
            // ----------------------------------------------------------------
            _handlers["ui.theme.apply_font_size"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.theme.apply_font_size <canvasId> <size>");

                if (!int.TryParse(args[0], out var canvasId))
                    return JsonError($"Invalid canvas ID: {args[0]}");

                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
                    return JsonError($"Invalid font size: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var canvasGo = FindGameObjectById(canvasId);
                    if (canvasGo == null)
                        return JsonError($"GameObject not found: {canvasId}");

                    var canvas = canvasGo.GetComponent<Canvas>();
                    if (canvas == null)
                        return JsonError($"No Canvas component on GO: {canvasId}");

                    int affected = 0;
                    ApplyFontSizeRecursive(canvasGo, size, ref affected);

                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { ok = true, affected });
                });
            };

            // ================================================================
            // 프리팹 명령 (2개)
            // ================================================================

            // ----------------------------------------------------------------
            // ui.prefab.save <goId> <path>
            // ----------------------------------------------------------------
            _handlers["ui.prefab.save"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.prefab.save <goId> <path>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                var path = ResolveProjectPath(args[1]);
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
                        return JsonError($"Failed to save UI prefab: {ex.Message}");
                    }
                });
            };

            // ----------------------------------------------------------------
            // ui.prefab.instantiate <guid> <parentId>
            // ----------------------------------------------------------------
            _handlers["ui.prefab.instantiate"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: ui.prefab.instantiate <guid> <parentId>");

                var guid = args[0];

                if (!int.TryParse(args[1], out var parentId))
                    return JsonError($"Invalid parent ID: {args[1]}");

                return ExecuteOnMainThread(() =>
                {
                    var parent = FindGameObjectById(parentId);
                    if (parent == null)
                        return JsonError($"Parent not found: {parentId}");

                    var go = PrefabUtility.InstantiatePrefab(guid);
                    if (go == null)
                        return JsonError($"Failed to instantiate prefab: guid={guid}");

                    go.transform.SetParent(parent.transform);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { id = go.GetInstanceID(), name = go.name });
                });
            };
        }

        // ================================================================
        // UI 헬퍼 메서드
        // ================================================================

        /// <summary>"x,y" 형식의 문자열을 Vector2로 파싱한다.</summary>
        private static Vector2 ParseVector2(string raw)
        {
            var cleaned = raw.Trim('(', ')', ' ');
            var parts = cleaned.Split(',');
            return new Vector2(
                float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture));
        }

        /// <summary>Vector2를 "x, y" 형식으로 포맷한다.</summary>
        private static string FormatVector2(Vector2 v)
        {
            return $"{v.x.ToString(CultureInfo.InvariantCulture)}, {v.y.ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>"x,y,z,w" 형식을 Vector4로 파싱한다.</summary>
        private static Vector4 ParseVector4(string raw)
        {
            var cleaned = raw.Trim('(', ')', ' ');
            var parts = cleaned.Split(',');
            return new Vector4(
                float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[3].Trim(), CultureInfo.InvariantCulture));
        }

        /// <summary>Vector4를 "x, y, z, w" 형식으로 포맷한다.</summary>
        private static string FormatVector4(Vector4 v)
        {
            return $"{v.x.ToString(CultureInfo.InvariantCulture)}, {v.y.ToString(CultureInfo.InvariantCulture)}, {v.z.ToString(CultureInfo.InvariantCulture)}, {v.w.ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>GO의 조상 Canvas를 찾는다. Canvas가 없으면 null.</summary>
        private static Canvas? FindParentCanvas(GameObject go)
        {
            var current = go.transform;
            while (current != null)
            {
                var canvas = current.gameObject.GetComponent<Canvas>();
                if (canvas != null) return canvas;
                current = current.parent;
            }
            return null;
        }

        /// <summary>Canvas 하위의 UI 트리를 재귀적으로 구축한다. RectTransform 정보 포함.</summary>
        private static object BuildUITreeNode(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            var children = new List<object>();
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i).gameObject;
                if (!child._isDestroyed)
                    children.Add(BuildUITreeNode(child));
            }

            // UI 컴포넌트 타입 수집
            var uiTypes = new List<string>();
            foreach (var comp in go.InternalComponents)
            {
                if (comp is IUIRenderable && !comp._isDestroyed)
                    uiTypes.Add(comp.GetType().Name);
            }
            if (go.GetComponent<Canvas>() != null) uiTypes.Add("Canvas");
            if (go.GetComponent<UILayoutGroup>() != null) uiTypes.Add("UILayoutGroup");

            return new
            {
                id = go.GetInstanceID(),
                name = go.name,
                active = go.activeSelf,
                rect = rt != null ? new
                {
                    anchoredPosition = FormatVector2(rt.anchoredPosition),
                    sizeDelta = FormatVector2(rt.sizeDelta),
                    anchorMin = FormatVector2(rt.anchorMin),
                    anchorMax = FormatVector2(rt.anchorMax),
                    pivot = FormatVector2(rt.pivot)
                } : null,
                uiComponents = uiTypes,
                children
            };
        }

        /// <summary>GO 서브트리의 UI 요소를 flat 목록으로 수집한다.</summary>
        private static void CollectUIElements(GameObject go, List<object> elements)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                var uiTypes = new List<string>();
                foreach (var comp in go.InternalComponents)
                {
                    if (comp is IUIRenderable && !comp._isDestroyed)
                        uiTypes.Add(comp.GetType().Name);
                }
                if (go.GetComponent<Canvas>() != null) uiTypes.Add("Canvas");
                if (go.GetComponent<UILayoutGroup>() != null) uiTypes.Add("UILayoutGroup");

                elements.Add(new
                {
                    id = go.GetInstanceID(),
                    name = go.name,
                    active = go.activeSelf,
                    uiComponents = uiTypes
                });
            }

            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i).gameObject;
                if (!child._isDestroyed)
                    CollectUIElements(child, elements);
            }
        }

        /// <summary>폰트를 GUID 또는 에셋 경로로 해석한다.</summary>
        private static Font? ResolveFont(string assetRef)
        {
            var db = Resources.GetAssetDatabase();
            if (db == null) return null;

            // GUID로 시도
            var path = db.GetPathFromGuid(assetRef);
            if (path != null)
                return db.LoadByGuid<Font>(assetRef);

            // 경로로 시도
            return db.Load<Font>(assetRef);
        }

        /// <summary>스프라이트를 GUID 또는 에셋 경로로 해석한다.</summary>
        private static Sprite? ResolveSprite(string assetRef)
        {
            var db = Resources.GetAssetDatabase();
            if (db == null) return null;

            // GUID로 시도
            var path = db.GetPathFromGuid(assetRef);
            if (path != null)
                return db.LoadByGuid<Sprite>(assetRef);

            // 경로로 시도
            return db.Load<Sprite>(assetRef);
        }

        /// <summary>오버랩 검출을 위해 GO 서브트리의 lastScreenRect를 수집한다.</summary>
        private static void CollectRectsForOverlap(GameObject go, List<(int id, string name, Rect sr)> rects)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                // IUIRenderable가 있는 GO만 오버랩 검사 대상
                bool hasUI = false;
                foreach (var comp in go.InternalComponents)
                {
                    if (comp is IUIRenderable && !comp._isDestroyed)
                    {
                        hasUI = true;
                        break;
                    }
                }

                if (hasUI)
                {
                    var sr = rt.lastScreenRect;
                    if (sr.width > 0 && sr.height > 0)
                        rects.Add((go.GetInstanceID(), go.name, sr));
                }
            }

            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i).gameObject;
                if (!child._isDestroyed)
                    CollectRectsForOverlap(child, rects);
            }
        }

        /// <summary>두 Rect의 AABB 겹침 여부를 판정하고 교차 영역을 반환한다.</summary>
        private static bool RectsOverlap(Rect a, Rect b, out Rect intersection)
        {
            float x1 = Math.Max(a.x, b.x);
            float y1 = Math.Max(a.y, b.y);
            float x2 = Math.Min(a.xMax, b.xMax);
            float y2 = Math.Min(a.yMax, b.yMax);

            if (x1 < x2 && y1 < y2)
            {
                intersection = new Rect(x1, y1, x2 - x1, y2 - y1);
                return true;
            }

            intersection = default;
            return false;
        }

        /// <summary>Canvas 하위의 지정 컴포넌트 타입의 Color 필드를 재귀적으로 일괄 변경한다.</summary>
        private static void ApplyThemeColorRecursive(GameObject go, string componentType, string fieldName, Color color, ref int affected)
        {
            foreach (var comp in go.InternalComponents)
            {
                if (comp._isDestroyed) continue;
                if (comp.GetType().Name != componentType) continue;

                var field = comp.GetType().GetField(fieldName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (field != null && field.FieldType == typeof(Color))
                {
                    field.SetValue(comp, color);
                    affected++;
                }
            }

            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i).gameObject;
                if (!child._isDestroyed)
                    ApplyThemeColorRecursive(child, componentType, fieldName, color, ref affected);
            }
        }

        /// <summary>Canvas 하위의 모든 UIText의 fontSize를 재귀적으로 일괄 변경한다.</summary>
        private static void ApplyFontSizeRecursive(GameObject go, float size, ref int affected)
        {
            var text = go.GetComponent<UIText>();
            if (text != null)
            {
                text.fontSize = size;
                affected++;
            }

            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i).gameObject;
                if (!child._isDestroyed)
                    ApplyFontSizeRecursive(child, size, ref affected);
            }
        }
    }
}
