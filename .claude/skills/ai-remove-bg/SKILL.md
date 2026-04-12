---
name: ai-remove-bg
description: "AlienHS invoke-comfyui 서비스(ComfyUI 래퍼)를 호출해 AI 이미지를 생성한 뒤 BEN2 RMBG로 배경을 제거하거나, **기존 로컬 이미지의 배경만 제거**하여 **투명 배경 PNG**를 프로젝트 폴더에 저장합니다. 스프라이트, 캐릭터 컷아웃, 아이템 아이콘, UI 엘리먼트, 오브젝트 에셋 등 **알파 채널이 필요한 래스터 이미지**를 만들 때 사용하세요. 사용자가 '배경 제거', '투명 배경', '알파 PNG', '스프라이트 만들어줘', '컷아웃 이미지', 'RMBG', 'BEN2', 'no background', 'transparent bg', '누끼' 등을 요청하거나, 게임 오브젝트/캐릭터/아이템처럼 배경이 없는 래스터 에셋이 필요한 맥락이라면 이 스킬을 사용하세요. 배경이 있는 일반 이미지(풍경, 씬, 컨셉아트)는 `ai-image-forge`를, 단순 벡터 아이콘은 `svg-image-forge`를 우선 고려합니다. **내부망 전용**: LAN 또는 같은 머신에서만 동작합니다."
---

# AI Remove BG

AlienHS `invoke-comfyui` CLI(`tools/invoke-comfyui/cli-invoke-comfyui.py`)를 호출해 **투명 배경 PNG**를 프로젝트에 저장하는 스킬. 두 가지 모드를 지원한다:

- **생성 + 배경 제거** (`--rmbg`): 프롬프트로 이미지를 생성한 뒤 BEN2로 배경 제거.
- **기존 이미지 배경 제거** (`--rmbg-input <파일>`): 로컬 PNG/JPEG/WEBP를 업로드해 배경만 제거 (생성 스킵).

스프라이트, 캐릭터 컷아웃, 아이템 아이콘, 오브젝트 에셋처럼 **알파 채널이 필요한 래스터 이미지**에 적합하다.
- 배경이 있는 일반 이미지(풍경/씬/컨셉아트) → `ai-image-forge`
- 단순 아이콘·로고·UI 요소 → `svg-image-forge`

## 사전 조건 확인 (필수)

이미지 생성 전에 다음을 확인한다:

1. **Python 3.10+** — stdlib만 쓰므로 추가 패키지 설치는 불필요하다.
2. **연결된 프로젝트** — `~/.ironrose/settings.toml`의 `last_project`가 유효해야 한다. **프로젝트가 연결되어 있지 않으면 이미지를 생성하지 말고 사용자에게 안내한 뒤 중단한다.**
3. **서버 주소 결정** — 아래 우선순위로 서버/ComfyUI URL을 결정한다:
   1. 이번 턴에 사용자가 명시한 주소
   2. `<last_project>/memory/ai-remove-bg-memory.md`에 저장된 **마지막 성공 주소**
   3. `<last_project>/memory/ai-image-forge-memory.md`에 저장된 마지막 성공 주소 (같은 서버를 공유하므로 폴백)
   4. `ALIENHS_SERVER` 환경변수
   5. 그래도 없으면 사용자에게 물어본다 (추측 금지)

필요 시 사용자에게 서버 URL을 물어본다. 호출 후 연결 실패가 나면 서버가 떠 있는지 안내하고 중단한다.

## 워크플로우

### 1. 요청 파악

- **무엇을**: 주제/소재 (캐릭터, 아이템, 오브젝트, 스프라이트 등)
- **스타일**: 사진풍/애니메/수채화/픽셀아트 등 화풍
- **용도**: 스프라이트/아이콘/캐릭터 등 — 명명과 저장 위치에 반영
- **저장 위치**: 프로젝트 `Assets/` 하위의 적절한 폴더 (아래 4단계 참고). **프로젝트가 연결되어 있지 않으면 생성하지 않는다.**

### 2. 프롬프트 작성

**정책: 항상 `--bypass-refine`을 사용한다.** 서버 측 AI 정제는 의도치 않은 요소를 섞을 수 있으므로, 프롬프트는 본 스킬이 직접 완성도 높은 형태로 작성한다.

영어로 작성할 것. 한국어 키워드는 모델이 혼란스러워할 수 있어 권장하지 않는다.

