# Phase 5: 폴백 검증 및 통합 로깅

## 목표
- BCnEncoder.NET의 BC1 지원 여부를 코드로 확정.
- 미지원 또는 예외 발생 시 **BC3로 폴백**하고 명시적 경고 로그.
- 전체 매핑표(`texture_type × quality`)를 기반으로 수동 통합 검증을 가능케 하는 로그 형식 정비.
- 이 Phase 완료 시: 모든 환경(Compressonator 유/무, GPU 유/무)에서 예측 가능한 폴백 동작.

## 선행 조건
- Phase 1, 2, 3 완료. Phase 4는 독립이므로 선후 무관.
- `TextureCompressionFormatResolver`, 신규 `CompressTexture()` 시그니처가 존재해야 함.

## 수정할 파일

### `src/IronRose.Engine/AssetPipeline/RoseCache.cs`

#### 변경 1: BC1 CPU 인코더 검증
- **위치**: `CompressWithCpuFallback` (Phase 3에서 수정된 시그니처).
- **목적**: BCnEncoder.NET이 `CompressionFormat.Bc1WithAlpha`를 실제로 지원하는지 런타임 검증.
- **방법**:
  - `CompressWithCpuFallback` 내부 switch에서 BC1 케이스를 `try/catch`로 감싼다.
  - 첫 BC1 인코딩 시도 시 성공하면 static flag `_bc1CpuSupported = true`를 세팅.
  - `EncodeToRawBytes`에서 예외가 발생하면 `_bc1CpuSupported = false`로 세팅하고 BC3로 폴백.
  - 이후 호출은 flag만 확인하여 중복 예외 경로 회피.
- **구현**:
  ```csharp
  private static bool? _bc1CpuSupported; // null = 미확인, true = 가능, false = 불가능

  private static byte[][] CompressWithCpuFallback(byte[] rgbaData, int width, int height, string format, bool isSrgb)
  {
      if (format == "BC1" && _bc1CpuSupported == false)
      {
          // 이미 BC1 지원 불가 확인됨 → 바로 BC3 폴백
          Debug.LogWarning("[RoseCache] BC1 CPU encoder unavailable, using BC3 fallback");
          format = "BC3";
      }

      var bcFormat = format switch
      {
          "BC5" => CompressionFormat.Bc5,
          "BC3" => CompressionFormat.Bc3,
          "BC1" => CompressionFormat.Bc1WithAlpha,
          _     => CompressionFormat.Bc7,
      };
      var encoder = new BcEncoder(bcFormat);
      encoder.OutputOptions.Quality = CompressionQuality.Balanced;
      encoder.OutputOptions.GenerateMipMaps = false;
      encoder.Options.IsParallel = true;

      try
      {
          var result = encoder.EncodeToRawBytes(rgbaData, width, height, BcPixelFormat.Rgba32);
          if (format == "BC1") _bc1CpuSupported = true;
          return result;
      }
      catch (Exception ex) when (format == "BC1")
      {
          _bc1CpuSupported = false;
          Debug.LogWarning($"[RoseCache] BC1 CPU encoder failed ({ex.Message}), falling back to BC3 for subsequent calls");
          // 즉시 BC3로 재시도
          return CompressWithCpuFallback(rgbaData, width, height, "BC3", isSrgb);
      }
  }
  ```
- **주의**: BC1 → BC3 폴백 시, `WriteTexture`가 기록하는 Veldrid 포맷(`BC1_Rgba_UNorm(_SRgb)`)과 실제 바이트(BC3)가 **불일치**한다. 이 경우 런타임 텍스처 업로드에서 크래시 발생.
  - **해결**: BC1 폴백 발생 시 상위(`CompressTexture`)에 전달하여 Veldrid 포맷도 BC3로 변경해야 함. 이를 위해 `CompressWithCpuFallback`의 반환 타입을 `(byte[][] data, string actualFormat)`로 변경하거나, out 파라미터로 실제 포맷을 전달.
  - **권장 구현**: out 파라미터 `out string actualFormat`를 추가:
    ```csharp
    private static byte[][] CompressWithCpuFallback(byte[] rgbaData, int w, int h, string format, bool isSrgb, out string actualFormat)
    ```
    호출 측(`CompressTexture`)에서 `actualFormat`이 요청값과 다르면 `veldridFormat`을 재계산(`TextureCompressionFormatResolver`에 역매핑 헬퍼 추가 또는 간단한 switch로 BC3_UNorm(_SRgb) 직접 지정).

#### 변경 2: `CompressTexture` 호출 측 수정
- **위치**: `CompressTexture` 본문의 CPU 폴백 호출부.
- **변경**:
  - `CompressWithCpuFallback` 호출 시 `out var actualFormat` 추가.
  - `actualFormat != cliFormat`이면 `veldridFormat` 재계산:
    ```csharp
    if (actualFormat != cliFormat)
    {
        veldridFormat = actualFormat switch
        {
            "BC3" => isSrgb ? Veldrid.PixelFormat.BC3_UNorm_SRgb : Veldrid.PixelFormat.BC3_UNorm,
            "BC1" => isSrgb ? Veldrid.PixelFormat.BC1_Rgba_UNorm_SRgb : Veldrid.PixelFormat.BC1_Rgba_UNorm,
            "BC5" => Veldrid.PixelFormat.BC5_UNorm,
            "BC7" => isSrgb ? Veldrid.PixelFormat.BC7_UNorm_SRgb : Veldrid.PixelFormat.BC7_UNorm,
            _ => veldridFormat,
        };
        Debug.LogWarning($"[RoseCache] Format fallback: {cliFormat} → {actualFormat} (Veldrid format updated accordingly)");
    }
    ```
  - Mip 체인 압축 시에도 동일 처리.

