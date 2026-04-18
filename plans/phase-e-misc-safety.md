# Phase E: Animator / PhysicsWorld3D / PlayerPrefs / Texture2D / EditorAssetSelection — 기타 스레드 안전 이슈 일괄 정리

## 목표

마스터 계획 `plans/threading-safety-fix-master.md` §Phase E (라인 365-422) 의 다음 5개 **잔여 이슈**를 일괄 정리한다. 각 수정은 독립적이며 범위가 작다.

| 서브 | 이슈 ID | 파일 | 요지 |
|------|---------|------|------|
| E-1 | **H3** | `src/IronRose.Engine/RoseEngine/Animator.cs` | `SampleAt`/`CapturePreviewSnapshot`/`RestorePreviewSnapshot` 에 `Volatile.Read` 적용 + `Update` Parallel.For 람다에서 target null 방어 |
| E-2 | **M3** | `src/IronRose.Physics/PhysicsWorld3D.cs` | `Step()` 내부의 `_contactCollector.Flush(...)` 호출부를 메인 전용 불변식으로 문서화 + `ThreadGuard.CheckMainThread` 가드 |
| E-3 | **M1** | `src/IronRose.Engine/RoseEngine/PlayerPrefs.cs` | `Save()` 에서 파일 I/O 를 lock 밖으로 이동 (snapshot 패턴) |
| E-4 | **M2** | `src/IronRose.Engine/RoseEngine/Texture2D.cs` | `DefaultNormal`/`DefaultMRO` 를 `Lazy<Texture2D>` (ExecutionAndPublication) 로 교체 |
| E-5 | **M4** | `src/IronRose.Engine/Editor/EditorAssetSelection.cs` | 모든 public API 진입부에 `ThreadGuard.CheckMainThread` 삽입 |

## 선행 조건

- **Phase A 머지 완료** (= `RoseEngine.ThreadGuard` 사용 가능).
- Phase B/C/D 머지 완료 (E-2/E-5 에서 암시적으로 의존되지는 않으나, `main` 최신 상태 기준 작업).

## Worktree 전략

- **단일 worktree**: `feat/phase-e-misc-safety`.
- 5개 파일이 서로 독립적이고 변경량이 작아 한 번의 `aca-coder` 호출로 순차 편집 → 빌드 성공 → 단일 커밋 → 리뷰.

---

## 배경 (aca-coder 가 파일을 다시 열지 않아도 되도록)

### 이미 정해진 규칙

- `ThreadGuard` 네임스페이스: `RoseEngine`. 위치: `src/IronRose.Contracts/ThreadGuard.cs`. 시그니처:
  ```csharp
  public static bool CheckMainThread(string context);  // 위반 시 LogError 후 false, 캡처 전이면 true, throw 없음
  [Conditional("DEBUG")] public static void DebugCheckMainThread(string context);
  ```
- 위반 시 **throw 금지**: `return` 또는 기본값 반환으로 fallback. 핫패스가 아니면 `CheckMainThread` (릴리스 포함), 초고빈도 핫패스만 `DebugCheckMainThread`.
- `IronRose.Physics` / `IronRose.Engine` 모두 `IronRose.Contracts` 를 참조 중이므로 `using RoseEngine;` 만 필요 시 추가하면 된다.
- 커밋 메시지 컨벤션: `fix(thread): ...` 또는 `refactor(thread): ...` 형식 (기존 phase 들 관례).

### 현재 코드 확인 결과 (정확한 라인 기반)

- `Animator.cs`:
  - `_targets` 필드: line 25 (`private PropertyTarget[]? _targets;`)
  - `SampleAt`: line 80-89
  - `InvalidateTargets`: line 95-98 (이미 `Volatile.Write`)
  - `CapturePreviewSnapshot`: line 110-120
  - `RestorePreviewSnapshot`: line 125-134
  - `Update`: line 139-181 (line 161 에 이미 `var targets = Volatile.Read(ref _targets);` 있음, Parallel.For 는 line 165-168)
  - `BuildTargets`: line 302-333 (이미 `Volatile.Write(ref _targets, …)`)

- `PhysicsWorld3D.cs`:
  - `Step(float)`: line 337-348. line 347 에 `_contactCollector.Flush(...)` 호출.
  - `_contactCollector` 필드: line 309.
  - `NarrowPhaseCallbacks.ConfigureContactManifold` 가 BepuPhysics worker thread 에서 `_collector.RecordContact(...)` 호출 (line 952-968). 이는 **lock 으로 보호됨**.
  - `ContactEventCollector.Flush`: line 66-92 — lock 없이 `_currentContacts` / `_previousContacts` 접근. `Flush` 는 `Step()` 안에서, `Step()` 은 메인 스레드에서만 호출되는 것이 설계 전제이지만 **현재 런타임 가드 없음**.
  - `using RoseEngine;` 는 이미 line 37 에 존재.

