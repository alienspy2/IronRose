# MyGame → IronRoseSimpleGameDemoProject 리네임 체크리스트

## 1. 디렉토리 이름 변경
- [ ] `mv ~/git/MyGame ~/git/IronRoseSimpleGameDemoProject`

## 2. MyGame 프로젝트 내부 파일 수정

| 파일 | 변경 내용 |
|------|-----------|
| `project.toml` | `name = "MyGame"` → `name = "IronRoseSimpleGameDemoProject"` |
| `MyGame.sln` → `IronRoseSimpleGameDemoProject.sln` | 파일명 변경 |
| `MyGame.code-workspace` → `IronRoseSimpleGameDemoProject.code-workspace` | 파일명 + 내부 `"name"`, `"dotnet.defaultSolution"` 값 변경 |
| `.vscode/settings.json` | `"dotnet.defaultSolution": "MyGame.sln"` → `"IronRoseSimpleGameDemoProject.sln"` |

## 3. IronRose 설정 파일

| 파일 | 변경 내용 |
|------|-----------|
| `~/.ironrose/settings.toml` | `last_project = "/home/alienspy/git/MyGame"` → 새 경로 |

## 4. 빌드 캐시 갱신
- [ ] `Scripts/obj/` — `dotnet clean` 후 리빌드하면 자동 갱신

## 수정 불필요 항목

- **`ImGuiStartupPanel.cs:35`** — 새 프로젝트 생성 시 기본 이름 `"MyGame"`, 제너릭 예시이므로 유지
- **`HideInConsoleStackTraceAttribute.cs:22`** — XML 주석 예시 코드, 유지
- **`README.md`, `doc/ProjectStructure.md`, `manual/NewProject.md`** — `MyGame`이 제너릭 예시 프로젝트명으로 사용됨, 유지
- **`making_log/`, `plans/`** — 이미 완료된 기록, 수정 불필요
- **`~/.claude/projects/-home-alienspy-git-MyGame/`** — Claude Code 세션 캐시, 자동 생성되므로 무시
