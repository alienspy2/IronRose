---
name: rose-cli
description: "IronRose 에디터 CLI 브릿지를 통해 실행 중인 엔진과 상호작용합니다. 씬 조회/편집, GameObject 생성/수정/삭제, Transform/Material/Light/Camera 조작, Play 모드 제어, 에셋 관리, 스프라이트 임포트 설정(9-slice border, pivot, PPU, quality, compression), UI 요소 생성/조작(Canvas, Text, Image, Panel, Button, Toggle, Slider, InputField, LayoutGroup, ScrollView), RectTransform 조작, UI 트리 조회, UI 디버깅, UI 정렬/분배, 스크린샷 등 140여 개의 명령을 지원합니다. 사용자가 CLI로 씬을 조작하거나, 오브젝트를 배치하거나, UI를 구성하거나, RectTransform을 설정하거나, 에디터 상태를 확인하거나, 프리팹을 다루거나, 렌더링 설정을 변경하거나, 텍스처를 스프라이트로 전환하려 할 때 이 스킬을 사용하세요. rose-cli, ironrose-cli, 씬 조작, 오브젝트 배치, 에디터 제어, UI 생성, Canvas, RectTransform, 앵커, 피벗, 스프라이트, 9slice, UI 디버깅 등의 키워드가 나오면 트리거합니다."
---

# IronRose CLI 브릿지

실행 중인 IronRose 에디터에 Named Pipe로 명령을 보내 씬과 오브젝트를 조작하는 CLI 도구.

## 호출 방법

스크립트에 shebang(`#!/usr/bin/env python3`)과 실행 권한이 있으므로 직접 실행한다.
IronRose 프로젝트 루트를 기준으로 상대 경로를 사용한다:

```bash
tools/ironrose-cli/ironrose_cli.py <command> [args...]
```

- `--project NAME`: 프로젝트 지정 (생략 시 `~/.ironrose/settings.toml`의 마지막 프로젝트 자동 감지)
- `--timeout SECONDS`: 연결 타임아웃 (기본 3초). 스크린샷 등 오래 걸리는 명령은 늘릴 것

### 편의 alias

긴 경로를 매번 쓰지 않도록, 스킬 사용 시 IronRose 프로젝트 루트 기준으로 변수를 설정하면 편리하다:
```bash
CLI="$(git rev-parse --show-toplevel)/tools/ironrose-cli/ironrose_cli.py"
```

## 응답 형식

모든 응답은 JSON:
- 성공: `{ "ok": true, "data": { ... } }` — data 부분만 pretty-print 출력 (exit 0)
- 실패: `{ "ok": false, "error": "..." }` — 에러 메시지 출력 (exit 1)

## 핵심 규칙

1. **에디터가 실행 중이어야 한다.** 에디터가 꺼져 있으면 파이프 연결 실패. "pipe not found" 에러가 나면 에디터를 먼저 실행하라고 안내할 것.
2. **메인 스레드 타임아웃은 5초.** 모달 대화상자가 열려 있으면 명령이 타임아웃될 수 있다.
3. **GameObject 식별은 ID 또는 이름.** `go.get`, `go.find` 등은 InstanceID(숫자)나 이름(문자열) 모두 지원. 이름에 공백이 있으면 따옴표로 감쌀 것.
4. **Vector3 형식:** `x,y,z` (공백 없이, 예: `0,5,0`)
5. **Color 형식:** `r,g,b,a` (0~1 범위, 예: `1,0,0,1` = 빨강)
6. **editor.screenshot은 비동기.** 명령 후 즉시 응답이 오지만 파일은 다음 프레임 이후에 생성된다. 캡처 후 파일을 읽으려면 잠시(0.5~1초) 대기할 것.

## 자주 쓰는 워크플로우

### 씬 상태 파악
```bash
$CLI ping                  # 연결 확인
$CLI scene.info             # 현재 씬 이름, 경로, GO 수
$CLI scene.tree             # 계층 트리 출력
$CLI scene.list             # 모든 GO 목록 (id, name)
```

