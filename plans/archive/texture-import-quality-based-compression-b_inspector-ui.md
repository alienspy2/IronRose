# Phase 2: Inspector UI 재작성

## 목표
- Inspector의 Texture Importer 섹션에서 **`compression` 드롭다운 제거**.
- `texture_type` 드롭다운에 `ColorWithAlpha` 추가.
- `quality` 드롭다운에 `NoCompression` 추가.
- `quality` → `compression` 자동 덮어쓰기 로직(line 2672–2700) 전부 제거.
- Resolver의 결과를 read-only 라벨로 **프리뷰 표시** (예: `"Format: BC7 (8 bpp)"`).
- 이 Phase 완료 시: UI가 새 스키마로 작동한다. 파이프라인(Phase 3)이 아직 구 compression 키 기반이지만, 메타에서 compression이 사라진 상태에서도 기존 `RoseCache.cs`의 fallback(`GetMetaString(..., "compression", "BC7")`)이 동작하므로 빌드/런 모두 문제 없다.

## 선행 조건
- Phase 1 (스키마/Resolver) 완료. `TextureCompressionFormatResolver`, `TextureMetadataMigration` 존재.
- `RoseMetadata.InferImporter`에서 `compression` 키가 제거되어 있어야 한다 (Phase 1에서 수행).

## 수정할 파일

### `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs`

**대상 메서드**: `DrawTextureImporterSettings()` (line 2600~2751 범위).

#### 변경 A: `isHdr` 분기 (line 2612–2616)
- **현재**: `DrawImporterCombo("compression", { "BC6H", "none" }, "BC6H")` + `DrawImporterCombo("texture_type", { "HDR", "Panoramic" }, "HDR")`.
- **변경 후**:
  - `DrawImporterCombo("compression", ...)` **라인 삭제**.
  - `DrawImporterCombo("texture_type", { "HDR", "Panoramic" }, "HDR")` 유지.
  - 이후 `quality` 드롭다운 노출: `DrawImporterCombo("quality", AllQualities, "High")`.
  - Resolver 프리뷰 라벨 표시 (아래 "프리뷰 라벨" 참조).

#### 변경 B: `isPanoramic` 분기 (line 2617–2639)
- **변경 후**:
  - `DrawImporterCombo("compression", ...)` **라인 삭제** (line 2619).
  - `DrawImporterCombo("texture_type", { "Color", "ColorWithAlpha", "NormalMap", "Sprite", "Panoramic" }, "Color")` — `ColorWithAlpha` 추가.
  - Panoramic 전환 시 `compression`/`srgb` 자동 갱신 블록(line 2623–2636)에서 **compression 관련 줄 전부 제거**. `srgb = false` 강제는 유지.
  - `DrawImporterInt("face_size", 512, 128, 4096)` 유지.
  - `quality` 드롭다운 추가.
  - 프리뷰 라벨 표시.

#### 변경 C: LDR 일반 분기 (else 블록, line 2640–2701)
- **현재**: NormalMap이면 BC5 disabled 콤보, 아니면 `{ BC7, BC5, BC3, none }` 콤보. 그 뒤 `texture_type`, `quality` 콤보. quality→compression 덮어쓰기 로직.
- **변경 후**:
  - `compression` 드롭다운 **완전 제거**(line 2644–2653의 `isNormalMap` 분기 전체).
  - `DrawImporterCombo("texture_type", { "Color", "ColorWithAlpha", "NormalMap", "Sprite", "Panoramic" }, "Color")` — `ColorWithAlpha` 추가.
  - NormalMap 전환 시 `compression`/`srgb` 자동 갱신 블록(line 2657–2670)에서 **compression 관련 줄 삭제**. `srgb = false` 강제는 유지.
  - `quality` 드롭다운: `DrawImporterCombo("quality", { "High", "Medium", "Low", "NoCompression" }, "High")`.
    - NormalMap일 때도 노출하되, 의미상 High/Med/Low는 모두 BC5로 동일 결과(비활성화 처리 불필요, 프리뷰 라벨이 설명).
  - `quality` → `compression` 자동 덮어쓰기 로직(line 2674–2700) **전체 삭제**.
  - 프리뷰 라벨 표시.

