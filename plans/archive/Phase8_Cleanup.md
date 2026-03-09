# Phase 8: 중간정리 - 검증 + KISS 리팩토링

> **목표**: Phase 1~7에서 3일간 빠르게 구현하면서 누적된 코드 중복·과잉 복잡성을 제거하여, 향후 AI Integration 진입 전 안정적인 코드베이스를 확보한다.

**핵심 원칙:**
- KISS (Keep It Simple, Stupid) — 불필요한 추상화·중복·데드코드 제거
- 기능 변경 없음 (리팩토링 only) — 동작은 현재와 100% 동일해야 함
- 빌드 + 데모 실행으로 검증

---

## 현재 코드 상태 (Phase 7 완료 기준)

### 코드 메트릭

| 영역 | LOC | 파일 수 | 복잡도 | 비고 |
|---|---|---|---|---|
| RoseEngine API | ~5,500 | 59 | 낮음~중간 | 잘 설계됨 |
| EngineCore | ~409 | 1 | 중간 | 핫리로드 상태 관리 복잡 |
| **RenderSystem** | **~1,163** | **1** | **높음** | **최대 파일, 중복 많음** |
| AssetPipeline | ~871 | 7 | 중간 | 일부 과잉 설계 |
| Demo | ~1,509 | 9 | 낮음 | 보일러플레이트 다수 |
| Rendering | ~1,025 | 9 | 중간 | PostProcessing 모듈화 완료 |
| Physics | ~395 | 3 | 낮음 | KISS 모범 사례 |
| Scripting | ~366 | 3 | 낮음 | TODO 스텁 존재 |

### 식별된 문제

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 데모 카메라/라이트/폰트 보일러플레이트 반복 | FrozenCode 6개 + LiveCode 1개 | 높음 |
| 2 | `ExecuteDestroy()` 하드코딩된 7개 타입 분기 (취약, 확장 시 수정 필수) | SceneManager.cs:395-506 | 높음 |
| 3 | `DrawOpaqueRenderers`/`DrawAllRenderers`/`DrawAllSprites`/`DrawAllTexts` 중복 | RenderSystem.cs:825-1087 | 높음 |
| 4 | `UploadDeferredLightData`/`UploadForwardLightData` 중복 | RenderSystem.cs | 중간 |
| 5 | `SetLightInfo()` switch(0~7) 하드코딩 | RenderSystem.cs:792-805 | 중간 |
| 6 | 매직넘버 (8, 64, 720 등) 산재 | RenderSystem.cs, DemoLauncher.cs | 중간 |
| 7 | DemoLauncher 핫리로드 복원 경로 3개 중복 | DemoLauncher.cs | 중간 |
| 8 | PBRDemo 주석 처리된 floor 코드 | PBRDemo.cs:96-104 | 낮음 |
| 9 | ScriptDomain TODO 스텁 (Phase 2.3 미구현 주석) | ScriptDomain.cs:60,69 | 낮음 |

---

## 8.1 데모 보일러플레이트 제거

**문제**: 모든 FrozenCode 데모에서 카메라/폰트 생성 패턴이 반복됨 (~80줄 중복).

```csharp
// 이 패턴이 7곳에 반복:
var camObj = new GameObject("Main Camera");
var cam = camObj.AddComponent<Camera>();
camObj.transform.position = new Vector3(...);

var fontPath = System.IO.Path.Combine(
    System.IO.Directory.GetCurrentDirectory(), "Assets", "Fonts", "NotoSans_eng.ttf");
if (System.IO.File.Exists(fontPath))
    _font = Font.CreateFromFile(fontPath, 32);
else
    _font = Font.CreateDefault(32);
```

**해결**: `DemoUtils` 유틸리티 클래스 추출.

