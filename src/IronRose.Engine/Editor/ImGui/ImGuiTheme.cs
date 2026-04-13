// ------------------------------------------------------------
// @file    ImGuiTheme.cs
// @brief   ImGui 스타일/컬러 팔레트 적용. Rose/Dark/Light 3종 테마를
//          EditorColorTheme enum 값에 따라 분기 적용한다.
// @deps    IronRose.Engine/EditorPreferences(EditorColorTheme), ImGuiNET
// @exports
//   static class ImGuiTheme
//     Apply(): void                        — Rose 테마를 적용 (하위 호환)
//     Apply(EditorColorTheme): void        — 지정된 테마 적용
// @note    공통 스타일(Rounding/Spacing)은 모든 테마에 공통 적용된다.
//          각 팔레트 메서드는 ImGuiCol의 전체 키 집합을 빠짐없이 채운다
//          (테마 전환 시 이전 테마 잔상이 남지 않도록).
//          Rose 팔레트는 기존 Iron Rose 베이지 톤을 그대로 유지한다 (회귀 방지).
//          Dark/Light는 ImGui 기본 StyleColorsDark()/StyleColorsLight() 참고값 기반.
// ------------------------------------------------------------
using System.Numerics;
using ImGuiNET;
using IronRose.Engine;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// Iron Rose editor theme loader. Applies Rose/Dark/Light palettes to ImGui style.
    /// </summary>
    public static class ImGuiTheme
    {
        /// <summary>Rose 테마를 적용한다 (하위 호환용 무인자 오버로드).</summary>
        public static void Apply()
        {
            Apply(EditorColorTheme.Rose);
        }

        /// <summary>지정된 테마를 적용한다. 공통 스타일 적용 후 색상만 분기.</summary>
        public static void Apply(EditorColorTheme theme)
        {
            var style = ImGui.GetStyle();

            // Rounding (모든 테마 공통)
            style.WindowRounding = 4f;
            style.FrameRounding = 3f;
            style.GrabRounding = 2f;
            style.TabRounding = 3f;
            style.ScrollbarRounding = 3f;

            // Spacing (모든 테마 공통)
            style.WindowPadding = new Vector2(8, 8);
            style.FramePadding = new Vector2(6, 3);
            style.ItemSpacing = new Vector2(8, 4);
            style.ItemInnerSpacing = new Vector2(4, 4);
            style.IndentSpacing = 16f;
            style.ScrollbarSize = 12f;
            style.GrabMinSize = 8f;

            // 색상 테이블만 테마별 분기
            switch (theme)
            {
                case EditorColorTheme.Dark:
                    ApplyDarkPalette(style);
                    break;
                case EditorColorTheme.Light:
                    ApplyLightPalette(style);
                    break;
                case EditorColorTheme.Rose:
                default:
                    ApplyRosePalette(style);
                    break;
            }
        }

        /// <summary>
        /// Iron Rose 테마 — 따뜻한 베이지 배경 + 어두운 회색 텍스트 + 장미 악센트.
        /// #E6DCD2 ironRoseColor 기반.
        /// </summary>
        private static void ApplyRosePalette(ImGuiStylePtr style)
        {
            // Iron Rose colors — ironRoseColor #E6DCD2 (0.902, 0.863, 0.824) 기반
            // 금속 백장미 베이지 톤 배경 + 매우 어두운 회색 텍스트
            var crust     = new Vector4(0.720f, 0.680f, 0.640f, 1f);  // #B8ADA3  darkest (금속 그림자)
            var mantle    = new Vector4(0.860f, 0.830f, 0.800f, 1f);  // #DBD4CC  secondary bg
            var base_     = new Vector4(0.902f, 0.863f, 0.824f, 1f);  // #E6DCD2  ironRoseColor (main bg)
            var surface0  = new Vector4(0.830f, 0.790f, 0.750f, 1f);  // #D4C9BF  frames
            var surface1  = new Vector4(0.790f, 0.745f, 0.700f, 1f);  // #C9BEB3  hovered
            var surface2  = new Vector4(0.750f, 0.700f, 0.650f, 1f);  // #BFB3A6  active
            var overlay0  = new Vector4(0.500f, 0.470f, 0.440f, 1f);  // #807870  disabled text
            var text      = new Vector4(0.133f, 0.133f, 0.133f, 1f);  // #222222  very dark gray
            var accent    = new Vector4(0.600f, 0.380f, 0.350f, 1f);  // #996159  장미 붉은색 (강조)
            var accentLt  = new Vector4(0.700f, 0.480f, 0.450f, 1f);  // #B37A73  light accent

            var colors = style.Colors;
            colors[(int)ImGuiCol.Text]                  = text;
            colors[(int)ImGuiCol.TextDisabled]          = overlay0;
            colors[(int)ImGuiCol.WindowBg]              = base_;
            colors[(int)ImGuiCol.ChildBg]               = mantle;
            colors[(int)ImGuiCol.PopupBg]               = base_ with { W = 0.97f };
            colors[(int)ImGuiCol.Border]                = surface0;
            colors[(int)ImGuiCol.BorderShadow]          = new Vector4(0, 0, 0, 0);
            colors[(int)ImGuiCol.FrameBg]               = surface0;
            colors[(int)ImGuiCol.FrameBgHovered]        = surface1;
            colors[(int)ImGuiCol.FrameBgActive]         = surface2;
            colors[(int)ImGuiCol.TitleBg]               = crust;
            colors[(int)ImGuiCol.TitleBgActive]         = surface1;
            colors[(int)ImGuiCol.TitleBgCollapsed]      = crust;
            colors[(int)ImGuiCol.MenuBarBg]             = mantle;
            colors[(int)ImGuiCol.ScrollbarBg]           = mantle;
            colors[(int)ImGuiCol.ScrollbarGrab]         = surface1;
            colors[(int)ImGuiCol.ScrollbarGrabHovered]  = surface2;
            colors[(int)ImGuiCol.ScrollbarGrabActive]   = overlay0;
            colors[(int)ImGuiCol.CheckMark]             = accent;
            colors[(int)ImGuiCol.SliderGrab]            = accent;
            colors[(int)ImGuiCol.SliderGrabActive]      = accentLt;
            colors[(int)ImGuiCol.Button]                = surface0;
            colors[(int)ImGuiCol.ButtonHovered]         = surface1;
            colors[(int)ImGuiCol.ButtonActive]          = surface2;
            colors[(int)ImGuiCol.Header]                = surface0;
            colors[(int)ImGuiCol.HeaderHovered]         = surface1;
            colors[(int)ImGuiCol.HeaderActive]          = surface2;
            colors[(int)ImGuiCol.Separator]             = surface0;
            colors[(int)ImGuiCol.SeparatorHovered]      = accent;
            colors[(int)ImGuiCol.SeparatorActive]       = accentLt;
            colors[(int)ImGuiCol.ResizeGrip]            = surface0;
            colors[(int)ImGuiCol.ResizeGripHovered]     = surface1;
            colors[(int)ImGuiCol.ResizeGripActive]      = accent;
            colors[(int)ImGuiCol.Tab]                   = mantle;
            colors[(int)ImGuiCol.TabHovered]            = surface1;
            colors[(int)ImGuiCol.TabSelected]           = surface0;
            colors[(int)ImGuiCol.TabDimmed]             = crust;
            colors[(int)ImGuiCol.TabDimmedSelected]     = mantle;
            colors[(int)ImGuiCol.DockingPreview]        = accent with { W = 0.7f };
            colors[(int)ImGuiCol.DockingEmptyBg]        = crust;
            colors[(int)ImGuiCol.TableHeaderBg]         = mantle;
            colors[(int)ImGuiCol.TableBorderStrong]     = surface0;
            colors[(int)ImGuiCol.TableBorderLight]      = surface0 with { W = 0.5f };
            colors[(int)ImGuiCol.TableRowBg]            = new Vector4(0, 0, 0, 0);
            colors[(int)ImGuiCol.TableRowBgAlt]         = mantle with { W = 0.3f };
        }

        /// <summary>
        /// Dark 테마 — ImGui 기본 StyleColorsDark() 기반, 파란색 계열 악센트.
        /// 어두운 회색 배경 + 밝은 텍스트.
        /// </summary>
        private static void ApplyDarkPalette(ImGuiStylePtr style)
        {
            var accent = new Vector4(0.26f, 0.59f, 0.98f, 1.00f);   // 파란색 악센트
            var accentActive = new Vector4(0.06f, 0.53f, 0.98f, 1.00f);

            var colors = style.Colors;
            colors[(int)ImGuiCol.Text]                  = new Vector4(1.00f, 1.00f, 1.00f, 1.00f);
            colors[(int)ImGuiCol.TextDisabled]          = new Vector4(0.50f, 0.50f, 0.50f, 1.00f);
            colors[(int)ImGuiCol.WindowBg]              = new Vector4(0.10f, 0.10f, 0.11f, 1.00f);
            colors[(int)ImGuiCol.ChildBg]               = new Vector4(0.00f, 0.00f, 0.00f, 0.00f);
            colors[(int)ImGuiCol.PopupBg]               = new Vector4(0.08f, 0.08f, 0.08f, 0.94f);
            colors[(int)ImGuiCol.Border]                = new Vector4(0.43f, 0.43f, 0.50f, 0.50f);
            colors[(int)ImGuiCol.BorderShadow]          = new Vector4(0.00f, 0.00f, 0.00f, 0.00f);
            colors[(int)ImGuiCol.FrameBg]               = new Vector4(0.20f, 0.21f, 0.22f, 0.54f);
            colors[(int)ImGuiCol.FrameBgHovered]        = new Vector4(0.40f, 0.40f, 0.40f, 0.40f);
            colors[(int)ImGuiCol.FrameBgActive]         = new Vector4(0.18f, 0.50f, 0.83f, 0.67f);
            colors[(int)ImGuiCol.TitleBg]               = new Vector4(0.04f, 0.04f, 0.04f, 1.00f);
            colors[(int)ImGuiCol.TitleBgActive]         = new Vector4(0.16f, 0.29f, 0.48f, 1.00f);
            colors[(int)ImGuiCol.TitleBgCollapsed]      = new Vector4(0.00f, 0.00f, 0.00f, 0.51f);
            colors[(int)ImGuiCol.MenuBarBg]             = new Vector4(0.14f, 0.14f, 0.14f, 1.00f);
            colors[(int)ImGuiCol.ScrollbarBg]           = new Vector4(0.02f, 0.02f, 0.02f, 0.53f);
            colors[(int)ImGuiCol.ScrollbarGrab]         = new Vector4(0.31f, 0.31f, 0.31f, 1.00f);
            colors[(int)ImGuiCol.ScrollbarGrabHovered]  = new Vector4(0.41f, 0.41f, 0.41f, 1.00f);
            colors[(int)ImGuiCol.ScrollbarGrabActive]   = new Vector4(0.51f, 0.51f, 0.51f, 1.00f);
            colors[(int)ImGuiCol.CheckMark]             = accent;
            colors[(int)ImGuiCol.SliderGrab]            = new Vector4(0.24f, 0.52f, 0.88f, 1.00f);
            colors[(int)ImGuiCol.SliderGrabActive]      = accent;
            colors[(int)ImGuiCol.Button]                = new Vector4(accent.X, accent.Y, accent.Z, 0.40f);
            colors[(int)ImGuiCol.ButtonHovered]         = accent;
            colors[(int)ImGuiCol.ButtonActive]          = accentActive;
            colors[(int)ImGuiCol.Header]                = new Vector4(accent.X, accent.Y, accent.Z, 0.31f);
            colors[(int)ImGuiCol.HeaderHovered]         = new Vector4(accent.X, accent.Y, accent.Z, 0.80f);
            colors[(int)ImGuiCol.HeaderActive]          = accent;
            colors[(int)ImGuiCol.Separator]             = new Vector4(0.43f, 0.43f, 0.50f, 0.50f);
            colors[(int)ImGuiCol.SeparatorHovered]      = new Vector4(0.10f, 0.40f, 0.75f, 0.78f);
            colors[(int)ImGuiCol.SeparatorActive]       = new Vector4(0.10f, 0.40f, 0.75f, 1.00f);
            colors[(int)ImGuiCol.ResizeGrip]            = new Vector4(accent.X, accent.Y, accent.Z, 0.20f);
            colors[(int)ImGuiCol.ResizeGripHovered]     = new Vector4(accent.X, accent.Y, accent.Z, 0.67f);
            colors[(int)ImGuiCol.ResizeGripActive]      = new Vector4(accent.X, accent.Y, accent.Z, 0.95f);
            colors[(int)ImGuiCol.Tab]                   = new Vector4(0.18f, 0.35f, 0.58f, 0.86f);
            colors[(int)ImGuiCol.TabHovered]            = new Vector4(accent.X, accent.Y, accent.Z, 0.80f);
            colors[(int)ImGuiCol.TabSelected]           = new Vector4(0.20f, 0.41f, 0.68f, 1.00f);
            colors[(int)ImGuiCol.TabDimmed]             = new Vector4(0.07f, 0.10f, 0.15f, 0.97f);
            colors[(int)ImGuiCol.TabDimmedSelected]     = new Vector4(0.14f, 0.26f, 0.42f, 1.00f);
            colors[(int)ImGuiCol.DockingPreview]        = new Vector4(accent.X, accent.Y, accent.Z, 0.70f);
            colors[(int)ImGuiCol.DockingEmptyBg]        = new Vector4(0.20f, 0.20f, 0.20f, 1.00f);
            colors[(int)ImGuiCol.TableHeaderBg]         = new Vector4(0.19f, 0.19f, 0.20f, 1.00f);
            colors[(int)ImGuiCol.TableBorderStrong]     = new Vector4(0.31f, 0.31f, 0.35f, 1.00f);
            colors[(int)ImGuiCol.TableBorderLight]      = new Vector4(0.23f, 0.23f, 0.25f, 1.00f);
            colors[(int)ImGuiCol.TableRowBg]            = new Vector4(0.00f, 0.00f, 0.00f, 0.00f);
            colors[(int)ImGuiCol.TableRowBgAlt]         = new Vector4(1.00f, 1.00f, 1.00f, 0.06f);
        }

        /// <summary>
        /// Light 테마 — ImGui 기본 StyleColorsLight() 기반, 파란색 계열 악센트.
        /// 밝은 회색 배경 + 검은 텍스트.
        /// </summary>
        private static void ApplyLightPalette(ImGuiStylePtr style)
        {
            var accent = new Vector4(0.26f, 0.59f, 0.98f, 1.00f);   // 파란색 악센트
            var accentActive = new Vector4(0.06f, 0.53f, 0.98f, 1.00f);

            var colors = style.Colors;
            colors[(int)ImGuiCol.Text]                  = new Vector4(0.00f, 0.00f, 0.00f, 1.00f);
            colors[(int)ImGuiCol.TextDisabled]          = new Vector4(0.60f, 0.60f, 0.60f, 1.00f);
            colors[(int)ImGuiCol.WindowBg]              = new Vector4(0.94f, 0.94f, 0.94f, 1.00f);
            colors[(int)ImGuiCol.ChildBg]               = new Vector4(0.00f, 0.00f, 0.00f, 0.00f);
            colors[(int)ImGuiCol.PopupBg]               = new Vector4(1.00f, 1.00f, 1.00f, 0.98f);
            colors[(int)ImGuiCol.Border]                = new Vector4(0.00f, 0.00f, 0.00f, 0.30f);
            colors[(int)ImGuiCol.BorderShadow]          = new Vector4(0.00f, 0.00f, 0.00f, 0.00f);
            colors[(int)ImGuiCol.FrameBg]               = new Vector4(1.00f, 1.00f, 1.00f, 1.00f);
            colors[(int)ImGuiCol.FrameBgHovered]        = new Vector4(accent.X, accent.Y, accent.Z, 0.40f);
            colors[(int)ImGuiCol.FrameBgActive]         = new Vector4(accent.X, accent.Y, accent.Z, 0.67f);
            colors[(int)ImGuiCol.TitleBg]               = new Vector4(0.96f, 0.96f, 0.96f, 1.00f);
            colors[(int)ImGuiCol.TitleBgActive]         = new Vector4(0.82f, 0.82f, 0.82f, 1.00f);
            colors[(int)ImGuiCol.TitleBgCollapsed]      = new Vector4(1.00f, 1.00f, 1.00f, 0.51f);
            colors[(int)ImGuiCol.MenuBarBg]             = new Vector4(0.86f, 0.86f, 0.86f, 1.00f);
            colors[(int)ImGuiCol.ScrollbarBg]           = new Vector4(0.98f, 0.98f, 0.98f, 0.53f);
            colors[(int)ImGuiCol.ScrollbarGrab]         = new Vector4(0.69f, 0.69f, 0.69f, 0.80f);
            colors[(int)ImGuiCol.ScrollbarGrabHovered]  = new Vector4(0.49f, 0.49f, 0.49f, 0.80f);
            colors[(int)ImGuiCol.ScrollbarGrabActive]   = new Vector4(0.49f, 0.49f, 0.49f, 1.00f);
            colors[(int)ImGuiCol.CheckMark]             = accent;
            colors[(int)ImGuiCol.SliderGrab]            = new Vector4(accent.X, accent.Y, accent.Z, 0.78f);
            colors[(int)ImGuiCol.SliderGrabActive]      = new Vector4(0.46f, 0.54f, 0.80f, 0.60f);
            colors[(int)ImGuiCol.Button]                = new Vector4(accent.X, accent.Y, accent.Z, 0.40f);
            colors[(int)ImGuiCol.ButtonHovered]         = accent;
            colors[(int)ImGuiCol.ButtonActive]          = accentActive;
            colors[(int)ImGuiCol.Header]                = new Vector4(accent.X, accent.Y, accent.Z, 0.31f);
            colors[(int)ImGuiCol.HeaderHovered]         = new Vector4(accent.X, accent.Y, accent.Z, 0.80f);
            colors[(int)ImGuiCol.HeaderActive]          = accent;
            colors[(int)ImGuiCol.Separator]             = new Vector4(0.39f, 0.39f, 0.39f, 0.62f);
            colors[(int)ImGuiCol.SeparatorHovered]      = new Vector4(0.14f, 0.44f, 0.80f, 0.78f);
            colors[(int)ImGuiCol.SeparatorActive]       = new Vector4(0.14f, 0.44f, 0.80f, 1.00f);
            colors[(int)ImGuiCol.ResizeGrip]            = new Vector4(0.35f, 0.35f, 0.35f, 0.17f);
            colors[(int)ImGuiCol.ResizeGripHovered]     = new Vector4(accent.X, accent.Y, accent.Z, 0.67f);
            colors[(int)ImGuiCol.ResizeGripActive]      = new Vector4(accent.X, accent.Y, accent.Z, 0.95f);
            colors[(int)ImGuiCol.Tab]                   = new Vector4(0.76f, 0.80f, 0.84f, 0.93f);
            colors[(int)ImGuiCol.TabHovered]            = new Vector4(accent.X, accent.Y, accent.Z, 0.80f);
            colors[(int)ImGuiCol.TabSelected]           = new Vector4(0.60f, 0.73f, 0.88f, 1.00f);
            colors[(int)ImGuiCol.TabDimmed]             = new Vector4(0.91f, 0.93f, 0.94f, 0.99f);
            colors[(int)ImGuiCol.TabDimmedSelected]     = new Vector4(0.74f, 0.82f, 0.91f, 1.00f);
            colors[(int)ImGuiCol.DockingPreview]        = new Vector4(accent.X, accent.Y, accent.Z, 0.22f);
            colors[(int)ImGuiCol.DockingEmptyBg]        = new Vector4(0.20f, 0.20f, 0.20f, 1.00f);
            colors[(int)ImGuiCol.TableHeaderBg]         = new Vector4(0.78f, 0.87f, 0.98f, 1.00f);
            colors[(int)ImGuiCol.TableBorderStrong]     = new Vector4(0.57f, 0.57f, 0.64f, 1.00f);
            colors[(int)ImGuiCol.TableBorderLight]      = new Vector4(0.68f, 0.68f, 0.74f, 1.00f);
            colors[(int)ImGuiCol.TableRowBg]            = new Vector4(0.00f, 0.00f, 0.00f, 0.00f);
            colors[(int)ImGuiCol.TableRowBgAlt]         = new Vector4(0.30f, 0.30f, 0.30f, 0.09f);
        }
    }
}
