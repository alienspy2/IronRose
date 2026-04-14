---
name: ai-image-manipulation
description: "래스터 이미지(PNG/JPEG/WEBP 등)에 로컬 후처리를 적용하는 범용 스킬입니다. 네트워크 호출 없이 Pillow(PIL)만으로 동작하며, 서브커맨드 단위로 기능을 **계속 확장**합니다. 현재 지원: 투명 여백 자동 크롭(`trim`), 포맷 변환(`convert`, PNG/JPEG/WEBP), 리사이즈(`resize`, LANCZOS/NEAREST 등). 향후 예정: 패딩, power-of-two 보정, 색 조정 등. 사용자가 '이미지 후처리', '래스터 이미지 조작', '투명 여백 잘라줘', '알파 여백 제거', '스프라이트 타이트하게', 'auto-crop alpha', 'trim transparent border', '포맷 변환', 'PNG→WEBP', 'JPEG 변환', '이미지 리사이즈', '썸네일', '크기 줄여줘', '이미지 크게/작게' 등을 요청하거나, **이미 만들어진 래스터 이미지에 로컬 후처리가 필요한 맥락**이라면 이 스킬을 사용하세요. 이미지 생성이 필요하면 `ai-image-forge`/`svg-image-forge`를, 배경 제거가 필요하면 `ai-remove-bg`를 먼저 쓰고 그 결과물을 이 스킬로 다듬습니다. **로컬 전용**."
---

# AI Image Manipulation

**래스터 이미지의 로컬 후처리를 담당하는 범용 스킬**. 네트워크 호출 없이 Pillow(PIL)만으로 동작하며, 하나의 CLI(`cli-image-manipulation.py`)에 **서브커맨드를 계속 추가**해 기능을 확장해 나간다.

- 이미지 **생성**이 필요하면 → `ai-image-forge`(AI 래스터) / `svg-image-forge`(SVG→PNG)
- **배경 제거**가 필요하면 → `ai-remove-bg`
- 이미 래스터 이미지가 있고 **로컬에서 다듬기**만 필요하면 → 이 스킬

## 위치

- CLI: `tools/image-manipulation/cli-image-manipulation.py`
- 의존성: Python 3.10+ / Pillow (시스템에 설치되어 있어야 함)

## 기능 목록 (확장 중)

| 서브커맨드 | 상태 | 설명 |
|---|---|---|
| `trim` | 구현됨 | 알파 채널 기준으로 불투명 영역의 bounding box를 구해 바깥 투명 여백을 자동 크롭 |
| `convert` | 구현됨 | 포맷 변환 (PNG/JPEG/WEBP). **원본 덮어쓰기(삭제)가 기본** — `--keep-original`로 보존 |
| `resize` | 구현됨 | 지정 크기/비율로 리사이즈 (LANCZOS/NEAREST 등 필터 선택). **입력 덮어쓰기가 기본** |
| `pad` | 예정 | 지정 크기까지 투명/단색 패딩 추가 (정렬 앵커 지정) |
| `pot` | 예정 | Power-of-two 크기로 패딩·리사이즈 |
| `tint`/`recolor` | 예정 | 색조 적용, 단색 실루엣 생성 등 |

> 새 기능은 반드시 **서브커맨드 형태**로 추가한다. 전역 플래그 범람을 피하고, 각 서브커맨드는 단일 책임만 갖게 한다.

### 확장 규칙 (미래의 나/에이전트를 위해)

