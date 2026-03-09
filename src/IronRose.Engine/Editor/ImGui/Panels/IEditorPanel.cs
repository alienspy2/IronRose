namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    /// <summary>에디터 패널 공통 인터페이스 — 표시 여부 토글 + 그리기.</summary>
    public interface IEditorPanel
    {
        bool IsOpen { get; set; }
        void Draw();
    }
}
