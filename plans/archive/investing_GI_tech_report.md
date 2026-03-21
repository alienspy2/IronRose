# Screen Space GI 기술 조사 보고서

## IronRose 엔진 현황

| 항목 | 상태 |
|------|------|
| 렌더링 방식 | Deferred PBR (4-target G-Buffer + HDR) |
| G-Buffer | RT0: Albedo (RGBA8), RT1: Normal+Roughness (RGBA16F), RT2: Material (RGBA8), RT3: WorldPos (RGBA16F), Depth (D32S8) |
| 셰이더 | GLSL 450 → SPIR-V (Vulkan) |
| 포스트프로세싱 | PostProcessStack (ping-pong HDR, 플러그인 아키텍처) |
| 컴퓨트 셰이더 | 있음 (BC5/BC7 텍스처 압축용) |
| 기존 SS 기법 | 없음 (SSAO/SSR/SSGI 미구현) |

---

## 후보 기술 비교 요약

| 기술 | 성능 (1080p) | 수정량 | 간접광 품질 | 노이즈 | 디노이저 필요 |
|------|-------------|--------|------------|--------|-------------|
| **A. GTAO + Multi-Bounce** | 0.3~0.6ms | 최소 (셰이더 2개 + Effect 1개) | AO + 근사 색상 번짐 | 낮음 | 공간 필터만 |
| **B. Visibility Bitmask (SSIL)** | 1.0~1.3ms | 소 (셰이더 2개 + Effect 1개) | AO + 1-bounce 간접광 | 낮음 | 공간 필터만 |
| **C. Radiance Cascades** | ~3ms (PoE2 GTX1050) | 중 (컴퓨트 3~4패스) | 멀티바운스 GI | 없음 | 불필요 |
| **D. Hi-Z Ray March SSGI** | 2~5ms | 대 (Hi-Z + 트레이스 + 디노이즈) | 최고 (풀 컬러 블리딩) | 높음 | 시공간 필터 필수 |
| **E. SSDO** | 2~5ms | 중 (SH 인코딩 + 블러) | 1-bounce 방향성 | 중간 | 에지 블러 |
| **F. HBIL** | ~2.5ms | 중 | 근거리 간접광 | 중간 | TAA 권장 |

---

## A. GTAO + Multi-Bounce Color Approximation

### 개요
Ground Truth Ambient Occlusion (Jimenez et al., 2016, Activision). 반구를 2D 슬라이스로 분해하고, 내부 적분을 **해석적(analytical)**으로 풀어 물리적으로 정확한 AO를 계산. Monte Carlo는 외부 적분(슬라이스 방향)에만 사용.

### 알고리즘 (XeGTAO 기준, 3-pass 컴퓨트)

**Pass 1 - PrefilterDepths**: 하드웨어 뎁스 → 선형 뷰스페이스 뎁스 (R16F) + 5 MIP 레벨 생성. 16x16 워크그룹, groupshared 메모리 활용.

**Pass 2 - MainPass**: 핵심 GTAO 계산.
- 각 픽셀에서 m개 슬라이스 방향으로 호라이즌 트레이싱
- 슬라이스당 n개 샘플 (뎁스 MIP 계층 활용)
- `IntegrateArc(h1, h2, n)` 해석적 적분:
  ```
  0.25 * (-cos(2*h1 - n) + cos(n) + 2*h1*sin(n))
  + 0.25 * (-cos(2*h2 - n) + cos(n) + 2*h2*sin(n))
  ```
- 출력: AO (R8) 또는 AO+BentNormal (RGBA8)

**Pass 3 - Denoise**: 5x5 뎁스 인식 공간 필터. 에지 가중 가우시안.

### 품질 프리셋 (XeGTAO)

| 프리셋 | 슬라이스 | 슬라이스당 샘플 | 총 SPP | 상대 비용 |
|--------|---------|---------------|--------|----------|
| Low | 1 | 2 | 2 | ~44% |
| Medium | 2 | 4 | 8 | ~67% |
| **High** | **3** | **6** | **18** | **100%** |
| Ultra | 9 | 6 | 54 | ~300% |

### 성능 (High, Bent Normal 없음)

| GPU | 1080p | 1440p | 4K |
|-----|-------|-------|-----|
| RTX 3070 | 0.35ms | 0.6ms | 1.4ms |
| RTX 2060 | 0.56ms | 0.9ms | 2.0ms |
| GTX 1050 | 2.2ms | 3.5ms | N/A |

