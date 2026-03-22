# CLI 래퍼 프로젝트 이름 감지 로직을 settings.toml 기반으로 변경

## 유저 보고 내용
- `ironrose_cli.py`의 `find_project_name()`이 cwd에서 `project.toml`을 탐색하여 프로젝트 이름을 읽는 방식이었음
- 이를 `~/.ironrose/settings.toml`의 `last_project` 경로에서 읽는 방식으로 변경 요청

## 원인
- 기존 로직은 cwd 기반으로 `project.toml`을 상위 디렉토리까지 탐색했으나, CLI를 어디서 실행하든 마지막으로 열었던 프로젝트에 연결해야 하므로 `settings.toml`의 `last_project`를 사용하는 것이 올바름
- C# 엔진 측(`ProjectContext.ReadLastProjectPath()`)은 이미 `settings.toml` 기반이었으나, Python CLI 래퍼만 구버전 로직을 사용 중이었음

## 수정 내용
- `find_project_name()`: cwd 탐색 로직 제거, `~/.ironrose/settings.toml`의 `[editor]` 섹션에서 `last_project` 경로를 읽고, 해당 경로의 `project.toml`에서 `[project]` 섹션의 `name`을 읽도록 변경
- `_read_toml_string()`: 새로 추가한 간이 TOML 파서. 지정 섹션/키의 문자열 값을 읽음. 외부 라이브러리 없이 정규식으로 처리
- settings.toml이나 project.toml이 없으면 에러 메시지를 stderr에 출력하고 exit(1)
- `--project` 옵션 우선순위는 기존과 동일 (명시 시 settings.toml보다 우선)
- 파일 헤더 주석, `--project` help 텍스트도 함께 갱신

## 변경된 파일
- `tools/ironrose-cli/ironrose_cli.py` -- find_project_name() 로직 변경, _read_toml_string() 추가, 헤더/help 텍스트 갱신

## 검증
- `python3 ironrose_cli.py --help` 정상 출력 확인
- `find_project_name()` 직접 호출 테스트: `~/.ironrose/settings.toml`에서 `last_project="/home/alienspy/git/MyGame"` 경로를 읽고, 해당 `project.toml`에서 `name="MyGame"`을 정상 반환 확인
