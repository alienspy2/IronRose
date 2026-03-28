---
name: image-forge
description: "사용자가 요청한 이미지를 SVG 코드로 생성하고 PNG로 변환하여 프로젝트 폴더에 저장합니다. 아이콘, UI 요소, 로고, 배지, 배경 패턴, 다이어그램, 플레이스홀더 이미지, 게임 에셋 등 어떤 종류의 이미지든 만들 수 있습니다. 사용자가 '이미지 만들어줘', '아이콘 만들어', 'SVG 생성', 'PNG 만들어', '로고 디자인', '이미지 그려줘', 'UI 아이콘', '버튼 이미지', '배경 이미지', '스프라이트', '텍스처 생성' 등 이미지 생성/제작과 관련된 요청을 할 때 이 스킬을 사용하세요. 사용자가 명시적으로 이미지 파일을 요청하지 않더라도, 시각적 에셋이 필요한 맥락이라면 적극적으로 이 스킬을 활용하세요."
---

# Image Forge

사용자의 요청에 맞는 이미지를 SVG로 생성하고 PNG로 변환하여 프로젝트에 저장하는 스킬.

## 사전 조건 확인 (필수)

이미지 생성을 시작하기 전에 반드시 의존 패키지가 설치되어 있는지 확인한다:

```bash
python3 -c "import cairosvg; from PIL import Image; print('OK')"
```

실패하면 사용자에게 아래와 같이 안내하고 **작업을 중단**한다:

> 이미지 생성에 필요한 Python 패키지가 설치되어 있지 않습니다.
> 다음 명령으로 설치해주세요:
> ```
> pip install cairosvg pillow
> ```
> 설치 후 다시 요청해주세요.

패키지가 정상 확인된 경우에만 이후 단계를 진행한다.

## 워크플로우

### 1. 요청 파악

사용자의 요청에서 다음을 파악한다:
- **무엇을**: 어떤 이미지인가 (아이콘, 로고, UI 요소, 배경 등)
- **크기**: 원하는 해상도 (지정 없으면 용도에 맞게 판단)
- **스타일**: 색상, 분위기, 디자인 방향
- **저장 위치**: 프로젝트 내 경로 (지정 없으면 기본 경로 사용)

### 2. SVG 생성

SVG 코드를 직접 작성한다. SVG는 벡터 기반이므로 깔끔한 도형, 아이콘, UI 요소에 적합하다.

**SVG 작성 원칙:**
- `viewBox`를 항상 설정하여 스케일링이 자연스럽도록 한다
- 복잡한 형태는 `<path>`의 d 속성으로, 단순한 형태는 `<rect>`, `<circle>`, `<ellipse>`, `<polygon>` 등 기본 도형으로 구성한다
- 그라디언트(`<linearGradient>`, `<radialGradient>`), 필터(`<filter>`), 클리핑(`<clipPath>`) 등 SVG 고급 기능을 적극 활용한다
- 텍스트는 `<text>` 요소로 넣되, 폰트 호환성을 위해 가능하면 기본 폰트(sans-serif, serif, monospace)를 사용한다
- 불필요한 공백이나 주석 없이 깔끔하게 작성한다

**이미지 유형별 권장 크기 (viewBox 기준):**

| 용도 | viewBox | PNG 추천 크기 |
|------|---------|--------------|
| 앱 아이콘 | `0 0 512 512` | 512x512 |
| UI 아이콘 (작은) | `0 0 24 24` 또는 `0 0 32 32` | 64x64 또는 128x128 |
| UI 버튼/배지 | `0 0 200 60` | 400x120 |
| 로고 | `0 0 400 400` | 512x512 또는 1024x1024 |
| 배경/텍스처 | `0 0 512 512` | 1024x1024 |
| 배너 | `0 0 800 200` | 1600x400 |
| 스프라이트 | `0 0 64 64` | 128x128 또는 256x256 |

사용자가 크기를 지정하면 그에 따른다.

### 3. PNG 변환

번들된 변환 스크립트를 사용한다:

```bash
python3 /home/alienspy/git/IronRose/.claude/skills/image-forge/scripts/svg2png.py \
  <input.svg> <output.png> [--width W] [--height H] [--scale S]
```

**옵션:**
- `--width W`: 출력 PNG 너비 (픽셀). 높이를 생략하면 비율 유지
- `--height H`: 출력 PNG 높이 (픽셀). 너비를 생략하면 비율 유지
- `--scale S`: 배율 (기본 1.0). width/height가 지정되면 무시됨

### 4. 저장

**기본 저장 경로 결정:**

1. 사용자가 경로를 지정하면 그 경로에 저장
2. MyGame 프로젝트가 존재하면: `/home/alienspy/git/MyGame/Assets/` 하위의 적절한 폴더
3. 그 외: 현재 작업 디렉토리

**저장 파일:**
- SVG 원본과 PNG 변환본 **모두** 저장한다
- SVG는 나중에 수정/재변환이 가능하므로 항상 보존한다
- 파일명은 사용자가 지정하지 않으면 내용을 반영하여 snake_case로 짓는다 (예: `play_button_icon.svg`, `play_button_icon.png`)

### 5. 결과 확인

PNG 파일을 Read 도구로 열어 사용자에게 보여준다. 생성된 이미지가 요청과 맞는지 확인할 수 있도록 한다.

## 전체 흐름 예시

사용자: "재생 버튼 아이콘 만들어줘"

1. SVG 파일 작성 → `/home/alienspy/git/MyGame/Assets/Icons/play_button.svg`
2. PNG 변환 실행:
   ```bash
   python3 /home/alienspy/git/IronRose/.claude/skills/image-forge/scripts/svg2png.py \
     /home/alienspy/git/MyGame/Assets/Icons/play_button.svg \
     /home/alienspy/git/MyGame/Assets/Icons/play_button.png \
     --width 128 --height 128
   ```
3. Read 도구로 PNG를 열어 사용자에게 결과 확인
4. 수정 요청이 있으면 SVG 수정 후 재변환

## 주의사항

- SVG의 한계를 인지한다: 사진 같은 래스터 이미지나 복잡한 일러스트레이션은 SVG로 표현이 어렵다. 벡터로 표현 가능한 범위 내에서 최대한 좋은 결과물을 만든다.
- CairoSVG가 지원하지 않는 SVG 기능(일부 CSS, 외부 폰트, 복잡한 필터 체인)은 피한다.
- 투명 배경이 필요한 경우 SVG에 배경 사각형을 넣지 않는다 (PNG는 기본적으로 투명).
- 게임 에셋의 경우 power-of-2 크기(64, 128, 256, 512, 1024)를 권장한다.
