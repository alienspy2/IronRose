// ------------------------------------------------------------
// @file    ComponentRegistry.cs
// @brief   정적 컬렉션 기반 컴포넌트 레지스트리의 lock/snapshot 공통 헬퍼.
//          엔진 어디에서든 이 헬퍼로 _all* 리스트를 감싸면 라이프사이클 변경과
//          외부 순회가 동기화된다. Snapshot() 은 락 내부에서 ToArray() 카피를
//          반환하므로 호출자는 안전하게 순회할 수 있다.
// @deps    (none — BCL 만 사용)
// @exports
//   sealed class ComponentRegistry<T> where T : class
//     Register(T): void
//     Unregister(T): bool
//     Clear(): void
//     Count: int
//     Contains(T): bool
//     Snapshot(): T[]
//     SnapshotWhere(Func<T,bool>): T[]
//     ForEachSnapshot(Action<T>): void
//     IndexOf/GetAt/RemoveAt/Insert
//     WithLock(Action<List<T>>): void
// @note    내부 List<T> 를 노출하지 않는다. 복합 조작은 WithLock 으로 원자성 확보.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace RoseEngine
{
    public sealed class ComponentRegistry<T> where T : class
    {
        private readonly object _lock = new();
        private readonly List<T> _items = new();

        public void Register(T item)
        {
            if (item == null) return;
            lock (_lock)
            {
                if (_items.Contains(item)) return;
                _items.Add(item);
            }
        }

        public bool Unregister(T item)
        {
            if (item == null) return false;
            lock (_lock)
            {
                return _items.Remove(item);
            }
        }

        public void Clear()
        {
            lock (_lock) { _items.Clear(); }
        }

        public int Count
        {
            get { lock (_lock) { return _items.Count; } }
        }

        public bool Contains(T item)
        {
            if (item == null) return false;
            lock (_lock) { return _items.Contains(item); }
        }

        public T[] Snapshot()
        {
            lock (_lock) { return _items.ToArray(); }
        }

        public T[] SnapshotWhere(Func<T, bool> predicate)
        {
            if (predicate == null) return Snapshot();
            lock (_lock)
            {
                var buf = new List<T>(_items.Count);
                foreach (var it in _items)
                    if (predicate(it)) buf.Add(it);
                return buf.ToArray();
            }
        }

        public void ForEachSnapshot(Action<T> action)
        {
            if (action == null) return;
            var snap = Snapshot();
            foreach (var it in snap) action(it);
        }

        public int IndexOf(T item)
        {
            if (item == null) return -1;
            lock (_lock) { return _items.IndexOf(item); }
        }

        public T GetAt(int index)
        {
            lock (_lock) { return _items[index]; }
        }

        public void RemoveAt(int index)
        {
            lock (_lock) { _items.RemoveAt(index); }
        }

        public void Insert(int index, T item)
        {
            if (item == null) return;
            lock (_lock) { _items.Insert(index, item); }
        }

        /// <summary>
        /// 복합 원자 조작용. 람다 내부에서는 lock 이 유지되므로
        /// 콜백은 즉시 리턴하는 짧은 조작만 수행해야 한다.
        /// 외부 Register/Unregister 재진입은 동일 스레드에서 허용된다 (C# lock 재진입).
        /// </summary>
        public void WithLock(Action<List<T>> action)
        {
            if (action == null) return;
            lock (_lock) { action(_items); }
        }
    }
}
