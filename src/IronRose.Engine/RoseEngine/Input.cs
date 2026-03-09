// ------------------------------------------------------------
// @file    Input.cs
// @brief   Silk.NET IInputContext 기반 입력 시스템. 키보드/마우스 상태를 프레임 단위로 관리.
//          Unity Input 클래스 호환 API 제공 (GetKey, GetAxis, GetMouseButton 등).
// @deps    RoseEngine/KeyCode, RoseEngine/KeyCodeMapping, RoseEngine/Vector2, RoseEngine/MouseButtonIndex
// @exports
//   static class Input
//     GetKey/GetKeyDown/GetKeyUp(KeyCode): bool       — 키보드 입력 조회
//     GetMouseButton/Down/Up(int): bool               — 마우스 버튼 조회
//     GetAxis(string): float                          — 축 입력 (Horizontal/Vertical/Mouse X/Y)
//     mousePosition: Vector2                          — 마우스 위치
//     mouseScrollDelta: float                         — 마우스 스크롤
//     inputString: string                             — 이 프레임 입력 문자열
//     anyKey/anyKeyDown: bool                         — 아무 키 눌림 여부
//     ImGuiWantsKeyboard/Mouse: bool                  — ImGui 입력 소비 플래그
//     Initialize(IInputContext): void                 — Silk.NET 이벤트 등록 (internal)
//     Update(): void                                  — 프레임 시작 시 이벤트 처리 (internal)
//     SkipNextDelta(): void                           — 다음 프레임 델타 무시 (internal)
//     ResetAllStates(): void                          — 모든 입력 상태 초기화 (internal)
//     SimulateKeyPress(KeyCode): void                 — 자동화용 키 시뮬레이션
// @note    ImGuiWantsMouse/Keyboard가 true이면 게임 입력 API는 false/0 반환.
//          GetKeyRaw/GetKeyDownRaw는 ImGui 차단 무시 (엔진 키용).
//          _skipNextDelta로 커서 모드 전환 시 델타 점프 방지.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using Silk.NET.Input;
using SilkKey = Silk.NET.Input.Key;
using SilkMouseButton = Silk.NET.Input.MouseButton;

namespace RoseEngine
{
    public static class Input
    {
        // 키보드 상태
        private static readonly HashSet<KeyCode> _keysHeld = new();
        private static readonly HashSet<KeyCode> _keysDown = new();
        private static readonly HashSet<KeyCode> _keysUp = new();

        // 마우스 버튼 상태 (0=Left, 1=Right, 2=Middle)
        private static readonly bool[] _mouseHeld = new bool[MouseButtonIndex.Count];
        private static readonly bool[] _mouseDown = new bool[MouseButtonIndex.Count];
        private static readonly bool[] _mouseUp = new bool[MouseButtonIndex.Count];

        // 마우스 위치/이동
        private static Vector2 _mousePosition;
        private static Vector2 _prevMousePosition;
        private static Vector2 _mouseDelta;
        private static float _mouseScrollDelta;

        // 프레임 간 이벤트 축적 버퍼
        private static readonly List<(KeyCode code, bool down)> _pendingKeyEvents = new();
        private static readonly List<(int index, bool down)> _pendingMouseEvents = new();
        private static float _pendingScrollDelta;
        private static Vector2 _latestMousePosition;

        // 커서 모드 전환 시 델타 점프 방지
        private static bool _skipNextDelta = false;

        // 문자 입력 (Unity Input.inputString 호환)
        private static readonly List<char> _pendingChars = new();
        private static string _inputString = "";

        // ImGui 입력 소비 플래그 (에디터 오버레이가 입력을 원하면 게임 입력 차단)
        public static bool ImGuiWantsKeyboard { get; set; }
        public static bool ImGuiWantsMouse { get; set; }

        // Game View 좌표 리매핑 (에디터 Game View 활성 시)
        internal static bool GameViewActive;
        internal static float GameViewMinX, GameViewMinY;
        internal static float GameViewMaxX, GameViewMaxY;
        internal static float GameViewRenderW, GameViewRenderH;

        // --- Public API: 키보드 (ImGui가 키보드를 원하면 게임에 false 반환) ---

        public static bool GetKey(KeyCode key) => !ImGuiWantsKeyboard && _keysHeld.Contains(key);
        public static bool GetKeyDown(KeyCode key) => !ImGuiWantsKeyboard && _keysDown.Contains(key);
        public static bool GetKeyUp(KeyCode key) => !ImGuiWantsKeyboard && _keysUp.Contains(key);