### Multi-Bounce 색상 근사 (Jimenez)
단일 바운스 AO를 알베도 기반 다중 반사로 보정하는 큐빅 다항식:
```hlsl
float3 MultiBounceAO(float ao, float3 albedo) {
    float3 a = 2.0404 * albedo - 0.3324;
    float3 b = -4.7951 * albedo + 0.6417;
    float3 c = 2.7552 * albedo + 0.6903;
    return max(ao, ((ao * a + b) * ao + c) * ao);
}
```
- 추가 GPU 비용: **사실상 0** (ALU 몇 개)
- 밝은 표면에서 과도한 어두워짐 방지
- 채널별 연산으로 색상 번짐 근사

### Bent Normal + 스펙큘러 오클루전 (GTSO)
- AO 계산 시 평균 미차단 방향 = bent normal
- IBL 샘플링 시 노멀 대신 bent normal 사용 → 방향성 오클루전
- 스펙큘러 오클루전: 반사벡터와 bent normal 사이 각도로 계산
- 추가 비용: ~25%

### IronRose 통합 방안
```
G-Buffer Pass
    ↓
GTAO PrefilterDepths (compute) → viewspace depth + MIPs
    ↓
GTAO MainPass (compute) → raw AO + edges
    ↓
GTAO Denoise (compute) → final AO (R8)
    ↓
Ambient Pass에서 적용: indirectDiffuse *= MultiBounceAO(ao, albedo)
```
- `PostProcessEffect`가 아닌 **라이팅 패스 이전**에 실행해야 함
- `deferred_ambient.frag`에서 AO 텍스처를 샘플링하여 적용
- 또는 별도 컴포짓 패스로 HDR 버퍼에 AO 적용

### 장단점
**장점**: 물리 기반 정확성 / 최고 성능 / 오픈소스(XeGTAO, MIT) / multi-bounce+bent normal 지원 / TAA 친화적 / 컴퓨트 전용
**단점**: 진정한 GI가 아님 (색상 블리딩은 근사) / 스크린 밖 정보 없음 / 얇은 오브젝트 헤리스틱 필요

### 참고자료
- Jimenez et al. 2016, "Practical Realtime Strategies for Accurate Indirect Occlusion"
- XeGTAO: https://github.com/GameTechDev/XeGTAO (MIT)
- SIGGRAPH 2016 Course: https://blog.selfshadow.com/publications/s2016-shading-course/

---

## B. Visibility Bitmask (SSIL 확장)

### 개요
GTAO의 2개 호라이즌 각도를 **32-bit 비저빌리티 비트마스크**로 대체. 각 비트가 반구 슬라이스의 각도 섹터를 표현. 겹치는 오클루더와 유한 두께 표면을 정확히 처리.

### 핵심 혁신
```glsl
uint updateSectors(float minH, float maxH, uint bitfield) {
    uint startBit = uint(minH * float(sectorCount));
    uint horizonAngle = uint(ceil((maxH - minH) * float(sectorCount)));
    uint angleBit = horizonAngle > 0u ? (0xFFFFFFFFu >> (32u - horizonAngle)) : 0u;
    return bitfield | (angleBit << startBit);
}
```
- `popcount(bitmask) / 32`로 가시성 계산
- 얇은 오브젝트 뒤로 빛 통과 가능 (무한 두께 문제 해결)

### 간접광 계산
```glsl
// 새로 차단 해제된 섹터에 의한 가중
lighting += (1.0 - float(bitCount(indirect & ~occlusion)) / float(sectorCount))
    * sampleLight * clamp(dot(normal, sampleHorizon), 0, 1)
    * clamp(dot(sampleNormal, -sampleHorizon), 0, 1);
```

### 성능 (RTX 2080, 1080p)

| 설정 | 비용 |
|------|------|
| AO only, 8 samples | 0.51ms |
| AO only, 16 samples | 1.13ms |
| 간접광, 8 samples | 1.23ms |
| 간접광, 16 samples, half-res | 1.07ms |
| 간접광, 32 samples | 4.33ms |

### IronRose 통합 방안
- GTAO와 동일한 파이프라인 위치 (라이팅 전)
- GTAO PrefilterDepths 재사용 가능
- MainPass에서 비트마스크 + 간접광 동시 계산
- 앰비언트 셰이더에서 간접광 텍스처 적용

### 장단점
**장점**: GTAO 대비 얇은 오브젝트 헤일로 제거 / 진짜 1-bounce 간접광 색상 / 노이즈 GTAO의 1/3~1/4 / 비트 연산으로 효율적
**단점**: GTAO보다 2~3배 비쌈 / 단일 레이어 뎁스 한계 / 샘플 수에 선형 비례