- 공통 출력 규약 유지: `--json` 플래그로 `{"ok": true, "output": "...", ...}` 또는 `{"ok": false, "error": "..."}`를 stdout에 출력.
- 배치 처리는 **쉘 루프**로 감싸는 패턴을 유지(서브커맨드는 단건만 처리). CLI 내부 glob·재귀는 구현하지 않는다.
- **기본 동작은 입력 덮어쓰기**(같은 경로 저장 또는 확장자 교체 후 원본 삭제). 보존이 필요하면 `-o`로 다른 경로를 지정하거나 `--keep-original`(convert) 플래그를 쓴다. `trim`은 기하 변경이 있어 예외적으로 `<stem>_trimmed.png` 접미사를 기본값으로 둔다.
- 로컬/결정적 동작 유지: 네트워크·외부 서비스 호출 금지. 외부 서비스가 필요하면 별도 스킬로 분리.
- 의존성은 가능한 한 Pillow+stdlib에 한정. 더 무거운 의존성이 필요한 기능은 별도 스킬/도구로 분리 고려.

## 🛑 파괴적 작업 전 백업 (필수)

입력 파일을 덮어쓰거나 삭제하는 모든 서브커맨드(`trim -o <input>`, `resize` 기본 동작, `convert` 기본 동작)는 **작업 실행 전에 원본을 백업**해야 한다.

### 규칙

1. **git 추적 여부 확인**: 작업 대상이 프로젝트 내부라면 `git ls-files --error-unmatch <파일>`로 추적 여부 확인.
   - **추적 중**: git에서 언제든 복구 가능하므로 별도 백업 불필요. `git status` 기준으로 로컬 수정 사항도 커밋/스태시되어 있는지 확인해 복구 가능 상태인지 점검.
   - **미추적**: **반드시** 백업 먼저 생성.

2. **백업 위치**: `/tmp/ai-image-manipulation-backup/<YYYYmmdd-HHMMSS>/` 아래에 원본 디렉토리 구조를 그대로 복사(단일 파일이면 파일명만).

   ```bash
   BK="/tmp/ai-image-manipulation-backup/$(date +%Y%m%d-%H%M%S)"
   mkdir -p "$BK"
   cp "$ASSET" "$BK/"
   ```

3. **유저 OK 전까지 백업 유지**: 작업을 완료하고 결과를 유저에게 보여준 뒤, **유저가 명시적으로 OK/확정**하기 전까지 백업을 절대 삭제하지 않는다. 유저가 "되돌려" "원래대로" 같은 피드백을 주면 백업에서 원본을 복원한다.

4. **여러 단계 연속 작업**: 한 턴에서 resize → trim → convert 같이 여러 단계를 거칠 때도, **첫 단계 직전**의 원본을 백업해두면 충분하다. 중간 산출물은 백업 불필요.

5. **유저 OK 확정 후**: 유저가 결과에 만족한다고 확정하면 백업을 `rm -rf`로 정리해도 된다. 그 전까지는 디스크 공간이 아까워도 놔둘 것.

### 복원 절차

유저가 원상복구를 요청하면:

```bash
# 최근 백업 찾기
ls -1d /tmp/ai-image-manipulation-backup/*/ | sort | tail -5
# 해당 디렉토리에서 원본 경로로 cp로 되돌림
cp "$BK/<file>" "<원본경로>"
# rose-cli로 재임포트
$CLI asset.import "<원본경로>"
```

복원 후 백업 디렉토리 정리 여부는 유저에게 확인.

## `trim` — 투명 여백 자동 크롭

### 언제 쓰나
- AI가 생성한 스프라이트의 바깥쪽에 **불필요한 투명 픽셀**이 남아 있어 실제 내용보다 캔버스가 큰 경우
- 9-slice/UI 용도가 아닌 일반 스프라이트를 **타이트한 bbox**로 정리해 텍스처 원자성/메모리 효율을 개선하고 싶을 때
- 여러 장을 일괄 정리할 때 (쉘 루프로 감싸서 배치 처리)

