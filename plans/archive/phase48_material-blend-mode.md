# Phase 48: Material별 Alpha Blending 모드 선택 기능

## 배경

현재 IronRose 렌더 파이프라인은 다음과 같이 고정된 렌더링 경로를 사용한다:

- **Mesh/Geometry**: 항상 Opaque (Deferred, G-Buffer에 직접 쓰기)
- **Sprite/Text**: 항상 Alpha Blend (Forward, SourceAlpha/InverseSourceAlpha)

Material에 블렌드 모드를 선택하는 기능이 없어, 반투명 메시(유리, 파티클, 홀로그램 등)나 Additive 이펙트(글로우, 레이저 등)를 메시로 표현할 수 없다.

## 목표

- Material에 `Opaque`, `AlphaBlend`, `Additive` 3가지 블렌드 모드를 선택 가능하게 한다.
- Opaque 메시는 기존대로 Deferred(G-Buffer) 경로를 사용한다.
- AlphaBlend/Additive 메시는 Forward 패스로 분리하여 렌더링한다.
- 반투명 메시는 카메라 거리 역순으로 정렬하여 올바른 블렌딩 결과를 보장한다.
- 에디터 InspectorPanel에서 Material의 BlendMode를 콤보박스로 선택할 수 있게 한다.

## 현재 상태

### Material.cs (`src/IronRose.Engine/RoseEngine/Material.cs`)
- BlendMode 관련 프로퍼티 없음.
- PBR 프로퍼티(color, metallic, roughness 등)와 텍스처 슬롯만 존재.

### RenderSystem.cs (`src/IronRose.Engine/RenderSystem.cs`)
- 파이프라인 필드: `_geometryPipeline`(Deferred), `_forwardPipeline`(Forward opaque), `_spritePipeline`(Forward alpha blend).
- 렌더 루프 순서:
  1. Geometry Pass -> G-Buffer (`DrawOpaqueRenderers`)
  2. Shadow Pass
  3. SSIL Pass
  4. Ambient/IBL Pass -> HDR
  5. Direct Lights -> HDR (Additive)
  6. Skybox
  7. Forward Pass -> HDR (wireframe, sprites, texts)
  8. Post-Processing + Upscale

### RenderSystem.Draw.cs (`src/IronRose.Engine/RenderSystem.Draw.cs`)
- `DrawOpaqueRenderers()`: `MeshRenderer._allRenderers`를 순회하며 모든 메시를 G-Buffer에 그림.
- `DrawMesh()`: 파이프라인 바인딩 없이 transform/material 업로드 후 draw call만 수행 (파이프라인은 호출자가 미리 설정).

### RoseCache.cs (`src/IronRose.Engine/AssetPipeline/RoseCache.cs`)
- `FormatVersion = 8`. Material 직렬화에 blendMode 필드 없음.
- `WriteMaterial()`/`ReadMaterial()`에서 color, emission, PBR, 텍스처만 직렬화.

### MaterialImporter.cs (`src/IronRose.Engine/AssetPipeline/MaterialImporter.cs`)
- `.mat` TOML 파일에서 Material 로드/저장. blendMode 필드 없음.

### ImGuiInspectorPanel.cs (`src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs`)
- `DrawMaterialEditor()` (라인 3022): color, normal, MRO, emission 편집 UI. BlendMode 콤보박스 없음.

## 설계

### 개요

1. `BlendMode` enum과 프로퍼티를 Material에 추가한다.
2. RenderSystem에서 AlphaBlend/Additive용 Forward 파이프라인을 추가 생성한다 (Veldrid Pipeline은 immutable).
3. 렌더 루프에서 MeshRenderer를 Opaque/Transparent로 분류하고, Transparent 메시를 Forward 패스에서 카메라 거리 역순으로 그린다.
4. 직렬화(RoseCache, MaterialImporter)에 blendMode를 추가한다.
5. 에디터 UI에 BlendMode 콤보박스를 추가한다.

**셰이더 수정은 불필요하다.** 블렌드 상태는 GPU 파이프라인 설정이므로, 기존 Forward 셰이더(`vertex.glsl`/`fragment.glsl`)를 그대로 사용하면 된다.

