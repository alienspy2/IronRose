# Phase 3: Unity Architecture 구현

## 목표
Unity의 GameObject/Component 아키텍처를 **있는 그대로** 구현합니다.
Shim(껍데기)이 아닌 실제 동작하는 엔진 구조입니다.

---

## 설계 철학

> **"Keep It Simple, Stupid (KISS)"**
>
> - ECS 변환 레이어 없음
> - 내부/외부 구조 분리 없음
> - Unity 아키텍처 그대로 구현
> - 성능 문제는 나중에 병목이 실제로 발생하면 최적화

> **플러그인 기반 핫 리로드**
>
> - 엔진은 안정적 기반으로 유지
> - 플러그인/LiveCode로 기능을 확장하고 핫 리로드
> - AI Digest로 검증된 코드를 엔진에 통합

---

## 작업 항목

### 3.1 기본 수학 타입 (IronRose.Engine)

**RoseEngine/Vector3.cs:**
```csharp
using System;

namespace RoseEngine
{
    public struct Vector3
    {
        public float x, y, z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Vector3 zero => new(0, 0, 0);
        public static Vector3 one => new(1, 1, 1);
        public static Vector3 up => new(0, 1, 0);
        public static Vector3 forward => new(0, 0, 1);
        public static Vector3 right => new(1, 0, 0);

        public float magnitude => MathF.Sqrt(x * x + y * y + z * z);
        public Vector3 normalized
        {
            get
            {
                float mag = magnitude;
                return mag > 0.00001f ? this / mag : zero;
            }
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) =>
            new(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3 operator -(Vector3 a, Vector3 b) =>
            new(a.x - b.x, a.y - b.y, a.z - b.z);

        public static Vector3 operator *(Vector3 a, float d) =>
            new(a.x * d, a.y * d, a.z * d);

        public static Vector3 operator /(Vector3 a, float d) =>
            new(a.x / d, a.y / d, a.z / d);

        public static float Dot(Vector3 a, Vector3 b) =>
            a.x * b.x + a.y * b.y + a.z * b.z;

        public static Vector3 Cross(Vector3 a, Vector3 b) =>
            new(
                a.y * b.z - a.z * b.y,
                a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x
            );

        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2})";
    }
}
```

**RoseEngine/Quaternion.cs:**
```csharp
using System;

namespace RoseEngine
{
    public struct Quaternion
    {
        public float x, y, z, w;

        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Quaternion identity => new(0, 0, 0, 1);

        public static Quaternion Euler(float x, float y, float z)
        {
            // 간단한 오일러 각도 변환 (실제로는 더 복잡)
            float cx = MathF.Cos(x * 0.5f * MathF.PI / 180f);
            float sx = MathF.Sin(x * 0.5f * MathF.PI / 180f);
            float cy = MathF.Cos(y * 0.5f * MathF.PI / 180f);
            float sy = MathF.Sin(y * 0.5f * MathF.PI / 180f);
            float cz = MathF.Cos(z * 0.5f * MathF.PI / 180f);
            float sz = MathF.Sin(z * 0.5f * MathF.PI / 180f);

            return new Quaternion(
                sx * cy * cz - cx * sy * sz,
                cx * sy * cz + sx * cy * sz,
                cx * cy * sz - sx * sy * cz,
                cx * cy * cz + sx * sy * sz
            );
        }

        public static Quaternion operator *(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
                a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z
            );
        }

        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2}, {w:F2})";
    }
}
```

**RoseEngine/Color.cs:**
```csharp
namespace RoseEngine
{
    public struct Color
    {
        public float r, g, b, a;

        public Color(float r, float g, float b, float a = 1.0f)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public static Color white => new(1, 1, 1, 1);
        public static Color black => new(0, 0, 0, 1);
        public static Color red => new(1, 0, 0, 1);
        public static Color green => new(0, 1, 0, 1);
        public static Color blue => new(0, 0, 1, 1);
    }
}
```

---

### 3.2 GameObject & Component 시스템

**Component.cs:**
```csharp
namespace RoseEngine
{
    public class Component
    {
        public GameObject gameObject { get; internal set; } = null!;
        public Transform transform { get; internal set; } = null!;
    }
}
```