### 오브젝트 생성 및 배치
```bash
$CLI go.create "MyObject"                    # 빈 GO 생성
$CLI go.create_primitive Cube                # 프리미티브 생성 (Cube/Sphere/Capsule/Cylinder/Plane/Quad)
$CLI transform.set_position <id> 0,5,0       # 위치 설정
$CLI transform.set_rotation <id> 0,45,0      # 회전 설정 (오일러)
$CLI transform.set_scale <id> 2,2,2          # 스케일 설정
$CLI transform.set_parent <id> <parentId>    # 부모 설정
```

### 오브젝트 정보 및 수정
```bash
$CLI go.get <id|name>                             # 상세 정보 (컴포넌트, 필드 포함)
$CLI go.set_field <id> <Component> <field> <value> # 필드 수정 (리플렉션)
$CLI go.set_active <id> true|false                 # 활성/비활성
$CLI go.rename <id> "NewName"                      # 이름 변경
$CLI go.destroy <id>                               # 삭제
$CLI go.duplicate <id>                             # 복제
```

### 머티리얼 및 라이팅
```bash
$CLI material.info <goId>                         # 머티리얼 정보
$CLI material.set_color <goId> 1,0,0,1            # 색상 변경
$CLI material.create MyMat Assets/Materials       # 새 머티리얼 생성
$CLI material.apply <goId> <guid|path>            # 머티리얼 적용
$CLI light.set_color <goId> 1,1,0.8,1             # 라이트 색상
$CLI light.set_intensity <goId> 2.5               # 라이트 강도
```

### Play 모드 제어
```bash
$CLI play.enter    # 플레이 시작
$CLI play.pause    # 일시정지
$CLI play.resume   # 재개
$CLI play.stop     # 정지
$CLI play.state    # 현재 상태 조회
```

### 프리팹 & 에셋
```bash
$CLI asset.find "tree"                       # 에셋 검색 (부분 매칭)
$CLI asset.list Assets/Models                # 폴더 내 에셋 목록
$CLI prefab.instantiate <guid> 0,0,0         # 프리팹 인스턴스 생성
$CLI prefab.save <goId> Assets/Prefabs/x.prefab  # GO를 프리팹으로 저장
$CLI prefab.info <guid>                      # 프리팹 에셋 정보 조회 (GO, 컴포넌트, 필드)
$CLI prefab.set_field <guid> PileScript explosionVfxPrefab asset:<prefabGuid>  # 프리팹 필드 수정
```

### 에디터 조작
```bash
$CLI select <id>               # 오브젝트 선택
$CLI select none               # 선택 해제
$CLI select.get                # 현재 선택 조회
$CLI editor.undo               # 실행취소
$CLI editor.redo               # 다시실행
$CLI editor.screenshot /tmp/shot.png  # 스크린샷
$CLI log.recent                # 최근 로그 조회
```

### UI 구성
```bash
# Canvas 및 UI 요소 생성
$CLI ui.create_canvas "MainUI"                 # Canvas 생성
$CLI ui.create_panel <canvasId> 0.2,0.2,0.2,0.9 # 패널 생성 (색상 지정)
$CLI ui.create_text <parentId> "Hello" 24      # 텍스트 생성 (내용, 폰트크기)
$CLI ui.create_button <parentId> "OK"          # 버튼 생성 (라벨)
$CLI ui.create_image <parentId>                # 이미지 생성
$CLI ui.create_toggle <parentId>               # 토글 생성
$CLI ui.create_slider <parentId>               # 슬라이더 생성
$CLI ui.create_input <parentId> "Enter text"   # 입력 필드 생성
$CLI ui.create_layout <parentId> Vertical      # 레이아웃 그룹 생성
$CLI ui.create_scroll <parentId>               # 스크롤뷰 생성

# UI 트리 조회
$CLI ui.tree                                   # 모든 Canvas의 UI 계층 트리
$CLI ui.tree <canvasId>                        # 특정 Canvas의 트리
$CLI ui.list                                   # UI 요소 flat 목록
$CLI ui.find "ButtonOK"                        # UI 요소 이름 검색
$CLI ui.canvas.list                            # 모든 Canvas 목록
```

