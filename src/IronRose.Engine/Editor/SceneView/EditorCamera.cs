using System;
using RoseEngine;

namespace IronRose.Engine.Editor.SceneView
{
    /// <summary>
    /// Scene View 에디터 전용 카메라. GameObject/Component가 아닌 독립 클래스.
    /// ImGui.GetIO()를 통해 입력을 직접 읽어 게임 Input 시스템과 충돌 없음.
    /// </summary>
    public class EditorCamera
    {
        /// <summary>씬 로드 시 복원할 카메라 상태. SceneSerializer가 설정하고 Update()에서 소비.</summary>
        internal static (Vector3 pos, Quaternion rot, Vector3 pivot)? PendingState;

        public Vector3 Position = new(0, 5, -10);
        public Quaternion Rotation = Quaternion.identity;
        public Vector3 Pivot = Vector3.zero;

        public float FieldOfView = 60f;
        public float NearClip = 0.01f;
        public float FarClip = 5000f;

        // Fly mode settings
        public float FlySpeed = 10f;
        public float FlySprintMultiplier = 3f;
        public float MouseSensitivity = 0.2f;

        // Orbit settings
        public float OrbitSpeed = 0.5f;

        // Pan settings
        public float PanSpeed = 0.02f;

        // Zoom settings
        public float ZoomSpeed = 2f;

        // Internal yaw/pitch for fly/orbit
        private float _yaw;
        private float _pitch;

        // Focus double-tap tracking
        private bool _lastFocusWasClean;  // true if last action was Focus (no other input since)
        private int? _lastFocusTargetId;

        // Animation state
        private bool _isAnimating;
        private Vector3 _animStartPos, _animTargetPos;
        private Vector3 _animStartPivot, _animTargetPivot;
        private float _animElapsed;
        private const float AnimDuration = 0.3f;

        public Vector3 Forward => Rotation * Vector3.forward;
        public Vector3 Right => Rotation * Vector3.right;
        public Vector3 Up => Rotation * Vector3.up;

        public EditorCamera()
        {
            // Initialize yaw/pitch from default rotation looking at pivot
            LookAt(Pivot);
        }

        public Matrix4x4 GetViewMatrix()
        {
            return Matrix4x4.LookAt(Position, Position + Forward, Up);
        }

        public Matrix4x4 GetProjectionMatrix(float aspect)
        {
            return Matrix4x4.Perspective(FieldOfView, aspect, NearClip, FarClip);
        }

        /// <summary>
        /// 매 프레임 호출. SceneViewInputState로부터 카메라 모드를 결정하고 이동/회전을 적용.
        /// </summary>
        public void Update(float dt, SceneViewInputState input)
        {
            // 씬 로드 시 저장된 카메라 상태 복원
            if (PendingState.HasValue)
            {
                var s = PendingState.Value;
                Position = s.pos;
                Rotation = s.rot;
                Pivot = s.pivot;
                // yaw/pitch를 Rotation에서 역산
                var fwd = Rotation * Vector3.forward;
                _yaw = MathF.Atan2(fwd.x, fwd.z) * (180f / MathF.PI);
                _pitch = -MathF.Asin(Math.Clamp(fwd.y, -1f, 1f)) * (180f / MathF.PI);
                PendingState = null;
            }

            bool hadCameraAction = false;

            if (input.IsFlyMode)
            {
                UpdateFly(dt, input);
                hadCameraAction = true;
            }
            else if (input.IsOrbitMode)
            {
                UpdateOrbit(dt, input);
                hadCameraAction = true;
            }
            else if (input.IsPanMode)
            {
                UpdatePan(dt, input);
                hadCameraAction = true;
            }

            if (input.ScrollDelta != 0f)
            {
                UpdateZoom(dt, input);
                hadCameraAction = true;
            }

            // Cancel animation on manual camera input
            if (hadCameraAction && _isAnimating)
                _isAnimating = false;

            if (input.FocusRequested)
            {
                FocusOnSelection();
            }
            else if (hadCameraAction)
            {
                // Any camera action resets the double-tap state
                _lastFocusWasClean = false;
                _lastFocusTargetId = null;
            }

            // Tick animation
            if (_isAnimating)
                UpdateAnimation(dt);
        }