**GameObject.cs:**
```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace RoseEngine
{
    public class GameObject
    {
        public string name;
        public Transform transform { get; private set; } = null!;
        private List<Component> _components = new();

        public GameObject(string name = "GameObject")
        {
            this.name = name;
            this.transform = AddComponent<Transform>();
        }

        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T();
            component.gameObject = this;
            component.transform = this.transform;
            _components.Add(component);

            // MonoBehaviour면 자동으로 씬에 등록
            if (component is MonoBehaviour mb)
            {
                SceneManager.RegisterBehaviour(mb);
            }

            return component;
        }

        public T? GetComponent<T>() where T : Component
        {
            return _components.OfType<T>().FirstOrDefault();
        }

        public T[] GetComponents<T>() where T : Component
        {
            return _components.OfType<T>().ToArray();
        }

        public static GameObject CreatePrimitive(PrimitiveType type)
        {
            var go = new GameObject($"Primitive_{type}");
            // TODO: Phase 4에서 MeshRenderer 추가
            return go;
        }
    }

    public enum PrimitiveType
    {
        Cube,
        Sphere,
        Cylinder,
        Plane
    }
}
```

**Transform.cs:**
```csharp
namespace RoseEngine
{
    public class Transform : Component
    {
        public Vector3 position = Vector3.zero;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 localScale = Vector3.one;

        public void Translate(Vector3 translation)
        {
            position += translation;
        }

        public void Rotate(float x, float y, float z)
        {
            rotation *= Quaternion.Euler(x, y, z);
        }

        public void Rotate(Vector3 eulerAngles)
        {
            rotation *= Quaternion.Euler(eulerAngles.x, eulerAngles.y, eulerAngles.z);
        }
    }
}
```

---

### 3.3 MonoBehaviour 라이프사이클

**MonoBehaviour.cs:**
```csharp
namespace RoseEngine
{
    public class MonoBehaviour : Component
    {
        public virtual void Awake() { }
        public virtual void Start() { }
        public virtual void Update() { }
        public virtual void LateUpdate() { }
        public virtual void OnDestroy() { }
    }
}
```

---

### 3.4 씬 관리 및 업데이트 루프 (IronRose.Engine)

**SceneManager.cs:**
```csharp
using System.Collections.Generic;

namespace RoseEngine
{
    public static class SceneManager
    {
        private static List<MonoBehaviour> _behaviours = new();

        public static void RegisterBehaviour(MonoBehaviour behaviour)
        {
            _behaviours.Add(behaviour);
            behaviour.Awake();
            behaviour.Start();
        }

        public static void Update(float deltaTime)
        {
            Time.deltaTime = deltaTime;
            Time.time += deltaTime;

            // Update 호출
            foreach (var behaviour in _behaviours)
            {
                behaviour.Update();
            }

            // LateUpdate 호출
            foreach (var behaviour in _behaviours)
            {
                behaviour.LateUpdate();
            }
        }

        public static void Clear()
        {
            foreach (var behaviour in _behaviours)
            {
                behaviour.OnDestroy();
            }
            _behaviours.Clear();
        }
    }
}
```

**Time.cs:**
```csharp
namespace RoseEngine
{
    public static class Time
    {
        public static float deltaTime { get; internal set; }
        public static float time { get; internal set; }
        public static int frameCount { get; internal set; }
    }
}
```

---

### 3.5 디버그 유틸리티

**Debug.cs:**
```csharp
using System;

namespace RoseEngine
{
    public static class Debug
    {
        public static void Log(object message)
        {
            Console.WriteLine($"[LOG] {message}");
        }

        public static void LogWarning(object message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[WARN] {message}");
            Console.ResetColor();
        }

        public static void LogError(object message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] {message}");
            Console.ResetColor();
        }
    }
}
```

---

### 3.6 Unity InputSystem (액션 기반 입력)

기존 `RoseEngine.Input` (레거시 입력)을 유지하면서, Unity의 새 Input System (`RoseEngine.InputSystem`) 패키지를 모사합니다.
기존 Silk.NET 입력 인프라 위에 액션 기반 API 레이어를 구축합니다.