### RectTransform 조작
```bash
$CLI ui.rect.get <goId>                          # RectTransform 전체 정보
$CLI ui.rect.set_preset <goId> MiddleCenter      # 앵커 프리셋 적용
$CLI ui.rect.set_preset <goId> StretchAll true    # 프리셋 (시각 위치 유지)
$CLI ui.rect.set_anchors <goId> 0,0 1,1           # anchorMin, anchorMax
$CLI ui.rect.set_position <goId> 100,50           # anchoredPosition
$CLI ui.rect.set_size <goId> 200,60               # sizeDelta
$CLI ui.rect.set_pivot <goId> 0.5,0.5             # pivot
$CLI ui.rect.set_offsets <goId> 10,10 -10,-10     # offsetMin, offsetMax (stretch)
```

### UI 컴포넌트 조작
```bash
# 텍스트
$CLI ui.text.set_text <goId> "New text"
$CLI ui.text.set_font_size <goId> 18
$CLI ui.text.set_color <goId> 1,1,1,1
$CLI ui.text.set_alignment <goId> MiddleCenter

# 이미지/패널
$CLI ui.image.set_sprite <goId> <spriteGuid>
$CLI ui.image.set_type <goId> Sliced              # 9-slice 모드
$CLI ui.panel.set_color <goId> 0.1,0.1,0.1,0.9

# 버튼
$CLI ui.button.set_colors <goId> 1,1,1,1 0.9,0.9,0.9,1 0.7,0.7,0.7,1 0.5,0.5,0.5,0.5
$CLI ui.button.set_interactable <goId> false

# 슬라이더/토글/인풋
$CLI ui.slider.set_range <goId> 0 100
$CLI ui.slider.set_value <goId> 50
$CLI ui.toggle.set_on <goId> true
$CLI ui.input.set_placeholder <goId> "Enter name..."
```

### UI 디버깅 & 유틸
```bash
$CLI ui.debug.rects true                          # 모든 Rect 아웃라인 표시
$CLI ui.debug.overlap                              # 겹치는 UI 요소 검출
$CLI ui.debug.hit_test 400 300                     # 화면 좌표 hit test

# 정렬/분배
$CLI ui.align left <id1> <id2> <id3>              # 좌측 정렬
$CLI ui.distribute horizontal <id1> <id2> <id3>   # 수평 균등 분배

# 테마 일괄 적용
$CLI ui.theme.apply_color <canvasId> UIText color 1,1,1,1
$CLI ui.theme.apply_font_size <canvasId> 16
```

## 전체 명령 레퍼런스

60개 이상의 명령에 대한 상세 사용법(인자, 반환값)은 아래 파일을 참조:

**[references/command-reference.md](references/command-reference.md)** — 카테고리별 전체 명령 레퍼런스

특정 명령의 사용법이 확실하지 않을 때 이 파일을 읽어볼 것.

## go.set_field 지원 타입

리플렉션으로 컴포넌트 필드를 수정할 때 지원되는 타입:

| 타입 | 값 형식 | 예시 |
|------|---------|------|
| float | 숫자 | `1.5` |
| int | 정수 | `42` |
| bool | true/false | `true` |
| string | 문자열 | `"hello"` |
| Vector3 | x,y,z | `1,2,3` |
| Color | r,g,b,a | `1,0,0,1` |
| enum | 이름 | `Directional` |

## 트러블슈팅

| 증상 | 원인 | 해결 |
|------|------|------|
| "pipe not found" | 에디터 미실행 | 에디터를 먼저 실행 |
| "Connection timed out" | 파이프 연결 지연 | `--timeout 10` 으로 늘리기 |
| "Main thread timeout" | 메인 스레드 블로킹 (모달 등) | 모달 닫고 재시도 |
| "GameObject not found" | 잘못된 ID/이름 | `scene.list`로 확인 |
| screenshot 파일 없음 | 비동기 캡처 | `sleep 1` 후 파일 확인 |
