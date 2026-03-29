// ------------------------------------------------------------
// @file    EngineConstants.cs
// @brief   엔진 전역에서 사용하는 상수 모음. 물리, 수학, 디렉터리 폴더명, 마우스 버튼 인덱스 상수를 정의한다.
// @deps    (없음)
// @exports
//   static class PhysicsConstants
//     DefaultFixedDeltaTime: float  — 기본 FixedUpdate 틱 레이트 (50Hz, 0.02초)
//   static class MathConstants
//     NormalizeEpsilon: float       — 벡터 정규화 시 0-division 방지 epsilon
//   static class EngineDirectories
//     CachePath: string             — 캐시 폴더명 ("RoseCache")
//     ScriptsPath: string           — 스크립트 폴더명 ("Scripts")
//   static class MouseButtonIndex
//     Left/Right/Middle/Count: int  — 마우스 버튼 인덱스
// @note    EngineDirectories는 폴더명 상수만 제공한다.
//          전체 경로 조합은 ProjectContext(또는 호출측)에서 담당한다.
// ------------------------------------------------------------
namespace RoseEngine
{
    /// <summary>물리 시뮬레이션 상수.</summary>
    public static class PhysicsConstants
    {
        /// <summary>기본 FixedUpdate 틱 레이트 (50Hz → 0.02초).</summary>
        public const float DefaultFixedDeltaTime = 1f / 50f;
    }

    /// <summary>수학 관련 상수 (Mathf 외).</summary>
    public static class MathConstants
    {
        /// <summary>벡터 정규화 시 0-division 방지 epsilon.</summary>
        public const float NormalizeEpsilon = 1e-5f;
    }

    /// <summary>
    /// 엔진 디렉터리 폴더명 상수.
    /// 전체 경로 조합은 ProjectContext(또는 호출측)에서 담당하며,
    /// 이 클래스는 폴더명 문자열만 제공한다.
    /// </summary>
    public static class EngineDirectories
    {
        /// <summary>캐시 폴더명.</summary>
        public const string CachePath = "RoseCache";

        /// <summary>스크립트 폴더명.</summary>
        public const string ScriptsPath = "Scripts";
    }

    /// <summary>마우스 버튼 인덱스 상수.</summary>
    public static class MouseButtonIndex
    {
        public const int Left = 0;
        public const int Right = 1;
        public const int Middle = 2;
        public const int Count = 3;
    }
}