**핵심 설계:**
- `InputAction`: 콜백 기반 입력 (started/performed/canceled 이벤트)
- `InputActionType`: Button(누름/뗌), Value(연속 값), PassThrough(매 프레임)
- `InputActionPhase`: Disabled → Waiting → Started → Performed → Canceled
- `InputControlPath`: `<Keyboard>/space` 형식 경로를 `KeyCode`로 변환, 레거시 `Input` 정적 상태 재활용
- `CompositeBinder`: `2DVector` (WASD → Vector2), `1DAxis` 컴포짓 바인딩

**프레임 업데이트 흐름:**
```
Program.OnUpdate()
  → Input.Update()           // 레거시 (기존)
  → InputSystem.Update()     // 신규: 모든 활성 InputAction 평가
  → EngineCore.Update()      // 게임 로직
```

**RoseEngine/InputSystem/InputAction.cs (핵심):**
```csharp
namespace RoseEngine.InputSystem
{
    public class InputAction
    {
        public string name;
        public InputActionType type;
        public InputActionPhase phase { get; internal set; }

        public event Action<CallbackContext>? started;
        public event Action<CallbackContext>? performed;
        public event Action<CallbackContext>? canceled;

        public InputAction(string name, InputActionType type = InputActionType.Button, string? binding = null);

        public void Enable();
        public void Disable();
        public T ReadValue<T>();  // float (Button), Vector2 (Value)

        public void AddBinding(string path);
        public CompositeBinder AddCompositeBinding(string composite);
    }
}
```

**사용 예시:**
```csharp
using RoseEngine;
using RoseEngine.InputSystem;

public class TestScript : MonoBehaviour
{
    private InputAction moveAction;
    private InputAction jumpAction;

    public override void Awake()
    {
        moveAction = new InputAction("Move", InputActionType.Value);
        moveAction.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");

        jumpAction = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
        jumpAction.performed += ctx => Debug.Log("[InputSystem] Jump!");

        moveAction.Enable();
        jumpAction.Enable();
    }

    public override void Update()
    {
        Vector2 move = moveAction.ReadValue<Vector2>();
        if (move.x != 0 || move.y != 0)
            Debug.Log($"[InputSystem] Move: {move}");
    }
}
```

**파일 구조 (7개):**
```
src/IronRose.Engine/RoseEngine/InputSystem/
├── InputActionType.cs     (~10줄)
├── InputActionPhase.cs    (~10줄)
├── InputBinding.cs        (~30줄)
├── InputControlPath.cs    (~170줄)
├── InputAction.cs         (~210줄)
├── InputActionMap.cs      (~45줄)
└── InputSystem.cs         (~25줄)
```

---

### 3.7 Camera, Mesh, MeshRenderer 및 최소 렌더링 파이프라인

Phase 3의 마지막 단계로, 3D 렌더링에 필요한 핵심 컴포넌트 타입을 구현합니다.
Phase 4(고급 렌더링)로 넘어가기 전에, 화면에 큐브가 렌더링되는 것까지 완성합니다.

**핵심 추가 타입:**

**Matrix4x4** — System.Numerics 위임:
```csharp
namespace RoseEngine
{
    public struct Matrix4x4
    {
        internal System.Numerics.Matrix4x4 inner;

        public static Matrix4x4 identity;
        public static Matrix4x4 TRS(Vector3 pos, Quaternion rot, Vector3 scale);
        public static Matrix4x4 Perspective(float fov, float aspect, float near, float far);
        public static Matrix4x4 LookAt(Vector3 eye, Vector3 target, Vector3 up);
    }
}
```

**Mesh + Vertex** — 정점/인덱스 데이터 + GPU 버퍼:
```csharp
namespace RoseEngine
{
    public struct Vertex  // Position + Normal + UV (32 bytes)
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;
    }

    public class Mesh
    {
        public Vertex[] vertices;
        public uint[] indices;
        internal DeviceBuffer? VertexBuffer;  // Veldrid GPU 버퍼
        internal DeviceBuffer? IndexBuffer;
        internal bool isDirty = true;

        public void UploadToGPU(GraphicsDevice device);
    }
}
```

