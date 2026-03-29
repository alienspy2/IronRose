# Phase 48d: 렌더 루프 분리 (Opaque/Transparent)

## 목표
- `DrawOpaqueRenderers()`에서 `BlendMode.Opaque`가 아닌 메시를 제외한다.
- 새 메서드 `DrawTransparentRenderers()`를 추가하여 AlphaBlend/Additive 메시를 Forward 패스로 렌더링한다.
- 반투명 메시를 카메라 거리 역순(Back-to-Front)으로 정렬하여 올바른 블렌딩 결과를 보장한다.
- Shadow 패스에서도 반투명 메시를 제외한다.
- 렌더 루프에서 스프라이트/텍스트보다 먼저 반투명 메시를 그린다.

## 선행 조건
- Phase 48c 완료 (`_meshAlphaBlendPipeline`, `_meshAdditivePipeline` 필드 존재)

## 수정할 파일

### 1. `src/IronRose.Engine/RenderSystem.Draw.cs`

**변경 1: DrawOpaqueRenderers()에 BlendMode 필터링 추가**

위치: `DrawOpaqueRenderers` 메서드 내, material override 로직 이후

현재 코드 (라인 15~31):
```csharp
        private void DrawOpaqueRenderers(CommandList cl, System.Numerics.Matrix4x4 viewProj)
        {
            foreach (var renderer in MeshRenderer._allRenderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer.gameObject._isEditorInternal) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter?.mesh == null) continue;
                var mesh = filter.mesh;
                mesh.UploadToGPU(_device!);
                if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) continue;

                // Material override: drag-hover preview용 임시 Material
                var mat = (_materialOverride != null &&
                           renderer.gameObject.GetInstanceID() == _materialOverrideObjectId)
                    ? _materialOverride
                    : renderer.material;
                var (matUniforms, texView, normalTexView, mroTexView) = PrepareMaterial(mat);
                DrawMesh(cl, viewProj, mesh, renderer.transform, matUniforms, texView, bindPerFrame: false, normalTexView, mroTexView);
            }
        }
```

변경 후:
```csharp
        private void DrawOpaqueRenderers(CommandList cl, System.Numerics.Matrix4x4 viewProj)
        {
            foreach (var renderer in MeshRenderer._allRenderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer.gameObject._isEditorInternal) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter?.mesh == null) continue;

                // Material override: drag-hover preview용 임시 Material
                var mat = (_materialOverride != null &&
                           renderer.gameObject.GetInstanceID() == _materialOverrideObjectId)
                    ? _materialOverride
                    : renderer.material;

                // Opaque가 아닌 메시는 Forward transparent 패스에서 처리
                if ((mat?.blendMode ?? BlendMode.Opaque) != BlendMode.Opaque) continue;

                var mesh = filter.mesh;
                mesh.UploadToGPU(_device!);
                if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) continue;

                var (matUniforms, texView, normalTexView, mroTexView) = PrepareMaterial(mat);
                DrawMesh(cl, viewProj, mesh, renderer.transform, matUniforms, texView, bindPerFrame: false, normalTexView, mroTexView);
            }
        }
```

핵심 변경:
- `mat` 변수를 mesh upload 전에 결정 (material override 로직 위치 변경)
- blendMode 체크 후 Opaque가 아니면 `continue`
- `mesh` 변수 선언과 `UploadToGPU`를 blendMode 체크 이후로 이동 (불필요한 GPU 업로드 방지)

---

**변경 2: DrawTransparentRenderers() 메서드 추가**

위치: `DrawOpaqueRenderers` 메서드 바로 다음, `DrawAllRenderers` 메서드 전

현재 코드:
```csharp
        }

        private void DrawAllRenderers(CommandList cl, System.Numerics.Matrix4x4 viewProj, bool useWireframeColor)
```

변경 후 (사이에 새 메서드 삽입):
```csharp
        }

        private void DrawTransparentRenderers(CommandList cl, System.Numerics.Matrix4x4 viewProj, Camera camera)
        {
            // 1. 반투명 메시 수집
            var transparentList = new System.Collections.Generic.List<(MeshRenderer renderer, Material mat, float distSq)>();
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
            BlendMode currentMode = BlendMode.Opaque; // sentinel (아직 파이프라인 미바인딩)

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

        private void DrawAllRenderers(CommandList cl, System.Numerics.Matrix4x4 viewProj, bool useWireframeColor)
```

구현 상세:
- `bindPerFrame: true` -- Forward 패스이므로 per-frame 리소스셋(라이트 데이터) 바인딩 필요
- `UploadForwardLightData` -- 라이트 데이터를 GPU 버퍼에 업로드 (기존 Forward 패스와 동일)
- 정렬은 오브젝트 중심점 기준 거리제곱(`sqrMagnitude`) 사용 (sqrt 불필요, 비교만 하면 됨)
- `currentMode`를 sentinel(`Opaque`)로 초기화하여 첫 반투명 메시에서 반드시 파이프라인 바인딩

**필요한 using 추가**: 파일 상단에 `using RoseEngine;`가 이미 존재하므로 `BlendMode` 접근 가능. `System.Collections.Generic`도 이미 존재하는지 확인. 없으면 추가하거나 `List<>` 앞에 전체 네임스페이스를 사용 (위 코드에서 `System.Collections.Generic.List`로 작성).

