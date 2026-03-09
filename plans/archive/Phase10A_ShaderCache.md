# Phase 10A: ShaderCache — GLSL→SPIR-V 컴파일 결과 디스크 캐시

## Context

IronRose 엔진은 매 시작 시 14개 이상의 GLSL 셰이더를 텍스트에서 SPIR-V로 런타임 컴파일합니다.
텍스처/메시는 `RoseCache`로 디스크 캐싱하지만 셰이더는 캐싱하지 않아 매번 컴파일 비용이 발생합니다.

GLSL → SPIR-V 컴파일 결과를 `RoseCache/shaders/`에 캐싱하여:
- 두 번째 실행부터 GLSL→SPIR-V 컴파일 스킵 (시작 시간 단축)
- 셰이더 소스 변경 시 자동 무효화 및 재컴파일

## 변경 파일

| 파일 | 작업 |
|------|------|
| `src/IronRose.Rendering/ShaderCompiler.cs` | **수정** — SPIR-V 캐시 로직 추가 |
| `src/IronRose.Engine/EngineCore.cs` | **수정** — 셰이더 캐시 디렉토리 설정 |

---

## 1. ShaderCompiler.cs 수정

### 정적 필드 추가

```csharp
private static string? _cacheDir;

public static void SetCacheDirectory(string path)
{
    _cacheDir = path;
    Directory.CreateDirectory(path);
}

public static void ClearCache()
{
    if (_cacheDir != null && Directory.Exists(_cacheDir))
    {
        Directory.Delete(_cacheDir, recursive: true);
        Directory.CreateDirectory(_cacheDir);
    }
}
```

### SPIR-V 캐시 파이프라인

```
[첫 실행 - 캐시 미스]
GLSL 소스 텍스트 읽기
  → SHA256 해시 계산
  → SpirvCompilation.CompileGlslToSpirv() → SPIR-V 바이트
  → 캐시 파일 저장 (RoseCache/shaders/<파일명>.spvcache)
  → SPIR-V 바이트를 ShaderDescription에 전달
  → CreateFromSpirv() (Veldrid가 SPIR-V 매직넘버 감지 → GLSL 컴파일 스킵)
  → Shader 생성

[이후 실행 - 캐시 히트]
GLSL 소스 텍스트 읽기
  → SHA256 해시 계산
  → 캐시 파일 로드 → 해시 비교 → 일치
  → 캐시된 SPIR-V 바이트를 ShaderDescription에 전달
  → CreateFromSpirv() (SPIR-V 매직넘버 감지 → GLSL 컴파일 스킵)
  → Shader 생성
```

### 캐시 파일 포맷 (.spvcache)

```
[32B]  SHA256 해시 (GLSL 소스 내용)
[4B]   SPIR-V 데이터 길이
[N×B]  SPIR-V 바이너리
```

### CompileGLSL() 수정

```csharp
public static Shader[] CompileGLSL(GraphicsDevice device, string vertexPath, string fragmentPath)
{
    // 각 셰이더에 대해 캐시된 SPIR-V 시도
    var vertexBytes = GetCachedOrCompileSpirv(vertexPath, ShaderStages.Vertex);
    var fragmentBytes = GetCachedOrCompileSpirv(fragmentPath, ShaderStages.Fragment);

    var vertexDesc = new ShaderDescription(ShaderStages.Vertex, vertexBytes, "main");
    var fragmentDesc = new ShaderDescription(ShaderStages.Fragment, fragmentBytes, "main");

    return device.ResourceFactory.CreateFromSpirv(vertexDesc, fragmentDesc);
}
```

### CompileComputeGLSL() 수정

동일한 패턴으로 컴퓨트 셰이더도 캐시.

### GetCachedOrCompileSpirv() 핵심 메서드

```csharp
private static byte[] GetCachedOrCompileSpirv(string glslPath, ShaderStages stage)
{
    string sourceText = File.ReadAllText(glslPath);

    if (_cacheDir == null)
        return Encoding.UTF8.GetBytes(sourceText); // 캐시 비활성 → 기존 동작

    byte[] sourceHash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceText));
    string cachePath = GetShaderCachePath(glslPath);

    // 캐시 히트 체크
    if (TryLoadCachedSpirv(cachePath, sourceHash, out var spirvBytes))
    {
        return spirvBytes;
    }

    // 캐시 미스: GLSL → SPIR-V 컴파일
    var result = SpirvCompilation.CompileGlslToSpirv(
        sourceText, Path.GetFileName(glslPath), stage,
        new GlslCompileOptions(false));  // debug=false

    SaveSpirvCache(cachePath, sourceHash, result.SpirvBytes);
    Console.WriteLine($"[ShaderCompiler] Compiled & cached: {glslPath}");
    return result.SpirvBytes;
}
```

### 폴백 처리

- `SpirvCompilation.CompileGlslToSpirv()` 실패 시 → GLSL 텍스트 바이트 반환 (기존 동작)
- 캐시 파일 읽기 실패 시 → 재컴파일
- 캐시 실패가 엔진 동작을 절대 방해하지 않음

### 영향받는 호출 지점 (수정 불필요 - API 그대로 유지)

| 호출 위치 | 셰이더 수 |
|----------|---------|
| `RenderSystem.Initialize()` | 9개 쌍 + 디버그 오버레이 |
| `GpuTextureCompressor.Initialize()` | 2개 컴퓨트 |
| `BloomEffect.OnInitialize()` | 3개 쌍 |
| `TonemapEffect.OnInitialize()` | 1개 쌍 |
| **합계** | **~28개 SPIR-V 캐시 파일** |

---

## 2. EngineCore.cs 수정

`Initialize()`에서 RenderSystem 초기화 **전**에 추가:

```csharp
// 셰이더 캐시 설정
if (!RoseConfig.DontUseCache)
{
    var shaderCacheDir = Path.Combine(Directory.GetCurrentDirectory(), "RoseCache", "shaders");
    ShaderCompiler.SetCacheDirectory(shaderCacheDir);

    if (RoseConfig.ForceClearCache)
        ShaderCompiler.ClearCache();
}
```

위치: `_graphicsManager.Initialize(_window)` 직후, `_renderSystem.Initialize()` 직전 (line 87-93 사이).

---

## 검증 방법

1. `dotnet build` — 컴파일 성공
2. 첫 실행 → `[ShaderCompiler] Compiled & cached: ...` 로그, `RoseCache/shaders/` 에 `.spvcache` 파일 생성
3. 두 번째 실행 → `[ShaderCompiler] Cache hit: ...` 로그
4. 셰이더 파일 수정 후 재실행 → 캐시 미스, 재컴파일 및 캐시 갱신
5. `rose_config.toml`에서 `force_clear_cache = true` → 셰이더 캐시도 삭제 확인
6. `rose_config.toml`에서 `dont_use_cache = true` → 캐시 비활성, 기존 동작 유지