        private void UpdateFly(float dt, SceneViewInputState input)
        {
            // Mouse look
            _yaw += input.MouseDelta.x * MouseSensitivity;
            _pitch += input.MouseDelta.y * MouseSensitivity;
            _pitch = Math.Clamp(_pitch, -89f, 89f);
            Rotation = Quaternion.Euler(_pitch, _yaw, 0f);

            // WASD movement
            float speed = FlySpeed * (input.IsSprintHeld ? FlySprintMultiplier : 1f) * dt;
            Vector3 move = Vector3.zero;
            if (input.MoveForward) move += Forward;
            if (input.MoveBackward) move -= Forward;
            if (input.MoveRight) move += Right;
            if (input.MoveLeft) move -= Right;
            if (input.MoveUp) move += Vector3.up;
            if (input.MoveDown) move -= Vector3.up;

            if (move.sqrMagnitude > 0f)
            {
                Position += move.normalized * speed;
                Pivot = Position + Forward * 5f;
            }
        }

        private void UpdateOrbit(float dt, SceneViewInputState input)
        {
            _yaw += input.MouseDelta.x * OrbitSpeed;
            _pitch += input.MouseDelta.y * OrbitSpeed;
            _pitch = Math.Clamp(_pitch, -89f, 89f);

            float dist = (Position - Pivot).magnitude;
            Rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Position = Pivot - Forward * dist;
        }

        private void UpdatePan(float dt, SceneViewInputState input)
        {
            // Screen-space panning: scale by distance for perspective correctness,
            // but use a much smaller factor so movement matches mouse feel.
            float dist = (Position - Pivot).magnitude;
            float panScale = 0.002f * dist;
            Vector3 offset = -Right * (input.MouseDelta.x * panScale) + Up * (input.MouseDelta.y * panScale);
            Position += offset;
            Pivot += offset;
        }

        private void UpdateZoom(float dt, SceneViewInputState input)
        {
            float dist = (Position - Pivot).magnitude;
            // Scale by dt*60 to be framerate-independent while keeping similar feel at 60fps
            float zoomAmount = input.ScrollDelta * ZoomSpeed * Math.Max(dist * 0.1f, 0.1f) * dt * 60f;
            Position += Forward * zoomAmount;
        }

        public void FocusOnSelection()
        {
            Debug.Log($"[EditorCamera:Focus] called. SelectedId={EditorSelection.SelectedGameObjectId}");
            var go = EditorSelection.SelectedGameObject;
            if (go == null)
            {
                Debug.Log("[EditorCamera:Focus] SelectedGameObject is null, returning");
                return;
            }

            int goId = go.GetInstanceID();
            bool isDoubleTap = _lastFocusWasClean && _lastFocusTargetId == goId;

            Debug.Log($"[EditorCamera:Focus] go={go.name} goId={goId} isDoubleTap={isDoubleTap} lastClean={_lastFocusWasClean} lastTargetId={_lastFocusTargetId}");

            if (isDoubleTap)
            {
                // Double-tap: zoom to fit object bounds
                Debug.Log("[EditorCamera:Focus] Double-tap → FocusFitBounds");
                FocusFitBounds(go);
                _lastFocusWasClean = false;
                _lastFocusTargetId = null;
            }
            else
            {
                // First tap: move to object keeping current view direction
                var targetPivot = go.transform.position;
                float dist = (Position - targetPivot).magnitude;
                if (dist < 1f) dist = 5f;
                var targetPos = targetPivot - Forward * dist;
                Debug.Log($"[EditorCamera:Focus] First-tap → objPos={go.transform.position} → targetPivot={targetPivot} targetPos={targetPos} dist={dist} Forward={Forward}");
                StartAnimation(targetPos, targetPivot);

                _lastFocusWasClean = true;
                _lastFocusTargetId = goId;
            }
        }