#### 변경 D: 프리뷰 라벨 (공통 UI)
모든 분기에서 `quality` 드롭다운 **직후** 아래 블록을 삽입:
```csharp
if (_editedImporter != null)
{
    var tt = _editedImporter.TryGetValue("texture_type", out var ttv2) ? ttv2?.ToString() ?? "Color" : "Color";
    var q  = _editedImporter.TryGetValue("quality", out var qv2) ? qv2?.ToString() ?? "High" : "High";
    var sr = _editedImporter.TryGetValue("srgb", out var sv2) && sv2 is true;
    var resolution = TextureCompressionFormatResolver.Resolve(tt, q, sr);
    ImGui.TextDisabled($"Format: {resolution.DisplayLabel}");
}
```
- `ImGui.TextDisabled`는 회색 텍스트로 read-only임을 시각적으로 표현.
- 사용자가 texture_type/quality/srgb를 바꾸면 다음 프레임에 자동 반영됨.
- 공통화 위해 `private void DrawCompressionFormatPreview()` 로 추출하는 것을 권장.

#### 변경 E: Sprite 강제 처리 블록 (line 2709–2733)
- 기존 로직 유지. Sprite는 `srgb=true`, `generate_mipmaps=false`, `wrap_mode=Clamp` 강제.
- `compression` 관련 코드는 없으므로 변경 불필요.

### `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs` — using 추가
- 파일 상단에 `using IronRose.AssetPipeline;` 이미 있다면 생략, 없으면 추가하여 `TextureCompressionFormatResolver` 참조.

## 추가/삭제할 시그니처
- 신규 private 메서드(선택): `private void DrawCompressionFormatPreview()` — `_editedImporter`를 참조하여 Resolver 결과 라벨을 그린다. 반환 void.
- 삭제: 없음(기존 메서드는 유지하고 내부 로직만 교체).

## 엣지 케이스 / 기존 로직 상호작용
- **기존 `.rose`에 `compression = "BC7"`이 남아 있는 상태에서 에디터를 띄우는 경우**: Phase 1의 마이그레이션이 로드 시점에 `compression`을 제거하고 저장한다. 따라서 UI가 띄워질 때는 이미 정리된 상태.
- **`quality`가 `NoCompression`이면 Compressonator CLI 경로를 타지 않음**: Phase 3에서 반영. Phase 2 시점에는 UI에서 선택 가능하지만 실제로는 파이프라인(RoseCache.StoreTexture)이 `compression` 키 없음 → 기본값 "BC7" → BC7 압축으로 처리됨. **이는 Phase 2와 Phase 3를 반드시 연속 머지해야 함을 의미**. 단독 머지 시 사용자 혼란 가능.
  - 완화책: Phase 2 프리뷰 라벨이 "R8G8B8A8 (Uncompressed)"로 표시되지만 실제 결과는 BC7. 이 불일치를 알리는 TODO 주석을 Phase 2 코드에 남기고 Phase 3에서 제거.
- **Sprite + NoCompression**: Resolver는 `Color`/`Sprite` 모두 NoCompression 시 R8G8B8A8를 반환. UI 상 허용.
- **NormalMap + NoCompression**: R8G8B8A8로 fallback. 노멀맵의 경우 디스크 크기 크지만 허용.
- **HDR + NoCompression**: RGBA16F. 이 경우도 UI 허용.
- 기존 `isHdr`/`isPanoramic`/`isSprite` 분기 구조는 유지. `isColorWithAlpha`는 별도 분기 불필요(LDR 일반 분기와 동일 처리).

## 검증 기준
- [ ] `dotnet build`가 성공한다.
- [ ] 에디터 실행 후 PNG 파일 선택 → Inspector에 compression 드롭다운이 **없다**.
- [ ] texture_type 드롭다운에 `ColorWithAlpha` 항목이 보인다.
- [ ] quality 드롭다운에 `NoCompression` 항목이 보인다.
- [ ] texture_type을 `Color`로 두고 quality를 `High → Medium → Low → NoCompression`으로 바꾸면 프리뷰 라벨이 각각 `BC7 (8 bpp)`, `BC7 (8 bpp)`, `BC1 (4 bpp)`, `R8G8B8A8 (32 bpp, Uncompressed)`로 변한다.
- [ ] texture_type을 `ColorWithAlpha`로 바꾸고 quality=Low → 라벨이 `BC3 (8 bpp)`.
- [ ] texture_type을 `NormalMap`으로 바꾸면 라벨이 `BC5 (8 bpp)` (quality 무관).
- [ ] HDR(.hdr) 파일 선택 시 compression 드롭다운 없음, quality 선택 가능, 라벨은 BC6H 또는 RGBA16F.
- [ ] `.rose` 저장 후 파일에 `compression` 키가 없다.

## 단위 테스트 대상
- 없음. UI 수동 검증.

## 참고
- 관련 플랜 §3 "UI 변경" 참조.
- Phase 2와 Phase 3은 **함께 머지**하는 것을 권장. 단독 머지 시 프리뷰 라벨과 실제 결과가 불일치하는 기간 발생.
- `DrawImporterCombo` 시그니처는 기존 코드와 동일하게 사용: `(string key, string[] options, string defaultValue)`.
