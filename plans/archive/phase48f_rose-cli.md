# Phase 48f: rose-cli 연동

## 목표
- `material.info` 응답에 `blendMode` 필드를 추가한다.
- `material.set_blend_mode` CLI 명령 핸들러를 추가한다.
- `material.create`에 blendMode 옵션 파라미터를 추가한다.

## 선행 조건
- Phase 48a 완료 (`BlendMode` enum 존재)
- Phase 48b 완료 (직렬화 -- `MaterialImporter.WriteMaterial`이 blendMode를 저장)

## 수정할 파일

### `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`

**변경 1: material.info 응답에 blendMode 추가**

위치: 라인 1121~1133, `material.info` 핸들러의 `JsonOk` 반환 부분

현재 코드:
```csharp
                    return JsonOk(new
                    {
                        name = mat.name,
                        color = FormatColor(mat.color),
                        metallic = mat.metallic,
                        roughness = mat.roughness,
                        occlusion = mat.occlusion,
                        emission = FormatColor(mat.emission),
                        hasMainTexture = mat.mainTexture != null,
                        hasNormalMap = mat.normalMap != null,
                        textureScale = $"{mat.textureScale.x.ToString(CultureInfo.InvariantCulture)}, {mat.textureScale.y.ToString(CultureInfo.InvariantCulture)}",
                        textureOffset = $"{mat.textureOffset.x.ToString(CultureInfo.InvariantCulture)}, {mat.textureOffset.y.ToString(CultureInfo.InvariantCulture)}"
                    });
```

변경 후:
```csharp
                    return JsonOk(new
                    {
                        name = mat.name,
                        blendMode = mat.blendMode.ToString(),
                        color = FormatColor(mat.color),
                        metallic = mat.metallic,
                        roughness = mat.roughness,
                        occlusion = mat.occlusion,
                        emission = FormatColor(mat.emission),
                        hasMainTexture = mat.mainTexture != null,
                        hasNormalMap = mat.normalMap != null,
                        textureScale = $"{mat.textureScale.x.ToString(CultureInfo.InvariantCulture)}, {mat.textureScale.y.ToString(CultureInfo.InvariantCulture)}",
                        textureOffset = $"{mat.textureOffset.x.ToString(CultureInfo.InvariantCulture)}, {mat.textureOffset.y.ToString(CultureInfo.InvariantCulture)}"
                    });
```

---

**변경 2: material.set_blend_mode 핸들러 추가**

위치: 라인 1232, `material.set_roughness` 핸들러 종료(`};`) 직후, `material.create` 핸들러 주석 전

현재 코드:
```csharp
                });
            };

            // ----------------------------------------------------------------
            // material.create -- 새 머티리얼 파일 생성 (메인 스레드)
            // ----------------------------------------------------------------
```

변경 후:
```csharp
                });
            };

            // ----------------------------------------------------------------
            // material.set_blend_mode -- 블렌드 모드 변경 (메인 스레드)
            // ----------------------------------------------------------------
            _handlers["material.set_blend_mode"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: material.set_blend_mode <goId> <Opaque|AlphaBlend|Additive>");

                if (!int.TryParse(args[0], out var id))
                    return JsonError($"Invalid GameObject ID: {args[0]}");

                if (!Enum.TryParse<BlendMode>(args[1], true, out var mode))
                    return JsonError($"Invalid blend mode: {args[1]}. Use: Opaque, AlphaBlend, Additive");

                return ExecuteOnMainThread(() =>
                {
                    var go = FindGameObjectById(id);
                    if (go == null)
                        return JsonError($"GameObject not found: {id}");

                    var renderer = go.GetComponent<MeshRenderer>();
                    if (renderer?.material == null)
                        return JsonError($"No material on GameObject: {id}");

                    renderer.material.blendMode = mode;
                    SaveMaterialToDisk(renderer.material);
                    SceneManager.GetActiveScene().isDirty = true;
                    return JsonOk(new { blendMode = mode.ToString() });
                });
            };

            // ----------------------------------------------------------------
            // material.create -- 새 머티리얼 파일 생성 (메인 스레드)
            // ----------------------------------------------------------------
```

패턴 참고: `material.set_metallic`, `material.set_roughness` 핸들러와 동일한 구조:
1. `args` 파라미터 검증
2. `int.TryParse`로 GO ID 파싱
3. 값 파싱 (여기서는 `Enum.TryParse<BlendMode>`)
4. `ExecuteOnMainThread`에서 GO 찾기 → renderer/material 접근 → 값 설정 → 저장

- `using RoseEngine;`가 파일에 이미 있는지 확인 필요. `BlendMode`는 `RoseEngine` 네임스페이스에 있음.
- `SaveMaterialToDisk`는 기존 private 메서드 (라인 2560) -- Material의 GUID로 경로를 찾아 TOML로 저장.

