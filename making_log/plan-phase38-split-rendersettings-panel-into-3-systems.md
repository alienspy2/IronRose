# Phase 38: Render Settings 분리 — 구현 계획

> 모놀리식 `ImGuiRenderSettingsPanel.cs` (406줄, 6개 섹션)을 3개 전문 시스템으로 분해

---

## 현재 상태 분석

### ImGuiRenderSettingsPanel.cs (삭제 예정)
**경로**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiRenderSettingsPanel.cs`

| 섹션 | 메서드 | 대상 프로퍼티 | 이동 목표 |
|------|--------|-------------|----------|
| Skybox | `DrawSkyboxSection()` (36-117), `ApplySkyboxTexture()` (122-182) | skybox, skyboxTextureGuid, skyboxExposure, skyboxRotation | **38-B** Scene Environment |
| Ambient | `DrawAmbientSection()` (184-195) | ambientIntensity, ambientLight | **38-B** Scene Environment |
| Sky | `DrawSkySection()` (197-220) | skyZenith/HorizonColor, skyZenith/HorizonIntensity, sunIntensity | **38-B** Scene Environment |
| FSR | `DrawFsrSection()` (222-261) | fsrEnabled, fsrScaleMode, fsrCustomScale, fsrSharpness, fsrJitterScale | **38-A** Renderer Settings |
| SSIL | `DrawSsilSection()` (263-317) | ssilEnabled, ssilRadius, ... (9개) | **38-A** Renderer Settings |
| PostProcessing | `DrawPostProcessSection()` (319-346), `DrawEffectParam()` (348-402) | PostProcessStack.Effects | **38-C** Volume System |

### RenderSettings.cs (142줄, 전역 static)
**경로**: `src/IronRose.Engine/RoseEngine/RenderSettings.cs`
- 모든 프로퍼티가 static → 씬 전환해도 유지되는 문제
- postProcessing (PostProcessStack?), skybox 관련 3개, ambient 2개, sky 5개, FSR 5개, SSIL 9개

### SceneSerializer.cs — 현재 저장 범위
**경로**: `src/IronRose.Engine/Editor/SceneSerializer.cs` (1760줄)
- **Save** (152-161행): `[renderSettings]` 에 skyboxTextureGuid/Exposure/Rotation만 저장
- **Load**: skybox GUID로 `RenderSettings.ApplySkyboxFromGuid()` 호출
- ambient, sky, FSR, SSIL, PP → 저장 안 됨

---

## Phase 38-A: Renderer Settings Panel (.renderer 에셋 프로파일)

### 목표
FSR/SSIL 14개 프로퍼티를 `.renderer` TOML 에셋으로 영속화. 프로파일 전환 가능.

### A1. RendererProfile 데이터 클래스

**신규**: `src/IronRose.Engine/RoseEngine/RendererProfile.cs` (~120줄)

```csharp
namespace RoseEngine
{
    public class RendererProfile
    {
        public string name { get; set; } = "Default";

        // FSR (5)
        public bool fsrEnabled { get; set; } = false;
        public FsrScaleMode fsrScaleMode { get; set; } = FsrScaleMode.Quality;
        public float fsrCustomScale { get; set; } = 1.2f;
        public float fsrSharpness { get; set; } = 0.5f;
        public float fsrJitterScale { get; set; } = 1.0f;

        // SSIL (9)
        public bool ssilEnabled { get; set; } = true;
        public float ssilRadius { get; set; } = 1.5f;
        public float ssilFalloffScale { get; set; } = 2.0f;
        public int ssilSliceCount { get; set; } = 3;
        public int ssilStepsPerSlice { get; set; } = 3;
        public float ssilAoIntensity { get; set; } = 0.5f;
        public bool ssilIndirectEnabled { get; set; } = true;
        public float ssilIndirectBoost { get; set; } = 0.37f;
        public float ssilSaturationBoost { get; set; } = 2.0f;

        /// <summary>런타임 RenderSettings에 반영</summary>
        public void ApplyToRenderSettings() { /* 14개 필드 복사 */ }

        /// <summary>런타임 RenderSettings에서 캡처</summary>
        public void CaptureFromRenderSettings() { /* 14개 필드 읽기 */ }
    }
}
```

### A2. RendererProfileImporter

**신규**: `src/IronRose.Engine/AssetPipeline/RendererProfileImporter.cs` (~130줄)

AnimationClipImporter 패턴 (`AnimationClipImporter.cs:33-54`) 따름:

```csharp
namespace IronRose.AssetPipeline
{
    public class RendererProfileImporter
    {
        public RendererProfile? Import(string path, RoseMetadata? meta = null)
        {
            // File.ReadAllText → Toml.ToModel → Parse [fsr], [ssil] 섹션
        }

        public static void Export(RendererProfile profile, string path)
        {
            // RendererProfile → TomlTable → File.WriteAllText
        }

        public static void WriteDefault(string path)
        {
            // new RendererProfile() 기본값으로 Export
        }
    }
}
```

**.renderer TOML 포맷**:
```toml
[fsr]
enabled = false
scale_mode = "Quality"
custom_scale = 1.2
sharpness = 0.5
jitter_scale = 1.0

[ssil]
enabled = true
radius = 1.5
falloff_scale = 2.0
slice_count = 3
steps_per_slice = 3
ao_intensity = 0.5
indirect_enabled = true
indirect_boost = 0.37
saturation_boost = 2.0
```

### A3. AssetDatabase + RoseMetadata 등록

**수정**: `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs`
- 41-48행 `SupportedExtensions`에 `".renderer"` 추가
- 20-27행 영역에 `private readonly RendererProfileImporter _rendererProfileImporter = new();` 추가
- 19행 영역에 `private readonly Dictionary<RendererProfile, string> _rendererProfileToGuid = new();` 추가
- Load<T> switch에 case 추가:
  ```csharp
  case "RendererProfileImporter":
      asset = _rendererProfileImporter.Import(absolutePath, meta);
      if (asset is RendererProfile rp) _rendererProfileToGuid[rp] = guid;
      break;
  ```

**수정**: `src/IronRose.Engine/AssetPipeline/RoseMetadata.cs`
- 150행 `_ =>` 앞에 추가:
  ```csharp
  ".renderer" => new TomlTable { ["type"] = "RendererProfileImporter" },
  ```

### A4. RenderSettings에 activeProfile 필드

**수정**: `src/IronRose.Engine/RoseEngine/RenderSettings.cs`
- 139행 뒤에 추가:
  ```csharp
  // --- Active Renderer Profile ---
  public static RendererProfile? activeRendererProfile { get; set; }
  public static string? activeRendererProfileGuid { get; set; }
  public static string activeRendererProfileName => activeRendererProfile?.name ?? "None";
  ```

### A5. EngineCore — Default.renderer 자동 생성

**수정**: `src/IronRose.Engine/EngineCore.cs`
- Initialize() 에서 AssetDatabase 초기화 직후:
  ```csharp
  EnsureDefaultRendererProfile();
  ```
- 새 메서드:
  ```csharp
  private void EnsureDefaultRendererProfile()
  {
      var path = Path.Combine("Assets", "Settings", "Default.renderer");
      if (!File.Exists(Path.GetFullPath(path)))
      {
          Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
          RendererProfileImporter.WriteDefault(Path.GetFullPath(path));
          RoseMetadata.LoadOrCreate(Path.GetFullPath(path));
          Debug.Log("[Engine] Created default renderer profile");
      }
      // EditorState에 저장된 GUID로 로드, 없으면 Default 로드
      var db = Resources.GetAssetDatabase();
      var guid = EditorState.ActiveRendererProfileGuid;
      RendererProfile? profile = null;
      if (!string.IsNullOrEmpty(guid))
          profile = db?.LoadByGuid<RendererProfile>(guid);
      if (profile == null)
          profile = db?.Load<RendererProfile>(path);
      if (profile != null)
      {
          RenderSettings.activeRendererProfile = profile;
          RenderSettings.activeRendererProfileGuid = db?.GetGuidFromPath(path);
          profile.ApplyToRenderSettings();
      }
  }
  ```

### A6. ImGuiProjectPanel — Create 메뉴 + 더블클릭

**수정**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs`

1. **상태 필드 추가** (48-61행 영역):
   ```csharp
   private bool _openCreateRendererProfilePopup;
   private string _newRendererProfileName = "";
   private FolderNode? _createRendererProfileTargetFolder;
   private string? _pendingActivateRendererPath;
   ```

2. **Create 메뉴** (442-461행 영역, "Animation Clip" 뒤):
   ```csharp
   if (ImGui.MenuItem("Renderer Profile"))
   {
       _createRendererProfileTargetFolder = _selectedFolder;
       _openCreateRendererProfilePopup = true;
       _newRendererProfileName = "New Renderer";
   }
   ```

3. **모달 팝업** (484행 영역, 기존 모달 뒤):
   ```csharp
   var rr = EditorModal.InputTextPopup("Create Renderer Profile##Modal",
       "Profile name:", ref _openCreateRendererProfilePopup,
       ref _newRendererProfileName, "Create");
   if (rr == EditorModal.Result.Confirmed && !string.IsNullOrWhiteSpace(_newRendererProfileName))
       CreateRendererProfileFile(_newRendererProfileName.Trim());
   ```

