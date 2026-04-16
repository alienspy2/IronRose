// ------------------------------------------------------------
// @file    ImGuiGameViewPanel.cs
// @brief   에디터 Game View 패널. 게임 렌더링 결과를 표시하고
//          Canvas UI 오버레이를 렌더링한다.
// @deps    CanvasRenderer, EditorPreferences, PanelMaximizer
// @exports
//   class ImGuiGameViewPanel : IEditorPanel
//     void Draw()             — 패널 렌더링
//     (uint,uint) GetRenderTargetSize(...)  — RT 크기 계산
// @note    Canvas UI 오버레이 렌더링 시 CanvasRenderer.IsInteractive를 true로 명시하여
//          Game View에서 게임 UI 입력이 정상 처리되도록 한다.
//          탭 우클릭 컨텍스트 메뉴에 "Focus on Play" 토글을 제공한다
//          (EditorPreferences.FocusGameViewOnPlay에 영속화됨).
// ------------------------------------------------------------
using System;
using System.Numerics;
using ImGuiNET;
using IronRose.Engine.Editor;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    public enum GameViewResolution
    {
        Native,
        FHD_1920x1080,
        HD_1280x720,
    }

    public class ImGuiGameViewPanel : IEditorPanel
    {
        private bool _isOpen = true;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }

        private IntPtr _textureId;
        private int _selectedResIdx = ResolutionKeyToIndex(EditorState.GameViewResolution);
        private bool _wireframe;
        private Vector2 _imageAreaSize; // 이미지 표시 영역 크기 (툴바 제외)

        // 입력 패스스루 상태
        private bool _isImageHovered;
        private bool _isWindowFocused;

        // 이미지 영역 스크린 좌표 (마우스 리매핑용)
        private Vector2 _imageScreenMin;
        private Vector2 _imageScreenMax;

        // 레이아웃 안정화: 에디터 열린 직후 N프레임은 swapchain fallback
        private int _layoutStableFrames = 0;
        private const int LayoutWarmupFrames = 5;
        private const uint MinRTSize = 128; // 최소 RT 크기

        private static readonly string[] ResolutionNames = { "Native", "1920 x 1080", "1280 x 720" };
        private static readonly string[] ResolutionKeys = { "native", "1920x1080", "1280x720" };

        private static int ResolutionKeyToIndex(string key)
        {
            for (int i = 0; i < ResolutionKeys.Length; i++)
                if (string.Equals(ResolutionKeys[i], key, StringComparison.OrdinalIgnoreCase))
                    return i;
            return 0;
        }

        public GameViewResolution SelectedResolution => (GameViewResolution)_selectedResIdx;
        public bool IsWireframe => _wireframe;

        /// <summary>Game View 이미지 위에 마우스가 있는지.</summary>
        public bool IsImageHovered => _isImageHovered;

        /// <summary>Game View 창이 포커스되었는지.</summary>
        public bool IsWindowFocused => _isWindowFocused;

        /// <summary>이미지 영역의 스크린 좌표 (좌상단).</summary>
        public Vector2 ImageScreenMin => _imageScreenMin;

        /// <summary>이미지 영역의 스크린 좌표 (우하단).</summary>
        public Vector2 ImageScreenMax => _imageScreenMax;

        public void SetTextureId(IntPtr textureId)
        {
            _textureId = textureId;
        }

        /// <summary>에디터가 열릴 때 호출 — 레이아웃 안정화 카운터 리셋</summary>
        public void ResetLayoutStabilization()
        {
            _layoutStableFrames = 0;
        }

        /// <summary>
        /// Returns the desired render target size for the selected resolution.
        /// For Native, returns the Game View image area size (from last frame).
        /// Falls back to swapchain size if panel hasn't been drawn yet or layout is still stabilizing.
        /// </summary>
        public (uint W, uint H) GetRenderTargetSize(uint swapchainW, uint swapchainH)
        {
            if (SelectedResolution != GameViewResolution.Native)
            {
                return SelectedResolution switch
                {
                    GameViewResolution.FHD_1920x1080 => (1920, 1080),
                    GameViewResolution.HD_1280x720 => (1280, 720),
                    _ => (swapchainW, swapchainH),
                };
            }

            // Native 모드: 레이아웃 안정화 대기
            _layoutStableFrames++;
            if (_layoutStableFrames <= LayoutWarmupFrames)
            {
                // 도킹 패널 배치가 안정화될 때까지 swapchain 크기 사용
                return (swapchainW, swapchainH);
            }

            // 이미지 영역이 최소 크기 이상인 경우에만 사용
            if (_imageAreaSize.X >= MinRTSize && _imageAreaSize.Y >= MinRTSize)
            {
                return ((uint)_imageAreaSize.X, (uint)_imageAreaSize.Y);
            }

            // fallback: 스왑체인 크기
            return (swapchainW, swapchainH);
        }

        public void Draw()
        {
            if (!ProjectContext.IsProjectLoaded) return;
            if (!IsOpen)
            {
                _isImageHovered = false;
                _isWindowFocused = false;
                return;
            }

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            var gameViewVisible = ImGui.Begin("Game View", ref _isOpen);
            PanelMaximizer.DrawTabContextMenu("Game View", DrawExtraContextMenuItems);
            if (gameViewVisible)
            {
                _isWindowFocused = ImGui.IsWindowFocused();

                // ── Toolbar ──
                DrawToolbar();

                ImGui.Separator();

                // ── Game View Image (종횡비 유지) ──
                var contentSize = ImGui.GetContentRegionAvail();
                _imageAreaSize = contentSize;

                if (_textureId != IntPtr.Zero && contentSize.X > 1 && contentSize.Y > 1)
                {
                    DrawScaledImage(contentSize);
                    // 이미지가 그려진 후 hover 및 스크린 좌표 추적
                    _isImageHovered = ImGui.IsItemHovered();
                    _imageScreenMin = ImGui.GetItemRectMin();
                    _imageScreenMax = ImGui.GetItemRectMax();

                    // Canvas UI 오버레이 렌더링 (Game View: 입력 처리 활성화)
                    var dl = ImGui.GetWindowDrawList();
                    float imgW = _imageScreenMax.X - _imageScreenMin.X;
                    float imgH = _imageScreenMax.Y - _imageScreenMin.Y;
                    RoseEngine.CanvasRenderer.IsInteractive = true;
                    RoseEngine.CanvasRenderer.RenderAll(dl, _imageScreenMin.X, _imageScreenMin.Y, imgW, imgH);
                }
                else
                {
                    _isImageHovered = false;
                    ImGui.TextDisabled("No render target");
                }
            }
            else
            {
                _isImageHovered = false;
                _isWindowFocused = false;
            }
            ImGui.End();
            ImGui.PopStyleVar();
        }

        private static void DrawExtraContextMenuItems()
        {
            bool focusOnPlay = EditorPreferences.FocusGameViewOnPlay;
            if (ImGui.MenuItem("Focus on Play", null, ref focusOnPlay))
            {
                EditorPreferences.FocusGameViewOnPlay = focusOnPlay;
                EditorPreferences.Save();
            }
        }

        private void DrawToolbar()
        {
            // Small padding for toolbar items
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 2));

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 4);
            ImGui.AlignTextToFramePadding();
            ImGui.Text("Resolution");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140);
            int prevRes = _selectedResIdx;
            ImGui.Combo("##Resolution", ref _selectedResIdx, ResolutionNames, ResolutionNames.Length);
            if (_selectedResIdx != prevRes)
            {
                EditorState.GameViewResolution = ResolutionKeys[_selectedResIdx];
                EditorState.Save();
            }

            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
            ImGui.Checkbox("Wireframe", ref _wireframe);

            ImGui.PopStyleVar();
        }

        private void DrawScaledImage(Vector2 contentSize)
        {
            // Get render target aspect ratio
            var (rtW, rtH) = SelectedResolution switch
            {
                GameViewResolution.FHD_1920x1080 => (1920f, 1080f),
                GameViewResolution.HD_1280x720 => (1280f, 720f),
                _ => (contentSize.X, contentSize.Y), // Native: fill panel
            };

            if (SelectedResolution == GameViewResolution.Native)
            {
                // Native: just fill the entire content area
                ImGui.Image(_textureId, contentSize);
                return;
            }

            // Calculate display size maintaining aspect ratio
            float texAspect = rtW / rtH;
            float panelAspect = contentSize.X / contentSize.Y;

            float displayW, displayH;
            if (texAspect > panelAspect)
            {
                // Texture is wider → letterbox (black bars top/bottom)
                displayW = contentSize.X;
                displayH = contentSize.X / texAspect;
            }
            else
            {
                // Texture is taller → pillarbox (black bars left/right)
                displayH = contentSize.Y;
                displayW = contentSize.Y * texAspect;
            }

            // Center the image
            float offsetX = (contentSize.X - displayW) * 0.5f;
            float offsetY = (contentSize.Y - displayH) * 0.5f;
            var cursorPos = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new Vector2(cursorPos.X + offsetX, cursorPos.Y + offsetY));

            ImGui.Image(_textureId, new Vector2(displayW, displayH));
        }
    }
}