**배경 제거용 프롬프트의 핵심 원칙:**

- **배경 단순화 지시 필수**: BEN2 RMBG의 정확도를 높이려면 생성 단계에서부터 배경을 단순하게 만들 것
  - 추천 키워드: `on pure white background`, `isolated on white background`, `plain white backdrop`, `studio white background`, `centered subject, clean background`
  - 배경에 디테일이 많으면 RMBG가 피사체 경계를 놓칠 수 있다
- **피사체 중앙 배치**: `centered composition`, `single subject`, `full body visible`
- **주제 명확화**: 단일 피사체로 한정 (`single`, `only one`). 여러 개가 섞이면 RMBG 결과가 지저분해진다
- **스타일 명시**: `photorealistic`, `anime illustration`, `watercolor`, `pixel art`, `game asset`, `concept art`
- **조명**: `soft even lighting`, `studio lighting`, `soft shadow beneath` — 강한 그림자는 RMBG가 배경으로 오인할 수 있음
- **디테일/품질 태그**: `highly detailed`, `sharp focus`, `clean edges`, `high contrast silhouette`
- **배제 키워드**: `no text`, `no watermark`, `no background elements`, `no floor`, `no wall`

**예시 프롬프트:**
- 캐릭터: `young female mage, silver robe, holding glowing staff, anime illustration style, centered full body, standing pose, on pure white background, soft even lighting, highly detailed, clean edges, no text`
- 아이템: `single ornate golden key, game asset style, isolated on white background, top-down 3/4 view, clean edges, sharp focus, no shadow, no text`
- 오브젝트: `wooden treasure chest, closed, painterly concept art, centered, pure white background, soft studio lighting, highly detailed, no floor, no wall`

### 3. 이미지 생성 + 배경 제거 호출

번들된 CLI를 호출한다. **`--json`, `--bypass-refine`, `--rmbg`, `--model z_image_turbo_nvfp4.safetensors`를 기본으로 항상 포함**한다:

```bash
python3 /home/alienspy/git/IronRose/tools/invoke-comfyui/cli-invoke-comfyui.py \
  "<영문 프롬프트>" \
  -o <저장 경로.png> \
  --json \
  --bypass-refine \
  --rmbg \
  --model z_image_turbo_nvfp4.safetensors \
  [--comfy-url <http://...>] \
  [--server <http://host:port>]
```

CLI는 두 개의 파일을 만든다:
- `<저장 경로>.png` — 원본(배경 포함)
- `<저장 경로>_nobg.png` — 배경 제거된 투명 PNG (본 스킬의 주 결과물)

**모델 선택 정책:**
- **1순위**: `z_image_turbo_nvfp4.safetensors` (NVFP4 양자화, 속도 우선)
- **폴백**: `z_image_turbo_bf16.safetensors` (BF16, GPU가 NVFP4 미지원이거나 1순위 실패 시)
- 다른 모델은 사용자가 명시적으로 요청할 때만 사용

**주요 옵션:**

| 옵션 | 설명 |
|---|---|
| `prompt` | (위치 인자) 이미지 프롬프트. 따옴표로 감쌀 것 |
| `-o, --output` | 저장 경로. 생략 시 `./genimage_<timestamp>.png` |
| `--bypass-refine` | **항상 포함**. 서버 측 AI 정제를 스킵 |
| `--rmbg` | **항상 포함**. 생성 후 BEN2로 배경 제거, `_nobg.png` 추가 저장 |
| `--model` | ComfyUI 모델. **`z_image_turbo_nvfp4.safetensors` 우선**, 실패 시 `z_image_turbo_bf16.safetensors` 폴백 |
| `--comfy-url` | ComfyUI 서버 URL 덮어쓰기 |
| `--server` | AlienHS 서버 URL (기본: `$ALIENHS_SERVER` 또는 `http://localhost:25000`) |
| `--json` | JSON 출력 (툴 연동 필수) |
| `--rmbg-input` | **기존 로컬 이미지** 업로드 후 배경만 제거 (생성 스킵). `prompt`, `--rmbg`와 상호 배타. |

**JSON 결과 형식:**

```json
{
  "ok": true,
  "paths": ["/abs/path/out.png"],
  "nobg_paths": ["/abs/path/out_nobg.png"],
  "refined_prompt": "",
  "prompt_id": "..."
}
```

실패 시:

```json
{"ok": false, "error": "..."}
```