4. **생성 메서드** (1087-1118행 패턴 따름):
   ```csharp
   private void CreateRendererProfileFile(string name)
   {
       var target = _createRendererProfileTargetFolder ?? _selectedFolder;
       if (target == null) return;
       var fileName = name + ".renderer";
       var filePath = Path.Combine(target.FullPath, fileName);
       var absPath = Path.GetFullPath(filePath);
       // 이름 충돌 처리 (기존 Material 패턴 따름)
       if (File.Exists(absPath)) { /* counter 증가 */ }
       RendererProfileImporter.WriteDefault(absPath);
       RoseMetadata.LoadOrCreate(absPath);
       _selectedAssetPath = filePath;
       _createRendererProfileTargetFolder = null;
   }
   ```

5. **더블클릭** (HandleAssetDoubleClick):
   ```csharp
   else if (string.Equals(ext, ".renderer", StringComparison.OrdinalIgnoreCase))
       _pendingActivateRendererPath = path;
   ```

6. **Consume 메서드**:
   ```csharp
   public string? ConsumePendingActivateRendererPath()
   {
       var p = _pendingActivateRendererPath;
       _pendingActivateRendererPath = null;
       return p;
   }
   ```

### A7. ImGuiRendererSettingsPanel

**신규**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiRendererSettingsPanel.cs` (~280줄)

```csharp
namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    public class ImGuiRendererSettingsPanel : IEditorPanel
    {
        private bool _isOpen = true;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }
        private float _autoSaveTimer;
        private bool _dirty;

        public void Draw()
        {
            if (!IsOpen) return;
            if (ImGui.Begin("Renderer Settings", ref _isOpen))
            {
                DrawProfileDropdown();
                DrawFsrSection();    // ImGuiRenderSettingsPanel.DrawFsrSection() 코드 이동
                DrawSsilSection();   // ImGuiRenderSettingsPanel.DrawSsilSection() 코드 이동
            }
            ImGui.End();
            AutoSave(Time.deltaTime);
        }

        private void DrawProfileDropdown()
        {
            // AssetDatabase에서 .renderer 파일 GUID 목록 가져오기
            // ComboBox로 표시, 선택 시 프로파일 로드 + ApplyToRenderSettings()
        }

        private void DrawFsrSection()
        {
            // ImGuiRenderSettingsPanel.DrawFsrSection() (222-261행) 그대로 이동
            // 값 변경 시 _dirty = true 추가
        }

        private void DrawSsilSection()
        {
            // ImGuiRenderSettingsPanel.DrawSsilSection() (263-317행) 그대로 이동
            // 값 변경 시 _dirty = true 추가
        }

        private void AutoSave(float dt)
        {
            if (!_dirty) return;
            _autoSaveTimer += dt;
            if (_autoSaveTimer >= 0.5f)
            {
                SaveActiveProfile();
                _autoSaveTimer = 0f;
                _dirty = false;
            }
        }

        private void SaveActiveProfile()
        {
            var profile = RenderSettings.activeRendererProfile;
            if (profile == null) return;
            profile.CaptureFromRenderSettings();
            var db = Resources.GetAssetDatabase();
            var guid = RenderSettings.activeRendererProfileGuid;
            if (guid != null)
            {
                var path = db?.GetPathFromGuid(guid);
                if (path != null) RendererProfileImporter.Export(profile, Path.GetFullPath(path));
            }
        }
    }
}
```

### A8. ImGuiOverlay + LayoutManager + EditorState 등록

**수정**: `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`

1. 필드 추가 (기존 `_renderSettings` 근처):
   ```csharp
   private ImGuiRendererSettingsPanel? _rendererSettings;
   ```

2. Initialize() (기존 `_renderSettings = new ...` 근처):
   ```csharp
   _rendererSettings = new ImGuiRendererSettingsPanel();
   ```

3. Draw panels (625행 근처):
   ```csharp
   _rendererSettings?.Draw();
   ```

4. Windows 메뉴 (484-486행 근처에 추가):
   ```csharp
   bool rs = _rendererSettings!.IsOpen;
   if (ImGui.MenuItem("Renderer Settings", null, ref rs))
       _rendererSettings.IsOpen = rs;
   ```

5. RestorePanelStates() (1408-1418행):
   ```csharp
   _rendererSettings!.IsOpen = EditorState.PanelRendererSettings;
   ```

6. SyncPanelStatesToEditorState() (1420-1430행):
   ```csharp
   EditorState.PanelRendererSettings = _rendererSettings?.IsOpen ?? true;
   ```

7. Project panel dispatch 처리 (HandleProjectSceneOpen 근처):
   ```csharp
   var rendererPath = _project?.ConsumePendingActivateRendererPath();
   if (rendererPath != null)
   {
       var db = Resources.GetAssetDatabase();
       var profile = db?.Load<RendererProfile>(rendererPath);
       if (profile != null)
       {
           RenderSettings.activeRendererProfile = profile;
           RenderSettings.activeRendererProfileGuid = db?.GetGuidFromPath(rendererPath);
           profile.ApplyToRenderSettings();
           EditorState.ActiveRendererProfileGuid = RenderSettings.activeRendererProfileGuid;
       }
   }
   ```

**수정**: `src/IronRose.Engine/Editor/ImGui/ImGuiLayoutManager.cs`
- ApplyDefaultIfNeeded() 시그니처에 `ImGuiRendererSettingsPanel rendererSettingsNew` 파라미터 추가
- 87행 뒤에: `ImGuiDockBuilder.DockWindow("Renderer Settings", renderSettingsId);` (기존 "Render Settings"와 같은 dock node에 탭)
- 98행 뒤에: `rendererSettingsNew.IsOpen = true;`

**수정**: `src/IronRose.Engine/Editor/EditorState.cs`
- 80행 뒤에:
  ```csharp
  public static bool PanelRendererSettings { get; set; } = true;
  public static string? ActiveRendererProfileGuid { get; set; }
  ```
- Load() panels 섹션 (146-156행):
  ```csharp
  if (panels.TryGetValue("renderer_settings", out var prs) && prs is bool brs) PanelRendererSettings = brs;
  ```
- Load() editor 섹션 (116-126행):
  ```csharp
  if (editor.TryGetValue("active_renderer_profile", out var varp) && varp is string sarp)
      ActiveRendererProfileGuid = sarp;
  ```
- Save() panels 섹션 (194-202행):
  ```csharp
  toml += $"renderer_settings = {BoolStr(PanelRendererSettings)}\n";
  ```
- Save() editor 섹션 (179-183행):
  ```csharp
  if (!string.IsNullOrEmpty(ActiveRendererProfileGuid))
      toml += $"active_renderer_profile = \"{ActiveRendererProfileGuid}\"\n";
  ```

### A9. 기존 패널에서 FSR/SSIL 제거

**수정**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiRenderSettingsPanel.cs`
- `DrawFsrSection()` (222-261행) 삭제
- `DrawSsilSection()` (263-317행) 삭제
- `Draw()` (20-34행)에서 호출 제거:
  ```csharp
  // 삭제: DrawFsrSection();
  // 삭제: DrawSsilSection();
  ```

**→ dotnet build + 실행 테스트**

---

## Phase 38-B: Scene Environment Panel (씬별 환경 설정)

### 목표
Skybox/Ambient/Sky를 `[sceneEnvironment]`로 씬 파일에 저장. 씬 전환 시 자동 교체.

### B1. ImGuiSceneEnvironmentPanel

**신규**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiSceneEnvironmentPanel.cs` (~300줄)

```csharp
namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    public class ImGuiSceneEnvironmentPanel : IEditorPanel
    {
        private bool _isOpen = true;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }

        private static bool _showSkyboxError;
        private static string _skyboxErrorMessage = "";
        private static readonly string[] _skyboxImageExtensions = { ".hdr", ".exr", ".png", ".jpg", ".jpeg", ".tga", ".bmp" };
        private static readonly string[] _skyboxHdrExtensions = { ".hdr", ".exr" };

        public void Draw()
        {
            if (!IsOpen) return;
            if (ImGui.Begin("Scene Environment", ref _isOpen))
            {
                DrawSkyboxSection();   // 기존 ImGuiRenderSettingsPanel:36-117 이동
                DrawAmbientSection();  // 기존 ImGuiRenderSettingsPanel:184-195 이동
                DrawSkySection();      // 기존 ImGuiRenderSettingsPanel:197-220 이동
            }
            ImGui.End();
        }

        // DrawSkyboxSection: 그대로 이동 + 값 변경 시 MarkSceneDirty() 호출
        // DrawAmbientSection: 그대로 이동 + MarkSceneDirty()
        // DrawSkySection: 그대로 이동 + MarkSceneDirty()
        // ApplySkyboxTexture: 그대로 이동 (122-182행)

        private static void MarkSceneDirty()
        {
            SceneManager.GetActiveScene().isDirty = true;
        }
    }
}
```

**변경 핵심**: 각 슬라이더/색상 변경 코드에 `MarkSceneDirty()` 호출 추가.
예시 (DrawAmbientSection):
```csharp
float intensity = RenderSettings.ambientIntensity;
if (EditorWidgets.SliderFloatWithInput("SE","Ambient Intensity", ref intensity, 0f, 5f))
{
    RenderSettings.ambientIntensity = intensity;
    MarkSceneDirty();
}
```

### B2. SceneSerializer — [sceneEnvironment] 직렬화

**수정**: `src/IronRose.Engine/Editor/SceneSerializer.cs`

**Save** (152-161행 교체):
```csharp
// [sceneEnvironment] — 11개 환경 프로퍼티
var envTable = new TomlTable();

// Skybox
if (!string.IsNullOrEmpty(RenderSettings.skyboxTextureGuid))
{
    envTable["skyboxTextureGuid"] = RenderSettings.skyboxTextureGuid;
    envTable["skyboxExposure"] = (double)RenderSettings.skyboxExposure;
    envTable["skyboxRotation"] = (double)RenderSettings.skyboxRotation;
}

