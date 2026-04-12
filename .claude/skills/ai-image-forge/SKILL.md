---
name: ai-image-forge
description: "AlienHS invoke-comfyui 서비스(ComfyUI 래퍼)를 호출해 AI 이미지를 생성하고 프로젝트 폴더에 PNG로 저장합니다. 사진풍·일러스트·컨셉아트·캐릭터·배경·텍스처 등 SVG로는 표현이 어려운 래스터 이미지를 만들 때 사용하세요. 사용자가 'AI 이미지 생성', '그림 그려줘', '컨셉아트', '캐릭터 이미지', '배경 일러스트', '포토리얼 텍스처', 'ComfyUI 호출', 'SD로 만들어줘', 'invoke-comfyui', 'genimage' 등을 요청하거나, 시각적으로 복잡해서 SVG보다는 래스터 생성이 적합한 맥락이라면 이 스킬을 사용하세요. 단순 아이콘/UI 요소/로고처럼 벡터로 충분한 경우에는 svg-image-forge를 우선 고려합니다. **내부망 전용**: LAN 또는 같은 머신에서만 동작합니다."
---

# AI Image Forge

AlienHS `invoke-comfyui` CLI(`tools/invoke-comfyui/cli-invoke-comfyui.py`)를 호출해 AI 이미지를 생성하고 프로젝트에 저장하는 스킬.

SVG로 표현이 어려운 사진풍/일러스트/컨셉아트/배경/텍스처 등에 적합하다. 벡터로 충분한 아이콘·UI 요소는 `svg-image-forge`를 먼저 고려할 것.

## 사전 조건 확인 (필수)

이미지 생성 전에 다음을 확인한다:

1. **Python 3.10+** — stdlib만 쓰므로 추가 패키지 설치는 불필요하다.
2. **연결된 프로젝트** — `~/.ironrose/settings.toml`의 `last_project`가 유효해야 한다. **프로젝트가 연결되어 있지 않으면 이미지를 생성하지 말고 사용자에게 안내한 뒤 중단한다.**
3. **서버 주소 결정** — 아래 우선순위로 서버/ComfyUI URL을 결정한다:
   1. 이번 턴에 사용자가 명시한 주소
   2. `<last_project>/memory/ai-image-forge-memory.md`에 저장된 **마지막 성공 주소**
   3. `ALIENHS_SERVER` 환경변수
   4. 그래도 없으면 사용자에게 물어본다 (추측 금지)

필요 시 사용자에게 서버 URL을 물어본다. 호출 후 연결 실패가 나면 서버가 떠 있는지 안내하고 중단한다.

## 워크플로우

### 1. 요청 파악

- **무엇을**: 주제/소재 (캐릭터, 배경, 텍스처, 컨셉아트 등)
- **스타일**: 사진풍/애니메/수채화/픽셀아트 등 화풍
- **크기/비율**: 사용자가 지정하지 않으면 용도에 맞춰 판단 (모델 기본값을 따르는 경우가 많음)
- **저장 위치**: 프로젝트 `Assets/` 하위의 적절한 폴더 (아래 4단계 참고). **프로젝트가 연결되어 있지 않으면 생성하지 않는다.**

### 2. 프롬프트 작성

**정책: 항상 `--bypass-refine`을 사용한다.** 서버 측 AI 정제는 의도치 않은 요소를 섞을 수 있으므로, 프롬프트는 본 스킬이 직접 완성도 높은 형태로 작성한다.

영어로 작성할 것. 한국어 키워드는 모델이 혼란스러워할 수 있어 권장하지 않는다.

