using System;
using System.Collections.Generic;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Windowing;
using SilkKey = Silk.NET.Input.Key;
using SilkMouseButton = Silk.NET.Input.MouseButton;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// Bridges Silk.NET input events to ImGui IO.
    /// Must be initialized after ImGui context is created.
    /// Multi-Viewport 지원: 보조 윈도우의 입력도 처리.
    /// </summary>
    public sealed class ImGuiInputHandler : IDisposable
    {
        private IInputContext? _inputContext;
        private IWindow? _mainWindow;
        private readonly List<(IInputContext ctx, IWindow win)> _secondaryInputs = new();

        public bool WantCaptureMouse => ImGui.GetIO().WantCaptureMouse;
        public bool WantCaptureKeyboard => ImGui.GetIO().WantCaptureKeyboard;

        public void Initialize(IInputContext inputContext, IWindow mainWindow)
        {
            _inputContext = inputContext;
            _mainWindow = mainWindow;

            foreach (var kb in inputContext.Keyboards)
            {
                kb.KeyDown += OnKeyDown;
                kb.KeyUp += OnKeyUp;
                kb.KeyChar += OnKeyChar;
            }
            foreach (var mouse in inputContext.Mice)
            {
                mouse.MouseDown += OnMouseDown;
                mouse.MouseUp += OnMouseUp;
                mouse.MouseMove += OnMainMouseMove;
                mouse.Scroll += OnScroll;
            }
        }

        /// <summary>보조 뷰포트 윈도우의 입력 컨텍스트를 등록.</summary>
        public void AddSecondaryInput(IInputContext ctx, IWindow window)
        {
            _secondaryInputs.Add((ctx, window));

            foreach (var kb in ctx.Keyboards)
            {
                kb.KeyDown += OnKeyDown;
                kb.KeyUp += OnKeyUp;
                kb.KeyChar += OnKeyChar;
            }
            foreach (var mouse in ctx.Mice)
            {
                mouse.MouseDown += OnMouseDown;
                mouse.MouseUp += OnMouseUp;
                // 보조 윈도우: 로컬 좌표 → 절대 스크린 좌표 변환
                var capturedWindow = window;
                mouse.MouseMove += (m, localPos) =>
                {
                    float absX = capturedWindow.Position.X + localPos.X;
                    float absY = capturedWindow.Position.Y + localPos.Y;
                    ImGui.GetIO().AddMousePosEvent(absX, absY);
                };
                mouse.Scroll += OnScroll;
            }
        }

        /// <summary>보조 뷰포트 윈도우의 입력 컨텍스트를 해제.</summary>
        public void RemoveSecondaryInput(IInputContext ctx)
        {
            // 보조 윈도우가 파괴되기 전에, 현재 눌려있는 키/마우스 버튼의
            // 릴리스 이벤트를 ImGui IO에 전달하여 "stuck key" 방지
            var io = ImGui.GetIO();
            foreach (var kb in ctx.Keyboards)
            {
                foreach (SilkKey key in Enum.GetValues(typeof(SilkKey)))
                {
                    if (key != SilkKey.Unknown && kb.IsKeyPressed(key))
                    {
                        var imKey = MapKey(key);
                        if (imKey != ImGuiKey.None)
                            io.AddKeyEvent(imKey, false);
                    }
                }
                kb.KeyDown -= OnKeyDown;
                kb.KeyUp -= OnKeyUp;
                kb.KeyChar -= OnKeyChar;
            }
            foreach (var mouse in ctx.Mice)
            {
                for (int i = 0; i < 5; i++)
                {
                    var btn = (SilkMouseButton)i;
                    if (mouse.IsButtonPressed(btn))
                        io.AddMouseButtonEvent(MapMouseButton(btn), false);
                }
                mouse.MouseDown -= OnMouseDown;
                mouse.MouseUp -= OnMouseUp;
                // MouseMove lambda 캡처는 -= 불가 — IInputContext.Dispose()에서 정리
                mouse.Scroll -= OnScroll;
            }
            _secondaryInputs.RemoveAll(x => x.ctx == ctx);
        }

        /// <summary>
        /// Warp the OS mouse cursor to the given window-local position.
        /// GLFW's glfwSetCursorPos does NOT fire the MouseMove callback,
        /// so the caller must also update ImGui's io.MousePos to avoid a delta spike.
        /// </summary>
        public void WarpMouse(System.Numerics.Vector2 windowLocalPos)
        {
            if (_inputContext?.Mice.Count > 0)
                _inputContext.Mice[0].Position = windowLocalPos;
        }

        /// <summary>Current window-local mouse position from Silk.NET.</summary>
        public System.Numerics.Vector2 GetMouseWindowPosition()
        {
            if (_inputContext?.Mice.Count > 0)
                return _inputContext.Mice[0].Position;
            return default;
        }

        public void Update(float deltaTime, int windowWidth, int windowHeight)
        {
            var io = ImGui.GetIO();
            io.DeltaTime = deltaTime > 0 ? deltaTime : 1f / 60f;
            io.DisplaySize = new System.Numerics.Vector2(windowWidth, windowHeight);
            io.DisplayFramebufferScale = System.Numerics.Vector2.One;
        }

        private void OnKeyDown(IKeyboard kb, SilkKey key, int scancode)
        {
            var imKey = MapKey(key);
            if (imKey != ImGuiKey.None)
                ImGui.GetIO().AddKeyEvent(imKey, true);

            UpdateModifiers(kb);
        }

        private void OnKeyUp(IKeyboard kb, SilkKey key, int scancode)
        {
            var imKey = MapKey(key);
            if (imKey != ImGuiKey.None)
                ImGui.GetIO().AddKeyEvent(imKey, false);

            UpdateModifiers(kb);
        }

        private void OnKeyChar(IKeyboard kb, char c)
        {
            ImGui.GetIO().AddInputCharacter(c);
        }

        private void OnMouseDown(IMouse mouse, SilkMouseButton button)
        {
            int idx = MapMouseButton(button);
            if (idx >= 0)
                ImGui.GetIO().AddMouseButtonEvent(idx, true);
        }

        private void OnMouseUp(IMouse mouse, SilkMouseButton button)
        {
            int idx = MapMouseButton(button);
            if (idx >= 0)
                ImGui.GetIO().AddMouseButtonEvent(idx, false);
        }

        private void OnMainMouseMove(IMouse mouse, System.Numerics.Vector2 pos)
        {
            var io = ImGui.GetIO();
            if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0 && _mainWindow != null)
            {
                // 뷰포트 모드: 절대 스크린 좌표
                float absX = _mainWindow.Position.X + pos.X;
                float absY = _mainWindow.Position.Y + pos.Y;
                io.AddMousePosEvent(absX, absY);
            }
            else
            {
                io.AddMousePosEvent(pos.X, pos.Y);
            }
        }

        private void OnScroll(IMouse mouse, ScrollWheel wheel)
        {
            ImGui.GetIO().AddMouseWheelEvent(wheel.X, wheel.Y);
        }

        private static void UpdateModifiers(IKeyboard kb)
        {
            var io = ImGui.GetIO();
            io.AddKeyEvent(ImGuiKey.ModCtrl, kb.IsKeyPressed(SilkKey.ControlLeft) || kb.IsKeyPressed(SilkKey.ControlRight));
            io.AddKeyEvent(ImGuiKey.ModShift, kb.IsKeyPressed(SilkKey.ShiftLeft) || kb.IsKeyPressed(SilkKey.ShiftRight));
            io.AddKeyEvent(ImGuiKey.ModAlt, kb.IsKeyPressed(SilkKey.AltLeft) || kb.IsKeyPressed(SilkKey.AltRight));
            io.AddKeyEvent(ImGuiKey.ModSuper, kb.IsKeyPressed(SilkKey.SuperLeft) || kb.IsKeyPressed(SilkKey.SuperRight));
        }

        private static int MapMouseButton(SilkMouseButton button) => button switch
        {
            SilkMouseButton.Left => 0,
            SilkMouseButton.Right => 1,
            SilkMouseButton.Middle => 2,
            SilkMouseButton.Button4 => 3,
            SilkMouseButton.Button5 => 4,
            _ => -1,
        };

        private static ImGuiKey MapKey(SilkKey key) => key switch
        {
            SilkKey.Tab => ImGuiKey.Tab,
            SilkKey.Left => ImGuiKey.LeftArrow,
            SilkKey.Right => ImGuiKey.RightArrow,
            SilkKey.Up => ImGuiKey.UpArrow,
            SilkKey.Down => ImGuiKey.DownArrow,
            SilkKey.PageUp => ImGuiKey.PageUp,
            SilkKey.PageDown => ImGuiKey.PageDown,
            SilkKey.Home => ImGuiKey.Home,
            SilkKey.End => ImGuiKey.End,
            SilkKey.Insert => ImGuiKey.Insert,
            SilkKey.Delete => ImGuiKey.Delete,
            SilkKey.Backspace => ImGuiKey.Backspace,
            SilkKey.Space => ImGuiKey.Space,
            SilkKey.Enter => ImGuiKey.Enter,
            SilkKey.Escape => ImGuiKey.Escape,
            SilkKey.KeypadEnter => ImGuiKey.KeypadEnter,
            SilkKey.A => ImGuiKey.A,
            SilkKey.B => ImGuiKey.B,
            SilkKey.C => ImGuiKey.C,
            SilkKey.D => ImGuiKey.D,
            SilkKey.E => ImGuiKey.E,
            SilkKey.F => ImGuiKey.F,
            SilkKey.G => ImGuiKey.G,
            SilkKey.H => ImGuiKey.H,
            SilkKey.I => ImGuiKey.I,
            SilkKey.J => ImGuiKey.J,
            SilkKey.K => ImGuiKey.K,
            SilkKey.L => ImGuiKey.L,
            SilkKey.M => ImGuiKey.M,
            SilkKey.N => ImGuiKey.N,
            SilkKey.O => ImGuiKey.O,
            SilkKey.P => ImGuiKey.P,
            SilkKey.Q => ImGuiKey.Q,
            SilkKey.R => ImGuiKey.R,
            SilkKey.S => ImGuiKey.S,
            SilkKey.T => ImGuiKey.T,
            SilkKey.U => ImGuiKey.U,
            SilkKey.V => ImGuiKey.V,
            SilkKey.W => ImGuiKey.W,
            SilkKey.X => ImGuiKey.X,
            SilkKey.Y => ImGuiKey.Y,
            SilkKey.Z => ImGuiKey.Z,
            SilkKey.Number0 => ImGuiKey._0,
            SilkKey.Number1 => ImGuiKey._1,
            SilkKey.Number2 => ImGuiKey._2,
            SilkKey.Number3 => ImGuiKey._3,
            SilkKey.Number4 => ImGuiKey._4,
            SilkKey.Number5 => ImGuiKey._5,
            SilkKey.Number6 => ImGuiKey._6,
            SilkKey.Number7 => ImGuiKey._7,
            SilkKey.Number8 => ImGuiKey._8,
            SilkKey.Number9 => ImGuiKey._9,
            SilkKey.F1 => ImGuiKey.F1,
            SilkKey.F2 => ImGuiKey.F2,
            SilkKey.F3 => ImGuiKey.F3,
            SilkKey.F4 => ImGuiKey.F4,
            SilkKey.F5 => ImGuiKey.F5,
            SilkKey.F6 => ImGuiKey.F6,
            SilkKey.F7 => ImGuiKey.F7,
            SilkKey.F8 => ImGuiKey.F8,
            SilkKey.F9 => ImGuiKey.F9,
            SilkKey.F10 => ImGuiKey.F10,
            SilkKey.F11 => ImGuiKey.F11,
            SilkKey.F12 => ImGuiKey.F12,
            _ => ImGuiKey.None,
        };

        public void Dispose()
        {
            // 보조 입력 정리
            foreach (var (ctx, _) in _secondaryInputs)
            {
                foreach (var kb in ctx.Keyboards)
                {
                    kb.KeyDown -= OnKeyDown;
                    kb.KeyUp -= OnKeyUp;
                    kb.KeyChar -= OnKeyChar;
                }
                foreach (var mouse in ctx.Mice)
                {
                    mouse.MouseDown -= OnMouseDown;
                    mouse.MouseUp -= OnMouseUp;
                    mouse.Scroll -= OnScroll;
                }
            }
            _secondaryInputs.Clear();

            // 메인 입력 정리
            if (_inputContext != null)
            {
                foreach (var kb in _inputContext.Keyboards)
                {
                    kb.KeyDown -= OnKeyDown;
                    kb.KeyUp -= OnKeyUp;
                    kb.KeyChar -= OnKeyChar;
                }
                foreach (var mouse in _inputContext.Mice)
                {
                    mouse.MouseDown -= OnMouseDown;
                    mouse.MouseUp -= OnMouseUp;
                    mouse.MouseMove -= OnMainMouseMove;
                    mouse.Scroll -= OnScroll;
                }
                _inputContext = null;
            }
            _mainWindow = null;
        }
    }
}
