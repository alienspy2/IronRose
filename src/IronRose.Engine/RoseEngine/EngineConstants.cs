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

    /// <summary>엔진 디렉터리 경로 상수.</summary>
    public static class EngineDirectories
    {
        public const string CachePath = "RoseCache";
        public const string LiveCodePath = "LiveCode";
        public const string FrozenCodePath = "FrozenCode";
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
