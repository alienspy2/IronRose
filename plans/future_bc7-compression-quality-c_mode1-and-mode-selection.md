# Phase 3: Mode 1 추가 및 모드 선택 전략

## 목표
- BC7 Mode 1 (2 서브셋, 64 파티션, RGB 6비트, shared p-bit, 3비트 인덱스) 구현
- 블록별 Mode 6 vs Mode 1 최적 모드 선택 로직 추가
- 불투명 텍스처(albedo, MRO 등)의 압축 품질 대폭 향상

## 선행 조건
- Phase 2 완료 (PCA 기반 엔드포인트 피팅이 Mode 1에서도 재사용됨)

## 수정할 파일

### `Shaders/compress_bc7.comp`
- **변경 내용**: Mode 1 인코딩 로직 추가, 모드 선택 분기 추가, 파티션/앵커 테이블 상수 추가
- **이유**: 설계 문서 섹션 A-3(Mode 1), A-4(모드 선택 전략)

#### 상세 구현 가이드

**1. 상수 테이블 추가**

셰이더 상단(WEIGHTS 배열 뒤)에 다음 상수 추가:

```glsl
// BC7 Mode 1 interpolation weights (3-bit, 8 entries)
const uint WEIGHTS3[8] = uint[8](
    0, 9, 18, 27, 37, 46, 55, 64
);

// BC7 2-subset partition table (64 partitions)
// 각 uint는 16개 텍셀의 서브셋 할당을 2비트씩 패킹
// 비트 [2*i +: 2] = 텍셀 i의 서브셋 번호 (0 or 1)
// Microsoft BC7 Format Reference의 Table P2 참조
const uint PARTITIONS2[64] = uint[64](
    0xCCCC3333u, 0x88887777u, 0xEEEE1111u, 0xECC81337u,
    0xC880377Fu, 0xFEEC0113u, 0xFEC80137u, 0xEC80137Fu,
    0xC80037FFu, 0x0000FFFFu, 0xCCCC0000u, 0xFFF00000u,
    0xEEE00000u, 0xFFF0FFF0u, 0xF0F0F0F0u, 0x0FF00FF0u,
    0xEEEE0000u, 0x88880000u, 0xFFFF0000u, 0xCCCC0000u,
    0xFF000000u, 0xCCC00000u, 0xFFFF0000u, 0xFF00FF00u,
    0xCCCCCCCCu, 0xFFCC00CCu, 0xFFFF00FFu, 0x0CCCC033u,
    0x00CC0033u, 0xCCCC0033u, 0x3333CCCCu, 0x00000CCCu,
    0x0000CCCCu, 0x00CC00CCu, 0x0CC00CC0u, 0x00CC00CCu,
    0x0C0C0C0Cu, 0x0CC00CC0u, 0xCC0000CCu, 0x0CC00000u,
    0x0C0CC0C0u, 0xCC000000u, 0xC0C00000u, 0xC0C0C0C0u,
    0x0CCC0000u, 0xCC330000u, 0xCC3300CCu, 0x00CCCC00u,
    0x33CC0000u, 0x00CC0000u, 0x33003300u, 0xCC0000CCu,
    0x0C000C00u, 0x00CC0000u, 0x33330000u, 0x003C003Cu,
    0xFC00FC00u, 0x33331111u, 0x0000CCCCu, 0x33000000u,
    0xCC00CC00u, 0x0C0C0000u, 0x000C000Cu, 0x3F003F00u
);

// 2-subset anchor index table (서브셋 1의 앵커 텍셀 인덱스)
// partition i의 서브셋 1에서 MSB를 0으로 fix-up할 텍셀 인덱스
const uint ANCHOR2[64] = uint[64](
    15u,15u,15u, 15u,15u,15u, 15u,15u,
    15u,15u,15u, 15u,15u,15u, 15u,15u,
    15u, 2u, 8u,  2u, 2u, 8u, 8u,15u,
     2u, 8u, 2u,  2u, 8u, 8u, 2u, 2u,
    15u,15u, 6u,  8u, 2u, 8u,15u,15u,
     2u, 8u, 2u,  2u, 2u,15u,15u, 6u,
     6u, 2u, 6u,  8u,15u,15u, 2u, 2u,
    15u,15u,15u, 15u,15u, 2u, 2u,15u
);
```