// Ambient
envTable["ambientIntensity"] = (double)RenderSettings.ambientIntensity;
envTable["ambientLight"] = ColorToArray(RenderSettings.ambientLight);

// Procedural Sky
envTable["skyZenithIntensity"] = (double)RenderSettings.skyZenithIntensity;
envTable["skyHorizonIntensity"] = (double)RenderSettings.skyHorizonIntensity;
envTable["sunIntensity"] = (double)RenderSettings.sunIntensity;
envTable["skyZenithColor"] = ColorToArray(RenderSettings.skyZenithColor);
envTable["skyHorizonColor"] = ColorToArray(RenderSettings.skyHorizonColor);

root["sceneEnvironment"] = envTable;
```

**Color 헬퍼** (유틸리티 메서드 추가):
```csharp
private static TomlArray ColorToArray(Color c) =>
    new() { (double)c.r, (double)c.g, (double)c.b, (double)c.a };

private static Color ArrayToColor(TomlArray arr, Color defaultColor)
{
    if (arr.Count < 3) return defaultColor;
    float a = arr.Count >= 4 ? ToFloat(arr[3]) : 1f;
    return new Color(ToFloat(arr[0]), ToFloat(arr[1]), ToFloat(arr[2]), a);
}
```

### B3. SceneSerializer — 로드 + 하위호환

**수정**: `src/IronRose.Engine/Editor/SceneSerializer.cs` Load()

씬 로드 시 (renderSettings 읽기 부분 교체):

```csharp
// 환경 기본값 리셋 (이전 씬 값 잔류 방지)
ResetEnvironmentDefaults();

// [sceneEnvironment] 우선, [renderSettings] 하위호환
if (root.TryGetValue("sceneEnvironment", out var envVal) && envVal is TomlTable envTable)
{
    RestoreEnvironmentFromTable(envTable);
}
else if (root.TryGetValue("renderSettings", out var rsVal) && rsVal is TomlTable rsTable)
{
    // 레거시: skybox 3개 필드만 복원
    RestoreLegacyRenderSettings(rsTable);
}
```

**새 메서드**:
```csharp
private static void ResetEnvironmentDefaults()
{
    RenderSettings.skyboxTextureGuid = null;
    RenderSettings.skybox = null;
    RenderSettings.skyboxExposure = 1.0f;
    RenderSettings.skyboxRotation = 0.0f;
    RenderSettings.ambientLight = new Color(0.2f, 0.2f, 0.2f, 1f);
    RenderSettings.ambientIntensity = 1.0f;
    RenderSettings.skyZenithColor = new Color(0.15f, 0.3f, 0.65f);
    RenderSettings.skyHorizonColor = new Color(0.6f, 0.7f, 0.85f);
    RenderSettings.skyZenithIntensity = 0.8f;
    RenderSettings.skyHorizonIntensity = 1.0f;
    RenderSettings.sunIntensity = 20.0f;
}

private static void RestoreEnvironmentFromTable(TomlTable env)
{
    // Skybox
    if (env.TryGetValue("skyboxTextureGuid", out var sgVal) && sgVal is string sg)
    {
        RenderSettings.skyboxTextureGuid = sg;
        if (env.TryGetValue("skyboxExposure", out var seVal)) RenderSettings.skyboxExposure = ToFloat(seVal);
        if (env.TryGetValue("skyboxRotation", out var srVal)) RenderSettings.skyboxRotation = ToFloat(srVal);
        RenderSettings.ApplySkyboxFromGuid();
    }
    // Ambient
    if (env.TryGetValue("ambientIntensity", out var aiVal)) RenderSettings.ambientIntensity = ToFloat(aiVal);
    if (env.TryGetValue("ambientLight", out var alVal) && alVal is TomlArray alArr)
        RenderSettings.ambientLight = ArrayToColor(alArr, new Color(0.2f, 0.2f, 0.2f, 1f));
    // Sky
    if (env.TryGetValue("skyZenithIntensity", out var ziVal)) RenderSettings.skyZenithIntensity = ToFloat(ziVal);
    if (env.TryGetValue("skyHorizonIntensity", out var hiVal)) RenderSettings.skyHorizonIntensity = ToFloat(hiVal);
    if (env.TryGetValue("sunIntensity", out var siVal)) RenderSettings.sunIntensity = ToFloat(siVal);
    if (env.TryGetValue("skyZenithColor", out var zcVal) && zcVal is TomlArray zcArr)
        RenderSettings.skyZenithColor = ArrayToColor(zcArr, new Color(0.15f, 0.3f, 0.65f));
    if (env.TryGetValue("skyHorizonColor", out var hcVal) && hcVal is TomlArray hcArr)
        RenderSettings.skyHorizonColor = ArrayToColor(hcArr, new Color(0.6f, 0.7f, 0.85f));
}

private static void RestoreLegacyRenderSettings(TomlTable rs)
{
    if (rs.TryGetValue("skyboxTextureGuid", out var sgVal) && sgVal is string sg)
    {
        RenderSettings.skyboxTextureGuid = sg;
        if (rs.TryGetValue("skyboxExposure", out var seVal)) RenderSettings.skyboxExposure = ToFloat(seVal);
        if (rs.TryGetValue("skyboxRotation", out var srVal)) RenderSettings.skyboxRotation = ToFloat(srVal);
        RenderSettings.ApplySkyboxFromGuid();
    }
    // ambient, sky는 기본값 유지 (ResetEnvironmentDefaults에서 이미 세팅)
}
```

### B4. SceneEnvironmentUndoAction

**신규**: `src/IronRose.Engine/Editor/Undo/SceneEnvironmentUndoAction.cs` (~70줄)

```csharp
namespace IronRose.Engine.Editor
{
    internal struct EnvironmentSnapshot
    {
        public string? skyboxTextureGuid; public float skyboxExposure, skyboxRotation;
        public Color ambientLight; public float ambientIntensity;
        public Color skyZenithColor, skyHorizonColor;
        public float skyZenithIntensity, skyHorizonIntensity, sunIntensity;

        public static EnvironmentSnapshot Capture() { /* RenderSettings에서 읽기 */ }
        public void Apply() { /* RenderSettings에 쓰기 + ApplySkyboxFromGuid */ }
    }

    public class SceneEnvironmentUndoAction : IUndoAction
    {
        public string Description { get; }
        private readonly EnvironmentSnapshot _before, _after;

        public SceneEnvironmentUndoAction(string desc, EnvironmentSnapshot before, EnvironmentSnapshot after)
        {
            Description = desc; _before = before; _after = after;
        }

        public void Undo() => _before.Apply();
        public void Redo() => _after.Apply();
    }
}
```

패널에서 사용: `IsItemActivated` → before 캡처, `IsItemDeactivatedAfterEdit` → after 캡처 → UndoSystem.Record()

### B5. ImGuiOverlay + LayoutManager + EditorState 등록

A8과 동일한 패턴으로:

**ImGuiOverlay.cs**:
- 필드: `private ImGuiSceneEnvironmentPanel? _sceneEnvironment;`
- Initialize: `_sceneEnvironment = new ImGuiSceneEnvironmentPanel();`
- Draw: `_sceneEnvironment?.Draw();`
- Menu: `"Scene Environment"` 추가
- RestorePanelStates: `_sceneEnvironment!.IsOpen = EditorState.PanelSceneEnvironment;`
- SyncPanelStates: `EditorState.PanelSceneEnvironment = _sceneEnvironment?.IsOpen ?? true;`

**ImGuiLayoutManager.cs**:
- ApplyDefaultIfNeeded에 파라미터 추가
- DockWindow("Scene Environment", renderSettingsId) — 같은 dock 노드에 탭

**EditorState.cs**:
- `public static bool PanelSceneEnvironment { get; set; } = true;`
- Load/Save에 `scene_environment` 키 추가

### B6. 기존 패널에서 Skybox/Ambient/Sky 제거

**수정**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiRenderSettingsPanel.cs`
- `DrawSkyboxSection()` (36-117행) 삭제
- `ApplySkyboxTexture()` (122-182행) 삭제
- `_skyboxImageExtensions`, `_skyboxHdrExtensions` (119-120행) 삭제
- `_showSkyboxError`, `_skyboxErrorMessage` (17-18행) 삭제
- `DrawAmbientSection()` (184-195행) 삭제
- `DrawSkySection()` (197-220행) 삭제
- `Draw()`에서 호출 제거

결과: `ImGuiRenderSettingsPanel.cs`에는 `DrawPostProcessSection()` + `DrawEffectParam()`만 남음 (~90줄)

**→ dotnet build + 실행 테스트**

---

## Phase 38-C: Post Processing Volume System

### 목표
전역 PP → 볼륨 기반. 카메라 위치로 진입 판정, 가중 평균 블렌딩, Volume 밖은 PP 없음.

### C1. PostProcessProfile 데이터 클래스

**신규**: `src/IronRose.Engine/RoseEngine/PostProcessProfile.cs` (~100줄)

```csharp
namespace RoseEngine
{
    public class EffectOverride
    {
        public string typeName { get; set; } = "";    // "Bloom", "Tonemap"
        public bool enabled { get; set; } = true;
        public Dictionary<string, float> parameters { get; set; } = new();
    }

    public class PostProcessProfile
    {
        public string name { get; set; } = "New Profile";
        public List<EffectOverride> effects { get; set; } = new();

        public EffectOverride? FindEffect(string typeName)
            => effects.Find(e => e.typeName == typeName);

        public EffectOverride AddEffect(string typeName)
        {
            var e = new EffectOverride { typeName = typeName };
            effects.Add(e);
            return e;
        }

        public void RemoveEffect(string typeName)
            => effects.RemoveAll(e => e.typeName == typeName);
    }
}
```

