# IronRose 핫 리로드 전략 변경

## 변경 날짜
2026-02-13

## 기존 전략의 문제점

### Phase 2A: 엔진 코드 핫 리로드
```
Engine DLL 자체를 핫 리로드
→ ALC로 언로드/재로드
→ 복잡한 상태 관리 필요
→ 타입 격리 문제 (Renderer, Scene 등)
```

**문제점**:
1. **상태 보존의 복잡성**: 엔진 전체를 언로드하면 렌더링 상태, 씬 그래프, 리소스 등 모든 것을 직렬화/복원해야 함
2. **타입 격리 문제**: Veldrid, Window 등 네이티브 리소스를 가진 타입들의 마샬링 어려움
3. **디버깅 어려움**: ALC 경계를 넘나드는 버그 추적 힘듦
4. **안정성 저하**: 엔진 코어가 불안정해지면 전체 시스템 다운

---

## 새로운 전략: 플러그인 기반 핫 리로드

### 핵심 개념

#### Bootstrapper/Engine 통합

기존에는 엔진 핫 리로드를 위해 Bootstrapper와 Engine을 분리하고, ALC + 리플렉션으로 동적 로드했다.
플러그인 방식으로 전환하면 엔진 자체를 동적 로드할 필요가 없으므로, **Bootstrapper와 Engine을 다시 하나로 합친다.**

- `IronRose.Bootstrapper` 프로젝트 제거
- `IronRose.Contracts`의 `IEngineCore` 인터페이스 제거 (분리용이었으므로 불필요)
- `EngineLoader` (ALC 동적 로드) 제거
- `EngineWatcher` (엔진 빌드 감시) 제거 → 플러그인 감시로 대체
- Program.cs (엔트리 포인트, 윈도우, 메인 루프)를 `IronRose.Engine`에 통합

```
┌─────────────────────────────────────┐
│  IronRose.Engine (EXE, 단일 프로세스) │
│  - 엔트리 포인트 + 윈도우 + 메인 루프  │
│  - 핫 리로드 대상 아님                │
│  - 플러그인 진입점 제공                │
│  - 안정적인 기반 기능                 │
└─────────────────────────────────────┘
         ↓ 플러그인 API       ↓ Roslyn 컴파일
┌────────────────────┐  ┌────────────────────┐
│  Plugins (DLL)     │  │  LiveCode (*.cs)   │
│  - ALC 핫 리로드    │  │  - Roslyn 핫 리로드  │
│  - 엔진 확장 기능    │  │  - 플러그인 API 사용 │
│  - 게임 로직        │  │  - 빠른 프로토타입   │
└────────────────────┘  └────────────────────┘
```

**LiveCode와 Plugin의 관계**: LiveCode(*.cs)는 Roslyn으로 런타임 컴파일되며, 엔진이 제공하는 플러그인 API(IEngine, EnginePlugin 등)를 사용할 수 있다. Plugin은 DLL 단위 핫 리로드, LiveCode는 파일 단위 핫 리로드로, 용도에 따라 선택한다.

### 1단계: 플러그인 시스템 설계

#### 플러그인 진입점 (Engine 제공)

