# IronRose 정적 분석 보고서 — 중복 / 보일러플레이트 / 복잡도

- 분석 일자: 2026-04-18
- 분석 대상: `src/IronRose.Engine/**` 전역 (Editor core · Undo · CLI · Inspector · UI · Gizmo · SceneView · AssetPipeline · RenderSystem)
- 분석 범위는 **정적 분석만**. 런타임 재현이나 실제 수정은 수행하지 않음
- 심각도 표기: **H** (High · 지금 버그의 온상) / **M** (Medium · 누적되면 흔들림) / **L** (Low · 여유 있을 때 정리)

---

## 0. 요약

네 영역을 병렬 분석한 결과, 공통으로 반복되는 주제는 다음과 같다.

1. **동일 도메인 개념이 여러 벌로 존재**
   - 씬 스냅샷 저장 경로(PlayMode vs PrefabMode), 선택 저장소(GameObject vs Asset), 드래그 상태머신(3D/2D UI/RectGizmo), RenderSystem ↔ SceneViewRenderer의 `DrawMesh`/`PrepareMaterial`, TOML 기반 Importer 네 벌
2. **반복되는 5~10줄 정형 패턴**
   - CLI 핸들러의 `인자검증→ID파싱→GO탐색→main thread→isDirty→JsonOk` 6단계 (20건+)
   - UI 컴포넌트의 히트테스트+이벤트 + try/catch 보일러플레이트 (5~7건)
   - Undo Action의 `(oldValue, newValue, Apply)` 3중 필드 패턴 (11건)
   - Importer의 `meta.TryGet(...) ?? default` 옵션 읽기 (다수)
   - Material/Importer/Inspector의 `BeginEdit → widget → EndEdit → Record` 보일러 (10건+)
3. **흐름이 섞여서 위험한 곳**
   - RenderSystem.cs 2017줄 한 파일의 pass 순서 암묵 의존성
   - RoseCache 압축 3단계 폴백(CLI→GPU→CPU)의 goto + 정적 필드 상태 엉킴
   - Prefab/Canvas Edit Mode 진입·이탈 시 Undo 스택·씬 스냅샷·ID 리맵 타이밍
   - CLI `ExecuteOnMainThread`의 5초 블로킹 + screenshot 같은 지연 명령의 응답 계약 불일치
   - SceneView의 Camera/3D Gizmo/UI Gizmo/RectGizmo/Collider 입력 우선순위가 암묵 if/else 체인

아래부터는 관점별로 모든 발견을 모아둔다. 각 항목 뒤의 `[영역:…]` 태그는 해당 발견이 나온 코드 영역을 의미한다(Editor / CLI-Inspector / AssetPipeline-Render / UI-Gizmo).

---

## 1. 비슷한 일을 하는 시스템 중복

