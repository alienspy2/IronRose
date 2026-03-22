# CLI 디스패처 Wave 1/Wave 2 핸들러 머지 충돌 누락 복원

## 유저 보고 내용
- CliCommandDispatcher.cs에서 머지 충돌 해결 과정에서 Wave 1과 Wave 2의 핸들러가 모두 누락되었다.
- 누락 범위: go.create, go.create_primitive, go.destroy, go.rename, go.duplicate, transform.get, transform.set_position, transform.set_rotation, transform.set_scale, transform.set_parent, component.add, component.remove, component.list, editor.undo, editor.redo (W1 15개) + prefab.instantiate, prefab.save, asset.list, asset.find, asset.guid, asset.path, scene.tree, scene.new (W2 8개)
- 헬퍼 메서드 FormatVector3(), ResolveComponentType(), BuildTreeNode()도 누락.

## 원인
- Git 머지 충돌 해결 시 Wave 1/Wave 2 핸들러 블록이 통째로 제거됨.
- 해당 헬퍼 메서드도 함께 유실됨.

## 수정 내용
1. `RegisterHandlers()` 메서드 내 `log.recent` 핸들러 뒤, `material.info` (Wave 3) 핸들러 전에 W1 (15개) + W2 (8개) 핸들러를 복원 삽입.
2. 헬퍼 메서드 영역에 FormatVector3(), ResolveComponentType(), BuildTreeNode() 3개 메서드 복원.
3. using 추가: `using IronRose.AssetPipeline;` (AssetDatabase 접근용).
4. `Object.DestroyImmediate()`, `Object.Instantiate()` 호출을 `RoseEngine.Object.DestroyImmediate()`, `RoseEngine.Object.Instantiate()`로 full-qualify (System.Object와의 모호성 해결).
5. 파일 헤더 frontmatter의 명령 목록과 deps에 W1/W2 관련 내용 추가.

## 변경된 파일
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` -- W1/W2 핸들러 23개 복원, 헬퍼 3개 복원, using/frontmatter 갱신

## 검증
- `dotnet build src/IronRose.Engine/IronRose.Engine.csproj` 빌드 성공 (에러 0, 워닝은 기존 코드에서 발생하는 것만 존재)
