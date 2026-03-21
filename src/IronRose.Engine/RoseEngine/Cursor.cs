// ------------------------------------------------------------
// @file    Cursor.cs
// @brief   커서 잠금/표시 상태를 관리하는 정적 클래스. Unity Cursor API 호환.
//          Silk.NET ICursor.CursorMode를 래핑하여 구현.
// @deps    RoseEngine/CursorLockMode, RoseEngine/EditorDebug,
//          IronRose.Engine.Editor/EditorPlayMode
// @exports
//   static class Cursor
//     lockState: CursorLockMode                  — 커서 잠금 모드 (get/set)
//     visible: bool                               — 커서 표시 여부 (get/set)
//     isEscapeOverridden: bool                    — ESC로 임시 해제 상태 여부 (읽기 전용)
//     IsEffectivelyLocked: bool                   — 실제 Locked 적용 중 여부 (internal)
//     Initialize(IMouse): void                    — Silk.NET 마우스 참조 설정 (internal)
//     EscapeRelease(): void                       — ESC 키로 커서 잠금 임시 해제 (internal)
//     ReacquireLock(): void                       — Game View 클릭으로 잠금 재진입 (internal)
//     ResetToDefault(): void                      — Play 모드 종료 시 기본값 리셋 (internal)
//     ApplyState(): void                          — 현재 논리 상태를 Silk.NET에 적용 (internal)
// @note    IsLockAllowed는 Playing 상태일 때만 true.
//          ESC 오버라이드는 에디터 모드에서만 EngineCore가 호출.
//          Confined 모드: Silk.NET 2.23.0에 IMouse.IsConfined가 없으므로
//          CursorMode만 적용하고 윈도우 영역 제한은 미구현 (TODO).
// ------------------------------------------------------------
using Silk.NET.Input;

namespace RoseEngine
{
    public static class Cursor
    {
        // 사용자가 스크립트에서 설정한 논리적 상태
        private static CursorLockMode _lockState = CursorLockMode.None;
        private static bool _visible = true;

        // ESC로 임시 해제된 상태
        private static bool _escapeOverride = false;

        // Silk.NET 마우스 참조 (Initialize 시 설정)
        private static IMouse? _mouse;

        /// <summary>커서 잠금 모드. Unity Cursor.lockState 호환.</summary>
        public static CursorLockMode lockState
        {
            get => _lockState;
            set
            {
                _lockState = value;
                _escapeOverride = false; // 새 lockState 설정 시 ESC 오버라이드 해제
                ApplyState();
            }
        }

        /// <summary>커서 표시 여부. Unity Cursor.visible 호환.</summary>
        public static bool visible
        {
            get => _visible;
            set
            {
                _visible = value;
                ApplyState();
            }
        }

        /// <summary>ESC로 임시 해제된 상태인지 여부 (읽기 전용).</summary>
        public static bool isEscapeOverridden => _escapeOverride;

        /// <summary>현재 실제로 Locked 상태가 적용 중인지 (ESC 해제 아닌 상태).</summary>
        internal static bool IsEffectivelyLocked =>
            _lockState == CursorLockMode.Locked && !_escapeOverride && IsLockAllowed;

        /// <summary>EngineCore.InitInput()에서 호출. Silk.NET IMouse 참조 저장.</summary>
        internal static void Initialize(IMouse mouse)
        {
            _mouse = mouse;
        }

        /// <summary>ESC 키로 커서 잠금 임시 해제.</summary>
        internal static void EscapeRelease()
        {
            if (_lockState == CursorLockMode.Locked && !_escapeOverride)
            {
                _escapeOverride = true;
                ApplyState();
                EditorDebug.Log("[Cursor] Escape override: cursor unlocked temporarily");
            }
        }

        /// <summary>Game View 클릭으로 Locked 상태 재진입.</summary>
        internal static void ReacquireLock()
        {
            if (_lockState == CursorLockMode.Locked && _escapeOverride)
            {
                _escapeOverride = false;
                ApplyState();
                EditorDebug.Log("[Cursor] Lock reacquired");
            }
        }

        /// <summary>Play 모드 종료 시 강제 리셋.</summary>
        internal static void ResetToDefault()
        {
            _lockState = CursorLockMode.None;
            _visible = true;
            _escapeOverride = false;
            ApplyState();
        }

        /// <summary>현재 커서 잠금이 허용되는 상태인지 판단.</summary>
        private static bool IsLockAllowed
        {
            get
            {
                // Playing 상태일 때만 커서 잠금 허용
                return IronRose.Engine.Editor.EditorPlayMode.State
                    == IronRose.Engine.Editor.PlayModeState.Playing;
            }
        }

        /// <summary>논리적 상태 + 에디터 상태를 종합하여 Silk.NET 커서 모드 적용.</summary>
        internal static void ApplyState()
        {
            if (_mouse == null) return;

            var cursor = _mouse.Cursor;

            if (!IsLockAllowed)
            {
                // Play 모드가 아니면 항상 정상 커서
                cursor.CursorMode = CursorMode.Normal;
                return;
            }

            if (_escapeOverride)
            {
                // ESC로 임시 해제 중 — visible 상태와 무관하게 커서를 보여줘야 함
                // (Unity 동작과 동일: ESC override는 lockState + visible 모두 기본값으로 되돌림)
                cursor.CursorMode = CursorMode.Normal;
                return;
            }

            switch (_lockState)
            {
                case CursorLockMode.None:
                    cursor.CursorMode = _visible ? CursorMode.Normal : CursorMode.Hidden;
                    break;

                case CursorLockMode.Locked:
                    // Disabled = 커서 숨김 + relative mouse mode (SDL_SetRelativeMouseMode)
                    cursor.CursorMode = CursorMode.Disabled;
                    break;

                case CursorLockMode.Confined:
                    // TODO: Silk.NET 2.23.0에 IMouse.IsConfined가 없으므로
                    // 윈도우 영역 제한은 미구현. CursorMode만 적용.
                    cursor.CursorMode = _visible ? CursorMode.Normal : CursorMode.Hidden;
                    break;
            }
        }
    }
}
