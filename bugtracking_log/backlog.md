# Bug Backlog

## 1. NativeFileDialog(zenity/kdialog) z-order
NativeFileDialog(zenity/kdialog)가 메인 GLFW 윈도우 뒤에 깔려서 보이지 않는 문제.

**시도한 것:**
- `--attach=0x{X11WindowId}` 옵션을 zenity/kdialog에 전달 → 효과 없음
- 윈도우 최소화/복원 핵 → 동작은 하지만 UX가 좋지 않음

**근본 해결 방안:**
1. xdg-desktop-portal의 FileChooser D-Bus API 사용 (zenity 대체)
2. ImGui 내부에 자체 파일 브라우저 구현

## 2. ImGui 다이얼로그 topmost 문제
Welcome 화면, New Project 다이얼로그 등 ImGui 윈도우가 다른 앱보다 항상 위에 표시됨.

**원인 추정:**
ImGuiPlatformBackend가 모든 보조 뷰포트에 GLFW `Floating = true`(always-on-top)를 설정. ConfigViewportsNoAutoMerge = true와 결합되어 floating ImGui 윈도우가 OS 레벨 topmost 윈도우로 생성됨.

**시도한 것:**
- `ViewportsEnable` 임시 해제 → 효과 없음
- ImGuiPlatformBackend.CreateWindow()에서 `Floating = true` 설정이 원인으로 추정되지만, ViewportsEnable 토글로는 해결 안 됨

**근본 해결 방안:**
1. ImGuiPlatformBackend에서 Floating 설정을 조건부로 변경 (특정 뷰포트만 floating)
2. xdg-desktop-portal의 FileChooser D-Bus API 사용 (zenity 대체)
3. ImGui 내부에 자체 파일 브라우저 구현