```csharp
// IronRose.Contracts/Plugin/EnginePlugin.cs
// abstract class + virtual 메서드. 플러그인은 필요한 훅만 override.
// 초기에는 최소 훅만 구현하고, 니즈에 따라 확장한다.
public abstract class EnginePlugin
{
    public abstract string Name { get; }
    public virtual int Priority => 0;  // 낮을수록 먼저 실행. 기본 0.

    // ── 생명주기 ──
    public abstract void OnLoad(IEngine engine);
    public virtual void OnUnload() { }

    // ── 메인 루프 ──
    public virtual void OnPreUpdate(float deltaTime) { }
    public virtual void OnPostUpdate(float deltaTime) { }

    // ── 렌더링 ──
    public virtual void OnPreRender(IRenderer renderer) { }
    public virtual void OnPostRender(IRenderer renderer) { }
}

// ── 향후 니즈에 따라 추가할 훅 후보 ──
// 메인 루프:    OnFixedUpdate, OnLateUpdate
// 렌더링:      OnRenderOverlay, OnRenderGizmos, OnViewportResize
// 씬:         OnSceneLoad/Unload/Transition
// GameObject:  OnCreated/Destroyed/Enabled/Disabled, OnComponentAdded/Removed, OnParentChanged
// 입력:        OnKeyDown/Up, OnMouseDown/Up/Move/Scroll, OnGamepad, OnTextInput
// 물리:        OnCollisionEnter/Stay/Exit, OnTriggerEnter/Exit, OnPrePhysicsStep/PostPhysicsStep
// 에셋:        OnAssetLoaded/Unloaded/Changed, OnResourceLow
// 오디오:      OnAudioPlay/Stop
// 네트워크:    OnPeerConnected/Disconnected, OnMessageReceived
// 윈도우:      OnWindowFocused/LostFocus/Minimized/Restored/Closing, OnApplicationPause/Resume
// 디버그:      OnDrawDebugUI, OnConsoleCommand, OnScreenshot
// 직렬화:      OnSaveState, OnReload

// 플러그인 매니저 (공유 ALC: 모든 플러그인이 하나의 ALC에서 로드)
public class PluginManager
{
    private AssemblyLoadContext? _pluginContext;
    private readonly List<EnginePlugin> _plugins = new();
    private readonly List<string> _pluginPaths = new();

    public void LoadPlugin(string dllPath)
    {
        _pluginContext ??= new AssemblyLoadContext("PluginContext", isCollectible: true);

        // Shadow Copy로 파일 잠금 방지 (핫 리로드 시 DLL 재빌드 가능)
        var bytes = File.ReadAllBytes(dllPath);
        var assembly = _pluginContext.LoadFromStream(new MemoryStream(bytes));
        var pluginType = assembly.GetTypes()
            .FirstOrDefault(t => typeof(EnginePlugin).IsAssignableFrom(t) && !t.IsAbstract);

        var plugin = (EnginePlugin)Activator.CreateInstance(pluginType);
        plugin.OnLoad(_engine);
        _plugins.Add(plugin);
        _pluginPaths.Add(dllPath);
        _plugins.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    // 플러그인 훅 호출 (예외 발생 시 해당 플러그인 해제)
    public void InvokeHook(Action<EnginePlugin> hook)
    {
        foreach (var plugin in _plugins.ToList())
        {
            try { hook(plugin); }
            catch (Exception ex)
            {
                Console.WriteLine($"[PluginManager] {plugin.Name} 예외 발생, 해제: {ex.Message}");
                plugin.OnUnload();
                _plugins.Remove(plugin);
            }
        }
    }

    public void HotReload()
    {
        var paths = _pluginPaths.ToList();

        // 1. 전체 언로드 (공유 ALC 한 번에)
        foreach (var plugin in _plugins)
            plugin.OnUnload();
        _plugins.Clear();
        _pluginPaths.Clear();

        _pluginContext?.Unload();
        _pluginContext = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // 2. 재빌드
        RebuildPlugins();

        // 3. 재로드
        foreach (var path in paths)
            LoadPlugin(path);
    }
}

// 사용 예시:
// pluginManager.InvokeHook(p => p.OnPreUpdate(deltaTime));
// pluginManager.InvokeHook(p => p.OnPreRender(renderer));
```

#### 엔진 확장 API (Engine 제공)

```csharp
// IronRose.Contracts/Plugin/IEngine.cs
// 최소로 시작, 니즈에 따라 확장한다.
public interface IEngine
{
    // ── 서브시스템 접근 ──
    IRenderer Renderer { get; }
    ISceneManager SceneManager { get; }
    IResourceManager Resources { get; }

    // ── 등록 ──
    void RegisterComponent<T>() where T : IComponent;

    // ── 이벤트 ──
    void Subscribe<TEvent>(Action<TEvent> handler);
    void Unsubscribe<TEvent>(Action<TEvent> handler);
    void Publish<TEvent>(TEvent eventData);

    // ── 로깅 ──
    void Log(string message);
    void LogWarning(string message);
    void LogError(string message);
}

// ── 향후 니즈에 따라 추가할 API 후보 ──
// 서브시스템:  IInputManager, IPhysicsWorld, IAudioEngine, ITimeInfo, IWindow
// 등록:       RegisterRenderPass, RegisterSystem, RegisterConsoleCommand, RegisterAssetLoader
// 코루틴:     StartCoroutine, StopCoroutine, Schedule
// 디버그:     DrawDebugLine, DrawDebugSphere, DrawDebugText
```

### 2단계: 게임 로직 플러그인