        public static bool anyKey => !ImGuiWantsKeyboard && (_keysHeld.Count > 0 || _mouseHeld[0] || _mouseHeld[1] || _mouseHeld[2]);
        public static bool anyKeyDown => !ImGuiWantsKeyboard && (_keysDown.Count > 0 || _mouseDown[0] || _mouseDown[1] || _mouseDown[2]);

        // Raw 버전: ImGui 차단 무시 (엔진 키 F11/F12/Ctrl+P 용)
        internal static bool GetKeyRaw(KeyCode key) => _keysHeld.Contains(key);
        internal static bool GetKeyDownRaw(KeyCode key) => _keysDown.Contains(key);
        internal static bool GetMouseButtonDownRaw(int button) => button >= 0 && button < MouseButtonIndex.Count && _mouseDown[button];

        /// <summary>이 프레임에 입력된 문자열 (타이핑된 문자, Unity Input.inputString 호환).</summary>
        public static string inputString => ImGuiWantsKeyboard ? "" : _inputString;
        internal static string inputStringRaw => _inputString;

        // --- Public API: 마우스 (ImGui가 마우스를 원하면 게임에 false 반환) ---

        public static bool GetMouseButton(int button) => !ImGuiWantsMouse && button >= 0 && button < MouseButtonIndex.Count && _mouseHeld[button];
        public static bool GetMouseButtonDown(int button) => !ImGuiWantsMouse && button >= 0 && button < MouseButtonIndex.Count && _mouseDown[button];
        public static bool GetMouseButtonUp(int button) => !ImGuiWantsMouse && button >= 0 && button < MouseButtonIndex.Count && _mouseUp[button];

        public static Vector2 mousePosition => _mousePosition;
        public static float mouseScrollDelta => (ImGuiWantsMouse) ? 0f : _mouseScrollDelta;

        // --- Public API: 축 입력 ---

        public static float GetAxis(string axisName)
        {
            switch (axisName)
            {
                case "Horizontal":
                {
                    float val = 0f;
                    if (_keysHeld.Contains(KeyCode.D) || _keysHeld.Contains(KeyCode.RightArrow)) val += 1f;
                    if (_keysHeld.Contains(KeyCode.A) || _keysHeld.Contains(KeyCode.LeftArrow)) val -= 1f;
                    return val;
                }
                case "Vertical":
                {
                    float val = 0f;
                    if (_keysHeld.Contains(KeyCode.W) || _keysHeld.Contains(KeyCode.UpArrow)) val += 1f;
                    if (_keysHeld.Contains(KeyCode.S) || _keysHeld.Contains(KeyCode.DownArrow)) val -= 1f;
                    return val;
                }
                case "Mouse X":
                    return _mouseDelta.x;
                case "Mouse Y":
                    return _mouseDelta.y;
                default:
                    return 0f;
            }
        }

        // --- Internal: 초기화 (Silk.NET IInputContext 이벤트 등록) ---

        internal static void Initialize(IInputContext context)
        {
            foreach (var kb in context.Keyboards)
            {
                kb.KeyDown += OnKeyDown;
                kb.KeyUp += OnKeyUp;
                kb.KeyChar += OnKeyChar;
            }
            foreach (var mouse in context.Mice)
            {
                mouse.MouseDown += OnMouseDown;
                mouse.MouseUp += OnMouseUp;
                mouse.MouseMove += OnMouseMove;
                mouse.Scroll += OnScroll;
            }
        }

        // --- Internal: 매 프레임 시작 시 호출 (축적된 이벤트 처리) ---