### 사용 규칙
- **입력은 알파 채널이 있는 이미지**여야 한다 (PNG/WEBP 등). 알파가 없으면 RGBA로 변환해도 모두 불투명이라 아무것도 잘리지 않는다.
- **9-slice/UI 텍스처도 trim 가능**. 단 border 픽셀 좌표와 PPU는 캔버스 변경으로 의미가 달라지므로, trim 후 **유저가 9-slice 설정을 재조정**해야 한다(자동 추정은 신뢰도가 낮다). 필요 정보를 출력해 유저에게 안내.
- **덮어쓰기 전 백업**: `-o <원본경로>`로 덮어쓸 경우 git 추적 여부를 확인하고, 추적되지 않은 파일은 먼저 백업할 것.

### 호출

```bash
python3 /home/alienspy/git/IronRose/tools/image-manipulation/cli-image-manipulation.py trim \
  <입력.png> \
  [-o <출력.png>] \
  [--padding N] \
  [--threshold N] \
  [--skip-if-noop] \
  [--json]
```

### 옵션

| 옵션 | 기본값 | 설명 |
|---|---|---|
| `input` | — | 입력 이미지 경로 (위치 인자) |
| `-o, --output` | `<입력stem>_trimmed.png` | 출력 경로. 입력과 **동일 경로**로 주면 원본 덮어쓰기 |
| `--padding` | `0` | bbox 바깥쪽에 남길 투명 여백 픽셀 수 (캔버스 경계를 넘지 않음) |
| `--threshold` | `0` | 알파 threshold. 이 값 **이하**인 픽셀을 투명으로 간주. 경계가 지저분할 때 `8~16` 정도로 올리면 잔재 픽셀 제거 가능 |
| `--skip-if-noop` | false | bbox가 원본과 같으면 파일을 쓰지 않고 `noop: true`만 반환 |
| `--json` | false | JSON 결과 출력 (툴 연동 필수) |

### JSON 결과 형식

성공:

```json
{
  "ok": true,
  "output": "/abs/path/out.png",
  "bbox": [left, upper, right, lower],
  "original_size": [W, H],
  "new_size": [w, h],
  "noop": false
}
```

실패:

```json
{"ok": false, "error": "..."}
```

- 이미지가 threshold 기준으로 완전 투명이면 `"image is fully transparent under given threshold"` 에러 반환 (잘라낼 영역 없음).

### 단건 예시

```bash
python3 /home/alienspy/git/IronRose/tools/image-manipulation/cli-image-manipulation.py trim \
  "$PROJECT/Assets/Sprites/hero_mage_nobg.png" \
  -o "$PROJECT/Assets/Sprites/hero_mage.png" \
  --padding 2 \
  --json
```

### 배치 예시 (디렉토리 일괄 정리)

CLI는 단건만 지원하므로 쉘 루프로 감싼다:

```bash
for f in "$PROJECT"/Assets/Sprites/*_nobg.png; do
  python3 /home/alienspy/git/IronRose/tools/image-manipulation/cli-image-manipulation.py trim \
    "$f" -o "$f" --padding 2 --skip-if-noop --json
done
```

## `convert` — 포맷 변환

### 언제 쓰나
- PNG → WEBP/JPEG 등 배포 포맷 변경, 또는 WEBP/JPEG → PNG 역변환
- 품질 옵션 재조정(JPEG/WEBP `--quality`)
- 불필요해진 원본 확장자를 정리하면서 한 번에 형식을 바꾸고 싶을 때

### 기본 동작: 덮어쓰기(원본 삭제)

`-o`를 생략하면 입력 파일 옆에 **새 확장자로 저장한 뒤 원본을 삭제**한다. 예를 들어 `foo.png` + `--format webp` → `foo.webp`가 남고 `foo.png`는 사라진다. 원본을 보존하려면 `--keep-original`을 붙이거나 `-o`로 다른 경로를 지정한다.

### 호출

```bash
python3 /home/alienspy/git/IronRose/tools/image-manipulation/cli-image-manipulation.py convert \
  <입력> \
  [-o <출력>] \
  [--format png|jpeg|webp] \
  [--quality N] \
  [--background #RRGGBB | 'r,g,b' | white] \
  [--keep-original] \
  [--json]
```

### 옵션

