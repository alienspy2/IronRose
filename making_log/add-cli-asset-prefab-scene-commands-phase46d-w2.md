# Phase 46d-w2: CLI 에셋/프리팹 명령 세트 구현

## 수행한 작업
- CliCommandDispatcher에 Wave 2 핸들러 8개를 추가하였다.
  - `prefab.instantiate` -- GUID로 프리팹 인스턴스 생성 (위치 옵션)
  - `prefab.save` -- GameObject를 .prefab 파일로 저장
  - `asset.list` -- 에셋 DB 전체/필터 목록 조회
  - `asset.find` -- 이름으로 에셋 검색 (부분 매칭, case-insensitive)
  - `asset.guid` -- 경로에서 GUID 조회
  - `asset.path` -- GUID에서 경로 조회
  - `scene.tree` -- 씬 계층 트리 조회 (재귀적 부모-자식 구조)
  - `scene.new` -- 새 빈 씬 생성
- `BuildTreeNode()` 재귀 헬퍼 메서드를 추가하였다.
- `using IronRose.AssetPipeline;`을 추가하였다 (AssetDatabase 접근용).
- frontmatter를 갱신하여 Wave 2 명령 목록과 새 의존성을 반영하였다.

## 변경된 파일
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` -- Wave 2 핸들러 8개 + BuildTreeNode 헬퍼 추가, frontmatter 갱신, using 추가

## 주요 결정 사항
- 명세서에 정의된 코드를 그대로 구현하였다. 별도의 설계 결정은 없다.
- 모든 에셋/프리팹 명령은 메인 스레드에서 실행된다 (AssetDatabase/SceneManager 접근 필요).
- `asset.list`의 filterPath는 Contains 부분 매칭으로, 디렉토리 필터가 아닌 문자열 포함 여부 검사이다.

## 다음 작업자 참고
- Wave 3 (visual-commands), Wave 4 (convenience-commands) 구현이 남아 있다.
- `Resources.GetAssetDatabase()`가 null이면 프로젝트 미로드 상태이므로 에셋 관련 명령은 에러를 반환한다.
- Python CLI 래퍼는 명령을 해석하지 않으므로 수정이 필요 없다.