**Material** — 단순 색상:
```csharp
public class Material
{
    public Color color { get; set; } = Color.white;
}
```

**MeshFilter / MeshRenderer** — Unity 패턴 분리:
```csharp
public class MeshFilter : Component
{
    public Mesh? mesh { get; set; }
}

public class MeshRenderer : Component
{
    public Material? material { get; set; }

    // 전역 렌더러 레지스트리 (RenderSystem이 매 프레임 순회)
    internal static readonly List<MeshRenderer> _allRenderers = new();

    internal override void OnAddedToGameObject()
    {
        _allRenderers.Add(this);
    }
}
```

**Camera** — View/Projection + Camera.main:
```csharp
public class Camera : Component
{
    public float fieldOfView = 60f;
    public float nearClipPlane = 0.1f;
    public float farClipPlane = 1000f;

    public static Camera? main { get; internal set; }

    internal override void OnAddedToGameObject()
    {
        if (main == null) main = this;
    }

    public Matrix4x4 GetViewMatrix();
    public Matrix4x4 GetProjectionMatrix(float aspectRatio);
}
```

**Component 확장 — OnAddedToGameObject 패턴:**
```csharp
public class Component
{
    // ... 기존 멤버 ...
    internal virtual void OnAddedToGameObject() { }  // 신규
}
```

GameObject.AddComponent()에서 자동 호출되어 각 컴포넌트가 자체 등록 로직을 수행합니다.
Camera는 Camera.main 설정, MeshRenderer는 전역 레지스트리 등록.

**PrimitiveGenerator** — 큐브 프리미티브 (24 정점, 36 인덱스):
```csharp
public static class PrimitiveGenerator
{
    public static Mesh CreateCube();  // 6면 x 4정점, 면별 법선
}

// GameObject.CreatePrimitive() 구현 완성:
public static GameObject CreatePrimitive(PrimitiveType type)
{
    var go = new GameObject(type.ToString());
    var filter = go.AddComponent<MeshFilter>();
    var renderer = go.AddComponent<MeshRenderer>();
    renderer.material = new Material();
    filter.mesh = PrimitiveGenerator.CreateCube();  // Cube의 경우
    return go;
}
```

**셰이더 (GLSL):**
- `Shaders/vertex.glsl`: World x ViewProjection 변환, 법선을 월드 공간으로
- `Shaders/fragment.glsl`: 램버트 조명 (하드코딩된 조명 방향) + Material 색상

**ShaderCompiler** — GLSL -> SPIR-V (Veldrid.SPIRV 사용):
```csharp
namespace IronRose.Rendering
{
    public static class ShaderCompiler
    {
        public static Shader[] CompileGLSL(GraphicsDevice device, string vertexPath, string fragmentPath);
    }
}
```

**RenderSystem** — Forward 렌더링:
```csharp
public class RenderSystem : IDisposable
{
    public void Initialize(GraphicsDevice device);   // 셰이더 컴파일 + 파이프라인 생성
    public void Render(CommandList cl, Camera camera, float aspectRatio);  // 메시 순회 + DrawIndexed
}
```

Uniform 버퍼: TransformUniforms (World + ViewProjection), MaterialUniforms (Color).

**GraphicsManager 변경:**
- `Device`, `CommandList`, `AspectRatio` public 프로퍼티 추가
- `Render()` -> `BeginFrame()` + `EndFrame()` 분리
- Depth buffer 활성화 (`PixelFormat.D32_Float_S8_UInt`)

**렌더링 흐름:**
```
EngineCore.Render()
  -> GraphicsManager.BeginFrame()   // Begin + Framebuffer + ClearColor + ClearDepth
  -> RenderSystem.Render(cl, cam)   // Pipeline bind + 메시 순회 + DrawIndexed
  -> GraphicsManager.EndFrame()     // End + Submit + SwapBuffers
```

**SceneManager.Clear() 확장:**
- 기존 MonoBehaviour 정리에 더해 `MeshRenderer.ClearAll()`, `Camera.ClearMain()` 호출
- 핫 리로드 시 렌더링 상태도 초기화

