using System;
using System.Collections.Generic;

namespace RoseEngine.InputSystem
{
    internal static class InputControlPath
    {
        private static readonly Dictionary<string, KeyCode> _keyboardMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // Alphabet
            { "a", KeyCode.A }, { "b", KeyCode.B }, { "c", KeyCode.C }, { "d", KeyCode.D },
            { "e", KeyCode.E }, { "f", KeyCode.F }, { "g", KeyCode.G }, { "h", KeyCode.H },
            { "i", KeyCode.I }, { "j", KeyCode.J }, { "k", KeyCode.K }, { "l", KeyCode.L },
            { "m", KeyCode.M }, { "n", KeyCode.N }, { "o", KeyCode.O }, { "p", KeyCode.P },
            { "q", KeyCode.Q }, { "r", KeyCode.R }, { "s", KeyCode.S }, { "t", KeyCode.T },
            { "u", KeyCode.U }, { "v", KeyCode.V }, { "w", KeyCode.W }, { "x", KeyCode.X },
            { "y", KeyCode.Y }, { "z", KeyCode.Z },

            // Numbers
            { "0", KeyCode.Alpha0 }, { "1", KeyCode.Alpha1 }, { "2", KeyCode.Alpha2 },
            { "3", KeyCode.Alpha3 }, { "4", KeyCode.Alpha4 }, { "5", KeyCode.Alpha5 },
            { "6", KeyCode.Alpha6 }, { "7", KeyCode.Alpha7 }, { "8", KeyCode.Alpha8 },
            { "9", KeyCode.Alpha9 },

            // Special keys
            { "space", KeyCode.Space },
            { "enter", KeyCode.Return },
            { "return", KeyCode.Return },
            { "escape", KeyCode.Escape },
            { "tab", KeyCode.Tab },
            { "backspace", KeyCode.Backspace },
            { "delete", KeyCode.Delete },
            { "insert", KeyCode.Insert },
            { "home", KeyCode.Home },
            { "end", KeyCode.End },
            { "pageUp", KeyCode.PageUp },
            { "pageDown", KeyCode.PageDown },

            // Arrows
            { "upArrow", KeyCode.UpArrow },
            { "downArrow", KeyCode.DownArrow },
            { "leftArrow", KeyCode.LeftArrow },
            { "rightArrow", KeyCode.RightArrow },

            // Modifiers
            { "leftShift", KeyCode.LeftShift },
            { "rightShift", KeyCode.RightShift },
            { "leftCtrl", KeyCode.LeftControl },
            { "rightCtrl", KeyCode.RightControl },
            { "leftAlt", KeyCode.LeftAlt },
            { "rightAlt", KeyCode.RightAlt },

            // Function keys
            { "f1", KeyCode.F1 }, { "f2", KeyCode.F2 }, { "f3", KeyCode.F3 },
            { "f4", KeyCode.F4 }, { "f5", KeyCode.F5 }, { "f6", KeyCode.F6 },
            { "f7", KeyCode.F7 }, { "f8", KeyCode.F8 }, { "f9", KeyCode.F9 },
            { "f10", KeyCode.F10 }, { "f11", KeyCode.F11 }, { "f12", KeyCode.F12 },
        };

        public enum DeviceType
        {
            Unknown,
            Keyboard,
            Mouse,
        }

        public enum MouseControl
        {
            None,
            LeftButton,
            RightButton,
            MiddleButton,
            Position,
            Delta,
            Scroll,
        }

        public struct ParsedPath
        {
            public DeviceType device;
            public string controlName;
            public KeyCode keyCode;         // valid when device == Keyboard
            public MouseControl mouseControl; // valid when device == Mouse
        }

