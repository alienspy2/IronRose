// ------------------------------------------------------------
// @file    CursorLockMode.cs
// @brief   커서 잠금 모드 열거형. Unity CursorLockMode 호환.
// @deps    (없음)
// @exports
//   enum CursorLockMode
//     None     — 기본 상태, 커서 자유 이동
//     Locked   — 커서 화면 중앙 고정 + 숨김, 마우스 델타만 반환
//     Confined — 커서가 윈도우 영역에 제한됨
// @note    Unity API와 동일한 이름/값 사용.
// ------------------------------------------------------------
namespace RoseEngine
{
    public enum CursorLockMode
    {
        None,       // 기본 상태, 커서 자유 이동
        Locked,     // 커서 화면 중앙 고정 + 숨김, 마우스 델타만 반환
        Confined,   // 커서가 윈도우 영역에 제한됨
    }
}