**주 결과물은 `nobg_paths`**이다. 원본은 참고/비교용.

### 3-B. 기존 이미지 배경 제거 (생성 스킵)

로컬에 있는 PNG/JPEG/WEBP의 배경만 제거하려면 `--rmbg-input <파일>`을 사용한다. `prompt`, `--rmbg`는 함께 쓸 수 없다.

```bash
python3 /home/alienspy/git/IronRose/tools/invoke-comfyui/cli-invoke-comfyui.py \
  --rmbg-input <입력파일> \
  -o <저장 경로.png> \
  --json \
  [--comfy-url <http://...>] \
  [--server <http://host:port>]
```

- `-o`를 생략하면 `<입력stem>_nobg.png`로 입력 파일 옆에 저장.
- `-o`를 **입력과 동일 경로**로 지정하면 원본을 투명 PNG로 **덮어쓴다**. 덮어쓸 원본이 git에 추적되지 않은 상태라면, 먼저 백업을 만들 것.
- JSON 결과의 `nobg_paths`만 채워지고 `paths`는 빈 배열.

**배치 처리 (디렉토리 전체)** — CLI는 단건만 지원하므로 쉘 루프로 감싼다:

```bash
for f in /path/to/Assets/Art/*.png; do
  python3 /home/alienspy/git/IronRose/tools/invoke-comfyui/cli-invoke-comfyui.py \
    --rmbg-input "$f" -o "$f" --json \
    --server <...> --comfy-url <...>
done
```

### 4. 저장

**저장 경로 결정 (프로젝트 필수):**

1. `~/.ironrose/settings.toml`을 읽어 `last_project` 값을 확인한다.
2. `last_project`가 비어 있거나 해당 경로가 존재하지 않으면 **이미지를 생성하지 않고 작업을 중단**한다. 사용자에게 다음과 같이 안내할 것:
   > 연결된 IronRose 프로젝트가 없어서 AI 이미지를 생성할 수 없습니다.
   > 에디터에서 프로젝트를 열어 `last_project`가 설정된 뒤 다시 요청해 주세요.
3. 프로젝트가 확인되면 해당 프로젝트의 `Assets/` 하위 용도별 폴더에 저장한다:
   - 스프라이트/2D 오브젝트: `Assets/Sprites/`
   - 캐릭터: `Assets/Characters/`
   - 아이템/아이콘: `Assets/Items/` 또는 `Assets/Icons/`
   - 그 외 투명 에셋: `Assets/Art/`
   - 필요하면 폴더를 새로 만들어도 된다.
4. 사용자가 명시적으로 경로를 지정한 경우에도 그 경로가 프로젝트 `Assets/` 내부인지 확인하고, 외부라면 사용자에게 확인을 받는다.

**명명 규칙:**
- 파일명은 내용을 반영한 snake_case (예: `hero_mage.png`, `golden_key.png`, `wooden_chest.png`)
- CLI가 자동으로 원본에는 지정한 이름을, 투명 버전에는 `_nobg` 접미사를 붙인다 (`hero_mage.png`, `hero_mage_nobg.png`)
- 동일 프롬프트로 여러 장이 반환되면 `_1`, `_2` 접미사가 추가로 붙는다

**원본 처리:**
- 원본(`hero_mage.png`)은 기본적으로 함께 저장된다. 사용자가 투명 버전만 필요하다고 명시하면, 투명 버전 확인 후 원본을 `rm`으로 삭제할 수 있다 (사용자에게 확인 후).

### 5. 결과 확인

`nobg_paths[0]`을 Read 도구로 열어 사용자에게 확인시킨다. 필요 시 원본(`paths[0]`)도 함께 열어 비교 가능하게 한다.

**배경 제거 품질 체크:**
- 피사체 경계가 깔끔한가? (머리카락, 털, 반투명 부분 특히 주의)
- 피사체 일부가 잘려나가지 않았나?
- 배경 잔재(회색 가장자리, 흐린 픽셀)가 남지 않았나?

**품질이 불만족스러우면:**
- 배경 단순화 키워드 강화 (`on pure white background, plain backdrop, clean edges`)
- 피사체 실루엣 대비 강화 (`high contrast silhouette`, `bold outline`)
- 강한 그림자 제거 (`no shadow`, `soft ambient light only`)
- 프롬프트를 보강하여 재생성
- 모델을 `z_image_turbo_bf16.safetensors`로 교체 시도