### 상세 설계

---

#### Phase 48a: Material에 BlendMode 추가

**수정 파일**: `src/IronRose.Engine/RoseEngine/Material.cs`

1. `BlendMode` enum 정의:
```csharp
namespace RoseEngine
{
    public enum BlendMode
    {
        Opaque = 0,
        AlphaBlend = 1,
        Additive = 2,
    }
}
```

2. Material 클래스에 프로퍼티 추가:
```csharp
public BlendMode blendMode { get; set; } = BlendMode.Opaque;
```

- 기본값 `Opaque`로 기존 동작과 호환.
- enum은 같은 파일 내 Material 클래스 위에 정의하거나, 별도 파일로 분리 (같은 파일 권장 — Material과 밀접).

---

#### Phase 48b: 직렬화 (RoseCache + MaterialImporter)

**수정 파일 1**: `src/IronRose.Engine/AssetPipeline/RoseCache.cs`

1. `FormatVersion` 8 -> 9 로 증가.
2. `WriteMaterial()` 맨 앞에 `writer.Write((byte)mat.blendMode);` 추가.
3. `ReadMaterial()` 맨 앞에 `mat.blendMode = (BlendMode)reader.ReadByte();` 추가.

```csharp
// WriteMaterial — 첫 줄에 추가
writer.Write((byte)mat.blendMode);
WriteColor(writer, mat.color);
// ...

// ReadMaterial — 첫 줄에 추가
mat.blendMode = (BlendMode)reader.ReadByte();
mat.color = ReadColor(reader);
// ...
```

> 버전 증가(8->9)에 의해 기존 캐시는 자동 무효화되므로 마이그레이션 불필요.

**수정 파일 2**: `src/IronRose.Engine/AssetPipeline/MaterialImporter.cs`

1. `Import()` 메서드에 blendMode 읽기 추가:
```csharp
var blendStr = config.GetString("blendMode", "Opaque");
if (Enum.TryParse<BlendMode>(blendStr, true, out var bm))
    mat.blendMode = bm;
```
- 위치: `mat.normalMapStrength = ...` 라인 다음.

2. `BuildConfig()` 메서드에 blendMode 쓰기 추가:
- 파라미터에 `BlendMode blendMode` 추가.
- 본문에 `config.SetValue("blendMode", blendMode.ToString());` 추가.
- `Opaque`가 기본값이므로 Opaque일 때도 명시적으로 저장한다 (TOML에서 명확하게 보이도록).

3. `WriteDefault()`: `BuildConfig` 호출에 `BlendMode.Opaque` 인자 추가.
4. `WriteMaterial()`: `BuildConfig` 호출에 `mat.blendMode` 인자 추가.

---

#### Phase 48c: RenderSystem 파이프라인 생성

**수정 파일**: `src/IronRose.Engine/RenderSystem.cs`

1. 새 파이프라인 필드 2개 추가 (라인 ~291, _spritePipeline 근처):
```csharp
private Pipeline? _meshAlphaBlendPipeline;
private Pipeline? _meshAdditivePipeline;
```

2. 파이프라인 생성 (라인 ~1057, _spritePipeline 생성 직전):

**_meshAlphaBlendPipeline**: Forward 셰이더 + Alpha Blend + Depth Test (write off) + Back-face Cull
```csharp
_meshAlphaBlendPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
{
    BlendState = new BlendStateDescription(
        RgbaFloat.Black,
        new BlendAttachmentDescription(
            blendEnabled: true,
            sourceColorFactor: BlendFactor.SourceAlpha,
            destinationColorFactor: BlendFactor.InverseSourceAlpha,
            colorFunction: BlendFunction.Add,
            sourceAlphaFactor: BlendFactor.One,
            destinationAlphaFactor: BlendFactor.InverseSourceAlpha,
            alphaFunction: BlendFunction.Add)),
    DepthStencilState = new DepthStencilStateDescription(
        depthTestEnabled: true, depthWriteEnabled: false, comparisonKind: ComparisonKind.LessEqual),
    RasterizerState = new RasterizerStateDescription(
        cullMode: FaceCullMode.Back, fillMode: PolygonFillMode.Solid,
        frontFace: FrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
    PrimitiveTopology = PrimitiveTopology.TriangleList,
    ResourceLayouts = new[] { _perObjectLayout!, _perFrameLayout! },
    ShaderSet = new ShaderSetDescription(
        vertexLayouts: new[] { vertexLayout },
        shaders: _forwardShaders!),
    Outputs = hdrOutputDesc,
});
```