**파일 구조 (신규 7개 + 셰이더 2개 + 기존 수정 5개):**
```
src/IronRose.Engine/RoseEngine/
├── Matrix4x4.cs              # 4x4 변환 행렬
├── Mesh.cs                   # Vertex 구조체 + Mesh 클래스
├── Material.cs               # 색상 기반 머터리얼
├── MeshFilter.cs             # Mesh 참조 컴포넌트
├── MeshRenderer.cs           # 렌더링 컴포넌트 + 정적 레지스트리
├── Camera.cs                 # View/Projection + Camera.main
└── PrimitiveGenerator.cs     # 큐브 프리미티브 생성

src/IronRose.Engine/
└── RenderSystem.cs           # Forward 렌더링 파이프라인

src/IronRose.Rendering/
└── ShaderCompiler.cs         # GLSL -> SPIR-V 컴파일

Shaders/
├── vertex.glsl               # MVP 버텍스 셰이더
└── fragment.glsl             # 램버트 조명 프래그먼트 셰이더
```

> **설계 결정:** RenderSystem은 RoseEngine 타입(Camera, MeshRenderer 등)에 의존하므로
> 순환 참조를 피하기 위해 IronRose.Rendering이 아닌 IronRose.Engine 프로젝트에 배치.
> ShaderCompiler는 Veldrid API만 사용하므로 IronRose.Rendering에 유지.

**사용 예시:**
```csharp
using RoseEngine;

public class TestScript : MonoBehaviour
{
    private GameObject cube;

    public override void Awake()
    {
        // 카메라 설정
        var camObj = new GameObject("Main Camera");
        camObj.AddComponent<Camera>();
        camObj.transform.position = new Vector3(0, 1, -3);

        // 큐브 생성 (MeshFilter + MeshRenderer 자동 추가)
        cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.GetComponent<MeshRenderer>().material = new Material(new Color(1f, 0.5f, 0.2f));
    }

    public override void Update()
    {
        cube.transform.Rotate(0, Time.deltaTime * 45, 0);
    }
}
```

**검증 결과:**
- `dotnet build` 0 오류, 0 경고
- 60 FPS 안정
- 주황색 큐브 렌더링, 램버트 조명(상단 밝음, 전면 어두움)
- 큐브 회전 동작
- 기존 InputSystem, 레거시 Input, 핫 리로드 정상 작동

---

## 검증 기준

✅ Unity 스타일 스크립트 작성 가능:

```csharp
using RoseEngine;

public class RotatingCube : MonoBehaviour
{
    void Update()
    {
        transform.Rotate(0, Time.deltaTime * 45, 0);

        if (Time.frameCount % 60 == 0)
        {
            Debug.Log($"Rotation: {transform.rotation}");
        }
    }
}
```

✅ GameObject 생성 및 Component 추가 동작
✅ MonoBehaviour 라이프사이클 메서드 호출
✅ Time.deltaTime 정상 작동

---

### 3.8 Unity 호환성 확장 (Phase 3.5++)

Phase 4(렌더링)로 넘어가기 전에, Unity 스크립트 호환성을 높이기 위해 추가 구현한 API들입니다.

#### Mathf 정적 클래스

Unity 스크립트에서 가장 많이 쓰이는 수학 유틸리티:

```csharp
namespace RoseEngine
{
    public static class Mathf
    {
        // 상수
        public const float PI, Infinity, NegativeInfinity, Deg2Rad, Rad2Deg, Epsilon;

        // 삼각함수
        public static float Sin(float f);
        public static float Cos(float f);
        public static float Atan2(float y, float x);

        // 보간 & 이동
        public static float Lerp(float a, float b, float t);
        public static float LerpUnclamped(float a, float b, float t);
        public static float InverseLerp(float a, float b, float value);
        public static float MoveTowards(float current, float target, float maxDelta);
        public static float SmoothDamp(float current, float target, ref float velocity, float smoothTime, ...);
        public static float SmoothStep(float from, float to, float t);

        // 클램프 & 근사
        public static float Clamp(float value, float min, float max);
        public static float Clamp01(float value);
        public static bool Approximately(float a, float b);

        // 반복 & 핑퐁
        public static float Repeat(float t, float length);
        public static float PingPong(float t, float length);

        // 각도
        public static float LerpAngle(float a, float b, float t);
        public static float MoveTowardsAngle(float current, float target, float maxDelta);
        public static float DeltaAngle(float current, float target);

        // 기타
        public static float PerlinNoise(float x, float y);
        // Abs, Min, Max, Pow, Sqrt, Sign, Floor, Ceil, Round, CeilToInt, FloorToInt, RoundToInt, ...
    }
}
```