### [H] 씬 스냅샷 저장 경로가 두 벌로 이원화 [영역: Editor]
- 위치: [EditorPlayMode.cs:39](src/IronRose.Engine/Editor/EditorPlayMode.cs#L39) `_savedSceneToml` / [EditorState.cs:101](src/IronRose.Engine/Editor/EditorState.cs#L101) `SavedSceneSnapshot`
- Play 진입과 Prefab Edit 진입이 `SceneSerializer.SaveToString()`을 각자 보관해두고, 복원은 모두 `SceneSerializer.LoadFromString()`으로 한다. 저장 위치와 클리어 책임이 서로 다른 모듈에 흩어져 있어 모드 동시 전환 시 상호작용이 추론하기 어렵다. `CleanupPrefabEditMode()`와 `Exit()`에 정리 코드가 중복됨.
- 정리 방향: "씬 임시 체크포인트"를 단일 `struct SceneCheckpoint` 타입으로 뽑고, Play/Prefab/Canvas 모드가 모두 이를 사용.

### [H] CLI 3D 명령과 UI 명령의 GO 조회/부모 설정 패턴 이중화 [영역: CLI-Inspector]
- 위치: [CliCommandDispatcher.cs:505-573](src/IronRose.Engine/Cli/CliCommandDispatcher.cs#L505-L573), [CliCommandDispatcher.UI.cs:57-327](src/IronRose.Engine/Cli/CliCommandDispatcher.UI.cs#L57-L327)
- UI 생성 10개 핸들러가 `int.TryParse → FindGameObjectById → parent null 체크 → new GameObject → transform.SetParent → isDirty = true → JsonOk({id, name})` 흐름을 각자 구현. 3D `go.create_primitive`, `go.duplicate`도 동일 흐름이다. 에러 메시지 문구조차 핸들러마다 미세하게 다름(`"Parent not found: {id}"` vs `"GameObject not found: {id}"`).
- 정리 방향: `CreateChildGameObject(int parentId, string defaultName) → (GameObject?, string? error)` 헬퍼 하나로 부모 탐색/검증을 통일.

### [H] `material.set_color / set_metallic / set_roughness / set_blend_mode` 4개가 동일 [영역: CLI-Inspector]
- 위치: [CliCommandDispatcher.cs:1212-1335](src/IronRose.Engine/Cli/CliCommandDispatcher.cs#L1212-L1335)
- 4개 핸들러가 `FindGameObjectById → GetComponent<MeshRenderer> → null체크 → 값 설정 → SaveMaterialToDisk → isDirty → JsonOk` 구조를 거의 그대로 반복. `light.set_*`, `camera.set_*` 등 유사 패턴 8건 추가.
- 정리 방향: `ApplyMaterialChange(args, Action<Material>)` 헬퍼로 find/validate/apply/save를 합치고 set 계열을 래핑.

### [H] Importer마다 독립 구현된 "TOML 파싱 → 객체 생성" 루프 [영역: AssetPipeline]
- 위치: [AnimationClipImporter.cs:56](src/IronRose.Engine/AssetPipeline/AnimationClipImporter.cs#L56), [RendererProfileImporter.cs:53](src/IronRose.Engine/AssetPipeline/RendererProfileImporter.cs#L53), [PostProcessProfileImporter.cs:50](src/IronRose.Engine/AssetPipeline/PostProcessProfileImporter.cs#L50), [MaterialImporter.cs:26](src/IronRose.Engine/AssetPipeline/MaterialImporter.cs#L26)
- 4개 Importer 모두 `File.Exists → TomlConfig.LoadFile → null 체크 → ParseXxx(config,path) → name = FileNameWithoutExtension`로 동일. `Export()` / `WriteDefault()` 정적 메서드도 3개가 동일 패턴 반복.
- 정리 방향: `abstract TomlImporterBase<T>` 기반 클래스. 파생은 `ParseFromConfig(TomlConfig, path)`만 구현.

### [H] RenderSystem의 `DrawMesh` / `PrepareMaterial` ↔ SceneViewRenderer 동명 메서드 중복 [영역: AssetPipeline-Render]
- 위치: [RenderSystem.cs:1756](src/IronRose.Engine/RenderSystem.cs#L1756), [RenderSystem.cs:1777](src/IronRose.Engine/RenderSystem.cs#L1777), [SceneViewRenderer.cs:676](src/IronRose.Engine/Rendering/SceneViewRenderer.cs#L676), [SceneViewRenderer.cs:709](src/IronRose.Engine/Rendering/SceneViewRenderer.cs#L709)
- 메인과 SceneView의 DrawMesh가 각각 유니폼 구조(`TransformUniforms` vs `SceneViewTransformUniforms`), `GetOrCreateResourceSet` 캐시 딕셔너리(3슬롯 vs 1슬롯)를 따로 보유. Shadow/모션 버퍼용 `PrevViewProjection`은 메인에만 있음.
- 정리 방향: `MeshDrawHelper` 정적 헬퍼로 공통 흐름 추출, 유니폼은 파라미터로 주입.

### [H] Gizmo 3종의 드래그 루프 독립 구현 [영역: UI-Gizmo]
- 위치: [TransformGizmo.cs:133-293](src/IronRose.Engine/Editor/SceneView/TransformGizmo.cs#L133-L293), [UITransformGizmo2D.cs:137-331](src/IronRose.Engine/Editor/SceneView/UITransformGizmo2D.cs#L137-L331), [RectGizmoEditor.cs:88-137](src/IronRose.Engine/Editor/SceneView/RectGizmoEditor.cs#L88-L137)
- `_isDragging` 플래그 → 마우스 릴리즈 감지 → delta 적용 → `EndXxxDrag()` Undo 기록 패턴이 5회 이상. UITransformGizmo2D는 내부에서도 Translate/Rotate/Scale이 각자 복사.
- 정리 방향: `DragSession<TStartState>` 제네릭으로 begin/process/end/record 추상화. 각 Gizmo는 delta 계산 람다만.

### [H] `ColorToU32(Color)` 7개 파일에 개별 복사 [영역: UI-Gizmo]
- 위치: UIToggle/UISlider/UIInputField/UIPanel/UIScrollView/UIImage/UIText
- 동일한 `Math.Clamp → byte 캐스트 → RGBA shift` 구현이 **23회 정의, 62회 호출**. RGBA/ABGR 혼동 버그의 단골 원인.
- 정리 방향: `CanvasRenderer` 또는 `UIColor` 정적 헬퍼에 단일 구현.

### [M] PrefabEditMode / CanvasEditMode — 공통 추상화 없이 평행 구현 [영역: Editor]
- 위치: [PrefabEditMode.cs](src/IronRose.Engine/Editor/PrefabEditMode.cs), [CanvasEditMode.cs](src/IronRose.Engine/Editor/CanvasEditMode.cs)
- 양쪽 모두 `Enter/Exit`, `EditorState.IsEditingXxx` bool+id, `EditorSelection.Clear()` 호출, 카메라/씬 복원을 각자 구현. Canvas는 카메라 복원을 ImGuiOverlay에 위임, Prefab은 씬 스냅샷으로 복원 — 비대칭.
- 정리 방향: `IEditorMode { Enter; Exit; Cleanup; }` + 활성 모드 스택 단일 관리.

### [M] EditorSelection / EditorAssetSelection — 거의 동일한 멀티셀렉션 두 벌 [영역: Editor]
- 위치: [EditorSelection.cs](src/IronRose.Engine/Editor/EditorSelection.cs), [EditorAssetSelection.cs](src/IronRose.Engine/Editor/EditorAssetSelection.cs)
- 둘 다 `List + HashSet + SelectionVersion(long) + Clear/Select/ToggleSelect/SetSelection`. Asset 쪽은 이벤트/Normalize가 추가된 정도.
- 정리 방향: 제네릭 `EditorSelectionStore<T>` 추출.

### [M] Inspector의 `DrawComponentFields` / `DrawMultiComponentFields` 쌍 [영역: CLI-Inspector]
- 위치: [ImGuiInspectorPanel.cs:4158-4209](src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs#L4158-L4209), [ImGuiInspectorPanel.cs:1536-1570](src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs#L1536-L1570)
- 필드 필터(`IsLiteral`, `_is` prefix, `SerializeField`/`HideInInspector`, `AssetNameExtractors`), `HeaderAttribute`/`RangeAttribute`/`TooltipAttribute` 읽기 등이 단일/다중 선택 두 메서드에 각각 복사. `intDropdown`은 다중만, `DrawOverrideMarker`는 단일만 지원 — 일관성 깨짐.
- 정리 방향: `FieldInfo → FieldMeta` 레코드로 메타 수집 단일화, 단일/다중 드로어는 결과만 소비.

### [M] "Edit Collider" / "Edit Canvas" 버튼 렌더링 인라인 반복 [영역: CLI-Inspector]
- 위치: [ImGuiInspectorPanel.cs:879-912](src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs#L879-L912)
- `bool editing → PushStyleColor(green) → Button → PopStyleColor` 흐름을 두 번 인라인. 향후 편집 모드 추가 시 3번째 복사 위험.
- 정리 방향: `DrawEditModeToggleButton(label, isActive, toggle)` 헬퍼.

### [M] CanvasRenderer 트리 순회 로직 4회 중복 [영역: UI-Gizmo]
- 위치: [CanvasRenderer.cs](src/IronRose.Engine/RoseEngine/CanvasRenderer.cs) `RenderNode:147-230`, `HitTestNode:409-446`, `HitTestAllNode:335-372`, `RectTestNode:448-486`
- 네 메서드 모두 `activeInHierarchy 체크 → GetWorldRect → screenRect 계산 → ScrollView offset 적용 → 자식 재귀`. ScrollView offset이 4곳 모두에 박혀 있다.
- 정리 방향: `TraverseTree(Action<GameObject, Rect, float> visitor)` Visitor 패턴.

### [M] UI 5종의 히트테스트 inRect 조건식 각자 구현 [영역: UI-Gizmo]
- 위치: UIButton:53-55 / UIToggle:68-70 / UISlider:100-101 / UIScrollView:55-56 / UIInputField:94-95
- `mousePos.X >= screenRect.x && ...`를 5번 복사. `<` vs `<=` 혼용.
- 정리 방향: `Rect.Contains(float,float)` 확장 메서드로 단일화.

### [M] `RoseCache` 매직 바이트 + BC6H sentinel 상수의 이중 정의 [영역: AssetPipeline-Render]
- 위치: [RoseCache.cs:392-440](src/IronRose.Engine/AssetPipeline/RoseCache.cs#L392-L440), `RoseCache.cs:105-106`, `TextureCompressionFormatResolver.BC6HVirtualId`
- 에셋 타입 바이트(1=Mesh, 2=Texture)와 `BC6HVirtualId=1000`이 두 파일에 각각 const로 있음. "반드시 일치해야 함" 주석만.
- 정리 방향: `RoseCacheConstants` 정적 클래스 또는 `enum`으로 단일화.

### [M] `AssetDatabase.ScanAssets` / `ScanAssetsSubtree` 루프 본체 [영역: AssetPipeline-Render]
- 위치: [AssetDatabase.cs:149-170](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L149-L170), [AssetDatabase.cs:192-215](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L192-L215)
- `foreach ext → GetFiles(AllDirectories) → dotfile 필터 → LoadOrCreate → guid/sub-asset 등록 → PumpWindowEvents` 동일. 차이는 Clear 먼저 여부뿐.
- 정리 방향: `ScanDirectory(string, bool clearFirst)` 하나로 병합.

### [L] `EditorCommand.ParseValue`가 기반/파생에 두 벌 [영역: Editor]
- 위치: [EditorCommand.cs:14, 56](src/IronRose.Engine/Editor/EditorCommand.cs)
- `new` 키워드로 파생에서 Vector3/Color 지원 버전 숨김. 새 Command가 Vector3/Color를 쓰려면 또 복사할 위험.
- 정리 방향: 기반 클래스 `ParseValue`를 Vector3/Color 지원 단일 버전으로 통합.

---

## 2. 보일러플레이트 반복 — 공통화로 얻는 이익이 큰 곳

### [H] CLI 핸들러의 "인자검증 → ID파싱 → GO탐색 → 실행 → isDirty → JsonOk" 6단계 반복 (20건+) [영역: CLI-Inspector]
- 위치: [CliCommandDispatcher.cs](src/IronRose.Engine/Cli/CliCommandDispatcher.cs) `transform.set_position(607)`, `set_rotation(638)`, `set_scale(667)`, `translate(1645)`, `rotate(1675)`, `set_local_position(1770)`, `light.set_*`, `camera.set_*`, `material.set_*` 등
- 각 핸들러가 `if (args.Length < N) JsonError("Usage: ...") → if (!int.TryParse) JsonError → ExecuteOnMainThread(() => { var go = FindGameObjectById; if (go == null) JsonError; try { ... } catch (Exception ex) { JsonError } })`를 반복. 일부 핸들러는 try-catch 누락으로 예외 안전성에 편차 있음.
- 정리 방향: `WithGameObject(args, argIdx, Func<GameObject,string> body)` 래퍼로 ID파싱+탐색+null체크+메인스레드 전환을 하나로.

### [H] UI 컴포넌트의 인터랙션 보일러플레이트 [영역: UI-Gizmo]
- 위치: [UIButton.cs:52-67](src/IronRose.Engine/RoseEngine/UI/UIButton.cs#L52-L67), [UIToggle.cs:65-78](src/IronRose.Engine/RoseEngine/UI/UIToggle.cs#L65-L78), [UISlider.cs:97-136](src/IronRose.Engine/RoseEngine/UI/UISlider.cs#L97-L136), [UIInputField.cs:91-121](src/IronRose.Engine/RoseEngine/UI/UIInputField.cs#L91-L121)
- 모든 인터랙티브 컴포넌트가 `!IsInteractive return → GetMousePos → inRect → IsHitOrAncestorOfHit → try/catch invoke` 5단계를 반복. 조건 표현이 컴포넌트별로 미묘하게 달라(UIButton: else 블록, UIToggle: early return, UIInputField: Play Mode 추가 조건) 동작 불일치 여지.
- 정리 방향: `UIInteractionContext.CanInteract(rect, go)` + `SafeInvoke(Action)` 확장, 또는 `UIComponent` 추상 기반에서 `OnInteract(delta)` 훅만 노출.

### [H] Undo Action의 `old/new/Apply` 3중 필드 패턴 (11파일) [영역: Editor]
- 위치: [Undo/Actions/](src/IronRose.Engine/Editor/Undo/Actions) — SetPropertyAction, SetActiveAction, SetTransformAction, RenameGameObjectAction, MaterialPropertyUndoAction, AnimationClipUndoAction 등
- 모든 "값 변경" Action이 `(_oldValue, _newValue)` 쌍 + `Undo() → Apply(_old)` + `Redo() → Apply(_new)` + `Apply(T)`를 각 파일에 복사. 단순 케이스(SetActiveAction의 Apply(bool) 한 줄)까지 30-50줄씩 파일당 작성.
- 정리 방향: `ReversibleAction<T>` 추상 베이스(Description, old, new, Apply(T))로 축약. `SetActiveAction`/`RenameGameObjectAction` 같은 단순 케이스는 이미 리플렉션 기반 `SetPropertyAction`에 통합 가능성 검토.

### [H] `SceneManager.GetActiveScene().isDirty = true` 직접 호출 30곳+ [영역: Editor]
- 위치: Undo/Actions/* 전체, ImGuiInspectorPanel, ImGuiHierarchyPanel, ImGuiOverlay, GameObjectFactory, EditorClipboard, SceneView/*, CLI dispatcher 전반
- `UndoSystem`에 `MarkSceneDirty()`가 이미 있음에도 각 Action의 Undo/Redo 본문에서 직접 `isDirty = true`. `UndoSystem.PerformUndo/Redo` 진입 시 일괄 처리하면 불필요.
- 정리 방향: UndoSystem을 신뢰하고 Action 내부 플래그 설정 제거.

### [H] Material/Importer 필드 드로어의 `BeginEdit → widget → SaveMatFile → IsItemDeactivatedAfterEdit → EndEdit → Record` 반복 [영역: CLI-Inspector]
- 위치: [ImGuiInspectorPanel.cs:3286-3366](src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs#L3286-L3366) `DrawMatFloat/DrawMatFloatRange/DrawMatVec2`, color/emission 블록 3122-3177 등 5건
- 10줄의 동일 패턴이 3-5회. 특히 `DrawMatFloat`에서 `SaveMatFile()`이 `changed` 분기와 `EndEdit` 분기 양쪽에서 호출되어 **reimport 이중 트리거 → 텍스처 preview 캐시 불필요 무효화**가 발생.
- 정리 방향: `DrawMatProperty<T>(key, defaultValue, Func<string, ref T, bool> widget, Action? postApply)` 제네릭 헬퍼.

### [H] ImGuiProjectPanel의 Create/Rename/Delete 팝업 6회 반복 [영역: UI-Gizmo]
- 위치: [ImGuiProjectPanel.cs:70-140](src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs#L70-L140) 필드, [ImGuiProjectPanel.cs:571-703](src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs#L571-L703) 팝업 블록
- Folder/Material/AnimClip/RendererProfile/PPProfile/Rename/Delete 7종마다 `_openCreateXxxPopup + _newXxxName + _createXxxTargetFolder` 3필드 세트 × 7 + 팝업 블록 × 6. 새 에셋 타입 추가 시 일괄 복사 필요.
- 정리 방향: `CreateAssetPopupState` 레코드 + `DrawCreatePopup(state, factory)` 제네릭 헬퍼.

### [H] RenderSystem 렌더러 순회 보일러 (`DrawOpaqueRenderers` / `DrawTransparentRenderers` / `DrawAllRenderers`) [영역: AssetPipeline-Render]
- 위치: [RenderSystem.Draw.cs:29-146](src/IronRose.Engine/RenderSystem.Draw.cs#L29-L146)
- 세 메서드가 `foreach MeshRenderer._allRenderers → !enabled 필터 → _isEditorInternal 필터 → GetComponent<MeshFilter> → filter?.mesh == null → UploadToGPU → VertexBuffer==null` 7단계를 반복. Material override 판별 코드도 3회 복사. `DrawAllTexts`, `DrawAllSprites`까지 포함 총 5건.
- 정리 방향: `IEnumerable<(Mesh, Transform, MaterialUniforms, TextureView?)> CollectDrawCalls(...)` 열거자로 추출, 각 pass는 파이프라인만 교체.

### [H] Importer 옵션 읽기 `meta?.importer.TryGetValue(...) ?? default` [영역: AssetPipeline-Render]
- 위치: [TextureImporter.cs:27, 49, 95](src/IronRose.Engine/AssetPipeline/TextureImporter.cs), FontImporter:17, AnimationClipImporter:69-73 외 다수
- `RoseCache`에는 이미 `GetMetaString` / `GetMetaBool` 헬퍼가 있지만 private이라 Importer가 공유 못함. TextureImporter에서 `max_size`가 두 경로에서 독립 Convert.ToInt32.
- 정리 방향: `RoseMetadata` 확장 메서드로 승격해 모든 Importer가 공유.

### [M] `EditorState.Save()`의 수동 TOML 조합 [영역: Editor]
- 위치: [EditorState.cs:255-302](src/IronRose.Engine/Editor/EditorState.cs#L255-L302)
- `toml += "[editor]\n"`, `toml += $"last_scene = ...\n"` 수동 문자열 이어붙이기. 다른 쪽은 `TomlConfig` API 사용. 따옴표 이스케이프 누락·특수문자 취약.
- 정리 방향: `TomlConfig.SetValue / SaveToFile` API로 통일.

### [M] Importer의 mixed 표시 처리 반복 (`DrawImporterFloat/Int/Bool/Combo`) [영역: CLI-Inspector]
- 위치: [ImGuiInspectorPanel.cs:3992-4116](src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs#L3992-L4116)
- `isMixed = _mixedImporterKeys.Contains(key) → PushStyleColor → widget → changed: 설정+Remove mixed → PopStyleColor` 4메서드에 반복.
- 정리 방향: `DrawImporterWidget(key, Action<string,bool> render)` 공통 래퍼.

### [M] UI Gizmo의 RectTransform 핸들러 "parentId → FindById → GetComponent<RectTransform> → null" (8건) [영역: CLI-Inspector]
- 위치: [CliCommandDispatcher.UI.cs:489](src/IronRose.Engine/Cli/CliCommandDispatcher.UI.cs#L489) `ui.rect.get` 등
- `WithRectTransform(args, Func<RectTransform, GameObject, string> body)` 헬퍼로 통합 가능.

### [M] UITransformGizmo2D Translate/Rotate/Scale 삼중 반복 [영역: UI-Gizmo]
- 위치: [UITransformGizmo2D.cs:113-628](src/IronRose.Engine/Editor/SceneView/UITransformGizmo2D.cs#L113-L628) `UpdateTranslate/UpdateRotate/UpdateScale` + `DrawOverlay` × 3
- 각 메서드가 `selectedGo/rt 체크 → panelW/H 계산 → _isDragging 분기 → hit test → begin drag → cursor` 동일. `DrawOverlay`도 `PushClipRect → draw → PopClipRect`로 3회.
- 정리 방향: `GetCommonContext(out ...)` 공통 추출, 각 도구는 HitTest/Process/Draw만 구현.

### [M] Hierarchy/Project 트리 노드 렌더 패턴 유사 [영역: UI-Gizmo]
- 위치: [ImGuiHierarchyPanel.cs:226-388](src/IronRose.Engine/Editor/ImGui/Panels/ImGuiHierarchyPanel.cs#L226-L388) DrawNode, [ImGuiProjectPanel.cs:729-838](src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs#L729-L838) DrawAssetEntry
- `TreeNodeEx → IsItemClicked → IsMouseDoubleClicked → BeginPopupContextItem → BeginDragDropSource → SetScrollHereY` 동일. 키보드 내비(F2, Ctrl+C/X/V) 로직이 양쪽에 각자 → 동작 편차 발생 쉬움.
- 정리 방향: `EditorTreeNodeWidget<T>` 범용 위젯.

### [M] ResourceLayout/ResourceSet/DeviceBuffer 생성 보일러 (12회+) [영역: AssetPipeline-Render]
- 위치: [RenderSystem.cs:459-820](src/IronRose.Engine/RenderSystem.cs#L459-L820) `Initialize()`, [SceneViewRenderer.cs:115-193](src/IronRose.Engine/Rendering/SceneViewRenderer.cs#L115-L193)
- `factory.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<T>(), Uniform|Dynamic))` 12회+, 레이아웃+셋 페어 6회+.
- 정리 방향: `CreateUniformBuffer<T>(factory)` 제네릭 헬퍼.

### [M] Shadow pass가 매 프레임 ResourceSet 할당/해제 [영역: AssetPipeline-Render]
- 위치: [RenderSystem.Shadow.cs:86-108](src/IronRose.Engine/RenderSystem.Shadow.cs#L86-L108)
- `shadowResourceSet`을 매 프레임 `CreateResourceSet` → 마지막에 `Dispose`. 구성이 고정적인데도 계속 재생성.
- 정리 방향: 멤버 필드로 캐시, 레이아웃 변경 시에만 재생성.

### [L] `DeleteGameObjectAction`의 미니 직렬화기 내장 [영역: Editor]
- 위치: [DeleteGameObjectAction.cs:93-129](src/IronRose.Engine/Editor/Undo/Actions/DeleteGameObjectAction.cs#L93-L129)
- `SceneSerializer.SerializeGameObjectHierarchy()`가 이미 있는데도 `GOSnapshot[]` 별도 구조 + 자체 캡처/복원.
- 정리 방향: 기존 직렬화 API 재사용.

---

## 3. 복잡해서 오류 여지가 큰 흐름

### [H] `RenderSystem.Render()`의 암묵적 pass 순서 의존성 [영역: AssetPipeline-Render]
- 위치: [RenderSystem.cs:1462-1714](src/IronRose.Engine/RenderSystem.cs#L1462-L1714)
- 단일 메서드가 Geometry→Shadow→SSIL→Ambient/IBL→Direct Lights→Skybox→Forward→PostProcess→Blit을 250줄에 걸쳐 순차 실행. 각 pass 간 명시적 "barrier" 없음. Shadow pass 뒤 `cl.SetFramebuffer(ctx.HdrFramebuffer)` + `cl.SetFullViewports()` 복원이 필수인데 누락되면 뷰포트가 섀도우 아틀라스 크기(4096²)로 고정된 채 조용히 렌더된다. FSR 리소스셋 조건부 Dispose/재생성이 Render() 본체에 인라인(1655-1666)으로 박혀 있음. `_activeCtx`가 클래스 멤버로 설정되어 partial 파일 전역이 암묵 의존 → 멀티 뷰포트(GameView + SceneView)에서 호출 순서 실수에 취약.
- 정리 방향: `IRenderPass.Execute(cl, ctx, camera)` 목록으로 분리, Render()는 목록 순회만. `_activeCtx`는 로컬 파라미터.

### [H] GPU 텍스처 압축 3단계 폴백의 상태 엉킴 [영역: AssetPipeline-Render]
- 위치: [RoseCache.cs:444-603](src/IronRose.Engine/AssetPipeline/RoseCache.cs#L444-L603)
- `CompressTexture()`가 CLI → GPU → CPU 순 폴백하는데 `_compressonatorCliPath`, `_gpuCompressor`, `_bc1CpuSupported` 정적 플래그 3개 + `bc1PreFallback`, `bc1RuntimeFallback` 지역 2개가 상호작용. `goto finalizeFormat`이 사용되고, CLI가 Mip 중간에 실패하면 전체 체인을 CPU 재생성. `bc1RuntimeFallback` 분기 누락 시 GPU 업로드 포맷 불일치 크래시. static 필드가 lock 없이 check→write, `AssetWarmupManager`가 백그라운드 Task에서 호출 가능.
- 정리 방향: `ITextureCompressor` 인터페이스 3구현(CliCompressor/GpuCompressor/CpuCompressor)으로 체인 분리. `_bc1CpuSupported`는 `Lazy<bool>`.

### [H] `AssetDatabase.Reimport()` 거대 switch + 롤백 복잡도 [영역: AssetPipeline-Render]
- 위치: [AssetDatabase.cs:694-890](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L694-L890)
- 하나의 메서드에 `_importDepth++ → try { 스냅샷 → Remove → 캐시 무효화 → importerType switch(10 case) → 씬 참조 교체 → succeeded = true } catch { 롤백 }`. case마다 `reimportSucceeded = true`를 수동 세팅해야 하고, `TextAssetImporter`는 "새 인스턴스를 넣지 않는다"는 특수 경로가 주석에만. `_importDepth`가 외부 PushImportGuard와 내부 Reimport가 같은 카운터 공유.
- 정리 방향: `IReimportStrategy.Reimport(path, meta, oldAsset, db)` 인터페이스로 case 분리, Reimport는 오케스트레이터로.

### [H] Prefab Edit Mode 진입/이탈의 Undo 스택 + ID 리맵 타이밍 [영역: Editor]
- 위치: [PrefabEditMode.cs:57-104, 166-226](src/IronRose.Engine/Editor/PrefabEditMode.cs)
- 진입: `UndoSystem.SaveAndClear()` → 씬 클리어 → 프리팹 로드. 이탈: `Clear()` → 씬 복원 → `Restore()` → `BuildRemap()` → `SetIdRemap()`. `BuildRemap()`은 `LoadFromString` 직후 GO 안정 상태를 가정하지만 파괴 플래그 남은 GO 섞임 보장 없음. 중첩 진입 경로의 리맵은 context에 저장, 최초 진입은 `EditorState.SavedGoIdMap`에 null 체크로만 방어(Exit:207). 리맵이 비면 Undo가 조용히 잘못된 GO를 찾는 버그로 이어짐.
- 정리 방향: `LoadFromString → BuildRemap`을 원자적 헬퍼로 묶고, 리맵 실패 시 명시적 경고 + Undo 스택 강제 클리어.

### [H] Script 리로드와 씬 상태 보존 책임 분산 + 데드코드 [영역: Editor]
- 위치: [ScriptReloadManager.cs:321-341](src/IronRose.Engine/ScriptReloadManager.cs#L321-L341) ExecuteReload, `386-437` Save/RestoreHotReloadableState
- 핫리로드 흐름: `_reloadRequested` → 디바운스 → `_reloadDelayFrames=2` → `ExecuteReload` → `BuildScripts` + `MigrateEditorComponents`. Play 종료 후는 콜백으로 연결, 씬 복원 뒤 리로드. `MigrateEditorComponents`는 씬 복원 후 `_components` 리스트 직접 교체 중 `RegisterBehaviour/UnregisterBehaviour` 끼어듦. **`SaveHotReloadableState/RestoreHotReloadableState`는 정의되어 있으나 `ExecuteReload`에서 호출되지 않음 → 데드코드**. 즉 `IHotReloadable` 구현체의 상태 저장이 실제로는 실행되지 않음.
- 정리 방향: 두 메서드를 `ExecuteReload`에 연결하거나 인터페이스 자체 제거. 미사용 경로 정리.

### [H] EditorBridge `_pingAssetPath`의 non-atomic read-clear [영역: Editor]
- 위치: [EditorBridge.cs:110-115](src/IronRose.Engine/Editor/EditorBridge.cs#L110-L115)
- `ConsumePingAssetPath()`: `var path = _pingAssetPath; _pingAssetPath = null; return path;` — `volatile` 아님. 같은 파일의 `_toggleWindowRequested`, `_buildStarted`는 volatile인데 이것만 누락. 엔진·에디터 스레드 사이 ping 소실 가능.
- 정리 방향: `volatile` 선언 또는 `Interlocked.Exchange`.

### [H] CLI `ExecuteOnMainThread`의 5초 블로킹 + 지연 명령 응답 계약 [영역: CLI-Inspector]
- 위치: [CliCommandDispatcher.cs:2730-2737](src/IronRose.Engine/Cli/CliCommandDispatcher.cs#L2730-L2737)
- 파이프 스레드가 `_mainThreadQueue.Enqueue(task)` 후 5초 블로킹. 메인이 Play 진입/모달 팝업으로 Update() 지연 시 타임아웃 오판. `play.enter`가 씬 저장·재로드를 수행하는 동안 CLI가 또 들어오면 큐 드레인 꼬임. **`editor.screenshot`은 `_pendingScreenshotPath` 기록 후 즉시 `ok=true` 반환, 실제 저장은 다음 프레임** → 클라이언트가 응답 직후 파일을 읽으면 부재.
- 정리 방향: 슬로우 명령용 비동기 폴링 프로토콜(요청-응답-완료 3단계). screenshot은 `screenshot.ready`로 완료 확인. 타임아웃은 명령 유형별로.

### [H] Inspector `_undoTracker.BeginEdit / EndEdit` 타이밍 [영역: CLI-Inspector]
- 위치: [ImGuiInspectorPanel.cs:370-437, 474-593, 3122-3177](src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs)
- `DragFloat3Clickable`이 true 시 `BeginEdit → 즉시 값 반영 → IsItemDeactivated 시 EndEdit`. Material은 변경 즉시 `SaveMatFile()` + EndEdit에서 또 `SaveMatFile()` → **reimport 이중 트리거**. Drag 중 GO 선택 변경/씬 로드가 일어나면 BeginEdit은 이전 GO 기준으로 열린 채 EndEdit 호출되지 않아 `_undoTracker` 스테일. RectTransform의 Undo key는 `"RectTransform.AnchoredPos2"` 같은 매직 스트링 — Begin/End 키 불일치 시 Undo가 조용히 누락.
- 정리 방향: Undo key를 `nameof()` 기반 상수화, Draw 진입 시 GO id 변경 감지 → 미결 BeginEdit 자동 취소.

### [H] Inspector 모드 전환 시 에셋/GO 상태 이중 추적 경쟁 [영역: CLI-Inspector]
- 위치: [ImGuiInspectorPanel.cs:162-202](src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs#L162-L202)
- `_lastGoId, _lastGoSelectionVersion, _lastAssetPath, _lastAssetSelectionVersion, _mode` 5필드로 "더 최근 선택"을 매 프레임 비교. `selectedGoId != null && selectedAssetPath != null`이 동시에 참이면 같은 프레임에 두 번 `ClearAssetState()` 발생 가능 → `_hasChanges` 리셋 → **적용 안 된 임포터 변경이 조용히 사라짐**. 두 version이 모두 0으로 시작하면 초기 선택 시 한쪽 누락.
- 정리 방향: `SelectionSnapshot(GoId, GoVersion, AssetPath, AssetVersion)` 단일 레코드 비교. 모드 전환 시 `_hasChanges`가 살아있으면 "저장 안 됨" 경고.

### [H] UITransformGizmo2D 드래그 중 element 추종의 좌표계 혼재 [영역: UI-Gizmo]
- 위치: [UITransformGizmo2D.cs:219](src/IronRose.Engine/Editor/SceneView/UITransformGizmo2D.cs#L219), `269-306`, `405`
- Translate 오버레이는 `rt.lastScreenRect`에서 live pivot 재계산(element 추종). ProcessTranslateDrag는 `_dragStartMousePos`(screen)와 `entry.StartAnchoredPos`(Canvas 로컬)를 혼합해 `cdx = dxScreen / scale`로 변환 후 anchoredPosition에 더함. Rotate는 `StartPivotScreen`(screen)과 `rt.anchoredPosition`(Canvas)이 동시 업데이트, `screenShiftX / scale`이 ImGui Y-down과 맞물려 부호 복잡화. **Canvas 중첩/localScale≠1일 때 scale chain 불일치**. `lastScreenRect`가 이전 프레임 렌더 결과라 드래그 시작 프레임 1프레임 지연. live pivot(Translate) vs frozen pivot(Rotate/Scale의 `_dragGizmoCenter`) 정책 불일치.
- 정리 방향: `CanvasCoordHelper.ScreenToCanvas(pos, canvas)` 단일 변환 경로, pivot 정책을 도구별 플래그로 명시 선언.

### [H] SceneView 입력 우선순위가 암묵 if/else 체인 [영역: UI-Gizmo]
- 위치: [ImGuiOverlay.cs:2438-2456, 2514-2527, 2617-2618](src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs)
- `isRectTool && hasRT → RectGizmoEditor` / `useUI2DGizmo → UITransformGizmo2D` / `IsEditingCollider → ColliderGizmoEditor` / `else → TransformGizmo`. 카메라 잠금은 `gizmoActive = !_cameraActive && _gizmo.IsInteracting` — 3D Gizmo IsInteracting만 체크. **UI2D/Rect Gizmo 드래그 중에도 `_cameraActive`는 별도로 LMB 시작 가능 → race condition**. `rectEditing` 플래그는 `RectGizmoEditor.IsDragging || UITransformGizmo2D.IsDragging`을 OR로 묶되 카메라 락과 독립.
- 정리 방향: `ActiveInputConsumer { None, Camera, Gizmo3D, GizmoUI2D, GizmoRect, Collider }` enum으로 단일 상태 집중, 우선순위는 명시 테이블.

### [H] RectTransform anchor/pivot — GetWorldRect vs offsetMin/offsetMax setter 이중 경로 [영역: UI-Gizmo]
- 위치: [RectTransform.cs:44-68, 106-129](src/IronRose.Engine/RoseEngine/RectTransform.cs), [RectGizmoEditor.cs:231-334](src/IronRose.Engine/Editor/SceneView/RectGizmoEditor.cs#L231-L334)
- `GetWorldRect`는 `anchorMin/Max, anchoredPosition, sizeDelta, pivot` 5필드로 계산. `RectGizmoEditor.ProcessDrag`는 핸들 종류별로 `anchoredPosition / offsetMin / offsetMax` 직접 조작. **offsetMin/offsetMax의 setter는 내부적으로 sizeDelta와 anchoredPosition을 동시 수정**. 코너 핸들(TopRight 등)이 두 setter를 같은 프레임 순차 호출 → 첫 setter의 부작용이 두 번째 setter 입력에 영향. workaround로 `_startAnchoredPos/_startSizeDelta` 매 프레임 복원하지만 순서가 깨지면 프레임 내 상태 오염.
- 정리 방향: handle별로 `anchoredPosition`과 `sizeDelta`를 직접 조작하는 단일 함수로 통일, offsetMin/offsetMax setter 체인을 거치지 않는 역산 공식을 문서화.

### [M] Play 진입 시 CanvasEditMode 강제 종료 후 카메라 복원 누락 가능성 [영역: Editor]
- 위치: [EditorPlayMode.cs:65-68](src/IronRose.Engine/Editor/EditorPlayMode.cs#L65-L68), [CanvasEditMode.cs:80-84](src/IronRose.Engine/Editor/CanvasEditMode.cs#L80-L84)
- `CanvasEditMode.Exit()`이 `EditorState.SavedCanvasCamera*`를 null 클리어. 실제 카메라 복원은 `ImGuiOverlay`가 이 값을 읽어 처리. Play 진입 경로에서 Exit()이 먼저 값을 날리면 카메라가 Canvas 위치에 고정된 채 Play 시작 가능. Stop 후에도 원래 위치가 아닌 Canvas 위치에 남을 수 있음.
- 정리 방향: `CanvasEditMode.Exit()`에서 즉시 카메라 복원, ImGuiOverlay 의존 제거.

### [M] ScriptReloadManager `_reloadDelayFrames=2`가 두 경로에서 설정 [영역: Editor]
- 위치: [ScriptReloadManager.cs:258-261, 295-319](src/IronRose.Engine/ScriptReloadManager.cs)
- 디바운스 만료(`ProcessReload`) + Play 종료(`OnExitPlayMode`)가 같은 필드를 쓴다. 거의 동시 트리거 시 두 번째 대입이 카운트 재설정. 의도는 맞지만 주석·방어코드 없음.
- 정리 방향: `ScheduleReloadAfterDelay()` 단일 메서드로 통합.

### [M] CanvasRenderer.HitTest — 회전/스케일 UI 요소는 AABB만 [영역: UI-Gizmo]
- 위치: [CanvasRenderer.cs:409-446, 234-250](src/IronRose.Engine/RoseEngine/CanvasRenderer.cs)
- `RenderNode`는 `TransformVertices`로 회전/스케일 적용 렌더링하지만 `HitTestNode`는 변환 전 `screenRect` AABB로만 히트테스트. 45° 회전된 UIButton이면 렌더 위치와 히트 영역이 어긋남. Gizmo 위치도 `lastScreenRect` 기준이라 함께 오염.
- 정리 방향: OBB 또는 변환된 코너 기반 테스트, 혹은 `lastScreenRect`를 변환 후 AABB로 업데이트.

### [M] Project 패널 pending 상태 8개 동시 활성화 경쟁 [영역: UI-Gizmo]
- 위치: [ImGuiProjectPanel.cs:119-122, 707-721](src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs)
- `_pendingSelectPath/_pendingSelectCtrl/_pendingSelectShift/_pendingOpenScenePath/_pendingOpenPrefabPath/_pendingOpenAnimPath/_pendingActivateRendererPath/_renameAssetPath` 8개 독립 필드. 드래그 중 F2 시 `_pendingSelectPath`가 드레인되지 않은 채 리네임 팝업이 열릴 수 있음.
- 정리 방향: `AssetInteractionState { Idle, PendingSelect, Dragging, Renaming, Deleting }` 단일 머신.

### [M] CLI에서 Undo 미기록 — Inspector와 일관성 부재 [영역: CLI-Inspector]
- 위치: CliCommandDispatcher.cs 전반 (`go.rename:543`, `transform.set_position:606`, `material.set_color:1212` …)
- 모든 씬 변경 CLI 명령이 `isDirty = true`만 세팅, `UndoSystem.Record()` 호출 없음. Inspector는 동일 작업을 Undo에 기록. CLI로 위치 변경 → Inspector Ctrl+Z → **CLI 변경은 안 되돌아가고 더 이전 Inspector 액션이 롤백** → 예상치 못한 상태.
- 정리 방향: CLI 핸들러에 Undo Action 기록 추가하거나, "CLI는 Undo와 독립" 방침 문서화 + `editor.undo`에 CLI 전용 로그.

### [M] AssetWarmupManager 메인/백그라운드 분리 타이밍 [영역: AssetPipeline-Render]
- 위치: [AssetWarmupManager.cs:87-100](src/IronRose.Engine/AssetWarmupManager.cs#L87-L100)
- 메시는 `Task.Run`으로 백그라운드, 텍스처는 메인 동기. 백그라운드에서 `.rose` 저장 시 `RoseMetadata.OnSaved` 이벤트가 다른 스레드에서 발생 → `AssetDatabase.OnRoseMetadataSaved`가 reimport 예약. `_pendingChanges`는 lock 보호되지만 `_loadedAssets`는 lock 없음. 워밍업 중 FSW가 `.rose` 변경 감지 시 백그라운드 워밍업과 메인스레드 reimport가 같은 에셋을 동시에 처리 가능.
- 정리 방향: 워밍업 경로도 `_suppressedPaths`로 FSW 이벤트 억제, metadata 저장 시 silent 경로 추가.

### [M] RoseCache `_compressonatorCliPath` 초기화 비원자 [영역: AssetPipeline-Render]
- 위치: [RoseCache.cs:651-681](src/IronRose.Engine/AssetPipeline/RoseCache.cs#L651-L681)
- null 체크 후 CLI 경로 탐색 → 쓰기. `GpuTextureCompressor`는 `lock (_lock)` 보호지만 이 필드는 별도 락 없이 static 공유.
- 정리 방향: `Lazy<string>` 또는 `Interlocked.CompareExchange`.

### [L] `CliPipeServer` 단일 연결 + Dispatch 블로킹 결합 [영역: CLI-Inspector]
- 위치: [CliPipeServer.cs:89](src/IronRose.Engine/Cli/CliPipeServer.cs#L89)
- `maxNumberOfServerInstances = 1`. 순차 처리 + 메인스레드 5초 블로킹 → 앞 명령 타임아웃이 뒤 명령의 지연으로 전파. 두 번째 클라이언트는 첫 연결 끊길 때까지 대기.
- 정리 방향: 인스턴스 수를 늘리거나, 빠른 쿼리·느린 변경 레인 분리.

---

## 4. 권장 우선순위 (버그 유발 빈도 / 수정 난이도 기준)

1. **지금 당장 버그의 온상** — 먼저 건드리면 불안정성 즉시 감소
   - [H] `RenderSystem.Render()` pass 분리 (pass별 클래스화)
   - [H] RoseCache 3단계 압축 폴백 → `ITextureCompressor` 체인
   - [H] AssetDatabase `Reimport()` → `IReimportStrategy` 분리
   - [H] Prefab Edit Mode 진입/이탈 + 리맵 원자화
   - [H] CLI `ExecuteOnMainThread` 타임아웃/응답 프로토콜 재설계 (특히 screenshot)
   - [H] SceneView 입력 우선순위 enum 단일화 (카메라 vs 각 Gizmo)
   - [H] UITransformGizmo2D 좌표계 헬퍼 단일화 + pivot 정책 명시

2. **중복/보일러를 먼저 깎으면 1번 작업이 쉬워짐**
   - [H] CLI `WithGameObject` / `WithRectTransform` 래퍼 도입
   - [H] `ColorToU32` 단일화 (UI)
   - [H] UI 컴포넌트 인터랙션 보일러 → `UIComponent` 추상화
   - [H] Undo Action `ReversibleAction<T>` 베이스 + `isDirty` UndoSystem 일원화
   - [H] Inspector `DrawMatProperty<T>` + Undo key 상수화 (SaveMatFile 이중호출 해결)

3. **구조 개선 (시간 있을 때)**
   - [M] EditorSelection 제네릭화
   - [M] IEditorMode 인터페이스로 Prefab/Canvas 모드 추상화
   - [M] TomlImporterBase<T>
   - [M] CanvasRenderer 트리 순회 Visitor
   - [M] Project/Hierarchy 공통 `EditorTreeNodeWidget`
   - [M] `AssetInteractionState` 단일 머신

4. **개별 버그 성격이라 짧게 수정 가능**
   - [H] `EditorBridge._pingAssetPath` volatile
   - [H] CanvasRenderer.HitTest가 OBB 사용
   - [H] Inspector 모드 전환 시 `_hasChanges` 보존
   - [M] Script 리로드 데드코드 정리 (Save/RestoreHotReloadableState)
   - [M] CanvasEditMode.Exit 카메라 복원 직접 수행
   - [M] RoseCache `_compressonatorCliPath` Lazy 초기화

---

## 5. 분석 범위에서 제외된 것 (후속 과제로 남음)

- `IronRose.Physics` (PhysicsWorld2D/3D, PhysicsManager)
- `IronRose.Scripting` (ScriptDomain, StateManager, IHotReloadable) — 부분적으로 ScriptReloadManager 쪽만 훑음
- `IronRose.RoseEditor` / `IronRose.Standalone` 진입점
- `IronRose.AssetPipeline` 프로젝트 자체 (현재는 Engine 내부 `AssetPipeline/`만 분석)
- 셰이더(HLSL)와 `external/` 의존성
- `IronRose.Contracts` Debug/EditorDebug 로깅 API 사용 패턴의 일관성

필요하면 이 범위들도 같은 3관점으로 추가 분석 가능.
