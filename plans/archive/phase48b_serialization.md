# Phase 48b: 직렬화 (RoseCache + MaterialImporter)

## 목표
- RoseCache 바이너리 캐시에 `blendMode` 필드를 직렬화/역직렬화한다.
- MaterialImporter TOML 파일에 `blendMode` 필드를 읽기/쓰기한다.
- 기존 캐시는 버전 증가로 자동 무효화되어 마이그레이션 불필요.
- 기존 `.mat` 파일에 `blendMode` 키가 없으면 `Opaque`로 기본 동작.

## 선행 조건
- Phase 48a 완료 (`BlendMode` enum 및 `Material.blendMode` 프로퍼티 존재)

## 수정할 파일

### 1. `src/IronRose.Engine/AssetPipeline/RoseCache.cs`

**변경 1: FormatVersion 증가**

위치: 라인 18

현재 코드:
```csharp
        private const int FormatVersion = 8; // v8: normalMapStrength
```

변경 후:
```csharp
        private const int FormatVersion = 9; // v9: Material.blendMode
```

**변경 2: WriteMaterial()에 blendMode 쓰기 추가**

위치: 라인 593~594, `WriteMaterial` 메서드 본문 첫 줄

현재 코드:
```csharp
        private static void WriteMaterial(BinaryWriter writer, Material mat)
        {
            WriteColor(writer, mat.color);
```

변경 후:
```csharp
        private static void WriteMaterial(BinaryWriter writer, Material mat)
        {
            writer.Write((byte)mat.blendMode);
            WriteColor(writer, mat.color);
```

**변경 3: ReadMaterial()에 blendMode 읽기 추가**

위치: 라인 607~610, `ReadMaterial` 메서드 본문 첫 줄

현재 코드:
```csharp
        private static Material ReadMaterial(BinaryReader reader)
        {
            var mat = new Material();
            mat.color = ReadColor(reader);
```

변경 후:
```csharp
        private static Material ReadMaterial(BinaryReader reader)
        {
            var mat = new Material();
            mat.blendMode = (BlendMode)reader.ReadByte();
            mat.color = ReadColor(reader);
```

- `using RoseEngine;`는 이미 파일 상단에 존재하므로 `BlendMode` 접근 가능.

---

### 2. `src/IronRose.Engine/AssetPipeline/MaterialImporter.cs`

**변경 1: Import()에 blendMode 읽기 추가**

위치: 라인 43~44, `mat.normalMapStrength = ...` 다음 줄

현재 코드:
```csharp
            mat.normalMapStrength = config.GetFloat("normalMapStrength", mat.normalMapStrength);

            // Texture transform
```

변경 후:
```csharp
            mat.normalMapStrength = config.GetFloat("normalMapStrength", mat.normalMapStrength);

            // Blend mode
            var blendStr = config.GetString("blendMode", "Opaque");
            if (Enum.TryParse<BlendMode>(blendStr, true, out var bm))
                mat.blendMode = bm;

            // Texture transform
```

- `using RoseEngine;`는 이미 파일 상단에 존재하므로 `BlendMode` 접근 가능.
- `Enum.TryParse`에 `ignoreCase: true`를 전달하여 대소문자 무관하게 파싱.

**변경 2: BuildConfig()에 blendMode 파라미터 추가**

위치: 라인 89~92, `BuildConfig` 메서드 시그니처

현재 코드:
```csharp
        private static TomlConfig BuildConfig(Color color, Color emission,
            float metallic, float roughness, float occlusion, float normalMapStrength,
            RoseEngine.Vector2 textureScale, RoseEngine.Vector2 textureOffset,
            string? mainTexGuid, string? normalMapGuid, string? mroMapGuid)
```

변경 후:
```csharp
        private static TomlConfig BuildConfig(Color color, Color emission,
            float metallic, float roughness, float occlusion, float normalMapStrength,
            BlendMode blendMode,
            RoseEngine.Vector2 textureScale, RoseEngine.Vector2 textureOffset,
            string? mainTexGuid, string? normalMapGuid, string? mroMapGuid)
```

**변경 3: BuildConfig() 본문에 blendMode 쓰기 추가**

위치: 라인 113~114, `config.SetValue("normalMapStrength", ...)` 바로 다음

현재 코드:
```csharp
            config.SetValue("normalMapStrength", (double)normalMapStrength);

            if (textureScale.x != 1f || textureScale.y != 1f)
```

변경 후:
```csharp
            config.SetValue("normalMapStrength", (double)normalMapStrength);
            config.SetValue("blendMode", blendMode.ToString());

            if (textureScale.x != 1f || textureScale.y != 1f)
```

**변경 4: WriteDefault() 호출부 수정**

위치: 라인 73, `WriteDefault` 메서드 내 `BuildConfig` 호출

현재 코드:
```csharp
            var config = BuildConfig(Color.white, Color.black, 0f, 0.5f, 1f, 1f,
                RoseEngine.Vector2.one, RoseEngine.Vector2.zero, null, null, null);
```

변경 후:
```csharp
            var config = BuildConfig(Color.white, Color.black, 0f, 0.5f, 1f, 1f,
                BlendMode.Opaque,
                RoseEngine.Vector2.one, RoseEngine.Vector2.zero, null, null, null);
```

**변경 5: WriteMaterial() 호출부 수정**

위치: 라인 82~85, `WriteMaterial` 메서드 내 `BuildConfig` 호출

현재 코드:
```csharp
            var config = BuildConfig(mat.color, mat.emission,
                mat.metallic, mat.roughness, mat.occlusion, mat.normalMapStrength,
                mat.textureScale, mat.textureOffset,
                mainTexGuid, normalMapGuid, mroMapGuid);
```

변경 후:
```csharp
            var config = BuildConfig(mat.color, mat.emission,
                mat.metallic, mat.roughness, mat.occlusion, mat.normalMapStrength,
                mat.blendMode,
                mat.textureScale, mat.textureOffset,
                mainTexGuid, normalMapGuid, mroMapGuid);
```

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] 기존 `.mat` 파일(blendMode 키 없음)을 Import하면 `mat.blendMode == BlendMode.Opaque`
- [ ] `WriteMaterial` → `Import` 라운드트립 시 blendMode 값 보존
- [ ] RoseCache FormatVersion이 9로 변경됨

## 참고
- RoseCache 버전 8 -> 9 변경으로 기존 캐시는 자동 무효화됨. 첫 실행 시 재생성이 이루어져 약간의 로딩 시간 추가 가능.
- `BuildConfig`에 파라미터 추가 시, `blendMode`를 `normalMapStrength` 다음, `textureScale` 앞에 배치하여 논리적 그룹핑을 유지한다.
- Opaque가 기본값이지만 TOML에 항상 명시적으로 저장하여 가독성 확보.
