using System.Collections.Generic;
using SilkKey = Silk.NET.Input.Key;

namespace RoseEngine
{
    public enum KeyCode
    {
        None = 0,

        // 알파벳 A-Z
        A, B, C, D, E, F, G, H, I, J, K, L, M,
        N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

        // 숫자 0-9
        Alpha0, Alpha1, Alpha2, Alpha3, Alpha4,
        Alpha5, Alpha6, Alpha7, Alpha8, Alpha9,

        // 화살표
        UpArrow, DownArrow, LeftArrow, RightArrow,

        // 특수키
        Space, Return, Escape, Tab, Backspace, Delete,
        Insert, Home, End, PageUp, PageDown,

        // 수정자키
        LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt,

        // 기능키 F1-F12
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,

        // 키패드
        Keypad0, Keypad1, Keypad2, Keypad3, Keypad4,
        Keypad5, Keypad6, Keypad7, Keypad8, Keypad9,
        KeypadDivide, KeypadMultiply, KeypadMinus, KeypadPlus, KeypadEnter, KeypadPeriod,

        // 기타
        CapsLock, NumLock, ScrollLock, Pause, PrintScreen,

        // 마우스 (GetKey로도 조회 가능)
        Mouse0, Mouse1, Mouse2,
    }

    internal static class KeyCodeMapping
    {
        private static readonly Dictionary<SilkKey, KeyCode> _map = new()
        {
            // 알파벳
            { SilkKey.A, KeyCode.A }, { SilkKey.B, KeyCode.B },
            { SilkKey.C, KeyCode.C }, { SilkKey.D, KeyCode.D },
            { SilkKey.E, KeyCode.E }, { SilkKey.F, KeyCode.F },
            { SilkKey.G, KeyCode.G }, { SilkKey.H, KeyCode.H },
            { SilkKey.I, KeyCode.I }, { SilkKey.J, KeyCode.J },
            { SilkKey.K, KeyCode.K }, { SilkKey.L, KeyCode.L },
            { SilkKey.M, KeyCode.M }, { SilkKey.N, KeyCode.N },
            { SilkKey.O, KeyCode.O }, { SilkKey.P, KeyCode.P },
            { SilkKey.Q, KeyCode.Q }, { SilkKey.R, KeyCode.R },
            { SilkKey.S, KeyCode.S }, { SilkKey.T, KeyCode.T },
            { SilkKey.U, KeyCode.U }, { SilkKey.V, KeyCode.V },
            { SilkKey.W, KeyCode.W }, { SilkKey.X, KeyCode.X },
            { SilkKey.Y, KeyCode.Y }, { SilkKey.Z, KeyCode.Z },

            // 숫자
            { SilkKey.Number0, KeyCode.Alpha0 }, { SilkKey.Number1, KeyCode.Alpha1 },
            { SilkKey.Number2, KeyCode.Alpha2 }, { SilkKey.Number3, KeyCode.Alpha3 },
            { SilkKey.Number4, KeyCode.Alpha4 }, { SilkKey.Number5, KeyCode.Alpha5 },
            { SilkKey.Number6, KeyCode.Alpha6 }, { SilkKey.Number7, KeyCode.Alpha7 },
            { SilkKey.Number8, KeyCode.Alpha8 }, { SilkKey.Number9, KeyCode.Alpha9 },

            // 화살표
            { SilkKey.Up, KeyCode.UpArrow },
            { SilkKey.Down, KeyCode.DownArrow },
            { SilkKey.Left, KeyCode.LeftArrow },
            { SilkKey.Right, KeyCode.RightArrow },

            // 특수키
            { SilkKey.Space, KeyCode.Space },
            { SilkKey.Enter, KeyCode.Return },
            { SilkKey.Escape, KeyCode.Escape },
            { SilkKey.Tab, KeyCode.Tab },
            { SilkKey.Backspace, KeyCode.Backspace },
            { SilkKey.Delete, KeyCode.Delete },
            { SilkKey.Insert, KeyCode.Insert },
            { SilkKey.Home, KeyCode.Home },
            { SilkKey.End, KeyCode.End },
            { SilkKey.PageUp, KeyCode.PageUp },
            { SilkKey.PageDown, KeyCode.PageDown },

            // 수정자키
            { SilkKey.ShiftLeft, KeyCode.LeftShift },
            { SilkKey.ShiftRight, KeyCode.RightShift },
            { SilkKey.ControlLeft, KeyCode.LeftControl },
            { SilkKey.ControlRight, KeyCode.RightControl },
            { SilkKey.AltLeft, KeyCode.LeftAlt },
            { SilkKey.AltRight, KeyCode.RightAlt },

            // 기능키
            { SilkKey.F1, KeyCode.F1 }, { SilkKey.F2, KeyCode.F2 },
            { SilkKey.F3, KeyCode.F3 }, { SilkKey.F4, KeyCode.F4 },
            { SilkKey.F5, KeyCode.F5 }, { SilkKey.F6, KeyCode.F6 },
            { SilkKey.F7, KeyCode.F7 }, { SilkKey.F8, KeyCode.F8 },
            { SilkKey.F9, KeyCode.F9 }, { SilkKey.F10, KeyCode.F10 },
            { SilkKey.F11, KeyCode.F11 }, { SilkKey.F12, KeyCode.F12 },

            // 키패드
            { SilkKey.Keypad0, KeyCode.Keypad0 }, { SilkKey.Keypad1, KeyCode.Keypad1 },
            { SilkKey.Keypad2, KeyCode.Keypad2 }, { SilkKey.Keypad3, KeyCode.Keypad3 },
            { SilkKey.Keypad4, KeyCode.Keypad4 }, { SilkKey.Keypad5, KeyCode.Keypad5 },
            { SilkKey.Keypad6, KeyCode.Keypad6 }, { SilkKey.Keypad7, KeyCode.Keypad7 },
            { SilkKey.Keypad8, KeyCode.Keypad8 }, { SilkKey.Keypad9, KeyCode.Keypad9 },
            { SilkKey.KeypadDivide, KeyCode.KeypadDivide },
            { SilkKey.KeypadMultiply, KeyCode.KeypadMultiply },
            { SilkKey.KeypadSubtract, KeyCode.KeypadMinus },
            { SilkKey.KeypadAdd, KeyCode.KeypadPlus },
            { SilkKey.KeypadEnter, KeyCode.KeypadEnter },
            { SilkKey.KeypadDecimal, KeyCode.KeypadPeriod },

            // 기타
            { SilkKey.CapsLock, KeyCode.CapsLock },
            { SilkKey.NumLock, KeyCode.NumLock },
            { SilkKey.ScrollLock, KeyCode.ScrollLock },
            { SilkKey.Pause, KeyCode.Pause },
            { SilkKey.PrintScreen, KeyCode.PrintScreen },
        };

        public static KeyCode FromSilkNet(SilkKey key)
        {
            return _map.TryGetValue(key, out var code) ? code : KeyCode.None;
        }
    }
}
