# ProjectContext — 프로젝트 경로 컨텍스트

> **파일**: `src/IronRose.Engine/ProjectContext.cs`
> **상태**: 구현 완료

## 개요

에셋 프로젝트의 루트 디렉토리와 엔진 루트 디렉토리를 자동 탐지하고, 프로젝트 내 주요 경로를 절대 경로로 제공하는 정적 클래스.

## 프로퍼티

| 프로퍼티 | 접근 | 경로 기준 | 설명 |
|----------|------|-----------|------|
| `ProjectRoot` | public | — | 에셋 프로젝트 루트 (project.toml이 있는 디렉토리) |
| `EngineRoot` | public | — | 엔진 소스 루트 (IronRose/ 디렉토리) |
| `ProjectName` | public | — | 프로젝트 이름 (`project.toml [project] name`) |
| `IsProjectLoaded` | public | — | project.toml 발견 여부 |
| `AssetsPath` | public | `ProjectRoot/Assets` | |
| `EditorAssetsPath` | internal | `EngineRoot/EditorAssets` | 에디터 에셋은 엔진 소유 |
| `CachePath` | public | `ProjectRoot/RoseCache` | |
| `LiveCodePath` | public | `ProjectRoot/LiveCode` | |
| `FrozenCodePath` | public | `ProjectRoot/FrozenCode` | |

## 초기화 흐름

`EngineCore.Initialize()` 시작부에서 `ProjectContext.Initialize()` 호출.

```
Initialize(projectRoot?)
  ├─ projectRoot 명시 → 해당 경로 사용
  ├─ null → FindProjectRoot(CWD) → FindProjectRoot(AppContext.BaseDirectory) → CWD 폴백
  │
  ├─ project.toml 발견
  │   ├─ [project] name → ProjectName
  │   ├─ [engine] path → EngineRoot (상대 경로를 절대 경로로 변환)
  │   ├─ IsProjectLoaded = true
  │   └─ ValidateBuildPropsAlignment() — Directory.Build.props와 engine.path 불일치 검증
  │
  └─ project.toml 미발견
      ├─ ReadLastProjectPath() — ~/.ironrose/settings.toml에서 마지막 프로젝트 경로 시도
      │   ├─ 성공 → Initialize(lastProjectPath) 재귀
      │   └─ 실패 → 레거시 .rose_last_project 마이그레이션 시도
      └─ 최종 실패 → EngineRoot = ProjectRoot = CWD, IsProjectLoaded = false
```

## 글로벌 설정

- **경로**: `~/.ironrose/settings.toml`
- **내용**: `[editor] last_project` — 마지막으로 열린 프로젝트 경로
- `SaveLastProjectPath()`: 프로젝트 열기/생성 시 저장
- `ReadLastProjectPath()`: 초기화 시 읽기, 경로의 project.toml 존재 여부 검증 후 반환
- 레거시 `.rose_last_project` 파일 발견 시 settings.toml로 마이그레이션 후 삭제

## 의존

- `Tomlyn` v0.20.0 — TOML 파싱 (직접 사용, TomlConfig 래퍼 미구현)
- `System.Xml.Linq` — Directory.Build.props XML 파싱 (.NET 기본 포함)
- `RoseEngine.Debug` — 로깅

## 알려진 이슈

`phase43_post_task_bugfixes.md` 참조:
- #7: SaveLastProjectPath 전체 덮어쓰기 → read-modify-write 필요
- #17: Initialize() 재귀 구조 → 가독성 개선 바람직
- #19: Initialize 전 파생 프로퍼티 접근 방어 없음
- #20: TomlConfig 래퍼 구현 예정
