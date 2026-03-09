namespace RoseEngine
{
    /// <summary>
    /// 씬 메타데이터. Unity의 Scene에 대응.
    /// 씬 파일 경로와 이름을 보유한다.
    /// </summary>
    public class Scene
    {
        /// <summary>씬 파일의 절대 경로 (.scene). 아직 저장 전이면 null.</summary>
        public string? path { get; set; }

        /// <summary>씬 이름 (파일명에서 확장자 제거).</summary>
        public string name { get; set; } = "Untitled";

        /// <summary>마지막 저장 이후 변경이 있으면 true.</summary>
        public bool isDirty { get; set; }
    }
}
