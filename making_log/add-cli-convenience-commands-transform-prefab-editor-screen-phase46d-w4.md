# Phase 46d-w4: CLI 편의 기능 명령 세트 구현

## 수행한 작업
- CliCommandDispatcher에 Wave 4 편의 기능 명령 22개 핸들러를 추가
- EngineCore.Update()에 CLI 스크린샷 요청 처리 코드 추가
- CliCommandDispatcher에 `_pendingScreenshotPath` internal static 필드 추가

### 추가된 명령 목록
1. `transform.translate` -- 상대 이동 (월드 좌표 기준)
2. `transform.rotate` -- 상대 회전 (로컬 기준, 오일러 각도)
3. `transform.look_at` -- 타겟 GO를 바라보도록 회전
4. `transform.get_children` -- 직접 자식 목록 조회
5. `transform.set_local_position` -- 로컬 위치 설정
6. `prefab.create_variant` -- Variant 프리팹 생성
7. `prefab.is_instance` -- 프리팹 인스턴스 여부 확인
8. `prefab.unpack` -- 프리팹 인스턴스 언팩
9. `asset.import` -- 에셋 임포트/리임포트 트리거
10. `asset.scan` -- 에셋 스캔 실행
11. `editor.screenshot` -- 현재 화면 캡처 (비동기, 다음 프레임에서 캡처)
12. `editor.copy` -- GO 복사 (클립보드)
13. `editor.paste` -- 클립보드에서 붙여넣기
14. `editor.select_all` -- 모든 GO 선택
15. `editor.undo_history` -- Undo/Redo 스택 설명 조회
16. `screen.info` -- 화면 정보 (width, height, dpi)
17. `scene.clear` -- 씬 내 모든 GO 삭제
18. `camera.set_clip` -- 클리핑 near/far 설정
19. `light.set_type` -- 라이트 타입 변경
20. `light.set_range` -- 라이트 범위 변경
21. `light.set_shadows` -- 그림자 on/off
22. `render.set_skybox_exposure` -- 스카이박스 노출 변경

## 변경된 파일
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` -- Wave 4 핸들러 22개 추가, `_pendingScreenshotPath` static 필드 추가, frontmatter 갱신
- `src/IronRose.Engine/EngineCore.cs` -- Update()에 CLI 스크린샷 요청 처리 코드 추가, frontmatter 갱신

## 주요 결정 사항
- `editor.screenshot`은 GPU 렌더링 프레임 끝에서만 캡처 가능하므로, static 필드에 경로를 저장하고 EngineCore가 다음 프레임에서 `GraphicsManager.RequestScreenshot()`을 호출하는 비동기 패턴을 사용
- `editor.copy`는 `EditorSelection.Select(id)`로 임시 선택 후 `EditorClipboard.CopyGameObjects()`를 호출하는 방식
- `asset.import`는 직접 임포트가 아닌 `ScanAssets()`로 전체 에셋 폴더를 재스캔하는 방식

## 다음 작업자 참고
- `editor.screenshot`은 CLI 응답은 즉시 반환되지만 실제 스크린샷 파일은 다음 렌더링 프레임 이후에 생성됨
- `_pendingScreenshotPath`는 메인 스레드에서만 읽고 쓰므로 스레드 안전 (핸들러도 ExecuteOnMainThread에서 실행)
- Phase 46d Wave 1~4가 모두 완료됨. 총 59개 이상의 CLI 명령이 등록됨
