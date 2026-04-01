# Future: BC7 GPU 압축 품질 개선

## 개요

현재 BC7 GPU 컴퓨트 셰이더(`compress_bc7.comp`)가 Mode 6만 사용하며, 엔드포인트 선택이 단순 min/max 방식이라 압축 품질이 낮다. Mode 1 추가 및 엔드포인트 피팅 개선으로 품질을 대폭 향상시킨다.

## 현재 문제점

1. **Mode 6만 사용** — 1 서브셋, RGBA 7비트. 색상 분포가 복잡한 블록에서 밴딩 발생.
2. **단순 min/max 엔드포인트** — 채널별 min/max로 엔드포인트 결정. 아웃라이어 픽셀에 의해 엔드포인트가 낭비됨.
3. **P-bit 최적화 없음** — R 채널 LSB 하나만 사용. 4가지 조합 중 최적을 탐색해야 함.
4. **CPU 폴백 품질 낮음** — `CompressionQuality.Fast`, `IsParallel = false`.

## 구현 항목

### A. GPU 셰이더 개선 (`compress_bc7.comp`)

#### A-1. PCA 기반 엔드포인트 피팅
- 블록 내 16개 텍셀의 공분산 행렬 계산
- 주성분(principal axis) 추출 → 텍셀을 축에 투영
- 투영 min/max를 엔드포인트로 사용
- min/max 대비 아웃라이어에 강인하고, 실제 색상 분포에 맞는 보간 축 생성

#### A-2. P-bit 전수탐색
- Mode 6: p0∈{0,1} × p1∈{0,1} = 4조합 시도
- 각 조합마다 인덱스 재계산 → 총 에러 비교 → 최소 에러 선택

#### A-3. Mode 1 추가 (2 서브셋)
- BC7 스펙의 2-서브셋 파티션 테이블 64개를 `uint[64]`로 인코딩
- 각 파티션마다:
  - 서브셋 0/1별 PCA 엔드포인트 피팅
  - RGB 6비트 양자화 + shared p-bit (2조합)
  - 3비트 인덱스 (8레벨) 탐색
  - 앵커 인덱스 fix-up (서브셋 0: index[0], 서브셋 1: 파티션별 앵커 테이블)
- 64 파티션 중 최소 에러 파티션 선택
- Mode 6 결과와 비교 → 블록별 최적 모드 출력

#### A-4. Mode 선택 전략
- 블록별로 Mode 6 에러 vs Mode 1 최적 에러 비교
- 알파가 모두 255인 불투명 블록: Mode 1 우선 시도 (RGB 전용이므로 비트 효율 높음)
- 알파 변화가 있는 블록: Mode 6만 사용 (Mode 1은 알파 미지원)

### B. CPU 폴백 개선 (`RoseCache.cs`)

- `CompressionQuality.Fast` → `CompressionQuality.Balanced`
- `IsParallel = false` → `IsParallel = true`

### C. GpuTextureCompressor 수정

- 개선된 셰이더 로드 (기존 파이프라인 교체, API 변경 없음)

## 기대 효과

| 항목 | 개선 |
|------|------|
| 불투명 텍스처 (albedo, MRO) | Mode 1의 2 서브셋으로 그래디언트/경계 밴딩 대폭 감소 |
| 반투명 텍스처 | PCA + P-bit 최적화로 Mode 6 품질 향상 |
| CPU 폴백 | Balanced 품질 + 병렬 처리로 품질/속도 모두 개선 |

## 성능 영향

- 임포트 시점에만 실행되므로 런타임 성능 영향 없음
- GPU 셰이더: 블록당 작업량 증가 (64 파티션 탐색), 2048x2048 기준 수십~수백ms 예상
- CPU 폴백: Balanced는 Fast 대비 느리나, IsParallel로 상쇄

## 참고

- BC7 Mode 1: 2 subsets, 64 partitions, RGB 6.6.6, shared p-bit, 3-bit indices
- BC7 Mode 6: 1 subset, RGBA 7.7.7.7, unique p-bit per endpoint, 4-bit indices
- 파티션 테이블: Microsoft BC7 Format Mode Reference 참조