### C2. PostProcessProfileImporter

**신규**: `src/IronRose.Engine/AssetPipeline/PostProcessProfileImporter.cs` (~120줄)

```csharp
namespace IronRose.AssetPipeline
{
    public class PostProcessProfileImporter
    {
        public PostProcessProfile? Import(string path, RoseMetadata? meta = null)
        {
            // Toml.ToModel → [[effects]] 배열 파싱
        }

        public static void Export(PostProcessProfile profile, string path)
        {
            // effects → TomlTableArray 변환 → File.WriteAllText
        }

        public static void WriteDefault(string path)
        {
            // Bloom(기본값) + Tonemap(기본값) 프로파일 생성
        }
    }
}
```

**.ppprofile TOML 포맷**:
```toml
[[effects]]
type = "Bloom"
enabled = true
[effects.params]
threshold = 0.8
soft_knee = 0.5
intensity = 0.5

[[effects]]
type = "Tonemap"
enabled = true
[effects.params]
exposure = 1.5
saturation = 1.6
contrast = 1.0
white_point = 10.0
gamma = 1.2
```

### C3. AssetDatabase + RoseMetadata 등록

A3과 동일한 패턴:

**AssetDatabase.cs**: `.ppprofile` 추가, `PostProcessProfileImporter` 필드/case 추가
**RoseMetadata.cs**: `.ppprofile` case 추가

### C4. Neutral values

**수정**: `src/IronRose.Rendering/PostProcessing/PostProcessEffect.cs`
- 추가:
  ```csharp
  /// <summary>이펙트가 꺼진 것과 동일한 결과를 내는 파라미터 값</summary>
  public virtual Dictionary<string, float> GetNeutralValues() => new();
  ```

**수정**: `src/IronRose.Rendering/PostProcessing/BloomEffect.cs`
```csharp
public override Dictionary<string, float> GetNeutralValues() => new()
{
    ["Threshold"] = 10.0f,   // 아무것도 통과 못함
    ["Soft Knee"] = 0.0f,
    ["Intensity"] = 0.0f,    // 블룸 없음
};
```

**수정**: `src/IronRose.Rendering/PostProcessing/TonemapEffect.cs`
```csharp
public override Dictionary<string, float> GetNeutralValues() => new()
{
    ["Exposure"] = 1.0f,     // 밝기 변화 없음
    ["Saturation"] = 1.0f,   // 채도 변화 없음
    ["Contrast"] = 1.0f,     // 대비 변화 없음
    ["White Point"] = 4.0f,
    ["Gamma"] = 2.2f,
};
```

### C5. PostProcessVolume 컴포넌트

**신규**: `src/IronRose.Engine/RoseEngine/PostProcessVolume.cs` (~80줄)

```csharp
namespace RoseEngine
{
    public class PostProcessVolume : MonoBehaviour
    {
        internal static readonly List<PostProcessVolume> _allVolumes = new();

        public float blendDistance = 0f;
        public float weight = 1f;
        public PostProcessProfile? profile;
        public string? profileGuid;  // 직렬화용

        public override void Awake()
        {
            base.Awake();
            if (!_allVolumes.Contains(this))
                _allVolumes.Add(this);
        }

        internal override void OnComponentDestroy()
        {
            _allVolumes.Remove(this);
            base.OnComponentDestroy();
        }

        /// <summary>BoxCollider 기반 AABB 반환 (blendDistance로 확장)</summary>
        public Bounds GetInflatedBounds()
        {
            var box = GetComponent<BoxCollider>();
            if (box == null) return new Bounds(transform.position, Vector3.zero);
            var worldCenter = transform.TransformPoint(box.center);
            var worldSize = Vector3.Scale(box.size, transform.lossyScale);
            var bounds = new Bounds(worldCenter, worldSize);
            bounds.Expand(blendDistance * 2f);
            return bounds;
        }

        /// <summary>BoxCollider 내부 바운드 (blendDistance 미적용)</summary>
        public Bounds GetInnerBounds()
        {
            var box = GetComponent<BoxCollider>();
            if (box == null) return new Bounds(transform.position, Vector3.zero);
            var worldCenter = transform.TransformPoint(box.center);
            var worldSize = Vector3.Scale(box.size, transform.lossyScale);
            return new Bounds(worldCenter, worldSize);
        }
    }
}
```

**참고**: `Bounds.cs`에 이미 `Contains()`, `ClosestPoint()`, `SqrDistance()`, `Expand()` 구현됨.

### C6. PostProcessManager

**신규**: `src/IronRose.Engine/PostProcessManager.cs` (~200줄)

```csharp
namespace IronRose.Engine
{
    public class PostProcessManager
    {
        internal static PostProcessManager? Instance { get; private set; }
        public bool IsPostProcessActive { get; private set; }

        public void Initialize() { Instance = this; }

        public void Update(Vector3 cameraPosition)
        {
            var stack = RenderSettings.postProcessing;
            if (stack == null) { IsPostProcessActive = false; return; }

            // 1. 진입 Volume 수집 + effectiveWeight 계산
            var activeVolumes = new List<(PostProcessVolume vol, float weight)>();
            foreach (var vol in PostProcessVolume._allVolumes)
            {
                if (vol.profile == null || !vol.gameObject.activeInHierarchy) continue;
                float factor = CalculateDistanceFactor(cameraPosition, vol);
                if (factor <= 0f) continue;
                activeVolumes.Add((vol, vol.weight * factor));
            }

            // 2. 진입 Volume 없음 → PP 비활성
            if (activeVolumes.Count == 0)
            {
                IsPostProcessActive = false;
                return;
            }
            IsPostProcessActive = true;

            // 3. 이펙트별 가중 평균 블렌딩
            float totalWeight = 0f;
            foreach (var (_, w) in activeVolumes) totalWeight += w;

            foreach (var effect in stack.Effects)
            {
                var neutralValues = effect.GetNeutralValues();
                foreach (var param in effect.GetParameters())
                {
                    if (param.ValueType != typeof(float)) continue;
                    float blended = 0f;
                    foreach (var (vol, ew) in activeVolumes)
                    {
                        var eo = vol.profile!.FindEffect(effect.Name);
                        float value;
                        if (eo != null && eo.enabled && eo.parameters.TryGetValue(param.Name, out var v))
                            value = v;
                        else if (neutralValues.TryGetValue(param.Name, out var nv))
                            value = nv;
                        else
                            value = (float)param.GetValue();
                        blended += value * ew;
                    }
                    param.SetValue(blended / totalWeight);
                }
            }
        }

        private float CalculateDistanceFactor(Vector3 cameraPos, PostProcessVolume vol)
        {
            var innerBounds = vol.GetInnerBounds();
            if (innerBounds.Contains(cameraPos)) return 1f;
            if (vol.blendDistance <= 0f) return 0f;
            float dist = Mathf.Sqrt(innerBounds.SqrDistance(cameraPos));
            return Mathf.Clamp01(1f - dist / vol.blendDistance);
        }

        public void Reset()
        {
            IsPostProcessActive = false;
        }

        public void Dispose()
        {
            Instance = null;
        }
    }
}
```

### C7. EngineCore 통합

**수정**: `src/IronRose.Engine/EngineCore.cs`

1. 필드: `private PostProcessManager? _postProcessManager;`
2. Initialize():
   ```csharp
   _postProcessManager = new PostProcessManager();
   _postProcessManager.Initialize();
   ```
3. Update() — SceneManager.Update() 이후, Render() 이전:
   ```csharp
   var camPos = Camera.main?.transform.position ?? Vector3.zero;
   _postProcessManager?.Update(camPos);
   ```
4. Shutdown(): `_postProcessManager?.Dispose();`

### C8. RenderSystem guard clause

**수정**: `src/IronRose.Engine/RenderSystem.cs`

PP 실행 전 (약 1576행 근처)에 guard 추가:

```csharp
bool ppActive = PostProcessManager.Instance?.IsPostProcessActive ?? true;  // Manager 없으면 기존 동작
```

기존 PP 실행 블록을 `if (ppActive)` / `else` 로 감싸기:

```csharp
if (ppActive)
{
    // 기존 코드: ctx.PostProcessStack?.Execute(...) 또는 ExecuteEffectsOnly + FSR
}
else
{
    // PP 없이 HDR → 스왑체인 직접 블릿
    ctx.PostProcessStack?.BlitToSwapchain(cl, ctx.HdrView!, FinalOutputFB);
}
```

**변경량**: ~10줄 (기존 코드 감싸기)

### C9. SceneSerializer — Volume 직렬화

**수정**: `src/IronRose.Engine/Editor/SceneSerializer.cs`

SerializeComponent (178행 근처):
```csharp
if (comp is PostProcessVolume ppv)
{
    compTable["type"] = "PostProcessVolume";
    compTable["blendDistance"] = (double)ppv.blendDistance;
    compTable["weight"] = (double)ppv.weight;
    if (!string.IsNullOrEmpty(ppv.profileGuid))
        compTable["profileGuid"] = ppv.profileGuid;
}
```

DeserializeComponent:
```csharp
case "PostProcessVolume":
    var ppv = go.AddComponent<PostProcessVolume>();
    if (ct.TryGetValue("blendDistance", out var bdVal)) ppv.blendDistance = ToFloat(bdVal);
    if (ct.TryGetValue("weight", out var wVal)) ppv.weight = ToFloat(wVal);
    if (ct.TryGetValue("profileGuid", out var pgVal) && pgVal is string pgStr)
    {
        ppv.profileGuid = pgStr;
        ppv.profile = db?.LoadByGuid<PostProcessProfile>(pgStr);
    }
    break;
```

