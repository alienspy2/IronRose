using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    public static class EditorModal
    {
        public enum Result { None, Confirmed, Cancelled }

        // ── Alert queue ──
        private static readonly Queue<string> _alertQueue = new();
        private static bool _alertOpen;

        /// <summary>
        /// 알림 메시지를 큐에 추가한다. 다음 프레임부터 모달로 표시된다.
        /// </summary>
        public static void EnqueueAlert(string message) => _alertQueue.Enqueue(message);

        /// <summary>
        /// 매 프레임 호출. 큐에 알림이 있으면 모달 팝업으로 하나씩 표시한다.
        /// </summary>
        public static void DrawAlertPopups()
        {
            if (!_alertOpen && _alertQueue.Count > 0)
                _alertOpen = true;

            if (_alertOpen)
            {
                ImGui.OpenPopup("Alert##EditorModal");
                _alertOpen = false;
            }

            if (!ImGui.BeginPopupModal("Alert##EditorModal", ImGuiWindowFlags.AlwaysAutoResize))
                return;

            if (_alertQueue.Count > 0)
            {
                var msg = _alertQueue.Peek();
                int lineCount = 1;
                foreach (char c in msg) { if (c == '\n') lineCount++; }

                if (lineCount > 15)
                {
                    float scrollHeight = ImGui.GetTextLineHeightWithSpacing() * 15;
                    ImGui.BeginChild("AlertScroll##EditorModal", new Vector2(0, scrollHeight), ImGuiChildFlags.Border);
                    ImGui.Text(msg);
                    ImGui.EndChild();
                }
                else
                {
                    ImGui.Text(msg);
                }
                ImGui.Spacing();

                if (ImGui.Button("OK", new Vector2(120, 0)) || ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                {
                    _alertQueue.Dequeue();
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.EndPopup();
        }

        /// <summary>
        /// InputText가 포함된 표준 팝업 모달을 렌더링한다.
        /// Escape/Cancel/Enter/Confirm 버튼 동작과 자동 포커스를 일괄 처리.
        /// </summary>
        public static Result InputTextPopup(
            string popupId,
            string label,
            ref bool open,
            ref string buffer,
            string confirmLabel = "OK")
        {
            if (open)
            {
                ImGui.OpenPopup(popupId);
                open = false;
            }
            if (!ImGui.BeginPopupModal(popupId, ImGuiWindowFlags.AlwaysAutoResize))
                return Result.None;

            ImGui.Text(label);
            if (ImGui.IsWindowAppearing())
                ImGui.SetKeyboardFocusHere();

            bool enter = ImGui.InputText($"##{popupId}_input", ref buffer, 256,
                ImGuiInputTextFlags.EnterReturnsTrue);

            var result = Result.None;
            if (enter || ImGui.Button(confirmLabel))
                result = Result.Confirmed;

            ImGui.SameLine();
            if (ImGui.Button($"Cancel##{popupId}_cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape))
                result = Result.Cancelled;

            if (result != Result.None)
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
            return result;
        }
    }
}