**_meshAdditivePipeline**: Forward 셰이더 + Additive Blend + Depth Test (write off) + Back-face Cull
```csharp
_meshAdditivePipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
{
    BlendState = new BlendStateDescription(
        RgbaFloat.Black,
        new BlendAttachmentDescription(
            blendEnabled: true,
            sourceColorFactor: BlendFactor.SourceAlpha,
            destinationColorFactor: BlendFactor.One,
            colorFunction: BlendFunction.Add,
            sourceAlphaFactor: BlendFactor.One,
            destinationAlphaFactor: BlendFactor.One,
            alphaFunction: BlendFunction.Add)),
    DepthStencilState = new DepthStencilStateDescription(
        depthTestEnabled: true, depthWriteEnabled: false, comparisonKind: ComparisonKind.LessEqual),
    RasterizerState = new RasterizerStateDescription(
        cullMode: FaceCullMode.Back, fillMode: PolygonFillMode.Solid,
        frontFace: FrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
    PrimitiveTopology = PrimitiveTopology.TriangleList,
    ResourceLayouts = new[] { _perObjectLayout!, _perFrameLayout! },
    ShaderSet = new ShaderSetDescription(
        vertexLayouts: new[] { vertexLayout },
        shaders: _forwardShaders!),
    Outputs = hdrOutputDesc,
});
```

핵심 차이:
- AlphaBlend: `destinationColorFactor = InverseSourceAlpha` (표준 알파 블렌딩)
- Additive: `destinationColorFactor = One` (가산 블렌딩)
- 둘 다 `depthWriteEnabled: false` (반투명 객체는 깊이 버퍼에 쓰지 않음)

3. `Dispose()` 메서드에 추가:
```csharp
_meshAlphaBlendPipeline?.Dispose();
_meshAdditivePipeline?.Dispose();
```

---

#### Phase 48d: 렌더 루프 분리 (핵심)

**수정 파일**: `src/IronRose.Engine/RenderSystem.Draw.cs`

1. `DrawOpaqueRenderers()` 수정: BlendMode.Opaque인 메시만 G-Buffer에 그리도록 필터링 추가.

```csharp
// 기존
if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;

// 변경
if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
var mat = /* materialOverride 또는 renderer.material */;
if ((mat?.blendMode ?? BlendMode.Opaque) != BlendMode.Opaque) continue;
```

주의: material override 로직이 이미 존재하므로, `mat` 변수를 먼저 결정한 후 blendMode 체크.

2. 새 메서드 `DrawTransparentRenderers()` 추가:

```csharp
private void DrawTransparentRenderers(CommandList cl, System.Numerics.Matrix4x4 viewProj, Camera camera)
{
    // 1. 반투명 메시 수집
    var transparentList = new List<(MeshRenderer renderer, Material mat, float distSq)>();
    var camPos = camera.transform.position;

    foreach (var renderer in MeshRenderer._allRenderers)
    {
        if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
        if (renderer.gameObject._isEditorInternal) continue;
        var filter = renderer.GetComponent<MeshFilter>();
        if (filter?.mesh == null) continue;

        var mat = (_materialOverride != null &&
                   renderer.gameObject.GetInstanceID() == _materialOverrideObjectId)
            ? _materialOverride
            : renderer.material;

        var blendMode = mat?.blendMode ?? BlendMode.Opaque;
        if (blendMode == BlendMode.Opaque) continue;

        float distSq = (renderer.transform.position - camPos).sqrMagnitude;
        transparentList.Add((renderer, mat!, distSq));
    }

    if (transparentList.Count == 0) return;

    // 2. 카메라에서 먼 순서(Back-to-Front)로 정렬
    transparentList.Sort((a, b) => b.distSq.CompareTo(a.distSq));

    // 3. Forward 라이트 데이터 업로드
    UploadForwardLightData(cl, camera);

    // 4. 블렌드 모드별로 파이프라인 바인딩하며 그리기
    BlendMode currentMode = BlendMode.Opaque; // sentinel

    foreach (var (renderer, mat, _) in transparentList)
    {
        if (mat.blendMode != currentMode)
        {
            currentMode = mat.blendMode;
            cl.SetPipeline(currentMode == BlendMode.AlphaBlend
                ? _meshAlphaBlendPipeline
                : _meshAdditivePipeline);
        }

        var filter = renderer.GetComponent<MeshFilter>();
        var mesh = filter!.mesh!;
        mesh.UploadToGPU(_device!);
        if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) continue;

        var (matUniforms, texView, normalTexView, mroTexView) = PrepareMaterial(mat);
        DrawMesh(cl, viewProj, mesh, renderer.transform, matUniforms, texView,
                 bindPerFrame: true, normalTexView, mroTexView);
    }
}
```

**수정 파일**: `src/IronRose.Engine/RenderSystem.cs`

3. 렌더 루프(라인 ~1555)에서 Forward Pass 영역에 `DrawTransparentRenderers` 호출 추가:

```csharp
// === 5. Forward Pass -> HDR (sprites, text, wireframe, transparent meshes) ===
if (DebugOverlaySettings.wireframe && _wireframePipeline != null)
{
    UploadForwardLightData(cl, camera);
    cl.SetPipeline(_wireframePipeline);
    DrawAllRenderers(cl, viewProj, useWireframeColor: true);
}

// --- 반투명 메시 (AlphaBlend/Additive) ---
DrawTransparentRenderers(cl, viewProj, camera);

// --- 스프라이트/텍스트 (기존 그대로) ---
if (_spritePipeline != null && SpriteRenderer._allSpriteRenderers.Count > 0)
{
    DrawAllSprites(cl, viewProj, camera);
}
```

> 반투명 메시를 스프라이트/텍스트보다 먼저 그리는 이유: 메시는 3D 공간에 있고 depth test를 하므로, 스프라이트/텍스트(보통 UI나 2D 오버레이)보다 먼저 그려야 올바른 합성이 된다.

4. `DrawAllRenderers()` (wireframe용): 이 메서드는 모든 메시를 wireframe으로 그리므로 blendMode 필터링 불필요 (모든 메시 표시).

---

#### Phase 48e: 에디터 UI

**수정 파일**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs`

1. `DrawMaterialEditor()` (라인 3022) 상단에 BlendMode 콤보박스 추가:

```csharp
// ── Blend Mode ──
{
    var blendNames = new[] { "Opaque", "AlphaBlend", "Additive" };
    int currentIdx = (int)Math.Clamp(
        _editedMatTable.GetLong("blendMode", 0), 0, 2);
    // blendMode가 문자열로 저장되는 경우 파싱
    var blendStr = _editedMatTable.GetString("blendMode", "Opaque");
    currentIdx = blendStr switch
    {
        "AlphaBlend" => 1,
        "Additive" => 2,
        _ => 0,
    };

    string wl = EditorWidgets.BeginPropertyRow("Blend Mode");
    if (ImGui.Combo(wl, ref currentIdx, blendNames, blendNames.Length))
    {
        _undoTracker.BeginEdit("Mat.blendMode", Toml.FromModel(_editedMatTable));
        _editedMatTable.SetValue("blendMode", blendNames[currentIdx]);
        SaveMatFile();
    }
    if (ImGui.IsItemDeactivatedAfterEdit())
    {
        if (_undoTracker.EndEdit("Mat.blendMode", out var oldSnap))
        {
            var newSnap = Toml.FromModel(_editedMatTable);
            UndoSystem.Record(new MaterialPropertyUndoAction(
                "Change Material blendMode", _matFilePath, (string)oldSnap!, newSnap));
        }
    }
}
```

위치: `DrawMaterialEditor()` 내 `ImGui.Spacing();` (라인 3028) 바로 뒤, `// -- Base Surface --` 전.