#### 변경 3: 통합 로그 포맷 정비
- **위치**: `StoreTexture` 및 `CompressTexture` 내부 로그 메시지.
- **변경**:
  - `StoreTexture` 시작 로그: `"[RoseCache] Storing texture '{assetPath}' {w}x{h} type={textureType} quality={quality} srgb={isSrgb} → format={resolution.DisplayLabel}"`.
  - `CompressTexture` 종료 로그: `"[RoseCache] Compressed {assetPath or dims} via {source} ({cliFormat}, {cliQuality}): {mipCount} mips, {elapsed}ms"`.
  - 폴백 발생 시 한 줄로 요약: `"[RoseCache] Fallback path: CLI={cliAvail}, GPU={gpuUsed}, CPU={cpuUsed}, BC1→BC3={b1Fallback}"`.

#### 변경 4: (선택) Compressonator 미존재 환경 감지 로그 1회만
- 기존 코드(line 617–620)는 이미 1회 로그 처리. 유지.

## 엣지 케이스 / 기존 로직 상호작용
- **BC1 CPU 미지원 + GPU도 BC1 미지원 + CLI 없음**: 최종 BC3로 폴백. 디스크 크기는 예상(BC1, 4bpp)의 2배(BC3, 8bpp). 기능상 문제 없음.
- **Mip 체인 중간에 포맷 전환**: mip[0]가 BC1 성공, mip[1]이 BC3로 폴백되면 캐시 파일의 포맷 불일치 발생. **방지책**: 첫 BC1 실패 시 `_bc1CpuSupported = false` 세팅 후 **해당 텍스처 전체를 BC3로 재시작**하는 것이 안전. 구현 복잡도를 위해 현재는 mip[0] 결과를 기준으로 포맷 결정하고, 이후 mip은 동일 포맷으로 강제.
  - 더 단순한 방어: 첫 호출에서 BC1 불가 판정되면 static flag 세팅 후 즉시 BC3로 전체 진행.
- **CLI 성공 + 이후 mip에서 CLI 실패 → CPU BC1 시도 → CPU BC1 실패 → BC3**: 캐시 포맷 불일치. 현실적 빈도 낮지만, 방어 위해 mip 체인 전체를 CLI 또는 CPU로 통일하는 게 이상적. 현재 코드(Phase 3 기준)는 mip별 개별 시도. 이 Phase에서는 BC1에 한해서만 첫 실패 시 전체 포맷을 BC3로 고정하는 로직을 도입.
- **sRGB + BC1 폴백 → BC3**: Veldrid 포맷도 `BC3_UNorm_SRgb`로 전환. 이후 디코드 헬퍼(`DecodeBcToRgba`)가 `BC3_UNorm_SRgb`를 처리하는지 확인. 기존 코드(line 919–925)는 `BC3_UNorm`과 `BC3_UNorm_SRgb` 둘 다 처리. OK.

## 검증 기준
- [ ] `dotnet build` 성공.
- [ ] Compressonator CLI를 임시로 없앤 환경에서 `Color / Low` PNG 임포트 → CPU BC1 성공 시 `BC1_Rgba_UNorm` 캐시. 성공 로그 확인.
- [ ] BCnEncoder.NET이 BC1 예외를 던지는 가상 시나리오 (일단 코드 리뷰로만 검증; 실제 예외 재현 어려울 수 있음) → BC3 폴백 경고 + `BC3_UNorm_SRgb` 또는 `BC3_UNorm` 포맷으로 저장. 런타임 크래시 없음.
- [ ] 런타임에 `_bc1CpuSupported`가 false로 세팅된 후 동일 세션 내 추가 BC1 요청 → 예외 재발생 없이 즉시 BC3 경로로 진행.
- [ ] 모든 `texture_type × quality` 조합(4×4 = 16개 LDR + 2×4 HDR) 중 대표 조합 몇 개를 수동 임포트하여 로그 상 실제 압축 포맷과 `DisplayLabel`이 일치하는지 확인.

## 단위 테스트 대상
- 없음. 수동 환경 구성 기반 검증.

## 참고
- 관련 플랜 §5 "폴백 경로" 참조.
- BCnEncoder.NET 버전은 `IronRose.Engine.csproj` 참조 확인 필요. `CompressionFormat.Bc1` / `Bc1WithAlpha` enum이 존재하는 버전이어야 한다.
  - 미결: 현재 참조 중인 BCnEncoder.NET 버전에서 BC1 지원 여부를 Phase 5 구현자가 csproj/패키지 버전 확인 후 결정. 지원 시 위 구현 그대로, 미지원 시 `CompressWithCpuFallback` switch의 `"BC1"` 케이스를 **항상 `CompressionFormat.Bc3`로 매핑하고 경고 로그만 출력**(1회).
- GPU BC1 구현은 본 plan의 **범위 밖**(원 plan §5 명시).