- `PlayerPrefs.cs`:
  - `_data` dict: line 35.
  - `_lock`: line 36.
  - `_dirty`: line 37.
  - `Save()` public: line 184-190.
  - `Shutdown()`: line 208-215 (lock 안에서 `SaveInternal` 호출).
  - `SaveInternal()`: line 271-304 — `_data` 순회 → `config` 빌드 → `config.SaveToFile(filePath, "[PlayerPrefs]")` (line 302) + `_dirty = false` (line 303).
  - `PrefEntry` 는 `readonly record struct` (line 33) — immutable value, snapshot 시 얕은 복사로 충분.

- `Texture2D.cs`:
  - `private static Texture2D? _defaultNormal;` line 208.
  - `private static Texture2D? _defaultMRO;` line 209.
  - `public static Texture2D DefaultNormal => _defaultNormal ??= CreateDefaultNormal();` line 212.
  - `public static Texture2D DefaultMRO => _defaultMRO ??= CreateDefaultMRO();` line 215.
  - 외부에서 `_defaultNormal` / `_defaultMRO` 에 **직접 대입하는 코드는 없음** (전체 src 검색 결과 확인). `RenderSystem.cs:601-604` 는 `Texture2D.DefaultNormal` getter 만 호출 후 로컬 필드(`_defaultNormalTexture`) 에 저장. 따라서 `Lazy<T>` 로 교체해도 안전.
  - 상단 `using System;` 이 이미 있어 `Lazy<T>` 사용에 추가 using 불필요.
  - `CreateDefaultNormal()` / `CreateDefaultMRO()` 는 line 194-198 / 201-205 에 존재하며 **GPU 접근 없음** (단순 1x1 byte 배열 `Texture2D` 생성). 스레드 안전.

- `EditorAssetSelection.cs`:
  - 네임스페이스: `IronRose.Engine.Editor`.
  - 현재 `using`: `System`, `System.Collections.Generic`, `System.Linq` (line 23-25).
  - public API (정확히 이 목록):
    - `Contains(string) : bool` — line 54-58
    - `Select(string) : void` — line 61-79
    - `SelectMany(IEnumerable<string>) : void` — line 82-112
    - `Add(string) : void` — line 115-133
    - `Remove(string) : void` — line 136-143
    - `Clear() : void` — line 146-152
    - 프로퍼티 `SelectionVersion`, `PrimaryPath`, `Paths`, `Count`, event `SelectionChanged` — **가드 대상 아님** (읽기 전용 또는 event add/remove. 다만 `SelectionChanged` += /-= 는 이벤트 컴파일러 생성 acessor 가 `Interlocked.CompareExchange` 기반이라 race 없음).
  - 가드가 필요한 진입점은 **쓰기 전용 메서드 6개**: `Contains`, `Select`, `SelectMany`, `Add`, `Remove`, `Clear`.
    - 단, `Contains` 는 읽기이지만 `_paths`/`_pathSet` 을 동시에 건드리는 쓰기 호출과 경합하면 여전히 위험. 포함 대상으로 유지.

---

## E-1. Animator `_targets` 순회 보호 (H3)

**파일**: `src/IronRose.Engine/RoseEngine/Animator.cs`

### 변경 1 — `SampleAt`: `_targets` 를 로컬 스냅샷으로 순회

라인 80-89 (`public void SampleAt(float time) { ... }`) 전체를 교체.

**old_string**:
```csharp
        public void SampleAt(float time)
        {
            if (clip == null) return;
            if (_targets == null || _targets.Length == 0)
                BuildTargets();
            if (_targets == null) return;

            for (int i = 0; i < _targets.Length; i++)
                _targets[i].Evaluate(time);
        }
```

**new_string**:
```csharp
        public void SampleAt(float time)
        {
            if (clip == null) return;
            if (_targets == null || _targets.Length == 0)
                BuildTargets();

            // InvalidateTargets 가 다른 스레드/경로에서 _targets 를 null 로 치환할 수 있으므로
            // Volatile.Read 로 스냅샷을 확보한 뒤 로컬 배열만 순회한다.
            var targets = Volatile.Read(ref _targets);
            if (targets == null) return;

            for (int i = 0; i < targets.Length; i++)
            {
                var t = targets[i];
                if (t == null) continue;
                t.Evaluate(time);
            }
        }
```