**좋은 프롬프트 작성 원칙:**
- **주제 먼저**: 피사체/장면을 구체적으로 (예: `young knight with silver armor`, `dense pine forest at dawn`)
- **스타일 명시**: `photorealistic`, `anime illustration`, `watercolor painting`, `pixel art`, `oil painting`, `concept art` 등
- **구도/카메라**: `close-up portrait`, `wide landscape shot`, `low angle`, `isometric view`, `top-down`
- **조명/분위기**: `soft volumetric light`, `golden hour`, `moody dark atmosphere`, `rim lighting`, `studio lighting`
- **디테일/품질 태그**: `highly detailed`, `sharp focus`, `8k`, `cinematic`, `trending on artstation`
- **색/팔레트**: 필요하면 명시 (예: `muted earth tones`, `vibrant neon palette`)
- **불필요한 요소 제외**: 원치 않는 요소가 있다면 프롬프트에 명시적으로 배제 키워드를 추가 (예: `no text`, `no watermark`, `clean background`)
- 쉼표로 태그를 구분하고, 중요한 것을 앞쪽에 배치

### 3. 이미지 생성 호출

번들된 CLI를 직접 호출한다. **`--json`, `--bypass-refine`, `--model z_image_turbo_nvfp4.safetensors`를 기본으로 항상 포함**한다:

```bash
python3 /home/alienspy/git/IronRose/tools/invoke-comfyui/cli-invoke-comfyui.py \
  "<영문 프롬프트>" \
  -o <저장 경로.png> \
  --json \
  --bypass-refine \
  --model z_image_turbo_nvfp4.safetensors \
  [--comfy-url <http://...>] \
  [--server <http://host:port>]
```

**모델 선택 정책:**
- **1순위**: `z_image_turbo_nvfp4.safetensors` (NVFP4 양자화, 속도 우선)
- **폴백**: `z_image_turbo_bf16.safetensors` (BF16, GPU가 NVFP4 미지원이거나 1순위가 실패/에러일 때)
- 다른 모델은 사용자가 명시적으로 요청할 때만 사용

**주요 옵션:**

| 옵션 | 설명 |
|---|---|
| `prompt` | (위치 인자) 이미지 프롬프트. 따옴표로 감쌀 것 |
| `-o, --output` | 저장 경로. 생략 시 `./genimage_<timestamp>.png` |
| `--bypass-refine` | **항상 포함**. 서버 측 AI 정제를 스킵하고 작성한 프롬프트를 그대로 사용 |
| `--model` | ComfyUI 모델 파일명. **`z_image_turbo_nvfp4.safetensors`를 우선 사용**하고, 실패/미지원 시 `z_image_turbo_bf16.safetensors`로 폴백 |
| `--comfy-url` | ComfyUI 서버 URL 덮어쓰기 |
| `--server` | AlienHS 서버 URL (기본: `$ALIENHS_SERVER` 또는 `http://localhost:25000`) |
| `--json` | JSON 출력 (툴 연동 필수) |

**JSON 결과 형식:**

```json
{"ok": true, "paths": ["/abs/path/out.png"], "refined_prompt": "...", "prompt_id": "..."}
```

실패 시:

```json
{"ok": false, "error": "..."}
```

### 4. 저장

**저장 경로 결정 (프로젝트 필수):**

1. `~/.ironrose/settings.toml`을 읽어 `last_project` 값을 확인한다.
2. `last_project`가 비어 있거나 해당 경로가 존재하지 않으면 **이미지를 생성하지 않고 작업을 중단**한다. 사용자에게 다음과 같이 안내할 것:
   > 연결된 IronRose 프로젝트가 없어서 AI 이미지를 생성할 수 없습니다.
   > 에디터에서 프로젝트를 열어 `last_project`가 설정된 뒤 다시 요청해 주세요.
3. 프로젝트가 확인되면 해당 프로젝트의 `Assets/` 하위 용도별 폴더에 저장한다 (예: `Assets/Textures/`, `Assets/Art/`, `Assets/Backgrounds/`, `Assets/Characters/`). 필요하면 폴더를 새로 만들어도 된다.
4. 사용자가 명시적으로 경로를 지정한 경우에도 그 경로가 프로젝트 `Assets/` 내부인지 확인하고, 외부라면 사용자에게 확인을 받는다.