### C10. ImGuiProjectPanel — Create 메뉴

A6과 동일한 패턴:
- Create > Post Process Profile 메뉴
- `PostProcessProfileImporter.WriteDefault()` 호출
- 더블클릭 → Inspector에서 열기 (편집용)

### C11. ImGuiInspectorPanel — Volume/Profile Inspector

**수정**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs`

1. **PostProcessVolume 컴포넌트 커스텀 드로어**:
   ```csharp
   if (comp is PostProcessVolume ppVol)
   {
       // blendDistance 슬라이더 (0-50)
       // weight 슬라이더 (0-1)
       // profile 에셋 참조 (드래그앤드롭 .ppprofile)
       // profileGuid 연동
   }
   ```

2. **PostProcessProfile 에셋 인스펙터** (Project 패널에서 .ppprofile 선택 시):
   - "Add Effect" 드롭다운 (Bloom, Tonemap 중 미등록 이펙트)
   - 이펙트별 접기/펴기 섹션
   - 각 파라미터 슬라이더
   - 이펙트 삭제 버튼
   - 값 변경 시 .ppprofile 자동 저장

### C12. ImGuiRenderSettingsPanel.cs 삭제 + 정리

**삭제**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiRenderSettingsPanel.cs`

**수정**: `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`
- `_renderSettings` 필드 삭제
- Initialize()에서 `_renderSettings = new ...` 삭제
- Draw()에서 `_renderSettings?.Draw()` 삭제
- Windows 메뉴에서 "Render Settings" 항목 삭제
- RestorePanelStates에서 `_renderSettings` 참조 삭제
- SyncPanelStatesToEditorState에서 참조 삭제

**수정**: `src/IronRose.Engine/Editor/ImGui/ImGuiLayoutManager.cs`
- ApplyDefaultIfNeeded에서 `ImGuiRenderSettingsPanel renderSettings` 파라미터 삭제
- DockWindow("Render Settings", ...) 삭제

**→ dotnet build + 실행 테스트**

---

## 구현 순서 요약

```
38-A: Renderer Settings (자립적)
  A1. RendererProfile.cs (신규)
  A2. RendererProfileImporter.cs (신규)
  A3. AssetDatabase.cs + RoseMetadata.cs (수정)
  A4. RenderSettings.cs (수정)
  A5. EngineCore.cs (수정)
  A6. ImGuiProjectPanel.cs (수정)
  A7. ImGuiRendererSettingsPanel.cs (신규)
  A8. ImGuiOverlay.cs + ImGuiLayoutManager.cs + EditorState.cs (수정)
  A9. ImGuiRenderSettingsPanel.cs (수정 — FSR/SSIL 삭제)
  → BUILD + TEST

38-B: Scene Environment (A 완료 후)
  B1. ImGuiSceneEnvironmentPanel.cs (신규)
  B2. SceneSerializer.cs — Save [sceneEnvironment] (수정)
  B3. SceneSerializer.cs — Load + 하위호환 (수정)
  B4. SceneEnvironmentUndoAction.cs (신규)
  B5. ImGuiOverlay.cs + ImGuiLayoutManager.cs + EditorState.cs (수정)
  B6. ImGuiRenderSettingsPanel.cs (수정 — Skybox/Ambient/Sky 삭제)
  → BUILD + TEST

38-C: PostProcess Volume (B 완료 후)
  C1. PostProcessProfile.cs (신규)
  C2. PostProcessProfileImporter.cs (신규)
  C3. AssetDatabase.cs + RoseMetadata.cs (수정)
  C4. PostProcessEffect.cs + BloomEffect.cs + TonemapEffect.cs (수정)
  C5. PostProcessVolume.cs (신규)
  C6. PostProcessManager.cs (신규)
  C7. EngineCore.cs (수정)
  C8. RenderSystem.cs (수정 — ~10줄 guard)
  C9. SceneSerializer.cs (수정)
  C10. ImGuiProjectPanel.cs (수정)
  C11. ImGuiInspectorPanel.cs (수정)
  C12. ImGuiRenderSettingsPanel.cs (삭제) + ImGuiOverlay.cs 정리
  → BUILD + TEST
```

## 파일 요약

| 구분 | 수 | 예상 줄 수 |
|------|---|----------|
| 신규 파일 | 9개 | ~1,390줄 |
| 수정 파일 | 14개 | — |
| 삭제 파일 | 1개 (ImGuiRenderSettingsPanel.cs) | -406줄 |

## 검증 체크리스트

### Phase 38-A
- [ ] Assets/Settings/Default.renderer 자동 생성
- [ ] Renderer Settings 패널 — 프로파일 드롭다운 동작
- [ ] FSR/SSIL 슬라이더 → RenderSettings 반영
- [ ] 값 변경 시 .renderer 파일 자동 저장
- [ ] Project > Create > Renderer Profile 동작
- [ ] 더블클릭으로 프로파일 전환

### Phase 38-B
- [ ] Scene Environment 패널 — Skybox/Ambient/Sky 표시
- [ ] 환경 변경 → 씬 저장 → 리로드 → 값 복원
- [ ] 새 씬 생성 → 기본값 적용
- [ ] 구 씬 파일 하위호환 ([renderSettings] → [sceneEnvironment] 마이그레이션)
- [ ] Undo/Redo 동작

### Phase 38-C
- [ ] .ppprofile 생성/편집
- [ ] PostProcessVolume 컴포넌트 Inspector
- [ ] 카메라 Volume 진입 → PP 적용
- [ ] 카메라 Volume 퇴장 → PP 사라짐
- [ ] blendDistance → 부드러운 페이드
- [ ] 두 Volume 겹침 → 블렌딩
- [ ] 씬 저장/로드 → Volume 컴포넌트 복원

---

## 구현 로그

### Phase 38-A 완료 ✅ (2026-03-01)

**새 파일 (3개)**:
- `src/IronRose.Engine/RoseEngine/RendererProfile.cs` — 14개 FSR/SSIL 프로퍼티 + ApplyToRenderSettings/CaptureFromRenderSettings
- `src/IronRose.Engine/AssetPipeline/RendererProfileImporter.cs` — .renderer TOML Import/Export/WriteDefault
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiRendererSettingsPanel.cs` — 프로파일 드롭다운 + FSR/SSIL 섹션 + 디바운스 자동 저장

**수정 파일 (7개)**:
- `AssetDatabase.cs` — .renderer SupportedExtensions, _rendererProfileImporter, Load<T> case, GUID 트래킹
- `RoseMetadata.cs` — InferImporter()에 .renderer case
- `RenderSettings.cs` — activeRendererProfile, activeRendererProfileGuid, activeRendererProfileName 추가
- `EngineCore.cs` — InitAssets()에서 EnsureDefaultRendererProfile() 호출 (Assets/Settings/Default.renderer 자동 생성 + EditorState GUID 복원)
- `ImGuiProjectPanel.cs` — Create > Renderer Profile 메뉴 (3곳), 모달 팝업, CreateRendererProfileFile(), HandleAssetDoubleClick .renderer 추가, ConsumePendingActivateRendererPath()
- `ImGuiOverlay.cs` — _rendererSettings 필드, Initialize/Draw/Windows 메뉴/RestorePanelStates/SyncPanelStatesToEditorState, .renderer 더블클릭 dispatch
- `ImGuiLayoutManager.cs` — ApplyDefaultIfNeeded에 rendererSettings 파라미터, DockWindow "Renderer Settings" (Render Settings와 같은 독 공간)
- `EditorState.cs` — PanelRendererSettings, ActiveRendererProfileGuid 추가 (Load/Save TOML)
- `ImGuiRenderSettingsPanel.cs` — DrawFsrSection/DrawSsilSection 제거, Draw()에서 호출 제거

**빌드**: `dotnet build` 성공 (오류 0, 경고 6 — 기존 경고)

### Phase 38-B 완료 ✅ (2026-03-01)

**새 파일 (1개)**:
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiSceneEnvironmentPanel.cs` — Skybox/Ambient/Sky 섹션 + Scene.isDirty 마킹

**수정 파일 (5개)**:
- `SceneSerializer.cs` — Save: `[sceneEnvironment]` 11개 프로퍼티 직렬화 (ColorToArray 활용). Load: `[sceneEnvironment]` 우선 + `[renderSettings]` 하위호환. ResetEnvironmentDefaults() + LoadSceneEnvironment() 헬퍼 추가
- `ImGuiOverlay.cs` — _sceneEnvironment 필드, Initialize/Draw/Windows 메뉴/RestorePanelStates/SyncPanelStatesToEditorState 등록
- `ImGuiLayoutManager.cs` — ApplyDefaultIfNeeded에 sceneEnvironment 파라미터, DockWindow "Scene Environment" (같은 독 공간)
- `EditorState.cs` — PanelSceneEnvironment 추가 (Load/Save TOML)
- `ImGuiRenderSettingsPanel.cs` — Skybox/Ambient/Sky 섹션 완전 제거, PostProcessing 섹션만 남음 (306줄 → 115줄)

**빌드**: `dotnet build` 성공 (오류 0, 경고 6 — 기존 경고)

### Phase 38-C 완료 ✅ (2026-03-01)

