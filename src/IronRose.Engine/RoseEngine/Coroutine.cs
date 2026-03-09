using System.Collections;

namespace RoseEngine
{
    public class Coroutine : YieldInstruction
    {
        internal IEnumerator routine;
        internal MonoBehaviour owner;
        internal float waitTimer;
        internal bool isDone;

        internal Coroutine(IEnumerator routine, MonoBehaviour owner)
        {
            this.routine = routine;
            this.owner = owner;
        }
    }
}
