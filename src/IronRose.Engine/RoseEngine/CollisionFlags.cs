// ------------------------------------------------------------
// @file    CollisionFlags.cs
// @brief   CharacterController.Move()가 반환하는 충돌 방향 플래그. Unity API 호환.
// @deps    없음
// @exports
//   enum CollisionFlags [Flags]
//     None  = 0  — 충돌 없음
//     Sides = 1  — 측면 충돌
//     Above = 2  — 천장 충돌
//     Below = 4  — 바닥 충돌
// @note    [Flags] 어트리뷰트로 비트 조합 가능 (예: Sides | Below).
// ------------------------------------------------------------
using System;

namespace RoseEngine
{
    [Flags]
    public enum CollisionFlags
    {
        None = 0,
        Sides = 1,
        Above = 2,
        Below = 4,
    }
}