**새 파일 (4개)**:
- `src/IronRose.Engine/RoseEngine/PostProcessProfile.cs` — PP 프로파일 데이터 클래스 (EffectOverride 목록, TryGetEffect/GetOrAddEffect)
- `src/IronRose.Engine/AssetPipeline/PostProcessProfileImporter.cs` — .ppprofile TOML Import/Export/WriteDefault
- `src/IronRose.Engine/RoseEngine/PostProcessVolume.cs` — MonoBehaviour, static _allVolumes 레지스트리, blendDistance/weight/profile, GetInnerBounds/GetInflatedBounds
- `src/IronRose.Engine/PostProcessManager.cs` — Singleton, Update(cameraPos): Volume 블렌딩, ComputeDistanceFactor, IsPostProcessActive

**수정 파일 (11개)**:
- `PostProcessEffect.cs` — `virtual Dictionary<string,float> GetNeutralValues()` 추가
- `BloomEffect.cs` — override GetNeutralValues(): Threshold=10, SoftKnee=0, Intensity=0
- `TonemapEffect.cs` — override GetNeutralValues(): Exposure=1, Saturation=1, Contrast=1, WhitePoint=4, Gamma=2.2
- `AssetDatabase.cs` — .ppprofile 등록 (SupportedExtensions, _ppProfileImporter, _ppProfileToGuid, Load case)
- `RoseMetadata.cs` — InferImporter()에 .ppprofile → PostProcessProfileImporter
- `EngineCore.cs` — _postProcessManager 생성/초기화, Update(cameraPos), Dispose
- `RenderSystem.cs` — ppActive guard: Volume 없으면 이펙트 건너뛰고 직접 blit
- `SceneSerializer.cs` — PostProcessVolume 직렬화/역직렬화 (blendDistance, weight, profileGuid)
- `ImGuiProjectPanel.cs` — Create > Post Process Profile 메뉴 (2곳), CreatePPProfileFile(), 모달
- `ImGuiInspectorPanel.cs` — PostProcessProfile AssetNameExtractor, GetTypeFilter, 드래그드롭, Browse 콜백, DrawPostProcessProfileEditor()
- `Attributes.cs` — HideInInspectorAttribute에 AttributeTargets.Property 추가

**삭제 파일 (1개)**:
- `ImGuiRenderSettingsPanel.cs` — 완전 삭제

**정리 파일 (4개)**:
- `ImGuiOverlay.cs` — _renderSettings 필드/메뉴/Draw/RestorePanelStates/SyncPanelStates 제거
- `ImGuiLayoutManager.cs` — renderSettings 파라미터/DockWindow 제거
- `EditorState.cs` — PanelRenderSettings 제거 (Load/Save)
- `PostProcessVolume.cs` — profileGuid에 [HideInInspector] (Property 지원 필요 → Attributes.cs 수정)

**블렌딩 알고리즘**:
- distanceFactor: BoxCollider inner bounds Contains → 1.0, 외부+blendDistance=0 → 0.0, 경계 → 선형 페이드
- effectiveWeight = volume.weight × distanceFactor
- 최종값 = Σ(value × effectiveWeight) / Σ(effectiveWeight)
- Volume에 없는 이펙트 → neutral value로 참여

**빌드**: `dotnet build` 성공 (오류 0, 경고 6 — 기존 경고)

---

## Phase 38 전체 완료 요약

| 구분 | 수 | 비고 |
|------|---|------|
| 새 파일 | 8개 | RendererProfile, RendererProfileImporter, ImGuiRendererSettingsPanel, ImGuiSceneEnvironmentPanel, PostProcessProfile, PostProcessProfileImporter, PostProcessVolume, PostProcessManager |
| 수정 파일 | ~15개 | AssetDatabase, RoseMetadata, RenderSettings, EngineCore, SceneSerializer, ImGuiProjectPanel, ImGuiOverlay, ImGuiLayoutManager, EditorState, ImGuiInspectorPanel, RenderSystem, PostProcessEffect, BloomEffect, TonemapEffect, Attributes |
| 삭제 파일 | 1개 | ImGuiRenderSettingsPanel.cs (406줄 → 0) |

**결과**: 모놀리식 Render Settings 패널이 3개 전문 시스템으로 완전 분리됨:
1. **Renderer Settings** — FSR/SSIL 14개 프로퍼티, .renderer TOML 프로파일 에셋으로 영속화
2. **Scene Environment** — Skybox/Ambient/Sky, 씬 파일에 [sceneEnvironment] 섹션으로 저장
3. **Post Processing Volume** — Volume 기반 PP, .ppprofile 에셋, 카메라 위치 기반 블렌딩

---

## Phase 38-C 상세 구현 기술서 (테스트/디버깅용)

> 아래는 Phase 38-C (Post Processing Volume System)의 모든 구현 세부사항을 기술한다.
> 디버깅 시 코드 위치와 데이터 흐름을 빠르게 파악하기 위한 참고 문서.

---

### 1. 아키텍처 개요

```
┌─────────────────────────────────────────────────────────────────┐
│  EngineCore.Update()                                            │
│    SceneManager.Update()                                        │
│    PostProcessManager.Update(cameraPos)  ← 블렌딩 수행          │
│      ├─ PostProcessVolume._allVolumes 순회                      │
│      ├─ ComputeDistanceFactor(cameraPos, vol)                   │
│      ├─ 이펙트별 weighted average 계산                           │
│      └─ PostProcessStack.Effects 에 블렌딩 값 직접 적용          │
│                                                                 │
│  RenderSystem.RenderScene()                                     │
│    ppActive = PostProcessManager.Instance?.IsPostProcessActive  │
│    ├─ ppActive=true  → PostProcessStack.Execute() (이펙트 실행)  │
│    └─ ppActive=false → PostProcessStack.BlitToSwapchain() 직접   │
└─────────────────────────────────────────────────────────────────┘
```

**핵심 원리**: PostProcessManager는 매 프레임 Volume들의 블렌딩 결과를 계산해서 PostProcessStack의 Effect 인스턴스(BloomEffect, TonemapEffect)의 프로퍼티를 **직접 덮어쓴다**. RenderSystem은 `IsPostProcessActive`만 보고 이펙트 실행 여부를 결정한다.

---

### 2. 파일별 상세 구조

#### 2.1 PostProcessProfile (`src/IronRose.Engine/RoseEngine/PostProcessProfile.cs`, 61줄)

```
PostProcessProfile
  ├─ name: string = "Default PP"
  ├─ effects: Dictionary<string, EffectOverride>
  │    key = effect.Name (예: "Bloom", "Tonemap")
  │    value = EffectOverride
  ├─ TryGetEffect(effectName) → EffectOverride?
  └─ GetOrAddEffect(effectName) → EffectOverride (없으면 생성)

EffectOverride
  ├─ effectName: string
  ├─ enabled: bool = true
  ├─ parameters: Dictionary<string, float>
  │    key = param DisplayName (EffectParam 어트리뷰트의 이름)
  │    예: "Threshold", "Soft Knee", "Intensity", "Exposure", "White Point"
  ├─ TryGetParam(paramName) → bool + out float
  └─ SetParam(paramName, float)
```

**중요**: 파라미터 키는 `[EffectParam("Display Name")]`의 DisplayName과 일치해야 한다.
- BloomEffect: `"Threshold"`, `"Soft Knee"`, `"Intensity"`
- TonemapEffect: `"Exposure"`, `"Saturation"`, `"Contrast"`, `"White Point"`, `"Gamma"`

#### 2.2 PostProcessProfileImporter (`src/IronRose.Engine/AssetPipeline/PostProcessProfileImporter.cs`, 140줄)

**TOML 포맷** (.ppprofile):
```toml
[Bloom]
enabled = true
Threshold = 0.8
"Soft Knee" = 0.5
Intensity = 0.5

[Tonemap]
enabled = true
Exposure = 1.5
Saturation = 1.6
Contrast = 1.0
"White Point" = 10.0
Gamma = 1.2
```

- `Import(path, meta)` → 파일 → PostProcessProfile 파싱
- `Export(profile, path)` → PostProcessProfile → TOML 파일 저장
- `WriteDefault(path)` → Bloom+Tonemap 기본값 프로파일 생성

**AssetDatabase 등록**:
- `AssetDatabase.cs`: SupportedExtensions에 `".ppprofile"`, `_ppProfileImporter` 필드, Load<T> case "PostProcessProfileImporter", `_ppProfileToGuid` GUID 추적
- `RoseMetadata.cs`: InferImporter()에 `.ppprofile` → `["type"] = "PostProcessProfileImporter"`

#### 2.3 PostProcessVolume (`src/IronRose.Engine/RoseEngine/PostProcessVolume.cs`, 73줄)

```
PostProcessVolume : MonoBehaviour
  ├─ static _allVolumes: List<PostProcessVolume>  (자기등록 패턴)
  ├─ blendDistance: float = 0  (Inspector에서 편집)
  ├─ weight: float = 1         (Inspector에서 편집)
  ├─ profile: PostProcessProfile?  (Inspector 에셋 참조)
  ├─ profileGuid: string?  [HideInInspector] (직렬화용)
  ├─ OnAddedToGameObject() → _allVolumes.Add(this)
  ├─ OnComponentDestroy() → _allVolumes.Remove(this)
  ├─ GetInnerBounds() → Bounds  (BoxCollider 월드 공간)
  └─ GetInflatedBounds() → Bounds  (inner + blendDistance*2 확장)
```

**필수 전제**: PostProcessVolume은 **반드시 BoxCollider와 함께** 사용해야 한다.
- PostProcessManager가 `vol.gameObject.GetComponent<BoxCollider>()`로 충돌체를 찾음
- BoxCollider가 없으면 해당 Volume은 무시됨 (line 45-46)