**신규 파일**: `src/IronRose.Demo/DemoUtils.cs`
```csharp
public static class DemoUtils
{
    /// <summary>카메라 생성. lookAt이 null이면 LookAt 생략.</summary>
    public static (Camera cam, Transform transform) CreateCamera(
        Vector3 position, Vector3? lookAt = null,
        CameraClearFlags clearFlags = CameraClearFlags.Skybox,
        Color? backgroundColor = null)
    {
        var camObj = new GameObject("Main Camera");
        var cam = camObj.AddComponent<Camera>();
        cam.clearFlags = clearFlags;
        if (backgroundColor.HasValue)
            cam.backgroundColor = backgroundColor.Value;
        camObj.transform.position = position;
        if (lookAt.HasValue)
            camObj.transform.LookAt(lookAt.Value);
        return (cam, camObj.transform);
    }

    /// <summary>NotoSans 폰트 로드 (fallback: CreateDefault).</summary>
    public static Font LoadFont(int size = 32)
    {
        var fontPath = System.IO.Path.Combine(
            System.IO.Directory.GetCurrentDirectory(), "Assets", "Fonts", "NotoSans_eng.ttf");
        try { return Font.CreateFromFile(fontPath, size); }
        catch { return Font.CreateDefault(size); }
    }
}
```

**수정 파일** (7개):

| 파일 | 변경 내용 |
|---|---|
| `AssetImportDemo.cs` | 카메라 생성 + 폰트 로딩 → `DemoUtils` 호출 |
| `CornellBoxDemo.cs` | 카메라 생성 → `DemoUtils` 호출 |
| `PBRDemo.cs` | 카메라 생성 + 폰트 로딩 → `DemoUtils` 호출 |
| `PhysicsDemo3D.cs` | 카메라 생성 → `DemoUtils` 호출 |
| `SpriteDemo.cs` | 카메라 생성 → `DemoUtils` 호출 |
| `TextDemo.cs` | 카메라 생성 + 폰트 로딩 → `DemoUtils` 호출 |
| `ColorPulseDemo.cs` | 카메라 생성 → `DemoUtils` 호출 |

**예상 절감**: ~80줄 중복 제거

**검증**: `dotnet build` 성공 + 각 데모(1~4키) 전환 정상

---

## 8.2 SceneManager.ExecuteDestroy() 단순화

**문제**: `ExecuteDestroy()`에서 7개 컴포넌트 타입을 `if (comp is X)` 패턴으로 개별 처리. 새 컴포넌트 추가 시 SceneManager 수정 필수 — 취약한 구조.

```csharp
// 현재: SceneManager.cs:425-452 (GameObject 분기), 479-503 (Component 분기) — 동일 코드 2번 반복
if (comp is MeshRenderer mr)
    MeshRenderer._allRenderers.Remove(mr);
if (comp is SpriteRenderer spr)
    SpriteRenderer._allSpriteRenderers.Remove(spr);
if (comp is TextRenderer txr)
    TextRenderer._allTextRenderers.Remove(txr);
if (comp is Light light)
    Light._allLights.Remove(light);
if (comp is Rigidbody rb3)
{
    rb3.RemoveFromPhysics();
    Rigidbody._allRigidbodies.Remove(rb3);
}
// ... 같은 패턴이 2번 반복
```

**해결**: `Component.OnComponentDestroy()` 가상 메서드 도입 → 각 컴포넌트가 자신의 정리 책임.

**수정 파일**:

| 파일 | 변경 내용 |
|---|---|
| `Component.cs` | `internal virtual void OnComponentDestroy() { }` 추가 |
| `MeshRenderer.cs` | `override OnComponentDestroy()` — `_allRenderers.Remove(this)` |
| `SpriteRenderer.cs` | `override OnComponentDestroy()` — `_allSpriteRenderers.Remove(this)` |
| `TextRenderer.cs` | `override OnComponentDestroy()` — `_allTextRenderers.Remove(this)` |
| `Light.cs` | `override OnComponentDestroy()` — `_allLights.Remove(this)` |
| `Rigidbody.cs` | `override OnComponentDestroy()` — `RemoveFromPhysics()` + 레지스트리 제거 |
| `Rigidbody2D.cs` | `override OnComponentDestroy()` — 동일 |
| `Camera.cs` | `override OnComponentDestroy()` — `ClearMain()` 조건부 호출 |
| `SceneManager.cs` | `ExecuteDestroy()` — 7개 `if` 분기 → `comp.OnComponentDestroy()` 단일 호출 |

