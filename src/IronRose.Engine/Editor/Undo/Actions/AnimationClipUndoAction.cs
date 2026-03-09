using System.Collections.Generic;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// AnimationClip 전체 상태 스냅샷 기반 Undo/Redo 액션.
    /// 커브 키프레임 + 이벤트 + length 를 통째로 저장/복원한다.
    /// </summary>
    public sealed class AnimationClipUndoAction : IUndoAction
    {
        public string Description { get; }

        private readonly AnimationClip _clip;
        private readonly Dictionary<string, Keyframe[]> _oldCurves;
        private readonly Dictionary<string, Keyframe[]> _newCurves;
        private readonly List<AnimationEvent> _oldEvents;
        private readonly List<AnimationEvent> _newEvents;
        private readonly float _oldLength;
        private readonly float _newLength;

        public AnimationClipUndoAction(
            string description,
            AnimationClip clip,
            Dictionary<string, Keyframe[]> oldCurves,
            List<AnimationEvent> oldEvents,
            float oldLength,
            Dictionary<string, Keyframe[]> newCurves,
            List<AnimationEvent> newEvents,
            float newLength)
        {
            Description = description;
            _clip = clip;
            _oldCurves = oldCurves;
            _newCurves = newCurves;
            _oldEvents = oldEvents;
            _newEvents = newEvents;
            _oldLength = oldLength;
            _newLength = newLength;
        }

        public void Undo() => RestoreSnapshot(_oldCurves, _oldEvents, _oldLength);
        public void Redo() => RestoreSnapshot(_newCurves, _newEvents, _newLength);

        private void RestoreSnapshot(
            Dictionary<string, Keyframe[]> curves,
            List<AnimationEvent> events,
            float length)
        {
            _clip.curves.Clear();
            foreach (var (path, keys) in curves)
            {
                var curve = new AnimationCurve();
                curve.SetKeys(keys);
                _clip.curves[path] = curve;
            }

            _clip.events.Clear();
            _clip.events.AddRange(events);
            _clip.length = length;
        }
    }
}
