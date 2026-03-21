# 로그 분리: Editor Log / Project Log

> Unity 방식처럼 에디터 로그와 프로젝트 로그를 분리한다.

## 개요

- **Editor Log**: 엔진 레포 `{EngineRoot}/Logs/`에 기록. 에디터 자체 동작, 에셋 임포트, UI 등.
- **Project Log**: 프로젝트 레포 `{ProjectRoot}/Logs/`에 기록. 게임 런타임, 사용자 스크립트 등.

## 현재 상태

- `Debug` 클래스가 단일 로그 경로 사용
- `ProjectContext.Initialize()` 전에는 CWD 기준, 이후에는 `ProjectRoot/Logs`로 전환
- 에디터/프로젝트 구분 없이 모든 로그가 한 파일에 섞임

## 수정 방향

- `Debug` 클래스에 로그 채널(Editor / Project) 개념 도입
- 에디터 로그 → `EngineRoot/Logs/`
- 프로젝트 로그 → `ProjectRoot/Logs/`
- phase43_post_task_bugfixes #12(Debug static 생성자), #14(로그 디렉토리 전환 잔존) 이슈도 함께 해소 가능