### 변경 2 — `CapturePreviewSnapshot`: 동일 패턴

라인 110-120 전체를 교체.

**old_string**:
```csharp
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
```

**new_string**:
```csharp
        public void CapturePreviewSnapshot()
        {
            if (clip == null) return;
            if (_targets == null || _targets.Length == 0)
                BuildTargets();

            var targets = Volatile.Read(ref _targets);
            if (targets == null || targets.Length == 0) return;

            _previewSnapshot = new float[targets.Length];
            for (int i = 0; i < targets.Length; i++)
            {
                var t = targets[i];
                _previewSnapshot[i] = t == null ? 0f : t.ReadCurrentValue();
            }
        }
```

### 변경 3 — `RestorePreviewSnapshot`: 동일 패턴

라인 125-134 전체를 교체.

**old_string**:
```csharp
        public void RestorePreviewSnapshot()
        {
            if (_previewSnapshot == null || _targets == null) return;

            int count = Math.Min(_previewSnapshot.Length, _targets.Length);
            for (int i = 0; i < count; i++)
                _targets[i].ApplyValue(_previewSnapshot[i]);

            _previewSnapshot = null;
        }
```

**new_string**:
```csharp
        public void RestorePreviewSnapshot()
        {
            var targets = Volatile.Read(ref _targets);
            if (_previewSnapshot == null || targets == null) return;

            int count = Math.Min(_previewSnapshot.Length, targets.Length);
            for (int i = 0; i < count; i++)
            {
                var t = targets[i];
                if (t == null) continue;
                t.ApplyValue(_previewSnapshot[i]);
            }

            _previewSnapshot = null;
        }
```

### 변경 4 — `Update` Parallel.For 람다에서 null 방어

라인 163-169 의 `if (targets.Length > 16) { Parallel.For(...) }` 분기. 이미 `targets` 로컬 변수를 사용 중이지만, `Parallel.For` 람다 안에서 `targets[i]` 가 null 일 때 NRE 방어를 추가한다. 같은 파일 `for` 분기도 동일하게 가드.

**old_string**:
```csharp
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
```

**new_string**:
```csharp
            // ── 멀티스레드: Curve Evaluate + 프로퍼티 적용 ──
            var targets = Volatile.Read(ref _targets);
            if (targets == null) return;
            if (targets.Length > 16) // 타겟이 많으면 병렬 처리
            {
                Parallel.For(0, targets.Length, i =>
                {
                    // 에디터/런타임에서 clip 교체로 배열 원소가 null 인 상태가 관측될 수 있다.
                    var t = targets[i];
                    if (t == null) return;
                    t.Evaluate(evalTime);
                });
            }
            else
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    var t = targets[i];
                    if (t == null) continue;
                    t.Evaluate(evalTime);
                }
            }
```

### 검증

- [ ] `dotnet build` 성공.
- [ ] 에디터 Animator 프리뷰 ON 상태에서 Inspector 로 `clip` 교체 반복 → NRE 없음.
- [ ] 많은 타겟(>16)이 있는 clip 재생 중 clip 교체 → crash 없음.

---

## E-2. PhysicsWorld3D `Step`/`Flush` 메인 스레드 불변식 (M3)

**파일**: `src/IronRose.Physics/PhysicsWorld3D.cs`

### 변경 1 — 클래스 요지 주석에 스레드 모델 명시

`public class PhysicsWorld3D : IDisposable` (line 296) 바로 위에 XML doc 추가. 현재 이 위치에는 주석이 없다.

**old_string**:
```csharp
    public class PhysicsWorld3D : IDisposable
    {
        private Simulation _simulation = null!;
```

**new_string**:
```csharp
    /// <summary>
    /// BepuPhysics Simulation 래퍼. 3D 물리 월드를 관리한다.
    /// 스레드 모델:
    /// - <see cref="Step"/> 및 <see cref="Flush"/> / <see cref="Reset"/> / <see cref="Dispose"/> 는 **메인 스레드 전용**.
    /// - Body/Static 추가·제거·수정 계열 API 도 메인 스레드 전용.
    /// - <see cref="ContactEventCollector.RecordContact"/> 는 BepuPhysics worker thread 에서 호출되며 내부 lock 으로 보호된다.
    /// - <see cref="ContactEventCollector.Flush"/> 는 Step 내부에서만 호출되며, 이 시점에는 worker thread 의 RecordContact 가 완료된 것이 보장된다.
    /// </summary>
    public class PhysicsWorld3D : IDisposable
    {
        private Simulation _simulation = null!;
```

