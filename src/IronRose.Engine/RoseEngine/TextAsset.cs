namespace RoseEngine
{
    /// <summary>
    /// 텍스트 파일(.txt, .json, .xml, .csv 등)을 에셋으로 로드하는 클래스.
    /// Unity의 TextAsset과 동일한 역할.
    /// </summary>
    public class TextAsset
    {
        public string name = "";
        public string text = "";
        public byte[]? bytes;

        public override string ToString() => text;
    }
}