### 참고자료
- arXiv:2301.11376 "Screen Space Indirect Lighting with Visibility Bitmask"
- GLSL 구현: https://cdrinmatane.github.io/posts/ssaovb-code/

---

## C. Radiance Cascades (Screen Space)

### 개요
Alexander Sannikov (Grinding Gear Games, PoE2) 제안. **반음영 가설(Penumbra Hypothesis)** 기반: 거리에 비례하는 공간 해상도 x 거리에 반비례하는 각도 해상도 = 상수. 이를 이용해 계층적 라디언스 필드를 일정한 비용으로 구축.

### 캐스케이드 구조

| 캐스케이드 | 프로브 수 | 프로브당 레이 | 레이 길이 | 총 레이 수 |
|-----------|----------|-------------|----------|-----------|
| 0 | NxN | R | L | N^2*R |
| 1 | N/2 x N/2 | 4R | 4L | N^2*R (동일) |
| 2 | N/4 x N/4 | 16R | 16L | N^2*R (동일) |
| 3 | N/8 x N/8 | 64R | 64L | N^2*R (동일) |

**핵심**: 모든 캐스케이드의 총 레이 수가 동일 -> **일정한 메모리/연산 비용**

### 캐스케이드 병합 (Top-Down)
```
merged_radiance = near.rgb + far.rgb * near.alpha
merged_visibility = near.alpha * far.alpha
```
- 상위 캐스케이드에서 하위로 전파
- 4개 인접 프로브 바이리니어 보간
- 방향 매칭으로 각도 해상도 보간

### 메모리 레이아웃
- **Direction-First (추천)**: 같은 방향의 모든 프로브 레이를 그룹화 -> 하드웨어 바이리니어 보간 활용, 캐시 효율 극대화, 텍스처 샘플 16->1 감소
- **Pre-Averaging**: 4레이 평균 -> 메모리 75% 절감

### 성능

| 환경 | 비용 |
|------|------|
| PoE2, GTX 1050 (2.5D) | ~3ms |
| Direction-First, 1920x1080, RTX 3080 | ~26ms (풀 3D) |
| Direction-First, 1024x1024, RTX 3080 | ~8ms |

### 3D 적용의 과제
- 2D->3D: 프로브 그리드가 3D, 각도 방향이 2D(반구) -> **O(N^3 x M^2)** 스토리지
- **Screen-Space 접근이 실용적**: 프로브를 스크린 그리드에 배치, 뎁스 버퍼로 레이마치
- 3D Vulkan 구현 존재: RadianceCascadesVK3D (GitHub: JTLee98)

### IronRose 통합 방안
```
G-Buffer Pass
    |
Depth MIP 생성 (compute)
    |
캐스케이드별 레이마치 (compute, direction-first)
    |
캐스케이드 병합 (compute, top-down)
    |
최종 리졸브 -> 캐스케이드 0 바이리니어 샘플 -> 앰비언트 조명에 합산
```
- 컴퓨트 셰이더 3~4패스
- 캐스케이드별 스크린 크기 텍스처 (RGBA16F)
- G-Buffer 데이터 직접 활용 (position, normal, albedo)

### 장단점
**장점**: 완전 노이즈 프리 (디노이저 불필요) / 멀티바운스 / 씬 복잡도 무관 비용 / 지연 없는 즉시 반응 / 자연스러운 반음영
**단점**: 3D 구현 복잡도 높음 / 캐스케이드 경계 링잉 아티팩트 / 스크린 밖 정보 없음 / 레퍼런스 구현 적음 / 아직 연구 단계 (정식 논문 미발표)

### 참고자료
- radiance-cascades.com (WIP paper)
- jason.today/rc (구현 가이드)
- RadianceCascadesVK3D: https://github.com/JTLee98/RadianceCascadesVK3D
- three-rc: https://github.com/CodyJasonBennett/three-rc (3D 구현)

---

## D. Hi-Z Screen Space Ray Marching SSGI

### 개요
Hierarchical-Z 버퍼(뎁스 MIP 체인)를 가속 구조로 사용, 스크린 스페이스에서 레이마칭하여 간접광을 계산. 이전 프레임 컬러 버퍼에서 히트 지점의 색상을 읽어 간접 조명으로 사용.