| 옵션 | 기본값 | 설명 |
|---|---|---|
| `input` | — | 입력 이미지 경로 |
| `-o, --output` | `<입력stem>.<new_ext>` (원본 삭제) | 출력 경로. 확장자만으로도 포맷 판정 가능. |
| `--format` | — | `png` / `jpeg`(`jpg`) / `webp`. `-o` 확장자로 추론 가능하면 생략 가능 |
| `--quality` | JPEG=92, WEBP=90, PNG=N/A | 1~100 |
| `--background` | `white` | JPEG로 변환 시 알파 평탄화 배경색 (`#FFFFFF`, `white`, `255,255,255` 등) |
| `--keep-original` | false | 원본 파일을 삭제하지 않음 |
| `--json` | false | JSON 결과 출력 |

### 주의

- **JPEG는 알파 미지원**: 알파가 있는 이미지를 JPEG로 변환하면 `--background`로 지정한 단색 위에 평탄화된다. 투명 영역이 의미 있는 에셋을 JPEG로 바꾸지 말 것.
- **덮어쓰기 전 git 확인**: 기본이 원본 삭제이므로, git 미추적 파일은 먼저 커밋/백업. 원본이 꼭 필요하면 `--keep-original`.
- **손실 압축**: JPEG/WEBP(lossy)는 재저장할수록 품질이 떨어진다. 원본 PNG를 보존한 채 배포용만 파생하는 워크플로우 권장.

## `resize` — 이미지 크기 변경

### 언제 쓰나
- AI 생성물이 너무 커서 실제 에셋 크기로 축소할 때
- 썸네일/아이콘 생성
- 픽셀 아트 업스케일(반드시 `--filter nearest`)
- 너비 또는 높이만 정해놓고 비율 유지 리사이즈

### 기본 동작: 덮어쓰기

`-o`를 생략하면 **입력 파일을 같은 경로에 덮어쓴다.** 다른 경로에 저장하려면 `-o`로 지정. 출력 포맷은 출력 경로의 확장자를 따르며, 확장자가 없거나 같으면 입력 포맷을 유지한다.

### 호출

```bash
python3 /home/alienspy/git/IronRose/tools/image-manipulation/cli-image-manipulation.py resize \
  <입력> \
  [-o <출력>] \
  [--width W] [--height H] | [--scale F] \
  [--fit stretch|contain|cover] \
  [--filter lanczos|bilinear|bicubic|nearest|box|hamming] \
  [--json]
```

### 옵션

| 옵션 | 기본값 | 설명 |
|---|---|---|
| `input` | — | 입력 이미지 경로 |
| `-o, --output` | 입력 경로(덮어쓰기) | 출력 경로. 확장자에 따라 포맷 변경 가능 |
| `--width` | — | 목표 너비(px). 단독 사용 시 높이는 비율 유지 |
| `--height` | — | 목표 높이(px). 단독 사용 시 너비는 비율 유지 |
| `--scale` | — | 균일 배율(예: `0.5`, `2`). `--width`/`--height`와 상호 배타 |
| `--fit` | `stretch` | `--width`/`--height` 둘 다 줄 때: `stretch`(비율 무시), `contain`(비율 유지 안쪽 맞춤), `cover`(비율 유지 바깥 덮기) |
| `--filter` | `lanczos` | 리샘플 필터. 픽셀 아트는 `nearest` 권장 |
| `--json` | false | JSON 결과 출력 |

### 예시

```bash
# 절반 크기로 축소 (덮어쓰기)
python3 ... resize "$PROJECT/Assets/Sprites/hero.png" --scale 0.5 --json

# 너비 256 고정, 높이는 비율 유지, 다른 경로 저장
python3 ... resize input.png -o thumb.png --width 256 --json

# 픽셀 아트 4배 업스케일
python3 ... resize pixel.png --scale 4 --filter nearest --json

# 정확히 512x512로 맞추되 비율 유지 (안쪽 맞춤)
python3 ... resize hero.png --width 512 --height 512 --fit contain --json
```

