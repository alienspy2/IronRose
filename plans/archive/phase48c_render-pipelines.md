# Phase 48c: RenderSystem 파이프라인 생성

## 목표
- `_meshAlphaBlendPipeline`과 `_meshAdditivePipeline` 2개의 Veldrid Pipeline을 생성한다.
- 기존 Forward 셰이더를 재사용하며, 블렌드 상태와 깊이 쓰기만 다르게 설정한다.
- `Dispose()`에 새 파이프라인 해제를 추가한다.

## 선행 조건
- Phase 48a 완료 (`BlendMode` enum 존재)
- Phase 48b 완료 (직렬화 -- 빌드 성공 보장 목적)

## 수정할 파일

### `src/IronRose.Engine/RenderSystem.cs`

**변경 1: 파이프라인 필드 2개 추가**

위치: 라인 291, `_spritePipeline` 필드 바로 다음

현재 코드:
```csharp
        private Pipeline? _spritePipeline;
        private DeviceBuffer? _lightBuffer;
```

변경 후:
```csharp
        private Pipeline? _spritePipeline;
        private Pipeline? _meshAlphaBlendPipeline;
        private Pipeline? _meshAdditivePipeline;
        private DeviceBuffer? _lightBuffer;
```

---

**변경 2: 파이프라인 생성 코드 추가**

위치: 라인 1080, `_spritePipeline` 생성 블록 종료(`});`) 바로 다음, `// --- Debug Overlay Pipeline` 주석 전

현재 코드:
```csharp
            });

            // --- Debug Overlay Pipeline (→ Swapchain, overwrite) ---
```

변경 후:
```csharp
            });

            // --- Mesh AlphaBlend Pipeline (→ HDR, alpha blend, depth write off) ---
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

            // --- Mesh Additive Pipeline (→ HDR, additive blend, depth write off) ---
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

            // --- Debug Overlay Pipeline (→ Swapchain, overwrite) ---
```

---

**변경 3: Dispose()에 파이프라인 해제 추가**

위치: 라인 1790, `_spritePipeline?.Dispose();` 바로 다음

현재 코드:
```csharp
            _spritePipeline?.Dispose();

            // Shadow atlas
```

변경 후:
```csharp
            _spritePipeline?.Dispose();
            _meshAlphaBlendPipeline?.Dispose();
            _meshAdditivePipeline?.Dispose();

            // Shadow atlas
```

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] 파이프라인 생성 코드에서 `vertexLayout`, `_forwardShaders`, `hdrOutputDesc`, `_perObjectLayout`, `_perFrameLayout` 변수가 이미 스코프에 있음을 확인 (같은 메서드 내 기존 파이프라인 생성과 동일 위치)
- [ ] 앱 실행 시 기존 렌더링이 정상 동작 (새 파이프라인은 아직 사용되지 않으므로)

## 참고
- **AlphaBlend vs Additive 핵심 차이**:
  - AlphaBlend: `destinationColorFactor = InverseSourceAlpha` (표준 알파 블렌딩 -- 투명한 유리, 반투명 효과)
  - Additive: `destinationColorFactor = One` (가산 블렌딩 -- 글로우, 레이저, 파티클 효과)
- 둘 다 `depthWriteEnabled: false` -- 반투명 객체는 깊이 버퍼에 쓰지 않아야 뒤의 물체가 가려지지 않음.
- 둘 다 `depthTestEnabled: true` -- 불투명 물체 뒤에 있으면 그리지 않음.
- `cullMode: FaceCullMode.Back` -- 스프라이트(`None`)와 달리 메시는 백페이스 컬링 유지.
- Veldrid Pipeline은 immutable이므로 블렌드 모드별로 별도 Pipeline 객체가 필요.
- `_forwardShaders`를 재사용하므로 셰이더 수정 불필요.