---

### 2. `src/IronRose.Engine/RenderSystem.cs`

**변경 3: 렌더 루프에 DrawTransparentRenderers 호출 추가**

위치: 라인 1555~1571, Forward Pass 영역

현재 코드:
```csharp
            // === 5. Forward Pass → HDR (sprites, text, wireframe) ===
            if (DebugOverlaySettings.wireframe && _wireframePipeline != null)
            {
                UploadForwardLightData(cl, camera);
                cl.SetPipeline(_wireframePipeline);
                DrawAllRenderers(cl, viewProj, useWireframeColor: true);
            }

            if (_spritePipeline != null && SpriteRenderer._allSpriteRenderers.Count > 0)
            {
                DrawAllSprites(cl, viewProj, camera);
            }
```

변경 후:
```csharp
            // === 5. Forward Pass → HDR (sprites, text, wireframe, transparent meshes) ===
            if (DebugOverlaySettings.wireframe && _wireframePipeline != null)
            {
                UploadForwardLightData(cl, camera);
                cl.SetPipeline(_wireframePipeline);
                DrawAllRenderers(cl, viewProj, useWireframeColor: true);
            }

            // --- 반투명 메시 (AlphaBlend/Additive) ---
            DrawTransparentRenderers(cl, viewProj, camera);

            if (_spritePipeline != null && SpriteRenderer._allSpriteRenderers.Count > 0)
            {
                DrawAllSprites(cl, viewProj, camera);
            }
```

반투명 메시를 스프라이트/텍스트보다 **먼저** 그리는 이유: 메시는 3D 공간에 있고 depth test를 하므로, 스프라이트/텍스트(보통 UI나 2D 오버레이)보다 먼저 그려야 올바른 합성이 된다.

`viewProj` 변수 참고: 이 위치에서 사용 가능한 `viewProj`는 지터링이 적용된 `jitteredViewProj`가 아닌 일반 `viewProj`임. Forward 패스에서는 기존 코드도 `viewProj`를 사용하므로 동일하게 사용. 실제로 이 스코프에서 어떤 변수가 사용되는지 확인 필요 -- 기존 `DrawAllRenderers(cl, viewProj, ...)` 호출과 동일한 `viewProj`를 사용한다.

---

### 3. `src/IronRose.Engine/RenderSystem.Shadow.cs`

**변경 4: Shadow Pass에서 반투명 메시 제외**

반투명 메시는 그림자를 드리우지 않도록 한다 (설계 문서 미결 사항 -- Phase 48에서는 비활성화).

위치 1: 라인 155~158, 첫 번째 shadow 렌더링 루프

현재 코드:
```csharp
            foreach (var renderer in MeshRenderer._allRenderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer.gameObject._isEditorInternal) continue;
```

변경 후:
```csharp
            foreach (var renderer in MeshRenderer._allRenderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer.gameObject._isEditorInternal) continue;
                if ((renderer.material?.blendMode ?? BlendMode.Opaque) != BlendMode.Opaque) continue;
```

위치 2: 라인 218~221, 두 번째 shadow 렌더링 루프 (Point Light faces)

현재 코드:
```csharp
                foreach (var renderer in MeshRenderer._allRenderers)
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    if (renderer.gameObject._isEditorInternal) continue;
```

변경 후:
```csharp
                foreach (var renderer in MeshRenderer._allRenderers)
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    if (renderer.gameObject._isEditorInternal) continue;
                    if ((renderer.material?.blendMode ?? BlendMode.Opaque) != BlendMode.Opaque) continue;
```

- Shadow 패스에서는 material override를 고려하지 않음 (기존 코드에서도 material override 없이 `renderer.material`을 직접 사용하지 않고 transform만 사용).
- `renderer.material`이 null일 수 있으므로 null-conditional + null-coalescing으로 안전하게 처리.
- `using RoseEngine;`가 이 파일에 있는지 확인 필요. 없으면 추가.

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] 에디터 실행 시 기존 Opaque 메시가 정상 렌더링됨 (회귀 없음)
- [ ] `DrawAllRenderers` (wireframe)는 blendMode 필터링 없이 모든 메시를 표시 (기존 동작 유지)
- [ ] 반투명 메시가 있을 때 Forward 패스에서 올바르게 그려지는지 확인 (Material의 blendMode를 AlphaBlend/Additive로 설정한 메시)

## 참고
- `DrawTransparentRenderers`에서 `List<>` 할당이 매 프레임 발생하므로, 향후 최적화 시 클래스 필드로 재사용 가능. 현재 단계에서는 간단함 우선.
- 오브젝트 중심점 기준 정렬의 한계: 교차하는 반투명 메시에서 시각적 아티팩트 발생 가능. 이는 Forward 렌더링의 일반적 한계이며 OIT 없이는 완벽한 해결 불가.
- `UploadForwardLightData`는 `DrawTransparentRenderers` 내부에서 호출하므로, wireframe이 비활성이고 반투명 메시만 있는 경우에도 라이트 데이터가 올바르게 업로드됨.
- RenderSystem.Shadow.cs에 `using RoseEngine;`가 없을 수 있음. 파일 상단의 using 목록을 확인하고 필요시 추가할 것.