### 변경 2 — `Step()` 진입부에 `ThreadGuard.CheckMainThread` 삽입

라인 337-348 의 `Step(float deltaTime)`. `using RoseEngine;` 는 이미 line 37 에 존재하므로 추가 using 불필요.

**old_string**:
```csharp
        public void Step(float deltaTime)
        {
            _stepCount++;
            if (_stepCount <= 3 || _stepCount % 300 == 0)
            {
                EditorDebug.Log($"[Physics3D:Step#{_stepCount}] bodies={_simulation.Bodies.ActiveSet.Count} statics={_simulation.Statics.Count} noGravity={_noGravityBodies.Count} ({string.Join(",", _noGravityBodies)})");
            }
            _simulation.Timestep(deltaTime, _threadDispatcher);

            // Narrow phase에서 수집된 접촉 쌍을 Enter/Stay/Exit로 분류
            _contactCollector.Flush(out _enteredPairs, out _stayingPairs, out _exitedPairs);
        }
```

**new_string**:
```csharp
        public void Step(float deltaTime)
        {
            // 메인 전용 — 위반 시 LogError 후 안전하게 조기 리턴 (unsafe 상태에서 Timestep 호출 금지).
            if (!ThreadGuard.CheckMainThread("PhysicsWorld3D.Step")) return;

            _stepCount++;
            if (_stepCount <= 3 || _stepCount % 300 == 0)
            {
                EditorDebug.Log($"[Physics3D:Step#{_stepCount}] bodies={_simulation.Bodies.ActiveSet.Count} statics={_simulation.Statics.Count} noGravity={_noGravityBodies.Count} ({string.Join(",", _noGravityBodies)})");
            }
            _simulation.Timestep(deltaTime, _threadDispatcher);

            // Narrow phase에서 수집된 접촉 쌍을 Enter/Stay/Exit로 분류.
            // _contactCollector.Flush 자체는 lock 이 없으나, 호출 시점이 Timestep 직후이고
            // 메인 스레드이므로 worker thread 의 RecordContact 는 이미 종료되어 있다.
            _contactCollector.Flush(out _enteredPairs, out _stayingPairs, out _exitedPairs);
        }
```

### 변경 3 — `ContactEventCollector` 클래스 주석 보강

라인 46 의 `internal class ContactEventCollector` 상단 주석을 **보강**한다.

**old_string**:
```csharp
    /// <summary>현재 프레임에서 접촉 중인 collidable 쌍을 수집합니다. 멀티스레드 안전.</summary>
    internal class ContactEventCollector
```

**new_string**:
```csharp
    /// <summary>
    /// 현재 프레임에서 접촉 중인 collidable 쌍을 수집합니다.
    /// 스레드 모델:
    /// - <see cref="RecordContact"/> 는 BepuPhysics worker thread 에서 호출되며 내부 lock 으로 보호.
    /// - <see cref="Flush"/> / <see cref="Clear"/> / <see cref="GetContactingIds"/> 는 **메인 스레드 전용**.
    ///   Timestep 종료 직후에 호출되므로 worker thread 의 RecordContact 와 시간적으로 겹치지 않는다.
    /// </summary>
    internal class ContactEventCollector
```

### 검증

- [ ] `dotnet build` 성공.
- [ ] 플레이모드 시작/정지 반복 시 로그에 `[ThreadGuard] PhysicsWorld3D.Step must be called on main thread` 출력 없음 (정상 경로는 모두 메인).
- [ ] 게임 실행 중 물리 Step 정상 동작 (기존 동작 유지).

---

## E-3. PlayerPrefs `Save()` lock 길이 단축 (M1)

**파일**: `src/IronRose.Engine/RoseEngine/PlayerPrefs.cs`

### 변경 1 — `Save()` 를 snapshot → lock 밖 I/O 패턴으로 재작성

라인 184-190 의 `Save()` 와 라인 271-304 의 `SaveInternal()` 를 함께 변경한다.

**접근 방식**:
1. `Save()` 는 lock 안에서 `_data` 의 얕은 복사본(`Dictionary<string, PrefEntry>`)을 만들고 `_dirty = false` 처리.
2. 복사본을 lock 밖에서 `SaveInternal(snapshot)` 에 전달 — 이 호출이 TOML 직렬화 + 파일 쓰기를 담당.
3. `Shutdown()` 도 동일 패턴으로 교체.
4. 파일 I/O 예외가 발생하면 **`_dirty` 를 원상복구**하여 다음 호출에서 재시도 가능하게 한다 (기존 동작과 호환되도록, 예외는 그대로 throw).

