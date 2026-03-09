namespace RoseEngine
{
    public static class Time
    {
        public static float timeScale { get; set; } = 1f;
        public static float deltaTime { get; internal set; }
        public static float unscaledDeltaTime { get; internal set; }
        public static float time { get; internal set; }
        public static float fixedDeltaTime { get; internal set; } = PhysicsConstants.DefaultFixedDeltaTime;
        public static float fixedTime { get; internal set; }
        public static int frameCount { get; internal set; }

        /// <summary>deltaTime의 최대값 (Unity의 Time.maximumDeltaTime과 동일). 기본값 1/3초.</summary>
        public static float maximumDeltaTime { get; set; } = 1f / 3f;
    }
}