---

**변경 3: material.create에 blendMode 옵션 파라미터 추가**

위치: 라인 1237~1267, `material.create` 핸들러

현재 코드:
```csharp
            _handlers["material.create"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: material.create <name> <dirPath> [r,g,b,a]");

                var matName = args[0];
                var dirPath = ResolveProjectPath(args[1]);

                return ExecuteOnMainThread(() =>
                {
                    var filePath = Path.Combine(dirPath, matName + ".mat");

                    // 색상이 지정되면 해당 색으로, 아니면 기본 흰색으로 생성
                    if (args.Length >= 3)
                    {
                        var mat = new Material { name = matName, color = ParseColor(args[2]) };
                        MaterialImporter.WriteMaterial(filePath, mat);
                    }
                    else
                    {
                        MaterialImporter.WriteDefault(filePath);
                    }
```

변경 후:
```csharp
            _handlers["material.create"] = args =>
            {
                if (args.Length < 2)
                    return JsonError("Usage: material.create <name> <dirPath> [r,g,b,a] [blendMode]");

                var matName = args[0];
                var dirPath = ResolveProjectPath(args[1]);

                return ExecuteOnMainThread(() =>
                {
                    var filePath = Path.Combine(dirPath, matName + ".mat");

                    // blendMode 파싱 (4번째 인자, 옵션)
                    var blendMode = BlendMode.Opaque;
                    if (args.Length >= 4 && Enum.TryParse<BlendMode>(args[3], true, out var bm))
                        blendMode = bm;

                    // 색상이 지정되면 해당 색으로, 아니면 기본 흰색으로 생성
                    if (args.Length >= 3)
                    {
                        var mat = new Material { name = matName, color = ParseColor(args[2]), blendMode = blendMode };
                        MaterialImporter.WriteMaterial(filePath, mat);
                    }
                    else
                    {
                        var mat = new Material { name = matName, blendMode = blendMode };
                        MaterialImporter.WriteMaterial(filePath, mat);
                    }
```

변경 사항:
- Usage 문자열에 `[blendMode]` 추가
- `args[3]`에서 blendMode 파싱 (옵션, 없으면 Opaque)
- 색상 없이 blendMode만 지정하는 경우에도 대응 -- `args.Length >= 4`이면 3번째(color) + 4번째(blendMode) 모두 있는 경우
- 기본 경로(`WriteDefault` 호출)도 blendMode가 지정된 경우 `WriteMaterial`로 변경하여 blendMode를 저장

**참고**: 기존 `WriteDefault` 호출 경로에서는 blendMode가 Opaque 기본값이 아닌 경우에도 `WriteMaterial`을 사용하도록 변경. blendMode가 Opaque이고 color도 미지정이면 기존과 동일하게 `WriteDefault` 사용해도 되지만, 코드 일관성을 위해 통일.

---

**변경 4: 파일 상단 주석 업데이트**

위치: 라인 34, 파일 상단 핸들러 목록 주석

현재 코드:
```csharp
//          material.info, material.set_color, material.set_metallic, material.set_roughness,
```

변경 후:
```csharp
//          material.info, material.set_color, material.set_metallic, material.set_roughness, material.set_blend_mode,
```

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `material.info <goId>` 응답에 `blendMode` 필드가 포함됨 (예: `"blendMode": "Opaque"`)
- [ ] `material.set_blend_mode <goId> AlphaBlend` 실행 시 Material의 blendMode가 변경되고 `.mat` 파일에 저장됨
- [ ] `material.set_blend_mode <goId> invalid` 실행 시 에러 메시지 반환
- [ ] `material.create <name> <dir> 1,1,1,1 Additive` 실행 시 blendMode가 Additive인 `.mat` 파일 생성

## 참고
- `Enum.TryParse<BlendMode>(args[1], true, out var mode)` -- `ignoreCase: true`로 "alphablend", "ADDITIVE" 등 대소문자 무관하게 파싱.
- `SaveMaterialToDisk`는 Material 인스턴스에서 GUID를 통해 `.mat` 파일 경로를 찾아 `MaterialImporter.WriteMaterial`을 호출하는 기존 헬퍼.
- `SceneManager.GetActiveScene().isDirty = true;` -- 씬 변경 플래그 설정 (기존 패턴).
- `using RoseEngine;`가 파일에 존재하는지 확인. `BlendMode`는 `RoseEngine` 네임스페이스.
- CLI에서 `BlendMode` enum 파싱 실패 시 명확한 에러 메시지("Use: Opaque, AlphaBlend, Additive")를 반환.
