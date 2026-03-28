# CLI UI 명령 핸들러 구현 (Phase 1~4)

## 수행한 작업
- CLI에 UI 시스템 관련 86개 명령 핸들러를 구현했다.
- 기존 `CliCommandDispatcher` 클래스를 `partial class`로 변환하고, 새 파일 `CliCommandDispatcher.UI.cs`에 모든 UI 핸들러를 분리 배치했다.
- `RegisterHandlers()` 끝에 `RegisterUIHandlers()` 호출을 추가했다.

### 구현한 명령 카테고리 (총 86개)
- **편의 생성** (10개): ui.create_canvas, ui.create_text, ui.create_image, ui.create_panel, ui.create_button, ui.create_toggle, ui.create_slider, ui.create_input, ui.create_layout, ui.create_scroll
- **트리/조회** (4개): ui.tree, ui.list, ui.find, ui.canvas.list
- **RectTransform** (8개): ui.rect.get, ui.rect.set_anchors, ui.rect.set_position, ui.rect.set_size, ui.rect.set_pivot, ui.rect.set_offsets, ui.rect.set_preset, ui.rect.get_world_rect
- **Canvas** (6개): ui.canvas.info, ui.canvas.set_render_mode, ui.canvas.set_sorting_order, ui.canvas.set_reference_resolution, ui.canvas.set_scale_mode, ui.canvas.set_match
- **UIText** (6개): ui.text.info, ui.text.set_text, ui.text.set_font_size, ui.text.set_color, ui.text.set_alignment, ui.text.set_overflow
- **UIImage** (5개): ui.image.info, ui.image.set_color, ui.image.set_type, ui.image.set_sprite, ui.image.set_preserve_aspect
- **UIPanel** (4개): ui.panel.info, ui.panel.set_color, ui.panel.set_sprite, ui.panel.set_type
- **UIButton** (4개): ui.button.info, ui.button.set_interactable, ui.button.set_colors, ui.button.set_transition
- **UIToggle** (4개): ui.toggle.info, ui.toggle.set_on, ui.toggle.set_interactable, ui.toggle.set_colors
- **UISlider** (7개): ui.slider.info, ui.slider.set_value, ui.slider.set_range, ui.slider.set_direction, ui.slider.set_whole_numbers, ui.slider.set_interactable, ui.slider.set_colors
- **UIInputField** (8개): ui.input.info, ui.input.set_text, ui.input.set_placeholder, ui.input.set_font_size, ui.input.set_max_length, ui.input.set_content_type, ui.input.set_interactable, ui.input.set_read_only
- **UILayoutGroup** (6개): ui.layout.info, ui.layout.set_direction, ui.layout.set_spacing, ui.layout.set_padding, ui.layout.set_child_alignment, ui.layout.set_force_expand
- **UIScrollView** (5개): ui.scroll.info, ui.scroll.set_scroll_position, ui.scroll.set_content_size, ui.scroll.set_direction, ui.scroll.set_sensitivity
- **디버깅** (3개): ui.debug.rects, ui.debug.overlap, ui.debug.hit_test
- **정렬/분배** (2개): ui.align, ui.distribute
- **테마** (2개): ui.theme.apply_color, ui.theme.apply_font_size
- **프리팹** (2개): ui.prefab.save, ui.prefab.instantiate

## 변경된 파일
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` -- `class` -> `partial class` 변경, `RegisterUIHandlers()` 호출 추가
- `src/IronRose.Engine/Cli/CliCommandDispatcher.UI.cs` -- 신규. 86개 UI 핸들러 및 헬퍼 메서드 (ParseVector2, FormatVector2, ParseVector4, FormatVector4, FindParentCanvas, BuildUITreeNode, CollectUIElements, ResolveSprite, CollectRectsForOverlap, RectsOverlap, ApplyThemeColorRecursive, ApplyFontSizeRecursive)

## 주요 결정 사항
- **partial class 분리**: 기존 CliCommandDispatcher.cs가 이미 2,700줄이므로, UI 핸들러를 별도 파일로 분리하여 관리 가능성을 높였다.
- **스프라이트 해석**: ResolveSprite 헬퍼를 추가하여 GUID와 에셋 경로 모두로 스프라이트를 로드할 수 있게 했다. AssetDatabase의 LoadByGuid<Sprite>와 Load<Sprite>를 사용한다.
- **오버랩 검출**: lastScreenRect의 AABB 겹침 검사로 O(n^2) 구현. UI 요소는 수백 개 이내이므로 성능 문제 없다.
- **정렬 기준**: 첫 번째 GO의 위치를 기준으로 나머지를 정렬. 피벗과 크기를 고려하여 edge 좌표를 계산한다.
- **분배**: 첫 번째와 마지막 GO의 anchoredPosition 사이를 선형 보간하여 균등 분배한다.
- **복합 생성 (Button)**: Button GO에 UIButton + UIImage + RectTransform을, Label 자식에 UIText + RectTransform(StretchAll)을 구성한다.
- **복합 생성 (ScrollView)**: ScrollView GO에 UIScrollView + RectTransform을, Content 자식에 RectTransform(TopStretch, 높이 600)을 구성한다.

## 다음 작업자 참고
- Python CLI 래퍼(ironrose_cli.py)는 수정 불필요. 명령을 해석하지 않고 릴레이하므로 새 명령이 자동 지원된다.
- CLI 스킬 문서 (.claude/skills/rose-cli/references/command-reference.md)에 UI 카테고리를 추가하면 AI 도구 연동이 더 수월해진다.
- ui.create_button의 Label 자식은 StretchAll 앵커로 설정되어 버튼 크기에 맞춰 자동 확장된다.
- ui.debug.overlap은 lastScreenRect 기반이므로, CanvasRenderer.RenderAll()이 한 번 이상 호출된 후에만 의미 있는 결과를 반환한다.
- ui.debug.hit_test는 CanvasRenderer.HitTest()를 직접 호출하며, screenX/screenY는 게임 뷰 기준 좌표이다.
