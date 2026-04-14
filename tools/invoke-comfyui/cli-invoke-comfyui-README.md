# cli-tool

AlienHS 서비스를 CLI로 호출하는 독립 스크립트 모음. 외부 의존성 없이 Python 3.10+ stdlib만 사용하므로, 원하는 프로젝트 폴더에 그대로 복사해 쓰면 된다.

**내부망 전용.** LAN(`192.168.x.x`) 또는 같은 머신(`127.0.0.1`)에서 호출할 때만 동작한다.

## cli-invoke-comfyui.py

AlienHS `invoke-comfyui` 서비스를 호출해 이미지를 생성하고 로컬 파일로 저장. 선택적으로 배경 제거(BEN2 RMBG) 후처리까지 이어서 수행.

### 기본 사용

```bash
python cli-invoke-comfyui.py "푸른 바다의 고래, 저녁노을"
# → ./genimage_20260412_153000.png

python cli-invoke-comfyui.py "cat on a sofa" -o cat.png --bypass-refine
# → ./cat.png

python cli-invoke-comfyui.py "robot on white background" -o robot.png --rmbg
# → ./robot.png        (원본)
# → ./robot_nobg.png   (배경 제거, Alpha 투명)

python cli-invoke-comfyui.py "robot" --server http://192.168.0.10:25000

# 로컬 이미지를 업로드해서 배경 제거만 수행 (생성 스킵)
python cli-invoke-comfyui.py --rmbg-input ./photo.jpg
# → ./photo_nobg.png
python cli-invoke-comfyui.py --rmbg-input ./photo.jpg -o ./out/clean.png
# → ./out/clean.png
```

### 옵션

| 옵션 | 설명 |
|---|---|
| `prompt` | 이미지 생성 프롬프트 (`--rmbg-input` 모드에서는 생략) |
| `-o, --output` | 저장 경로 (기본: `./genimage_<timestamp>.png`) |
| `--server` | AlienHS 서버 URL (기본: `http://localhost:25000`, `ALIENHS_SERVER` 환경변수) |
| `--bypass-refine` | AI 프롬프트 정제 건너뛰고 원본 프롬프트 그대로 사용 |
| `--model` | ComfyUI 모델 파일명 |
| `--comfy-url` | ComfyUI 서버 URL 덮어쓰기 |
| `--rmbg` | 생성 후 배경 제거(BEN2) 수행. 결과는 `<원본stem>_nobg.png`로 저장 |
| `--rmbg-input` | 로컬 이미지 파일을 업로드해서 배경 제거만 수행 (생성 스킵). PNG/JPEG/WEBP |
| `--json` | JSON 출력 (Claude Code 툴 연동용) |

### Claude Code에서 사용

프로젝트 루트에 복사 후 Claude에게 Bash 도구로 실행시키면 된다:

```bash
python cli-invoke-comfyui.py "사용자가 원하는 이미지 설명" --json
python cli-invoke-comfyui.py "투명 배경의 로봇 아이콘" --rmbg --json
```

JSON 출력 예:

```json
{
  "ok": true,
  "paths": ["C:/proj/genimage_20260412_153000.png"],
  "server_filenames": ["<prompt_id>_1_ComfyUI_00001_.png"],
  "refined_prompt": "...",
  "prompt_id": "abc123",
  "nobg_paths": ["C:/proj/genimage_20260412_153000_nobg.png"]
}
```

`nobg_paths`는 `--rmbg`를 지정한 경우에만 포함된다.
