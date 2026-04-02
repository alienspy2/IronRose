# Phase 2: PCA 기반 엔드포인트 피팅 및 P-bit 전수탐색

## 목표
- Mode 6의 엔드포인트 결정 방식을 단순 min/max에서 PCA 기반으로 개선
- P-bit 4조합 전수탐색으로 최적 양자화 달성
- Mode 6 전용 개선이므로 기존 셰이더의 동작 구조(입출력, 바인딩)는 변경 없음

## 선행 조건
- Phase 1 완료 (필수는 아님, 독립적으로 적용 가능하나 순서상 Phase 1 이후)

## 수정할 파일

### `Shaders/compress_bc7.comp`
- **변경 내용**: `main()` 함수 내부의 엔드포인트 계산 로직과 인덱스 계산 로직을 전면 교체
- **이유**: 설계 문서 섹션 A-1(PCA), A-2(P-bit 전수탐색)
- **기존 유지 사항**: 
  - 셰이더 헤더(#version, layout, binding 선언) 그대로 유지
  - `loadPixel()`, `writeBits()` 헬퍼 함수 그대로 유지
  - WEIGHTS 배열 그대로 유지
  - 최종 비트 패킹(Mode 6 비트 레이아웃) 그대로 유지
  - 입출력 버퍼 구조 변경 없음

#### 상세 구현 가이드

**1. PCA 기반 엔드포인트 피팅 (A-1)**

텍셀 로드 루프에서 min/max 계산을 제거하고, 대신 다음을 수행:

```glsl
// 1) 16개 텍셀의 평균(mean) 계산
vec4 mean = vec4(0.0);
for (uint t = 0u; t < 16u; t++) {
    mean += vec4(texels[t]);
}
mean /= 16.0;

// 2) 공분산 행렬 계산 (4x4, RGBA)
// 대칭 행렬이므로 상삼각 10개 원소만 필요
// cov[i][j] = sum((texel[k][i] - mean[i]) * (texel[k][j] - mean[j])) / 16
float cov[10]; // 00,01,02,03, 11,12,13, 22,23, 33 순서
// 초기화 0으로 한 뒤 루프 돌며 누적

// 3) Power iteration으로 주성분 축 추출
// 초기 벡터: (1,1,1,1) 정규화
// 4~8회 반복: axis = cov * axis; axis = normalize(axis);
// GPU에서 4x4 행렬-벡터 곱은 인라인으로 직접 계산

// 4) 텍셀을 주성분 축에 투영하여 min/max 값 얻기
float projMin = 1e30, projMax = -1e30;
uint minIdx = 0u, maxIdx = 0u;
for (uint t = 0u; t < 16u; t++) {
    float proj = dot(vec4(texels[t]) - mean, axis);
    if (proj < projMin) { projMin = proj; minIdx = t; }
    if (proj > projMax) { projMax = proj; maxIdx = t; }
}
// minCol = texels[minIdx], maxCol = texels[maxIdx]
```

- Power iteration 반복 횟수: 8회 (GPU에서 충분히 빠르고 수렴 보장)
- `normalize()` 시 길이가 0에 가까우면(모든 텍셀 동일) 기존 min/max 폴백 사용

**2. P-bit 전수탐색 (A-2)**

기존 코드의 단순 p-bit 결정(`pb0 = minCol.r & 1u`) 대신:

```glsl
// 4조합: (pb0=0,pb1=0), (0,1), (1,0), (1,1)
uint bestPb0 = 0u, bestPb1 = 0u;
uint bestTotalError = 0xFFFFFFFFu;
uint bestIndices[16];

for (uint pbitCombo = 0u; pbitCombo < 4u; pbitCombo++) {
    uint p0 = pbitCombo & 1u;
    uint p1 = (pbitCombo >> 1u) & 1u;
    
    // 7비트 양자화: e_7 = (color >> 1) & 0x7F
    // 풀 8비트 복원: val = (e_7 << 1) | pbit; val = val | (val >> 7)
    uvec4 ep0q, ep1q; // 양자화된 7비트 엔드포인트
    uvec4 ep0f, ep1f; // 복원된 8비트 엔드포인트
    
    for (uint ch = 0u; ch < 4u; ch++) {
        ep0q[ch] = (minCol[ch] >> 1u) & 0x7Fu;
        ep1q[ch] = (maxCol[ch] >> 1u) & 0x7Fu;
        uint v0 = (ep0q[ch] << 1u) | p0;
        uint v1 = (ep1q[ch] << 1u) | p1;
        ep0f[ch] = v0 | (v0 >> 7u);
        ep1f[ch] = v1 | (v1 >> 7u);
    }
    
    // 모든 텍셀에 대해 인덱스 탐색 + 에러 합산
    uint totalError = 0u;
    uint tempIndices[16];
    for (uint t = 0u; t < 16u; t++) {
        // 기존 인덱스 탐색 코드와 동일
        // bestIdx, bestDist 찾기
        tempIndices[t] = bestIdx;
        totalError += bestDist;
    }
    
    if (totalError < bestTotalError) {
        bestTotalError = totalError;
        bestPb0 = p0;
        bestPb1 = p1;
        bestIndices = tempIndices; // 배열 복사
    }
}
```

- 인덱스 탐색 로직은 기존과 동일 (WEIGHTS 배열 사용, 4채널 SSD 계산)
- 최적 p-bit 조합의 인덱스를 사용하여 앵커 fix-up 및 비트 패킹 진행
- 앵커 fix-up 로직(index[0] >= 8이면 엔드포인트 스왑 + 인덱스 반전)은 기존과 동일하게 유지

**3. 전체 main() 흐름 정리**

1. 텍셀 16개 로드 (기존과 동일)
2. PCA로 주성분 축 계산
3. 축에 투영하여 min/max 텍셀 선택 -> minCol, maxCol
4. P-bit 4조합 전수탐색 -> bestPb0, bestPb1, bestIndices
5. 앵커 fix-up (기존과 동일)
6. 비트 패킹 (기존과 동일, bestPb0/bestPb1 사용)

## 검증 기준
- [ ] `dotnet build` 성공 (셰이더 파일은 빌드 대상이 아니므로 C# 빌드에 영향 없음)
- [ ] 셰이더 파일이 유효한 GLSL 450 코드인지 확인 (문법 오류 없음)
- [ ] 기존 바인딩(set=0, binding 0/1/2)과 워크그룹 크기(8x8x1) 유지
- [ ] 에디터에서 텍스처 임포트 시 GPU 압축이 정상 동작하는지 확인

## 참고
- GLSL에는 동적 배열 복사가 없으므로 `bestIndices` 갱신은 for 루프로 원소별 복사 필요
- Power iteration에서 공분산 행렬이 영행렬(모든 텍셀 동일색)인 경우: 축이 0벡터가 되므로 이때는 어차피 min=max이고 인덱스가 모두 0이 됨. normalize 전에 length 체크하여 0이면 임의 축(1,0,0,0) 사용
- PCA는 RGBA 4채널 모두에 대해 수행. 알파 변화가 큰 반투명 텍스처에서도 올바른 축 선택됨
- 이 phase에서는 Mode 1은 추가하지 않음. Mode 6의 품질만 개선