**자기등록 흐름**:
```
AddComponent<PostProcessVolume>()
  → MonoBehaviour 생성자
  → OnAddedToGameObject()
    → _allVolumes.Add(this)

RemoveComponent / Destroy
  → OnComponentDestroy()
    → _allVolumes.Remove(this)
```

#### 2.4 PostProcessManager (`src/IronRose.Engine/PostProcessManager.cs`, 173줄)

**초기화**: `EngineCore.InitPhysics()` → `new PostProcessManager()` + `.Initialize()`
- Initialize()에서 `Instance = this` 설정 (싱글톤)

**매 프레임 Update(Vector3 cameraPos) 흐름**:

```
1. RenderSettings.postProcessing (PostProcessStack) null 체크
   → null이면 IsPostProcessActive = false, return

2. Volume 순회 (PostProcessVolume._allVolumes):
   for each vol:
     ├─ profile == null 또는 weight <= 0 → skip
     ├─ gameObject inactive → skip
     ├─ BoxCollider 없음 → skip
     ├─ ComputeDistanceFactor(cameraPos, vol) → 0~1
     │   ├─ inner bounds Contains(cameraPos) → 1.0
     │   ├─ blendDistance == 0 && 외부 → 0.0
     │   └─ 경계 영역 → 1.0 - (dist / blendDistance), linear
     ├─ distFactor <= 0 → skip
     └─ effectiveWeight = vol.weight * distFactor
        → activeVolumes에 추가, totalWeight 누적

3. activeVolumes 비어있음 → IsPostProcessActive = false, return

4. IsPostProcessActive = true

5. 이펙트별 블렌딩 (stack.Effects 순회):
   for each effect (예: BloomEffect, TonemapEffect):
     a. neutralValues = effect.GetNeutralValues()
        예: Bloom → {Threshold:10, Soft Knee:0, Intensity:0}
     b. blendedValues 초기화: 모든 키 = 0
     c. Volume 순회:
        ├─ ov = vol.profile.TryGetEffect(effect.Name)
        ├─ ov != null && ov.enabled:
        │   각 파라미터: blendedValues[name] += (ov값 또는 neutral) * ew
        └─ ov == null 또는 !ov.enabled:
            각 파라미터: blendedValues[name] += neutral * ew
     d. weighted average: blendedValues[name] /= totalWeight
     e. effect 인스턴스에 적용:
        param.SetValue(blendedVal)  ← 직접 프로퍼티 덮어쓰기
     f. effect.Enabled = anyEnabled (하나라도 enabled ov가 있으면 true)
```

**ComputeDistanceFactor 상세**:
```
GetInnerBounds() = BoxCollider 월드 bounds (scale 반영)

inner bounds Contains(cameraPos)? → return 1.0
blendDistance <= 0 && 외부?        → return 0.0
dist = sqrt(innerBounds.SqrDistance(cameraPos))
dist >= blendDistance?             → return 0.0
else                               → return 1.0 - dist/blendDistance
```

#### 2.5 RenderSystem guard (`src/IronRose.Engine/RenderSystem.cs`, 1579-1658행)

```csharp
bool ppActive = PostProcessManager.Instance?.IsPostProcessActive ?? false;

// FSR 경로:
if (fsrActive) {
    var postProcessResult = ppActive
        ? ctx.PostProcessStack?.ExecuteEffectsOnly(cl, ctx.HdrView!)  // PP 이펙트 실행
        : ctx.HdrView!;                                                // PP 건너뜀
    // ... FSR 업스케일 + CAS 샤프닝 ...
    ctx.PostProcessStack?.BlitToSwapchain(cl, blitSource, FinalOutputFramebuffer);
}
// 표준 경로:
else {
    if (ppActive)
        ctx.PostProcessStack?.Execute(cl, ctx.HdrView!, FinalOutputFramebuffer);
            // = ExecuteEffectsOnly + BlitToSwapchain
    else
        ctx.PostProcessStack?.BlitToSwapchain(cl, ctx.HdrView!, FinalOutputFramebuffer);
            // = 감마 보정 blit만 (blit.frag: pow(color, 1/2.2))
}
```

**ppActive=false 일 때의 동작**: 이펙트 없이 HDR → sRGB 감마 보정 blit만 수행. 화면은 정상 출력되되 PP 효과(Bloom, Tonemap)가 적용되지 않음.

#### 2.6 EngineCore 통합 (`src/IronRose.Engine/EngineCore.cs`)

| 위치 | 코드 |
|------|------|
| 필드 (24행) | `private PostProcessManager? _postProcessManager;` |
| 초기화 (478-479행) | `_postProcessManager = new PostProcessManager(); _postProcessManager.Initialize();` |
| Update (177-179행) | `var mainCam = Camera.main; _postProcessManager?.Update(mainCam?.transform.position ?? Vector3.zero);` |
| Shutdown (364행) | `_postProcessManager?.Dispose();` |

**Update 순서**: `SceneManager.Update()` (175행) → `PostProcessManager.Update()` (179행)
- 모든 오브젝트 업데이트 후 블렌딩 수행하므로 카메라 위치가 최신 상태

#### 2.7 SceneSerializer 직렬화 (`src/IronRose.Engine/Editor/SceneSerializer.cs`)

**Save** (SerializeComponent, 300-310행):
```csharp
if (comp is PostProcessVolume ppv)
{
    var fields = new TomlTable
    {
        ["blendDistance"] = (double)ppv.blendDistance,
        ["weight"] = (double)ppv.weight,
    };
    if (!string.IsNullOrEmpty(ppv.profileGuid))
        fields["profileGuid"] = ppv.profileGuid;
    return new TomlTable { ["type"] = "PostProcessVolume", ["fields"] = fields };
}
```

**Load** (DeserializeComponent, case "PostProcessVolume"):
```csharp
var ppv = go.AddComponent<PostProcessVolume>();
ppv.blendDistance = ToFloat(bdVal);
ppv.weight = ToFloat(wVal);
ppv.profileGuid = pgStr;
var profile = db?.LoadByGuid<PostProcessProfile>(pgStr);
ppv.profile = profile;  // null이면 경고 로그
```

**씬 파일 TOML 예시**:
```toml
[[gameObjects]]
name = "PP Volume"

  [[gameObjects.components]]
  type = "BoxCollider"
  [gameObjects.components.fields]
  sizeX = 20.0
  sizeY = 20.0
  sizeZ = 20.0

  [[gameObjects.components]]
  type = "PostProcessVolume"
  [gameObjects.components.fields]
  blendDistance = 5.0
  weight = 1.0
  profileGuid = "abc-123-def"
```

#### 2.8 Inspector UI (`src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs`)

**PostProcessVolume Inspector 표시**:
- `blendDistance`, `weight` → 범용 프로퍼티 리플렉션으로 자동 표시 (float 슬라이더)
- `profile` → AssetNameExtractors에 PostProcessProfile 등록 → DrawAssetReferences()에서 표시
- `profileGuid` → `[HideInInspector]` 어트리뷰트로 숨김

**PostProcessProfile 에셋 드래그드롭** (profile 필드):
1. `.ppprofile` 파일을 Inspector의 profile 슬롯에 드래그
2. `db.Load<PostProcessProfile>(droppedPath)` → profile 설정
3. `db.GetGuidFromPath(droppedPath)` → profileGuid 동기화

**PostProcessProfile 에셋 Inspector** (DrawPostProcessProfileEditor):
- `.ppprofile` 파일 선택 시 Inspector에 표시
- PostProcessStack의 모든 이펙트(Bloom, Tonemap) 파라미터를 프로파일 기준으로 편집
- 값 변경 즉시 `PostProcessProfileImporter.Export()` 호출하여 파일 저장
- ImGui ID 접두사: `"PP"` (슬라이더/드래그 위젯)

#### 2.9 ProjectPanel Create 메뉴 (`src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs`)

- 에셋 목록 우클릭 → Create > Post Process Profile
- 폴더 트리 우클릭 → Create > Post Process Profile
- 모달: "Create PP Profile##Modal" → `CreatePPProfileFile(name)`
- `PostProcessProfileImporter.WriteDefault(path)` 호출 → Bloom + Tonemap 기본값

---

### 3. GetNeutralValues() 정의

Volume 밖에서의 "PP 효과 없음" 상태를 정의. 블렌딩 시 Volume에 없는 이펙트 → neutral value로 참여.

| 이펙트 | 파라미터 | Neutral 값 | 의미 |
|--------|----------|-----------|------|
| **Bloom** | Threshold | 10.0 | 극히 높은 임계값 → 블룸 없음 |
| | Soft Knee | 0.0 | |
| | Intensity | 0.0 | 블룸 강도 0 |
| **Tonemap** | Exposure | 1.0 | 노출 변화 없음 |
| | Saturation | 1.0 | 채도 변화 없음 |
| | Contrast | 1.0 | 대비 변화 없음 |
| | White Point | 4.0 | 기본 화이트 포인트 |
| | Gamma | 2.2 | 표준 sRGB 감마 |

---

### 4. 테스트 시나리오

#### 4.1 기본 동작 확인

1. **Volume 없이 실행**
   - 기대: PP 이펙트 미적용 (BlitToSwapchain만), 화면 정상 출력
   - 확인: `IsPostProcessActive == false` 로그 또는 디버그 표시
   - 주의: Tonemap 이펙트도 실행 안 됨 → HDR→sRGB blit.frag 감마만 적용

2. **Volume 1개 + 카메라 내부**
   - 준비: GameObject 생성, BoxCollider + PostProcessVolume 추가, .ppprofile 연결
   - 기대: distFactor=1.0, 프로파일의 PP 값이 그대로 적용
   - 확인: Bloom/Tonemap 효과가 프로파일 설정대로 보임

