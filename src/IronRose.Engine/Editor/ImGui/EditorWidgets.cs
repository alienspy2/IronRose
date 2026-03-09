using System.Collections.Generic;
using ImGuiNET;
using RoseEngine;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// Editor 패널 전체에서 공유하는 ImGui 위젯 헬퍼.
    /// 싱글클릭 텍스트 편집, Slider+Input 콤보, Color 변환 등을 일괄 처리.
    /// Unity Inspector 스타일: 라벨 왼쪽 고정폭, 값 위젯 오른쪽 stretch.
    /// </summary>
    public static class EditorWidgets
    {
        // ── 싱글클릭 → 텍스트 편집 상태 ──
        private static readonly HashSet<string> _singleClickFocus = new();
        private static readonly HashSet<string> _inTextEdit = new();
        private static readonly Dictionary<string, int> _float3FocusAxis = new();

        // ── Label-Value Layout (Unity Inspector style) ──

        /// <summary>
        /// Label width ratio (0..1) relative to available content width.
        /// Unity uses roughly 0.4 (40%).
        /// </summary>
        public static float LabelWidthRatio { get; set; } = 0.4f;

        private static bool IsHiddenLabel(string label) => label.StartsWith("##");

        /// <summary>
        /// 왼쪽에 라벨을 그리고 값 위젯 커서 위치를 설정한다.
        /// ## 접두사 라벨이면 스킵하고 false 반환.
        /// </summary>
        private static bool BeginPropertyLayout(string label)
        {
            if (IsHiddenLabel(label)) return false;

            float availWidth = ImGui.GetContentRegionAvail().X;
            float labelWidth = availWidth * LabelWidthRatio;

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
            ImGui.SameLine(labelWidth);
            ImGui.SetNextItemWidth(-1f);
            return true;
        }

        /// <summary>
        /// raw ImGui 위젯(Checkbox, InputText, BeginCombo 등) 호출 전에 사용.
        /// 라벨을 왼쪽에 그리고, 위젯에 전달할 ## hidden label 문자열을 반환한다.
        /// </summary>
        public static string BeginPropertyRow(string label)
        {
            if (IsHiddenLabel(label)) return label;

            float availWidth = ImGui.GetContentRegionAvail().X;
            float labelWidth = availWidth * LabelWidthRatio;

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
            ImGui.SameLine(labelWidth);
            ImGui.SetNextItemWidth(-1f);
            return "##" + label;
        }

        // ── DragFloat / DragInt Clickable ──

        public static bool DragFloatClickable(string id, string label, ref float v, float speed, string format = "%.3f")
        {
            bool hasLayout = BeginPropertyLayout(label);
            string widgetLabel = hasLayout ? ("##" + id) : label;

            if (_singleClickFocus.Remove(id))
            {
                ImGui.SetKeyboardFocusHere(0);
                _inTextEdit.Add(id);
            }
            bool changed = ImGui.DragFloat(widgetLabel, ref v, speed, 0f, 0f, format);
            if (ImGui.IsItemDeactivated())
            {
                if (_inTextEdit.Remove(id)) { }
                else if (!ImGui.IsItemDeactivatedAfterEdit())
                    _singleClickFocus.Add(id);
            }
            return changed;
        }

        public static bool DragIntClickable(string id, string label, ref int v, float speed = 1f)
        {
            bool hasLayout = BeginPropertyLayout(label);
            string widgetLabel = hasLayout ? ("##" + id) : label;

            if (_singleClickFocus.Remove(id))
            {
                ImGui.SetKeyboardFocusHere(0);
                _inTextEdit.Add(id);
            }
            bool changed = ImGui.DragInt(widgetLabel, ref v, speed);
            if (ImGui.IsItemDeactivated())
            {
                if (_inTextEdit.Remove(id)) { }
                else if (!ImGui.IsItemDeactivatedAfterEdit())
                    _singleClickFocus.Add(id);
            }
            return changed;
        }

        public static bool DragFloat2Clickable(string id, string label, ref System.Numerics.Vector2 v, float speed)
        {
            bool hasLayout = BeginPropertyLayout(label);
            string widgetLabel = hasLayout ? ("##" + id) : label;

            if (_float3FocusAxis.Remove(id, out int axis))
            {
                ImGui.SetKeyboardFocusHere(axis);
                _inTextEdit.Add(id);
            }
            bool changed = ImGui.DragFloat2(widgetLabel, ref v, speed);
            if (ImGui.IsItemDeactivated())
            {
                if (_inTextEdit.Remove(id)) { }
                else if (!ImGui.IsItemDeactivatedAfterEdit())
                {
                    var min = ImGui.GetItemRectMin();
                    float itemWidth = ImGui.CalcItemWidth();
                    float mouseX = ImGui.GetIO().MousePos.X;
                    float relX = mouseX - min.X;
                    int clickedAxis = relX < itemWidth / 2f ? 0 : 1;
                    _float3FocusAxis[id] = clickedAxis;
                }
            }
            return changed;
        }

        public static bool DragFloat3Clickable(string id, string label, ref System.Numerics.Vector3 v, float speed)
        {
            bool hasLayout = BeginPropertyLayout(label);
            string widgetLabel = hasLayout ? ("##" + id) : label;

            if (_float3FocusAxis.Remove(id, out int axis))
            {
                ImGui.SetKeyboardFocusHere(axis);
                _inTextEdit.Add(id);
            }
            bool changed = ImGui.DragFloat3(widgetLabel, ref v, speed);
            if (ImGui.IsItemDeactivated())
            {
                if (_inTextEdit.Remove(id)) { }
                else if (!ImGui.IsItemDeactivatedAfterEdit())
                {
                    var min = ImGui.GetItemRectMin();
                    float itemWidth = ImGui.CalcItemWidth();
                    float mouseX = ImGui.GetIO().MousePos.X;
                    float relX = mouseX - min.X;
                    int clickedAxis = relX < itemWidth / 3f ? 0 : relX < itemWidth * 2f / 3f ? 1 : 2;
                    _float3FocusAxis[id] = clickedAxis;
                }
            }
            return changed;
        }

        public static bool DragFloat4Clickable(string id, string label, ref System.Numerics.Vector4 v, float speed)
        {
            bool hasLayout = BeginPropertyLayout(label);
            string widgetLabel = hasLayout ? ("##" + id) : label;

            if (_float3FocusAxis.Remove(id, out int axis))
            {
                ImGui.SetKeyboardFocusHere(axis);
                _inTextEdit.Add(id);
            }
            bool changed = ImGui.DragFloat4(widgetLabel, ref v, speed);
            if (ImGui.IsItemDeactivated())
            {
                if (_inTextEdit.Remove(id)) { }
                else if (!ImGui.IsItemDeactivatedAfterEdit())
                {
                    var min = ImGui.GetItemRectMin();
                    float itemWidth = ImGui.CalcItemWidth();
                    float mouseX = ImGui.GetIO().MousePos.X;
                    float relX = mouseX - min.X;
                    int clickedAxis = relX < itemWidth / 4f ? 0 : relX < itemWidth * 2f / 4f ? 1 : relX < itemWidth * 3f / 4f ? 2 : 3;
                    _float3FocusAxis[id] = clickedAxis;
                }
            }
            return changed;
        }

        // ── Raw 내부용 (레이아웃 없음, Slider+Input 콤보에서 사용) ──

        private static bool DragFloatClickableRaw(string id, string label, ref float v, float speed, string format = "%.3f")
        {
            if (_singleClickFocus.Remove(id))
            {
                ImGui.SetKeyboardFocusHere(0);
                _inTextEdit.Add(id);
            }
            bool changed = ImGui.DragFloat(label, ref v, speed, 0f, 0f, format);
            if (ImGui.IsItemDeactivated())
            {
                if (_inTextEdit.Remove(id)) { }
                else if (!ImGui.IsItemDeactivatedAfterEdit())
                    _singleClickFocus.Add(id);
            }
            return changed;
        }

        private static bool DragIntClickableRaw(string id, string label, ref int v, float speed = 1f)
        {
            if (_singleClickFocus.Remove(id))
            {
                ImGui.SetKeyboardFocusHere(0);
                _inTextEdit.Add(id);
            }
            bool changed = ImGui.DragInt(label, ref v, speed);
            if (ImGui.IsItemDeactivated())
            {
                if (_inTextEdit.Remove(id)) { }
                else if (!ImGui.IsItemDeactivatedAfterEdit())
                    _singleClickFocus.Add(id);
            }
            return changed;
        }

        // ── Slider + Input 콤보 ──

        public static bool SliderFloatWithInput(string id, string label, ref float v, float min, float max, string format = "%.2f")
        {
            return SliderFloatWithInput(id, label, ref v, min, max, out _, format);
        }

        public static bool SliderFloatWithInput(string id, string label, ref float v, float min, float max, out bool sliderDeactivated, string format = "%.2f")
        {
            bool hasLayout = BeginPropertyLayout(label);
            string uniqueId = id + "." + label;
            string hiddenSlider = "##slider_" + uniqueId;
            string inputLabel = hasLayout ? ("##" + uniqueId + ".input") : label;

            float totalW = ImGui.CalcItemWidth();
            float inputW = 60f;
            float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
            ImGui.PushItemWidth(totalW - inputW - spacing);
            bool changed = ImGui.SliderFloat(hiddenSlider, ref v, min, max, format);
            ImGui.PopItemWidth();
            sliderDeactivated = ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SameLine(0, spacing);
            ImGui.PushItemWidth(inputW);
            changed |= DragFloatClickableRaw(uniqueId + ".input", inputLabel, ref v, 0.01f, format);
            ImGui.PopItemWidth();
            if (v < min) v = min;
            if (v > max) v = max;
            return changed;
        }

        public static bool SliderIntWithInput(string id, string label, ref int v, int min, int max)
        {
            return SliderIntWithInput(id, label, ref v, min, max, out _);
        }

        public static bool SliderIntWithInput(string id, string label, ref int v, int min, int max, out bool sliderDeactivated)
        {
            bool hasLayout = BeginPropertyLayout(label);
            string uniqueId = id + "." + label;
            string hiddenSlider = "##slider_" + uniqueId;
            string inputLabel = hasLayout ? ("##" + uniqueId + ".input") : label;

            float totalW = ImGui.CalcItemWidth();
            float inputW = 60f;
            float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
            ImGui.PushItemWidth(totalW - inputW - spacing);
            bool changed = ImGui.SliderInt(hiddenSlider, ref v, min, max);
            ImGui.PopItemWidth();
            sliderDeactivated = ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SameLine(0, spacing);
            ImGui.PushItemWidth(inputW);
            changed |= DragIntClickableRaw(uniqueId + ".input", inputLabel, ref v);
            ImGui.PopItemWidth();
            if (v < min) v = min;
            if (v > max) v = max;
            return changed;
        }

        // ── Color (RoseEngine.Color ↔ System.Numerics.Vector4) ──

        public static bool ColorEdit4(string label, ref Color color)
        {
            bool hasLayout = BeginPropertyLayout(label);
            string widgetLabel = hasLayout ? ("##color_" + label) : label;

            var nc = new System.Numerics.Vector4(color.r, color.g, color.b, color.a);
            bool changed = ImGui.ColorEdit4(widgetLabel, ref nc);
            if (changed)
                color = new Color(nc.X, nc.Y, nc.Z, nc.W);
            return changed;
        }
    }
}