```csharp
// Game/Plugins/GameplayPlugin.cs
public class GameplayPlugin : EnginePlugin
{
    public override string Name => "Gameplay";

    private IEngine _engine;

    public override void OnLoad(IEngine engine)
    {
        _engine = engine;

        // 커스텀 컴포넌트 등록
        engine.RegisterComponent<PlayerController>();
        engine.RegisterComponent<EnemyAI>();

        // 이벤트 구독
        engine.Subscribe<CollisionEvent>(OnCollision);

        Console.WriteLine("[Plugin] Gameplay loaded");
    }

    // 필요한 훅만 override
    public override void OnPreUpdate(float deltaTime)
    {
        UpdatePlayerInput();
        UpdateAI();
    }

    public override void OnPreRender(IRenderer renderer)
    {
        DrawDebugInfo(renderer);
    }

    private void OnCollision(CollisionEvent evt)
    {
        Console.WriteLine($"[Plugin] Collision: {evt.A} <-> {evt.B}");
    }
}
```

### 3단계: AI Digest 시스템

#### 실행 후 통합 프로세스

```
┌─────────────────────────────────────┐
│  1. 런타임 코딩 & 테스트              │
│     - 플러그인으로 기능 구현           │
│     - 핫 리로드로 빠른 반복            │
└─────────────────────────────────────┘
              ↓
┌─────────────────────────────────────┐
│  2. AI Digest 과정                   │
│     - 플러그인 코드 분석               │
│     - 사용자 선택 (어떤 기능 통합?)    │
│     - 엔진 코드 생성/수정              │
└─────────────────────────────────────┘
              ↓
┌─────────────────────────────────────┐
│  3. 엔진 통합                         │
│     - 플러그인 코드 → 엔진 코드 병합   │
│     - 단위 테스트 생성                 │
│     - 문서화                          │
└─────────────────────────────────────┘
```

#### Claude Code를 활용한 AI Digest

별도의 스크립트나 명령 파일 없이, Claude Code에 자연어로 직접 지시한다.

**예시 프롬프트:**

```
GameplayPlugin을 분석해서 엔진에 통합해줘.
- PlayerController → src/IronRose.Engine/Components/PlayerController.cs로 통합
- CustomRenderPass → src/IronRose.Engine/Rendering/CustomRenderPass.cs로 통합
- EnemyAI는 아직 실험적이니 플러그인에 유지
- 통합한 코드에 대해 단위 테스트 작성하고 빌드 확인해줘
```

Claude Code가 직접 플러그인 코드를 읽고, 엔진 코드로 변환하고, 테스트 작성 및 빌드 검증까지 수행한다. 중간 계층(JSON 명령 파일, Python 스크립트) 없이 자연어 지시만으로 동일한 결과를 얻을 수 있다.

---

## 장점

### 1. 안정성
- ✅ 엔진 코어는 항상 안정적
- ✅ 플러그인 예외 발생 시 해당 플러그인만 해제 (try-catch)
- ✅ 복잡한 상태 관리 불필요

### 2. 개발 속도
- ✅ 플러그인 핫 리로드는 훨씬 빠름 (작은 DLL)
- ✅ 엔진 재컴파일 불필요
- ✅ 빠른 반복 개발 가능

### 3. 확장성
- ✅ 모듈러 아키텍처
- ⏳ 서드파티 플러그인 지원 (향후 검토)
- ✅ 기능별 독립적 개발

### 4. AI 워크플로우 최적화
- ✅ 런타임 실험 → 검증된 기능만 통합
- ✅ 점진적 엔진 개선
- ✅ 자동화된 통합 프로세스

---

## 단점

### 1. 플러그인 API 설계 필요
- ⚠️ 어떤 진입점을 제공할지 설계 필요
- **해결책**: 최소 훅(생명주기 + 메인 루프 + 렌더링)으로 시작, 니즈에 따라 확장
- 개발 중 전용이므로 API 호환성은 고려하지 않음

### 2. ~~성능 오버헤드~~ → 무시
- 플러그인은 개발 중에만 사용하고, 검증된 코드는 AI Digest로 엔진에 통합
- 릴리스에는 플러그인이 남지 않으므로 성능 오버헤드는 고려 대상 아님

### 3. ~~AI Digest 과정의 복잡도~~ → 무시
- Claude Code가 플러그인 코드를 직접 읽고 엔진 코드로 변환
- 별도의 변환 로직이나 템플릿 불필요
- 반복적인 Digest 작업은 Claude Code의 skill이나 sub agent로 구현하여 자동화 권장

---