### Hi-Z 버퍼 구축
```hlsl
// 각 MIP: 4개 이웃 중 최솟값
float d = min(min(d0, d1), min(d2, d3));
```
- R32F 또는 R16F, 풀 MIP 체인
- 구축 비용: ~0.5ms

### Hi-Z 트래버설
```
level = startLevel (보통 2)
WHILE level >= 0:
    cell = GetCell(ray.xy, mipResolution[level])
    minDepth = SampleHiZ(level, cell)
    IF ray.z > minDepth:
        ray = AdvanceToDepth(minDepth)  // 빈 공간 건너뜀
    newCell = GetCell(ray.xy)
    IF newCell != cell:
        ray = CrossBoundary(ray, cell)
        level += 2  // 상위 MIP로 올라감
    ELSE:
        level -= 1  // 하위 MIP로 내려감
```
- 100 이터레이션으로 선형 1000 이터레이션과 동등한 거리 커버

### 풀 파이프라인 (6패스)

1. **Stochastic Direction 생성**: 코사인 가중 반구 샘플 + IGN 노이즈
2. **Hi-Z 버퍼 구축**: 뎁스 MIP 체인
3. **레이마치**: Hi-Z 트래버설, 히트 시 이전 프레임 컬러 읽기
4. **시공간 디노이즈**:
   - 1/4 해상도 다운샘플
   - 바이래터럴 업스케일
   - A-Trous 웨이블릿 필터 (5레벨)
   - 템포럴 리프로젝션 + 네이버후드 클램핑
5. **컴포짓**: 직접광 + 간접광 합산

### 성능

| 설정 | 해상도 | 비용 |
|------|--------|------|
| Hi-Z, 100 iter | 1080p | ~1.8ms (트레이스만) |
| Hi-Z, 1000 iter | 1080p | ~1.8ms |
| 풀 파이프라인 (트레이스+디노이즈) | 1080p | ~2.5~5ms |
| UE4 SSGI Quality 3 (16ray x 8step) | 1080p | ~3~5ms |
| Half-res + bilateral upsample | 1080p | ~1.5~3ms |

### 디노이즈: SVGF (Spatiotemporal Variance-Guided Filtering)
```
FOR each level i = 0 to 4:
    stepSize = 2^i
    FOR each pixel:
        w = gaussian * w_depth * w_normal * w_luminance
        output = weighted sum of neighbors
```
- 템포럴 누적: 리프로젝션 + 지수 이동 평균
- 디스오클루전 감지: 뎁스 차이 임계값 + 노멀 유사도
- 고스팅 방지: 네이버후드 클램핑 (3x3 min/max)

### Half-Resolution 최적화
```hlsl
// 바이래터럴 업샘플: 뎁스 유사도 기반 가중
float4 depth_weights = exp(-abs(half_depths - full_depth) / sigma);
float4 final_weights = bilinear_weights * depth_weights;
```
- 레이마치 4x 절감 / 업샘플 ~0.15ms

### IronRose 통합 방안
```
G-Buffer Pass
    |
Hi-Z MIP 생성 (compute)
    |
Stochastic Direction 생성 (compute)
    |
Ray March (compute, half-res) -> raw indirect color
    |
Spatial Denoise (compute, A-Trous 3~5 level)
    |
Temporal Accumulation (compute) -> stable indirect
    |
Bilateral Upsample (compute) -> full-res
    |
Ambient/Composite Pass에서 적용
```
- 필요 버퍼: Hi-Z MIP (R16F), direction (RGBA16F), raw GI (RGBA16F), denoise intermediate x2, temporal history (RGBA16F), final GI (RGBA16F)
- 가장 많은 버퍼와 패스 필요

### 장단점
**장점**: 최고 품질 간접광 / 풀 컬러 블리딩 / 스펙큘러+디퓨즈 모두 가능 / Hi-Z는 SSR에도 재사용 가능
**단점**: 가장 비쌈 / 가장 많은 코드량 / 디노이저 필수 / 템포럴 고스팅 / 스크린 밖 정보 없음 / 이전 프레임 의존성

### 참고자료
- jpgrenier.org/ssr.html (Hi-Z 정밀도 분석)
- McGuire & Mara 2014, JCGT "Efficient GPU Screen-Space Ray Tracing"
- gamehacker1999.github.io/posts/SSGI/ (풀 파이프라인)

---

## E. SSDO (Screen Space Directional Occlusion)

### 개요
SSAO를 확장하여 방향성 오클루전 + 1-bounce 간접광을 계산. 구면 조화 함수(SH)로 인코딩하여 라이팅 셰이더에서 빛 방향과 dot product로 평가.