**old_string 1** (라인 184-190):
```csharp
        public static void Save()
        {
            lock (_lock)
            {
                SaveInternal();
            }
        }
```

**new_string 1**:
```csharp
        public static void Save()
        {
            // 1) lock 안에서는 얕은 복사본만 만든다 (파일 I/O 를 들고 있지 않는다).
            Dictionary<string, PrefEntry> snapshot;
            lock (_lock)
            {
                EnsureLoaded();
                snapshot = new Dictionary<string, PrefEntry>(_data);
                _dirty = false;
            }

            // 2) lock 밖에서 실제 파일 쓰기 수행. 예외 시 _dirty 복구.
            try
            {
                SaveInternal(snapshot);
            }
            catch
            {
                lock (_lock) { _dirty = true; }
                throw;
            }
        }
```

**old_string 2** (라인 208-215, `Shutdown()`):
```csharp
        internal static void Shutdown()
        {
            lock (_lock)
            {
                if (_dirty)
                    SaveInternal();
            }
        }
```

**new_string 2**:
```csharp
        internal static void Shutdown()
        {
            Dictionary<string, PrefEntry>? snapshot = null;
            lock (_lock)
            {
                if (_dirty)
                {
                    snapshot = new Dictionary<string, PrefEntry>(_data);
                    _dirty = false;
                }
            }

            if (snapshot != null)
            {
                try
                {
                    SaveInternal(snapshot);
                }
                catch
                {
                    lock (_lock) { _dirty = true; }
                    throw;
                }
            }
        }
```

**old_string 3** (라인 271-304, `SaveInternal()` 시그니처 및 본문):
```csharp
        private static void SaveInternal()
        {
            // lock 내부에서만 호출됨 (lock은 호출자가 잡음)
            var config = TomlConfig.CreateEmpty();

            // 타입별 섹션 생성
            var intSection = TomlConfig.CreateEmpty();
            var floatSection = TomlConfig.CreateEmpty();
            var stringSection = TomlConfig.CreateEmpty();

            foreach (var kvp in _data)
            {
                switch (kvp.Value.Type)
                {
                    case PrefType.Int:
                        intSection.SetValue(kvp.Key, (long)(int)kvp.Value.Value);
                        break;
                    case PrefType.Float:
                        floatSection.SetValue(kvp.Key, (double)(float)kvp.Value.Value);
                        break;
                    case PrefType.String:
                        stringSection.SetValue(kvp.Key, (string)kvp.Value.Value);
                        break;
                }
            }

            config.SetSection(SECTION_INT, intSection);
            config.SetSection(SECTION_FLOAT, floatSection);
            config.SetSection(SECTION_STRING, stringSection);

            var filePath = GetPrefsFilePath();
            config.SaveToFile(filePath, "[PlayerPrefs]");
            _dirty = false;
        }
```

**new_string 3**:
```csharp
        /// <summary>
        /// lock 외부에서 호출되는 파일 I/O 경로.
        /// 인자 snapshot 은 호출자가 lock 안에서 확보한 _data 의 얕은 복사본이다.
        /// _data / _dirty 에 직접 접근하지 않는다.
        /// </summary>
        private static void SaveInternal(Dictionary<string, PrefEntry> snapshot)
        {
            var config = TomlConfig.CreateEmpty();

            // 타입별 섹션 생성
            var intSection = TomlConfig.CreateEmpty();
            var floatSection = TomlConfig.CreateEmpty();
            var stringSection = TomlConfig.CreateEmpty();

            foreach (var kvp in snapshot)
            {
                switch (kvp.Value.Type)
                {
                    case PrefType.Int:
                        intSection.SetValue(kvp.Key, (long)(int)kvp.Value.Value);
                        break;
                    case PrefType.Float:
                        floatSection.SetValue(kvp.Key, (double)(float)kvp.Value.Value);
                        break;
                    case PrefType.String:
                        stringSection.SetValue(kvp.Key, (string)kvp.Value.Value);
                        break;
                }
            }

            config.SetSection(SECTION_INT, intSection);
            config.SetSection(SECTION_FLOAT, floatSection);
            config.SetSection(SECTION_STRING, stringSection);

            var filePath = GetPrefsFilePath();
            config.SaveToFile(filePath, "[PlayerPrefs]");
        }
```

**주의**: `PrefEntry` 는 `readonly record struct`(line 33) 이므로 value type. `new Dictionary<string, PrefEntry>(_data)` 로 만든 얕은 복사본은 키(string, immutable)와 값(record struct, immutable) 모두 불변이라 스냅샷으로서 충분하다.

