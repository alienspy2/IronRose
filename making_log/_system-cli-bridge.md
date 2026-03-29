# CLI 브릿지 시스템

## 구조
- `src/IronRose.Engine/Cli/CliPipeServer.cs` -- Named Pipe 서버. 백그라운드 스레드에서 클라이언트 연결 수신/응답.
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` -- 명령 파싱 및 핸들러 디스패치. 메인 스레드 큐 동기화.
- `src/IronRose.Engine/Cli/CliLogBuffer.cs` -- 최근 로그 링 버퍼 (최대 1000개, 스레드 안전).
- `src/IronRose.Engine/EngineCore.cs` -- CLI 서버 생명주기 관리 (Initialize/Update/Shutdown).
- `tools/ironrose-cli/ironrose_cli.py` -- Python CLI 래퍼. Named Pipe로 명령 전송 및 JSON 응답 출력.

### 의존 관계
```
EngineCore --> CliPipeServer --> CliCommandDispatcher --> CliLogBuffer
                                                     --> SceneManager, ProjectContext (RoseEngine)

Claude Code --Bash--> ironrose_cli.py --Unix Domain Socket--> CliPipeServer
```

## 핵심 동작

### 데이터 흐름
1. EngineCore.Initialize()에서 CliLogBuffer 생성 -> LogSink 연결 -> CliCommandDispatcher 생성 -> CliPipeServer 시작
2. 클라이언트가 Named Pipe에 연결하여 평문 명령 전송 (길이 접두사 프레임)
3. CliPipeServer(백그라운드 스레드)가 요청을 읽어 CliCommandDispatcher.Dispatch() 호출
4. 백그라운드에서 직접 처리 가능한 명령(ping)은 즉시 응답
5. 메인 스레드 접근이 필요한 명령(scene.*)은 ConcurrentQueue에 넣고 ManualResetEventSlim으로 대기
6. EngineCore.Update()에서 ProcessMainThreadQueue() 호출 -> 큐 소비 -> Done.Set()으로 대기 해제
7. 응답 JSON을 클라이언트에 전송

### Python CLI 래퍼 동작
1. `argparse`로 `--project`, `--timeout`, 나머지 command 인자를 파싱
2. `--project` 미지정 시 `~/.ironrose/settings.toml`의 `[editor] last_project` 경로에서 `project.toml`을 읽어 프로젝트명 감지
3. 파이프 경로 결정: Linux `/tmp/CoreFxPipe_ironrose-cli-{name}`, Windows `\\.\pipe\ironrose-cli-{name}`
4. 공백 포함 인자에 쌍따옴표를 씌워서 평문 요청 문자열 생성
5. Unix Domain Socket (Linux) 또는 파일 핸들 (Windows)로 연결
6. 길이 접두사 프레임으로 요청 전송, 응답 수신
7. JSON 파싱: `ok=true`이면 `data` pretty-print (stdout, exit 0), `ok=false`이면 `error` (stderr, exit 1)

### 메시지 프레임 포맷
- [4 bytes little-endian 길이][N bytes UTF-8 문자열]
- 최대 메시지 크기: 16MB

### 파이프 이름
- 형식: `ironrose-cli-{SanitizedProjectName}`
- Linux 실제 경로: `/tmp/CoreFxPipe_ironrose-cli-{name}` (.NET 런타임 규칙, Unix Domain Socket)
- Windows 실제 경로: `\\.\pipe\ironrose-cli-{name}`

## 구조 (추가)

### partial class 분리
- `CliCommandDispatcher.cs` -- 기존 핸들러 + 핵심 인프라 (Dispatch, ProcessMainThreadQueue, 헬퍼)
- `CliCommandDispatcher.UI.cs` -- UI 관련 86개 핸들러 (RegisterUIHandlers()). partial class로 분리.
  - 헬퍼: ParseVector2, FormatVector2, ParseVector4, FormatVector4, FindParentCanvas, BuildUITreeNode, CollectUIElements, ResolveSprite, CollectRectsForOverlap, RectsOverlap, ApplyThemeColorRecursive, ApplyFontSizeRecursive

## 지원 명령 목록 (UI 명령 추가 완료)

| 명령 | 실행 위치 | 설명 |
|------|-----------|------|
| `ping` | 백그라운드 | 연결 테스트 |
| `scene.info` | 메인 스레드 | 현재 씬 정보 |
| `scene.list` | 메인 스레드 | 전체 GameObject 목록 |
| `scene.save` | 메인 스레드 | 현재 씬 저장 ([path] 선택) |
| `scene.load` | 메인 스레드 | 씬 파일 로드 |
| `scene.tree` | 메인 스레드 | 계층 트리 (부모-자식 재귀 구조) |
| `scene.new` | 메인 스레드 | 새 빈 씬 생성 (Clear + 새 Scene) |
| `go.get` | 메인 스레드 | GO 상세 정보 (컴포넌트/필드 포함) |
| `go.find` | 메인 스레드 | 이름으로 GO 검색 (정확 매칭) |
| `go.set_active` | 메인 스레드 | GO 활성/비활성 |
| `go.set_field` | 메인 스레드 | 컴포넌트 필드 수정 (리플렉션) |
| `select` | 메인 스레드 | 에디터 선택 변경 |
| `play.enter` | 메인 스레드 | Play 모드 진입 |
| `play.stop` | 메인 스레드 | Play 모드 종료 |
| `play.pause` | 메인 스레드 | 일시정지 |
| `play.resume` | 메인 스레드 | 재개 |
| `play.state` | 메인 스레드 | 현재 Play 상태 조회 |
| `prefab.instantiate` | 메인 스레드 | GUID로 프리팹 인스턴스 생성 ([x,y,z] 위치 옵션) |
| `prefab.save` | 메인 스레드 | GO를 .prefab 파일로 저장 (GUID 반환) |
| `asset.list` | 메인 스레드 | 에셋 DB 전체/필터 목록 (Contains 부분 매칭) |
| `asset.find` | 메인 스레드 | 이름으로 에셋 검색 (case-insensitive 부분 매칭) |
| `asset.guid` | 메인 스레드 | 경로에서 GUID 조회 |
| `asset.path` | 메인 스레드 | GUID에서 경로 조회 |
| `material.info` | 메인 스레드 | GO의 MeshRenderer 머티리얼 정보 조회 |
| `material.set_color` | 메인 스레드 | 머티리얼 색상 변경 |
| `material.set_metallic` | 메인 스레드 | metallic 값 변경 |
| `material.set_roughness` | 메인 스레드 | roughness 값 변경 |
| `light.info` | 메인 스레드 | Light 컴포넌트 정보 조회 |
| `light.set_color` | 메인 스레드 | 라이트 색상 변경 |
| `light.set_intensity` | 메인 스레드 | 라이트 강도 변경 |
| `camera.info` | 메인 스레드 | 카메라 정보 조회 (id 미지정 시 Camera.main) |
| `camera.set_fov` | 메인 스레드 | FOV 설정 |
| `render.info` | 메인 스레드 | 전역 렌더 설정 조회 (ambient, skybox, FSR, SSIL) |
| `render.set_ambient` | 메인 스레드 | 앰비언트 라이트 색상 변경 |
| `log.recent` | 백그라운드 | 최근 로그 조회 (스레드 안전) |
| `transform.translate` | 메인 스레드 | 상대 이동 (월드 좌표) |
| `transform.rotate` | 메인 스레드 | 상대 회전 (로컬, 오일러) |
| `transform.look_at` | 메인 스레드 | 타겟 GO를 바라봄 |
| `transform.get_children` | 메인 스레드 | 직접 자식 목록 조회 |
| `transform.set_local_position` | 메인 스레드 | 로컬 위치 설정 |
| `prefab.create_variant` | 메인 스레드 | Variant 프리팹 생성 |
| `prefab.is_instance` | 메인 스레드 | 프리팹 인스턴스 여부 확인 |
| `prefab.unpack` | 메인 스레드 | 프리팹 인스턴스 언팩 |
| `asset.import` | 메인 스레드 | 에셋 임포트/리임포트 (ScanAssets 호출) |
| `asset.scan` | 메인 스레드 | 에셋 스캔 실행 ([path] 선택) |
| `editor.screenshot` | 메인 스레드 | 화면 캡처 (비동기, 다음 프레임에서 캡처) |
| `editor.copy` | 메인 스레드 | GO 복사 (클립보드) |
| `editor.paste` | 메인 스레드 | 클립보드에서 붙여넣기 |
| `editor.select_all` | 메인 스레드 | 모든 GO 선택 |
| `editor.undo_history` | 메인 스레드 | Undo/Redo 스택 설명 조회 |
| `screen.info` | 메인 스레드 | 화면 정보 (width, height, dpi) |
| `scene.clear` | 메인 스레드 | 씬 내 모든 GO 삭제 (Scene 유지) |
| `camera.set_clip` | 메인 스레드 | 클리핑 near/far 설정 |
| `light.set_type` | 메인 스레드 | 라이트 타입 변경 (Directional/Point/Spot) |
| `light.set_range` | 메인 스레드 | 라이트 범위 변경 |
| `light.set_shadows` | 메인 스레드 | 그림자 on/off |
| `render.set_skybox_exposure` | 메인 스레드 | 스카이박스 노출 변경 |
| **UI 편의 생성** | | |
| `ui.create_canvas` | 메인 스레드 | Canvas + RectTransform GO 생성 |
| `ui.create_text` | 메인 스레드 | UIText GO 생성 (부모 하위) |
| `ui.create_image` | 메인 스레드 | UIImage GO 생성 |
| `ui.create_panel` | 메인 스레드 | UIPanel GO 생성 (색상 옵션) |
| `ui.create_button` | 메인 스레드 | UIButton + UIImage + Label(UIText) 복합 생성 |
| `ui.create_toggle` | 메인 스레드 | UIToggle GO 생성 |
| `ui.create_slider` | 메인 스레드 | UISlider GO 생성 |
| `ui.create_input` | 메인 스레드 | UIInputField GO 생성 |
| `ui.create_layout` | 메인 스레드 | UILayoutGroup GO 생성 |
| `ui.create_scroll` | 메인 스레드 | UIScrollView + Content 자식 GO 생성 |
| **UI 트리/조회** | | |
| `ui.tree` | 메인 스레드 | Canvas UI 계층 트리 (RectTransform 정보 포함) |
| `ui.list` | 메인 스레드 | UI 요소 flat 목록 |
| `ui.find` | 메인 스레드 | UI 컴포넌트가 있는 GO 이름 검색 |
| `ui.canvas.list` | 메인 스레드 | 모든 Canvas 목록 |
| **RectTransform** | | |
| `ui.rect.get` | 메인 스레드 | RectTransform 전체 정보 |
| `ui.rect.set_anchors` | 메인 스레드 | anchorMin, anchorMax 설정 |
| `ui.rect.set_position` | 메인 스레드 | anchoredPosition 설정 |
| `ui.rect.set_size` | 메인 스레드 | sizeDelta 설정 |
| `ui.rect.set_pivot` | 메인 스레드 | pivot 설정 |
| `ui.rect.set_offsets` | 메인 스레드 | offsetMin, offsetMax 설정 |
| `ui.rect.set_preset` | 메인 스레드 | AnchorPreset 적용 |
| `ui.rect.get_world_rect` | 메인 스레드 | lastScreenRect 조회 |
| **Canvas** | | |
| `ui.canvas.info` | 메인 스레드 | Canvas 상세 정보 |
| `ui.canvas.set_render_mode` | 메인 스레드 | renderMode 변경 |
| `ui.canvas.set_sorting_order` | 메인 스레드 | sortingOrder 변경 |
| `ui.canvas.set_reference_resolution` | 메인 스레드 | referenceResolution 변경 |
| `ui.canvas.set_scale_mode` | 메인 스레드 | scaleMode 변경 |
| `ui.canvas.set_match` | 메인 스레드 | matchWidthOrHeight 변경 |
| **UIText** | | |
| `ui.text.info` | 메인 스레드 | UIText 정보 |
| `ui.text.set_text` | 메인 스레드 | 텍스트 내용 변경 |
| `ui.text.set_font_size` | 메인 스레드 | fontSize 변경 |
| `ui.text.set_color` | 메인 스레드 | 색상 변경 |
| `ui.text.set_alignment` | 메인 스레드 | alignment 변경 |
| `ui.text.set_overflow` | 메인 스레드 | overflow 변경 |
| **UIImage** | | |
| `ui.image.info` | 메인 스레드 | UIImage 정보 |
| `ui.image.set_color` | 메인 스레드 | 색상 변경 |
| `ui.image.set_type` | 메인 스레드 | imageType 변경 |
| `ui.image.set_sprite` | 메인 스레드 | 스프라이트 변경 (GUID 또는 경로) |
| `ui.image.set_preserve_aspect` | 메인 스레드 | preserveAspect 변경 |
| **UIPanel** | | |
| `ui.panel.info` | 메인 스레드 | UIPanel 정보 |
| `ui.panel.set_color` | 메인 스레드 | 색상 변경 |
| `ui.panel.set_sprite` | 메인 스레드 | 스프라이트 변경 |
| `ui.panel.set_type` | 메인 스레드 | imageType 변경 |
| **UIButton** | | |
| `ui.button.info` | 메인 스레드 | UIButton 정보 |
| `ui.button.set_interactable` | 메인 스레드 | interactable 변경 |
| `ui.button.set_colors` | 메인 스레드 | 4색 일괄 설정 |
| `ui.button.set_transition` | 메인 스레드 | transition 변경 |
| **UIToggle** | | |
| `ui.toggle.info` | 메인 스레드 | UIToggle 정보 |
| `ui.toggle.set_on` | 메인 스레드 | isOn 변경 |
| `ui.toggle.set_interactable` | 메인 스레드 | interactable 변경 |
| `ui.toggle.set_colors` | 메인 스레드 | 배경/체크마크 색상 설정 |
| **UISlider** | | |
| `ui.slider.info` | 메인 스레드 | UISlider 정보 |
| `ui.slider.set_value` | 메인 스레드 | value 변경 |
| `ui.slider.set_range` | 메인 스레드 | min/max 설정 |
| `ui.slider.set_direction` | 메인 스레드 | direction 변경 |
| `ui.slider.set_whole_numbers` | 메인 스레드 | wholeNumbers 변경 |
| `ui.slider.set_interactable` | 메인 스레드 | interactable 변경 |
| `ui.slider.set_colors` | 메인 스레드 | 3색 일괄 설정 |
| **UIInputField** | | |
| `ui.input.info` | 메인 스레드 | UIInputField 정보 |
| `ui.input.set_text` | 메인 스레드 | text 변경 |
| `ui.input.set_placeholder` | 메인 스레드 | placeholder 변경 |
| `ui.input.set_font_size` | 메인 스레드 | fontSize 변경 |
| `ui.input.set_max_length` | 메인 스레드 | maxLength 변경 |
| `ui.input.set_content_type` | 메인 스레드 | contentType 변경 |
| `ui.input.set_interactable` | 메인 스레드 | interactable 변경 |
| `ui.input.set_read_only` | 메인 스레드 | readOnly 변경 |
| **UILayoutGroup** | | |
| `ui.layout.info` | 메인 스레드 | UILayoutGroup 정보 |
| `ui.layout.set_direction` | 메인 스레드 | direction 변경 |
| `ui.layout.set_spacing` | 메인 스레드 | spacing 변경 |
| `ui.layout.set_padding` | 메인 스레드 | padding 변경 (Vector4) |
| `ui.layout.set_child_alignment` | 메인 스레드 | childAlignment 변경 |
| `ui.layout.set_force_expand` | 메인 스레드 | forceExpand width/height 설정 |
| **UIScrollView** | | |
| `ui.scroll.info` | 메인 스레드 | UIScrollView 정보 |
| `ui.scroll.set_scroll_position` | 메인 스레드 | scrollPosition 변경 |
| `ui.scroll.set_content_size` | 메인 스레드 | contentSize 변경 |
| `ui.scroll.set_direction` | 메인 스레드 | horizontal/vertical 설정 |
| `ui.scroll.set_sensitivity` | 메인 스레드 | scrollSensitivity 변경 |
| **디버깅** | | |
| `ui.debug.rects` | 메인 스레드 | CanvasRenderer.DebugDrawRects 토글 |
| `ui.debug.overlap` | 메인 스레드 | 겹치는 UI 요소 검출 |
| `ui.debug.hit_test` | 메인 스레드 | 스크린 좌표 hit test |
| **정렬/분배** | | |
| `ui.align` | 메인 스레드 | UI 요소 정렬 (left/right/top/bottom/center) |
| `ui.distribute` | 메인 스레드 | UI 요소 균등 분배 |
| **테마** | | |
| `ui.theme.apply_color` | 메인 스레드 | Canvas 하위 색상 일괄 적용 |
| `ui.theme.apply_font_size` | 메인 스레드 | Canvas 하위 폰트 크기 일괄 적용 |
| **UI 프리팹** | | |
| `ui.prefab.save` | 메인 스레드 | UI GO 프리팹 저장 |
| `ui.prefab.instantiate` | 메인 스레드 | UI 프리팹 인스턴스화 (부모 즉시 설정) |

### go.set_field 지원 타입
- float, int, bool, string, Vector3, Color, enum
- `ParseFieldValue`/`ParseVector3`/`ParseColor` 헬퍼 메서드로 파싱
- `SetFieldCommand.ParseValue`와 동일한 로직 (private이므로 별도 복사)

### FormatColor 헬퍼
- Color를 "r, g, b, a" 문자열로 포맷 (InvariantCulture 사용)
- material.info, light.info, camera.info, render.info에서 사용

## Phase 47a 변경사항
- `ResolveComponentType()`의 FrozenCode/LiveCode 이중 검색이 Scripts 단일 검색으로 변경됨
- `assembly.info` 핸들러의 JSON 응답 키가 `liveCodeDemoTypes`/`liveCodeDemoCount`에서 `scriptDemoTypes`/`scriptDemoCount`로 변경됨
- `EngineCore.LiveCodeDemoTypes` 프로퍼티 참조는 Phase 47c에서 이름 변경 예정

## 주의사항
- CLI 서버는 프로젝트 미로드 상태에서도 동작한다 (ping 등 기본 명령만 사용 가능)
- 메인 스레드 큐 대기 타임아웃은 5초. 모달 대화상자 등으로 메인 스레드가 블로킹되면 타임아웃 에러 반환
- `maxNumberOfServerInstances = 1`: 동시 클라이언트 연결 미지원
- Linux에서 Stop() 시 소켓 파일을 직접 삭제하여 WaitForConnectionAsync 블로킹을 해제함
- LogSink 람다에서 CliLogBuffer.Push()를 호출하므로, CliLogBuffer는 반드시 LogSink 연결 전에 생성해야 함
- Python 래퍼는 명령을 해석하지 않으므로, C# 서버에 명령만 추가하면 래퍼 수정 불필요
- `editor.screenshot`은 비동기 패턴 사용: CLI 핸들러가 `_pendingScreenshotPath`에 경로 저장 -> EngineCore.Update()에서 소비 -> `GraphicsManager.RequestScreenshot()` 호출 -> 다음 EndFrame에서 캡처. 응답은 즉시 반환되지만 파일은 다음 프레임 이후 생성
- `editor.copy`는 `EditorSelection.Select(id)` 후 `EditorClipboard.CopyGameObjects(cut: false)` 호출 (선택 상태 변경 사이드 이펙트 있음)
- `scene.clear`는 `SceneManager.Clear()` 호출. `scene.new`와 달리 새 Scene 객체를 만들지 않고 기존 씬의 name/path 유지
- `ui.debug.overlap`은 lastScreenRect 기반이므로, CanvasRenderer.RenderAll()이 한 번 이상 호출된 후에만 유효한 결과 반환
- `ui.image.set_sprite`, `ui.panel.set_sprite`는 GUID 또는 에셋 경로 모두 지원. AssetDatabase.LoadByGuid/Load 사용
- `ui.create_button`은 Button GO(UIButton+UIImage) + Label 자식(UIText, StretchAll 앵커) 복합 구조
- `ui.create_scroll`은 ScrollView GO(UIScrollView) + Content 자식(RectTransform, TopStretch) 복합 구조

## 사용하는 외부 라이브러리
- `System.IO.Pipes` -- .NET 표준 라이브러리. Named Pipe 서버/클라이언트.
- `System.Text.Json` -- .NET 표준 라이브러리. JSON 직렬화.
- Python 표준 라이브러리만 사용 (`socket`, `struct`, `json`, `argparse`, `os`, `sys`, `re`, `time`).
- 추가 NuGet/pip 패키지 없음.