#### RoseEngine.Random

```csharp
namespace RoseEngine
{
    public static class Random
    {
        public static int seed { set; }
        public static float value { get; }                     // 0..1
        public static float Range(float min, float max);
        public static int Range(int min, int max);             // max exclusive
        public static Vector3 insideUnitSphere { get; }
        public static Vector3 onUnitSphere { get; }
        public static Vector2 insideUnitCircle { get; }
        public static Quaternion rotation { get; }
        public static Color ColorHSV(float hueMin = 0f, float hueMax = 1f, ...);
    }
}
```

#### RoseEngine.Object 기반 클래스

Component와 GameObject의 공통 베이스:

```csharp
namespace RoseEngine
{
    public class Object
    {
        public virtual string name { get; set; }
        public int GetInstanceID();

        // 파괴 (프레임 끝 지연 파괴 + 즉시 파괴)
        public static void Destroy(Object obj, float t = 0f);
        public static void DestroyImmediate(Object obj);

        // 복제 (컴포넌트 필드 복사 + 자식 재귀 복제)
        public static T Instantiate<T>(T original) where T : Object;
        public static T Instantiate<T>(T original, Vector3 position, Quaternion rotation);
        public static T Instantiate<T>(T original, Transform parent);

        // 씬 검색
        public static T? FindObjectOfType<T>() where T : Object;
        public static T[] FindObjectsOfType<T>() where T : Object;

        // Destroyed 오브젝트는 false
        public static implicit operator bool(Object? obj);
    }
}
```

**Destroy 흐름:**
```
Object.Destroy(go, delay) → SceneManager._destroyQueue에 등록
  → 매 프레임 타이머 감소 → 0 이하 시:
    → 자식 먼저 재귀 파괴
    → OnDisable() → OnDestroy() → StopAllCoroutines → CancelInvoke
    → MeshRenderer 레지스트리 해제, Camera.main 정리
    → _allGameObjects에서 제거
    → _isDestroyed = true (bool 캐스트 시 false 반환)
```

#### GameObject 확장

```csharp
public class GameObject : Object
{
    // 활성화/비활성화
    public bool activeSelf { get; }
    public bool activeInHierarchy { get; }      // 부모 체인 탐색
    public void SetActive(bool value);          // OnEnable/OnDisable 자동 호출

    // 태그 & 레이어
    public string tag { get; set; }
    public int layer { get; set; }
    public bool CompareTag(string tag);

    // 씬 검색
    public static GameObject? Find(string name);
    public static GameObject? FindWithTag(string tag);
    public static GameObject[] FindGameObjectsWithTag(string tag);

    // AddComponent 자동 MonoBehaviour 등록
    public T AddComponent<T>();     // → SceneManager.RegisterBehaviour 자동 호출
}
```

#### MonoBehaviour 확장

```csharp
public class MonoBehaviour : Component
{
    // 라이프사이클 (Awake → OnEnable → Start → Update → LateUpdate → OnDisable → OnDestroy)
    public virtual void OnEnable() { }
    public virtual void OnDisable() { }

    // 코루틴
    public Coroutine StartCoroutine(IEnumerator routine);
    public Coroutine StartCoroutine(string methodName);
    public void StopCoroutine(Coroutine coroutine);
    public void StopCoroutine(string methodName);
    public void StopAllCoroutines();

    // 지연 호출
    public void Invoke(string methodName, float time);
    public void InvokeRepeating(string methodName, float time, float repeatRate);
    public void CancelInvoke();
    public void CancelInvoke(string methodName);
    public bool IsInvoking();
    public bool IsInvoking(string methodName);
}
```