### 검증

- [ ] `dotnet build` 성공.
- [ ] 플레이모드 진행 중 스크립트에서 `PlayerPrefs.SetInt` 루프 후 `PlayerPrefs.Save()` 호출 → 정상 저장, 프레임 hitches 감소 (파일 I/O 중 `Get*` 블로킹 없음).
- [ ] 저장 파일 권한 차단 시 예외가 호출자에 전파되고 `_dirty` 가 복구되어 다음 `Save()` 시 다시 기록 시도.
- [ ] 앱 종료 (`Shutdown()`) 시 기존과 동일하게 dirty 상태면 저장됨.

---

## E-4. Texture2D `DefaultNormal`/`DefaultMRO` race-safe lazy (M2)

**파일**: `src/IronRose.Engine/RoseEngine/Texture2D.cs`

### 변경 — `??=` 를 `Lazy<Texture2D>` (ExecutionAndPublication) 로 교체

`using System.Threading;` 추가가 **필요하다** (현재 상단에 없음). `using System;` 만으로는 `LazyThreadSafetyMode` 접근 불가 → `System.Threading` 네임스페이스 추가 필수.

**old_string 1** (라인 29-36, using 블록 상단):
```csharp
using System;
using System.IO;
using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Veldrid;
```

**new_string 1**:
```csharp
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Veldrid;
```

**old_string 2** (라인 207-215):
```csharp
        // Shared default texture instances (lazy-initialized)
        private static Texture2D? _defaultNormal;
        private static Texture2D? _defaultMRO;

        /// <summary>Shared flat normal map (1x1). Thread-safe lazy init.</summary>
        public static Texture2D DefaultNormal => _defaultNormal ??= CreateDefaultNormal();

        /// <summary>Shared default MRO map (M=0, R=0.5, O=1). Thread-safe lazy init.</summary>
        public static Texture2D DefaultMRO => _defaultMRO ??= CreateDefaultMRO();
```

**new_string 2**:
```csharp
        // Shared default texture instances (thread-safe lazy).
        // LazyThreadSafetyMode.ExecutionAndPublication: 여러 스레드가 동시에 Value 를 읽어도
        // CreateDefault* 는 정확히 한 번만 실행된다. _defaultNormal/_defaultMRO 필드 대입은 없음
        // (Lazy<T>.Value 가 내부 초기화를 관리).
        private static readonly Lazy<Texture2D> _defaultNormal =
            new(CreateDefaultNormal, LazyThreadSafetyMode.ExecutionAndPublication);
        private static readonly Lazy<Texture2D> _defaultMRO =
            new(CreateDefaultMRO, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Shared flat normal map (1x1). Thread-safe lazy init.</summary>
        public static Texture2D DefaultNormal => _defaultNormal.Value;

        /// <summary>Shared default MRO map (M=0, R=0.5, O=1). Thread-safe lazy init.</summary>
        public static Texture2D DefaultMRO => _defaultMRO.Value;
```

**주의 사항**:
- `_defaultNormal` / `_defaultMRO` 의 타입이 `Texture2D?` → `Lazy<Texture2D>` 로 변경되는 것이므로, **외부에서 이 필드에 대입하는 코드가 없는지** 재확인했다. `grep` 결과: `src/IronRose.Engine/RenderSystem.cs` 는 `_defaultNormalTexture` / `_defaultMROTexture` 라는 **다른 이름의 인스턴스 필드**를 사용. 전역 `_defaultNormal`/`_defaultMRO` 에 직접 쓰는 호출자는 전무하다 (이름이 다르므로 컴파일 에러로도 즉시 드러난다).
- `CreateDefaultNormal()` / `CreateDefaultMRO()` 는 GPU 리소스 없이 순수 byte 배열로 `Texture2D` 생성만 수행하므로, 어느 스레드에서 최초 초기화되더라도 안전하다.

### 검증

- [ ] `dotnet build` 성공 (다른 파일에서 `Texture2D._defaultNormal`/`_defaultMRO` 에 접근하는 코드가 있다면 빌드가 실패한다 — 수정 전 `grep` 결과로는 없음이 확인됨).
- [ ] 에디터 시작 시 `Texture2D.DefaultNormal` / `DefaultMRO` 가 최초 `UploadToGPU` 호출에서 정상 생성되어 사용됨.
- [ ] 동일 프로세스 내에서 `DefaultNormal` / `DefaultMRO` 가 여러 번 호출되어도 **단일 인스턴스**가 반환됨 (기존과 동일).

---

## E-5. EditorAssetSelection public API 가드 (M4)

