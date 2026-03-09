# IronRose Render Pipeline Analysis

IronRose 엔진은 Veldrid 그래픽스 API를 기반으로 하는 **Forward/Deferred 하이브리드 렌더링 파이프라인**을 사용합니다. 물리 기반 렌더링(PBR), 실시간 그림자(Shadow Atlas), 스크린 공간 간접 조명(SSIL) 및 최신 업스케일링 기술(FSR)을 지원합니다.

## 1. 개요 (Overview)
- **Graphics API**: Veldrid (Vulkan, Direct3D 11, Metal, OpenGL 지원)
- **Rendering Strategy**: Deferred Shading을 주축으로 하며, 투명 객체 및 디버그 오버레이는 Forward Pass로 처리합니다.
- **Color Space**: Linear HDR (R16G16B16A16_Float) 기반 렌더링 후 ACES Tone Mapping 적용.

---

## 2. G-Buffer 구조
Deferred 렌더링을 위해 다음의 5개 Color Target과 1개의 Depth Buffer를 사용합니다 (MRT).

| RT 명칭 | 포맷 | 내용 |
| :--- | :--- | :--- |
| **RT0: Albedo** | R8_G8_B8_A8_UNorm | 베이스 컬러 및 투명도(Alpha) |
| **RT1: Normal** | R16_G16_B16_A16_Float | 월드 공간 노멀 플로우 ($xyz$) |
| **RT2: Material** | R8_G8_B8_A8_UNorm | $x$: Metallic, $y$: Roughness, $z$: Occlusion |
| **RT3: WorldPos** | R16_G16_B16_A16_Float | 월드 좌표 ($xyz$), $a=0$이면 기하구조 없음 |
| **RT4: Velocity** | R16_G16_Float | 프레임 간 픽셀 이동 벡터 (FSR/TAA용) |
| **Depth** | D32_Float_S8_UInt | 깊이 정보 |

---

## 3. 프레임 렌더링 흐름 (Frame Flow)

### Step 1: Geometry Pass (G-Buffer)
- 불투명(Opaque) 객체들을 G-Buffer에 렌더링합니다.
- TAA/FSR을 위한 Halton Sequence 기반의 **Sub-pixel Jittering**이 적용됩니다.

### Step 2: Shadow Pass (Shadow Atlas)
- 모든 그림자 투영 광원(Light)에 대해 4096x4096 크기의 **Shadow Atlas**에 깊이 정보를 기록합니다.
- **VSM (Variance Shadow Mapping)** 방식을 채택하여 $Depth$와 $Depth^2$를 기록(RG32F)합니다.
- 기록 후 가우시안 블루러(Gaussian Blur)를 적용하여 부드러운 그림자 경계를 구현합니다.

### Step 3: SSIL / GTAO Pass (Compute)
- **SSIL (Screen-Space Indirect Lighting)**: 화면 공간 레이마칭을 통해 간접 조명과 AO를 계산합니다.
- Depth MIP Chain 생성 $\rightarrow$ Main Raymarching $\rightarrow$ Bilateral Denoise $\rightarrow$ Temporal Filter 과정을 거칩니다.

### Step 4: Ambient / IBL Pass
- G-Buffer와 SSIL 결과를 조합하여 환경광(Ambient)과 IBL(Image Based Lighting)을 렌더링합니다.
- 파노라마 익위렉탱귤러(Equirectangular) 텍스처를 큐브맵으로 변환하여 사용합니다.

### Step 5: Direct Lights Pass (Additive)
- **Light Volume Rendering**: 점광원(Point)은 구체(Sphere), 스포트광원(Spot)은 원뿔(Cone) 메쉬를 사용하여 해당 영역만 연산합니다.
- 가시 영역(Viewport) 내의 모든 광원을 Additive Blending으로 HDR 버퍼에 누적합니다.

### Step 6: Skybox & Forward Pass
- HDR 버퍼 위에 스카이박스를 렌더링합니다.
- 투명 객체(Sprite, Text) 및 디버그 와이어프레임을 순차적으로 Forward 렌더링합니다.

### Step 7: Post-Processing & Upscaling
- **Bloom**: 쓰레숄드 추출 후 가우시안 블러 합성.
- **Tone Mapping**: HDR을 LDR로 변환 (ACES).
- **FSR (FidelityFX Super Resolution)**: 고해상도 업스케일링 및 Temporal Anti-Aliasing.
- **CAS (Contrast Adaptive Sharpening)**: 최종 선명도 보정.

---

## 4. 주요 기술적 특징

### 실시간 그림자 (Shadows)
- **VSM**: Variance Shadow Maps를 사용하여 그림자 앨리어싱을 억제하고 소프트 쉐도우를 구현합니다.
- **Atlas Allocation**: 광원의 해상도에 따라 아틀라스 공간을 동적으로 할당합니다.
- **Point Light**: 6면(Cube Face)을 아틀라스의 별도 타일에 각각 렌더링하는 방식을 사용합니다.

### 간접 조명 (SSIL)
- 화면 공간의 기하 정보를 활용하여 실시간 Global Illumination 효과를 모사합니다.
- 노멀 정보를 활용하여 빛의 산란(Indirect Bounce)을 표현합니다.

### 업스케일링 (FSR & Jitter)
- 렌더링 해상도를 낮춰 성능을 확보하고, temporal 정보와 업스케일링 알고리즘을 통해 품질을 복원합니다.
- 프레임마다 미세한 카메라 흔들림(Jitter)을 주어 정보를 더 많이 수집합니다.

### 렌더 컨텍스트 (RenderContext)
- 에디터 내의 Scene View와 Game View를 분리하여 관리할 수 있도록 설계되었습니다.
- 해상도 독립적인 렌더링 리소스를 유지하며 멀티 뷰포트 출력을 지원합니다.
