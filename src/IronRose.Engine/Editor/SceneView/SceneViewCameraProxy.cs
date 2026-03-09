using System;
using RoseEngine;

namespace IronRose.Engine.Editor.SceneView
{
    /// <summary>
    /// WYSIWYG 모드에서 RenderSystem.Render(Camera, aspectRatio) 호출 시
    /// EditorCamera 값을 기존 Camera 컴포넌트로 전달하기 위한 프록시.
    /// </summary>
    public class SceneViewCameraProxy : IDisposable
    {
        private readonly GameObject _go;
        private readonly Camera _camera;

        public Camera Camera => _camera;

        public SceneViewCameraProxy()
        {
            _go = new GameObject("__EditorCameraProxy__");
            _go._isEditorInternal = true;
            _camera = _go.AddComponent<Camera>();

            // Camera.main이 이 프록시로 설정되지 않도록 보장
            if (Camera.main == _camera)
                Camera.main = null;
        }

        /// <summary>
        /// EditorCamera의 현재 상태를 Camera 컴포넌트에 동기화.
        /// WYSIWYG 렌더 직전에 호출.
        /// </summary>
        public void Sync(EditorCamera editorCam)
        {
            _go.transform.position = editorCam.Position;
            _go.transform.rotation = editorCam.Rotation;
            _camera.fieldOfView = editorCam.FieldOfView;
            _camera.nearClipPlane = editorCam.NearClip;
            _camera.farClipPlane = editorCam.FarClip;
            _camera.clearFlags = CameraClearFlags.Skybox;
        }

        public void Dispose()
        {
            if (!_go._isDestroyed)
            {
                RoseEngine.Object.DestroyImmediate(_go);
            }
        }
    }
}