**파일**: `src/IronRose.Engine/Editor/EditorAssetSelection.cs`

### 변경 1 — `using RoseEngine;` 추가

현재 `using System; using System.Collections.Generic; using System.Linq;` 만 존재. `ThreadGuard` 접근을 위해 추가 필요.

**old_string**:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace IronRose.Engine.Editor
```

**new_string**:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using RoseEngine;

namespace IronRose.Engine.Editor
```

### 변경 2 — 6개 public 쓰기/조회 메서드 진입부에 가드 삽입

**가이드라인**:
- `CheckMainThread` (Debug/Release 공통) 사용. 이 API 는 핫패스가 아니다.
- 위반 시 **throw 없이 void 는 return, bool 은 false 반환**.
- 위반 로그는 `ThreadGuard` 내부 5초 쿨다운으로 홍수 억제됨.

#### (1) `Contains`

**old_string**:
```csharp
        /// <summary>O(1) 포함 여부.</summary>
        public static bool Contains(string path)
        {
            var normalized = Normalize(path);
            return normalized != null && _pathSet.Contains(normalized);
        }
```

**new_string**:
```csharp
        /// <summary>O(1) 포함 여부. 메인 스레드 전용 (내부 컬렉션이 lock-free).</summary>
        public static bool Contains(string path)
        {
            if (!ThreadGuard.CheckMainThread("EditorAssetSelection.Contains")) return false;
            var normalized = Normalize(path);
            return normalized != null && _pathSet.Contains(normalized);
        }
```

#### (2) `Select`

**old_string**:
```csharp
        /// <summary>단일 선택으로 교체. null/empty면 Clear와 동일.</summary>
        public static void Select(string path)
        {
            var normalized = Normalize(path);
            if (normalized == null)
            {
                Clear();
                return;
            }
```

**new_string**:
```csharp
        /// <summary>단일 선택으로 교체. null/empty면 Clear와 동일. 메인 스레드 전용.</summary>
        public static void Select(string path)
        {
            if (!ThreadGuard.CheckMainThread("EditorAssetSelection.Select")) return;

            var normalized = Normalize(path);
            if (normalized == null)
            {
                Clear();
                return;
            }
```

> 주: `Clear()` 는 같은 파일 내부 호출이지만 `Clear()` 자체도 가드가 있어 중복 체크된다. 중복은 성능상 무시 가능 (`CheckMainThread` 가 메인이면 즉시 true 반환).

#### (3) `SelectMany`

**old_string**:
```csharp
        /// <summary>여러 개로 교체. 순서대로 추가하며 마지막이 Primary가 된다.</summary>
        public static void SelectMany(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                Clear();
                return;
            }
```

**new_string**:
```csharp
        /// <summary>여러 개로 교체. 순서대로 추가하며 마지막이 Primary가 된다. 메인 스레드 전용.</summary>
        public static void SelectMany(IEnumerable<string> paths)
        {
            if (!ThreadGuard.CheckMainThread("EditorAssetSelection.SelectMany")) return;

            if (paths == null)
            {
                Clear();
                return;
            }
```

#### (4) `Add`

**old_string**:
```csharp
        /// <summary>기존 선택에 추가. 이미 있으면 Primary로 끌어올린다.</summary>
        public static void Add(string path)
        {
            var normalized = Normalize(path);
            if (normalized == null) return;
```

**new_string**:
```csharp
        /// <summary>기존 선택에 추가. 이미 있으면 Primary로 끌어올린다. 메인 스레드 전용.</summary>
        public static void Add(string path)
        {
            if (!ThreadGuard.CheckMainThread("EditorAssetSelection.Add")) return;

            var normalized = Normalize(path);
            if (normalized == null) return;
```

#### (5) `Remove`

**old_string**:
```csharp
        /// <summary>선택 해제. 없으면 no-op.</summary>
        public static void Remove(string path)
        {
            var normalized = Normalize(path);
            if (normalized == null) return;
            if (!_pathSet.Remove(normalized)) return;
            _paths.Remove(normalized);
            BumpAndNotify();
        }
```

**new_string**:
```csharp
        /// <summary>선택 해제. 없으면 no-op. 메인 스레드 전용.</summary>
        public static void Remove(string path)
        {
            if (!ThreadGuard.CheckMainThread("EditorAssetSelection.Remove")) return;

            var normalized = Normalize(path);
            if (normalized == null) return;
            if (!_pathSet.Remove(normalized)) return;
            _paths.Remove(normalized);
            BumpAndNotify();
        }
```

#### (6) `Clear`

