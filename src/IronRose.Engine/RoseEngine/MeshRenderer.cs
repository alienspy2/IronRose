// ------------------------------------------------------------
// @file    MeshRenderer.cs
// @brief   메시 렌더러 컴포넌트. 전역 레지스트리(_allRenderers) 에 등록되어
//          RenderSystem/SceneViewRenderer/AssetDatabase 가 매 프레임/리임포트 시 순회한다.
// @deps    RoseEngine/Component, RoseEngine/Material, RoseEngine/ComponentRegistry,
//          RoseEngine/ThreadGuard
// @exports
//   class MeshRenderer : Component
//     material: Material?                                    — 머티리얼
//     enabled: bool                                          — 렌더 활성화
//     _allRenderers: ComponentRegistry<MeshRenderer>         — 전역 레지스트리 (internal)
//     ClearAll(): void                                       — 씬 클리어용 (internal)
// @note    Register/Unregister 는 메인 스레드에서만 호출되어야 한다
//          (ThreadGuard.DebugCheckMainThread 로 Debug 빌드에서 검증).
// ------------------------------------------------------------
namespace RoseEngine
{
    public class MeshRenderer : Component
    {
        public Material? material { get; set; }
        public bool enabled { get; set; } = true;

        internal static readonly ComponentRegistry<MeshRenderer> _allRenderers = new();

        internal override void OnAddedToGameObject()
        {
            ThreadGuard.DebugCheckMainThread("MeshRenderer.Register");
            _allRenderers.Register(this);
        }

        internal override void OnComponentDestroy()
        {
            ThreadGuard.DebugCheckMainThread("MeshRenderer.Unregister");
            _allRenderers.Unregister(this);
        }

        internal static void ClearAll() => _allRenderers.Clear();
    }
}