        private void FocusFitBounds(GameObject go)
        {
            // Calculate world-space bounds from MeshFilter if available
            var filter = go.GetComponent<MeshFilter>();
            Bounds localBounds;
            if (filter?.mesh != null)
            {
                localBounds = filter.mesh.bounds;
            }
            else
            {
                // Fallback: small default bounds
                localBounds = new Bounds(Vector3.zero, Vector3.one);
            }

            // Transform bounds to world space (approximate using scale)
            var scale = go.transform.lossyScale;
            var worldSize = new Vector3(
                localBounds.size.x * MathF.Abs(scale.x),
                localBounds.size.y * MathF.Abs(scale.y),
                localBounds.size.z * MathF.Abs(scale.z));
            var worldCenter = go.transform.TransformPoint(localBounds.center);

            // Calculate distance to fit the bounding sphere in view
            float radius = worldSize.magnitude * 0.5f;
            if (radius < 0.01f) radius = 0.5f;
            float halfFovRad = FieldOfView * 0.5f * Mathf.Deg2Rad;
            float fitDist = radius / MathF.Sin(halfFovRad);

            StartAnimation(worldCenter - Forward * fitDist, worldCenter);
        }

        /// <summary>
        /// 시야 방향을 유지한 채, 지정한 월드 좌표를 새 Pivot으로 설정하고 카메라를 이동.
        /// </summary>
        public void FocusOnPoint(Vector3 worldPoint)
        {
            float dist = (Position - Pivot).magnitude;
            if (dist < 0.1f) dist = 5f;
            StartAnimation(worldPoint - Forward * dist, worldPoint);
        }

        private void StartAnimation(Vector3 targetPos, Vector3 targetPivot)
        {
            _animStartPos = Position;
            _animStartPivot = Pivot;
            _animTargetPos = targetPos;
            _animTargetPivot = targetPivot;
            _animElapsed = 0f;
            _isAnimating = true;
        }

        private void UpdateAnimation(float dt)
        {
            _animElapsed += dt;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_animElapsed / AnimDuration));
            Position = Vector3.Lerp(_animStartPos, _animTargetPos, t);
            Pivot = Vector3.Lerp(_animStartPivot, _animTargetPivot, t);
            if (_animElapsed >= AnimDuration)
            {
                Position = _animTargetPos;
                Pivot = _animTargetPivot;
                _isAnimating = false;
            }
        }

        public void LookAt(Vector3 target)
        {
            Vector3 dir = (target - Position);
            if (dir.sqrMagnitude < 0.0001f) return;
            dir = dir.normalized;

            _yaw = MathF.Atan2(dir.x, dir.z) * (180f / MathF.PI);
            // Negate: Quaternion.Euler(pitch,…) rotates Forward.y negative for positive pitch,
            // but asin(dir.y) returns negative when looking down — signs are opposite.
            _pitch = -MathF.Asin(Math.Clamp(dir.y, -1f, 1f)) * (180f / MathF.PI);
            Rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }
    }

    /// <summary>
    /// Scene View 입력 상태. ImGui.GetIO()에서 수집하여 전달.
    /// </summary>
    public struct SceneViewInputState
    {
        // Camera modes
        public bool IsFlyMode;       // RMB held
        public bool IsOrbitMode;     // Alt + LMB
        public bool IsPanMode;       // MMB held

        // Fly WASD
        public bool MoveForward;     // W
        public bool MoveBackward;    // S
        public bool MoveLeft;        // A
        public bool MoveRight;       // D
        public bool MoveUp;          // E
        public bool MoveDown;        // Q
        public bool IsSprintHeld;    // Shift

        // Mouse
        public Vector2 MouseDelta;
        public float ScrollDelta;

        // Focus
        public bool FocusRequested;  // F key
    }
}
