// ------------------------------------------------------------
// @file    EditorPlayMode.cs
// @brief   에디터 Play/Pause/Stop 모드 상태 관리. 씬 스냅샷 저장/복원 포함.
// @deps    RoseEngine/SceneManager, RoseEngine/SceneSerializer, RoseEngine/Application,
//          RoseEngine/Time, RoseEngine/Debug, RoseEngine/Cursor, RoseEngine/Input,
//          RoseEngine/PhysicsManager, RoseEngine/Animator, RoseEngine/Object,
//          IronRose.Engine.Editor/EditorSelection, IronRose.Engine.Editor/EditorState,
//          IronRose.Engine.Editor/CanvasEditMode, IronRose.Engine.Editor/UndoSystem
// @exports
//   enum PlayModeState { Edit, Playing, Paused }
//   static class EditorPlayMode
//     State: PlayModeState                            — 현재 Play 모드 상태
//     IsInPlaySession: bool                           — Playing 또는 Paused 상태 여부
//     OnResetFixedAccumulator: Action?                — 물리 accumulator 리셋 콜백
//     EnterPlayMode(): void                           — Play 모드 진입
//     StopPlayMode(): void                            — Play 모드 종료 및 씬 복원
//     PausePlayMode(): void                           — 일시정지
//     ResumePlayMode(): void                          — 재개
//     TogglePause(): void                             — Pause/Resume 토글
// @note    Enter/Stop 시 Cursor.ResetToDefault() 호출.
//          Pause 시 Cursor.ApplyState()로 잠금 해제, Resume 시 재적용.
// ------------------------------------------------------------
using System;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    public enum PlayModeState
    {
        Edit,
        Playing,
        Paused,
    }

    public static class EditorPlayMode
    {
        public static PlayModeState State { get; private set; } = PlayModeState.Edit;

        private static string? _savedSceneToml;
        private static string? _savedScenePath;
        private static string? _savedSceneName;
        private static bool _savedSceneDirty;

        /// <summary>EngineCore가 Play/Stop 시 _fixedAccumulator를 리셋할 수 있도록 하는 콜백.</summary>
        public static Action? OnResetFixedAccumulator;

        /// <summary>
        /// Play 모드 진입 직전 호출되는 콜백.
        /// ScriptReloadManager가 FileSystemWatcher를 중단하는 데 사용됩니다.
        /// </summary>
        public static Action? OnBeforeEnterPlayMode;

        /// <summary>
        /// Play 모드 종료 후 호출되는 콜백.
        /// ScriptReloadManager가 FileSystemWatcher를 재활성화하고 변경 감지를 수행하는 데 사용됩니다.
        /// </summary>
        public static Action? OnAfterStopPlayMode;

        public static bool IsInPlaySession => State == PlayModeState.Playing || State == PlayModeState.Paused;

        public static void EnterPlayMode()
        {
            if (State != PlayModeState.Edit) return;

            // Canvas Edit Mode 중 Play 진입 시 자동 퇴출
            if (EditorState.IsEditingCanvas)
                CanvasEditMode.Exit();

            // Play mode 진입 전 콜백 (예: FileSystemWatcher 중단)
            OnBeforeEnterPlayMode?.Invoke();

            var scene = SceneManager.GetActiveScene();
            _savedSceneToml = SceneSerializer.SaveToString();
            _savedScenePath = scene.path;
            _savedSceneName = scene.name;
            _savedSceneDirty = scene.isDirty;

            // 물리 월드 리셋: 이전 세션에서 남은 body 정리
            PhysicsManager.Instance?.Reset();

            // 입력 상태 리셋: 이전 Edit 모드에서 남은 키/마우스 상태 정리
            Input.ResetAllStates();

            State = PlayModeState.Playing;
            Application.isPlaying = true;
            Application.isPaused = false;

            Time.time = 0f;
            Time.fixedTime = 0f;
            Time.frameCount = 0;

            // EngineCore의 _fixedAccumulator 리셋
            OnResetFixedAccumulator?.Invoke();

            // 임시: 등록된 clip이 있는 Animator 자동 재생
            foreach (var animator in RoseEngine.Object.FindObjectsOfType<Animator>())
            {
                if (animator.clip != null)
                    animator.Play();
            }

            // 커서 상태는 스크립트가 설정하므로 기본값으로 시작
            Cursor.ResetToDefault();

            EditorDebug.Log("[Editor] Entered Play mode");
        }

        public static void PausePlayMode()
        {
            if (State != PlayModeState.Playing) return;

            State = PlayModeState.Paused;
            Application.isPaused = true;

            // 일시정지 시 커서 잠금 해제 (에디터 조작 가능하도록)
            Cursor.ApplyState(); // IsLockAllowed가 Paused에서는 false

            EditorDebug.Log("[Editor] Paused");
        }

        public static void ResumePlayMode()
        {
            if (State != PlayModeState.Paused) return;

            State = PlayModeState.Playing;
            Application.isPaused = false;

            // Resume 시 스크립트가 설정한 커서 상태 재적용
            Cursor.ApplyState();

            EditorDebug.Log("[Editor] Resumed");
        }

        public static void StopPlayMode()
        {
            if (!IsInPlaySession) return;

            // 임시: 재생 중인 Animator 정지
            foreach (var animator in RoseEngine.Object.FindObjectsOfType<Animator>())
                animator.Stop();

            // 입력 상태 리셋: Play 중 남은 키/마우스 상태 정리
            Input.ResetAllStates();

            State = PlayModeState.Edit;
            Application.isPlaying = false;
            Application.isPaused = false;

            if (_savedSceneToml != null)
            {
                SceneSerializer.LoadFromString(_savedSceneToml);

                var scene = SceneManager.GetActiveScene();
                scene.path = _savedScenePath;
                scene.name = _savedSceneName ?? "Untitled";
                scene.isDirty = _savedSceneDirty;

                _savedSceneToml = null;
            }

            // EngineCore의 _fixedAccumulator 리셋
            OnResetFixedAccumulator?.Invoke();

            EditorSelection.Clear();
            UndoSystem.Clear();

            // Play mode 종료 → ProjectSettings의 활성 렌더러 프로파일 재적용
            ReapplyActiveRendererProfile();

            // Play 모드 종료 시 커서를 기본 상태로 복원
            Cursor.ResetToDefault();

            EditorDebug.Log("[Editor] Stopped Play mode, scene restored");

            // Play 모드 종료 후 보류 중인 작업 수행 (예: 핫 리로드)
            OnAfterStopPlayMode?.Invoke();
        }

        /// <summary>ProjectSettings에 저장된 활성 렌더러 프로파일을 로드하여 RenderSettings에 반영.</summary>
        private static void ReapplyActiveRendererProfile()
        {
            var guid = ProjectSettings.ActiveRendererProfileGuid;
            if (string.IsNullOrEmpty(guid)) return;

            var db = Resources.GetAssetDatabase();
            var profile = db?.LoadByGuid<RendererProfile>(guid);
            if (profile == null) return;

            RenderSettings.activeRendererProfile = profile;
            RenderSettings.activeRendererProfileGuid = guid;
            profile.ApplyToRenderSettings();
        }

        public static void TogglePause()
        {
            if (State == PlayModeState.Playing)
                PausePlayMode();
            else if (State == PlayModeState.Paused)
                ResumePlayMode();
        }
    }
}
