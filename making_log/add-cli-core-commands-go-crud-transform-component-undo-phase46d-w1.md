# Phase 46d-w1: CLI 핵심 명령 세트 구현 (go CRUD, transform, component, undo/redo)

## 수행한 작업
- CliCommandDispatcher에 Wave 1 핵심 명령 15개 핸들러를 추가하였다.
- FormatVector3(), ResolveComponentType() 헬퍼 메서드 2개를 추가하였다.
- `using IronRose.Scripting;`을 추가하여 LiveCode 어셈블리 타입 검색을 지원한다.

### 추가된 명령
1. **go.create** -- 빈 GameObject 생성 (`new GameObject(name)`)
2. **go.create_primitive** -- Primitive GO 생성 (`GameObject.CreatePrimitive(PrimitiveType)`)
3. **go.destroy** -- GameObject 삭제 (`RoseEngine.Object.DestroyImmediate(go)`)
4. **go.rename** -- GameObject 이름 변경 (`go.name = newName`)
5. **go.duplicate** -- GameObject 복제 (`RoseEngine.Object.Instantiate(go)`)
6. **transform.get** -- position/rotation/scale 조회 (FormatVector3 헬퍼 사용)
7. **transform.set_position** -- 월드 위치 설정
8. **transform.set_rotation** -- 오일러 회전 설정
9. **transform.set_scale** -- 로컬 스케일 설정
10. **transform.set_parent** -- 부모 변경 ("none" 또는 "0"이면 루트로 이동)
11. **component.add** -- 컴포넌트 추가 (ResolveComponentType 헬퍼로 타입 해석)
12. **component.remove** -- 컴포넌트 제거 (Transform 제거 방지)
13. **component.list** -- GO의 모든 컴포넌트 목록 (리플렉션으로 필드/프로퍼티 조회)
14. **editor.undo** -- 실행취소 (`UndoSystem.PerformUndo()`)
15. **editor.redo** -- 다시실행 (`UndoSystem.PerformRedo()`)

## 변경된 파일
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` -- 15개 핸들러 + 2개 헬퍼 메서드 추가, frontmatter 갱신, using 추가

## 주요 결정 사항
- `Object.DestroyImmediate()`와 `Object.Instantiate()`에서 `RoseEngine.Object`와 `System.Object` 간 모호성이 발생하여, `RoseEngine.Object.DestroyImmediate()`, `RoseEngine.Object.Instantiate()` 형태로 full-qualify하였다.
- `ResolveComponentType()`은 엔진 내장 -> FrozenCode -> LiveCode 순서로 타입을 검색한다. LiveCode 어셈블리 접근 시 `ReflectionTypeLoadException`을 catch하여 collectible ALC 해제 후 안전하게 처리한다.
- `component.list`에서 `gameObject`, `transform` 프로퍼티는 순환 참조 방지를 위해 제외한다.

## 다음 작업자 참고
- Wave 2 (asset.*, prefab.*, scene.tree, scene.new) 구현이 다음 단계이다.
- 기존 경고(SceneManager, ImGuiOverlay, ImGuiSpriteEditorPanel)는 본 작업 범위가 아니므로 수정하지 않았다.
- `using IronRose.Scripting;`은 현재 ResolveComponentType()에서만 사용하지만, 향후 LiveCode 관련 명령 추가 시에도 필요하다.
