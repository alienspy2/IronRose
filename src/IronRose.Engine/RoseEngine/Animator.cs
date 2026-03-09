using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace RoseEngine
{
    /// <summary>
    /// AnimationClip을 재생하여 프로퍼티를 시간 기반으로 변화시키는 컴포넌트.
    /// - Curve 평가 + 프로퍼티 적용: 멀티스레드 (Animator 단위 병렬)
    /// - AnimationEvent 호출: 메인 스레드 전용 (큐에 적재 후 순차 invoke)
    /// </summary>
    public class Animator : MonoBehaviour
    {
        public AnimationClip? clip;
        public float speed = 1f;

        private bool _isPlaying;
        private bool _isPaused;
        private float _elapsed;

        // 리플렉션 캐시 — Play() 시 빌드
        private PropertyTarget[]? _targets;

        // 이벤트 큐 — 멀티스레드에서 적재, Update 끝에서 메인스레드 invoke
        private readonly ConcurrentQueue<AnimationEvent> _eventQueue = new();
        private int _lastEventIndex;
        private int _loopCount; // Loop 이벤트 중복 발화 방지용

        public bool isPlaying => _isPlaying;

        /// <summary>현재 재생 위치 (초).</summary>
        public float time
        {
            get => _elapsed;
            set => _elapsed = value;
        }

        public void Play()
        {
            if (clip == null)
            {
                Debug.LogWarning($"[Animator] No clip assigned on '{gameObject.name}'");
                return;
            }

            _elapsed = 0f;
            _lastEventIndex = 0;
            _loopCount = 0;
            _isPlaying = true;
            _isPaused = false;

            BuildTargets();
            Debug.Log($"[Animator] Play '{clip.name}' on '{gameObject.name}' ({_targets?.Length ?? 0} targets)");
        }

        public void Stop()
        {
            _isPlaying = false;
            _isPaused = false;
            _elapsed = 0f;
        }

        public void Pause()
        {
            _isPaused = true;
        }

        public void Resume()
        {
            _isPaused = false;
        }

        /// <summary>
        /// 에디터 프리뷰용: 지정 시간에서 모든 커브를 평가하여 프로퍼티에 적용.
        /// 런타임 재생 상태와 무관하게 동작한다.
        /// </summary>
        public void SampleAt(float time)
        {
            if (clip == null) return;
            if (_targets == null || _targets.Length == 0)
                BuildTargets();
            if (_targets == null) return;

            for (int i = 0; i < _targets.Length; i++)
                _targets[i].Evaluate(time);
        }

        /// <summary>
        /// 현재 clip이 변경되었을 때 타겟 캐시를 무효화.
        /// 에디터에서 clip 교체 후 호출.
        /// </summary>
        public void InvalidateTargets()
        {
            Volatile.Write(ref _targets, null);
        }

        /// <summary>디버깅용: 현재 타겟 수.</summary>
        public int TargetCount => Volatile.Read(ref _targets)?.Length ?? 0;

        // ── 에디터 프리뷰 스냅샷 (원래 상태 저장/복원) ──

        private float[]? _previewSnapshot;

        /// <summary>
        /// 현재 프로퍼티 값을 스냅샷으로 저장 (에디터 프리뷰 시작 시 호출).
        /// </summary>
        public void CapturePreviewSnapshot()
        {
            if (clip == null) return;
            if (_targets == null || _targets.Length == 0)
                BuildTargets();
            if (_targets == null || _targets.Length == 0) return;

            _previewSnapshot = new float[_targets.Length];
            for (int i = 0; i < _targets.Length; i++)
                _previewSnapshot[i] = _targets[i].ReadCurrentValue();
        }

        /// <summary>
        /// CapturePreviewSnapshot()에서 저장한 값을 복원 (에디터 프리뷰 종료 시 호출).
        /// </summary>
        public void RestorePreviewSnapshot()
        {
            if (_previewSnapshot == null || _targets == null) return;

            int count = Math.Min(_previewSnapshot.Length, _targets.Length);
            for (int i = 0; i < count; i++)
                _targets[i].ApplyValue(_previewSnapshot[i]);

            _previewSnapshot = null;
        }

        /// <summary>프리뷰 스냅샷이 저장되어 있는지 여부.</summary>
        public bool HasPreviewSnapshot => _previewSnapshot != null;

        public override void Update()
        {
            if (!_isPlaying || _isPaused || clip == null || _targets == null)
                return;

            float dt = Time.deltaTime * speed;
            _elapsed += dt;

            float clipLength = clip.length;
            if (clipLength <= 0f)
                return;

            // WrapMode 적용
            float evalTime = WrapTime(_elapsed, clipLength, clip.wrapMode, out bool stopped);

            if (stopped)
            {
                _isPlaying = false;
                evalTime = clipLength; // 마지막 프레임 적용
            }

            // ── 멀티스레드: Curve Evaluate + 프로퍼티 적용 ──
            var targets = Volatile.Read(ref _targets);
            if (targets == null) return;
            if (targets.Length > 16) // 타겟이 많으면 병렬 처리
            {
                Parallel.For(0, targets.Length, i =>
                {
                    targets[i].Evaluate(evalTime);
                });
            }
            else
            {
                for (int i = 0; i < targets.Length; i++)
                    targets[i].Evaluate(evalTime);
            }

            // ── 이벤트 수집 (멀티스레드 안전 — ConcurrentQueue) ──
            CollectEvents(evalTime);

            // ── 메인 스레드: 이벤트 invoke ──
            FlushEvents();
        }

        // ================================================================
        // WrapMode 시간 변환
        // ================================================================

        private static float WrapTime(float elapsed, float length, WrapMode mode, out bool stopped)
        {
            stopped = false;

            switch (mode)
            {
                case WrapMode.Loop:
                    return elapsed % length;

                case WrapMode.PingPong:
                    float cycle = elapsed % (length * 2f);
                    return cycle <= length ? cycle : length * 2f - cycle;

                case WrapMode.ClampForever:
                    return Mathf.Clamp(elapsed, 0f, length);

                case WrapMode.Once:
                default:
                    if (elapsed >= length)
                    {
                        stopped = true;
                        return length;
                    }
                    return elapsed;
            }
        }

        // ================================================================
        // 이벤트 수집 + Flush
        // ================================================================

        private void CollectEvents(float evalTime)
        {
            var events = clip!.events;
            if (events.Count == 0) return;

            // Loop/PingPong: 새 루프 사이클 진입 시에만 이벤트 인덱스 리셋
            if ((clip.wrapMode == WrapMode.Loop || clip.wrapMode == WrapMode.PingPong) && clip.length > 0f)
            {
                int currentLoop = (int)(_elapsed / clip.length);
                if (currentLoop > _loopCount)
                {
                    _loopCount = currentLoop;
                    _lastEventIndex = 0;
                }
            }

            // 순방향 재생 기준: _lastEventIndex부터 evalTime까지의 이벤트 수집
            for (int i = _lastEventIndex; i < events.Count; i++)
            {
                if (events[i].time <= evalTime)
                {
                    _eventQueue.Enqueue(events[i]);
                    _lastEventIndex = i + 1;
                }
                else
                {
                    break;
                }
            }
        }

        private void FlushEvents()
        {
            while (_eventQueue.TryDequeue(out var evt))
            {
                InvokeEvent(evt);
            }
        }

        private void InvokeEvent(AnimationEvent evt)
        {
            if (string.IsNullOrEmpty(evt.functionName)) return;

            // 동일 GameObject의 모든 MonoBehaviour에서 메서드 검색
            var components = gameObject.GetComponents<MonoBehaviour>();
            foreach (var comp in components)
            {
                if (comp._isDestroyed || !comp.enabled) continue;

                var method = comp.GetType().GetMethod(
                    evt.functionName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (method == null) continue;

                try
                {
                    var pars = method.GetParameters();
                    if (pars.Length == 0)
                        method.Invoke(comp, null);
                    else if (pars.Length == 1)
                    {
                        var pType = pars[0].ParameterType;
                        if (pType == typeof(float))
                            method.Invoke(comp, new object[] { evt.floatParameter });
                        else if (pType == typeof(int))
                            method.Invoke(comp, new object[] { evt.intParameter });
                        else if (pType == typeof(string))
                            method.Invoke(comp, new object[] { evt.stringParameter! });
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Animator] Event '{evt.functionName}' failed: {ex.Message}");
                }

                break; // 첫 번째 매칭 컴포넌트에서만 호출
            }
        }

        // ================================================================
        // PropertyTarget 캐시 빌드
        // ================================================================

        private void BuildTargets()
        {
            if (clip == null) { Volatile.Write(ref _targets, Array.Empty<PropertyTarget>()); return; }

            var list = new List<PropertyTarget>();
            int failCount = 0;

            foreach (var (path, curve) in clip.curves)
            {
                var target = ResolveTarget(path, curve);
                if (target != null)
                {
                    list.Add(target);
                }
                else
                {
                    var parts = path.Split('.');
                    string hint = parts.Length < 2
                        ? "too few segments (need at least 'property.axis')"
                        : parts.Length >= 3 && FindTargetGameObject(parts[0]) == null
                            ? $"GameObject '{parts[0]}' not found in hierarchy"
                            : "property or component not found";
                    Debug.LogWarning($"[Animator] Cannot resolve '{path}' on '{gameObject.name}' — {hint}");
                    failCount++;
                }
            }

            Volatile.Write(ref _targets, list.ToArray());

            if (failCount > 0)
                Debug.LogWarning($"[Animator] {failCount}/{clip.curves.Count} property paths failed to resolve on '{gameObject.name}'");
        }

        /// <summary>
        /// propertyPath를 파싱하여 PropertyTarget 생성.
        ///
        /// 지원 형식:
        /// 1) "localPosition.x"                     → self Transform (레거시 단축)
        /// 2) "SpriteRenderer.color.r"              → self Component (레거시)
        /// 3) "ObjName.localPosition.x"             → named object Transform
        /// 4) "ObjName.SpriteRenderer.color.r"      → named object Component.field.sub
        /// 5) "ObjName.SpriteRenderer.alpha"         → named object Component.field
        /// </summary>
        private PropertyTarget? ResolveTarget(string path, AnimationCurve curve)
        {
            var parts = path.Split('.');

            // ── 1) Legacy: Transform shorthand on self — "localPosition.x" (2 parts) ──
            if (parts.Length == 2 && IsTransformProperty(parts[0]))
            {
                return ResolveTransformTarget(transform, parts[0], parts[1], curve);
            }

            // ── 2) New format: objectName prefix — 3+ parts ──
            if (parts.Length >= 3)
            {
                var targetGo = FindTargetGameObject(parts[0]);
                if (targetGo != null)
                {
                    // "ObjName.localPosition.x" — Transform on target object (3 parts)
                    if (parts.Length == 3 && IsTransformProperty(parts[1]))
                    {
                        return ResolveTransformTarget(targetGo.transform, parts[1], parts[2], curve);
                    }

                    // "ObjName.Component.field" (3 parts)
                    if (parts.Length == 3)
                    {
                        var comp = FindComponentByTypeName(targetGo, parts[1]);
                        if (comp != null)
                            return ResolveComponentField(comp, parts[2], null, curve);
                    }

                    // "ObjName.Component.field.sub" (4 parts)
                    if (parts.Length == 4)
                    {
                        var comp = FindComponentByTypeName(targetGo, parts[1]);
                        if (comp != null)
                            return ResolveComponentField(comp, parts[2], parts[3], curve);
                    }
                }
            }

            // ── 3) Legacy fallback: "Component.field[.sub]" on self ──
            if (parts.Length >= 2)
            {
                var comp = FindComponentByTypeName(gameObject, parts[0]);
                if (comp != null)
                {
                    if (parts.Length == 2)
                        return ResolveComponentField(comp, parts[1], null, curve);
                    if (parts.Length == 3)
                        return ResolveComponentField(comp, parts[1], parts[2], curve);
                }
            }

            return null;
        }

        /// <summary>
        /// self 또는 자식 중 이름이 일치하는 GameObject를 찾는다.
        /// </summary>
        private GameObject? FindTargetGameObject(string objName)
        {
            if (gameObject.name == objName)
                return gameObject;
            return FindDescendant(transform, objName);
        }

        private static GameObject? FindDescendant(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.gameObject.name == name)
                    return child.gameObject;
                var found = FindDescendant(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private static bool IsTransformProperty(string name)
        {
            return name == "localPosition" || name == "localScale" || name == "localEulerAngles";
        }

        private static PropertyTarget? ResolveTransformTarget(Transform target, string propName, string axis, AnimationCurve curve)
        {
            int axisIdx = AxisIndex(axis);
            if (axisIdx < 0) return null;

            return new TransformPropertyTarget(target, propName, axisIdx, curve);
        }

        private PropertyTarget? ResolveComponentField(Component comp, string fieldName, string? subField, AnimationCurve curve)
        {
            var type = comp.GetType();

            // 필드 먼저, 없으면 프로퍼티
            var fi = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (fi != null)
            {
                if (subField != null)
                    return new StructFieldSubTarget(comp, fi, subField, curve);
                else
                    return new DirectFieldTarget(comp, fi, curve);
            }

            var pi = type.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (pi != null && pi.CanRead && pi.CanWrite)
            {
                if (subField != null)
                    return new StructPropertySubTarget(comp, pi, subField, curve);
                else
                    return new DirectPropertyTarget(comp, pi, curve);
            }

            return null;
        }

        private static Component? FindComponentByTypeName(GameObject go, string typeName)
        {
            foreach (var comp in go._components)
            {
                if (comp.GetType().Name == typeName)
                    return comp;
            }
            return null;
        }

        private static int AxisIndex(string axis)
        {
            return axis switch
            {
                "x" => 0,
                "y" => 1,
                "z" => 2,
                "w" => 3,
                "r" => 0,
                "g" => 1,
                "b" => 2,
                "a" => 3,
                _ => -1,
            };
        }

        // ================================================================
        // PropertyTarget 추상 + 구현
        // ================================================================

        private abstract class PropertyTarget
        {
            protected readonly AnimationCurve _curve;
            protected PropertyTarget(AnimationCurve curve) => _curve = curve;
            public abstract void Evaluate(float time);
            public abstract float ReadCurrentValue();
            public abstract void ApplyValue(float val);
        }

        /// <summary>Transform의 localPosition/localScale/localEulerAngles 축별 타겟.</summary>
        private sealed class TransformPropertyTarget : PropertyTarget
        {
            private readonly Transform _transform;
            private readonly string _propName;
            private readonly int _axis;

            public TransformPropertyTarget(Transform transform, string propName, int axis, AnimationCurve curve)
                : base(curve)
            {
                _transform = transform;
                _propName = propName;
                _axis = axis;
            }

            public override void Evaluate(float time) => ApplyValue(_curve.Evaluate(time));

            public override float ReadCurrentValue()
            {
                return _propName switch
                {
                    "localPosition" => GetAxis(_transform.localPosition, _axis),
                    "localScale" => GetAxis(_transform.localScale, _axis),
                    "localEulerAngles" => GetAxis(_transform.localEulerAngles, _axis),
                    _ => 0f,
                };
            }

            public override void ApplyValue(float val)
            {
                switch (_propName)
                {
                    case "localPosition":
                        var pos = _transform.localPosition;
                        SetAxis(ref pos, _axis, val);
                        _transform.localPosition = pos;
                        break;
                    case "localScale":
                        var scale = _transform.localScale;
                        SetAxis(ref scale, _axis, val);
                        _transform.localScale = scale;
                        break;
                    case "localEulerAngles":
                        var euler = _transform.localEulerAngles;
                        SetAxis(ref euler, _axis, val);
                        _transform.localEulerAngles = euler;
                        break;
                }
            }

            private static float GetAxis(Vector3 v, int axis) => axis switch
            {
                0 => v.x, 1 => v.y, 2 => v.z, _ => 0f,
            };

            private static void SetAxis(ref Vector3 v, int axis, float val)
            {
                switch (axis)
                {
                    case 0: v.x = val; break;
                    case 1: v.y = val; break;
                    case 2: v.z = val; break;
                }
            }
        }

        /// <summary>float 필드 직접 대입.</summary>
        private sealed class DirectFieldTarget : PropertyTarget
        {
            private readonly object _obj;
            private readonly FieldInfo _field;

            public DirectFieldTarget(object obj, FieldInfo field, AnimationCurve curve) : base(curve)
            {
                _obj = obj;
                _field = field;
            }

            public override void Evaluate(float time) => ApplyValue(_curve.Evaluate(time));
            public override float ReadCurrentValue() => (float)(_field.GetValue(_obj) ?? 0f);
            public override void ApplyValue(float val) => _field.SetValue(_obj, val);
        }

        /// <summary>float 프로퍼티 직접 대입.</summary>
        private sealed class DirectPropertyTarget : PropertyTarget
        {
            private readonly object _obj;
            private readonly PropertyInfo _prop;

            public DirectPropertyTarget(object obj, PropertyInfo prop, AnimationCurve curve) : base(curve)
            {
                _obj = obj;
                _prop = prop;
            }

            public override void Evaluate(float time) => ApplyValue(_curve.Evaluate(time));
            public override float ReadCurrentValue() => (float)(_prop.GetValue(_obj) ?? 0f);
            public override void ApplyValue(float val) => _prop.SetValue(_obj, val);
        }

        /// <summary>struct 필드의 서브필드 (예: Color.r) — box/unbox 경유.</summary>
        private sealed class StructFieldSubTarget : PropertyTarget
        {
            private readonly object _obj;
            private readonly FieldInfo _field;
            private readonly int _subAxis;

            public StructFieldSubTarget(object obj, FieldInfo field, string subField, AnimationCurve curve) : base(curve)
            {
                _obj = obj;
                _field = field;
                _subAxis = AxisIndex(subField);
            }

            public override void Evaluate(float time) => ApplyValue(_curve.Evaluate(time));

            public override float ReadCurrentValue()
            {
                var boxed = _field.GetValue(_obj);
                return boxed == null ? 0f : GetSubField(boxed, _subAxis);
            }

            public override void ApplyValue(float val)
            {
                var boxed = _field.GetValue(_obj);
                if (boxed == null) return;
                SetSubField(ref boxed, _subAxis, val);
                _field.SetValue(_obj, boxed);
            }
        }

        /// <summary>struct 프로퍼티의 서브필드 (예: SpriteRenderer.color.r).</summary>
        private sealed class StructPropertySubTarget : PropertyTarget
        {
            private readonly object _obj;
            private readonly PropertyInfo _prop;
            private readonly int _subAxis;

            public StructPropertySubTarget(object obj, PropertyInfo prop, string subField, AnimationCurve curve) : base(curve)
            {
                _obj = obj;
                _prop = prop;
                _subAxis = AxisIndex(subField);
            }

            public override void Evaluate(float time) => ApplyValue(_curve.Evaluate(time));

            public override float ReadCurrentValue()
            {
                var boxed = _prop.GetValue(_obj);
                return boxed == null ? 0f : GetSubField(boxed, _subAxis);
            }

            public override void ApplyValue(float val)
            {
                var boxed = _prop.GetValue(_obj);
                if (boxed == null) return;
                SetSubField(ref boxed, _subAxis, val);
                _prop.SetValue(_obj, boxed);
            }
        }

        /// <summary>boxed struct에서 서브필드 읽기.</summary>
        private static float GetSubField(object boxed, int axis)
        {
            return boxed switch
            {
                Vector2 v2 => axis == 0 ? v2.x : v2.y,
                Vector3 v3 => axis switch { 0 => v3.x, 1 => v3.y, _ => v3.z },
                Vector4 v4 => axis switch { 0 => v4.x, 1 => v4.y, 2 => v4.z, _ => v4.w },
                Color c => axis switch { 0 => c.r, 1 => c.g, 2 => c.b, _ => c.a },
                Quaternion q => axis switch { 0 => q.x, 1 => q.y, 2 => q.z, _ => q.w },
                _ => 0f,
            };
        }

        /// <summary>boxed struct의 x/y/z/w(r/g/b/a) 서브필드 설정.</summary>
        private static void SetSubField(ref object boxed, int axis, float val)
        {
            switch (boxed)
            {
                case Vector2 v2:
                    if (axis == 0) v2.x = val; else v2.y = val;
                    boxed = v2; break;
                case Vector3 v3:
                    SetAxisV3(ref v3, axis, val);
                    boxed = v3; break;
                case Vector4 v4:
                    if (axis == 0) v4.x = val; else if (axis == 1) v4.y = val;
                    else if (axis == 2) v4.z = val; else v4.w = val;
                    boxed = v4; break;
                case Color c:
                    if (axis == 0) c.r = val; else if (axis == 1) c.g = val;
                    else if (axis == 2) c.b = val; else c.a = val;
                    boxed = c; break;
                case Quaternion q:
                    if (axis == 0) q.x = val; else if (axis == 1) q.y = val;
                    else if (axis == 2) q.z = val; else q.w = val;
                    boxed = q; break;
            }
        }

        private static void SetAxisV3(ref Vector3 v, int axis, float val)
        {
            switch (axis)
            {
                case 0: v.x = val; break;
                case 1: v.y = val; break;
                case 2: v.z = val; break;
            }
        }
    }
}