## 구현 로드맵

### Phase 2B: 플러그인 시스템 구현 (새로운 Phase)

**목표**: 엔진 핫 리로드를 플러그인 시스템으로 대체

#### 0단계: Bootstrapper/Engine 통합 ✅ (완료: 2026-02-13)
- [x] Program.cs를 `IronRose.Engine`으로 이동
- [x] `IronRose.Engine`을 EXE 프로젝트로 변경
- [x] `IronRose.Bootstrapper` 프로젝트 제거
- [x] `IronRose.Contracts`에서 `IEngineCore` 제거, 플러그인 API 컨테이너로 전환
- [x] `EngineLoader`, `EngineWatcher` 제거
- [x] 리플렉션 제거, 직접 참조로 전환
- [x] 빌드 및 실행 확인

#### 1단계: 플러그인 인프라 (1-2일)
- [ ] `EnginePlugin` 베이스 클래스 정의
- [ ] `PluginManager` 구현
- [ ] 플러그인 생명주기 관리
- [ ] 플러그인 ALC 격리

#### 2단계: 엔진 확장 API (1-2일)
- [ ] `IEngine` 인터페이스 설계
- [ ] 컴포넌트 등록 시스템
- [ ] 이벤트 시스템 (Subscribe/Publish)

#### 3단계: 핫 리로드 (1-2일)
- [ ] `PluginWatcher`: 플러그인 프로젝트 변경 감시 → `dotnet build` → ALC 리로드
- [ ] `LiveCodeWatcher`: `LiveCode/*.cs` 변경 감시 → Roslyn 컴파일 → ScriptDomain 리로드
- [ ] 플러그인 언로드/재로드
- [ ] 상태 보존 (플러그인 레벨)

#### 4단계: 예제 플러그인 (1일)
- [ ] `HelloWorldPlugin` (기본 예제)
- [ ] `GameplayPlugin` (게임 로직)
- [ ] `DebugPlugin` (디버그 렌더링)

#### 5단계: AI Digest 워크플로우 (1일)
- [ ] Claude Code용 Digest 프롬프트 템플릿 작성
- [ ] 통합 검증 (테스트 + 빌드) 확인 절차 정리

---

## 기존 코드 처리

### Bootstrapper/Engine 통합 ✅ (완료)
- ✅ **제거 완료**: `IronRose.Bootstrapper` 프로젝트
- ✅ **전환 완료**: `IronRose.Contracts` → 플러그인 API 컨테이너로 용도 변경 (`IEngineCore` 제거)
- ✅ **제거 완료**: `EngineLoader` (ALC 동적 로드)
- ✅ **제거 완료**: `EngineWatcher` (엔진 빌드 감시)
- ✅ **제거 완료**: `bin-hot` 전략
- ✅ **통합 완료**: Program.cs → `IronRose.Engine`으로 이동
- ✅ **제거 완료**: 리플렉션 → 직접 참조로 전환
- 향후: ALC 관련 코드 → 플러그인 격리로 재사용
- 향후: PluginWatcher + LiveCodeWatcher 구현

---

## 결론

### 전략 변경 승인 여부: ✅ **승인 권장**

**이유**:
1. **더 실용적**: 엔진 핫 리로드의 복잡도 회피
2. **더 안정적**: 엔진 코어 안정성 유지
3. **더 빠른 개발**: 플러그인 핫 리로드가 더 빠름
4. **더 확장 가능**: 모듈러 아키텍처
5. **AI 워크플로우 최적화**: 실험 → 검증 → 통합 파이프라인

### 다음 액션
1. ~~**Bootstrapper/Engine 통합**~~ ✅ 완료
2. ~~**코드 정리**~~ ✅ 완료
3. ~~**문서 업데이트**~~ ✅ 완료 (Phase2A → 폐기 표시, 모든 plan 문서 반영)
4. **구현 시작** (다음 단계)
   - `EnginePlugin` 베이스 클래스부터 시작
   - 플러그인 핫 리로드 구현

---

## 추가 고려사항

### 1. 플러그인 격리 수준: 공유 ALC (결정)
- 모든 플러그인을 하나의 ALC에서 로드
- 핫 리로드 시 ALC 전체를 언로드/재로드

### 2. ~~플러그인 의존성/버전 관리~~ → 불필요
- 개발 중 전용이므로 의존성 관리, 버전 호환성은 고려하지 않음
- 실행 순서가 중요한 경우 Priority로 제어