### 알고리즘
1. G-Buffer에서 위치 P, 노멀 n 읽기
2. 반경 r 반구에서 랜덤 샘플링 (스크린 디스크 투영)
3. 오클루전 방향 벡터를 2-band SH (L0+L1, 4계수)로 누적
4. 에지 보존 블러
5. 라이팅 시 SH 오클루전 x 라이트 방향 dot product로 감쇠

### 성능 (1280x720, GTX 8800)

| 설정 | 누적 | 블러 | 합계 |
|------|------|------|------|
| 16 samples | 2.6ms | 2.3ms (9x9) | ~5ms |
| 8 samples | 1.4ms | 1.3ms (5x5) | ~2.7ms |
| half-res, 16 samples | 1.0ms | 1.3ms | ~2.3ms |

### 장단점
**장점**: 방향성 오클루전 + 간접광 동시 / SSAO 대비 약간의 추가 비용 / SH로 다수 광원 효율적 처리
**단점**: 2008년 기법 / SH 2-band는 저주파만 표현 / 스크린 밖 한계 / 노멀맵 간섭

### 참고자료
- kayru.org/articles/dssdo/

---

## F. HBIL (Horizon-Based Indirect Lighting)

### 개요
HBAO/GTAO를 확장하여 호라이즌 슬라이스를 따라 간접광을 해석적으로 적분. 새로운 호라이즌 샘플이 기존 최대 각도를 높일 때, 각도 차이 x 해당 샘플 색상으로 가중.

### 알고리즘
1. 슬라이스 방향으로 스크린 스페이스 스텝
2. 각 스텝에서 최대 호라이즌 각도 추적
3. 호라이즌 갱신 시 각도 차이 = 새로 차단된 입체각
4. 해당 샘플의 색상 x 각도 차이 = 간접광 기여
5. 전체 슬라이스 합산

### 성능
- ~2.5ms at 1280x720, GTX 680 (비최적화)

### 장단점
**장점**: 단일 패스 / AO+간접광 동시 / 물리적 근거 있는 적분 / 레이마치 불필요
**단점**: 높이장(height field) 가정 -> 얇은 표면 뒤 빛 누출 / 낮은 샘플에서 노이즈 / 근거리만

### 참고자료
- GodComplex HBIL: https://github.com/Patapom/GodComplex/blob/master/Tests/TestHBIL/README.md

---

## 최종 추천 순위 (IronRose 기준)

### 1순위: GTAO + Multi-Bounce (성능 최우선 시)
- **이유**: 0.3~0.6ms로 최고 성능 / XeGTAO 오픈소스 직접 포팅 가능 / 수정량 최소 / multi-bounce로 색상 근사
- **구현량**: 컴퓨트 셰이더 3개 + RenderSystem에 3 디스패치 추가 + ambient 셰이더 수정
- **결과**: AO + 근사 GI (진정한 간접광은 아님)

### 2순위: Visibility Bitmask SSIL (균형 선택)
- **이유**: GTAO 기반이라 구조 유사 / 진짜 1-bounce 간접광 색상 / 얇은 오브젝트 문제 해결 / 1~1.3ms
- **구현량**: 컴퓨트 셰이더 3개 + RenderSystem 수정 + ambient 셰이더 수정
- **결과**: AO + 실제 1-bounce 색상 블리딩

### 3순위: Radiance Cascades (최신 기술 시)
- **이유**: 노이즈 프리 / 멀티바운스 / 씬 무관 비용
- **주의**: 3D 스크린스페이스 구현이 아직 실험적 / 레퍼런스 적음 / ~3ms+
- **구현량**: 컴퓨트 셰이더 4~5개 + 캐스케이드 텍스처 + 병합 로직

### 4순위: Hi-Z Ray March SSGI (최고 품질 시)
- **이유**: 최고 품질 풀 컬러 GI / 업계 표준
- **주의**: 2~5ms / 디노이저 필수 / 가장 많은 코드
- **구현량**: 셰이더 6~8개 + 다수 버퍼 + 템포럴 히스토리 관리

### 추천 조합 전략
**Phase 1**: GTAO + Multi-Bounce 구현 (기반 인프라: 뎁스 MIP, 컴퓨트 파이프라인)
**Phase 2**: Visibility Bitmask로 업그레이드 (GTAO 인프라 재사용, MainPass만 교체)
**Phase 3 (선택)**: Hi-Z 추가 시 SSR과 공유 가능