3. **Volume 1개 + blendDistance=0 + 카메라 외부**
   - 기대: distFactor=0, Volume 비활성, PP 없음

4. **Volume 1개 + blendDistance > 0 + 카메라 경계**
   - 기대: 카메라가 inner bounds에서 blendDistance만큼 떨어진 범위에서 선형 페이드
   - 확인: 경계 접근 시 PP 효과가 서서히 나타남

#### 4.2 블렌딩 테스트

5. **Volume 2개 겹침**
   - Volume A: Bloom Intensity=1.0, weight=1.0
   - Volume B: Bloom Intensity=0.0, weight=1.0
   - 카메라가 두 Volume 내부: 기대 Bloom Intensity = 0.5 (50:50 평균)

6. **Volume 가중치 차이**
   - Volume A: weight=0.8, Volume B: weight=0.2
   - 둘 다 내부: effectiveWeight A=0.8, B=0.2
   - 기대: A 값에 80% 편향된 블렌딩

#### 4.3 직렬화 테스트

7. **씬 저장/로드**
   - PostProcessVolume이 있는 씬 저장 → 다시 열기
   - 확인: blendDistance, weight, profileGuid 복원
   - 확인: profile 객체가 LoadByGuid로 올바르게 로드됨

8. **프로파일 GUID 변경**
   - Inspector에서 profile 드래그드롭으로 교체
   - 씬 저장 → 로드 → 새 프로파일 적용 확인

#### 4.4 에디터 UI 테스트

9. **Create > Post Process Profile**
   - Project 패널 우클릭 → Create > Post Process Profile
   - 기본 .ppprofile 파일 생성 확인 (Bloom + Tonemap 기본값)

10. **PostProcessProfile 에셋 Inspector**
    - .ppprofile 선택 → Inspector에 이펙트별 파라미터 표시
    - 값 변경 → 파일 즉시 저장 확인

11. **PostProcessVolume Inspector**
    - blendDistance / weight 슬라이더 표시
    - profile 에셋 참조 (드래그드롭 / Browse 버튼)
    - profileGuid 필드 숨김 확인

12. **Add Component > PostProcessVolume**
    - Add Component 팝업에서 "PostProcess" 검색 → 추가 가능

#### 4.5 엣지 케이스

13. **BoxCollider 없는 PostProcessVolume**
    - 기대: 무시됨 (PostProcessManager line 45-46)

14. **profile=null인 PostProcessVolume**
    - 기대: 무시됨 (PostProcessManager line 42)

15. **비활성 GameObject의 Volume**
    - 기대: 무시됨 (PostProcessManager line 43)

16. **Camera.main == null**
    - 기대: cameraPos = Vector3.zero로 폴백 (EngineCore line 178-179)

---

### 5. 디버깅 가이드

#### 5.1 PP가 전혀 적용되지 않을 때

체크리스트 (PostProcessManager.Update 흐름 순서대로):

```
□ RenderSettings.postProcessing != null?
  → PostProcessStack이 RenderSystem에서 생성되었는지 확인
  → RenderSystem 초기화 시 PostProcessStack.AddEffect(new BloomEffect()) 호출 확인

□ PostProcessVolume._allVolumes.Count > 0?
  → Volume이 씬에 존재하는지 (AddComponent → OnAddedToGameObject → _allVolumes.Add)

□ vol.profile != null?
  → .ppprofile 에셋이 Volume에 연결되었는지
  → 씬 로드 시 LoadByGuid<PostProcessProfile> 성공했는지

□ vol.weight > 0?
  → Inspector에서 weight가 0이 아닌지

□ vol.gameObject.activeSelf == true?
  → GameObject가 활성인지

□ vol.gameObject.GetComponent<BoxCollider>() != null?
  → Volume과 같은 GameObject에 BoxCollider가 있는지

□ ComputeDistanceFactor > 0?
  → 카메라가 BoxCollider 안(또는 blendDistance 범위 내)에 있는지
  → innerBounds.Contains(cameraPos) 또는 dist < blendDistance

□ IsPostProcessActive == true?
  → 위 조건을 모두 만족하면 true가 됨

□ RenderSystem에서 ppActive == true?
  → PostProcessManager.Instance?.IsPostProcessActive
```

**디버그 로그 추가 위치**:
```csharp
// PostProcessManager.Update() 시작 부분에 추가:
Debug.Log($"[PPM] Volumes={PostProcessVolume._allVolumes.Count}, stack={stack?.Effects.Count}");

// activeVolumes 루프 후:
Debug.Log($"[PPM] Active volumes={activeVolumes.Count}, totalWeight={totalWeight}");

// ComputeDistanceFactor에서:
Debug.Log($"[PPM] Vol={vol.gameObject.name}, dist={dist}, blendDist={vol.blendDistance}, factor={factor}");
```

#### 5.2 블렌딩 값이 이상할 때

```
□ 파라미터 이름 불일치?
  → EffectOverride.parameters 키 == EffectParam DisplayName
  → 예: "Soft Knee" (공백 포함) vs "SoftKnee" (틀림)
  → .ppprofile TOML에서 키 이름 확인

□ Neutral values가 올바른지?
  → BloomEffect.GetNeutralValues() / TonemapEffect.GetNeutralValues() 확인
  → Volume에 이펙트가 없으면 neutral 값으로 참여

□ effect.Enabled 상태?
  → anyEnabled: 하나라도 enabled override가 있으면 true
  → 모든 Volume의 해당 이펙트가 disabled면 effect.Enabled = false → 렌더링 안 됨
```

#### 5.3 씬 로드 후 Volume이 작동하지 않을 때

```
□ SceneSerializer가 PostProcessVolume을 직렬화했는지?
  → 씬 .scene 파일에서 type = "PostProcessVolume" 확인

□ profileGuid가 저장되었는지?
  → 씬 파일에 profileGuid 필드 존재 확인

□ .ppprofile 파일의 GUID가 올바른지?
  → .ppprofile.rose 메타 파일의 guid 확인
  → 씬 파일의 profileGuid와 일치하는지

□ AssetDatabase가 .ppprofile을 인식하는지?
  → [Asset] 로그에서 .ppprofile 로딩 확인
  → LoadByGuid 실패 시 경고 로그 출력됨
```

#### 5.4 Inspector에서 profile이 표시되지 않을 때

```
□ PostProcessProfile이 AssetNameExtractors에 등록되었는지?
  → ImGuiInspectorPanel.cs의 AssetNameExtractors 딕셔너리 확인

□ profile 프로퍼티가 public get/set인지?
  → PostProcessVolume.profile { get; set; } ← 올바름

□ profileGuid가 [HideInInspector]로 숨겨졌는지?
  → HideInInspectorAttribute에 AttributeTargets.Property 포함 확인
  → Attributes.cs: [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
```

---

### 6. 핵심 코드 위치 빠른 참조

| 기능 | 파일 | 행 |
|------|------|---|
| PostProcessProfile 클래스 | `src/IronRose.Engine/RoseEngine/PostProcessProfile.cs` | 전체 |
| EffectOverride 클래스 | 같은 파일 | 43-60 |
| PostProcessVolume 컴포넌트 | `src/IronRose.Engine/RoseEngine/PostProcessVolume.cs` | 전체 |
| _allVolumes 자기등록 | 같은 파일 | 27-35 |
| GetInnerBounds | 같은 파일 | 58-71 |
| PostProcessManager 싱글톤 | `src/IronRose.Engine/PostProcessManager.cs` | 15 |
| Update(cameraPos) | 같은 파일 | 27-133 |
| ComputeDistanceFactor | 같은 파일 | 141-159 |
| Bloom GetNeutralValues | `src/IronRose.Rendering/PostProcessing/BloomEffect.cs` | 51-56 |
| Tonemap GetNeutralValues | `src/IronRose.Rendering/PostProcessing/TonemapEffect.cs` | 43-50 |
| PostProcessEffect.GetParameters | `src/IronRose.Rendering/PostProcessing/PostProcessEffect.cs` | 45-68 |
| PostProcessEffect.GetNeutralValues (기본) | 같은 파일 | 75-88 |
| RenderSystem ppActive guard | `src/IronRose.Engine/RenderSystem.cs` | 1579-1658 |
| EngineCore PostProcessManager 초기화 | `src/IronRose.Engine/EngineCore.cs` | 478-479 |
| EngineCore Update 호출 | 같은 파일 | 177-179 |
| EngineCore Dispose | 같은 파일 | 364 |
| SceneSerializer Save Volume | `src/IronRose.Engine/Editor/SceneSerializer.cs` | 300-310 |
| SceneSerializer Load Volume | 같은 파일 | case "PostProcessVolume" |
| Inspector AssetNameExtractors | `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs` | AssetNameExtractors dict |
| Inspector PP 드래그드롭 | 같은 파일 | PostProcessProfile 드래그드롭 분기 |
| Inspector DrawPostProcessProfileEditor | 같은 파일 | DrawPostProcessProfileEditor 메서드 |
| ProjectPanel Create PP Profile | `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs` | CreatePPProfileFile 메서드 |
| .ppprofile Import/Export | `src/IronRose.Engine/AssetPipeline/PostProcessProfileImporter.cs` | 전체 |
| AssetDatabase .ppprofile 지원 | `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` | _ppProfileImporter, case "PostProcessProfileImporter" |
| HideInInspector Property 지원 | `src/IronRose.Engine/RoseEngine/Attributes.cs` | 8-9 |