### 6. 서버 주소 기록 (성공 시에만)

`ok: true`로 생성과 배경 제거가 **실제로 성공했을 때에만** 이번에 사용한 서버/ComfyUI/모델 정보를 `<last_project>/memory/ai-remove-bg-memory.md`에 덮어쓴다. 실패·중단·에러 응답에서는 기록하지 않는다.

**파일 포맷 (덮어쓰기):**

```markdown
# ai-remove-bg — Last Successful Settings

- updated: 2026-04-12T15:30:00
- server: http://192.168.0.5:25000
- comfy_url: http://192.168.0.18:23000
- model: z_image_turbo_nvfp4.safetensors
```

다음 호출부터 사용자가 서버 주소를 지정하지 않으면 이 파일의 값을 재사용한다. `memory/` 폴더가 없으면 만들고, 파일은 항상 최신 성공 설정으로 덮어쓴다(히스토리 보존 불필요).

## 전체 흐름 예시

사용자: "투명 배경의 보물상자 스프라이트 만들어줘"

1. `~/.ironrose/settings.toml`에서 `last_project` 경로 확인. 없거나 유효하지 않으면 **생성하지 않고 중단**하여 사용자에게 프로젝트 연결을 요청
2. 저장 경로 결정: `<project>/Assets/Sprites/treasure_chest.png`
3. CLI 호출:
   ```bash
   python3 /home/alienspy/git/IronRose/tools/invoke-comfyui/cli-invoke-comfyui.py \
     "single ornate wooden treasure chest with golden trim, closed lid, painterly game asset style, centered composition, isolated on pure white background, soft even studio lighting, high contrast silhouette, highly detailed, sharp focus, clean edges, no shadow, no floor, no text, no watermark" \
     -o "<project>/Assets/Sprites/treasure_chest.png" \
     --json \
     --bypass-refine \
     --rmbg \
     --model z_image_turbo_nvfp4.safetensors
   ```
4. JSON 파싱 → `nobg_paths[0]`(`treasure_chest_nobg.png`)을 Read로 열어 결과 확인
5. 경계 품질이 좋으면 완료. 아쉬우면 프롬프트 보강 후 재생성

## 주의사항

- **내부망 전용**: AlienHS 서버는 LAN(`192.168.x.x`) 또는 로컬(`127.0.0.1`)에서만 인증을 통과한다. 외부망이라면 즉시 중단하고 사용자에게 안내.
- **타임아웃**: CLI 기본 타임아웃은 600초. 생성 + RMBG 두 단계를 모두 거치므로 일반 생성보다 조금 더 오래 걸릴 수 있다.
- **결정적 재현 안 됨**: 같은 프롬프트라도 매번 다른 결과가 나올 수 있다. 시드 제어는 현재 CLI에 노출되어 있지 않으므로, 재현이 필요하면 엔진/CLI 측 개선이 선행되어야 한다 (→ 유저에게 알릴 것).
- **기존 이미지 배경 제거**: `--rmbg-input <파일>` 옵션으로 지원. 단, **단건 파일만** 처리하며 디렉토리/재귀 배치는 쉘 루프로 감싸야 한다. 입력 포맷은 PNG/JPEG/WEBP만 허용.
- **덮어쓰기 주의**: `-o <원본경로>`로 원본을 덮어쓸 때, 원본이 git에 추적되지 않은 상태(`git status`로 확인)라면 되돌릴 수 없다. 먼저 백업 디렉토리로 복사한 뒤 진행할 것.
- **BEN2 품질 한계**: 머리카락/털/반투명 요소, 피사체와 배경의 대비가 낮은 경우 경계가 지저분해질 수 있다. 생성 프롬프트에서 배경을 순백에 가깝게 고정하는 것이 가장 효과적이다.
- **게임 에셋 크기**: 모델 출력 크기는 모델/서버 설정에 의존한다. power-of-2 크기가 필요하면 후처리로 리사이즈 고려 (svg-image-forge의 `svg2png.py`나 PIL 사용).
- **SVG vs AI**: 단순 아이콘·로고·UI 요소는 AI로 생성하면 오히려 정돈되지 않은 결과가 나오기 쉽다. 이 경우 `svg-image-forge`를 우선 사용.
- **라이선스**: 생성 이미지가 상용 배포에 문제 없는지 사용 모델의 라이선스를 확인할 것 (프로젝트 `project_imagesharp_license` 메모리 참고).
