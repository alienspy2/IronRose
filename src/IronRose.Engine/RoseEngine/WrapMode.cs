namespace RoseEngine
{
    /// <summary>
    /// 애니메이션 재생 종료 시 동작 모드.
    /// </summary>
    public enum WrapMode
    {
        /// <summary>한 번 재생 후 정지 (마지막 프레임 유지).</summary>
        Once,

        /// <summary>끝까지 재생 후 처음부터 반복.</summary>
        Loop,

        /// <summary>끝까지 재생 → 역재생 → 반복.</summary>
        PingPong,

        /// <summary>한 번 재생 후 마지막 프레임에 고정 (Once와 동일하나 의미 구분용).</summary>
        ClampForever,
    }
}