**명명 규칙:**
- 파일명은 내용을 반영한 snake_case (예: `forest_background.png`, `hero_portrait.png`)
- 동일 프롬프트로 여러 장 생성되면 CLI가 자동으로 `_1`, `_2` 접미사를 붙인다

### 5. 결과 확인

생성된 PNG를 Read 도구로 열어 사용자에게 확인시킨다. `--bypass-refine` 사용 시 `refined_prompt`는 보통 비어 있다.

수정 요청이 있으면:
- 프롬프트 키워드를 보강·교체해 재생성
- 스타일 변경은 스타일 태그 조정 또는 `--model` 교체
- 불필요한 요소가 계속 섞여 나오면 프롬프트 앞부분에 명시적 배제 키워드(`no text`, `no people` 등)를 추가

### 6. 서버 주소 기록 (성공 시에만)

`ok: true`로 생성이 **실제로 성공했을 때에만** 이번에 사용한 서버/ComfyUI/모델 정보를 `<last_project>/memory/ai-image-forge-memory.md`에 덮어쓴다. 실패·중단·에러 응답에서는 기록하지 않는다.

**파일 포맷 (덮어쓰기):**

```markdown
# ai-image-forge — Last Successful Settings

- updated: 2026-04-12T15:30:00
- server: http://192.168.0.5:25000
- comfy_url: http://192.168.0.18:23000
- model: z_image_turbo_nvfp4.safetensors
```

다음 호출부터 사용자가 서버 주소를 지정하지 않으면 이 파일의 값을 재사용한다. `memory/` 폴더가 없으면 만들고, 파일은 항상 최신 성공 설정으로 덮어쓴다(히스토리 보존 불필요).

## 전체 흐름 예시

사용자: "판타지 숲 배경 그려줘"

1. `~/.ironrose/settings.toml`에서 `last_project` 경로 확인. 없거나 경로가 유효하지 않으면 **생성하지 않고 중단**하여 사용자에게 프로젝트 연결을 요청
2. 저장 경로 결정: `<project>/Assets/Backgrounds/fantasy_forest.png`
3. CLI 호출:
   ```bash
   python3 /home/alienspy/git/IronRose/tools/invoke-comfyui/cli-invoke-comfyui.py \
     "mystical fantasy forest landscape, ancient giant trees, thick moss covering roots, soft volumetric god rays through canopy, morning mist, painterly concept art style, cinematic wide shot, highly detailed, no text, no people" \
     -o "<project>/Assets/Backgrounds/fantasy_forest.png" \
     --json \
     --bypass-refine \
     --model z_image_turbo_nvfp4.safetensors
   ```
4. JSON 파싱 → `paths[0]`을 Read로 열어 사용자에게 결과 확인
5. 필요 시 프롬프트 보강 후 재생성

## 주의사항

- **내부망 전용**: AlienHS 서버는 LAN(`192.168.x.x`) 또는 로컬(`127.0.0.1`)에서만 인증을 통과한다. 외부망이라면 즉시 중단하고 사용자에게 안내.
- **타임아웃**: CLI 기본 타임아웃은 600초. 서버가 바쁘면 그 안에서 대기한다.
- **결정적 재현 안 됨**: 같은 프롬프트라도 매번 다른 결과가 나올 수 있다. 시드 제어는 현재 CLI에 노출되어 있지 않으므로, 재현이 필요하면 엔진/CLI 측 개선이 선행되어야 한다 (→ 유저에게 알릴 것).
- **게임 에셋 크기**: 모델 출력 크기는 모델/서버 설정에 의존한다. power-of-2 크기가 필요하면 후처리로 리사이즈 고려 (svg-image-forge의 `svg2png.py`나 PIL 사용).
- **SVG vs AI**: 단순 아이콘·로고·UI 요소는 AI로 생성하면 오히려 정돈되지 않은 결과가 나오기 쉽다. 이 경우 `svg-image-forge`를 우선 사용.
- **라이선스**: 생성 이미지가 상용 배포에 문제 없는지 사용 모델의 라이선스를 확인할 것 (프로젝트 `project_imagesharp_license` 메모리 참고).
