using System.Numerics;
using ImGuiNET;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// Iron Rose theme — warm rose-tinted light background with dark text.
    /// </summary>
    public static class ImGuiTheme
    {
        public static void Apply()
        {
            var style = ImGui.GetStyle();

            // Rounding
            style.WindowRounding = 4f;
            style.FrameRounding = 3f;
            style.GrabRounding = 2f;
            style.TabRounding = 3f;
            style.ScrollbarRounding = 3f;

            // Spacing
            style.WindowPadding = new Vector2(8, 8);
            style.FramePadding = new Vector2(6, 3);
            style.ItemSpacing = new Vector2(8, 4);
            style.ItemInnerSpacing = new Vector2(4, 4);
            style.IndentSpacing = 16f;
            style.ScrollbarSize = 12f;
            style.GrabMinSize = 8f;

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
            var subtext0  = new Vector4(0.250f, 0.240f, 0.230f, 1f);  // #403D3A  dark gray secondary
            var accent    = new Vector4(0.600f, 0.380f, 0.350f, 1f);  // #996159  장미 붉은색 (강조)
            var accentLt  = new Vector4(0.700f, 0.480f, 0.450f, 1f);  // #B37A73  light accent
            var accentDk  = new Vector4(0.480f, 0.280f, 0.260f, 1f);  // #7A4742  dark accent
            var red       = new Vector4(0.750f, 0.220f, 0.250f, 1f);  // #BF3840
            var peach     = new Vector4(0.800f, 0.520f, 0.380f, 1f);  // #CC8561
            var yellow    = new Vector4(0.720f, 0.620f, 0.320f, 1f);  // #B89E52
            var green     = new Vector4(0.310f, 0.560f, 0.370f, 1f);  // #4F8F5E

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
    }
}