**Before/After**:
```csharp
// Before (SceneManager.cs — 28줄, 2번 반복 = 56줄)
if (comp is MeshRenderer mr) MeshRenderer._allRenderers.Remove(mr);
if (comp is SpriteRenderer spr) SpriteRenderer._allSpriteRenderers.Remove(spr);
// ... 7개 타입

// After (SceneManager.cs — 1줄)
comp.OnComponentDestroy();
```

**예상 절감**: SceneManager에서 ~50줄 제거, 향후 컴포넌트 추가 시 SceneManager 수정 불필요

**검증**: `dotnet build` 성공 + PhysicsDemo3D에서 오브젝트 파괴 정상 동작

---

## 8.3 RenderSystem 라이트 데이터 중복 제거

**문제 1**: `SetLightInfo()` switch문이 index 0~7을 하드코딩.

```csharp
// 현재: RenderSystem.cs:792-805
private static void SetLightInfo(ref LightUniforms data, int index, LightInfoGPU info)
{
    switch (index)
    {
        case 0: data.Light0 = info; break;
        case 1: data.Light1 = info; break;
        // ... case 7
    }
}
```

**해결**: `LightUniforms` 구조체의 개별 `Light0~Light7` 필드를 유지하되, `Unsafe.Add`로 인덱싱.

```csharp
private static unsafe void SetLightInfo(ref LightUniforms data, int index, LightInfoGPU info)
{
    fixed (LightInfoGPU* ptr = &data.Light0)
        ptr[index] = info;
}
```

**문제 2**: `UploadDeferredLightData()`와 `UploadForwardLightData()` 라이트 수집 로직이 거의 동일.

**해결**: 공통 라이트 수집 메서드 `CollectLightData()` 추출, 호출부에서 각각의 버퍼에 업로드.

**문제 3**: 매직넘버 산재.

**해결**: 상수 추출.
```csharp
private const int MaxForwardLights = 8;
private const int MaxDeferredLights = 64;
```

**수정 파일**: `src/IronRose.Engine/RenderSystem.cs`

**예상 절감**: ~30줄 제거 + 가독성 향상

**검증**: PBR 데모 라이팅 정상 + Cornell Box 라이팅 정상

---

## 8.4 RenderSystem Draw 메서드 중복 축소

**문제**: `DrawOpaqueRenderers`(55줄), `DrawAllRenderers`(56줄), `DrawAllSprites`(44줄), `DrawAllTexts`(42줄) — 공통 패턴(Transform 업로드 → Material 업로드 → ResourceSet 바인딩 → DrawIndexed)이 4번 반복.

**해결**: 공통 draw 헬퍼 `DrawMesh()` 추출.

```csharp
/// <summary>단일 메시 draw call — Transform/Material 업로드 + ResourceSet 바인딩 + DrawIndexed.</summary>
private void DrawMesh(CommandList cl, System.Numerics.Matrix4x4 viewProj,
    Mesh mesh, Transform t, MaterialUniforms matUniforms, TextureView? texView)
{
    mesh.UploadToGPU(_device!);
    if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) return;

    cl.UpdateBuffer(_transformBuffer, 0, new TransformUniforms
    {
        World = Matrix4x4.TRS(t.position, t.rotation, t.localScale).ToNumerics(),
        ViewProjection = viewProj,
    });
    cl.UpdateBuffer(_materialBuffer, 0, matUniforms);

    cl.SetGraphicsResourceSet(0, GetOrCreateResourceSet(texView));
    cl.SetVertexBuffer(0, mesh.VertexBuffer);
    cl.SetIndexBuffer(mesh.IndexBuffer, IndexFormat.UInt32);
    cl.DrawIndexed((uint)mesh.indices.Length);
}
```

각 Draw 메서드는 렌더러 목록 순회 + 필터링만 담당하고, 실제 draw call은 `DrawMesh()`로 위임.

**수정 파일**: `src/IronRose.Engine/RenderSystem.cs`

**예상 절감**: ~80줄 제거

**검증**: 모든 데모 렌더링 정상 (Deferred + Forward + Sprite + Text + Wireframe)

---

## 8.5 데드코드 정리

### 8.5.1 PBRDemo 주석 코드 삭제
```csharp
// PBRDemo.cs:96-104 — 주석 처리된 floor 코드
// var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
// ...
```
→ **삭제**