**코루틴 지원 YieldInstruction:**
- `yield return null;` — 다음 프레임
- `yield return new WaitForSeconds(1f);` — 1초 대기
- `yield return new WaitForEndOfFrame();`
- `yield return new WaitUntil(() => condition);`
- `yield return new WaitWhile(() => condition);`
- `yield return StartCoroutine(Nested());` — 중첩 코루틴
- `yield return otherIEnumerator;` — 자동 중첩

**코루틴 사용 예시:**
```csharp
public class CoroutineDemo : MonoBehaviour
{
    public override void Start()
    {
        StartCoroutine(SpawnLoop());
    }

    IEnumerator SpawnLoop()
    {
        while (true)
        {
            var go = Instantiate(prefab, Random.insideUnitSphere * 5f, Random.rotation);
            Destroy(go, 3f);     // 3초 후 자동 파괴
            yield return new WaitForSeconds(0.5f);
        }
    }
}
```

#### Transform 부모/자식 계층

```csharp
public class Transform : Component
{
    // 계층 구조
    public Transform? parent { get; set; }
    public int childCount { get; }
    public Transform? root { get; }
    public void SetParent(Transform? parent, bool worldPositionStays = true);
    public Transform GetChild(int index);
    public Transform? Find(string name);      // '/' 구분자 지원
    public void DetachChildren();
    public bool IsChildOf(Transform parent);
    public int GetSiblingIndex();
    public void SetSiblingIndex(int index);

    // 로컬 좌표 (내부 저장)
    public Vector3 localPosition { get; set; }
    public Quaternion localRotation { get; set; }
    public Vector3 localScale { get; set; }
    public Vector3 localEulerAngles { get; set; }

    // 월드 좌표 (부모 체인 계산)
    public Vector3 position { get; set; }       // get: 부모 chain → world, set: world → local 역변환
    public Quaternion rotation { get; set; }
    public Vector3 lossyScale { get; }

    // 공간 변환
    public void Translate(Vector3 translation, Space relativeTo);
    public void Rotate(Vector3 eulers, Space relativeTo);
    public void Rotate(Vector3 axis, float angle);
    public void RotateAround(Vector3 point, Vector3 axis, float angle);
    public void LookAt(Transform target);
    public void LookAt(Vector3 worldPosition, Vector3 worldUp);
    public Vector3 TransformPoint(Vector3 localPoint);
    public Vector3 InverseTransformPoint(Vector3 worldPoint);
    public Vector3 TransformDirection(Vector3 localDirection);
    public Vector3 InverseTransformDirection(Vector3 worldDirection);
}

public enum Space { World, Self }
```

#### Component 확장

```csharp
public class Component : Object
{
    public string tag { get; set; }
    public bool CompareTag(string tag);
    public T? GetComponentInChildren<T>() where T : Component;
    public T? GetComponentInParent<T>() where T : Component;
    public T[] GetComponentsInChildren<T>() where T : Component;
    public T[] GetComponentsInParent<T>() where T : Component;
}
```

#### Quaternion 확장

```csharp
// 신규 메서드
public static Quaternion Inverse(Quaternion q);
public static Quaternion Normalize(Quaternion q);
public Quaternion normalized { get; }
public static Quaternion Lerp(Quaternion a, Quaternion b, float t);
public static Quaternion LerpUnclamped(Quaternion a, Quaternion b, float t);
public static Quaternion Slerp(Quaternion a, Quaternion b, float t);
public static Quaternion SlerpUnclamped(Quaternion a, Quaternion b, float t);
public static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegreesDelta);
public static float Angle(Quaternion a, Quaternion b);
public static Quaternion LookRotation(Vector3 forward, Vector3 upwards = default);
public static Quaternion FromToRotation(Vector3 from, Vector3 to);
```

#### Vector3 확장

