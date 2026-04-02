# Phase 1: CPU 폴백 품질 개선

## 목표
- CPU 폴백 경로(GPU 미사용 시)의 BC7/BC5 압축 품질을 Fast에서 Balanced로 향상
- 병렬 처리를 활성화하여 속도 저하를 상쇄

## 선행 조건
- 없음 (첫 번째 phase)

## 수정할 파일

### `src/IronRose.Engine/AssetPipeline/RoseCache.cs`
- **변경 내용**: `CompressWithCpuFallback` 메서드 내 BCnEncoder 설정 2곳 변경
  - `encoder.OutputOptions.Quality = CompressionQuality.Fast;` -> `encoder.OutputOptions.Quality = CompressionQuality.Balanced;`
  - `encoder.Options.IsParallel = false;` -> `encoder.Options.IsParallel = true;`
- **이유**: 설계 문서 섹션 B에 명시된 CPU 폴백 품질 개선. Balanced는 Fast 대비 압축 품질이 크게 향상되며, IsParallel=true로 멀티스레드 처리하여 속도 저하를 상쇄한다.
- **위치**: 517~519번째 줄 부근, `CompressWithCpuFallback` 메서드 내부
- **현재 코드**:
  ```csharp
  encoder.OutputOptions.Quality = CompressionQuality.Fast;
  encoder.OutputOptions.GenerateMipMaps = false;
  encoder.Options.IsParallel = false;
  ```
- **변경 후 코드**:
  ```csharp
  encoder.OutputOptions.Quality = CompressionQuality.Balanced;
  encoder.OutputOptions.GenerateMipMaps = false;
  encoder.Options.IsParallel = true;
  ```

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `CompressionQuality.Balanced`로 변경됨을 코드에서 확인
- [ ] `IsParallel = true`로 변경됨을 코드에서 확인

## 참고
- BCnEncoder.NET의 `CompressionQuality` enum: Fast, Balanced, BestQuality
- Balanced는 Fast 대비 블록당 더 많은 모드/파티션을 시도하여 품질 향상
- IsParallel=true는 .NET의 Parallel.For를 내부적으로 사용하여 블록 단위 병렬 처리
- 이 변경은 GPU 컴퓨트 셰이더를 사용할 수 없는 환경(Vulkan 미지원 등)에서의 폴백 경로에만 영향
