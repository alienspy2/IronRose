# Phase 4: 기본 렌더링 파이프라인

## 목표
Veldrid를 사용하여 3D 메시를 화면에 그리는 기본 Forward Rendering을 구현합니다.

---

## 4.1 메시 렌더링 시스템 ✅

**Mesh.cs** — GPU 업로드 지원, dirty 플래그로 불필요한 재업로드 방지:
```csharp
[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 UV;
    public static uint SizeInBytes => (uint)Marshal.SizeOf<Vertex>();
}

public class Mesh
{
    public Vertex[] vertices;
    public uint[] indices;
    internal bool isDirty = true;

    public void UploadToGPU(GraphicsDevice device) { /* dirty 체크, 이전 버퍼 Dispose 후 재생성 */ }
    public void Dispose() { /* GPU 버퍼 해제 */ }
}
```

**MeshFilter.cs** — mesh 데이터 보유 컴포넌트:
```csharp
public class MeshFilter : Component
{
    public Mesh? mesh { get; set; }
}
```

**MeshRenderer.cs** — 전역 렌더러 목록 자동 등록:
```csharp
public class MeshRenderer : Component
{
    public Material? material { get; set; }
    public bool enabled { get; set; } = true;
    internal static readonly List<MeshRenderer> _allRenderers = new();
    internal override void OnAddedToGameObject() => _allRenderers.Add(this);
}
```

**Material.cs**:
```csharp
public class Material
{
    public Color color { get; set; } = Color.white;
}
```

RenderSystem은 `MeshRenderer`에서 같은 GameObject의 `MeshFilter.mesh`를 참조하여 렌더링합니다.

---

## 4.2 셰이더 (GLSL → SPIR-V 크로스 컴파일) ✅

**Shaders/vertex.glsl** — Transforms uniform 블록 (World + ViewProjection):
```glsl
#version 450
layout(set = 0, binding = 0) uniform Transforms {
    mat4 World;
    mat4 ViewProjection;
};

void main() {
    vec4 worldPos = World * vec4(Position, 1.0);
    gl_Position = ViewProjection * worldPos;
    frag_Normal = mat3(World) * Normal;
    frag_UV = UV;
}
```

**Shaders/fragment.glsl** — Lambert diffuse + ambient:
```glsl
#version 450
layout(set = 0, binding = 1) uniform MaterialData {
    vec4 Color;
};

void main() {
    vec3 lightDir = normalize(vec3(0.5, 1.0, -0.5));
    float ndotl = max(dot(normalize(frag_Normal), lightDir), 0.0);
    float lighting = 0.2 + ndotl * 0.8;
    out_Color = vec4(Color.rgb * lighting, Color.a);
}
```

**ShaderCompiler.cs (IronRose.Rendering)** — Veldrid.SPIRV 기반 GLSL→SPIR-V 크로스 컴파일:
```csharp
public static class ShaderCompiler
{
    public static Shader[] CompileGLSL(GraphicsDevice device, string vertexPath, string fragmentPath);
}
```

---

## 4.3 카메라 시스템 ✅

**Camera.cs** — 왼손 좌표계 뷰/투영 행렬, 첫 번째 Camera가 `Camera.main`에 자동 등록:
```csharp
public class Camera : Component
{
    public float fieldOfView = 60f;
    public float nearClipPlane = 0.1f;
    public float farClipPlane = 1000f;
    public static Camera? main { get; internal set; }

    public Matrix4x4 GetViewMatrix()
        => Matrix4x4.LookAt(transform.position, transform.position + transform.forward, transform.up);

    public Matrix4x4 GetProjectionMatrix(float aspectRatio)
        => Matrix4x4.Perspective(fieldOfView, aspectRatio, nearClipPlane, farClipPlane);
}
```

**Matrix4x4.cs** — System.Numerics SIMD 위임, 왼손 좌표계 LookAt/Perspective 직접 구현:
```csharp
public struct Matrix4x4
{
    internal System.Numerics.Matrix4x4 inner;

    public static Matrix4x4 TRS(Vector3 pos, Quaternion rot, Vector3 scale);
    public static Matrix4x4 Perspective(float fovDegrees, float aspect, float near, float far);  // depth [0,1]
    public static Matrix4x4 LookAt(Vector3 eye, Vector3 target, Vector3 up);
    public static Matrix4x4 operator *(Matrix4x4 a, Matrix4x4 b);
}
```

`aspectRatio`는 Camera 필드가 아닌 `GetProjectionMatrix()` 파라미터로 전달 (윈도우 리사이즈 대응).

---

## 4.4 프리미티브 생성 ✅

**PrimitiveGenerator.cs** — 5종 프리미티브:

| 프리미티브 | 설명 |
|-----------|------|
| `CreateCube()` | 24 vertices (면별 노멀), 36 indices |
| `CreateSphere(lon, lat)` | UV Sphere, 기본 24x16 세그먼트 |
| `CreateCapsule(lon, capRings, bodyRings)` | 상/하 반구 + 원통, 높이 2, 반지름 0.5 |
| `CreatePlane(resolution)` | 10x10 유닛, 기본 10x10 서브디비전 |
| `CreateQuad()` | 1x1, Z+ 방향 |

**GameObject.CreatePrimitive()** — MeshFilter + MeshRenderer 자동 구성:
```csharp
public static GameObject CreatePrimitive(PrimitiveType type)
{
    var go = new GameObject(type.ToString());
    var filter = go.AddComponent<MeshFilter>();
    var renderer = go.AddComponent<MeshRenderer>();
    renderer.material = new Material();
    filter.mesh = type switch { Cube => CreateCube(), Sphere => CreateSphere(), ... };
    return go;
}
```

---

## 4.5 렌더링 파이프라인 통합 ✅

**RenderSystem.cs (IronRose.Rendering)** — Solid + Wireframe 듀얼 파이프라인:
```csharp
public class RenderSystem : IDisposable
{
    private Pipeline? _pipeline;           // Solid pass
    private Pipeline? _wireframePipeline;  // Debug.wireframe 활성 시 오버레이

    public void Initialize(GraphicsDevice device);
    public void Render(CommandList cl, Camera? camera, float aspectRatio);
}
```

**GraphicsManager.cs** — Silk.NET 윈도우 + Veldrid Vulkan 통합:
- Silk.NET `IWindow` → Veldrid `SwapchainSource` (Win32/X11/Wayland)
- `BeginFrame()` / `EndFrame()` 프레임 관리
- 스크린샷 캡처 (ImageSharp)
- 윈도우 리사이즈 자동 처리

**EngineCore.cs 렌더 루프:**
```
BeginFrame → ClearColor + ClearDepth
  → RenderSystem.Render(commandList, Camera.main, aspectRatio)
EndFrame → Submit → SwapBuffers
```

---

## 검증 기준

✅ 3D 프리미티브(Cube, Sphere, Capsule, Plane, Quad)가 화면에 렌더링됨
✅ Lambert 조명으로 면의 음영이 구분됨
✅ 카메라 위치/방향 변경 시 시점 반영됨
✅ Wireframe 오버레이 디버그 모드 지원
✅ 윈도우 리사이즈 시 종횡비 자동 보정
✅ LiveCode에서 프리미티브 생성 + 회전 스크립트 동작:

```csharp
public class RotatingCube : MonoBehaviour
{
    public override void Start()
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
    }

    public override void Update()
    {
        transform.Rotate(0, Time.deltaTime * 45, 0);
    }
}
```

---

## 다음 단계
→ [Phase 5: Unity 에셋 임포터](Phase5_AssetImporter.md)