```csharp
// 신규 메서드
public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta);
public static Vector3 SmoothDamp(Vector3 current, Vector3 target, ref Vector3 velocity, float smoothTime, ...);
public static float Angle(Vector3 from, Vector3 to);
public static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis);
public static Vector3 Scale(Vector3 a, Vector3 b);
public static Vector3 Project(Vector3 vector, Vector3 onNormal);
public static Vector3 ProjectOnPlane(Vector3 vector, Vector3 planeNormal);
public static Vector3 Reflect(Vector3 inDirection, Vector3 inNormal);
public static Vector3 ClampMagnitude(Vector3 vector, float maxLength);
public static Vector3 Min(Vector3 a, Vector3 b);
public static Vector3 Max(Vector3 a, Vector3 b);
public static Vector3 RotateTowards(Vector3 current, Vector3 target, float maxRadians, float maxMagnitude);
public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t);
public void Normalize();
public void Set(float x, float y, float z);
public float this[int index] { get; set; }
```

#### Color 확장

```csharp
public static Color HSVToRGB(float h, float s, float v);
public static void RGBToHSV(Color color, out float h, out float s, out float v);
```

#### Unity 속성 (Attribute)

에디터 없이도 Unity 스크립트 호환성을 위해 속성 정의:

```csharp
[SerializeField] private float speed = 5f;
[Header("Movement")]
[Range(0f, 100f)] public float maxSpeed;
[Tooltip("Speed multiplier")]
[HideInInspector] public float internalValue;
[RequireComponent(typeof(MeshRenderer))]
[DisallowMultipleComponent]
public class MyScript : MonoBehaviour { }
```

#### SceneManager 확장

코루틴 스케줄러, Invoke 타이머, Deferred Destroy 큐 통합:

```
SceneManager.Update(deltaTime):
  1. Time 갱신
  2. Pending Start() 처리
  3. Invoke 타이머 처리 (리플렉션 호출)
  4. Update() 호출 (activeInHierarchy && enabled 체크)
  5. 코루틴 처리 (WaitForSeconds 타이머, 중첩 코루틴, CustomYieldInstruction)
  6. LateUpdate() 호출
  7. Deferred Destroy 큐 처리
  8. frameCount++
```

**파일 구조 (신규 6개 + 기존 수정 8개):**
```
src/IronRose.Engine/RoseEngine/
├── Mathf.cs              # 수학 유틸리티 (~120줄)
├── Random.cs             # 난수 생성 (~55줄)
├── Object.cs             # 기반 클래스: Destroy, Instantiate, FindObjectOfType (~120줄)
├── Attributes.cs         # SerializeField, Header, Range 등 (~75줄)
├── YieldInstruction.cs   # WaitForSeconds, WaitUntil 등 (~40줄)
├── Coroutine.cs          # 코루틴 핸들 (~15줄)
├── Component.cs          # (수정) Object 상속, GetComponentInChildren/Parent (~95줄)
├── GameObject.cs         # (수정) Object 상속, SetActive, Find, tag (~190줄)
├── MonoBehaviour.cs      # (수정) OnEnable/OnDisable, 코루틴, Invoke (~105줄)
├── Transform.cs          # (수정) 부모/자식 계층, 로컬/월드 좌표 (~250줄)
├── SceneManager.cs       # (수정) GO 레지스트리, 코루틴, Invoke, Destroy 큐 (~500줄)
├── Quaternion.cs         # (수정) Inverse, Lerp, Slerp, LookRotation (~275줄)
├── Vector3.cs            # (수정) MoveTowards, SmoothDamp, Angle, Scale 등 (~195줄)
└── Color.cs              # (수정) HSVToRGB, RGBToHSV (~75줄)
```

**검증 결과:**
- `dotnet build` 0 오류, 0 경고
- Demo 프로젝트 정상 실행 (60 FPS)
- MonoBehaviour 라이프사이클 정상 (Awake → OnEnable → Start → Update → LateUpdate)
- 기존 InputSystem, 레거시 Input, 핫 리로드 정상 작동
- 3D 렌더링 (Sphere, Cube, Capsule, Quad, Plane) 정상

---

## 예상 소요 시간
**4-5일**

---

## 다음 단계
→ [Phase 4: 기본 렌더링 파이프라인](Phase4_BasicRendering.md)