        public static ParsedPath Parse(string path)
        {
            var result = new ParsedPath();

            if (string.IsNullOrEmpty(path))
                return result;

            // Expected format: "<Device>/control"
            int slashIndex = path.IndexOf('/');
            if (slashIndex < 0)
                return result;

            string devicePart = path.Substring(0, slashIndex).Trim();
            string controlPart = path.Substring(slashIndex + 1).Trim();
            result.controlName = controlPart;

            // Remove angle brackets
            if (devicePart.StartsWith("<") && devicePart.EndsWith(">"))
                devicePart = devicePart.Substring(1, devicePart.Length - 2);

            if (devicePart.Equals("Keyboard", StringComparison.OrdinalIgnoreCase))
            {
                result.device = DeviceType.Keyboard;
                if (_keyboardMap.TryGetValue(controlPart, out var keyCode))
                    result.keyCode = keyCode;
            }
            else if (devicePart.Equals("Mouse", StringComparison.OrdinalIgnoreCase))
            {
                result.device = DeviceType.Mouse;
                result.mouseControl = controlPart.ToLowerInvariant() switch
                {
                    "leftbutton" or "press" => MouseControl.LeftButton,
                    "rightbutton" => MouseControl.RightButton,
                    "middlebutton" => MouseControl.MiddleButton,
                    "position" => MouseControl.Position,
                    "delta" => MouseControl.Delta,
                    "scroll" => MouseControl.Scroll,
                    _ => MouseControl.None,
                };
            }

            return result;
        }

        /// <summary>
        /// Reads a float value (0 or 1) for a single binding path from the legacy Input system.
        /// </summary>
        public static float ReadButtonValue(in ParsedPath parsed)
        {
            switch (parsed.device)
            {
                case DeviceType.Keyboard:
                    return Input.GetKey(parsed.keyCode) ? 1f : 0f;

                case DeviceType.Mouse:
                    return parsed.mouseControl switch
                    {
                        MouseControl.LeftButton => Input.GetMouseButton(0) ? 1f : 0f,
                        MouseControl.RightButton => Input.GetMouseButton(1) ? 1f : 0f,
                        MouseControl.MiddleButton => Input.GetMouseButton(2) ? 1f : 0f,
                        _ => 0f,
                    };

                default:
                    return 0f;
            }
        }

        /// <summary>
        /// Reads a Vector2 value for mouse position/delta/scroll.
        /// </summary>
        public static Vector2 ReadVector2Value(in ParsedPath parsed)
        {
            if (parsed.device != DeviceType.Mouse)
                return Vector2.zero;

            return parsed.mouseControl switch
            {
                MouseControl.Position => Input.mousePosition,
                MouseControl.Scroll => new Vector2(0f, Input.mouseScrollDelta),
                _ => Vector2.zero,
            };
        }

        /// <summary>
        /// Returns true if a button binding was just pressed this frame.
        /// </summary>
        public static bool ReadButtonDown(in ParsedPath parsed)
        {
            switch (parsed.device)
            {
                case DeviceType.Keyboard:
                    return Input.GetKeyDown(parsed.keyCode);

                case DeviceType.Mouse:
                    return parsed.mouseControl switch
                    {
                        MouseControl.LeftButton => Input.GetMouseButtonDown(0),
                        MouseControl.RightButton => Input.GetMouseButtonDown(1),
                        MouseControl.MiddleButton => Input.GetMouseButtonDown(2),
                        _ => false,
                    };

                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns true if a button binding was just released this frame.
        /// </summary>
        public static bool ReadButtonUp(in ParsedPath parsed)
        {
            switch (parsed.device)
            {
                case DeviceType.Keyboard:
                    return Input.GetKeyUp(parsed.keyCode);

                case DeviceType.Mouse:
                    return parsed.mouseControl switch
                    {
                        MouseControl.LeftButton => Input.GetMouseButtonUp(0),
                        MouseControl.RightButton => Input.GetMouseButtonUp(1),
                        MouseControl.MiddleButton => Input.GetMouseButtonUp(2),
                        _ => false,
                    };

                default:
                    return false;
            }
        }
    }
}