**old_string**:
```csharp
        /// <summary>전체 해제. 이미 비어있으면 no-op.</summary>
        public static void Clear()
        {
            if (_paths.Count == 0) return;
            _paths.Clear();
            _pathSet.Clear();
            BumpAndNotify();
        }
```

**new_string**:
```csharp
        /// <summary>전체 해제. 이미 비어있으면 no-op. 메인 스레드 전용.</summary>
        public static void Clear()
        {
            if (!ThreadGuard.CheckMainThread("EditorAssetSelection.Clear")) return;

            if (_paths.Count == 0) return;
            _paths.Clear();
            _pathSet.Clear();
            BumpAndNotify();
        }
```

### 검증

- [ ] `dotnet build` 성공.
- [ ] 에디터에서 Project 패널 클릭, Ctrl+클릭 멀티 선택, Escape 등 기존 UX 정상 동작 (모든 호출이 메인이므로 로그 없음).
- [ ] CLI `asset.select` 계열 핸들러가 `ExecuteOnMainThread` 로 감싸져 있는지 재확인 (Phase D-III 의 CLI 감사 결과). 혹시 누락된 경로에서 비메인 호출이 발생하면 `[ThreadGuard] EditorAssetSelection.Xxx must be called on main thread` 로그가 남는다. **로그가 찍히면 호출부를 ExecuteOnMainThread 로 감싸는 버그 수정 대상**.

---

## 전체 검증 체크리스트 (리뷰용)

- [ ] `dotnet build` 성공.
- [ ] 5개 파일 모두 편집됨:
  - `src/IronRose.Engine/RoseEngine/Animator.cs` (SampleAt/CapturePreviewSnapshot/RestorePreviewSnapshot Volatile.Read + Update null 방어)
  - `src/IronRose.Physics/PhysicsWorld3D.cs` (Step 가드 + 클래스/Collector 주석)
  - `src/IronRose.Engine/RoseEngine/PlayerPrefs.cs` (Save/Shutdown snapshot 패턴 + SaveInternal 시그니처 변경)
  - `src/IronRose.Engine/RoseEngine/Texture2D.cs` (`Lazy<Texture2D>` 교체 + `using System.Threading;`)
  - `src/IronRose.Engine/Editor/EditorAssetSelection.cs` (6 메서드 가드 + `using RoseEngine;`)
- [ ] 스모크 테스트 (마스터 계획 §Phase E 스모크):
  1. 에디터 Animator 프리뷰 + Inspector 에서 clip 교체 반복.
  2. 플레이모드 중 `PlayerPrefs.Save` 대량 호출.
  3. 큰 씬 로드 중 PostProcessVolume 추가/삭제.
- [ ] `[ThreadGuard]` 로그가 정상 경로에서 찍히지 않음 (기존 코드 가정 깨진 곳 없음).

## 참고 / 미결

- **Animator**: `SetAxisV3`/`GetSubField` 와 `PropertyTarget.Evaluate` 는 `Parallel.For` 워커 스레드에서 실행된다. 현재 구현은 reflection + boxed struct 경로라 원자적이지 않으나, 동일 target 을 여러 워커가 동시에 건드리지 않는 한(= `Parallel.For` 의 인덱스별 분산이면 안전) race 는 없다. 본 Phase 에서는 **기존 동작 유지**가 원칙이므로 이 지점은 수정하지 않는다.
- **PhysicsWorld3D**: `Reset()`, `Dispose()`, `AddDynamic*/AddStatic*/AddKinematic*`, `RemoveBody/RemoveStatic`, `Set*Pose/Set*Velocity` 등도 메인 전용이지만, 본 Phase 는 마스터 계획의 M3 요지("`Flush` 불변식 명시 + Step 가드")에 한정한다. 추가 가드는 별도 phase 로 분리 가능.
- **PlayerPrefs**: 마스터 계획은 "디바운스 백그라운드 라이터" 를 대안으로 제시했으나 변경 범위가 커 snapshot 후 lock 밖 쓰기 패턴을 선택. 향후 저장 빈도가 올라가면 별도 phase 에서 debounced writer 도입 검토.
- **Texture2D**: `Lazy<T>` 교체는 API 호환성을 유지한다 (프로퍼티 시그니처 동일). 단, 직접 필드 접근 코드가 추후 추가되지 않도록 `private static readonly` 로 고정.
- **EditorAssetSelection**: `SelectionChanged` 이벤트 add/remove accessor 는 C# 컴파일러가 `Interlocked.CompareExchange` 로 처리해 race 없음. 가드 대상에서 제외.
