namespace RoseEngine
{
    /// <summary>
    /// 프리팹 인스턴스에 자동 부착.
    /// 원본 프리팹 에셋과의 연결을 유지.
    /// Transform(position/rotation/scale)만 씬에서 다를 수 있음.
    /// 그 외 프로퍼티 변경은 Prefab Variant를 통해서만 가능.
    /// </summary>
    [DisallowMultipleComponent]
    public class PrefabInstance : Component
    {
        /// <summary>프리팹 에셋 GUID (AssetDatabase 참조)</summary>
        [SerializeField]
        public string prefabGuid = "";
    }
}
