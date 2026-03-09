using System;

namespace IronRose.API
{
    /// <summary>
    /// 에디터 씬 관련 플러그인 API.
    /// New Scene 시 기본 오브젝트 세트 생성 등.
    /// </summary>
    public static class EditorScene
    {
        public static Action? CreateDefaultSceneImpl;

        public static void CreateDefaultScene()
        {
            CreateDefaultSceneImpl?.Invoke();
        }
    }
}
