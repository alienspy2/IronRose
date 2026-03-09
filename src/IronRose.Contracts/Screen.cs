using System;

namespace IronRose.API
{
    /// <summary>
    /// 화면/렌더링 관련 플러그인 API.
    /// LiveCode 스크립트에서 호출 가능.
    /// </summary>
    public static class Screen
    {
        public static Action<float, float, float>? SetClearColorImpl;

        public static void SetClearColor(float r, float g, float b)
        {
            SetClearColorImpl?.Invoke(r, g, b);
        }
    }
}