        internal static void Update()
        {
            // 이전 프레임 Down/Up + 문자 입력 초기화
            _keysDown.Clear();
            _keysUp.Clear();
            _inputString = _pendingChars.Count > 0 ? new string(_pendingChars.ToArray()) : "";
            _pendingChars.Clear();
            Array.Clear(_mouseDown, 0, MouseButtonIndex.Count);
            Array.Clear(_mouseUp, 0, MouseButtonIndex.Count);

            // 키보드 이벤트 처리
            foreach (var (code, down) in _pendingKeyEvents)
            {
                if (down)
                {
                    if (_keysHeld.Add(code))
                        _keysDown.Add(code);
                }
                else
                {
                    if (_keysHeld.Remove(code))
                        _keysUp.Add(code);
                }
            }
            _pendingKeyEvents.Clear();

            // 마우스 버튼 이벤트 처리
            foreach (var (idx, down) in _pendingMouseEvents)
            {
                if (down)
                {
                    if (!_mouseHeld[idx])
                        _mouseDown[idx] = true;
                    _mouseHeld[idx] = true;
                }
                else
                {
                    if (_mouseHeld[idx])
                        _mouseUp[idx] = true;
                    _mouseHeld[idx] = false;
                }
            }
            _pendingMouseEvents.Clear();

            // 마우스 위치 및 델타 (raw 좌표 기반으로 계산 후 리매핑)
            if (_skipNextDelta)
            {
                _mouseDelta = Vector2.zero;
                _prevMousePosition = _latestMousePosition;
                _skipNextDelta = false;
            }
            else
            {
                _mouseDelta = _latestMousePosition - _prevMousePosition;
                _prevMousePosition = _latestMousePosition;
            }
            _mousePosition = _latestMousePosition;

            // Game View 좌표 리매핑: 윈도우 좌표 → 렌더 타겟 좌표
            if (GameViewActive)
            {
                float imgW = GameViewMaxX - GameViewMinX;
                float imgH = GameViewMaxY - GameViewMinY;
                if (imgW > 1 && imgH > 1 && GameViewRenderW > 0 && GameViewRenderH > 0)
                {
                    float scaleX = GameViewRenderW / imgW;
                    float scaleY = GameViewRenderH / imgH;
                    _mousePosition = new Vector2(
                        (_mousePosition.x - GameViewMinX) * scaleX,
                        (_mousePosition.y - GameViewMinY) * scaleY);
                    _mouseDelta = new Vector2(_mouseDelta.x * scaleX, _mouseDelta.y * scaleY);
                }
            }

            // 마우스 스크롤
            _mouseScrollDelta = _pendingScrollDelta;
            _pendingScrollDelta = 0f;
        }

        // --- Internal: 모든 키/마우스 상태 초기화 (Play mode 전환 시 호출) ---

        internal static void ResetAllStates()
        {
            _keysHeld.Clear();
            _keysDown.Clear();
            _keysUp.Clear();

            Array.Clear(_mouseHeld, 0, MouseButtonIndex.Count);
            Array.Clear(_mouseDown, 0, MouseButtonIndex.Count);
            Array.Clear(_mouseUp, 0, MouseButtonIndex.Count);

            _pendingKeyEvents.Clear();
            _pendingMouseEvents.Clear();
            _pendingChars.Clear();
            _pendingScrollDelta = 0f;
            _mouseScrollDelta = 0f;
            _inputString = "";
        }

        /// <summary>다음 프레임의 델타를 무시 (커서 모드 전환 시 점프 방지).</summary>
        internal static void SkipNextDelta() => _skipNextDelta = true;

        // --- Automation: 키 시뮬레이션 ---

        /// <summary>자동화용 키 입력 시뮬레이션. 다음 프레임에 KeyDown+KeyUp으로 처리됨.</summary>
        public static void SimulateKeyPress(KeyCode code)
        {
            _pendingKeyEvents.Add((code, true));
            _pendingKeyEvents.Add((code, false));
        }

        // --- Silk.NET 이벤트 콜백 (이벤트 축적) ---

        private static void OnKeyDown(IKeyboard kb, SilkKey key, int scancode)
        {
            var code = KeyCodeMapping.FromSilkNet(key);
            if (code != KeyCode.None)
                _pendingKeyEvents.Add((code, true));
        }

        private static void OnKeyUp(IKeyboard kb, SilkKey key, int scancode)
        {
            var code = KeyCodeMapping.FromSilkNet(key);
            if (code != KeyCode.None)
                _pendingKeyEvents.Add((code, false));
        }

        private static void OnKeyChar(IKeyboard kb, char c)
        {
            if (c >= 32) // printable characters only
                _pendingChars.Add(c);
        }

        private static void OnMouseDown(IMouse mouse, SilkMouseButton button)
        {
            int idx = MouseButtonToIndex(button);
            if (idx >= 0)
                _pendingMouseEvents.Add((idx, true));
        }

        private static void OnMouseUp(IMouse mouse, SilkMouseButton button)
        {
            int idx = MouseButtonToIndex(button);
            if (idx >= 0)
                _pendingMouseEvents.Add((idx, false));
        }

        private static void OnMouseMove(IMouse mouse, System.Numerics.Vector2 pos)
        {
            _latestMousePosition = new Vector2(pos.X, pos.Y);
        }

        private static void OnScroll(IMouse mouse, ScrollWheel wheel)
        {
            _pendingScrollDelta += wheel.Y;
        }

        private static int MouseButtonToIndex(SilkMouseButton button)
        {
            return button switch
            {
                SilkMouseButton.Left => 0,
                SilkMouseButton.Right => 1,
                SilkMouseButton.Middle => 2,
                _ => -1,
            };
        }
    }
}