**중요**: 위의 파티션 테이블 값은 **참조용 예시**이다. 실제 구현 시 반드시 Microsoft BC7 Format 공식 스펙(https://learn.microsoft.com/en-us/windows/win32/direct3d11/bc7-format-mode-reference)의 2-subset partition table을 정확히 인코딩해야 한다. 각 `uint`의 비트 [2*i +: 2]가 텍셀 i의 서브셋 번호(0 또는 1)를 나타내도록 인코딩.

**2. 서브셋 텍셀 분류 헬퍼**

```glsl
// 파티션 p에서 텍셀 t가 속하는 서브셋 번호 (0 or 1) 반환
uint getSubset(uint partitionIdx, uint texelIdx) {
    return (PARTITIONS2[partitionIdx] >> (texelIdx * 2u)) & 1u;
}
```

**참고**: 위 인코딩은 텍셀당 2비트를 사용하는 방식이다. 실제로 2-subset은 텍셀당 1비트면 충분하므로, 텍셀당 1비트 패킹으로 최적화 가능:
```glsl
uint getSubset(uint partitionIdx, uint texelIdx) {
    return (PARTITIONS2[partitionIdx] >> texelIdx) & 1u;
}
```
이 경우 PARTITIONS2의 각 uint는 하위 16비트만 사용. 어느 방식이든 스펙과 일관성만 있으면 됨.

**3. Mode 1 인코딩 함수**

`main()` 외부 또는 내부에 Mode 1 인코딩 로직 구현. 메인 루프 구조:

```glsl
// Mode 1 최적 인코딩 탐색
uint mode1BestError = 0xFFFFFFFFu;
uint mode1BestPartition = 0u;
uvec4 mode1BestData = uvec4(0u);

// 불투명 블록 체크: 모든 텍셀의 알파가 255인지 확인
bool allOpaque = true;
for (uint t = 0u; t < 16u; t++) {
    if (texels[t].a != 255u) { allOpaque = false; break; }
}

if (allOpaque) {
    for (uint part = 0u; part < 64u; part++) {
        // 서브셋 0/1 텍셀 분류
        // 각 서브셋별 PCA 엔드포인트 피팅 (Phase 2의 PCA 로직 재사용)
        //   - 서브셋에 속하는 텍셀만으로 mean, covariance, power iteration
        //   - RGB 3채널만 사용 (Mode 1은 알파 미지원)
        
        // RGB 6비트 양자화 + shared p-bit (2조합: pbit=0, pbit=1)
        // 각 서브셋의 두 엔드포인트가 같은 p-bit 공유
        for (uint pbit = 0u; pbit < 2u; pbit++) {
            // 6비트 양자화: e_6 = (color >> 2) & 0x3F
            // 복원: val = (e_6 << 1) | pbit; 이것이 7비트 값
            //        8비트 = (7bit << 1) | (7bit >> 6)
            
            // 3비트 인덱스 (8레벨) 탐색: WEIGHTS3 사용
            // 서브셋별로 인덱스 계산 + SSD 에러 합산
            
            // 앵커 fix-up:
            //   서브셋 0: index[0]의 MSB가 1이면 엔드포인트 스왑 + 인덱스 반전(7-idx)
            //   서브셋 1: ANCHOR2[part] 위치 텍셀의 인덱스 MSB가 1이면 스왑 + 반전
            
            // 에러 합산
            uint partError = subset0Error + subset1Error;
            if (partError < mode1BestError) {
                mode1BestError = partError;
                mode1BestPartition = part;
                // 비트 패킹하여 mode1BestData에 저장
            }
        }
    }
}
```

**4. Mode 1 비트 패킹**

BC7 Mode 1 비트 레이아웃 (128비트):
```
비트 0~1:   모드 (0b10 = Mode 1, 비트 1이 set)
비트 2~7:   파티션 인덱스 (6비트, 0~63)
비트 8~13:  R0 (6비트)
비트 14~19: R1 (6비트)
비트 20~25: R2 (6비트) -- 서브셋 1의 엔드포인트 0
비트 26~31: R3 (6비트) -- 서브셋 1의 엔드포인트 1
비트 32~55: G0,G1,G2,G3 (각 6비트)
비트 56~79: B0,B1,B2,B3 (각 6비트)
비트 80:    P0 (서브셋 0 shared p-bit)
비트 81:    P1 (서브셋 1 shared p-bit)
비트 82~127: 인덱스 (46비트)
  - 앵커 텍셀은 2비트 (MSB 생략), 나머지는 3비트
  - 서브셋 0 앵커: 항상 텍셀 0
  - 서브셋 1 앵커: ANCHOR2[partition]
```

인덱스 패킹 시 주의:
- 텍셀 0~15 순서로 인덱스를 패킹
- 텍셀 0은 항상 2비트 (서브셋 0 앵커)
- ANCHOR2[partition] 위치의 텍셀은 2비트 (서브셋 1 앵커)
- 나머지 텍셀은 3비트

**5. 모드 선택 (A-4)**

```glsl
// Phase 2에서 계산한 Mode 6 결과의 에러
uint mode6Error = bestTotalError; // Phase 2의 P-bit 전수탐색 결과

// 최종 출력 결정
uvec4 finalData;
if (allOpaque && mode1BestError < mode6Error) {
    finalData = mode1BestData;
} else {
    finalData = mode6Data; // Phase 2에서 패킹한 Mode 6 데이터
}

// 출력
uint blockIdx = (by * blocksX + bx) * 4u;
blocks[blockIdx + 0u] = finalData.x;
blocks[blockIdx + 1u] = finalData.y;
blocks[blockIdx + 2u] = finalData.z;
blocks[blockIdx + 3u] = finalData.w;
```

**6. main() 전체 흐름 재구성**

1. 텍셀 16개 로드
2. 불투명 체크 (allOpaque)
3. **Mode 6 인코딩** (Phase 2의 PCA + P-bit 전수탐색) -> mode6Data, mode6Error
4. **Mode 1 인코딩** (allOpaque인 경우만) -> mode1BestData, mode1BestError
5. 모드 선택: 에러가 작은 쪽 출력
6. 블록 쓰기

**7. 코드 구조화 제안**

셰이더가 길어지므로 함수로 분리 권장:
- `uvec4 encodeMode6(uvec4 texels[16], out uint error)` - Mode 6 전체 인코딩
- `uvec4 encodeMode1(uvec4 texels[16], out uint error)` - Mode 1 전체 인코딩  
- `void pcaFit(uvec4 texels[16], uint mask, out uvec4 ep0, out uvec4 ep1)` - PCA 피팅 (mask로 서브셋 필터링)

GLSL 450에서는 배열을 함수 인자로 넘기는 것이 가능하나, `inout`/`out` 배열은 성능에 영향이 있을 수 있음. 인라인으로 작성하는 것도 고려.

## 검증 기준
- [ ] `dotnet build` 성공 (셰이더 파일은 빌드 대상이 아니므로 C# 빌드에 영향 없음)
- [ ] 셰이더 파일이 유효한 GLSL 450 코드
- [ ] 기존 바인딩(set=0, binding 0/1/2)과 워크그룹 크기(8x8x1) 유지
- [ ] 불투명 텍스처(알파 255) 임포트 시 Mode 1이 선택되는 블록이 존재하는지 확인
- [ ] 반투명 텍스처 임포트 시 Mode 6만 사용되는지 확인 (Mode 1이 선택되지 않아야 함)
- [ ] 임포트된 텍스처의 시각적 품질이 이전보다 향상됨 (밴딩 감소)

## 참고
- BC7 스펙 참조: https://learn.microsoft.com/en-us/windows/win32/direct3d11/bc7-format-mode-reference
- **파티션 테이블의 정확한 값은 반드시 공식 스펙에서 가져올 것**. 이 명세서의 PARTITIONS2 값은 참조용 예시이며, 실제 스펙 테이블과 다를 수 있음
- 앵커 인덱스 테이블(ANCHOR2)도 공식 스펙의 "Anchor index values for the second subset" 테이블 참조
- Mode 1의 64 파티션 전수탐색은 블록당 연산량이 상당함. 2048x2048 텍스처 기준 수십~수백ms 소요 예상이나, 임포트 시점에만 실행되므로 런타임 성능 영향 없음
- GLSL에서 `uint[16]` 배열을 로컬 변수로 많이 사용하면 레지스터 압박이 생길 수 있음. 64 파티션 루프 안에서 임시 변수 사용을 최소화하도록 주의
- GpuTextureCompressor.cs는 변경 불필요. 셰이더를 파일 경로로 로드(`Path.Combine(shaderDir, "compress_bc7.comp")`)하므로 셰이더 교체만으로 적용됨
