# Phase 48e: 에디터 UI (BlendMode 콤보박스)

## 목표
- `DrawMaterialEditor()`에 BlendMode 콤보박스를 추가하여 Material의 블렌드 모드를 편집할 수 있게 한다.
- `DrawReadOnlyMaterialInspector()`에 BlendMode 읽기 전용 표시를 추가한다.

## 선행 조건
- Phase 48a 완료 (`BlendMode` enum 및 `Material.blendMode` 프로퍼티 존재)
- Phase 48b 완료 (직렬화 -- TOML에 blendMode 키가 저장/로드됨)

## 수정할 파일

### `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs`

**변경 1: DrawMaterialEditor()에 BlendMode 콤보박스 추가**

위치: `DrawMaterialEditor` 메서드 내, `ImGui.Spacing();` (라인 3028) 바로 다음, `// ── Base Surface ──` (라인 3030) 전.

현재 코드:
```csharp
            ImGui.Spacing();

            // ── Base Surface ──
            DrawMatTextureSlot("mainTextureGuid", "Main Texture");
```

변경 후:
```csharp
            ImGui.Spacing();

            // ── Blend Mode ──
            {
                var blendNames = new[] { "Opaque", "AlphaBlend", "Additive" };
                int currentIdx = 0;
                if (_editedMatTable!.TryGetValue("blendMode", out var blendVal))
                {
                    var blendStr = blendVal?.ToString() ?? "Opaque";
                    currentIdx = blendStr switch
                    {
                        "AlphaBlend" => 1,
                        "Additive" => 2,
                        _ => 0,
                    };
                }

                string wl = EditorWidgets.BeginPropertyRow("Blend Mode");
                if (ImGui.Combo(wl, ref currentIdx, blendNames, blendNames.Length))
                {
                    _undoTracker.BeginEdit("Mat.blendMode", Toml.FromModel(_editedMatTable));
                    _editedMatTable["blendMode"] = blendNames[currentIdx];
                    SaveMatFile();
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    if (_undoTracker.EndEdit("Mat.blendMode", out var oldSnap))
                    {
                        var newSnap = Toml.FromModel(_editedMatTable);
                        UndoSystem.Record(new MaterialPropertyUndoAction(
                            "Change Material blendMode", _matFilePath!, (string)oldSnap!, newSnap));
                    }
                }
            }

            ImGui.Spacing();

            // ── Base Surface ──
            DrawMatTextureSlot("mainTextureGuid", "Main Texture");
```

구현 상세:
- `_editedMatTable`은 `TomlTable` 타입 (라인 92에 선언)
- `TryGetValue`로 blendMode 키를 읽어 문자열로 변환 후 인덱스 매핑
- `ImGui.Combo`로 드롭다운 UI 표시
- 변경 시 `_editedMatTable["blendMode"]`에 문자열 값 직접 저장 (TOML 키-값)
- Undo 패턴은 기존 `DrawMatFloat`와 동일한 패턴 사용
- `SaveMatFile()`은 기존 메서드 -- TOML을 디스크에 저장

**주의**: `ImGui.Combo`는 `IsItemDeactivatedAfterEdit()`를 콤보박스 선택 시점에 즉시 트리거할 수 있으므로, `BeginEdit`를 Combo 내부에서, `EndEdit`를 `IsItemDeactivatedAfterEdit` 내부에서 호출하는 패턴을 사용한다. 다만 Combo는 선택 즉시 값이 확정되므로 `IsItemDeactivatedAfterEdit`가 호출되지 않을 수 있다. 안전하게 Combo 변경 시점에서 바로 undo 기록하는 방식으로 구현해도 좋다. 아래는 간소화된 대안:

```csharp
                if (ImGui.Combo(wl, ref currentIdx, blendNames, blendNames.Length))
                {
                    var oldSnap = Toml.FromModel(_editedMatTable);
                    _editedMatTable["blendMode"] = blendNames[currentIdx];
                    SaveMatFile();
                    var newSnap = Toml.FromModel(_editedMatTable);
                    UndoSystem.Record(new MaterialPropertyUndoAction(
                        "Change Material blendMode", _matFilePath!, oldSnap, newSnap));
                }
```

이 간소화 버전을 권장한다. Combo는 DragFloat와 달리 드래그 중 연속 변경이 없으므로 `BeginEdit/EndEdit` 패턴이 불필요하다.

---

**변경 2: DrawReadOnlyMaterialInspector()에 BlendMode 읽기 전용 표시 추가**

위치: `DrawReadOnlyMaterialInspector` 메서드 내, `ImGui.BeginDisabled();` 다음의 `ImGui.Spacing();` (라인 3104) 바로 다음, `// ── Base Surface ──` 전.

현재 코드:
```csharp
            ImGui.BeginDisabled();

            ImGui.Spacing();

            // ── Base Surface ──
            DrawReadOnlyTextureSlot(db, mat.mainTexture, "Main Texture");
```

변경 후:
```csharp
            ImGui.BeginDisabled();

            ImGui.Spacing();

            // ── Blend Mode ──
            {
                string wl = EditorWidgets.BeginPropertyRow("Blend Mode");
                ImGui.TextUnformatted(mat.blendMode.ToString());
            }

            ImGui.Spacing();

            // ── Base Surface ──
            DrawReadOnlyTextureSlot(db, mat.mainTexture, "Main Texture");
```

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] 에디터에서 `.mat` 파일 선택 시 Material Inspector에 "Blend Mode" 콤보박스가 표시됨
- [ ] 콤보박스에서 Opaque/AlphaBlend/Additive 3개 옵션 선택 가능
- [ ] 변경 후 `.mat` 파일에 `blendMode = "AlphaBlend"` 등 문자열이 저장됨
- [ ] GLB sub-asset Material Inspector에서 blendMode가 읽기 전용으로 표시됨
- [ ] Undo 동작 확인 (Ctrl+Z로 블렌드 모드 변경 취소)

## 참고
- `EditorWidgets.BeginPropertyRow`는 라벨-값 2열 레이아웃을 설정하는 기존 헬퍼.
- `Toml.FromModel`은 `TomlTable`을 TOML 문자열로 직렬화하는 유틸리티.
- `MaterialPropertyUndoAction`은 기존 Undo 액션 타입 -- `.mat` 파일 경로와 이전/이후 스냅샷으로 undo/redo 수행.
- `_matFilePath`는 `DrawMaterialEditor`가 호출될 때 이미 설정되어 있음 (null 체크는 메서드 첫 줄에서 수행).
- Combo가 DragFloat와 달리 연속 변경이 없으므로, Undo는 선택 즉시 기록하는 간소화 패턴을 사용한다.
