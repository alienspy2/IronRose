# Phase 46d-w3: CLI 렌더링/비주얼 명령 세트 구현

## 수행한 작업
- CliCommandDispatcher에 Wave 3 렌더링/비주얼 명령 11개 핸들러를 추가함
- FormatColor() 헬퍼 메서드를 추가하여 Color를 "r, g, b, a" 문자열로 포맷

## 추가된 명령
1. `material.info <goId>` -- MeshRenderer의 머티리얼 정보 조회 (color, metallic, roughness, occlusion, emission, 텍스처 유무 등)
2. `material.set_color <goId> <r,g,b,a>` -- 머티리얼 색상 변경
3. `material.set_metallic <goId> <value>` -- metallic 값 변경 (0.0~1.0)
4. `material.set_roughness <goId> <value>` -- roughness 값 변경 (0.0~1.0)
5. `light.info <id>` -- Light 컴포넌트 정보 조회 (type, color, intensity, range, spot angles, shadow 설정 등)
6. `light.set_color <id> <r,g,b,a>` -- 라이트 색상 변경
7. `light.set_intensity <id> <value>` -- 라이트 강도 변경
8. `camera.info [id]` -- 카메라 정보 조회 (id 미지정 시 Camera.main 사용)
9. `camera.set_fov <id> <fov>` -- FOV 설정
10. `render.info` -- 전역 렌더 설정 조회 (ambient, skybox, FSR, SSIL 등)
11. `render.set_ambient <r,g,b[,a]>` -- 앰비언트 라이트 색상 변경

## 변경된 파일
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` -- 11개 핸들러 + FormatColor() 헬퍼 추가, frontmatter 갱신

## 주요 결정 사항
- `render.set_ambient`는 ParseColor()를 사용하지 않고 직접 파싱 -- RGB 3채널만 받을 수 있으면서 RGBA도 허용하기 위함 (명세서 지시 따름)
- `camera.info`는 인자 선택적 -- 미지정 시 Camera.main을 사용하여 편의성 제공
- 모든 setter 명령에서 `SceneManager.GetActiveScene().isDirty = true` 설정하여 변경 사항 추적

## 다음 작업자 참고
- Wave 4 (convenience commands) 구현이 남아 있음
- material.set_* 명령들은 값 범위 검증을 하지 않음 (0.0~1.0 범위를 넘어도 설정됨). 필요 시 clamping 추가 고려
- using 추가 불필요 -- 모든 렌더링 관련 클래스는 이미 `using RoseEngine;`으로 접근 가능