2. `DrawReadOnlyMaterialInspector()` (라인 3096)에도 읽기 전용 BlendMode 표시 추가:

```csharp
// Blend Mode (readonly)
{
    var blendStr = mat.blendMode.ToString();
    string wl = EditorWidgets.BeginPropertyRow("Blend Mode");
    ImGui.TextUnformatted(blendStr);
}
```

---

#### Phase 48f: rose-cli 연동

**수정 파일**: `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`

현재 material 관련 CLI 명령어: `material.info`, `material.set_color`, `material.set_metallic`, `material.set_roughness`, `material.create`, `material.apply`. BlendMode 관련 명령 없음.

1. `material.info` 응답에 `blendMode` 필드 추가 (라인 ~1123):
```csharp
return JsonOk(new
{
    name = mat.name,
    blendMode = mat.blendMode.ToString(),  // 추가
    color = FormatColor(mat.color),
    // ...
});
```

2. `material.set_blend_mode` 핸들러 추가 (`material.set_roughness` 핸들러 다음):
```csharp
_handlers["material.set_blend_mode"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: material.set_blend_mode <goId> <Opaque|AlphaBlend|Additive>");
    var id = args[0];
    if (!Enum.TryParse<BlendMode>(args[1], true, out var mode))
        return JsonError($"Invalid blend mode: {args[1]}. Use: Opaque, AlphaBlend, Additive");
    return MainThread(() =>
    {
        var (renderer, _) = FindMeshRenderer(id);
        if (renderer?.material == null)
            return JsonError($"No material on GameObject: {id}");
        renderer.material.blendMode = mode;
        SaveMaterialToDisk(renderer.material);
        return JsonOk(new { blendMode = mode.ToString() });
    });
};
```

3. `material.create`에 blendMode 옵션 파라미터 추가:
```csharp
// 기존: material.create <name> <dirPath> [r,g,b,a]
// 변경: material.create <name> <dirPath> [r,g,b,a] [blendMode]
```

---

### 영향 범위

