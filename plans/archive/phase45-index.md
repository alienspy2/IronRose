# Phase 45 Index: PlayerPrefs 및 Application.persistentDataPath 구현

## 설계 문서
- `plans/phase45_playerprefs-and-persistent-data-path.md`

## Phase 목록

| Phase | 제목 | 파일 | 선행 | 상태 |
|-------|------|------|------|------|
| 45a | Application 클래스 확장 및 ProjectContext company 필드 읽기 | phase45a_application-paths.md | - | 미완료 |
| 45b | PlayerPrefs 클래스 구현 및 EngineCore 통합 | phase45b_playerprefs-implementation.md | 45a | 미완료 |

## 의존 관계
```
Phase 45a (Application 확장 + ProjectContext 수정 + EngineCore 경로 초기화 + 템플릿)
    |
    v
Phase 45b (PlayerPrefs 신규 구현 + EngineCore 통합)
```

## Phase 분할 근거
- **45a**: Application 프로퍼티 추가, ProjectContext의 company 필드 읽기, EngineCore 경로 초기화, 템플릿 수정. 기존 파일 4개 수정. PlayerPrefs가 없어도 빌드 가능.
- **45b**: PlayerPrefs 정적 클래스 신규 파일 1개 생성, EngineCore에 Initialize/Shutdown 호출 추가. Phase 45a에서 추가한 `Application.companyName`, `ProjectContext.ProjectName`에 의존.

## 영향 범위 요약

| 파일 | Phase | 변경 유형 |
|------|-------|-----------|
| `src/IronRose.Engine/RoseEngine/Application.cs` | 45a | 수정 - 프로퍼티 4개 + InitializePaths() 추가 |
| `src/IronRose.Engine/ProjectContext.cs` | 45a | 수정 - company 필드 읽기 추가 |
| `src/IronRose.Engine/EngineCore.cs` | 45a, 45b | 수정 - InitApplication() 경로 초기화 (45a), PlayerPrefs 초기화/종료 (45b) |
| `templates/default/project.toml` | 45a | 수정 - company 필드 주석 추가 |
| `src/IronRose.Engine/RoseEngine/PlayerPrefs.cs` | 45b | 신규 - PlayerPrefs 클래스 전체 구현 |