### 주의

- **LANCZOS는 픽셀 아트에 부적합**: 경계가 번진다. 픽셀 아트는 `--filter nearest`를 써야 도트가 보존된다.
- **확대는 품질 저하**: 래스터 업스케일은 본질적으로 정보 생성이 아니다. 가능하면 상위 해상도에서 생성한 뒤 축소.
- **메타 동기화 필요**: 크기가 바뀌면 PPU/피벗이 의미상 달라질 수 있다. 필요하면 rose-cli로 임포트 재실행.

## 워크플로우 가이드

### 기본 플로우

1. **전제 조건 확인** — 입력 파일이 알파 채널을 가진 PNG/WEBP인지 확인. 대상이 9-slice/UI 에셋이 **아님**을 확인.
2. **임포트 설정 체크** — rose-cli로 대상 스프라이트의 임포트 설정(`pivot`, `border`, `PPU`)을 미리 조회. 트리밍 후 좌표가 의미를 잃을 수 있는 값이 걸려 있으면 **작업 중단하고 유저에게 확인**.
3. **미리보기 실행** — 먼저 `-o <다른 경로>`로 결과를 저장해 Read로 확인한 뒤, 문제없으면 원본에 덮어쓰기.
4. **결과 확인** — `new_size`가 기대한 타이트한 bbox인지 확인. 전체가 사라지는 등 이상하면 `--threshold`를 낮추거나 원본 알파를 검토.
5. **에셋 재임포트** — rose-cli로 대상 스프라이트를 재임포트하여 PPU/pivot이 새로운 해상도에 맞게 재계산되도록 한다. 필요하면 pivot을 `center` 등으로 다시 지정.

### `ai-remove-bg`와의 연계

대부분의 경우 `ai-remove-bg`의 `*_nobg.png`가 입력이 된다:

```bash
# 1) 배경 제거
python3 tools/invoke-comfyui/cli-invoke-comfyui.py "..." \
  -o "$PROJECT/Assets/Sprites/hero.png" --rmbg --json --bypass-refine \
  --model z_image_turbo_nvfp4.safetensors

# 2) 투명 여백 트림
python3 tools/image-manipulation/cli-image-manipulation.py trim \
  "$PROJECT/Assets/Sprites/hero_nobg.png" \
  -o "$PROJECT/Assets/Sprites/hero.png" \
  --padding 2 --json
```

## 주의사항

- **9-slice/UI 메타 재조정은 유저 담당**: 9-slice border, pivot, PPU는 픽셀 좌표 기준이다. trim/resize로 캔버스가 바뀌면 의미가 어긋나므로, 작업 후 유저가 rose-cli로 재설정한다. AI가 자동 추정하지 말 것(신뢰도 낮음).
- **경계 잔재 픽셀**: BEN2 등 RMBG 결과물에 반투명한 잔재가 남아 bbox가 실제보다 크게 잡힐 수 있다. `--threshold 8~16`으로 약한 알파를 투명 취급해서 해결.
- **padding은 캔버스 내부로만 확장**: 원본 경계를 벗어나지 않도록 clamp 된다. 캔버스 바깥으로 여백을 추가하는 기능은 이 서브커맨드에 없음(필요하면 `pad` 서브커맨드 신설 요청).
- **결정적 결과**: 네트워크 호출 없음. 같은 입력/옵션이면 항상 같은 결과.
- **Pillow 의존**: 시스템에 Pillow가 없으면 에러(`pip install Pillow`). IronRose 개발 환경에는 이미 설치되어 있음.
- **rose-cli와의 관계**: 이 스킬은 파일만 교체한다. 프로젝트 에셋 메타(임포트 옵션)를 함께 갱신해야 하면 **rose-cli**로 재임포트할 것 — `.asset`/`.meta`를 직접 편집하지 않는다.