| 파일 | 변경 유형 |
|------|-----------|
| `src/IronRose.Engine/RoseEngine/Material.cs` | enum 추가, 프로퍼티 추가 |
| `src/IronRose.Engine/AssetPipeline/RoseCache.cs` | FormatVersion 증가, 직렬화 수정 |
| `src/IronRose.Engine/AssetPipeline/MaterialImporter.cs` | TOML 읽기/쓰기에 blendMode 추가 |
| `src/IronRose.Engine/RenderSystem.cs` | 파이프라인 2개 추가, 렌더 루프 수정, Dispose 수정 |
| `src/IronRose.Engine/RenderSystem.Draw.cs` | DrawOpaqueRenderers 필터링, DrawTransparentRenderers 추가 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs` | Material 에디터 UI 수정 |
| `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` | material.info에 blendMode 추가, material.set_blend_mode 핸들러 추가 |

### 기존 기능 영향

- **Opaque 메시**: 변경 없음. `blendMode` 기본값이 `Opaque`이므로 기존 동작 유지.
- **Sprite/Text**: 변경 없음. 기존 `_spritePipeline` 그대로 사용.
- **RoseCache**: 버전 8->9로 변경. 기존 캐시는 자동 무효화되어 재생성됨 (첫 실행 시 약간의 로딩 시간 추가).
- **기존 .mat 파일**: blendMode 키가 없으면 `Opaque`로 기본 동작. 하위 호환성 유지.

## 구현 단계

### Phase 48a: Material BlendMode 추가
- [ ] `BlendMode` enum 정의 (Material.cs)
- [ ] `Material.blendMode` 프로퍼티 추가
- [ ] `dotnet build` 확인

### Phase 48b: 직렬화
- [ ] RoseCache FormatVersion 8 -> 9
- [ ] `WriteMaterial()`에 blendMode byte 쓰기
- [ ] `ReadMaterial()`에 blendMode byte 읽기
- [ ] `MaterialImporter.Import()`에 blendMode 문자열 읽기
- [ ] `MaterialImporter.BuildConfig()`에 blendMode 파라미터 및 쓰기 추가
- [ ] `WriteDefault()`, `WriteMaterial()` 호출부 수정
- [ ] `dotnet build` 확인

### Phase 48c: 렌더 파이프라인 생성
- [ ] `_meshAlphaBlendPipeline` 필드 및 생성
- [ ] `_meshAdditivePipeline` 필드 및 생성
- [ ] `Dispose()`에 파이프라인 해제 추가
- [ ] `dotnet build` 확인

### Phase 48d: 렌더 루프 분리
- [ ] `DrawOpaqueRenderers()`에 Opaque 필터링 추가
- [ ] `DrawTransparentRenderers()` 메서드 추가
- [ ] 렌더 루프 Forward Pass 영역에 `DrawTransparentRenderers()` 호출 추가
- [ ] `dotnet build` 확인
- [ ] 에디터에서 Opaque 메시가 기존대로 렌더링되는지 확인
- [ ] AlphaBlend/Additive 메시가 Forward 패스에서 올바르게 렌더링되는지 확인

### Phase 48e: 에디터 UI
- [ ] `DrawMaterialEditor()`에 BlendMode 콤보박스 추가
- [ ] `DrawReadOnlyMaterialInspector()`에 BlendMode 읽기 전용 표시 추가
- [ ] `dotnet build` 확인
- [ ] 에디터에서 BlendMode 변경 -> 저장 -> 재로드 사이클 확인

### Phase 48f: rose-cli 연동
- [ ] `material.info`에 `blendMode` 필드 추가 (현재 color, metallic, roughness 등만 반환)
- [ ] `material.set_blend_mode` 핸들러 추가 (`material.set_color` 패턴 참고)
- [ ] `material.create`에 blendMode 옵션 파라미터 추가
- [ ] `dotnet build` 확인

## 대안 검토

### 1. 반투명 메시도 Deferred로 처리 (OIT)
- Order-Independent Transparency(OIT) 기법으로 G-Buffer에서 반투명을 처리하는 방법.
- **불채택 이유**: 구현 복잡도가 매우 높고(Weighted Blended OIT 또는 Per-Pixel Linked Lists 필요), 현 단계에서는 Forward 분리로 충분하다.

### 2. 블렌드 모드를 셰이더 기반으로 전환
- 셰이더 내부에서 `discard`를 사용한 Alpha Test(Cutout) 방식.
- **불채택 이유**: Alpha Test(Cutout)는 별도 블렌드 모드로 나중에 추가할 수 있다. 현재 요구사항인 AlphaBlend/Additive는 GPU 파이프라인 블렌드 상태로 처리하는 것이 정석이다.

### 3. 모든 메시를 Forward로 전환
- Deferred를 폐기하고 Forward+ 렌더링으로 통일.
- **불채택 이유**: 기존 Deferred 파이프라인(G-Buffer, SSIL, 다중 라이트 볼륨 등)이 이미 완성되어 있으므로, 하이브리드 방식 유지가 합리적이다.

## 미결 사항

- **Shadow**: 반투명 메시가 Shadow Pass에 참여해야 하는지? 현재는 `DrawOpaqueRenderers`와 별도의 shadow 경로를 사용 중. AlphaBlend 메시는 그림자를 드리우지 않거나 별도 처리가 필요할 수 있다. -> Phase 48에서는 반투명 메시의 그림자를 비활성화(생략)하고, 향후 필요시 별도 Phase로 추가한다.
- **Alpha Cutout(AlphaTest)**: `discard` 기반의 Cutout 모드는 현재 범위에 포함하지 않음. Deferred에서도 사용 가능한 모드이므로 별도 Phase로 추가 가능.
- **반투명 메시 정렬의 한계**: 오브젝트 중심점 기준 정렬은 교차하는 반투명 메시에서 시각적 아티팩트가 발생할 수 있다. 이는 Forward 렌더링의 일반적인 한계이며, OIT가 아닌 이상 완벽한 해결은 불가능하다.
