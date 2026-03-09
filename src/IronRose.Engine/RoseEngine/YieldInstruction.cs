using System;
using System.Collections;

namespace RoseEngine
{
    public class YieldInstruction { }

    public class WaitForSeconds : YieldInstruction
    {
        internal float duration;
        public WaitForSeconds(float seconds) { duration = seconds; }
    }

    public class WaitForEndOfFrame : YieldInstruction { }

    public class WaitForFixedUpdate : YieldInstruction { }

    public abstract class CustomYieldInstruction : IEnumerator
    {
        public abstract bool keepWaiting { get; }
        public object? Current => null;
        public bool MoveNext() => keepWaiting;
        public void Reset() { }
    }

    public class WaitUntil : CustomYieldInstruction
    {
        private readonly Func<bool> _predicate;
        public override bool keepWaiting => !_predicate();
        public WaitUntil(Func<bool> predicate) { _predicate = predicate; }
    }

    public class WaitWhile : CustomYieldInstruction
    {
        private readonly Func<bool> _predicate;
        public override bool keepWaiting => _predicate();
        public WaitWhile(Func<bool> predicate) { _predicate = predicate; }
    }
}