### 8.5.2 ScriptDomain TODO 스텁 정리
```csharp
// ScriptDomain.cs:60
// 기존 상태 저장 (TODO: Phase 2.3에서 구현)
// SaveState();
// ScriptDomain.cs:69
// 상태 복원 (TODO: Phase 2.3에서 구현)
// RestoreState();
```
→ Phase 2.3은 더 이상 계획에 없음. IHotReloadable 인터페이스가 이미 이 역할을 대체. **주석 삭제**.

### 8.5.3 IronRose.Contracts 검토

`IronRose.Contracts`에는 `Screen.cs` 1개 파일만 존재 (`SetClearColor` 델리게이트).
LiveCode 플러그인 API 컨테이너로서 역할이 있으므로 **유지**, 단 향후 Phase에서 API 확장 시 활용.

**수정 파일**: `PBRDemo.cs`, `ScriptDomain.cs`

**검증**: `dotnet build` 성공

---

## 8.6 DemoLauncher 핫리로드 로직 정리

**문제**: 데모 전환 시 3개의 유사한 경로가 존재:
1. `LoadDemo()` (빌트인) — SceneManager.Clear → DemoLauncher 재생성 → LaunchBuiltinDemo
2. `LoadLiveCodeDemo()` — SceneManager.Clear → DemoLauncher 재생성 → AddComponent(type)
3. `AutoRestoreLiveCodeDemo()` — 핫리로드 후 타입명으로 검색 → AddComponent(type)

모두 "Scene Clear → DemoLauncher 재등록 → 데모 인스턴스화" 패턴이 동일.

**해결**: 공통 `SwitchDemo()` 메서드 추출.

```csharp
private void SwitchDemo(int builtinIndex, System.Type? liveCodeType)
{
    _isLoading = true;
    _activeBuiltinDemo = builtinIndex;
    _activeLiveCodeDemo = liveCodeType?.Name ?? "";

    SceneManager.Clear();

    var selectorGo = new GameObject("DemoSelector");
    var selector = selectorGo.AddComponent<DemoLauncher>();
    selector._currentDemo = builtinIndex;
    selector._currentLiveCodeDemo = _activeLiveCodeDemo;

    _isLoading = false;

    if (liveCodeType != null)
    {
        var go = new GameObject(liveCodeType.Name);
        go.AddComponent(liveCodeType);
    }
    else if (builtinIndex >= 0)
    {
        LaunchBuiltinDemo(builtinIndex);
    }

    CreateHud();
}
```

**수정 파일**: `src/IronRose.Demo/DemoLauncher.cs`

**예상 절감**: ~30줄 제거 + 단일 책임 경로

**검증**: 빌트인 데모 전환(1~4키) + LiveCode 데모 전환 + 핫리로드 후 데모 복원 정상

---

## 새로 생성/수정되는 파일 목록

| 파일 | 작업 | 섹션 |
|---|---|---|
| `src/IronRose.Demo/DemoUtils.cs` | **신규** | 8.1 |
| `src/IronRose.Demo/FrozenCode/AssetImportDemo.cs` | 수정 | 8.1 |
| `src/IronRose.Demo/FrozenCode/CornellBoxDemo.cs` | 수정 | 8.1 |
| `src/IronRose.Demo/FrozenCode/PBRDemo.cs` | 수정 | 8.1, 8.5.1 |
| `src/IronRose.Demo/FrozenCode/PhysicsDemo3D.cs` | 수정 | 8.1 |
| `src/IronRose.Demo/FrozenCode/SpriteDemo.cs` | 수정 | 8.1 |
| `src/IronRose.Demo/FrozenCode/TextDemo.cs` | 수정 | 8.1 |
| `src/IronRose.Demo/LiveCode/ColorPulseDemo.cs` | 수정 | 8.1 |
| `src/IronRose.Demo/DemoLauncher.cs` | 수정 | 8.6 |
| `src/IronRose.Engine/RoseEngine/Component.cs` | 수정 | 8.2 |
| `src/IronRose.Engine/RoseEngine/SceneManager.cs` | 수정 | 8.2 |
| `src/IronRose.Engine/RoseEngine/MeshRenderer.cs` | 수정 | 8.2 |
| `src/IronRose.Engine/RoseEngine/SpriteRenderer.cs` | 수정 | 8.2 |
| `src/IronRose.Engine/RoseEngine/TextRenderer.cs` | 수정 | 8.2 |
| `src/IronRose.Engine/RoseEngine/Light.cs` | 수정 | 8.2 |
| `src/IronRose.Engine/RoseEngine/Rigidbody.cs` | 수정 | 8.2 |
| `src/IronRose.Engine/RoseEngine/Rigidbody2D.cs` | 수정 | 8.2 |
| `src/IronRose.Engine/RoseEngine/Camera.cs` | 수정 | 8.2 |
| `src/IronRose.Engine/RenderSystem.cs` | 수정 | 8.3, 8.4 |
| `src/IronRose.Scripting/ScriptDomain.cs` | 수정 | 8.5.2 |

