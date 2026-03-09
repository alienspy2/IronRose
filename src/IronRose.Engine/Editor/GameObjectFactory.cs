using RoseEngine;

namespace IronRose.Engine.Editor
{
    public enum CreateGameObjectType
    {
        Empty,
        Camera,
        DirectionalLight,
        PointLight,
        SpotLight,
        Cube,
        Sphere,
        Capsule,
        Cylinder,
        Plane,
        Quad,
        // UI
        UICanvas,
        UIPanel,
        UIText,
        UIImage,
        UIButton,
        UISlider,
        UIToggle,
        UIScrollView,
        UIInputField,
    }

    /// <summary>
    /// 컨텍스트 메뉴에서 새 GameObject를 생성하는 팩토리.
    /// CreateGameObjectAction.Redo()에서도 재사용.
    /// </summary>
    public static class GameObjectFactory
    {
        public static GameObject? Create(CreateGameObjectType type, int? parentId)
        {
            GameObject go;
            switch (type)
            {
                case CreateGameObjectType.Empty:
                    go = new GameObject("GameObject");
                    break;

                case CreateGameObjectType.Camera:
                    go = new GameObject("Camera");
                    go.AddComponent<Camera>();
                    break;

                case CreateGameObjectType.DirectionalLight:
                    go = new GameObject("Directional Light");
                    var dirLight = go.AddComponent<Light>();
                    dirLight.type = LightType.Directional;
                    break;

                case CreateGameObjectType.PointLight:
                    go = new GameObject("Point Light");
                    var pointLight = go.AddComponent<Light>();
                    pointLight.type = LightType.Point;
                    pointLight.range = 10f;
                    break;

                case CreateGameObjectType.SpotLight:
                    go = new GameObject("Spot Light");
                    var spotLight = go.AddComponent<Light>();
                    spotLight.type = LightType.Spot;
                    spotLight.range = 10f;
                    spotLight.spotAngle = 30f;
                    spotLight.spotOuterAngle = 45f;
                    break;

                case CreateGameObjectType.Cube:
                    go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    break;
                case CreateGameObjectType.Sphere:
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    break;
                case CreateGameObjectType.Capsule:
                    go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    break;
                case CreateGameObjectType.Cylinder:
                    go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    break;
                case CreateGameObjectType.Plane:
                    go = GameObject.CreatePrimitive(PrimitiveType.Plane);
                    break;
                case CreateGameObjectType.Quad:
                    go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    break;

                // ── UI ──
                case CreateGameObjectType.UICanvas:
                    go = new GameObject("Canvas");
                    go.AddComponent<Canvas>();
                    // Canvas.OnAddedToGameObject이 RectTransform을 자동 추가
                    break;

                case CreateGameObjectType.UIPanel:
                    go = CreateUIGameObject("Panel");
                    go.AddComponent<UIPanel>();
                    break;

                case CreateGameObjectType.UIText:
                    go = CreateUIGameObject("Text");
                    var text = go.AddComponent<UIText>();
                    text.text = "New Text";
                    break;

                case CreateGameObjectType.UIImage:
                    go = CreateUIGameObject("Image");
                    go.AddComponent<UIImage>();
                    break;

                case CreateGameObjectType.UIButton:
                    go = CreateUIGameObject("Button");
                    go.AddComponent<UIPanel>();
                    go.AddComponent<UIButton>();
                    // 자식 텍스트
                    var btnLabel = CreateUIGameObject("Text");
                    btnLabel.transform.SetParent(go.transform);
                    var btnText = btnLabel.AddComponent<UIText>();
                    btnText.text = "Button";
                    btnText.alignment = TextAnchor.MiddleCenter;
                    break;

                case CreateGameObjectType.UISlider:
                    go = CreateUIGameObject("Slider");
                    go.AddComponent<UISlider>();
                    var sliderRt = go.GetComponent<RectTransform>();
                    sliderRt!.sizeDelta = new Vector2(200, 30);
                    break;

                case CreateGameObjectType.UIToggle:
                    go = CreateUIGameObject("Toggle");
                    go.AddComponent<UIToggle>();
                    var toggleRt = go.GetComponent<RectTransform>();
                    toggleRt!.sizeDelta = new Vector2(24, 24);
                    break;

                case CreateGameObjectType.UIScrollView:
                    go = CreateUIGameObject("Scroll View");
                    go.AddComponent<UIPanel>();
                    go.AddComponent<UIScrollView>();
                    break;

                case CreateGameObjectType.UIInputField:
                    go = CreateUIGameObject("InputField");
                    var inputField = go.AddComponent<UIInputField>();
                    inputField.placeholder = "Enter text...";
                    var inputRt = go.GetComponent<RectTransform>();
                    inputRt!.sizeDelta = new Vector2(200, 30);
                    break;

                default:
                    return null;
            }

            if (parentId.HasValue)
            {
                var parent = UndoUtility.FindGameObjectById(parentId.Value);
                if (parent != null)
                    go.transform.SetParent(parent.transform);
            }

            return go;
        }

        /// <summary>RectTransform이 포함된 UI용 GameObject 생성.</summary>
        private static GameObject CreateUIGameObject(string name)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = new Vector2(1, 1);
            rt.sizeDelta = Vector2.zero;
            return go;
        }

        /// <summary>
        /// 생성 + Undo 기록 + 선택 + dirty 표시를 한 번에 수행.
        /// </summary>
        public static void CreateWithUndo(CreateGameObjectType type, int? parentId)
        {
            var go = Create(type, parentId);
            if (go == null) return;

            UndoSystem.Record(new CreateGameObjectAction(
                $"Create {go.name}", type, parentId, go.GetInstanceID()));

            EditorSelection.SelectGameObject(go);
            SceneManager.GetActiveScene().isDirty = true;
        }
    }
}