---

## 검증 방법

### 검증 도구

리팩토링 각 단계마다 아래 두 가지 도구를 적극 활용하여 동작 동일성을 확인한다.

1. **콘솔 로그 (`Debug.Log`)**
   - 각 섹션 작업 전후로 데모를 실행하여 콘솔 출력을 비교한다.
   - 리팩토링 중 의심스러운 경로에 임시 `Debug.Log`를 추가하여 호출 흐름을 확인한다.
   - 예: `Debug.Log($"[Verify] DrawMesh called: {mesh.vertices.Length} verts");`
   - 검증 완료 후 임시 로그는 반드시 제거한다.

2. **자동 스크린샷 (`EngineCore.ScreenCaptureEnabled`)**
   - `EngineCore.ScreenCaptureEnabled = true`로 설정하면 자동으로 스크린샷을 캡처한다.
   - **리팩토링 전(Before)**: 각 데모별 스크린샷 1장씩 캡처하여 기준 이미지로 보관.
   - **리팩토링 후(After)**: 동일 데모에서 스크린샷을 캡처하여 Before와 픽셀 단위 비교.
   - 렌더링 결과가 동일하면 리팩토링 성공. 차이가 있으면 원인 조사 후 수정.
   - 특히 8.3/8.4 (RenderSystem 변경) 후에는 모든 데모 스크린샷을 반드시 비교한다.

### 단계별 검증 흐름

```
[섹션 작업 전]
  1. dotnet build → 성공 확인
  2. dotnet run → EngineCore.ScreenCaptureEnabled로 자동 스크린샷 캡처 (Before)
  3. 콘솔 로그 정상 출력 확인

[섹션 작업 후]
  4. dotnet build → 성공 확인 (경고 0개, 오류 0개)
  5. dotnet run → EngineCore.ScreenCaptureEnabled로 자동 스크린샷 캡처 (After)
  6. Before/After 스크린샷 비교 → 렌더링 동일 확인
  7. 콘솔 로그 비교 → 동작 흐름 동일 확인
  8. 의심 경로에 임시 Debug.Log 추가 → 호출 횟수/순서 확인 → 확인 후 제거
```

### 검증 체크리스트

- [ ] `dotnet build` 성공 (경고 0개, 오류 0개)
- [ ] Demo 1 (Cornell Box): 렌더링 + Post-Processing 파라미터 조정 정상 — 스크린샷 비교 통과
- [ ] Demo 2 (Asset Import): GLB 모델 로드 + 좌우 화살표 탐색 정상 — 스크린샷 비교 통과
- [ ] Demo 3 (3D Physics): 중력 시뮬레이션 + SPACE 임펄스 정상 — 콘솔 로그 확인
- [ ] Demo 4 (PBR): 5x5 구체 그리드 + IBL + Skybox 정상 — 스크린샷 비교 통과
- [ ] LiveCode (ColorPulseDemo): 핫 리로드 후 데모 자동 복원 정상 — 콘솔 로그 확인
- [ ] 데모 전환(숫자키): 모든 빌트인/LiveCode 데모 간 전환 정상
- [ ] F1 Wireframe 토글 정상
- [ ] 기능 변경 없음 — 리팩토링 전후 스크린샷 + 로그 동일
